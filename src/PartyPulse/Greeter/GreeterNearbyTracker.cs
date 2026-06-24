using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using PartyPulse.Api;

namespace PartyPulse.Greeter;

public sealed record NearbyGreeterPlayer(
    ulong GameObjectId,
    string CharacterName,
    string WorldName,
    float DistanceSquared)
{
    public string DisplayName => $"{CharacterName} @ {WorldName}";
}

public sealed class GreeterNearbyTracker
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private readonly IObjectTable objectTable;
    private readonly Dictionary<string, NearbyGreeterPlayer> nearby = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> submitted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    private Guid profileId;
    private long openingId;
    private DateTimeOffset nextScanAt = DateTimeOffset.MinValue;

    public GreeterNearbyTracker(IObjectTable objectTable) => this.objectTable = objectTable;

    public int NearbyCount => nearby.Count;

    public void Prepare(Guid venueProfileId, long currentOpeningId)
    {
        if (profileId == venueProfileId && openingId == currentOpeningId)
            return;

        profileId = venueProfileId;
        openingId = currentOpeningId;
        nearby.Clear();
        submitted.Clear();
        pending.Clear();
        nextScanAt = DateTimeOffset.MinValue;
    }

    public void ScanIfDue()
    {
        if (openingId <= 0 || DateTimeOffset.UtcNow < nextScanAt)
            return;
        nextScanAt = DateTimeOffset.UtcNow.Add(ScanInterval);
        Scan();
    }

    public IReadOnlyList<GreeterObservationRequest> TakeUnsubmittedObservations()
    {
        var observations = pending
            .Where(key => !submitted.Contains(key))
            .Take(100)
            .Select(key => nearby.TryGetValue(key, out var player)
                ? new GreeterObservationRequest(player.CharacterName, player.WorldName)
                : null)
            .Where(static item => item is not null)
            .Cast<GreeterObservationRequest>()
            .ToArray();

        foreach (var observation in observations)
            submitted.Add(Key(observation.CharacterName, observation.WorldName));
        return observations;
    }

    public void MarkSubmitted(IEnumerable<GreeterObservationRequest> observations)
    {
        foreach (var observation in observations)
            pending.Remove(Key(observation.CharacterName, observation.WorldName));
    }

    public void ReleaseSubmission(IEnumerable<GreeterObservationRequest> observations)
    {
        foreach (var observation in observations)
            submitted.Remove(Key(observation.CharacterName, observation.WorldName));
    }

    public bool TryGetNearby(string characterName, string worldName, out NearbyGreeterPlayer? player)
    {
        if (nearby.TryGetValue(Key(characterName, worldName), out var value))
        {
            player = value;
            return true;
        }
        player = null;
        return false;
    }

    public IReadOnlyList<NearbyGreeterPlayer> GetNearby() =>
        nearby.Values.OrderBy(value => value.DistanceSquared).ToArray();

    public void Clear()
    {
        profileId = Guid.Empty;
        openingId = 0;
        nearby.Clear();
        submitted.Clear();
        pending.Clear();
        nextScanAt = DateTimeOffset.MinValue;
    }

    private void Scan()
    {
        var next = new Dictionary<string, NearbyGreeterPlayer>(StringComparer.OrdinalIgnoreCase);
        var local = objectTable.LocalPlayer;
        var localId = local?.GameObjectId ?? 0;
        var localPosition = local?.Position;

        foreach (var battleChara in objectTable.PlayerObjects)
        {
            if (battleChara is not IPlayerCharacter player ||
                !player.IsValid() ||
                !player.IsTargetable ||
                player.GameObjectId == 0 ||
                player.GameObjectId == localId)
                continue;

            var name = player.Name.TextValue.Trim();
            var world = player.HomeWorld.IsValid
                ? player.HomeWorld.Value.Name.ToString().Trim()
                : string.Empty;
            if (name.Length == 0 || world.Length == 0)
                continue;

            var distance = localPosition is { } position
                ? Vector3.DistanceSquared(position, player.Position)
                : player.ObjectIndex;
            var candidate = new NearbyGreeterPlayer(player.GameObjectId, name, world, distance);
            var key = Key(name, world);
            if (!next.TryGetValue(key, out var existing) || distance < existing.DistanceSquared)
                next[key] = candidate;
        }

        nearby.Clear();
        foreach (var pair in next)
        {
            nearby[pair.Key] = pair.Value;
            if (!submitted.Contains(pair.Key))
                pending.Add(pair.Key);
        }
    }

    private static string Key(string name, string world) => $"{name.Trim()}\n{world.Trim()}";
}
