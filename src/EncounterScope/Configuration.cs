using Dalamud.Configuration;
namespace EncounterScope;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    public void Normalize()
    {
        Version = 1;
    }
}
