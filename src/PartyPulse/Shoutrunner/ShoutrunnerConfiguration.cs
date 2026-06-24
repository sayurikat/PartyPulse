using System;
using System.Collections.Generic;

namespace PartyPulse.Shoutrunner;

[Serializable]
public sealed class ShoutrunnerProfileConfiguration
{
    public Guid VenueProfileId { get; set; }

    public List<string> SelectedWorldNames { get; set; } = [];

    public long? ActiveOpeningId { get; set; }

    public List<string> CompletedDestinationKeys { get; set; } = [];

    public DateTimeOffset? NextTravelAllowedAtUtc { get; set; }

    public List<ShoutrunnerLocalLogEntry> PendingLogs { get; set; } = [];
}

[Serializable]
public sealed class ShoutrunnerLocalLogEntry
{
    public Guid ClientEntryId { get; set; } = Guid.NewGuid();

    public long OpeningId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public string EventType { get; set; } = string.Empty;

    public int CompletedLocations { get; set; }

    public int TotalLocations { get; set; }

    public int? WorldId { get; set; }

    public string? WorldName { get; set; }

    public string? DatacenterName { get; set; }

    public string? CityName { get; set; }

    public string? Reason { get; set; }
}
