using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record LinkCurrentCharacterRequest(
    string VenueCode,
    string CharacterName,
    string WorldName,
    int DeviceId,
    string RefreshToken);

public sealed record LinkCurrentCharacterResponse(int CharacterId);

public sealed record RedeemDevicePairingCodeRequest(
    string VenueCode,
    string CharacterName,
    string WorldName,
    string DeviceName,
    string PairingCode);

public sealed record SelfCharacterSummary(
    int CharacterId,
    string CharacterName,
    string WorldName,
    bool IsCurrent,
    bool IsMain)
{
    public string DisplayName => $"{CharacterName} @ {WorldName}";
}

public sealed record SelfServiceViewResponse(
    bool IsOwner,
    bool IsLastOwner,
    IReadOnlyList<SelfCharacterSummary> Characters);

public sealed record SelfServiceOperationResponse(bool Success);

public sealed record DevicePairingCodeResponse(
    string PairingCode,
    DateTimeOffset ExpiresAt);
