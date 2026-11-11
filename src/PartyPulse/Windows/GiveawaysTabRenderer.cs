using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Giveaways;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class GiveawaysTabRenderer(Plugin plugin)
{
    private const string LocalDateTimeFormat = "yyyy-MM-dd HH:mm";
    private Guid activeProfileId;

    private long? editingGiveawayId;
    private bool editingPostedGiveaway;
    private long giveawayChannelId;
    private string giveawayTitle = string.Empty;
    private string giveawayDescription = string.Empty;
    private int giveawayPrizeGil;
    private string congratulationsMessage = "Congratulations <winner>!";
    private string startsAtLocal = string.Empty;
    private string endsAtLocal = string.Empty;

    private long? editingSchedulerId;
    private long schedulerChannelId;
    private string schedulerTitle = string.Empty;
    private string schedulerDescription = string.Empty;
    private int schedulerPrizeGil;
    private string schedulerCongratulations = "Congratulations <winner>!";
    private int firstDrawOffsetMinutes = 30;
    private int repeatIntervalMinutes = 15;
    private int lastDrawBufferMinutes = 10;
    private string schedulerStartMode = GiveawaySchedulerStartModes.BeforeEachDraw;
    private int schedulerStartOffsetMinutes = 5;
    private bool schedulerEnabled = true;
    private string announcementMacro = string.Empty;
    private GiveawayWinnerSummary? pendingWinner;
    private string? pendingWinnerAction;
    private PlayerIdentity? pendingTradeTarget;
    private bool requestWinnerConfirmation;

    public void Draw(VenueConnectionConfiguration venue, MainSubtab subtab)
    {
        ResetForVenueChange(venue);
        if (subtab == MainSubtab.GiveawayWinners)
        {
            DrawWinners(venue);
            return;
        }

        plugin.EnsureGiveawaysLoaded(venue);
        var snapshot = plugin.Giveaways.GetSnapshot(venue);
        var view = snapshot.View;
        var isBusy = plugin.Giveaways.IsBusy(venue.ProfileId);

        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Refresh giveaways")) plugin.RefreshGiveaways(venue);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled($"Times use {VenueTimeZone.Resolve(venue).DisplayName}; Discord renders them for each member.");

        if (view is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            return;
        }

        if (!view.Capabilities.CanManage)
        {
            ImGui.TextWrapped("You do not have permission to manage giveaways.");
            return;
        }

        if (!view.Capabilities.HasLinkedGuild)
        {
            ImGui.TextColored(PartyPulseUi.Warning, "Link a Discord server before creating giveaways.");
        }
        else if (!view.Channels.Any(channel => channel.CanPost))
        {
            ImGui.TextColored(
                PartyPulseUi.Warning,
                "No postable channel is available yet. Check the bot's channel permissions and wait for Discord metadata synchronization.");
        }

        switch (subtab)
        {
            case MainSubtab.GiveawaysManage:
                DrawActiveGiveawayMacro(venue);
                DrawGiveawayEditor(venue, snapshot, view, isBusy);
                ImGui.Spacing();
                ImGui.Separator();
                DrawGiveawayLists(venue, view, isBusy);
                break;
            case MainSubtab.GiveawaysScheduler:
                DrawSchedulerEditor(venue, view, isBusy);
                ImGui.Spacing();
                ImGui.Separator();
                DrawSchedulers(venue, view, isBusy);
                break;
        }
    }

    private void DrawWinners(VenueConnectionConfiguration venue)
    {
        plugin.EnsureGiveawayWinnersLoaded(venue);
        var snapshot = plugin.Giveaways.GetWinnersSnapshot(venue);
        var busy = plugin.Giveaways.IsBusy(venue.ProfileId) || plugin.IsGiveawayWinnerOperationBusy(venue.ProfileId);

        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Refresh winners"))
            plugin.RefreshGiveawayWinners(venue);
        ImGui.EndDisabled();

        if (snapshot.View is not { } view)
        {
            ImGui.TextWrapped(snapshot.Message);
            return;
        }

        DrawActiveGiveawayMacro(venue);
        if (ImGui.CollapsingHeader("Winner announcement macro"))
        {
            ImGui.TextWrapped("Saved for this venue on this device. Use <discordname> in the message text to insert the winner's guild nickname, saved display name, or username in that order.");
            ImGui.InputTextMultiline("##GiveawayAnnouncementMacro", ref announcementMacro, 4000,
                new Vector2(0, 90 * ImGuiHelpers.GlobalScale));
            var normalized = announcementMacro.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n').TrimEnd();
            var validMacro = string.IsNullOrWhiteSpace(normalized) || Plugin.IsValidGiveawayAnnouncementMacro(normalized);
            ImGui.BeginDisabled(busy || !validMacro || normalized == venue.GiveawayWinnerAnnouncementMacro);
            if (ImGui.Button("Save announcement macro"))
            {
                venue.GiveawayWinnerAnnouncementMacro = normalized;
                plugin.Configuration.Save();
            }
            ImGui.EndDisabled();
            if (!validMacro)
                ImGui.TextColored(PartyPulseUi.Warning, "Use <discordname> in the message text, with up to 15 lines of 180 characters.");
        }

        if (view.Winners.Count == 0)
            ImGui.TextDisabled("No winners awaiting payout.");

        plugin.TargetProvider.TryGetCurrentTarget(out var target, out _);
        var canAnnounce = !plugin.IsGameMacroBusy &&
            (venue.GiveawayWinnerAnnouncementMacro?.Contains("<discordname>", StringComparison.Ordinal) ?? false);

        foreach (var winner in view.Winners)
        {
            ImGui.PushID(winner.GiveawayId.ToString(CultureInfo.InvariantCulture));
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextUnformatted(winner.Title);
            var (name, label) = Plugin.GetWinnerDiscordName(winner);
            var discordDetails = $"{label}: {name}";
            if (!string.Equals(name, winner.DiscordUsername, StringComparison.Ordinal))
                discordDetails += $"   ·   Discord username: {winner.DiscordUsername}";
            ImGui.TextWrapped(discordDetails);
            ImGui.TextDisabled(winner.PrizeGil is { } prize ? $"{prize:N0} gil" : "No gil prize recorded");
            ImGui.TextDisabled($"Drawn {VenueTimeZone.Format(venue, winner.WinnerSelectedAt, "g")}");
            ImGui.TextDisabled(winner.LastAnnouncedAt is { } announced
                ? $"Last announced {VenueTimeZone.Format(venue, announced, "g")}"
                : "Not announced yet");

            ImGui.BeginDisabled(busy || !canAnnounce);
            if (ImGui.SmallButton("Announce"))
                plugin.RunGiveawayWinnerAction(venue, winner, GiveawayWinnerActions.Announce);
            ImGui.EndDisabled();

            ImGui.SameLine();
            var hasTrade = winner.TradeAttemptId is not null;
            ImGui.BeginDisabled(busy || target is null || winner.PrizeGil is null || hasTrade);
            if (ImGui.SmallButton("Pay with Dropbox"))
                OpenWinnerConfirmation(winner, GiveawayWinnerActions.BeginTrade, target);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && target is null)
                ImGui.SetTooltip("Target the winner's player character in game.");

            ImGui.SameLine();
            ImGui.BeginDisabled(busy);
            if (ImGui.SmallButton("Confirm paid"))
                OpenWinnerConfirmation(winner, GiveawayWinnerActions.ConfirmPaid);
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(busy || hasTrade);
            if (ImGui.SmallButton("Cancel win"))
                OpenWinnerConfirmation(winner, GiveawayWinnerActions.Cancel);
            ImGui.EndDisabled();

            if (hasTrade)
            {
                ImGui.TextWrapped(winner.TradeStartedAt is { } started
                    ? $"Trade reserved {VenueTimeZone.Format(venue, started, "g")}. Check the trade before retrying."
                    : "Trade reserved. Check the trade before retrying.");
                var canRelease = winner.TradeStartedAt is { } startedAt &&
                    startedAt <= DateTimeOffset.UtcNow.AddSeconds(-30);
                ImGui.BeginDisabled(busy || !canRelease);
                if (ImGui.SmallButton("Release trade"))
                    OpenWinnerConfirmation(winner, GiveawayWinnerActions.ReleaseTrade);
                ImGui.EndDisabled();
                if (!canRelease && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Release becomes available 30 seconds after reserving a trade.");
            }

            ImGui.PopID();
        }

        DrawWinnerConfirmation(venue, busy);
    }

    private void OpenWinnerConfirmation(
        GiveawayWinnerSummary winner, string action, PlayerIdentity? target = null)
    {
        pendingWinner = winner;
        pendingWinnerAction = action;
        pendingTradeTarget = target;
        requestWinnerConfirmation = true;
    }

    private void DrawWinnerConfirmation(VenueConnectionConfiguration venue, bool busy)
    {
        const string popup = "Giveaway winner###PartyPulseGiveawayWinnerAction";
        if (requestWinnerConfirmation)
        {
            requestWinnerConfirmation = false;
            ImGui.OpenPopup(popup);
        }
        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (pendingWinner is { } winner && pendingWinnerAction is { } action)
        {
            ImGui.PushTextWrapPos(390 * ImGuiHelpers.GlobalScale);
            ImGui.TextWrapped(action switch
            {
                GiveawayWinnerActions.BeginTrade =>
                    $"Start a {winner.PrizeGil:N0} gil trade to {pendingTradeTarget?.DisplayName} for {winner.DiscordUsername}? Verify this character belongs to the winner.",
                GiveawayWinnerActions.ConfirmPaid =>
                    $"Record {winner.DiscordUsername} as paid for this giveaway? Confirm after delivering the prize.",
                GiveawayWinnerActions.Cancel =>
                    $"Cancel the win for {winner.DiscordUsername}? This removes the winner from the payout queue.",
                _ => $"Release the trade for {winner.DiscordUsername}? Check that no gil was transferred before allowing another attempt.",
            });
            ImGui.PopTextWrapPos();

            ImGui.BeginDisabled(busy);
            if (ImGui.Button("Confirm"))
            {
                plugin.RunGiveawayWinnerAction(venue, winner, action, pendingTradeTarget);
                pendingWinnerAction = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
        }

        if (ImGui.Button("Back"))
        {
            pendingWinnerAction = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawActiveGiveawayMacro(VenueConnectionConfiguration venue)
    {
        if (!CanSetPrize(venue))
            return;

        plugin.EnsureTimedMacrosLoaded(venue);
        var macro = plugin.TimedMacros.GetSnapshot(venue).View?.Macros.FirstOrDefault(value =>
            value.TypeCode == TimedMacroTypeCodes.GiveawayAdvertisement && value.CanExecute);
        if (macro is null)
            return;

        ImGui.BeginDisabled(!macro.Enabled || !macro.IsConfigured || plugin.IsGameMacroBusy ||
                           plugin.TimedMacros.IsBusy(venue.ProfileId));
        if (ImGui.Button("Execute giveaway macro"))
            plugin.RunTimedMacro(venue, macro, null);
        ImGui.EndDisabled();
        if (!macro.IsConfigured)
            ImGui.TextDisabled("Configure the giveaway macro in Timed Macros > Setup.");
        ImGui.Spacing();
    }

    private void DrawGiveawayEditor(
        VenueConnectionConfiguration venue,
        GiveawayManagementSnapshot snapshot,
        GiveawayManagementViewResponse view,
        bool isBusy)
    {
        PartyPulseUi.SectionHeader(
            editingGiveawayId is null ? "New giveaway" : "Edit giveaway",
            editingPostedGiveaway
                ? "The Discord title, description, end time, and winner message can still be updated. The channel and start are locked after posting."
                : "The end must remain in the future. Use <winner> in the congratulations text to mention the selected member.");

        DrawChannelCombo("Channel", view, ref giveawayChannelId, editingPostedGiveaway);
        ImGui.SetNextItemWidth(460 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Title", ref giveawayTitle, 100);
        ImGui.InputTextMultiline(
            "Description",
            ref giveawayDescription,
            2000,
            new Vector2(0, 85 * ImGuiHelpers.GlobalScale));
        ImGui.InputTextMultiline(
            "Congratulations message",
            ref congratulationsMessage,
            2000,
            new Vector2(0, 65 * ImGuiHelpers.GlobalScale));
        var canSetPrize = CanSetPrize(venue);
        var prizeValid = !canSetPrize || DrawPrizeInput("Winner prize (gil)", ref giveawayPrizeGil);

        ImGui.BeginDisabled(editingPostedGiveaway);
        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Start", ref startsAtLocal, 16);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("End", ref endsAtLocal, 16);
        ImGui.TextDisabled($"Format: {LocalDateTimeFormat}");

        var validStart = TryParseLocalDateTime(venue, startsAtLocal, out var startsAt, out var startError);
        var validEnd = TryParseLocalDateTime(venue, endsAtLocal, out var endsAt, out var endError);
        var timeError = !validStart ? startError
            : !validEnd ? endError
            : endsAt <= startsAt ? "End must be later than start."
            : endsAt <= snapshot.EstimatedServerNow ? "End must be in the future."
            : string.Empty;
        var contentError = string.IsNullOrWhiteSpace(giveawayTitle) ? "Enter a title."
            : string.IsNullOrWhiteSpace(giveawayDescription) ? "Enter a description."
            : !congratulationsMessage.Contains("<winner>", StringComparison.Ordinal)
                ? "Congratulations message must include <winner>."
                : ContainsUnsafeMention(congratulationsMessage)
                    ? "Only the <winner> placeholder may create a mention."
                    : RenderedWinnerMessageLength(congratulationsMessage) > 2000
                        ? "Congratulations message is too long after replacing <winner>."
                        : string.Empty;
        var canSave = giveawayChannelId > 0 && prizeValid && string.IsNullOrEmpty(timeError) && string.IsNullOrEmpty(contentError);
        if (!string.IsNullOrEmpty(contentError)) ImGui.TextColored(PartyPulseUi.Warning, contentError);
        else if (!string.IsNullOrEmpty(timeError)) ImGui.TextColored(PartyPulseUi.Warning, timeError);

        ImGui.BeginDisabled(isBusy || !canSave);
        if (ImGui.Button(editingGiveawayId is null ? "Create giveaway" : "Save giveaway"))
        {
            plugin.SaveGiveaway(venue, editingGiveawayId, new SaveGiveawayRequest
            {
                ChannelId = giveawayChannelId,
                Title = giveawayTitle,
                Description = giveawayDescription,
                CongratulationsMessage = congratulationsMessage,
                StartsAt = startsAt,
                EndsAt = endsAt,
                PrizeGil = canSetPrize && giveawayPrizeGil > 0 ? giveawayPrizeGil : null,
            });
            ClearGiveawayDraft(venue);
        }
        ImGui.EndDisabled();
        if (editingGiveawayId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel edit")) ClearGiveawayDraft(venue);
        }
    }

    private void DrawGiveawayLists(VenueConnectionConfiguration venue, GiveawayManagementViewResponse view, bool isBusy)
    {
        PartyPulseUi.SectionHeader("Active and pending", "Posted edits are synchronized back to the original Discord message.");
        DrawGiveawayTable(venue, view.ActiveAndPending, isBusy, canEdit: true, "ActiveGiveaways");
        ImGui.Spacing();
        PartyPulseUi.SectionHeader("Ended giveaways");
        DrawGiveawayTable(venue, view.Ended, isBusy, canEdit: false, "EndedGiveaways");
    }

    private void DrawGiveawayTable(
        VenueConnectionConfiguration venue,
        System.Collections.Generic.IReadOnlyList<GiveawaySummary> giveaways,
        bool isBusy,
        bool canEdit,
        string tableId)
    {
        if (giveaways.Count == 0)
        {
            ImGui.TextDisabled(canEdit ? "No active or pending giveaways." : "No ended giveaways.");
            return;
        }

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable(tableId, 7, flags)) return;
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 75 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Title");
        ImGui.TableSetupColumn("Channel");
        ImGui.TableSetupColumn("Start");
        ImGui.TableSetupColumn("End");
        ImGui.TableSetupColumn("Entries", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed,
            (canEdit ? 105 : 55) * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var giveaway in giveaways)
        {
            ImGui.PushID($"{tableId}-{giveaway.GiveawayId}");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(giveaway.Status);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(giveaway.Title);
            if (giveaway.SourceType == GiveawaySourceTypes.Scheduler) ImGui.TextDisabled("Scheduled");
            if (giveaway.PrizeGil is { } prize) ImGui.TextDisabled($"{prize:N0} gil");
            if (!string.IsNullOrWhiteSpace(giveaway.WinnerDisplayName))
                ImGui.TextDisabled($"Winner: {giveaway.WinnerDisplayName}");
            if (!string.IsNullOrWhiteSpace(giveaway.LastError))
            {
                ImGui.TextColored(PartyPulseUi.Warning, "Discord sync issue");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(giveaway.LastError);
            }
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted($"#{giveaway.ChannelName}");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(VenueTimeZone.Format(venue, giveaway.StartsAt, "g"));
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(VenueTimeZone.Format(venue, giveaway.EndsAt, "g"));
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(giveaway.EntryCount.ToString("N0", CultureInfo.CurrentCulture));
            ImGui.TableSetColumnIndex(6);
            ImGui.BeginDisabled(isBusy);
            if (ImGui.SmallButton("Copy")) LoadGiveawayDraft(venue, giveaway, copy: true);
            if (canEdit && giveaway.Status != GiveawayStatuses.Ending)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Edit")) LoadGiveawayDraft(venue, giveaway, copy: false);
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawSchedulerEditor(VenueConnectionConfiguration venue, GiveawayManagementViewResponse view, bool isBusy)
    {
        PartyPulseUi.SectionHeader(
            editingSchedulerId is null ? "New scheduler" : "Edit scheduler",
            "Templates create real giveaways only when their calculated start is due. Edits affect future giveaways only.");
        DrawChannelCombo("Scheduler channel", view, ref schedulerChannelId, disabled: false);
        ImGui.SetNextItemWidth(460 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Scheduler title", ref schedulerTitle, 100);
        ImGui.InputTextMultiline("Scheduler description", ref schedulerDescription, 2000,
            new Vector2(0, 85 * ImGuiHelpers.GlobalScale));
        ImGui.InputTextMultiline("Scheduler congratulations", ref schedulerCongratulations, 2000,
            new Vector2(0, 65 * ImGuiHelpers.GlobalScale));
        var canSetPrize = CanSetPrize(venue);
        var prizeValid = !canSetPrize || DrawPrizeInput("Winner prize (gil)", ref schedulerPrizeGil);

        ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("First draw: minutes after opening", ref firstDrawOffsetMinutes);
        ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Repeat every (minutes)", ref repeatIntervalMinutes);
        ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Last draw: minutes before closing", ref lastDrawBufferMinutes);

        var startModeLabel = schedulerStartMode == GiveawaySchedulerStartModes.BeforeEachDraw
            ? "Before each draw"
            : "Once, relative to opening";
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Giveaway starts", startModeLabel))
        {
            if (ImGui.Selectable("Before each draw", schedulerStartMode == GiveawaySchedulerStartModes.BeforeEachDraw))
            {
                schedulerStartMode = GiveawaySchedulerStartModes.BeforeEachDraw;
                if (schedulerStartOffsetMinutes <= 0) schedulerStartOffsetMinutes = 5;
            }
            if (ImGui.Selectable("Once, relative to opening", schedulerStartMode == GiveawaySchedulerStartModes.OpeningRelative))
            {
                schedulerStartMode = GiveawaySchedulerStartModes.OpeningRelative;
                schedulerStartOffsetMinutes = 0;
            }
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt(
            schedulerStartMode == GiveawaySchedulerStartModes.BeforeEachDraw
                ? "Minutes before each draw"
                : "Minutes from opening (negative = before)",
            ref schedulerStartOffsetMinutes);
        ImGui.Checkbox("Enabled", ref schedulerEnabled);

        var timingValid = firstDrawOffsetMinutes is >= 0 and <= 10080 &&
                          repeatIntervalMinutes is >= 1 and <= 10080 &&
                          lastDrawBufferMinutes is >= 0 and <= 10080 &&
                          schedulerStartOffsetMinutes is >= -10080 and <= 10080 &&
                          (schedulerStartMode == GiveawaySchedulerStartModes.BeforeEachDraw
                              ? schedulerStartOffsetMinutes > 0
                              : schedulerStartOffsetMinutes < firstDrawOffsetMinutes);
        var contentValid = schedulerChannelId > 0 && !string.IsNullOrWhiteSpace(schedulerTitle) &&
                           !string.IsNullOrWhiteSpace(schedulerDescription) &&
                           schedulerCongratulations.Contains("<winner>", StringComparison.Ordinal) &&
                           !ContainsUnsafeMention(schedulerCongratulations) &&
                           RenderedWinnerMessageLength(schedulerCongratulations) <= 2000;
        if (!timingValid)
            ImGui.TextColored(PartyPulseUi.Warning, "Check the minute values; a shared start must be earlier than the first draw.");
        else if (!contentValid)
            ImGui.TextColored(PartyPulseUi.Warning, "Select a channel and complete all text fields, including <winner>.");

        ImGui.BeginDisabled(isBusy || !timingValid || !contentValid || !prizeValid);
        if (ImGui.Button(editingSchedulerId is null ? "Create scheduler" : "Save scheduler"))
        {
            plugin.SaveGiveawayScheduler(venue, editingSchedulerId, new SaveGiveawaySchedulerRequest
            {
                ChannelId = schedulerChannelId,
                Title = schedulerTitle,
                Description = schedulerDescription,
                CongratulationsMessage = schedulerCongratulations,
                FirstDrawOffsetMinutes = firstDrawOffsetMinutes,
                RepeatIntervalMinutes = repeatIntervalMinutes,
                LastDrawBufferMinutes = lastDrawBufferMinutes,
                StartMode = schedulerStartMode,
                StartOffsetMinutes = schedulerStartOffsetMinutes,
                Enabled = schedulerEnabled,
                PrizeGil = canSetPrize && schedulerPrizeGil > 0 ? schedulerPrizeGil : null,
            });
            ClearSchedulerDraft();
        }
        ImGui.EndDisabled();
        if (editingSchedulerId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel scheduler edit")) ClearSchedulerDraft();
        }
    }

    private void DrawSchedulers(VenueConnectionConfiguration venue, GiveawayManagementViewResponse view, bool isBusy)
    {
        PartyPulseUi.SectionHeader("Schedulers");
        if (view.Schedulers.Count == 0)
        {
            ImGui.TextDisabled("No giveaway schedulers.");
            return;
        }
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("GiveawaySchedulers", 6, flags)) return;
        ImGui.TableSetupColumn("Title");
        ImGui.TableSetupColumn("Channel");
        ImGui.TableSetupColumn("First draw");
        ImGui.TableSetupColumn("Repeat");
        ImGui.TableSetupColumn("Start rule");
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 50 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        foreach (var scheduler in view.Schedulers)
        {
            ImGui.PushID($"scheduler-{scheduler.SchedulerId}");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(scheduler.Title);
            if (scheduler.PrizeGil is { } prize) ImGui.TextDisabled($"{prize:N0} gil");
            if (!scheduler.Enabled) ImGui.TextDisabled("Disabled");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"#{scheduler.ChannelName}");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted($"+{scheduler.FirstDrawOffsetMinutes} min");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted($"{scheduler.RepeatIntervalMinutes} min");
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(scheduler.StartMode == GiveawaySchedulerStartModes.BeforeEachDraw
                ? $"{scheduler.StartOffsetMinutes} min before each draw"
                : $"{scheduler.StartOffsetMinutes:+#;-#;0} min from opening");
            ImGui.TableSetColumnIndex(5);
            ImGui.BeginDisabled(isBusy);
            if (ImGui.SmallButton("Edit")) LoadSchedulerDraft(scheduler);
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private bool CanSetPrize(VenueConnectionConfiguration venue) =>
        plugin.Giveaways.GetWinnersSnapshot(venue).View is not null;

    private static bool DrawPrizeInput(string label, ref int prizeGil)
    {
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt(label, ref prizeGil, 0);
        if (prizeGil is < 0 or > GiveawayLimits.MaxPrizeGil)
        {
            ImGui.TextColored(PartyPulseUi.Warning, $"Enter up to {GiveawayLimits.MaxPrizeGil:N0} gil.");
            return false;
        }
        if (prizeGil == 0)
            ImGui.TextDisabled("Zero leaves the gil prize unset or keeps the saved amount.");
        return true;
    }

    private static void DrawChannelCombo(
    string label,
    GiveawayManagementViewResponse view,
    ref long selectedChannelId,
    bool disabled)
    {
        var currentChannelId = selectedChannelId;

        var selected = view.Channels.FirstOrDefault(channel => channel.ChannelId == currentChannelId);

        ImGui.BeginDisabled(disabled);
        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);

        if (ImGui.BeginCombo(
                label,
                selected is null
                    ? "Select a channel"
                    : DiscordChannelDisplayName.ToAsciiLetters(selected.Name)))
        {
            foreach (var channel in view.Channels.Where(channel => channel.CanPost)
                         .OrderByDescending(channel => channel.Position)
                         .ThenBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase))
            {
                var displayName = DiscordChannelDisplayName.ToAsciiLetters(channel.Name);
                if (ImGui.Selectable(
                        $"{displayName}##GiveawayChannel{channel.ChannelId}",
                        channel.ChannelId == currentChannelId))
                {
                    selectedChannelId = channel.ChannelId;
                    currentChannelId = channel.ChannelId; // keeps local in sync if needed
                }
            }

            ImGui.EndCombo();
        }

        ImGui.EndDisabled();
    }

    private void LoadGiveawayDraft(VenueConnectionConfiguration venue, GiveawaySummary giveaway, bool copy)
    {
        editingGiveawayId = copy ? null : giveaway.GiveawayId;
        editingPostedGiveaway = !copy && giveaway.PostedAt is not null;
        giveawayChannelId = giveaway.ChannelId;
        giveawayTitle = giveaway.Title;
        giveawayDescription = giveaway.Description;
        giveawayPrizeGil = giveaway.PrizeGil ?? 0;
        congratulationsMessage = giveaway.CongratulationsMessage;
        startsAtLocal = VenueTimeZone.Format(venue, giveaway.StartsAt, LocalDateTimeFormat, CultureInfo.InvariantCulture);
        endsAtLocal = VenueTimeZone.Format(venue, giveaway.EndsAt, LocalDateTimeFormat, CultureInfo.InvariantCulture);
    }

    private void LoadSchedulerDraft(GiveawaySchedulerSummary scheduler)
    {
        editingSchedulerId = scheduler.SchedulerId;
        schedulerChannelId = scheduler.ChannelId;
        schedulerTitle = scheduler.Title;
        schedulerDescription = scheduler.Description;
        schedulerPrizeGil = scheduler.PrizeGil ?? 0;
        schedulerCongratulations = scheduler.CongratulationsMessage;
        firstDrawOffsetMinutes = scheduler.FirstDrawOffsetMinutes;
        repeatIntervalMinutes = scheduler.RepeatIntervalMinutes;
        lastDrawBufferMinutes = scheduler.LastDrawBufferMinutes;
        schedulerStartMode = scheduler.StartMode;
        schedulerStartOffsetMinutes = scheduler.StartOffsetMinutes;
        schedulerEnabled = scheduler.Enabled;
    }

    private void ResetForVenueChange(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId) return;
        activeProfileId = venue.ProfileId;
        announcementMacro = venue.GiveawayWinnerAnnouncementMacro ?? string.Empty;
        requestWinnerConfirmation = false;
        pendingWinnerAction = null;
        ClearGiveawayDraft(venue);
        ClearSchedulerDraft();
    }

    private void ClearGiveawayDraft(VenueConnectionConfiguration venue)
    {
        editingGiveawayId = null;
        editingPostedGiveaway = false;
        giveawayChannelId = 0;
        giveawayTitle = string.Empty;
        giveawayDescription = string.Empty;
        giveawayPrizeGil = 0;
        congratulationsMessage = "Congratulations <winner>!";
        var now = DateTimeOffset.UtcNow.AddMinutes(5);
        startsAtLocal = VenueTimeZone.Format(venue, now, LocalDateTimeFormat, CultureInfo.InvariantCulture);
        endsAtLocal = VenueTimeZone.Format(venue, now.AddMinutes(30), LocalDateTimeFormat, CultureInfo.InvariantCulture);
    }

    private void ClearSchedulerDraft()
    {
        editingSchedulerId = null;
        schedulerChannelId = 0;
        schedulerTitle = string.Empty;
        schedulerDescription = string.Empty;
        schedulerPrizeGil = 0;
        schedulerCongratulations = "Congratulations <winner>!";
        firstDrawOffsetMinutes = 30;
        repeatIntervalMinutes = 15;
        lastDrawBufferMinutes = 10;
        schedulerStartMode = GiveawaySchedulerStartModes.BeforeEachDraw;
        schedulerStartOffsetMinutes = 5;
        schedulerEnabled = true;
    }

    private static bool TryParseLocalDateTime(
        VenueConnectionConfiguration venue,
        string value,
        out DateTimeOffset dateTimeOffset,
        out string error) =>
        VenueTimeZone.TryParseExact(
            venue,
            value,
            LocalDateTimeFormat,
            CultureInfo.InvariantCulture,
            out dateTimeOffset,
            out error);

    private static bool ContainsUnsafeMention(string value) =>
        value.Contains("@everyone", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("@here", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("<@", StringComparison.Ordinal);

    private static int RenderedWinnerMessageLength(string value)
    {
        var length = value.Length;
        for (var index = 0; (index = value.IndexOf("<winner>", index, StringComparison.Ordinal)) >= 0; index += 8)
            length += 15;
        return length;
    }
}
