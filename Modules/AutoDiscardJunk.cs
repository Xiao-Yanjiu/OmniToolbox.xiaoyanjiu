// ============================================================================
// AutoDiscardJunk.Omni.cs —— Omni 妙妙屋 本地模块（TreeHouse）「自动丢垃圾」 v1
//
//   - 三栏界面管理背包物品：左 = 黑名单 / 中 = 背包物品 / 右 = 白名单
//   - 黑名单 + 白名单 + 背包 = 游戏内背包全部物品（三栏互不重叠）
//   - HQ 物品与普通物品分开显示、分开记录（HQ 带金色标记）
//   - 勾选「启动」后，背包中出现黑名单物品时自动丢弃（确认弹窗自动点"是"）
//
// 导入方法：
//   Omni 妙妙屋 → 本地模块 → 填入本文件绝对路径
// ⚠ 启动前请务必确认黑名单内容，被丢弃的物品无法找回！
// ============================================================================

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
        Description = "三栏管理背包物品：黑名单中的物品会在启动后自动丢弃。\n" +
                      "黑名单 + 白名单 + 背包 = 游戏内背包物品。HQ 物品与普通物品分开管理。\n" +
                      "宏命令：/discard 开关面板，/discardrun 开关自动丢弃。",
        Category    = ModuleCategory.Item,
        Author      = "小烟酒",
    };

    // ------------------------------ 常量 ------------------------------

    /// <summary>玩家随身背包 (4 个背包格)</summary>
    private static readonly InventoryType[] PlayerBagTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    // 主题令牌：运行时跟随 Omni 当前主题（线条 绿意 / 桃夭 / 鸢尾 / 石墨 等）
    private static Vector4 BlacklistColor => OmniTheme.Tokens.Error;
    private static Vector4 InventoryColor => OmniTheme.ControlAccent;
    private static Vector4 WhitelistColor => OmniTheme.Tokens.Success;
    private static Vector4 HQColor        => OmniTheme.Tokens.Warning;

    private const string CommandTogglePanel = "/discard";
    private const string CommandToggleRun   = "/discardrun";

    private static string ConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncherCN", "pluginConfigs", "OmniAutoDiscardJunk.json");

    // ------------------------------ 状态 ------------------------------

    private Config config = new();
    private bool   isRunning;
    private bool   windowOpen;
    private string searchInput = string.Empty;
    private string lastAction  = string.Empty;

    // 丢弃动作队列：每次框架 tick 执行一个（丢弃 → 下一帧点"是"），与官方节奏一致
    private readonly Queue<System.Action> discardQueue = new();
    private long nextScanAt;

    private ICommandManager? commandManager;
    private readonly HashSet<string> registeredCommands = new();

    // ------------------------------ 生命周期 ------------------------------

    public AutoDiscardJunk()
    {
        LoadOwnConfig();
    }

    protected override void OnEnable()
    {
        // 命令服务（优先走宿主静态服务，失败则反射兜底）
        commandManager = DalamudServices.CommandManager ?? GetService<ICommandManager>("Dalamud.Game.Command.CommandManager");
        RegisterCommands();

        // 独立窗口绘制（宿主插件 UiBuilder 的 Draw 事件，与内置模块同一套机制）
        if (DalamudServices.PluginInterface != null)
            DalamudServices.PluginInterface.UiBuilder.Draw += DrawPanel;

        // 丢弃循环（框架 Update，主线程）
        if (DalamudServices.Framework != null)
            DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        // 停止丢弃
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
        // 双保险：确保事件已解绑、丢弃已停止
        isRunning = false;
        discardQueue.Clear();
    }

    // ------------------------------ 配置自持久化 ------------------------------

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
        catch { /* 损坏则用默认配置 */ }
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
        catch { /* 保存失败静默 */ }
    }

    // ------------------------------ 宏命令 ------------------------------

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
            try { commandManager.RemoveHandler(cmd); } catch { /* 忽略 */ }
        }
        registeredCommands.Clear();
    }

    private void TogglePanel()
    {
        windowOpen = !windowOpen;
        lastAction = windowOpen ? "已打开面板" : "已关闭面板";
    }

    // ------------------------------ 丢弃循环 ------------------------------

    private void OnFrameworkUpdate(IFramework framework)
    {
        // 每帧执行一个丢弃动作（丢弃 → 下一帧点"是"），避免确认弹窗尚未出现
        if (discardQueue.Count > 0)
        {
            try { discardQueue.Dequeue()(); } catch { /* 忽略 */ }
            return;
        }

        if (!isRunning) return;

        var now = Environment.TickCount64;
        if (now < nextScanAt) return;

        var willDiscard = false;
        try { willDiscard = ScanAndDiscardOnce(); } catch { /* 单轮扫描异常静默跳过 */ }

        nextScanAt = now + (willDiscard ? 100 : 500);
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

        nextScanAt = Environment.TickCount64 + 200; // 启动后短延时再首扫
    }

    /// <summary>
    /// 扫描一轮随身背包，将本轮找到的全部黑名单物品堆叠一次性入队丢弃。
    /// 按完整物品 ID (含 HQ 标记) 精确匹配：普通/HQ 分别只丢对应版本。
    /// </summary>
    private bool ScanAndDiscardOnce()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        var enqueuedCount = 0;
        var lastDiscardName = string.Empty;

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

                // 捕获槽位快照，入队后逐帧执行
                var capturedItem = item;
                var capturedType = item->Container;
                var capturedSlot = item->Slot;
                var capturedAddonId = AgentInventoryContext.Instance()->OwnerAddonId;

                discardQueue.Enqueue(() => AgentInventoryContext.Instance()
                    ->DiscardItem(capturedItem, capturedType, capturedSlot, capturedAddonId));
                discardQueue.Enqueue(() => AddonSelectYesnoEvent.ClickYes());

                enqueuedCount++;
                lastDiscardName = GetDisplayName(fullID);
            }
        }

        if (enqueuedCount > 0)
            lastAction = enqueuedCount == 1
                ? $"正在丢弃: {lastDiscardName}"
                : $"正在批量丢弃 {enqueuedCount} 组, 最后: {lastDiscardName}";

        return enqueuedCount > 0;
    }

    // ------------------------------ 独立面板 UI ------------------------------

    private void DrawPanel()
    {
        if (!windowOpen) return;

        // Omni UI 字体（失败则退回默认字体，不影响功能）
        IDisposable? fontHandle = null;
        try { fontHandle = OmniFonts.GetUIFont().Push(); } catch { /* 字体不可用 */ }

        try
        {
            ImGui.SetNextWindowSize(OmniTheme.Scale(new Vector2(760f, 480f)), ImGuiCond.FirstUseEver);

            // 整窗套用 Omni 当前主题：配色/圆角/间距全部跟随（绿意/桃夭/鸢尾/石墨等）
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
        catch { /* 渲染异常静默，避免影响游戏 */ }
        finally
        {
            fontHandle?.Dispose();
        }
    }

    public override bool HasSettings => true;

    public override bool DrawSettings()
    {
        var changed = false;

        ImGui.TextUnformatted($"命令服务: {(commandManager != null ? "正常" : "失败")}   ·   状态: {(isRunning ? "运行中" : "已停止")}");
        ImGui.TextWrapped("本模块的面板是独立浮动窗口，用宏命令 /discard 打开或关闭；/discardrun 启动或停止自动丢弃。");

        ImGui.Spacing();

        if (ImGui.Button(windowOpen ? "关闭独立面板" : "打开独立面板"))
        {
            TogglePanel();
        }

        ImGui.SameLine();
        if (ImGui.Button(isRunning ? "停止自动丢弃" : "启动自动丢弃"))
        {
            SetRunning(!isRunning);
        }

        return changed;
    }

    private void DrawControlBar()
    {
        // Omni 自绘复选框，跟随主题
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

    // ------------------------------ 数据获取 ------------------------------

    /// <summary>
    /// 获取随身背包 (Inventory1-4) 中可显示的物品及总堆叠数量。
    /// 以完整物品 ID (含 HQ 标记) 为键，HQ 物品与普通物品分开显示。
    /// 已排除黑名单与白名单中的物品。
    /// </summary>
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

    // ------------------------------ 物品 ID 工具 ------------------------------

    /// <summary>
    /// 从背包槽位解析物品 ID 与 HQ 标记。
    /// 不使用 GetItemId()：当前游戏版本 HQ 物品的原始 ItemId 字段已含 +500000 偏移，
    /// GetItemId() 会再叠加一次 500000，得到翻倍 ID 导致查表失败。
    /// 改用 Flags 位判定 HQ，并防御性剥离原始 ID 中可能存在的 +500000 偏移。
    /// </summary>
    private static (uint BaseID, bool IsHQ, uint FullID) ResolveItem(InventoryItem* item)
    {
        var raw    = item->ItemId;
        var isHQ   = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        var baseID = raw >= 500_000u ? raw - 500_000u : raw;
        return (baseID, isHQ, baseID + (isHQ ? 500_000u : 0));
    }

    /// <summary>HQ 物品的完整 ID = 基础 ID + 500000</summary>
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

    // ------------------------------ 反射 ------------------------------

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

    // ------------------------------ 配置模型 ------------------------------

    [Serializable]
    private sealed class Config
    {
        // 存储完整物品 ID: 普通物品 = 游戏物品 ID, HQ 物品 = 游戏物品 ID + 500000
        public HashSet<uint> Blacklist { get; set; } = new();
        public HashSet<uint> Whitelist { get; set; } = new();
    }
}
