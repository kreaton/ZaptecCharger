using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Stromligning;
using System.Windows.Controls;
using System.Windows.Navigation;
using Velopack;
using Velopack.Sources;
using Zaptec.Api;

namespace ZaptecMonitor;

public partial class SettingsWindow : Window
{
    private sealed record ChargerOption(string Id, string Display);

    private const string FirstChargerDisplay = "First charger on the account";
    private readonly List<ChargerOption> _chargers = [];

    /// <summary>The saved settings, valid when the dialog returns true.</summary>
    public AppSettings Result { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        Result = current.Clone();

        UsernameBox.Text = Result.Zaptec.Username;
        PasswordBox.Password = Result.Zaptec.Password;
        SettingsPathRun.Text = AppSettings.UserSettingsPath;

        _chargers.Add(new ChargerOption("", FirstChargerDisplay));
        if (!string.IsNullOrWhiteSpace(Result.Zaptec.ChargerId))
            _chargers.Add(new ChargerOption(Result.Zaptec.ChargerId, Result.Zaptec.ChargerId));
        BindChargers(Result.Zaptec.ChargerId);

        PollingCheck.IsChecked = Result.Zaptec.PollingEnabled;
        PollIntervalBox.Text = Result.Zaptec.PollIntervalMinutes.ToString();

        ScheduleCheck.IsChecked = Result.Schedule.Enabled;
        StartAtFromCheck.IsChecked = Result.Schedule.StartAtFrom;
        PauseAtToCheck.IsChecked = Result.Schedule.PauseAtTo;
        FromBox.Text = Result.Schedule.From;
        ToBox.Text = Result.Schedule.To;

        Result.Price ??= new();
        IncludeVatCheck.IsChecked = Result.Price.IncludeVat;
        Loaded += async (_, _) => await LoadPriceListsAsync();

        VersionText.Text = $"{BuildInfo.Display}  ·  {BuildInfo.Copyright}";

        UpdateEnabledStates();
        UpdateSchedulePreview();

        if (!Result.Zaptec.HasCredentials)
            Loaded += (_, _) => UsernameBox.Focus();
    }

    private void BindChargers(string selectedId)
    {
        ChargerCombo.ItemsSource = null;
        ChargerCombo.ItemsSource = _chargers;
        ChargerCombo.SelectedValue = _chargers.Any(c => c.Id == selectedId) ? selectedId : "";
    }

    private async void FindChargers_Click(object sender, RoutedEventArgs e)
    {
        var user = UsernameBox.Text.Trim();
        var pwd = PasswordBox.Password;
        if (user.Length == 0 || pwd.Length == 0)
        {
            ChargerHint.Text = "Enter e-mail and password first.";
            return;
        }

        var selected = ChargerCombo.SelectedValue as string ?? "";
        FindChargersButton.IsEnabled = false;
        ChargerHint.Text = "Signing in…";
        try
        {
            using var client = new ZaptecClient(user, pwd);
            var found = await client.GetChargersAsync();

            _chargers.Clear();
            _chargers.Add(new ChargerOption("", FirstChargerDisplay));
            foreach (var c in found)
                _chargers.Add(new ChargerOption(c.Id, $"{c.Name}{(c.IsOnline ? "" : " (offline)")}"));
            BindChargers(selected);

            ChargerHint.Text = found.Count switch
            {
                0 => "Signed in, but no chargers were found on this account.",
                1 => "Signed in. Found 1 charger.",
                var n => $"Signed in. Found {n} chargers.",
            };
        }
        catch (Exception ex)
        {
            ChargerHint.Text = ex.Message;
        }
        finally
        {
            FindChargersButton.IsEnabled = true;
        }
    }

    private void PollingCheck_Changed(object sender, RoutedEventArgs e) => UpdateEnabledStates();

    private void ScheduleCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateEnabledStates();
        UpdateSchedulePreview();
    }

    private void ScheduleTime_Changed(object sender, TextChangedEventArgs e) => UpdateSchedulePreview();

    private void UpdateEnabledStates()
    {
        // Called from Checked handlers during InitializeComponent, before every control exists.
        if (PollIntervalBox is null || ScheduleFields is null) return;
        PollIntervalBox.IsEnabled = PollingCheck.IsChecked == true;
        ScheduleFields.IsEnabled = ScheduleCheck.IsChecked == true;
    }

    private void UpdateSchedulePreview()
    {
        if (SchedulePreview is null || FromBox is null || ToBox is null ||
            StartAtFromCheck is null || PauseAtToCheck is null) return;

        if (ScheduleCheck.IsChecked != true)
        {
            SchedulePreview.Text = "The app will not start or pause charging on its own.";
            return;
        }

        if (!ChargeSchedule.TryParseTime(FromBox.Text, out var from) ||
            !ChargeSchedule.TryParseTime(ToBox.Text, out var to))
        {
            SchedulePreview.Text = "Enter both times as HH:mm, e.g. 23:00.";
            return;
        }

        var start = StartAtFromCheck.IsChecked == true;
        var pause = PauseAtToCheck.IsChecked == true;
        if (!start && !pause)
        {
            SchedulePreview.Text = "Pick start, pause or both, or the schedule does nothing.";
            return;
        }
        if (from == to)
        {
            SchedulePreview.Text = "From and To are equal: charging is allowed all day.";
            return;
        }

        var hours = ((to.ToTimeSpan() - from.ToTimeSpan()).TotalHours + 24) % 24;
        var what = (start, pause) switch
        {
            (true, true) => $"Starts at {from:HH\\:mm}, pauses at {to:HH\\:mm}",
            (true, false) => $"Starts at {from:HH\\:mm}; never pauses on its own",
            _ => $"Pauses at {to:HH\\:mm}; you start it again by hand",
        };
        SchedulePreview.Text = $"{what} ({hours:0.#} h window{(from > to ? ", across midnight" : "")}).";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = "";

        if (!int.TryParse(PollIntervalBox.Text.Trim(), out var interval) || interval < AppSettings.MinPollMinutes)
        {
            Fail(1, $"Refresh interval must be a whole number of at least {AppSettings.MinPollMinutes} minute.");
            return;
        }

        var scheduleOn = ScheduleCheck.IsChecked == true;
        if (!ChargeSchedule.TryParseTime(FromBox.Text, out var from) ||
            !ChargeSchedule.TryParseTime(ToBox.Text, out var to))
        {
            if (scheduleOn)
            {
                Fail(2, "Schedule times must be HH:mm.");
                return;
            }
            from = new TimeOnly(23, 0);
            to = new TimeOnly(6, 0);
        }

        Result.Zaptec.Username = UsernameBox.Text.Trim();
        Result.Zaptec.Password = PasswordBox.Password;
        Result.Zaptec.ChargerId = ChargerCombo.SelectedValue as string ?? "";
        Result.Zaptec.PollingEnabled = PollingCheck.IsChecked == true;
        Result.Zaptec.PollIntervalMinutes = interval;
        Result.Schedule.Enabled = scheduleOn;
        Result.Schedule.StartAtFrom = StartAtFromCheck.IsChecked == true;
        Result.Schedule.PauseAtTo = PauseAtToCheck.IsChecked == true;
        Result.Schedule.From = from.ToString("HH:mm");
        Result.Schedule.To = to.ToString("HH:mm");

        var price = Result.Price ??= new();
        price.IncludeVat = IncludeVatCheck.IsChecked == true;
        if (_priceListsLoaded) // otherwise keep the previous choices rather than wiping them
        {
            var supplier = SupplierCombo.SelectedItem as Supplier;
            var hasSupplier = supplier is not null && supplier != NoSupplier;
            price.SupplierId = hasSupplier ? supplier!.Id : "";
            price.CustomerGroupId = hasSupplier ? (CustomerGroupCombo.SelectedItem as CustomerGroup)?.Id ?? "" : "";
            price.CompanyName = hasSupplier && CompanyCombo.SelectedItem is Company c && c != NoCompany ? c.Name : "";
            price.ProductId = price.CompanyName.Length > 0 ? (ProductCombo.SelectedItem as Product)?.Id ?? "" : "";
        }

        try
        {
            Result.Save();
        }
        catch (Exception ex)
        {
            Fail(-1, "Could not save: " + ex.Message);
            return;
        }

        DialogResult = true;
    }

    // ---------- Electricity price (stromligning.dk) ----------

    private static readonly Supplier NoSupplier = new() { Name = "(none – show kWh only)" };
    private static readonly Company NoCompany = new() { Name = "(none – spot price without surcharge)" };
    private bool _priceListsLoaded;

    private async Task LoadPriceListsAsync()
    {
        var p = Result.Price!;
        PriceStatus.Text = "Loading grid and electricity companies…";
        SupplierCombo.IsEnabled = CompanyCombo.IsEnabled = false;
        try
        {
            using var client = new StromligningClient(userAgent: ElectricityPrices.UserAgent);
            var suppliersTask = client.GetSuppliersAsync(CancellationToken.None);
            var companiesTask = client.GetCompaniesAsync(CancellationToken.None);
            await Task.WhenAll(suppliersTask, companiesTask);

            var cmp = StringComparer.Create(CultureInfo.GetCultureInfo("da-DK"), ignoreCase: true);
            SupplierCombo.Items.Add(NoSupplier);
            foreach (var s in suppliersTask.Result.OrderBy(s => s.Name, cmp))
                SupplierCombo.Items.Add(s);
            CompanyCombo.Items.Add(NoCompany);
            foreach (var c in companiesTask.Result.Where(c => c.SpotProducts.Any()).OrderBy(c => c.Name, cmp))
                CompanyCombo.Items.Add(c);

            SupplierCombo.SelectedItem = SupplierCombo.Items.OfType<Supplier>().FirstOrDefault(s => s.Id == p.SupplierId) ?? NoSupplier;
            if (CustomerGroupCombo.Items.OfType<CustomerGroup>().FirstOrDefault(g => g.Id == p.CustomerGroupId) is { } group)
                CustomerGroupCombo.SelectedItem = group;
            CompanyCombo.SelectedItem = CompanyCombo.Items.OfType<Company>().FirstOrDefault(c => c.Name == p.CompanyName) ?? NoCompany;
            if (ProductCombo.Items.OfType<Product>().FirstOrDefault(x => x.Id == p.ProductId) is { } product)
                ProductCombo.SelectedItem = product;

            _priceListsLoaded = true;
            PriceStatus.Text = p.IsConfigured && !HasSavedPrice()
                ? "Filled in from your StromForbrug/StromPriser settings. Press Save to keep it."
                : "";
        }
        catch (Exception ex)
        {
            PriceStatus.Text = "Could not load the lists from stromligning.dk: " + ex.Message;
        }
        finally
        {
            SupplierCombo.IsEnabled = true;
            UpdatePriceEnabledStates();
        }
    }

    /// <summary>True when the user's settings file already holds price choices (not borrowed from another app).</summary>
    private static bool HasSavedPrice()
    {
        try
        {
            return File.Exists(AppSettings.UserSettingsPath) &&
                   File.ReadAllText(AppSettings.UserSettingsPath).Contains("\"Price\"", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    private void SupplierCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CustomerGroupCombo.Items.Clear();
        if (SupplierCombo.SelectedItem is Supplier s && s != NoSupplier)
        {
            foreach (var g in s.CustomerGroups)
                CustomerGroupCombo.Items.Add(g);
            if (CustomerGroupCombo.Items.Count > 0)
            {
                var i = s.CustomerGroups.FindIndex(g => g.Default);
                CustomerGroupCombo.SelectedIndex = i >= 0 ? i : 0;
            }
        }
        UpdatePriceEnabledStates();
    }

    private void CompanyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ProductCombo.Items.Clear();
        if (CompanyCombo.SelectedItem is Company c && c != NoCompany)
        {
            foreach (var product in c.SpotProducts)
                ProductCombo.Items.Add(product);
            if (ProductCombo.Items.Count > 0)
                ProductCombo.SelectedIndex = 0;
        }
        UpdatePriceEnabledStates();
    }

    private void UpdatePriceEnabledStates()
    {
        var hasSupplier = SupplierCombo.SelectedItem is Supplier s && s != NoSupplier;
        CustomerGroupCombo.IsEnabled = CompanyCombo.IsEnabled = IncludeVatCheck.IsEnabled = hasSupplier;
        ProductCombo.IsEnabled = hasSupplier && ProductCombo.Items.Count > 0;
    }

    private void Fail(int tab, string message)
    {
        if (tab >= 0) Tabs.SelectedIndex = tab;
        ValidationText.Text = message;
    }

    private const string RepoUrl = "https://github.com/kreaton/ZaptecCharger";

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        var updates = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
        if (!updates.IsInstalled)
        {
            UpdateStatus.Text = "Updates only work in the installed app.";
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        try
        {
            var newVersion = await updates.CheckForUpdatesAsync();
            if (newVersion is null)
            {
                UpdateStatus.Text = "You have the latest version.";
                return;
            }

            var version = newVersion.TargetFullRelease.Version;
            var answer = MessageBox.Show(this,
                $"Version {version} is available. Download and install it now? The app restarts afterwards.",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                UpdateStatus.Text = $"Version {version} is available.";
                return;
            }

            UpdateStatus.Text = $"Downloading {version}…";
            await updates.DownloadUpdatesAsync(newVersion);
            updates.ApplyUpdatesAndRestart(newVersion);
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Update failed: " + ex.Message;
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
