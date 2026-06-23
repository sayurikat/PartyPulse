using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public static class TimedMacroTypeCodes
{
    public const string VipAdvertisement = "vip.advertisement";
    public const string Custom = "custom";
}

public sealed record TimedMacroCapabilities(
    bool CanExecuteAny,
    bool CanManageAny);

public sealed record TimedMacroOpeningSummary(
    long OpeningId,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    int AddressWorldId,
    string AddressWorldName,
    int AddressCityId,
    string AddressCityName,
    int AddressWard,
    int AddressPlot,
    string? Title)
{
    public string AddressDisplay =>
        $"{AddressWorldName}, {AddressCityName}, Ward {AddressWard}, Plot {AddressPlot}";
}

public sealed record TimedMacroSummary(
    long TimedMacroId,
    string InstanceCode,
    string TypeCode,
    string DisplayName,
    string? Description,
    bool AllowsMultipleInstances,
    byte MaxLines,
    short MaxLineLength,
    string? MacroText,
    int IntervalMinutes,
    bool Enabled,
    string SourceType,
    string? SourceReference,
    DateTimeOffset? UpdatedAt,
    int? UpdatedByUserId,
    bool CanExecute,
    bool CanManage,
    DateTimeOffset? LastExecutedAt,
    int? LastExecutedByUserId,
    int ExecutionCount,
    DateTimeOffset? NextDueAt,
    bool IsDue)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(MacroText);
    public bool IsCustom => string.Equals(TypeCode, TimedMacroTypeCodes.Custom, StringComparison.OrdinalIgnoreCase);
}

public sealed record TimedMacroViewResponse(
    TimedMacroCapabilities Capabilities,
    DateTimeOffset ServerNow,
    TimedMacroOpeningSummary? CurrentOpening,
    IReadOnlyList<TimedMacroSummary> Macros);

public sealed record CreateTimedMacroRequest(
    string DisplayName,
    string? MacroText,
    int IntervalMinutes,
    bool Enabled);

public sealed record UpdateTimedMacroRequest(
    string DisplayName,
    string? MacroText,
    int IntervalMinutes,
    bool Enabled);

public sealed record SaveTimedMacroResponse(
    long TimedMacroId,
    DateTimeOffset UpdatedAt);

public sealed record ArchiveTimedMacroResponse(
    long TimedMacroId,
    DateTimeOffset ArchivedAt);

public sealed record RecordTimedMacroExecutionRequest(
    long OpeningId,
    Guid ClientExecutionId);

public sealed record RecordTimedMacroExecutionResponse(
    long OpeningId,
    long TimedMacroId,
    DateTimeOffset LastExecutedAt,
    int LastExecutedByUserId,
    int ExecutionCount,
    DateTimeOffset NextDueAt);
