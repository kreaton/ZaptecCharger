using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Zaptec.Api;

/// <summary>
/// Minimal client for the Zaptec Cloud API (https://api.zaptec.com).
/// Handles OAuth password-grant token caching and exposes charger list, state and pause/resume.
/// </summary>
public sealed class ZaptecClient : IDisposable
{
    public const string DefaultBaseUrl = "https://api.zaptec.com";

    // Command ids per https://docs.zaptec.com (sendCommand).
    private const int CmdStopChargingFinal = 506; // pause / stop
    private const int CmdResumeCharging = 507;    // resume / start

    // State observation ids.
    private const int ObsTotalChargePower = 513;
    private const int ObsSessionEnergy = 553;
    private const int ObsOperationMode = 710;
    private const int ObsFinalStopActive = 718;
    private const int ObsNextScheduleEvent = 763;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _username;
    private readonly string _password;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public ZaptecClient(string username, string password, HttpClient? httpClient = null)
    {
        _username = username;
        _password = password;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { BaseAddress = new Uri(DefaultBaseUrl) };
        _http.BaseAddress ??= new Uri(DefaultBaseUrl);
    }

    public async Task<IReadOnlyList<ZaptecCharger>> GetChargersAsync(CancellationToken ct = default)
    {
        // Roles=3 => user (1) + owner (2) chargers.
        using var resp = await SendAsync(HttpMethod.Get, "/api/chargers?Roles=3", ct);
        var page = await resp.Content.ReadFromJsonAsync<ChargerPage>(Json, ct);
        return page?.Data ?? [];
    }

    public async Task<ChargerState> GetStateAsync(string chargerId, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"/api/chargers/{chargerId}/state", ct);
        var items = await resp.Content.ReadFromJsonAsync<List<Observation>>(Json, ct) ?? [];
        var values = items
            .GroupBy(o => o.StateId)
            .ToDictionary(g => g.Key, g => g.Last().ValueAsString);

        return new ChargerState
        {
            Mode = (ChargerOperationMode)(int)Num(values, ObsOperationMode),
            FinalStopActive = Num(values, ObsFinalStopActive) >= 1,
            PowerWatts = Num(values, ObsTotalChargePower),
            SessionEnergyKwh = Num(values, ObsSessionEnergy),
            NextScheduleEvent = ParseScheduleEvent(values.GetValueOrDefault(ObsNextScheduleEvent)),
            RetrievedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>Pause charging (command 506). Only valid while charging.</summary>
    public Task StopChargingAsync(string chargerId, CancellationToken ct = default) =>
        SendCommandAsync(chargerId, CmdStopChargingFinal, ct);

    /// <summary>Resume charging (command 507). Only valid when paused via the API.</summary>
    public Task StartChargingAsync(string chargerId, CancellationToken ct = default) =>
        SendCommandAsync(chargerId, CmdResumeCharging, ct);

    private async Task SendCommandAsync(string chargerId, int commandId, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Post, $"/api/chargers/{chargerId}/sendCommand/{commandId}", ct);
    }

    /// <summary>
    /// Observation 763 is undocumented in detail. Accept it only if it reads as a future date/time;
    /// anything else (empty, "0", an unknown format) is treated as "no scheduled event".
    /// </summary>
    private static DateTimeOffset? ParseScheduleEvent(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var t))
            return null;
        return t > DateTimeOffset.UtcNow && t < DateTimeOffset.UtcNow.AddDays(8) ? t : null;
    }

    private static string? TryGetDetails(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("Details", out var d) &&
                   d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString())
                ? d.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double Num(Dictionary<int, string?> values, int id) =>
        values.TryGetValue(id, out var s) &&
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    // ---- HTTP plumbing -------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(forceRefresh: attempt > 0, ct);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (method == HttpMethod.Post)
                req.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ZaptecApiException("Could not reach Zaptec: " + ex.Message, null, ex);
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                resp.Dispose();
                continue; // token may have been revoked; refresh once and retry
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var code = resp.StatusCode;
                resp.Dispose();
                // Zaptec errors look like {"Code":528,"Details":"Charging is not Paused nor Scheduled; ..."}.
                var details = TryGetDetails(body);
                throw new ZaptecApiException(
                    details is not null ? $"Zaptec: {details}" : $"Zaptec returned {(int)code} {code}. {body}".Trim(),
                    code);
            }

            return resp;
        }
    }

    private async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token is not null && DateTimeOffset.UtcNow < _tokenExpires)
                return _token;

            using var req = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["username"] = _username,
                    ["password"] = _password,
                }),
            };

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new ZaptecApiException(
                    resp.StatusCode == HttpStatusCode.BadRequest
                        ? "Login failed - check username and password in the settings file."
                        : $"Login failed with {(int)resp.StatusCode} {resp.StatusCode}.",
                    resp.StatusCode);
            }

            var tok = await resp.Content.ReadFromJsonAsync<TokenResponse>(Json, ct)
                      ?? throw new ZaptecApiException("Empty token response.");
            _token = tok.Access_Token;
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, tok.Expires_In - 60));
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public void Dispose()
    {
        _tokenLock.Dispose();
        if (_ownsHttp) _http.Dispose();
    }

    // ---- DTOs ---------------------------------------------------------------------------

    private sealed class TokenResponse
    {
        public string Access_Token { get; set; } = "";
        public int Expires_In { get; set; }
    }

    private sealed class ChargerPage
    {
        public List<ZaptecCharger> Data { get; set; } = [];
    }

    private sealed class Observation
    {
        public int StateId { get; set; }
        public string? ValueAsString { get; set; }
    }
}
