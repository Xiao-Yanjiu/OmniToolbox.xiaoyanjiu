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

public sealed class CommandCatalog : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "宏指令目录",
        Description = "",
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

    protected override void OnEnable()
    {
        enabled = true;
        try
        {
            ownsCommand = DalamudServices.CommandManager.AddHandler(WakeCommand,
                new CommandInfo((_, _) => open = !open) { HelpMessage = "打开或关闭宏指令目录" });
            LoadNativeCommands();
            Refresh();
            DalamudServices.PluginInterface.UiBuilder.Draw += Draw;
        }
        catch { Stop(); throw; }
    }
    protected override void OnDisable() => Stop();
    protected override void OnDispose() => Stop();
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
        if (ImGui.Button("打开宏指令目录")) open = true;
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
            if (native.Count == 0) status = "游戏指令表为空，请点击刷新重试。";
        }
        catch (Exception e) { status = "游戏指令读取失败：" + e.Message; }
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
        catch (Exception e) { status = "插件指令刷新失败：" + e.Message; }
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
        using var font = OmniFonts.GetUIFont().Push();
        using var theme = new ComicStyleScope();
        float scale = OmniTheme.ScaleValue;
        ImGui.SetNextWindowSize(new Vector2(880, 600) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(560, 380) * scale, new Vector2(float.MaxValue));
        bool visible = ImGui.Begin("宏指令目录###OmniCommandCatalog", ref open);
        try { if (visible) DrawContent(scale); }
        finally { ImGui.End(); }
    }
    private void DrawContent(float scale)
    {
        ImGui.SetNextItemWidth(Math.Max(100 * scale, ImGui.GetContentRegionAvail().X - 230 * scale));
        ImGui.InputTextWithHint("##search", "搜索指令或功能说明…", ref search, 256);
        ImGui.SameLine(); ImGui.SetNextItemWidth(120 * scale);
        ImGui.Combo("##source", ref filter, ["全部", "游戏", "插件"], 3);
        ImGui.SameLine();
        if (ImGui.Button("刷新")) { LoadNativeCommands(); Refresh(); }

        var visible = entries.Where(e => Matches(e, search, filter)).ToList();
        ImGui.TextDisabled($"{visible.Count} / {entries.Count} 条指令 · 插件指令每秒刷新");
        float footerHeight = ImGui.GetFrameHeightWithSpacing() * 5 + ImGui.GetTextLineHeightWithSpacing() * 2;
        if (ImGui.BeginTable("commands", 3, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Max(ImGui.GetFrameHeight() * 3, ImGui.GetContentRegionAvail().Y - footerHeight))))
        {
            try
            {
                ImGui.TableSetupColumn("指令", ImGuiTableColumnFlags.WidthFixed, 190 * scale);
                ImGui.TableSetupColumn("来源", ImGuiTableColumnFlags.WidthFixed, 50 * scale);
                ImGui.TableSetupColumn("功能说明", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
                foreach (var entry in visible)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    if (ImGui.Selectable(entry.Command, selected == entry.Command, ImGuiSelectableFlags.SpanAllColumns))
                    { if (selected != entry.Command) parameters = ""; selected = entry.Command; }
                    ImGui.TableNextColumn(); ImGui.TextDisabled(entry.Plugin ? "插件" : "游戏");
                    ImGui.TableNextColumn(); ImGui.TextWrapped(entry.Description);
                }
            }
            finally { ImGui.EndTable(); }
        }
        if (visible.Count == 0) ImGui.TextDisabled("没有匹配的指令");
        var choice = entries.FirstOrDefault(e => e.Command == selected);
        ImGui.Separator();
        ImGui.TextUnformatted(choice == null ? "请选择一条指令" : "已选：" + choice.Command);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##parameters", "指令参数（可留空）", ref parameters, 512);
        ImGui.BeginDisabled(choice == null);
        try
        {
            if (ImGui.Button("执行", new Vector2(100 * scale, 0)) && choice != null) QueueExecution(choice, parameters.Trim());
            ImGui.SameLine();
            if (ImGui.Button("复制指令") && choice != null) ImGui.SetClipboardText(BuildCommand(choice.Command, parameters.Trim()));
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
                throw new InvalidOperationException("一次只能执行一条指令，且不超过 500 字节。");
            status = "正在提交…";
            _ = DalamudServices.Framework.RunOnFrameworkThread(() =>
            {
                if (!enabled) return;
                try
                {
                    if (choice.Plugin)
                    {
                        if (!DalamudServices.CommandManager.ProcessCommand(text)) throw new InvalidOperationException("插件指令已注销，请刷新后重试。");
                        status = "插件指令已受理：" + text;
                    }
                    else
                    {
                        if (!DalamudServices.PlayerState.IsLoaded) throw new InvalidOperationException("请先进入游戏。");
                        if (!DalamudServices.CommandManager.ProcessCommand(text)) ChatManager.Instance().SendMessage(text);
                        status = "已提交：" + text + "（执行结果以游戏提示为准）";
                    }
                }
                catch (Exception e) { status = "执行失败：" + e.Message; }
            });
        }
        catch (Exception e) { status = "执行失败：" + e.Message; }
    }
}
