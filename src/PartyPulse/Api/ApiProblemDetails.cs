namespace PartyPulse.Api;

public sealed class ApiProblemDetails
{
    public string? Title { get; init; }

    public string? Detail { get; init; }

    public string? Code { get; init; }

    public string? TraceId { get; init; }
}
