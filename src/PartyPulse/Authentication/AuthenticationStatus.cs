namespace PartyPulse.Authentication;

public enum AuthenticationStatus
{
    NotConfigured,
    Disconnected,
    WaitingForPlayer,
    Connecting,
    Connected,
    CharacterNotLinked,
    Expired,
    Failed,
}
