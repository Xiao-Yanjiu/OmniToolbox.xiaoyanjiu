#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using OmniToolbox.UI.Theme;
using OmenTools.OmenService;

namespace OmniToolbox.LocalModules;

[ModuleApiVersion(1)]
public sealed class CommandCatalog : OmniModuleBase
{
    public override OmniToolbox.UI.ModuleLocalization Localization => CommandCatalogText.Catalog;

    public override ModuleInfo Info { get; } = new()
    {


        Category = ModuleCategory.Interface,
        Author = "小烟酒"
    };
    public override bool HasSettings => true;
    private const string WakeCommand = "/cmdcatalog";
    private bool enabled, open, ownsCommand;
    private string search = "", parameters = "", selected = "", status = "";
    private int filter;
    private long refreshAt;
    private readonly Dictionary<string, Entry> native = new(StringComparer.OrdinalIgnoreCase);
    private List<Entry> entries = [];
    private sealed record Entry(string Command, string Description, bool Plugin);

    protected override void OnStart(TreeTools.Lifecycle.FeatureLifetime activation)
    {
        CommandCatalogText.Bind(this, activation);

        api1Stopped = false;

        activation.OnStopping(StopActivation);

        enabled = true;
        try
        {
            ownsCommand = DalamudServices.CommandManager.AddHandler(WakeCommand,
                new CommandInfo((_, _) => open = !open) { HelpMessage = CommandCatalogText.Get("Text0001") });
            LoadNativeCommands();
            Refresh();
            DalamudServices.PluginInterface.UiBuilder.Draw += Draw;
        }
        catch { Stop(); throw; }
    }
    private bool api1Stopped = true;
    private void StopActivation() { if (api1Stopped) return; api1Stopped = true; StopCore(); }
    protected override void OnStop() => StopActivation();
    private void StopCore() => Stop();
    protected override void OnDestroy() => Stop();
    private void Stop()
    {
        enabled = false;
        open = false;
        DalamudServices.PluginInterface.UiBuilder.Draw -= Draw;
        if (ownsCommand) DalamudServices.CommandManager.RemoveHandler(WakeCommand);
        ownsCommand = false;
        entries.Clear();
        native.Clear();
    }
    public override bool TryHandleCommand(string arguments)
    {
        if (!arguments.Trim().Equals("ui", StringComparison.OrdinalIgnoreCase)) return false;
        open = !open;
        return true;
    }
    public override bool DrawSettings()
    {
        if (ImGui.Button(CommandCatalogText.Get("Text0002"))) open = true;
        ImGui.SameLine();
        ImGui.TextDisabled(ownsCommand ? WakeCommand : "/omni CommandCatalog ui");
        return false;
    }

#pragma warning disable PendingExcelSchema
    private void LoadNativeCommands()
    {
        try
        {
            var next = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Experimental.TextCommand>())
            {
                string description = row.Description.ExtractText().Trim();
                if (description.Length == 0) description = "游戏未提供说明";
                foreach (string alias in new[] { row.Command.ExtractText(), row.ShortCommand.ExtractText(), row.Alias.ExtractText(), row.ShortAlias.ExtractText() })
                {
                    string name = alias.Trim();
                    if (name.StartsWith('/') && name.Length > 1)
                        next[name] = new Entry(name, description, false);
                }
            }
            native.Clear();
            foreach (var item in next) native[item.Key] = item.Value;
            if (native.Count == 0) status = CommandCatalogText.Get("Text0003");
        }
        catch (Exception e) { status = CommandCatalogText.Get("Text0004") + e.Message; }
    }
#pragma warning restore PendingExcelSchema

    private void Refresh()
    {
        try
        {
            var combined = new Dictionary<string, Entry>(native, StringComparer.OrdinalIgnoreCase);
            foreach (var item in DalamudServices.CommandManager.Commands.ToArray())
            {
                string help = item.Value.HelpMessage?.Trim() ?? "";
                combined[item.Key] = new Entry(item.Key, help.Length == 0 ? "插件未提供说明" : help, true);
            }
            entries = combined.Values.OrderBy(e => e.Command, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) { status = CommandCatalogText.Get("Text0005") + e.Message; }
        refreshAt = Environment.TickCount64 + 1000;
    }
    private static bool Matches(Entry entry, string query, int source) =>
        (source == 0 || entry.Plugin == (source == 2)) &&
        (string.IsNullOrWhiteSpace(query) || entry.Command.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ||
         entry.Description.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));

    private void Draw()
    {
        if (!enabled || !open) return;
        if (Environment.TickCount64 >= refreshAt) Refresh();
        using var font = TreeToolKit.Fonts.GetUIFont().Push();
        using var theme = new ComicStyleScope();
        float scale = OmniTheme.ScaleValue;
        ImGui.SetNextWindowSize(new Vector2(880, 600) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(560, 380) * scale, new Vector2(float.MaxValue));
        bool visible = ImGui.Begin(CommandCatalogText.Get("Text0006"), ref open);
        try { if (visible) DrawContent(scale); }
        finally { ImGui.End(); }
    }
    private void DrawContent(float scale)
    {
        ImGui.SetNextItemWidth(Math.Max(100 * scale, ImGui.GetContentRegionAvail().X - 230 * scale));
        ImGui.InputTextWithHint("##search", CommandCatalogText.Get("Text0007"), ref search, 256);
        ImGui.SameLine(); ImGui.SetNextItemWidth(120 * scale);
        ImGui.Combo("##source", ref filter, [CommandCatalogText.Get("Text0008"), CommandCatalogText.Get("Text0009"), CommandCatalogText.Get("Text0010")], 3);
        ImGui.SameLine();
        if (ImGui.Button(CommandCatalogText.Get("Text0011"))) { LoadNativeCommands(); Refresh(); }

        var visible = entries.Where(e => Matches(e, search, filter)).ToList();
        ImGui.TextDisabled(string.Format(CommandCatalogText.Get("Text0012"), visible.Count, entries.Count));
        float footerHeight = ImGui.GetFrameHeightWithSpacing() * 5 + ImGui.GetTextLineHeightWithSpacing() * 2;
        if (ImGui.BeginTable("commands", 3, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Max(ImGui.GetFrameHeight() * 3, ImGui.GetContentRegionAvail().Y - footerHeight))))
        {
            try
            {
                ImGui.TableSetupColumn(CommandCatalogText.Get("Text0013"), ImGuiTableColumnFlags.WidthFixed, 190 * scale);
                ImGui.TableSetupColumn(CommandCatalogText.Get("Text0014"), ImGuiTableColumnFlags.WidthFixed, 50 * scale);
                ImGui.TableSetupColumn(CommandCatalogText.Get("Text0015"), ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
                foreach (var entry in visible)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    if (ImGui.Selectable(entry.Command, selected == entry.Command, ImGuiSelectableFlags.SpanAllColumns))
                    { if (selected != entry.Command) parameters = ""; selected = entry.Command; }
                    ImGui.TableNextColumn(); ImGui.TextDisabled(entry.Plugin ? CommandCatalogText.Get("Text0010") : CommandCatalogText.Get("Text0009"));
                    ImGui.TableNextColumn(); ImGui.TextWrapped(entry.Description);
                }
            }
            finally { ImGui.EndTable(); }
        }
        if (visible.Count == 0) ImGui.TextDisabled(CommandCatalogText.Get("Text0016"));
        var choice = entries.FirstOrDefault(e => e.Command == selected);
        ImGui.Separator();
        ImGui.TextUnformatted(choice == null ? CommandCatalogText.Get("Text0017") : CommandCatalogText.Get("Text0018") + choice.Command);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##parameters", CommandCatalogText.Get("Text0019"), ref parameters, 512);
        ImGui.BeginDisabled(choice == null);
        try
        {
            if (ImGui.Button(CommandCatalogText.Get("Text0020"), new Vector2(100 * scale, 0)) && choice != null) QueueExecution(choice, parameters.Trim());
            ImGui.SameLine();
            if (ImGui.Button(CommandCatalogText.Get("Text0021")) && choice != null) ImGui.SetClipboardText(BuildCommand(choice.Command, parameters.Trim()));
        }
        finally { ImGui.EndDisabled(); }
        ImGui.TextWrapped(status);
    }
    private static string BuildCommand(string command, string arguments) => arguments.Length == 0 ? command : command + " " + arguments;
    private void QueueExecution(Entry choice, string arguments)
    {
        string text = BuildCommand(choice.Command, arguments);
        try
        {
            if (text.Contains('\n') || text.Contains('\r') || Encoding.UTF8.GetByteCount(text) > 500)
                throw new InvalidOperationException(CommandCatalogText.Get("Text0022"));
            status = CommandCatalogText.Get("Text0023");
            _ = DalamudServices.Framework.RunOnFrameworkThread(() =>
            {
                if (!enabled) return;
                try
                {
                    if (choice.Plugin)
                    {
                        if (!DalamudServices.CommandManager.ProcessCommand(text)) throw new InvalidOperationException(CommandCatalogText.Get("Text0024"));
                        status = CommandCatalogText.Get("Text0025") + text;
                    }
                    else
                    {
                        if (!DalamudServices.PlayerState.IsLoaded) throw new InvalidOperationException(CommandCatalogText.Get("Text0026"));
                        if (!DalamudServices.CommandManager.ProcessCommand(text)) ChatManager.Instance().SendMessage(text);
                        status = CommandCatalogText.Get("Text0027") + text + CommandCatalogText.Get("Text0028");
                    }
                }
                catch (Exception e) { status = CommandCatalogText.Get("Text0029") + e.Message; }
            });
        }
        catch (Exception e) { status = CommandCatalogText.Get("Text0029") + e.Message; }
    }
}

internal static class CommandCatalogText
{
    internal static readonly OmniToolbox.UI.ModuleLocalization Catalog = OmniToolbox.UI.ModuleLocalization.Create(
        new System.Collections.Generic.Dictionary<string,string>
        {
            ["Text0001"] = "打开或关闭宏指令目录",
            ["Text0002"] = "打开宏指令目录",
            ["Text0003"] = "游戏指令表为空，请点击刷新重试。",
            ["Text0004"] = "游戏指令读取失败：",
            ["Text0005"] = "插件指令刷新失败：",
            ["Text0006"] = "宏指令目录###OmniCommandCatalog",
            ["Text0007"] = "搜索指令或功能说明…",
            ["Text0008"] = "全部",
            ["Text0009"] = "游戏",
            ["Text0010"] = "插件",
            ["Text0011"] = "刷新",
            ["Text0012"] = "{0} / {1} 条指令 · 插件指令每秒刷新",
            ["Text0013"] = "指令",
            ["Text0014"] = "来源",
            ["Text0015"] = "功能说明",
            ["Text0016"] = "没有匹配的指令",
            ["Text0017"] = "请选择一条指令",
            ["Text0018"] = "已选：",
            ["Text0019"] = "指令参数（可留空）",
            ["Text0020"] = "执行",
            ["Text0021"] = "复制指令",
            ["Text0022"] = "一次只能执行一条指令，且不超过 500 字节。",
            ["Text0023"] = "正在提交…",
            ["Text0024"] = "插件指令已注销，请刷新后重试。",
            ["Text0025"] = "插件指令已受理：",
            ["Text0026"] = "请先进入游戏。",
            ["Text0027"] = "已提交：",
            ["Text0028"] = "（执行结果以游戏提示为准）",
            ["Text0029"] = "执行失败：",
            ["Title"] = "宏指令目录",
            ["Description"] = "实时列出游戏与插件斜杠指令及功能说明，支持搜索、来源筛选、填写参数执行和复制；使用 /cmdcatalog 打开，界面跟随 OMNI 主题。"
        });
    private static System.WeakReference<OmniToolbox.Common.Module.Abstractions.OmniModuleBase>? owner;
    private static TreeTools.Lifecycle.FeatureLifetime? lifetime;
internal static void Bind(OmniToolbox.Common.Module.Abstractions.OmniModuleBase module, TreeTools.Lifecycle.FeatureLifetime scope)
{
    owner = new(module);
    lifetime = scope;
}
internal static System.Threading.Tasks.Task Track(System.Threading.Tasks.Task task)
{
    var scope = lifetime;
    if (!task.IsCompleted && scope is { IsStopping: false })
    {
        try { scope.Track(task); }
        catch (System.InvalidOperationException) when (scope.IsStopping) { }
    }
    return task;
}
internal static System.Threading.Tasks.Task<T> Track<T>(System.Threading.Tasks.Task<T> task)
{
    Track((System.Threading.Tasks.Task)task);
    return task;
}
    internal static string Get(string key)
    {
        if (owner != null && owner.TryGetTarget(out var module))
            return OmniToolbox.UI.OmniLoc.Get(module, key).Replace('\u241f', '\0');
        return Catalog.Get(key, OmniToolbox.UI.OmniLoc.CurrentLanguage).Replace('\u241f', '\0');
    }
}
