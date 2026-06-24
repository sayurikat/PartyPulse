using System;
using System.Collections.Generic;
using System.Linq;
using PartyPulse.Api;

namespace PartyPulse.Shoutrunner;

public sealed record ShoutrunnerDestination(
    int WorldId,
    string WorldName,
    string DatacenterName,
    string RegionName,
    string CityName)
{
    public string Key => CreateKey(WorldId, CityName);

    public static string CreateKey(int worldId, string cityName) =>
        $"{worldId}:{cityName.Trim().ToLowerInvariant()}";
}

public sealed record ShoutrunnerCurrentLocation(
    string WorldName,
    string? CityName);

public sealed record ShoutrunnerRouteSnapshot(
    int CompletedLocations,
    int TotalLocations,
    ShoutrunnerDestination? NextDestination,
    ShoutrunnerCurrentLocation? CurrentLocation,
    bool IsAtNextDestination,
    bool TravelCooldownActive,
    TimeSpan TravelCooldownRemaining);

public static class ShoutrunnerRoutePlanner
{
    public static readonly IReadOnlyList<string> Cities =
    [
        "Limsa Lominsa Lower Decks",
        "New Gridania",
        "Ul'dah - Steps of Nald"
    ];

    public static ShoutrunnerRouteSnapshot Build(
        IReadOnlyList<ShoutrunnerWorldSummary> worlds,
        ShoutrunnerProfileConfiguration profile,
        ShoutrunnerCurrentLocation? currentLocation,
        DateTimeOffset nowUtc)
    {
        var selected = worlds
            .Where(world => profile.SelectedWorldNames.Contains(world.WorldName, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var completed = profile.CompletedDestinationKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var total = selected.Length * Cities.Count;
        var completedCount = selected.Sum(world => Cities.Count(city =>
            completed.Contains(ShoutrunnerDestination.CreateKey(world.WorldId, city))));
        var currentDatacenter = currentLocation is null
            ? null
            : worlds.FirstOrDefault(world =>
                string.Equals(world.WorldName, currentLocation.WorldName, StringComparison.OrdinalIgnoreCase))?.DatacenterName;
        var next = FindNext(selected, completed, currentLocation, currentDatacenter);
        var atDestination = next is not null && currentLocation is not null &&
                            string.Equals(next.WorldName, currentLocation.WorldName, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(next.CityName, currentLocation.CityName, StringComparison.OrdinalIgnoreCase);
        var remaining = profile.NextTravelAllowedAtUtc is { } allowedAt && allowedAt > nowUtc
            ? allowedAt - nowUtc
            : TimeSpan.Zero;

        return new ShoutrunnerRouteSnapshot(
            completedCount,
            total,
            next,
            currentLocation,
            atDestination,
            remaining > TimeSpan.Zero,
            remaining);
    }

    private static ShoutrunnerDestination? FindNext(
        IReadOnlyList<ShoutrunnerWorldSummary> selected,
        IReadOnlySet<string> completed,
        ShoutrunnerCurrentLocation? currentLocation,
        string? currentDatacenter)
    {
        if (selected.Count == 0) return null;

        var currentWorld = currentLocation is null
            ? null
            : selected.FirstOrDefault(world =>
                string.Equals(world.WorldName, currentLocation.WorldName, StringComparison.OrdinalIgnoreCase));
        if (currentWorld is not null)
        {
            var currentCity = Cities.FirstOrDefault(city =>
                string.Equals(city, currentLocation!.CityName, StringComparison.OrdinalIgnoreCase) &&
                !completed.Contains(ShoutrunnerDestination.CreateKey(currentWorld.WorldId, city)));
            if (currentCity is not null)
                return Create(currentWorld, currentCity);

            var city = Cities.FirstOrDefault(value =>
                !completed.Contains(ShoutrunnerDestination.CreateKey(currentWorld.WorldId, value)));
            if (city is not null)
                return Create(currentWorld, city);
        }

        var orderedWorlds = selected
            .Where(world => currentWorld is null || world.WorldId != currentWorld.WorldId)
            .OrderBy(world => currentDatacenter is not null &&
                              string.Equals(world.DatacenterName, currentDatacenter, StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1)
            .ThenBy(world => world.DatacenterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase);

        foreach (var world in orderedWorlds)
        {
            var city = Cities.FirstOrDefault(value =>
                !completed.Contains(ShoutrunnerDestination.CreateKey(world.WorldId, value)));
            if (city is not null)
                return Create(world, city);
        }

        return null;
    }

    private static ShoutrunnerDestination Create(ShoutrunnerWorldSummary world, string city) =>
        new(world.WorldId, world.WorldName, world.DatacenterName, world.RegionName, city);
}
