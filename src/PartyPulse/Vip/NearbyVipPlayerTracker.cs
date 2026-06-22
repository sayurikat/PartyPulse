using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using PartyPulse.Api;

namespace PartyPulse.Vip;

public sealed record NearbyVipCharacter(
    int VipPlayerId,
    ulong GameObjectId,
    string CharacterName,
    string WorldName)
{
    public string DisplayName => $"{CharacterName} @ {WorldName}";
}

/// <summary>
/// Tracks nearby player characters that are linked to VIP players.
///
/// The VIP character list is converted to a case-insensitive dictionary once
/// per refreshed VIP view. Spawned player characters are then cached by their
/// game object ID, so unchanged objects do not repeat name/world matching on
/// every scan.
/// </summary>
public sealed class NearbyVipPlayerTracker
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly Dictionary<string, int> vipPlayerByCharacter =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, ObservedPlayer> observedPlayers = new();
    private readonly Dictionary<int, NearbyCandidate> nearbyPlayers = new();

    private Guid activeProfileId;
    private VipManagementViewResponse? activeView;
    private DateTimeOffset nextScanAt = DateTimeOffset.MinValue;

    public NearbyVipPlayerTracker(
        IObjectTable objectTable,
        ITargetManager targetManager)
    {
        this.objectTable = objectTable;
        this.targetManager = targetManager;
    }

    public int NearbyVipPlayerCount => nearbyPlayers.Count;

    public void Prepare(Guid profileId, VipManagementViewResponse view)
    {
        if (activeProfileId == profileId && ReferenceEquals(activeView, view))
        {
            return;
        }

        activeProfileId = profileId;
        activeView = view;
        vipPlayerByCharacter.Clear();

        foreach (var character in view.Characters)
        {
            vipPlayerByCharacter[CreateIdentityKey(character.CharacterName, character.WorldName)] =
                character.VipPlayerId;
        }

        observedPlayers.Clear();
        nearbyPlayers.Clear();
        nextScanAt = DateTimeOffset.MinValue;
    }

    public void ScanIfDue()
    {
        if (activeView is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < nextScanAt)
        {
            return;
        }

        nextScanAt = now.Add(ScanInterval);
        Scan();
    }

    public bool IsNearby(int vipPlayerId) => nearbyPlayers.ContainsKey(vipPlayerId);

    public bool TryGetNearbyCharacter(
        int vipPlayerId,
        out NearbyVipCharacter? nearbyCharacter)
    {
        if (nearbyPlayers.TryGetValue(vipPlayerId, out var candidate))
        {
            nearbyCharacter = candidate.Character;
            return true;
        }

        nearbyCharacter = null;
        return false;
    }

    public bool TryTarget(int vipPlayerId, out string errorMessage)
    {
        if (!nearbyPlayers.TryGetValue(vipPlayerId, out var candidate))
        {
            errorMessage = "No linked character for this VIP player is currently nearby.";
            return false;
        }

        var gameObject = objectTable.SearchById(candidate.Character.GameObjectId);
        if (gameObject is not IPlayerCharacter player ||
            !player.IsValid() ||
            !player.IsTargetable ||
            !TryReadIdentity(player, out var characterName, out var worldName) ||
            !string.Equals(
                characterName,
                candidate.Character.CharacterName,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                worldName,
                candidate.Character.WorldName,
                StringComparison.OrdinalIgnoreCase))
        {
            nearbyPlayers.Remove(vipPlayerId);
            observedPlayers.Remove(candidate.Character.GameObjectId);
            errorMessage = "The nearby character is no longer available. The nearby list will refresh shortly.";
            return false;
        }

        targetManager.Target = player;
        errorMessage = string.Empty;
        return true;
    }

    public void ClearProfile(Guid profileId)
    {
        if (activeProfileId == profileId)
        {
            Clear();
        }
    }

    public void Clear()
    {
        activeProfileId = Guid.Empty;
        activeView = null;
        vipPlayerByCharacter.Clear();
        observedPlayers.Clear();
        nearbyPlayers.Clear();
        nextScanAt = DateTimeOffset.MinValue;
    }

    private void Scan()
    {
        var seenObjectIds = new HashSet<ulong>();
        var nextNearbyPlayers = new Dictionary<int, NearbyCandidate>();
        var localPlayer = objectTable.LocalPlayer;
        var localPlayerId = localPlayer?.GameObjectId ?? 0;
        var localPosition = localPlayer?.Position;

        foreach (var battleChara in objectTable.PlayerObjects)
        {
            if (battleChara is not IPlayerCharacter player ||
                !player.IsValid() ||
                !player.IsTargetable ||
                player.GameObjectId == 0 ||
                player.GameObjectId == localPlayerId)
            {
                continue;
            }

            var gameObjectId = player.GameObjectId;
            seenObjectIds.Add(gameObjectId);

            if (!observedPlayers.TryGetValue(gameObjectId, out var observed))
            {
                if (!TryReadIdentity(player, out var characterName, out var worldName))
                {
                    continue;
                }

                vipPlayerByCharacter.TryGetValue(
                    CreateIdentityKey(characterName, worldName),
                    out var vipPlayerId);
                observed = new ObservedPlayer(
                    vipPlayerId > 0 ? vipPlayerId : null,
                    characterName,
                    worldName);
                observedPlayers[gameObjectId] = observed;
            }

            if (observed.VipPlayerId is not { } matchedVipPlayerId)
            {
                continue;
            }

            var distanceSquared = localPosition is { } position
                ? Vector3.DistanceSquared(position, player.Position)
                : player.ObjectIndex;
            var nearbyCharacter = new NearbyVipCharacter(
                matchedVipPlayerId,
                gameObjectId,
                observed.CharacterName,
                observed.WorldName);
            var candidate = new NearbyCandidate(nearbyCharacter, distanceSquared);

            if (!nextNearbyPlayers.TryGetValue(matchedVipPlayerId, out var existing) ||
                candidate.DistanceSquared < existing.DistanceSquared)
            {
                nextNearbyPlayers[matchedVipPlayerId] = candidate;
            }
        }

        if (observedPlayers.Count > seenObjectIds.Count)
        {
            var removedObjectIds = new List<ulong>();
            foreach (var gameObjectId in observedPlayers.Keys)
            {
                if (!seenObjectIds.Contains(gameObjectId))
                {
                    removedObjectIds.Add(gameObjectId);
                }
            }

            foreach (var gameObjectId in removedObjectIds)
            {
                observedPlayers.Remove(gameObjectId);
            }
        }

        nearbyPlayers.Clear();
        foreach (var pair in nextNearbyPlayers)
        {
            nearbyPlayers[pair.Key] = pair.Value;
        }
    }

    private static bool TryReadIdentity(
        IPlayerCharacter player,
        out string characterName,
        out string worldName)
    {
        characterName = player.Name.TextValue.Trim();
        worldName = player.HomeWorld.IsValid
            ? player.HomeWorld.Value.Name.ToString().Trim()
            : string.Empty;

        return characterName.Length > 0 && worldName.Length > 0;
    }

    private static string CreateIdentityKey(string characterName, string worldName) =>
        $"{characterName.Trim()}\n{worldName.Trim()}";

    private sealed record ObservedPlayer(
        int? VipPlayerId,
        string CharacterName,
        string WorldName);

    private sealed record NearbyCandidate(
        NearbyVipCharacter Character,
        float DistanceSquared);
}
