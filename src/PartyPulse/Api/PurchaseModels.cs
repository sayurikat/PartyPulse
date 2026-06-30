using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record PurchaseCapabilities(
    bool CanView,
    bool CanCreate,
    bool CanManage);

public sealed record PurchaseSummary(
    long PurchaseId,
    string Title,
    string Details,
    long TotalPriceGil,
    string Status,
    int CreatedByUserId,
    string CreatedByDisplayName,
    string CreatedByCharacterName,
    string CreatedByWorldName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    int? ApprovedByUserId,
    string? ApprovedByDisplayName,
    DateTimeOffset? SettledAt,
    int? SettledByUserId,
    string? SettledByDisplayName,
    DateTimeOffset? RejectedAt,
    int? RejectedByUserId,
    string? RejectedByDisplayName,
    string? RejectionReason,
    DateTimeOffset? CancelledAt,
    int? CancelledByUserId,
    string? CancelledByDisplayName);

public sealed record PurchasesManagementViewResponse(
    PurchaseCapabilities Capabilities,
    IReadOnlyList<PurchaseSummary> Purchases);

public sealed record CreatePurchaseRequest(
    string Title,
    string Details,
    long TotalPriceGil);

public sealed record RejectPurchaseRequest(string Reason);

public sealed record CancelPurchaseRequest(bool ConfirmRepaidToClub);

public sealed record CreatePurchaseResponse(
    long PurchaseId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? SettledAt);

public sealed record PurchaseStateChangeResponse(
    long PurchaseId,
    string Status,
    DateTimeOffset ChangedAt);
