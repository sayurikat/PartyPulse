using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record VipManagementCapabilities(
    bool CanView,
    bool CanSell,
    bool CanManagePackages,
    bool CanManagePlayers,
    bool CanManagePayments);

public sealed record VipPackageSummary(
    int PackageId,
    string Name,
    byte Tier,
    int PriceGil,
    long? DiscordRoleId,
    int DaysGranted,
    int MonthsGranted,
    int YearsGranted,
    bool Lifetime,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt)
{
    public bool IsArchived => ArchivedAt is not null;

    public string DurationDisplay
    {
        get
        {
            if (Lifetime)
            {
                return "Lifetime";
            }

            var parts = new List<string>(3);
            if (YearsGranted > 0)
            {
                parts.Add($"{YearsGranted} year{(YearsGranted == 1 ? string.Empty : "s")}");
            }

            if (MonthsGranted > 0)
            {
                parts.Add($"{MonthsGranted} month{(MonthsGranted == 1 ? string.Empty : "s")}");
            }

            if (DaysGranted > 0)
            {
                parts.Add($"{DaysGranted} day{(DaysGranted == 1 ? string.Empty : "s")}");
            }

            return string.Join(", ", parts);
        }
    }
}

public sealed record VipPlayerSummary(
    int VipPlayerId,
    string? DiscordUsername,
    long? DiscordId,
    string? DiscordNickname,
    int? PreferredCharacterId,
    string DisplayCharacterName,
    string DisplayWorldName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastSubscriptionEndsAt,
    bool HasLifetime)
{
    public string CharacterDisplay => $"{DisplayCharacterName} @ {DisplayWorldName}";

    public string DiscordDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DiscordNickname))
            {
                return !string.IsNullOrWhiteSpace(DiscordUsername)
                    ? $"{DiscordNickname} (@{DiscordUsername})"
                    : DiscordNickname!;
            }

            return !string.IsNullOrWhiteSpace(DiscordUsername)
                ? $"@{DiscordUsername}"
                : "Not recorded";
        }
    }
}

public sealed record VipCharacterSummary(
    int CharacterId,
    int VipPlayerId,
    string CharacterName,
    string WorldName,
    bool IsPreferred)
{
    public string DisplayName => $"{CharacterName} @ {WorldName}";
}

public sealed record VipSubscriptionSummary(
    long SubscriptionId,
    int VipPlayerId,
    int PackageId,
    string PackageName,
    byte VipTier,
    int PurchasePriceGil,
    int DaysGranted,
    int MonthsGranted,
    int YearsGranted,
    bool Lifetime,
    DateTimeOffset PurchasedAt,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    int? SoldByUserId,
    string SellerDisplayName,
    DateTimeOffset? PaidToVenueAt,
    int? PaidToVenueByUserId,
    string? PaidToVenueByDisplayName,
    DateTimeOffset? CancelledAt,
    int? CancelledByUserId,
    string? CancelledByDisplayName,
    string? CancellationReason,
    long? PendingSettlementId)
{
    public bool IsCancelled => CancelledAt is not null;
    public bool IsSettled => PaidToVenueAt is not null;
    public bool IsInPendingSettlement => PendingSettlementId is not null;
}

public sealed record VipManagementViewResponse(
    VipManagementCapabilities Capabilities,
    long PersonalUnpaidGil,
    long PersonalPendingSettlementGil,
    IReadOnlyList<VipPackageSummary> Packages,
    IReadOnlyList<VipPlayerSummary> Players,
    IReadOnlyList<VipCharacterSummary> Characters,
    IReadOnlyList<VipSubscriptionSummary> Subscriptions)
{
    public long PersonalAvailableSettlementGil =>
        Math.Max(0, PersonalUnpaidGil - PersonalPendingSettlementGil);
}

public sealed record CreateVipPackageRequest(
    string Name,
    int PriceGil,
    int DaysGranted,
    int MonthsGranted,
    int YearsGranted,
    bool Lifetime,
    long? DiscordRoleId);

public sealed record UpdateVipPackageRequest(
    string Name,
    int PriceGil,
    int DaysGranted,
    int MonthsGranted,
    int YearsGranted,
    bool Lifetime,
    long? DiscordRoleId,
    bool Archived);

public sealed record SellVipSubscriptionRequest(
    string CharacterName,
    string WorldName,
    int PackageId,
    string DiscordUsername,
    int? VipPlayerId,
    bool CustomerPaymentConfirmed);

public sealed record LinkVipCharacterRequest(string CharacterName, string WorldName);
public sealed record UpdateVipPlayerRequest(string? DiscordUsername);
public sealed record CancelVipSubscriptionRequest(string? Reason);
public sealed record SetVipSubscriptionPaymentStatusRequest(bool Settled);

public sealed record VipPackageOperationResponse(int PackageId);

public sealed record VipCharacterOperationResponse(
    int VipPlayerId,
    int CharacterId,
    int PreferredCharacterId);

public sealed record VipPreferredCharacterResponse(
    int VipPlayerId,
    int PreferredCharacterId);

public sealed record VipPlayerOperationResponse(
    int VipPlayerId,
    string? DiscordUsername);

public sealed record VipSubscriptionCancellationResponse(
    long SubscriptionId,
    int VipPlayerId,
    DateTimeOffset CancelledAt);

public sealed record VipSubscriptionPaymentStatusResponse(
    long SubscriptionId,
    bool Settled,
    DateTimeOffset? PaidToVenueAt);

public sealed record SellVipSubscriptionResponse(
    long SubscriptionId,
    int VipPlayerId,
    int CharacterId,
    int PackageId,
    int PurchasePriceGil,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    bool Lifetime,
    long PersonalUnpaidGil);
