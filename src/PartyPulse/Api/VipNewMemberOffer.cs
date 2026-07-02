using System;

namespace PartyPulse.Api;

/// <summary>
/// Client-only state used to carry a detected VIP prospect into the sales UI.
/// This is deliberately not part of PartyPulse.Contracts because it is never serialized.
/// </summary>
public sealed record VipNewMemberOffer(
    Guid VenueProfileId,
    long OpeningId,
    int VipPlayerId,
    int CharacterId,
    string CharacterName,
    string WorldName);
