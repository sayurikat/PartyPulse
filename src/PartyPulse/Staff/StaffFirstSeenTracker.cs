using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using PartyPulse.Api;

namespace PartyPulse.Staff;

/// <summary>
/// Tracks linked Staff characters currently rendered by the game and batches
/// opening-specific first-seen observations. State is isolated per venue profile
/// so one plugin instance can safely serve multiple configured venues.
/// </summary>
public sealed class StaffFirstSeenTracker(IObjectTable objectTable)
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailedUploadDelay = TimeSpan.FromSeconds(30);
    private readonly Dictionary<Guid, VenueState> states = new();

    public void Prepare(
        Guid profileId,
        long openingId,
        StaffManagementViewResponse view)
    {
        if (!states.TryGetValue(profileId, out var state) || state.OpeningId != openingId)
        {
            state = new VenueState(openingId);
            states[profileId] = state;
        }
        else if (ReferenceEquals(state.View, view))
        {
            return;
        }

        state.View = view;
        state.NextUploadAt = DateTimeOffset.MinValue;
        var activeStaffIds = view.StaffMembers
            .Where(static item => item.ArchivedAt is null)
            .Select(static item => item.StaffMemberId)
            .ToHashSet();
        state.LinkedCharacters.Clear();
        foreach (var character in view.Characters.Where(
                     item => item.StaffMemberId is { } staffId &&
                             activeStaffIds.Contains(staffId)))
        {
            state.LinkedCharacters[Key(character.CharacterName, character.WorldName)] = character;
        }

        state.Pending.RemoveWhere(key => !state.LinkedCharacters.ContainsKey(key));
        state.Submitted.RemoveWhere(key => !state.LinkedCharacters.ContainsKey(key));

        var alreadySeenStaff = view.FirstSeen
            .Where(item => item.OpeningId == openingId)
            .Select(static item => item.StaffMemberId)
            .ToHashSet();
        foreach (var character in state.LinkedCharacters.Values.Where(
                     item => item.StaffMemberId is { } staffId && alreadySeenStaff.Contains(staffId)))
        {
            state.Submitted.Add(Key(character.CharacterName, character.WorldName));
        }
    }

    public void ScanIfDue(Guid profileId)
    {
        if (!states.TryGetValue(profileId, out var state) ||
            state.OpeningId <= 0 ||
            DateTimeOffset.UtcNow < state.NextScanAt)
        {
            return;
        }

        state.NextScanAt = DateTimeOffset.UtcNow.Add(ScanInterval);

        ObservePlayer(state, objectTable.LocalPlayer);
        foreach (var player in objectTable.PlayerObjects.OfType<IPlayerCharacter>())
        {
            ObservePlayer(state, player);
        }
    }

    public IReadOnlyList<StaffFirstSeenObservationRequest> TakeUnsubmittedObservations(
        Guid profileId)
    {
        if (!states.TryGetValue(profileId, out var state) ||
            DateTimeOffset.UtcNow < state.NextUploadAt)
        {
            return Array.Empty<StaffFirstSeenObservationRequest>();
        }

        var result = state.Pending
            .Where(key => !state.Submitted.Contains(key))
            .Take(100)
            .Select(key => state.LinkedCharacters.TryGetValue(key, out var character)
                ? new StaffFirstSeenObservationRequest(character.CharacterName, character.WorldName)
                : null)
            .Where(static item => item is not null)
            .Cast<StaffFirstSeenObservationRequest>()
            .ToArray();

        foreach (var observation in result)
        {
            state.Submitted.Add(Key(observation.CharacterName, observation.WorldName));
        }

        return result;
    }

    public void MarkSubmitted(
        Guid profileId,
        IEnumerable<StaffFirstSeenObservationRequest> observations)
    {
        if (!states.TryGetValue(profileId, out var state))
        {
            return;
        }

        foreach (var observation in observations)
        {
            state.Pending.Remove(Key(observation.CharacterName, observation.WorldName));
        }

        state.NextUploadAt = DateTimeOffset.MinValue;
    }

    public void ReleaseSubmission(
        Guid profileId,
        IEnumerable<StaffFirstSeenObservationRequest> observations)
    {
        if (!states.TryGetValue(profileId, out var state))
        {
            return;
        }

        foreach (var observation in observations)
        {
            state.Submitted.Remove(Key(observation.CharacterName, observation.WorldName));
        }

        state.NextUploadAt = DateTimeOffset.UtcNow.Add(FailedUploadDelay);
    }

    public void RemoveProfile(Guid profileId) => states.Remove(profileId);

    public void Clear() => states.Clear();

    private static void ObservePlayer(VenueState state, IPlayerCharacter? player)
    {
        if (player is null ||
            !player.IsValid() ||
            !player.IsTargetable ||
            player.GameObjectId == 0)
        {
            return;
        }

        var characterName = player.Name.TextValue.Trim();
        var worldName = player.HomeWorld.IsValid
            ? player.HomeWorld.Value.Name.ToString().Trim()
            : string.Empty;
        var key = Key(characterName, worldName);
        if (characterName.Length > 0 &&
            worldName.Length > 0 &&
            state.LinkedCharacters.ContainsKey(key) &&
            !state.Submitted.Contains(key))
        {
            state.Pending.Add(key);
        }
    }

    private static string Key(string name, string world) =>
        $"{name.Trim()}\n{world.Trim()}";

    private sealed class VenueState(long openingId)
    {
        public long OpeningId { get; } = openingId;
        public StaffManagementViewResponse? View { get; set; }
        public DateTimeOffset NextScanAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset NextUploadAt { get; set; } = DateTimeOffset.MinValue;
        public Dictionary<string, StaffCharacterSummary> LinkedCharacters { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Submitted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
