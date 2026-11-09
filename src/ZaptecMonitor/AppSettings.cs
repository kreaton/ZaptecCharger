using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZaptecMonitor;

public sealed class ZaptecSettings
{
    public string Username { get; set; } = "";

    /// <summary>Plain-text password. Only used in the hand-edited appsettings.json next to the exe.</summary>
    public string Password { get; set; } = "";

    /// <summary>DPAPI-protected password (current Windows user). Written by the settings screen.</summary>
    public string ProtectedPassword { get; set; } = "";

    /// <summary>Optional. Leave empty to use the first charger on the account.</summary>
    public string ChargerId { get; set; } = "";

    public bool PollingEnabled { get; set; } = true;

    public int PollIntervalMinutes { get; set; } = 15;

    [JsonIgnore]
    public bool HasCredentials => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
}

/// <summary>Charging window (From–To) and what the app does at its edges while a car is connected.</summary>
public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }
    public string From { get; set; } = "23:00";
    public string To { get; set; } = "06:00";

    /// <summary>Resume a paused session when the window opens (at From).</summary>
    public bool StartAtFrom { get; set; } = true;

    /// <summary>Pause charging when the window closes (at To) and keep it paused until From.</summary>
    public bool PauseAtTo { get; set; } = true;
}

/// <summary>
/// Where the electricity prices for the history's cost estimate come from (stromligning.dk).
/// The grid company decides tariffs and price area, the trading company's product the surcharge.
/// </summary>
public sealed class PriceSettings
{
    /// <summary>Grid company (netselskab) id at stromligning.dk. Empty: no prices, kWh only.</summary>
    public string SupplierId { get; set; } = "";
    public string CustomerGroupId { get; set; } = "";

    /// <summary>Trading company (elhandelsselskab). It has no id in the API, so it's kept by name.</summary>
    public string CompanyName { get; set; } = "";
    public string ProductId { get; set; } = "";

    public bool IncludeVat { get; set; } = true;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SupplierId);

    /// <summary>
    /// The same choices made in the StromForbrug or StromPriser apps (%APPDATA%\…\settings.json),
    /// so the user doesn't have to pick them again. Null if neither app has been set up.
    /// </summary>
    public static PriceSettings? FromSiblingApps()
    {
        foreach (var app in new[] { "StromForbrug", "StromPriser" })
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), app, "settings.json");
                if (!File.Exists(path)) continue;

                var p = JsonSerializer.Deserialize<PriceSettings>(File.ReadAllText(path), AppSettings.ReadOptions);
                if (p is null || !p.IsConfigured) continue;
                p.CustomerGroupId ??= "";
                p.CompanyName ??= "";
                p.ProductId ??= "";
                return p;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Unreadable: try the next app, or start blank.
            }
        }
        return null;
    }
}

public sealed class AppSettings
{
    public const int MinPollMinutes = 1;

    internal static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public ZaptecSettings Zaptec { get; set; } = new();
    public ScheduleSettings Schedule { get; set; } = new();

    /// <summary>Null in files written before prices existed; Load fills it in.</summary>
    public PriceSettings? Price { get; set; }

    /// <summary>Per-user settings written by the settings screen. Takes precedence over appsettings.json.</summary>
    public static string UserSettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZaptecMonitor", "settings.json");

    /// <summary>Hand-edited defaults shipped next to the exe.</summary>
    public static string DefaultSettingsPath { get; } = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>The file the current settings were read from (null if none existed).</summary>
    [JsonIgnore]
    public string? LoadedFrom { get; private set; }

    public static AppSettings Load()
    {
        var path = File.Exists(UserSettingsPath) ? UserSettingsPath
                 : File.Exists(DefaultSettingsPath) ? DefaultSettingsPath
                 : null;

        var s = path is null
            ? new AppSettings()
            : JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), ReadOptions) ?? new AppSettings();

        s.Zaptec ??= new();
        s.Schedule ??= new();
        // Never set up here: borrow the choices from StromForbrug/StromPriser until the user saves.
        s.Price ??= PriceSettings.FromSiblingApps() ?? new();
        s.LoadedFrom = path;

        if (!string.IsNullOrEmpty(s.Zaptec.ProtectedPassword))
        {
            try { s.Zaptec.Password = Unprotect(s.Zaptec.ProtectedPassword); }
            catch (CryptographicException) { s.Zaptec.Password = ""; } // written by another Windows user/machine
        }

        s.Zaptec.PollIntervalMinutes = Math.Max(MinPollMinutes, s.Zaptec.PollIntervalMinutes);
        return s;
    }

    /// <summary>Writes the per-user settings file, with the password DPAPI-protected.</summary>
    public void Save()
    {
        var copy = Clone();
        copy.Zaptec.ProtectedPassword = string.IsNullOrEmpty(Zaptec.Password) ? "" : Protect(Zaptec.Password);
        copy.Zaptec.Password = "";

        Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsPath)!);
        File.WriteAllText(UserSettingsPath, JsonSerializer.Serialize(copy, WriteOptions));
        LoadedFrom = UserSettingsPath;
    }

    public AppSettings Clone()
    {
        var c = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, WriteOptions), ReadOptions)!;
        c.LoadedFrom = LoadedFrom;
        return c;
    }

    private static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string b64) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser));
}
