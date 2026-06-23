using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using PartyPulse.Api;

namespace PartyPulse.Vip;

public sealed record NearbyVipArrivalCharacter(
    int VipPlayerId,
    int CharacterId,
    ulong GameObjectId,
    string CharacterName,
    string WorldName)
{
    public string DisplayName => $"{CharacterName} @ {WorldName}";
}

/// <summary>
/// Dedicated cache for the opening-arrival workflow. It intentionally does not
/// share state with the general VIP nearby filter because the lifetimes and
/// server-observation semantics are different.
/// </summary>
public sealed class VipArrivalNearbyTracker
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private readonly IObjectTable objectTable;
    private readonly Dictionary<string, VipCharacterIdentity> vipByCharacter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, ObservedObject> observedObjects = new();
    private readonly Dictionary<int, NearbyCandidate> nearbyByVipPlayer = new();
    private readonly HashSet<int> submittedVipPlayers = new();
    private readonly Dictionary<int, int> pendingObservations = new();
    private Guid profileId;
    private long openingId;
    private VipManagementViewResponse? view;
    private DateTimeOffset nextScanAt = DateTimeOffset.MinValue;

    public VipArrivalNearbyTracker(IObjectTable objectTable) => this.objectTable = objectTable;

    public int NearbyCount => nearbyByVipPlayer.Count;

    public void Prepare(Guid venueProfileId, long currentOpeningId, VipManagementViewResponse currentView)
    {
        if (profileId == venueProfileId && openingId == currentOpeningId && ReferenceEquals(view, currentView))
            return;

        profileId = venueProfileId;
        openingId = currentOpeningId;
        view = currentView;
        vipByCharacter.Clear();
        var now = DateTimeOffset.UtcNow;
        var activeVipPlayerIds = currentView.Subscriptions
            .Where(subscription =>
                !subscription.IsCancelled &&
                subscription.StartsAt <= now &&
                (subscription.Lifetime || subscription.EndsAt > now))
            .Select(subscription => subscription.VipPlayerId)
            .ToHashSet();
        foreach (var character in currentView.Characters.Where(
                     character => activeVipPlayerIds.Contains(character.VipPlayerId)))
        {
            vipByCharacter[Key(character.CharacterName, character.WorldName)] =
                new VipCharacterIdentity(character.VipPlayerId, character.CharacterId, character.CharacterName, character.WorldName);
        }
        observedObjects.Clear();
        nearbyByVipPlayer.Clear();
        submittedVipPlayers.Clear();
        pendingObservations.Clear();
        nextScanAt = DateTimeOffset.MinValue;
    }

    public void ScanIfDue()
    {
        if (view is null || openingId <= 0 || DateTimeOffset.UtcNow < nextScanAt) return;
        nextScanAt = DateTimeOffset.UtcNow.Add(ScanInterval);
        Scan();
    }

    public IReadOnlyList<VipArrivalObservationRequest> TakeUnsubmittedObservations()
    {
        var result = pendingObservations
            .Where(value => !submittedVipPlayers.Contains(value.Key))
            .Select(static value => new VipArrivalObservationRequest(value.Key, value.Value))
            .ToArray();
        foreach (var observation in result)
        {
            submittedVipPlayers.Add(observation.VipPlayerId);
        }

        return result;
    }

    public void MarkSubmitted(IEnumerable<VipArrivalObservationRequest> observations)
    {
        foreach (var observation in observations)
        {
            pendingObservations.Remove(observation.VipPlayerId);
        }
    }

    public void ReleaseSubmission(IEnumerable<VipArrivalObservationRequest> observations)
    {
        foreach (var observation in observations)
        {
            submittedVipPlayers.Remove(observation.VipPlayerId);
        }
    }

    public bool TryGetNearby(int vipPlayerId, out NearbyVipArrivalCharacter? character)
    {
        if (nearbyByVipPlayer.TryGetValue(vipPlayerId, out var candidate))
        {
            character = candidate.Character;
            return true;
        }
        character = null;
        return false;
    }

    public void Clear()
    {
        profileId = Guid.Empty;
        openingId = 0;
        view = null;
        vipByCharacter.Clear();
        observedObjects.Clear();
        nearbyByVipPlayer.Clear();
        submittedVipPlayers.Clear();
        pendingObservations.Clear();
        nextScanAt = DateTimeOffset.MinValue;
    }

    private void Scan()
    {
        var seen = new HashSet<ulong>();
        var nextNearby = new Dictionary<int, NearbyCandidate>();
        var local = objectTable.LocalPlayer;
        var localId = local?.GameObjectId ?? 0;
        var localPosition = local?.Position;

        foreach (var battleChara in objectTable.PlayerObjects)
        {
            if (battleChara is not IPlayerCharacter player ||
                !player.IsValid() || !player.IsTargetable ||
                player.GameObjectId == 0 || player.GameObjectId == localId)
                continue;

            seen.Add(player.GameObjectId);
            if (!observedObjects.TryGetValue(player.GameObjectId, out var observed))
            {
                var name = player.Name.TextValue.Trim();
                var world = player.HomeWorld.IsValid ? player.HomeWorld.Value.Name.ToString().Trim() : string.Empty;
                vipByCharacter.TryGetValue(Key(name, world), out var identity);
                observed = new ObservedObject(identity, name, world);
                observedObjects[player.GameObjectId] = observed;
            }

            if (observed.Identity is not { } matched) continue;
            var distance = localPosition is { } position
                ? Vector3.DistanceSquared(position, player.Position)
                : player.ObjectIndex;
            var candidate = new NearbyCandidate(
                new NearbyVipArrivalCharacter(
                    matched.VipPlayerId, matched.CharacterId, player.GameObjectId,
                    matched.CharacterName, matched.WorldName),
                distance);
            if (!nextNearby.TryGetValue(matched.VipPlayerId, out var existing) || distance < existing.DistanceSquared)
                nextNearby[matched.VipPlayerId] = candidate;
        }

        foreach (var id in observedObjects.Keys.Where(id => !seen.Contains(id)).ToArray())
            observedObjects.Remove(id);

        nearbyByVipPlayer.Clear();
        foreach (var pair in nextNearby)
        {
            nearbyByVipPlayer[pair.Key] = pair.Value;
            if (!submittedVipPlayers.Contains(pair.Key))
            {
                pendingObservations[pair.Key] = pair.Value.Character.CharacterId;
            }
        }
    }

    private static string Key(string name, string world) => $"{name.Trim()}\n{world.Trim()}";
    private sealed record VipCharacterIdentity(int VipPlayerId, int CharacterId, string CharacterName, string WorldName);
    private sealed record ObservedObject(VipCharacterIdentity? Identity, string CharacterName, string WorldName);
    private sealed record NearbyCandidate(NearbyVipArrivalCharacter Character, float DistanceSquared);
}
