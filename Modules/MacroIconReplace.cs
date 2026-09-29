using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.Notifications;
using OmniToolbox.UI.Theme;
using AtkComponentBase = FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentBase;
using AtkComponentIcon = FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentIcon;
using AtkComponentNode = FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentNode;
using AtkImageNode = FFXIVClientStructs.FFXIV.Component.GUI.AtkImageNode;
using AtkResNode = FFXIVClientStructs.FFXIV.Component.GUI.AtkResNode;
using AtkStage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage;
using AtkTexture = FFXIVClientStructs.FFXIV.Component.GUI.AtkTexture;
using AtkTextureResource = FFXIVClientStructs.FFXIV.Component.GUI.AtkTextureResource;
using AtkTextureResourceManager = FFXIVClientStructs.FFXIV.Component.GUI.AtkTextureResourceManager;
using ComponentType = FFXIVClientStructs.FFXIV.Component.GUI.ComponentType;
using GameTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;
using IconSubFolder = FFXIVClientStructs.FFXIV.Component.GUI.IconSubFolder;
using NodeType = FFXIVClientStructs.FFXIV.Component.GUI.NodeType;
using RaptureHotbarModule = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;
using RaptureMacroModule = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureMacroModule;
using TextureType = FFXIVClientStructs.FFXIV.Component.GUI.TextureType;

namespace OmniToolbox.LocalModules;

public sealed class MacroIconReplace : ModuleBase
{
    // ==================================================================
    //  清单信息
    // ==================================================================

    public override ModuleInfo Info { get; } = new()
    {
        Title = "宏图标替换",
        Description = "把宏的图标换成自定义图片（本地图片或网站图标），不用时可一键还原",
        Category = ModuleCategory.Interface,
        Author = "Omni 本地模块",
        Commands = new[]
        {
            new ModuleCommand("/omni MacroIconReplace 打开/关闭窗口", "/omni MacroIconReplace"),
            new ModuleCommand("/omni MacroIconReplace restore 还原全部图标", "/omni MacroIconReplace restore"),
        }
    };

    // ==================================================================
    //  配置（宿主会自动持久化名为 config 的成员）
    // ==================================================================

    [Serializable]
    public sealed class MacroIconEntry
    {
        /// <summary>0 = 个人宏，1 = 共享宏。</summary>
        public uint Set { get; set; }

        /// <summary>0 起的宏序号。</summary>
        public uint Index { get; set; }

        /// <summary>建立替换时该宏的图标 ID（用于定位界面上的图标 + 还原）。</summary>
        public uint IconId { get; set; }

        /// <summary>本地图片路径，或网址。</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Source 是否为网址。</summary>
        public bool IsUrl { get; set; }

        /// <summary>仅用于界面显示。</summary>
        public string MacroName { get; set; } = string.Empty;
    }

    [Serializable]
    public sealed class MacroIconReplaceConfig
    {
        public int ConfigVersion { get; set; } = 1;

        /// <summary>总开关：关掉会立刻把所有图标还原。</summary>
        public bool Enabled { get; set; } = true;

        public bool WindowVisible { get; set; } = true;

        public List<MacroIconEntry> Entries { get; set; } = new();

        // ================= 跨「模块重载」的账本（关键） =================
        // 模块每次重载都是全新实例，内存里的一切全丢，但**游戏里被我们改过的贴图还在**。
        // 没有这本账，新实例就认不出「这个槽指着的是上一版留下的我们的图」，
        // 于是既擦不掉、也还原不回来 —— 这就是「宏面板还原了、热键栏还原不了」的真凶。
        // 指针只在同一次游戏进程内有效，所以用 KernelSession 做闸门：换进程整本作废，
        // 绝不拿旧指针去写别人的内存。

        /// <summary>账本所属的游戏进程标识。对不上（重启过游戏）就整本清空。</summary>
        public string KernelSession { get; set; } = string.Empty;

        /// <summary>我们做过的内核贴图地址（我们自己画的那张替换图）。</summary>
        public List<long> OurKernels { get; set; } = new();

        /// <summary>与 OurKernels 一一对应：这张贴图是给哪个图标编号做的。</summary>
        public List<uint> OurKernelIconIds { get; set; } = new();

        /// <summary>我们按游戏原生路径重建的「原生图标」贴图（原值丢了也能把资源写回原生样子）。</summary>
        public List<long> NativeKernels { get; set; } = new();

        public List<uint> NativeKernelIconIds { get; set; } = new();

        /// <summary>我们动过的图标编号 —— 即使替换条目被删掉，还原时也要负责把它写干净。</summary>
        public List<uint> TouchedIconIds { get; set; } = new();
    }

    /// <summary>
    /// 我们自己的设置镜像（落盘在 pluginConfigs/OmniToolbox/MacroIconReplace/settings.json）。
    /// 宿主存档读不出来时靠它兜底 —— 设置只有一份存档太脆，两份才叫"不会丢"。
    /// </summary>
    [Serializable]
    public sealed class OwnSettings
    {
        public int Version { get; set; } = 1;
        public bool Enabled { get; set; } = true;
        public List<MacroIconEntry> Entries { get; set; } = new();
    }

    private static readonly JsonSerializerOptions OwnJsonOptions = new() { WriteIndented = true };

    /// <summary>我们自己的数据目录（设置镜像 + 图片缓存）。惰性解析一次，之后走缓存。</summary>
    private static string ownDataDir;
    private static bool ownDataDirResolved;

    /// <summary>本游戏进程的标识（进程号 + 启动时间）。算一次就缓存，同进程内绝不改变。</summary>
    private static string kernelSession;

    private MacroIconReplaceConfig config = new();

    // ==================================================================
    //  常量
    // ==================================================================

    private const uint SetIndividual = 0;
    private const uint SetShared = 1;
    private const uint MacroCount = 100;
    private const int MaxWalkDepth = 48;

    /// <summary>一次整树扫描最多访问多少个节点（防呆；超时会被下面的时间预算更早掐断）。</summary>
    private const int NodeBudgetPerSweep = 120000;

    /// <summary>
    /// 一次整树扫描的时间预算（毫秒）。超了立刻收工，剩下的等下一轮（见 sweepCursor 轮转）。
    /// 扫描跑在游戏主线程的 update 里，卡太久会掉帧 —— 宁可不扫完，也不能卡。
    /// </summary>
    private const long SweepTimeBudgetMs = 6;

    /// <summary>「还原」那次扫描的时间预算（毫秒）。用户点的一次性动作，给宽一点换「一次擦干净」。</summary>
    private const long RestoreSweepBudgetMs = 60;

    /// <summary>补丁记录连续多少轮整树扫描都没再被扫到，就丢掉（防过期指针长期滞留）。</summary>
    private const int PatchPruneAfterSweeps = 40;

    /// <summary>补丁记录表上限，超了先清最早见过的（正常也就几十条）。</summary>
    private const int MaxPatchRecords = 4096;

    /// <summary>连续多少次内存操作抛异常就熔断，暂停写内存一段时间。</summary>
    private const int FaultsBeforePause = 3;

    /// <summary>熔断后暂停写内存多久（毫秒）。</summary>
    private const long FaultPauseMs = 10000;

    /// <summary>自动重放的检查周期（毫秒）。</summary>
    private const long ReapplyCheckIntervalMs = 2000;

    /// <summary>单个界面单元的 UldManager.NodeList 最多走多少个节点（防呆上限）。</summary>
    private const int MaxNodesPerUnit = 8192;

    /// <summary>替换贴图的边长。固定值：ULD 里图标的 UV 恒为整张贴图，尺寸不影响显示。</summary>
    private const int KernelSize = 80;

    /// <summary>每隔多少帧重新整棵界面树扫一遍（抓新打开的界面单元）。</summary>
    private const int SweepIntervalFrames = 15;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    private static readonly Regex LinkTagRegex =
        new(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HrefRegex =
        new(@"href\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RelRegex =
        new(@"rel\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ==================================================================
    //  运行时状态
    // ==================================================================

    private sealed class MacroRow
    {
        public uint Set;
        public uint Index;
        public uint IconId;
        public uint IconRowId;
        public string Name = string.Empty;
        public string Display = string.Empty;
    }

    /// <summary>一张图片的加载结果（后台线程 → 主线程）。</summary>
    private sealed class ImageReady
    {
        public ulong Key;
        public byte[] Bytes;
        public IDalamudTextureWrap Preview;
        public string Error = string.Empty;
    }

    /// <summary>一张内核贴图的构建结果（后台线程 → 主线程）。</summary>
    private sealed class KernelReady
    {
        public ulong EntryKey;
        public long Kernel;
        public IDalamudTextureWrap Keep;
        public string Error = string.Empty;
    }

    /// <summary>一张「按游戏原生路径重建」的原生图标贴图（后台线程 → 主线程）。</summary>
    private sealed class NativeReady
    {
        public uint IconId;
        public long Kernel;
        public IDalamudTextureWrap Keep;
        public string Error = string.Empty;
    }

    /// <summary>
    /// 一处被我们改过的贴图槽。
    /// 关键：游戏随时可能把贴图刷回原样，所以每帧都要靠这张表重新写回去；
    /// 还原时也靠它把原始状态写回，不需要向游戏重新请求图标。
    /// </summary>
    private sealed class PatchRecord
    {
        public uint IconId;
        public long Kernel;
        public TextureType OrigType;
        public long OrigKernel;
        public bool RefAdded;
        public bool Written;

        /// <summary>
        /// 最后一次在整树扫描里被确认「这个槽还活着」时的扫描代数。
        /// 太久没被扫到 → 这个指针很可能指向已经销毁的界面单元，直接丢掉，
        /// 绝不让过期指针长期留在表里被每帧去读。
        /// </summary>
        public int LastSeenSweep;

        /// <summary>
        /// 接手时这个槽原本就是「内核贴图」类型。
        /// 这通常意味着原值不可信（可能是上一个模块实例留下的我们的贴图），
        /// 还原时就不要写回它，改成重指回编号图标的原生贴图资源。
        /// </summary>
        public bool OrigWasKernel;
    }

    private sealed class EntryRuntime
    {
        public MacroIconEntry Cfg;

        /// <summary>
        /// 每个宏只做一张贴图。尺寸固定，不去读游戏那张贴图的宽高 ——
        /// 因为界面上的图标贴图在没加载完时宽高可能是 0，跟着它走会永远写不进去。
        /// ULD 里图标的 UV 恒为整张贴图，尺寸不影响显示。
        /// </summary>
        public long Kernel;

        /// <summary>正在后台构建，避免重复请求。</summary>
        public volatile bool Building;

        /// <summary>我们持有的贴图句柄，用来保证内核贴图不会被别人提前释放。</summary>
        public readonly List<IDalamudTextureWrap> Wraps = new();

        public volatile byte[] Bytes;
        public IDalamudTextureWrap Preview;
        public volatile string Error = string.Empty;

        /// <summary>资源级替换：最近一次写入的 AtkTextureResource 地址（仅诊断/核对用）。</summary>
        public nint ResPtr;

        /// <summary>资源级替换：该资源原本的内核贴图（还原用）。</summary>
        public nint OrigResKernel;

        /// <summary>资源级：这个编号的图标资源被我们动过（还原必须负责它，哪怕条目已删）。</summary>
        public bool ResPatched;

        /// <summary>资源级替换：贴图文件句柄原本的内核贴图（还原用）。</summary>
        public nint OrigHandleKernel;

        /// <summary>
        /// 资源级替换：接手时那格里已经是「我们做过的贴图」（模块被重载过）→
        /// 真正的原值不可知。还原时绝不写脏指针，只提示用户让游戏自行重载。
        /// </summary>
        public bool ResOrigUnknown;

        /// <summary>
        /// 这张图自动重试加载过几次。
        /// 自动重放是每 2 秒检查一次的，没有这个计数的话，
        /// 「图片被删了 / 网址抓不到」的条目会每 2 秒重来一次（白白联网、白刷日志）。
        /// </summary>
        public int LoadAttempts;
    }

    private readonly List<MacroRow> macroRows = new();
    private readonly Dictionary<ulong, EntryRuntime> runtimes = new();
    private readonly Dictionary<uint, EntryRuntime> iconMap = new();

    /// <summary>图标贴图路径 crc32 → 图标 ID（贴图资源里 IconId 为空时的兜底判定依据）。</summary>
    private readonly Dictionary<uint, uint> pathHashToIcon = new();

    /// <summary>诊断期间统计：图片节点贴图资源的 TexPathHash → 出现次数（离线比对路径用）。</summary>
    private readonly Dictionary<uint, int> diagTexHashes = new();

    /// <summary>诊断期间当前单元名（给 Icon 组件定位用）。</summary>
    private string diagUnitName = "?";

    /// <summary>诊断期间统计：Icon 组件所在单元 → 次数（看宏面板到底在不在扫描结果里）。</summary>
    private readonly Dictionary<string, int> diagIconUnits = new(StringComparer.Ordinal);

    /// <summary>诊断期间已访问节点（NodeList 各元素之间会互相连成链，必须去重，否则统计翻倍）。</summary>
    private readonly HashSet<nint> diagVisited = new();

    /// <summary>诊断期间：节点 Type 原始数值 → 出现次数。</summary>
    private readonly Dictionary<int, int> diagTypeHist = new();
    private readonly ConcurrentQueue<ImageReady> imageReady = new();
    private readonly ConcurrentQueue<KernelReady> kernelReady = new();
    private readonly Dictionary<uint, ISharedImmediateTexture> iconTextureCache = new();

    /// <summary>贴图槽地址 → 我们改过的记录（每帧守卫 / 还原都靠它）。</summary>
    private readonly Dictionary<nint, PatchRecord> patches = new();

    /// <summary>整树扫描代数：给补丁记录做过期判定（见 PatchRecord.LastSeenSweep）。</summary>
    private int sweepGen;

    /// <summary>本次扫描的时间死线（TickCount64）—— 遍历超过它就立刻收工，保证不卡主线程。</summary>
    private long walkDeadline;

    /// <summary>下一轮整树扫描从第几个界面单元开始（预算不够时的轮转起点，防饿死）。</summary>
    private int sweepCursor;

    /// <summary>
    /// 还原时「需要重新加载图标」的组件。
    /// 收集和动作分开做：遍历整棵树的时候绝不改动界面对象，等走完了再统一处理。
    /// </summary>
    private readonly List<PendingReload> reloadQueue = new();

    private readonly struct PendingReload
    {
        public readonly nint Icon;
        public readonly uint IconId;

        public PendingReload(nint icon, uint iconId)
        {
            Icon = icon;
            IconId = iconId;
        }
    }

    /// <summary>内存操作连续出错计数，以及熔断截止时间（TickCount64）。</summary>
    private int writeFaults;
    private long pauseWritesUntil;

    /// <summary>下一次检查「记录还在、替换却没了」的时间（TickCount64）。</summary>
    private long nextReapplyAt;

    /// <summary>诊断被请求（由界面按钮置位，实际扫描放到游戏主线程 update 里做）。</summary>
    private volatile bool diagnosticRequested;

    /// <summary>
    /// 我们自己做过的所有内核贴图（一张 80×80，才 25KB，一律只泄漏不释放）。
    /// 用途：还原时「记录丢了但槽位还指着我们的图」能靠它认出来并救场。
    /// </summary>
    private readonly HashSet<long> ourKernels = new();

    /// <summary>
    /// 我们做过的内核贴图地址 → 图标编号。
    /// 跨模块重载靠它「认亲」：新实例看到某个槽指着这个地址，就知道那是我上一版的图、属于哪个编号。
    /// </summary>
    private readonly Dictionary<long, uint> kernelIconId = new();

    /// <summary>图标编号 → 我们按游戏原生路径重建的原生图标贴图（原值丢了也能还原成原生样子）。</summary>
    private readonly Dictionary<uint, long> nativeKernels = new();

    /// <summary>我们动过的图标编号（资源级）—— 条目被删掉之后仍然要负责还原。</summary>
    private readonly HashSet<uint> touchedIcons = new();

    /// <summary>正在后台重建原生图标贴图的编号（防重复请求）。</summary>
    private readonly HashSet<uint> nativeBuilding = new();

    /// <summary>重建完原生图、等着写回资源的编号。</summary>
    private readonly HashSet<uint> pendingHeal = new();

    /// <summary>重建完成的原生贴图（后台 → 主线程）。</summary>
    private readonly ConcurrentQueue<NativeReady> nativeReady = new();

    /// <summary>重建出来的原生贴图句柄（同样只持有、不释放）。</summary>
    private readonly List<IDalamudTextureWrap> nativeKeeps = new();

    /// <summary>长期保留的贴图句柄：我们做过的内核贴图一张都不释放（泄漏换「绝不野指针」）。</summary>
    private readonly List<IDalamudTextureWrap> keptWraps = new();

    /// <summary>本次还原里已经让组件重新 LoadIcon 过的组件地址（一次还原不重复折腾同一个组件）。</summary>
    private readonly HashSet<nint> reloadedComponents = new();

    /// <summary>本次还原里槽位重指的处数（只用于日志）。</summary>
    private int repointedSlots;

    /// <summary>
    /// 本次还原只处理这个图标编号（0 = 全部）。
    /// 用途：单个宏点「还原」时，不能顺手把别的宏的替换也一起撤掉。
    /// </summary>
    private uint restoreOnlyIcon;

    private ulong selectedKey;
    private int refreshCountdown;
    private int nodeBudget;
    private int sweepCountdown;
    private string pathInput = string.Empty;
    private string urlInput = string.Empty;
    private string statusLine = string.Empty;
    private bool disposed;
    private bool sweepNow;
    private int errorStreak;

    /// <summary>
    /// 「编号 → 运行时」映射需要重建。任何动过 config.Entries 的地方都置上它，
    /// 帧循环里发现就重建一次 —— 这样即使某个入口忘了调 RebuildIconMap，
    /// 替换也不会静默失效（重启后不生效就是这么来的）。
    /// </summary>
    private bool mapDirty = true;

    /// <summary>设置变了、等着写盘。放在帧循环里做，避免每帧重复写文件。</summary>
    private bool persistPending;

    /// <summary>自动诊断：图片就绪后连续多少次扫描都没命中任何贴图槽，就自动写一次诊断日志。</summary>
    private const int AutoDiagAfterSweeps = 20;
    private int noHitSweeps;
    private bool autoDiagDone;

    private static ulong KeyOf(uint set, uint index) => ((ulong)set << 32) | index;

    // ==================================================================
    //  生命周期
    // ==================================================================

    protected override void OnEnable()
    {
        disposed = false;
        NormalizeConfig();
        try
        {
            DalamudServices.PluginInterface.UiBuilder.Draw += Draw;
        }
        catch (Exception e)
        {
            LogError("挂载绘制失败：" + e.Message);
        }

        // ★ 所有「读写游戏内存」的活都挂在 Framework.Update（游戏主线程 update），
        //   不是 UiBuilder.Draw —— 后者在 DXGI Present 里跑，那一刻渲染线程正在读这些对象。
        //   ⚠️ 少了这一行，OnFrameworkUpdate 永远不会被调用：替换、还原、自动重放会全体静默失效。
        try
        {
            DalamudServices.Framework.Update += OnFrameworkUpdate;
        }
        catch (Exception e)
        {
            LogError("挂载帧更新失败：" + e.Message);
        }

        // ★ 顺序有讲究（上一版就是错在这里）：
        //   先 RestartSavedEntries（建运行时 + 起图片加载，然后它自己会 RebuildIconMap），
        //   再 RefreshMacroList（顺带按游戏里的真实图标 ID 校正条目）。
        //   反过来写的话，RebuildIconMap 跑的时候运行时还没建 → 映射恒空 → 整个替换静默失效。
        RestartSavedEntries();
        RebuildIconMap();
        RefreshMacroList();

        if (config.Entries.Count > 0 && config.Enabled)
        {
            LogError("已载入 " + config.Entries.Count + " 条替换设置，正在自动重新应用（无需重设）");
        }
    }

    protected override void OnDisable()
    {
        try { DalamudServices.PluginInterface.UiBuilder.Draw -= Draw; } catch { }
        try { DalamudServices.Framework.Update -= OnFrameworkUpdate; } catch { }
        PersistConfig();
        Shutdown();
    }

    protected override void OnDispose()
    {
        disposed = true;
        try { DalamudServices.PluginInterface.UiBuilder.Draw -= Draw; } catch { }
        try { DalamudServices.Framework.Update -= OnFrameworkUpdate; } catch { }
        PersistConfig();
        Shutdown();
    }

    /// <summary>还原所有图标 / 释放贴图。重载模块必走。</summary>
    private void Shutdown()
    {
        try { RestoreAll(keepEntries: true); } catch (Exception e) { LogError("还原失败：" + e.Message); }

        foreach (var rt in runtimes.Values)
        {
            if (rt.Preview != null)
            {
                try { rt.Preview.Dispose(); } catch { }
                rt.Preview = null;
            }
        }

        runtimes.Clear();
        iconMap.Clear();
        macroRows.Clear();
        iconTextureCache.Clear();
    }

    private void NormalizeConfig()
    {
        if (config.Entries == null) config.Entries = new List<MacroIconEntry>();
        if (config.OurKernels == null) config.OurKernels = new List<long>();
        if (config.OurKernelIconIds == null) config.OurKernelIconIds = new List<uint>();
        if (config.NativeKernels == null) config.NativeKernels = new List<long>();
        if (config.NativeKernelIconIds == null) config.NativeKernelIconIds = new List<uint>();
        if (config.TouchedIconIds == null) config.TouchedIconIds = new List<uint>();

        // 存档被写坏时列表里可能有 null，后面到处都在取 e.IconId —— 一次性清掉
        config.Entries.RemoveAll(e => e == null);

        LoadKernelLedger();

        // 宿主存档里一条设置都没有（首次升级、宿主没还原回来、存档被覆盖…）→ 读我们自己的镜像。
        if (config.Entries.Count == 0) TryLoadOwnMirror();
    }

    // ==================================================================
    //  设置持久化（宿主存档 + 自己的镜像）
    // ==================================================================

    /// <summary>
    /// 我们自己的数据目录：pluginConfigs/OmniToolbox/MacroIconReplace/
    /// 放在插件配置目录下一个独立子目录里 —— 与宿主的 Config.json / TreeHouse 互不干扰。
    /// </summary>
    private static string OwnDataDir()
    {
        if (ownDataDirResolved) return ownDataDir;
        ownDataDirResolved = true;

        try
        {
            var root = DalamudServices.PluginInterface?.ConfigDirectory?.FullName;
            if (string.IsNullOrEmpty(root))
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "XIVLauncherCN", "pluginConfigs", "OmniToolbox");
            }

            ownDataDir = Path.Combine(root, "MacroIconReplace");
        }
        catch
        {
            ownDataDir = Path.Combine(Path.GetTempPath(), "MacroIconReplace");
        }

        return ownDataDir;
    }

    private static string MirrorPath => Path.Combine(OwnDataDir(), "settings.json");

    /// <summary>
    /// ⚠️ 为什么是反射而不是直接调 SaveHostConfig：
    /// 宿主 ModuleBase 上的 SaveHostConfig 属性是 internal（程序集内部，已用元数据
    /// 逐位核过：get_/set_ 的 MethodAttributes 可访问性位 = 3 = Assembly），
    /// 而本模块是 Omni 用 Roslyn 现场编译出来的另一个程序集 ——
    /// internal 成员跨程序集不可见，直接写 SaveHostConfig?.Invoke() 会编译失败：
    ///     error CS0103: The name 'SaveHostConfig' does not exist in the current context
    /// （2026-09-29 实际踩到，模块「加载失败」）。
    /// 反射可以跨程序集访问 non-public 成员；注意必须逐级 BaseType + DeclaredOnly，
    /// 因为 Type.GetProperty 默认不返回基类的 non-public 成员。
    /// 拿不到就算了：镜像 settings.json 还在，顶多是宿主存档晚一步刷新。
    /// </summary>
    private static PropertyInfo saveHostConfigProp;

    private void InvokeSaveHostConfig()
    {
        try
        {
            if (saveHostConfigProp == null)
            {
                for (var t = GetType(); t != null; t = t.BaseType)
                {
                    var pi = t.GetProperty(
                        "SaveHostConfig",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (pi != null)
                    {
                        saveHostConfigProp = pi;
                        break;
                    }
                }
            }

            if (saveHostConfigProp != null && saveHostConfigProp.GetValue(this) is Action act)
                act();
        }
        catch
        {
            // 反射失败（改名/权限）不影响任何功能：镜像写盘照旧
        }
    }

    /// <summary>
    /// 把当前设置存下来。两件事都做：
    ///   ① 叫宿主立刻存一次它的配置（我们的 config 就在里面）；
    ///   ② 自己再写一份镜像 —— 宿主那边出任何意外都还有备份。
    /// 任何一步失败都静默：存不下来顶多是"重启后要重设"，绝不能因此影响替换本身。
    /// </summary>
    private void PersistConfig()
    {
        InvokeSaveHostConfig();

        try
        {
            Directory.CreateDirectory(OwnDataDir());

            var mirror = new OwnSettings
            {
                Enabled = config.Enabled,
                Entries = new List<MacroIconEntry>(config.Entries),
            };

            File.WriteAllText(MirrorPath, JsonSerializer.Serialize(mirror, OwnJsonOptions));
        }
        catch
        {
            // 镜像写不进去就算了，宿主存档还在
        }
    }

    /// <summary>标记"设置变了，找个空闲帧写盘"（避免在每帧路径里反复写文件）。</summary>
    private void MarkPersistPending() => persistPending = true;

    /// <summary>宿主存档空了时的兜底：从我们自己的镜像恢复设置。</summary>
    private void TryLoadOwnMirror()
    {
        try
        {
            var path = MirrorPath;
            if (!File.Exists(path)) return;

            var mirror = JsonSerializer.Deserialize<OwnSettings>(File.ReadAllText(path));
            if (mirror?.Entries == null || mirror.Entries.Count == 0) return;

            config.Entries.Clear();
            foreach (var e in mirror.Entries)
            {
                if (e == null || e.IconId == 0 || string.IsNullOrWhiteSpace(e.Source)) continue;
                config.Entries.Add(e);
            }

            if (mirror.Enabled == false) config.Enabled = false;

            LogError("宿主存档里没有设置，已从本地镜像恢复 " + config.Entries.Count + " 条替换设置");
        }
        catch (Exception e)
        {
            LogError("读本地镜像失败：" + e.Message);
        }
    }

    // ==================================================================
    //  图片字节缓存（重启后不必联网 / 不必原图还在）
    // ==================================================================

    private static string ImageCacheDir()
    {
        var dir = Path.Combine(OwnDataDir(), "img");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>缓存文件名前缀：按「来源串」区分（本地路径 / 网址各自一套）。</summary>
    private static string CachePrefix(string source, bool isUrl)
        => (isUrl ? "u_" : "f_") + Crc32((source ?? string.Empty).Trim()).ToString("X8");

    /// <summary>
    /// 缓存文件名。本地图片把「最后写入时间 + 文件大小」编进名字：
    /// 图片换了内容 → 名字跟着变 → 绝不会读到上个版本的旧图。
    /// </summary>
    private static string CacheFileName(string source, bool isUrl)
    {
        var prefix = CachePrefix(source, isUrl);
        if (isUrl) return prefix + ".bin";

        try
        {
            var info = new FileInfo(source);
            if (info.Exists) return prefix + "_" + info.LastWriteTimeUtc.Ticks + "_" + info.Length + ".bin";
        }
        catch
        {
            // 读不到文件信息就退回不带指纹的名字
        }

        return prefix + ".bin";
    }

    /// <summary>把抓到的图片字节存下来（下次启动直接用，不用联网、不用原图）。</summary>
    private static void WriteImageCache(string source, bool isUrl, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(source) || bytes == null || bytes.Length == 0) return;

        try
        {
            var name = CacheFileName(source, isUrl);
            var path = Path.Combine(ImageCacheDir(), name);
            File.WriteAllBytes(path, bytes);

            // 同一个来源的旧缓存清掉（本地图换内容会换名字，旧的那份就没用了）
            var prefix = CachePrefix(source, isUrl);
            foreach (var old in Directory.GetFiles(ImageCacheDir(), prefix + "*.bin"))
            {
                if (string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(old); } catch { }
            }
        }
        catch
        {
            // 缓存写不进去不影响本次替换
        }
    }

    /// <summary>
    /// 读图片缓存。本地文件优先取「当前文件状态」那一份；
    /// 读不到就退而求其次拿这个名字前缀下最新的一份（文件被删/被移动也能救）。
    /// </summary>
    private static byte[] TryReadImageCache(string source, bool isUrl)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        try
        {
            var dir = ImageCacheDir();

            if (!isUrl)
            {
                var exact = Path.Combine(dir, CacheFileName(source, false));
                if (File.Exists(exact)) return File.ReadAllBytes(exact);
            }

            var files = Directory.GetFiles(dir, CachePrefix(source, isUrl) + "*.bin");
            if (files.Length == 0) return null;

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return File.ReadAllBytes(files[files.Length - 1]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清掉某个来源的图片缓存（用户点「重新抓取」时用）。</summary>
    private static void ClearImageCache(string source, bool isUrl)
    {
        if (string.IsNullOrWhiteSpace(source)) return;

        try
        {
            foreach (var f in Directory.GetFiles(ImageCacheDir(), CachePrefix(source, isUrl) + "*.bin"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 本次游戏进程的标识：进程号 + 进程启动时间。同一个游戏进程内恒定，重启游戏必变。
    /// ⚠️ 别再用 AtkStage 单例地址 + TickCount：插件加载那一刻 AtkStage 还是 null，
    ///    算出来是一串占位值；进了游戏、同一进程里再重载模块又变成另一串 ——
    ///    账本会被误判成「换进程了」而整本清空，跨模块重载的认亲就白做了。
    /// </summary>
    private static string CurrentKernelSession()
    {
        if (kernelSession != null) return kernelSession;

        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            kernelSession = process.Id.ToString() + ":" + process.StartTime.Ticks.ToString();
        }
        catch
        {
            kernelSession = "0:0";
        }

        return kernelSession;
    }

    /// <summary>
    /// 把落盘的「我们做过的贴图账本」读进内存。
    /// 为什么必须落盘：模块每次重载都是新实例、内存全丢，可游戏里被我们改过的贴图还在。
    /// 没有这本账，新实例看到自己的图也认不出来，就永远擦不干净。
    /// 指针只在同一次游戏进程内有效 → 换进程（重启游戏）整本作废，绝不拿旧指针乱写。
    /// </summary>
    private unsafe void LoadKernelLedger()
    {
        var cur = CurrentKernelSession();

        if (!string.Equals(config.KernelSession, cur, StringComparison.Ordinal))
        {
            config.KernelSession = cur;
            config.OurKernels.Clear();
            config.OurKernelIconIds.Clear();
            config.NativeKernels.Clear();
            config.NativeKernelIconIds.Clear();
            config.TouchedIconIds.Clear();
            MarkPersistPending();
        }

        ourKernels.Clear();
        kernelIconId.Clear();
        nativeKernels.Clear();
        touchedIcons.Clear();

        for (var i = 0; i < config.OurKernels.Count; i++)
        {
            var k = config.OurKernels[i];
            if (k == 0) continue;

            ourKernels.Add(k);
            if (i < config.OurKernelIconIds.Count) kernelIconId[k] = config.OurKernelIconIds[i];
        }

        for (var i = 0; i < config.NativeKernels.Count; i++)
        {
            if (i >= config.NativeKernelIconIds.Count) break;

            var k = config.NativeKernels[i];
            var id = config.NativeKernelIconIds[i];
            if (k != 0 && id != 0) nativeKernels[id] = k;
        }

        foreach (var id in config.TouchedIconIds)
        {
            if (id != 0) touchedIcons.Add(id);
        }
    }

    /// <summary>记下「这张贴图是我们做的、属于哪个编号」（同时落盘，跨模块重载也认得出来）。</summary>
    private void RememberOurKernel(long kernel, uint iconId)
    {
        if (kernel == 0 || iconId == 0) return;

        kernelIconId[kernel] = iconId;
        if (!ourKernels.Add(kernel)) return;

        config.OurKernels.Add(kernel);
        config.OurKernelIconIds.Add(iconId);
        MarkPersistPending();
    }

    /// <summary>记下一张「重建出来的原生图标贴图」（还原时当干净值用）。</summary>
    private void RememberNativeKernel(long kernel, uint iconId)
    {
        if (kernel == 0 || iconId == 0) return;

        nativeKernels[iconId] = kernel;
        config.NativeKernels.Add(kernel);
        config.NativeKernelIconIds.Add(iconId);
        MarkPersistPending();
    }

    /// <summary>记下「我们动过这个编号的图标资源」——条目删了也照样负责还原它。</summary>
    private void RememberTouchedIcon(uint iconId)
    {
        if (iconId == 0) return;
        if (!touchedIcons.Add(iconId)) return;

        config.TouchedIconIds.Add(iconId);
        MarkPersistPending();
    }

    /// <summary>图标编号 → 游戏里的贴图路径（ui/icon/066000/066101.tex）。</summary>
    private static string IconTexturePath(uint iconId)
        => "ui/icon/" + (iconId / 1000).ToString("D3") + "000/" + iconId.ToString("D6") + ".tex";

    /// <summary>这个编号是不是「我们动过的」？（决定还原时要不要折腾相关组件）</summary>
    private bool IsOurIconId(uint iconId)
    {
        if (iconId == 0) return false;
        if (touchedIcons.Contains(iconId) || iconMap.ContainsKey(iconId)) return true;

        foreach (var kv in kernelIconId)
        {
            if (kv.Value == iconId) return true;
        }

        return false;
    }

    /// <summary>模块重新启用 / 游戏重启后加载时，把上次保存的替换重新跑一遍（不需要用户重设）。</summary>
    private void RestartSavedEntries()
    {
        foreach (var e in config.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Source) || e.IconId == 0) continue;

            var rt = GetOrCreateRuntime(e);

            // 自动重试上限 3 次（图被删了 / 网址抓不到时不要每 2 秒重来一遍）
            if (rt.Bytes == null && !rt.Building && rt.LoadAttempts < 3)
            {
                rt.LoadAttempts++;
                StartImageLoad(rt, e.Source, e.IsUrl);
            }
        }

        // ★ 重建映射 + 立刻安排一次整树扫描：
        //   少了这两步，"设置还在"和"图标真的换了"之间就断了（上一版的真凶）。
        RebuildIconMap();
        sweepNow = true;
        sweepCountdown = 0;
        noHitSweeps = 0;
        autoDiagDone = false;

        if (config.Entries.Count > 0)
        {
            statusLine = "已载入 " + config.Entries.Count + " 条替换设置，正在自动重新应用…";
        }
    }

    public override bool HasSettings => true;

    /// <summary>模块设置页：只放开关和入口，宏列表在独立窗口里（UiBuilder.Draw 绘制）。</summary>
    public override bool DrawSettings()
    {
        try
        {
            return DrawSettingsCore();
        }
        catch (Exception e)
        {
            LogError("设置面板异常：" + e.Message);
            return false;
        }
    }

    private bool DrawSettingsCore()
    {
        var changed = false;

        ImGui.TextUnformatted("宏图标替换");
        ImGui.TextDisabled("把宏的图标换成自定义图片（本地图片 / 网站图标）。");

        ImGui.Spacing();

        ImGui.Spacing();

        var enabled = config.Enabled;
        if (ImGui.Checkbox("启用图标替换（总开关）##mirSettingsEnabled", ref enabled))
        {
            config.Enabled = enabled;
            if (!enabled)
            {
                RestoreAll(keepEntries: true);
            }
            else
            {
                RestartSavedEntries();
                RebuildIconMap();
            }

            PersistConfig();
            changed = true;
        }

        var visible = config.WindowVisible;
        if (ImGui.Checkbox("显示主窗口（宏列表）##mirSettingsWindow", ref visible))
        {
            config.WindowVisible = visible;
            PersistConfig();
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("还原全部图标##mirSettingsRestoreAll"))
        {
            RestoreAll(keepEntries: false);
            changed = true;
        }

        ImGui.Spacing();

        ImGui.TextDisabled("替换原理：让游戏自己画我们做的贴图。");
        ImGui.TextDisabled("宏面板 / 热键栏 / 图标选择器里显示该宏图标的地方会一起变。");

        return changed;
    }

    public override bool ResetSettings()
    {
        RestoreAll(keepEntries: false);
        config = new MacroIconReplaceConfig();
        NormalizeConfig();
        mapDirty = true;
        PersistConfig();
        selectedKey = 0;
        statusLine = "已重置";
        return true;
    }

    public override bool TryHandleCommand(string arguments)
    {
        var arg = (arguments ?? string.Empty).Trim().ToLowerInvariant();
        if (arg.Contains("restore") || arg.Contains("还原"))
        {
            RestoreAll(keepEntries: false);
            OmniNotifier.Chat("宏图标替换：已还原全部宏图标");
            return true;
        }

        config.WindowVisible = !config.WindowVisible;
        PersistConfig();
        OmniNotifier.Chat(config.WindowVisible ? "宏图标替换窗口已打开" : "宏图标替换窗口已关闭");
        return true;
    }

    // ==================================================================
    //  宏列表
    // ==================================================================

    private unsafe void RefreshMacroList()
    {
        macroRows.Clear();

        var module = RaptureMacroModule.Instance();
        if (module == null) return;

        for (uint set = SetIndividual; set <= SetShared; set++)
        {
            for (uint i = 0; i < MacroCount; i++)
            {
                var macro = module->GetMacro(set, i);
                if (macro == null) continue;
                if (!macro->IsNotEmpty()) continue;

                var row = new MacroRow
                {
                    Set = set,
                    Index = i,
                    IconId = macro->IconId,
                    IconRowId = macro->MacroIconRowId,
                };

                try { row.Name = macro->Name.ToString(); } catch { row.Name = string.Empty; }
                row.Display = (set == SetIndividual ? "个人 " : "共享 ")
                              + (i + 1).ToString("00") + "  "
                              + (string.IsNullOrWhiteSpace(row.Name) ? "（无名宏）" : row.Name);

                macroRows.Add(row);
            }
        }

        SyncEntryIconIds();
    }

    /// <summary>宏在游戏里被改了图标时，让替换记录跟着走。</summary>
    private void SyncEntryIconIds()
    {
        var dirty = false;
        foreach (var e in config.Entries)
        {
            MacroRow row = null;
            foreach (var r in macroRows)
            {
                if (r.Set == e.Set && r.Index == e.Index) { row = r; break; }
            }

            if (row == null) continue;
            if (row.IconId != e.IconId)
            {
                e.IconId = row.IconId;
                dirty = true;
            }
        }

        if (dirty) RebuildIconMap();
    }

    /// <summary>
    /// 重建「图标编号 → 运行时」映射。映射唯一的来源就是 config.Entries ——
    /// 运行时不在就现建一个，不再要求「调用方先把 runtimes 建好」。
    /// （上一版就是靠这个隐含前提，OnEnable 顺序一错映射就是空的，替换整个静默失效。）
    /// </summary>
    private void RebuildIconMap()
    {
        iconMap.Clear();
        pathHashToIcon.Clear();

        foreach (var e in config.Entries)
        {
            if (e.IconId == 0) continue;

            var rt = GetOrCreateRuntime(e);
            iconMap[e.IconId] = rt;

            // 第二条判定依据：按图标贴图路径的 crc32 匹配（游戏里图标贴图路径规则固定）
            var stem = "ui/icon/" + (e.IconId / 1000).ToString("D3") + "000/" + e.IconId.ToString("D6");
            pathHashToIcon[Crc32(stem + ".tex")] = e.IconId;
            pathHashToIcon[Crc32(stem + "_hr1.tex")] = e.IconId;
        }

        mapDirty = false;
        sweepNow = true;
    }

    /// <summary>标准 CRC-32（反射多项式 0xEDB88320）—— 游戏用它给资源路径做哈希。</summary>
    private static uint Crc32(string text)
    {
        var crc = 0xFFFFFFFFu;
        var bytes = Encoding.ASCII.GetBytes(text);

        for (var i = 0; i < bytes.Length; i++)
        {
            crc ^= bytes[i];
            for (var b = 0; b < 8; b++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private EntryRuntime FindRuntimeByIconId(uint iconId)
    {
        if (iconId == 0) return null;
        return iconMap.TryGetValue(iconId, out var rt) ? rt : null;
    }

    private MacroIconEntry GetOrCreateEntry(uint set, uint index, uint iconId, string macroName)
    {
        foreach (var e in config.Entries)
        {
            if (e.Set == set && e.Index == index)
            {
                e.IconId = iconId;
                if (!string.IsNullOrWhiteSpace(macroName)) e.MacroName = macroName;
                return e;
            }
        }

        var entry = new MacroIconEntry
        {
            Set = set,
            Index = index,
            IconId = iconId,
            MacroName = macroName ?? string.Empty,
        };
        config.Entries.Add(entry);
        return entry;
    }

    private EntryRuntime GetOrCreateRuntime(MacroIconEntry e)
    {
        var key = KeyOf(e.Set, e.Index);
        if (runtimes.TryGetValue(key, out var rt))
        {
            // 配置对象换过（重开模块 / 重置 / 从镜像恢复）→ 跟上最新那一条，
            // 不然资源级守护读的是旧 IconId，改了图标编号就成了空转。
            if (!ReferenceEquals(rt.Cfg, e)) rt.Cfg = e;
            return rt;
        }

        rt = new EntryRuntime { Cfg = e };
        runtimes[key] = rt;
        return rt;
    }

    // ==================================================================
    //  界面上的图标节点
    // ==================================================================

    /// <summary>取一个图片节点当前正在显示的那张贴图。</summary>
    private static unsafe AtkTexture* GetImageNodeTexture(AtkImageNode* image)
    {
        if (image == null) return null;

        var parts = image->PartsList;

        // PartCount 也做个上限校验：内存被回收复用时，PartsList 本身可能就是块垃圾地址，
        // 拿它当数组下标去读 = 访问违例。正常图片节点的 PartCount 只有个位数。
        if (parts == null || parts->Parts == null) return null;
        if (parts->PartCount == 0 || parts->PartCount > 512) return null;

        var partId = image->PartId;
        if (partId >= parts->PartCount) partId = 0;

        var asset = parts->Parts[partId].UldAsset;
        if (asset == null) return null;

        return &asset->AtkTexture;
    }

    /// <summary>
    /// 这张贴图显示的是哪个图标？
    /// 第一依据：AtkTextureResource.IconId（按 ID 加载的图标会有值）。
    /// 第二依据：贴图路径哈希（IconId 为 0 时兜底，按 ui/icon/066000/066101.tex 的 crc32 匹配）。
    /// </summary>
    private unsafe uint ResolveTextureIconId(AtkTexture* texture)
    {
        if (texture == null) return 0;

        // ★★ 铁律：AtkTexture 是联合体（explicit layout）——
        //   Resource / Crest / KernelTexture 三个字段**共用偏移 0x8**（元数据 FieldLayout 实证），
        //   TextureType(偏移 0x10) 决定这一格里放的到底是什么：Resource=1 / Crest=2 / KernelTexture=3。
        //   我们替换过的槽，TextureType 就是 KernelTexture(3)，那一格里放的是**我们那张内核贴图**；
        //   此时再把它当 AtkTextureResource* 去读 IconId，读出来的是我们贴图对象的内存 = 垃圾值。
        //   这会让所有「按图标 ID 核对」的逻辑（尤其是还原时的校验）集体失效。
        //   → 只要类型不是 Resource，就一律认为「这个槽读不出图标 ID」，直接返回 0。
        if (texture->TextureType != TextureType.Resource) return 0;

        var res = texture->Resource;
        if (res == null) return 0;

        // 0xFFFFFFFF 是「无效图标」的占位值，不能当成真图标 ID
        if (res->IconId != 0 && res->IconId != 0xFFFFFFFF) return res->IconId;

        // IconId 空着 → 试路径哈希（不同的界面加载图标的方式不一样，这条能兜住）
        if (pathHashToIcon.Count > 0 && res->TexPathHash != 0
            && pathHashToIcon.TryGetValue(res->TexPathHash, out var mapped))
        {
            return mapped;
        }

        return 0;
    }

    /// <summary>遍历所有已加载的界面单元（宏面板、热键栏、图标选择器都在这张表里）。</summary>
    private unsafe void Sweep(bool restore)
    {
        var stage = AtkStage.Instance();
        if (stage == null) return;

        var unitManager = stage->RaptureAtkUnitManager;
        if (unitManager == null) return;

        ref var list = ref unitManager->AtkUnitManager.AllLoadedUnitsList;
        var entries = list.Entries;
        var count = (int)list.Count;

        var total = count < entries.Length ? count : entries.Length;
        if (total <= 0) return;

        // 还原要一次走完（只跑一次，可以放开预算）；替换每 15 帧一次，给个上限防呆。
        nodeBudget = restore ? int.MaxValue : NodeBudgetPerSweep;

        // ★ 时间预算：这段跑在游戏主线程 update 里，卡久了就是掉帧。
        //   还原是用户点的一次性动作，给宽一点（几十毫秒一次卡顿可以接受）；替换给紧一点。
        walkDeadline = Environment.TickCount64 + (restore ? RestoreSweepBudgetMs : SweepTimeBudgetMs);

        sweepGen++;   // 本轮扫描代数：补丁记录靠它做过期判定

        // ★ 轮转起点：预算有限时一轮扫不完，如果每轮都从头扫，
        //   后面的界面就永远轮不到（图标一直不换）。所以每轮从上次断掉的地方接着扫，
        //   保证「若干个 15 帧周期内，每个界面单元都会被扫到」。
        var start = restore ? 0 : (sweepCursor < total ? sweepCursor : 0);
        var scanned = 0;

        for (var step = 0; step < total; step++)
        {
            if (Environment.TickCount64 >= walkDeadline) break;

            var idx = start + step;
            if (idx >= total) idx -= total;

            scanned = step + 1;

            var unit = entries[idx].Value;
            if (unit == null) continue;

            // ★ 关键：addon 的节点是「扁平」挂在它自己的 UldManager.NodeList 数组里的，
            //   RootNode.ChildNode 链几乎是空的（实测 _ActionBar01 只走出 2 个节点）。
            //   只走 ChildNode 链会漏掉界面上绝大多数节点 —— 图标恰恰全漏在里面。
            ref var uld = ref unit->UldManager;
            var nl = uld.NodeList;
            if (nl == null) continue;

            var cnt = (int)uld.NodeListCount;
            for (var k = 0; k < cnt && k < MaxNodesPerUnit; k++)
            {
                var n = nl[k];
                if (n == null) continue;
                Walk(n, 0, restore);
            }
        }

        if (restore)
        {
            sweepCursor = 0;
        }
        else
        {
            var next = start + scanned;
            sweepCursor = next >= total ? 0 : next;
        }
    }

    private unsafe void Walk(AtkResNode* node, int depth, bool restore)
    {
        if (node == null) return;
        if (depth > MaxWalkDepth) return;
        if (nodeBudget-- <= 0) return;

        // 时间死线：每 1024 个节点核一次表，超时立刻停（整棵树不再往下走）
        if ((nodeBudget & 0x3FF) == 0 && Environment.TickCount64 >= walkDeadline) return;

        try
        {
            // 1) 普通图片节点 —— 界面上的图标绝大多数就画在这里。
            //    直接转型（图片节点本身以 AtkResNode 打头），不依赖 GetAsAtkImageNode 的运行时判定。
            if (node->Type == NodeType.Image)
            {
                HandleTexture(GetImageNodeTexture((AtkImageNode*)node), restore);
            }

            // 2) 组件节点：Type 原始值 = 1000+X（实测 1001/1002/1003/1005/1013/1018/1035…）。
            //    NodeType.Component=10000 只是枚举里的哨兵值，和任何真实节点都不相等！
            //    走 AtkComponentNode.Component → GetComponentType()，
            //    这条路比 GetAsAtkComponentIcon() 可靠（后者会因运行时判定失败而返回 null）。
            var rawType = (uint)node->Type;
            if (rawType >= 1000 && rawType < 2000)
            {
                var comp = ((AtkComponentNode*)node)->Component;
                if (comp != null && ComponentAccessOk(comp, node))
                {
                    if (comp->GetComponentType() == ComponentType.Icon)
                    {
                        var icon = (AtkComponentIcon*)comp;

                        // 组件自己带着 IconId —— 比「贴图资源里的 IconId」可靠，
                        // 因为有些界面加载图标不走按 ID 加载的路径，贴图资源里 IconId 会是 0。
                        var cid = icon->IconId;

                        var tex = icon->IconImage != null ? GetImageNodeTexture(icon->IconImage) : null;
                        if (tex == null && icon->FrameIcon != null) tex = GetImageNodeTexture(icon->FrameIcon);

                        if (cid != 0 && cid != 0xFFFFFFFF)
                        {
                            HandleTextureForced(tex, icon, cid, restore);
                            if (icon->Texture != null) HandleTextureForced(&icon->Texture->AtkTexture, icon, cid, restore);
                        }
                        else
                        {
                            HandleTexture(tex, restore);
                            if (icon->Texture != null) HandleTexture(&icon->Texture->AtkTexture, restore);
                        }
                    }

                    // 组件内部的节点树挂在 UldManager.NodeList 数组里，不在 ChildNode 链上！
                    // 图标组件、图标贴图全藏在这层 —— 不钻进去永远看不到（这是第四版失败的根因）。
                    //
                    // ★ 这一步才是真正会「解引用一串指针」的地方（拿 NodeList 当数组读），
                    //   所以组件指针必须通过 ComponentAccessOk 的校验才敢做。
                    //   （GetComponentType 本身只是读 OwnerNode->Type，是安全的，不受此限制，
                    //     免得守卫口径万一偏严就把整个替换功能误杀了。）
                    if (comp->UldManager.NodeList != null)
                    {
                        var nl = comp->UldManager.NodeList;
                        var cnt = comp->UldManager.NodeListCount;
                        for (var k = 0; k < cnt && k < 8192; k++)
                        {
                            if (nl[k] == node) continue;   // 防自引用死循环
                            if (Environment.TickCount64 >= walkDeadline) break;
                            Walk(nl[k], depth + 1, restore);
                        }
                    }
                }
            }
        }
        catch
        {
            // 单个节点异常不影响其它节点
        }

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
        {
            if (Environment.TickCount64 >= walkDeadline) break;
            Walk(child, depth + 1, restore);
        }
    }

    /// <summary>
    /// 这个组件指针能不能安全地「当结构体用」？
    /// 判据（都不调虚函数，只读字段 + 算术）：
    ///   ① 自洽：组件记的 AtkResNode / OwnerNode 至少有一个指回我们正在走的这个节点；
    ///   ② 兜底：指针值本身落在进程可达地址范围内、且 UldManager 的节点数在合理区间。
    /// ① 命中就直接放行；① 命中不了但 ② 成立也放行 —— 宁可宽松一点，
    /// 也绝不因为守卫口径偏严把整个替换功能误杀掉（那是更糟的失败）。
    /// 只有「地址本身就不像话」这种明显野指针才会被拦下。
    /// </summary>
    private static unsafe bool ComponentAccessOk(AtkComponentBase* comp, AtkResNode* node)
    {
        if (comp == null) return false;

        var addr = (ulong)comp;
        if (addr <= 0x10000 || addr >= 0x00007FFFFFFF0000) return false;   // 明显不是个用户态地址

        try
        {
            if (node != null
                && ((nint)comp->AtkResNode == (nint)node || (nint)comp->OwnerNode == (nint)node))
            {
                return true;
            }

            // 自洽校验没过（可能只是我们对字段语义理解不同）→ 退一步做粗筛：
            // 节点表指针必须是「空 或 像样的地址」，节点数必须在 0..8192。
            var nl = comp->UldManager.NodeList;
            var cnt = (int)comp->UldManager.NodeListCount;
            if (cnt < 0 || cnt > 8192) return false;
            if (nl == null) return true;

            var nlp = (ulong)nl;
            return nlp > 0x10000 && nlp < 0x00007FFFFFFF0000;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>处理一张贴图：该换就换。</summary>
    private unsafe void HandleTexture(AtkTexture* texture, bool restore)
    {
        if (texture == null) return;

        var ptr = (nint)texture;

        if (restore)
        {
            if (patches.TryGetValue(ptr, out var rec))
            {
                if (restoreOnlyIcon != 0 && rec.IconId != restoreOnlyIcon) return;

                RestoreTexture(texture, rec);
                patches.Remove(ptr);
                return;
            }

            // 记录丢了也要救：这个槽要是指着「我们做过的贴图」（账本里查得到），
            // 就按账本查出它属于哪个编号，重指回那个编号的原生贴图资源。
            // ★ 这一步完全不依赖任何记录 —— 模块重载过、记录被旧版删过，照样还原得回来。
            var kid = OurIconIdOf(texture);
            if (kid != 0 && (restoreOnlyIcon == 0 || kid == restoreOnlyIcon))
            {
                RepointToIconResource(texture, kid);
            }

            return;
        }

        var iconId = ResolveTextureIconId(texture);
        if (iconId != 0)
        {
            // 这张贴图现在画的就是某个图标
            if (FindRuntimeByIconId(iconId) != null)
            {
                ApplyTexture(texture, iconId);
                return;
            }

            // 画的是别的图标 → 我们之前那条补丁记录作废（贴图槽被游戏换成别的图标了）
            patches.Remove(ptr);
            return;
        }

        // 贴图已经不带图标信息了（游戏把我们的内核贴图换掉、或者清了资源），
        // 但只要是「我们用过的那个槽」，就照旧写回去，保证替换不会被游戏刷没。
        if (patches.TryGetValue(ptr, out var old))
        {
            var rt = FindRuntimeByIconId(old.IconId);
            if (rt == null)
            {
                patches.Remove(ptr);
                return;
            }

            WriteKernel(texture, rt, old);
        }
    }

    /// <summary>
    /// 组件级匹配：图标 ID 由 AtkComponentIcon.IconId 直接给出，不依赖贴图资源里记的 ID。
    /// </summary>
    private unsafe void HandleTextureForced(AtkTexture* texture, AtkComponentIcon* icon, uint iconId, bool restore)
    {
        if (iconId == 0) return;

        if (restore)
        {
            if (restoreOnlyIcon != 0 && iconId != restoreOnlyIcon) return;

            if (texture != null)
            {
                var ptr = (nint)texture;

                if (patches.TryGetValue(ptr, out var rec))
                {
                    RestoreTexture(texture, rec);
                    patches.Remove(ptr);
                    return;
                }

                // 记录丢了也要救：账本里查得出这个槽属于哪个编号 → 重指回那个编号的原生贴图资源
                if (OurIconIdOf(texture) != 0)
                {
                    RepointToIconResource(texture, iconId);
                    return;
                }
            }

            // ★ 最关键的一步（热键栏就靠它还原）：
            //   拿不到贴图指针时，让组件自己「卸掉 + 重新加载图标」——
            //   组件会从（已经写干净的）图标资源重新取图，我们那张图就此从界面上消失。
            //   热键栏的宏图标组件正是「贴图指针摸不到、但组件自己能重载」这种形态。
            ReloadComponentIcon(icon, iconId);
            return;
        }

        if (texture == null) return;

        var p = (nint)texture;

        // 组件说它画的是这个图标 → 就按这个算（贴图资源里有没有记录不管）
        if (FindRuntimeByIconId(iconId) != null)
        {
            ApplyTexture(texture, iconId);
        }
        else if (patches.ContainsKey(p))
        {
            // 我们改过这个槽，但现在组件画的是别的图标 → 补丁作废
            patches.Remove(p);
        }
    }

    /// <summary>
    /// 让一个图标组件重新加载自己的图标（游戏自己的 LoadIcon 通道）。
    /// 这是「贴图指针摸不到」的图标唯一的可靠还原手段：它会让组件重新去取图标资源里的图。
    /// 只对「我们动过的编号」生效，避免无谓地折腾满屏图标。
    /// </summary>
    private unsafe void ReloadComponentIcon(AtkComponentIcon* icon, uint iconId)
    {
        if (icon == null || iconId == 0) return;
        if (!IsOurIconId(iconId)) return;

        var key = (nint)icon;
        if (!reloadedComponents.Add(key)) return;   // 同一次还原里每个组件只折腾一次

        // ★ 只登记，不动手：遍历整棵树的过程中绝不能改界面对象
        //   （改的就是正在遍历的东西，轻则漏扫重则踩到已释放内存）。
        //   等整棵树走完，由 FlushReloadQueue() 统一处理。
        if (reloadQueue.Count < 512) reloadQueue.Add(new PendingReload(key, iconId));
    }

    /// <summary>
    /// 把登记下来的「组件重新加载图标」一次性做完 —— 必须在整树遍历之后调用。
    /// 这是「贴图指针摸不到」的图标（热键栏的宏图标就是）唯一可靠的还原手段。
    /// </summary>
    private unsafe void FlushReloadQueue()
    {
        if (reloadQueue.Count == 0) return;

        foreach (var item in reloadQueue)
        {
            if (item.Icon == 0) continue;

            try
            {
                var icon = (AtkComponentIcon*)item.Icon;

                // 组件已经不再画这个图标了 → 别去碰它
                if (icon->IconId != item.IconId) continue;

                if (icon->IsIconLoaded()) icon->UnloadIcon();
                icon->LoadIcon(item.IconId);
            }
            catch
            {
                // 单个组件失败不影响别的
            }
        }

        reloadQueue.Clear();
    }

    private unsafe void ApplyTexture(AtkTexture* texture, uint iconId)
    {
        var rt = FindRuntimeByIconId(iconId);
        if (rt == null) return;

        var ptr = (nint)texture;
        if (!patches.TryGetValue(ptr, out var rec) || rec.IconId != iconId)
        {
            // 表太大就先清一遍 —— 正常也就几十条，涨到这个数说明攒了一堆死指针
            if (patches.Count >= MaxPatchRecords) patches.Clear();

            rec = new PatchRecord { IconId = iconId };
            patches[ptr] = rec;
        }

        rec.LastSeenSweep = sweepGen;   // 这个槽本轮确认还活着

        // 贴图还没做好 → 让后台去做，这一帧先不动它
        if (rt.Kernel == 0)
        {
            RequestKernel(rt);
            return;
        }

        WriteKernel(texture, rt, rec);
    }

    private unsafe void WriteKernel(AtkTexture* texture, EntryRuntime rt, PatchRecord rec)
    {
        var kernel = rt.Kernel;
        if (kernel == 0) return;

        // 已经是我们写进去的那张 → 什么都不用做（每帧守卫的关键）
        if (texture->TextureType == TextureType.KernelTexture
            && (long)texture->KernelTexture == kernel)
        {
            return;
        }

        // 第一次动这张贴图时，把游戏原本的状态记下来，还原才有依据
        if (!rec.Written)
        {
            rec.OrigType = texture->TextureType;
            rec.OrigKernel = (long)texture->KernelTexture;

            // 原本就是内核贴图 → 这个「原值」多半不可信（可能就是上个实例留下的我们的图），
            // 或者压根不是原生状态。标记下来，还原时改走「重指资源」那条干净路。
            rec.OrigWasKernel = rec.OrigType == TextureType.KernelTexture;
        }

        // 游戏界面把贴图交出去后可能自己会释放它；补一次引用，
        // 保证「我们持有 = 游戏释放」两边平衡，绝不因为多释放一次而崩。
        if (!rec.RefAdded)
        {
            try { ((GameTexture*)kernel)->IncRef(); } catch { }
            rec.RefAdded = true;
        }

        texture->KernelTexture = (GameTexture*)kernel;
        texture->TextureType = TextureType.KernelTexture;

        rec.Kernel = kernel;
        rec.Written = true;
        rec.LastSeenSweep = sweepGen;
    }

    /// <summary>
    /// 把贴图槽写回游戏原本的样子。
    /// 校验口径（关键）：**只在它还指着我们那张内核贴图时才写回**。
    ///   · 不能再用「解析出的图标 ID == 记录里的图标 ID」当门槛 —— 我们写进去的是内核贴图，
    ///     联合体里原来的 AtkTextureResource 指针已被覆盖，ID 解析必然失败，
    ///     那样子写回会被无条件跳过，槽位就永远留着我们的图（这就是「还原不回来」的真凶）。
    ///   · 而「还指着我们的 kernel」这个条件天然安全：内存被游戏回收复用了，就不可能再等于
    ///     我们那个 kernel 地址，于是自动跳过，绝不写脏数据。
    /// 返回是否真的写了。
    /// </summary>
    private unsafe bool RestoreTexture(AtkTexture* texture, PatchRecord rec)
    {
        if (texture == null || !rec.Written) return false;

        try
        {
            // 这个槽现在还指着我们那张图吗？
            // 只有「类型是内核贴图 + 指针正好是我们那个 kernel」才认 —— 内存被游戏回收复用了，
            // 就不可能再相等，于是自动跳过，绝不往一块不属于我们的内存里写东西。
            var stillOurs = rec.Kernel != 0
                            && texture->TextureType == TextureType.KernelTexture
                            && (long)texture->KernelTexture == rec.Kernel;

            if (!stillOurs) return false;

            // 原值本身是内核贴图 → 它不可信（多半是模块上次被重载时留下的我们的图），
            // 绝不能写回它。这种情况只能靠「重指回这个编号图标的原生贴图资源」来还原。
            if (rec.OrigWasKernel)
            {
                if (rec.IconId != 0 && RepointToIconResource(texture, rec.IconId))
                {
                    rec.Written = false;
                    return true;
                }

                return false;
            }

            // 联合体同一格：把原始值原样写回（原始是资源指针就写资源指针，是内核就写内核）
            texture->KernelTexture = (GameTexture*)rec.OrigKernel;
            texture->TextureType = rec.OrigType;
            rec.Written = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 这个槽指着的是我们做的贴图吗？是的话返回它对应的图标编号（查账本，跨模块重载也有效）。
    /// </summary>
    private unsafe uint OurIconIdOf(AtkTexture* texture)
    {
        if (texture == null || texture->TextureType != TextureType.KernelTexture) return 0;

        var k = (long)texture->KernelTexture;
        if (k == 0) return 0;

        return kernelIconId.TryGetValue(k, out var id) ? id : 0u;
    }

    /// <summary>
    /// 兜底还原：把一个「还指着我们贴图、但没有历史记录」的槽，重新指回这个编号图标的原生状态。
    /// ★ 优先走游戏自己的 AtkTexture.LoadIconTexture —— 它会自己处理好
    ///   「卸掉旧贴图 / 取图标资源 / 记引用计数」，这正是图标贴图在游戏里的原生形态。
    ///   手工往联合体里塞 Resource 指针是上一版的做法：没记引用计数，
    ///   资源被管理器卸载后这个槽就悬空了（渲染时读到野指针 = 崩溃隐患），所以改掉。
    /// 返回是否真的改好了。
    /// </summary>
    private unsafe bool RepointToIconResource(AtkTexture* texture, uint iconId)
    {
        if (texture == null || iconId == 0) return false;

        try
        {
            // 游戏自己的通道：成功的话 TextureType 会变成 Resource（图标资源）
            texture->LoadIconTexture(iconId, IconSubFolder.None);

            if (texture->TextureType == TextureType.Resource && texture->Resource != null)
            {
                repointedSlots++;
                return true;
            }

            // 兜底（理论上走不到）：直接借用管理器缓存的那份资源
            var stage = AtkStage.Instance();
            var mgr = stage != null ? stage->AtkTextureResourceManager : null;
            if (mgr == null) return false;

            var res = mgr->LoadIconTexture((int)iconId, IconSubFolder.None);
            if (res == null) return false;

            texture->Resource = res;
            texture->TextureType = TextureType.Resource;
            repointedSlots++;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 已经换过的贴图，每帧核对一次：游戏把贴图刷回去了就立刻再写一次。
    /// 只遍历我们改过的那几十个槽，开销可以忽略。
    /// ★ 过期回收：太久没在整树扫描里露过面的记录直接丢掉 ——
    ///   界面单元一关，那个地址随时会被复用，留着就是每帧去读一块不属于我们的内存。
    /// </summary>
    private unsafe void GuardPatched()
    {
        if (patches.Count == 0) return;

        List<nint> dead = null;

        foreach (var kv in patches)
        {
            var texture = (AtkTexture*)kv.Key;
            var rec = kv.Value;

            var rt = FindRuntimeByIconId(rec.IconId);

            // 条目已被移除（或贴图被重置）→ 这个槽要是还指着我们的图，就地写回原样，然后丢记录
            if (rt == null)
            {
                RestoreTexture(texture, rec);
                (dead ??= new List<nint>()).Add(kv.Key);
                continue;
            }

            if (texture == null || rt.Kernel == 0) continue;

            // 已经指着我们的图 → 什么都不用做（每帧守卫的绝大部分开销就停在这一行）
            if (texture->TextureType == TextureType.KernelTexture
                && (long)texture->KernelTexture == rec.Kernel)
            {
                continue;
            }

            // 太久了没被整树扫描再见过 → 认定这个界面已经不在了，丢掉记录
            if (sweepGen - rec.LastSeenSweep > PatchPruneAfterSweeps)
            {
                (dead ??= new List<nint>()).Add(kv.Key);
                continue;
            }

            // 槽位被游戏换成别的图标了 → 我们的补丁作废（丢弃记录，绝不再往上写我们的图）
            var iconId = ResolveTextureIconId(texture);
            if (iconId != 0 && iconId != rec.IconId)
            {
                (dead ??= new List<nint>()).Add(kv.Key);
                continue;
            }

            WriteKernel(texture, rt, rec);
            rec.LastSeenSweep = sweepGen;
        }

        if (dead != null)
        {
            foreach (var p in dead) patches.Remove(p);
        }
    }

    // ==================================================================
    //  资源级替换：全局换掉「编号图标」本体
    // ==================================================================

    /// <summary>
    /// 每帧核对：把「编号图标」在贴图资源管理器里的内核贴图保持成我们的图。
    /// 游戏画这个编号时读的就是这条资源 —— 宏面板、热键栏、拖拽、图标选择器全部生效，
    /// 不需要任何窗口开着。游戏若重载了贴图（内核指针变回原图），这里立刻再写一次。
    /// </summary>
    private unsafe void EnsureResourcePatched(EntryRuntime rt)
    {
        if (rt.Kernel == 0 || rt.Cfg == null || rt.Cfg.IconId == 0) return;

        try
        {
            var stage = AtkStage.Instance();
            var mgr = stage != null ? stage->AtkTextureResourceManager : null;
            if (mgr == null) return;

            // 按编号取资源（管理器缓存共享同一份；被卸载重载了拿到的就是新实例）
            var res = mgr->LoadIconTexture((int)rt.Cfg.IconId, IconSubFolder.None);
            if (res == null) return;

            rt.ResPtr = (nint)res;

            // 主字段：不是我们的图就换上，并把游戏当时的原图记下来（还原用）
            var cur = (nint)res->KernelTextureObject;
            if (cur != rt.Kernel)
            {
                // 只记「可信的原值」：如果当前值本身就是我们做过的贴图
                // （模块重载过 → 上一实例写的图还在，真正的原值已无从得知），
                // 就标记不可信、不记 —— 还原时绝不可能写回一个脏指针。
                if (ourKernels.Contains((long)cur))
                {
                    rt.ResOrigUnknown = true;
                }
                else
                {
                    rt.OrigResKernel = cur;
                    rt.ResOrigUnknown = false;
                }

                res->KernelTextureObject = (GameTexture*)rt.Kernel;
                try { ((GameTexture*)rt.Kernel)->IncRef(); } catch { }
            }

            // 记进账本：这个编号的图标资源被我们动过。
            // 哪怕之后替换条目被删掉，还原时也必须负责把它写干净。
            rt.ResPatched = true;
            RememberTouchedIcon(rt.Cfg.IconId);

            // 文件句柄里的内核贴图同样处理（有些渲染路径读这里）
            var handle = res->TexFileResourceHandle;
            if (handle != null)
            {
                var cur2 = (nint)handle->Texture;
                if (cur2 != rt.Kernel)
                {
                    if (!ourKernels.Contains((long)cur2)) rt.OrigHandleKernel = cur2;
                    else rt.ResOrigUnknown = true;

                    handle->Texture = (GameTexture*)rt.Kernel;
                }
            }
        }
        catch
        {
            // 单帧失败不影响其它逻辑
        }
    }

    /// <summary>把「编号图标」的贴图资源写回游戏原本的样子。</summary>
    private unsafe void RestoreResourceLevel(EntryRuntime rt, bool force = false)
    {
        if (rt.Cfg == null || rt.Cfg.IconId == 0) return;

        if (!force && !rt.ResPatched && !touchedIcons.Contains(rt.Cfg.IconId)) return;

        // 把我们进场时记下的「真·原值」交给它当首选目标
        var prefer = rt.OrigResKernel != 0 ? rt.OrigResKernel : rt.OrigHandleKernel;
        HealIconResource(rt.Cfg.IconId, false, prefer);

        rt.ResPatched = false;
        rt.OrigResKernel = 0;
        rt.OrigHandleKernel = 0;
        rt.ResOrigUnknown = false;
        rt.ResPtr = 0;
    }

    /// <summary>
    /// 把一个图标编号的贴图资源写干净（把我们的图从「编号图标本体」上摘下来）。
    /// 只有「那一格现在确实放着我们做的图」时才动 —— 资源干净就绝不碰。
    /// 目标值优先级：prefer（我们进场时记下的真·原值）→ 当前另一个字段里的非我方值
    ///             → 我们按游戏原生路径重建的原生图 → 后台重建（下一帧收尾）。
    /// ★ 为什么要把 prefer 放最前：那一格已经被我们换成自己的图之后，
    ///   原来的贴图对象就没有引用者了，久了可能被游戏释放 —— 这时候把记下的指针写回去
    ///   等于写一个悬空指针进游戏资源（迟早崩）。优先用它自己的句柄值 / 我们重建的图更安全。
    /// </summary>
    private unsafe bool HealIconResource(uint iconId, bool afterRebuild, long prefer = 0)
    {
        if (iconId == 0) return false;

        try
        {
            var stage = AtkStage.Instance();
            var mgr = stage != null ? stage->AtkTextureResourceManager : null;
            if (mgr == null) return false;

            var res = mgr->LoadIconTexture((int)iconId, IconSubFolder.None);
            if (res == null) return false;

            var curKernel = (nint)res->KernelTextureObject;
            var handle = res->TexFileResourceHandle;
            var curHandle = handle != null ? (nint)handle->Texture : 0;

            var kernelOurs = curKernel != 0 && ourKernels.Contains((long)curKernel);
            var handleOurs = handle != null && curHandle != 0 && ourKernels.Contains((long)curHandle);

            if (!kernelOurs && !handleOurs) return false;   // 干净的，不碰

            // 目标值：优先「我们进场前它自己那张原生图」
            long target = 0;

            if (prefer != 0 && !ourKernels.Contains(prefer)) target = prefer;
            if (target == 0 && !kernelOurs && curKernel != 0) target = curKernel;
            if (target == 0 && !handleOurs && curHandle != 0) target = curHandle;
            if (target == 0 && nativeKernels.TryGetValue(iconId, out var nk) && nk != 0) target = nk;

            if (target == 0)
            {
                // 原值已经不可知（模块重载过，上一版的图还在里面）。
                // → 后台按游戏原生路径重建一张，好了之后由帧循环把它写回去。
                //   全程只写我们自己造的对象，绝不动别人的内存。
                StartNativeRebuild(iconId);
                return false;
            }

            if (kernelOurs) res->KernelTextureObject = (GameTexture*)target;
            if (handleOurs) handle->Texture = (GameTexture*)target;

            LogError("还原：图标 " + iconId + " 的贴图资源已写回原生图（原值"
                     + (afterRebuild ? "/重建收尾" : "") + "）");
            return true;
        }
        catch (Exception e)
        {
            LogError("还原图标资源失败（" + iconId + "）：" + e.Message);
            return false;
        }
    }

    /// <summary>按游戏原生路径重建一张图标贴图（后台线程），建成后由帧循环写回资源。</summary>
    private void StartNativeRebuild(uint iconId)
    {
        if (iconId == 0) return;
        if (nativeKernels.TryGetValue(iconId, out var have) && have != 0) return;
        if (!nativeBuilding.Add(iconId)) return;

        pendingHeal.Add(iconId);

        _ = Task.Run(async () =>
        {
            try
            {
                var provider = DalamudServices.TextureProvider;

                var shared = provider.GetFromGame(IconTexturePath(iconId));
                var wrap = shared.GetWrapOrEmpty();
                if (wrap == null) throw new InvalidOperationException("取不到原生图标贴图");

                var native = provider.ConvertToKernelTexture(wrap, true).ToInt64();
                if (native == 0) throw new InvalidOperationException("转内核贴图失败");

                nativeReady.Enqueue(new NativeReady { IconId = iconId, Kernel = native, Keep = wrap });
            }
            catch (Exception e)
            {
                nativeReady.Enqueue(new NativeReady { IconId = iconId, Kernel = 0, Error = e.Message });
            }
        });
    }

    /// <summary>每帧对所有启用的条目做资源级守护（极便宜：几个条目 × 一次缓存查表）。</summary>
    private unsafe void GuardResourceLevel()
    {
        foreach (var rt in runtimes.Values)
        {
            if (rt.Cfg == null || rt.Cfg.IconId == 0) continue;
            if (FindEntry(rt.Cfg.Set, rt.Cfg.Index) == null) continue;   // 条目已移除 → 不再补
            if (rt.Kernel != 0) EnsureResourcePatched(rt);
        }
    }

    // ==================================================================
    //  贴图流水线
    // ==================================================================

    private void RequestKernel(EntryRuntime rt)
    {
        if (rt.Building) return;

        var bytes = rt.Bytes;
        if (bytes == null || bytes.Length == 0) return;

        rt.Building = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var provider = DalamudServices.TextureProvider;

                var source = await provider
                    .CreateFromImageAsync(bytes, "MacroIconReplace:source")
                    .ConfigureAwait(false);

                var args = new TextureModificationArgs { NewWidth = KernelSize, NewHeight = KernelSize };

                // source 用完即弃（leaveWrapOpen: false 由 Dalamud 负责释放）
                var fitted = await provider
                    .CreateFromExistingTextureAsync(source, args, false, "MacroIconReplace:fitted")
                    .ConfigureAwait(false);

                if (fitted == null) throw new InvalidOperationException("贴图尺寸调整失败");

                var kernel = provider.ConvertToKernelTexture(fitted, true).ToInt64();
                if (kernel == 0) throw new InvalidOperationException("转内核贴图失败");

                kernelReady.Enqueue(new KernelReady
                {
                    EntryKey = KeyOf(rt.Cfg.Set, rt.Cfg.Index),
                    Kernel = kernel,
                    Keep = fitted,
                });

                // 顺手按游戏原生路径再重建一张「原生图标」贴图。
                // 用途：万一原值已经找不回来（模块被重载过、资源被游戏刷过），
                // 还原时可以把图标资源直接写回这张「原生样子」的图 —— 视觉上与游戏原生一致，
                // 而且不需要依赖任何历史记录。
                var iconId = rt.Cfg != null ? rt.Cfg.IconId : 0u;
                if (iconId != 0)
                {
                    try
                    {
                        var shared = provider.GetFromGame(IconTexturePath(iconId));
                        var wrap = shared.GetWrapOrEmpty();
                        if (wrap != null)
                        {
                            var native = provider.ConvertToKernelTexture(wrap, true).ToInt64();
                            if (native != 0)
                            {
                                nativeReady.Enqueue(new NativeReady
                                {
                                    IconId = iconId,
                                    Kernel = native,
                                    Keep = wrap,
                                });
                            }
                        }
                    }
                    catch
                    {
                        // 重建失败不影响替换本身
                    }
                }
            }
            catch (Exception e)
            {
                kernelReady.Enqueue(new KernelReady
                {
                    EntryKey = KeyOf(rt.Cfg.Set, rt.Cfg.Index),
                    Kernel = 0,
                    Error = e.Message,
                });
            }
        });
    }

    /// <summary>
    /// 取图。顺序按「快 + 稳」排：
    ///   网址 → 先读本地缓存（重启/断网也能立刻生效），没有再联网抓，抓到顺手缓存；
    ///   本地图 → 读文件（能反映你换图了），读不到就退回缓存（图被删/被移走也还认）。
    /// ignoreCache = true 时强制重新抓（详情页那个「重新抓取」按钮）。
    /// </summary>
    private void StartImageLoad(EntryRuntime rt, string source, bool isUrl, bool ignoreCache = false)
    {
        var entryKey = KeyOf(rt.Cfg.Set, rt.Cfg.Index);
        rt.Error = string.Empty;

        _ = Task.Run(async () =>
        {
            try
            {
                byte[] data = null;

                if (isUrl)
                {
                    if (!ignoreCache) data = TryReadImageCache(source, true);

                    if (data == null || data.Length == 0)
                    {
                        data = await FetchFaviconAsync(source).ConfigureAwait(false);
                        if (data != null && data.Length > 0) WriteImageCache(source, true, data);
                    }
                }
                else if (File.Exists(source))
                {
                    data = await File.ReadAllBytesAsync(source).ConfigureAwait(false);
                    if (data != null && data.Length > 0) WriteImageCache(source, false, data);
                }
                else
                {
                    // 本地图不在了（删了/挪了/换机器）→ 用缓存里的那份，别让替换莫名其妙掉
                    data = TryReadImageCache(source, false);
                }

                if (data == null || data.Length == 0)
                {
                    imageReady.Enqueue(new ImageReady
                    {
                        Key = entryKey,
                        Error = isUrl ? "没抓到这个网站的图标" : "读不到这个本地图片（缓存里也没有）",
                    });
                    return;
                }

                var preview = await DalamudServices.TextureProvider
                    .CreateFromImageAsync(data, "MacroIconReplace:preview")
                    .ConfigureAwait(false);

                imageReady.Enqueue(new ImageReady { Key = entryKey, Bytes = data, Preview = preview });
            }
            catch (Exception e)
            {
                imageReady.Enqueue(new ImageReady { Key = entryKey, Error = e.Message });
            }
        });
    }

    private void DrainReady()
    {
        while (imageReady.TryDequeue(out var img))
        {
            if (!runtimes.TryGetValue(img.Key, out var rt)) continue;

            if (!string.IsNullOrEmpty(img.Error))
            {
                rt.Error = img.Error;
                continue;
            }

            rt.Bytes = img.Bytes;
            rt.Error = string.Empty;
            ReleaseKernels(rt, fullRestore: false);

            if (rt.Preview != null)
            {
                try { rt.Preview.Dispose(); } catch { }
            }

            rt.Preview = img.Preview;

            // 图片一就绪就安排整树扫描，立刻把贴图做出来写上，不用等下一个周期
            sweepNow = true;
        }

        while (kernelReady.TryDequeue(out var k))
        {
            if (!runtimes.TryGetValue(k.EntryKey, out var rt)) continue;

            rt.Building = false;

            if (k.Kernel == 0)
            {
                rt.Error = "贴图构建失败：" + k.Error;
                continue;
            }

            // 旧贴图直接丢掉（绝不 Dispose，见 ReleaseKernels 的说明）
            rt.Kernel = k.Kernel;

            // 记进账本（同时落盘）：下次模块重载，新实例靠它认得出来这是我们的图
            RememberOurKernel(k.Kernel, rt.Cfg != null ? rt.Cfg.IconId : 0u);
            if (k.Keep != null) rt.Wraps.Add(k.Keep);

            // 新贴图就绪 → 立刻整树扫一遍，不用等下一个周期
            sweepNow = true;
        }

        while (nativeReady.TryDequeue(out var n))
        {
            nativeBuilding.Remove(n.IconId);

            if (n.Kernel == 0)
            {
                LogError("原生图标贴图重建失败（图标 " + n.IconId + "）：" + n.Error);
                continue;
            }

            if (n.Keep != null) nativeKeeps.Add(n.Keep);
            RememberNativeKernel(n.Kernel, n.IconId);

            // 重建好了 → 立刻收尾：把图标资源里我们的图换成这张原生图。
            // （资源本来是干净的就不动它 —— HealIconResource 自带判断）
            if (pendingHeal.Contains(n.IconId))
            {
                pendingHeal.Remove(n.IconId);
                HealIconResource(n.IconId, true);
            }
        }
    }

    /// <summary>
    /// 把「我们持有的贴图句柄」转进长期保留清单 —— 我们做过的内核贴图一张都不释放。
    /// 界面槽万一还指着它，就绝不会野指针（一张 80×80 才 25KB，这个交易非常划算）。
    /// </summary>
    private void ParkWraps(EntryRuntime rt)
    {
        if (rt.Wraps.Count == 0) return;

        keptWraps.AddRange(rt.Wraps);
        rt.Wraps.Clear();
    }

    /// <summary>
    /// 丢掉某个宏的贴图。
    /// 铁律一：**我们做过的内核贴图永不由我们 Dispose**（只丢引用、留它泄漏）。
    ///   一张 80×80 贴图才 25KB，泄漏换「绝不因为多释放一次而崩」，这个交易划算。
    /// fullRestore = true（用户点还原 / 删条目）：把资源写干净 + 全界面清扫 + 刷新热键栏；
    /// fullRestore = false（换图，马上要贴新图）：只是丢掉旧贴图引用。
    ///   旧贴图我们从不释放，槽位指着它不会野指针，新图就绪后会被整树扫一遍重新写上去。
    /// </summary>
    private unsafe void ReleaseKernels(EntryRuntime rt, bool fullRestore = true)
    {
        var iconId = rt.Cfg != null ? rt.Cfg.IconId : 0u;

        if (!fullRestore)
        {
            // 换图：旧贴图句柄转进长期保留清单（不释放），界面槽还指着它也绝不野指针
            ParkWraps(rt);
            rt.Kernel = 0;
            rt.Building = false;
            return;
        }

        // 单条目还原三步（只针对这个编号，绝不牵连别的宏）：
        //   1) 把「编号图标」的资源写干净；
        //   2) 全界面扫一遍，把这个编号留下的槽重指回原生贴图资源（不看记录，靠账本）；
        //   3) 让热键栏重新挂宏槽位 —— 热键栏内部缓存的我们的图靠这一步刷掉。
        HealIconResource(iconId, false);

        if (iconId != 0)
        {
            reloadedComponents.Clear();
            reloadQueue.Clear();
            repointedSlots = 0;
            restoreOnlyIcon = iconId;

            try
            {
                nodeBudget = int.MaxValue;
                Sweep(true);
            }
            catch (Exception e)
            {
                LogError("还原图标 " + iconId + " 失败：" + e.Message);
            }
            finally
            {
                restoreOnlyIcon = 0;
            }

            // ★ 整棵树走完之后，才去做「让组件重新加载图标」这件事
            FlushReloadQueue();
        }

        RestorePatchesFor(iconId);
        RestoreResourceLevel(rt, true);

        if (iconId != 0)
        {
            ReloadHotbarMacroSlots();
            LogError("还原图标 " + iconId + "：槽位重指 " + repointedSlots + " 处 / 组件重载 " + reloadedComponents.Count + " 个");
        }

        ParkWraps(rt);
        rt.Kernel = 0;
        rt.Building = false;
    }

    /// <summary>这个图标 ID 在界面上已经被换掉了几处。</summary>
    private int CountPatchesFor(uint iconId)
    {
        if (iconId == 0 || patches.Count == 0) return 0;

        var n = 0;
        foreach (var kv in patches)
        {
            if (kv.Value.IconId == iconId) n++;
        }

        return n;
    }

    /// <summary>
    /// 把某个图标 ID 对应的界面槽全部写回原样。返回写回了几处。
    /// ⚠️ 这里**不能**再用「重新解析出的图标 ID == iconId」做门槛（联合体已被覆盖，必然失败，
    ///    结果是记录被删掉、槽位却留着我们的图 = 永远还原不回来）。写回由 RestoreTexture 自校验。
    /// </summary>
    private unsafe int RestorePatchesFor(uint iconId)
    {
        if (iconId == 0 || patches.Count == 0) return 0;

        List<nint> dead = null;
        var restored = 0;

        foreach (var kv in patches)
        {
            if (kv.Value.IconId != iconId) continue;

            if (RestoreTexture((AtkTexture*)kv.Key, kv.Value)) restored++;

            (dead ??= new List<nint>()).Add(kv.Key);
        }

        if (dead != null)
        {
            foreach (var p in dead) patches.Remove(p);
        }

        if (restored > 0) LogError("还原图标 " + iconId + "：界面槽写回 " + restored + " 处");
        return restored;
    }

    /// <summary>把所有改过的界面槽写回原样。返回写回了几处。</summary>
    private unsafe int RestoreAllPatches()
    {
        var restored = 0;

        foreach (var kv in patches)
        {
            // 自校验写回：读不出「还指着我们的 kernel」就跳过（内存已被回收，绝不碰）
            if (RestoreTexture((AtkTexture*)kv.Key, kv.Value)) restored++;
        }

        patches.Clear();
        return restored;
    }

    // ==================================================================
    //  还原
    // ==================================================================

    private unsafe void RestoreAll(bool keepEntries)
    {
        reloadedComponents.Clear();
        reloadQueue.Clear();
        repointedSlots = 0;

        // 0) 先把「编号图标」的资源写干净 —— 槽位随后重指过去才有意义
        //    （资源里还留着我们的图时，重指过去等于没还原）。
        var healed = 0;
        foreach (var id in CollectIconIds())
        {
            if (HealIconResource(id, false)) healed++;
        }

        // 1) 整树清扫：凡是还指着我们贴图的槽，统统重指回该编号的原生贴图资源。
        //    ★ 这一步不看记录表，全靠账本 —— 模块重载过、记录被旧版删过，照样救得回来。
        var before = patches.Count;
        var written = 0;

        try
        {
            nodeBudget = int.MaxValue;
            Sweep(true);
            written += RestoreAllPatches();
        }
        catch (Exception e)
        {
            LogError("还原图标失败：" + e.Message);
            try { written += RestoreAllPatches(); } catch { }
        }

        // ★ 整棵树走完之后，才去动「让组件重新加载图标」——
        //   遍历途中改界面对象是自找麻烦（改的正是自己正在走的东西）。
        FlushReloadQueue();

        LogError("还原：资源写回 " + healed + " 个编号 / 槽位重指 " + repointedSlots
                 + " 处 / 记录写回 " + written + " 处（记录表 " + before + " 条）");

        // 2) 让游戏把热键栏的宏槽位重新挂一遍。
        //    热键栏会在这一步按编号重新取图标 —— 它内部缓存的我们的图就此被刷掉，
        //    这是热键栏最可靠的还原手段（游戏自己的刷新入口）。
        ReloadHotbarMacroSlots();

        // 3) 丢掉运行时状态。注意：我们做过的内核贴图**一律不 Dispose**（见 ReleaseKernels）
        foreach (var rt in runtimes.Values)
        {
            RestoreResourceLevel(rt, true);

            ParkWraps(rt);
            rt.Kernel = 0;
            rt.Building = false;
            rt.Bytes = null;

            if (rt.Preview != null)
            {
                try { rt.Preview.Dispose(); } catch { }
                rt.Preview = null;
            }
        }

        patches.Clear();
        runtimes.Clear();
        iconMap.Clear();

        if (pendingHeal.Count > 0)
        {
            LogError("还原：还有 " + pendingHeal.Count + " 个编号等原生图重建完成后收尾（会自动完成）");
        }

        if (!keepEntries)
        {
            config.Entries.Clear();

            // 条目清空了 → 立刻落盘（镜像 + 宿主存档都写），
            // 不然重启后这批设置会从旧存档里"复活"，图标又变回去了。
            PersistConfig();
        }
    }

    /// <summary>需要写干净的图标编号 = 账本 ∪ 现有条目 ∪ 补丁记录。</summary>
    private List<uint> CollectIconIds()
    {
        var ids = new List<uint>();
        var seen = new HashSet<uint>();

        foreach (var id in touchedIcons)
        {
            if (id != 0 && seen.Add(id)) ids.Add(id);
        }

        foreach (var kv in iconMap)
        {
            if (kv.Key != 0 && seen.Add(kv.Key)) ids.Add(kv.Key);
        }

        foreach (var kv in patches)
        {
            if (kv.Value.IconId != 0 && seen.Add(kv.Value.IconId)) ids.Add(kv.Value.IconId);
        }

        foreach (var kv in kernelIconId)
        {
            if (kv.Value != 0 && seen.Add(kv.Value)) ids.Add(kv.Value);
        }

        return ids;
    }

    /// <summary>
    /// 让游戏把热键栏上的宏槽位重新挂一遍（游戏自己的刷新入口）。
    /// 热键栏上「已经缓存了我们那张图」的槽位会在这时按编号重新取图 —— 于是被刷回原样。
    /// </summary>
    private unsafe void ReloadHotbarMacroSlots()
    {
        try
        {
            var hotbar = RaptureHotbarModule.Instance();
            if (hotbar == null) return;

            hotbar->ReloadAllMacroSlots();
        }
        catch (Exception e)
        {
            LogError("刷新热键栏失败：" + e.Message);
        }
    }

    private void RemoveEntry(MacroIconEntry entry)
    {
        var key = KeyOf(entry.Set, entry.Index);
        if (runtimes.TryGetValue(key, out var rt))
        {
            ReleaseKernels(rt);
            if (rt.Preview != null)
            {
                try { rt.Preview.Dispose(); } catch { }
                rt.Preview = null;
            }
            runtimes.Remove(key);
        }

        config.Entries.Remove(entry);
        RebuildIconMap();

        // ★ 还原必须立刻落盘：不写盘的话，重启之后这条设置又回来了 ——
        //   用户要的就是「只有我点还原，图标才回原样」。
        PersistConfig();
    }

    // ==================================================================
    //  图片：本地文件 / 网址图标
    // ==================================================================

    private static async Task<byte[]> FetchFaviconAsync(string raw)
    {
        var startUrl = NormalizeUrl(raw);
        if (startUrl == null) return null;

        var uri = new Uri(startUrl);
        var origin = uri.Scheme + "://" + uri.Authority;

        // 1) 先按惯例取 /favicon.ico
        var ico = await TryGetBytesAsync(origin + "/favicon.ico").ConfigureAwait(false);
        if (ico != null)
        {
            var picked = ExtractImageFromIco(ico);
            if (picked != null) return picked;
            if (IsSupportedImage(ico)) return ico;
        }

        // 2) 抓页面，读 <link rel="...icon..." href="...">
        var html = await TryGetTextAsync(uri).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(html))
        {
            foreach (Match tag in LinkTagRegex.Matches(html))
            {
                var rel = RelRegex.Match(tag.Value);
                if (!rel.Success) continue;
                if (rel.Groups[1].Value.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0) continue;

                var href = HrefRegex.Match(tag.Value);
                if (!href.Success) continue;

                var abs = ResolveUrl(uri, href.Groups[1].Value);
                if (abs == null) continue;

                var data = await TryGetBytesAsync(abs).ConfigureAwait(false);
                if (data == null || data.Length == 0) continue;

                var picked = ExtractImageFromIco(data);
                if (picked != null) return picked;
                if (IsSupportedImage(data)) return data;
            }
        }

        // 3) 兜底：/favicon.png
        var png = await TryGetBytesAsync(origin + "/favicon.png").ConfigureAwait(false);
        if (png != null && IsSupportedImage(png)) return png;

        return null;
    }

    private static string NormalizeUrl(string raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0) return null;

        if (!s.Contains("://")) s = "https://" + s;

        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        return uri.ToString();
    }

    private static string ResolveUrl(Uri baseUri, string href)
    {
        var h = (href ?? string.Empty).Trim();
        if (h.Length == 0) return null;
        if (h.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;

        if (Uri.TryCreate(baseUri, h, out var abs))
        {
            if (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps) return abs.ToString();
        }

        return null;
    }

    private static async Task<byte[]> TryGetBytesAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> TryGetTextAsync(Uri uri)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return body != null && body.Length > 400000 ? body.Substring(0, 400000) : body;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSupportedImage(byte[] d)
    {
        if (d == null || d.Length < 12) return false;

        // PNG
        if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return true;
        // JPEG
        if (d[0] == 0xFF && d[1] == 0xD8) return true;
        // GIF
        if (d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return true;
        // BMP
        if (d[0] == 0x42 && d[1] == 0x4D) return true;
        // WEBP
        if (d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46 &&
            d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50) return true;

        return false;
    }

    /// <summary>
    /// 从 .ico 里挑一张最大的图：内嵌 PNG 直接取出；老式 BMP 的补一个文件头再交给解码器。
    /// 不是 ico 就返回 null。
    /// </summary>
    private static byte[] ExtractImageFromIco(byte[] d)
    {
        try
        {
            if (d == null || d.Length < 22) return null;

            // ICONDIR: 00 00 01 00 | count(2)
            if (!(d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0)) return null;

            var count = d[4] | (d[5] << 8);
            if (count <= 0 || count > 64) return null;

            var bestOffset = 0;
            var bestSize = 0;
            var bestScore = -1;

            for (var i = 0; i < count; i++)
            {
                var p = 6 + i * 16;
                if (p + 16 > d.Length) break;

                var w = d[p] == 0 ? 256 : d[p];
                var h = d[p + 1] == 0 ? 256 : d[p + 1];
                var bpp = d[p + 6] | (d[p + 7] << 8);
                var size = BitConverter.ToInt32(d, p + 8);
                var offset = BitConverter.ToInt32(d, p + 12);

                if (size <= 0 || offset <= 0 || offset >= d.Length) continue;

                var score = w * h * 100 + bpp;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOffset = offset;
                    bestSize = size;
                }
            }

            if (bestScore < 0) return null;
            if (bestOffset + bestSize > d.Length) bestSize = d.Length - bestOffset;
            if (bestSize < 16) return null;

            // 内嵌 PNG
            if (d[bestOffset] == 0x89 && d[bestOffset + 1] == 0x50 &&
                d[bestOffset + 2] == 0x4E && d[bestOffset + 3] == 0x47)
            {
                var png = new byte[bestSize];
                Buffer.BlockCopy(d, bestOffset, png, 0, bestSize);
                return png;
            }

            return WrapIcoBitmap(d, bestOffset, bestSize);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把 ico 里的 BITMAPINFOHEADER 数据包成一个可解码的 .bmp 字节流。</summary>
    private static byte[] WrapIcoBitmap(byte[] d, int offset, int size)
    {
        if (size < 40) return null;

        var dibSize = BitConverter.ToInt32(d, offset);
        if (dibSize < 40 || dibSize > size) return null;

        var width = BitConverter.ToInt32(d, offset + 4);
        var height = BitConverter.ToInt32(d, offset + 8) / 2; // ico 里高度含 AND 掩码
        var bitCount = d[offset + 14] | (d[offset + 15] << 8);

        if (width <= 0 || height <= 0) return null;
        if (bitCount != 1 && bitCount != 4 && bitCount != 8 && bitCount != 24 && bitCount != 32) return null;

        var paletteColors = 0;
        if (bitCount <= 8)
        {
            var used = BitConverter.ToInt32(d, offset + 32);
            paletteColors = used > 0 ? used : 1 << bitCount;
        }

        var paletteBytes = paletteColors * 4;
        var rowBytes = ((width * bitCount + 31) / 32) * 4;
        var pixelBytes = rowBytes * height;

        var dibBytes = dibSize + paletteBytes + pixelBytes;
        if (offset + dibBytes > d.Length) dibBytes = d.Length - offset;
        if (dibBytes < dibSize) return null;

        var outBytes = new byte[14 + dibBytes];

        outBytes[0] = 0x42; // B
        outBytes[1] = 0x4D; // M
        BitConverter.GetBytes(14 + dibBytes).CopyTo(outBytes, 2);
        BitConverter.GetBytes(14 + dibSize + paletteBytes).CopyTo(outBytes, 10);

        Buffer.BlockCopy(d, offset, outBytes, 14, dibBytes);

        // 修正 biHeight（去掉 AND 掩码那一半）
        BitConverter.GetBytes(height).CopyTo(outBytes, 14 + 8);

        return outBytes;
    }

    // ==================================================================
    //  游戏主线程帧更新 —— 所有「读写游戏内存」的活都在这里做
    // ==================================================================

    /// <summary>
    /// ★ 为什么不用 ImGui 的 Draw 回调干这些活：
    ///   UiBuilder.Draw 是挂在 DXGI Present 上的 —— 那一刻游戏的渲染线程正在读这些
    ///   AtkTexture / 组件对象。我们在那儿改贴图指针、叫组件重载图标，是在跟渲染线程抢同一块内存，
    ///   平时侥幸没事，赶上界面被销毁 / 内存被复用就是一次崩溃。
    ///   Framework.Update 是游戏自己的主线程 update，UI 还没开始构建渲染 ——
    ///   这里才是游戏自己改这些东西的时机。
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed) return;

        try
        {
            DrainReady();

            // 设置变了就在这儿补一次写盘（一次帧循环最多写一次，不会每帧刷文件）
            if (persistPending)
            {
                persistPending = false;
                PersistConfig();
            }

            // 映射该重建就重建（任何入口漏调 RebuildIconMap 也不会导致替换失效）
            if (mapDirty) RebuildIconMap();

            // ★ 自动重放 / 自愈：设置还在、替换却没跑着 → 重新应用一遍。
            //   「关掉模块再打开」「重启电脑」都靠它，不需要你再设置一次。
            EnsureAutoReapply();

            // 判据只看「总开关 + 有没有设置」—— 不再看 iconMap.Count，
            // 否则映射一旦没建起来，这里就永远进不来（上一版重启后不生效的原因）。
            if (config.Enabled && config.Entries.Count > 0)
            {
                if (!WritesBlocked())
                {
                    // 每帧：只核对「已经换过的那几十个贴图槽」（极便宜）。
                    // 游戏把贴图刷回去了，这里立刻再写一次 —— 替换才不会被刷没。
                    GuardPatched();

                    // 资源级：全局把「编号图标」本体的贴图换掉，不依赖任何窗口开着
                    GuardResourceLevel();

                    // 每 N 帧（或刚有新贴图做好）整棵界面树扫一遍，抓新打开的界面单元
                    if (sweepNow || --sweepCountdown <= 0)
                    {
                        sweepNow = false;
                        sweepCountdown = SweepIntervalFrames;
                        Sweep(false);
                        AutoDiagnoseIfNeeded();
                    }
                }
            }
            else
            {
                sweepCountdown = 0;
                noHitSweeps = 0;
            }

            // 宏列表定期刷新：不管窗口开没开都刷。
            // （重启游戏后第一次加载时人还没登录，宏模块还拿不到；登录后靠这里补上来，
            //   窗口没开也不会一直显示"没读到宏"。）
            if (--refreshCountdown <= 0)
            {
                refreshCountdown = 90;
                RefreshMacroList();
            }

            // 诊断要扫几十万个节点，绝对不能放在绘制帧里 —— 界面上点按钮只是置个位。
            if (diagnosticRequested)
            {
                diagnosticRequested = false;
                RunDiagnostic();
            }

            writeFaults = 0;
        }
        catch (Exception e)
        {
            // 连续出错就熔断：暂停写内存一段时间，免得在异常状态下越写越坏。
            // 「还原」不受熔断限制 —— 那是用户明确要求、而且要尽量把界面擦干净的动作。
            writeFaults++;
            if (writeFaults <= FaultsBeforePause)
            {
                LogError("帧更新异常：" + e.Message);
            }
            else if (pauseWritesUntil == 0)
            {
                pauseWritesUntil = Environment.TickCount64 + FaultPauseMs;
                LogError("连续异常 " + writeFaults + " 次，暂停写入 " + (FaultPauseMs / 1000)
                         + " 秒以防越写越坏（点「还原」依旧可用）");
            }
        }
    }

    /// <summary>熔断中吗？（连续出错后暂停写内存，到点自动恢复）</summary>
    private bool WritesBlocked()
    {
        var until = pauseWritesUntil;
        if (until == 0) return false;

        if (Environment.TickCount64 >= until)
        {
            pauseWritesUntil = 0;
            writeFaults = 0;
            LogError("熔断结束，恢复写入");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 自动重放 / 自愈：设置明明存在、也该生效，运行时却没建起来
    /// （模块刚被重载、游戏刚重启、宿主换了个实例…），就把记录里的替换重新应用一遍。
    /// 这是「关掉模块再打开 / 重启电脑都不用重新设置」的兜底保证。
    /// 只在「确实有活要干」时才动手 —— 不然每 2 秒重置一次扫描状态会把自动诊断搅乱。
    /// </summary>
    private void EnsureAutoReapply()
    {
        if (!config.Enabled || config.Entries.Count == 0) return;

        var now = Environment.TickCount64;
        if (now < nextReapplyAt) return;
        nextReapplyAt = now + ReapplyCheckIntervalMs;

        var need = false;

        foreach (var e in config.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Source) || e.IconId == 0) continue;

            var rt = FindRuntime(e.Set, e.Index);
            if (rt == null)
            {
                need = true;      // 运行时根本没建起来 → 就是「重启后不生效」那种情况
                break;
            }

            // 图还没读到、也没在加载、而且还有重试额度 → 需要重新拉一次
            if (rt.Bytes == null && !rt.Building && rt.LoadAttempts < 3)
            {
                need = true;
                break;
            }
        }

        if (!need) return;

        RestartSavedEntries();
        RebuildIconMap();
    }

    // ==================================================================
    //  绘制（只画界面，绝不碰游戏内存）
    // ==================================================================

    private void Draw()
    {
        if (disposed) return;

        try
        {
            if (!config.WindowVisible) return;

            DrawWindowCore();
            errorStreak = 0;
        }
        catch (Exception e)
        {
            errorStreak++;
            if (errorStreak <= 3)
            {
                LogError("绘制异常：" + e.Message);
            }
        }
    }

    private bool DrawWindowCore()
    {
        // using var：方法退出时（含异常）一定会把样式弹回去。
        // ⚠️ 它必须在 ImGui.End() **之后**才 Dispose —— 所以下面用 try/finally 把 End 写进 finally，
        //    绝不出现「窗口还开着就把样式弹掉」这种栈错位（ImGui 栈一错位，下一次 Pop 就可能越界 → 崩）。
        using var scope = new ComicStyleScope();

        ImGui.SetNextWindowSize(new Vector2(860f, 520f), ImGuiCond.FirstUseEver);

        var flags = ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking;
        var open = config.WindowVisible;

        if (!ImGui.Begin("宏图标替换###MacroIconReplace", ref open, flags))
        {
            ImGui.End();
            return false;
        }

        // ★ 无论中途出什么岔子，Begin/End、BeginChild/EndChild 都必须配平。
        //   （上一版是直接 return，一旦 DrawMacroList/DrawDetail 抛异常，End 就永远不会被调用。）
        var childOpen = false;

        try
        {
            if (config.WindowVisible != open)
            {
                config.WindowVisible = open;
                PersistConfig();
            }

            DrawToolbar();
            ImGui.Separator();

            var listHeight = MathF.Max(120f, ImGui.GetContentRegionAvail().Y - 30f);

            ImGui.BeginChild("##mirList", new Vector2(330f, listHeight), true);
            childOpen = true;
            DrawMacroList();
            ImGui.EndChild();
            childOpen = false;

            ImGui.SameLine();

            ImGui.BeginChild("##mirDetail", new Vector2(0f, listHeight), true);
            childOpen = true;
            DrawDetail();
            ImGui.EndChild();
            childOpen = false;

            ImGui.Separator();
            ImGui.TextDisabled(string.IsNullOrEmpty(statusLine) ? "就绪" : statusLine);
        }
        catch (Exception e)
        {
            errorStreak++;
            if (errorStreak <= 3)
            {
                LogError("绘制异常：" + e.Message);
            }
        }
        finally
        {
            if (childOpen)
            {
                try { ImGui.EndChild(); } catch { }
            }

            try { ImGui.End(); } catch { }
        }

        return false;
    }

    private void DrawToolbar()
    {
        var enabled = config.Enabled;
        if (ImGui.Checkbox("启用图标替换", ref enabled))
        {
            config.Enabled = enabled;
            if (!enabled)
            {
                RestoreAll(keepEntries: true);
                statusLine = "已停用，所有图标已还原";
            }
            else
            {
                RestartSavedEntries();
                RebuildIconMap();
                statusLine = "已启用";
            }

            PersistConfig();
        }

        ImGui.SameLine();
        if (ImGui.Button("刷新宏列表"))
        {
            RefreshMacroList();
            statusLine = "宏列表已刷新（共 " + macroRows.Count + " 条）";
        }

        ImGui.SameLine();
        if (ImGui.Button("立即重放设置"))
        {
            RestartSavedEntries();
            statusLine = "已按保存的设置重新应用（" + config.Entries.Count + " 条）";
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("把保存过的替换重新应用一遍。\n设置本身已经存在磁盘上，重开游戏/重启电脑会自动重放，一般不用点这个。");
        }

        ImGui.SameLine();
        if (ImGui.Button("还原全部"))
        {
            RestoreAll(keepEntries: false);
            statusLine = "已还原全部宏图标";
        }

        ImGui.SameLine();
        if (ImGui.Button("诊断：扫界面图标"))
        {
            // 只置位：真正那次扫描（几十万节点）放到游戏主线程的 update 里做，
            // 免得卡住整个绘制帧（卡帧还容易连带内存访问踩到不该踩的地方）。
            diagnosticRequested = true;
            statusLine = "诊断已排队，结果会写进日志（[MacroIconReplace] 开头的行）";
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("把界面上所有「带图标 ID 的贴图」统计进日志。\n替换不生效时点这个，把日志里的 [MacroIconReplace] 行发出来。");
        }
    }

    /// <summary>
    /// 图片就绪了却连续多轮扫描一个槽都没命中 —— 不等你手动点诊断，
    /// 自动把界面扫描结果写进日志，方便直接定位「图标 ID 口径」问题。
    /// </summary>
    private void AutoDiagnoseIfNeeded()
    {
        var pending = false;
        foreach (var rt in runtimes.Values)
        {
            if (rt.Bytes != null && rt.Kernel == 0)
            {
                pending = true;
                break;
            }
        }

        if (!pending || patches.Count > 0)
        {
            noHitSweeps = 0;
            return;
        }

        noHitSweeps++;

        if (noHitSweeps >= AutoDiagAfterSweeps && !autoDiagDone)
        {
            autoDiagDone = true;
            LogError("自动诊断：图片已就绪但连续 " + noHitSweeps + " 轮扫描都没命中任何贴图槽 ——");
            RunDiagnostic();
        }
    }

    /// <summary>
    /// 诊断：把界面上「所有带图标 ID 的贴图」扫一遍，按图标 ID 归类写进日志。
    /// 万一替换不生效，这份日志能直接回答「宏面板到底有没有在画这个图标 ID」。
    /// </summary>
    private unsafe void RunDiagnostic()
    {
        var stage = AtkStage.Instance();
        var unitManager = stage != null ? stage->RaptureAtkUnitManager : null;
        if (unitManager == null)
        {
            LogError("诊断：拿不到界面单元管理器");
            statusLine = "诊断失败：拿不到界面单元管理器";
            return;
        }

        ref var list = ref unitManager->AtkUnitManager.AllLoadedUnitsList;
        var entries = list.Entries;
        var count = (int)list.Count;

        var found = new Dictionary<uint, int>();
        var compFound = new Dictionary<uint, int>();
        var compTypes = new Dictionary<int, int>();
        diagTypeHist.Clear();
        diagVisited.Clear();
        var unitInfo = new List<string>();
        diagTexHashes.Clear();
        diagIconUnits.Clear();

        var imageNodes = 0;
        var iconComponents = 0;
        var textures = 0;
        var guard = 400000;
        var unitCount = 0;
        var totalNodes = 0;

        for (var i = 0; i < count && i < entries.Length; i++)
        {
            var unit = entries[i].Value;
            if (unit == null) continue;

            ref var uld = ref unit->UldManager;
            var nl = uld.NodeList;
            if (nl == null) continue;

            var cnt = (int)uld.NodeListCount;
            if (cnt <= 0) continue;

            unitCount++;

            var name = "?";
            // Name 现在是 Span<byte>，ToString() 会打出 "System.Span<Byte>[32]"；
            // 必须用 NameString 属性拿真名字。
            try { name = string.IsNullOrWhiteSpace(unit->NameString) ? "?" : unit->NameString; } catch { }
            diagUnitName = name;

            for (var k = 0; k < cnt && k < MaxNodesPerUnit; k++)
            {
                var n = nl[k];
                if (n == null) continue;
                CollectDiagnostic(n, 0, found, compFound, compTypes, ref imageNodes, ref iconComponents, ref textures, ref guard);
            }

            // NodeListCount 就是这个 addon 的真实节点总数，直接用它最准
            totalNodes += cnt;
            unitInfo.Add(name + "(" + cnt + ")");
        }

        var sb = new StringBuilder();
        sb.Append("诊断：界面单元 ").Append(unitCount)
          .Append(" 个 / 节点总数 ").Append(totalNodes)
          .Append(" / 图片节点 ").Append(imageNodes)
          .Append(" / 图标组件 ").Append(iconComponents)
          .Append(" / 贴图槽 ").Append(textures)
          .Append(" / 贴图带图标ID的 ").Append(found.Count).Append(" 种")
          .Append(" / 组件自带IconId的 ").Append(compFound.Count).Append(" 种");
        LogError(sb.ToString());

        // 节点类型原始数值分布 —— 一眼看出遍历有没有走通、Type 数值到底是什么
        var hb = new StringBuilder("   节点类型分布：");
        hb.Append("(Image=").Append((int)NodeType.Image)
          .Append("；10000=NodeType.Component哨兵值，组件节点实际是1000+X) ");

        var hkeys = new List<int>(diagTypeHist.Keys);
        hkeys.Sort((a, b) => diagTypeHist[b].CompareTo(diagTypeHist[a]));
        for (var i = 0; i < hkeys.Count && i < 14; i++)
        {
            hb.Append(hkeys[i]).Append('×').Append(diagTypeHist[hkeys[i]]).Append(' ');
        }

        LogError(hb.ToString());

        var cb = new StringBuilder("   组件类型分布：");
        cb.Append("(枚举Icon=").Append((int)ComponentType.Icon).Append(") ");
        var ckeys = new List<int>(compTypes.Keys);
        ckeys.Sort((a, b) => compTypes[b].CompareTo(compTypes[a]));
        for (var i = 0; i < ckeys.Count && i < 12; i++)
        {
            cb.Append(ckeys[i]).Append('×').Append(compTypes[ckeys[i]]).Append(' ');
        }

        if (ckeys.Count == 0) cb.Append("（一个组件节点都没遍历到）");
        LogError(cb.ToString());

        // 关注宏/热键栏相关单元，以及节点最多的前几个单元
        var focus = new List<string>();
        foreach (var u in unitInfo)
        {
            if (u.IndexOf("Macro", StringComparison.OrdinalIgnoreCase) >= 0
                || u.IndexOf("Hotbar", StringComparison.OrdinalIgnoreCase) >= 0
                || u.IndexOf("ActionBar", StringComparison.OrdinalIgnoreCase) >= 0
                || u.StartsWith("_", StringComparison.Ordinal))
            {
                focus.Add(u);
            }
        }

        LogError("   单元名（宏/热键栏相关）：" + (focus.Count == 0 ? "无" : string.Join(" ", focus)));

        // 全部单元名（按节点数降序，最多 60 个）—— 用来确认宏面板单元到底在不在遍历结果里
        var allNames = new List<string>(unitInfo);
        allNames.Sort((a, b) =>
        {
            var na = int.Parse(a.Substring(a.LastIndexOf('(') + 1).TrimEnd(')'));
            var nb = int.Parse(b.Substring(b.LastIndexOf('(') + 1).TrimEnd(')'));
            return nb.CompareTo(na);
        });
        LogError("   全部单元名：" + string.Join(" ", allNames));

        // Icon 组件都长在哪些单元上（宏面板开着的话，这里应该出现 Macro(IconId=66101) 之类）
        var iuLine = new StringBuilder("   Icon 组件定位：");
        if (diagIconUnits.Count == 0) iuLine.Append("（一个都没有）");
        foreach (var kv in diagIconUnits)
        {
            iuLine.Append(kv.Key).Append('×').Append(kv.Value).Append(' ');
        }
        LogError(iuLine.ToString());

        // 宏面板存在性检查 —— 宏窗口的节点只在它开着时存在！
        var macroUnits = new List<string>();
        foreach (var u in unitInfo)
        {
            if (u.IndexOf("Macro", StringComparison.OrdinalIgnoreCase) >= 0) macroUnits.Add(u);
        }
        LogError("   宏面板单元：" + (macroUnits.Count == 0
            ? "没找到！！宏窗口必须开着（里面能看到宏列表）才有它的单元 —— 请开着宏面板再诊断"
            : string.Join(" ", macroUnits)));

        // 贴图路径哈希 —— 图标如果走了图集或别的路径，这里能看出来
        var pathLine = new StringBuilder("   贴图路径哈希 ").Append(diagTexHashes.Count).Append(" 种：");
        var hkeys2 = new List<uint>(diagTexHashes.Keys);
        hkeys2.Sort((a, b) => diagTexHashes[b].CompareTo(diagTexHashes[a]));
        for (var i = 0; i < hkeys2.Count && i < 20; i++)
        {
            pathLine.Append(hkeys2[i].ToString("X8")).Append('×').Append(diagTexHashes[hkeys2[i]]).Append(' ');
        }

        if (diagTexHashes.Count == 0) pathLine.Append("（一个都没有）");
        LogError(pathLine.ToString());

        // 我们要替换的图标路径哈希有没有出现在界面上
        var wantLine = new StringBuilder("   目标图标路径哈希命中：");
        var anyWant = false;
        foreach (var e in config.Entries)
        {
            if (e.IconId == 0) continue;
            var stem = "ui/icon/" + (e.IconId / 1000).ToString("D3") + "000/" + e.IconId.ToString("D6");
            var h1 = Crc32(stem + ".tex");
            var h2 = Crc32(stem + "_hr1.tex");
            diagTexHashes.TryGetValue(h1, out var n1);
            diagTexHashes.TryGetValue(h2, out var n2);
            anyWant = anyWant || n1 + n2 > 0;
            wantLine.Append(e.IconId).Append("→").Append(n1 + n2).Append("处 ");
        }

        if (!anyWant) wantLine.Append("（全部 0 处 —— 图标贴图根本没按独立路径加载，多半走了图集或组件）");
        LogError(wantLine.ToString());

        unitInfo.Sort((a, b) =>
        {
            var na = int.Parse(a.Substring(a.LastIndexOf('(') + 1).TrimEnd(')'));
            var nb = int.Parse(b.Substring(b.LastIndexOf('(') + 1).TrimEnd(')'));
            return nb.CompareTo(na);
        });

        LogError("   节点最多的单元：" + string.Join(" ", unitInfo.GetRange(0, Math.Min(10, unitInfo.Count))));

        foreach (var e in config.Entries)
        {
            if (e.IconId == 0) continue;

            found.TryGetValue(e.IconId, out var c);
            compFound.TryGetValue(e.IconId, out var cc);

            var hint = string.Empty;
            if (c == 0 && cc == 0)
            {
                // 没命中：把界面上与目标同千位段的 ID 全列出来，方便对照是不是 ID 口径不同
                var near = new List<uint>();
                var block = e.IconId / 1000;
                foreach (var k in found.Keys)
                {
                    if (k / 1000 == block) near.Add(k);
                }

                foreach (var k in compFound.Keys)
                {
                    if (k / 1000 == block && !near.Contains(k)) near.Add(k);
                }

                near.Sort();
                hint = "；界面上 " + (block * 1000) + " 段的图标 ID："
                       + (near.Count == 0 ? "无" : string.Join(",", near));
            }

            LogError("   宏「" + e.MacroName + "」(个人/共享=" + e.Set + " 序号=" + e.Index
                     + ") 目标图标 " + e.IconId
                     + " → 贴图资源命中 " + c + " 处 / 组件命中 " + cc + " 处" + hint);
        }

        var keys = new List<uint>(found.Keys);
        foreach (var k in compFound.Keys)
        {
            if (!keys.Contains(k)) keys.Add(k);
        }

        keys.Sort();

        var line = new StringBuilder("   界面上的图标 ID（资源/组件合并）：");
        for (var i = 0; i < keys.Count && i < 80; i++)
        {
            found.TryGetValue(keys[i], out var f);
            compFound.TryGetValue(keys[i], out var cf);
            line.Append(keys[i]).Append('×').Append(f + cf).Append(' ');
        }

        if (keys.Count == 0) line.Append("（一个都没有 —— 宏面板先打开再点诊断）");
        LogError(line.ToString());

        statusLine = "诊断已写入日志：贴图带图标ID " + found.Count + " 种 / 组件自带IconId " + compFound.Count + " 种";
    }

    /// <summary>统计节点树规模，并把每个节点的 Type 原始数值计数（用来核对枚举口径）。</summary>
    private static unsafe int CountTypes(AtkResNode* node, int depth, Dictionary<int, int> hist)
    {
        if (node == null || depth > MaxWalkDepth) return 0;

        var total = 1;

        try
        {
            var t = (int)node->Type;
            hist[t] = hist.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        catch
        {
            // 忽略
        }

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
        {
            total += CountTypes(child, depth + 1, hist);
            if (total > 400000) break;
        }

        // 组件内部节点也要算进来（挂在 UldManager.NodeList，不在 ChildNode 链上）
        if ((uint)node->Type >= 1000 && (uint)node->Type < 2000)
        {
            try
            {
                var comp = ((AtkComponentNode*)node)->Component;

                // 自洽校验：内存被复用的话 comp->UldManager.NodeList 可能就是块乱七八糟的地址，
                // 拿它当数组去读 = 直接访问违例。校验不过就整块跳过。
                if (comp != null && ComponentAccessOk(comp, node))
                {
                    var nl = comp->UldManager.NodeList;
                    if (nl != null)
                    {
                        var cnt = comp->UldManager.NodeListCount;
                        for (var k = 0; k < cnt && k < 8192; k++)
                        {
                            if (nl[k] == node) continue;
                            total += CountTypes(nl[k], depth + 1, hist);
                            if (total > 400000) break;
                        }
                    }
                }
            }
            catch
            {
                // 忽略
            }
        }

        return total;
    }

    private unsafe void CollectDiagnostic(
        AtkResNode* node,
        int depth,
        Dictionary<uint, int> found,
        Dictionary<uint, int> compFound,
        Dictionary<int, int> compTypes,
        ref int imageNodes,
        ref int iconComponents,
        ref int textures,
        ref int guard)
    {
        if (node == null || depth > MaxWalkDepth || guard-- <= 0) return;

        // NodeList 里各元素本身还互相连成 ChildNode/NextSibling 链，必须按地址去重，
        // 否则同一个节点被数很多遍，统计全失真。
        if (!diagVisited.Add((nint)node)) return;

        try
        {
            var t = (int)node->Type;
            diagTypeHist[t] = diagTypeHist.TryGetValue(t, out var tcnt) ? tcnt + 1 : 1;

            if (node->Type == NodeType.Image)
            {
                imageNodes++;
                Tally(GetImageNodeTexture((AtkImageNode*)node), found, ref textures);
            }
            else if ((uint)node->Type >= 1000 && (uint)node->Type < 2000)
            {
                // 组件节点：Type 原始值 = 1000+X；NodeType.Component=10000 只是哨兵值
                var comp = ((AtkComponentNode*)node)->Component;

                // 自洽校验之后才敢碰它（GetComponentType 本身只读字段，
                // 但紧接着要钻它内部的节点表 —— 那一步是真解引用，必须校验）
                if (comp != null && ComponentAccessOk(comp, node))
                {
                    var ct = comp->GetComponentType();
                    compTypes[(int)ct] = compTypes.TryGetValue((int)ct, out var n) ? n + 1 : 1;

                    if (ct == ComponentType.Icon)
                    {
                        iconComponents++;
                        var icon = (AtkComponentIcon*)comp;

                        if (icon->IconId != 0 && icon->IconId != 0xFFFFFFFF)
                        {
                            compFound[icon->IconId] = compFound.TryGetValue(icon->IconId, out var cc)
                                ? cc + 1
                                : 1;
                        }

                        var iconKey = diagUnitName + "(IconId=" + (icon->IconId == 0xFFFFFFFF ? "无效" : icon->IconId.ToString()) + ")";
                        diagIconUnits[iconKey] = diagIconUnits.TryGetValue(iconKey, out var iu) ? iu + 1 : 1;

                        if (icon->IconImage != null) Tally(GetImageNodeTexture(icon->IconImage), found, ref textures);
                        if (icon->Texture != null) Tally(&icon->Texture->AtkTexture, found, ref textures);
                    }

                    // 组件内部的节点树挂在 UldManager.NodeList 数组（不在 ChildNode 链上）—— 必须钻进去
                    var nl = comp->UldManager.NodeList;
                    if (nl != null)
                    {
                        var cnt = comp->UldManager.NodeListCount;
                        for (var k = 0; k < cnt && k < 8192; k++)
                        {
                            if (nl[k] == node) continue;
                            CollectDiagnostic(nl[k], depth + 1, found, compFound, compTypes, ref imageNodes, ref iconComponents, ref textures, ref guard);
                        }
                    }
                }
            }
        }
        catch
        {
            // 忽略
        }

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
        {
            CollectDiagnostic(child, depth + 1, found, compFound, compTypes, ref imageNodes, ref iconComponents, ref textures, ref guard);
        }
    }

    private unsafe void Tally(AtkTexture* texture, Dictionary<uint, int> found, ref int textures)
    {
        if (texture == null) return;

        textures++;

        // 顺带统计贴图路径哈希 —— 万一图标走的是图集/别的路径，哈希能离线比对出真相
        //
        // ★★ 防崩：AtkTexture 是联合体（Resource / Crest / KernelTexture 共用偏移 0x8）。
        //   不先看 TextureType 就直接当 AtkTextureResource* 去读，等于把「内核贴图对象」
        //   或「Crest」的地址当资源结构来解引用 —— 那是在读一块含义完全不同的内存，
        //   轻则统计出一堆垃圾哈希，重则访问违例把游戏带走。
        //   （上一版就是漏了这一句。）
        try
        {
            if (texture->TextureType == TextureType.Resource)
            {
                var res = texture->Resource;
                if (res != null && res->TexPathHash != 0)
                {
                    diagTexHashes[res->TexPathHash] = diagTexHashes.TryGetValue(res->TexPathHash, out var hc) ? hc + 1 : 1;
                }
            }
        }
        catch
        {
            // 忽略
        }

        var id = ResolveTextureIconId(texture);
        if (id == 0) return;

        found[id] = found.TryGetValue(id, out var c) ? c + 1 : 1;
    }

    private void DrawMacroList()
    {
        if (macroRows.Count == 0)
        {
            ImGui.TextDisabled("没读到宏。进游戏后打开一次宏面板再点「刷新宏列表」。");
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);

        for (var i = 0; i < macroRows.Count; i++)
        {
            var row = macroRows[i];
            var key = KeyOf(row.Set, row.Index);
            var isSelected = selectedKey == key;

            var rowStart = ImGui.GetCursorScreenPos();
            var rowSize = new Vector2(0f, 24f);

            if (ImGui.Selectable("##mirRow" + i, isSelected, ImGuiSelectableFlags.None, rowSize))
            {
                selectedKey = key;
                var picked = FindEntry(row.Set, row.Index);
                pathInput = picked != null && !picked.IsUrl ? picked.Source : string.Empty;
                urlInput = picked != null && picked.IsUrl ? picked.Source : string.Empty;
            }

            var replaced = FindEntry(row.Set, row.Index) != null;
            var iconHandle = GetGameIconHandle(row.IconId, 20f, drawList, rowStart + new Vector2(4f, 2f));

            var label = row.Display;
            if (row.IconId == 0) label += "   [未设图标]";
            else label += "   [图标 " + row.IconId + "]";
            if (replaced) label += "   [已替换]";

            drawList.AddText(rowStart + new Vector2(iconHandle > 0f ? 30f : 4f, 4f), textColor, label);
        }
    }

    private void DrawDetail()
    {
        var row = FindRow(selectedKey);
        if (row == null)
        {
            ImGui.TextDisabled("左边选一个宏。");
            return;
        }

        ImGui.TextUnformatted(row.Display);
        if (!string.IsNullOrWhiteSpace(row.Name))
        {
            ImGui.TextDisabled("宏名：" + row.Name);
        }

        ImGui.Separator();

        // ---- 当前图标 ----
        ImGui.TextUnformatted("当前图标");
        var iconSize = 64f;
        var iconPos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(iconSize, iconSize));

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(iconPos, iconPos + new Vector2(iconSize, iconSize), 0x40000000u, 4f);

        if (row.IconId == 0)
        {
            drawList.AddText(iconPos + new Vector2(8f, iconSize * 0.4f), 0xFF808080u, "未设置");
        }
        else
        {
            GetGameIconHandle(row.IconId, iconSize, drawList, iconPos);
        }

        ImGui.SameLine();
        ImGui.BeginGroup();

        var entry = FindEntry(row.Set, row.Index);

        try
        {
            ImGui.TextUnformatted("图标 ID：" + row.IconId);
            ImGui.TextDisabled("顺序号：" + row.IconRowId);

            if (entry != null)
            {
                ImGui.TextColored(new Vector4(0.4f, 0.85f, 0.45f, 1f), "已替换 → " + Shorten(entry.Source));
            }
            else if (row.IconId == 0)
            {
                ImGui.TextColored(new Vector4(0.95f, 0.6f, 0.3f, 1f), "游戏原生未设置图标，不允许替换");
            }
            else
            {
                ImGui.TextDisabled("尚未替换");
            }
        }
        finally
        {
            // 无论中间出什么差错，组必须关上（ImGui 的组/窗口栈错位会一路影响到下一次绘制）
            ImGui.EndGroup();
        }

        ImGui.Separator();

        var canReplace = row.IconId != 0;

        if (!canReplace)
        {
            ImGui.TextDisabled("这个宏没设过图标（图标 ID = 0），按你的要求不允许修改。");
            ImGui.TextDisabled("先在游戏里给它随便选一个图标，再回来替换。");
            return;
        }

        if (entry != null && entry.IsUrl)
        {
            if (string.IsNullOrEmpty(urlInput)) urlInput = entry.Source;
        }
        else if (entry != null && !entry.IsUrl)
        {
            if (string.IsNullOrEmpty(pathInput)) pathInput = entry.Source;
        }

        ImGui.TextUnformatted("本地图片");
        var width = MathF.Max(200f, ImGui.GetContentRegionAvail().X - 90f);
        ImGui.SetNextItemWidth(width);
        ImGui.InputText("##mirPath", ref pathInput, 512);

        ImGui.SameLine();
        if (ImGui.Button("应用##mirPathApply"))
        {
            ApplyForRow(row, pathInput, false);
        }

        ImGui.TextDisabled("支持 png / jpg / bmp / gif / webp（例如 D:\\pics\\baidu.png）");

        ImGui.Spacing();

        ImGui.TextUnformatted("网址图标");
        ImGui.SetNextItemWidth(width);
        ImGui.InputText("##mirUrl", ref urlInput, 512);

        ImGui.SameLine();
        if (ImGui.Button("抓取##mirUrlApply"))
        {
            ApplyForRow(row, urlInput, true);
        }

        ImGui.TextDisabled("填网址即可，例如 www.baidu.com —— 自动取该网站的 favicon");

        if (entry != null && entry.IsUrl)
        {
            ImGui.SameLine();
            if (ImGui.Button("重新抓取##mirUrlRefetch"))
            {
                ClearImageCache(entry.Source, true);
                var rtRefetch = GetOrCreateRuntime(entry);
                rtRefetch.Bytes = null;

                if (rtRefetch.Preview != null)
                {
                    try { rtRefetch.Preview.Dispose(); } catch { }
                    rtRefetch.Preview = null;
                }

                StartImageLoad(rtRefetch, entry.Source, true, ignoreCache: true);
                sweepNow = true;
                noHitSweeps = 0;
                autoDiagDone = false;
                statusLine = "正在重新抓取：" + Shorten(entry.Source);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("忽略本地缓存，重新联网取一次。\n（平时启动是优先用缓存的：断网、网站挂了也不影响已设好的图标）");
            }
        }

        var rt = entry != null ? FindRuntime(row.Set, row.Index) : null;
        if (rt != null)
        {
            ImGui.Spacing();

            if (rt.Preview != null && IsTexValid(rt.Preview))
            {
                ImGui.TextUnformatted("替换效果");
                var pPos = ImGui.GetCursorScreenPos();
                var pSize = 96f;
                ImGui.Dummy(new Vector2(pSize, pSize));
                ImGui.GetWindowDrawList().AddImage(rt.Preview.Handle, pPos, pPos + new Vector2(pSize, pSize));
            }

            if (!string.IsNullOrEmpty(rt.Error))
            {
                ImGui.TextColored(new Vector4(0.95f, 0.45f, 0.4f, 1f), "错误：" + rt.Error);
            }
            else if (rt.Kernel == 0)
            {
                if (rt.Building)
                {
                    ImGui.TextDisabled("图片已就绪，正在做贴图…");
                }
                else
                {
                    ImGui.TextDisabled("已就绪，正在全局替换图标编号 " + entry.IconId + " 的贴图…");
                }
            }
            else
            {
                ImGui.TextDisabled("已全局替换图标 " + entry.IconId + "（任何显示这个图标的地方都会变，无需开宏面板）");
            }

            ImGui.Spacing();
            if (ImGui.Button("还原这个宏##mirRestore"))
            {
                RemoveEntry(entry);
                statusLine = "已还原：" + row.Display;
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("说明：替换的是「这个图标 ID」，所以宏面板 / 热键栏里");
        ImGui.TextDisabled("凡是显示该宏图标的地方都会一起变。");
        ImGui.TextDisabled("设置会自动存盘：关模块 / 重启游戏 / 重启电脑后重新打开都会自动重新应用，");
        ImGui.TextDisabled("不用再设一遍 —— 只有点「还原」才会真的变回游戏原图标。");
    }

    private void ApplyForRow(MacroRow row, string source, bool isUrl)
    {
        var src = (source ?? string.Empty).Trim().Trim('"');
        if (src.Length == 0)
        {
            statusLine = isUrl ? "网址是空的" : "图片路径是空的";
            return;
        }

        if (row.IconId == 0)
        {
            statusLine = "该宏未设置图标，不允许替换";
            return;
        }

        var entry = GetOrCreateEntry(row.Set, row.Index, row.IconId, row.Name);
        entry.Source = src;
        entry.IsUrl = isUrl;

        var rt = GetOrCreateRuntime(entry);
        ReleaseKernels(rt, fullRestore: false);
        rt.Bytes = null;
        rt.Error = string.Empty;
        rt.LoadAttempts = 0;   // 用户手动指定 → 自动重试次数重新计数

        if (rt.Preview != null)
        {
            try { rt.Preview.Dispose(); } catch { }
            rt.Preview = null;
        }

        RebuildIconMap();
        StartImageLoad(rt, src, isUrl);
        sweepNow = true;
        noHitSweeps = 0;
        autoDiagDone = false;
        PersistConfig();
        statusLine = "正在加载：" + Shorten(src);
    }

    // ==================================================================
    //  小工具
    // ==================================================================

    private MacroRow FindRow(ulong key)
    {
        if (key == 0) return null;
        var set = (uint)(key >> 32);
        var index = (uint)(key & 0xFFFFFFFF);

        foreach (var row in macroRows)
        {
            if (row.Set == set && row.Index == index) return row;
        }

        return null;
    }

    private MacroIconEntry FindEntry(uint set, uint index)
    {
        foreach (var e in config.Entries)
        {
            if (e.Set == set && e.Index == index) return e;
        }

        return null;
    }

    private EntryRuntime FindRuntime(uint set, uint index)
    {
        return runtimes.TryGetValue(KeyOf(set, index), out var rt) ? rt : null;
    }

    /// <summary>画一张游戏图标，返回图标宽度（没画成返回 0）。</summary>
    private float GetGameIconHandle(uint iconId, float size, ImDrawListPtr drawList, Vector2 pos)
    {
        if (iconId == 0) return 0f;

        try
        {
            if (!iconTextureCache.TryGetValue(iconId, out var shared))
            {
                if (iconTextureCache.Count > 512) iconTextureCache.Clear();

                var lookup = new GameIconLookup(iconId, false, false, null);
                shared = DalamudServices.TextureProvider.GetFromGameIcon(lookup);
                if (shared == null) return 0f;

                iconTextureCache[iconId] = shared;
            }

            var wrap = shared.GetWrapOrEmpty();
            if (!IsTexValid(wrap)) return 0f;

            drawList.AddImage(wrap.Handle, pos, pos + new Vector2(size, size));
            return size;
        }
        catch
        {
            return 0f;
        }
    }

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

    private static string Shorten(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= 46 ? s : s.Substring(0, 43) + "...";
    }

    private static void LogError(string message)
    {
        try { DalamudServices.PluginLog.Error("[MacroIconReplace] " + message); } catch { }
    }
}
