using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;

namespace PartyPulse.Windows;

public sealed class DjsTabRenderer(Plugin plugin)
{
    private Guid activeProfileId;
    private long? editingDjId;
    private string name = string.Empty;
    private string twitchUrl = string.Empty;
    private bool resident;
    private string note = string.Empty;
    private long? pendingArchiveDjId;
    private bool requestArchivePopup;
    private bool settingsInitialized;
    private string defaultHourlyRateGil = "0";
    private long selectedLinkDjId;
    private long selectedPayoutDjId;
    private bool proxyPayoutConfirmed;
    private bool balanceRefundConfirmed;
    private string payoutTargetKey = string.Empty;

    public void Draw(VenueConnectionConfiguration venue, MainSubtab subtab)
    {
        ResetForVenueChange(venue);
        plugin.EnsureDjsLoaded(venue);

        var snapshot = plugin.Djs.GetSnapshot(venue);
        var view = snapshot.View;
        if (view is not null && !view.Capabilities.CanManageDirectory)
            return;

        var isBusy = plugin.Djs.IsBusy(venue.ProfileId);
        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Refresh DJs"))
            plugin.RefreshDjs(venue);
        ImGui.EndDisabled();

        if (view is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            return;
        }

        EnsureSettingsDraft(view);
        switch (subtab)
        {
            case MainSubtab.DjsDirectory:
                DrawEditor(venue, isBusy);
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                DrawDirectory(venue, view, isBusy);
                break;
            case MainSubtab.DjsCharacters:
                DrawCharacterLinks(venue, view, isBusy);
                break;
            case MainSubtab.DjsPayments:
                DrawBalancePayout(venue, view, isBusy);
                break;
            case MainSubtab.DjsSettings:
                DrawVenueSettings(venue, view, isBusy);
                break;
        }
        DrawArchivePopup(venue, isBusy);
    }

    private void DrawVenueSettings(
        VenueConnectionConfiguration venue,
        DjViewResponse view,
        bool isBusy)
    {
        ImGui.TextUnformatted("Venue DJ pricing");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Default price per hour (gil)", ref defaultHourlyRateGil, 16);

        var valid = long.TryParse(
                        defaultHourlyRateGil,
                        NumberStyles.Integer | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture,
                        out var parsedRate) &&
                    parsedRate is >= 0 and <= int.MaxValue;
        if (valid)
            ImGui.TextDisabled($"Saved value: {view.DefaultHourlyRateGil:N0} gil/hour. New DJ slots suggest a total from this rate and their duration.");
        else
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), $"Enter a whole amount from 0 to {int.MaxValue:N0} gil.");

        ImGui.BeginDisabled(isBusy || !valid || parsedRate == view.DefaultHourlyRateGil);
        if (ImGui.Button("Save default DJ rate"))
            plugin.UpdateDjSettings(venue, new UpdateDjSettingsRequest(parsedRate));
        ImGui.EndDisabled();
    }

    private void DrawEditor(VenueConnectionConfiguration venue, bool isBusy)
    {
        ImGui.TextUnformatted(editingDjId is null ? "Register DJ" : $"Edit DJ #{editingDjId}");

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Name", ref name, 100);
        ImGui.SetNextItemWidth(420 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Twitch link", ref twitchUrl, 500);
        ImGui.Checkbox("Resident DJ", ref resident);
        ImGui.TextUnformatted("Notes");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##DjNotes", ref note, 2000, new Vector2(0, 85 * ImGuiHelpers.GlobalScale));

        var valid = !string.IsNullOrWhiteSpace(name) &&
                    (string.IsNullOrWhiteSpace(twitchUrl) ||
                     Uri.TryCreate(twitchUrl.Trim(), UriKind.Absolute, out var uri) &&
                     (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                     (string.Equals(uri.Host, "twitch.tv", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(uri.Host, "www.twitch.tv", StringComparison.OrdinalIgnoreCase)));

        ImGui.BeginDisabled(isBusy || !valid);
        if (ImGui.Button(editingDjId is null ? "Register DJ" : "Save DJ"))
        {
            plugin.SaveDj(
                venue,
                editingDjId,
                new SaveDjRequest(
                    name.Trim(),
                    string.IsNullOrWhiteSpace(twitchUrl) ? null : twitchUrl.Trim(),
                    resident,
                    string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
            ClearDraft();
        }
        ImGui.EndDisabled();

        if (editingDjId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel edit"))
                ClearDraft();
        }
    }

    private void DrawCharacterLinks(
        VenueConnectionConfiguration venue,
        DjViewResponse view,
        bool isBusy)
    {
        ImGui.TextUnformatted("DJ characters");
        ImGui.TextDisabled("Target a player character, choose the DJ, then link it. A DJ may have multiple characters.");

        if (view.Djs.Count == 0)
        {
            ImGui.TextDisabled("Register a DJ before assigning characters.");
            return;
        }

        var selectedDj = view.Djs.FirstOrDefault(value => value.DjId == selectedLinkDjId) ?? view.Djs[0];
        if (selectedLinkDjId <= 0)
            selectedLinkDjId = selectedDj.DjId;

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Assign target to DJ", selectedDj.Name))
        {
            foreach (var dj in view.Djs.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var selected = dj.DjId == selectedLinkDjId;
                if (ImGui.Selectable($"{dj.Name}##DjCharacterLink{dj.DjId}", selected))
                    selectedLinkDjId = dj.DjId;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var hasTarget = plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetReason);
        ImGui.SameLine();
        ImGui.BeginDisabled(isBusy || !hasTarget);
        if (ImGui.Button("Link current target") && target is not null)
        {
            plugin.LinkDjCharacter(
                venue,
                new LinkDjCharacterRequest(
                    selectedLinkDjId,
                    target.CharacterName,
                    target.WorldName));
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled(hasTarget && target is not null ? target.DisplayName : targetReason);

        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("DjCharacterLinkTable", 4, flags))
            return;

        ImGui.TableSetupColumn("DJ");
        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("World");
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var character in view.Characters
                     .OrderBy(
                         value => view.Djs.FirstOrDefault(dj => dj.DjId == value.DjId)?.Name ?? string.Empty,
                         StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.CharacterName, StringComparer.OrdinalIgnoreCase))
        {
            var djName = view.Djs.FirstOrDefault(value => value.DjId == character.DjId)?.Name ?? $"DJ #{character.DjId}";
            ImGui.PushID($"dj-character-{character.CharacterId}");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(djName);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(character.CharacterName);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(character.WorldName);
            ImGui.TableSetColumnIndex(3);
            ImGui.BeginDisabled(isBusy);
            if (ImGui.SmallButton("Unlink"))
            {
                plugin.LinkDjCharacter(
                    venue,
                    new LinkDjCharacterRequest(
                        null,
                        character.CharacterName,
                        character.WorldName));
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawBalancePayout(
        VenueConnectionConfiguration venue,
        DjViewResponse view,
        bool isBusy)
    {
        ImGui.TextUnformatted("DJ balance payout");
        ImGui.TextDisabled("Completed unpaid bookings are combined into one Dropbox trade. Confirm the payment after the trade succeeds.");

        if (!view.Capabilities.CanManagePayments)
        {
            ImGui.TextDisabled("You do not have venue.djs.manage permission to pay DJs.");
            return;
        }

        var hasTarget = plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetReason);
        var currentTargetKey = target is null
            ? string.Empty
            : $"{target.CharacterName}@{target.WorldName}";
        if (!string.Equals(currentTargetKey, payoutTargetKey, StringComparison.Ordinal))
        {
            payoutTargetKey = currentTargetKey;
            proxyPayoutConfirmed = false;
            balanceRefundConfirmed = false;
        }

        var linkedTarget = target is null
            ? null
            : view.Characters.FirstOrDefault(character =>
                string.Equals(character.CharacterName, target.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(character.WorldName, target.WorldName, StringComparison.OrdinalIgnoreCase));

        if (linkedTarget is not null)
            selectedPayoutDjId = linkedTarget.DjId;
        else if (selectedPayoutDjId <= 0 || view.Djs.All(dj => dj.DjId != selectedPayoutDjId))
            selectedPayoutDjId = view.Djs.FirstOrDefault(dj => GetOutstandingBalance(view, dj.DjId) > 0)?.DjId
                                 ?? view.Djs.FirstOrDefault()?.DjId
                                 ?? 0;

        var selectedDj = view.Djs.FirstOrDefault(dj => dj.DjId == selectedPayoutDjId);
        ImGui.TextUnformatted("Current target");
        ImGui.SameLine();
        ImGui.TextDisabled(hasTarget && target is not null ? target.DisplayName : targetReason);

        if (linkedTarget is not null && selectedDj is not null)
        {
            ImGui.TextUnformatted($"Recognized DJ: {selectedDj.Name}");
            proxyPayoutConfirmed = false;
        }
        else
        {
            ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
            if (ImGui.BeginCombo("Collecting for DJ", selectedDj?.Name ?? "Select DJ"))
            {
                foreach (var dj in view.Djs.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var selected = dj.DjId == selectedPayoutDjId;
                    var outstanding = GetOutstandingBalance(view, dj.DjId);
                    if (ImGui.Selectable($"{dj.Name} — {outstanding:N0} gil##DjPayout{dj.DjId}", selected))
                    {
                        selectedPayoutDjId = dj.DjId;
                        proxyPayoutConfirmed = false;
                    }
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            ImGui.Checkbox("I confirm the target is collecting for the selected DJ", ref proxyPayoutConfirmed);
        }

        if (selectedDj is null)
        {
            ImGui.TextDisabled("Register a DJ before starting a payout.");
            return;
        }

        var outstandingBalance = GetOutstandingBalance(view, selectedDj.DjId);
        var startedPayments = view.Bookings
            .Where(booking => booking.DjId == selectedDj.DjId &&
                              booking.PaymentId is not null &&
                              string.Equals(booking.PaymentStatus, DjPaymentStatusCodes.Started, StringComparison.OrdinalIgnoreCase))
            .OrderBy(booking => booking.PaymentStartedAt)
            .ThenBy(booking => booking.PaymentId)
            .ToArray();
        var pendingBalance = startedPayments.Sum(booking => booking.PriceGil);

        ImGui.TextUnformatted($"Outstanding: {outstandingBalance:N0} gil");
        ImGui.SameLine();
        ImGui.TextDisabled($"Trade awaiting confirmation: {pendingBalance:N0} gil");

        if (startedPayments.Length > 0)
        {
            var paymentIds = startedPayments.Select(booking => booking.PaymentId!.Value).Distinct().ToArray();
            var targetLabel = startedPayments[0].PaymentTargetCharacterName is { Length: > 0 } targetName
                ? $"{targetName} @ {startedPayments[0].PaymentTargetWorldName}"
                : "recorded target";
            ImGui.TextDisabled($"Dropbox target: {targetLabel}{(startedPayments[0].PaymentViaProxy ? " (proxy)" : string.Empty)}");
            ImGui.BeginDisabled(isBusy);
            if (ImGui.Button($"Confirm {pendingBalance:N0} gil paid"))
            {
                balanceRefundConfirmed = false;
                plugin.ConfirmDjPayments(venue, paymentIds);
            }
            ImGui.EndDisabled();

            ImGui.Checkbox("I confirm any gil already traded was refunded", ref balanceRefundConfirmed);
            ImGui.BeginDisabled(isBusy || !balanceRefundConfirmed);
            if (ImGui.Button("Cancel payout attempt"))
            {
                balanceRefundConfirmed = false;
                plugin.CancelDjPayments(venue, paymentIds);
            }
            ImGui.EndDisabled();
            return;
        }

        var proxyRequired = linkedTarget is null;
        ImGui.BeginDisabled(
            isBusy ||
            !hasTarget ||
            target is null ||
            outstandingBalance <= 0 ||
            (proxyRequired && !proxyPayoutConfirmed));
        if (ImGui.Button($"Pay {outstandingBalance:N0} gil via Dropbox") && target is not null)
        {
            plugin.StartDjBalancePayment(
                venue,
                selectedDj.DjId,
                target.CharacterName,
                target.WorldName,
                proxyRequired);
        }
        ImGui.EndDisabled();
    }

    private static long GetOutstandingBalance(DjViewResponse view, long djId) =>
        view.Bookings
            .Where(booking => booking.DjId == djId &&
                              booking.EndsAt <= view.ServerNow &&
                              booking.PriceGil > 0 &&
                              !booking.HasActivePayment &&
                              !string.Equals(booking.StatusCode, DjBookingStatusCodes.Unavailable, StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(booking.StatusCode, DjBookingStatusCodes.Cancelled, StringComparison.OrdinalIgnoreCase))
            .Sum(booking => booking.PriceGil);

    private static long GetPendingBalance(DjViewResponse view, long djId) =>
        view.Bookings
            .Where(booking => booking.DjId == djId &&
                              string.Equals(booking.PaymentStatus, DjPaymentStatusCodes.Started, StringComparison.OrdinalIgnoreCase))
            .Sum(booking => booking.PriceGil);

    private void DrawDirectory(
        VenueConnectionConfiguration venue,
        DjViewResponse view,
        bool isBusy)
    {
        ImGui.TextUnformatted("DJ directory");
        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("DjDirectoryTable", 7, flags, new Vector2(0, 300 * ImGuiHelpers.GlobalScale)))
            return;

        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Resident", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Twitch");
        ImGui.TableSetupColumn("Outstanding", ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("In trade", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Note");
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var dj in view.Djs
                     .OrderByDescending(value => value.Resident)
                     .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            ImGui.PushID($"dj-{dj.DjId}");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(dj.Name);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(dj.Resident ? "Yes" : "No");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(dj.TwitchUrl ?? "Not recorded");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted($"{GetOutstandingBalance(view, dj.DjId):N0}");
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted($"{GetPendingBalance(view, dj.DjId):N0}");
            ImGui.TableSetColumnIndex(5);
            ImGui.TextWrapped(dj.Note ?? string.Empty);
            ImGui.TableSetColumnIndex(6);
            ImGui.BeginDisabled(isBusy);
            if (ImGui.SmallButton("Edit"))
                LoadDraft(dj);
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete"))
            {
                pendingArchiveDjId = dj.DjId;
                requestArchivePopup = true;
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawArchivePopup(VenueConnectionConfiguration venue, bool isBusy)
    {
        if (requestArchivePopup)
        {
            requestArchivePopup = false;
            ImGui.OpenPopup("Delete DJ###PartyPulseArchiveDj");
        }

        if (!ImGui.BeginPopupModal("Delete DJ###PartyPulseArchiveDj", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextWrapped("Delete this DJ from the active directory? Existing booking and status history is preserved for statistics.");
        ImGui.BeginDisabled(isBusy || pendingArchiveDjId is null);
        if (ImGui.Button("Delete DJ"))
        {
            plugin.ArchiveDj(venue, pendingArchiveDjId!.Value);
            if (editingDjId == pendingArchiveDjId)
                ClearDraft();
            pendingArchiveDjId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Keep"))
        {
            pendingArchiveDjId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void EnsureSettingsDraft(DjViewResponse view)
    {
        if (settingsInitialized)
            return;

        defaultHourlyRateGil = view.DefaultHourlyRateGil.ToString(CultureInfo.InvariantCulture);
        selectedLinkDjId = view.Djs.FirstOrDefault()?.DjId ?? 0;
        settingsInitialized = true;
    }

    private void LoadDraft(DjSummary dj)
    {
        editingDjId = dj.DjId;
        name = dj.Name;
        twitchUrl = dj.TwitchUrl ?? string.Empty;
        resident = dj.Resident;
        note = dj.Note ?? string.Empty;
    }

    private void ClearDraft()
    {
        editingDjId = null;
        name = string.Empty;
        twitchUrl = string.Empty;
        resident = false;
        note = string.Empty;
    }

    private void ResetForVenueChange(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId)
            return;

        activeProfileId = venue.ProfileId;
        ClearDraft();
        pendingArchiveDjId = null;
        requestArchivePopup = false;
        settingsInitialized = false;
        defaultHourlyRateGil = "0";
        selectedLinkDjId = 0;
        selectedPayoutDjId = 0;
        proxyPayoutConfirmed = false;
        balanceRefundConfirmed = false;
        payoutTargetKey = string.Empty;
    }
}
