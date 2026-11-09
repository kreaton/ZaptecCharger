using Stromligning;

namespace ZaptecMonitor;

/// <summary>Hourly household electricity prices (kr/kWh, everything on the bill) from stromligning.dk.</summary>
public static class ElectricityPrices
{
    public static string UserAgent => $"ZaptecMonitor/{BuildInfo.Version.Split('-', '+')[0]}";

    // A month of hourly prices is ~745 rows; keep requests that size.
    private static readonly TimeSpan Chunk = TimeSpan.FromDays(31);

    /// <summary>
    /// Price per kWh for each local clock hour in [from, to), incl. or ex. VAT as the settings say.
    /// Keys are local wall-clock hour starts, matching <c>HourlyEnergy.HourStart</c>.
    /// When summer time ends the repeated hour gets the average of both.
    /// </summary>
    public static async Task<(Dictionary<DateTime, double> PerKwh, string? PriceArea)> GetHourlyAsync(
        StromligningClient client, PriceSettings settings, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default)
    {
        var points = new List<PricePoint>();
        string? area = null;
        for (var start = from; start < to; start += Chunk)
        {
            var end = start + Chunk < to ? start + Chunk : to;
            var response = await client.GetPricesAsync(settings.SupplierId, settings.CustomerGroupId,
                settings.ProductId, start, end, ct, aggregation: "1h");
            area ??= response.PriceArea;
            points.AddRange(response.Prices);
        }

        var perKwh = points
            .Where(p => p.Price is not null)
            .GroupBy(p => HourStart(TimeZoneInfo.ConvertTime(p.Date, TimeZoneInfo.Local).DateTime))
            .ToDictionary(g => g.Key, g => g.Average(p => settings.IncludeVat ? p.Price!.Total : p.Price!.Value));
        return (perKwh, area);
    }

    private static DateTime HourStart(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0);
}
