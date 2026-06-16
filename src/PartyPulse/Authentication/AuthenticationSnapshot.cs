using System;
namespace PartyPulse.Authentication;

public sealed record AuthenticationSnapshot(
    AuthenticationStatus Status,
    string Message,
    DateTimeOffset? AccessTokenExpiresAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt)
{
    public static AuthenticationSnapshot Disconnected { get; } = new(
        AuthenticationStatus.Disconnected,
        "Not connected.",
        null,
        null,
        null);
}
