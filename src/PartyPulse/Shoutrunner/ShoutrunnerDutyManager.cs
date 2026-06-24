using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.Vip;

namespace PartyPulse.Shoutrunner;

public sealed record ShoutrunnerActionResult(bool Success, string Message)
{
    public static ShoutrunnerActionResult Ok(string message) => new(true, message);
    public static ShoutrunnerActionResult Fail(string message) => new(false, message);
}

public sealed record ShoutrunnerReportBatch(
    IReadOnlyList<Guid> ClientEntryIds,
    ReportShoutrunnerDutyRequest Request);

public sealed class ShoutrunnerDutyManager(
    Configuration configuration,
    ICommandManager commandManager,
    IPlayerState playerState,
    IClientState clientState,
    IDataManager dataManager,
    GameMacroExecutionService macroExecutionService)
{
    private static readonly TimeSpan PostShoutTravelDelay = TimeSpan.FromSeconds(10);
    private readonly object syncRoot = new();

    public ShoutrunnerProfileConfiguration GetProfile(VenueConnectionConfiguration venue) =>
        configuration.GetShoutrunnerProfile(venue.ProfileId);

    public void SetWorldSelected(
        VenueConnectionConfiguration venue,
        ShoutrunnerWorldSummary world,
        bool selected)
    {
        lock (syncRoot)
        {
            var profile = GetProfile(venue);
            profile.SelectedWorldNames.RemoveAll(value =>
                string.Equals(value, world.WorldName, StringComparison.OrdinalIgnoreCase));
            if (selected)
            {
                profile.SelectedWorldNames.Add(world.WorldName.Trim());
            }
            else
            {
                RemoveCompletedWorld(profile, world.WorldId);
            }

            profile.SelectedWorldNames = profile.SelectedWorldNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            configuration.Save();
        }
    }

    public void SetDatacenterSelected(
        VenueConnectionConfiguration venue,
        IEnumerable<ShoutrunnerWorldSummary> worlds,
        bool selected)
    {
        lock (syncRoot)
        {
            var profile = GetProfile(venue);
            var names = worlds.Select(static world => world.WorldName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            profile.SelectedWorldNames.RemoveAll(names.Contains);
            if (selected)
            {
                profile.SelectedWorldNames.AddRange(names);
            }
            else
            {
                foreach (var world in worlds)
                    RemoveCompletedWorld(profile, world.WorldId);
            }

            profile.SelectedWorldNames = profile.SelectedWorldNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            configuration.Save();
        }
    }

    public ShoutrunnerRouteSnapshot GetRouteSnapshot(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse context,
        ActiveShoutrunnerPublication publication)
    {
        EnsureOpening(venue, publication.OpeningId);
        return ShoutrunnerRoutePlanner.Build(
            context.Worlds,
            GetProfile(venue),
            TryGetCurrentLocation(out var location) ? location : null,
            DateTimeOffset.UtcNow);
    }

    public ShoutrunnerActionResult TravelNext(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse context,
        ActiveShoutrunnerPublication publication)
    {
        var route = GetRouteSnapshot(venue, context, publication);
        if (route.TotalLocations == 0)
            return ShoutrunnerActionResult.Fail("Select at least one world in Shoutrunner setup first.");
        if (route.NextDestination is null)
            return ShoutrunnerActionResult.Fail("All selected destinations are complete.");
        if (route.TravelCooldownActive)
            return ShoutrunnerActionResult.Fail($"Wait {Math.Ceiling(route.TravelCooldownRemaining.TotalSeconds)} more seconds before leaving.");
        if (route.IsAtNextDestination)
            return ShoutrunnerActionResult.Fail("You are already at the next destination. Use Shout before travelling onward.");

        var command = route.CurrentLocation is not null &&
                      string.Equals(route.CurrentLocation.WorldName, route.NextDestination.WorldName, StringComparison.OrdinalIgnoreCase)
            ? $"/li {route.NextDestination.CityName}"
            : $"/li {route.NextDestination.WorldName}";
        return commandManager.ProcessCommand(command)
            ? ShoutrunnerActionResult.Ok($"Started Lifestream travel: {command}")
            : ShoutrunnerActionResult.Fail("Lifestream did not accept the travel command. Make sure Lifestream is installed and loaded.");
    }

    public async Task<ShoutrunnerActionResult> ExecuteShoutAsync(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse context,
        ActiveShoutrunnerPublication publication,
        CancellationToken cancellationToken)
    {
        var route = GetRouteSnapshot(venue, context, publication);
        if (route.NextDestination is null)
            return ShoutrunnerActionResult.Fail("All selected destinations are complete.");
        if (!route.IsAtNextDestination)
            return ShoutrunnerActionResult.Fail("Travel to the displayed world and city before running the shout macro.");

        var execution = await macroExecutionService.ExecuteUntargetedAsync(publication.Text, cancellationToken);
        if (!execution.Success)
            return ShoutrunnerActionResult.Fail($"{execution.ErrorMessage} [{execution.ErrorCode}]");

        lock (syncRoot)
        {
            var profile = GetProfile(venue);
            if (!profile.CompletedDestinationKeys.Contains(route.NextDestination.Key, StringComparer.OrdinalIgnoreCase))
                profile.CompletedDestinationKeys.Add(route.NextDestination.Key);
            profile.NextTravelAllowedAtUtc = DateTimeOffset.UtcNow.Add(PostShoutTravelDelay);

            var updatedRoute = ShoutrunnerRoutePlanner.Build(
                context.Worlds,
                profile,
                route.CurrentLocation,
                DateTimeOffset.UtcNow);
            profile.PendingLogs.Add(CreateLog(
                publication.OpeningId,
                ShoutrunnerDutyEventTypes.Shout,
                updatedRoute.CompletedLocations,
                updatedRoute.TotalLocations,
                route.NextDestination,
                null));
            if (updatedRoute.TotalLocations > 0 &&
                updatedRoute.CompletedLocations == updatedRoute.TotalLocations)
            {
                profile.PendingLogs.Add(CreateLog(
                    publication.OpeningId,
                    ShoutrunnerDutyEventTypes.Completed,
                    updatedRoute.CompletedLocations,
                    updatedRoute.TotalLocations,
                    null,
                    null));
            }
            configuration.Save();
        }

        return ShoutrunnerActionResult.Ok("Shout macro started and the destination was marked complete.");
    }

    public ShoutrunnerActionResult Reset(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse context,
        ActiveShoutrunnerPublication publication,
        string reason)
    {
        var normalizedReason = reason.Trim();
        if (normalizedReason.Length == 0)
            return ShoutrunnerActionResult.Fail("Enter a reset reason.");

        lock (syncRoot)
        {
            var profile = GetProfile(venue);
            EnsureOpeningLocked(profile, publication.OpeningId);
            var route = ShoutrunnerRoutePlanner.Build(
                context.Worlds,
                profile,
                TryGetCurrentLocation(out var location) ? location : null,
                DateTimeOffset.UtcNow);
            profile.PendingLogs.Add(CreateLog(
                publication.OpeningId,
                ShoutrunnerDutyEventTypes.Reset,
                route.CompletedLocations,
                route.TotalLocations,
                null,
                normalizedReason));
            profile.CompletedDestinationKeys.Clear();
            profile.NextTravelAllowedAtUtc = null;
            configuration.Save();
        }

        return ShoutrunnerActionResult.Ok("Shoutrunner progress reset and logged locally.");
    }


    public ShoutrunnerActionResult CompleteRound(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse context,
        ActiveShoutrunnerPublication publication)
    {
        lock (syncRoot)
        {
            var profile = GetProfile(venue);
            EnsureOpeningLocked(profile, publication.OpeningId);
            var route = ShoutrunnerRoutePlanner.Build(
                context.Worlds,
                profile,
                TryGetCurrentLocation(out var location) ? location : null,
                DateTimeOffset.UtcNow);
            if (route.TotalLocations == 0)
                return ShoutrunnerActionResult.Fail("Select at least one world before completing a round.");
            if (route.CompletedLocations != route.TotalLocations)
                return ShoutrunnerActionResult.Fail("The current Shoutrunner round is not complete yet.");

            profile.CompletedDestinationKeys.Clear();
            // Keep the post-shout cooldown so starting the next round cannot cut off
            // the final macro. It expires normally after ten seconds.
            configuration.Save();
        }

        return ShoutrunnerActionResult.Ok("Shoutrunner round completed. Progress is ready for a new round.");
    }

    public ShoutrunnerActionResult ReturnToVenue(
        VenueConnectionConfiguration venue,
        OpeningPublicationOpeningSummary? opening)
    {
        var worldName = opening?.AddressWorldName ?? venue.AddressWorldName;
        var cityName = opening?.AddressCityName ?? venue.AddressCityName;
        var ward = opening?.AddressWard ?? venue.AddressWard;
        var plot = opening?.AddressPlot ?? venue.AddressPlot;
        if (string.IsNullOrWhiteSpace(worldName) ||
            string.IsNullOrWhiteSpace(cityName) ||
            ward is not (> 0) ||
            plot is not (> 0))
        {
            return ShoutrunnerActionResult.Fail("The current opening does not have a complete housing address.");
        }

        var command = $"/li {worldName.Trim()} {cityName.Trim()} {ward.Value} {plot.Value}";
        return commandManager.ProcessCommand(command)
            ? ShoutrunnerActionResult.Ok($"Started Lifestream travel back to {venue.DisplayLabel}: {command}")
            : ShoutrunnerActionResult.Fail("Lifestream did not accept the venue return command. Make sure Lifestream is installed and loaded.");
    }

    public ShoutrunnerReportBatch? CreateReportBatch(VenueConnectionConfiguration venue)
    {
        lock (syncRoot)
        {
            var entries = GetProfile(venue).PendingLogs
                .Where(static entry => entry.ClientEntryId != Guid.Empty)
                .Take(500)
                .ToArray();
            if (entries.Length == 0) return null;

            return new ShoutrunnerReportBatch(
                entries.Select(static entry => entry.ClientEntryId).ToArray(),
                new ReportShoutrunnerDutyRequest(entries.Select(static entry =>
                    new ShoutrunnerDutyLogEntryRequest(
                        entry.ClientEntryId,
                        entry.OpeningId,
                        entry.OccurredAtUtc,
                        entry.EventType,
                        entry.CompletedLocations,
                        entry.TotalLocations,
                        entry.WorldId,
                        entry.WorldName,
                        entry.DatacenterName,
                        entry.CityName,
                        entry.Reason)).ToArray()));
        }
    }

    public void ConfirmReported(VenueConnectionConfiguration venue, IReadOnlyCollection<Guid> clientEntryIds)
    {
        lock (syncRoot)
        {
            var ids = clientEntryIds.ToHashSet();
            GetProfile(venue).PendingLogs.RemoveAll(entry => ids.Contains(entry.ClientEntryId));
            configuration.Save();
        }
    }

    private void EnsureOpening(VenueConnectionConfiguration venue, long openingId)
    {
        lock (syncRoot)
        {
            var profile = GetProfile(venue);
            if (profile.ActiveOpeningId == openingId) return;
            EnsureOpeningLocked(profile, openingId);
            configuration.Save();
        }
    }

    private static void EnsureOpeningLocked(ShoutrunnerProfileConfiguration profile, long openingId)
    {
        if (profile.ActiveOpeningId == openingId) return;
        profile.ActiveOpeningId = openingId;
        profile.CompletedDestinationKeys.Clear();
        profile.NextTravelAllowedAtUtc = null;
    }

    private static void RemoveCompletedWorld(ShoutrunnerProfileConfiguration profile, int worldId)
    {
        var prefix = $"{worldId}:";
        profile.CompletedDestinationKeys.RemoveAll(key =>
            key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryGetCurrentLocation(out ShoutrunnerCurrentLocation? location)
    {
        location = null;
        if (!playerState.IsLoaded || !clientState.IsLoggedIn || !playerState.CurrentWorld.IsValid)
            return false;

        var worldName = playerState.CurrentWorld.Value.Name.ToString().Trim();
        if (worldName.Length == 0) return false;

        var territory = dataManager.GetExcelSheet<TerritoryType>().GetRow(clientState.TerritoryType);
        var placeName = territory.PlaceName.Value.Name.ToString().Trim();
        var cityName = ShoutrunnerRoutePlanner.Cities.FirstOrDefault(city =>
            string.Equals(city, placeName, StringComparison.OrdinalIgnoreCase));
        location = new ShoutrunnerCurrentLocation(worldName, cityName);
        return true;
    }

    private static ShoutrunnerLocalLogEntry CreateLog(
        long openingId,
        string eventType,
        int completed,
        int total,
        ShoutrunnerDestination? destination,
        string? reason) => new()
    {
        ClientEntryId = Guid.NewGuid(),
        OpeningId = openingId,
        OccurredAtUtc = DateTimeOffset.UtcNow,
        EventType = eventType,
        CompletedLocations = completed,
        TotalLocations = total,
        WorldId = destination?.WorldId,
        WorldName = destination?.WorldName,
        DatacenterName = destination?.DatacenterName,
        CityName = destination?.CityName,
        Reason = reason
    };
}
