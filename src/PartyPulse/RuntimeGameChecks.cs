#if PARTYPULSE_RUNTIME_STATUS
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PartyPulse.Integrations.Dropbox;
using PartyPulse.Integrations.Lifestream;
using PartyPulse.Models;
using PartyPulse.Shoutrunner;

namespace PartyPulse;

internal sealed class RuntimeGameChecks(DropboxApi dropbox, ShoutrunnerDutyManager shoutrunner)
{
    private readonly LifestreamApi lifestream = new(Plugin.PluginInterface, Plugin.Log);

    public object Read(VenueAddress? address) => new
    {
        Commands = Capture(() => new
        {
            PartyPulse = HasCommand("/pulse"),
            Lifestream = HasCommand("/li"),
            Dropbox = HasCommand("/dropbox"),
        }),
        NearbyPlayers = Capture(ReadNearbyPlayers),
        Shoutrunner = Capture(() => shoutrunner.TryGetCurrentLocation(out var location) ? location : null),
        Macro = Capture(ReadMacro),
        PartyFinder = Capture(ReadPartyFinder),
        Lifestream = Capture(() => lifestream.ReadRuntimeStatus(address)),
        Dropbox = Capture(dropbox.ReadRuntimeStatus),
    };

    private static bool HasCommand(string command) =>
        Plugin.CommandManager.Commands.Keys.Contains(command, StringComparer.OrdinalIgnoreCase);

    private static object ReadNearbyPlayers()
    {
        var table = Plugin.ObjectTable;
        var local = table.LocalPlayer;
        var selected = Plugin.TargetManager.Target as IPlayerCharacter;
        var visible = 0;
        var usable = 0;
        foreach (var entry in table.PlayerObjects)
        {
            if (entry is not IPlayerCharacter player || player.GameObjectId == local?.GameObjectId)
                continue;

            visible++;
            if (local is not null && player.GameObjectId != 0 && player.IsValid() && player.IsTargetable &&
                player.HomeWorld.IsValid && !string.IsNullOrWhiteSpace(player.HomeWorld.Value.Name.ToString()) &&
                !string.IsNullOrWhiteSpace(player.Name.TextValue) &&
                float.IsFinite(Vector3.DistanceSquared(local.Position, player.Position)))
            {
                usable++;
            }
        }

        return new
        {
            LocalPlayerAvailable = local is not null,
            VisiblePlayerCount = visible,
            UsablePlayerCount = usable,
            SelectedPlayerFound = selected is null ? (bool?)null : table.SearchById(selected.GameObjectId) is IPlayerCharacter,
        };
    }

    private static unsafe object ReadMacro()
    {
        var macro = RaptureMacroModule.Instance();
        var shell = RaptureShellModule.Instance();
        return new
        {
            MacroModuleAvailable = macro != null,
            ShellModuleAvailable = shell != null,
            TemporarySlotAvailable = macro != null && macro->GetMacro(0, 99) != null,
            Idle = shell == null ? (bool?)null : !shell->MacroLocked && shell->MacroCurrentLine < 0,
        };
    }

    private static unsafe object ReadPartyFinder() => new
    {
        AgentAvailable = AgentLookingForGroup.Instance() != null,
        Recruiting = Plugin.Condition[ConditionFlag.UsingPartyFinder],
        RecruitButton = ReadPartyFinderButton("LookingForGroup", 46),
        SubmitButton = ReadPartyFinderButton("LookingForGroupCondition", 113),
        RefreshButton = ReadPartyFinderButton("LookingForGroupDetail", 109),
    };

    private static unsafe bool? ReadPartyFinderButton(string addonName, uint buttonId)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(addonName, 1).Address;
        if (addon == null || !addon->IsVisible) return null;

        var button = addon->GetComponentButtonById(buttonId);
        return button != null && button->AtkResNode != null && button->AtkResNode->IsVisible() &&
               button->AtkComponentBase.OwnerNode != null &&
               button->AtkComponentBase.OwnerNode->AtkResNode.AtkEventManager.Event != null;
    }

    private static object? Capture(Func<object?> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception)
        {
            return new { Error = exception.GetType().Name };
        }
    }
}
#endif
