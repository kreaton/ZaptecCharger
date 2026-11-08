using System.Globalization;
using Zaptec.Api;

namespace ZaptecMonitor;

public enum ScheduleAction { None, Start, Stop }

/// <summary>
/// A daily charging window, e.g. 23:00–06:00 (wraps past midnight). From == To means "always allowed".
/// While a car is connected the app resumes charging inside the window and pauses it outside.
/// </summary>
public sealed record ChargeSchedule(TimeOnly From, TimeOnly To, bool StartAtFrom = true, bool PauseAtTo = true)
{
    private static readonly string[] Formats = ["H:mm", "HH:mm", "H.mm", "HH.mm", "HHmm"];

    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact((text ?? "").Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static ChargeSchedule? FromSettings(ScheduleSettings s) =>
        s.Enabled && (s.StartAtFrom || s.PauseAtTo) && TryParseTime(s.From, out var from) && TryParseTime(s.To, out var to)
            ? new ChargeSchedule(from, to, s.StartAtFrom, s.PauseAtTo)
            : null;

    public bool IsAllowed(TimeOnly t) =>
        From == To || (From < To ? t >= From && t < To : t >= From || t < To);

    public bool IsAllowed(DateTime t) => IsAllowed(TimeOnly.FromDateTime(t));

    /// <summary>The next moment after <paramref name="now"/> at which the window opens or closes.</summary>
    public DateTime NextBoundary(DateTime now)
    {
        DateTime Next(TimeOnly t)
        {
            var d = now.Date + t.ToTimeSpan();
            return d > now ? d : d.AddDays(1);
        }
        var a = Next(From);
        var b = Next(To);
        return a < b ? a : b;
    }

    /// <summary>What the schedule wants done for the given charger state, if anything.</summary>
    public ScheduleAction Decide(ChargerState state, DateTime now)
    {
        if (state.Mode is ChargerOperationMode.Disconnected or ChargerOperationMode.Unknown)
            return ScheduleAction.None; // only active while a car is connected

        if (IsAllowed(now))
            return StartAtFrom && state.CanStart ? ScheduleAction.Start : ScheduleAction.None;

        return PauseAtTo && state.CanStop ? ScheduleAction.Stop : ScheduleAction.None;
    }

    public override string ToString() => From == To ? "all day" : $"{From:HH\\:mm}–{To:HH\\:mm}";
}
