using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using PartyPulse.Models;

namespace PartyPulse;

public sealed partial class Plugin
{
    private const string DiscordNamePlaceholder = "<discordname>";
    private readonly ConcurrentDictionary<Guid, byte> giveawayWinnerOperations = new();

    internal static (string Name, string Label) GetWinnerDiscordName(GiveawayWinnerSummary winner)
    {
        if (!string.IsNullOrWhiteSpace(winner.DiscordNickname))
            return (winner.DiscordNickname, "Guild nickname");
        if (!string.IsNullOrWhiteSpace(winner.DiscordDisplayName))
            return (winner.DiscordDisplayName, "Discord display name");
        return (winner.DiscordUsername, "Discord username");
    }

    internal static bool IsValidGiveawayAnnouncementMacro(string macro)
    {
        if (!macro.Contains(DiscordNamePlaceholder, StringComparison.Ordinal))
            return false;

        var lines = macro.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .TrimEnd().Split('\n');
        return lines.Length <= 15 && lines.All(line =>
        {
            if (line.Length > 180)
                return false;

            var command = line.TrimStart();
            var tokenEnd = command.IndexOfAny([' ', '\t']);
            var firstToken = tokenEnd < 0 ? command : command[..tokenEnd];
            return !firstToken.StartsWith('/') || !firstToken.Contains(DiscordNamePlaceholder, StringComparison.Ordinal);
        });
    }

    public bool IsGiveawayWinnerOperationBusy(Guid profileId) =>
        giveawayWinnerOperations.ContainsKey(profileId);

    public void EnsureGiveawayWinnersLoaded(VenueConnectionConfiguration venue)
    {
        if (Giveaways.ShouldLoadWinners(venue))
            RefreshGiveawayWinners(venue);
    }

    public void RefreshGiveawayWinners(VenueConnectionConfiguration venue) =>
        Observe(Giveaways.LoadWinnersAsync(venue, LifetimeToken), $"load giveaway winners for {venue.VenueCode}");

    public void RunGiveawayWinnerAction(
        VenueConnectionConfiguration venue,
        GiveawayWinnerSummary winner,
        string action,
        PlayerIdentity? tradeTarget = null) =>
        Observe(RunGiveawayWinnerActionAsync(venue, winner, action, tradeTarget),
            $"handle giveaway winner {winner.GiveawayId}");

    private async Task RunGiveawayWinnerActionAsync(
        VenueConnectionConfiguration venue,
        GiveawayWinnerSummary winner,
        string action,
        PlayerIdentity? tradeTarget)
    {
        if (!IdentityProvider.TryGetCurrent(out var actor, out var reason))
        {
            ChatGui.PrintError(reason, "PartyPulse");
            return;
        }

        var profileId = venue.ProfileId;
        var deviceId = venue.DeviceId;
        if (!giveawayWinnerOperations.TryAdd(profileId, 0))
            return;

        bool IsSameActor() =>
            venue.IsRegistered && venue.DeviceId == deviceId &&
            Configuration.GetSelectedVenue()?.ProfileId == profileId &&
            IdentityProvider.TryGetCurrent(out var current, out _) && current == actor;

        try
        {
            if (action is GiveawayWinnerActions.Announce or GiveawayWinnerActions.BeginTrade)
            {
                var refresh = await Giveaways.LoadWinnersAsync(venue, LifetimeToken);
                if (!refresh.Success || refresh.Value is null)
                {
                    ReportVipFailure(refresh.Failure, "The current giveaway winners could not be loaded.");
                    return;
                }

                var currentWinner = refresh.Value.Winners.FirstOrDefault(value => value.GiveawayId == winner.GiveawayId);
                if (currentWinner is null || !IsSameActor())
                {
                    ChatGui.PrintError("The winner, character, or venue changed. Review the winner list and try again.", "PartyPulse");
                    return;
                }

                if (action == GiveawayWinnerActions.Announce)
                {
                    var macro = venue.GiveawayWinnerAnnouncementMacro;
                    if (string.IsNullOrWhiteSpace(macro) || !IsValidGiveawayAnnouncementMacro(macro))
                    {
                        ChatGui.PrintError("Configure the giveaway winner announcement macro first.", "PartyPulse");
                        return;
                    }

                    var (discordName, _) = GetWinnerDiscordName(currentWinner);
                    // Keep Discord-supplied text on one line and prevent it from injecting game tags.
                    var safeName = new string(discordName
                        .Where(character => !char.IsControl(character) && character is not '\u2028' and not '\u2029')
                        .ToArray()).Replace('<', '＜').Replace('>', '＞').Trim();
                    if (safeName.StartsWith('/'))
                        safeName = "／" + safeName[1..];
                    var execution = await gameMacroExecutionService.ExecuteUntargetedAsync(
                        macro.Replace(DiscordNamePlaceholder, safeName, StringComparison.Ordinal),
                        LifetimeToken, IsSameActor);
                    if (!execution.Success)
                    {
                        ChatGui.PrintError(execution.ErrorMessage ?? "The winner announcement could not be started.", "PartyPulse");
                        return;
                    }
                }
                else
                {
                    await StartGiveawayWinnerTradeAsync(venue, winner, currentWinner, actor!, tradeTarget, IsSameActor);
                    return;
                }
            }

            var result = await Giveaways.UpdateWinnerAsync(
                venue, winner.GiveawayId,
                new UpdateGiveawayWinnerRequest
                {
                    Action = action,
                    TradeAttemptId = action is GiveawayWinnerActions.ReleaseTrade or GiveawayWinnerActions.ConfirmPaid
                        ? winner.TradeAttemptId : null,
                },
                actor!, LifetimeToken);
            if (!result.Success)
            {
                ReportVipFailure(result.Failure, "The giveaway winner action could not be recorded.");
                if (action == GiveawayWinnerActions.Announce)
                    ChatGui.PrintError("The announcement macro started. Its last-used time could not be confirmed.", "PartyPulse");
            }
            else
            {
                ChatGui.Print(action switch
                {
                    GiveawayWinnerActions.Announce => "Winner announcement started.",
                    GiveawayWinnerActions.ConfirmPaid => "Giveaway winner payment confirmed.",
                    GiveawayWinnerActions.Cancel => "Giveaway win cancelled.",
                    GiveawayWinnerActions.ReleaseTrade => "Giveaway trade reservation released.",
                    _ => "Giveaway winner updated.",
                }, "PartyPulse");
            }

            await Giveaways.LoadWinnersAsync(venue, LifetimeToken);
        }
        finally
        {
            giveawayWinnerOperations.TryRemove(profileId, out _);
        }
    }

    private async Task StartGiveawayWinnerTradeAsync(
        VenueConnectionConfiguration venue,
        GiveawayWinnerSummary reviewedWinner,
        GiveawayWinnerSummary currentWinner,
        PlayerIdentity actor,
        PlayerIdentity? target,
        Func<bool> isSameActor)
    {
        if (target is null || currentWinner.PrizeGil is not { } amount ||
            amount != reviewedWinner.PrizeGil || currentWinner.TradeAttemptId is not null)
        {
            ChatGui.PrintError("The winner's payout changed or a trade is already reserved. Review the winner list.", "PartyPulse");
            return;
        }

        var readiness = await settlementTradeService.CheckReadyAsync(target.CharacterName, target.WorldName, LifetimeToken);
        if (!readiness.Success)
        {
            ReportPluginIntegrationFailure(readiness.Failure, "Dropbox is unavailable or the trade target changed.");
            return;
        }

        if (!isSameActor())
            return;

        // Leave time for the local dispatch before another operator may release the reservation.
        var dispatchWindow = Stopwatch.StartNew();
        var reservation = await Giveaways.UpdateWinnerAsync(
            venue, currentWinner.GiveawayId,
            new UpdateGiveawayWinnerRequest { Action = GiveawayWinnerActions.BeginTrade, TradeAttemptId = Guid.NewGuid() },
            actor, LifetimeToken);
        if (!reservation.Success)
        {
            ReportVipFailure(reservation.Failure, "The giveaway trade could not be reserved.");
        }
        else
        {
            var trade = await settlementTradeService.InitiateTradeAsync(
                target.CharacterName, target.WorldName, amount, LifetimeToken,
                () => dispatchWindow.Elapsed < TimeSpan.FromSeconds(25) && isSameActor());
            if (trade.Success)
            {
                ChatGui.Print($"Dropbox started the {amount:N0} gil trade to {target.DisplayName}. Confirm paid after the trade completes.", "PartyPulse");
            }
            else
            {
                ReportPluginIntegrationFailure(trade.Failure, "Dropbox could not start the giveaway trade.");
                ChatGui.Print("The giveaway trade is reserved. Verify whether any gil moved before releasing it.", "PartyPulse");
            }
        }

        await Giveaways.LoadWinnersAsync(venue, LifetimeToken);
    }
}
