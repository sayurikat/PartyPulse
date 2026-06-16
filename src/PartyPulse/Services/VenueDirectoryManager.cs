using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Models;

namespace PartyPulse.Services;

public enum VenueDirectoryStatus
{
    Idle,
    LookingUp,
    Added,
    Failed,
}

public sealed record VenueDirectorySnapshot(VenueDirectoryStatus Status, string Message)
{
    public static VenueDirectorySnapshot Idle { get; } = new(
        VenueDirectoryStatus.Idle,
        "Add a venue by public code or by your current housing location.");
}

public sealed class VenueDirectoryManager : IDisposable
{
    private readonly Configuration configuration;
    private readonly PartyPulseApiClient apiClient;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object snapshotLock = new();
    private VenueDirectorySnapshot snapshot = VenueDirectorySnapshot.Idle;

    public VenueDirectoryManager(
        Configuration configuration,
        PartyPulseApiClient apiClient,
        IFramework framework,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.apiClient = apiClient;
        this.framework = framework;
        this.log = log;
    }

    public VenueDirectorySnapshot GetSnapshot()
    {
        lock (snapshotLock)
        {
            return snapshot;
        }
    }

    public Task<ApiResult<VenueConnectionConfiguration>> AddByCodeAsync(
        string venueCode,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        if (!VenueConnectionConfiguration.TryNormalizeVenueCode(venueCode, out var normalizedCode))
        {
            var failure = new ApiFailure(
                ApiFailureKind.Validation,
                "INVALID_VENUE_CODE",
                "Venue code must use the format PULSE-XXXXXX.");
            SetSnapshot(VenueDirectoryStatus.Failed, failure.Message);
            return Task.FromResult(ApiResult<VenueConnectionConfiguration>.Failed(failure));
        }

        return ExecuteLookupAsync(
            apiBaseUrl,
            cancellationToken,
            baseUri => apiClient.GetPublicVenueByCodeAsync(baseUri, normalizedCode, cancellationToken),
            $"Looking up {normalizedCode}...");
    }

    public Task<ApiResult<VenueConnectionConfiguration>> AddByAddressAsync(
        VenueAddress address,
        string apiBaseUrl,
        CancellationToken cancellationToken) =>
        ExecuteLookupAsync(
            apiBaseUrl,
            cancellationToken,
            baseUri => apiClient.GetPublicVenueByAddressAsync(baseUri, address, cancellationToken),
            $"Looking up {address.DisplayText}...");

    public void Dispose() => operationGate.Dispose();

    private async Task<ApiResult<VenueConnectionConfiguration>> ExecuteLookupAsync(
        string apiBaseUrl,
        CancellationToken cancellationToken,
        Func<Uri, Task<ApiResult<PublicVenueResponse>>> lookup,
        string progressMessage)
    {
        if (!PartyPulseApiClient.TryCreateBaseUri(apiBaseUrl, out var baseUri, out var urlError))
        {
            var failure = new ApiFailure(ApiFailureKind.Validation, "INVALID_API_URL", urlError);
            SetSnapshot(VenueDirectoryStatus.Failed, failure.Message);
            return ApiResult<VenueConnectionConfiguration>.Failed(failure);
        }

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            SetSnapshot(VenueDirectoryStatus.LookingUp, progressMessage);
            var result = await lookup(baseUri!);
            if (!result.Success || result.Value is null)
            {
                var failure = result.Failure ?? new ApiFailure(
                    ApiFailureKind.InvalidResponse,
                    "VENUE_LOOKUP_FAILED",
                    "The venue lookup failed.");
                SetSnapshot(VenueDirectoryStatus.Failed, failure.Message);
                return ApiResult<VenueConnectionConfiguration>.Failed(failure);
            }

            VenueConnectionConfiguration? savedVenue = null;
            await framework.RunOnTick(
                () => savedVenue = Upsert(result.Value),
                cancellationToken: cancellationToken);

            if (savedVenue is null)
            {
                throw new InvalidOperationException("The venue was returned by the API but was not saved.");
            }

            SetSnapshot(
                VenueDirectoryStatus.Added,
                $"Added {savedVenue.DisplayLabel}: {savedVenue.AddressDisplay}.");
            log.Information(
                "Added or updated public venue {VenueCode} ({VenueId}) in plugin configuration.",
                savedVenue.VenueCode,
                savedVenue.VenueId);
            return ApiResult<VenueConnectionConfiguration>.Succeeded(savedVenue);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private VenueConnectionConfiguration Upsert(PublicVenueResponse response)
    {
        var normalizedCode = VenueConnectionConfiguration.NormalizeVenueCode(response.VenueCode);
        var venue = configuration.VenueConnections.FirstOrDefault(x => x.VenueId == response.VenueId)
            ?? configuration.VenueConnections.FirstOrDefault(x =>
                string.Equals(
                    VenueConnectionConfiguration.NormalizeVenueCode(x.VenueCode),
                    normalizedCode,
                    StringComparison.Ordinal));

        if (venue is null)
        {
            venue = new VenueConnectionConfiguration();
            configuration.VenueConnections.Add(venue);
        }

        venue.ApplyPublicVenue(response);
        configuration.SelectedVenueProfileId = venue.ProfileId;
        configuration.Normalize();
        configuration.Save();
        return venue;
    }

    private void SetSnapshot(VenueDirectoryStatus status, string message)
    {
        lock (snapshotLock)
        {
            snapshot = new VenueDirectorySnapshot(status, message);
        }
    }
}
