using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmenTools;
using OmniToolbox.Items;
using OmniToolbox.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
namespace OmniToolbox.LocalGlamourPreview;

public sealed unsafe partial class OmniGlamourPreview
{
    // Kept in a dedicated data file; never writes the built-in module's presets.
    private BetterGlamourManagementConfig config = new();
    private ItemPreviewService itemPreviewService = null!;
    private string? dataPath;
    public OmniGlamourPreview() { }
    public override bool HasSettings => true;
    public override bool DrawSettings()
    {
        ImGui.TextWrapped("选择方案自动更新右侧预览。请先停用内置的幻化管理增强，避免两套场景应用互相影响。");
        if (ImGui.Button("打开幻化管理与预览")) OpenManager();
        return false;
    }
    public override bool TryHandleCommand(string arguments)
    {
        if (!string.Equals(arguments.Trim(), "open", StringComparison.OrdinalIgnoreCase)) return false;
        OpenManager();
        return true;
    }
    private void TickOwnedPreview(IFramework framework) { itemPreviewService?.Tick(framework); RemoteTick(); }
    private void LoadLocalPresets()
    {
        string directory = Path.Combine(DService.Instance().PI.GetPluginConfigDirectory(), "TreeHouse");
        dataPath = Path.Combine(directory, "OmniGlamourPreview.presets.json");
        var builtIn = Path.Combine(directory, "BetterGlamourManagement.json");
        bool own = File.Exists(dataPath);
        var source = own ? dataPath : builtIn;
        if (!File.Exists(source)) { config = new(); return; }
        config = DecodeLocalPresets(File.ReadAllText(source), !own);
    }
    internal static BetterGlamourManagementConfig DecodeLocalPresets(string json, bool firstImport)
    {
        var root = JObject.Parse(json);
        var data = root["Config"] ?? root;
        var loaded = data.ToObject<BetterGlamourManagementConfig>() ?? throw new InvalidDataException("方案格式无效");
        if (loaded.Presets == null || loaded.Presets.Count > 1000 || loaded.Presets.Any(p => p == null || p.Items == null))
            throw new InvalidDataException("方案列表无效");
        // Importing data must not automatically equip a bound set on enable.
        if (firstImport) foreach (var preset in loaded.Presets) preset.GearsetIndex = -1;
        return loaded;
    }
    private void saveConfig()
    {
        if (dataPath == null) throw new InvalidOperationException("方案尚未加载");
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        string temporary = dataPath + ".tmp";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(config, Formatting.Indented));
        File.Move(temporary, dataPath, true);
    }
}
