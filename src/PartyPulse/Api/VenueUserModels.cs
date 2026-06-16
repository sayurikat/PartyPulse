using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record VenueUserManagementCapabilities(
    bool CanView,
    bool CanCreate,
    bool CanEdit,
    bool CanRecover,
    bool CanManagePermissions);

public sealed record VenuePermissionDefinition(
    string PermissionKey,
    string? Description);

public sealed record VenueUserSummary(
    int UserId,
    string DisplayName,
    string? DiscordHandle,
    long? DiscordId,
    string? DiscordName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt,
    string? MainCharacterName,
    string? MainCharacterWorld,
    bool IsOwner,
    IReadOnlyList<string> Permissions)
{
    public string DiscordDisplay =>
        !string.IsNullOrWhiteSpace(DiscordName)
            ? DiscordName!
            : !string.IsNullOrWhiteSpace(DiscordHandle)
                ? $"@{DiscordHandle}"
                : "Not linked";

    public string CharacterDisplay =>
        !string.IsNullOrWhiteSpace(MainCharacterName) && !string.IsNullOrWhiteSpace(MainCharacterWorld)
            ? $"{MainCharacterName} @ {MainCharacterWorld}"
            : "Not registered yet";
}

public sealed record VenueUserManagementViewResponse(
    VenueUserManagementCapabilities Capabilities,
    IReadOnlyList<VenuePermissionDefinition> AvailablePermissions,
    IReadOnlyList<VenueUserSummary> Users);

public sealed record CreateVenueUserRequest(
    string DisplayName,
    string? DiscordHandle);

public sealed record CreateVenueUserResponse(
    int UserId,
    string InviteCode,
    DateTimeOffset InviteExpiresAt);

public sealed record UpdateVenueUserProfileRequest(
    string DisplayName,
    string? DiscordHandle);

public sealed record SetVenueUserPermissionsRequest(
    IReadOnlyList<string> PermissionKeys);

public sealed record VenueUserOperationResponse(int UserId);

public sealed record SetVenueUserPermissionsResponse(
    int UserId,
    int AssignedPermissionCount);

public sealed record CreateRecoveryCodeResponse(
    int UserId,
    string RecoveryCode,
    DateTimeOffset RecoveryCodeExpiresAt);
