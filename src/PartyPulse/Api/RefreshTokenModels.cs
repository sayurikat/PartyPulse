using System;
namespace PartyPulse.Api;

public sealed record RefreshTokenRequest(
    string VenueCode,
    string CharacterName,
    string WorldName,
    int DeviceId,
    string RefreshToken);

public sealed record RefreshTokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    string TokenType);
