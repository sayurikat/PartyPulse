using System;
using System.Net;

namespace PartyPulse.Api;

public enum ApiFailureKind
{
    Validation,
    Authentication,
    Permission,
    RateLimited,
    Unavailable,
    Transport,
    InvalidResponse,
    Unknown,
}

public sealed record ApiFailure(
    ApiFailureKind Kind,
    string Code,
    string Message,
    HttpStatusCode? StatusCode = null,
    string? TraceId = null,
    TimeSpan? RetryAfter = null);

public sealed record ApiResult<T>(bool Success, T? Value, ApiFailure? Failure)
{
    public static ApiResult<T> Succeeded(T value) => new(true, value, null);

    public static ApiResult<T> Failed(ApiFailure failure) => new(false, default, failure);
}
