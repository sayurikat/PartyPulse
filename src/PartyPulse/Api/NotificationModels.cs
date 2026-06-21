using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record UserNotificationSummary(
    long NotificationId,
    string NotificationType,
    string Title,
    string Message,
    string? ActionKey,
    long? ActionEntityId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

public sealed record NotificationPollResponse(
    int UnseenNotificationCount,
    int PendingSettlementCount,
    bool CanManageSettlements,
    IReadOnlyList<UserNotificationSummary> Notifications);

public sealed record MarkNotificationSeenRequest(bool Dismissed);

public sealed record MarkNotificationSeenResponse(
    long NotificationId,
    DateTimeOffset SeenAt,
    bool Dismissed);
