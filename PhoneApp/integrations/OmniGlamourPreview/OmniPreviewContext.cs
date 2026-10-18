using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
namespace OmniToolbox.LocalGlamourPreview;
internal sealed class OmniPreviewContext
{
    [PluginService] public IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] public ITextureReadbackProvider TextureReadback { get; private set; } = null!;
    [PluginService] public IFramework Framework { get; private set; } = null!;
    [PluginService] public IClientState ClientState { get; private set; } = null!;
    [PluginService] public IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public ICondition Condition { get; private set; } = null!;
    [PluginService] public IGameGui GameGui { get; private set; } = null!;
    [PluginService] public IDataManager DataManager { get; private set; } = null!;
    [PluginService] public IPluginLog Log { get; private set; } = null!;
}
