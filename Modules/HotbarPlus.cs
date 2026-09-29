using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Lumina.Excel;
using Lumina.Text.ReadOnly;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.Notifications;
using OmniToolbox.UI.Theme;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using RaptureHotbarModule = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;
using UIGlobals = FFXIVClientStructs.FFXIV.Client.UI.UIGlobals;
using AtkStage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage;
using AtkDragDropManager = FFXIVClientStructs.FFXIV.Component.GUI.AtkDragDropManager;
using AtkDragDropInterface = FFXIVClientStructs.FFXIV.Component.GUI.AtkDragDropInterface;
using DragDropType = FFXIVClientStructs.FFXIV.Component.GUI.DragDropType;
using ActionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager;
using ActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType;
using UseActionMode = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.UseActionMode;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaCraftAction = Lumina.Excel.Sheets.CraftAction;
using LuminaEmote = Lumina.Excel.Sheets.Emote;
using LuminaGeneralAction = Lumina.Excel.Sheets.GeneralAction;
using LuminaItem = Lumina.Excel.Sheets.Item;
using LuminaMainCommand = Lumina.Excel.Sheets.MainCommand;
using LuminaMarker = Lumina.Excel.Sheets.Marker;
using LuminaMount = Lumina.Excel.Sheets.Mount;
using LuminaCompanion = Lumina.Excel.Sheets.Companion;

namespace OmniToolbox.LocalModules;

/// <summary>
/// 自定义热键栏 Plus —— 把 Expanded Hotbars 的核心机制移植进 Omni，并新增「翻页」。
///
/// 实现要点（全部基于本机实际程序集核对过的 API）：
///  - 每个格子持有一份 <b>私有的</b> <c>RaptureHotbarModule.HotbarSlot</c> 结构副本（不在游戏热键栏数组里），
///    显示走 <c>GetSlotAppearance</c> + <c>PrepareSlotForRender</c>，执行走 <c>ExecuteSlot</c>，
///    因此图标 / 冷却 / 变灰 / 资源消耗 / 连击高亮 / 距离判定全部由游戏原样计算，无需自研判定。
///  - 界面用 ImGui 绘制（Omni 模块唯一被验证可行的自绘通道）。
///  - 原生热键栏的隐藏请在游戏「系统设置 → 界面设置 → HUD」里自行关闭，
///    本模块不去动游戏 HUD，避免与其他插件冲突。
/// </summary>
public sealed class HotbarPlus : ModuleBase
{
    // ==================================================================
    //  模块信息
    // ==================================================================

    public override ModuleInfo Info { get; } = new()
    {
        Title = "自定义热键栏 Plus",
        Description = "在屏幕上放置可自定义的技能栏：真实图标/冷却/资源/连击高亮，支持多页翻页、每格自定义键位、自定义图标与本地图片。原生热键栏可在游戏 HUD 设置里隐藏。",
        Category = ModuleCategory.Interface,
        Author = "小烟酒",
        Commands = new[]
        {
            new ModuleCommand("/omni HotbarPlus → 切换全部自定义热键栏显示", "/omni HotbarPlus"),
            new ModuleCommand("/omni HotbarPlus toggle <名称或序号> → 切换指定热键栏", "/omni HotbarPlus toggle 1"),
            new ModuleCommand("/omni HotbarPlus page <名称或序号> <页码|next|prev> → 翻页", "/omni HotbarPlus page 1 next")
        }
    };

    // ==================================================================
    //  配置（宿主自动持久化名为 *Config 的成员）
    // ==================================================================

    [Serializable]
    public sealed class HotbarPlusConfig
    {
        /// <summary>配置结构版本；升级默认样式时用 NormalizeConfig 做一次性迁移。</summary>
        public int ConfigVersion { get; set; }

        /// <summary>拖拽 / 点击 / 执行的诊断日志（排查问题时开着，日志带 [HotbarPlus] 前缀）。</summary>
        public bool DebugLog { get; set; } = true;

        /// <summary>是否允许把游戏内正在拖拽的技能 / 物品放到自定义格子上。</summary>
        public bool EnableDragDrop { get; set; } = true;

        /// <summary>所有自定义热键栏。</summary>
        public List<HotbarBarConfig> Bars { get; set; } = new();

        /// <summary>是否在格子上显示绑定的键位文字。</summary>
        public bool ShowKeybinds { get; set; } = true;

        /// <summary>是否显示鼠标悬浮说明。</summary>
        public bool ShowTooltips { get; set; } = true;

        /// <summary>全局显示开关（快捷键切换用）。</summary>
        public bool OverlayEnabled { get; set; } = true;

        /// <summary>全局隐藏 / 显示全部热键栏的按键（虚拟键码，0 = 未绑定）。</summary>
        public int ToggleAllKey { get; set; }

        /// <summary>全局开关的修饰键（1 Ctrl / 2 Alt / 4 Shift，可相加）。</summary>
        public int ToggleAllMods { get; set; }

        /// <summary>滑动条 / 拖拽时的整体吸附步长（像素），0 表示不吸附。</summary>
        public float SnapStep { get; set; } = 1f;
    }

    [Serializable]
    public sealed class HotbarBarConfig
    {
        public string Name { get; set; } = "热键栏 1";
        public bool Visible { get; set; } = true;
        public bool Locked { get; set; }

        public float PositionX { get; set; } = 400f;
        public float PositionY { get; set; } = 300f;

        /// <summary>相对主视口的比例位置，用于分辨率变化后自动归位。&lt;0 表示尚未计算。</summary>
        public float PositionRatioX { get; set; } = -1f;
        public float PositionRatioY { get; set; } = -1f;

        /// <summary>整体缩放。</summary>
        public float Scale { get; set; } = 1f;

        /// <summary>整栏背景不透明度（0 = 完全透明，只留格子底）。</summary>
        public float BackgroundOpacity { get; set; } = 0.55f;

        public int Columns { get; set; } = 12;
        public int Rows { get; set; } = 1;

        public float SlotSize { get; set; } = 44f;
        public float SlotSpacing { get; set; } = 0f;

        /// <summary>是否响应鼠标左键点击释放技能。</summary>
        public bool EnableClicking { get; set; } = true;

        /// <summary>是否显示翻页条（上一页 / 页码 / 下一页）。原生热键栏没有常驻翻页条，默认关。</summary>
        public bool ShowPageControls { get; set; } = false;

        /// <summary>总页数。</summary>
        public int PageCount { get; set; } = 3;

        /// <summary>当前页码（0 起）。</summary>
        public int CurrentPage { get; set; }

        /// <summary>翻到最后一页后再 next 是否回到第一页。</summary>
        public bool CyclePages { get; set; } = true;

        public int PrevPageKey { get; set; }
        public int PrevPageMods { get; set; }
        public int NextPageKey { get; set; }
        public int NextPageMods { get; set; }

        /// <summary>每页的格子数据。</summary>
        public List<HotbarPageConfig> Pages { get; set; } = new();

        /// <summary>该栏的全局倍率（供运行时计算）。</summary>
        public float EffectiveScale => Scale <= 0.01f ? 0.01f : Scale;
    }

    [Serializable]
    public sealed class HotbarPageConfig
    {
        /// <summary>页名（可留空），仅用于设置面板展示。</summary>
        public string Name { get; set; } = string.Empty;

        public List<HotbarSlotConfig> Slots { get; set; } = new();
    }

    [Serializable]
    public sealed class HotbarSlotConfig
    {
        /// <summary>槽位类型，取 HotbarSlotType 的数值；0 = 空。</summary>
        public int Type { get; set; }

        /// <summary>槽位 ID（技能 ID / 物品 ID / 宏序号等）。</summary>
        public uint Id { get; set; }

        /// <summary>绑定的虚拟键码，0 = 未绑定。</summary>
        public int KeyCode { get; set; }

        /// <summary>键位修饰键：1 Ctrl / 2 Alt / 4 Shift，可相加。</summary>
        public int KeyMods { get; set; }

        /// <summary>自定义图标覆盖（游戏图标 ID）；0 = 使用槽位原生图标。</summary>
        public uint CustomIconId { get; set; }

        /// <summary>本地图片路径；非空时优先于 CustomIconId。</summary>
        public string CustomImagePath { get; set; } = string.Empty;

        /// <summary>可选角标文字。</summary>
        public string Label { get; set; } = string.Empty;
    }

    private HotbarPlusConfig config = new();

    // ==================================================================
    //  运行时
    // ==================================================================

    private readonly Dictionary<long, SlotRuntime> runtimes = new();

    /// <summary>正在等待用户按键的格子键（设置面板用）。</summary>
    private string capturingKeybind = string.Empty;

    /// <summary>本地图片贴图缓存：路径 → 贴图。（后台线程会写入，必须用并发容器）</summary>
    private readonly ConcurrentDictionary<string, IDalamudTextureWrap> imageCache = new();

    private readonly ConcurrentDictionary<string, byte> imageLoading = new();
    private readonly ConcurrentDictionary<string, byte> imageFailed = new();

    /// <summary>设置面板里当前选中的热键栏。</summary>
    private int selectedBar;

    // ---- 游戏内拖拽（AtkDragDropManager 轮询）----
    /// <summary>上一帧游戏是否处于拖拽中。</summary>
    private bool ddWasDragging;

    /// <summary>最近一次捕获的拖拽 payload：DragDropType 原始值 / Int2（通常是动作或物品 ID）/ ReferenceIndex。</summary>
    private int ddLastType = -1;
    private int ddLastInt2;
    private int ddLastRef;

    /// <summary>拖拽中鼠标悬停的自定义格子（-1 = 无）。</summary>
    private int ddHoverBar = -1;
    private int ddHoverPage = -1;
    private int ddHoverSlot = -1;

    /// <summary>本帧游戏是否处于拖拽中（绘制格子时用来画高亮框）。</summary>
    private bool ddDragging;

    /// <summary>悬浮窗整体是否可见（由全局开关控制）。</summary>
    private bool overlayVisible = true;

    private bool disposed;

    /// <summary>一个格子的原生运行时数据。</summary>
    private sealed class SlotRuntime
    {
        public RaptureHotbarModule.HotbarSlot Data;
        public RaptureHotbarModule.HotbarUIIntermediate State;

        /// <summary>当前已写入 Data 的配置类型（-1 表示尚未写入）。</summary>
        public int AppliedType = -1;

        /// <summary>当前已写入 Data 的配置 ID。</summary>
        public uint AppliedId = uint.MaxValue;
    }

    // ==================================================================
    //  生命周期
    // ==================================================================

    protected override void OnEnable()
    {
        disposed = false;
        NormalizeConfig();
        overlayVisible = config.OverlayEnabled;
        DalamudServices.PluginInterface.UiBuilder.Draw += Draw;
    }

    protected override void OnDisable()
    {
        try { DalamudServices.PluginInterface.UiBuilder.Draw -= Draw; } catch { }
        ReleaseRuntime();
    }

    protected override void OnDispose()
    {
        disposed = true;
        try { DalamudServices.PluginInterface.UiBuilder.Draw -= Draw; } catch { }
        ReleaseRuntime();
    }

    private void ReleaseRuntime()
    {
        runtimes.Clear();
        capturingKeybind = string.Empty;
        foreach (var tex in imageCache.Values)
        {
            try { tex.Dispose(); } catch { }
        }
        imageCache.Clear();
        imageLoading.Clear();
        imageFailed.Clear();
    }

    // ==================================================================
    //  命令
    // ==================================================================

    public override bool TryHandleCommand(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            ToggleAll();
            return true;
        }

        var parts = arguments.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            ToggleAll();
            return true;
        }

        if (Matches(parts[0], "toggle", "切换"))
        {
            if (parts.Length >= 2)
            {
                ToggleBar(parts[1]);
            }
            else
            {
                ToggleAll();
            }
            return true;
        }

        if (Matches(parts[0], "page", "翻页"))
        {
            if (parts.Length >= 3 && FindBar(parts[1]) is { } bar)
            {
                StepPage(bar, parts[2]);
            }
            else
            {
                Notify("用法：/omni HotbarPlus page <名称或序号> <页码|next|prev>");
            }
            return true;
        }

        if (Matches(parts[0], "show", "显示", "hide", "隐藏"))
        {
            var show = Matches(parts[0], "show", "显示");
            if (parts.Length >= 2 && FindBar(parts[1]) is { } bar)
            {
                bar.Visible = show;
            }
            else
            {
                foreach (var b in config.Bars) b.Visible = show;
                overlayVisible = show;
                config.OverlayEnabled = show;
            }
            return true;
        }

        Notify("未知参数。可用：toggle / page / show / hide，或留空切换全部。");
        return true;
    }

    private static bool Matches(string value, params string[] candidates)
    {
        foreach (var c in candidates)
        {
            if (string.Equals(value, c, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void ToggleAll()
    {
        var anyVisible = config.Bars.Any(b => b.Visible);
        foreach (var b in config.Bars) b.Visible = !anyVisible;
        overlayVisible = !anyVisible;
        config.OverlayEnabled = !anyVisible;
        Notify(anyVisible ? "自定义热键栏已全部隐藏" : "自定义热键栏已全部显示");
    }

    private void ToggleBar(string key)
    {
        var bar = FindBar(key);
        if (bar == null)
        {
            Notify($"未找到热键栏「{key}」");
            return;
        }
        bar.Visible = !bar.Visible;
        Notify($"{bar.Name}：{(bar.Visible ? "显示" : "隐藏")}");
    }

    private HotbarBarConfig FindBar(string key)
    {
        foreach (var bar in config.Bars)
        {
            if (string.Equals(bar.Name, key, StringComparison.Ordinal)) return bar;
        }
        if (int.TryParse(key, out var index) && index >= 1 && index <= config.Bars.Count)
        {
            return config.Bars[index - 1];
        }
        return null;
    }

    private void StepPage(HotbarBarConfig bar, string arg)
    {
        if (Matches(arg, "next", "下一页", "+"))
        {
            ChangePage(bar, bar.CurrentPage + 1);
            return;
        }
        if (Matches(arg, "prev", "上一页", "-"))
        {
            ChangePage(bar, bar.CurrentPage - 1);
            return;
        }
        if (int.TryParse(arg, out var page))
        {
            ChangePage(bar, page - 1);
            return;
        }
        Notify("页码参数无效");
    }

    private void ChangePage(HotbarBarConfig bar, int target)
    {
        var count = Math.Max(1, bar.PageCount);
        if (bar.CyclePages)
        {
            target %= count;
            if (target < 0) target += count;
        }
        else
        {
            target = Math.Clamp(target, 0, count - 1);
        }
        bar.CurrentPage = target;
    }

    private static void Notify(string message)
    {
        try { OmniNotifier.Chat($"[自定义热键栏] {message}"); } catch { }
    }

    // ==================================================================
    //  悬浮窗绘制
    // ==================================================================

    private void Draw()
    {
        if (disposed) return;
        try
        {
            DrawCore();
        }
        catch (Exception e)
        {
            try { DalamudServices.PluginLog.Error(e, "[HotbarPlus] 绘制异常"); } catch { }
        }
    }

    private void DrawCore()
    {
        overlayVisible = config.OverlayEnabled;

        ProcessGlobalHotkey();
        CaptureDragPayload();

        if (!overlayVisible) return;
        if (!DalamudServices.PlayerState.IsLoaded) return;

        ProcessKeybinds();

        // 只在拖拽中的帧重置悬停记录（由格子绘制回填）；
        // 松手那一帧 ddDragging 已变 false，保留上一帧的悬停位置才能正确落格
        if (ddDragging)
        {
            ddHoverBar = -1;
            ddHoverPage = -1;
            ddHoverSlot = -1;
        }

        for (var i = 0; i < config.Bars.Count; i++)
        {
            var bar = config.Bars[i];
            if (!bar.Visible) continue;
            DrawBar(bar, i);
        }

        TryAcceptDrop();
    }

    private unsafe void DrawBar(HotbarBarConfig bar, int barIndex)
    {
        var page = GetPage(bar, bar.CurrentPage);
        if (page == null) return;

        var scale = bar.EffectiveScale;
        var slotSize = Math.Max(16f, bar.SlotSize) * scale;
        var spacing = Math.Max(0f, bar.SlotSpacing) * scale;
        var columns = Math.Max(1, bar.Columns);
        var rows = Math.Max(1, bar.Rows);
        var pad = 3f * scale;

        var gridW = columns * slotSize + (columns - 1) * spacing;
        var gridH = rows * slotSize + (rows - 1) * spacing;
        // 翻页区高度常驻占位（避免窗口随 hover 跳动），内容只在悬停时绘制
        var pagerH = bar.ShowPageControls && bar.PageCount > 1 ? 14f * scale : 0f;
        var windowW = gridW + pad * 2f;
        var windowH = gridH + (pagerH > 0f ? pagerH + 1f : 0f) + pad * 2f;

        ImGui.SetNextWindowPos(new Vector2(bar.PositionX, bar.PositionY), ImGuiCond.FirstUseEver);

        var flags = ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoTitleBar |
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoDocking |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (bar.Locked) flags |= ImGuiWindowFlags.NoMove;

        ImGui.SetNextWindowSize(new Vector2(windowW, windowH), ImGuiCond.Always);

        using var style = new ImGuiStyleScope();
        style.Push(ImGuiStyleVar.WindowPadding, new Vector2(pad, pad));
        style.Push(ImGuiStyleVar.WindowBorderSize, 0f);

        var bgAlpha = Math.Clamp(bar.BackgroundOpacity, 0f, 1f);
        // 原生热键栏底：纯黑半透明，无边框
        var bgColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, bgAlpha));

        var windowName = $"###OmniHotbarPlus{barIndex}";
        if (ImGui.Begin(windowName, flags))
        {
            var dl = ImGui.GetWindowDrawList();
            var origin = ImGui.GetCursorScreenPos();
            var winHovered = ImGui.IsWindowHovered();

            if (bgAlpha > 0.005f)
            {
                var panelMin = origin - new Vector2(pad, pad);
                var panelMax = origin + new Vector2(gridW, gridH) + new Vector2(pad, pad + (pagerH > 0f ? pagerH + 1f : 0f));
                dl.AddRectFilled(panelMin, panelMax, bgColor, 3f * scale);
            }

            // 滚轮翻页（原生热键栏同款交互：悬停滚动即翻页）
            if (winHovered && bar.PageCount > 1)
            {
                var wheel = ImGui.GetIO().MouseWheel;
                if (wheel != 0) ChangePage(bar, bar.CurrentPage + (wheel < 0 ? 1 : -1));
            }

            for (var row = 0; row < rows; row++)
            {
                for (var col = 0; col < columns; col++)
                {
                    var slotIndex = row * columns + col;
                    if (slotIndex >= page.Slots.Count) break;

                    var cfg = page.Slots[slotIndex];
                    var pos = origin + new Vector2(col * (slotSize + spacing), row * (slotSize + spacing));

                    ImGui.SetCursorScreenPos(pos);
                    ImGui.PushID((IntPtr)slotIndex);
                    ImGui.InvisibleButton($"##hpSlot{barIndex}_{slotIndex}", new Vector2(slotSize, slotSize));
                    var hovered = ImGui.IsItemHovered();
                    // 原生手感：按下瞬间就执行。InvisibleButton 的返回值是“按下+抬起”完整点击，
                    // 用户按住稍一移动就丢 —— 观感就是“点了没反应”。
                    var pressed = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
                    ImGui.PopID();

                    if (pressed && bar.EnableClicking && cfg.Type > 0)
                    {
                        ExecuteSlotRuntime(barIndex, bar.CurrentPage, slotIndex);
                    }

                    if (ddDragging && hovered)
                    {
                        ddHoverBar = barIndex;
                        ddHoverPage = bar.CurrentPage;
                        ddHoverSlot = slotIndex;
                    }

                    DrawSlotVisual(dl, bar, barIndex, bar.CurrentPage, slotIndex, cfg, pos, slotSize, hovered);

                    if (hovered && !ddDragging)
                    {
                        if (config.ShowTooltips) DrawSlotTooltip(cfg, barIndex, bar.CurrentPage, slotIndex);
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    }
                }
            }

            // ---- 翻页条（悬停本栏时才出现，平时完全隐形，观感与原生一致） ----
            if (pagerH > 0f && winHovered)
            {
                ImGui.SetCursorScreenPos(origin + new Vector2(0f, gridH + 1f));
                DrawPager(dl, bar, barIndex, origin, gridW, gridH, pagerH, slotSize);
            }

            ClampWindow(bar, windowW, windowH);
        }

        ImGui.End();
    }

    private void DrawPager(ImDrawListPtr dl, HotbarBarConfig bar, int barIndex, Vector2 origin,
        float gridW, float gridH, float pagerH, float slotSize)
    {
        var page = GetPage(bar, bar.CurrentPage);
        var pageName = page != null && !string.IsNullOrEmpty(page.Name) ? page.Name : string.Empty;
        var label = pageName.Length > 0
            ? $"{bar.CurrentPage + 1}/{bar.PageCount} {pageName}"
            : $"{bar.CurrentPage + 1}/{bar.PageCount}";
        var labelSize = ImGui.CalcTextSize(label);

        var btnW = 14f * bar.EffectiveScale;
        var btnH = pagerH;
        var gap = 4f;
        var totalW = btnW * 2f + gap * 2f + labelSize.X;
        var startX = origin.X + MathF.Max(0f, (gridW - totalW) * 0.5f);
        var pagerY = origin.Y + gridH + 1f;

        // 上一页
        ImGui.SetCursorScreenPos(new Vector2(startX, pagerY));
        ImGui.PushID((IntPtr)(barIndex * 1000 + 900));
        var prevClicked = ImGui.InvisibleButton("##hpPrev", new Vector2(btnW, btnH));
        var prevHovered = ImGui.IsItemHovered();
        ImGui.PopID();
        DrawArrow(dl, new Vector2(startX, pagerY), new Vector2(btnW, btnH), false, prevHovered);

        // 页码
        var labelPos = new Vector2(startX + btnW + gap, pagerY);
        dl.AddText(new Vector2(labelPos.X, labelPos.Y + (pagerH - labelSize.Y) * 0.5f), 0xB4E8E8E8u, label);

        // 下一页
        var nextX = labelPos.X + labelSize.X + gap;
        ImGui.SetCursorScreenPos(new Vector2(nextX, pagerY));
        ImGui.PushID((IntPtr)(barIndex * 1000 + 901));
        var nextClicked = ImGui.InvisibleButton("##hpNext", new Vector2(btnW, btnH));
        var nextHovered = ImGui.IsItemHovered();
        ImGui.PopID();
        DrawArrow(dl, new Vector2(nextX, pagerY), new Vector2(btnW, btnH), true, nextHovered);

        if (prevClicked) ChangePage(bar, bar.CurrentPage - 1);
        if (nextClicked) ChangePage(bar, bar.CurrentPage + 1);
    }

    private static void DrawArrow(ImDrawListPtr dl, Vector2 pos, Vector2 size, bool right, bool hovered)
    {
        if (hovered) dl.AddRectFilled(pos, pos + size, 0x28FFFFFFu);

        var c = pos + size * 0.5f;
        var h = size.Y * 0.26f;
        var w = size.X * 0.16f;
        var col = hovered ? 0xFFFFFFFFu : 0x96FFFFFFu;

        if (right)
        {
            dl.PathLineTo(new Vector2(c.X - w, c.Y - h));
            dl.PathLineTo(new Vector2(c.X + w, c.Y));
            dl.PathLineTo(new Vector2(c.X - w, c.Y + h));
        }
        else
        {
            dl.PathLineTo(new Vector2(c.X + w, c.Y - h));
            dl.PathLineTo(new Vector2(c.X - w, c.Y));
            dl.PathLineTo(new Vector2(c.X + w, c.Y + h));
        }
        dl.PathFillConvex(col);
    }

    /// <summary>绘制一个格子：图标 + 冷却 / 充能 / 消耗 / 变灰 / 越界 / 连击高亮 + 键位角标。</summary>
    private unsafe void DrawSlotVisual(ImDrawListPtr dl, HotbarBarConfig bar, int barIndex, int pageIndex,
        int slotIndex, HotbarSlotConfig cfg, Vector2 p0, float size, bool hovered)
    {
        var p1 = p0 + new Vector2(size);

        // 原生热键栏的格子本身几乎透明：平时不画底，悬停才点亮一个白罩
        if (hovered) dl.AddRectFilled(p0, p1, 0x34FFFFFFu);

        var hasSlot = cfg.Type > 0;
        var iconId = 0u;
        var greyed = false;
        var outOfRange = false;
        var ants = false;
        var darkFraction = 0f;
        var cdSeconds = 0u;
        var chargeCount = 0u;
        var chargeMode = false;
        var costValue = 0u;
        var costType = 0;
        var costMode = 0;

        if (hasSlot)
        {
            var rt = GetRuntime(barIndex, pageIndex, slotIndex);
            if (rt != null)
            {
                EnsureSlotContent(rt, cfg);
                RefreshSlot(rt);

                var isMacro = rt.Data.CommandType == RaptureHotbarModule.HotbarSlotType.Macro;

                fixed (RaptureHotbarModule.HotbarUIIntermediate* st = &rt.State)
                {
                    iconId = st->IconId;
                    cdSeconds = st->CooldownSeconds;
                    chargeCount = st->CurrentCharges;
                    chargeMode = st->CooldownMode == 3;
                    costValue = st->CostValue;
                    costType = st->CostType;
                    costMode = st->CostDisplayMode;

                    var available = st->ActionAvailable1 && st->ActionAvailable2;
                    greyed = !available && !isMacro;
                    outOfRange = !st->ActionTargetSatisfied;
                    ants = st->DrawAnts;

                    if (chargeMode)
                    {
                        darkFraction = (100f - Math.Clamp(st->ChargePercent, 0u, 100u)) / 100f;
                    }
                    else if (st->CooldownPercent > 0)
                    {
                        darkFraction = Math.Clamp(st->CooldownPercent, 0u, 100u) / 100f;
                    }
                }
            }
        }

        // ---- 图标（原生图标四周留 ~5% 内边距，不顶满格子） ----
        if (cfg.CustomIconId > 0) iconId = cfg.CustomIconId;
        var texture = ResolveSlotTexture(cfg, iconId);
        if (IsTexValid(texture))
        {
            var inset = MathF.Max(1f, size * 0.05f);
            dl.AddImage(texture!.Handle, p0 + new Vector2(inset), p1 - new Vector2(inset));
        }
        else if (hasSlot)
        {
            dl.AddRectFilled(p0, p1, 0x33000000u);
        }

        // ---- 越界红斜线 ----
        if (hasSlot && outOfRange)
        {
            dl.AddLine(new Vector2(p0.X + size * 0.18f, p1.Y - size * 0.18f),
                       new Vector2(p1.X - size * 0.18f, p0.Y + size * 0.18f), 0xCC2020E0u, MathF.Max(1.5f, size * 0.06f));
        }

        // ---- 冷却 / 充能遮罩 ----
        if (darkFraction > 0.001f)
        {
            DrawCooldownWedge(dl, p0 + new Vector2(size * 0.5f), size * 0.5f - 1f, darkFraction, 0x99000000u);
        }

        // ---- 变灰 ----
        if (greyed)
        {
            dl.AddRectFilled(p0, p1, 0x90000000);
        }

        // ---- 连击高亮（流动虚线边框） ----
        if (ants)
        {
            DrawMarchingAnts(dl, p0, p1, size);
        }

        // ---- 冷却秒数 ----
        if (cdSeconds > 0 && size >= 30f)
        {
            var text = cdSeconds >= 60u
                ? $"{cdSeconds / 60u}:{cdSeconds % 60u:00}"
                : cdSeconds.ToString(CultureInfo.InvariantCulture);
            DrawCenteredText(dl, p0, new Vector2(size), text, 0xFFFFFFFFu, size * 0.40f);
        }

        // ---- 消耗数字 ----
        if (hasSlot && costValue > 0)
        {
            string costText = null;
            if (costMode == 2) costText = costValue.ToString(CultureInfo.InvariantCulture);
            else if (costMode == 4) costText = "x" + costValue.ToString(CultureInfo.InvariantCulture);
            if (costText != null && size >= 28f)
            {
                var col = CostColor(costType);
                var sz = ImGui.CalcTextSize(costText);
                dl.AddText(new Vector2(p0.X + 2f, p1.Y - sz.Y - 1f), col, costText);
            }
        }

        // ---- 充能数 ----
        if (hasSlot && chargeMode && chargeCount > 0 && size >= 30f)
        {
            var text = chargeCount.ToString(CultureInfo.InvariantCulture);
            var sz = ImGui.CalcTextSize(text);
            dl.AddText(new Vector2(p1.X - sz.X - 2f, p1.Y - sz.Y - 1f), 0xFF60E0FFu, text);
        }

        // ---- 键位角标（原生样式：左上角白字 + 黑描边） ----
        if (config.ShowKeybinds && cfg.KeyCode != 0 && size >= 26f)
        {
            var text = FormatKeybind(cfg.KeyCode, cfg.KeyMods);
            var pos = new Vector2(p0.X + 2f, p0.Y + 1f);
            dl.AddText(pos + new Vector2(1f, 1f), 0xFF000000u, text);
            dl.AddText(pos, 0xFFFFFFFFu, text);
        }

        // ---- 拖拽悬停高亮（原生放置框风格） ----
        if (ddDragging && hovered)
        {
            dl.AddRect(p0 + new Vector2(1f), p1 - new Vector2(1f), 0xFF00D7FFu, 0f, 2f);
        }

        // ---- 角标文字 ----
        if (!string.IsNullOrEmpty(cfg.Label) && size >= 30f)
        {
            DrawCenteredText(dl, p0, new Vector2(size), cfg.Label, 0xFFFFD060u, size * 0.30f);
        }

        // ---- 空槽位提示：悬停时淡淡画个内框，提示这里可以拖入 ----
        if (!hasSlot && hovered)
        {
            dl.AddRect(p0 + new Vector2(2f), p1 - new Vector2(2f), 0x30AAAAAAu, 0f, 1f);
        }
    }

    private static uint CostColor(int costType)
    {
        switch (costType)
        {
            case 1: return 0xFF50E050u; // HP 绿
            case 2: return 0xFFC090FFu; // MP 浅紫
            case 3: return 0xFF3090FFu; // TP 橙
            case 4: return 0xFFB070E0u; // CP
            case 5: return 0xFF40E0E0u; // GP
            case 6: return 0xFFFFB040u; // 职业量谱
            default: return 0xFFE0E0E0u;
        }
    }

    /// <summary>按「已消耗比例」画一个从正上方顺时针覆盖的扇形遮罩。</summary>
    private static void DrawCooldownWedge(ImDrawListPtr dl, Vector2 center, float radius, float fraction, uint color)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        if (fraction <= 0.001f || radius <= 0.5f) return;

        var total = fraction * MathF.PI * 2f;
        // 缺口随时间顺时针扩大：暗区从 -90° + (2π - total) 一直画到 -90° + 2π
        var darkStart = -MathF.PI / 2f + (MathF.PI * 2f - total);
        var segments = Math.Max(1, (int)MathF.Ceiling(total / (MathF.PI / 16f)));
        if (segments > 48) segments = 48;

        for (var i = 0; i < segments; i++)
        {
            var a0 = darkStart + total * i / segments;
            var a1 = darkStart + total * (i + 1) / segments + 0.006f;
            dl.PathLineTo(center);
            dl.PathLineTo(center + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius);
            dl.PathLineTo(center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius);
            dl.PathFillConvex(color);
        }
    }

    private static void DrawMarchingAnts(ImDrawListPtr dl, Vector2 p0, Vector2 p1, float size)
    {
        var perimeter = (p1.X - p0.X) * 2f + (p1.Y - p0.Y) * 2f;
        if (perimeter <= 1f) return;

        var dash = MathF.Max(4f, size * 0.18f);
        var phase = (float)(ImGui.GetTime() * 40.0) % (dash * 2f);
        var offset = -phase;

        for (var d = offset; d < perimeter; d += dash * 2f)
        {
            var a = d;
            var b = d + dash;
            if (b <= 0f) continue;
            if (a < 0f) a = 0f;
            if (a >= perimeter) break;
            if (b > perimeter) b = perimeter;
            DrawPerimeterSegment(dl, p0, p1, a, b, 0xFFFFFF60u, MathF.Max(1.5f, size * 0.05f));
        }
    }

    private static void DrawPerimeterSegment(ImDrawListPtr dl, Vector2 p0, Vector2 p1, float a, float b, uint col, float thickness)
    {
        var w = p1.X - p0.X;
        var h = p1.Y - p0.Y;
        var perimeter = (w + h) * 2f;

        var start = PointOnPerimeter(p0, w, h, perimeter, a);
        var end = PointOnPerimeter(p0, w, h, perimeter, b);
        dl.AddLine(start, end, col, thickness);
    }

    private static Vector2 PointOnPerimeter(Vector2 p0, float w, float h, float perimeter, float distance)
    {
        if (distance <= 0f) return p0;
        if (distance < w) return new Vector2(p0.X + distance, p0.Y);
        distance -= w;
        if (distance < h) return new Vector2(p0.X + w, p0.Y + distance);
        distance -= h;
        if (distance < w) return new Vector2(p0.X + w - distance, p0.Y + h);
        distance -= w;
        if (distance < h) return new Vector2(p0.X, p0.Y + h - distance);
        return p0;
    }

    private static void DrawCenteredText(ImDrawListPtr dl, Vector2 p0, Vector2 size, string text, uint col, float fontSize)
    {
        if (string.IsNullOrEmpty(text)) return;
        var measured = ImGui.CalcTextSize(text);
        if (measured.X <= 0f) return;
        var pos = p0 + (size - measured) * 0.5f;

        // 数字压在图标上，按字号加一圈深色描边提高可读性
        var outline = MathF.Max(1f, fontSize * 0.08f);
        dl.AddText(pos + new Vector2(outline, outline), 0xFF000000u, text);
        dl.AddText(pos, col, text);
    }

    private void DrawSlotTooltip(HotbarSlotConfig cfg, int barIndex, int pageIndex, int slotIndex)
    {
        if (cfg.Type <= 0)
        {
            if (cfg.KeyCode != 0)
            {
                ImGui.SetTooltip("空槽位（已绑定键位：" + FormatKeybind(cfg.KeyCode, cfg.KeyMods) + "）");
            }
            return;
        }

        var lines = new List<string>();
        var rt = GetRuntime(barIndex, pageIndex, slotIndex);
        var gameName = string.Empty;
        if (rt != null)
        {
            gameName = NativeSlotName(rt);
        }

        if (!string.IsNullOrEmpty(cfg.Label))
        {
            lines.Add(cfg.Label);
        }

        if (!string.IsNullOrEmpty(gameName))
        {
            lines.Add(gameName);
        }
        else
        {
            var desc = DescribeSlot(cfg);
            if (!string.IsNullOrEmpty(desc)) lines.Add(desc);
        }

        if (cfg.KeyCode != 0)
        {
            lines.Add("键位：" + FormatKeybind(cfg.KeyCode, cfg.KeyMods));
        }

        if (lines.Count == 0) return;
        ImGui.SetTooltip(string.Join("\n", lines));
    }

    private void ClampWindow(HotbarBarConfig bar, float windowW, float windowH)
    {
        var viewport = ImGui.GetMainViewport();
        var vpSize = viewport.WorkSize;
        var vpPos = viewport.WorkPos;
        if (vpSize.X < 8f || vpSize.Y < 8f) return;

        if (bar.PositionRatioX < 0f || bar.PositionRatioY < 0f)
        {
            bar.PositionRatioX = (bar.PositionX - vpPos.X) / vpSize.X;
            bar.PositionRatioY = (bar.PositionY - vpPos.Y) / vpSize.Y;
        }

        var current = ImGui.GetWindowPos();
        var dragging = !bar.Locked && ImGui.IsWindowFocused() && ImGui.IsMouseDown(ImGuiMouseButton.Left);
        if (dragging)
        {
            bar.PositionX = current.X;
            bar.PositionY = current.Y;
            bar.PositionRatioX = (current.X - vpPos.X) / vpSize.X;
            bar.PositionRatioY = (current.Y - vpPos.Y) / vpSize.Y;
            return;
        }

        var maxX = MathF.Max(0f, vpSize.X - windowW);
        var maxY = MathF.Max(0f, vpSize.Y - windowH);
        var target = new Vector2(
            Math.Clamp(vpPos.X + bar.PositionRatioX * vpSize.X, vpPos.X, vpPos.X + maxX),
            Math.Clamp(vpPos.Y + bar.PositionRatioY * vpSize.Y, vpPos.Y, vpPos.Y + maxY));

        if (Vector2.DistanceSquared(current, target) > 0.25f)
        {
            bar.PositionX = target.X;
            bar.PositionY = target.Y;
            ImGui.SetWindowPos(target);
        }
    }

    // ==================================================================
    //  原生热键栏互操作
    // ==================================================================

    private static long RuntimeKey(int barIndex, int pageIndex, int slotIndex)
    {
        return ((long)barIndex << 40) ^ ((long)pageIndex << 20) ^ (long)slotIndex;
    }

    private SlotRuntime GetRuntime(int barIndex, int pageIndex, int slotIndex)
    {
        var key = RuntimeKey(barIndex, pageIndex, slotIndex);
        if (runtimes.TryGetValue(key, out var rt)) return rt;
        rt = new SlotRuntime();
        runtimes[key] = rt;
        return rt;
    }

    /// <summary>
    /// 把配置写入私有 HotbarSlot 副本（内容变化时才写）。
    /// 注意：<c>HotbarUIIntermediate.Ctor()</c> 的真实签名是 <c>HotbarUIIntermediate* Ctor()</c>（返回指针），
    /// 因此本方法必须是 <c>unsafe</c>，否则编译期报 CS0214。
    /// </summary>
    private static unsafe void EnsureSlotContent(SlotRuntime rt, HotbarSlotConfig cfg)
    {
        if (rt.AppliedType == cfg.Type && rt.AppliedId == cfg.Id) return;
        rt.AppliedType = cfg.Type;
        rt.AppliedId = cfg.Id;

        if (cfg.Type <= 0)
        {
            rt.Data.Set(RaptureHotbarModule.HotbarSlotType.Empty, 0);
        }
        else
        {
            rt.Data.Set((RaptureHotbarModule.HotbarSlotType)cfg.Type, cfg.Id);
        }

        try { rt.State.Ctor(); } catch { }
    }

    /// <summary>让游戏把 Apparent 数据与渲染中间态算好（图标 / 冷却 / 可用性都在里面）。</summary>
    private static unsafe void RefreshSlot(SlotRuntime rt)
    {
        var module = RaptureHotbarModule.Instance();
        if (module == null) return;

        fixed (RaptureHotbarModule.HotbarSlot* pData = &rt.Data)
        fixed (RaptureHotbarModule.HotbarUIIntermediate* pState = &rt.State)
        {
            RaptureHotbarModule.HotbarSlotType outType;
            uint outActionId;
            ushort unused;
            RaptureHotbarModule.GetSlotAppearance(&outType, &outActionId, &unused, module, pData);
            pData->ApparentActionId = outActionId;
            pData->ApparentSlotType = outType;
            module->PrepareSlotForRender(pData, pState);
        }
    }

    private unsafe void ExecuteSlotRuntime(int barIndex, int pageIndex, int slotIndex)
    {
        var rt = GetRuntime(barIndex, pageIndex, slotIndex);
        var module = RaptureHotbarModule.Instance();
        if (module == null || rt == null) return;
        byte ret;
        fixed (RaptureHotbarModule.HotbarSlot* pData = &rt.Data)
        {
            ret = module->ExecuteSlot(pData);
        }

        if (ret == 0)
        {
            // ExecuteSlot 没吃下去（返回 0）→ 对动作类槽位直接走 ActionManager 直发，保证“点了一定有反应”
            if (config.DebugLog)
            {
                try
                {
                    DalamudServices.PluginLog.Debug(
                        $"[HotbarPlus] ExecuteSlot 返回 0（{DescribeSlotRaw((int)rt.Data.CommandType, rt.Data.CommandId)}），改用 ActionManager 直发");
                }
                catch { }
            }
            TryFallbackAction((int)rt.Data.CommandType, rt.Data.CommandId);
        }
        else if (config.DebugLog)
        {
            try
            {
                DalamudServices.PluginLog.Debug(
                    $"[HotbarPlus] ExecuteSlot 已执行（ret={ret}）：{DescribeSlotRaw((int)rt.Data.CommandType, rt.Data.CommandId)}");
            }
            catch { }
        }
    }

    /// <summary>ExecuteSlot 失败时的直发兜底（只覆盖动作类槽位；HotbarSlotType → ActionType 数值不同，须显式映射）。</summary>
    private static unsafe void TryFallbackAction(int slotType, uint id)
    {
        if (id == 0) return;
        var at = slotType switch
        {
            1 => ActionType.Action,
            9 => ActionType.CraftAction,
            10 => ActionType.GeneralAction,
            11 => ActionType.BuddyAction,
            12 => ActionType.MainCommand,
            13 => ActionType.Companion,
            16 => ActionType.PetAction,
            17 => ActionType.Mount,
            _ => ActionType.None,
        };
        if (at == ActionType.None) return;
        try
        {
            var am = ActionManager.Instance();
            if (am == null) return;
            // 0xE0000000 = self ObjectID；其余参数取绑定默认值
            am->UseAction(at, id, 0xE0000000ul, 0, UseActionMode.None, 0, null);
        }
        catch { }
    }

    private unsafe void ExecutePageSlot(HotbarBarConfig bar, int barIndex, int slotIndex)
    {
        ExecuteSlotRuntime(barIndex, bar.CurrentPage, slotIndex);
    }

    // ==================================================================
    //  游戏内拖拽支持（把技能 / 物品从游戏界面拖到自定义格子上）
    //
    //  原理：AtkStage.Instance()->DragDropManager 是游戏的全局拖拽管理器。
    //  拖拽期间（IsDragging = true），活动的 AtkDragDropInterface 上有
    //  DragDropType（载荷类型）与 GetPayloadContainer()->Int2（动作/物品 ID），
    //  与 KTK DragDropNode 的 DragDropPayload.FromDragDropInterface 取法一致。
    //  松开鼠标的那一帧 IsDragging 变 false，此时若光标悬停在我们的格子上就写入。
    // ==================================================================

    /// <summary>每帧调用：记录游戏拖拽状态与当前 payload（拖拽中 payload 不变，松手后会被游戏清掉，所以必须边拖边记）。</summary>
    private unsafe void CaptureDragPayload()
    {
        ddDragging = false;
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null)
            {
                ddWasDragging = false;
                return;
            }

            var mgr = &stage->DragDropManager;
            if (mgr == null || !mgr->IsDragging)
            {
                ddWasDragging = false;
                return;
            }

            ddDragging = true;
            var isStart = !ddWasDragging;   // 读的是上一帧的值：这一帧刚开始拖
            ddWasDragging = true;

            // payload 优先读管理器自带的内嵌容器（值字段，最可靠）；
            // DragDropType / ReferenceIndex 只在接口上，接口拿不到就沿用上次的
            var mpc = &mgr->PayloadContainer;
            var ddi = GetActiveDragInterface(mgr);

            var int2 = mpc->Int2;
            if (int2 == 0 && ddi != null)
            {
                var pc2 = ddi->GetPayloadContainer();
                if (pc2 != null) int2 = pc2->Int2;
            }

            if (isStart && config.DebugLog)
            {
                try
                {
                    var t = ddi != null ? (int)ddi->DragDropType : -1;
                    DalamudServices.PluginLog.Info(
                        $"[HotbarPlus] 捕获拖拽：DragDropType={t}, 容器 Int1={mpc->Int1}, Int2={mpc->Int2}, Int2(接口)={int2}, RefIndex={(ddi != null ? ddi->DragDropReferenceIndex : -1)}");
                }
                catch { }
            }

            if (ddi != null) ddLastType = (int)ddi->DragDropType;
            if (ddi != null) ddLastRef = ddi->DragDropReferenceIndex;
            if (int2 != 0) ddLastInt2 = int2;
            else if (mpc->Int1 != 0) ddLastInt2 = mpc->Int1;
        }
        catch
        {
            ddDragging = false;
        }
    }

    /// <summary>
    /// 在 DragDrop1 / DragDrop2 两个候选里找当前活动的拖拽接口。
    /// 注意：<c>AtkDragDropManager.DragDropS</c> 的类型是 <c>AtkComponentDragDrop*</c>，
    /// 与 <c>AtkDragDropInterface*</c> 不是同一类型（无 IsActive / DragDropType），不可混用。
    /// </summary>
    private static unsafe AtkDragDropInterface* GetActiveDragInterface(AtkDragDropManager* mgr)
    {
        if (mgr == null) return null;

        var p1 = mgr->DragDrop1;
        var p2 = mgr->DragDrop2;

        if (p1 != null && p1->IsActive) return p1;
        if (p2 != null && p2->IsActive) return p2;

        if (p1 != null && (int)p1->DragDropType != 0) return p1;
        if (p2 != null && (int)p2->DragDropType != 0) return p2;

        if (p1 != null) return p1;
        return p2;
    }

    /// <summary>松手落格：把最近捕获的 payload 写进悬停的格子。</summary>
    private unsafe void TryAcceptDrop()
    {
        if (!config.EnableDragDrop) return;
        if (ddWasDragging) return;          // 还在拖拽中，等松手
        if (ddLastType < 0) return;         // 没有捕获过 payload
        if (ddHoverBar < 0 || ddHoverPage < 0 || ddHoverSlot < 0) return;

        var barOk = ddHoverBar < config.Bars.Count;
        var bar = barOk ? config.Bars[ddHoverBar] : null;
        var page = bar != null ? GetPage(bar, ddHoverPage) : null;
        if (page == null || ddHoverSlot >= page.Slots.Count) { ddLastType = -1; return; }

        var (slotType, id) = ResolveDragPayload(ddLastType, ddLastInt2, ddLastRef);
        var rawType = ddLastType;
        var rawInt2 = ddLastInt2;
        var rawRef = ddLastRef;
        ddLastType = -1;

        if (slotType <= 0 || id <= 0)
        {
            try
            {
                DalamudServices.PluginLog.Debug(
                    $"[HotbarPlus] 拖拽载荷无法解析：DragDropType={rawType}, Int2={rawInt2}, RefIndex={rawRef}");
            }
            catch { }
            return;
        }

        var cfg = page.Slots[ddHoverSlot];
        cfg.Type = slotType;
        cfg.Id = id;

        // 丢弃该格的运行时缓存，下一帧强制重写原生槽位数据
        runtimes.Remove(RuntimeKey(ddHoverBar, ddHoverPage, ddHoverSlot));

        try
        {
            DalamudServices.PluginLog.Info(
                $"[HotbarPlus] 已把拖拽内容放进 {bar.Name} 第 {ddHoverSlot + 1} 格：{DescribeSlotRaw(slotType, id)}");
        }
        catch { }
    }

    /// <summary>
    /// 把 DragDropType + Int2 解析成 (HotbarSlotType 数值, ID)。
    /// 一般载荷：Int2 就是动作 / 物品等 ID。
    /// DragDropType.ActionBar（从原生热键栏拖出）：Int2 的编码无权威资料，
    /// 先按「槽位全局索引」的几种常见编码试探读取原生槽位，都不中再按动作 ID 兜底；
    /// 同时写调试日志，实机一次即可校准。
    /// </summary>
    private static unsafe (int Type, uint Id) ResolveDragPayload(int dragType, int int2, int refIndex)
    {
        var ddt = (DragDropType)dragType;

        if (ddt != DragDropType.ActionBar)
        {
            var slotType = (int)UIGlobals.GetHotbarSlotTypeFromDragDropType(ddt);
            return slotType <= 0 ? (0, 0) : (slotType, (uint)Math.Max(0, int2));
        }

        // ---- 从原生热键栏拖出的情况 ----
        var module = RaptureHotbarModule.Instance();
        if (module != null && int2 > 0)
        {
            // 候选编码：(hotbar, slot) 均按 GetSlotById 的 0 基约定
            var candidates = new (int hb, int sl)[4]
            {
                (int2 / 12, int2 % 12),             // 10 栏 × 12 格，0 基
                (int2 / 10, int2 % 10),             // 10 栏 × 10 格
                ((int2 - 1) / 12, (int2 - 1) % 12), // 1 基
                ((int2 - 1) / 10, (int2 - 1) % 10), // 1 基 × 10
            };

            foreach (var (hb, sl) in candidates)
            {
                if (hb < 0 || hb >= 10 || sl < 0 || sl >= 12) continue;
                try
                {
                    var pSlot = module->GetSlotById((uint)hb, (uint)sl);
                    if (pSlot == null) continue;
                    var t = (int)pSlot->CommandType;
                    var id = pSlot->CommandId;
                    if (t > 0 && id > 0)
                    {
                        try
                        {
                            DalamudServices.PluginLog.Debug(
                                $"[HotbarPlus] ActionBar 载荷按槽位索引解码命中：Int2={int2} → 栏{hb + 1} 格{sl + 1}");
                        }
                        catch { }
                        return (t, id);
                    }
                }
                catch { }
            }
        }

        // 兜底：按映射表直接当动作处理
        var fallback = (int)UIGlobals.GetHotbarSlotTypeFromDragDropType(ddt);
        try
        {
            DalamudServices.PluginLog.Debug(
                $"[HotbarPlus] ActionBar 载荷未按槽位索引命中，按映射兜底：Int2={int2}, RefIndex={refIndex}, 映射类型={fallback}");
        }
        catch { }
        return fallback <= 0 ? (0, 0) : (fallback, (uint)Math.Max(0, int2));
    }

    /// <summary>从游戏原生热键栏读取一格（热键栏 1-10 对应 id 0-9）。</summary>
    private static unsafe bool TryReadNativeSlot(int hotbarNumber, int slotNumber, out int type, out uint id)
    {
        type = 0;
        id = 0;
        var module = RaptureHotbarModule.Instance();
        if (module == null) return false;
        if (hotbarNumber < 1 || hotbarNumber > 10) return false;
        if (slotNumber < 1 || slotNumber > 12) return false;

        var slot = module->GetSlotById((uint)(hotbarNumber - 1), (uint)(slotNumber - 1));
        if (slot == null) return false;
        type = (int)slot->CommandType;
        id = slot->CommandId;
        return true;
    }

    // ==================================================================
    //  键位
    // ==================================================================

    private void ProcessGlobalHotkey()
    {
        if (config.ToggleAllKey == 0) return;
        if (!KeyComboDown(config.ToggleAllKey, config.ToggleAllMods)) return;
        try { DalamudServices.KeyState[config.ToggleAllKey] = false; } catch { }
        ToggleAll();
    }

    private void ProcessKeybinds()
    {
        if (ImGui.GetIO().WantTextInput) return;

        for (var b = 0; b < config.Bars.Count; b++)
        {
            var bar = config.Bars[b];
            if (!bar.Visible) continue;

            if (bar.NextPageKey != 0 && KeyComboDown(bar.NextPageKey, bar.NextPageMods))
            {
                try { DalamudServices.KeyState[bar.NextPageKey] = false; } catch { }
                ChangePage(bar, bar.CurrentPage + 1);
                continue;
            }
            if (bar.PrevPageKey != 0 && KeyComboDown(bar.PrevPageKey, bar.PrevPageMods))
            {
                try { DalamudServices.KeyState[bar.PrevPageKey] = false; } catch { }
                ChangePage(bar, bar.CurrentPage - 1);
                continue;
            }

            var page = GetPage(bar, bar.CurrentPage);
            if (page == null) continue;

            for (var i = 0; i < page.Slots.Count; i++)
            {
                var cfg = page.Slots[i];
                if (cfg.KeyCode == 0 || cfg.Type <= 0) continue;
                if (!KeyComboDown(cfg.KeyCode, cfg.KeyMods)) continue;

                try { DalamudServices.KeyState[cfg.KeyCode] = false; } catch { }
                ExecutePageSlot(bar, b, i);
            }
        }
    }

    /// <summary>判断某个「主键 + 修饰键」组合当前是否按下（修饰键必须精确匹配）。</summary>
    private static bool KeyComboDown(int keyCode, int mods)
    {
        try
        {
            var keys = DalamudServices.KeyState;
            if (keys == null) return false;
            if (!keys.IsVirtualKeyValid(keyCode)) return false;
            if (!keys[keyCode]) return false;

            var wantCtrl = (mods & 1) != 0;
            var wantAlt = (mods & 2) != 0;
            var wantShift = (mods & 4) != 0;

            var ctrl = ModifierDown(keys, 0x11); // VK_CONTROL
            var alt = ModifierDown(keys, 0x12);  // VK_MENU
            var shift = ModifierDown(keys, 0x10); // VK_SHIFT

            if (wantCtrl != ctrl) return false;
            if (wantAlt != alt) return false;
            if (wantShift != shift) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ModifierDown(Dalamud.Plugin.Services.IKeyState keys, int vk)
    {
        try
        {
            if (!keys.IsVirtualKeyValid(vk)) return false;
            return keys[vk];
        }
        catch
        {
            return false;
        }
    }

    private static string FormatKeybind(int keyCode, int mods)
    {
        var sb = new System.Text.StringBuilder();
        if ((mods & 1) != 0) sb.Append("Ctrl+");
        if ((mods & 2) != 0) sb.Append("Alt+");
        if ((mods & 4) != 0) sb.Append("Shift+");
        sb.Append(KeyName(keyCode));
        return sb.ToString();
    }

    private static string KeyName(int keyCode)
    {
        switch (keyCode)
        {
            case 0x08: return "Backspace";
            case 0x09: return "Tab";
            case 0x0D: return "Enter";
            case 0x1B: return "Esc";
            case 0x20: return "Space";
            case 0x21: return "PgUp";
            case 0x22: return "PgDn";
            case 0x23: return "End";
            case 0x24: return "Home";
            case 0x25: return "←";
            case 0x26: return "↑";
            case 0x27: return "→";
            case 0x28: return "↓";
            case 0x2D: return "Ins";
            case 0x2E: return "Del";
            case 0xBA: return ";";
            case 0xBB: return "=";
            case 0xBC: return ",";
            case 0xBD: return "-";
            case 0xBE: return ".";
            case 0xBF: return "/";
            case 0xC0: return "`";
            case 0xDB: return "[";
            case 0xDC: return "\\";
            case 0xDD: return "]";
            case 0xDE: return "'";
            case 0x6A: return "Num*";
            case 0x6B: return "Num+";
            case 0x6D: return "Num-";
            case 0x6E: return "Num.";
            case 0x6F: return "Num/";
        }

        if (keyCode >= 0x30 && keyCode <= 0x39) return ((char)keyCode).ToString();
        if (keyCode >= 0x41 && keyCode <= 0x5A) return ((char)keyCode).ToString();
        if (keyCode >= 0x60 && keyCode <= 0x69) return "Num" + (keyCode - 0x60).ToString(CultureInfo.InvariantCulture);
        if (keyCode >= 0x70 && keyCode <= 0x87) return "F" + (keyCode - 0x6F).ToString(CultureInfo.InvariantCulture);
        return "0x" + keyCode.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>设置面板里等待用户按键；返回 true 表示已经捕获到一个组合。</summary>
    private bool TryCaptureKeybind(out int keyCode, out int mods)
    {
        keyCode = 0;
        mods = 0;

        var keys = DalamudServices.KeyState;
        if (keys == null) return false;

        var ctrl = ModifierDown(keys, 0x11);
        var alt = ModifierDown(keys, 0x12);
        var shift = ModifierDown(keys, 0x10);
        var modFlags = (ctrl ? 1 : 0) | (alt ? 2 : 0) | (shift ? 4 : 0);

        for (var vk = 1; vk < 256; vk++)
        {
            if (vk == 0x11 || vk == 0x12 || vk == 0x10) continue; // 纯修饰键
            if (vk == 0x01 || vk == 0x02) continue;               // 鼠标左右键
            try
            {
                if (!keys.IsVirtualKeyValid(vk)) continue;
                if (!keys[vk]) continue;
            }
            catch
            {
                continue;
            }

            keyCode = vk;
            mods = modFlags;
            return true;
        }

        return false;
    }

    // ==================================================================
    //  贴图
    // ==================================================================

    private static bool IsTexValid(IDalamudTextureWrap tex)
    {
        if (tex == null) return false;
        try
        {
            return !EqualityComparer<ImTextureID>.Default.Equals(tex.Handle, default);
        }
        catch
        {
            return true;
        }
    }

    private IDalamudTextureWrap ResolveSlotTexture(HotbarSlotConfig cfg, uint iconId)
    {
        if (!string.IsNullOrEmpty(cfg.CustomImagePath))
        {
            var tex = GetLocalImageTexture(cfg.CustomImagePath);
            if (IsTexValid(tex)) return tex;
        }

        if (iconId == 0) return null;

        try
        {
            return ImageHelper.GetGameIcon(iconId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取并缓存本地图片贴图（后台线程加载，主线程消费）。</summary>
    private IDalamudTextureWrap GetLocalImageTexture(string path)
    {
        if (imageCache.TryGetValue(path, out var cached)) return cached;
        if (imageFailed.ContainsKey(path)) return null;
        if (!imageLoading.TryAdd(path, 0)) return null;

        if (!File.Exists(path))
        {
            imageLoading.TryRemove(path, out _);
            imageFailed.TryAdd(path, 0);
            return null;
        }

        var captured = path;
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(captured).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    imageFailed.TryAdd(captured, 0);
                    return;
                }

                // name 用于贴图管理器内部标识；显式传入可避免依赖可选参数默认值。
                var wrap = await DalamudServices.TextureProvider
                    .CreateFromImageAsync(bytes, "HotbarPlus:" + captured)
                    .ConfigureAwait(false);

                if (wrap != null && !disposed)
                {
                    imageCache[captured] = wrap;
                }
                else
                {
                    try { wrap?.Dispose(); } catch { }
                    imageFailed.TryAdd(captured, 0);
                }
            }
            catch
            {
                imageFailed.TryAdd(captured, 0);
            }
            finally
            {
                imageLoading.TryRemove(captured, out _);
            }
        });

        return null;
    }

    // ==================================================================
    //  设置面板
    // ==================================================================

    public override bool HasSettings => true;

    public override bool DrawSettings()
    {
        try
        {
            return DrawSettingsCore();
        }
        catch (Exception e)
        {
            try { DalamudServices.PluginLog.Error(e, "[HotbarPlus] 设置面板异常"); } catch { }
            return false;
        }
    }

    public override bool ResetSettings()
    {
        config = new HotbarPlusConfig();
        NormalizeConfig();
        selectedBar = 0;
        runtimes.Clear();
        return true;
    }

    private bool DrawSettingsCore()
    {
        var changed = false;
        var scale = OmniTheme.ScaleValue;

        ImGui.TextUnformatted("自定义热键栏 Plus");
        ImGui.Separator();

        // ---------------- 全局 ----------------
        var enabled = config.OverlayEnabled;
        if (ImGui.Checkbox("显示全部热键栏##hpGlobalEnabled", ref enabled))
        {
            config.OverlayEnabled = enabled;
            overlayVisible = enabled;
            changed = true;
        }
        ImGui.SameLine();
        var showKeys = config.ShowKeybinds;
        if (ImGui.Checkbox("显示键位角标##hpShowKeys", ref showKeys))
        {
            config.ShowKeybinds = showKeys;
            changed = true;
        }
        ImGui.SameLine();
        var showTips = config.ShowTooltips;
        if (ImGui.Checkbox("悬浮说明##hpShowTips", ref showTips))
        {
            config.ShowTooltips = showTips;
            changed = true;
        }
        ImGui.SameLine();
        var allowDrag = config.EnableDragDrop;
        if (ImGui.Checkbox("允许从游戏内拖入技能##hpAllowDrag", ref allowDrag))
        {
            config.EnableDragDrop = allowDrag;
            changed = true;
        }

        ImGui.SameLine();
        var dbg = config.DebugLog;
        if (ImGui.Checkbox("诊断日志##hpDebugLog", ref dbg))
        {
            config.DebugLog = dbg;
            changed = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("开启后，拖拽捕获与技能执行都会写日志（带 [HotbarPlus] 前缀）。\n排查问题时开着，正常用可以关。");
        }

        DrawKeybindRow("全局开关快捷键", "hpGlobalKey", config.ToggleAllKey, config.ToggleAllMods,
            (k, m) => { config.ToggleAllKey = k; config.ToggleAllMods = m; }, () => changed = true);

        ImGui.TextUnformatted("提示：游戏原生热键栏请到 系统设置 → 界面设置 → HUD 里自行隐藏。");
        ImGui.Spacing();
        ImGui.Separator();

        // ---------------- 栏选择 ----------------
        var preview = config.Bars.Count > 0 && selectedBar < config.Bars.Count ? config.Bars[selectedBar].Name : "（无）";
        ImGui.SetNextItemWidth(180f * scale);
        if (ImGui.BeginCombo("##hpBarSelect", preview))
        {
            for (var i = 0; i < config.Bars.Count; i++)
            {
                if (ImGui.Selectable($"{config.Bars[i].Name}##hpBarItem{i}", i == selectedBar))
                {
                    selectedBar = i;
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("新增热键栏##hpAddBar"))
        {
            var offset = 30f * config.Bars.Count;
            config.Bars.Add(new HotbarBarConfig
            {
                Name = "热键栏 " + (config.Bars.Count + 1),
                PositionX = 400f + offset,
                PositionY = 300f + offset
            });
            selectedBar = config.Bars.Count - 1;
            changed = true;
        }

        if (config.Bars.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("删除此栏##hpRemoveBar"))
            {
                config.Bars.RemoveAt(Math.Clamp(selectedBar, 0, config.Bars.Count - 1));
                selectedBar = Math.Max(0, selectedBar - 1);
                runtimes.Clear();
                changed = true;
            }
        }

        if (config.Bars.Count == 0)
        {
            ImGui.TextUnformatted("还没有热键栏，点「新增热键栏」开始。");
            return changed;
        }

        selectedBar = Math.Clamp(selectedBar, 0, config.Bars.Count - 1);
        var bar = config.Bars[selectedBar];
        ImGui.Spacing();
        changed |= DrawBarEditor(bar, selectedBar);
        ImGui.Spacing();
        changed |= DrawPageEditor(bar, selectedBar);

        return changed;
    }

    private bool DrawBarEditor(HotbarBarConfig bar, int barIndex)
    {
        var changed = false;

        var name = bar.Name;
        ImGui.SetNextItemWidth(160f * OmniTheme.ScaleValue);
        if (ImGui.InputText($"名称##hpBarName{barIndex}", ref name, 64))
        {
            bar.Name = name;
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;

        ImGui.SameLine();
        var visible = bar.Visible;
        if (ImGui.Checkbox($"显示##hpBarVisible{barIndex}", ref visible))
        {
            bar.Visible = visible;
            changed = true;
        }

        ImGui.SameLine();
        var locked = bar.Locked;
        if (ImGui.Checkbox($"锁定位置##hpBarLocked{barIndex}", ref locked))
        {
            bar.Locked = locked;
            changed = true;
        }

        ImGui.SameLine();
        var clicking = bar.EnableClicking;
        if (ImGui.Checkbox($"点击释放##hpBarClick{barIndex}", ref clicking))
        {
            bar.EnableClicking = clicking;
            changed = true;
        }

        ImGui.SameLine();
        var pager = bar.ShowPageControls;
        if (ImGui.Checkbox($"显示翻页条##hpBarPager{barIndex}", ref pager))
        {
            bar.ShowPageControls = pager;
            changed = true;
        }

        ImGui.SameLine();
        var cycle = bar.CyclePages;
        if (ImGui.Checkbox($"循环翻页##hpBarCycle{barIndex}", ref cycle))
        {
            bar.CyclePages = cycle;
            changed = true;
        }

        // 数值参数
        changed |= DrawFloatRow("缩放", "hpBarScale" + barIndex, bar.Scale, 0.1f, 3f, "0.00",
            v => bar.Scale = v, 0.05f, 1f);
        changed |= DrawIntRow("列数", "hpBarCols" + barIndex, bar.Columns, 1, 16, v => bar.Columns = v);
        changed |= DrawIntRow("行数", "hpBarRows" + barIndex, bar.Rows, 1, 12, v => bar.Rows = v);
        changed |= DrawFloatRow("格子大小", "hpBarSize" + barIndex, bar.SlotSize, 16f, 96f, "0",
            v => bar.SlotSize = v, 1f, 4f);
        changed |= DrawFloatRow("格子间距", "hpBarGap" + barIndex, bar.SlotSpacing, 0f, 24f, "0",
            v => bar.SlotSpacing = v, 1f, 4f);
        changed |= DrawFloatRow("背景不透明度", "hpBarBg" + barIndex, bar.BackgroundOpacity, 0f, 1f, "0.00",
            v => bar.BackgroundOpacity = v, 0.01f, 0.05f);

        changed |= DrawKeybindRow("上一页键位", "hpBarPrevKey" + barIndex, bar.PrevPageKey, bar.PrevPageMods,
            (k, m) => { bar.PrevPageKey = k; bar.PrevPageMods = m; }, () => changed = true);
        changed |= DrawKeybindRow("下一页键位", "hpBarNextKey" + barIndex, bar.NextPageKey, bar.NextPageMods,
            (k, m) => { bar.NextPageKey = k; bar.NextPageMods = m; }, () => changed = true);

        return changed;
    }

    private bool DrawPageEditor(HotbarBarConfig bar, int barIndex)
    {
        var changed = false;

        ImGui.Separator();
        ImGui.TextUnformatted($"当前页：第 {bar.CurrentPage + 1} / {Math.Max(1, bar.PageCount)} 页");

        ImGui.SameLine();
        if (ImGui.Button($"◀ 上一页##hpPagePrev{barIndex}"))
        {
            ChangePage(bar, bar.CurrentPage - 1);
        }
        ImGui.SameLine();
        if (ImGui.Button($"下一页 ▶##hpPageNext{barIndex}"))
        {
            ChangePage(bar, bar.CurrentPage + 1);
        }

        changed |= DrawIntRow("总页数", "hpBarPages" + barIndex, bar.PageCount, 1, 16, v => bar.PageCount = v);

        var page = GetPage(bar, bar.CurrentPage);
        if (page == null)
        {
            ImGui.TextUnformatted("该页不存在。");
            return changed;
        }

        var pageName = page.Name ?? string.Empty;
        ImGui.SetNextItemWidth(160f * OmniTheme.ScaleValue);
        if (ImGui.InputText($"页名##hpPageName{barIndex}_{bar.CurrentPage}", ref pageName, 32))
        {
            page.Name = pageName;
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;

        // ---------------- 一键导入 ----------------
        ImGui.Spacing();
        if (ImGui.Button($"导入游戏热键栏 {bar.CurrentPage + 1} → 本页##hpImportOne{barIndex}"))
        {
            ImportNativeBarIntoPage(bar, bar.CurrentPage, bar.CurrentPage + 1);
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.Button($"导入游戏热键栏 1~{Math.Max(1, bar.PageCount)} → 各页##hpImportAll{barIndex}"))
        {
            var count = Math.Max(1, bar.PageCount);
            for (var i = 0; i < count && i < 10; i++)
            {
                ImportNativeBarIntoPage(bar, i, i + 1);
            }
            changed = true;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("格子设置（类型 / 键位 / 图标覆盖）：");

        var slotCount = Math.Max(1, bar.Columns) * Math.Max(1, bar.Rows);
        EnsureSlotList(page, slotCount);

        for (var i = 0; i < page.Slots.Count; i++)
        {
            var cfg = page.Slots[i];
            ImGui.PushID((IntPtr)(barIndex * 4096 + bar.CurrentPage * 256 + i));

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{i + 1,3}");

            ImGui.SameLine();
            var desc = DescribeSlot(cfg);
            ImGui.TextUnformatted(desc.Length > 0 ? desc : "—");

            ImGui.SameLine();
            if (ImGui.Button("设置"))
            {
                editingSlotBar = barIndex;
                editingSlotPage = bar.CurrentPage;
                editingSlotIndex = i;
                ImGui.OpenPopup("##hpSlotEditor");
            }

            ImGui.SameLine();
            if (ImGui.Button("清空"))
            {
                cfg.Type = 0;
                cfg.Id = 0;
                changed = true;
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(cfg.KeyCode != 0 ? "键:" + FormatKeybind(cfg.KeyCode, cfg.KeyMods) : "键:—");
            ImGui.SameLine();
            if (ImGui.Button(capturingKeybind == SlotKeyString(barIndex, bar.CurrentPage, i) ? "按键中…" : "绑定"))
            {
                capturingKeybind = SlotKeyString(barIndex, bar.CurrentPage, i);
            }
            ImGui.SameLine();
            if (ImGui.Button("清除键位"))
            {
                cfg.KeyCode = 0;
                cfg.KeyMods = 0;
                changed = true;
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(cfg.CustomIconId > 0 ? $"图标:{cfg.CustomIconId}" : "图标:原生");
            ImGui.SameLine();
            if (ImGui.Button("选择图标"))
            {
                var captured = cfg;
                OpenIconBrowser(iconId =>
                {
                    captured.CustomIconId = iconId;
                    captured.CustomImagePath = string.Empty;
                });
            }
            ImGui.SameLine();
            if (ImGui.Button("清除图标"))
            {
                cfg.CustomIconId = 0;
                cfg.CustomImagePath = string.Empty;
                changed = true;
            }

            ImGui.PopID();
        }

        // 键位捕获
        if (capturingKeybind.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted($"请按下要绑定的按键…（当前目标 {capturingKeybind}，按 Esc 取消）");
            if (TryCaptureKeybind(out var keyCode, out var mods))
            {
                if (keyCode == 0x1B)
                {
                    capturingKeybind = string.Empty;
                }
                else if (ApplyCapturedKeybind(keyCode, mods))
                {
                    capturingKeybind = string.Empty;
                    changed = true;
                }
            }
        }

        // 弹窗
        if (ImGui.BeginPopup("##hpSlotEditor"))
        {
            changed |= DrawSlotEditorPopup(bar, barIndex);
            ImGui.EndPopup();
        }

        return changed;
    }

    private int editingSlotBar = -1;
    private int editingSlotPage = -1;
    private int editingSlotIndex = -1;

    private static string SlotKeyString(int barIndex, int pageIndex, int slotIndex)
    {
        return barIndex + ":" + pageIndex + ":" + slotIndex;
    }

    private bool ApplyCapturedKeybind(int keyCode, int mods)
    {
        var parts = capturingKeybind.Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var barIndex)) return false;
        if (!int.TryParse(parts[1], out var pageIndex)) return false;
        if (!int.TryParse(parts[2], out var slotIndex)) return false;

        if (barIndex < 0 || barIndex >= config.Bars.Count) return false;
        var bar = config.Bars[barIndex];
        var page = GetPage(bar, pageIndex);
        if (page == null || slotIndex < 0 || slotIndex >= page.Slots.Count) return false;

        page.Slots[slotIndex].KeyCode = keyCode;
        page.Slots[slotIndex].KeyMods = mods;
        return true;
    }

    private bool DrawSlotEditorPopup(HotbarBarConfig bar, int barIndex)
    {
        var changed = false;
        if (editingSlotBar != barIndex) return false;
        var page = GetPage(bar, editingSlotPage);
        if (page == null || editingSlotIndex < 0 || editingSlotIndex >= page.Slots.Count) return false;
        var cfg = page.Slots[editingSlotIndex];

        ImGui.TextUnformatted($"第 {editingSlotIndex + 1} 格");

        // 类型
        ImGui.SetNextItemWidth(180f * OmniTheme.ScaleValue);
        if (ImGui.BeginCombo("类型##hpSlotType", TypeName(cfg.Type)))
        {
            for (var t = 0; t < SlotTypeOptions.Length; t++)
            {
                var option = SlotTypeOptions[t];
                if (ImGui.Selectable($"{option.Name}##hpSlotType{t}", cfg.Type == option.Type))
                {
                    cfg.Type = option.Type;
                    cfg.Id = 0;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }

        // 按名称查找
        ImGui.SetNextItemWidth(180f * OmniTheme.ScaleValue);
        if (ImGui.InputText("名称查找##hpSlotSearch", ref searchName, 64))
        {
            searchResolved = string.Empty;
        }
        ImGui.SameLine();
        if (ImGui.Button("查找##hpSlotSearchBtn"))
        {
            if (TryResolveByName(cfg.Type, searchName, out var resolvedId, out var resolvedName))
            {
                cfg.Id = resolvedId;
                searchResolved = resolvedName;
                changed = true;
            }
            else
            {
                searchResolved = string.Empty;
                Notify("没有找到完全同名的条目，请检查名称是否与游戏内一致。");
            }
        }
        if (searchResolved.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("→ " + searchResolved);
        }

        // 直接填 ID
        var idText = cfg.Id.ToString(CultureInfo.InvariantCulture);
        ImGui.SetNextItemWidth(120f * OmniTheme.ScaleValue);
        if (ImGui.InputText("ID##hpSlotId", ref idText, 16, ImGuiInputTextFlags.CharsDecimal))
        {
            if (uint.TryParse(idText, out var parsed)) cfg.Id = parsed;
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;

        // 从原生热键栏读取
        ImGui.Separator();
        ImGui.TextUnformatted("从游戏原生热键栏读取：");
        DrawNativeImportRow(cfg, (type, id, label) =>
        {
            cfg.Type = type;
            cfg.Id = id;
            searchResolved = label;
            changed = true;
        });

        // 角标文字
        ImGui.Separator();
        var label2 = cfg.Label ?? string.Empty;
        ImGui.SetNextItemWidth(120f * OmniTheme.ScaleValue);
        if (ImGui.InputText("角标文字##hpSlotLabel", ref label2, 16))
        {
            cfg.Label = label2;
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;

        ImGui.Separator();
        if (ImGui.Button("关闭##hpSlotEditorClose"))
        {
            ImGui.CloseCurrentPopup();
        }

        return changed;
    }

    private string searchName = string.Empty;
    private string searchResolved = string.Empty;
    private int importHotbar = 1;
    private int importSlot = 1;

    private void DrawNativeImportRow(HotbarSlotConfig cfg, Action<int, uint, string> apply)
    {
        ImGui.SetNextItemWidth(70f * OmniTheme.ScaleValue);
        var hotbarText = importHotbar.ToString(CultureInfo.InvariantCulture);
        if (ImGui.InputText("热键栏##hpImportBar", ref hotbarText, 3, ImGuiInputTextFlags.CharsDecimal))
        {
            if (int.TryParse(hotbarText, out var v)) importHotbar = Math.Clamp(v, 1, 10);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f * OmniTheme.ScaleValue);
        var slotText = importSlot.ToString(CultureInfo.InvariantCulture);
        if (ImGui.InputText("格##hpImportSlot", ref slotText, 3, ImGuiInputTextFlags.CharsDecimal))
        {
            if (int.TryParse(slotText, out var v)) importSlot = Math.Clamp(v, 1, 12);
        }

        ImGui.SameLine();
        if (ImGui.Button("读取##hpImportRead"))
        {
            if (TryReadNativeSlot(importHotbar, importSlot, out var type, out var id))
            {
                var label = DescribeSlotRaw(type, id);
                apply(type, id, label);
            }
            else
            {
                Notify("读取失败：请确认已进入游戏角色且栏号/格号有效。");
            }
        }
    }

    // ==================================================================
    //  设置面板小工具
    // ==================================================================

    private static bool DrawFloatRow(string label, string id, float value, float min, float max, string format,
        Action<float> setter, float step, float stepFast)
    {
        ImGui.SetNextItemWidth(140f * OmniTheme.ScaleValue);
        var v = value;
        if (ImGui.DragFloat($"{label}##{id}", ref v, step, min, max, format))
        {
            setter(v);
        }
        return ImGui.IsItemDeactivatedAfterEdit();
    }

    private static bool DrawIntRow(string label, string id, int value, int min, int max, Action<int> setter)
    {
        var changed = false;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        if (ImGui.Button($"-##{id}"))
        {
            setter(Math.Clamp(value - 1, min, max));
            changed = true;
        }
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(value.ToString(CultureInfo.InvariantCulture));
        ImGui.SameLine();
        if (ImGui.Button($"+##{id}"))
        {
            setter(Math.Clamp(value + 1, min, max));
            changed = true;
        }
        return changed;
    }

    private bool DrawKeybindRow(string label, string id, int keyCode, int mods, Action<int, int> setter, Action markChanged)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine();

        var capturing = capturingKeybind == id;
        if (ImGui.Button(capturing ? "按键中…##" + id : "绑定##" + id))
        {
            capturingKeybind = id;
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(keyCode != 0 ? FormatKeybind(keyCode, mods) : "—");
        ImGui.SameLine();
        if (ImGui.Button("清除##" + id))
        {
            setter(0, 0);
            markChanged();
            return true;
        }

        if (capturing && TryCaptureKeybind(out var capturedKey, out var capturedMods))
        {
            if (capturedKey == 0x1B)
            {
                capturingKeybind = string.Empty;
            }
            else
            {
                setter(capturedKey, capturedMods);
                capturingKeybind = string.Empty;
                markChanged();
                return true;
            }
        }

        return false;
    }

    // ==================================================================
    //  槽位描述 / 名称解析
    // ==================================================================

    private sealed class SlotTypeOption
    {
        public int Type;
        public string Name;
    }

    private static readonly SlotTypeOption[] SlotTypeOptions =
    {
        new SlotTypeOption { Type = 0,  Name = "（空）" },
        new SlotTypeOption { Type = (int)RaptureHotbarModule.HotbarSlotType.Action,           Name = "技能" },
        new SlotTypeOption { Type = (int)RaptureHotbarModule.HotbarSlotType.CraftAction,      Name = "制作技能" },
        new SlotTypeOption { Type = (int)RaptureHotbarModule.HotbarSlotType.Item,             Name = "物品" },
        new SlotTypeOption { Type = (int)RaptureHotbarModule.HotbarSlotType.Emote,            Name = "情感动作" },
        new SlotTypeOption { Type = (int)RaptureHotbarModule.HotbarSlotType.Mount,            Name = "坐骑" },
        new SlotTypeOption { Type = (int)RaptureHotbarModule.HotbarSlotType.Companion,        Name = "宠物" },
        new SlotTypeOption { Type = (int)RaptureHotbarModule.HotbarSlotType.GeneralAction,    Name = "通用技能" },
        new SlotTypeOption { Type = (int)RaptureHotbarModule.HotbarSlotType.MainCommand,      Name = "主命令" },
        new SlotTypeOption { Type = (int)RaptureHotbarModule.HotbarSlotType.Marker,           Name = "名牌" },
        new SlotTypeOption { Type = (int)RaptureHotbarModule.HotbarSlotType.GearSet,          Name = "套装" },
        new SlotTypeOption { Type = (int)RaptureHotbarModule.HotbarSlotType.Macro,            Name = "宏" },
    };

    private static string TypeName(int type)
    {
        foreach (var option in SlotTypeOptions)
        {
            if (option.Type == type) return option.Name;
        }
        return "类型 " + type.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>取一个格子当前（游戏解析后）的技能/物品名，用于悬浮提示。</summary>
    private static string NativeSlotName(SlotRuntime rt)
    {
        try
        {
            var useApparent = rt.Data.ApparentSlotType != RaptureHotbarModule.HotbarSlotType.Empty;
            var type = (int)(useApparent ? rt.Data.ApparentSlotType : rt.Data.CommandType);
            var id = useApparent ? rt.Data.ApparentActionId : rt.Data.CommandId;
            if (type <= 0 || id == 0) return string.Empty;
            return TryGetLuminaName(type, id, out var found) ? found : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DescribeSlot(HotbarSlotConfig cfg)
    {
        if (cfg.Type <= 0) return string.Empty;
        return DescribeSlotRaw(cfg.Type, cfg.Id);
    }

    private static string DescribeSlotRaw(int type, uint id)
    {
        if (type <= 0) return string.Empty;
        var typeName = TypeName(type);
        var name = TryGetLuminaName(type, id, out var found) ? found : ("#" + id.ToString(CultureInfo.InvariantCulture));
        return $"{typeName} {name}";
    }

    private static bool TryGetLuminaName(int type, uint id, out string name)
    {
        name = string.Empty;
        try
        {
            if (type == (int)RaptureHotbarModule.HotbarSlotType.Action)
            {
                var row = LuminaGetter.Get<LuminaAction>().GetRowOrDefault(id);
                if (row is { } r) { name = SafeText(r.Name); return name.Length > 0; }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.CraftAction)
            {
                var row = LuminaGetter.Get<LuminaCraftAction>().GetRowOrDefault(id);
                if (row is { } r) { name = SafeText(r.Name); return name.Length > 0; }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.Item)
            {
                var row = LuminaGetter.Get<LuminaItem>().GetRowOrDefault(id);
                if (row is { } r) { name = SafeText(r.Name); return name.Length > 0; }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.Emote)
            {
                var row = LuminaGetter.Get<LuminaEmote>().GetRowOrDefault(id);
                if (row is { } r) { name = SafeText(r.Name); return name.Length > 0; }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.Mount)
            {
                var row = LuminaGetter.Get<LuminaMount>().GetRowOrDefault(id);
                if (row is { } r) { name = SafeText(r.Singular); return name.Length > 0; }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.Companion)
            {
                var row = LuminaGetter.Get<LuminaCompanion>().GetRowOrDefault(id);
                if (row is { } r) { name = SafeText(r.Singular); return name.Length > 0; }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.GeneralAction)
            {
                var row = LuminaGetter.Get<LuminaGeneralAction>().GetRowOrDefault(id);
                if (row is { } r) { name = SafeText(r.Name); return name.Length > 0; }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.MainCommand)
            {
                var row = LuminaGetter.Get<LuminaMainCommand>().GetRowOrDefault(id);
                if (row is { } r) { name = SafeText(r.Name); return name.Length > 0; }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.Marker)
            {
                var row = LuminaGetter.Get<LuminaMarker>().GetRowOrDefault(id);
                if (row is { } r) { name = SafeText(r.Name); return name.Length > 0; }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.Macro)
            {
                name = "宏 #" + id.ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string SafeText(ReadOnlySeString value)
    {
        try { return value.ExtractText() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool TryResolveByName(int type, string name, out uint id, out string resolved)
    {
        id = 0;
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var target = name.Trim();

        try
        {
            if (type == (int)RaptureHotbarModule.HotbarSlotType.Action)
            {
                foreach (var row in LuminaGetter.Get<LuminaAction>())
                {
                    if (SafeText(row.Name) != target) continue;
                    id = row.RowId;
                    resolved = target;
                    return id > 0;
                }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.CraftAction)
            {
                foreach (var row in LuminaGetter.Get<LuminaCraftAction>())
                {
                    if (SafeText(row.Name) != target) continue;
                    id = row.RowId;
                    resolved = target;
                    return id > 0;
                }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.Item)
            {
                foreach (var row in LuminaGetter.Get<LuminaItem>())
                {
                    if (SafeText(row.Name) != target) continue;
                    id = row.RowId;
                    resolved = target;
                    return id > 0;
                }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.Emote)
            {
                foreach (var row in LuminaGetter.Get<LuminaEmote>())
                {
                    if (SafeText(row.Name) != target) continue;
                    id = row.RowId;
                    resolved = target;
                    return id > 0;
                }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.Mount)
            {
                foreach (var row in LuminaGetter.Get<LuminaMount>())
                {
                    if (SafeText(row.Singular) != target) continue;
                    id = row.RowId;
                    resolved = target;
                    return id > 0;
                }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.Companion)
            {
                foreach (var row in LuminaGetter.Get<LuminaCompanion>())
                {
                    if (SafeText(row.Singular) != target) continue;
                    id = row.RowId;
                    resolved = target;
                    return id > 0;
                }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.GeneralAction)
            {
                foreach (var row in LuminaGetter.Get<LuminaGeneralAction>())
                {
                    if (SafeText(row.Name) != target) continue;
                    id = row.RowId;
                    resolved = target;
                    return id > 0;
                }
            }
            else if (type == (int)RaptureHotbarModule.HotbarSlotType.MainCommand)
            {
                foreach (var row in LuminaGetter.Get<LuminaMainCommand>())
                {
                    if (SafeText(row.Name) != target) continue;
                    id = row.RowId;
                    resolved = target;
                    return id > 0;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    // ==================================================================
    //  配置整理
    // ==================================================================

    private static HotbarPageConfig GetPage(HotbarBarConfig bar, int index)
    {
        if (bar.Pages == null || bar.Pages.Count == 0) return null;
        if (index < 0) index = 0;
        if (index >= bar.Pages.Count) index = bar.Pages.Count - 1;
        return bar.Pages[index];
    }

    private static void EnsureSlotList(HotbarPageConfig page, int count)
    {
        page.Slots ??= new List<HotbarSlotConfig>();
        while (page.Slots.Count < count) page.Slots.Add(new HotbarSlotConfig());
        if (page.Slots.Count > count) page.Slots.RemoveRange(count, page.Slots.Count - count);
    }

    private void NormalizeConfig()
    {
        config ??= new HotbarPlusConfig();
        config.Bars ??= new List<HotbarBarConfig>();

        if (config.Bars.Count == 0)
        {
            config.Bars.Add(new HotbarBarConfig());
        }

        for (var i = 0; i < config.Bars.Count; i++)
        {
            var bar = config.Bars[i];
            if (string.IsNullOrWhiteSpace(bar.Name)) bar.Name = "热键栏 " + (i + 1);

            bar.Columns = Math.Clamp(bar.Columns, 1, 16);
            bar.Rows = Math.Clamp(bar.Rows, 1, 12);
            bar.SlotSize = Math.Clamp(bar.SlotSize <= 0f ? 44f : bar.SlotSize, 16f, 96f);
            bar.SlotSpacing = Math.Clamp(bar.SlotSpacing, 0f, 24f);
            bar.Scale = Math.Clamp(bar.Scale <= 0f ? 1f : bar.Scale, 0.1f, 3f);
            bar.BackgroundOpacity = Math.Clamp(bar.BackgroundOpacity, 0f, 1f);
            bar.PageCount = Math.Clamp(bar.PageCount, 1, 16);
            bar.CurrentPage = Math.Clamp(bar.CurrentPage, 0, bar.PageCount - 1);

            bar.Pages ??= new List<HotbarPageConfig>();
            while (bar.Pages.Count < bar.PageCount) bar.Pages.Add(new HotbarPageConfig());
            if (bar.Pages.Count > bar.PageCount) bar.Pages.RemoveRange(bar.PageCount, bar.Pages.Count - bar.PageCount);

            var slotCount = bar.Columns * bar.Rows;
            for (var p = 0; p < bar.Pages.Count; p++)
            {
                var page = bar.Pages[p];
                if (page == null)
                {
                    page = new HotbarPageConfig();
                    bar.Pages[p] = page;
                }
                page.Name ??= string.Empty;
                EnsureSlotList(page, slotCount);

                foreach (var slot in page.Slots)
                {
                    slot.CustomImagePath ??= string.Empty;
                    slot.Label ??= string.Empty;
                    if (slot.Type < 0) slot.Type = 0;
                }
            }
        }

        const float defaultSnap = 1f;
        if (config.SnapStep < 0f) config.SnapStep = defaultSnap;

        // 样式迁移 v2：对齐原生热键栏观感（44px / 间距 2 / 深色底）。只跑一次。
        if (config.ConfigVersion < 2)
        {
            foreach (var b in config.Bars)
            {
                b.SlotSize = 44f;
                b.SlotSpacing = 2f;
                b.Scale = 1f;
                b.BackgroundOpacity = 0.78f;
            }
            config.ConfigVersion = 2;
        }

        // 样式迁移 v3：完全对齐原生（间距 0 / 半透明黑底 / 无常驻翻页条）。只跑一次。
        if (config.ConfigVersion < 3)
        {
            foreach (var b in config.Bars)
            {
                b.SlotSize = 44f;
                b.SlotSpacing = 0f;
                b.Scale = 1f;
                b.BackgroundOpacity = 0.55f;
                b.ShowPageControls = false;
            }
            config.ConfigVersion = 3;
        }
    }

    private void ImportNativeBarIntoPage(HotbarBarConfig bar, int pageIndex, int nativeHotbarNumber)
    {
        var page = GetPage(bar, pageIndex);
        if (page == null) return;

        var slotCount = Math.Max(1, bar.Columns) * Math.Max(1, bar.Rows);
        EnsureSlotList(page, slotCount);

        var copied = 0;
        for (var i = 0; i < page.Slots.Count && i < 12; i++)
        {
            if (!TryReadNativeSlot(nativeHotbarNumber, i + 1, out var type, out var id)) continue;
            page.Slots[i].Type = type;
            page.Slots[i].Id = id;
            copied++;
        }

        runtimes.Clear();
        Notify($"已从游戏热键栏 {nativeHotbarNumber} 导入 {copied} 格到「{bar.Name}」第 {pageIndex + 1} 页");
    }

    // ==================================================================
    //  ImGui 样式栈（避免直接依赖 ImRaii 的命名空间）
    // ==================================================================

    private sealed class ImGuiStyleScope : IDisposable
    {
        private int count;

        public void Push(ImGuiStyleVar variable, float value)
        {
            ImGui.PushStyleVar(variable, value);
            count++;
        }

        public void Push(ImGuiStyleVar variable, Vector2 value)
        {
            ImGui.PushStyleVar(variable, value);
            count++;
        }

        public void Dispose()
        {
            if (count <= 0) return;
            ImGui.PopStyleVar(count);
            count = 0;
        }
    }
}
