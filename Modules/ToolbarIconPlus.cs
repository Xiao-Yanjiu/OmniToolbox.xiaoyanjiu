using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Host;
using AgentMacro = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMacro;
using AddonMacro = FFXIVClientStructs.FFXIV.Client.UI.AddonMacro;
using AtkComponentBase = FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentBase;
using AtkComponentDragDrop = FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentDragDrop;
using AtkComponentIcon = FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentIcon;
using AtkComponentNode = FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentNode;
using AtkImageNode = FFXIVClientStructs.FFXIV.Component.GUI.AtkImageNode;
using AtkResNode = FFXIVClientStructs.FFXIV.Component.GUI.AtkResNode;
using IconSubFolder = FFXIVClientStructs.FFXIV.Component.GUI.IconSubFolder;
// ★v12★ 宿主 RefreshMacroAddonSlot / ShowMacroDragDropIcon 里两个标量掩码要用到
using DragDropVisibilityFlag = FFXIVClientStructs.FFXIV.Component.GUI.DragDropVisibilityFlag;
using IconComponentFlags = FFXIVClientStructs.FFXIV.Component.GUI.IconComponentFlags;
using AtkStage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage;
// ★v18★ 热键栏扫描要拿 addon 单元本体（读 IsVisible、再按偏移取 ActionBarSlotVector/RaptureHotbarId）
using AtkUnitBase = FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase;
using AtkTexture = FFXIVClientStructs.FFXIV.Component.GUI.AtkTexture;
using ComponentType = FFXIVClientStructs.FFXIV.Component.GUI.ComponentType;
using CsGameTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;
using NodeType = FFXIVClientStructs.FFXIV.Component.GUI.NodeType;
using RaptureHotbarModule = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;
using RaptureMacroModule = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureMacroModule;
using RaptureShellModule = FFXIVClientStructs.FFXIV.Client.UI.Shell.RaptureShellModule;
using TextureType = FFXIVClientStructs.FFXIV.Component.GUI.TextureType;

namespace OmniToolbox.LocalModules;

public sealed class ToolbarIconPlus : ModuleBase
{
    // ------------------------------ 常量 / 路径 ------------------------------

    private const string Tag = "[ToolbarIconPlus]";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";

    private static string AppDataDir => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string LocalAppDataDir => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>本模块自己的配置文件。</summary>
    private static string OwnConfigPath =>
        Path.Combine(AppDataDir, "XIVLauncherCN", "pluginConfigs", "OmniToolbarIconPlus.json");

    /// <summary>favicon / 转换后图片的本地缓存目录。</summary>
    private static string IconCacheDir =>
        Path.Combine(LocalAppDataDir, "OmniToolbarIconPlus", "icons");

    // ------------------------------ 模块信息 ------------------------------

    public override ModuleInfo Info { get; } = new()
    {
        Title = "更好的图标管理",
        Description = "网站/本地图标进宿主浏览器；游戏宏图标与全局图标替换。",
        Category = ModuleCategory.Interface,
        Author = "小烟酒",
    };

    // ------------------------------ 状态 ------------------------------

    private Config config = new();

    /// <summary>底部「新增」输入框内容。</summary>
    private string newInput = "";

    private string statusLine = "";
    private bool statusIsError;

    /// <summary>是否允许做「深度探测」（遍历宿主插件实例的对象图）。一次性开关，用完即清。</summary>
    private bool deepProbe;

    private static object? managerCache;          // TreeHouseManager 实例（整个会话有效）
    private static PropertyInfo? saveHostConfigPropCache;

    // ---- 宿主图标浏览器注入 ----
    private const uint BrowserIdBase = 0x7F000000u;     // 本地图标库保留 ID 段起点
    private const int BrowserIdCapacity = 256;           // 保留段容量
    private long lastBrowserEnsureTick;
    private long lastManagerAttemptTick;
    private string browserState = "";                    // 注入状态（面板显示）
    private static Type? texProviderTypeCache;           // ITextureProvider 反射缓存
    private static object? texProviderCache;
    private static MethodInfo? getFromFileCache;
    private string lastInjectionDiag = "";               // 上次注入诊断串（变化才写日志）

    // ---- detour（HarmonyX / 0Harmony：把保留段 ID 接进宿主的「内置文件图标」通道）----
    private const string HarmonyId = "ToolbarIconPlus.Omni";
    private static Type? harmonyTypeCache;
    private static System.Reflection.Assembly? harmonyAsmCache;   // 保住引用，别让动态加载的程序集被回收
    private static string harmonyLoadInfo = "未尝试";
    private static bool harmonyDiskTried;               // 磁盘加载只做一次，避免重试时反复读 2.3MB DLL
    private static object? harmonyInstance;
    private static bool detoursInstalled;
    private static string detourState = "detour 未挂";
    private static string texProviderState = "未探测";
    private static ToolbarIconPlus? selfRef;             // prefix 静态方法回读模块配置用
    private static readonly Dictionary<uint, object?> detourWrapCache = new Dictionary<uint, object?>();
    private const int DetourTargetCount = 5;             // 需要挂的补丁个数（用于「已挂 N/5」与重试判定）
    private static readonly HashSet<string> detourPatched = new HashSet<string>(StringComparer.Ordinal);
    private static string detourLastLogged = "detour 未挂";
    private static long lastDetourRetryTick;

    // ---- 每帧心跳（不依赖面板是否打开）----
    private static Delegate? frameworkTickDelegate;

    // ---- 宏图标（把保留段编号的贴图槽换成图库里的图片）----
    private const string MacroIconCommand = "/图库图标";        // 主命令（中文）
    private const string MacroIconCommandAlias = "/ticon";      // 备用别名（纯 ASCII，宏里更保险）
    private const int MacroIconKernelSize = 80;                 // 内核贴图边长（ULD 里图标 UV 恒为整张，尺寸不影响显示）
    private const int MacroIconWalkDepth = 24;                  // 组件内节点树递归深度上限
    private const int MacroIconNodeBudget = 8192;               // 单个界面单元最多走多少节点
    private const long MacroIconSweepBudgetMs = 4;              // 一轮扫描的时间预算（毫秒）
// ★v14 150→1000：常驻开销降到原来的 1/6（用户要求「需要显示的时候才显示」）。
    private const int MacroIconSwapIntervalMs = 250;            // 扫描节流（毫秒）

// 喂给原 LoadIcon 的「真实占位图标」候选。
    private static readonly uint[] MacroIconPlaceholders = { 62100, 62101, 60409, 1 };

    private static bool macroIconCommandRegistered;
    private static string macroIconCommandState = "未注册";
    private static readonly ConcurrentDictionary<uint, IntPtr> macroKernels = new();
    private static readonly ConcurrentDictionary<uint, byte> macroKernelPending = new();
    private static readonly ConcurrentDictionary<uint, byte> macroKernelFailed = new();
// 内核贴图做完后必须**一直持有**那个 wrap（ConvertToKernelTexture 的 leaveWrapOpen=true
    private static readonly ConcurrentQueue<IDalamudTextureWrap> macroKernelKeeps = new();
    private static string macroIconKernelDiag = "";             // 最近一次内核贴图构建结果（面板显示）
    private static int macroSwapHits;                           // 上一轮扫描命中并写入的贴图槽数
    private static int macroSwapUnits;                          // 上一轮扫描的界面单元数
    private static long lastMacroSwapTick;
    private static int macroSwapCursor;                         // 轮转起点（预算不够时下轮接着扫）

// ---- 宏图标：加载期拦截（★ 扫描救不回"加载就失败"的槽，这里才是稳的那条路）----
    private static Hook<AtkComponentIconLoadIconDelegate>? macroIconLoadHook;
    private static string macroIconHookState = "未挂";

// ---- ★v22：把替换点下沉到「贴图加载层」—— 「载体编号」思路的正确形态 ----
    private static Hook<AtkTextureLoadIconTextureDelegate>? macroTexHook;
    private static string macroTexHookState = "未挂";
    private static int macroTexHookCalls;                       // 贴图层拦截到的「保留段编号」请求数（诊断）
    private static int macroTexHookHits;                        // 其中真正递上内核贴图的次数（诊断）

// ---- ★v15：切页后「贴图槽被游戏卸载」的急救 ----
    private static int macroSlotRebuilds;                       // 槽重建次数（诊断）
    private static readonly Dictionary<nint, long> macroRebuildBackoff = new Dictionary<nint, long>();

// ---- ★v17：按「组件对象」认图标是错的 —— 跨栏串图的根源，改回「只认编号」----
    private static int macroSlotReverts;                        // 归还（把占错槽的图摘掉）次数（诊断）
    private static int macroRevertUnknown;                      // 想归还但组件编号是 0、不知道该恢复成什么（诊断）
    private static readonly Dictionary<nint, long> macroRevertBackoff = new Dictionary<nint, long>();
    private static int macroEmptyCompSeen;                      // 扫到「三槽全空」的图标组件数（诊断）
    private static int macroRebuildAttempts;                    // 重建尝试次数（诊断）
    private static int macroRebuildFails;                       // 重建失败次数（诊断）

// ---- ★v18 内容驱动：按「热键栏槽自己的编号」直接定位到了哪个图标组件 ----
    private const int ActionBarSlotStride = 0xC8;   // sizeof(ActionBarSlot)
    private const int AbeVectorOff = 0x238;         // AddonActionBarBase.ActionBarSlotVector
    private const int AbeHotbarIdOff = 0x254;       // AddonActionBarBase.RaptureHotbarId
    private const int AbeSlotCountOff = 0x256;      // AddonActionBarBase.SlotCount
    private const int AbsIconNodeOff = 0xB8;        // ActionBarSlot.Icon（AtkComponentNode*）
    private const int CompNodeCompOff = 0xC0;       // AtkComponentNode.Component
    private const int HSlotIconIdOff = 0xD0;        // HotbarSlot.IconId
    private const int HSlotCmdIdOff = 0xB8;         // HotbarSlot.CommandId
    private const int HSlotCmdTypeOff = 0xC7;       // HotbarSlot.CommandType（byte，Macro=7）
    private static int macroHotbarOwnSlots;                     // 最近一轮：热键栏里「内容是我们的」的槽数
    private static int macroHotbarFixes;                        // 累计：热键栏贴图补写次数
    private static int macroHotbarBlindFixes;                   // 累计：组件被清成 0 时靠槽数据救回的次数
    private static string macroHotbarDiag = "";                 // 最近一次热键栏逐槽诊断
    private static string lastMacroHotbarDiagSig = "";           // 热键栏诊断签名（相同就不重复打）
// 热键栏「逐槽暂存」缓冲区（一趟跑完复用；上限 13 个界面 × 32 槽）。
    private static readonly uint[] macroHbSlotIcons = new uint[32];
    private static readonly uint[] macroHbCompIds = new uint[32];
    private static readonly uint[] macroHbLibIds = new uint[32];
    private static readonly nint[] macroHbIconPtrs = new nint[32];
    private static readonly bool[] macroHbHasIcon = new bool[32];

// ---- 宏执行 hook（通道三，★ 确定性通道）----
    private static Hook<ExecuteMacroDelegate>? macroExecHook;
    private static string macroExecHookState = "未挂";
    private static int macroExecSeen;                           // 拦截到的宏执行次数（诊断）
    private static int macroExecHits;                           // 其中命中本模块指令的次数（诊断）
    private static string macroExecLastDiag = "无";              // 最近一次宏执行观察（面板/日志显示）
    private static int macroIconHookCalls;                      // 拦截到的 LoadIcon 调用次数（诊断）
    private static int macroIconHookReserved;                   // 其中编号落在保留段的次数（诊断）
    private static int macroIconHookRuled;                      // 其中命中替换规则的次数（诊断）
    private static int macroIconHookSwaps;                      // 其中真正换上我们贴图的次数
    private static long lastMacroHookLogTick;                   // hook 存活日志节流（不依赖扫描那条路）
    private static string lastMacroHookLogSig = "";              // 上次 hook 存活日志的内容签名（相同就不重复打）
    private static int macroIconHookErrors;                     // 原函数调用失败/占位图标都不认的次数（诊断）
    private static long lastMacroSweepLogTick;                  // 扫描诊断日志节流
    private static string lastMacroSweepLogSig = "";            // 上次诊断日志的内容签名（相同就不重复打）
    private static int macroSweepComps;                         // 累计扫到的图标组件数（诊断）
    private static int macroSweepReserved;                      // 累计其中 IconId 落在保留段的个数（诊断）
    // ★v14 安全门诊断：回答「图标没刷新，到底是门挡了、还是没命中」
    private static int macroSweepGatedByMask;                   // 因加载遮罩而整轮跳过的次数（诊断）
    private static int macroSweepNotVisible;                    // 因界面不可见而跳过的单元数（诊断）
    // 「本轮新增」打点：累计数只会一直涨，看着吓人还没有信息量；打点后日志/面板显示增量
    private static int macroSweepCompsMark;                     // 上次打点时的累计值
    private static int macroSweepCompsDelta;                    // 距上次打点新增的图标组件数
    private static int macroSweepRuledDelta;                    // 距上次打点新增的规则命中数
    private static int macroSweepReservedDelta;                 // 距上次打点新增的保留段数
    private static int macroSweepRuledLast;                     // 上次打点时的规则命中累计值
    private static int macroSweepReservedLast;                  // 上次打点时的保留段累计值
    private static int macroSweepRuled;                         // 累计其中命中替换规则的个数（诊断）

// ---- ★v12★ 「面板槽已应用」状态 + 贴图就绪后补刷新 ----
    private static readonly Dictionary<int, uint> macroPanelApplied = new();
    private static bool macroPanelWasVisible;                   // 上拍面板是否可见（重新打开要重应用）
    private static uint macroPanelSetSeen = 9;                  // 上拍面板显示的表（9 = 未知）
    private static uint macroPanelPageSeen = 9;                 // 上拍面板显示的页（9 = 未知）
    private static nint macroPanelCompSeen;                     // 上拍面板首个组件的地址（检测列表被重画）
    private static long lastHotbarSyncTick;                     // 热键栏安全网节流（5s）
    private static int macroHotbarReloads;                      // 累计热键栏重载次数（诊断）

// ---- 宏内指令：第二条通道（自己读「宏当前执行到哪一行」）----
    private static int macroLineSeenIndex = -1;                 // 上次看到的宏行号（0 = 宏没在跑）
    private static string macroLineTextSeen = "";               // 上次看到的宏行原文
    private static bool macroLinePrimed;                        // 是否已经记过基线（第一次只记不执行）
    private static string macroShellState = "未检测";           // RaptureShellModule 是否可取（诊断）
    private static int macroLineSeen;                           // 见过的宏行数（诊断）
    private static int macroLineLogged;                         // 已写进日志的宏行数（限流防刷屏）
    private static int macroLineHits;                           // 其中命中本模块指令的宏行数（诊断）
    private static string macroLineLastDiag = "无";             // 最近一次宏行观察（面板/日志显示）

// ---- 通道四：宏文本扫描（★ 现在的主通道，借鉴宿主内置「更好的用户宏」）----
    private static long lastMacroTextSweepTick;                 // 通道四节流
    private static int macroTextCmdSeen;                        // 累计发现的指令行数（诊断）
    private static int macroTextWrites;                         // 累计写图标次数（诊断）
    private static string macroTextLastDiag = "无";             // 最近一次写入（面板/日志显示）

// ---- 写完图标后的「界面刷新」（★ 两个缓存界面，不刷就等于没改）----
    private static int macroUiRefreshes;                        // 热键栏刷新次数（诊断）
    private static int macroPanelFixes;                         // 宏面板槽重刷次数（诊断）

// ---- ★v10/v11：让图标「一直显示、不用点」与「标记行不被执行」的五件事 ----
    private static int macroPanelScans;                         // 面板对齐扫描轮数（诊断）
    private static long lastFastMacroCheckTick;                 // 快通道节流（只查选中的那一条宏）
    private static int fastMacroHits;                           // 快通道命中并写入次数（诊断）
    private static long lastMacroOpenTick;                      // AgentMacro.OpenMacro 节流（防抖/防重入）
    private static int macroRunGuardHits;                       // 「宏正在执行 → 静默让行」次数（诊断）

// ---- 宏面板槽清单（哪些槽要显示我们的编号）----
    private static readonly Dictionary<int, uint> macroIconSlots = new Dictionary<int, uint>();
    private static long lastPanelSyncTick;                      // 面板检查节流（250ms 检查一拍，不等于写一拍）

// ---- 图标替换规则（原编号 → 目标编号）----
    private const int IconRuleMax = 512;                        // 规则条数上限（防手滑灌爆配置）
    private static readonly ConcurrentDictionary<uint, uint> iconRuleMap = new();  // From → To 运行时索引
    private static readonly ConcurrentDictionary<IntPtr, IconSlotPatch> iconSlotPatches = new();
    private static int iconRuleSwaps;                           // 规则命中并贴上图的次数（诊断）
    private static int iconRuleRestores;                        // 还原尝试成功的次数（诊断）
    private static long lastIconPatchPruneTick;                 // 槽记录回收节流（诊断/内存卫生）
    private static string iconRuleState = "0 条";               // 规则状态（面板显示）
    private string ruleFromInput = "";                          // 面板输入框：原编号
    private string ruleToInput = "";                            // 面板输入框：目标编号

// 一条「被我们改过的贴图槽」的记录，只为还原而存在。
    private sealed class IconSlotPatch
    {
        public uint From;          // 触发这条替换的原编号（还原时按它重载原生贴图）
        public uint To;            // 换成的目标编号
        public IntPtr Kernel;      // 写进槽的内核贴图地址（0 = 没走内核贴图）
        public long LastSeen;      // 上次出现在扫描/拦截里的时间（过期记录回收用）
    }

    // ---- 状态开关 / 后台任务 ----
    private volatile bool moduleActive;                  // 模块是否处于启用状态（心跳据此收手）
    private volatile bool busy;
    private volatile string busyLabel = "";
    private volatile FetchResult? pendingFetch;

    private static readonly HttpClient Http = CreateHttpClient(false, "");

    // ------------------------------ 构造 / 生命周期 ------------------------------

    public ToolbarIconPlus()
    {
        LoadOwnConfig();
        try { Directory.CreateDirectory(IconCacheDir); } catch { /* 忽略 */ }
    }

    protected override void OnEnable()
    {
        try
        {
            Directory.CreateDirectory(IconCacheDir);
            LoadOwnConfig();
            lastBrowserEnsureTick = 0;
            selfRef = this;
            moduleActive = true;
            InstallDetours();               // 挂 detour：内置文件图标通道（搜索 / 渲染 / 指令收纳 / 宏图标）
            RegisterMacroIconCommand();     // 注册宏内可用的命令：给宏设图库图标
            InstallMacroIconHook();         // 挂原生 hook：图标加载那一刻把贴图槽换成我们的图
            InstallMacroTextureHook();      // ★v22 贴图层主通道：让游戏拿保留段编号也能"加载成功"
            InstallMacroExecHook();         // 挂原生 hook：宏开始执行时直接拿到宏行（通道三）
            EnsureBrowserInjection(true);   // 启用即尝试注入（浏览器实例没就绪会自动跳过）
            EnsureFrameworkTick();          // 挂每帧心跳：面板关着也能维持注入、并驱动宏图标扫描
            Log("模块已启用");
        }
        catch (Exception e)
        {
            Log("OnEnable 异常: " + e.Message, true);
        }
    }

    protected override void OnDisable()
    {
        try
        {
            moduleActive = false;
            pendingFetch = null;
            busy = false;
            SaveOwnConfig();
            UnhookFrameworkTick();
            // ★ 反注入要最先做：趁模块还活着把宿主替我们持着的 wrap 引用撤走，
            //   否则重载空窗期宿主照常绘制，访问已释放的 wrap 直接崩掉宿主 UI。
            UninstallBrowserInjection();
            // ★v22：先撤贴图层 hook 再还原槽 —— 否则还原完下一帧它又会把「保留段编号」换成我们的图。
            UninstallMacroTextureHook();
            // ★ 必须先把我们改过的贴图槽还原干净再撤 hook / detour ——
            //   撤完之后我们持有的内核贴图 wrap 会被回收，留在槽里的指针就是悬空指针（会崩）。
            RestoreAllIconPatches();
            UnregisterMacroIconCommand();
            UninstallMacroIconHook();
            UninstallMacroExecHook();
            UninstallDetours();
        }
        catch { /* 忽略 */ }
    }

    protected override void OnDispose()
    {
        try
        {
            moduleActive = false;
            pendingFetch = null;
            busy = false;
            UnhookFrameworkTick();
            UninstallBrowserInjection();    // 同 OnDisable：先把宿主引用撤走（见 OnDisable 注释）
            UninstallMacroTextureHook();    // ★v22：先撤贴图层 hook 再还原槽（原因同 OnDisable）
            RestoreAllIconPatches();
            UnregisterMacroIconCommand();
            UninstallMacroIconHook();
            UninstallMacroExecHook();
            UninstallDetours();
        }
        catch { /* 忽略 */ }
    }

    // ------------------------------ 配置自持久化 ------------------------------

    private void LoadOwnConfig()
    {
        try
        {
            var path = OwnConfigPath;
            if (!File.Exists(path)) return;
            var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(path, Encoding.UTF8));
            if (loaded != null) config = loaded;
            // 编号空间先自检/修复，再重建规则索引：detour 与各种缓存都是按编号索引的，
            // 编号一旦重复或复用，就会出现「换了图还是显示旧图」这种查不出原因的怪事。
            NormalizeIconIdSpace();
            // 配置进内存后立刻重建规则索引 —— detour 是静态方法，只能读静态的 iconRuleMap
            RebuildIconRuleMap();
        }
        catch { /* 损坏则用默认 */ }
    }

    private void SaveOwnConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(OwnConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(OwnConfigPath,
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* 静默 */ }
    }

// 把配置里的规则表重建成运行时索引（detour 是静态方法，只读这个字典，不碰 config）。
    private void RebuildIconRuleMap()
    {
        try
        {
            iconRuleMap.Clear();
            var n = 0;
            foreach (var r in config.IconReplaceRules)
            {
                if (r == null || r.From == 0 || r.To == 0) continue;
                if (InReservedRange(r.From)) continue;
                iconRuleMap[r.From] = r.To;
                n++;
            }
            iconRuleState = n + " 条";
        }
        catch { iconRuleState = "重建失败"; }
    }

// 设置面板

    public override bool HasSettings => true;

    public override bool DrawSettings()
    {
        var changed = false;
        try
        {
            changed = DrawSettingsCore();
        }
        catch (Exception e)
        {
            Log("DrawSettings 异常: " + e, true);
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), "界面异常：" + e.Message);
        }
        return changed;
    }

    private bool DrawSettingsCore()
    {
        var changed = false;

        // ---- 后台抓取结果回填（主线程） ----
        var fetched = pendingFetch;
        if (fetched != null)
        {
            pendingFetch = null;
            busy = false;
            if (fetched.Ok)
            {
                if (uint.TryParse(fetched.Key, out var fid))
                {
                    var entry = config.BrowserImages.FirstOrDefault(x => x.Id == fid);
                    if (entry != null)
                    {
                        entry.Path = fetched.Path;
                        if (entry.Name.Length == 0) entry.Name = fetched.SourceUrl;
                        // ★ 编号没变、文件变了 —— 必须作废缓存，否则浏览器/宏图标还是旧图
                        InvalidateIconCaches(entry.Id);
                        SaveOwnConfig();
                        EnsureBrowserInjection(true);
                        SetStatus("favicon 已入库：" + fetched.Path, false);
                    }
                    else
                    {
                        // 条目在抓取期间被删了 → 直接当新条目入库
                        AddBrowserImage(fetched.Path, fetched.SourceUrl, fetched.SourceUrl);
                    }
                }
                else
                {
                    AddBrowserImage(fetched.Path, fetched.SourceUrl, fetched.SourceUrl);
                }
            }
            else
            {
                SetStatus("favicon 抓取失败：" + fetched.Message, true);
            }
        }

        // ---- 宿主图标浏览器注入保活（节流在函数内部） ----
        EnsureBrowserInjection(false);

        // ---- 说明 / 状态 ----
        if (busy)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "处理中：" + busyLabel);
        else if (statusLine.Length > 0)
            ImGui.TextColored(statusIsError ? new Vector4(1f, 0.45f, 0.45f, 1f) : new Vector4(0.5f, 0.95f, 0.6f, 1f), statusLine);

        ImGui.Spacing();
        ImGui.Separator();

        // ---- 图标库列表（每行：序号 + 浏览器编号 + 来源名 + 刷新 + 删除） ----
        var n = 0;
        for (var i = 0; i < config.BrowserImages.Count; i++)
        {
            var b = config.BrowserImages[i];
            n++;
            var remove = false;

            ImGui.PushID("libimg_" + b.Id);
            ImGui.TextUnformatted("#" + n);
            ImGui.SameLine();

            // 行内只显示编号，完整来源收进悬停提示
            ImGui.TextUnformatted($"ID {b.Id}");
            if (ImGui.IsItemHovered())
            {
                var tip = b.SourceUrl.Length > 0 ? "来源网址：" + b.SourceUrl : "来源文件：" + b.Path;
                if (!File.Exists(b.Path) && b.Path.Length > 0)
                    tip += "\n⚠ 文件缺失，浏览器里不会显示";
                ImGui.SetTooltip(tip);
            }
            ImGui.SameLine();

            if (b.SourceUrl.Length > 0 && b.Path.Length == 0)
            {
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "正在抓取…");
                ImGui.SameLine();
            }
            else if (File.Exists(b.Path))
            {
                ImGui.TextDisabled(b.Name.Length > 0 ? b.Name : Path.GetFileName(b.Path));
                ImGui.SameLine();
            }
            else if (b.Path.Length > 0)
            {
                ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), "文件缺失");
                ImGui.SameLine();
            }

            if (ImGui.SmallButton("刷新")) RefreshEntry(b);

            ImGui.SameLine();
            if (ImGui.SmallButton("删除")) remove = true;
            ImGui.PopID();

            if (remove)
            {
                var goneId = b.Id;
                // 先把这条编号在各处缓存里的痕迹清掉：回收站在浏览器里、我们自己的内核贴图，
                // 不清就会出现「编号还在、图已经没了」的僵尸格子。
                InvalidateIconCaches(goneId);
                config.BrowserImages.RemoveAt(i);
                SaveOwnConfig();
                SetStatus("已删除该图标（编号 " + goneId + " 不会再被新图占用）", false);
                EnsureBrowserInjection(true);
                changed = true;
                i--;    // 补回索引，下一轮不跳元素
            }
        }

        if (config.BrowserImages.Count == 0)
            ImGui.TextDisabled("图标库为空");

        // ---- 底部新增行 ----
        ImGui.Spacing();
        var newBuf = newInput;
        ImGui.PushItemWidth(-170);
        if (ImGui.InputTextWithHint("##newsrc", "输入网站网址（如 www.baidu.com）或本地图片完整路径", ref newBuf, 1024))
            newInput = newBuf;
        ImGui.PopItemWidth();
        ImGui.SameLine();
        if (ImGui.SmallButton("添加")) TryAddFromInput(newInput);

        // ---- 工具行 ----
        ImGui.Spacing();
        if (ImGui.Button("打开图标浏览器##openbrowser"))
        {
            // 先补一次注入（宿主可能清过 tabCaches），再打开选择器 —— 一按就能验证注入了没有
            try
            {
                EnsureBrowserInjection(true);
                OpenIconBrowser(id => SetStatus(id == 0u ? "未选择图标" : $"已选择图标（编号 {id}）", false));
                SetStatus("已打开图标浏览器：请停在【第一页】，图标在最前面（左上角）", false);
            }
            catch (Exception e) { SetStatus("打开浏览器失败：" + e.Message, true); }
        }

        DrawMacroIconSection(ref changed);
        DrawIconReplaceSection(ref changed);

        return changed;
    }

    private void SetStatus(string msg, bool isErr)
    {
        statusLine = msg;
        statusIsError = isErr;
    }

// 图标库条目管理

// 分配下一个保留段 ID。★ 只往前走、绝不回收 —— 见 Config.NextIconId 的注释：
    private uint AllocId()
    {
        var id = AllocIdInternal();
        if (id == 0) SetStatus("图标库已满（" + BrowserIdCapacity + " 张，编号不会回收）", true);
        return id;
    }

    /// <summary>按水位线取一个没被占用的保留段编号，并把水位线推到它之后。</summary>
    private uint AllocIdInternal()
    {
        var id = config.NextIconId < BrowserIdBase ? BrowserIdBase : config.NextIconId;
        while (id < BrowserIdBase + BrowserIdCapacity
               && config.BrowserImages.Any(b => b != null && b.Id == id))
            id++;
        if (id >= BrowserIdBase + BrowserIdCapacity) return 0;
        config.NextIconId = id + 1;
        return id;
    }

// 编号空间自检（配置进内存后立刻跑一次）：
    private void NormalizeIconIdSpace()
    {
        try
        {
            if (config.BrowserImages.Count == 0)
            {
                if (config.NextIconId < BrowserIdBase) config.NextIconId = BrowserIdBase;
                return;
            }

            var used = new HashSet<uint>();
            foreach (var b in config.BrowserImages)
            {
                if (b == null) continue;
                if (!InReservedRange(b.Id) || !used.Add(b.Id)) b.Id = 0;   // 0 = 待重新分配
            }

            uint next = BrowserIdBase;
            foreach (var b in config.BrowserImages)
                if (b != null && b.Id >= next) next = b.Id + 1;
            if (config.NextIconId < next) config.NextIconId = next;

            foreach (var b in config.BrowserImages)
            {
                if (b == null || b.Id != 0) continue;
                b.Id = AllocIdInternal();
                if (b.Id == 0) break;      // 库满，剩下的保持 0（界面上会显示为无效条目）
            }
        }
        catch { /* 自检失败就按原样用 */ }
    }

    /// <summary>底部输入框「添加」：网址 → 抓 favicon；存在的本地路径 → 直接入库。</summary>
    private void TryAddFromInput(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) { SetStatus("请先输入网址或图片路径", true); return; }

        if (LooksLikeUrl(t))
        {
            var norm = NormalizeUrl(t);
            if (norm == null) { SetStatus("网址格式不正确", true); return; }
            if (config.BrowserImages.Any(x => x.SourceUrl == norm)) { SetStatus("该网址已在库里", false); return; }

            var id = AllocId();
            if (id == 0) return;
            var host = norm;
            try { host = new Uri(norm).Host; } catch { /* 保底用完整网址 */ }
            if (host.Length == 0) host = norm;

            config.BrowserImages.Add(new Config.BrowserImageEntry { Id = id, SourceUrl = norm, Name = host });
            SaveOwnConfig();
            newInput = "";
            busy = true;
            busyLabel = "抓取 " + host;
            StartFaviconFetch(id.ToString(), norm);
            SetStatus("开始抓取 " + host + " 的 favicon…", false);
        }
        else if (File.Exists(t))
        {
            AddBrowserImage(t, Path.GetFileNameWithoutExtension(t), "");
            newInput = "";
        }
        else
        {
            SetStatus("不是网址，也不是存在的文件：" + t, true);
        }
    }

    /// <summary>往图标库加一张本地图片（自动分配保留段 ID，重复路径只留一条）。</summary>
    private void AddBrowserImage(string path, string name, string sourceUrl)
    {
        var full = (path ?? "").Trim();
        if (!File.Exists(full)) { SetStatus("文件不存在：" + full, true); return; }
        if (config.BrowserImages.Any(b => string.Equals(b.Path, full, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("已在图标库里：" + full, false);
            return;
        }

        var id = AllocId();
        if (id == 0) return;

        var nm = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(full) : name.Trim();
        config.BrowserImages.Add(new Config.BrowserImageEntry { Id = id, Path = full, Name = nm, SourceUrl = sourceUrl ?? "" });
        SaveOwnConfig();
        EnsureBrowserInjection(true);
        SetStatus($"已加入图标库：{nm}（ID {id}）", false);
    }

    /// <summary>行内「刷新」：条目带来源网址就重新抓 favicon，否则校验本地文件并重新注入。</summary>
    private void RefreshEntry(Config.BrowserImageEntry b)
    {
        if (b.SourceUrl.Length > 0)
        {
            if (busy) { SetStatus("已有抓取任务在进行中", true); return; }
            busy = true;
            busyLabel = "抓取 " + b.SourceUrl;
            StartFaviconFetch(b.Id.ToString(), b.SourceUrl);
            return;
        }

        var t = b.Path.Trim();
        if (t.Length == 0) { SetStatus("该条目没有来源，请删除后重新添加", true); return; }
        if (!File.Exists(t)) { SetStatus("文件不存在：" + t, true); return; }
        // 本地文件也可能被换过（用户把同一路径的文件替换了）→ 作废缓存再注入
        InvalidateIconCaches(b.Id);
        EnsureBrowserInjection(true);
        SetStatus("已重新注入：" + t, false);
    }

// 宿主图标浏览器注入（把本地图片挂进浏览器的「本地图标」页）

// 把图标库注入宿主的 IconBrowser：
    private void EnsureBrowserInjection(bool force)
    {
        try
        {
            if (config.BrowserImages.Count == 0)
            {
                browserState = "";
                return;
            }

            if (force) detourWrapCache.Clear();   // 添加/删除/刷新后让 detour 的贴图缓存失效

            var now = Environment.TickCount64;
            if (!force && now - lastBrowserEnsureTick < 2000) return;
            lastBrowserEnsureTick = now;

            var t = FindLoadedType("IconBrowser");
            if (t == null) { browserState = "未找到 IconBrowser 类型"; return; }

            var ids = config.BrowserImages
                .Where(b => b.Id >= BrowserIdBase && b.Id < BrowserIdBase + BrowserIdCapacity && File.Exists(b.Path))
                .Select(b => (int)b.Id).Distinct().ToList();
            if (ids.Count == 0) { browserState = "图标库没有有效条目（检查文件是否存在）"; return; }

            var dictField = t.GetField("gameIconTextures", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var cacheField = t.GetField("tabCaches", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var isOpenField = t.GetField("<IsOpen>k__BackingField",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var instList = GetBrowserInstances().ToList();

            // 注入无需避让「浏览器开着」：宿主 UI 与心跳同在主线程，我们只是在下一次绘制前补列表，
            // 不会与宿主枚举并发；避让反而会在宿主重建 tabCaches（TTL 到期 / 换页）后丢图标。

            // ---- ② 贴图加载入口（缓存反射结果，避免每 2 秒扫程序集） ----
            if (texProviderCache == null || getFromFileCache == null)
            {
                EnsureTexProvider();
            }
            var texProv = texProviderCache;
            var getFromFile = getFromFileCache;

            // ---- ③ 收藏页定位：「收藏」= GameIconTabs 里 LabelKey 含 "Featured" 的那一页
            //      （宿主 1.1.6.3 没有 FavoriteItems，收藏就是一个普通页签，当前版本为页 0）----
            var favTabIndex = FindFavoriteTabIndex(t);
            if (favTabIndex < 0) favTabIndex = 0;   // 找不到 label 就按当前版本事实兜底：页 0

            // ---- ④ 逐浏览器实例：填贴图字典（只收活 wrap）+ 插收藏页 + 清其它页残留 ----
            int instTotal = 0, favHit = 0, favAdded = 0, instDictOk = 0, cleanTabs = 0;
            var detail = new StringBuilder();

            foreach (var inst in instList)
            {
                instTotal++;
                try
                {
                    int dictHit = 0;
                    if (dictField != null && getFromFile != null && texProv != null
                        && dictField.GetValue(inst) is IDictionary texDict)
                    {
                        foreach (var b in config.BrowserImages)
                        {
                            if (b.Id < BrowserIdBase || b.Id >= BrowserIdBase + BrowserIdCapacity) continue;
                            if (!File.Exists(b.Path)) continue;
                            object? w = null;
                            try { w = getFromFile.Invoke(texProv, new object[] { b.Path }); }
                            catch { /* 单张失败不影响其它 */ }
                            // ★只放活 wrap 进字典★：UnknownTextureWrap（加载失败的占位品，会随实例
                            // 释放）和已释放的 wrap，宿主一画就抛 ObjectDisposedException —— 崩 Omni 的元凶
                            if (IsWrapAlive(w)) { texDict[(int)b.Id] = w; dictHit++; }
                            else texDict.Remove((int)b.Id);
                        }

                        // ★ 删掉那些已经不存在的编号：字典与列表都是按编号索引的，
                        //   留着旧编号 = 浏览器里一直挂着一张指向已删图片的空格子。
                        var stale = new List<object>();
                        foreach (var key in texDict.Keys)
                            if (key is int ki && InReservedRange((uint)ki) && !ids.Contains(ki)) stale.Add(key);
                        foreach (var key in stale) texDict.Remove(key);

                        if (dictHit > 0) instDictOk++;
                    }
                    else
                    {
                        detail.Append($"[#{instTotal} 字典不可用]");
                    }

// ---- 收藏页：把保留段编号插进「收藏」页签的 Icons 列表（List<int>）----
                    if (cacheField != null
                        && cacheField.GetValue(inst) is IDictionary favCaches
                        && favCaches.Contains(favTabIndex)
                        && GetProp(favCaches[favTabIndex], "Icons") is IList favIcons)
                    {
                        var inPlace = favIcons.Count >= ids.Count;
                        if (inPlace)
                            for (var k = 0; k < ids.Count; k++)
                                if (!Equals(favIcons[k], ids[k])) { inPlace = false; break; }

                        if (!inPlace)
                        {
                            foreach (var id in ids)
                                while (favIcons.Contains(id)) favIcons.Remove(id);
                            for (var k = ids.Count - 1; k >= 0; k--)
                            {
                                favIcons.Insert(0, ids[k]);
                                favAdded++;
                            }
                        }
                        favHit++;
                    }
                    else
                    {
                        detail.Append($"[#{instTotal} 收藏页缓存未就绪(页{favTabIndex})]");
                    }

                    // ---- 其它页签清残留：旧版本曾把保留段编号插进所有页签，逐页剔出去（收藏页除外）----
                    if (cacheField?.GetValue(inst) is IDictionary caches)
                    {
                        foreach (var cacheKey in caches.Keys)
                        {
                            if (favTabIndex >= 0 && Equals(cacheKey, favTabIndex)) continue;
                            var cache = caches[cacheKey];
                            if (cache == null) continue;
                            if (!(GetProp(cache, "Icons") is IList iconList)) continue;
                            var removedCount = 0;
                            for (var k = iconList.Count - 1; k >= 0; k--)
                                if (iconList[k] is int vi && InReservedRange((uint)vi))
                                {
                                    iconList.RemoveAt(k);
                                    removedCount++;
                                }
                            if (removedCount > 0)
                            {
                                cleanTabs++;
                                detail.Append($"[页{cacheKey} 清残留 {removedCount}]");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    detail.Append($"[#{instTotal} 失败:{ex.GetType().Name}:{ex.Message}]");
                    Log("浏览器实例注入失败: " + ex.Message, true);
                }
            }

            browserState = $"已注入 {ids.Count} 张图 → 宿主「收藏」页(页{favTabIndex})"
                         + $" · 实例 {instTotal} 个 / 收藏可写 {favHit} 个（新增 {favAdded}）"
                         + " / 字典可用 " + instDictOk + " 个 / 清页签残留 " + cleanTabs + " 页"
                         + "\n" + detourState + " · 贴图源：" + texProviderState;

            // 把「ID → 名称」塞进宿主搜索字典（按名字搜得到我们）；
            // 名称注入本身无害，不必等 detour 挂上
            foreach (var inst in instList) InjectSearchNames(inst);

            // 诊断串只在变化时写日志，避免 2 秒一条刷屏
            var selfBrowser = GetFieldValue(this, "<HostIconBrowser>k__BackingField");
            var anyOpen = false;
            if (isOpenField != null)
            {
                foreach (var inst in instList)
                {
                    try { if (isOpenField.GetValue(inst) is bool ob && ob) { anyOpen = true; break; } }
                    catch { /* 忽略 */ }
                }
            }
            var diag = $"self={(selfBrowser == null ? "空" : "就绪")} 实例={instTotal} 收藏={favHit}(新增{favAdded}) "
                     + $"清残留={cleanTabs}页 字典={instDictOk} 浏览器开着={anyOpen}";
            if (diag != lastInjectionDiag || force)
            {
                lastInjectionDiag = diag;
                Log($"注入诊断：{diag} | {detail}");
                if (selfBrowser == null)
                    Log("本模块 HostIconBrowser 为空 —— 宿主未把浏览器注入给本模块，需要先在本模块打开一次图标浏览器或换其它入口", true);
            }
        }
        catch (Exception ex)
        {
            browserState = "注入失败：" + ex.Message;
            Log("EnsureBrowserInjection 异常: " + ex, true);
        }
    }

// 在 IconBrowser.GameIconTabs（静态页签定义表）里找 LabelKey 含 "Featured" 的下标 ——
    private static int FindFavoriteTabIndex(Type ibType)
    {
        try
        {
            var fld = ibType.GetField("GameIconTabs",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (fld?.GetValue(null) is System.Collections.IEnumerable tabs && !(tabs is string))
            {
                var idx = 0;
                foreach (var def in tabs)
                {
                    if (def != null)
                    {
                        var label = def.GetType().GetProperty("LabelKey")?.GetValue(def, null) as string ?? "";
                        if (label.IndexOf("Featured", StringComparison.OrdinalIgnoreCase) >= 0) return idx;
                    }
                    idx++;
                }
            }
        }
        catch { /* 宿主结构变化时走调用方兜底 */ }
        return -1;
    }

// 停用/卸载时**反注入**：把宿主还替我们持着的引用全部撤走 ——
    private void UninstallBrowserInjection()
    {
        try
        {
            var t = FindLoadedType("IconBrowser");
            if (t == null) return;

            var dictField = t.GetField("gameIconTextures", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var cacheField = t.GetField("tabCaches", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var removed = 0;
            foreach (var inst in GetBrowserInstances())
            {
                try
                {
                    if (dictField?.GetValue(inst) is IDictionary texDict)
                    {
                        var stale = new List<object>();
                        foreach (var key in texDict.Keys)
                            if (key is int ki && InReservedRange((uint)ki)) stale.Add(key);
                        foreach (var key in stale) { texDict.Remove(key); removed++; }
                    }

                    if (cacheField?.GetValue(inst) is IDictionary caches)
                    {
                        foreach (var cacheKey in caches.Keys)
                        {
                            var cache = caches[cacheKey];
                            if (cache == null || !(GetProp(cache, "Icons") is IList iconList)) continue;
                            for (var k = iconList.Count - 1; k >= 0; k--)
                                if (iconList[k] is int vi && InReservedRange((uint)vi))
                                {
                                    iconList.RemoveAt(k);
                                    removed++;
                                }
                        }
                    }

                }
                catch { /* 单实例失败不影响其它 */ }
            }
            detourWrapCache.Clear();
            Log("浏览器反注入完成：清除引用 " + removed + " 处");
        }
        catch (Exception ex)
        {
            Log("UninstallBrowserInjection 异常: " + ex.Message, true);
        }
    }

    /// <summary>收集所有能拿到的 IconBrowser 实例（自己的 + 其它模块的）。</summary>
    private IEnumerable<object> GetBrowserInstances()
    {
        var list = new List<object>();
        TryAddBrowserInstance(this, list);
        try
        {
            var mgr = FindTreeHouseManager();
            if (mgr != null && GetProp(mgr, "Modules") is IEnumerable mods)
            {
                foreach (var m in mods)
                    if (m != null) TryAddBrowserInstance(m, list);
            }
        }
        catch { /* 管理器不可达就算了 */ }
        return list;
    }

    private static void TryAddBrowserInstance(object module, List<object> list)
    {
        try
        {
            var v = GetFieldValue(module, "<HostIconBrowser>k__BackingField");
            if (v != null && !list.Any(x => ReferenceEquals(x, v))) list.Add(v);
        }
        catch { /* 忽略 */ }
    }

    // ---- 每帧心跳：让注入与缓存续期不依赖「本模块面板是否打开」 ----

// 订阅 Dalamud 的 IFramework.Update（宿主未提供每帧回调，只能靠它）。
    private void EnsureFrameworkTick()
    {
        if (frameworkTickDelegate != null) return;
        try
        {
            // ① 首选宿主访问器（OmniToolbox.Host.DalamudServices.Framework，强类型 IFramework）
            object? fw = DalamudServices.Framework;
            // ② 退到 Dalamud.Service<IFramework>
            if (fw == null)
            {
                var fwType = FindAnyLoadedType("IFramework");
                if (fwType == null) { Log("未找到 IFramework：保活仅在面板打开时进行"); return; }
                fw = GetDalamudService(fwType);
            }
            if (fw == null) { Log("IFramework 服务取不到：保活仅在面板打开时进行"); return; }

            // 事件要从接口类型上找：宿主给的实例可能是显式实现的内部类，按实例类型查会查不到
            var ev = typeof(IFramework).GetEvent("Update", BindingFlags.Instance | BindingFlags.Public)
                     ?? fw.GetType().GetEvent("Update", BindingFlags.Instance | BindingFlags.Public);
            var mi = typeof(ToolbarIconPlus).GetMethod(nameof(OnFrameworkTick),
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (ev == null || mi == null) { Log("IFramework.Update 事件不可用：保活仅在面板打开时进行"); return; }

            var del = Delegate.CreateDelegate(ev.EventHandlerType, this, mi);
            ev.AddEventHandler(fw, del);
            frameworkTickDelegate = del;
            Log("已挂上每帧心跳：注入保活与宏图标扫描不再依赖面板打开");
        }
        catch (Exception ex)
        {
            Log("挂每帧心跳失败（不影响其它功能）：" + ex.Message, true);
        }
    }

    private void UnhookFrameworkTick()
    {
            if (frameworkTickDelegate == null) return;
            try
            {
                object? fw = DalamudServices.Framework;
                if (fw == null)
                {
                    var fwType = FindAnyLoadedType("IFramework");
                    fw = fwType == null ? null : GetDalamudService(fwType);
                }
                var ev = typeof(IFramework).GetEvent("Update", BindingFlags.Instance | BindingFlags.Public)
                         ?? fw?.GetType().GetEvent("Update", BindingFlags.Instance | BindingFlags.Public);
                if (fw != null && ev != null) ev.RemoveEventHandler(fw, frameworkTickDelegate);
            }
            catch { /* 忽略 */ }
            finally { frameworkTickDelegate = null; }
    }

    /// <summary>每帧回调（框架线程）：维持注入、驱动宏图标扫描，与 UI 同一主线程，可安全补列表。</summary>
    private void OnFrameworkTick(IFramework framework)
    {
        try
        {
            if (!moduleActive) return;      // 已停用：别再挂 detour / 注入

            // detour 没挂齐就偶尔重试（宿主是异步初始化的；已挂的不会重复挂）
            if (detourPatched.Count < DetourTargetCount
                && Environment.TickCount64 - lastDetourRetryTick > 15000)
                InstallDetours();

            // 原生 hook 没挂上（宿主服务晚就绪）也定期重试
            if (macroIconLoadHook == null
                && Environment.TickCount64 - lastDetourRetryTick > 15000)
                InstallMacroIconHook();

            // ★v22：贴图层主通道同样定期重试 —— 它一挂上，组件级就不必再做"事后补救"。
            if (macroTexHook == null
                && Environment.TickCount64 - lastDetourRetryTick > 15000)
                InstallMacroTextureHook();

            // 宏执行 hook 没挂上同样定期重试
            if (macroExecHook == null
                && Environment.TickCount64 - lastDetourRetryTick > 15000)
                InstallMacroExecHook();

            EnsureBrowserInjection(false);
            TickMacroIconSwap();            // 宏 / 热键栏的贴图槽替换（自带节流）
            PollMacroCommandLine();         // 宏内指令兜底通道：宿主不分发宏行时我们自己接（见下方注释）
            TickMacroIconFast();            // ★v10★ 150ms 快通道：只查「面板里选中的那条宏」，敲完即落位
            TickPanelIconSync();            // ★v11★ 250ms 面板重刷：对「含指令的槽」无条件重跑刷新例程
            TickMacroTextSweep();           // ★通道四★ 宏文本扫描：不等执行，扫到指令就写图标（见字段区注释）
        }
        catch { /* 心跳里绝不抛 */ }
    }

// detour（HarmonyX / 0Harmony）：把保留段 ID 接进宿主的「内置文件图标」通道

    private static bool InReservedRange(uint id)
        => id >= BrowserIdBase && id < BrowserIdBase + BrowserIdCapacity;

// 把 HarmonyX（0Harmony.dll）找出来并返回 HarmonyLib.Harmony 类型。
    private static Type? EnsureHarmonyType()
    {
        if (harmonyTypeCache != null) return harmonyTypeCache;

        // ① 进程里已经加载过的（别的插件先挂了 Harmony）
        foreach (var asm in SafeAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase)) continue;
            var t = asm.GetType("HarmonyLib.Harmony", false);
            if (t == null) continue;
            harmonyAsmCache = asm;
            harmonyLoadInfo = "已在进程 v" + asm.GetName().Version;
            harmonyTypeCache = t;
            return t;
        }

        // ② 交给加载上下文去解析（宿主插件 ALC 的依赖解析链往往认得 0Harmony）
        try
        {
            var asm = Assembly.Load(new AssemblyName("0Harmony"));
            var t = asm?.GetType("HarmonyLib.Harmony", false);
            if (t != null)
            {
                harmonyAsmCache = asm;
                harmonyLoadInfo = "Assembly.Load v" + asm!.GetName().Version;
                harmonyTypeCache = t;
                return t;
            }
        }
        catch (Exception e) { harmonyLoadInfo = "Assembly.Load 失败(" + e.GetType().Name + ")"; }

        // ③ 标准 Type.GetType
        try
        {
            var t = Type.GetType("HarmonyLib.Harmony, 0Harmony", false);
            if (t != null)
            {
                harmonyLoadInfo = "Type.GetType";
                harmonyTypeCache = t;
                return t;
            }
        }
        catch { /* 继续 */ }

        // ④ 从磁盘显式加载（Dalamud 的 0Harmony.dll 就在 addon/Hooks/&lt;ver&gt;/ 下）
        //    只在第一次尝试（含 2.3MB 的 DLL 读入），后续重试走前面几个便宜的路径
        if (!harmonyDiskTried)
        {
            harmonyDiskTried = true;
            foreach (var dir in HarmonySearchDirs())
            {
                try
                {
                    var path = Path.Combine(dir, "0Harmony.dll");
                    if (!File.Exists(path)) continue;
                    var asm = Assembly.LoadFrom(path);
                    var t = asm?.GetType("HarmonyLib.Harmony", false);
                    if (t == null) continue;
                    harmonyAsmCache = asm;
                    harmonyLoadInfo = "LoadFrom v" + asm!.GetName().Version + " @ " + dir;
                    harmonyTypeCache = t;
                    return t;
                }
                catch (Exception e)
                {
                    harmonyLoadInfo = "LoadFrom 失败(" + e.GetType().Name + " @ " + dir + ")";
                }
            }
        }

        if (string.IsNullOrEmpty(harmonyLoadInfo) || !harmonyLoadInfo.StartsWith("全策略", StringComparison.Ordinal))
            harmonyLoadInfo = "全策略失败：" + harmonyLoadInfo;
        return null;
    }

    /// <summary>取已加载程序集列表（个别程序集读名字会抛，逐个吞掉）。</summary>
    private static IEnumerable<System.Reflection.Assembly> SafeAssemblies()
    {
        System.Reflection.Assembly[] arr;
        try { arr = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return new System.Reflection.Assembly[0]; }
        return arr;
    }

    /// <summary>0Harmony.dll 的候选目录（去重，按命中概率排序）。</summary>
    private static IEnumerable<string> HarmonySearchDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        // a) ImGui 绑定程序集所在目录（必定已加载，且和 0Harmony.dll 同目录）
        try
        {
            var d = Path.GetDirectoryName(typeof(ImGui).Assembly.Location);
            if (!string.IsNullOrEmpty(d) && seen.Add(d!)) list.Add(d!);
        }
        catch { /* 忽略 */ }

        // b) 进程里 Dalamud* / OmenTools / OmniToolbox.Common 的所在目录
        try
        {
            foreach (var a in SafeAssemblies())
            {
                string? loc = null;
                try { loc = a.Location; } catch { /* 动态程序集没有 Location */ }
                if (string.IsNullOrEmpty(loc)) continue;
                var d = Path.GetDirectoryName(loc);
                if (string.IsNullOrEmpty(d)) continue;
                var n = a.GetName().Name ?? "";
                var hit = n.StartsWith("Dalamud", StringComparison.OrdinalIgnoreCase)
                          || n.Equals("OmenTools", StringComparison.OrdinalIgnoreCase)
                          || n.Equals("OmniToolbox.Common", StringComparison.OrdinalIgnoreCase);
                if (hit && seen.Add(d!)) list.Add(d!);
            }
        }
        catch { /* 忽略 */ }

        // c) 当前 AppDomain 基础目录
        try
        {
            var d = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(d) && seen.Add(d!)) list.Add(d!);
        }
        catch { /* 忽略 */ }

        // d) %APPDATA%\XIVLauncherCN\addon\Hooks\*（dev 优先，其余按时间倒序）
        try
        {
            var root = Path.Combine(AppDataDir, "XIVLauncherCN", "addon", "Hooks");
            if (Directory.Exists(root))
            {
                var dirs = Directory.GetDirectories(root)
                    .OrderByDescending(x => string.Equals(Path.GetFileName(x), "dev", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(x => { try { return Directory.GetLastWriteTimeUtc(x); } catch { return DateTime.MinValue; } });
                foreach (var d in dirs)
                    if (seen.Add(d)) list.Add(d);
            }
        }
        catch { /* 忽略 */ }

        return list;
    }

    /// <summary>OmenTools 的 ImageHelper（查游戏图标贴图的入口，宏图标等会用到）。</summary>
    private static Type? FindImageHelperType()
    {
        foreach (var asm in SafeAssemblies())
        {
            try
            {
                var t = asm.GetType("OmenTools.OmenService.ImageHelper", false);
                if (t != null) return t;
            }
            catch { /* 忽略 */ }
        }
        return FindAnyLoadedType("ImageHelper");
    }

    private void InstallDetours()
    {
        selfRef = this;
        if (detourPatched.Count >= DetourTargetCount) return;

        // 重试节流：宿主是异步初始化的，可能第一次调用时 OmenTools / IconBrowser 还没就绪
        var nowTick = Environment.TickCount64;
        if (detourPatched.Count > 0 && nowTick - lastDetourRetryTick < 3000) return;
        lastDetourRetryTick = nowTick;

        try
        {
            var hType = EnsureHarmonyType();
            if (hType == null)
            {
                SetDetourState("0Harmony 不可用（detour 未挂）：" + harmonyLoadInfo, true);
                return;
            }

            var hmType = hType.Assembly.GetType("HarmonyLib.HarmonyMethod");
            var ibType = typeof(ModuleBase).Assembly.GetType("OmniToolbox.UI.IconBrowser");
            if (hmType == null || ibType == null)
            {
                SetDetourState("HarmonyMethod 或 IconBrowser 类型未找到（detour 未挂）", true);
                return;
            }

            if (harmonyInstance == null)
                harmonyInstance = Activator.CreateInstance(hType, HarmonyId);
            var harmonyObj = harmonyInstance;
            if (harmonyObj == null)
            {
                SetDetourState("Harmony 实例创建失败（detour 未挂）", true);
                return;
            }

            var flags = BindingFlags.Public | BindingFlags.NonPublic
                      | BindingFlags.Instance | BindingFlags.Static;

            // ★ 主补丁：把保留段 ID 变成「宿主的内置文件图标」（搜索 + 一切渲染的根）
            var mPath = ibType.GetMethod("GetBuiltInIconPath", flags, null, new[] { typeof(uint) }, null);
            // 冗余补丁：直接接管搜索判定 / 贴图获取 / 游戏图标入口（防 JIT 内联把小方法吃掉）
            var mExists = ibType.GetMethod("IconExists", flags, null, new[] { typeof(uint) }, null);
            var mTex = ibType.GetMethod("GetIconTexture", flags, null, new[] { typeof(uint) }, null);

            MethodInfo? mGame = null;
            MethodInfo? mGameEx = null;
            var helperType = FindImageHelperType();
            if (helperType != null)
            {
                mGame = helperType.GetMethod("GetGameIcon", flags, null,
                    new[] { typeof(uint), typeof(bool) }, null);
                mGameEx = helperType.GetMethod("TryGetGameIcon", flags, null,
                    new[] { typeof(uint), typeof(IDalamudTextureWrap).MakeByRefType(), typeof(bool) }, null);
            }

            PatchOnce(hType, hmType, harmonyObj, "IconBrowser.GetBuiltInIconPath", mPath, nameof(PrefixGetBuiltInIconPath));
            PatchOnce(hType, hmType, harmonyObj, "IconBrowser.IconExists", mExists, nameof(PrefixIconExists));
            PatchOnce(hType, hmType, harmonyObj, "IconBrowser.GetIconTexture", mTex, nameof(PrefixGetIconTexture));
            PatchOnce(hType, hmType, harmonyObj, "ImageHelper.GetGameIcon", mGame, nameof(PrefixGetGameIcon));
            PatchOnce(hType, hmType, harmonyObj, "ImageHelper.TryGetGameIcon", mGameEx, nameof(PrefixTryGetGameIcon));

            detoursInstalled = detourPatched.Count > 0;
            SetDetourState("detour 已挂 " + detourPatched.Count + "/" + DetourTargetCount
                         + "（" + string.Join("·", detourPatched) + "）"
                         + " · " + harmonyLoadInfo, detourPatched.Count == 0);

            // 顺手把贴图源探测结果写进日志，便于一眼定位「图能不能画出来」
            var fwObj = GetHostService("Framework");
            EnsureTexProvider();
            Log("贴图源探测：" + texProviderState + " · 宿主访问器 Framework=" + (fwObj == null ? "空" : "就绪"));
        }
        catch (Exception ex)
        {
            SetDetourState("detour 安装失败: " + ex.GetType().Name + " " + ex.Message + " || " + ex, true);
        }
    }

    /// <summary>挂一个补丁（幂等：同一个目标只挂一次，失败的下轮重试）。</summary>
    private static void PatchOnce(Type hType, Type hmType, object harmony, string key,
                                 MethodBase? original, string prefixName)
    {
        if (original == null || detourPatched.Contains(key)) return;
        if (PatchWith(hType, hmType, harmony, original, prefixName)) detourPatched.Add(key);
    }

    /// <summary>detourState 只在变化时写日志（重试时不刷屏）。</summary>
    private static void SetDetourState(string state, bool isError)
    {
        detourState = state;
        if (string.Equals(detourLastLogged, state, StringComparison.Ordinal)) return;
        detourLastLogged = state;
        Log(state, isError);
    }

    private static bool PatchWith(Type hType, Type hmType, object harmony, MethodBase original, string prefixName)
    {
        try
        {
            var prefix = typeof(ToolbarIconPlus).GetMethod(prefixName,
                BindingFlags.NonPublic | BindingFlags.Static);
            if (prefix == null) return false;
            var hm = Activator.CreateInstance(hmType, prefix);

            // 选 Patch 的重载：第 1 参 MethodBase，其余全是 HarmonyMethod（参数最少的那个）
            var patchMi = hType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "Patch")
                .Where(m =>
                {
                    var p = m.GetParameters();
                    if (p.Length < 2) return false;
                    if (p[0].ParameterType != typeof(MethodBase)) return false;
                    foreach (var q in p.Skip(1))
                        if (q.ParameterType != hmType) return false;
                    return true;
                })
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault();
            if (patchMi == null) return false;

            var ps = patchMi.GetParameters();
            var args = new object?[ps.Length];
            args[0] = original;
            args[1] = hm;
            patchMi.Invoke(harmony, args);
            return true;
        }
        catch (Exception ex)
        {
            Log("Patch " + prefixName + " 失败: " + ex.GetType().Name + " " + ex.Message, true);
            return false;
        }
    }

    private static void UninstallDetours()
    {
        if (!detoursInstalled && harmonyInstance == null) return;
        try
        {
            var hType = harmonyTypeCache;
            if (hType != null && harmonyInstance != null)
            {
                // ★ 0Harmony 没有 UnpatchSelf（HarmonyX 2.4 只有 UnpatchAll(string)），按它调。
                var unpatchAll = hType.GetMethod("UnpatchAll",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
                if (unpatchAll != null)
                {
                    unpatchAll.Invoke(unpatchAll.IsStatic ? null : harmonyInstance, new object?[] { HarmonyId });
                    Log("detour 已解除（宿主方法还原）");
                }
                else
                {
                    Log("0Harmony 没有 UnpatchAll(string)，跳过还原（进程退出时自然消失）", true);
                }
            }
        }
        catch (Exception ex)
        {
            Log("解除 detour 失败: " + ex.Message, true);
        }
        finally
        {
            detoursInstalled = false;
            harmonyInstance = null;
            detourPatched.Clear();          // 下次启用会重新挂
            detourLastLogged = "detour 未挂";
            detourWrapCache.Clear();
            detourState = "detour 未挂";
        }
    }

    // ---- 五个 prefix：只拦保留段 ID，其余返回 true 放行原方法 ----

// ★核心★ 让 GetBuiltInIconPath(我们的 ID) 返回本地文件路径。
    private static bool PrefixGetBuiltInIconPath(uint __0, ref string __result)
    {
        if (!InReservedRange(__0)) return true;
        var path = FindPathForId(__0);
        if (path == null) return true;      // 文件不在：放行原方法（返回 null，自然降级）
        __result = path;
        return false;
    }

    /// <summary> IconExists(我们的 ID) 直接按「本地文件在不在」判定 —— 搜索框输入编号就能搜到。</summary>
    private static bool PrefixIconExists(uint __0, ref bool __result)
    {
        if (!InReservedRange(__0)) return true;
        __result = FindPathForId(__0) != null;
        return false;
    }

    /// <summary>GetIconTexture(我们的 ID) 直接给本地文件的贴图 —— 工具栏 / 指令收纳能画出我们的图。</summary>
    private static bool PrefixGetIconTexture(uint __0, ref IDalamudTextureWrap __result)
    {
        if (!InReservedRange(__0)) return true;
        var wrap = GetDetourWrap(__0) as IDalamudTextureWrap;
        if (wrap == null) return true;      // 贴图还没加载好：放行原方法，下一帧再说
        __result = wrap;
        return false;
    }

    /// <summary>ImageHelper.GetGameIcon(我们的 ID) 直接给本地文件的贴图（宏图标等直达入口）。</summary>
    private static bool PrefixGetGameIcon(uint __0, bool __1, ref IDalamudTextureWrap __result)
    {
        if (!InReservedRange(__0)) return true;
        var wrap = GetDetourWrap(__0) as IDalamudTextureWrap;
        if (wrap == null) return true;
        __result = wrap;
        return false;
    }

// ImageHelper.TryGetGameIcon(我们的 ID, ref wrap, bool)：GetGameIcon 本体只有十几字节 IL，
    private static bool PrefixTryGetGameIcon(uint __0, ref IDalamudTextureWrap __1, bool __2, ref bool __result)
    {
        if (!InReservedRange(__0)) return true;
        var wrap = GetDetourWrap(__0) as IDalamudTextureWrap;
        if (wrap == null) return true;
        __1 = wrap;
        __result = true;
        return false;
    }

    // ---- detour 的支撑方法 ----

// wrap 是否还能安全交给宿主绘制。两种「死 wrap」都必须拦下：
    private static bool IsWrapAlive(object? wrap)
    {
        if (wrap == null) return false;
        try
        {
            if (wrap.GetType().Name == "UnknownTextureWrap") return false;
            var prop = wrap.GetType().GetProperty("Handle");
            if (prop == null) return true;          // 没有 Handle 属性的类型无从验证，放行
            return prop.GetValue(wrap) != null;     // 已 Dispose 的 wrap 在这里抛 ObjectDisposedException
        }
        catch (TargetInvocationException tie) when (tie.InnerException is ObjectDisposedException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch
        {
            return true;                            // 其它反射失败不拦（宁可多画一次也不错杀）
        }
    }

// 取「我们的 ID → 本地文件贴图」。走的就是宿主自己 GetIconTexture 的内置分支：
    private static object? GetDetourWrap(uint id)
    {
        if (detourWrapCache.TryGetValue(id, out var cached))
        {
            if (IsWrapAlive(cached)) return cached;
            detourWrapCache.Remove(id);             // 缓存里的 wrap 死了 → 就地重建
        }
        object? wrap = null;
        try
        {
            var path = FindPathForId(id);
            if (path != null && EnsureTexProvider() && getFromFileCache != null && texProviderCache != null)
            {
                var shared = getFromFileCache.Invoke(texProviderCache, new object[] { path });
                if (shared != null)
                    // GetWrapOrDefault 有一个可选参数（default wrap），必须传一参 null，
                    // 传零参会 TargetParameterCountException「Parameter count mismatch」
                    wrap = shared.GetType().GetMethod("GetWrapOrDefault")?.Invoke(shared, new object?[] { null });
            }
        }
        catch (Exception ex)
        {
            Log("加载贴图失败(ID " + id + "): " + ex.Message, true);
        }
        if (IsWrapAlive(wrap)) detourWrapCache[id] = wrap;
        else detourWrapCache.Remove(id);
        return wrap;
    }

    private static bool EnsureTexProvider()
    {
        if (texProviderCache != null && getFromFileCache != null) return true;

        // ① 首选宿主的服务访问器 OmniToolbox.Host.DalamudServices.TextureProvider
        //    （宿主插件初始化时自己填的，一定可用；不用碰 Dalamud.Service<T>）
        if (texProviderCache == null)
        {
            texProviderCache = GetHostService("TextureProvider");
            if (texProviderCache != null) texProviderState = "宿主访问器";
        }

        // ② 退路：老办法（找 ITextureProvider 类型 + Dalamud.Service<T>）
        if (texProviderCache == null)
        {
            texProviderTypeCache = FindAnyLoadedType("ITextureProvider");
            if (texProviderTypeCache == null)
            {
                texProviderState = "找不到 ITextureProvider 类型";
                return false;
            }
            texProviderCache = GetDalamudService(texProviderTypeCache);
            if (texProviderCache != null) texProviderState = "Dalamud.Service";
        }

        if (texProviderCache == null)
        {
            texProviderState = "ITextureProvider 实例取不到";
            return false;
        }

        getFromFileCache = texProviderCache.GetType().GetMethods()
            .FirstOrDefault(x => x.Name == "GetFromFile"
                                 && x.GetParameters().Length == 1
                                 && x.GetParameters()[0].ParameterType == typeof(string));
        if (getFromFileCache == null)
        {
            texProviderState = "GetFromFile(string) 未找到";
            return false;
        }
        return true;
    }

// 读宿主自己的静态服务访问器 OmniToolbox.Host.DalamudServices（纯反射，不引入编译期依赖）。
    private static object? GetHostService(string propName)
    {
        try
        {
            var t = typeof(ModuleBase).Assembly.GetType("OmniToolbox.Host.DalamudServices");
            var p = t?.GetProperty(propName, BindingFlags.Public | BindingFlags.Static);
            return p?.GetValue(null);
        }
        catch { return null; }
    }

    private static string? FindPathForId(uint id)
    {
        var s = selfRef;
        if (s == null) return null;
        var entry = s.config.BrowserImages.FirstOrDefault(b => b.Id == id);
        if (entry == null) return null;
        var p = entry.Path;
        if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
        return p;
    }

// 把「ID → 名称」塞进宿主搜索用的 mapSymbolIcons（懒加载只建一次、之后不清空）。
    private void InjectSearchNames(object inst)
    {
        try
        {
            if (config.BrowserImages.Count == 0) return;
            var g = inst.GetType().GetMethod("GetMapSymbolIcons",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (g == null) return;
            var raw = g.Invoke(inst, null);
            if (!(raw is IDictionary)) return;
            var dict = (IDictionary)raw;
            foreach (var b in config.BrowserImages)
            {
                if (!InReservedRange(b.Id)) continue;
                var key = (int)b.Id;
                if (dict.Contains(key)) continue;
                var label = b.Name;
                if (string.IsNullOrEmpty(label)) label = "本地图标 " + b.Id;
                dict[key] = label;
            }
        }
        catch { /* 搜索名注入失败不影响其它功能 */ }
    }

// TreeHouseManager 发现（为拿到其它模块的浏览器实例）

    private object? FindTreeHouseManager()
    {
        if (managerCache != null) return managerCache;
        try
        {
            var now = Environment.TickCount64;
            if (!deepProbe && now - lastManagerAttemptTick < 5000) return null;
            lastManagerAttemptTick = now;

            // 策略一：本模块被宿主注入的对象（SaveHostConfig 的 Target / HostIconBrowser）→ 对象图里挖
            var pi = FindSaveHostConfigProperty(GetType());
            var act = pi?.GetValue(this) as Action;
            var found = DigManager(act?.Target, 8);
            if (found == null) found = DigManager(GetProp(this, "HostIconBrowser"), 8);
            if (found != null)
            {
                Log($"管理器已找到（来源：宿主注入对象）：{found.GetType().FullName}");
                managerCache = found;
                return found;
            }

            // 策略二：宿主可能把它放在某个静态字段里（单例）
            found = FindManagerInStatics();
            if (found != null)
            {
                Log($"管理器已找到（来源：静态字段扫描）：{found.GetType().FullName}");
                managerCache = found;
                return found;
            }

            // 策略三：Dalamud 服务容器（宿主若注册过就能直接取）
            var mgrType = FindLoadedType("TreeHouseManager");
            if (mgrType != null)
            {
                var viaService = GetDalamudService(mgrType);
                if (viaService != null && IsManagerLike(viaService))
                {
                    Log($"管理器已找到（来源：Dalamud 服务容器）：{viaService.GetType().FullName}");
                    managerCache = viaService;
                    return viaService;
                }
            }

            // 策略四（较重，仅深度探测时跑）：宿主插件实例的对象图
            //（管理器是手动 new 的、不注册进任何容器，字段名还被混淆，只能从插件实例出发遍历）。
            var deepState = "未尝试";
            if (deepProbe)
            {
                deepProbe = false;      // 一次性
                found = FindManagerViaHostPlugin();
                if (found != null)
                {
                    Log($"管理器已找到（来源：宿主插件实例对象图）：{found.GetType().FullName}");
                    managerCache = found;
                    return found;
                }
                deepState = "空";
            }

            var svcState = (mgrType == null) ? "类型未加载" : "未注册";
            Log("没找到 TreeHouseManager：宿主注入对象=空，静态扫描=空，服务容器=" + svcState
                + "，插件对象图=" + deepState, true);
        }
        catch (Exception e)
        {
            Log("FindTreeHouseManager 异常: " + e, true);
        }
        return null;
    }

    /// <summary>引用相等比较器（对象图遍历去重/防环用）。</summary>
    private sealed class RefCmp : IEqualityComparer<object>
    {
        public static readonly RefCmp Instance = new RefCmp();
        public bool Equals(object? a, object? b) { return ReferenceEquals(a, b); }
        public int GetHashCode(object o)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }

    private static readonly Dictionary<Type, bool> managerLikeCache = new Dictionary<Type, bool>();
    private static readonly Dictionary<Type, FieldInfo[]> fieldsCache = new Dictionary<Type, FieldInfo[]>();

    private static FieldInfo[] FieldsOf(Type t)
    {
        FieldInfo[] v;
        if (fieldsCache.TryGetValue(t, out v)) return v;
        try
        {
            v = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch { v = new FieldInfo[0]; }
        if (fieldsCache.Count < 8000) fieldsCache[t] = v;
        return v;
    }

    /// <summary>这些命名空间的子树对本任务没有价值，跳过能省大量时间。</summary>
    private static bool SkipNamespace(string ns)
    {
        return ns.StartsWith("System", StringComparison.Ordinal)
            || ns.StartsWith("Newtonsoft", StringComparison.Ordinal)
            || ns.StartsWith("Microsoft", StringComparison.Ordinal)
            || ns.StartsWith("ImGui", StringComparison.Ordinal)
            || ns.StartsWith("Lumina", StringComparison.Ordinal)
            || ns.StartsWith("SixLabors", StringComparison.Ordinal);
    }

    /// <summary>是不是我们要的模块管理器：类型名含 TreeHouseManager，或有「元素类型名含 Module」的 Modules 集合。</summary>
    private static bool IsManagerLike(object o)
    {
        if (o == null) return false;
        try
        {
            var t = o.GetType();
            bool cached;
            if (managerLikeCache.TryGetValue(t, out cached)) return cached;
            var ok = ComputeManagerLike(t);
            if (managerLikeCache.Count < 8000) managerLikeCache[t] = ok;
            return ok;
        }
        catch { return false; }
    }

    private static bool ComputeManagerLike(Type t)
    {
        // 宿主字段名被混淆成随机串，但类型名没动，所以这里以类型名为准
        if (t.Name.IndexOf("TreeHouseManager", StringComparison.OrdinalIgnoreCase) >= 0) return true;

        // 名字既不含 Manager 也不含 TreeHouse 的，绝大多数是噪声，直接排除（性能关键）
        if (t.Name.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) < 0
            && t.Name.IndexOf("TreeHouse", StringComparison.OrdinalIgnoreCase) < 0) return false;

        try
        {
            var p = t.GetProperty("Modules",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null || p.GetIndexParameters().Length > 0) return false;
            if (!typeof(IEnumerable).IsAssignableFrom(p.PropertyType)) return false;

            var et = p.PropertyType.IsGenericType
                ? p.PropertyType.GetGenericArguments()[0]
                : typeof(object);
            return et.Name.IndexOf("Module", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    /// <summary>字段/类型名看起来跟 TreeHouse 系统有关的，优先探索（宿主字段名虽被混淆，类型名没混淆）。</summary>
    private static bool LooksRelevant(string name)
    {
        return name.IndexOf("TreeHouse", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Router", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Tab", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>在对象图里找管理器：BFS + 引用去重 + 访问预算 + 可疑优先。</summary>
    private static object? DigManager(object? root, int maxDepth)
    {
        if (root == null) return null;
        try
        {
            var seen = new HashSet<object>(RefCmp.Instance);
            var hot = new Queue<KeyValuePair<object, int>>();
            var cold = new Queue<KeyValuePair<object, int>>();
            seen.Add(root);
            cold.Enqueue(new KeyValuePair<object, int>(root, 0));
            var budget = 40000;

            while ((hot.Count > 0 || cold.Count > 0) && budget-- > 0)
            {
                var cur = hot.Count > 0 ? hot.Dequeue() : cold.Dequeue();
                var o = cur.Key;
                var d = cur.Value;

                if (IsManagerLike(o)) return o;
                if (d >= maxDepth) continue;

                Type t;
                try { t = o.GetType(); } catch { continue; }
                if (t.IsPrimitive || t.IsEnum || t == typeof(string)) continue;
                if (SkipNamespace(t.Namespace ?? "")) continue;

                var fs = FieldsOf(t);
                for (var i = 0; i < fs.Length; i++)
                {
                    var f = fs[i];
                    if (f.IsStatic) continue;
                    object? v;
                    try { v = f.GetValue(o); } catch { continue; }
                    if (v == null || v is string) continue;
                    Type vt;
                    try { vt = v.GetType(); } catch { continue; }
                    if (vt.IsPrimitive || vt.IsEnum) continue;
                    if (!seen.Add(v)) continue;
                    var item = new KeyValuePair<object, int>(v, d + 1);
                    if (LooksRelevant(vt.Name) || LooksRelevant(f.FieldType.Name)) hot.Enqueue(item);
                    else cold.Enqueue(item);
                }
            }
        }
        catch { /* 忽略 */ }
        return null;
    }

    /// <summary>按完整类型名在所有已加载程序集里找类型。</summary>
    private static Type? FindTypeByFullName(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            catch { /* 有些程序集 GetType 会抛 */ }
        }
        return null;
    }

    /// <summary>读实例字段（含私有，沿继承链向上找）。</summary>
    private static object? GetFieldValue(object? o, string name)
    {
        if (o == null) return null;
        try
        {
            var t = o.GetType();
            while (t != null)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                        | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null)
                {
                    try { return f.GetValue(o); }
                    catch { return null; }
                }
                t = t.BaseType;
            }
        }
        catch { /* 忽略 */ }
        return null;
    }

    /// <summary>策略四：从 OmniToolbox 插件实例出发遍历对象图找管理器。</summary>
    private object? FindManagerViaHostPlugin()
    {
        try
        {
            var pmType = FindTypeByFullName("Dalamud.Plugin.Internal.PluginManager");
            if (pmType == null) { Log("策略四：找不到 PluginManager 类型"); return null; }

            var pm = GetDalamudService(pmType);
            if (pm == null) { Log("策略四：PluginManager 实例取不到"); return null; }

            var list = GetProp(pm, "InstalledPlugins") as IEnumerable;
            if (list == null) { Log("策略四：InstalledPlugins 不可读"); return null; }

            var scanned = 0;
            var names = new List<string>();
            foreach (var lp in list)
            {
                if (lp == null) continue;
                scanned++;
                var nm = ToStr(GetProp(lp, "InternalName"));
                if (nm.Length == 0) nm = ToStr(GetProp(lp, "Name"));
                if (names.Count < 6) names.Add(nm.Length > 0 ? nm : "?");
                if (nm.IndexOf("OmniToolbox", StringComparison.OrdinalIgnoreCase) < 0) continue;

                var inst = GetFieldValue(lp, "instance");
                if (inst == null) { Log("策略四：OmniToolbox 插件项找到了，但 instance 为空"); continue; }

                Log("策略四：插件实例=" + inst.GetType().FullName + "，开始遍历对象图");
                var hit = DigManager(inst, 8);
                if (hit != null) return hit;
                Log("策略四：插件实例对象图里没挖到管理器");
            }
            Log("策略四：扫了 " + scanned + " 个已加载插件（" + string.Join("/", names) + "），没找到 OmniToolbox");
        }
        catch (Exception e)
        {
            Log("策略四异常: " + e.Message, true);
        }
        return null;
    }

    /// <summary>扫所有已加载程序集的静态字段，找管理器实例（宿主若用静态单例持有它）。</summary>
    private static object? FindManagerInStatics()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var an = asm.GetName().Name ?? "";
                if (an.IndexOf("OmniToolbox", StringComparison.OrdinalIgnoreCase) < 0) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    var list = new List<Type>();
                    foreach (var x in ex.Types)
                    {
                        if (x != null) list.Add(x);
                    }
                    types = list.ToArray();
                }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    FieldInfo[] fs;
                    try
                    {
                        fs = t.GetFields(BindingFlags.Static | BindingFlags.Public
                                                       | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    }
                    catch { continue; }

                    foreach (var f in fs)
                    {
                        var ftn = f.FieldType.Name;
                        if (ftn.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) < 0
                            && ftn.IndexOf("TreeHouse", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        object? v = null;
                        try { v = f.GetValue(null); } catch { continue; }
                        if (v != null && IsManagerLike(v)) return v;
                    }
                }
            }
        }
        catch { /* 忽略 */ }
        return null;
    }

    /// <summary>按类型简单名在当前已加载的程序集里找类型。</summary>
    private static Type? FindLoadedType(string simpleName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var an = asm.GetName().Name ?? "";
                if (an.IndexOf("OmniToolbox", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var t = asm.GetType(simpleName, false);
                if (t != null) return t;
                foreach (var x in asm.GetTypes())
                    if (x.Name == simpleName) return x;
            }
            catch { continue; }
        }
        return null;
    }

    /// <summary>按简单名在所有已加载程序集里找类型（含 Dalamud 的接口）。</summary>
    private static Type? FindAnyLoadedType(string simpleName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(simpleName, false);
                if (t != null) return t;
                foreach (var x in asm.GetTypes())
                    if (x.Name == simpleName) return x;
            }
            catch { continue; }
        }
        return null;
    }

    private static PropertyInfo? FindSaveHostConfigProperty(Type t)
    {
        if (saveHostConfigPropCache != null) return saveHostConfigPropCache;
        try
        {
            // 注意：Type.GetProperty 默认不返回基类的 non-public 成员，必须逐级 DeclaredOnly
            for (var x = t; x != null; x = x.BaseType)
            {
                var pi = x.GetProperty("SaveHostConfig",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (pi != null)
                {
                    saveHostConfigPropCache = pi;
                    return pi;
                }
            }
        }
        catch { /* 忽略 */ }
        return null;
    }

    private static object? GetProp(object? o, string name)
    {
        if (o == null) return null;
        try
        {
            var t = o.GetType();
            while (t != null)
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public
                                            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                {
                    try { return p.GetValue(o); }
                    catch { return null; }
                }
                t = t.BaseType;
            }
        }
        catch { /* 忽略 */ }
        return null;
    }

    private static object? GetDalamudService(Type serviceType)
    {
        try
        {
            // 不能只按程序集名找 "Dalamud"：模块跑在独立 ALC 时可能扫不到。
            // 改成按类型全名扫 Service`1（与 FindAnyLoadedType 同一套枚举逻辑）。
            var serviceDef = FindAnyLoadedType("Service`1");
            if (serviceDef == null || serviceDef.FullName != "Dalamud.Service`1")
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var x in asm.GetTypes())
                            if (x.FullName == "Dalamud.Service`1" && x.IsGenericTypeDefinition)
                            {
                                serviceDef = x;
                                break;
                            }
                    }
                    catch { continue; }
                    if (serviceDef != null && serviceDef.FullName == "Dalamud.Service`1") break;
                }
            }
            if (serviceDef == null) return null;
            var get = serviceDef.MakeGenericType(serviceType)
                .GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                           null, Type.EmptyTypes, null);
            return get?.Invoke(null, null);
        }
        catch { return null; }
    }

    private static string ToStr(object? o)
    {
        try { return o?.ToString() ?? ""; }
        catch { return ""; }
    }

// favicon 抓取

    private sealed class FetchResult
    {
        public string Key = "";
        public bool Ok;
        public string Path = "";
        public string SourceUrl = "";
        public string Message = "";
    }

    private void StartFaviconFetch(string key, string urlInput)
    {
        var http = Http;
        if (config.UseProxy && !string.IsNullOrWhiteSpace(config.ProxyUrl))
            http = CreateHttpClient(true, config.ProxyUrl);

        _ = Task.Run(async () =>
        {
            var res = new FetchResult { Key = key, SourceUrl = urlInput.Trim() };
            try
            {
                var fetched = await FetchFaviconAsync(http, urlInput.Trim()).ConfigureAwait(false);
                if (fetched == null)
                {
                    res.Message = "没有找到可用的 favicon（站点可能没有图标）";
                }
                else
                {
                    res.Ok = true;
                    res.Path = fetched.Value.path;
                    res.SourceUrl = fetched.Value.srcUrl;
                }
            }
            catch (Exception e)
            {
                res.Message = e.Message;
            }
            pendingFetch = res;
        });
    }

    /// <summary>抓 favicon → 落盘（统一转 PNG）→ 返回本地路径。</summary>
    private async Task<(string path, string srcUrl)?> FetchFaviconAsync(HttpClient http, string input)
    {
        var url = NormalizeUrl(input);
        if (url == null) throw new Exception("网址格式不正确");

        var baseUri = new Uri(url);
        var origin = baseUri.GetLeftPart(UriPartial.Authority);
        var candidates = new List<string> { origin + "/favicon.ico" };

        // 解析页面里的 <link rel="...icon..." href="...">
        try
        {
            var html = await GetBytesLimitedAsync(http, url, 512 * 1024).ConfigureAwait(false);
            if (html != null)
            {
                foreach (var href in ExtractIconHrefs(Encoding.UTF8.GetString(html)))
                {
                    try
                    {
                        var abs = new Uri(baseUri, href).ToString();
                        if (!candidates.Contains(abs)) candidates.Add(abs);
                    }
                    catch { /* 坏链接忽略 */ }
                }
            }
        }
        catch (Exception e)
        {
            Log("解析页面图标失败（继续尝试默认路径）：" + e.Message);
        }

        candidates.Add(origin + "/favicon.png");

        var lastErr = "未知错误";
        foreach (var c in candidates)
        {
            try
            {
                var bytes = await GetBytesLimitedAsync(http, c, 2 * 1024 * 1024).ConfigureAwait(false);
                if (bytes == null || bytes.Length < 16) { lastErr = "内容为空"; continue; }
                var saved = SaveIconBytes(bytes, url);
                if (saved == null) { lastErr = "不是可识别的图片格式"; continue; }
                return (saved, c);
            }
            catch (Exception e)
            {
                lastErr = e.Message;
            }
        }

        Log($"favicon 全部候选失败：{string.Join(" | ", candidates)} → {lastErr}", true);
        return null;
    }

    private static string? NormalizeUrl(string input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith("//", StringComparison.Ordinal)) s = "https:" + s;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != "http" && uri.Scheme != "https") return null;
        return uri.ToString();
    }

    private static bool LooksLikeUrl(string s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return false;
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("//", StringComparison.Ordinal)) return true;
        if (t.Contains("://", StringComparison.Ordinal)) return true;
        // 带盘符（含 ':'）或 Windows 路径分隔符的一律按本地路径处理
        if (t.IndexOf(':') >= 0 || t.IndexOf('\\') >= 0) return false;
        if (File.Exists(t)) return false;
        // 剩下带点的裸域名（www.baidu.com 这类）当网址
        return t.IndexOf('.') >= 0;
    }

    private static async Task<byte[]?> GetBytesLimitedAsync(HttpClient http, string url, int maxBytes)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new Exception("HTTP " + (int)resp.StatusCode);

        using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var n = await stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
            if (total > maxBytes) break;
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private static List<string> ExtractIconHrefs(string html)
    {
        var result = new List<KeyValuePair<int, string>>();
        try
        {
            foreach (Match tag in Regex.Matches(html, "<link\\b[^>]*>", RegexOptions.IgnoreCase))
            {
                var text = tag.Value;
                var rel = GetAttr(text, "rel");
                if (string.IsNullOrWhiteSpace(rel)) continue;
                var relLower = rel!.ToLowerInvariant();
                if (!relLower.Contains("icon")) continue;

                var href = GetAttr(text, "href");
                if (string.IsNullOrWhiteSpace(href)) continue;

                var score = 10;
                if (relLower.Contains("apple-touch-icon")) score = 30;
                if (relLower.Contains("shortcut")) score += 1;
                if (relLower.Contains("mask-icon")) score = 1;   // svg 蒙版图标，优先级最低

                var sizes = GetAttr(text, "sizes");
                if (!string.IsNullOrWhiteSpace(sizes))
                {
                    var m = Regex.Match(sizes!, "(\\d+)x(\\d+)");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var px))
                        score += Math.Min(px, 256);
                }
                result.Add(new KeyValuePair<int, string>(score, href!));
            }
        }
        catch { /* 忽略 */ }

        return result.OrderByDescending(x => x.Key)
                     .Select(x => x.Value)
                     .Distinct()
                     .ToList();
    }

    private static string? GetAttr(string tag, string name)
    {
        var m = Regex.Match(tag, name + "\\s*=\\s*(\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        if (m.Groups[2].Success) return m.Groups[2].Value;
        if (m.Groups[3].Success) return m.Groups[3].Value;
        if (m.Groups[4].Success) return m.Groups[4].Value;
        return null;
    }

    /// <summary>把抓到的图标字节落盘（ico/bmp 转 PNG，其余原样保存）。</summary>
    private static string? SaveIconBytes(byte[] bytes, string sourceUrl)
    {
        try
        {
            Directory.CreateDirectory(IconCacheDir);
            var hash = Sha1Hex(sourceUrl);
            if (hash.Length > 16) hash = hash.Substring(0, 16);

            if (IsPng(bytes))
            {
                var p = Path.Combine(IconCacheDir, hash + ".png");
                File.WriteAllBytes(p, bytes);
                return p;
            }
            if (IsJpeg(bytes))
            {
                var p = Path.Combine(IconCacheDir, hash + ".jpg");
                File.WriteAllBytes(p, bytes);
                return p;
            }
            if (IsGif(bytes))
            {
                var p = Path.Combine(IconCacheDir, hash + ".gif");
                File.WriteAllBytes(p, bytes);
                return p;
            }
            if (IsWebp(bytes))
            {
                var p = Path.Combine(IconCacheDir, hash + ".webp");
                File.WriteAllBytes(p, bytes);
                return p;
            }

            if (TryIcoToPng(bytes, out var png) || TryBmpToPng(bytes, out png))
            {
                var p = Path.Combine(IconCacheDir, hash + ".png");
                File.WriteAllBytes(p, png);
                return p;
            }
        }
        catch { /* 忽略 */ }
        return null;
    }

    // ---- 格式识别 ----

    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
    private static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8;
    private static bool IsGif(byte[] b) => b.Length > 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46;
    private static bool IsWebp(byte[] b) => b.Length > 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50;
    private static bool IsBmp(byte[] b) => b.Length > 2 && b[0] == 0x42 && b[1] == 0x4D;

    // ---- ICO / BMP → PNG ----

    private static bool TryIcoToPng(byte[] data, out byte[] png)
    {
        png = Array.Empty<byte>();
        try
        {
            if (data.Length < 22) return false;
            if (BitConverter.ToInt16(data, 0) != 0 || BitConverter.ToInt16(data, 2) != 1) return false;
            var count = BitConverter.ToInt16(data, 4);
            if (count <= 0 || count > 64) return false;

            var bestOff = -1;
            var bestSize = 0;
            var bestScore = -1;
            for (var i = 0; i < count; i++)
            {
                var e = 6 + i * 16;
                if (e + 16 > data.Length) break;
                var w = data[e] == 0 ? 256 : data[e];
                var h = data[e + 1] == 0 ? 256 : data[e + 1];
                var size = BitConverter.ToInt32(data, e + 8);
                var off = BitConverter.ToInt32(data, e + 12);
                if (size <= 0 || off <= 0 || off + size > data.Length) continue;
                var score = w * h;
                if (score > bestScore) { bestScore = score; bestOff = off; bestSize = size; }
            }
            if (bestOff < 0 || bestSize <= 8) return false;

            var slice = new byte[bestSize];
            Buffer.BlockCopy(data, bestOff, slice, 0, bestSize);

            // ICO 里直接内嵌 PNG 的情况
            if (IsPng(slice))
            {
                png = slice;
                return true;
            }

            // 老格式：DIB（BMP 无文件头）→ 解码后重编码 PNG
            if (TryDecodeDib(slice, true, out var w2, out var h2, out var rgba))
            {
                png = EncodePngRgba(w2, h2, rgba);
                return true;
            }
        }
        catch { /* 忽略 */ }
        return false;
    }

    private static bool TryBmpToPng(byte[] data, out byte[] png)
    {
        png = Array.Empty<byte>();
        try
        {
            if (!IsBmp(data) || data.Length < 54) return false;
            var off = BitConverter.ToInt32(data, 10);
            if (off <= 0 || off >= data.Length) off = 54;
            var dib = new byte[data.Length - off];
            Buffer.BlockCopy(data, off, dib, 0, dib.Length);
            if (!TryDecodeDib(dib, false, out var w, out var h, out var rgba)) return false;
            png = EncodePngRgba(w, h, rgba);
            return true;
        }
        catch { return false; }
    }

// 解码 BITMAPINFOHEADER + 像素（32/24/8/4/1bpp），输出顶向下的 RGBA。
    private static bool TryDecodeDib(byte[] dib, bool isIco, out int width, out int height, out byte[] rgba)
    {
        width = 0;
        height = 0;
        rgba = Array.Empty<byte>();
        try
        {
            if (dib.Length < 40) return false;
            var headerSize = BitConverter.ToInt32(dib, 0);
            if (headerSize < 40 || headerSize > dib.Length) return false;

            var biWidth = BitConverter.ToInt32(dib, 4);
            var biHeight = BitConverter.ToInt32(dib, 8);
            var bitCount = BitConverter.ToInt16(dib, 14);
            var clrUsed = BitConverter.ToInt32(dib, 32);

            var absH = Math.Abs(biHeight);
            var h = isIco ? absH / 2 : absH;
            if (biWidth <= 0 || biWidth > 1024 || h <= 0 || h > 1024) return false;

            var colors = bitCount <= 8 ? (clrUsed > 0 ? clrUsed : 1 << bitCount) : 0;
            var palOff = headerSize;
            var pxOff = palOff + colors * 4;
            var stride = ((biWidth * bitCount + 31) / 32) * 4;
            if (pxOff < 0 || pxOff + stride * h > dib.Length) return false;

            rgba = new byte[biWidth * h * 4];
            var bottomUp = biHeight > 0;

            for (var y = 0; y < h; y++)
            {
                var srcY = bottomUp ? (h - 1 - y) : y;
                var row = pxOff + srcY * stride;
                var dst = y * biWidth * 4;

                if (bitCount == 32)
                {
                    for (var x = 0; x < biWidth; x++)
                    {
                        var s = row + x * 4;
                        rgba[dst + x * 4 + 0] = dib[s + 2];
                        rgba[dst + x * 4 + 1] = dib[s + 1];
                        rgba[dst + x * 4 + 2] = dib[s + 0];
                        rgba[dst + x * 4 + 3] = dib[s + 3];
                    }
                }
                else if (bitCount == 24)
                {
                    for (var x = 0; x < biWidth; x++)
                    {
                        var s = row + x * 3;
                        rgba[dst + x * 4 + 0] = dib[s + 2];
                        rgba[dst + x * 4 + 1] = dib[s + 1];
                        rgba[dst + x * 4 + 2] = dib[s + 0];
                        rgba[dst + x * 4 + 3] = 255;
                    }
                }
                else if (bitCount == 8 || bitCount == 4 || bitCount == 1)
                {
                    for (var x = 0; x < biWidth; x++)
                    {
                        int pi;
                        if (bitCount == 8) pi = dib[row + x];
                        else if (bitCount == 4)
                        {
                            var bb = dib[row + x / 2];
                            pi = (x % 2 == 0) ? (bb >> 4) : (bb & 0x0F);
                        }
                        else
                        {
                            var bb = dib[row + x / 8];
                            pi = (bb >> (7 - (x % 8))) & 1;
                        }
                        var p = palOff + pi * 4;
                        if (p + 3 >= dib.Length) return false;
                        rgba[dst + x * 4 + 0] = dib[p + 2];
                        rgba[dst + x * 4 + 1] = dib[p + 1];
                        rgba[dst + x * 4 + 2] = dib[p + 0];
                        rgba[dst + x * 4 + 3] = 255;
                    }
                }
                else return false;
            }

            // 32bpp 但 alpha 全 0（老 ICO 常见）→ 视为不透明
            if (bitCount == 32)
            {
                var anyAlpha = false;
                for (var i = 3; i < rgba.Length; i += 4)
                {
                    if (rgba[i] != 0) { anyAlpha = true; break; }
                }
                if (!anyAlpha)
                    for (var i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
            }

            width = biWidth;
            height = h;
            return true;
        }
        catch { return false; }
    }

    // ---- 极简 PNG 编码（8bit RGBA，手写 IHDR/IDAT/IEND + CRC32） ----

    private static byte[] EncodePngRgba(int width, int height, byte[] rgba)
    {
        using var outMs = new MemoryStream();
        outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

        var ihdr = new byte[13];
        WriteBe32(ihdr, 0, width);
        WriteBe32(ihdr, 4, height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // color type: RGBA
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WritePngChunk(outMs, "IHDR", ihdr);

        var stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0;   // filter type 0
            Buffer.BlockCopy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        byte[] comp;
        using (var cm = new MemoryStream())
        {
            using (var z = new ZLibStream(cm, CompressionLevel.Optimal, true))
                z.Write(raw, 0, raw.Length);
            comp = cm.ToArray();
        }

        WritePngChunk(outMs, "IDAT", comp);
        WritePngChunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    private static void WriteBe32(byte[] b, int off, int value)
    {
        b[off + 0] = (byte)((value >> 24) & 0xFF);
        b[off + 1] = (byte)((value >> 16) & 0xFF);
        b[off + 2] = (byte)((value >> 8) & 0xFF);
        b[off + 3] = (byte)(value & 0xFF);
    }

    private static void WritePngChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe32(len, 0, data.Length);
        s.Write(len, 0, 4);

        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t, 0, 4);
        s.Write(data, 0, data.Length);

        // PNG 规范：CRC 覆盖「类型 + 数据」
        var crc = Crc32(data, 0, data.Length, Crc32(t, 0, 4, 0xFFFFFFFFu));
        var crcBytes = new byte[4];
        WriteBe32(crcBytes, 0, unchecked((int)(crc ^ 0xFFFFFFFFu)));
        s.Write(crcBytes, 0, 4);
    }

    private static uint[]? crcTable;

    private static uint Crc32(byte[] data, int offset, int length, uint seed)
    {
        if (crcTable == null)
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            crcTable = table;
        }

        var crc = seed;
        for (var i = 0; i < length; i++)
            crc = crcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

// 杂项工具

    private static HttpClient CreateHttpClient(bool useProxy, string proxyUrl)
    {
        try
        {
            if (useProxy && !string.IsNullOrWhiteSpace(proxyUrl))
            {
                var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy(proxyUrl),
                    UseProxy = true,
                };
                return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            }
        }
        catch { /* 退回直连 */ }
        return new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    private static string Sha1Hex(string s)
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch
        {
            return Math.Abs(s.GetHashCode()).ToString("x8") + s.Length.ToString("x8");
        }
    }

    private static void Log(string msg, bool error = false)
    {
        try
        {
            var log = DalamudServices.PluginLog;
            if (log == null) return;
            if (error) log.Warning(Tag + " " + msg);
            else log.Info(Tag + " " + msg);
        }
        catch { /* 忽略 */ }
    }

    // ------------------------------ 诊断 ------------------------------

    private void WriteDiagnostics()
    {
        try
        {
            Log("========== 诊断开始 ==========");
            Log($"图标库条目={config.BrowserImages.Count}  UseProxy={config.UseProxy}");
            foreach (var b in config.BrowserImages)
                Log($"  #{b.Id} name={b.Name} url={b.SourceUrl} path={b.Path} 存在={File.Exists(b.Path)}");
            Log($"缓存目录：{IconCacheDir}");
            Log("browserState=" + browserState);
            Log("detour: " + detourState + " · 贴图缓存=" + detourWrapCache.Count + " 条"
                + " · 贴图源=" + texProviderState);
            Log("宏图标: 命令=" + macroIconCommandState + " · 开关=" + config.MacroIconSwap
                + " · 内核贴图 就绪=" + macroKernels.Count + " 构建中=" + macroKernelPending.Count
                + " 失败=" + macroKernelFailed.Count
                + " · 上轮扫描单元=" + macroSwapUnits + " 写入槽=" + macroSwapHits);
            Log("图标替换规则: " + iconRuleState + " · 规则换上=" + iconRuleSwaps
                + " · 已还原槽=" + iconRuleRestores + " · 记录中的槽=" + iconSlotPatches.Count
                + " · 扫描命中规则=" + macroSweepRuled);
            for (var i = 0; i < config.IconReplaceRules.Count && i < 32; i++)
            {
                var r = config.IconReplaceRules[i];
                if (r != null) Log("  规则[" + i + "] " + r.From + " → " + r.To + DescribeIconId(r.To));
            }
            Log("宏图标 hook=" + macroIconHookState + " · 拦截 " + macroIconHookCalls
                + " 次（保留段 " + macroIconHookReserved + "）/ 换上 " + macroIconHookSwaps
                + " 次 · 图标组件累计 " + macroSweepComps + "（本轮 +" + macroSweepCompsDelta + "）"
                + " 其中保留段 " + macroSweepReserved + " · 心跳=" + (frameworkTickDelegate != null ? "已挂" : "未挂"));
            Log("宏图标 诊断：" + MacroDiagText());
            if (!string.IsNullOrEmpty(macroIconKernelDiag)) Log("  最近贴图：" + macroIconKernelDiag);
            Log("Harmony: " + harmonyLoadInfo + " · 已挂=" + detoursInstalled
                + " · 程序集=" + (harmonyAsmCache == null ? "空" : (harmonyAsmCache.GetName().Name ?? "?"))
                + " · Type=" + (harmonyTypeCache == null ? "空" : (harmonyTypeCache.Assembly.FullName ?? "?")));
            try
            {
                var names = new List<string>();
                foreach (var a in SafeAssemblies())
                {
                    var n = a.GetName().Name ?? "";
                    if (n.IndexOf("harmony", StringComparison.OrdinalIgnoreCase) >= 0) names.Add(n);
                }
                Log("进程内 Harmony 相关程序集: " + (names.Count == 0 ? "无" : string.Join(", ", names)));
            }
            catch { /* 忽略 */ }

            var mgr = FindTreeHouseManager();
            Log("TreeHouseManager=" + (mgr == null ? "未找到" : mgr.GetType().FullName));

            var insts = GetBrowserInstances().ToList();
            Log("浏览器实例数=" + insts.Count);
            var ibType = FindLoadedType("IconBrowser");
            var selF2 = ibType?.GetField("selectedGameIconTab",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var cacheF2 = ibType?.GetField("tabCaches",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var dictF2 = ibType?.GetField("gameIconTextures",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var inst in insts)
            {
                Log("  实例: " + inst.GetType().FullName);
                try
                {
                    if (selF2 != null) Log("    当前停留页 selectedGameIconTab=" + (selF2.GetValue(inst)?.ToString() ?? "?"));
                    if (cacheF2?.GetValue(inst) is IDictionary cs2)
                    {
                        var sb = new StringBuilder();
                        foreach (DictionaryEntry kv in cs2)
                        {
                            var lst = GetProp(kv.Value, "Icons") as IList;
                            var cnt = lst == null ? -1 : lst.Count;
                            var has = 0;
                            if (lst != null)
                                foreach (var b in config.BrowserImages)
                                    if (lst.Contains((int)b.Id)) has++;
                            var done = GetProp(kv.Value, "IsComplete");
                            sb.Append($"[页{kv.Key} 图标{cnt} 含我们的{has} 扫完={done}]");
                        }
                        Log("    tabCaches: " + sb);
                    }
                    else Log("    tabCaches 不可读");
                    if (dictF2?.GetValue(inst) is IDictionary td2)
                    {
                        var hit = 0;
                        foreach (var b in config.BrowserImages)
                            if (td2.Contains((int)b.Id) && td2[(int)b.Id] != null) hit++;
                        Log($"    gameIconTextures 可用贴图={hit}/{config.BrowserImages.Count}");
                    }
                }
                catch (Exception ex) { Log("    读实例状态失败: " + ex.Message, true); }
            }

            Log("========== 诊断结束 ==========");
        }
        catch (Exception e)
        {
            Log("WriteDiagnostics 异常: " + e, true);
        }
    }

// v4.0：宏图标 —— 让游戏宏（含热键栏）显示图库里的自定义图标

    // ------------------------------ 命令注册 / 注销 ------------------------------

    private void RegisterMacroIconCommand()
    {
        if (macroIconCommandRegistered) return;
        try
        {
            var cm = DalamudServices.CommandManager;
            if (cm == null)
            {
                macroIconCommandState = "注册失败：CommandManager 为空";
                Log("宏图标命令 " + macroIconCommandState, true);
                return;
            }

            var info = new CommandInfo(OnMacroIconCommand)
            {
                HelpMessage = "图库宏图标：/图库图标 list 看列表；/图库图标 <名称|编号> 应用到选中的宏",
                ShowInHelp = true,
// ★v10★ 关键：宏里那一行只是「标记」，运行时必须**不执行**。
                AllowedInMacros = false,
            };

            // 中文命令 + 纯 ASCII 别名都挂上：宏内输入时 ASCII 更保险，哪个成功用哪个
            var okMain = false;
            var okAlias = false;
            try { okMain = cm.AddHandler(MacroIconCommand, info); } catch { /* 忽略 */ }
            try { okAlias = cm.AddHandler(MacroIconCommandAlias, info); } catch { /* 忽略 */ }

            macroIconCommandRegistered = okMain || okAlias;
            macroIconCommandState = (okMain ? MacroIconCommand : "×") + " / " + (okAlias ? MacroIconCommandAlias : "×");
            Log("宏图标命令注册：" + macroIconCommandState, !macroIconCommandRegistered);
        }
        catch (Exception e)
        {
            macroIconCommandState = "注册异常：" + e.GetType().Name;
            Log("宏图标命令注册异常: " + e, true);
        }
    }

    private static void UnregisterMacroIconCommand()
    {
        if (!macroIconCommandRegistered) return;
        try
        {
            var cm = DalamudServices.CommandManager;
            if (cm != null)
            {
                try { cm.RemoveHandler(MacroIconCommand); } catch { /* 忽略 */ }
                try { cm.RemoveHandler(MacroIconCommandAlias); } catch { /* 忽略 */ }
            }
        }
        catch { /* 忽略 */ }
        finally
        {
            macroIconCommandRegistered = false;
            macroIconCommandState = "已注销";
        }
    }

    private static void Chat(string msg)
    {
        // ★ 每条命令反馈必须同时进 /xllog：实机排查「命令到底触发没触发」就靠它。
        //   （第一版只打聊天栏，宏里跑了没反应时日志里什么都没有，无法定位。）
        Log(msg);
        try
        {
            var cg = DalamudServices.ChatGUI;
            if (cg != null)
            {
                cg.Print(Tag + " " + msg, null, null);
                return;
            }
        }
        catch { /* 落到日志 */ }
    }

    private static void OnMacroIconCommand(string command, string args)
    {
// ★★★ v10：宏里那一行只是「标记」——宏真正执行时必须**静默让行** ★★★
        if (IsMacroRunning())
        {
            macroRunGuardHits++;
            Log("宏正在执行：标记指令「" + command + " " + (args ?? "") + "」按设计静默让行（不应用、不提示）");
            return;
        }

// ★ 无条件入口日志：命令到底触发没触发，/xllog 第一行就见分晓。
        var line = CurrentMacroLineIndex();
        if (line >= 0)
        {
            macroRunGuardHits++;
            Log("宏内指令（宿主分发）：行" + line + "「" + command + " " + (args ?? "")
                + "」按设计忽略（标记行不在宏运行时生效）");
            return;
        }
        Log("命令已触发（聊天框）：" + command + " 参数=" + (args ?? ""));

        var s = selfRef;
        if (s == null)
        {
            // 不能无声返回：命令到底触发没触发，/xllog 里必须看得到
            Log("命令已触发但模块引用为空（未正常启用？）", true);
            return;
        }
        try { s.HandleMacroIconCommand((args ?? "").Trim()); }
        catch (Exception e)
        {
            Chat("命令出错：" + e.Message);
            Log("宏图标命令异常: " + e, true);
        }
    }

    private void HandleMacroIconCommand(string args)
    {
        if (args.Length == 0 || args == "?" || args == "h" || args == "help" || args == "帮助")
        {
            Chat("用法：" + MacroIconCommand + " list ｜ " + MacroIconCommand + " <名称或编号>"
                 + " ｜ " + MacroIconCommand + " 个人|共享 <1-100> <名称或编号>"
                 + " ｜ " + MacroIconCommand + " 宏列表（把游戏里所有非空宏的内容写进 /xllog）");
            return;
        }

        if (args == "宏列表" || args == "macros" || args == "dump")
        {
            DumpGameMacros();
            return;
        }

        if (args == "list" || args == "ls" || args == "列表")
        {
            Chat("图库共 " + config.BrowserImages.Count + " 张：");
            foreach (var b in config.BrowserImages)
                Chat("  编号 " + b.Id + " · " + (string.IsNullOrEmpty(b.Name) ? "(无名)" : b.Name)
                     + (string.IsNullOrEmpty(b.SourceUrl) ? "" : "  ← " + b.SourceUrl));
            return;
        }

        if (config.BrowserImages.Count == 0)
        {
            Chat("图库还是空的：先在模块面板里加几张图再试。");
            return;
        }

        // 可选前缀：个人 / 共享 (+ 1..100 的序号)
        var parts = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var set = 0u;
        var index = -1;
        var iconText = args;

        if (parts.Length >= 2 && IsSharedKeyword(parts[0], out var prefixSet))
        {
            set = prefixSet;
            if (!uint.TryParse(parts[1], out var idxNum) || idxNum < 1 || idxNum > 100)
            {
                Chat("序号要是 1-100 之间的数字，例如：" + MacroIconCommand + " 个人 3 鱼糕");
                return;
            }
            index = (int)idxNum - 1;
            iconText = string.Join(" ", parts.Skip(2));
        }

        if (iconText.Trim().Length == 0)
        {
            Chat("没说要用哪张图：先 " + MacroIconCommand + " list 看一下名称。");
            return;
        }

        var iconId = ResolveLibraryIcon(iconText, out var label);
        if (iconId == 0)
        {
            Chat("在图库里没找到「" + iconText.Trim() + "」：" + MacroIconCommand + " list 看可用名称。");
            return;
        }

        if (index < 0)
        {
            if (!TryGetSelectedMacro(out set, out var selIndex))
            {
                Chat("没读到宏面板里选中的宏：先打开「用户宏」窗口并选中一个宏，"
                     + "或用 " + MacroIconCommand + " 个人 3 名称 指定。");
                return;
            }
            index = (int)selIndex;
        }

        if (ApplyMacroIcon(set, (uint)index, iconId))
        {
            // 写入的同时已经把热键栏与宏面板的缓存刷掉了（见 RefreshMacroIconUsers），
            // 而且面板每帧都在做对齐（TickPanelIconSync）—— 面板开着不用重开、关着打开就是新图。
            Chat("已把" + (set == 0 ? "个人宏 " : "共享宏 ") + (index + 1)
                 + " 的图标设为「" + label + "」（编号 " + iconId + "）。"
                 + (IsMacroPanelOpen() ? "宏面板与热键栏已刷新。" : "热键栏已刷新；宏面板打开后即为新图标。"));
        }
        else
        {
            Chat("设置失败：读不到宏数据（宏模块还没就绪）。");
        }
    }

// 宏面板（Macro addon）现在是不是可见。
    private static unsafe bool IsMacroPanelOpen()
    {
        try
        {
            var agent = AgentMacro.Instance();
            if (agent == null || agent->SelectedMacroSet > 1) return false;
            var stage = AtkStage.Instance();
            if (stage == null) return false;
            var um = stage->RaptureAtkUnitManager;
            if (um == null) return false;
            var addon = (AddonMacro*)um->GetAddonByName("Macro", 1);
            return addon != null && addon->AtkUnitBase.IsVisible;
        }
        catch { return false; }
    }

// 把游戏里所有「非空宏」的内容写进日志：序号、图标编号、每一行原文。
    private unsafe void DumpGameMacros()
    {
        try
        {
            var module = RaptureMacroModule.Instance();
            if (module == null) { Chat("宏模块还没就绪，稍后再试。"); return; }

            var total = 0;
            var hits = 0;
            for (uint set = 0; set <= 1; set++)
            {
                for (uint i = 0; i < 100; i++)
                {
                    var macro = module->GetMacro(set, i);
                    if (macro == null) continue;
                    bool notEmpty;
                    try { notEmpty = macro->IsNotEmpty(); } catch { notEmpty = true; }
                    if (!notEmpty) continue;

                    total++;
                    var lines = new List<string>();
                    try
                    {
                        var linePtr = &macro->Name;
                        for (var ln = 0; ln < 15; ln++)
                        {
                            linePtr = linePtr + 1;              // Name 之后紧跟第 1..15 行
                            try
                            {
                                var s = linePtr->ToString() ?? "";
                                if (s.Trim().Length == 0) continue;
                                lines.Add("    L" + (ln + 1) + "「" + MacroLineForLog(s, 90) + "」");
                                var norm = NormalizeMacroLine(s);
                                if (norm.StartsWith(MacroIconCommand, StringComparison.OrdinalIgnoreCase)
                                    || norm.StartsWith(MacroIconCommandAlias, StringComparison.OrdinalIgnoreCase))
                                    hits++;
                            }
                            catch { /* 单行读失败就跳过 */ }
                        }
                    }
                    catch { lines.Add("    （读取行失败）"); }

                    Log("宏列表：" + (set == 1 ? "共享" : "个人") + (i + 1)
                        + " IconId=" + macro->IconId + " 图标行=" + macro->MacroIconRowId
                        + " 非空行=" + lines.Count);
                    foreach (var l in lines) Log(l);
                }
            }

            Log("宏列表：共 " + total + " 个非空宏，其中含本模块指令的行 " + hits + " 处（命令 " + MacroIconCommand + "）");
            Chat("已把 " + total + " 个非空宏写进日志（搜「宏列表」），其中含 " + MacroIconCommand + " 的 " + hits + " 处。");
        }
        catch (Exception e)
        {
            Chat("读宏数据失败：" + e.Message);
            Log("宏列表导出失败：" + e, true);
        }
    }

// 宏内指令：第二条通道的具体实现（设计说明见字段区那段注释）

    /// <summary>宏当前执行到第几行（0 = 没有宏在跑）。</summary>
    private static unsafe int CurrentMacroLineIndex()
    {
        try
        {
            var shell = RaptureShellModule.Instance();
            return shell == null ? 0 : shell->MacroCurrentLine;
        }
        catch { return 0; }
    }

// 宏行归一化：去首尾空白、全角斜杠「／」→「/」、全角空格「　」→ 半角空格。
    private static string NormalizeMacroLine(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == '\uFF0F') sb.Append('/');        // ／ → /
            else if (ch == '\u3000') sb.Append(' ');   // 　 → 空格
            else sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>这一行是不是本模块指令？是就取出参数（已 trim）。只认行首带 '/' 的写法，免得把普通喊话误判成指令。</summary>
    private static bool TrySplitOwnCommand(string line, out string args)
    {
        args = "";
        if (string.IsNullOrEmpty(line) || line[0] != '/') return false;
        return MatchCommandPrefix(line, MacroIconCommand, out args)
               || MatchCommandPrefix(line, MacroIconCommandAlias, out args);
    }

    private static bool MatchCommandPrefix(string line, string name, out string rest)
    {
        rest = "";
        if (line.Length < name.Length) return false;
        if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
        // /ticonxx 这种不算：命令名后面必须是空格或行尾
        if (line.Length > name.Length && line[name.Length] != ' ') return false;
        rest = line.Substring(name.Length).Trim();
        return true;
    }

    /// <summary>日志用：控制字符替换掉并截断，宏行原文里的换行/怪异字符不会污染日志。</summary>
    private static string MacroLineForLog(string raw, int max)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(Math.Min(raw.Length, max) + 1);
        foreach (var ch in raw)
        {
            if (sb.Length >= max) { sb.Append('…'); break; }
            sb.Append(char.IsControl(ch) ? '·' : ch);
        }
        return sb.ToString();
    }

// 宏内指令的兜底通道。每帧调用，但「行号没变」时立刻返回（只读一个 int），
    private static unsafe void PollMacroCommandLine()
    {
        try
        {
            var shell = RaptureShellModule.Instance();
            if (shell == null) { macroShellState = "空"; return; }
            macroShellState = "就绪";

            var index = shell->MacroCurrentLine;
            var raw = shell->MacroLineText.ToString() ?? "";

            // 第一次看只记基线不执行：游戏可能还留着上一次宏的最后一行，
            // 一上来就执行等于「模块一加载就莫名其妙改了一次图标」。
            if (!macroLinePrimed)
            {
                macroLinePrimed = true;
                macroLineSeenIndex = index;
                macroLineTextSeen = raw;
                return;
            }

// 三个信号任意一个成立就认为「这行在跑」：
            var newRun = index > 0 && macroLineSeenIndex <= 0;
            var lineChanged = index > 0 && index != macroLineSeenIndex;
            var textChanged = raw != macroLineTextSeen;
            macroLineSeenIndex = index;
            macroLineTextSeen = raw;

            if (!newRun && !lineChanged && !textChanged) return;
            if (string.IsNullOrEmpty(raw)) return;

            macroLineSeen++;
            var text = NormalizeMacroLine(raw);
            var hit = TrySplitOwnCommand(text, out var args);
            if (hit) macroLineHits++;
            macroLineLastDiag = "行" + index + "「" + MacroLineForLog(raw, 24) + "」" + (hit ? "命中" : "");

// 宏行观察日志：把**原文**打出来（前 40 行，之后只打命中行）——
            if (hit || macroLineLogged < 40)
            {
                macroLineLogged++;
                Log("宏行观察：行号=" + index + "「" + MacroLineForLog(raw, 80) + "」"
                    + (hit ? " → 命中本模块指令，参数=" + args + "；写入目标 → " + MacroDiagText() : ""));
            }

// ★v11：命中**只观察，不执行**。
        }
        catch { /* 心跳里的观察绝不把游戏带崩 */ }
    }

// ---------------------------- 通道四：宏文本扫描 ----------------------------

    /// <summary>通道四入口：2 秒一拍，整轮扫 200 个宏约零点几毫秒（只读非空宏的文本）。</summary>
    private void TickMacroTextSweep()
    {
        var now = Environment.TickCount64;
        if (now - lastMacroTextSweepTick < 2000) return;
        lastMacroTextSweepTick = now;
        try { SweepMacroIconCommands(); }
        catch (Exception e) { Log("宏文本扫描异常: " + e.Message, true); }
        // ★v11：这里不做面板重刷 —— 那件事已改成 250ms 一拍的 TickPanelIconSync()（见心跳），
        //   2 秒才修一次正是用户抱怨的「要等一会 / 要点一下」。
    }

    private unsafe void SweepMacroIconCommands()
    {
        var module = RaptureMacroModule.Instance();
        if (module == null) return;
        // ★ 宏正在执行时不写宏数据：避免「游戏读宏行的同时我们改宏结构」这种重入。
        //   错过的下一条也不会丢 —— 文本还在，下一轮（或快通道）照样会命中。
        if (IsMacroRunning()) return;

        var foundThisRound = 0;     // 每轮快照（不累计 —— 累计值只会吓人，实测踩过）
        // ★v11：本轮「含本模块指令的槽」重新收集一份，扫完整体替换 macroIconSlots ——
        //   用户把指令行删掉/改了目标后，旧槽必须在 2 秒内从重刷清单里消失。
        var foundSlots = new Dictionary<int, uint>();

        for (uint set = 0; set <= 1; set++)
        {
            for (uint i = 0; i < 100; i++)
            {
                var macro = module->GetMacro(set, i);
                if (macro == null) continue;
                bool notEmpty;
                try { notEmpty = macro->IsNotEmpty(); } catch { notEmpty = true; }
                if (!notEmpty) continue;

                // 找指令行 + 解析「目标宏 + 目标编号」（与快通道共用同一段，解析逻辑不许漂移）
                if (!TryResolveMacroIconCommand(macro, set, i, out var targetSet, out var targetIndex,
                                                out var iconId, out var rawArgs))
                    continue;
                foundThisRound++;

                // ★ 记入「需要持续重刷的面板槽」：**必须在下面那条「值相等就 continue」之前**记，
                //   否则稳态（图标已是目标号）时这个槽永远不会进重刷清单 —— 那正是 v10 的死角。
                foundSlots[(int)targetSet * 100 + (int)targetIndex] = iconId;

                var target = module->GetMacro(targetSet, targetIndex);
                if (target == null) continue;
                if (target->IconId == iconId) continue;     // 宏数据稳态：一行不写（界面交给 250ms 重刷）

                target->SetIcon(iconId);
                try { module->SetSavePendingFlag(true, targetSet); } catch { /* 落盘交给游戏 */ }
                macroTextWrites++;
                macroTextLastDiag = (targetSet == 1 ? "共享" : "个人") + (targetIndex + 1) + " → " + iconId;
                Log("宏文本扫描：命中指令行，已写宏图标 " + macroTextLastDiag + "（参数=" + MacroLineForLog(rawArgs, 50) + "）");

                // ★ 写完必须把热键栏与宏面板的缓存刷掉（否则就是「图标不出现 / 要点一下」）
                RefreshMacroIconUsers(targetSet, targetIndex, iconId);
            }
        }

        macroTextCmdSeen = foundThisRound;      // 本轮「含可解析指令」的宏数（快照，非累计）

        // ★v11：整体替换重刷清单（不 merge —— 删掉指令行的槽必须能退出去）
        macroIconSlots.Clear();
        foreach (var kv in foundSlots) macroIconSlots[kv.Key] = kv.Value;
    }

// 读一条宏的文本，找出本模块指令并解析出「目标宏 + 目标图标编号」。
    private static unsafe bool TryResolveMacroIconCommand(
        RaptureMacroModule.Macro* macro, uint ownSet, uint ownIndex,
        out uint targetSet, out uint targetIndex, out uint iconId, out string rawArgs)
    {
        targetSet = ownSet;
        targetIndex = ownIndex;
        iconId = 0;
        rawArgs = "";
        if (macro == null) return false;

        // ---- 扫 15 行找第一条本模块指令（偏移推进写法与 /图库图标 宏列表 导出同款）----
        string? cmdArgs = null;
        try
        {
            var linePtr = &macro->Name;
            for (var ln = 0; ln < 15; ln++)
            {
                linePtr = linePtr + 1;              // Name 之后紧跟第 1..15 行
                var raw = linePtr->ToString() ?? "";
                if (raw.Trim().Length == 0) continue;
                if (TrySplitOwnCommand(NormalizeMacroLine(raw), out var args))
                {
                    cmdArgs = args;
                    break;
                }
            }
        }
        catch { return false; }
        if (cmdArgs == null) return false;

        // ---- 解析写入目标 ----
        var parts = cmdArgs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var iconText = cmdArgs;
        if (parts.Length >= 2 && IsSharedKeyword(parts[0], out var prefixSet)
            && uint.TryParse(parts[1], out var idxNum) && idxNum >= 1 && idxNum <= 100)
        {
            targetSet = prefixSet;
            targetIndex = idxNum - 1;
            iconText = string.Join(" ", parts.Skip(2));
        }
        if (iconText.Trim().Length == 0) return false;

        // ---- 参数 → 图标编号（图库名称/编号；纯数字不在图库段时是游戏原生图标）----
        uint resolved;
        try { resolved = ResolveLibraryIcon(iconText, out _); }
        catch { return false; }
        if (resolved == 0) return false;

        iconId = resolved;
        rawArgs = cmdArgs;
        return true;
    }

// ★v10 快通道：只查「宏面板里当前选中的那一条宏」，150ms 一拍。
    private unsafe void TickMacroIconFast()
    {
        var now = Environment.TickCount64;
        if (now - lastFastMacroCheckTick < 150) return;
        lastFastMacroCheckTick = now;
        try
        {
            if (IsMacroRunning()) return;                     // 宏执行中：不碰宏数据
            if (!TryGetSelectedMacro(out var set, out var index)) return;
            var module = RaptureMacroModule.Instance();
            if (module == null) return;
            var macro = module->GetMacro(set, index);
            if (macro == null) return;
            if (!TryResolveMacroIconCommand(macro, set, index, out var targetSet, out var targetIndex,
                                            out var iconId, out _))
                return;
            if (targetSet != set || targetIndex != index) return;   // 带前缀的跨宏指令交给 2 秒全量扫描
            if (macro->IconId == iconId) return;              // 稳态：一行不动
            if (ApplyMacroIcon(set, index, iconId))
            {
                fastMacroHits++;
                Log("宏图标快通道：选中宏 " + (set == 1 ? "共享" : "个人") + (index + 1) + " 图标落位 → " + iconId);
            }
        }
        catch { /* 快通道绝不把游戏带崩 */ }
    }

// ★v12 面板/热键栏同步入口：250ms 一拍。
    private static unsafe void TickPanelIconSync()
    {
        if (IsMacroRunning()) return;
        var now = Environment.TickCount64;
        if (now - lastPanelSyncTick < 250) return;
        lastPanelSyncTick = now;
        try { SyncMacroPanelIcons(); }
        catch { /* 面板对齐绝不把游戏带崩 */ }
        try { TickMacroHotbarSafety(); }
        catch { /* 热键栏安全网绝不把游戏带崩 */ }
    }

// 现在有没有宏正在执行。用于两件事：
    private static unsafe bool IsMacroRunning()
    {
        try
        {
            var shell = RaptureShellModule.Instance();
            if (shell == null) return false;
            return shell->MacroLocked || shell->MacroCurrentLine >= 0;
        }
        catch { return false; }
    }

// 宏运行状态的原始读数（诊断用）：MacroLocked / MacroCurrentLine。
    private static unsafe string MacroLockDiag()
    {
        try
        {
            var shell = RaptureShellModule.Instance();
            if (shell == null) return "shell空";
            return (shell->MacroLocked ? "锁" : "-") + "/行" + shell->MacroCurrentLine;
        }
        catch { return "异常"; }
    }

// ======================= 写完图标 → 刷新吃缓存的界面 =======================

// 把「宏图标已改」推给热键栏与用户宏面板。顺序与宿主 ApplyMacroIcon 一致：
    private static unsafe void RefreshMacroIconUsers(uint set, uint index, uint iconId)
    {
        var needKernel = InReservedRange(iconId);
        if (needKernel) { try { EnsureMacroKernel(iconId); } catch { /* 后台构建失败不致命 */ } }
        var ready = !needKernel || macroKernels.ContainsKey(iconId) || macroKernelFailed.ContainsKey(iconId);

// ---- ① 热键栏：槽位按宏重载（宿主 ApplyMacroIcon 里那句 ReloadMacroSlot）----
        if (ready)
        {
            try
            {
                var hb = RaptureHotbarModule.Instance();
                if (hb != null)
                {
                    hb->ReloadMacroSlots((byte)set, (byte)index);
// 再补一次全量重载：宿主自己「改了宏图标」时用的就是 ReloadAllMacroSlots()
                    hb->ReloadAllMacroSlots();
                    macroUiRefreshes++;
                    macroHotbarReloads++;
                }
            }
            catch { /* 热键栏没就绪就等下一轮（安全网会补） */ }
        }

        // ---- ② 用户宏面板：只刷「面板正在显示的那张宏表」，否则数组不对应 ----
        if (!TryGetMacroAddon(set, out var addon)) return;

// ★v12：面板应用交给 250ms 的 SyncMacroPanelIcons 统一负责 —— 它会等贴图就绪、并且记账
        if (ready)
        {
            if (ApplyIconToMacroPanelSlot(addon, set, index, iconId))
                macroPanelApplied[(int)set * 100 + (int)index] = iconId;
        }

// ---- ③ 面板正选中这条宏 → 重选一次，让编辑器左上角的大图标也跟着换 ----
        if (!ready) return;
        try
        {
            var agent = AgentMacro.Instance();
            if (agent != null
                && agent->SelectedMacroSet == set && agent->SelectedMacroIndex == index
                && addon->AtkUnitBase.IsVisible)
            {
                var now = Environment.TickCount64;
                if (now - lastMacroOpenTick > 1000)
                {
                    lastMacroOpenTick = now;
                    agent->OpenMacro(set, index);
                    if (ApplyIconToMacroPanelSlot(addon, set, index, iconId))
                        macroPanelApplied[(int)set * 100 + (int)index] = iconId;
                }
            }
        }
        catch { /* 忽略 */ }
    }

// 拿 Macro 面板的 addon 指针。★ 必须同时确认「面板正在显示的就是 set 这张宏表」——
    private static unsafe bool TryGetMacroAddon(uint set, out AddonMacro* addon)
    {
        addon = null;
        try
        {
            if (set > 1) return false;
            var agent = AgentMacro.Instance();
            if (agent == null) return false;
            if (agent->SelectedMacroSet != set) return false;     // 面板显示的不是这张表

            var stage = AtkStage.Instance();
            if (stage == null) return false;
            var unitManager = stage->RaptureAtkUnitManager;
            if (unitManager == null) return false;
            addon = (AddonMacro*)unitManager->GetAddonByName("Macro", 1);
            return addon != null;
        }
        catch { return false; }
    }

// 把图标写进宏面板里第 index 个槽（宏列表每格 = 一个 AtkComponentDragDrop 组件）。
    private static unsafe bool ApplyIconToMacroPanelSlot(AddonMacro* addon, uint set, uint index, uint iconId)
    {
        try
        {
            if (addon == null || iconId == 0 || set > 1 || index >= 100) return false;

            var module = RaptureMacroModule.Instance();
            if (module == null) return false;
            var macro = module->GetMacro(set, index);
            if (macro == null) return false;

            var comps = addon->DragDropComponent;
            if (index >= (uint)comps.Length) return false;
            var comp = comps[(int)index].Value;
            if (comp == null) return false;

            bool notEmpty;
            try { notEmpty = macro->IsNotEmpty(); } catch { notEmpty = true; }

// ---- ① ★v12 关键修正★ 面板的三条缓存必须**成套**更新 ----
            var created = addon->MacroCreated;
            if (index < (uint)created.Length) created[(int)index] = notEmpty;

            var icons = addon->MacroSetIcon;
            if (index < (uint)icons.Length)
                icons[(int)index] = (!notEmpty) ? 0 : (int)iconId;

// 名字：宿主写的是 CopyNullTerminated(macro->Name)，即「取到第一个 NUL 为止」。
            try
            {
                var names = addon->MacroName;
                if (index < (uint)names.Length)
                {
                    var full = macro->Name.ToString() ?? "";
                    var z = full.IndexOf('\0');
                    var name = z >= 0 ? full.Substring(0, z) : full;
                    var cur = "";
                    try { cur = names[(int)index].ToString() ?? ""; } catch { cur = "\u0001"; }
                    if (!string.Equals(cur, name, StringComparison.Ordinal))
                        names[(int)index].SetString(name);
                }
            }
            catch { /* 名字写失败不影响图标，绝不让它把整条刷新带崩 */ }

// ---- ② 外层 drag&drop 组件：与 ShowMacroDragDropIcon 逐条对应 ----
            comp->VisibilityFlags = (DragDropVisibilityFlag)(((int)comp->VisibilityFlags) & 253);
            comp->LoadIcon(iconId);
            try { comp->SetQuantityText(string.Empty); } catch { /* 原生签名差异，忽略 */ }
            comp->SetIconEnabled(false);

// ---- ③ 内层 AtkComponentIcon：★真正被用户看见的那一层 ----
            var inner = comp->AtkComponentIcon;
            if (inner != null)
            {
                inner->Flags = (IconComponentFlags)(((uint)inner->Flags) & 0xFFFF7FFFu);
                inner->LoadIcon(iconId);
                inner->SetIsMacro(true);
                inner->SetIconImageDisableState(false);
                inner->UpdateIndicator();
            }

// ---- ④ ★v11 完全缺失这一步★ 强制重绘 ----
            MarkMacroDragDropDirty(comp);

            macroPanelFixes++;
            return true;
        }
        catch { return false; /* 面板结构变化时保底 */ }
    }

// 宿主 MarkMacroDragDropDirty 的同款移植：把这一格相关的 Res 节点标记为需要重绘。
    private static unsafe void MarkMacroDragDropDirty(AtkComponentDragDrop* comp)
    {
        if (comp == null) return;
        try
        {
            var owner = comp->OwnerNode;
            if (owner != null) owner->AtkResNode.DrawFlags |= 1u;
        }
        catch { /* 忽略 */ }

        try
        {
            var inner = comp->AtkComponentIcon;
            if (inner == null) return;
            // 内层组件的宿主节点
            var io = inner->OwnerNode;
            if (io != null) io->AtkResNode.DrawFlags |= 1u;
            // 图标自己的几条装饰节点（FrameContainer / ComboBorder / Frame / OuterResNode）
            if (inner->FrameContainer != null) inner->FrameContainer->DrawFlags |= 1u;
            if (inner->ComboBorder != null) inner->ComboBorder->DrawFlags |= 1u;
            if (inner->Frame != null) inner->Frame->DrawFlags |= 1u;
            if (inner->OuterResNode != null) inner->OuterResNode->DrawFlags |= 1u;
        }
        catch { /* 忽略 */ }
    }

// ★v12 面板同步：**按需**刷新（不再是 v11 的「250ms 无条件重写」）。
    private static unsafe void SyncMacroPanelIcons()
    {
        if (macroIconSlots.Count == 0) return;      // 没有用到本模块指令的宏：整件事都不用做
        var agent = AgentMacro.Instance();
        if (agent == null) return;
        var set = agent->SelectedMacroSet;
        if (set > 1) return;                        // 2 = 拿不到（宿主的哨兵值），面板没在显示任何表
        if (!TryGetMacroAddon(set, out var addon)) return;
        // ★ 只在面板**真的可见**时才去碰组件：不可见时（关闭/正在销毁的过渡帧）不触碰，
        //   免得摸到正在回收的 addon 指针。
        if (!addon->AtkUnitBase.IsVisible) { macroPanelWasVisible = false; return; }
        var module = RaptureMacroModule.Instance();
        if (module == null) return;

        // ---- 面板「世代」检测 ----
        var page = addon->SelectedPage;
        var firstComp = (nint)0;
        try
        {
            var cs = addon->DragDropComponent;
            if (cs.Length > 0 && cs[0].Value != null) firstComp = (nint)cs[0].Value;
        }
        catch { /* 拿不到就不参与世代判断 */ }

        if (!macroPanelWasVisible || set != macroPanelSetSeen
            || page != macroPanelPageSeen || firstComp != macroPanelCompSeen)
        {
            macroPanelApplied.Clear();              // 列表被重画过：全部重应用一轮
            macroPanelWasVisible = true;
            macroPanelSetSeen = set;
            macroPanelPageSeen = page;
            macroPanelCompSeen = firstComp;
        }

        macroPanelScans++;
        var tableBase = (int)set * 100;             // 面板现在显示的是哪张宏表

        // 记账清理：用户把指令行删掉后，那个槽已经不在 macroIconSlots 里了，
        // 记账也该一起丢 —— 否则「同一个槽先删指令、再加回来」会因为记账还命中而被跳过。
        if (macroPanelApplied.Count > macroIconSlots.Count)
        {
            var stale = new List<int>();
            foreach (var k in macroPanelApplied.Keys)
                if (!macroIconSlots.ContainsKey(k)) stale.Add(k);
            foreach (var k in stale) macroPanelApplied.Remove(k);
        }

        foreach (var kv in macroIconSlots)
        {
            var offset = kv.Key - tableBase;
            if (offset < 0 || offset >= 100) continue;  // 这个槽不在面板当前显示的宏表里
            var index = (uint)offset;

            // 以宏数据里的编号为准（指令改了，扫描轮会更新表里的值）；宏数据没编号时用表里的。
            var desired = kv.Value;
            try
            {
                var macro = module->GetMacro(set, index);
                if (macro != null && macro->IconId != 0) desired = (uint)macro->IconId;
            }
            catch { /* 读不到就按表里的值刷，绝不让面板同步把游戏带崩 */ }
            if (desired == 0) continue;

            // ---- ★ 已应用过同一个编号 → 一个原生调用都不发（这就是「不再慢、不再乱码」的关键）----
            if (macroPanelApplied.TryGetValue(kv.Key, out var applied) && applied == desired) continue;

// ---- 保留段编号要等内核贴图就绪 ----
            if (InReservedRange(desired))
            {
                EnsureMacroKernel(desired);         // 催一把后台构建（已就绪/已在建则为空操作）
// 还没就绪就下一拍再来；但**构建失败**的必须放行 —— 那时再等也没有意义，
                if (!macroKernels.ContainsKey(desired) && !macroKernelFailed.ContainsKey(desired))
                    continue;
            }

            if (ApplyIconToMacroPanelSlot(addon, set, index, desired))
            {
                macroPanelApplied[kv.Key] = desired;
                ReloadHotbarFor(set, index);        // ★ 宿主 ApplyMacroIcon 里那句 ReloadMacroSlot
            }
        }
    }

// 单槽热键栏重载：与宿主 ReloadMacroSlot 完全对应（`RaptureHotbarModule.ReloadMacroSlots(set, index)`）。
    private static unsafe void ReloadHotbarFor(uint set, uint index)
    {
        try
        {
            var hb = RaptureHotbarModule.Instance();
            if (hb == null) return;
            hb->ReloadMacroSlots((byte)set, (byte)index);
            macroHotbarReloads++;
        }
        catch { /* 热键栏没就绪就等下一轮 */ }
    }

// 热键栏安全网（★v16 降频并改性质）：面板那套有「世代检测」，热键栏这边没法同样便宜地
    private static unsafe void TickMacroHotbarSafety()
    {
        if (macroIconSlots.Count == 0) return;
        var now = Environment.TickCount64;
        if (now - lastHotbarSyncTick < 20000) return;
        lastHotbarSyncTick = now;
        try
        {
            var hb = RaptureHotbarModule.Instance();
            if (hb == null) return;
            hb->ReloadAllMacroSlots();
            macroHotbarReloads++;
        }
        catch { /* 忽略 */ }
    }

    /// <summary>「个人/共享」关键词（含中英文写法）→ 宏表编号（0=个人 1=共享）。</summary>
    private static bool IsSharedKeyword(string token, out uint set)
    {
        set = 0;
        switch (token)
        {
            case "共享":
            case "shared":
            case "s":
            case "g":
                set = 1u;
                return true;
            case "个人":
            case "individual":
            case "i":
                set = 0u;
                return true;
            default:
                return false;
        }
    }

// 把命令行参数解析成图标编号：优先图库（名称 / 来源网址 / 编号），
    private static uint ResolveLibraryIcon(string text, out string label)
    {
        var t = (text ?? "").Trim();
        label = t;

        var cfg = selfRef?.config;
        if (cfg == null) return 0;

        foreach (var b in cfg.BrowserImages)
            if (!string.IsNullOrEmpty(b.Name) && string.Equals(b.Name, t, StringComparison.OrdinalIgnoreCase))
            { label = b.Name; return b.Id; }

        foreach (var b in cfg.BrowserImages)
            if (!string.IsNullOrEmpty(b.SourceUrl) && string.Equals(b.SourceUrl, t, StringComparison.OrdinalIgnoreCase))
            { label = b.Name; return b.Id; }

        foreach (var b in cfg.BrowserImages)
            if (!string.IsNullOrEmpty(b.Name) && b.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
            { label = b.Name; return b.Id; }

        if (uint.TryParse(t, out var num) && num != 0)
        {
            if (InReservedRange(num))
            {
                // 落在保留段却不在图库里 —— 不认，免得把宏指到一个画不出来的编号上
                var hit = cfg.BrowserImages.FirstOrDefault(x => x.Id == num);
                if (hit == null) return 0;
                label = hit.Name;
                return hit.Id;
            }
            label = "游戏图标 " + num;
            return num;
        }

        return 0;
    }

    private static unsafe bool TryGetSelectedMacro(out uint set, out uint index)
    {
        set = 0;
        index = 0;
        try
        {
            var agent = AgentMacro.Instance();
            if (agent == null) return false;
            set = agent->SelectedMacroSet;
            index = agent->SelectedMacroIndex;
            if (set > 1) set = 0;
            return index < 100;
        }
        catch { return false; }
    }

// 诊断：把「宏面板当前选中的宏」的真实 IconId 打出来。
    private static unsafe string MacroDiagText()
    {
        // 宏行观察放最前面：它是「宏内指令」那条通道的唯一直接证据，
        // 宏窗口开没开都要看得到（以前宏窗口一关，诊断就只剩「选中宏=无」，什么也判断不了）。
        var head = "shell=" + macroShellState
                   + " 宏执行钩子=" + macroExecHookState
                   + "（拦 " + macroExecSeen + " 次/命中 " + macroExecHits + "，最近 " + macroExecLastDiag + "）"
                   + " 宏行=" + macroLineLastDiag
                   + "（见过 " + macroLineSeen + " 行/命中 " + macroLineHits + "）"
                   + " 宏文本扫描=" + macroTextLastDiag
                   + "（指令 " + macroTextCmdSeen + " 处/写 " + macroTextWrites + " 次）"
                   + " 界面刷新=热键栏" + macroUiRefreshes + "次/面板重刷" + macroPanelFixes + "次"
                   + " ｜ 快通道=" + fastMacroHits + "｜面板对齐=" + macroPanelScans + "轮"
                   + "（重刷清单" + macroIconSlots.Count + "槽）"
// ★v12 新增两个计数，实机验证点：
                   + "｜热键栏重载=" + macroHotbarReloads + "｜面板已应用=" + macroPanelApplied.Count
                   + "｜宏内让行=" + macroRunGuardHits + "｜宏锁=" + MacroLockDiag();
        try
        {
            if (!TryGetSelectedMacro(out var set, out var idx)) return head + " ｜ 选中宏=无（宏窗口没开？）";
            var who = (set == 1 ? "共享" : "个人") + (idx + 1);
            var module = RaptureMacroModule.Instance();
            if (module == null) return head + " ｜ 选中宏=" + who + " 模块=空";
            var macro = module->GetMacro(set, idx);
            if (macro == null) return head + " ｜ 选中宏=" + who + " 条目=空";
            return head + " ｜ 选中宏=" + who + " IconId=" + macro->IconId + " 图标行=" + macro->MacroIconRowId;
        }
        catch { return head + " ｜ 选中宏=读取异常"; }
    }

    private static unsafe bool ApplyMacroIcon(uint set, uint index, uint iconId)
    {
        try
        {
            var module = RaptureMacroModule.Instance();
            if (module == null) return false;
            var macro = module->GetMacro(set, index);
            if (macro == null) return false;

            macro->SetIcon(iconId);
            // 标记存档待写：游戏自己会在合适的时机落盘，我们只负责把它"点亮"
            try { module->SetSavePendingFlag(true, set); } catch { /* 忽略 */ }

            // ★v11：立刻进「持续重刷清单」—— 全量扫描最多 2 秒后才重新收集，这中间靠 250ms
            //   重刷保证宏面板一直显示正确图标（不用等、不用点）。
            macroIconSlots[(int)set * 100 + (int)index] = iconId;

// ★ 宏数据变了 ≠ 界面会重画：热键栏槽位与宏面板（含列表每格的拖拽图标）都各自缓存，
            RefreshMacroIconUsers(set, index, iconId);

            Log("宏图标已写入：set=" + set + " index=" + index + " icon=" + iconId);
            return true;
        }
        catch (Exception e)
        {
            Log("写宏图标失败: " + e.Message, true);
            return false;
        }
    }

    // ------------------------------ 贴图槽替换 ------------------------------

    private void TickMacroIconSwap()
    {
        if (!config.MacroIconSwap) return;
        // 图库空且没有替换规则 → 没事可做。两者取或：规则的目标可能全是游戏图标，不依赖图库。
        if (config.BrowserImages.Count == 0 && iconRuleMap.Count == 0) return;
        var now = Environment.TickCount64;
        if (now - lastMacroSwapTick < MacroIconSwapIntervalMs) return;
        lastMacroSwapTick = now;
        try { SweepMacroIcons(); }
        catch (Exception e) { Log("宏图标扫描异常: " + e.Message, true); }
    }

// ★v18 热键栏「内容驱动」认领

// ★v20 定稿：**不再猜界面名，直接枚举游戏自己的 addon 列表**。
    private const string HotbarAddonNamePrefix = "_Action";

    /// <summary>`AtkUnitBase` 的 addon 名（内联 32 字节，NUL 结尾）字段偏移与容量。</summary>
    private const int AubNameOff = 0x08;
    private const int AubNameLen = 32;

// addon 名是否以给定前缀开头。**纯字节读、零字符串分配**（每帧要过上百个 addon）。
    private static unsafe bool UnitNameHasPrefix(AtkUnitBase* unit, string prefix)
    {
        var p = (byte*)unit + AubNameOff;
        var n = prefix.Length;
        if (n > AubNameLen) n = AubNameLen;
        for (var i = 0; i < n; i++)
            if (p[i] != (byte)prefix[i]) return false;
        return true;
    }

    /// <summary>取 addon 名（内联 32 字节，NUL 结尾）。只在确认是我们关心的 addon 后才调用。</summary>
    private static unsafe string UnitNameText(AtkUnitBase* unit)
    {
        var p = (byte*)unit + AubNameOff;
        var len = 0;
        while (len < AubNameLen && p[len] != 0) len++;
        var chars = new char[len];
        for (var i = 0; i < len; i++) chars[i] = (char)p[i];
        return new string(chars);
    }

// 这个 addon 是不是「常规热键栏」（热键栏 1~10）。
    private static bool IsHotbarCandidate(string nm)
    {
        if (string.IsNullOrEmpty(nm)) return false;
        if (!nm.StartsWith("_ActionBar", StringComparison.Ordinal)) return false;
        if (nm.StartsWith("_ActionBarEx", StringComparison.Ordinal)) return false;   // Ex = 另一条栏，先不碰
        return true;
    }

// 这个宏（个人 set=0 / 共享 set=1，第 index 条）现在的图标编号，落在我们保留段就返回它。
    private static unsafe uint MacroLibIdFor(uint set, uint index)
    {
        if (set > 1 || index >= 100) return 0;
        try
        {
            var module = RaptureMacroModule.Instance();
            if (module == null) return 0;
            var macro = module->GetMacro(set, index);
            if (macro == null) return 0;
            var id = macro->IconId;
            return InReservedRange(id) ? id : 0;
        }
        catch { return 0; }
    }

    /// <summary>一个贴图槽的状态字符：`.`=空槽 `O`=我们的内核贴图 `k`=别的内核贴图 `x`=原生资源贴图。</summary>
    private static unsafe char SlotStateChar(AtkTexture* t, IntPtr kernel)
    {
        if (t == null) return '.';
        try
        {
            if (t->TextureType != TextureType.KernelTexture) return 'x';
            if (kernel != IntPtr.Zero && (nint)t->KernelTexture == kernel) return 'O';
            return 'k';
        }
        catch { return '?'; }
    }

    /// <summary>三个槽（IconImage / FrameIcon / Texture）的状态拍成 3 个字符。</summary>
    private static unsafe string SlotMaskOf(AtkComponentIcon* icon, uint libId)
    {
        if (icon == null) return "???";
        var kernel = macroKernels.TryGetValue(libId, out var k) ? k : IntPtr.Zero;
        var a = icon->IconImage != null ? SlotStateChar(GetImageNodeTexture(icon->IconImage), kernel) : '.';
        var b = icon->FrameIcon != null ? SlotStateChar(GetImageNodeTexture(icon->FrameIcon), kernel) : '.';
        var c = icon->Texture != null ? SlotStateChar(&icon->Texture->AtkTexture, kernel) : '.';
        return string.Concat(a, b, c);
    }

// ★v18 热键栏「内容驱动」认领 —— 每 250ms 随扫描跑一次。
    private static unsafe void SweepHotbarMacroIcons()
    {
        if (macroKernels.Count == 0) { macroHotbarDiag = "内核未就绪"; return; }
        var stage = AtkStage.Instance();
        if (stage == null) { macroHotbarDiag = "无Stage"; return; }
        var um = stage->RaptureAtkUnitManager;
        if (um == null) { macroHotbarDiag = "无UnitManager"; return; }
        var hb = RaptureHotbarModule.Instance();
        if (hb == null) { macroHotbarDiag = "无热键栏模块"; return; }

        var owned = 0;
        var fixes = 0;
        var blind = 0;
        var foundUnits = 0;
        var sb = new StringBuilder(384);

// ★v20：枚举「游戏自己已加载的全部 addon」，挑出名字以 `_Action` 开头的
        ref var allUnits = ref um->AtkUnitManager.AllLoadedUnitsList;
        var allEntries = allUnits.Entries;
        var allCount = (int)allUnits.Count;
        var allTotal = allCount < allEntries.Length ? allCount : allEntries.Length;

        for (var ui = 0; ui < allTotal; ui++)
        {
            var unit = allEntries[ui].Value;
            if (unit == null || !IsUserPointer(unit)) continue;
            if (!UnitNameHasPrefix(unit, HotbarAddonNamePrefix)) continue;
            foundUnits++;

            var addonName = UnitNameText(unit);
            var isMine = IsHotbarCandidate(addonName);
            var ab = (byte*)unit;
            var hbId = *(byte*)(ab + AbeHotbarIdOff);
            var slotCount = *(byte*)(ab + AbeSlotCountOff);
            if (slotCount == 0 || slotCount > 32) slotCount = 12;
            var isVisible = unit->IsVisible;

// ★v20 诊断：**枚举到的每一个 `_Action*` 都登记一行**（含不可见的、含本版不处理的）——
            if (sb.Length < 1000)
                sb.Append(' ').Append(addonName).Append(isMine ? "*" : "")
                  .Append(isVisible ? "(显 " : "(隐 ").Append(hbId).Append('/').Append(slotCount).Append(')');

            if (!isMine) continue;

// ★v20：**不再因为「不可见」就跳过**。这些单元来自游戏自己的「已加载」列表，

            var vecFirst = *(byte**)(ab + AbeVectorOff);
            if (vecFirst == null || !IsUserPointer(vecFirst)) continue;

// ---- 第 ① 遍（只读）：收集「游戏数据 ↔ 组件现状」，并核对两者对不对得上 ----
            var matches = 0;
            var mismatches = 0;
            var unitOwned = 0;
            for (var s = 0; s < 32; s++) macroHbHasIcon[s] = false;
            for (var s = 0; s < slotCount; s++)
            {
                var rec = vecFirst + s * ActionBarSlotStride;
                if (!IsUserPointer(rec)) continue;
                var iconNode = *(AtkComponentNode**)(rec + AbsIconNodeOff);
                if (iconNode == null || !IsUserPointer(iconNode)) continue;
                var comp = *(AtkComponentBase**)((byte*)iconNode + CompNodeCompOff);
                if (comp == null || !ComponentAccessOk(comp, (AtkResNode*)iconNode)) continue;
                if (comp->GetComponentType() != ComponentType.Icon) continue;
                var icon = (AtkComponentIcon*)comp;
                var compId = icon->IconId;

                // —— 权威判据：热键栏模块里这个槽的内容 ——
                uint slotIcon = 0, cmdType = 0, cmdId = 0;
                try
                {
                    var slot = hb->GetSlotById(hbId, (uint)s);
                    if (slot != null)
                    {
                        slotIcon = *(uint*)((byte*)slot + HSlotIconIdOff);
                        cmdType = *(byte*)((byte*)slot + HSlotCmdTypeOff);
                        cmdId = *(uint*)((byte*)slot + HSlotCmdIdOff);
                    }
                }
                catch { /* 单槽读取失败不影响别的槽 */ }

// ★v21 ★唯一判据 = **游戏自己的槽数据**。
                var libId = 0u;
                if (InReservedRange(slotIcon)) libId = slotIcon;
                else if (cmdType == 7 && slotIcon == 0) libId = MacroLibIdFor(cmdId / 256u, cmdId % 256u);

                macroHbSlotIcons[s] = slotIcon;
                macroHbCompIds[s] = compId;
                macroHbLibIds[s] = libId;
                macroHbIconPtrs[s] = (nint)icon;
                macroHbHasIcon[s] = true;

                if (slotIcon != 0 && compId != 0 && !InReservedRange(slotIcon) && !InReservedRange(compId))
                {
                    if (slotIcon == compId) matches++; else mismatches++;
                }
            }

// 核对出「系统性错位」才收手：零星几条不一致可能是形态差异（数量角标、显现动作脸之类），
            if (mismatches > matches)
            {
                sb.Append(' ').Append(addonName).Append("(栏").Append(hbId).Append(" 映射核对不通过 合")
                  .Append(matches).Append("/不合").Append(mismatches).Append(" 本轮不写)");
                continue;
            }

            // ---- 第 ② 遍：认领 + 补写（只碰 libId != 0 的槽；其余一个字节都不动）----
            for (var s = 0; s < slotCount; s++)
            {
                if (!macroHbHasIcon[s] || macroHbLibIds[s] == 0) continue;
                var libId = macroHbLibIds[s];
                var compId = macroHbCompIds[s];
                var icon = (AtkComponentIcon*)macroHbIconPtrs[s];

                owned++;
                unitOwned++;
                if (sb.Length < 900)
                    sb.Append(' ').Append(s).Append(":槽").Append(macroHbSlotIcons[s])
                      .Append("/件").Append(compId).Append('/').Append(SlotMaskOf(icon, libId));

// ★★★v21：这里**删掉了那道用组件编号当否决票的锁**（原句 `if (compId != 0 &&
                var wrote = AttachKernelToIcon(icon, libId, libId);
                if (wrote > 0) fixes++;
                else if (!AnySlotIsOurKernel(icon, libId))
                {
                    // 一个槽都没换上、三个槽里也没有任何一个已经是我们的内核贴图 ⇒ 这不是「本来就对」，
                    // 而是游戏切页时把内层贴图槽卸载了（0x63e2b0），只剩空壳 ⇒ 必须重建。
                    if (compId == 0) blind++;
                    RebuildReservedIconSlot(icon, libId);     // 用真实占位图把槽建起来，再换回我们的图
                }
                if (icon->IconId != libId) { try { icon->IconId = libId; } catch { } }
            }

            if (unitOwned > 0)
                sb.Append(" → 我方").Append(unitOwned)
                  .Append(" 核对合").Append(matches).Append("/不合").Append(mismatches);
        }       // ★v20：闭合「已加载 addon 枚举循环」

        macroHotbarOwnSlots = owned;
        macroHotbarFixes += fixes;
        macroHotbarBlindFixes += blind;
        // 前缀直接给出「游戏里到底枚举到几个 `_Action*`」——这是判断「栏到底存不存在」的第一眼依据。
        if (sb.Length == 0)
            macroHotbarDiag = "_Action*共" + foundUnits + "个｜无（枚举到的都不是热键栏，或槽内容都不是我们的）";
        else
            sb.Insert(0, "_Action*共" + foundUnits + "个｜");
        if (sb.Length > 0) macroHotbarDiag = sb.ToString();
    }

// 扫描所有已加载的界面单元，把我们编号的图标槽换成本地图。
    private unsafe void SweepMacroIcons()
    {
// ★v14 安全门（消除「加载时崩溃」）★
        if (!IsUiSafeToTouch()) { macroSweepGatedByMask++; return; }

        // ★v18：热键栏先走「内容驱动」那条路 —— 它是切页问题的正解，且开销可忽略。
        //   放在最前面：即便下面那轮遍历的 4ms 预算被耗尽，热键栏这一趟也一定跑到了。
        try { SweepHotbarMacroIcons(); }
        catch { /* 热键栏这一趟失败不影响下面的全界面扫描 */ }

        // ★v15：清理「重建失败退避表」里已过期的条目。组件地址会随界面重建不断变化，
        //   不及时清就会一直涨（表本身很小，这点遍历开销可以忽略）。
        if (macroRebuildBackoff.Count > 0)
        {
            var nowT = Environment.TickCount64;
            List<nint>? dead = null;
            foreach (var kv in macroRebuildBackoff)
                if (nowT >= kv.Value) (dead ??= new List<nint>()).Add(kv.Key);
            if (dead != null)
                foreach (var k in dead) macroRebuildBackoff.Remove(k);
        }

        // ★v17：清理「归还退避表」里已过期的条目（5 秒一条，与重建退避表同款）。
        if (macroRevertBackoff.Count > 0)
        {
            var nowR = Environment.TickCount64;
            List<nint>? deadRevert = null;
            foreach (var kv in macroRevertBackoff)
                if (nowR >= kv.Value) (deadRevert ??= new List<nint>()).Add(kv.Key);
            if (deadRevert != null)
                foreach (var k in deadRevert) macroRevertBackoff.Remove(k);
        }

        var stage = AtkStage.Instance();
        if (stage == null) return;
        var unitManager = stage->RaptureAtkUnitManager;
        if (unitManager == null) return;

        ref var list = ref unitManager->AtkUnitManager.AllLoadedUnitsList;
        var entries = list.Entries;
        var count = (int)list.Count;
        var total = count < entries.Length ? count : entries.Length;
        if (total <= 0) return;

        var deadline = Environment.TickCount64 + MacroIconSweepBudgetMs;

        // 轮转起点：预算不够时一轮扫不完，每轮从上次断掉的地方接着扫，
        // 保证「若干个周期内每个界面单元都会被扫到」，而不是永远只扫前几个。
        var start = macroSwapCursor < total ? macroSwapCursor : 0;
        var scanned = 0;
        var hits = 0;

        for (var step = 0; step < total; step++)
        {
            if (Environment.TickCount64 >= deadline) break;

            var idx = start + step;
            if (idx >= total) idx -= total;
            scanned = step + 1;

            var unit = entries[idx].Value;
            if (unit == null) continue;

// ★v14 只碰「可见」的界面单元 —— 这条既是安全门也是用户要的「按需」：
            if (!unit->IsVisible) { macroSweepNotVisible++; continue; }

            // ★ 关键：addon 的节点是「扁平」挂在它自己的 UldManager.NodeList 数组里的，
            //   RootNode.ChildNode 链几乎是空的 —— 只走 ChildNode 会漏掉界面上绝大多数节点。
            ref var uld = ref unit->UldManager;
            var nl = uld.NodeList;
            if (nl == null || !IsUserPointer(nl)) continue;
            var cnt = (int)uld.NodeListCount;
            if (cnt <= 0 || cnt > MacroIconNodeBudget) continue;

            for (var k = 0; k < cnt; k++)
            {
                if (Environment.TickCount64 >= deadline) break;
                var n = nl[k];
                if (n == null || !IsUserPointer(n)) continue;
                hits += WalkMacroIconNode(n, 0, deadline);
            }
        }

        var next = start + scanned;
        macroSwapCursor = next >= total ? 0 : next;
        macroSwapUnits = total;
        macroSwapHits = hits;

// 节流诊断：让 /xllog 能回答"扫描到底跑没跑、看到些什么"。
        var nowMs = Environment.TickCount64;
        if (nowMs - lastMacroSweepLogTick >= 10000)
        {
            lastMacroSweepLogTick = nowMs;
            var diag = MacroDiagText();
            // 累计值只涨不清零，看不出节奏；打点算增量，日志里「本轮 +N」才是活动量
            macroSweepCompsDelta = macroSweepComps - macroSweepCompsMark;
            macroSweepCompsMark = macroSweepComps;
            macroSweepReservedDelta = macroSweepReserved - macroSweepReservedLast;
            macroSweepReservedLast = macroSweepReserved;
            macroSweepRuledDelta = macroSweepRuled - macroSweepRuledLast;
            macroSweepRuledLast = macroSweepRuled;
            var sig = total + "/" + macroSweepComps + "/" + macroSweepReserved + "/" + macroSweepRuled + "/" + hits
                      + "/" + macroIconHookCalls + "/" + macroIconHookReserved + "/" + macroIconHookSwaps
                      + "/" + macroKernels.Count + "/" + macroSweepGatedByMask + "/" + macroSweepNotVisible
                      + "/" + macroSlotRebuilds + "/" + macroSlotReverts + "/" + macroRevertUnknown
                      + "/" + macroRebuildAttempts + "/" + macroRebuildFails + "/" + macroEmptyCompSeen
                      + "/" + macroHotbarOwnSlots + "/" + macroHotbarFixes + "/" + macroHotbarBlindFixes
                      + "/" + diag;
            if (sig != lastMacroSweepLogSig)
            {
                lastMacroSweepLogSig = sig;
                Log("宏图标扫描：界面单元=" + total + " 图标组件=" + macroSweepComps + "（本轮 +" + macroSweepCompsDelta + "）"
                    + " 保留段编号=" + macroSweepReserved + "（本轮 +" + macroSweepReservedDelta + "）"
                    + " 规则命中=" + macroSweepRuled + "（本轮 +" + macroSweepRuledDelta + "）"
                    + " 本轮写入=" + hits
                    + " 槽重建=" + macroSlotRebuilds + "（切页后槽被卸载的急救计数）"
                    + " 归还=" + macroSlotReverts + "（v17：把占在别人槽里的我们那张图摘掉的次数）"
                    + " 编号空残留=" + macroRevertUnknown
                    + " ｜ 重建尝试=" + macroRebuildAttempts + " 重建失败=" + macroRebuildFails
                    + " 空壳组件=" + macroEmptyCompSeen
                    + " ｜ 内核贴图 就绪=" + macroKernels.Count
                    + " 构建中=" + macroKernelPending.Count + " 失败=" + macroKernelFailed.Count
                    + " ｜ hook " + macroIconHookState
                    + "（调用=" + macroIconHookCalls + " 保留段=" + macroIconHookReserved
                    + " 换上=" + macroIconHookSwaps + "）"
                    + " ｜ v14安全门 遮罩整轮跳过=" + macroSweepGatedByMask + " 不可见单元跳过=" + macroSweepNotVisible
                    + " ｜ v18 热键栏 我方槽=" + macroHotbarOwnSlots + " 补写=" + macroHotbarFixes
                    + " 空壳救回=" + macroHotbarBlindFixes
                    + " ｜ " + diag);
// ★v18 热键栏逐槽明细单独一行（内容变了才打）：槽编号 / 组件编号 / 三槽状态
                var hbSig = macroHotbarOwnSlots + "/" + macroHotbarFixes + "/" + macroHotbarBlindFixes + "/" + macroHotbarDiag;
                if (hbSig != lastMacroHotbarDiagSig)
                {
                    lastMacroHotbarDiagSig = hbSig;
                    Log("宏图标热键栏：我方槽=" + macroHotbarOwnSlots + " 补写=" + macroHotbarFixes
                        + "（其中空壳救回=" + macroHotbarBlindFixes + "）｜" + macroHotbarDiag);
                }
            }
        }

// 记录回收：太久没在界面里露过面的槽记录丢掉。界面单元一关，那个地址随时会被复用，
        if (nowMs - lastIconPatchPruneTick >= 60000)
        {
            lastIconPatchPruneTick = nowMs;
            var cut = nowMs - 300000;
            var pruned = 0;
            foreach (var kv in iconSlotPatches)
            {
                if (kv.Value != null && kv.Value.LastSeen < cut)
                {
                    if (iconSlotPatches.TryRemove(kv.Key, out _)) pruned++;
                }
            }
            if (pruned > 0) Log("图标替换：回收过期槽记录 " + pruned + " 条，剩 " + iconSlotPatches.Count + " 条");
        }
    }

    /// <summary>递归走节点，把属于保留段编号的贴图槽换掉；返回本节点子树里写成功的槽数。</summary>
    private unsafe int WalkMacroIconNode(AtkResNode* node, int depth, long deadline)
    {
// ★v14：第一道也是最要紧的一道 —— 连 node->Type 都不读就先验指针。
        if (node == null || !IsUserPointer(node)) return 0;
        if (depth > MacroIconWalkDepth) return 0;
        if (Environment.TickCount64 >= deadline) return 0;

        var hits = 0;
        try
        {
            // ① 普通图片节点：宏面板 / 热键栏的图标有时就画在这里
            if (node->Type == NodeType.Image)
                hits += SwapTextureIfOurs(GetImageNodeTexture((AtkImageNode*)node), 0);

            // ② 组件节点：Type 的原始值 = 1000+X（NodeType.Component=10000 只是枚举哨兵，和真实节点不等）
            var raw = (uint)node->Type;
            if (raw >= 1000 && raw < 2000)
            {
                var comp = ((AtkComponentNode*)node)->Component;
// ★ 必须先过野指针守卫再把它「当结构体读」：raw 类型落在 1000..1999 只说明
                if (comp != null && ComponentAccessOk(comp, node))
                {
                    if (comp->GetComponentType() == ComponentType.Icon)
                    {
                        var icon = (AtkComponentIcon*)comp;
                        // 组件自带的 IconId 比「贴图资源里的 IconId」可靠
                        var id = icon->IconId;
                        macroSweepComps++;

                        // 诊断：三槽全空的组件有几个。正常组件极少三槽全空，
                        // 这个数字直接告诉我们「有多少槽被游戏卸载成了空壳」。
                        if (icon->IconImage == null && icon->FrameIcon == null && icon->Texture == null)
                            macroEmptyCompSeen++;

// ★v17：识别**只看编号**。这个位置当前的宏/技能编号是我们图库的编号，
                        var effId = InReservedRange(id) ? id : 0u;

                        if (effId != 0)
                        {
                            macroSweepReserved++;
                            // ★v13 三个槽独立写（FrameIcon 不再只是备胎）—— 同 AttachKernelToIcon
                            var slotWrote = 0;
                            if (icon->IconImage != null)
                                slotWrote += SwapTextureIfOurs(GetImageNodeTexture(icon->IconImage), effId);
                            if (icon->FrameIcon != null)
                                slotWrote += SwapTextureIfOurs(GetImageNodeTexture(icon->FrameIcon), effId);
                            if (icon->Texture != null)
                                slotWrote += SwapTextureIfOurs(&icon->Texture->AtkTexture, effId);
                            hits += slotWrote;

// ★v15 切页急救：一个槽都没换上，并且三个槽里也没有任何
                            if (slotWrote == 0 && !AnySlotIsOurKernel(icon, effId))
                                RebuildReservedIconSlot(icon, effId);
                        }
                        else
                        {
// ★v17 归还：这个位置现在的编号已经不是我们的了，但槽里还占着我们那张
                            if (macroKernels.Count > 0) RevertIconSlotIfForeign(icon, id);

// 替换规则命中（原编号 → 目标编号）。两种目标分别处理：
                            uint ruleTo = 0;
                            if (id != 0 && iconRuleMap.TryGetValue(id, out ruleTo))
                            {
                                macroSweepRuled++;
                                if (InReservedRange(ruleTo))
                                {
                                    var tex = icon->IconImage != null ? GetImageNodeTexture(icon->IconImage) : null;
                                    if (tex == null && icon->FrameIcon != null) tex = GetImageNodeTexture(icon->FrameIcon);
                                    hits += SwapTextureToKernel(tex, ruleTo, id);
                                    if (icon->Texture != null)
                                        hits += SwapTextureToKernel(&icon->Texture->AtkTexture, ruleTo, id);
                                }
                                else
                                {
                                    hits += OverlayIconTexture(icon, id, ruleTo);
                                }
                            }
                        }
                    }

                    // 组件内部的节点树同样挂在 UldManager.NodeList 数组里，不在 ChildNode 链上
                    var nl2 = comp->UldManager.NodeList;
                    var c2 = comp->UldManager.NodeListCount;
                    if (nl2 != null && IsUserPointer(nl2) && c2 > 0 && c2 <= MacroIconNodeBudget)
                    {
                        for (var k = 0; k < c2; k++)
                        {
                            if (Environment.TickCount64 >= deadline) break;
                            var nn = nl2[k];
                            if (nn == null || !IsUserPointer(nn) || nn == node) continue;   // 防自引用死循环 + 野指针
                            hits += WalkMacroIconNode(nn, depth + 1, deadline);
                        }
                    }
                }
            }
        }
        catch { /* 单个节点异常不影响其它节点 */ }

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
        {
            if (Environment.TickCount64 >= deadline) break;
            // 链上出现野指针必须立刻**停**（不能再去取它的 NextSiblingNode —— 那一下就崩）
            if (!IsUserPointer(child)) break;
            hits += WalkMacroIconNode(child, depth + 1, deadline);
        }
        return hits;
    }

// 这个组件指针能不能安全地「当结构体用」？（判据只读字段 + 算术，不调虚函数）
    private static unsafe bool ComponentAccessOk(AtkComponentBase* comp, AtkResNode* node)
    {
        if (comp == null) return false;

        var addr = (ulong)comp;
        if (addr <= 0x10000 || addr >= 0x00007FFFFFFF0000) return false;   // 明显不是用户态地址

        try
        {
            if (node != null
                && ((nint)comp->AtkResNode == (nint)node || (nint)comp->OwnerNode == (nint)node))
            {
                return true;
            }

            var nl = comp->UldManager.NodeList;
            var cnt = (int)comp->UldManager.NodeListCount;
            if (cnt < 0 || cnt > MacroIconNodeBudget) return false;
            if (nl == null) return true;

            var nlp = (ulong)nl;
            return nlp > 0x10000 && nlp < 0x00007FFFFFFF0000;
        }
        catch
        {
            return false;
        }
    }

// ★v14 指针范围守卫：这个地址像不像一个「能安全解引用的用户态指针」？
    private static unsafe bool IsUserPointer(void* p)
    {
        var a = (ulong)p;
        return a > 0x10000 && a < 0x00007FFFFFFF0000;
    }

// ★v14 安全门：现在能不能安全地碰 UI 节点树？
    private static unsafe bool IsUiSafeToTouch()
    {
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null) return false;
            var um = stage->RaptureAtkUnitManager;
            if (um == null) return false;
            if (um->GetAddonByName("LoadingScreen", 1) != null) return false;
            if (um->GetAddonByName("BlackScreen", 1) != null) return false;
            if (um->GetAddonByName("_LoadingScreen", 1) != null) return false;
            return true;
        }
        catch { return false; }
    }

    private static unsafe AtkTexture* GetImageNodeTexture(AtkImageNode* image)
    {
        if (image == null || !IsUserPointer(image)) return null;

        var parts = image->PartsList;
// PartCount 上限校验：内存被回收复用时 PartsList 本身可能就是垃圾地址，拿它当下标 = 访问违例。
        if (parts == null || !IsUserPointer(parts)) return null;
        if (parts->Parts == null || !IsUserPointer(parts->Parts)) return null;
        if (parts->PartCount == 0 || parts->PartCount > 512) return null;

        var partId = image->PartId;
        if (partId >= parts->PartCount) partId = 0;

        var asset = parts->Parts[partId].UldAsset;
        if (asset == null || !IsUserPointer(asset)) return null;

        return &asset->AtkTexture;
    }

// 贴图槽归我们管就换上我们的内核贴图。
    private static unsafe int SwapTextureIfOurs(AtkTexture* texture, uint idHint)
    {
        if (texture == null) return 0;

        var id = idHint;
        if (id == 0)
        {
            if (texture->TextureType != TextureType.Resource) return 0;
            var res = texture->Resource;
            if (res == null) return 0;
            id = res->IconId;
        }

        if (!InReservedRange(id)) return 0;

        var kernel = macroKernels.TryGetValue(id, out var k) ? k : IntPtr.Zero;
        if (kernel == IntPtr.Zero)
        {
            EnsureMacroKernel(id);      // 还没做好 → 让后台去做，这一帧不动它
            return 0;
        }

        // 已经是我们写进去的那张 → 什么都不用做（这是每帧守卫的关键，不然会疯狂重写）
        if (texture->TextureType == TextureType.KernelTexture
            && (nint)texture->KernelTexture == kernel)
            return 0;

        IncRefKernel(kernel);
        texture->KernelTexture = (CsGameTexture*)kernel;
        texture->TextureType = TextureType.KernelTexture;
        return 1;
    }

// 替换规则专用：把贴图槽换成「图库编号 libId」的内核贴图，并按原编号 fromId 留还原底。
    private static unsafe int SwapTextureToKernel(AtkTexture* texture, uint libId, uint fromId)
    {
        if (texture == null || !InReservedRange(libId)) return 0;

        var kernel = macroKernels.TryGetValue(libId, out var k) ? k : IntPtr.Zero;
        if (kernel == IntPtr.Zero)
        {
            EnsureMacroKernel(libId);   // 还没做好 → 让后台去做，这一帧不动它
            return 0;
        }

        // 先留底再写：还原只认记录，记录丢了就永远还原不回来
        NoteIconSlot(texture, fromId, libId, kernel);

        if (texture->TextureType == TextureType.KernelTexture
            && (nint)texture->KernelTexture == kernel)
            return 0;                   // 已经是我们的贴图 → 什么都不做

        IncRefKernel(kernel);
        texture->KernelTexture = (CsGameTexture*)kernel;
        texture->TextureType = TextureType.KernelTexture;
        return 1;
    }

// 把内核贴图写进槽之前必须补一次引用。★ 这是花屏→崩驱动那个事故的修复点：
    private static unsafe void IncRefKernel(IntPtr kernel)
    {
        if (kernel == IntPtr.Zero) return;
        try { ((CsGameTexture*)kernel)->IncRef(); } catch { /* 忽略 */ }
    }

// 「某个图库编号的图内容变了 / 这条没了」→ 把按编号索引的缓存全部作废，重建一次。
    private static void InvalidateIconCaches(uint id)
    {
        if (!InReservedRange(id)) return;
        try
        {
            detourWrapCache.Remove(id);                 // detour 通道（指令收纳 / 工具栏 / 浏览器 GetIconTexture）
            macroKernels.TryRemove(id, out _);          // 我们自己的内核贴图（宏图标 / 规则换图）
            macroKernelFailed.TryRemove(id, out _);     // 清掉「失败」记录，否则不会重建
            macroKernelPending.TryRemove(id, out _);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>把一个图库编号的本地图做成游戏内核贴图（后台线程做，按编号只做一次）。</summary>
    private static void EnsureMacroKernel(uint id)
    {
        if (macroKernels.ContainsKey(id)) return;
        if (macroKernelFailed.ContainsKey(id)) return;
        if (!macroKernelPending.TryAdd(id, 0)) return;

        var s = selfRef;
        var entry = s?.config.BrowserImages.FirstOrDefault(b => b.Id == id);
        var path = entry?.Path ?? "";

        _ = Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    macroKernelFailed.TryAdd(id, 0);
                    macroIconKernelDiag = "编号 " + id + " 跳过：文件不存在";
                    return;
                }

                var provider = GetHostTextureProvider();
                if (provider == null)
                {
                    macroKernelFailed.TryAdd(id, 0);
                    macroIconKernelDiag = "编号 " + id + " 跳过：TextureProvider 不可用";
                    return;
                }

                var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                var source = await provider.CreateFromImageAsync(bytes, "ToolbarIconPlus:icon").ConfigureAwait(false);
                if (source == null)
                {
                    macroKernelFailed.TryAdd(id, 0);
                    macroIconKernelDiag = "编号 " + id + " 失败：解码为空";
                    return;
                }

                var fitArgs = new TextureModificationArgs { NewWidth = MacroIconKernelSize, NewHeight = MacroIconKernelSize };
                var fitted = await provider
                    .CreateFromExistingTextureAsync(source, fitArgs, false, "ToolbarIconPlus:icon80")
                    .ConfigureAwait(false);
                if (fitted == null)
                {
                    macroKernelFailed.TryAdd(id, 0);
                    macroIconKernelDiag = "编号 " + id + " 失败：缩放为空";
                    return;
                }

                var kernel = provider.ConvertToKernelTexture(fitted, true);
                if (kernel == IntPtr.Zero)
                {
                    macroKernelFailed.TryAdd(id, 0);
                    macroIconKernelDiag = "编号 " + id + " 失败：转内核贴图返回 0";
                    return;
                }

                // 补一次引用：我们持有它，游戏释放自己那份时不会把我们正在用的图抽走
                // ★ lambda 不继承外层方法的 unsafe 上下文，指针必须放进 unsafe 块（CS0214）
                try { unsafe { ((CsGameTexture*)kernel)->IncRef(); } } catch { /* 忽略 */ }

                macroKernelKeeps.Enqueue(fitted);   // 见字段注释：wrap 绝不能被回收
                macroKernels[id] = kernel;
                macroIconKernelDiag = "编号 " + id + " 就绪 ← " + path;
                Log("宏图标贴图就绪：编号 " + id + " ← " + path);
            }
            catch (Exception e)
            {
                macroKernelFailed.TryAdd(id, 0);
                macroIconKernelDiag = "编号 " + id + " 异常：" + e.Message;
                Log("宏图标贴图构建失败（编号 " + id + "）：" + e, true);
            }
            finally { macroKernelPending.TryRemove(id, out _); }
        });
    }

    private static ITextureProvider? GetHostTextureProvider()
    {
        try
        {
            var p = DalamudServices.TextureProvider;
            if (p != null) return p;
        }
        catch { /* 落到反射兜底 */ }

        try
        {
            if (GetHostService("TextureProvider") is ITextureProvider typed) return typed;
        }
        catch { /* 忽略 */ }

        return null;
    }

    // ------------------------------ 加载期拦截（原生 hook） ------------------------------

// AtkComponentIcon.LoadIcon 的签名：bool LoadIcon(AtkComponentIcon* this, uint iconId)。
    private unsafe delegate bool AtkComponentIconLoadIconDelegate(AtkComponentIcon* thisPtr, uint iconId);

// AtkTexture.LoadIconTexture 的签名：
    private unsafe delegate bool AtkTextureLoadIconTextureDelegate(AtkTexture* thisPtr, int iconId, IconSubFolder subFolder);

// 从 FFXIVClientStructs 的 `&lt;Type&gt;.Addresses.&lt;成员&gt;` 里取原生函数地址。
    private static IntPtr ResolveFcsAddress(Type owner, string member)
    {
        try
        {
            var nested = owner.GetNestedType(
                "Addresses", BindingFlags.Public | BindingFlags.NonPublic);
            if (nested == null) return IntPtr.Zero;

            const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            object? holder = null;
            var fld = nested.GetField(member, S);
            if (fld != null) holder = fld.GetValue(null);
            if (holder == null)
            {
                var prop = nested.GetProperty(member, S);
                if (prop != null) holder = prop.GetValue(null, null);
            }
            if (holder == null) return IntPtr.Zero;

            // 拿到的可能是 IntPtr 本身，也可能是 Address 对象（值在它的 Value 成员里）
            if (holder is IntPtr direct) return direct;

            var ht = holder.GetType();
            var mm = ht.GetMember("Value",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault();
            object? raw = null;
            if (mm is FieldInfo f2) raw = f2.GetValue(holder);
            else if (mm is PropertyInfo p2) raw = p2.GetValue(holder, null);
            if (raw is IntPtr ip) return ip;
            return IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>取 AtkComponentIcon.LoadIcon 的原生地址（组件级通道，v11 起在用）。</summary>
    private static IntPtr ResolveIconLoadAddress() =>
        ResolveFcsAddress(typeof(AtkComponentIcon), "LoadIcon");

    /// <summary>取 AtkTexture.LoadIconTexture 的原生地址（★v22 贴图层主通道）。</summary>
    private static IntPtr ResolveAtkTextureLoadIconAddress() =>
        ResolveFcsAddress(typeof(AtkTexture), "LoadIconTexture");

// 取 IGameInteropProvider。两条路任一条成即可：
    private static IGameInteropProvider? ResolveInteropProvider()
    {
        try
        {
            var pi = DalamudServices.PluginInterface;
            if (pi != null)
            {
                var gm = pi.GetType().GetMethod("GetService", new[] { typeof(Type) });
                var got = gm?.Invoke(pi, new object[] { typeof(IGameInteropProvider) });
                if (got is IGameInteropProvider p1) return p1;
            }
        }
        catch { /* 落到下一条 */ }

        try
        {
            if (GetDalamudService(typeof(IGameInteropProvider)) is IGameInteropProvider p2) return p2;
        }
        catch { /* 落到返回 null */ }

        return null;
    }

// 挂 AtkComponentIcon.LoadIcon 的原生 hook。
    private static unsafe void InstallMacroIconHook()
    {
        if (macroIconLoadHook != null) return;
        try
        {
            var addr = ResolveIconLoadAddress();
            if (addr == IntPtr.Zero)
            {
                macroIconHookState = "地址解析失败（客户端版本不匹配？）";
                Log("宏图标 hook 未挂：" + macroIconHookState, true);
                return;
            }

            var interop = ResolveInteropProvider();
            if (interop == null)
            {
                macroIconHookState = "IGameInteropProvider 取不到";
                Log("宏图标 hook 未挂：" + macroIconHookState, true);
                return;
            }

            macroIconLoadHook = interop.HookFromAddress<AtkComponentIconLoadIconDelegate>(
                addr, DetourIconLoadIcon, IGameInteropProvider.HookBackend.Automatic);
            macroIconLoadHook.Enable();
            macroIconHookState = "已挂 @ 0x" + addr.ToInt64().ToString("X");
            Log("宏图标 hook 已挂：AtkComponentIcon.LoadIcon " + macroIconHookState);

        }
        catch (Exception e)
        {
            macroIconLoadHook = null;
            macroIconHookState = "挂载失败：" + e.GetType().Name;
            Log("宏图标 hook 挂载失败（退回纯扫描）：" + e, true);
        }
    }

// ★v22 挂 AtkTexture.LoadIconTexture 的原生 hook —— 贴图层主通道。
    private static unsafe void InstallMacroTextureHook()
    {
        if (macroTexHook != null) return;
        try
        {
            var addr = ResolveAtkTextureLoadIconAddress();
            if (addr == IntPtr.Zero)
            {
                macroTexHookState = "地址解析失败（FCS 无 AtkTexture.Addresses.LoadIconTexture？）";
                Log("宏图标贴图层 hook 未挂：" + macroTexHookState, true);
                return;
            }

            var interop = ResolveInteropProvider();
            if (interop == null)
            {
                macroTexHookState = "IGameInteropProvider 取不到";
                Log("宏图标贴图层 hook 未挂：" + macroTexHookState, true);
                return;
            }

            macroTexHook = interop.HookFromAddress<AtkTextureLoadIconTextureDelegate>(
                addr, DetourAtkTextureLoadIcon, IGameInteropProvider.HookBackend.Automatic);
            macroTexHook.Enable();
            macroTexHookState = "已挂 @ 0x" + addr.ToInt64().ToString("X");
            Log("宏图标贴图层 hook 已挂：AtkTexture.LoadIconTexture " + macroTexHookState);
        }
        catch (Exception e)
        {
            macroTexHook = null;
            macroTexHookState = "挂载失败：" + e.GetType().Name;
            Log("宏图标贴图层 hook 挂载失败（退回组件级 + 扫描）：" + e, true);
        }
    }

    private static void UninstallMacroTextureHook()
    {
        try { macroTexHook?.Dispose(); } catch { /* 忽略 */ }
        macroTexHook = null;
        macroTexHookState = "未挂";
    }

    private static void UninstallMacroIconHook()
    {
        try { macroIconLoadHook?.Dispose(); } catch { /* 忽略 */ }
        macroIconLoadHook = null;
        macroIconHookState = "未挂";
    }

// RaptureShellModule.ExecuteMacro 的签名：void ExecuteMacro(RaptureShellModule* this, Macro* macro)。
    private unsafe delegate void ExecuteMacroDelegate(RaptureShellModule* shell, RaptureMacroModule.Macro* macro);

    /// <summary>从 CS 的 Addresses 里取 RaptureShellModule.ExecuteMacro 的原生地址（反射，同 LoadIcon 的套路）。</summary>
    private static IntPtr ResolveMacroExecAddress()
    {
        try
        {
            var nested = typeof(RaptureShellModule).GetNestedType(
                "Addresses", BindingFlags.Public | BindingFlags.NonPublic);
            if (nested == null) return IntPtr.Zero;

            const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            object? holder = null;
            var fld = nested.GetField("ExecuteMacro", S);
            if (fld != null) holder = fld.GetValue(null);
            if (holder == null)
            {
                var prop = nested.GetProperty("ExecuteMacro", S);
                if (prop != null) holder = prop.GetValue(null, null);
            }
            if (holder == null) return IntPtr.Zero;
            if (holder is IntPtr direct) return direct;

            var ht = holder.GetType();
            var mm = ht.GetMember("Value",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault();
            object? raw = null;
            if (mm is FieldInfo f2) raw = f2.GetValue(holder);
            else if (mm is PropertyInfo p2) raw = p2.GetValue(holder, null);
            return raw is IntPtr ip ? ip : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    private static unsafe void InstallMacroExecHook()
    {
        if (macroExecHook != null) return;
        try
        {
            var addr = ResolveMacroExecAddress();
            if (addr == IntPtr.Zero)
            {
                macroExecHookState = "地址解析失败（客户端版本不匹配？）";
                Log("宏执行 hook 未挂：" + macroExecHookState, true);
                return;
            }
            var interop = ResolveInteropProvider();
            if (interop == null)
            {
                macroExecHookState = "IGameInteropProvider 取不到";
                Log("宏执行 hook 未挂：" + macroExecHookState, true);
                return;
            }
            macroExecHook = interop.HookFromAddress<ExecuteMacroDelegate>(
                addr, DetourExecuteMacro, IGameInteropProvider.HookBackend.Automatic);
            macroExecHook.Enable();
            macroExecHookState = "已挂 @ 0x" + addr.ToInt64().ToString("X");
            Log("宏执行 hook 已挂：RaptureShellModule.ExecuteMacro " + macroExecHookState);
        }
        catch (Exception e)
        {
            macroExecHook = null;
            macroExecHookState = "挂载失败：" + e.GetType().Name;
            Log("宏执行 hook 挂载失败（退回宏行轮询）：" + e, true);
        }
    }

    private static void UninstallMacroExecHook()
    {
        try { macroExecHook?.Dispose(); } catch { /* 忽略 */ }
        macroExecHook = null;
        macroExecHookState = "未挂";
    }

// ExecuteMacro 的 detour：宏开始执行的那一刻就能拿到宏数据指针 ——
    private static unsafe void DetourExecuteMacro(RaptureShellModule* shell, RaptureMacroModule.Macro* macro)
    {
        var hook = macroExecHook;
        try { hook?.Original(shell, macro); } catch { /* 原函数异常不能拦 */ }

        try
        {
            if (macro == null) return;
            macroExecSeen++;

            var name = "";
            try { name = macro->Name.ToString() ?? ""; } catch { /* 名字读不到不致命 */ }

            // 行数组紧跟 Name（Utf8String=0x68，已用程序集 ClassLayout 双向核实）。
            var linePtr = &macro->Name;
            for (var ln = 0; ln < 15; ln++)
            {
                linePtr = linePtr + 1;
                string raw;
                try { raw = linePtr->ToString() ?? ""; }
                catch { break; }
                if (raw.Trim().Length == 0) continue;

                var text = NormalizeMacroLine(raw);
                if (!TrySplitOwnCommand(text, out var args)) continue;

                macroExecHits++;
                macroExecLastDiag = "宏「" + MacroLineForLog(name, 16) + "」行" + (ln + 1);
                Log("宏执行钩子：宏「" + name + "」第 " + (ln + 1)
                    + " 行「" + MacroLineForLog(raw, 80) + "」命中本模块指令，参数=" + args);
// ★v11：宏执行钩子从此**只做观察**（纯诊断通道）——
                break;                      // 一个宏只观察第一条命中行
            }

            if (macroExecHits == 0) macroExecLastDiag = "宏「" + MacroLineForLog(name, 16) + "」无命中";
        }
        catch { /* 观察绝不把游戏带崩 */ }
    }

// 用真实占位图标走一遍游戏自己的加载流程。两处用到它：
    private static unsafe bool LoadPlaceholderIcon(Hook<AtkTextureLoadIconTextureDelegate> hook,
                                                   AtkTexture* tex, IconSubFolder subFolder)
    {
        for (var i = 0; i < MacroIconPlaceholders.Length; i++)
        {
            try
            {
                if (hook.Original(tex, (int)MacroIconPlaceholders[i], subFolder)) return true;
            }
            catch { /* 换下一个 */ }
        }
        return false;
    }

// ★v22 贴图层 detour ——「载体编号」方案的落地。
    private static unsafe bool DetourAtkTextureLoadIcon(AtkTexture* thisPtr, int iconId, IconSubFolder subFolder)
    {
        var hook = macroTexHook;
        if (hook == null) return false;
        if (thisPtr == null) return false;

        // ---- 铁律①：热路径直通，开销只有一次范围判断 ----
        if (!InReservedRange((uint)iconId))
            return hook.Original(thisPtr, iconId, subFolder);

        macroTexHookCalls++;

        try
        {
            var libId = (uint)iconId;
            if (!macroKernels.TryGetValue(libId, out var kernel) || kernel == IntPtr.Zero)
            {
                // ---- 铁律②：内核还没做好 → 催后台 + 这一帧用真实占位图顶住 ----
                EnsureMacroKernel(libId);
                return LoadPlaceholderIcon(hook, thisPtr, subFolder);
            }

            // 槽里已经是我们这张内核贴图 → 什么都不做，免得对同一张图反复 IncRef。
            if (thisPtr->TextureType == TextureType.KernelTexture
                && (nint)thisPtr->KernelTexture == kernel)
                return true;

            // 旧内容本身就是内核贴图（多半是上一轮我们自己写的）→ 直接覆盖，无游戏资源需要回收。
            if (thisPtr->TextureType == TextureType.KernelTexture)
            {
                thisPtr->KernelTexture = (CsGameTexture*)kernel;
                macroTexHookHits++;
                return true;
            }

            // ---- 铁律③：旧内容是游戏资源 / 空 → 先借占位图走一遍游戏自己的加载（它会把旧的卸干净），
            //      再把贴图槽换成我们的内核贴图。全程不碰游戏的引用计数。 ----
            if (!LoadPlaceholderIcon(hook, thisPtr, subFolder))
                return false;

            IncRefKernel(kernel);
            thisPtr->KernelTexture = (CsGameTexture*)kernel;
            thisPtr->TextureType = TextureType.KernelTexture;
            macroTexHookHits++;
            return true;
        }
        catch
        {
            try { return hook.Original(thisPtr, iconId, subFolder); } catch { return false; }
        }
    }

// LoadIcon 的 detour。三条铁律：
    private static unsafe bool DetourIconLoadIcon(AtkComponentIcon* thisPtr, uint iconId)
    {
        macroIconHookCalls++;

        var hook = macroIconLoadHook;
        if (hook == null) return false;

        var srcIsLib = InReservedRange(iconId);
        if (srcIsLib) macroIconHookReserved++;

// 规则命中（原编号 → 目标编号）。From 恒为游戏真实编号，见 RebuildIconRuleMap。
        uint ruleTo = 0;
        var hasRule = !srcIsLib && iconRuleMap.TryGetValue(iconId, out ruleTo);
        if (hasRule) macroIconHookRuled++;

        var ok = false;
        try
        {
            if (hasRule)
            {
                if (InReservedRange(ruleTo))
                {
                    // 目标是我们图库的图：原编号本来就游戏认得的，先让它正常加载（不必用占位），
                    // 再把贴图槽换成目标编号的内核贴图。
                    ok = hook.Original(thisPtr, iconId);
                    if (AttachKernelToIcon(thisPtr, ruleTo, iconId) > 0) iconRuleSwaps++;
                }
                else
                {
                    // 目标是游戏自己的图标：原编号照常加载（组件的 IconId 保持原编号，宏数据干净），
                    // 再把贴图槽覆盖成目标编号的原生贴图 —— 走游戏自己的加载通道，引用计数它自己管。
                    ok = hook.Original(thisPtr, iconId);
                    if (ok && OverlayIconTexture(thisPtr, iconId, ruleTo) > 0) iconRuleSwaps++;
                }
            }
            else if (srcIsLib)
            {
// 保留段编号：先按原样跑一次 —— ★v22 起这一步通常**直接成功**：贴图层 hook 会接住
                ok = hook.Original(thisPtr, iconId);
                for (var i = 0; i < MacroIconPlaceholders.Length && !ok; i++)
                    ok = hook.Original(thisPtr, MacroIconPlaceholders[i]);
                if (AttachKernelToIcon(thisPtr, iconId, iconId) == 0)
                    LogIfSlotNotOurs(thisPtr, iconId, "uint");
                else if (thisPtr->IconId != iconId)
// ★v15：用占位图标建槽会把组件 IconId 改写成占位编号 —— 必须写回。
                    thisPtr->IconId = iconId;
            }
            else
            {
                // 普通图标（绝大多数调用）：纯转发，开销只有一次字典查询
                ok = hook.Original(thisPtr, iconId);
            }
        }
        catch { /* 换图失败绝不能影响游戏 */ }

        // 原函数没能成功（占位编号都不存在 / hook 已被释放）——同样计入诊断，避免"静默空白"
        if ((srcIsLib || hasRule) && !ok) macroIconHookErrors++;

        // ★ 存活诊断：这条日志不看扫描、不看心跳，只要游戏调过一次 LoadIcon 就会由它自己打出来。
        //   之前几轮翻车的根源就是"分不清是 hook 没挂上、还是挂了但游戏没调"——有这行就能分辨。
        var now = Environment.TickCount64;
        if (now - lastMacroHookLogTick >= 15000)
        {
            lastMacroHookLogTick = now;
            // 只看"会变的量"：数量不变就闭嘴，免得挂机时刷屏；一变就立刻打一行
            var sig = macroIconHookReserved + "/" + macroIconHookRuled + "/" + macroIconHookSwaps
                      + "/" + iconRuleSwaps + "/" + macroIconHookErrors
                      + "/" + macroTexHookCalls + "/" + macroTexHookHits
                      + "/" + macroKernels.Count + "/" + macroKernelFailed.Count;
            if (sig != lastMacroHookLogSig)
            {
                lastMacroHookLogSig = sig;
                Log("宏图标 hook 存活：LoadIcon 调用=" + macroIconHookCalls
                    + " 其中保留段=" + macroIconHookReserved + " 其中规则=" + macroIconHookRuled
                    + " 已换槽=" + macroIconHookSwaps + "（规则换上=" + iconRuleSwaps + "）"
                    + " 原函数失败=" + macroIconHookErrors
                    + " ｜ ★贴图层 " + macroTexHookState + "（拦=" + macroTexHookCalls + " 换=" + macroTexHookHits + "）"
                    + " ｜ 内核贴图 就绪=" + macroKernels.Count
                    + " 构建中=" + macroKernelPending.Count + " 失败=" + macroKernelFailed.Count);
            }
        }

        return ok;
    }

// 内核就绪却没有任何槽是内核贴图时打一行组件状态（取证「热键栏切页后空白」用）。
    private static unsafe void LogIfSlotNotOurs(AtkComponentIcon* icon, uint libId, string source)
    {
        try
        {
            if (icon == null) return;
            if (!macroKernels.TryGetValue(libId, out var kernel) || kernel == IntPtr.Zero)
                return;                                  // 内核还没就绪 → 属正常等待，不算异常
            if (AnySlotIsKernel(icon, kernel)) return;   // 槽已就位 → 健康
            var now = Environment.TickCount64;
            if (now - lastMacroDataDiagTick < 5000) return;
            var desc = DescribeIconState(icon);
            if (desc == lastMacroDataDiagSig) return;
            lastMacroDataDiagTick = now;
            lastMacroDataDiagSig = desc;
            Log("宏图标[" + source + "] 保留段未换槽：" + desc, true);
        }
        catch { /* 取证绝不把游戏带崩 */ }
    }

    /// <summary>三个已知槽（IconImage / FrameIcon / Texture）里是否有任何一个已是这张内核贴图。</summary>
    private static unsafe bool AnySlotIsKernel(AtkComponentIcon* icon, IntPtr kernel)
    {
        try
        {
            if (icon->IconImage != null)
            {
                var t = GetImageNodeTexture(icon->IconImage);
                if (t != null && t->TextureType == TextureType.KernelTexture
                    && (nint)t->KernelTexture == kernel) return true;
            }
            if (icon->FrameIcon != null)
            {
                var t = GetImageNodeTexture(icon->FrameIcon);
                if (t != null && t->TextureType == TextureType.KernelTexture
                    && (nint)t->KernelTexture == kernel) return true;
            }
            if (icon->Texture != null)
            {
                var t = &icon->Texture->AtkTexture;
                if (t->TextureType == TextureType.KernelTexture
                    && (nint)t->KernelTexture == kernel) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>把图标组件的关键槽位拍成一行可对比的字符串（取证 / 限频签名共用）。</summary>
    private static unsafe string DescribeIconState(AtkComponentIcon* icon)
    {
        try
        {
            if (icon == null) return "comp=null";
            string Tex(AtkTexture* t)
            {
                if (t == null) return "空";
                return "0x" + ((nint)t).ToString("X")
                     + "/类型" + t->TextureType.ToString()
                     + "/内核" + ((nint)t->KernelTexture).ToString("X");
            }
            var img = icon->IconImage != null ? GetImageNodeTexture(icon->IconImage) : null;
            var frame = icon->FrameIcon != null ? GetImageNodeTexture(icon->FrameIcon) : null;
            return "comp=0x" + ((nint)icon).ToString("X")
                 + " IconId=" + icon->IconId
                 + " IconImage=" + (icon->IconImage == null ? "空" : Tex(img))
                 + " FrameIcon=" + (icon->FrameIcon == null ? "空" : Tex(frame))
                 + " Texture=" + (icon->Texture == null ? "空" : Tex(&icon->Texture->AtkTexture))
                 + " 内核就绪=" + macroKernels.Count;
        }
        catch (Exception e)
        {
            return "描述失败(" + e.GetType().Name + ")";
        }
    }

    /// <summary>单个贴图槽是不是「编号 id 对应的那张内核贴图」。</summary>
    private static unsafe bool IconSlotIsKernel(AtkTexture* tex, IntPtr kernel)
    {
        if (tex == null || kernel == IntPtr.Zero) return false;
        try { return tex->TextureType == TextureType.KernelTexture && (nint)tex->KernelTexture == (nint)kernel; }
        catch { return false; }
    }

// 组件的三个贴图槽里，有没有任何一个已经指向「编号 id 的内核贴图」？
    private static unsafe bool AnySlotIsOurKernel(AtkComponentIcon* icon, uint id)
    {
        if (icon == null) return false;
        if (!macroKernels.TryGetValue(id, out var kernel) || kernel == IntPtr.Zero) return false;
        if (icon->IconImage != null && IconSlotIsKernel(GetImageNodeTexture(icon->IconImage), kernel)) return true;
        if (icon->FrameIcon != null && IconSlotIsKernel(GetImageNodeTexture(icon->FrameIcon), kernel)) return true;
        if (icon->Texture != null && IconSlotIsKernel(&icon->Texture->AtkTexture, kernel)) return true;
        return false;
    }

// ★v17 归还：槽里还占着**我们的某张内核贴图**，但组件此刻的编号已经不是保留段编号了
    private static unsafe void RevertIconSlotIfForeign(AtkComponentIcon* icon, uint id)
    {
        if (icon == null || InReservedRange(id)) return;
        if (macroKernels.Count == 0) return;
        if (!AnyForeignSlotIsOurs(icon)) return;      // 槽本来就干净 → 一个字节都不碰
        if (id == 0) { macroRevertUnknown++; return; } // 编号是 0：不知道该恢复成什么，只计数不动作

        var key = (nint)icon;
        var now = Environment.TickCount64;
        if (macroRevertBackoff.TryGetValue(key, out var until) && now < until) return;
        macroRevertBackoff[key] = now + 5000;

        try
        {
            var hook = macroIconLoadHook;
            if (hook != null) hook.Original(icon, id);   // 走 trampoline，不过我们自己的 detour
            else icon->LoadIcon(id);                     // hook 未挂时退化为 FCS 入口
            macroSlotReverts++;
        }
        catch { /* 归还失败不影响游戏（下次刷槽游戏自己也会纠正） */ }
    }

    /// <summary>三个槽里有没有任何一个还挂着**我们的**内核贴图（不看是哪一张）。</summary>
    private static unsafe bool AnyForeignSlotIsOurs(AtkComponentIcon* icon)
    {
        if (icon == null) return false;
        try
        {
            var t1 = icon->IconImage != null ? GetImageNodeTexture(icon->IconImage) : null;
            var t2 = icon->FrameIcon != null ? GetImageNodeTexture(icon->FrameIcon) : null;
            var t3 = icon->Texture != null ? &icon->Texture->AtkTexture : null;
            if (t1 == null && t2 == null && t3 == null) return false;

            // 快速闸门：三个槽里但凡没有一个处于 KernelTexture 状态，就根本不可能挂着我们的图。
            // 游戏界面上绝大多数槽都是 Resource ⇒ 这一句让每轮的开销几乎归零（不必遍历内核表）。
            var kernelLike = false;
            if (t1 != null && t1->TextureType == TextureType.KernelTexture) kernelLike = true;
            if (!kernelLike && t2 != null && t2->TextureType == TextureType.KernelTexture) kernelLike = true;
            if (!kernelLike && t3 != null && t3->TextureType == TextureType.KernelTexture) kernelLike = true;
            if (!kernelLike) return false;

            foreach (var kv in macroKernels)
            {
                var k = kv.Value;
                if (k == IntPtr.Zero) continue;
                if (IconSlotIsKernel(t1, k) || IconSlotIsKernel(t2, k) || IconSlotIsKernel(t3, k)) return true;
            }
        }
        catch { /* 校验失败按「干净」处理 → 不动它 */ }
        return false;
    }

// ★v15 切页急救：把一个「贴图槽被游戏卸载、只剩空壳」的保留段组件救回来。
    private static unsafe void RebuildReservedIconSlot(AtkComponentIcon* icon, uint id)
    {
        if (icon == null) return;
        var key = (nint)icon;
        var now = Environment.TickCount64;
        if (macroRebuildBackoff.TryGetValue(key, out var until) && now < until) return;
        if (!macroKernels.TryGetValue(id, out var kernel) || kernel == IntPtr.Zero)
        {
            EnsureMacroKernel(id);              // 内核还没做好 → 催一把，这一轮先放过
            return;
        }

        var ok = false;
        macroRebuildAttempts++;
        try
        {
            var hook = macroIconLoadHook;
            for (var i = 0; i < MacroIconPlaceholders.Length && !ok; i++)
            {
                var ph = MacroIconPlaceholders[i];
                // 优先走 trampoline（不过 detour，语义最干净）；hook 未挂时退化为 FCS 入口。
                if (hook != null) ok = hook.Original(icon, ph);
                else { icon->LoadIcon(ph); ok = true; }
            }
            if (!ok) { macroRebuildFails++; macroRebuildBackoff[key] = now + 5000; return; }

            if (AttachKernelToIcon(icon, id, id) == 0)
            {
                macroRebuildFails++;
                macroRebuildBackoff[key] = now + 5000;   // 槽还是没建起来 → 退避，别每轮空转
                return;
            }

// ★ 闭环关键：编号必须回到「宏数据里的那个」。
            if (icon->IconId != id) icon->IconId = id;   // 纠正被占位图标改掉的编号（这条必须留）
            macroSlotRebuilds++;
            macroRebuildBackoff.Remove(key);
        }
        catch { macroRebuildFails++; macroRebuildBackoff[key] = now + 5000; }
    }

    private static long lastMacroDataDiagTick;
    private static string lastMacroDataDiagSig = "";

// 把一个图标组件的贴图槽指向该图库编号的内核贴图（加载期调用，编号已知）。
    private static unsafe int AttachKernelToIcon(AtkComponentIcon* icon, uint libId, uint fromId)
    {
        if (icon == null || !InReservedRange(libId)) return 0;

// 只有「保留段编号直接写进宏」那条路才需要把 IconId 补回去 —— 游戏可能因为加载失败
        if (InReservedRange(fromId))
        {
            try { icon->IconId = libId; } catch { /* 忽略 */ }
        }

        if (!macroKernels.TryGetValue(libId, out var kernel) || kernel == IntPtr.Zero)
        {
            // 还没做好 → 让后台去做。游戏的图标组件会反复重设图标（选宏 / 开面板 / 刷新），
            // 做好之后的下一次 LoadIcon 就能命中；扫描那条路也会兜上。
            EnsureMacroKernel(libId);
            return 0;
        }

        var wrote = 0;
        try
        {
// ★v13 三个槽全部独立写（此前 FrameIcon 只是 IconImage 拿不到时的备胎）——
            if (icon->IconImage != null)
                wrote += WriteKernelSlot(GetImageNodeTexture(icon->IconImage), fromId, libId, kernel);
            if (icon->FrameIcon != null)
                wrote += WriteKernelSlot(GetImageNodeTexture(icon->FrameIcon), fromId, libId, kernel);
            if (icon->Texture != null)
                wrote += WriteKernelSlot(&icon->Texture->AtkTexture, fromId, libId, kernel);
        }
        catch { /* 单个槽失败不影响其它 */ }

        if (wrote > 0) macroIconHookSwaps++;
        return wrote;
    }

// 把一个贴图槽换成内核贴图（含 v11 守卫与还原留底）。返回是否真的写上。
    private static unsafe int WriteKernelSlot(AtkTexture* tex, uint fromId, uint libId, IntPtr kernel)
    {
        if (tex == null) return 0;
        try
        {
            if (tex->TextureType == TextureType.KernelTexture
                && (nint)tex->KernelTexture == kernel)
                return 0;                       // 已经是我们这张 → 什么都不做

            NoteIconSlot(tex, fromId, libId, kernel);
            IncRefKernel(kernel);
            tex->KernelTexture = (CsGameTexture*)kernel;
            tex->TextureType = TextureType.KernelTexture;
            return 1;
        }
        catch { return 0; }
    }

// 登记一个「被我们改过的贴图槽」。这是还原的唯一依据，所以每次写内存前都要先记。
    private static unsafe void NoteIconSlot(AtkTexture* tex, uint fromId, uint toId, IntPtr kernel)
    {
        if (tex == null || fromId == 0) return;
        try
        {
            var key = (IntPtr)tex;
            var rec = iconSlotPatches.GetOrAdd(key, _ => new IconSlotPatch());
            rec.From = fromId;
            rec.To = toId;
            rec.Kernel = kernel;                       // 0 = 走的是「游戏编号目标」那条路（槽里是原生资源）
            rec.LastSeen = Environment.TickCount64;
        }
        catch { /* 记录失败不影响替换本身 */ }
    }

// 替换规则的目标是**游戏编号**时：把组件的贴图槽覆盖成那个编号的原生贴图。
    private static unsafe int OverlayIconTexture(AtkComponentIcon* icon, uint fromId, uint toId)
    {
        if (icon == null) return 0;
        var wrote = 0;
        try
        {
            if (icon->Texture != null)
                wrote += OverlayIconSlot(&icon->Texture->AtkTexture, fromId, toId);

            var img = icon->IconImage != null ? GetImageNodeTexture(icon->IconImage) : null;
            if (img == null && icon->FrameIcon != null) img = GetImageNodeTexture(icon->FrameIcon);
            if (img != null) wrote += OverlayIconSlot(img, fromId, toId);
        }
        catch { /* 单个槽失败不影响其它 */ }
        return wrote;
    }

// 把一个贴图槽覆盖成目标编号的原生贴图。
    private static unsafe int OverlayIconSlot(AtkTexture* tex, uint fromId, uint toId)
    {
        if (tex == null || toId == 0) return 0;
        try
        {
            // 已经是对应图标 → 只给还原记录续期，不做任何写入
            if (tex->TextureType == TextureType.Resource && tex->Resource != null
                && tex->Resource->IconId == toId)
            {
                NoteIconSlot(tex, fromId, toId, IntPtr.Zero);
                return 0;
            }

            NoteIconSlot(tex, fromId, toId, IntPtr.Zero);
            tex->LoadIconTexture(toId, IconSubFolder.None);

            if (tex->TextureType == TextureType.Resource && tex->Resource != null
                && tex->Resource->IconId == toId)
                return 1;

            // 目标图没能加载出来 → 滚回原编号（槽不会停在坏状态上）
            tex->LoadIconTexture(fromId, IconSubFolder.None);
            return 0;
        }
        catch { return 0; }
    }

    // ------------------------------ 替换还原 ------------------------------

// 把一个被我们改过的贴图槽还原回原编号的原生贴图。
    private static unsafe bool RestoreIconSlot(IntPtr slot, IconSlotPatch rec)
    {
        if (slot == IntPtr.Zero || rec == null) return false;
        var tex = (AtkTexture*)slot;
        if (tex == null) return false;

        try
        {
            if (rec.Kernel != IntPtr.Zero)
            {
                // 还指着我们那张内核贴图吗？
                if (tex->TextureType != TextureType.KernelTexture) return false;
                if ((nint)tex->KernelTexture != rec.Kernel) return false;
            }
            else
            {
                // 目标是游戏编号那条路：槽里应该是那个编号的原生图标资源
                if (tex->TextureType != TextureType.Resource) return false;
                var res0 = tex->Resource;
                if (res0 == null || res0->IconId != rec.To) return false;
            }

            // 原编号是游戏真实编号 → 走游戏通道重载它的原生贴图
            if (rec.From != 0 && !InReservedRange(rec.From))
            {
                tex->LoadIconTexture(rec.From, IconSubFolder.None);
                if (tex->TextureType == TextureType.Resource && tex->Resource != null) return true;
            }

// 原编号本身就是我们的图库编号（宏里直接写了编号那种）→ 游戏没有这张图，
            tex->LoadIconTexture(MacroIconPlaceholders[0], IconSubFolder.None);
            return true;
        }
        catch { return false; }
    }

// 按条件还原一批槽（match 决定这批要处理哪些 From）。返回成功还原的个数。
    private static unsafe int RestoreIconPatches(Func<uint, bool> match)
    {
        var n = 0;
        try
        {
            foreach (var kv in iconSlotPatches)
            {
                if (kv.Value == null || !match(kv.Value.From)) continue;
                if (RestoreIconSlot(kv.Key, kv.Value)) n++;
                iconSlotPatches.TryRemove(kv.Key, out _);
            }
            iconRuleRestores += n;
        }
        catch { /* 忽略 */ }
        return n;
    }

    /// <summary>还原某条规则（原编号 from）影响到的所有槽，并清掉记录。返回还原个数。</summary>
    private static int RestoreIconPatchesFor(uint from) => RestoreIconPatches(f => f == from);

    /// <summary>还原全部被我们改过的槽（停用 / 卸载模块时调，绝不留悬空指针）。</summary>
    private static int RestoreAllIconPatches() => RestoreIconPatches(_ => true);

    // ------------------------------ 面板区块 ------------------------------

    private void DrawMacroIconSection(ref bool changed)
    {
        try
        {
            ImGui.Separator();

            var on = config.MacroIconSwap;
            if (ImGui.Checkbox("启用图标替换（图库图贴上界面 / 下面的替换规则生效）##macroswap", ref on))
            {
                config.MacroIconSwap = on;
                changed = true;
                SaveOwnConfig();
            }
        }
        catch (Exception e)
        {
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), "宏图标区块异常：" + e.Message);
        }
    }

// 「图标替换规则」区块：左边原编号、右边目标编号 —— 添加建规则、删除撤规则并把界面还原回去。
    private void DrawIconReplaceSection(ref bool changed)
    {
        try
        {
            ImGui.Separator();
            ImGui.TextUnformatted("图标替换规则（原编号 → 目标编号）");

            ImGui.SetNextItemWidth(110);
            ImGui.InputText("原编号##rulefrom", ref ruleFromInput, 16);
            ImGui.SameLine();
            ImGui.TextUnformatted("→");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110);
            ImGui.InputText("替换为##ruleto", ref ruleToInput, 16);
            ImGui.SameLine();
            if (ImGui.Button("添加##ruleadd")) AddIconRuleFromInputs(ref changed);
            ImGui.SameLine();
            if (ImGui.Button("删除##ruledel")) RemoveIconRuleFromInputs(ref changed);
            ImGui.SameLine();
            if (ImGui.Button("全部清除##ruleclear")) ClearAllIconRules(ref changed);

            if (config.IconReplaceRules.Count == 0)
            {
                return;
                return;
            }

            // 规则列表。删除会让 List 变短，所以删完立刻 break，下一帧再画剩下的。
            for (var i = 0; i < config.IconReplaceRules.Count; i++)
            {
                var r = config.IconReplaceRules[i];
                if (r == null) continue;

                ImGui.PushID("iconrule_" + i);
                ImGui.TextUnformatted(r.From + "  →  " + r.To + "   " + DescribeIconId(r.To));
                ImGui.SameLine();
                if (ImGui.Button("删除"))
                {
                    RemoveIconRuleAt(i, ref changed);
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }
        }
        catch (Exception e)
        {
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), "图标替换区块异常：" + e.Message);
        }
    }

    /// <summary>把编号描述成人话（图库编号显示图名；游戏编号直说；保留段里查不到的明确标红字）。</summary>
    private string DescribeIconId(uint id)
    {
        if (!InReservedRange(id)) return "（游戏图标）";
        var e = config.BrowserImages.FirstOrDefault(b => b.Id == id);
        return e == null ? "（图库里没这个编号！）" : "（图库：" + e.Name + "）";
    }

    /// <summary>宽松解析编号：接受十进制，也接受 0x 前缀的十六进制（保留段编号很长，十六进制更好念）。</summary>
    private static bool TryParseIconNumber(string s, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out id);
        return uint.TryParse(s, out id);
    }

    private void AddIconRuleFromInputs(ref bool changed)
    {
        try
        {
            if (!TryParseIconNumber(ruleFromInput, out var from) || from == 0)
            {
                Chat("原编号不合法：请填游戏图标编号（十进制，或 0x 开头的十六进制）");
                return;
            }
            if (InReservedRange(from))
            {
                Chat("原编号不能填图库编号（" + BrowserIdBase + " 起）—— 图库编号直接写进宏就能用，不必走规则");
                return;
            }
            if (!TryParseIconNumber(ruleToInput, out var to) || to == 0)
            {
                Chat("目标编号不合法：请填要显示成的编号");
                return;
            }
            if (InReservedRange(to) && config.BrowserImages.All(b => b.Id != to))
            {
                Chat("目标编号 " + to + " 在保留段里，但图库中没有这张图 —— 先添加图片再用它的编号");
                return;
            }
            if (config.IconReplaceRules.Count >= IconRuleMax)
            {
                Chat("规则已达上限 " + IconRuleMax + " 条，请先清理");
                return;
            }

            // 同一个原编号只留一条（重复添加＝就地改目标）
            var old = config.IconReplaceRules.FirstOrDefault(r => r != null && r.From == from);
            if (old != null)
            {
                if (old.To == to) { Chat("这条规则已经存在：" + from + " → " + to); return; }
                old.To = to;
                Chat("已更新规则：" + from + " → " + to + DescribeIconId(to));
            }
            else
            {
                config.IconReplaceRules.Add(new Config.IconReplaceRule { From = from, To = to });
                Chat("已添加规则：" + from + " → " + to + DescribeIconId(to));
            }

            RebuildIconRuleMap();
            SaveOwnConfig();
            changed = true;
            ruleFromInput = "";
            ruleToInput = "";
        }
        catch (Exception e) { Log("添加替换规则异常: " + e, true); }
    }

    private void RemoveIconRuleFromInputs(ref bool changed)
    {
        if (!TryParseIconNumber(ruleFromInput, out var from) || from == 0)
        {
            Chat("删除前请先在「原编号」框里填要删除的规则的原编号");
            return;
        }
        RemoveIconRuleCore(from, ref changed);
    }

    private void RemoveIconRuleAt(int index, ref bool changed)
    {
        try
        {
            if (index < 0 || index >= config.IconReplaceRules.Count) return;
            var r = config.IconReplaceRules[index];
            if (r == null) { config.IconReplaceRules.RemoveAt(index); SaveOwnConfig(); changed = true; return; }
            RemoveIconRuleCore(r.From, ref changed);
        }
        catch (Exception e) { Log("删除替换规则异常: " + e, true); }
    }

    /// <summary>按原编号删规则并还原界面 —— 删除必须连带还原，否则界面会一直停在被换掉的样子。</summary>
    private void RemoveIconRuleCore(uint from, ref bool changed)
    {
        var n = config.IconReplaceRules.RemoveAll(r => r != null && r.From == from);
        if (n == 0) { Chat("没有找到原编号为 " + from + " 的规则"); return; }

        var restored = RestoreIconPatchesFor(from);
        RebuildIconRuleMap();
        SaveOwnConfig();
        changed = true;
        Chat("已删除规则 " + from + "，还原贴图槽 " + restored + " 个");
    }

    private void ClearAllIconRules(ref bool changed)
    {
        try
        {
            var n = config.IconReplaceRules.Count;
            if (n == 0) { Chat("当前没有替换规则"); return; }
            config.IconReplaceRules.Clear();
            var restored = RestoreAllIconPatches();
            RebuildIconRuleMap();
            SaveOwnConfig();
            changed = true;
            Chat("已清除全部 " + n + " 条替换规则，还原贴图槽 " + restored + " 个");
        }
        catch (Exception e) { Log("清除替换规则异常: " + e, true); }
    }

    // ------------------------------ 配置模型 ------------------------------

    [Serializable]
    private sealed class Config
    {
        public bool UseProxy { get; set; }
        public string ProxyUrl { get; set; } = "http://127.0.0.1:10808";

        /// <summary>宏图标：把保留段编号的贴图槽换成图库里的图片（本体关闭后自动停手）。</summary>
        public bool MacroIconSwap { get; set; } = true;

// 图标替换规则（原编号 → 目标编号）。左侧填游戏里原本的图标编号，右侧填想显示成的编号：
        public List<IconReplaceRule> IconReplaceRules { get; set; } = new();

        /// <summary>一条替换规则。</summary>
        public sealed class IconReplaceRule
        {
            /// <summary>原图标编号（游戏真实编号，不含我们自己的保留段）。</summary>
            public uint From { get; set; }

            /// <summary>替换后显示的编号（图库编号或游戏编号）。</summary>
            public uint To { get; set; }
        }

        /// <summary>本地图标库条目（注入宿主图标浏览器的「本地图标」页）。</summary>
        public List<BrowserImageEntry> BrowserImages { get; set; } = new();

// 下一个要分配的保留段编号（只增不减的水位线）。
        public uint NextIconId { get; set; }

        public sealed class BrowserImageEntry
        {
            /// <summary>浏览器里的虚拟图标 ID（0x7F000000 起的保留段）。</summary>
            public uint Id { get; set; }

            /// <summary>本地图片文件完整路径（favicon 抓下来后也会填上）。</summary>
            public string Path { get; set; } = "";

            /// <summary>显示名（来源网址主机名或文件名）。</summary>
            public string Name { get; set; } = "";

            /// <summary>来源网址（本地图片条目为空）。</summary>
            public string SourceUrl { get; set; } = "";
        }
    }
}
