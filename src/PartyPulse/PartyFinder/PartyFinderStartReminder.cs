using System;
using System.Collections.Generic;
using PartyPulse.Models;

namespace PartyPulse.PartyFinder;

internal sealed class PartyFinderStartReminder
{
    private readonly HashSet<(int VenueId, long OpeningId, PlayerIdentity Character)> notified = [];

    public bool TryRemind(
        int venueId,
        long openingId,
        PlayerIdentity identity,
        Action<string> notify,
        string venueName)
    {
        if (!notified.Add((venueId, openingId, identity)))
            return false;

        notify($"You're at {venueName}. Start Party Finder for this opening when you're ready.");
        return true;
    }
}
