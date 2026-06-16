using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using PartyPulse.Models;

namespace PartyPulse.Services;

public sealed class TargetPlayerProvider(ITargetManager targetManager)
{
    public bool TryGetCurrentTarget(out PlayerIdentity? identity, out string reason)
    {
        identity = null;

        if (targetManager.Target is not IPlayerCharacter player)
        {
            reason = "Target a player character first.";
            return false;
        }

        var characterName = player.Name.TextValue.Trim();
        if (characterName.Length == 0)
        {
            reason = "The targeted player's name is not available.";
            return false;
        }

        if (!player.HomeWorld.IsValid)
        {
            reason = "The targeted player's home world is not available.";
            return false;
        }

        var worldName = player.HomeWorld.Value.Name.ToString().Trim();
        if (worldName.Length == 0)
        {
            reason = "The targeted player's home world is empty.";
            return false;
        }

        identity = new PlayerIdentity(characterName, worldName);
        reason = string.Empty;
        return true;
    }
}
