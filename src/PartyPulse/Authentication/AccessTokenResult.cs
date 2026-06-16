using PartyPulse.Api;

namespace PartyPulse.Authentication;

public sealed record AccessTokenResult(bool Success, string? AccessToken, ApiFailure? Failure)
{
    public static AccessTokenResult Succeeded(string accessToken) => new(true, accessToken, null);

    public static AccessTokenResult Failed(ApiFailure failure) => new(false, null, failure);
}
