using System;

namespace PartyPulse.Api;

public sealed record RedeemInviteRequest(
    string VenueCode,
    string CharacterName,
    string WorldName,
    string DeviceName,
    string InviteCode);

public sealed record RecoverAccountRequest(
    string VenueCode,
    string CharacterName,
    string WorldName,
    string DeviceName,
    string RecoveryCode);

public sealed record DeviceAuthenticationResponse(
    int DeviceId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    string TokenType);

public sealed record AuthenticationConfirmationResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string TokenType);
