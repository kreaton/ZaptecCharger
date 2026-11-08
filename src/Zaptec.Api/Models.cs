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
