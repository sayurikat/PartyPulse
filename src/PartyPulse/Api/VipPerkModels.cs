using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record VipPerkCapabilities(
    bool CanView,
    bool CanManage,
    bool CanRedeem,
    bool CanUndo);

public sealed record VipPerkSummary(
    int PerkId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record VipPackagePerkSummary(
    int PackagePerkId,
    int PackageId,
    string PackageName,
    int PerkId,
    string PerkName,
    string? RenewalUnit,
    int? RenewalInterval,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record VipPerkAvailabilitySummary(
    int VipPlayerId,
    int CharacterId,
    string CharacterName,
    string WorldName,
    long SubscriptionId,
    int PackageId,
    string PackageName,
    int PackagePerkId,
    int PerkId,
    string PerkName,
    string? RenewalUnit,
    int? RenewalInterval,
    DateTimeOffset PeriodStart,
    DateTimeOffset? PeriodEnd,
    DateTimeOffset? NextResetAt,
    bool Available,
    long? RedemptionId,
    DateTimeOffset? LastRedeemedAt,
    string? RedeemedByDisplayName,
    string? SourceType,
    long? SourceId);

public sealed record VipPerkRedemptionSummary(
    long RedemptionId,
    long SubscriptionId,
    int PackagePerkId,
    int PerkId,
    string PerkName,
    string TargetCharacterName,
    string TargetWorldName,
    DateTimeOffset PeriodStart,
    DateTimeOffset? PeriodEnd,
    DateTimeOffset RedeemedAt,
    int RedeemedByUserId,
    string RedeemedByDisplayName,
    string SourceType,
    long? SourceId,
    string? Note,
    DateTimeOffset? UndoneAt,
    int? UndoneByUserId,
    string? UndoneByDisplayName,
    string? UndoReason);

public sealed record VipPerkManagementViewResponse(
    VipPerkCapabilities Capabilities,
    IReadOnlyList<VipPerkSummary> Perks,
    IReadOnlyList<VipPackagePerkSummary> PackageAssignments,
    IReadOnlyList<VipPerkAvailabilitySummary> Availability,
    IReadOnlyList<VipPerkRedemptionSummary> Redemptions);

public sealed record CreateVipPerkRequest(string Name);

public sealed record UpdateVipPerkRequest(
    string Name,
    bool Archived);

public sealed record SetVipPackagePerkRequest(
    bool Assigned,
    string? RenewalUnit,
    int? RenewalInterval);

public sealed record RedeemVipPerkRequest(
    string TargetCharacterName,
    string TargetWorldName,
    int PerkId,
    string? Note);

public sealed record UndoVipPerkRedemptionRequest(string? Reason);

public sealed record VipPerkOperationResponse(int PerkId);

public sealed record VipPackagePerkOperationResponse(
    int? PackagePerkId,
    bool Assigned);

public sealed record RedeemVipPerkResponse(
    long RedemptionId,
    long SubscriptionId,
    int PerkId,
    string PerkName,
    DateTimeOffset PeriodStart,
    DateTimeOffset? PeriodEnd,
    DateTimeOffset? NextAvailableAt,
    DateTimeOffset RedeemedAt);

public sealed record UndoVipPerkRedemptionResponse(
    long RedemptionId,
    DateTimeOffset UndoneAt);
