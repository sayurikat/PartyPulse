using Dalamud.Configuration;
using System;

namespace PartyPulse;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool SomePropertyToBeSavedAndWithADefault { get; set; } = true;



    public string ApiBaseUrl { get; set; } = "https://partypulse.azurewebsites.net";
    public string? RefreshToken { get; set; }
    public string? AccessToken { get; set; }
    public string? AccessSignature { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public string? DeviceName { get; set; }
    public Guid DeviceId { get; set; } = Guid.Empty;


    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
