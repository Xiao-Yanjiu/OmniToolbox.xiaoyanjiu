
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.AddonEvent;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using OmniToolbox.UI.Controls;
using OmniToolbox.UI.Theme;

namespace OmniToolbox.LocalModules;

public sealed unsafe class AutoDiscardJunk : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动丢垃圾",
        Description = "",
        Category    = ModuleCategory.Item,
        Author      = "小烟酒",
    };

    private static readonly InventoryType[] PlayerBagTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static Vector4 BlacklistColor => OmniTheme.Tokens.Error;
    private static Vector4 InventoryColor => OmniTheme.ControlAccent;
    private static Vector4 WhitelistColor => OmniTheme.Tokens.Success;
    private static Vector4 HQColor        => OmniTheme.Tokens.Warning;

    private const string CommandTogglePanel = "/discard";
    private const string CommandToggleRun   = "/discardrun";

    private static string ConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncherCN", "pluginConfigs", "OmniAutoDiscardJunk.json");

    private Config config = new();
    private bool   isRunning;
    private bool   windowOpen;
    private string searchInput = string.Empty;
    private string lastAction  = string.Empty;

    private readonly Queue<System.Action> discardQueue = new();
    private long nextScanAt;

    private ICommandManager? commandManager;
    private readonly HashSet<string> registeredCommands = new();

    public AutoDiscardJunk()
    {
        LoadOwnConfig();
    }

    protected override void OnEnable()
    {
        commandManager = DalamudServices.CommandManager ?? GetService<ICommandManager>("Dalamud.Game.Command.CommandManager");
        RegisterCommands();

        if (DalamudServices.PluginInterface != null)
            DalamudServices.PluginInterface.UiBuilder.Draw += DrawPanel;

        if (DalamudServices.Framework != null)
            DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        isRunning = false;
        discardQueue.Clear();

        if (DalamudServices.Framework != null)
            DalamudServices.Framework.Update -= OnFrameworkUpdate;

        if (DalamudServices.PluginInterface != null)
            DalamudServices.PluginInterface.UiBuilder.Draw -= DrawPanel;

        UnregisterCommands();
        commandManager = null;
    }

    protected override void OnDispose()
    {
        isRunning = false;
        discardQueue.Clear();
    }

    private void LoadOwnConfig()
    {
        try
        {
            var path = ConfigFilePath;
            if (File.Exists(path))
            {
                var json   = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Config>(json);
                if (loaded != null) config = loaded;
            }
        }
        catch {   }
    }

    private void SaveOwnConfig()
    {
        try
        {
            var path = ConfigFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            }));
        }
        catch {   }
    }

    private void RegisterCommands()
    {
        if (commandManager == null) return;
        AddCommand(CommandTogglePanel, new CommandInfo((command, arguments) => TogglePanel()));
        AddCommand(CommandToggleRun,   new CommandInfo((command, arguments) => SetRunning(!isRunning)));
    }

    private void AddCommand(string cmd, CommandInfo info)
    {
        try
        {
            if (!registeredCommands.Contains(cmd) && commandManager!.AddHandler(cmd, info))
                registeredCommands.Add(cmd);
        }
        catch (Exception e)
        {
            lastAction = $"命令 {cmd} 注册异常: {e.Message}";
        }
    }

    private void UnregisterCommands()
    {
        if (commandManager == null) return;
        foreach (var cmd in registeredCommands.ToList())
        {
            try { commandManager.RemoveHandler(cmd); } catch {   }
        }
        registeredCommands.Clear();
    }

    private void TogglePanel()
    {
        windowOpen = !windowOpen;
        lastAction = windowOpen ? "已打开面板" : "已关闭面板";
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (discardQueue.Count > 0)
        {
            try { discardQueue.Dequeue()(); } catch {   }
            return;
        }

        if (!isRunning) return;

        var now = Environment.TickCount64;
        if (now < nextScanAt) return;

        var willDiscard = false;
        try { willDiscard = ScanAndDiscardOnce(); } catch {   }

        nextScanAt = now + (willDiscard ? 200 : 500);
    }

    private void SetRunning(bool value)
    {
        if (value == isRunning) return;
        isRunning = value;
        lastAction = value ? "自动丢弃已启动" : "自动丢弃已停止";

        if (!value)
        {
            discardQueue.Clear();
            return;
        }

        nextScanAt = Environment.TickCount64 + 200;
    }

    private bool ScanAndDiscardOnce()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        foreach (var type in PlayerBagTypes)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || container->Items == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null) continue;
                if (item->Quantity <= 0) continue;

                var (_, _, fullID) = ResolveItem(item);
                if (!config.Blacklist.Contains(fullID)) continue;

                var capturedItem = item;
                var capturedType = item->Container;
                var capturedSlot = item->Slot;
                var capturedAddonId = AgentInventoryContext.Instance()->OwnerAddonId;

                discardQueue.Enqueue(() => AgentInventoryContext.Instance()
                    ->DiscardItem(capturedItem, capturedType, capturedSlot, capturedAddonId));
                discardQueue.Enqueue(() => AddonSelectYesnoEvent.ClickYes());

                lastAction = $"正在丢弃: {GetDisplayName(fullID)}";
                return true;
            }
        }

        return false;
    }

    private void DrawPanel()
    {
        if (!windowOpen) return;

        IDisposable? fontHandle = null;
        try { fontHandle = OmniFonts.GetUIFont().Push(); } catch {   }

        try
        {
            ImGui.SetNextWindowSize(OmniTheme.Scale(new Vector2(760f, 480f)), ImGuiCond.FirstUseEver);

            using var theme = new ComicStyleScope();

            if (!ImGui.Begin("自动丢垃圾###OmniAutoDiscardJunk", ref windowOpen))
            {
                ImGui.End();
                return;
            }

            try
            {
                DrawControlBar();

                ImGui.Spacing();
                DrawColumns();

                if (!string.IsNullOrEmpty(lastAction))
                {
                    ImGui.Spacing();
                    ImGui.TextDisabled(lastAction);
                }
            }
            finally
            {
                ImGui.End();
            }
        }
        catch {   }
        finally
        {
            fontHandle?.Dispose();
        }
    }

    public override bool HasSettings => true;

    public override bool DrawSettings()
    {
        var changed = false;

        ImGui.TextWrapped("宏命令：/discard 开关面板，/discardrun 开关自动丢弃。");

        ImGui.Spacing();

        if (ImGui.Button(windowOpen ? "关闭独立面板" : "打开独立面板"))
        {
            TogglePanel();
        }

        return changed;
    }

    private void DrawControlBar()
    {
        var state = isRunning;
        if (OmniControls.Checkbox("###OmniDiscardToggle", ref state))
            SetRunning(state);

        ImGui.SameLine();
        if (isRunning)
            ImGui.TextColored(WhitelistColor, "运行中，黑名单物品将被自动丢弃");
        else
            ImGui.TextDisabled("已停止");

        ImGui.SameLine(ImGui.GetContentRegionAvail().X - OmniTheme.Scale(110f));
        if (ImGui.Button(isRunning ? "停止" : "启动", OmniTheme.Scale(new Vector2(100f, 0))))
            SetRunning(!isRunning);
    }

    private void DrawColumns()
    {
        var avail = ImGui.GetContentRegionAvail();
        var listHeight = ImGui.GetFrameHeightWithSpacing() * 12f;
        var columnSize = new Vector2((avail.X - 16f) / 3f, listHeight + 40f);

        DrawBlacklist(columnSize);
        ImGui.SameLine();
        DrawInventory(columnSize);
        ImGui.SameLine();
        DrawWhitelist(columnSize);
    }

    private void DrawBlacklist(Vector2 size)
    {
        if (!ImGui.BeginChild("###OmniDiscardBlacklist", size, true)) return;

        try
        {
            var headerWidth = ImGui.GetContentRegionAvail().X;
            ImGui.TextColored(BlacklistColor, "黑名单 (自动丢弃)");

            ImGui.SameLine(MathF.Max(0f, headerWidth - 64f));
            if (ImGui.Button("清空", new Vector2(60f, 0)) &&
                ImGui.IsKeyDown(ImGuiKey.LeftCtrl) &&
                config.Blacklist.Count > 0)
            {
                config.Blacklist.Clear();
                SaveOwnConfig();
                lastAction = "已清空黑名单";
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("按住 Ctrl 点击以清空");

            ImGui.Separator();

            if (config.Blacklist.Count == 0)
            {
                ImGui.TextDisabled("点击背包物品左侧的 <- 按钮加入黑名单");
                return;
            }

            foreach (var id in config.Blacklist.OrderBy(GetItemName).ToList())
            {
                ImGui.PushID((int)id);

                var rowWidth = ImGui.GetContentRegionAvail().X;
                var isHQ     = IsHQ(id);

                if (isHQ) ImGui.TextColored(HQColor, GetDisplayName(id));
                else      ImGui.TextUnformatted(GetDisplayName(id));

                ImGui.SameLine(MathF.Max(0f, rowWidth - 52f));
                if (ImGui.Button("删除", new Vector2(48f, 0)))
                {
                    config.Blacklist.Remove(id);
                    SaveOwnConfig();
                }

                ImGui.PopID();
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawInventory(Vector2 size)
    {
        if (!ImGui.BeginChild("###OmniDiscardInventory", size, true)) return;

        try
        {
            var headerWidth = ImGui.GetContentRegionAvail().X;
            ImGui.TextColored(InventoryColor, "背包物品");

            ImGui.SameLine(MathF.Max(0f, headerWidth - 144f));
            ImGui.SetNextItemWidth(134f);
            ImGui.InputTextWithHint("###OmniDiscardSearch", "搜索...", ref searchInput, 64);

            ImGui.Separator();

            var items = GetInventoryDisplay();
            if (items.Count == 0)
            {
                ImGui.TextDisabled("背包中没有可显示的物品");
                return;
            }

            foreach (var id in items.Keys.OrderBy(GetItemName, StringComparer.CurrentCulture))
            {
                var displayName = GetDisplayName(id);

                if (!string.IsNullOrWhiteSpace(searchInput) &&
                    !displayName.Contains(searchInput, StringComparison.OrdinalIgnoreCase))
                    continue;

                ImGui.PushID((int)id);

                var rowWidth = ImGui.GetContentRegionAvail().X;
                var isHQ     = IsHQ(id);

                if (ImGui.Button("<-", new Vector2(34f, 0)))
                {
                    config.Blacklist.Add(id);
                    config.Whitelist.Remove(id);
                    SaveOwnConfig();
                    lastAction = $"已加入黑名单: {displayName}";
                }

                ImGui.SameLine();

                if (isHQ) ImGui.TextColored(HQColor, $"{displayName} x{items[id]}");
                else      ImGui.TextUnformatted($"{displayName} x{items[id]}");

                ImGui.SameLine(MathF.Max(38f, rowWidth - 38f));
                if (ImGui.Button("->", new Vector2(34f, 0)))
                {
                    config.Whitelist.Add(id);
                    config.Blacklist.Remove(id);
                    SaveOwnConfig();
                    lastAction = $"已加入白名单: {displayName}";
                }

                ImGui.PopID();
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawWhitelist(Vector2 size)
    {
        if (!ImGui.BeginChild("###OmniDiscardWhitelist", size, true)) return;

        try
        {
            var headerWidth = ImGui.GetContentRegionAvail().X;
            ImGui.TextColored(WhitelistColor, "白名单 (受保护)");

            ImGui.SameLine(MathF.Max(0f, headerWidth - 64f));
            if (ImGui.Button("清空", new Vector2(60f, 0)) &&
                ImGui.IsKeyDown(ImGuiKey.LeftCtrl) &&
                config.Whitelist.Count > 0)
            {
                config.Whitelist.Clear();
                SaveOwnConfig();
                lastAction = "已清空白名单";
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("按住 Ctrl 点击以清空");

            ImGui.Separator();

            if (config.Whitelist.Count == 0)
            {
                ImGui.TextDisabled("点击背包物品右侧的 -> 按钮加入白名单");
                return;
            }

            foreach (var id in config.Whitelist.OrderBy(GetItemName).ToList())
            {
                ImGui.PushID((int)id);

                var rowWidth = ImGui.GetContentRegionAvail().X;
                var isHQ     = IsHQ(id);

                if (isHQ) ImGui.TextColored(HQColor, GetDisplayName(id));
                else      ImGui.TextUnformatted(GetDisplayName(id));

                ImGui.SameLine(MathF.Max(0f, rowWidth - 52f));
                if (ImGui.Button("删除", new Vector2(48f, 0)))
                {
                    config.Whitelist.Remove(id);
                    SaveOwnConfig();
                }

                ImGui.PopID();
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private Dictionary<uint, uint> GetInventoryDisplay()
    {
        var result = new Dictionary<uint, uint>();

        var manager = InventoryManager.Instance();
        if (manager == null) return result;

        foreach (var type in PlayerBagTypes)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || container->Items == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null) continue;
                if (item->Quantity <= 0) continue;

                var (_, _, fullID) = ResolveItem(item);
                if (fullID == 0) continue;
                if (config.Blacklist.Contains(fullID) ||
                    config.Whitelist.Contains(fullID)) continue;

                result[fullID] = result.TryGetValue(fullID, out var count)
                    ? count + (uint)item->Quantity
                    : (uint)item->Quantity;
            }
        }

        return result;
    }

    private static (uint BaseID, bool IsHQ, uint FullID) ResolveItem(InventoryItem* item)
    {
        var raw    = item->ItemId;
        var isHQ   = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        var baseID = raw >= 500_000u ? raw - 500_000u : raw;
        return (baseID, isHQ, baseID + (isHQ ? 500_000u : 0));
    }

    private static bool IsHQ(uint itemID) => itemID >= 500_000u;

    private static uint ToBaseID(uint itemID) => IsHQ(itemID) ? itemID - 500_000u : itemID;

    private string GetItemName(uint itemID)
    {
        var baseID = ToBaseID(itemID);
        try
        {
            var sheet = DalamudServices.DataManager.GetExcelSheet<Item>();
            return sheet.GetRow(baseID).Name.ToString();
        }
        catch
        {
            return $"未知物品 ({baseID})";
        }
    }

    private string GetDisplayName(uint itemID) =>
        GetItemName(itemID) + (IsHQ(itemID) ? " (HQ)" : string.Empty);

    private static T? GetService<T>(string implTypeFullName) where T : class
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Dalamud");
            var impl       = asm?.GetType(implTypeFullName, false);
            var serviceDef = asm?.GetType("Dalamud.Service`1", false);
            if (impl == null || serviceDef == null) return null;
            var get = serviceDef.MakeGenericType(impl)
                .GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, Type.EmptyTypes, null);
            return (T?)get?.Invoke(null, null);
        }
        catch { return null; }
    }

    [Serializable]
    private sealed class Config
    {
        public HashSet<uint> Blacklist { get; set; } = new();
        public HashSet<uint> Whitelist { get; set; } = new();
    }
}

