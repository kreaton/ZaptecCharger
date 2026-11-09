using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Stromligning;
using Zaptec.Api;

namespace ZaptecMonitor;

/// <summary>
/// Hour-by-hour table of energy charged, built from Zaptec's archived sessions, with an
/// approximate cost from stromligning.dk's hourly prices when a grid company is set up.
/// </summary>
public partial class HistoryWindow : Window
{
    /// <summary>One table row. Strings are pre-formatted so copy/paste and CSV match what's shown.</summary>
    public sealed record Row(DateTime HourStart, double KwhValue, bool Estimated, double? PriceValue)
    {
        public double? CostValue => PriceValue * KwhValue;

        public string Date => HourStart.ToString("yyyy-MM-dd  ddd", CultureInfo.CurrentCulture);
        public string Hour => $"{HourStart:HH}:00–{HourStart.AddHours(1):HH}:00";
        public string Kwh => KwhValue.ToString("0.00", CultureInfo.CurrentCulture);
        public string Price => PriceValue?.ToString("0.00", CultureInfo.CurrentCulture) ?? "";
        public string Cost => CostValue?.ToString("0.00", CultureInfo.CurrentCulture) ?? "";
        public string Note => Estimated ? "≈" : "";
    }

    // Zaptec doesn't document a maximum range, so ask in month-sized pieces.
    private static readonly TimeSpan Chunk = TimeSpan.FromDays(31);

    private readonly Func<(ZaptecClient? Client, ZaptecCharger? Charger)> _connection;
    private readonly Func<PriceSettings?> _priceSettings;
    private readonly StromligningClient _prices = new(userAgent: ElectricityPrices.UserAgent);
    private List<Row> _rows = [];
    private bool _hasPrices;
    private bool _loading;

    /// <param name="connection">Returns the main window's current client and charger (they change after a re-login).</param>
    /// <param name="priceSettings">Returns the current price settings (they change when settings are saved).</param>
    public HistoryWindow(Func<(ZaptecClient? Client, ZaptecCharger? Charger)> connection, Func<PriceSettings?> priceSettings)
    {
        InitializeComponent();
        _connection = connection;
        _priceSettings = priceSettings;
        SetRange(DateTime.Today.AddDays(-6), DateTime.Today);
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _prices.Dispose();
    }

    private void SetRange(DateTime from, DateTime to)
    {
        FromPicker.SelectedDate = from;
        ToPicker.SelectedDate = to;
    }

    private async void Preset_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        var first = new DateTime(today.Year, today.Month, 1);
        switch ((sender as Button)?.Tag as string)
        {
            case "7": SetRange(today.AddDays(-6), today); break;
            case "30": SetRange(today.AddDays(-29), today); break;
            case "month": SetRange(first, today); break;
            case "lastmonth": SetRange(first.AddMonths(-1), first.AddDays(-1)); break;
        }
        await LoadAsync();
    }

    private async void Load_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading) return;

        var (client, charger) = _connection();
        if (client is null || charger is null)
        {
            StatusText.Text = "Not connected to a charger yet. Sign in from Settings first.";
            return;
        }
        if (FromPicker.SelectedDate is not { } fromDate || ToPicker.SelectedDate is not { } toDate)
        {
            StatusText.Text = "Pick both dates.";
            return;
        }
        if (toDate < fromDate) (fromDate, toDate) = (toDate, fromDate);

        ChargerText.Text = charger.Name;
        _loading = true;
        LoadButton.IsEnabled = ExportButton.IsEnabled = false;
        StatusText.Text = "Loading…";
        try
        {
            // Local midnight to midnight. Sessions are filtered by END time, so also fetch a day past
            // the range (a session ending tomorrow may have charged today) and trim rows afterwards.
            var rangeStart = fromDate.Date;
            var rangeEnd = toDate.Date.AddDays(1);
            var queryFrom = new DateTimeOffset(rangeStart);
            var queryTo = new DateTimeOffset(rangeEnd.AddDays(1));

            // Prices in parallel with the sessions. A price failure only costs the kroner columns.
            var price = _priceSettings();
            var pricesTask = price is { IsConfigured: true }
                ? ElectricityPrices.GetHourlyAsync(_prices, price, new DateTimeOffset(rangeStart), new DateTimeOffset(rangeEnd))
                : null;

            var sessions = new List<ChargeSession>();
            for (var start = queryFrom; start < queryTo; start += Chunk)
            {
                var end = start + Chunk < queryTo ? start + Chunk : queryTo;
                StatusText.Text = $"Loading {start.LocalDateTime:d MMM}–{end.LocalDateTime.AddDays(-1):d MMM}…";
                sessions.AddRange(await client.GetSessionsAsync(charger.Id, start, end));
            }

            Dictionary<DateTime, double>? perKwh = null;
            string? priceError = null;
            if (pricesTask is not null)
            {
                StatusText.Text = "Loading electricity prices…";
                try { perKwh = (await pricesTask).PerKwh; }
                catch (Exception ex) { priceError = ex.Message; }
            }

            _rows = ChargeHistory.ToHourly(sessions, TimeZoneInfo.Local)
                .Where(h => h.HourStart >= rangeStart && h.HourStart < rangeEnd)
                .Select(h => new Row(h.HourStart, h.Kwh, h.Estimated,
                    perKwh is not null && perKwh.TryGetValue(h.HourStart, out var p) ? p : null))
                .ToList();

            _hasPrices = perKwh is not null;
            PriceColumn.Visibility = CostColumn.Visibility = _hasPrices ? Visibility.Visible : Visibility.Collapsed;
            Grid.ItemsSource = _rows;
            ShowTotals(fromDate, toDate, price, priceError);
            ExportButton.IsEnabled = _rows.Count > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _loading = false;
            LoadButton.IsEnabled = true;
        }
    }

    private void ShowTotals(DateTime fromDate, DateTime toDate, PriceSettings? price, string? priceError)
    {
        var kwh = _rows.Sum(r => r.KwhValue);
        var days = _rows.Select(r => r.HourStart.Date).Distinct().Count();
        var priced = _rows.Where(r => r.CostValue is not null).ToList();
        var cost = priced.Sum(r => r.CostValue!.Value);
        var pricedKwh = priced.Sum(r => r.KwhValue);

        TotalText.Text = $"{kwh:0.00} kWh in {_rows.Count} hours";
        CostText.Text = _hasPrices && priced.Count > 0
            ? $"≈ {cost:N2} kr  ·  {cost / pricedKwh:0.00} kr/kWh on average{(price!.IncludeVat ? " (incl. VAT)" : " (ex. VAT)")}" +
              (priced.Count < _rows.Count ? $"  ·  {_rows.Count - priced.Count} hour(s) without a price" : "")
            : "";
        CostText.Visibility = CostText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var parts = new List<string>();
        if (_rows.Count == 0) parts.Add("No charging in this period.");
        else parts.Add($"{days} day(s) with charging · {fromDate:d}–{toDate:d}");
        if (_rows.Any(r => r.Estimated)) parts.Add("≈ in the last column: spread evenly over the session (no hourly data)");
        if (priceError is not null) parts.Add("No prices from stromligning.dk: " + priceError);
        else if (price is not { IsConfigured: true }) parts.Add("Pick your grid company under Settings → Price to see the cost");
        if (_rows.Count > 0) parts.Add("Ctrl+C copies selected rows");
        StatusText.Text = string.Join(" · ", parts);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"charge-history-{FromPicker.SelectedDate:yyyy-MM-dd}-to-{ToPicker.SelectedDate:yyyy-MM-dd}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;

        // Semicolon + local decimal separator opens straight into a Danish (or English) Excel.
        var culture = CultureInfo.CurrentCulture;
        var sep = culture.TextInfo.ListSeparator;
        var sb = new StringBuilder();
        var header = new List<string> { "Date", "Hour", "kWh" };
        if (_hasPrices) header.AddRange(["Price kr/kWh", "Cost kr"]);
        header.Add("Estimated");
        sb.AppendLine(string.Join(sep, header));
        foreach (var r in _rows)
        {
            var fields = new List<string>
            {
                r.HourStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Hour,
                r.KwhValue.ToString("0.000", culture),
            };
            if (_hasPrices)
            {
                fields.Add(r.PriceValue?.ToString("0.0000", culture) ?? "");
                fields.Add(r.CostValue?.ToString("0.00", culture) ?? "");
            }
            fields.Add(r.Estimated ? "yes" : "");
            sb.AppendLine(string.Join(sep, fields));
        }

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            StatusText.Text = "Saved " + dlg.FileName;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save: " + ex.Message;
        }
    }
}
