using PartyPulse.Api;

namespace PartyPulse.SelfService;

public enum SelfServiceStatus
{
    NotLoaded,
    Loading,
    Ready,
    Failed
}

public sealed record SelfServiceSnapshot(
    SelfServiceStatus Status,
    string Message,
    SelfServiceViewResponse? View,
    DevicePairingCodeResponse? LatestPairingCode)
{
    public static SelfServiceSnapshot NotLoaded { get; } = new(
        SelfServiceStatus.NotLoaded,
        "Self-service data has not been loaded.",
        null,
        null);
}
