namespace Zaptec.Api;

/// <summary>A charger as returned by GET /api/chargers.</summary>
public sealed class ZaptecCharger
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public bool IsOnline { get; set; }
}

/// <summary>Value of the ChargerOperationMode observation (state id 710).</summary>
public enum ChargerOperationMode
{
    Unknown = 0,
    Disconnected = 1,
    ConnectedRequesting = 2,
    ConnectedCharging = 3,
    ConnectedFinished = 5,
}

/// <summary>A snapshot of the most useful state observations for one charger.</summary>
public sealed class ChargerState
{
    public ChargerOperationMode Mode { get; init; }

    /// <summary>True when charging was paused through the API (FinalStopActive, state 718).</summary>
    public bool FinalStopActive { get; init; }

    /// <summary>Total charge power in watts (state 513).</summary>
    public double PowerWatts { get; init; }

    /// <summary>Energy delivered in the current session, kWh (state 553).</summary>
    public double SessionEnergyKwh { get; init; }

    /// <summary>Next event of a schedule set in the Zaptec app/portal (state 763), when the charger reports one.</summary>
    public DateTimeOffset? NextScheduleEvent { get; init; }

    public DateTimeOffset RetrievedAt { get; init; }

    /// <summary>
    /// Worth sending Resume: either paused via the API, or waiting — Zaptec accepts Resume for a session
    /// held by its own schedule ("Scheduled") and rejects it otherwise with a reason.
    /// </summary>
    public bool CanTryStart => CanStart || Mode == ChargerOperationMode.ConnectedRequesting;

    /// <summary>Pause is only accepted by Zaptec while actively charging.</summary>
    public bool CanStop => Mode == ChargerOperationMode.ConnectedCharging;

    /// <summary>Resume is only accepted when charging was paused via the API.</summary>
    public bool CanStart => Mode == ChargerOperationMode.ConnectedFinished && FinalStopActive;

    public string Description => Mode switch
    {
        ChargerOperationMode.Disconnected => "No car connected",
        ChargerOperationMode.ConnectedRequesting => "Car connected, waiting to charge",
        ChargerOperationMode.ConnectedCharging => "Charging",
        ChargerOperationMode.ConnectedFinished when FinalStopActive => "Paused",
        ChargerOperationMode.ConnectedFinished => "Finished (car stopped drawing power)",
        _ => "Unknown",
    };
}

public sealed class ZaptecApiException : Exception
{
    public System.Net.HttpStatusCode? StatusCode { get; }

    public ZaptecApiException(string message, System.Net.HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;
}

/// <summary>
/// A meter reading in a session's energyDetails. Zaptec documents the value as cumulative, but real
/// data is per interval: the energy delivered since the previous reading (or the session start),
/// with readings on whole hours. <see cref="ChargeHistory"/> handles both.
/// </summary>
public readonly record struct EnergyReading(DateTimeOffset Timestamp, double Kwh);

/// <summary>A completed charge session from GET /api/sessions/archived.</summary>
public sealed class ChargeSession
{
    public string Id { get; init; } = "";
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public double EnergyKwh { get; init; }

    /// <summary>Meter readings during the session, oldest first. Can be empty.</summary>
    public IReadOnlyList<EnergyReading> Readings { get; init; } = [];
}

/// <summary>Energy charged in one clock hour (local time).</summary>
public sealed record HourlyEnergy(DateTime HourStart, double Kwh, bool Estimated);

public static class ChargeHistory
{
    /// <summary>
    /// Splits sessions into clock hours in the given time zone. Hours with 0 kWh are left out.
    /// <para>
    /// Each reading's energy belongs to the time since the previous reading, but at most the hour
    /// before it: readings come every whole hour while the car draws power, so a longer gap means
    /// the car was idle (for example waiting for a schedule) until the last hour.
    /// Sessions without readings are spread evenly from start to end and marked Estimated.
    /// </para>
    /// </summary>
    public static IReadOnlyList<HourlyEnergy> ToHourly(IEnumerable<ChargeSession> sessions, TimeZoneInfo zone)
    {
        var kwh = new Dictionary<DateTime, double>();
        var estimated = new HashSet<DateTime>();

        foreach (var s in sessions)
        {
            var readings = s.Readings.Where(r => r.Timestamp >= s.Start).ToList();
            if (readings.Count == 0 || readings.All(r => r.Kwh <= 0))
            {
                var end = s.End is { } e && e > s.Start ? e : s.Start;
                Spread(s.Start, end, s.EnergyKwh, zone, kwh, estimated);
                continue;
            }

            // Tolerate the documented (cumulative) format too: convert it to per-interval values.
            var values = IsCumulative(readings, s.EnergyKwh)
                ? readings.Select((r, i) => i == 0 ? r.Kwh : r.Kwh - readings[i - 1].Kwh).ToList()
                : readings.Select(r => r.Kwh).ToList();

            var prev = s.Start;
            for (var i = 0; i < readings.Count; i++)
            {
                var t = readings[i].Timestamp;
                var from = t - prev > TimeSpan.FromHours(1) ? t.AddHours(-1) : prev;
                Spread(from, t, values[i], zone, kwh, null);
                prev = t;
            }
        }

        return kwh
            .Where(p => p.Value >= 0.0005)
            .OrderBy(p => p.Key)
            .Select(p => new HourlyEnergy(p.Key, p.Value, estimated.Contains(p.Key)))
            .ToList();
    }

    /// <summary>True when readings rise steadily and end at the session total (the documented format).</summary>
    private static bool IsCumulative(List<EnergyReading> r, double sessionKwh)
    {
        if (r.Count < 3) return false; // too few to tell; both readings give the same total then
        for (var i = 1; i < r.Count; i++)
            if (r[i].Kwh < r[i - 1].Kwh) return false;
        return Math.Abs(r[^1].Kwh - sessionKwh) <= 0.02 * sessionKwh + 0.01;
    }

    /// <summary>Spreads <paramref name="energy"/> evenly over [from, to) by clock hour.</summary>
    private static void Spread(DateTimeOffset from, DateTimeOffset to, double energy, TimeZoneInfo zone,
                               Dictionary<DateTime, double> kwh, HashSet<DateTime>? estimated)
    {
        if (energy <= 0) return;

        var total = (to - from).TotalSeconds;
        if (total <= 0)
        {
            Add(LocalHour(to, zone), energy);
            return;
        }

        // Walk UTC hour boundaries; every zone we care about has whole-hour offsets.
        var t = from;
        while (t < to)
        {
            var nextHour = new DateTimeOffset(t.UtcDateTime.Date.AddHours(t.UtcDateTime.Hour + 1), TimeSpan.Zero);
            var segEnd = nextHour < to ? nextHour : to;
            Add(LocalHour(t, zone), energy * (segEnd - t).TotalSeconds / total);
            t = segEnd;
        }

        void Add(DateTime hour, double value)
        {
            kwh[hour] = kwh.GetValueOrDefault(hour) + value;
            estimated?.Add(hour);
        }
    }

    private static DateTime LocalHour(DateTimeOffset t, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(t, zone).DateTime;
        return new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0);
    }
}
