using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.Notifications;
using OmniToolbox.UI.Theme;
using OmenTools;
using OmenTools.OmenService;

namespace OmniToolbox.LocalModules;

/// <summary>
/// 游戏内音乐播放器（QQ音乐单平台）。
///  - 真实播放：插件内直接解析播放地址并出声（MCI / winmm），无需本地客户端
///  - 搜索 + LRC 歌词滚动（按真实播放进度同步）
///  - QQ 扫码登录；登录后可读取「我喜欢的音乐」与全部收藏歌单
///  - 搜索结果、我的歌单里每首歌都带「我喜欢 / 取消喜欢」按钮
///  - 「我喜欢」增删：走 QQ音乐 musics.fcg 加密通道（旧 fcg 接口已全部失效）。
///    签名与加解密由官方 VMP 混淆脚本完成，无法在 C# 内复算，故由
///    `%LOCALAPPDATA%\OmniMusic\node\node.exe`（随插件预置的 Node 运行时）执行；
///    也可在设置页指定自装 Node 的 node.exe 路径。
///  - 播放列表（队列）管理 + 自动连播
///  - 悬浮窗：封面放大模糊做背景、大圆角卡片、矢量图标控制键，
///    可固定在角色头顶或自由拖动（左上角小圆钮切换）
///
/// 线程约定（重要）：后台线程只允许写 volatile 字段，绝不允许调用
/// 游戏 / 插件 / ImGui / 通知 API，一切界面与配置改动都由主线程消费。
/// </summary>
public sealed class MusicPlayer : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "游戏内音乐播放器",
        Description = "QQ音乐：真实播放、搜索、歌词、扫码登录、我喜欢/收藏歌单、变色悬浮窗",
        Category = ModuleCategory.Interface,
        Author = "小烟酒",
        Commands = new[]
        {
            new ModuleCommand("/omni MusicPlayer 打开/关闭悬浮窗", "/omni MusicPlayer")
        }
    };

    // ------------------------------------------------------------------
    // 配置
    // ------------------------------------------------------------------
    [Serializable]
    public sealed class MusicPlayerConfig
    {
        public bool WindowVisible { get; set; } = true;
        public bool WindowLocked { get; set; }
        public bool AnchorToHead { get; set; }
        public float HeadOffsetY { get; set; } = 2.3f;
        public float OverlayX { get; set; } = 200f;
        public float OverlayY { get; set; } = 200f;
        public float OverlayScale { get; set; } = 1.0f;      // 悬浮窗大小
        public bool DynamicColor { get; set; } = true;       // 跟随歌曲变色
        public float OverlayOpacity { get; set; } = 0.86f;   // 背景（卡片）不透明度
        public float CoverOpacity { get; set; } = 1.0f;      // 封面图不透明度（与背景独立）
        public bool ShowLyricInHead { get; set; } = true;    // 头顶窗显示当前歌词行
        public int ContentTab { get; set; }                  // 0搜索 1推荐 2收藏 3播放列表
        public int Volume { get; set; } = 80;                // 0-100
        public float LyricFontScale { get; set; } = 1.0f;
        public bool ShowMeta { get; set; } = true;           // 显示作词作曲
        public List<Track> Queue { get; set; } = new();      // 播放列表
        public string QQCookie { get; set; } = string.Empty;
        public string QQNickname { get; set; } = string.Empty;
        public string QQMusicId { get; set; } = string.Empty;
        public string NodePath { get; set; } = string.Empty;   // 本机 node.exe 路径（收藏写操作需要，留空自动探测）
        // 模块加载时自动检测 Node：本机已有则完全不下载；没有才自动安装一次（仅一次）。
        public bool NodeAutoInstall { get; set; } = true;
        // Node 自动安装的下载地址（首选；留空则只用下面的内置多源）。
        // 默认 = 本仓库 Release 附件，并且**前置了国内可达的 GitHub 反代镜像**——
        // 直连 github.com / raw.githubusercontent.com 在国内经常拉不动，走镜像才稳。
        // 该地址失败时会自动按 BuiltinNodeUrls 依次重试（镜像 → 直连 → Node 官方国内镜像）。
        public string NodeDownloadUrl { get; set; } =
            "https://gh-proxy.com/https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/download/node-v22.22.2/node-v22.22.2-win-x64.zip";
    }

    private MusicPlayerConfig config = new();

    // ------------------------------------------------------------------
    // 网络
    // ------------------------------------------------------------------
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly HttpClientHandler AuthHandler = new()
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true,
        AllowAutoRedirect = true
    };

    private static readonly HttpClient AuthHttp = new(AuthHandler) { Timeout = TimeSpan.FromSeconds(20) };

    // 下载 Node 运行时专用：80MB 级别的文件，20s 超时必然中断，这里用不设超时
    private static readonly HttpClient DlHttp = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private const string UA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    private const string QQ = "QQ音乐";
    private const int FavDirId = 201;   // QQ音乐「我喜欢」的歌单目录 ID

    // ------------------------------------------------------------------
    // 状态
    // ------------------------------------------------------------------
    // 搜索
    private string searchInput = string.Empty;
    private readonly List<Track> searchResults = new();
    private bool searching;
    private string searchError = string.Empty;
    private volatile List<Track>? pendingSearchResults;

    // 我喜欢 / 歌单
    private sealed record PlaylistInfo(string Id, string Name, int Count);

    private readonly List<Track> favTracks = new();
    private readonly HashSet<long> likedIds = new();
    private bool loadingFav;
    private string favError = string.Empty;
    private volatile List<Track>? pendingFavTracks;

    // 推荐（QQ音乐榜单，未登录即可加载）
    private readonly List<Track> recommendTracks = new();
    private bool loadingRecommend;
    private string recommendError = string.Empty;
    private volatile List<Track>? pendingRecommendTracks;
    private int recommendTopId = 26;
    private string recommendTopName = "热歌榜";
    private bool recommendInited;      // 推荐页是否已自动加载过一次

    private readonly List<PlaylistInfo> playlists = new();
    private bool loadingPlaylists;
    private string playlistError = string.Empty;
    private volatile List<PlaylistInfo>? pendingPlaylists;

    // 收藏页横向切换：0=我喜欢的音乐，1..n=playlists[i-1]
    private int favViewIdx;
    private bool favListsInited;      // 是否已自动拉过一次歌单列表
    // 各歌单曲目缓存（dirId -> 曲目列表），切页不重复拉取
    private readonly Dictionary<string, List<Track>> playlistTrackCache = new();
    private readonly HashSet<string> playlistTrackLoading = new();
    private sealed class PlTracksResult { public string DirId = ""; public List<Track> Tracks = new(); }
    private volatile PlTracksResult? pendingPlaylistTrackCache;

    // 喜欢状态回执（后台 → 主线程）
    private sealed class FavToggleResult { public long SongId; public string Mid = ""; public bool Liked; public bool Success; public string Message = ""; }
    private volatile FavToggleResult? pendingFavToggle;

    // 播放
    private Track? currentTrack;
    private int currentQueueIndex = -1;
    private string? pendingPlayUrl;
    private bool resolvingPlay;
    private volatile string playError = string.Empty;
    private DateTime lastAutoNextTime = DateTime.MinValue;

    // 歌词
    private readonly List<LrcLine> currentLyrics = new();
    private bool loadingLyric;
    private string lyricError = string.Empty;
    private int lyricCurrentIndex = -1;
    private volatile List<LrcLine>? pendingLyrics;

    // 封面（volatile 只能用于引用类型，用小包装类）
    private sealed class CoverBytes { public string Url = ""; public byte[] Bytes = Array.Empty<byte>(); }
    private sealed class TexResult { public string Url = ""; public IDalamudTextureWrap? Tex; }

    private volatile CoverBytes? pendingCoverBytes;
    private volatile TexResult? pendingCoverTex;
    private volatile TexResult? pendingBlurTex;
    private readonly Dictionary<string, IDalamudTextureWrap?> coverCache = new();
    private readonly HashSet<string> coverLoading = new();
    private readonly Queue<string> coverOrder = new();   // 插入顺序，用于容量淘汰

    // 模糊背景：用同一封面的 90x90 小图拉伸铺满 = 放大模糊（参考主流播放器悬浮窗）
    private readonly Dictionary<string, IDalamudTextureWrap?> blurCache = new();
    private readonly HashSet<string> blurLoading = new();

    // 玻璃渐变底：内嵌 1x64 白色垂直渐变 PNG（不透明度上 255 → 下 110）。
    // 圆角一律用 DrawRoundedImage 的逐列裁切实现——本宿主环境下 ImGui 的 rounding 参数
    // （AddRectFilled / AddImageRounded 都一样）会被忽略，实测全部渲染成直角。
    private const string GlassGradientPngB64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAABACAYAAADbER1AAAAAhUlEQVR42hXEgWZCAQBA0b44kUSSSGISyUQSmUhmJJFEMpFEEkliEkkkiSSm3rncE3oFhYL/8cQDd9xwxQVnnHDEAXvs8IctNlhjhSUWmGOGKSYY4xcjDDFAHz100UEbP/hGC0008IU6aqiigjI+UUIRBeTxgRyyyCCNFJJIII4Yoogg/AYGeuzhjJiGwwAAAABJRU5ErkJggg==";
    private IDalamudTextureWrap? glassTexture;
    private volatile IDalamudTextureWrap? pendingGlassTex;
    private bool glassTexRequested;

    // 登录（0空闲 1等待扫码 2已扫描 3成功 4失败/过期）
    private volatile byte qqLoginStage;
    private volatile string loginMsg = string.Empty;
    private volatile string loginDebug = string.Empty;
    private volatile byte[]? pendingQrBytes;
    private volatile bool loginRunning;
    private IDalamudTextureWrap? qrTexture;
    private string qrTextureKey = string.Empty;
    private string qrRequestedKey = string.Empty;
    private IDalamudTextureWrap? pendingQrTex;
    private volatile string? pendingQrTexKey;

    // 登录结果（后台 → 主线程，避免后台线程碰 config / 通知 API）
    private sealed class LoginOutcome
    {
        public string Nickname = "";
        public string MusicId = "";
        public string Cookie = "";
        public bool Success;
        public string Message = "";
    }

    private volatile LoginOutcome? pendingLogin;
    private volatile string loginNick = string.Empty;
    private bool loginDirty;          // 需要触发一次配置保存（DrawSettings 返回 true）


    // 跨线程 → 主线程的消息 / 配置写入队列
    private readonly object chatLock = new();
    private readonly List<string> pendingChats = new();

    private string searchDebug = string.Empty;

    private bool disposed;

    // ------------------------------------------------------------------
    // 日志
    // ------------------------------------------------------------------
    private static void Log(string msg)
    {
        try { DalamudServices.PluginLog.Info("[MusicPlayer] " + msg); } catch { }
    }

    private static void LogError(string msg)
    {
        try { DalamudServices.PluginLog.Error("[MusicPlayer] " + msg); } catch { }
    }

    private static string Snip(string? text, int max = 160)
    {
        if (string.IsNullOrEmpty(text)) return "(空响应)";
        var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    /// <summary>后台线程请求主线程播报一条聊天消息（绝不直接调用通知 API）。</summary>
    private void QueueChat(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        lock (chatLock) pendingChats.Add(message);
    }

    // ------------------------------------------------------------------
    // 生命周期
    // ------------------------------------------------------------------
    protected override void OnEnable()
    {
        disposed = false;
        RestoreCookies(config.QQCookie);
        config.ContentTab = Math.Clamp(config.ContentTab, 0, 3);
        DalamudServices.PluginInterface.UiBuilder.Draw += Draw;

        // 注意：这里刻意不发任何网络请求。原先在 OnEnable 里自动拉"我喜欢/歌单"，
        // 会让「启用/重载模块」直接触发大响应解析，是重载崩溃的放大因素。
        // 改由登录成功后、或用户点「刷新数据」时拉取。
        //
        // 唯一例外：Node 运行时的自检安装（后台线程、延迟启动、只写磁盘不解析大响应）。
        // 分享场景下接收者不必自己装 Node；**本机已有 Node 则完全不下载**。
        _ = Task.Run(EnsureNodeRuntimeOnLoadAsync);
    }

    protected override void OnDisable()
    {
        try { DalamudServices.PluginInterface.UiBuilder.Draw -= Draw; } catch { }
        SafeShutdown();
    }

    protected override void OnDispose()
    {
        disposed = true;
        SafeShutdown();
    }

    /// <summary>释放一切可能残留的原生 / 图形资源。重载模块时必须走一遍。</summary>
    private void SafeShutdown()
    {
        try { EngineStop(); } catch { }
        try { MciClose(); } catch { }
        try { ReleaseTextures(); } catch { }
        try { lock (chatLock) pendingChats.Clear(); } catch { }
        pendingLogin = null;
        pendingMciFile = null;
        pendingPlayUrl = null;
        pendingQrBytes = null;
        pendingCoverBytes = null;
        currentTrack = null;
        enginePlaying = false;
    }

    /// <summary>释放全部贴图（模块重载时旧贴图不释放会变成野句柄）。</summary>
    private void ReleaseTextures()
    {
        foreach (var kv in coverCache)
        {
            try { if (kv.Value is IDisposable d) d.Dispose(); } catch { }
        }
        coverCache.Clear();
        coverLoading.Clear();
        foreach (var kv in blurCache)
        {
            try { if (kv.Value is IDisposable d) d.Dispose(); } catch { }
        }
        blurCache.Clear();
        blurLoading.Clear();
        try { if (qrTexture is IDisposable d1) d1.Dispose(); } catch { }
        try { if (pendingQrTex is IDisposable d2) d2.Dispose(); } catch { }
        qrTexture = null;
        pendingQrTex = null;
        qrTextureKey = string.Empty;
        qrRequestedKey = string.Empty;
        try { if (glassTexture is IDisposable d3) d3.Dispose(); } catch { }
        try { if (pendingGlassTex is IDisposable d4) d4.Dispose(); } catch { }
        glassTexture = null;
        pendingGlassTex = null;
        glassTexRequested = false;
    }

    /// <summary>
    /// 贴图可用性判断。刻意不依赖 ImTextureID 的内部字段（不同 Dalamud 版本定义不同），
    /// 只用 EqualityComparer 与默认值比较，编译期/运行期都不会出问题。
    /// </summary>
    private static bool IsTexValid(IDalamudTextureWrap? t)
    {
        if (t == null) return false;
        try
        {
            return !EqualityComparer<ImTextureID>.Default.Equals(t.Handle, default);
        }
        catch
        {
            return true;
        }
    }

    public override bool HasSettings => true;

    // ------------------------------------------------------------------
    // 设置面板
    // ------------------------------------------------------------------
    public override bool DrawSettings()
    {
        try
        {
            return DrawSettingsCore();
        }
        catch (Exception e)
        {
            try { LogError("设置面板绘制异常：" + e); } catch { }
            return false;
        }
    }

    private bool DrawSettingsCore()
    {
        var changed = false;
        using var scope = new ComicStyleScope();

        ConsumePending();

        // 播放器本体在上，设置区折叠收纳到播放器下方（见本方法末尾）
        changed |= DrawLoginSection();
        ImGui.Separator();

        var tab = config.ContentTab;

        // 【崩溃防御】这里绝不做任何 PushStyleColor/PopStyleColor（历史崩溃根因：
        // 条件式 push/pop 把宿主 ComicStyleScope 的样式栈抽干 → cimgui 访问冲突）。
        // 用 ImGui 原生 TabBar：当前页高亮由 ImGui 自己绘制，文字清晰、无字形依赖。
        // BeginTabItem 用不带 ref p_open 的重载（内部向 native 传 null），
        // 这样标签上不会渲染关闭叉叉。
        var tabBarBegan = false;
        try
        {
            tabBarBegan = ImGui.BeginTabBar("##musicContentTabs");
            if (tabBarBegan)
            {
                var t0 = ImGui.BeginTabItem("搜索");
                try { if (t0) tab = 0; } finally { if (t0) ImGui.EndTabItem(); }

                var t1 = ImGui.BeginTabItem("推荐");
                try { if (t1) tab = 1; } finally { if (t1) ImGui.EndTabItem(); }

                var t2 = ImGui.BeginTabItem("收藏");
                try { if (t2) tab = 2; } finally { if (t2) ImGui.EndTabItem(); }

                var t3 = ImGui.BeginTabItem("播放列表");
                try { if (t3) tab = 3; } finally { if (t3) ImGui.EndTabItem(); }
            }
        }
        finally
        {
            if (tabBarBegan) ImGui.EndTabBar();
        }
        if (tab != config.ContentTab) { config.ContentTab = tab; changed = true; }

        ImGui.Separator();

        switch (config.ContentTab)
        {
            case 0: changed |= DrawSearchTab(); break;
            case 1: changed |= DrawRecommendTab(); break;
            case 2: changed |= DrawFavoriteTab(); break;
            default: changed |= DrawQueueTab(); break;
        }

        ImGui.Separator();

        DrawNowPlayingSection(ref changed);

        ImGui.Separator();

        // ---------------- 设置折叠区（置于播放器下方，可收起/展开） ----------------
        if (ImGui.CollapsingHeader("音乐播放器设置", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var visible = config.WindowVisible;
            if (ImGui.Checkbox("显示悬浮窗", ref visible))
            {
                config.WindowVisible = visible;
                if (visible) { overlaySuspended = false; drawErrorStreak = 0; }
                changed = true;
            }

            // 固定到角色头顶：这里与悬浮窗左上角的「拖/顶」小圆钮等效，两处都可切换。
            var anchor = config.AnchorToHead;
            if (ImGui.Checkbox("固定到角色头顶（跟随角色移动）", ref anchor))
            {
                if (config.AnchorToHead && !anchor && lastOverlaySize.X > 1f)
                {
                    // 取消固定时以「当前实际位置」作为自由位置，避免跳回很久以前的旧坐标
                    config.OverlayX = lastOverlayPos.X;
                    config.OverlayY = lastOverlayPos.Y;
                }
                config.AnchorToHead = anchor;
                if (anchor) smoothAnchorInit = false;    // 重新对齐，避免从旧锚点插值过去
                changed = true;
                Log($"悬浮窗：取消/启用固定头顶 -> {config.AnchorToHead}");
            }

            if (ImGui.Button("把悬浮窗拉回屏幕中央##ovRecenter"))
            {
                var io = ImGui.GetIO();
                var w = lastOverlaySize.X > 1f ? lastOverlaySize.X : 340f * config.OverlayScale;
                var h = lastOverlaySize.Y > 1f ? lastOverlaySize.Y : 148f * config.OverlayScale;
                config.AnchorToHead = false;             // 锚定时位置由头顶决定，先解除
                smoothAnchorInit = false;
                config.OverlayX = Math.Max(4f, (io.DisplaySize.X - w) * 0.5f);
                config.OverlayY = Math.Max(4f, (io.DisplaySize.Y - h) * 0.5f);
                config.WindowVisible = true;
                overlaySuspended = false;
                drawErrorStreak = 0;
                overlayForcePos = true;                  // 下一帧立即生效
                changed = true;
                Log($"悬浮窗：位置重置到 ({config.OverlayX:F0},{config.OverlayY:F0})，屏幕 {io.DisplaySize.X:F0}x{io.DisplaySize.Y:F0}");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("换了分辨率/窗口模式后如果找不到悬浮窗，点这里把它拉回屏幕中央。\n（正常拖到屏幕外时也会自动被拉回来，不会再丢。）");

            if (config.AnchorToHead)
            {
                ImGui.TextUnformatted("头顶高度");
                ImGui.SetNextItemWidth(-1f);
                var off = config.HeadOffsetY;
                if (ImGui.SliderFloat("##ovHeadY", ref off, 1.2f, 4.5f, "%.2f"))
                {
                    config.HeadOffsetY = off;
                    changed = true;
                }
            }

            ImGui.TextUnformatted("悬浮窗大小");
            ImGui.SetNextItemWidth(-1f);
            var ovScale = config.OverlayScale;
            if (ImGui.SliderFloat("##ovScale", ref ovScale, 0.3f, 1.5f, "%.2f"))
            {
                config.OverlayScale = ovScale;
                changed = true;
            }

            ImGui.TextUnformatted("背景不透明度（0=完全透明）");
            ImGui.SetNextItemWidth(-1f);
            var op = config.OverlayOpacity;
            if (ImGui.SliderFloat("##ovOpacity", ref op, 0f, 1f, "%.2f"))
            {
                config.OverlayOpacity = op;
                changed = true;
            }

            ImGui.TextUnformatted("封面不透明度（0=完全透明）");
            ImGui.SetNextItemWidth(-1f);
            var cop = config.CoverOpacity;
            if (ImGui.SliderFloat("##ovCoverOpacity", ref cop, 0f, 1f, "%.2f"))
            {
                config.CoverOpacity = cop;
                changed = true;
            }

            // 「跟随歌曲改变背景色」不再提供开关：模糊封面背景下该选项已几乎无感知，
            // DynamicColor 逻辑保留（供 CurrentSongPalette 兜底底色使用），config 字段不动以兼容旧存档。

            var headLyric = config.ShowLyricInHead;
            if (ImGui.Checkbox("悬浮窗显示当前歌词", ref headLyric)) { config.ShowLyricInHead = headLyric; changed = true; }

            // 收藏写操作依赖本机 Node.js（官方签名/响应解密是 VMP 混淆脚本，无法在 C# 内复算）
            // 安装时机 = 模块加载时一次性自检：已有就不动，没有才装一次（不再在收藏时下载）。
            ImGui.TextUnformatted("Node 运行时（收藏功能需要）");
            var found = FindNodeExeCached();
            ImGui.TextDisabled(found != null ? "已就绪：检测到本机 node.exe，不会下载" : "未检测到：加载模块时会自动安装一次，也可点下面的按钮立即安装");
            ImGui.TextDisabled("安装位置：%LOCALAPPDATA%\\OmniMusic\\node\\node.exe");

            var autoInst = config.NodeAutoInstall;
            if (ImGui.Checkbox("加载模块时自动检测并安装（已有则跳过）", ref autoInst))
            {
                config.NodeAutoInstall = autoInst;
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("只在「本机没有任何 Node」时才会下载安装，装好后不再重复下载。\n关掉后只能手动指定 node.exe 路径或点下面的按钮安装。");

            var nodePath = config.NodePath ?? string.Empty;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputTextWithHint("##nodePathBox", "node.exe 完整路径（可选，留空=自动探测）", ref nodePath, 260))
            {
                config.NodePath = nodePath.Trim();
                changed = true;
            }

            var nodeUrl = config.NodeDownloadUrl ?? string.Empty;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputTextWithHint("##nodeUrlBox", "Node 下载地址（zip 发行包或裸 node.exe 直链）", ref nodeUrl, 400))
            {
                config.NodeDownloadUrl = nodeUrl.Trim();
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("默认指向本仓库 Release 的 Node 附件（走国内可达的 GitHub 镜像），仅在缺 Node 时下载一次。\n该源失败会自动依次重试：另一个镜像 → GitHub 直连 → Node 官方国内镜像（npmmirror）。");

            if (nodeDlRunning)
            {
                ImGui.TextDisabled(nodeDlState);
            }
            else
            {
                if (ImGui.Button("立即下载并安装 Node##nodeDlNow"))
                {
                    nodeDlState = "准备安装…";
                    _ = Task.Run(async () => { await TryDownloadNodeAsync(); });
                }
                if (nodeDlState.Length > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(nodeDlState);
                }
            }
        }

        if (loginDirty)
        {
            loginDirty = false;
            changed = true;
        }

        return changed;
    }

    public override bool ResetSettings()
    {
        config = new MusicPlayerConfig();
        return true;
    }

    public override bool TryHandleCommand(string arguments)
    {
        config.WindowVisible = !config.WindowVisible;
        if (config.WindowVisible) { overlaySuspended = false; drawErrorStreak = 0; }
        OmniNotifier.Chat($"音乐播放器悬浮窗已{(config.WindowVisible ? "打开" : "关闭")}");
        return true;
    }

    // ------------------------------------------------------------------
    // 登录区
    // ------------------------------------------------------------------
    private bool DrawLoginSection()
    {
        var changed = false;
        ImGui.TextUnformatted("账号登录（QQ音乐）");

        if (!string.IsNullOrEmpty(config.QQNickname))
        {
            ImGui.TextColored(new Vector4(0.4f, 0.85f, 0.45f, 1f), $"已登录：{config.QQNickname}");
            ImGui.SameLine();
            if (!string.IsNullOrEmpty(config.QQMusicId))
            {
                ImGui.TextDisabled($"(musicid {config.QQMusicId})");
            }
            ImGui.SameLine();
            if (ImGui.Button("刷新数据##qqRefresh"))
            {
                TriggerLoadFavorites();
                TriggerLoadPlaylists();
            }
            ImGui.SameLine();
            if (ImGui.Button("退出登录##qqLogout"))
            {
                ClearCookies("qq.com");
                config.QQCookie = string.Empty;
                config.QQNickname = string.Empty;
                config.QQMusicId = string.Empty;
                favTracks.Clear();
                likedIds.Clear();
                playlists.Clear();
                playlistTrackCache.Clear();
                playlistTrackLoading.Clear();
                favViewIdx = 0;
                favListsInited = false;
                loginMsg = string.Empty;
                loginDebug = string.Empty;
                changed = true;
            }
        }
        else
        {
            ImGui.TextDisabled("未登录");
            ImGui.SameLine();
            if (ImGui.Button("QQ音乐扫码登录##qqLogin") && !loginRunning)
            {
                StartQQLogin();
            }
            if (!string.IsNullOrEmpty(config.QQCookie) && string.IsNullOrEmpty(config.QQNickname))
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(检测到残留登录态，可重新扫码)");
            }
        }

        // 二维码显示
        var qr = pendingQrBytes;
        if (qr != null && qr.Length > 0)
        {
            EnsureQrTexture();
            if (IsTexValid(qrTexture))
            {
                ImGui.Image(qrTexture!.Handle, new Vector2(160f, 160f));
            }
            else
            {
                ImGui.TextDisabled("二维码图片解码中…");
            }
        }

        var msg = loginMsg;
        if (!string.IsNullOrEmpty(msg))
        {
            var isErr = qqLoginStage == 4;
            ImGui.TextColored(
                isErr ? new Vector4(1f, 0.5f, 0.4f, 1f) : new Vector4(0.6f, 0.9f, 0.7f, 1f), msg);
        }
        var dbg = loginDebug;
        if (!string.IsNullOrEmpty(dbg))
        {
            ImGui.TextDisabled(dbg);
        }

        return changed;
    }

    // ------------------------------------------------------------------
    // 搜索页
    // ------------------------------------------------------------------
    private bool DrawSearchTab()
    {
        var changed = false;

        ImGui.SetNextItemWidth(-90f);
        var submit = ImGui.InputTextWithHint("##musicSearchBox", "输入歌名/歌手，按回车或点右侧搜索", ref searchInput, 128,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var doSearch = ImGui.Button("搜索##doSearchBtn", new Vector2(70f, 0f));

        if (submit || doSearch)
        {
            Log($"搜索触发：\"{searchInput}\"（{searchInput.Length}字） searching={searching}");
            if (searching) searchDebug = "上一次搜索还在进行中，请稍候";
            else TriggerSearch(searchInput);
        }

        ImGui.TextDisabled($"输入框：[{(searchInput.Length == 0 ? "空" : searchInput)}]  {searchInput.Length}字  状态：{(searching ? "搜索中…" : "空闲")}");
        if (!string.IsNullOrEmpty(searchDebug)) ImGui.TextDisabled(searchDebug);
        if (!string.IsNullOrEmpty(searchError))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), searchError);
        }

        ImGui.TextUnformatted($"搜索结果 ({searchResults.Count})  — 双击播放，点「收藏」加入我喜欢");
        // BeginChild / EndChild 必须成对：中途抛异常也要 EndChild，否则 ImGui 栈失衡会连锁崩溃
        ImGui.BeginChild("##musicResults", new Vector2(0, 230f), true);
        try
        {
            for (var i = 0; i < searchResults.Count; i++)
            {
                var song = searchResults[i];
                DrawSongRow($"sr{i}", song, searchResults, i, ref changed);
            }
            if (searchResults.Count == 0)
            {
                ImGui.TextDisabled("输入歌名后点击搜索（需先在 3-2 登录以获得完整曲库）");
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        return changed;
    }

    // ------------------------------------------------------------------
    // 推荐页（QQ音乐榜单，未登录即可用）
    // ------------------------------------------------------------------
    private bool DrawRecommendTab()
    {
        var changed = false;

        ImGui.TextDisabled("QQ音乐榜单（无需登录即可加载，双击播放，可收藏）");

        // 首次进入推荐页自动拉一次默认榜单；用标志位保证只拉一次，不会每帧重复请求
        if (!recommendInited && !loadingRecommend && recommendTracks.Count == 0)
        {
            recommendInited = true;
            TriggerLoadRecommend(recommendTopId, recommendTopName);
        }

        // 榜单切换：同样只改文字标记，不碰样式栈
        var tops = new (int Id, string Name)[]
        {
            (26, "热歌榜"), (4, "新歌榜"), (62, "飙升榜"), (27, "经典老歌"), (72, "情歌榜")
        };
        for (var i = 0; i < tops.Length; i++)
        {
            // 圆角胶囊按钮：InvisibleButton 命中 + DrawList 自绘选中态，不用样式栈
            if (DrawPillButton($"##recTop{tops[i].Id}", tops[i].Name, recommendTopId == tops[i].Id, 74f))
            {
                recommendTopId = tops[i].Id;
                recommendTopName = tops[i].Name;
                TriggerLoadRecommend(recommendTopId, recommendTopName);
            }
            if (i < tops.Length - 1) ImGui.SameLine();
        }
        ImGui.NewLine();

        if (ImGui.Button("刷新榜单##recReload") && !loadingRecommend)
        {
            TriggerLoadRecommend(recommendTopId, recommendTopName);
        }
        ImGui.SameLine();
        if (recommendTracks.Count > 0 && ImGui.Button("整榜播放##recPlayAll"))
        {
            SetQueueAndPlay(recommendTracks, 0);
            QueueChat($"已加载{recommendTopName}（{recommendTracks.Count}首），开始播放");
            changed = true;
        }
        ImGui.SameLine();
        if (loadingRecommend) ImGui.TextDisabled("加载中...");
        if (!string.IsNullOrEmpty(recommendError))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), recommendError);
        }

        ImGui.TextUnformatted($"{recommendTopName}  ({recommendTracks.Count} 首)");

        ImGui.BeginChild("##recList", new Vector2(0, 230f), true);
        try
        {
            for (var i = 0; i < recommendTracks.Count; i++)
            {
                DrawSongRow($"rec{i}", recommendTracks[i], recommendTracks, i, ref changed);
            }
            if (recommendTracks.Count == 0)
            {
                ImGui.TextDisabled("点击上方任意榜单名即可加载推荐歌曲");
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        return changed;
    }

    // ------------------------------------------------------------------
    // 收藏页（我喜欢的音乐 + 收藏的歌单，合并为一个「收藏」）
    // ------------------------------------------------------------------
    private bool DrawFavoriteTab()
    {
        var changed = false;

        if (string.IsNullOrEmpty(config.QQMusicId))
        {
            // 未登录：只提示，绝不发起任何请求、绝不触碰列表，避免误触发崩溃
            ImGui.TextWrapped("收藏内容需要登录 QQ音乐。请先在上方「账号登录」里扫码登录，登录成功后这里会自动加载。");
            if (!loginRunning)
            {
                ImGui.NewLine();
                if (ImGui.Button("立即扫码登录##favGoLogin")) StartQQLogin();
            }
            return false;
        }

        // 首次进入自动拉一次歌单列表（横向切换排需要歌单名）
        if (!favListsInited && !loadingPlaylists && playlists.Count == 0)
        {
            favListsInited = true;
            TriggerLoadPlaylists();
        }

        // ---------------- 横向切换排：我喜欢的音乐 + 各歌单 + 刷新 ----------------
        var favLabel = "我喜欢的音乐";
        if (DrawPillButton("##favViewFav", favLabel, favViewIdx == 0, ImGui.CalcTextSize(favLabel).X + 26f))
            favViewIdx = 0;
        for (var i = 0; i < playlists.Count; i++)
        {
            ImGui.SameLine();
            var pl = playlists[i];
            var label = Truncate(pl.Name, 7);
            var w = ImGui.CalcTextSize(label).X + 26f;
            if (DrawPillButton($"##favViewPl{i}", label, favViewIdx == i + 1, w))
            {
                favViewIdx = i + 1;
                EnsurePlaylistTracks(pl);
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("刷新##plReload") && !loadingPlaylists) TriggerLoadPlaylists();
        ImGui.SameLine();
        if (loadingPlaylists) ImGui.TextDisabled("歌单列表加载中...");
        if (!string.IsNullOrEmpty(playlistError))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), playlistError);
        }

        ImGui.NewLine();

        if (favViewIdx <= 0)
        {
            // ---------------- 我喜欢的音乐 ----------------
            if (ImGui.Button("刷新##favReload") && !loadingFav) TriggerLoadFavorites();
            ImGui.SameLine();
            if (favTracks.Count > 0 && ImGui.Button("整单播放##favPlayAll"))
            {
                SetQueueAndPlay(favTracks, 0);
                QueueChat($"已加载我喜欢的音乐（{favTracks.Count}首），开始播放");
                changed = true;
            }
            ImGui.SameLine();
            if (loadingFav) ImGui.TextDisabled("加载中...");
            if (!string.IsNullOrEmpty(favError))
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), favError);
            }

            ImGui.BeginChild("##favList", new Vector2(0, 300f), true);
            try
            {
                for (var i = 0; i < favTracks.Count; i++)
                {
                    DrawSongRow($"fav{i}", favTracks[i], favTracks, i, ref changed);
                }
                if (favTracks.Count == 0)
                {
                    ImGui.TextDisabled(loadingFav ? "加载中..." : "点击「刷新」加载我喜欢的音乐");
                }
            }
            finally
            {
                ImGui.EndChild();
            }
        }
        else
        {
            // ---------------- 某个收藏歌单 ----------------
            var pi = favViewIdx - 1;
            if (pi >= playlists.Count)
            {
                favViewIdx = 0;   // 歌单列表刚被刷新过，索引失效：回到我喜欢
            }
            else
            {
                var pl = playlists[pi];
                var has = playlistTrackCache.TryGetValue(pl.Id, out var tracks);
                if (ImGui.Button("整单播放##plPlayAll"))
                {
                    if (has && tracks is { Count: > 0 })
                    {
                        SetQueueAndPlay(tracks, 0);
                        QueueChat($"已加载「{pl.Name}」（{tracks.Count}首），开始播放");
                        changed = true;
                    }
                    else
                    {
                        EnsurePlaylistTracks(pl);
                    }
                }
                ImGui.SameLine();
                ImGui.TextDisabled($"{pl.Name}（{pl.Count}首）");
                ImGui.SameLine();
                if (playlistTrackLoading.Contains(pl.Id)) ImGui.TextDisabled("加载中...");

                ImGui.BeginChild("##plTracks", new Vector2(0, 300f), true);
                try
                {
                    if (has && tracks != null)
                    {
                        for (var i = 0; i < tracks.Count; i++)
                        {
                            DrawSongRow($"pl{i}", tracks[i], tracks, i, ref changed);
                        }
                        if (tracks.Count == 0 && !playlistTrackLoading.Contains(pl.Id))
                        {
                            ImGui.TextDisabled("该歌单没有可用曲目");
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled("正在加载歌单曲目...");
                    }
                }
                finally
                {
                    ImGui.EndChild();
                }
            }
        }

        return changed;
    }

    // ------------------------------------------------------------------
    // 播放列表页
    // ------------------------------------------------------------------
    private bool DrawQueueTab()
    {
        var changed = false;

        if (ImGui.Button("清空播放列表##qClear") && config.Queue.Count > 0)
        {
            config.Queue.Clear();
            currentQueueIndex = -1;
            currentTrack = null;
            changed = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"共 {config.Queue.Count} 首");

        ImGui.BeginChild("##musicQueue", new Vector2(0, 230f), true);
        try
        {
            for (var i = config.Queue.Count - 1; i >= 0; i--)
            {
                var song = config.Queue[i];
                var isCurrent = i == currentQueueIndex;
                var doubleClicked = false;
                var removed = false;
                ImGui.PushID($"q{i}");
                try
                {
                    // 用 Selectable 自带的高亮，替代手写 Push/PopStyleColor（样式栈必须绝对平衡）
                    // 按钮统一右对齐（与 DrawSongRow 同一策略）
                    var st = ImGui.GetStyle();
                    var wPlay = ImGui.CalcTextSize("播放").X + st.FramePadding.X * 2;
                    var wFav = ImGui.CalcTextSize("已喜欢").X + st.FramePadding.X * 2;
                    var wDel = ImGui.CalcTextSize("移除").X + st.FramePadding.X * 2;
                    var btnTotal = wPlay + wFav + wDel + st.ItemSpacing.X * 2;
                    var contentRight = ImGui.GetWindowContentRegionMax().X;
                    var rowW = Math.Max(80f, contentRight - ImGui.GetCursorPosX() - btnTotal - st.ItemSpacing.X);
                    ImGui.Selectable($"{(isCurrent ? "[播放中] " : "")}{song.Title} - {song.Artist}", isCurrent,
                        ImGuiSelectableFlags.None, new Vector2(rowW, 0));

                    doubleClicked = ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(0);
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(contentRight - btnTotal);
                    if (ImGui.SmallButton("播放")) doubleClicked = true;
                    ImGui.SameLine();
                    DrawFavButton(song, ref changed);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("移除"))
                    {
                        config.Queue.RemoveAt(i);
                        if (i == currentQueueIndex) currentQueueIndex = -1;
                        else if (i < currentQueueIndex) currentQueueIndex--;
                        removed = true;
                        changed = true;
                    }
                }
                finally
                {
                    ImGui.PopID();
                }
                if (removed) continue;

                if (doubleClicked) PlayQueueIndex(i);
            }
            if (config.Queue.Count == 0)
            {
                ImGui.TextDisabled("播放列表为空，去\"搜索\"或\"我的歌单\"里添加吧");
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        return changed;
    }

    /// <summary>统一的歌曲行：选择 + 播放 + 我喜欢。</summary>
    private void DrawSongRow(string idScope, Track song, List<Track> source, int index, ref bool changed)
    {
        ImGui.PushID(idScope);
        var doubleClicked = false;   // 必须在 try 之前定值：try 内赋值不满足 C# 定值分析
        try
        {
            var isCurrent = (currentTrack != null && song.Id.Length > 0 && currentTrack.Id == song.Id)
                            || ReferenceEquals(song, currentTrack);
            // 按钮统一右对齐：先量按钮区总宽，Selectable 限宽 + 按钮定位到行尾，
            // VIP 行与非 VIP 行的按钮位置完全一致
            var st = ImGui.GetStyle();
            var wPlay = ImGui.CalcTextSize("播放").X + st.FramePadding.X * 2;
            var wFav = ImGui.CalcTextSize("已喜欢").X + st.FramePadding.X * 2;
            var wVip = song.IsVip ? ImGui.CalcTextSize("VIP").X + st.ItemSpacing.X : 0f;
            var btnTotal = wPlay + wFav + wVip + st.ItemSpacing.X * 2;
            var contentRight = ImGui.GetWindowContentRegionMax().X;
            var rowW = Math.Max(80f, contentRight - ImGui.GetCursorPosX() - btnTotal - st.ItemSpacing.X);
            ImGui.Selectable($"{(isCurrent ? "[播放中] " : "")}{song.Title} - {song.Artist}", isCurrent,
                ImGuiSelectableFlags.None, new Vector2(rowW, 0));

            doubleClicked = ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(0);
            ImGui.SameLine();
            ImGui.SetCursorPosX(contentRight - btnTotal);
            if (song.IsVip)
            {
                ImGui.TextColored(new Vector4(1f, 0.82f, 0.35f, 1f), "VIP");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("VIP 歌曲：未登录或非会员可能无法播放");
                ImGui.SameLine();
            }
            if (ImGui.SmallButton("播放")) doubleClicked = true;
            ImGui.SameLine();
            DrawFavButton(song, ref changed);
        }
        finally
        {
            // ID 栈同样必须成对，抛出异常也要弹回去
            ImGui.PopID();
        }

        if (doubleClicked)
        {
            // 双击：把所在列表整单入队，并从这首开始播
            Log($"双击播放：{song.Title}（列表 {source.Count} 首，index={index}）");
            if (source.Count > 0) SetQueueAndPlay(source, index);
            else EnqueueAndPlay(song);
            changed = true;
        }
    }

    /// <summary>圆角胶囊选择按钮：InvisibleButton 命中 + DrawList 画选中底色与文字，无样式栈依赖。</summary>
    private bool DrawPillButton(string id, string label, bool selected, float width)
    {
        var height = ImGui.GetFrameHeight() * 0.86f;
        ImGui.InvisibleButton(id, new Vector2(width, height));
        var clicked = ImGui.IsItemClicked();
        var hovered = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        if (selected)
        {
            // 选中：主题色底 + 圆角 + 细描边
            dl.AddRectFilled(min, max, 0xFF6FA864, height * 0.5f);
            dl.AddRect(min, max, 0xCC8FD684, height * 0.5f);
        }
        else if (hovered)
        {
            dl.AddRectFilled(min, max, 0x33FFFFFF, height * 0.5f);
        }

        var ts = ImGui.CalcTextSize(label);
        var textCol = selected ? 0xFFF2FFF0 : hovered ? 0xFFF0F0E8 : ImGui.GetColorU32(ImGuiCol.Text);
        dl.AddText(new Vector2((min.X + max.X - ts.X) * 0.5f, (min.Y + max.Y - ts.Y) * 0.5f), textCol, label);
        return clicked;
    }

    /// <summary>「我喜欢」按钮：未收藏 / 已喜欢（不用符号字形，游戏字体渲染不出）。</summary>
    private void DrawFavButton(Track song, ref bool changed)
    {
        var canFav = song.SongId > 0;
        var liked = song.SongId > 0 && likedIds.Contains(song.SongId);

        if (!canFav)
        {
            ImGui.TextDisabled("收藏");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("未取到歌曲数字ID，无法收藏");
            return;
        }

        // 收藏按钮同样不使用样式栈：状态完全由按钮文字表达
        if (ImGui.SmallButton(liked ? "已喜欢" : "收藏"))
        {
            Log($"点击收藏：{song.Title}（id={song.SongId}，like={!liked}）");
            ToggleFavorite(song, !liked);
            changed = true;
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(liked ? "点击取消喜欢" : "加入「我喜欢的音乐」");
    }

    // ------------------------------------------------------------------
    // 正在播放区
    // ------------------------------------------------------------------
    private void DrawNowPlayingSection(ref bool changed)
    {
        if (currentTrack == null)
        {
            ImGui.TextDisabled("未在播放。从上方内容源选择歌曲即可开始。");
            return;
        }

        var cover = GetCurrentCoverTexture();
        if (IsTexValid(cover))
        {
            ImGui.Image(cover!.Handle, new Vector2(64f, 64f));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.TextUnformatted($"正在播放：{currentTrack.Title}");
        ImGui.TextDisabled($"{currentTrack.Artist}  ·  {QQ}");
        ImGui.EndGroup();

        ImGui.SameLine();
        DrawFavButton(currentTrack, ref changed);

        // 进度条（可点击跳转）
        var frac = engineDuration > 0 ? (float)(enginePosition / engineDuration) : 0f;
        frac = Math.Clamp(frac, 0f, 1f);
        ImGui.ProgressBar(frac, new Vector2(-1f, 18f), $"{FmtTime(enginePosition)} / {FmtTime(engineDuration)}");
        if (engineDuration > 0 && ImGui.IsItemClicked())
        {
            var rect = ImGui.GetItemRectMin();
            var size = ImGui.GetItemRectSize();
            var x = ImGui.GetIO().MousePos.X - rect.X;
            EngineSeek(Math.Clamp(x / Math.Max(1f, size.X), 0f, 1f) * engineDuration);
        }

        if (ImGui.Button("上一曲##npPrev", new Vector2(64f, 30f))) PlayPrev();
        ImGui.SameLine();
        if (ImGui.Button(enginePlaying ? "暂停##npToggle" : "播放##npToggle", new Vector2(70f, 30f))) EngineTogglePause();
        ImGui.SameLine();
        if (ImGui.Button("下一曲##npNext", new Vector2(64f, 30f))) PlayNext(auto: false);
        ImGui.SameLine();
        if (resolvingPlay) ImGui.TextDisabled("解析播放地址...");
        var perr = playError;
        if (!string.IsNullOrEmpty(perr))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), perr);
        }

        var vol = config.Volume;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("音量##npVol", ref vol, 0, 100))
        {
            config.Volume = vol;
            ApplyVolume();
            changed = true;
        }

        // 主窗口不再显示歌词列表（歌词只在悬浮窗展示）；歌词索引在每帧 Draw 中更新
    }

    // ------------------------------------------------------------------
    // 每帧：消费后台结果 + 引擎轮询 + 悬浮窗
    // ------------------------------------------------------------------
    private void Draw()
    {
        if (disposed) return;

        // 各阶段独立隔离：任何一步异常都不影响其它步骤，也绝不冒泡到宿主
        try { FlushChats(); }
        catch (Exception e) { try { LogError("FlushChats 异常：" + e); } catch { } }

        try { ConsumePending(); }
        catch (Exception e) { try { LogError("ConsumePending 异常：" + e); } catch { } }

        try { PollPlayback(); }
        catch (Exception e) { try { LogError("PollPlayback 异常：" + e); } catch { } }

        try { EnsureCover(); }
        catch (Exception e) { try { LogError("EnsureCover 异常：" + e); } catch { } }

        // 歌词索引按播放进度推进（悬浮窗状态行使用）
        try { UpdateLyricIndex(); }
        catch (Exception e) { try { LogError("UpdateLyricIndex 异常：" + e); } catch { } }

        if (config.WindowVisible && !overlaySuspended)
        {
            try
            {
                DrawOverlay();
            }
            catch (Exception e)
            {
                // 连续异常熔断：防止"每帧都崩"把宿主拖死
                try { LogError("悬浮窗绘制异常：" + e); } catch { }
                var now = DateTime.UtcNow;
                if ((now - lastDrawErrorTime).TotalSeconds > 2) drawErrorStreak = 0;
                drawErrorStreak++;
                lastDrawErrorTime = now;
                if (drawErrorStreak >= 120)
                {
                    overlaySuspended = true;
                    LogError("悬浮窗连续异常已熔断，自动隐藏（重新开关悬浮窗可恢复）");
                    QueueChat("音乐悬浮窗出现异常已自动隐藏，可在设置里重新打开");
                }
            }
        }
    }

    /// <summary>主线程统一播报（后台线程只入队，绝不直接调游戏 API）。</summary>
    private void FlushChats()
    {
        string[] items;
        lock (chatLock)
        {
            if (pendingChats.Count == 0) return;
            items = pendingChats.ToArray();
            pendingChats.Clear();
        }
        foreach (var item in items)
        {
            try { OmniNotifier.Chat(item); }
            catch (Exception e) { LogError("聊天播报失败：" + e.Message); }
        }
    }

    // ==================================================================
    // 悬浮窗（跟随歌曲变色）
    // ==================================================================
    // ---- 悬浮窗运行时状态 ----
    private Vector2 smoothAnchorPos;       // 头顶锚点的平滑位置（消除角色 idle 呼吸带来的抖动）
    private bool smoothAnchorInit;
    private bool overlaySuspended;         // 连续绘制异常后的自动熔断
    private int drawErrorStreak;
    private DateTime lastDrawErrorTime = DateTime.MinValue;

    // ---- 位置丢失（分辨率变化 / 拖到屏幕外）的兜底支持 ----
    private Vector2 lastOverlayPos;        // 最近一帧悬浮窗的实际位置（设置页「取消固定/回中」的基准）
    private Vector2 lastOverlaySize;
    private bool overlayForcePos;          // 下一帧强制按 config 坐标摆放（重置位置后立即生效）
    private bool overlayRescueNotified;    // 越界自动拉回的聊天提示只发一次

    private const string BuildTag = "v15.0";
    private bool overlayDiagLogged;

    private void DrawOverlay()
    {
        var scale = Math.Clamp(config.OverlayScale, 0.3f, 1.6f);
        var overlayW = 340f * scale;
        var overlayH = 148f * scale;

        var flags = ImGuiWindowFlags.NoTitleBar
                    | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoBackground      // 杜绝宿主皮肤画出方形窗口底（圆角效果的前提）
                    | ImGuiWindowFlags.AlwaysUseWindowPadding;
        // 锁定 = 不可拖动，但窗口仍接收鼠标输入——这样"锁"按钮可以再点一次解锁
        if (config.WindowLocked || config.AnchorToHead) flags |= ImGuiWindowFlags.NoMove;

        var anchored = config.AnchorToHead;
        if (anchored)
        {
            if (!TryGetHeadScreenPos(out var target)) return;

            // 防抖：角色 idle 呼吸只有 1~3px 的漂移。死区之外才做插值跟随，
            // 且最终位置取整像素——亚像素坐标每帧变化就是"小幅抖动"的来源。
            if (!smoothAnchorInit || Vector2.Distance(target, smoothAnchorPos) > 80f)
            {
                smoothAnchorPos = target;
                smoothAnchorInit = true;
            }
            else
            {
                var d = target - smoothAnchorPos;
                if (Math.Abs(d.X) > 3f || Math.Abs(d.Y) > 3f)
                {
                    smoothAnchorPos = Vector2.Lerp(smoothAnchorPos, target, 0.40f);
                }
                // 死区内的微小漂移：完全不动
            }

            var io = ImGui.GetIO();
            var x = Math.Clamp(smoothAnchorPos.X - overlayW / 2f, 4f, Math.Max(4f, io.DisplaySize.X - overlayW - 4f));
            var y = Math.Clamp(smoothAnchorPos.Y - overlayH, 4f, Math.Max(4f, io.DisplaySize.Y - overlayH - 4f));
            ImGui.SetNextWindowPos(new Vector2((float)Math.Round(x), (float)Math.Round(y)));
            ImGui.SetNextWindowSize(new Vector2(overlayW, overlayH));
        }
        else
        {
            // 重置位置后要立即生效：那一帧用 Always，之后回到 FirstUseEver（不干扰手动拖动）
            ImGui.SetNextWindowPos(new Vector2(config.OverlayX, config.OverlayY),
                overlayForcePos ? ImGuiCond.Always : ImGuiCond.FirstUseEver);
            overlayForcePos = false;
            // 必须每帧强制尺寸：用 FirstUseEver 的话窗口尺寸只在首次生效，
            // 调「悬浮窗大小」只会缩放内部元素、窗口本身不跟着变（实测踩到）。
            ImGui.SetNextWindowSize(new Vector2(overlayW, overlayH), ImGuiCond.Always);
        }

        var radius = 24f * scale;

        // 窗口自身背景完全交给自绘
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, radius);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f * scale, 12f * scale));

        // Begin 成功之后才允许 End；Push 在进入前，Pop 放 finally
        var began = false;
        try
        {
            began = ImGui.Begin("###OmniMusicOverlay", flags);
            if (began)
            {
                DrawOverlayBody(scale, radius, anchored);
            }
        }
        catch (Exception e)
        {
            try { LogError("悬浮窗绘制异常：" + e); } catch { }
        }
        finally
        {
            if (began) { try { ImGui.End(); } catch { } }
            try { ImGui.PopStyleVar(3); } catch { }
            try { ImGui.PopStyleColor(2); } catch { }
        }
    }

    private void DrawOverlayBody(float scale, float radius, bool anchored)
    {
        // 视觉全部画在「前景绘制列表」。原因：宿主皮肤会在窗口矩形下垫一层圆角较小的深色底板
        // （实测圆角 ≈18~20px），从我们大圆弧（24*scale ≈ 34px）的四个角露出来——
        // 这就是用户看到"半边圆、半边有角"的元凶。窗口自身的绘制被裁剪在窗口矩形内，盖不住它；
        // 前景列表不受窗口裁剪、且渲染于所有窗口之后，背景板外扩一圈即可把底板彻底盖住。
        // 命中测试（InvisibleButton）仍留在窗口里，位置不变，交互不受影响。
        var dl = ImGui.GetForegroundDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();

        // 一次性输出运行时实际参数，配合截图定位渲染差异
        if (!overlayDiagLogged)
        {
            overlayDiagLogged = true;
            Log($"[构建]{BuildTag} scale={scale:F2}(config {config.OverlayScale:F2}) radius={radius:F1} " +
                $"window={(int)(max.X - min.X)}x{(int)(max.Y - min.Y)} opacity={config.OverlayOpacity:F2}");
        }

        const float expand = 10f;    // 背景板外扩量：足以盖住圆角 ≲30px 的宿主底板
        var vmin = min - new Vector2(expand, expand);
        var vmax = max + new Vector2(expand, expand);
        DrawOverlayBackdrop(dl, vmin, vmax, radius, currentTrack == null);

        // ---- 左缘竖排小圆钮：锚定切换 / 锁定 ----
        // 锁定只禁用「拖动窗口」与「锚定切换」，播放控制/进度条照常可用（用户要求）。
        var utilR = 11f * scale;
        var utilCx = min.X + 8f * scale + utilR;
        var utilCy = min.Y + 10f * scale + utilR;
        var utilStep = utilR * 2 + 6f * scale;
        // 锁定状态下：锚定钮失效变暗，锁钮保持可点——点一下即可解锁
        if (OverlayCircleButton(dl, "##ovAnchorBtn", anchored ? "顶" : "拖",
                new Vector2(utilCx - utilR, utilCy - utilR), new Vector2(utilCx + utilR, utilCy + utilR),
                anchored, anchored ? "取消固定，改为自由拖动" : "固定到角色头顶", !config.WindowLocked))
        {
            if (anchored)
            {
                var pos = ImGui.GetWindowPos();
                config.OverlayX = pos.X;
                config.OverlayY = pos.Y;
            }
            config.AnchorToHead = !config.AnchorToHead;
            Log($"悬浮窗：切换锚定 -> {config.AnchorToHead}");
        }
        var lockCy = utilCy + utilStep;
        if (OverlayCircleButton(dl, "##ovLockBtn", "锁",
                new Vector2(utilCx - utilR, lockCy - utilR), new Vector2(utilCx + utilR, lockCy + utilR),
                config.WindowLocked, config.WindowLocked ? "点击解锁悬浮窗" : "锁定悬浮窗", true))
        {
            config.WindowLocked = !config.WindowLocked;
            Log($"悬浮窗：锁定 -> {config.WindowLocked}");
        }

        // ---- 左缘竖向音量条：锁钮下方，锁定时同样可拖（音量是全局 MCI 设置，与锁定无关） ----
        DrawOverlayVolume(dl, min, max, scale, utilCx, lockCy + utilR);

        var W = max.X - min.X;
        var H = max.Y - min.Y;
        var pad = 14f * scale;
        var coverSize = 104f * scale;
        var coverMin = new Vector2(max.X - pad - coverSize, min.Y + (H - coverSize) * 0.5f);
        var coverMax = coverMin + new Vector2(coverSize, coverSize);
        var utilW = utilR * 2 + 8f * scale;          // 小圆钮列宽，文字让位
        var textLeft = min.X + pad + utilW;
        var textRight = coverMin.X - 10f * scale;

        if (currentTrack == null)
        {
            // 无歌时的封面占位：同样受「封面不透明度」控制，调到 0 就完全隐藏
            var idleCover = GetCurrentCoverTexture();
            if (CoverAlpha() > 0.004f)
            {
                if (IsTexValid(idleCover))
                {
                    DrawRoundedImage(dl, idleCover!.Handle, coverMin, coverMax,
                        Col(new Vector3(1f, 1f, 1f), 0.25f * CoverAlpha()), radius, Vector2.Zero, Vector2.One);
                }
                else
                {
                    FillRounded(dl, coverMin, coverMax, Col(new Vector3(1f, 1f, 1f), 0.06f), radius);
                }
            }

            var idleFont = ImGui.GetFont();
            var idleFs = ImGui.GetFontSize();
            dl.AddText(idleFont, idleFs, new Vector2(textLeft, min.Y + H * 0.5f - 24f * scale),
                Col(new Vector3(1f, 1f, 1f), 0.92f), "暂无播放内容");
            dl.AddText(idleFont, idleFs, new Vector2(textLeft, min.Y + H * 0.5f - 2f * scale),
                Col(new Vector3(1f, 1f, 1f), 0.55f), "在模块设置里搜索并双击播放");
        }
        else
        {
            DrawOverlayContent(dl, min, max, scale, radius, textLeft, textRight, coverMin, coverMax);
        }

        // ---- 位置兜底：分辨率/窗口模式变化后旧坐标可能落在屏幕外（"找不到悬浮窗"）。
        // 无论是否锁定、是否锚定，都把窗口夹回可视区域（锁定只限制用户拖动，不限制自动纠正）。
        lastOverlayPos = min;
        lastOverlaySize = max - min;
        {
            var io0 = ImGui.GetIO();
            var maxX = Math.Max(4f, io0.DisplaySize.X - W - 4f);
            var maxY = Math.Max(4f, io0.DisplaySize.Y - H - 4f);
            var nx = Math.Clamp(min.X, 4f, maxX);
            var ny = Math.Clamp(min.Y, 4f, maxY);
            if (Math.Abs(nx - min.X) > 0.5f || Math.Abs(ny - min.Y) > 0.5f)
            {
                // 不用 SetWindowPos（未在本环境验证过的绑定），改为写入 config + 下一帧强制坐标
                config.OverlayX = nx;
                config.OverlayY = ny;
                overlayForcePos = true;
                Log($"悬浮窗：坐标越界（屏幕 {io0.DisplaySize.X:F0}x{io0.DisplaySize.Y:F0}），已拉回 ({nx:F0},{ny:F0})");
                if (!overlayRescueNotified)
                {
                    overlayRescueNotified = true;
                    QueueChat("悬浮窗曾跑到屏幕外，已自动拉回可视区域");
                }
            }
            else if (!anchored && !config.WindowLocked)
            {
                if (Math.Abs(min.X - config.OverlayX) > 0.5f || Math.Abs(min.Y - config.OverlayY) > 0.5f)
                {
                    config.OverlayX = min.X;
                    config.OverlayY = min.Y;
                }
            }
        }
    }

    /// <summary>悬浮窗主体：全部固定坐标自绘。所有元素用 SetCursorScreenPos 定位，
    /// 字体缩放同帧设置并恢复，从根上避免内容 reflow 导致的抖动。</summary>
    private void DrawOverlayContent(ImDrawListPtr dl, Vector2 min, Vector2 max, float scale, float radius,
        float textLeft, float textRight, Vector2 coverMin, Vector2 coverMax)
    {
        var track = currentTrack!;

        // ---- 封面（右侧，圆角与悬浮窗一致） ----
        // 封面不透明度可独立调节，且允许调到 0（完全透明）——此时封面连占位块一起不画。
        var cover = GetCurrentCoverTexture();
        var coverAlpha = CoverAlpha();
        if (coverAlpha <= 0.004f)
        {
            // 完全透明：跳过封面（含「无封面」占位）
        }
        else if (IsTexValid(cover))
        {
            DrawRoundedImage(dl, cover!.Handle, coverMin, coverMax,
                Col(new Vector3(1f, 1f, 1f), coverAlpha), radius, Vector2.Zero, Vector2.One);
        }
        else
        {
            FillRounded(dl, coverMin, coverMax, Col(new Vector3(1f, 1f, 1f), 0.10f), radius);
            var phTs = ImGui.CalcTextSize("无封面");
            var coverSpan = coverMax - coverMin;
            dl.AddText(coverMin + (coverSpan - phTs) * 0.5f,
                Col(new Vector3(1f, 1f, 1f), 0.35f), "无封面");
        }

        var tw = Math.Max(60f, textRight - textLeft);
        var font = ImGui.GetFont();
        var fsz = ImGui.GetFontSize();

        // ---- 标题（大字白色） ----
        var titleY = min.Y + 11f * scale;
        dl.AddText(font, fsz * 1.34f * scale, new Vector2(textLeft, titleY),
            Col(new Vector3(1f, 1f, 1f), 0.98f),
            Truncate(track.Title, Math.Max(4, (int)(tw / (9.8f * scale)))));

        // ---- 歌手（灰色） ----
        dl.AddText(font, fsz, new Vector2(textLeft, titleY + 30f * scale),
            Col(new Vector3(1f, 1f, 1f), 0.55f),
            Truncate(track.Artist, Math.Max(4, (int)(tw / (7.1f * scale)))));

        // ---- 控制按钮行：上一曲 / 播放暂停 / 下一曲（自绘矢量图标，替代中文字按钮） ----
        var btnY = titleY + 54f * scale;
        var btnH = 28f * scale;
        var btnW = 44f * scale;
        var gap = 10f * scale;
        var bx = textLeft;
        if (OverlayVectorButton(dl, "##ovPrev", OvIcon.Prev,
                new Vector2(bx, btnY), new Vector2(bx + btnW, btnY + btnH), true))
        {
            Log("悬浮窗：上一曲");
            PlayPrev();
        }
        bx += btnW + gap;
        if (OverlayVectorButton(dl, "##ovToggle", enginePlaying ? OvIcon.Pause : OvIcon.Play,
                new Vector2(bx, btnY), new Vector2(bx + btnW, btnY + btnH), true))
        {
            Log("悬浮窗：播放/暂停");
            EngineTogglePause();
        }
        bx += btnW + gap;
        if (OverlayVectorButton(dl, "##ovNext", OvIcon.Next,
                new Vector2(bx, btnY), new Vector2(bx + btnW, btnY + btnH), true))
        {
            Log("悬浮窗：下一曲");
            PlayNext(auto: false);
        }

        // ---- 状态行：错误 > 当前歌词 > 下一曲预告 ----
        var lineY = btnY + btnH + 5f * scale;
        var perr = playError;
        var lyric = config.ShowLyricInHead ? CurrentLyricLine() : string.Empty;
        if (perr.Length > 0)
        {
            dl.AddText(font, fsz, new Vector2(textLeft, lineY),
                Col(new Vector3(1f, 0.45f, 0.4f), 0.95f), Truncate(perr, 26));
        }
        else if (lyric.Length > 0)
        {
            dl.AddText(font, fsz, new Vector2(textLeft, lineY),
                Col(new Vector3(1f, 0.88f, 0.55f), 0.95f), Truncate(lyric, 24));
        }
        else if (config.Queue.Count > 0)
        {
            var nextIdx = (currentQueueIndex + 1) % config.Queue.Count;
            var next = config.Queue[nextIdx];
            dl.AddText(font, fsz, new Vector2(textLeft, lineY),
                Col(new Vector3(1f, 1f, 1f), 0.50f), $"下一曲：{Truncate(next.Title, 18)}");
        }

        // ---- 进度条（细线 + 端点圆点，可点击/拖动跳转） ----
        var barH = Math.Max(3f, 3.5f * scale);
        var barY = max.Y - 13f * scale - barH;
        var barW = textRight - textLeft;
        var frac = engineDuration > 0 ? Math.Clamp((float)(enginePosition / engineDuration), 0f, 1f) : 0f;
        var barMin = new Vector2(textLeft, barY);
        var barMax = new Vector2(textRight, barY + barH);

        // 进度条：锁定状态下同样可点击/拖动跳转（锁定只禁用拖动窗口，不禁用控件）
        ImGui.SetCursorScreenPos(new Vector2(textLeft, barY - 6f * scale));
        ImGui.InvisibleButton("##ovBar", new Vector2(barW, barH + 12f * scale));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"{FmtTime(enginePosition)} / {FmtTime(engineDuration)}");
            if (engineDuration > 0 && ImGui.IsItemActive())
            {
                var mx = ImGui.GetIO().MousePos.X - barMin.X;
                EngineSeek(Math.Clamp(mx / Math.Max(1f, barW), 0f, 1f) * engineDuration);
            }
        }
        FillRounded(dl, barMin, barMax, Col(new Vector3(1f, 1f, 1f), 0.18f), barH);
        FillRounded(dl, barMin, new Vector2(barMin.X + barW * frac, barMax.Y),
            Col(new Vector3(1f, 1f, 1f), 0.92f), barH);
        dl.AddCircleFilled(new Vector2(barMin.X + barW * frac, (barMin.Y + barMax.Y) * 0.5f),
            4.2f * scale, Col(new Vector3(1f, 1f, 1f), 0.98f));
    }

    /// <summary>悬浮窗控制按钮图标种类（自绘矢量，不依赖图标字体）。</summary>
    private enum OvIcon { Prev, Play, Pause, Next }

    /// <summary>悬浮窗圆角胶囊按钮 + 居中矢量图标（三角/竖杠），替代中文字按钮。</summary>
    private bool OverlayVectorButton(ImDrawListPtr dl, string id, OvIcon icon, Vector2 minV, Vector2 maxV,
        bool interactive = true)
    {
        // interactive=false：只画不响应（锁定状态）
        var clicked = false;
        if (interactive)
        {
            ImGui.SetCursorScreenPos(minV);
            clicked = ImGui.InvisibleButton(id, maxV - minV);
        }
        var hovered = interactive && ImGui.IsItemHovered();
        var held = interactive && ImGui.IsItemActive();

        var rounding = (maxV.Y - minV.Y) * 0.42f;
        FillRounded(dl, minV, maxV, Col(new Vector3(1f, 1f, 1f),
            !interactive ? 0.05f : held ? 0.30f : hovered ? 0.18f : 0.10f), rounding);

        var cx = (minV.X + maxV.X) * 0.5f;
        var cy = (minV.Y + maxV.Y) * 0.5f;
        var s = (maxV.Y - minV.Y) * 0.30f;     // 图标半高
        var col = Col(new Vector3(1f, 1f, 1f), interactive ? 0.95f : 0.40f);

        switch (icon)
        {
            case OvIcon.Pause:
            {
                var w = s * 0.34f;
                var g = s * 0.38f;
                FillRounded(dl, new Vector2(cx - g - w, cy - s), new Vector2(cx - g, cy + s), col, w * 0.5f);
                FillRounded(dl, new Vector2(cx + g, cy - s), new Vector2(cx + g + w, cy + s), col, w * 0.5f);
                break;
            }
            case OvIcon.Play:
            {
                var w = s * 0.95f;
                dl.PathLineTo(new Vector2(cx - w * 0.55f, cy - s));
                dl.PathLineTo(new Vector2(cx - w * 0.55f, cy + s));
                dl.PathLineTo(new Vector2(cx + w * 0.75f, cy));
                dl.PathFillConvex(col);
                break;
            }
            case OvIcon.Prev:
            case OvIcon.Next:
            {
                var dir = icon == OvIcon.Next ? 1f : -1f;
                var tw = s * 0.92f;              // 单个三角宽
                var step = tw * 0.72f;           // 双三角错位
                var total = tw + step;
                var x0 = cx - total * 0.5f;
                for (var k = 0; k < 2; k++)
                {
                    var baseX = dir > 0 ? x0 + k * step : x0 + total - k * step;
                    dl.PathLineTo(new Vector2(baseX, cy - s));
                    dl.PathLineTo(new Vector2(baseX, cy + s));
                    dl.PathLineTo(new Vector2(baseX + dir * tw, cy));
                    dl.PathFillConvex(col);
                }
                break;
            }
        }
        return clicked;
    }

    /// <summary>悬浮窗左上角透明小圆钮。</summary>
    private bool OverlayCircleButton(ImDrawListPtr dl, string id, string glyph, Vector2 minV, Vector2 maxV,
        bool active, string tip, bool interactive = true)
    {
        // interactive=false：只画不响应（锁定状态：窗口可接收鼠标，但除"锁"钮外全部失效）
        var clicked = false;
        if (interactive)
        {
            ImGui.SetCursorScreenPos(minV);
            clicked = ImGui.InvisibleButton(id, maxV - minV);
        }
        var hovered = interactive && ImGui.IsItemHovered();
        var c = (minV + maxV) * 0.5f;
        var r = (maxV.X - minV.X) * 0.5f;
        dl.AddCircleFilled(c, r, Col(new Vector3(1f, 1f, 1f),
            !interactive ? 0.06f : hovered ? 0.24f : 0.10f));
        if (active)
        {
            dl.AddCircle(c, r, Col(new Vector3(1f, 0.85f, 0.4f), interactive ? 0.60f : 0.35f), 0, 1.4f);
        }
        var ts = ImGui.CalcTextSize(glyph);
        dl.AddText(c - ts * 0.5f, Col(new Vector3(1f, 1f, 1f), interactive ? 0.90f : 0.40f), glyph);
        if (hovered && tip.Length > 0) ImGui.SetTooltip(tip);
        return clicked;
    }

    /// <summary>悬浮窗左缘竖向音量条：位于「顶/锁」小圆钮正下方，从钮下沿一直伸到进度条上方。
    /// 音量是全局 MCI 设置（setaudio volume），与锁定无关——锁定时同样可点可拖（与进度条同策略）。
    /// 命中区用 InvisibleButton（在窗口内），视觉画在前景列表（与悬浮窗其它元素一致）。</summary>
    private void DrawOverlayVolume(ImDrawListPtr dl, Vector2 min, Vector2 max, float scale, float cx, float topY)
    {
        var top = topY + 6f * scale;
        var bottom = max.Y - 20f * scale;                 // 底部让出进度条区域
        var trackH = bottom - top;
        if (trackH < 24f * scale) return;                 // 窗口太矮：放不下就不画

        var trackW = Math.Max(2.5f, 3.5f * scale);                // 绝对下限：悬浮窗缩到很小也看得见
        var hitW = Math.Max(12f, Math.Max(14f * scale, trackW * 3f));   // 命中区比视觉宽得多，好点好拖
        var knobR = Math.Max(2.5f, 4.5f * scale);

        var hitMin = new Vector2(cx - hitW * 0.5f, top - 4f * scale);
        var hitMax = new Vector2(cx + hitW * 0.5f, bottom + 4f * scale);
        ImGui.SetCursorScreenPos(hitMin);
        ImGui.InvisibleButton("##ovVol", hitMax - hitMin);
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();

        if (held)
        {
            // 竖向：顶部=100，底部=0
            var my = ImGui.GetIO().MousePos.Y;
            var frac = Math.Clamp((bottom - my) / trackH, 0f, 1f);
            var v = (int)Math.Round(frac * 100f);
            if (v != config.Volume)
            {
                config.Volume = v;
                ApplyVolume();
            }
        }
        if (hovered) ImGui.SetTooltip($"音量 {config.Volume}");

        var fracNow = Math.Clamp(config.Volume / 100f, 0f, 1f);
        var fillTop = bottom - trackH * fracNow;
        // 凹槽
        FillRounded(dl, new Vector2(cx - trackW * 0.5f, top), new Vector2(cx + trackW * 0.5f, bottom),
            Col(new Vector3(1f, 1f, 1f), 0.16f), trackW);
        // 已填充部分（从底部往上）
        if (fracNow > 0.003f)
            FillRounded(dl, new Vector2(cx - trackW * 0.5f, fillTop), new Vector2(cx + trackW * 0.5f, bottom),
                Col(new Vector3(1f, 1f, 1f), held ? 0.95f : hovered ? 0.85f : 0.70f), trackW);
        // 旋钮圆点（AddCircleFilled 在本环境渲染可靠）
        dl.AddCircleFilled(new Vector2(cx, fillTop), knobR,
            Col(new Vector3(1f, 1f, 1f), hovered || held ? 0.98f : 0.85f));
    }

    /// <summary>角色头顶屏幕坐标。任何一步不成立都返回 false，绝不冒险调用 native。</summary>
    private bool TryGetHeadScreenPos(out Vector2 pos)
    {
        pos = default;
        try
        {
            var player = DService.Instance().ObjectTable.LocalPlayer;
            if (player == null) return false;

            var p = player.Position;
            if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z)) return false;
            if (float.IsInfinity(p.X) || float.IsInfinity(p.Y) || float.IsInfinity(p.Z)) return false;

            var headPos = p + new Vector3(0f, config.HeadOffsetY, 0f);
            if (!DService.Instance().GameGUI.WorldToScreen(headPos, out var sp)) return false;
            if (float.IsNaN(sp.X) || float.IsNaN(sp.Y)) return false;
            if (float.IsInfinity(sp.X) || float.IsInfinity(sp.Y)) return false;

            pos = sp;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>悬浮窗背景。
    /// 有歌：封面的 90x90 小图拉伸铺满（放大即模糊，主流播放器做法）+ 渐变暗化层保证文字可读。
    /// 无歌：内嵌渐变纹理着色的半透明玻璃底（游戏画面透出，透明度自动比播放时更低）。
    /// 圆角全部由 DrawRoundedImage 的逐列裁切实现（几何自算，不依赖 ImGui 的 rounding 参数——
    /// 本宿主环境下 AddRectFilled / AddImageRounded 的 rounding 都会被忽略，实测呈直角）。</summary>
    private void DrawOverlayBackdrop(ImDrawListPtr dl, Vector2 min, Vector2 max, float radius, bool idle)
    {
        var opacity = Math.Clamp(config.OverlayOpacity, 0f, 1f);
        // 完全透明：整块背景（含细边高光线）都不画
        if (opacity <= 0.001f) return;
        var blur = GetCurrentCoverBlurTexture();
        EnsureGlassTexture();
        var grad = glassTexture;

        if (IsTexValid(blur))
        {
            // 只取中心一块再铺满整卡 = 二次放大，明显更糊（QQ 小图本身只有 90x90）
            DrawRoundedImage(dl, blur!.Handle, min, max,
                Col(new Vector3(1f, 1f, 1f), opacity * 0.95f), radius,
                new Vector2(0.30f, 0.30f), new Vector2(0.70f, 0.70f));
            // 再叠一层"更放大"的同图，弱化细节、柔化观感
            DrawRoundedImage(dl, blur!.Handle, min, max,
                Col(new Vector3(1f, 1f, 1f), opacity * 0.40f), radius,
                new Vector2(0.43f, 0.43f), new Vector2(0.57f, 0.57f));
            // 渐变暗化层（上浅下深），保证白字可读
            if (IsTexValid(grad))
                DrawRoundedImage(dl, grad!.Handle, min, max,
                    Col(new Vector3(0.02f, 0.02f, 0.05f), 0.42f * opacity), radius,
                    Vector2.Zero, Vector2.One);
        }
        else if (IsTexValid(grad))
        {
            var (cTop, cBottom) = CurrentSongPalette();
            var mid = Lerp(cTop, cBottom, 0.35f);
            // 玻璃底：随歌变色的半透明垂直渐变；无歌时再降一档透明度，更通透
            var a = idle ? opacity * 0.55f : opacity;
            DrawRoundedImage(dl, grad!.Handle, min, max, Col(mid, a), radius, Vector2.Zero, Vector2.One);
            // 底部加深：翻转 uv 复用同一张渐变纹理，形成上亮下暗的层次
            DrawRoundedImage(dl, grad!.Handle, min, max, Col(cBottom, a * 0.55f), radius,
                new Vector2(0f, 1f), new Vector2(1f, 0f));
        }
        else
        {
            // 纹理尚未就绪的一帧兜底（直角，仅持续到贴图加载完成）
            var (cTop, cBottom) = CurrentSongPalette();
            var mid = Lerp(cTop, cBottom, 0.35f);
            dl.AddRectFilled(min, max, Col(mid, idle ? opacity * 0.55f : opacity));
        }

        var edge = Col(new Vector3(1f, 1f, 1f), 0.10f);
        dl.AddLine(new Vector2(min.X + radius, min.Y + 0.5f), new Vector2(max.X - radius, min.Y + 0.5f), edge);
        dl.AddLine(new Vector2(min.X + radius, max.Y - 0.5f), new Vector2(max.X - radius, max.Y - 0.5f), edge);
        dl.AddLine(new Vector2(min.X + 0.5f, min.Y + radius), new Vector2(min.X + 0.5f, max.Y - radius), edge);
        dl.AddLine(new Vector2(max.X - 0.5f, min.Y + radius), new Vector2(max.X - 0.5f, max.Y - radius), edge);
    }

    private string CurrentLyricLine()
    {
        if (lyricCurrentIndex < 0 || lyricCurrentIndex >= currentLyrics.Count) return string.Empty;
        return currentLyrics[lyricCurrentIndex].Text.Trim();
    }

    /// <summary>歌词元信息行（作词/作曲/编曲等），始终从歌词列表中过滤。</summary>
    private static bool IsLyricMetaLine(string t)
    {
        if (string.IsNullOrEmpty(t) || t.Length > 48) return false;
        return t.Contains("作词") || t.Contains("作曲") || t.Contains("编曲") || t.Contains("填词")
               || t.Contains("词：") || t.Contains("曲：") || t.Contains("词:") || t.Contains("曲:")
               || t.Contains("Lyricist", StringComparison.OrdinalIgnoreCase)
               || t.Contains("Composer", StringComparison.OrdinalIgnoreCase)
               || t.Contains("Arranger", StringComparison.OrdinalIgnoreCase)
               || t.Contains("作詞") || t.Contains("作曲");
    }

    // ------------------------------------------------------------------
    // 颜色工具
    // ------------------------------------------------------------------
    private static uint Col(Vector3 c, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, Math.Clamp(a, 0f, 1f)));

    // 注意：ImDrawList 的 AddRectFilled / AddImageRounded / AddText 等颜色参数都是 uint（用上面的 Col）。
    // 若需要 Vector4（如 ImGui.TextColored），直接 new Vector4(...)，不要再引入 VCol 这类同形异义的重载——
    // 曾因 VCol 返回 Vector4 误传给 AddRectFilled 触发 CS1503。

    /// <summary>封面绘制用的不透明度（独立于背景，可调至 0 = 完全透明）。</summary>
    private float CoverAlpha() => Math.Clamp(config.CoverOpacity, 0f, 1f);

    /// <summary>
    /// 圆角矩形的「逐列裁切」渲染：圆角轮廓由我们自己按几何公式逐列计算，
    /// 每列用一条贴图条带（上下按圆角内缩）拼出形状，并做逐像素覆盖率的边缘羽化（手工抗锯齿）。
    /// 为什么不用 ImGui 的 rounding 参数：本宿主环境下 AA 被关、rounding 也不可靠，
    /// 画出来要么直角、要么 3 段折线的粗台阶；条带几何完全由我们控制，必定生效。
    /// 相邻条带首尾相接不重叠，不会叠加出深色接缝。
    /// </summary>
    private void DrawRoundedImage(ImDrawListPtr dl, ImTextureID tex, Vector2 min, Vector2 max,
        uint col, float radius, Vector2 uvMin, Vector2 uvMax)
    {
        var w = max.X - min.X;
        var h = max.Y - min.Y;
        if (w <= 1f || h <= 1.5f) return;

        var r = Math.Clamp(radius, 0f, Math.Min(w, h) * 0.5f);
        var invW = 1f / w;
        var invH = 1f / h;
        var du = uvMax.X - uvMin.X;
        var dv = uvMax.Y - uvMin.Y;

        // 手工拆 RGBA（ImU32 是 ABGR 位序：R 在低 8 位），便于给边缘半覆盖行缩放 alpha
        var cr = col & 0xFF;
        var cg = (col >> 8) & 0xFF;
        var cb = (col >> 16) & 0xFF;
        var ca = (col >> 24) & 0xFF;

        void Emit(float x0, float y0, float x1, float y1, float alphaMul)
        {
            if (x1 - x0 < 0.02f || y1 - y0 < 0.02f) return;
            var m = Math.Clamp(ca / 255f * alphaMul, 0f, 1f);
            if (m <= 0.004f) return;
            var c = ((uint)Math.Round(m * 255f) << 24) | (cb << 16) | (cg << 8) | cr;
            dl.AddImage(tex, new Vector2(x0, y0), new Vector2(x1, y1),
                new Vector2(uvMin.X + (x0 - min.X) * invW * du, uvMin.Y + (y0 - min.Y) * invH * dv),
                new Vector2(uvMin.X + (x1 - min.X) * invW * du, uvMin.Y + (y1 - min.Y) * invH * dv),
                c);
        }

        var x = min.X;
        var guard = 0;
        while (x < max.X - 0.01f && guard++ < 8192)
        {
            var dl0 = x - min.X;        // 列左端距左边缘
            var dr0 = max.X - x;        // 列左端距右边缘
            if (dl0 >= r && dr0 > r)
            {
                // 直边区：一段画到右圆角区起点，省顶点
                Emit(x, min.Y, max.X - r, max.Y, 1f);
                x = Math.Max(max.X - r, x + 0.01f);
                continue;
            }

            var x1 = Math.Min(x + 1f, max.X);   // 圆角区：1px 一列
            var inset = Math.Max(CornerInset(dl0, r), CornerInset(max.X - x1, r));
            if (inset <= 0.001f)
            {
                Emit(x, min.Y, x1, max.Y, 1f);
            }
            else
            {
                var topF = min.Y + inset;       // 浮点上下边缘
                var botF = max.Y - inset;
                var nT = MathF.Floor(topF);
                var nB = MathF.Floor(botF);
                if (nB - nT < 1f)
                {
                    // 形状整体落在一个像素行内：按净覆盖率出一条
                    Emit(x, nT, x1, nT + 1f, botF - topF);
                }
                else
                {
                    Emit(x, nT, x1, nT + 1f, 1f - (topF - nT));  // 顶部半覆盖行（羽化）
                    Emit(x, nT + 1f, x1, nB, 1f);                // 实体
                    Emit(x, nB, x1, nB + 1f, botF - nB);         // 底部半覆盖行（羽化）
                }
            }
            x = x1;
        }
    }

    /// <summary>距边缘 d 处，圆角（半径 r）对应的上下内缩量；d >= r 时为 0（直边）。</summary>
    private static float CornerInset(float d, float r)
    {
        if (r <= 0f || d >= r) return 0f;
        var t = r - Math.Max(0f, d);
        return r - MathF.Sqrt(Math.Max(0f, r * r - t * t));
    }

    /// <summary>悬浮窗专用圆角填充（走逐列裁切的贴图路径，见 DrawRoundedImage）。
    /// 纹理未就绪时退回普通矩形（仅启动头几帧）。</summary>
    private void FillRounded(ImDrawListPtr dl, Vector2 min, Vector2 max, uint col, float rounding)
    {
        var grad = glassTexture;
        if (IsTexValid(grad))
            DrawRoundedImage(dl, grad!.Handle, min, max, col, rounding, Vector2.Zero, Vector2.One);
        else
            dl.AddRectFilled(min, max, col);
    }

    private (Vector3 Top, Vector3 Bottom) CurrentSongPalette()
    {
        if (!config.DynamicColor || currentTrack == null)
        {
            return (new Vector3(0.12f, 0.14f, 0.20f), new Vector3(0.05f, 0.06f, 0.10f));
        }

        var key = currentTrack.MediaMid.Length > 0 ? currentTrack.MediaMid : currentTrack.Id;
        var h = (uint)StableHash(key) / (float)int.MaxValue;   // 0..1
        var top = HslToRgb(h, 0.42f, 0.15f);
        var bottom = HslToRgb((h + 0.07f) % 1f, 0.50f, 0.055f);
        return (top, bottom);
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in s) h = h * 31 + c;
            return h & 0x7FFFFFFF;
        }
    }

    private static Vector3 HslToRgb(float h, float s, float l)
    {
        h -= MathF.Floor(h);
        var c = (1f - MathF.Abs(2f * l - 1f)) * s;
        var x = c * (1f - MathF.Abs(h * 6f % 2f - 1f));
        var m = l - c / 2f;
        float r, g, b;
        switch ((int)(h * 6f))
        {
            case 0: r = c; g = x; b = 0f; break;
            case 1: r = x; g = c; b = 0f; break;
            case 2: r = 0f; g = c; b = x; break;
            case 3: r = 0f; g = x; b = c; break;
            case 4: r = x; g = 0f; b = c; break;
            default: r = c; g = 0f; b = x; break;
        }
        return new Vector3(r + m, g + m, b + m);
    }

    // 说明：曾用 PathFillConvex 色带自绘「圆角渐变矩形」（FillRoundedVerticalGradient/FillRoundedSolid），
    // PIL 离线复刻渲染正常，但游戏内实测呈现为斜切角（成因不明），已全部改为原生圆角图元并删除。

    private static Vector4 Lerp(Vector4 a, Vector4 b, float t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);

    private static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    private static string Truncate(string s, int max)
    {
        s = (s ?? string.Empty).Trim();
        if (max < 4) max = 4;
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    private static string FmtTime(double t)
    {
        if (double.IsNaN(t) || t < 0) t = 0;
        var s = (int)t;
        return $"{s / 60:00}:{s % 60:00}";
    }

    // ==================================================================
    // 播放引擎（纯 MCI / winmm）
    // 说明：不再使用 WMPlayer.OCX COM 组件。在游戏进程内创建 WMP ActiveX 控件
    //       会加载 wmp.dll / DirectShow 滤镜链并创建隐藏窗口，且 PollPlayback
    //       每帧反射读取 currentMedia/currentPosition（加载中会阻塞），
    //       这是导致游戏崩溃与重载崩溃的主因。改为「下载到临时文件 + MCI 播放」。
    // ==================================================================
    private bool mciActive;
    private volatile string? pendingMciFile;
    private DateTime lastPollTime = DateTime.MinValue;

    // ---- 播放状态（仅主线程读写，PollPlayback 每帧刷新）----
    private volatile bool enginePlaying;   // 是否正在播放
    private double enginePosition;         // 当前进度（秒）
    private double engineDuration;         // 总时长（秒）

    /// <summary>清理 30 分钟前的缓存音频，避免临时目录无限膨胀。</summary>
    private static void CleanupOldTracks(string dir)
    {
        try
        {
            var cutoff = DateTime.Now.AddMinutes(-30);
            foreach (var f in Directory.GetFiles(dir, "track_*"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    private void EnginePlay(string url)
    {
        playError = string.Empty;
        MciClose();
        enginePlaying = false;
        enginePosition = 0;
        engineDuration = 0;

        if (string.IsNullOrEmpty(url)) return;

        var ext = url.Contains(".mp3", StringComparison.OrdinalIgnoreCase) ? ".mp3" : ".m4a";
        _ = Task.Run(async () =>
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "OmniMusic");
                Directory.CreateDirectory(dir);
                CleanupOldTracks(dir);
                var path = Path.Combine(dir, $"track_{Guid.NewGuid():N}{ext}");
                using var dlReq = new HttpRequestMessage(HttpMethod.Get, url);
                dlReq.Headers.Referrer = new Uri("https://y.qq.com/portal/player.html");
                dlReq.Headers.TryAddWithoutValidation("User-Agent", UA);
                using var dlResp = await Http.SendAsync(dlReq);
                dlResp.EnsureSuccessStatusCode();
                var bytes = await dlResp.Content.ReadAsByteArrayAsync();
                if (disposed) return;
                await File.WriteAllBytesAsync(path, bytes);
                if (disposed)
                {
                    try { File.Delete(path); } catch { }
                    return;
                }
                pendingMciFile = path;
            }
            catch (Exception e)
            {
                playError = "音频下载失败: " + e.Message;
            }
        });
    }

    private void EngineTogglePause()
    {
        if (!mciActive) return;
        Mci(enginePlaying ? $"pause {MciAlias}" : $"resume {MciAlias}");
        enginePlaying = !enginePlaying;
    }

    private void EngineSeek(double seconds)
    {
        if (!mciActive || engineDuration <= 0) return;
        seconds = Math.Max(0, seconds);
        Mci($"seek {MciAlias} to {(int)(seconds * 1000)}");
        Mci($"play {MciAlias}");
        enginePosition = seconds;
    }

    private void ApplyVolume()
    {
        if (!mciActive) return;
        var v = Math.Clamp(config.Volume, 0, 100);
        Mci($"setaudio {MciAlias} volume to {v * 10}");
    }

    private void EngineStop()
    {
        MciClose();
        enginePlaying = false;
        enginePosition = 0;
        engineDuration = 0;
    }

    private void PollPlayback()
    {
        // MCI 状态查询开销较大，限流到 5Hz（禁止每帧调用）
        var now = DateTime.UtcNow;
        if ((now - lastPollTime).TotalMilliseconds < 200) return;
        lastPollTime = now;

        if (mciActive)
        {
            var posStr = Mci($"status {MciAlias} position");
            var lenStr = Mci($"status {MciAlias} length");
            var mode = Mci($"status {MciAlias} mode");
            var len = int.TryParse(lenStr, out var l) ? l : 0;
            var pos = int.TryParse(posStr, out var p) ? p : 0;

            engineDuration = len / 1000.0;
            enginePosition = pos / 1000.0;
            enginePlaying = mode == "playing";

            if (len > 0 && pos >= len - 400 && mode != "paused" &&
                (now - lastAutoNextTime).TotalMilliseconds > 1500)
            {
                lastAutoNextTime = now;
                enginePlaying = false;
                MciClose();
                PlayNext(auto: true);
            }
        }

        var mciFile = pendingMciFile;
        if (mciFile != null)
        {
            pendingMciFile = null;
            if (MciOpen(mciFile))
            {
                mciActive = true;
                ApplyVolume();
            }
            else
            {
                playError = "本地音频播放失败（MCI 无法打开该文件）";
            }
        }
    }

    private static int ToInt(object? o)
    {
        try { return o == null ? 0 : Convert.ToInt32(o, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static double ToDouble(object? o)
    {
        try { return o == null ? 0 : Convert.ToDouble(o, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    // ---------------- MCI ----------------
    private const string MciAlias = "omniMusicPlayer";

    [DllImport("winmm.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int mciSendString(string command, StringBuilder? ret, int retLen, IntPtr hwndCallback);

    private static string Mci(string command)
    {
        var sb = new StringBuilder(256);
        mciSendString(command, sb, 256, IntPtr.Zero);
        return sb.ToString().Trim();
    }

    private bool MciOpen(string file)
    {
        Mci($"close {MciAlias}");
        var err = mciSendString($"open \"{file}\" type mpegvideo alias {MciAlias}", null, 0, IntPtr.Zero);
        if (err != 0)
        {
            // 部分系统上 mpegvideo 不认该容器，退回让 MCI 自行挑选设备
            err = mciSendString($"open \"{file}\" alias {MciAlias}", null, 0, IntPtr.Zero);
        }
        if (err != 0)
        {
            Log($"MCI 打开失败，错误码 {err}：{file}");
            return false;
        }
        err = mciSendString($"play {MciAlias}", null, 0, IntPtr.Zero);
        return err == 0;
    }

    private void MciClose()
    {
        mciSendString($"close {MciAlias}", null, 0, IntPtr.Zero);
        mciActive = false;
    }

    // ------------------------------------------------------------------
    // 播放队列
    // ------------------------------------------------------------------
    private void PlayTrack(Track t)
    {
        currentTrack = t;
        currentQueueIndex = config.Queue.IndexOf(t);
        playError = string.Empty;

        TriggerLyric(t);
        TriggerCover(t);

        resolvingPlay = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var (url, isMp3) = await ResolvePlayUrl(t);
                if (url != null && !isMp3 &&
                    string.IsNullOrEmpty(config.QQMusicId))
                {
                    // 未登录只能拿 C400 m4a（MCI 不支持 AAC），下载也放不出声，
                    // 直接提示登录，避免出现"MCI 无法打开该文件"的迷惑报错
                    url = null;
                    playError = "未登录只能获取 m4a 音源（本机播放器不支持），请扫码登录后播放";
                }
                pendingPlayUrl = url;
                if (url == null && playError.Length == 0)
                {
                    // 实测（2026-09）：
                    // - 未登录：VIP 曲（pay.play=1）拿不到 vkey → 提示扫码登录
                    // - 已登录+绿钻：普通 VIP 曲实测全部可播（含情人·蔡徐坤——
                    //   之前误报数字专辑，实为 media_mid 拼错，已修复）
                    var loggedIn = !string.IsNullOrEmpty(config.QQMusicId);
                    playError = !loggedIn
                        ? "VIP 歌曲：请先扫码登录（绿钻账号可播放）"
                        : "获取播放地址失败：该歌曲版权受限或接口异常（详情见日志）";
                }
            }
            catch (Exception e)
            {
                pendingPlayUrl = null;
                playError = "解析播放地址失败: " + e.Message;
            }
            finally
            {
                resolvingPlay = false;
            }
        });
    }

    private void PlayQueueIndex(int i)
    {
        if (i < 0 || i >= config.Queue.Count) return;
        PlayTrack(config.Queue[i]);
    }

    private void PlayNext(bool auto)
    {
        if (config.Queue.Count == 0)
        {
            if (auto) SendMediaKey(0xB0);
            return;
        }
        PlayQueueIndex((currentQueueIndex + 1) % config.Queue.Count);
    }

    private void PlayPrev()
    {
        if (config.Queue.Count == 0)
        {
            SendMediaKey(0xB1);
            return;
        }
        PlayQueueIndex((currentQueueIndex - 1 + config.Queue.Count) % config.Queue.Count);
    }

    private void EnqueueAndPlay(Track t)
    {
        if (!config.Queue.Contains(t)) config.Queue.Add(t);
        PlayTrack(t);
    }

    private void SetQueueAndPlay(List<Track> tracks, int startIndex)
    {
        if (tracks.Count == 0) return;
        Log($"入队播放：{tracks.Count} 首，起始 {startIndex}");
        config.Queue = new List<Track>(tracks);
        PlayQueueIndex(Math.Clamp(startIndex, 0, tracks.Count - 1));
    }

    // ==================================================================
    // 搜索（QQ音乐）
    // ==================================================================
    private void TriggerSearch(string keyword)
    {
        keyword = (keyword ?? string.Empty).Trim();
        if (keyword.Length == 0)
        {
            searchError = "请先输入歌名或歌手";
            searchDebug = "输入框为空，已忽略本次点击";
            return;
        }

        searching = true;
        searchError = string.Empty;
        searchResults.Clear();
        searchDebug = $"正在搜索「{keyword}」…";

        Log($"开始搜索：{keyword}");
        _ = Task.Run(async () =>
        {
            try
            {
                var results = await SearchQQ(keyword);
                Log($"搜索完成：{keyword} -> {results.Count} 首");
                pendingSearchResults = results;
            }
            catch (Exception e)
            {
                LogError($"搜索异常：{keyword} -> {e}");
                searchError = $"搜索失败: {e.Message}";
                searchDebug = "请求异常：" + e.Message;
            }
            finally
            {
                searching = false;
            }
        });
    }

    private async Task<(int Status, string Body)> HttpGetText(string url, string referer)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(referer);
        req.Headers.TryAddWithoutValidation("User-Agent", UA);
        var resp = await AuthHttp.SendAsync(req);
        // 老版 fcg 接口（getmyfav 等）返回 Content-Type: text/html;charset=gb2312，
        // ReadAsStringAsync 遇到 .NET 未注册的字符集会直接抛
        // "The character set provided in ContentType is invalid" —— 实际 body 是 UTF-8/ASCII JSON。
        // 因此忽略响应头字符集，按字节读取后统一 UTF-8 解码。
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return ((int)resp.StatusCode, Encoding.UTF8.GetString(bytes));
    }

    /// <summary>
    /// 加载 QQ音乐榜单。实测：c.y.qq.com 的 fcg_v8_toplist_cp 接口未登录即可返回歌曲列表，
    /// 因此「推荐」不依赖登录，未登录也能用。
    /// </summary>
    private void TriggerLoadRecommend(int topId, string topName)
    {
        if (loadingRecommend) return;
        loadingRecommend = true;
        recommendError = string.Empty;
        recommendTopName = topName;

        _ = Task.Run(async () =>
        {
            try
            {
                var url = "https://c.y.qq.com/v8/fcg-bin/fcg_v8_toplist_cp.fcg" +
                          $"?topid={topId}&page=detail&type=top&song_begin=0&song_num=60" +
                          "&format=json&inCharset=utf-8&outCharset=utf-8&platform=yqq&needNewCode=0";
                var (status, body) = await HttpGetText(url, "https://y.qq.com/");
                Log($"推荐榜单[{topName}]：HTTP {status}，{body.Length} 字符");

                var tracks = ParseToplist(body);
                if (tracks.Count == 0) recommendError = "没有取到榜单歌曲，可稍后重试";
                pendingRecommendTracks = tracks;
            }
            catch (Exception e)
            {
                LogError("推荐榜单加载失败：" + e);
                recommendError = "加载失败: " + e.Message;
            }
            finally
            {
                loadingRecommend = false;
            }
        });
    }

    /// <summary>解析榜单响应：songlist[] 每项是 { ..., data: {songmid, songname, singer, albummid} }。</summary>
    private static List<Track> ParseToplist(string body)
    {
        var tracks = new List<Track>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("songlist", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return tracks;
            }
            foreach (var item in arr.EnumerateArray())
            {
                var payload = item;
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    payload = d;
                }
                var t = QQSongFromJson(payload);
                if (t != null) tracks.Add(t);
            }
        }
        catch (JsonException)
        {
        }
        catch (Exception e)
        {
            LogError("榜单解析异常：" + e.Message);
        }
        return tracks;
    }

    private async Task<List<Track>> SearchQQ(string keyword)
    {
        var results = new List<Track>();
        var report = new StringBuilder();
        var urls = new[]
        {
            $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?p=1&n=30&w={Uri.EscapeDataString(keyword)}&format=json",
            $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=30&w={Uri.EscapeDataString(keyword)}&g_tk=5381&loginUin=0&hostUin=0&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0"
        };

        for (var i = 0; i < urls.Length; i++)
        {
            var (status, body) = await HttpGetText(urls[i], "https://y.qq.com/portal/player.html");
            report.Append($"[接口{i + 1} HTTP {status} / {body.Length}字] ");
            if (TryParseQQSearch(body, results) && results.Count > 0)
            {
                searchDebug = $"{report.ToString().TrimEnd()} 解析到 {results.Count} 首";
                return results;
            }
            report.Append("无结果：" + Snip(body, 120) + " ");
        }

        searchDebug = "未取到结果 — " + report.ToString().TrimEnd();
        Log("搜索未取到结果：" + report);
        return results;
    }

    private static bool TryParseQQSearch(string body, List<Track> results)
    {
        results.Clear();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return false;
            if (!data.TryGetProperty("song", out var song) || !song.TryGetProperty("list", out var list)) return false;
            if (list.ValueKind != JsonValueKind.Array) return false;

            foreach (var item in list.EnumerateArray())
            {
                var t = QQSongFromJson(item);
                if (t != null) results.Add(t);
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>QQ音乐歌曲 JSON 的统一容错解析（搜索 / 歌单 / 我喜欢 通用）。</summary>
    private static Track? QQSongFromJson(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) return null;
        try
        {
            var mid = FirstString(s, "mid", "songmid", "songMid", "song_mid");
            if (mid.Length == 0) return null;

            var name = FirstString(s, "name", "songname", "title", "song_name");
            if (name.Length == 0) name = "未知";

            var artist = "未知";
            if (s.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array &&
                singers.GetArrayLength() > 0)
            {
                var parts = new List<string>();
                foreach (var sg in singers.EnumerateArray())
                {
                    var n = FirstString(sg, "name");
                    if (n.Length > 0) parts.Add(n);
                    if (parts.Count >= 3) break;
                }
                if (parts.Count > 0) artist = string.Join("/", parts);
            }

            // media_mid 是拼 vkey 文件名的关键（M500{media_mid}.mp3），不能拿歌曲 mid 凑数！
            // 实测：我不难过/情人等歌 mid≠media_mid，用错会拿到 404 的假试听票据。
            // 榜单接口（fcg_v8_toplist_cp）没有 file 对象，media_mid 在顶层 strMediaMid 字段。
            var mediaMid = FirstString(s, "strMediaMid", "media_mid", "mediaMid");
            if (mediaMid.Length == 0 && s.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
            {
                mediaMid = FirstString(file, "media_mid", "mediaMid");
            }
            if (mediaMid.Length == 0) mediaMid = mid; // 兜底，ResolvePlayUrl 会在播放前校正

            var albumMid = string.Empty;
            if (s.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
            {
                albumMid = FirstString(album, "mid", "pmid");
                if (albumMid.Length == 0) albumMid = FirstString(album, "albumMid");
            }
            if (albumMid.Length == 0) albumMid = FirstString(s, "albummid", "albumMid");

            var songId = FirstLong(s, "id", "songId", "song_id", "songid");

            // VIP 判定：pay.payplay == 1（搜索/榜单/歌单响应均带 pay 字段，实测）
            var isVip = false;
            if (s.TryGetProperty("pay", out var payEl) && payEl.ValueKind == JsonValueKind.Object)
            {
                if (payEl.TryGetProperty("payplay", out var pp) && pp.ValueKind == JsonValueKind.Number)
                {
                    isVip = pp.GetInt32() == 1;
                }
            }

            var cover = albumMid.Length > 0
                ? $"https://y.gtimg.cn/music/photo_new/T002R500x500M000{albumMid}.jpg"
                : string.Empty;

            return new Track
            {
                Title = name,
                Artist = artist,
                Id = mid,
                MediaMid = mediaMid,
                SongId = songId,
                IsVip = isVip,
                CoverUrl = cover
            };
        }
        catch
        {
            return null;
        }
    }

    private static string FirstString(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return string.Empty;
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return string.Empty;
    }

    private static long FirstLong(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var p)) return p;
        }
        return 0;
    }

    // ==================================================================
    // 播放地址解析（QQ音乐）
    // ==================================================================
    /// <summary>
    /// 解析播放地址。返回 (URL, 是否 mp3)。
    /// mp3 与 m4a 的区分很重要：MCI（winmm）原生只认 mp3，不支持 AAC/m4a
    /// （实测 m4a 下载成功但 MCI 错误码 277 打不开）。未登录时 VIP 限制下只能拿
    /// C400 m4a（M500 返回 104003），此时应提示登录而不是让 MCI 报错。
    /// </summary>
    private async Task<(string? Url, bool IsMp3)> ResolvePlayUrl(Track t)
    {
        // media_mid 校正：个别老格式数据源可能没带 strMediaMid/file.media_mid，
        // 此时 MediaMid 回退成了歌曲 mid，拼出的文件名会拿到 404 假票据（实测）。
        // 播放前用单曲详情接口拿真正的 media_mid。
        if (t.MediaMid.Length == 0 || t.MediaMid == t.Id)
        {
            await EnsureMediaMid(t);
        }

        // M500 (128k mp3) 优先；C400 (128k m4a) 兜底（未登录或个别无 mp3 的歌）
        var purl = await GetQQPurl(t, preferMp3: true);
        var isMp3 = true;
        if (string.IsNullOrEmpty(purl))
        {
            purl = await GetQQPurl(t, preferMp3: false);
            isMp3 = false;
        }
        if (string.IsNullOrEmpty(purl)) return (null, false);

        if (purl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Log($"播放地址：{t.Title} -> {Snip(purl, 120)}");
            return (purl, isMp3);
        }

        // 实测（2026-09）：登录态 vkey 只能从 isure.stream 下载；接口下发的 sip 列表
        // （aqqmusic.tc.qq.com 等）对登录态 purl 会 404；免登录 purl 用 sip 反而正常。
        // 所以不信任单一 host，构建候选列表后逐个实测（下载首字节验证）。
        var candidates = new List<string>
        {
            "https://isure.stream.qqmusic.qq.com/",
            "http://isure.stream.qqmusic.qq.com/",
            "http://dl.stream.qqmusic.qq.com/"
        };
        // 追加 sip 常见 host 兜底（免登录 purl 需要它们）
        candidates.Add("http://aqqmusic.tc.qq.com/");
        candidates.Add("http://ws.stream.qqmusic.qq.com/");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in candidates)
        {
            if (!seen.Add(host)) continue;
            var full = host + purl.TrimStart('/');
            if (await ProbeAudioUrl(full))
            {
                Log($"播放地址：{t.Title} -> {Snip(full, 120)}");
                return (full, isMp3);
            }
        }

        Log($"播放地址全部 host 验证失败：{t.Title}（疑似 VIP/无播放权限）");
        return (null, false);
    }

    /// <summary>
    /// 单曲详情接口补齐 media_mid（fcg_play_single_song 免登录可用，响应带 file.media_mid）。
    /// 失败时保持原值不动。
    /// </summary>
    private async Task EnsureMediaMid(Track t)
    {
        try
        {
            var url = "https://c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg" +
                      $"?format=json&songmid={Uri.EscapeDataString(t.Id)}";
            var (status, body) = await HttpGetText(url, "https://y.qq.com/");
            if (status != 200) return;
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var arr) ||
                arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return;
            var s0 = arr[0];
            if (s0.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
            {
                var mm = FirstString(file, "media_mid", "mediaMid");
                if (mm.Length > 0)
                {
                    Log($"media_mid 校正：{t.Title} {t.MediaMid} -> {mm}");
                    t.MediaMid = mm;
                }
            }
        }
        catch (Exception e)
        {
            Log($"media_mid 校正失败（忽略，按原值继续）：{t.Title}：{e.Message}");
        }
    }

    /// <summary>实测一个音频 URL 是否可下载（发 GET 读首字节即断开，避免把 404 留给播放器）。</summary>
    private static async Task<bool> ProbeAudioUrl(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://y.qq.com/portal/player.html");
            req.Headers.TryAddWithoutValidation("User-Agent", UA);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return false;
            var stream = await resp.Content.ReadAsStreamAsync();
            var buf = new byte[16];
            var n = await stream.ReadAsync(buf, 0, buf.Length);
            return n > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<string?> GetQQPurl(Track t, bool preferMp3)
    {
        var uin = string.IsNullOrEmpty(config.QQMusicId) ? "0" : config.QQMusicId;

        // 实测（2026-09）：POST musicu.fcg 的 vkey.GetVkeyServerBase64 一律返回 500003/860100005，
        // 无论是否登录、歌曲是否免费。老版 GET data= + vkey.GetVkeyServer 仍可用：
        // 免费歌免登录即可拿到 purl，登录后 VIP 账号可拿 M500。
        var filename = preferMp3 ? $"M500{t.MediaMid}.mp3" : $"C400{t.MediaMid}.m4a";
        var data = new Dictionary<string, object>
        {
            ["req_0"] = new Dictionary<string, object>
            {
                ["module"] = "vkey.GetVkeyServer",
                ["method"] = "CgiGetVkey",
                ["param"] = new Dictionary<string, object>
                {
                    ["guid"] = "7329278450",
                    ["songmid"] = new[] { t.Id },
                    ["songtype"] = new[] { 0 },
                    ["uin"] = uin,
                    ["loginflag"] = 1,
                    ["platform"] = "20",
                    ["filename"] = new[] { filename }
                }
            },
            ["comm"] = new Dictionary<string, object>
            {
                ["uin"] = uin, ["format"] = "json", ["ct"] = 24, ["cv"] = 0
            }
        };

        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg?format=json&data=" +
                  Uri.EscapeDataString(JsonSerializer.Serialize(data));
        var (status, body) = await HttpGetText(url, "https://y.qq.com/portal/player.html");
        Log($"vkey 响应（{(preferMp3 ? "mp3" : "m4a")}，uin={uin}，HTTP {status}）：{Snip(body, 160)}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("req_0", out var r)) return null;
            if (!r.TryGetProperty("data", out var d)) return null;
            if (!d.TryGetProperty("midurlinfo", out var arr) || arr.GetArrayLength() == 0) return null;
            var info = arr[0];
            var purl = info.TryGetProperty("purl", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(purl)) return null;

            // 注意：不在这里拼 sip[0]。实测（2026-09）登录态 vkey 只能从 isure.stream 下载，
            // 而服务端下发的 sip 域名（aqqmusic.tc.qq.com 等）对登录态 purl 会 404。
            // 返回相对 purl，host 候选由 ResolvePlayUrl 逐个实测挑选。
            return purl;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> PostMusicu(Dictionary<string, object> payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("User-Agent", UA);
        req.Headers.Referrer = new Uri("https://y.qq.com/portal/player.html");
        var resp = await AuthHttp.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (body.Length > 12_000_000)
        {
            LogError($"musicu 响应异常庞大（{body.Length} 字符），已拒收防止解析崩溃");
            return "{}";
        }
        return body;
    }

    /// <summary>g_tk：qm_keyst 的 hash33（5381 种子）；未登录时 hash33("") = 5381，与参考实现一致。</summary>
    private uint MusicGtk()
    {
        var key = GetCookieValue("qm_keyst");
        if (key.Length == 0) key = GetCookieValue("qqmusic_key");
        return Hash33(key, 5381u);
    }

    private async Task<string> MusicuCall(string module, string method, Dictionary<string, object> param)
    {
        var uin = string.IsNullOrEmpty(config.QQMusicId) ? "0" : config.QQMusicId;
        return await PostMusicu(new Dictionary<string, object>
        {
            ["comm"] = new Dictionary<string, object>
            {
                ["ct"] = 24, ["cv"] = 4747474, ["v"] = 1300, ["uin"] = uin,
                ["g_tk"] = MusicGtk(), ["g_tk_new_20200303"] = MusicGtk(),
                ["format"] = "json", ["platform"] = "yqq.json", ["needNewCode"] = 1
            },
            ["req"] = new Dictionary<string, object>
            {
                ["module"] = module,
                ["method"] = method,
                ["param"] = param
            }
        });
    }

    // ==================================================================
    // 歌词（QQ音乐）
    // ==================================================================
    private void TriggerLyric(Track song)
    {
        loadingLyric = true;
        lyricError = string.Empty;
        currentLyrics.Clear();
        lyricCurrentIndex = -1;

        _ = Task.Run(async () =>
        {
            try
            {
                var lrc = await GetQQLyric(song.Id);
                pendingLyrics = ParseLrc(lrc);
                Log($"歌词：{song.Title} -> {pendingLyrics.Count} 行");
            }
            catch (Exception e)
            {
                LogError($"歌词获取失败：{song.Title} -> {e}");
                lyricError = $"歌词获取失败: {e.Message}";
            }
            finally
            {
                loadingLyric = false;
            }
        });
    }

    private async Task<string> GetQQLyric(string songMid)
    {
        // 1) 先拿数字 songID（新版歌词接口必须要）
        long songId = 0;
        try
        {
            var detail = await MusicuCall("music.pf_song_detail_svr", "get_song_detail",
                new Dictionary<string, object> { ["song_mid"] = songMid });
            using var dd = JsonDocument.Parse(detail);
            if (dd.RootElement.TryGetProperty("req", out var dr) && dr.TryGetProperty("data", out var ddata) &&
                ddata.TryGetProperty("track_info", out var ti) && ti.TryGetProperty("id", out var idEl) &&
                idEl.ValueKind == JsonValueKind.Number)
            {
                songId = idEl.GetInt64();
            }
        }
        catch (Exception e)
        {
            LogError($"QQ歌曲详情异常：{e.Message}");
        }

        // 2) 新版接口（免登录可用，Base64 LRC）
        try
        {
            var body = await MusicuCall("music.musichallSong.PlayLyricInfo", "GetPlayLyricInfo",
                new Dictionary<string, object> { ["songMID"] = songMid, ["songID"] = songId });
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("req", out var r) && r.TryGetProperty("data", out var d) &&
                d.TryGetProperty("lyric", out var ly))
            {
                var b64 = ly.GetString() ?? string.Empty;
                if (b64.Length > 0)
                {
                    Log($"QQ歌词成功：{songMid}，{b64.Length}字节Base64");
                    return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                }
            }
            Log($"QQ歌词新版接口无数据：{Snip(body, 140)}");
        }
        catch (Exception e)
        {
            LogError($"QQ歌词新版接口异常：{e.Message}");
        }

        // 3) 旧接口兜底
        try
        {
            var url = $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Uri.EscapeDataString(songMid)}&format=json&nobase64=0&g_tk=5381";
            var (status, body) = await HttpGetText(url, "https://y.qq.com/portal/player.html");
            Log($"QQ歌词旧接口：HTTP {status} / {Snip(body, 100)}");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("lyric", out var lyric))
            {
                var b64 = lyric.GetString() ?? string.Empty;
                if (b64.Length > 0) return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
        }
        catch (Exception e)
        {
            LogError($"QQ歌词旧接口异常：{e.Message}");
        }

        return string.Empty;
    }

    private void UpdateLyricIndex()
    {
        if (currentLyrics.Count == 0)
        {
            lyricCurrentIndex = -1;
            return;
        }

        var elapsed = enginePosition;
        var index = -1;
        for (var i = 0; i < currentLyrics.Count; i++)
        {
            if (currentLyrics[i].Time <= elapsed) index = i;
            else break;
        }
        lyricCurrentIndex = index;
    }

    private static List<LrcLine> ParseLrc(string lrc)
    {
        var lines = new List<LrcLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return lines;

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var matches = Regex.Matches(line, @"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]");
            if (matches.Count == 0) continue;

            var text = Regex.Replace(line, @"\[[^\]]*\]", string.Empty).Trim();
            foreach (Match m in matches)
            {
                var min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var frac = m.Groups[3].Success
                    ? double.Parse("0." + m.Groups[3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture)
                    : 0;
                lines.Add(new LrcLine(min * 60 + sec + frac, text));
            }
        }

        return lines.OrderBy(l => l.Time).ToList();
    }

    // ==================================================================
    // 封面
    // ==================================================================
    private void TriggerCover(Track t)
    {
        if (t == null) return;
        if (string.IsNullOrEmpty(t.CoverUrl)) return;
        if (coverCache.ContainsKey(t.CoverUrl) || coverLoading.Contains(t.CoverUrl)) return;

        coverLoading.Add(t.CoverUrl);
        var coverUrl = t.CoverUrl;
        TriggerCoverBlur(coverUrl);
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(coverUrl);
                pendingCoverBytes = new CoverBytes { Url = coverUrl, Bytes = bytes };
            }
            catch
            {
                pendingCoverBytes = new CoverBytes { Url = coverUrl, Bytes = Array.Empty<byte>() };
            }
        });
    }

    /// <summary>拉取封面的 90x90 小图做模糊背景（键仍用原封面 URL）。</summary>
    private void TriggerCoverBlur(string coverUrl)
    {
        if (blurCache.ContainsKey(coverUrl) || blurLoading.Contains(coverUrl)) return;
        // QQ音乐封面 URL 形如 T002R500x500M000<albumMid>.jpg，替换尺寸段即得小图
        var m = Regex.Match(coverUrl, @"R\d+x\d+M");
        if (!m.Success) return;
        var blurUrl = coverUrl.Substring(0, m.Index) + "R90x90M" + coverUrl[(m.Index + m.Length)..];

        blurLoading.Add(coverUrl);
        _ = Task.Run(async () =>
        {
            IDalamudTextureWrap? wrap = null;
            try
            {
                var bytes = await Http.GetByteArrayAsync(blurUrl);
                if (bytes.Length > 0) wrap = await DalamudServices.TextureProvider.CreateFromImageAsync(bytes);
            }
            catch { wrap = null; }
            pendingBlurTex = new TexResult { Url = coverUrl, Tex = wrap };
        });
    }

    private void EnsureCover()
    {
        var cb = pendingCoverBytes;
        if (cb != null)
        {
            pendingCoverBytes = null;

            if (cb.Bytes.Length == 0)
            {
                coverCache[cb.Url] = null;
                coverLoading.Remove(cb.Url);
            }
            else
            {
                var bytesCopy = cb.Bytes;
                var url = cb.Url;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var wrap = await DalamudServices.TextureProvider.CreateFromImageAsync(bytesCopy);
                        if (disposed)
                        {
                            // 模块已卸载：立即归还贴图，避免留下野句柄
                            try { wrap.Dispose(); } catch { }
                            return;
                        }
                        pendingCoverTex = new TexResult { Url = url, Tex = wrap };
                    }
                    catch
                    {
                        pendingCoverTex = new TexResult { Url = url, Tex = null };
                    }
                });
            }
        }

        var tr = pendingCoverTex;
        if (tr != null)
        {
            pendingCoverTex = null;
            if (disposed)
            {
                try { tr.Tex?.Dispose(); } catch { }
            }
            else
            {
                coverCache[tr.Url] = tr.Tex;
                coverLoading.Remove(tr.Url);
                coverOrder.Enqueue(tr.Url);
                // 容量上限：长期挂机听歌时防止贴图无限膨胀吃光显存
                while (coverOrder.Count > 24)
                {
                    var oldUrl = coverOrder.Dequeue();
                    if (coverCache.TryGetValue(oldUrl, out var oldTex))
                    {
                        coverCache.Remove(oldUrl);
                        try { if (oldTex is IDisposable d) d.Dispose(); } catch { }
                    }
                    if (blurCache.Remove(oldUrl, out var oldBlur))
                    {
                        try { oldBlur?.Dispose(); } catch { }
                    }
                }
            }
        }

        var br = pendingBlurTex;
        if (br != null)
        {
            pendingBlurTex = null;
            if (disposed)
            {
                try { br.Tex?.Dispose(); } catch { }
            }
            else
            {
                blurCache[br.Url] = br.Tex;
                blurLoading.Remove(br.Url);
            }
        }

        var gt = pendingGlassTex;
        if (gt != null)
        {
            pendingGlassTex = null;
            if (disposed)
            {
                try { gt.Dispose(); } catch { }
            }
            else
            {
                glassTexture = gt;
            }
        }
    }

    /// <summary>惰性创建内嵌的 1x64 白色渐变贴图（玻璃底 / 播放中兜底渐变共用）。</summary>
    private void EnsureGlassTexture()
    {
        if (glassTexRequested) return;
        glassTexRequested = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var wrap = await DalamudServices.TextureProvider.CreateFromImageAsync(
                    Convert.FromBase64String(GlassGradientPngB64));
                pendingGlassTex = wrap;
            }
            catch { pendingGlassTex = null; }
        });
    }

    private IDalamudTextureWrap? GetCurrentCoverTexture()
    {
        if (currentTrack == null || string.IsNullOrEmpty(currentTrack.CoverUrl)) return null;
        return coverCache.TryGetValue(currentTrack.CoverUrl, out var tex) ? tex : null;
    }

    /// <summary>当前封面对应的 90x90 模糊底图（键 = 原封面 URL）。</summary>
    private IDalamudTextureWrap? GetCurrentCoverBlurTexture()
    {
        if (currentTrack == null || string.IsNullOrEmpty(currentTrack.CoverUrl)) return null;
        return blurCache.TryGetValue(currentTrack.CoverUrl, out var tex) ? tex : null;
    }

    // ==================================================================
    // QQ音乐：我喜欢的音乐 / 收藏歌单
    // ==================================================================
    private void TriggerLoadFavorites(bool silent = false)
    {
        if (string.IsNullOrEmpty(config.QQMusicId))
        {
            if (!silent) favError = "请先登录 QQ音乐";
            return;
        }

        loadingFav = true;
        favError = string.Empty;
        var uin = config.QQMusicId;

        _ = Task.Run(async () =>
        {
            try
            {
                var mids = await FetchLikedMids(FavDirId, uin);
                var tracks = await FetchSongsByMids(mids);
                if (tracks.Count == 0)
                {
                    favError = "「我喜欢的音乐」还没有歌曲，或登录态已失效（可重新扫码）";
                }
                pendingFavTracks = tracks;
            }
            catch (Exception e)
            {
                LogError("我喜欢列表加载失败：" + e);
                if (!silent) favError = "加载失败: " + e.Message;
            }
            finally
            {
                loadingFav = false;
            }
        });
    }

    /// <summary>取自己文件夹（dirid）内的歌曲 mid 列表。实测：fcg_musiclist_getmyfav 需登录，
    /// 返回 map / mapmid 两个「mid(或id) -> 1」字典；mapmid 的 key 就是 songmid。</summary>
    private async Task<List<string>> FetchLikedMids(long dirId, string uin)
    {
        var gtk = MusicGtk();
        var url = "https://c.y.qq.com/splcloud/fcgi-bin/fcg_musiclist_getmyfav.fcg" +
                  $"?dirid={dirId}&dirinfo=1&g_tk={gtk}&format=json&uin={uin}&loginUin={uin}";
        var (status, body) = await HttpGetText(url, "https://y.qq.com/n/yqq/playlist");
        Log($"getmyfav(dirid={dirId}) HTTP {status}：{Snip(body, 200)}");

        var mids = new List<string>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("mapmid", out var mm) && mm.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in mm.EnumerateObject())
            {
                if (prop.Name.Length > 0) mids.Add(prop.Name);
            }
        }
        return mids;
    }

    /// <summary>按 songmid 列表批量取歌曲详情。fcg_play_single_song 免登录、每首一次请求，
    /// 8 路并发控制节奏。</summary>
    private async Task<List<Track>> FetchSongsByMids(List<string> mids)
    {
        var tracks = new List<Track>();
        if (mids.Count == 0) return tracks;

        foreach (var chunk in mids.Chunk(8))
        {
            var jobs = chunk.Select(async mid =>
            {
                try
                {
                    var url = "https://c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg" +
                              $"?format=json&songmid={Uri.EscapeDataString(mid)}";
                    var (status, body) = await HttpGetText(url, "https://y.qq.com/");
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("data", out var d) &&
                        d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0)
                    {
                        return QQSongFromJson(d[0]);
                    }
                }
                catch (Exception e)
                {
                    LogError($"单曲详情失败 {mid}：{e.Message}");
                }
                return null;
            });
            var done = await Task.WhenAll(jobs);
            tracks.AddRange(done.Where(t => t != null).Select(t => t!));
        }
        return tracks;
    }

    private void TriggerLoadPlaylists(bool silent = false)
    {
        if (string.IsNullOrEmpty(config.QQMusicId))
        {
            if (!silent) playlistError = "请先登录 QQ音乐";
            return;
        }

        loadingPlaylists = true;
        playlistError = string.Empty;
        var uin = config.QQMusicId;

        _ = Task.Run(async () =>
        {
            try
            {
                // 实测：老接口 fcg_user_created_diss 带 hostUin=0/loginUin=0 + Referer profile.html
                // 才返回自己的歌单（musicasset.PlaylistBaseRead 已被服务端拒绝 40000）。
                // 返回 disslist 含「我喜欢(201)」与自建歌单；205(QZone背景音乐)/206(本地上传) 对播放无意义。
                var gtk = MusicGtk();
                var url = "https://c.y.qq.com/rsc/fcgi-bin/fcg_user_created_diss" +
                          $"?hostUin=0&hostuin={uin}&sin=0&size=200&g_tk={gtk}&loginUin=0" +
                          "&format=json&inCharset=utf8&outCharset=utf-8&notice=0" +
                          "&platform=yqq.json&needNewCode=0";
                var (status, body) = await HttpGetText(url, "https://y.qq.com/portal/profile.html");
                Log($"歌单列表响应：HTTP {status}，{Snip(body, 200)}");

                var list = new List<PlaylistInfo>();
                using (var doc = JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("data", out var d) &&
                        d.TryGetProperty("disslist", out var dl) && dl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var pl in dl.EnumerateArray())
                        {
                            var dirId = FirstLong(pl, "dirid", "dirId", "dissid");
                            var name = FirstString(pl, "diss_name", "dissname", "dirName", "title");
                            var count = (int)FirstLong(pl, "song_cnt", "songNum", "songnum");
                            if (dirId <= 0 || name.Length == 0) continue;
                            if (dirId == 205 || dirId == 206) continue;   // QZone背景音乐 / 本地上传
                            if (dirId == FavDirId) continue;              // 我喜欢单独展示
                            list.Add(new PlaylistInfo(
                                dirId.ToString(CultureInfo.InvariantCulture), name, count));
                        }
                    }
                }

                if (list.Count == 0)
                    playlistError = "暂无其他歌单";
                pendingPlaylists = list;
            }
            catch (Exception e)
            {
                LogError("歌单列表加载失败：" + e);
                if (!silent) playlistError = "加载失败: " + e.Message;
            }
            finally
            {
                loadingPlaylists = false;
            }
        });
    }

    /// <summary>确保某个歌单的曲目已加载（结果进缓存，主线程落账）。切到该歌单页时调用。</summary>
    private void EnsurePlaylistTracks(PlaylistInfo pl)
    {
        if (playlistTrackCache.ContainsKey(pl.Id) || playlistTrackLoading.Contains(pl.Id)) return;
        playlistTrackLoading.Add(pl.Id);
        playlistError = string.Empty;
        var uin = config.QQMusicId;
        var dirId = pl.Id;
        var name = pl.Name;
        Log($"加载歌单曲目：{name}（id={dirId}）");

        _ = Task.Run(async () =>
        {
            var tracks = new List<Track>();
            try
            {
                List<string> mids;
                if (long.TryParse(dirId, out var dir) && dir == FavDirId)
                {
                    // 我喜欢：走 getmyfav
                    mids = await FetchLikedMids(dir, uin);
                }
                else
                {
                    // 自建/收藏文件夹：getmyfav 同样适用（实测 dirid=206 可返回自己的文件夹内容）
                    mids = await FetchLikedMids(long.TryParse(dirId, out var d2) ? d2 : 0, uin);
                    if (mids.Count == 0)
                    {
                        // 兜底：老版歌单详情接口（对部分歌单仍可用）
                        var gtk = MusicGtk();
                        var url = "https://c.y.qq.com/qzone/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg" +
                                  $"?type=1&json=1&utf8=1&onlysong=0&disstid={Uri.EscapeDataString(dirId)}" +
                                  $"&loginUin=0&format=json&inCharset=utf8&outCharset=utf-8&notice=0" +
                                  $"&platform=yqq.json&needNewCode=0&g_tk={gtk}&uin={uin}";
                        var (status, body) = await HttpGetText(url, "https://y.qq.com/n/yqq/playlist");
                        Log($"歌单详情兜底（{dirId}）HTTP {status}：{Snip(body, 160)}");
                        tracks = ExtractSongList(body);
                    }
                }

                if (tracks.Count == 0)
                    tracks = await FetchSongsByMids(mids);
            }
            catch (Exception e)
            {
                LogError("歌单曲目加载失败：" + e);
                playlistError = "歌单曲目加载失败: " + e.Message;
            }
            pendingPlaylistTrackCache = new PlTracksResult { DirId = dirId, Tracks = tracks };
        });
    }

    /// <summary>从 musicu 响应里尽力抽取歌曲数组（兼容多种字段命名）。</summary>
    private static List<Track> ExtractSongList(string body)
    {
        var tracks = new List<Track>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!TryFindSongArray(doc.RootElement, out var arr)) return tracks;
            foreach (var el in arr.EnumerateArray())
            {
                var t = QQSongFromJson(el);
                if (t != null) tracks.Add(t);
            }
        }
        catch (JsonException)
        {
        }
        return tracks;
    }

    private static bool TryFindSongArray(JsonElement el, out JsonElement array, int depth = 0)
    {
        array = default;
        // 深度上限：异常响应可能嵌套极深，递归爆栈会直接杀死进程且无法捕获
        if (depth > 12) return false;
        if (el.ValueKind != JsonValueKind.Object) return false;

        // 常见字段名优先（浅层先找）
        foreach (var name in new[] { "v_songInfo", "songlist", "songs", "songList", "v_songinfo", "list" })
        {
            if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
            {
                // 确认是"歌曲数组"而不是其它结构
                var first = v[0];
                if (first.ValueKind == JsonValueKind.Object &&
                    (first.TryGetProperty("mid", out _) || first.TryGetProperty("songmid", out _) ||
                     first.TryGetProperty("songMid", out _) || first.TryGetProperty("name", out _)))
                {
                    array = v;
                    return true;
                }
            }
        }

        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object &&
                TryFindSongArray(prop.Value, out array, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    // ==================================================================
    // 收藏写操作：musics.fcg 加密通道（通过本机 Node.js sidecar）
    // ==================================================================
    // 2026-09 实测：老 fcg 接口（fcg_music_add2songdir / delbatchsong）已全部失效
    //（返回 403 no permit 或假成功不落库），musicu.fcg 的 musicasset 写模块返回
    // 80105（要求请求签名）。唯一可用通道：
    //   POST https://u6.y.qq.com/cgi-bin/musics.fcg?_=<ts>&encoding=ag-1&sign=<zzc...>
    //     请求体 = base64( IV(12) + AES-128-GCM(plain) + tag(16) )
    //     响应体 = 加密二进制（响应密钥藏在官方 VMP 内部，与请求密钥不同，无法在 C# 侧解密）
    //   签名的中间段与响应解密都由 y.qq.com 的 VMP 混淆脚本计算，无法在 C# 内复算，
    //   因此内嵌官方签名/加解密脚本（gzip 后约 27KB），每次写操作调用本机 Node.js
    //   执行完整链路（签名 → 加密 → POST → 解密），C# 只负责拼参数与解析结果。
    //   本机需装有 Node.js（PATH 或常见安装目录自动探测；也可在 config.NodePath 指定）。

    private static volatile string? _qmFcgScriptPath;

    /// <summary>内嵌 musics.fcg 写通道脚本（gzip+base64 的 Node 脚本）。</summary>
    private const string QmFcgScriptGzipB64 =
        "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";

    private static byte[] QmUnGzip(byte[] gz)
    {
        using var src = new MemoryStream(gz);
        using var gzip = new GZipStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        gzip.CopyTo(dst);
        return dst.ToArray();
    }

    /// <summary>把内嵌写通道脚本写到本地缓存目录，返回脚本路径。</summary>
    private string EnsureQmFcgScript()
    {
        var p = _qmFcgScriptPath;
        if (p != null && File.Exists(p)) return p;
        var dir = Path.Combine(Path.GetTempPath(), "OmniMusic");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "omni_qmfcg_v1.js");
        File.WriteAllBytes(path, QmUnGzip(Convert.FromBase64String(QmFcgScriptGzipB64)));
        _qmFcgScriptPath = path;
        return path;
    }

    // ---- Node 运行时自动安装（分享场景：接收者拿到模块即可用，不必自行装 Node） ----
    private readonly object nodeDlLock = new();
    private volatile bool nodeDlRunning;
    private volatile string nodeDlState = string.Empty;   // 设置页显示用的状态文本
    private string? nodeExeCached;                        // 设置页用（避免每帧扫 PATH）
    private DateTime nodeProbeTime = DateTime.MinValue;

    /// <summary>内置下载源，按顺序尝试（前一个失败自动换下一个）。
    /// 顺序按 2026-09-28 实测速度排（国内直连 github.com 只有几 KB/s，必须走反代镜像）：
    /// 1) 本仓库 Release 附件 —— gh-proxy.com（实测约 500KB/s）
    /// 2) 本仓库 Release 附件 —— ghproxy.net（实测约 300KB/s）
    /// 3) 本仓库 Release 附件 —— ghfast.top（备用镜像）
    /// 4) 本仓库 Release 附件 —— 直连 GitHub（海外或能直连的环境）
    /// 5) Node 官方发行包 —— npmmirror 国内镜像（终极兜底，不依赖自己的仓库）</summary>
    private static readonly string[] BuiltinNodeUrls =
    {
        "https://gh-proxy.com/https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/download/node-v22.22.2/node-v22.22.2-win-x64.zip",
        "https://ghproxy.net/https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/download/node-v22.22.2/node-v22.22.2-win-x64.zip",
        "https://ghfast.top/https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/download/node-v22.22.2/node-v22.22.2-win-x64.zip",
        "https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/download/node-v22.22.2/node-v22.22.2-win-x64.zip",
        "https://registry.npmmirror.com/-/binary/node/v22.22.2/node-v22.22.2-win-x64.zip",
    };

    /// <summary>按顺序给出可用下载源：设置里的自定义地址优先，其后是内置候选。</summary>
    private List<string> NodeUrlCandidates()
    {
        var list = new List<string>();
        var cfg = (config.NodeDownloadUrl ?? string.Empty).Trim();
        if (cfg.StartsWith("http", StringComparison.OrdinalIgnoreCase)) list.Add(cfg);
        foreach (var u in BuiltinNodeUrls)
            if (!list.Contains(u, StringComparer.OrdinalIgnoreCase)) list.Add(u);
        return list;
    }

    /// <summary>Node 运行的落地位置（与「随模块预置」的路径一致，探测顺序里已包含它）。</summary>
    private static string NodeTargetPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmniMusic", "node", "node.exe");

    /// <summary>设置页显示用：带 3 秒缓存的探测（PATH 扫描不适合每帧跑）。</summary>
    private string? FindNodeExeCached()
    {
        if (nodeExeCached != null && File.Exists(nodeExeCached)) return nodeExeCached;
        if ((DateTime.UtcNow - nodeProbeTime) < TimeSpan.FromSeconds(3)) return null;
        nodeProbeTime = DateTime.UtcNow;
        nodeExeCached = FindNodeExe();
        return nodeExeCached;
    }

    /// <summary>
    /// 模块加载时的一次性 Node 自检安装（分享场景：接收者拿到模块即可用，不必自己装 Node）。
    /// 规则（用户明确要求，防止反复下载）：
    ///   1) 本机已有 Node → **完全不联网、不下载**，直接返回；
    ///   2) 没有 Node 且开启了自动安装 → 自动下载安装一次，落地到
    ///      %LOCALAPPDATA%\OmniMusic\node\node.exe，之后探测即命中，不会再下载；
    ///   3) 失败后 24 小时内不重试（标记文件节流），避免每次重载都拉一遍；
    ///   4) 全程在后台线程执行，并延迟 3 秒启动，不在重载瞬间做重活。
    /// 收藏等写操作**不再触发下载**——只做本机探测，找不到就明确报错提示。
    /// </summary>
    private async Task EnsureNodeRuntimeOnLoadAsync()
    {
        try
        {
            await Task.Delay(3000);      // 让模块先稳定，避免重载瞬间并发做重活

            var existing = FindNodeExe();
            if (existing != null)
            {
                nodeExeCached = existing;
                nodeDlState = "已检测到本机 Node，无需安装";
                Log($"[Node] 本机已有 Node，跳过安装：{existing}");
                return;
            }

            if (!config.NodeAutoInstall)
            {
                nodeDlState = "未检测到 Node（自动安装已关闭）";
                Log("[Node] 未检测到 Node，且自动安装已关闭");
                return;
            }

            // 节流标记落在磁盘上（不依赖 config 落盘时机）：失败后 24h 内不再自动尝试
            var dir = Path.GetDirectoryName(NodeTargetPath())!;
            var mark = Path.Combine(dir, ".last-install-attempt");
            try
            {
                if (File.Exists(mark) &&
                    long.TryParse(File.ReadAllText(mark).Trim(), out var lastUnix) &&
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastUnix < 24 * 3600)
                {
                    nodeDlState = "上次自动安装未成功，24 小时内不再重试（可在设置里手动下载）";
                    Log("[Node] 24 小时内已尝试过自动安装，本次跳过");
                    return;
                }
            }
            catch { /* 标记文件不可用时忽略节流，不影响主流程 */ }

            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(mark, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            }
            catch { }

            nodeDlState = "未检测到 Node，正在自动安装（仅一次）…";
            await TryDownloadNodeAsync();
        }
        catch (Exception e)
        {
            try { LogError("[Node] 加载时自检异常：" + e.Message); } catch { }
        }
    }

    /// <summary>
    /// 下载 Node 运行时到 %LOCALAPPDATA%\OmniMusic\node\node.exe。
    /// **多源自动回退**：按 NodeUrlCandidates() 顺序依次尝试（本仓库 Release 附件走国内镜像 →
    /// 另一个镜像 → GitHub 直连 → Node 官方国内镜像），任一成功即返回。
    /// 已有一个下载在跑时不重复触发（返回 null，调用方按仍未找到处理）。
    /// </summary>
    private async Task<string?> TryDownloadNodeAsync()
    {
        var urls = NodeUrlCandidates();
        if (urls.Count == 0) return null;

        lock (nodeDlLock)
        {
            if (nodeDlRunning) return null;
            nodeDlRunning = true;
        }

        try
        {
            var target = NodeTargetPath();
            QueueChat("正在下载并安装 Node 运行时（仅一次，之后不再下载）…");
            for (var i = 0; i < urls.Count; i++)
            {
                nodeDlState = $"正在下载 Node 运行时…（源 {i + 1}/{urls.Count}）";
                Log($"[Node] 尝试下载源 {i + 1}/{urls.Count}：{urls[i]}");
                if (await TryFetchNodeAsync(urls[i], target))
                {
                    nodeExeCached = target;
                    nodeDlState = "Node 运行时已就绪";
                    Log($"[Node] 下载完成并校验通过：{target}");
                    QueueChat("Node 运行时已安装完成，收藏功能可用（之后不会再下载）");
                    return target;
                }
                Log("[Node] 该下载源失败，自动换下一个");
            }

            nodeDlState = "所有下载源都失败（可在设置里手动指定 node.exe 路径）";
            LogError("[Node] 全部下载源均失败");
            QueueChat("Node 运行时安装失败：所有下载源都不可用。可在设置里手动指定 node.exe 路径，或稍后重试。");
            return null;
        }
        finally
        {
            nodeDlRunning = false;
        }
    }

    /// <summary>
    /// 从单个地址抓取并落地 node.exe，返回是否成功。
    /// 自动识别两种直链：官方发行 zip（内含 node.exe，自动解包）或裸 node.exe。
    /// </summary>
    private async Task<bool> TryFetchNodeAsync(string url, string target)
    {
        var tmp = target + ".download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using (var resp = await DlHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                using var src = await resp.Content.ReadAsStreamAsync();
                using var dst = File.Create(tmp);
                var buf = new byte[1 << 16];
                long got = 0;
                var lastPct = -1;
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n);
                    got += n;
                    if (total <= 0) continue;
                    var pct = (int)(got * 100 / total);
                    if (pct < 100 && pct / 5 == lastPct / 5) continue;
                    lastPct = pct;
                    nodeDlState = $"正在下载 Node 运行时… {pct}%";
                }
            }

            var len = new FileInfo(tmp).Length;
            if (len < 5_000_000) throw new InvalidOperationException($"下载内容过小（{len} 字节），可能不是有效的发行包");

            // 前两字节是 'PK' → 官方 zip，解包出 node.exe；否则当作裸 node.exe
            var sig = new byte[2];
            using (var fs = File.OpenRead(tmp)) fs.Read(sig, 0, 2);
            if (sig[0] == 0x50 && sig[1] == 0x4B)
            {
                nodeDlState = "正在解包…";
                // 注意：必须先把 zip 及其底层文件流的句柄全部释放，才能 File.Delete(tmp)（Windows 不允许删除被占用的文件）
                using (var zfs = File.OpenRead(tmp))
                using (var zip = new ZipArchive(zfs, ZipArchiveMode.Read))
                {
                    var entry = zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, "node.exe", StringComparison.OrdinalIgnoreCase));
                    if (entry == null) throw new InvalidOperationException("发行包内未找到 node.exe");
                    using var es = entry.Open();
                    using var outp = File.Create(target);
                    es.CopyTo(outp);
                }
                File.Delete(tmp);
            }
            else
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
            }

            if (!VerifyNodeExe(target))
            {
                try { File.Delete(target); } catch { }
                throw new InvalidOperationException("下载的 Node 无法运行（校验失败）");
            }
            return true;
        }
        catch (Exception e)
        {
            nodeDlState = "下载失败：" + e.Message;
            LogError($"[Node] 下载失败（{url}）：" + e.Message);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    /// <summary>跑一次 `node -v` 确认下载到的可执行文件真能用。</summary>
    private static bool VerifyNodeExe(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-v",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return outp.TrimStart().StartsWith("v", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>探测本机 Node.js 可执行文件：显式配置 → PATH → 常见安装位置。</summary>
    private string? FindNodeExe()
    {
        var cfg = config.NodePath;
        if (!string.IsNullOrWhiteSpace(cfg) && File.Exists(cfg)) return cfg;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(';'))
        {
            var d = dir.Trim().Trim('"');
            if (d.Length == 0) continue;
            try { var p = Path.Combine(d, "node.exe"); if (File.Exists(p)) return p; }
            catch { /* 非法路径字符等 */ }
        }

        try
        {
            string[] candidates =
            {
                // 随插件预置的运行时（%LOCALAPPDATA%\OmniMusic\node\node.exe）
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmniMusic", "node", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 调用 sidecar 执行 musics.fcg 写请求（签名+加密+POST+解密都在 Node 侧完成），
    /// 返回解密后的响应 JSON 文本。
    /// </summary>
    private async Task<string> QmFcgCallAsync(string payload, string uin, string keyst)
    {
        // 这里只做本机探测，不做任何下载——安装时机统一在模块加载时（避免每次收藏都触发下载）
        var node = FindNodeExe();
        if (node == null)
            throw new InvalidOperationException(
                "未找到 Node.js 运行时：请在模块设置的「音乐播放器设置」里点「立即下载并安装 Node」，或手动指定 node.exe 路径。");
        var script = EnsureQmFcgScript();

        var psi = new ProcessStartInfo
        {
            FileName = node,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(payload);
        psi.ArgumentList.Add(uin);
        if (keyst.Length > 0) psi.ArgumentList.Add(keyst);

        using var proc = Process.Start(psi);
        if (proc == null) throw new InvalidOperationException("Node.js 进程启动失败");
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        var exitTask = proc.WaitForExitAsync();
        if (await Task.WhenAny(exitTask, Task.Delay(15000)) != exitTask)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException("Node 写通道超时（15s）");
        }
        var so = (await outTask).Trim();
        var se = (await errTask).Trim();
        if (so.Length == 0)
            throw new InvalidOperationException("写通道无输出" + (se.Length > 0 ? "：" + Snip(se, 200) : ""));
        return so;
    }

    /// <summary>
    /// musics.fcg 加密写请求：拼 payload → sidecar → 解析。
    /// 返回 (req_1.code, req_1.data)。
    /// </summary>
    private async Task<(long Code, JsonElement Data)> QmFcgWriteAsync(string module, string method, object param)
    {
        var uin = config.QQMusicId.Trim();
        // 登录态里 qm_keyst 的值可能带 @domain 后缀，实际有效值在 @ 之前
        var keyst = GetCookieValue("qm_keyst");
        if (keyst.Length == 0) keyst = GetCookieValue("qqmusic_key");
        var at = keyst.IndexOf('@');
        if (at > 0) keyst = keyst[..at];

        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["comm"] = new Dictionary<string, object>
            {
                ["cv"] = 4747474, ["ct"] = 24, ["format"] = "json",
                ["inCharset"] = "utf-8", ["outCharset"] = "utf-8", ["notice"] = 0,
                ["platform"] = "yqq.json", ["needNewCode"] = 1,
                ["uin"] = uin,
                ["g_tk_new_20200303"] = MusicGtk(), ["g_tk"] = MusicGtk(),
            },
            ["req_1"] = new Dictionary<string, object>
            {
                ["module"] = module,
                ["method"] = method,
                ["param"] = param,
            },
        });

        var text = await QmFcgCallAsync(payload, uin, keyst);
        Log("musics.fcg[" + module + "." + method + "]：" + Snip(text, 220));

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("__err", out var errEl) && errEl.ValueKind == JsonValueKind.String)
            throw new InvalidOperationException("写通道失败：" + errEl.GetString());

        var topCode = root.TryGetProperty("code", out var tc) && tc.ValueKind == JsonValueKind.Number && tc.TryGetInt64(out var l0) ? l0 : -1;
        if (topCode != 0) return (topCode, default);
        if (!root.TryGetProperty("req_1", out var r1)) return (-2, default);
        var code = r1.TryGetProperty("code", out var c1) && c1.ValueKind == JsonValueKind.Number && c1.TryGetInt64(out var l1) ? l1 : -1;
        var data = r1.TryGetProperty("data", out var d1) ? d1.Clone() : default;
        return (code, data);
    }

    /// <summary>收藏 / 取消收藏（我喜欢）。走 musics.fcg 加密通道（AddSonglist / DelSonglist）。</summary>
    private void ToggleFavorite(Track song, bool like)
    {
        if (song.SongId <= 0) return;
        if (string.IsNullOrEmpty(config.QQMusicId))
        {
            QueueChat("请先登录 QQ音乐再收藏歌曲");
            return;
        }

        var songId = song.SongId;
        var mid = song.Id;

        // 先做本地乐观更新，后台失败再回滚
        if (like) likedIds.Add(songId); else likedIds.Remove(songId);

        _ = Task.Run(async () =>
        {
            var result = new FavToggleResult { SongId = songId, Mid = mid, Liked = like };
            try
            {
                var param = new Dictionary<string, object>
                {
                    ["dirId"] = FavDirId,
                    ["tid"] = 0,
                    ["bFmtUtf8"] = true,
                    ["v_songInfo"] = new object[]
                    {
                        new Dictionary<string, object> { ["songId"] = songId, ["songType"] = 13 },
                    },
                };
                var (code, data) = await QmFcgWriteAsync(
                    "music.musicasset.PlaylistDetailWrite", like ? "AddSonglist" : "DelSonglist", param);

                result.Success = code == 0;
                result.Message = code == 0
                    ? (like ? "已加入「我喜欢的音乐」" : "已从「我喜欢的音乐」移除")
                    : code == 1000
                        ? "收藏失败：登录态已失效，请重新扫码"
                        : code == 500026
                            ? (like ? "歌曲已在「我喜欢的音乐」里" : "歌曲不在「我喜欢的音乐」里")
                            : "收藏失败（code=" + code + "），详情见日志";
                if (code != 0 && data.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        if (data.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String)
                        {
                            var msg = m.GetString();
                            if (!string.IsNullOrEmpty(msg)) result.Message += "：" + msg;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception e)
            {
                LogError("收藏操作异常：" + e);
                result.Success = false;
                result.Message = "收藏操作失败: " + e.Message;
                // 回滚乐观更新
                if (like) likedIds.Remove(songId); else likedIds.Add(songId);
            }

            pendingFavToggle = result;
        });
    }

    // ==================================================================
    // QQ音乐扫码登录
    // ==================================================================
    private void StartQQLogin()
    {
        loginRunning = true;
        qqLoginStage = 1;
        loginMsg = "正在获取QQ登录二维码...";
        loginDebug = string.Empty;
        pendingQrBytes = null;
        // 二维码贴图的 key 是"字节数+魔数"，两次小图可能同 key；
        // 重扫前清空，保证新码一定会刷新贴图
        qrTextureKey = string.Empty;
        qrRequestedKey = string.Empty;

        _ = Task.Run(async () =>
        {
            try
            {
                // 0) 前置：先访问一次 xlogin 页面，让 CookieContainer 拿到
                //    pt_login_sig / pt_local_token / pt_guid_sig 等会话 cookie。
                //    缺了这些 cookie，ptqrlogin 会直接 403（实测）。
                try
                {
                    var xloginUrl = "https://xui.ptlogin2.qq.com/cgi-bin/xlogin?appid=716027609&daid=383&style=40" +
                                    "&s_url=" + Uri.EscapeDataString("https://graph.qq.com/oauth2.0/login_jump") +
                                    "&pt_3rd_aid=100497308";
                    var (xstatus, _) = await HttpGetText(xloginUrl, "https://graph.qq.com/");
                    Log($"QQ登录前置 xlogin：HTTP {xstatus}");
                }
                catch (Exception ex)
                {
                    LogError("xlogin 前置请求失败（继续尝试）：" + ex.Message);
                }

                // 1) 二维码：QQ互联 appid=716027609 + daid=383，通过 pt_3rd_aid=100497308 关联到 QQ音乐
                var qr = Array.Empty<byte>();
                var qrUrls = new[]
                {
                    "https://ssl.ptlogin2.qq.com/ptqrshow?appid=716027609&e=2&l=M&s=3&d=72&v=4&daid=383&pt_3rd_aid=100497308",
                    "https://ssl.ptlogin2.qq.com/ptqrshow?appid=716027609&e=2&l=M&s=3&d=72&v=4&daid=383&pt_3rd_aid=100497308&t=1"
                };
                foreach (var u in qrUrls)
                {
                    try
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, u);
                        req.Headers.TryAddWithoutValidation("User-Agent", UA);
                        req.Headers.TryAddWithoutValidation("Accept",
                            "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
                        req.Headers.Referrer = new Uri("https://xui.ptlogin2.qq.com/");
                        var resp = await AuthHttp.SendAsync(req);
                        var bytes = await resp.Content.ReadAsByteArrayAsync();
                        Log($"QQ二维码请求：HTTP {(int)resp.StatusCode}，{bytes.Length} 字节");
                        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 &&
                            bytes[2] == 0x4E && bytes[3] == 0x47)   // PNG 魔数 \x89PNG
                        {
                            qr = bytes;
                            break;
                        }
                        loginDebug = $"二维码接口返回异常：HTTP {(int)resp.StatusCode}，{bytes.Length} 字节";
                    }
                    catch (Exception ex)
                    {
                        LogError($"QQ二维码请求失败：{ex.Message}");
                    }
                }

                if (qr.Length == 0)
                {
                    throw new Exception("二维码接口没有返回图片，可稍后重试");
                }
                pendingQrBytes = qr;

                // 2) 轮询扫码状态。
                //    注意三点（均为 curl/多栈实测结论）：
                //    a) pt_3rd_aid=100497308 必须带上，缺了直接 403；
                //    b) action 里是"当前毫秒时间戳"，不能用别的小数字；
                //    c) .NET HttpClient 的 Windows TLS 指纹(SChannel)可能被腾讯 WAF 拦，
                //       403 时自动降级到 wininet 通道再试。
                var qrsig = AuthHandler.CookieContainer.GetCookies(new Uri("https://ssl.ptlogin2.qq.com"))["qrsig"]?.Value
                            ?? AuthHandler.CookieContainer.GetCookies(new Uri("https://ptlogin2.qq.com"))["qrsig"]?.Value
                            ?? string.Empty;
                var token = Hash33(qrsig);
                var loginSig = AuthHandler.CookieContainer.GetCookies(new Uri("https://ssl.ptlogin2.qq.com"))["pt_login_sig"]?.Value
                               ?? string.Empty;
                Log($"QQ登录：qrsig长度={qrsig.Length}，ptqrtoken={token}，login_sig长度={loginSig.Length}");
                loginMsg = "请使用手机QQ扫码登录";

                var deadline = DateTime.UtcNow.AddMinutes(3);
                string? redirectUrl = null;
                var useWinInet = false;          // HttpClient 被 WAF 拒时切 true
                while (DateTime.UtcNow < deadline && !disposed)
                {
                    await Task.Delay(2000);
                    var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var pollUrl =
                        $"https://ssl.ptlogin2.qq.com/ptqrlogin?u1={Uri.EscapeDataString("https://graph.qq.com/oauth2.0/login_jump")}" +
                        $"&ptqrtoken={token}&ptredirect=0&h=1&t=1&g=1&from_ui=1&ptlang=2052&action=0-0-{ts}" +
                        $"&js_ver=20122715&js_type=1&login_sig={Uri.EscapeDataString(loginSig)}&pt_uistyle=40" +
                        $"&aid=716027609&daid=383&pt_3rd_aid=100497308&";
                    var body = string.Empty;
                    if (!useWinInet)
                    {
                        try
                        {
                            var preq = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                            preq.Headers.TryAddWithoutValidation("User-Agent", UA);
                            preq.Headers.Referrer = new Uri("https://xui.ptlogin2.qq.com/");
                            var presp = await AuthHttp.SendAsync(preq);
                            body = await presp.Content.ReadAsStringAsync();
                            if ((int)presp.StatusCode == 403 || body.Length == 0)
                            {
                                useWinInet = true;
                                Log("ptqrlogin 走 HttpClient 被 403/空响应，切换 wininet 通道");
                                loginDebug = "直连被拦截，已切换备用网络通道…";
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogError("ptqrlogin HttpClient 异常：" + ex.Message);
                            useWinInet = true;
                            continue;
                        }
                    }
                    else
                    {
                        var cookieHeader = "qrsig=" + qrsig +
                            (loginSig.Length > 0 ? "; pt_login_sig=" + loginSig : "");
                        body = WinInetGet(pollUrl,
                            "User-Agent: " + UA +
                            "\r\nReferer: https://xui.ptlogin2.qq.com/" +
                            "\r\nCookie: " + cookieHeader);
                        Log($"ptqrlogin(wininet)：{Snip(body, 120)}");
                    }

                    // ptuiCB('code','0','url','0','msg','nick')
                    var m = Regex.Match(body, @"ptuiCB\('(\d+)','[^']*','([^']*)','[^']*','([^']*)',\s*'([^']*)'");
                    if (!m.Success)
                    {
                        if (body.Length > 0) Log($"QQ轮询响应无法解析：{Snip(body, 200)}");
                        continue;
                    }
                    var code = m.Groups[1].Value;
                    if (code == "66")
                    {
                        qqLoginStage = 4;
                        loginMsg = "二维码已过期，请重新点击 QQ音乐扫码登录";
                        break;
                    }
                    if (code == "67") qqLoginStage = 1;
                    if (code == "65")
                    {
                        qqLoginStage = 2;
                        loginMsg = "已扫描，请在手机上确认登录";
                    }
                    if (code == "0")
                    {
                        redirectUrl = m.Groups[2].Value;
                        var nick = m.Groups[4].Value.Trim();
                        if (string.IsNullOrEmpty(nick) || nick == "。") nick = "已登录";
                        loginNick = nick;
                        qqLoginStage = 3;
                        Log($"QQ扫码成功：昵称={nick}，跳转={Snip(redirectUrl, 160)}");
                        break;
                    }
                }

                // 3) 跟随跳转拿 code，再换 QQ音乐登录态
                if (redirectUrl != null)
                {
                    loginMsg = "扫码成功，正在换取音乐登录态…";

                    // 1) 跟进 check_sig：在 .qq.com 域种下 p_skey / p_uin 等 ptlogin 票据
                    var creq = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
                    creq.Headers.TryAddWithoutValidation("User-Agent", UA);
                    var cresp = await AuthHttp.SendAsync(creq);
                    var finalUrl = cresp.RequestMessage?.RequestUri?.ToString() ?? redirectUrl;
                    Log($"QQ登录 check_sig 完成：{Snip(finalUrl, 220)}");

                    var pSkey = GetCookieValue("p_skey");
                    Log($"QQ登录 p_skey 长度={pSkey.Length}");

                    // 2) 关键步骤：主动 POST /oauth2.0/authorize 换 OAuth code。
                    //    实测 code 不是 check_sig 跳转带来的——login_jump 终点没有 code 参数，
                    //    必须拿 p_skey 作 g_tk 签名、以 ptlogin 会话身份提交授权表单，
                    //    302 的 Location 里才有 code（参考开源 qqlogin.js 实测流程）。
                    string authCode = string.Empty;
                    try
                    {
                        var gtkKey = GetCookieValue("qqmusic_key");
                        if (gtkKey.Length == 0) gtkKey = pSkey;
                        if (gtkKey.Length == 0) gtkKey = GetCookieValue("skey");
                        // g_tk 必须用 5381 种子（QQ 官方算法）；ptqrtoken 才是 0 种子
                        var gtk = Hash33(gtkKey.Length > 0 ? gtkKey : string.Empty, 5381u);
                        var uiCookie = GetCookieValue("ui").ToUpperInvariant();

                        var formBody =
                            "response_type=code" +
                            "&client_id=100497308" +
                            "&redirect_uri=" + Uri.EscapeDataString(
                                "https://y.qq.com/portal/wx_redirect.html?login_type=1&surl=https%3A%2F%2Fy.qq.com%2F") +
                            "&scope=get_user_info%2Cget_app_friends" +
                            "&state=state&switch=&from_ptlogin=1&src=1&update_auth=1" +
                            "&openapi=1010_1030&g_tk=" + gtk +
                            "&auth_time=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
                            "&ui=" + Uri.EscapeDataString(uiCookie);

                        // 不自动跟随重定向：302 的 Location 就是 code 载体。
                        // CookieContainer 与 AuthHttp 共享，会话身份一致。
                        using var noRedirect = new HttpClient(new HttpClientHandler
                        {
                            CookieContainer = AuthHandler.CookieContainer,
                            UseCookies = true,
                            AllowAutoRedirect = false
                        }) { Timeout = TimeSpan.FromSeconds(20) };

                        var areq = new HttpRequestMessage(HttpMethod.Post, "https://graph.qq.com/oauth2.0/authorize")
                        {
                            Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded")
                        };
                        areq.Headers.TryAddWithoutValidation("User-Agent", UA);
                        areq.Headers.TryAddWithoutValidation("Origin", "https://graph.qq.com");
                        areq.Headers.TryAddWithoutValidation("Referer", "https://graph.qq.com/");
                        using var aresp = await noRedirect.SendAsync(areq);
                        var loc = aresp.Headers.Location?.ToString() ?? string.Empty;
                        Log($"QQ登录 authorize：HTTP {(int)aresp.StatusCode}，Location={Snip(loc, 200)}");
                        var cm = Regex.Match(loc, @"[?&]code=([^&]+)");
                        if (cm.Success) authCode = Uri.UnescapeDataString(cm.Groups[1].Value);
                    }
                    catch (Exception ex)
                    {
                        LogError("QQ登录 authorize 步骤异常：" + ex.Message);
                    }
                    Log($"QQ登录授权 code 长度={authCode.Length}");

                    var musicKey = string.Empty;
                    long musicId = 0;
                    var nickFromApi = string.Empty;
                    if (authCode.Length > 0)
                    {
                        var lbody = await PostMusicu(new Dictionary<string, object>
                        {
                            ["comm"] = new Dictionary<string, object>
                            {
                                ["g_tk"] = Hash33(pSkey.Length > 0 ? pSkey : string.Empty, 5381u),
                                ["platform"] = "yqq", ["ct"] = 24, ["cv"] = 0
                            },
                            ["req"] = new Dictionary<string, object>
                            {
                                ["module"] = "QQConnectLogin.LoginServer",
                                ["method"] = "QQLogin",
                                ["param"] = new Dictionary<string, object> { ["code"] = authCode }
                            }
                        });
                        Log($"QQLogin 响应：{Snip(lbody, 400)}");

                        try
                        {
                            using var ld = JsonDocument.Parse(lbody);
                            if (ld.RootElement.TryGetProperty("req", out var reqEl) &&
                                reqEl.TryGetProperty("data", out var qdata))
                            {
                                if (qdata.TryGetProperty("musickey", out var mk)) musicKey = mk.GetString() ?? string.Empty;
                                if (qdata.TryGetProperty("musicid", out var mi) && mi.ValueKind == JsonValueKind.Number)
                                    musicId = mi.GetInt64();
                                if (qdata.TryGetProperty("str_musicid", out var smi) &&
                                    smi.ValueKind == JsonValueKind.String &&
                                    long.TryParse(smi.GetString(), out var parsedId))
                                    musicId = parsedId;
                                if (qdata.TryGetProperty("nick", out var nkEl) &&
                                    nkEl.ValueKind == JsonValueKind.String)
                                    nickFromApi = nkEl.GetString() ?? string.Empty;
                            }
                        }
                        catch (JsonException)
                        {
                        }
                    }

                    if (musicId <= 0)
                    {
                        // 兜底：用 cookie 里的 uin / wxuin
                        var uinCookie = AuthHandler.CookieContainer.GetCookies(new Uri("https://y.qq.com"))["uin"]?.Value;
                        if (!string.IsNullOrEmpty(uinCookie) &&
                            long.TryParse(uinCookie.TrimStart('o'), out var cookieId))
                        {
                            musicId = cookieId;
                        }
                    }

                    if (musicKey.Length > 0)
                    {
                        // 域名必须是 .qq.com：.NET CookieContainer 对 "y.qq.com" 只匹配该主机，
                        // 不会随请求发给 u.y.qq.com / c.y.qq.com —— 之前 vkey 500003、歌单 40000 就是因为票据没带上
                        AuthSetCookie(".qq.com", "qm_keyst", musicKey);
                        AuthSetCookie(".qq.com", "qqmusic_key", musicKey);
                        if (musicId > 0) AuthSetCookie(".qq.com", "uin", "o" + musicId);
                        AuthSetCookie(".qq.com", "login_type", "1");
                        AuthSetCookie(".qq.com", "tmeLoginType", "2");
                    }

                    try
                    {
                        var yreq = new HttpRequestMessage(HttpMethod.Get, "https://y.qq.com/");
                        yreq.Headers.TryAddWithoutValidation("User-Agent", UA);
                        await AuthHttp.SendAsync(yreq);
                    }
                    catch
                    {
                    }

                    var cookieDump = DumpCookies("qq.com");
                    var nickname = nickFromApi.Length > 0 ? nickFromApi
                        : (string.IsNullOrEmpty(loginNick) || loginNick.Trim() == "。" ? "已登录" : loginNick);

                    if (musicKey.Length > 0 || musicId > 0)
                    {
                        // 关键：不在后台线程碰 config / 通知 API，交给主线程
                        pendingLogin = new LoginOutcome
                        {
                            Nickname = nickname,
                            MusicId = musicId > 0 ? musicId.ToString(CultureInfo.InvariantCulture) : string.Empty,
                            Cookie = cookieDump,
                            Success = true,
                            Message = $"QQ音乐登录成功：{nickname}"
                        };
                    }
                    else
                    {
                        pendingLogin = new LoginOutcome
                        {
                            Nickname = nickname,
                            MusicId = string.Empty,
                            Cookie = cookieDump,
                            Success = false,
                            Message = $"QQ 扫码成功（{nickname}），但换取音乐登录态失败"
                        };
                        loginDebug = "已拿到 QQ 授权码，但音乐 musickey 未返回，请重新扫码再试一次";
                    }
                }
                else if (qqLoginStage != 3 && qqLoginStage != 4)
                {
                    qqLoginStage = 4;
                    loginMsg = "登录超时，请重试";
                }
            }
            catch (Exception e)
            {
                qqLoginStage = 4;
                loginMsg = "QQ音乐登录失败: " + e.Message;
                LogError("QQ登录异常：" + e);
            }
            finally
            {
                loginRunning = false;
                pendingQrBytes = null;
                loginNick = string.Empty;
            }
        });
    }

    /// <summary>按 cookie 名取值（跨域遍历，取第一个非空）。仅后台线程调用。</summary>
    private static string GetCookieValue(string name)
    {
        try
        {
            foreach (var c in AuthHandler.CookieContainer.GetAllCookies().Cast<Cookie>())
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(c.Value))
                {
                    return c.Value;
                }
            }
        }
        catch
        {
        }
        return string.Empty;
    }

    /// <summary>hash33。二维码 ptqrtoken 用种子 0；g_tk 用种子 5381（QQ 官方算法，实测 100046=g_tk 校验失败）。</summary>
    private static uint Hash33(string s, uint seed = 0)
    {
        uint h = seed;
        foreach (var c in s) h += (h << 5) + c;
        return h & 0x7FFFFFFF;
    }

    // ==================================================================
    // Cookie 工具
    // ==================================================================
    private static string DumpCookies(string domainPart)
    {
        var parts = new List<string>();
        try
        {
            foreach (var c in AuthHandler.CookieContainer.GetAllCookies().Cast<Cookie>())
            {
                if (c.Domain.Contains(domainPart, StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add($"{c.Name}={c.Value}@{c.Domain}");
                }
            }
        }
        catch
        {
        }
        return string.Join("; ", parts);
    }

    private static void RestoreCookies(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return;
        foreach (var item in stored.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = item.Trim();
            var at = seg.LastIndexOf('@');
            var eq = seg.IndexOf('=');
            if (at > 0 && eq > 0 && eq < at)
            {
                var name = seg[..eq];
                var value = seg[(eq + 1)..at];
                var domain = seg[(at + 1)..];
                try { AuthHandler.CookieContainer.Add(new Cookie(name, value, "/", domain)); }
                catch { }
            }
        }
    }

    private static void AuthSetCookie(string domain, string name, string value)
    {
        if (value.Length == 0) return;
        try
        {
            AuthHandler.CookieContainer.Add(new Cookie(name, value, "/", domain)
            {
                Expires = DateTime.Now.AddDays(30),
                Secure = true
            });
        }
        catch (Exception e)
        {
            LogError($"写 cookie 失败 {name}@{domain}：{e.Message}");
        }
    }

    private static string ExtractQueryValue(string url, string key)
    {
        try
        {
            var idx = url.IndexOf('?');
            if (idx < 0 || idx == url.Length - 1) return string.Empty;
            foreach (var part in url[(idx + 1)..].Split('&'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && string.Equals(kv[0], key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(kv[1]);
                }
            }
        }
        catch
        {
        }
        return string.Empty;
    }

    private static void ClearCookies(string domainPart)
    {
        var cc = AuthHandler.CookieContainer;
        try
        {
            foreach (var c in cc.GetAllCookies().Cast<Cookie>()
                         .Where(x => x.Domain.Contains(domainPart, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                try
                {
                    // CookieContainer 没有 Remove，标准做法是塞一个已过期的同名 Cookie 顶掉
                    cc.Add(new Cookie(c.Name, string.Empty, c.Path, c.Domain) { Expired = true });
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    // ==================================================================
    // wininet 备用通道（登录轮询专用）
    // 背景：腾讯 WAF 会拒绝 .NET HttpClient 的 Windows SChannel TLS 指纹
    //       （多客户端实测均 403，而 OpenSSL 栈可通）。
    //       wininet 是 IE 同款网络栈，指纹不同，作为轮询的降级通道。
    //       Cookie 手动拼在请求头里，不污染系统 cookie 存储。
    // ==================================================================
    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr InternetOpenA(string agent, int accessType, string? proxy, string? proxyBypass, int flags);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr InternetOpenUrlA(IntPtr hInternet, string url, string? headers, int headersLength, int flags, IntPtr context);

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetCloseHandle(IntPtr hInternet);

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetReadFile(IntPtr hFile, byte[] buffer, int toRead, out int read);

    private const int INTERNET_OPEN_TYPE_PRECONFIG = 0;
    private const int INTERNET_FLAG_RELOAD = unchecked((int)0x80000000);
    private const int INTERNET_FLAG_NO_CACHE_WRITE = 0x04000000;
    private const int INTERNET_FLAG_PRAGMA_NOCACHE = 0x00000100;

    /// <summary>用 wininet 发 GET。失败/无响应返回空串。仅后台线程调用。</summary>
    private static string WinInetGet(string url, string headers)
    {
        var hOpen = IntPtr.Zero;
        var hUrl = IntPtr.Zero;
        try
        {
            hOpen = InternetOpenA("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", INTERNET_OPEN_TYPE_PRECONFIG, null, null, 0);
            if (hOpen == IntPtr.Zero) return string.Empty;

            hUrl = InternetOpenUrlA(hOpen, url, headers, headers.Length,
                INTERNET_FLAG_RELOAD | INTERNET_FLAG_NO_CACHE_WRITE | INTERNET_FLAG_PRAGMA_NOCACHE, IntPtr.Zero);
            if (hUrl == IntPtr.Zero) return string.Empty;

            var buf = new byte[8192];
            using var ms = new MemoryStream();
            while (ms.Length < 1_048_576)
            {
                var ok = InternetReadFile(hUrl, buf, buf.Length, out var read);
                if (!ok || read <= 0) break;
                ms.Write(buf, 0, read);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            try { if (hUrl != IntPtr.Zero) InternetCloseHandle(hUrl); } catch { }
            try { if (hOpen != IntPtr.Zero) InternetCloseHandle(hOpen); } catch { }
        }
    }

    // ==================================================================
    // 系统媒体键（无队列时的兜底控制）
    // ==================================================================
    private static void SendMediaKey(ushort vk)
    {
        var inputs = new[] { MakeKeyInput(vk, false), MakeKeyInput(vk, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT MakeKeyInput(ushort vk, bool keyUp)
    {
        return new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyUp ? 0x0002u : 0u,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ==================================================================
    // 后台结果消费（一律在 UI / 主线程执行）
    // ==================================================================
    private void ConsumePending()
    {
        ConsumeQrTexture();

        var search = pendingSearchResults;
        if (search != null)
        {
            pendingSearchResults = null;
            searchResults.Clear();
            searchResults.AddRange(search);
        }

        var lyrics = pendingLyrics;
        if (lyrics != null)
        {
            pendingLyrics = null;
            currentLyrics.Clear();
            currentLyrics.AddRange(lyrics);
            if (currentLyrics.Count == 0) lyricError = "未获取到歌词";
        }

        // ---- 登录结果：主线程写 config + 播报 + 拉取数据 ----
        var login = pendingLogin;
        if (login != null)
        {
            pendingLogin = null;
            if (login.Success) config.QQNickname = login.Nickname;   // 失败时不写昵称，避免显示「已登录」却无登录态
            config.QQCookie = login.Cookie;
            if (login.MusicId.Length > 0) config.QQMusicId = login.MusicId;
            loginMsg = login.Message;
            loginDirty = true;

            Log("登录结果：" + login.Message + " / musicid=" + config.QQMusicId);

            if (login.Success || !string.IsNullOrEmpty(config.QQMusicId))
            {
                TriggerLoadFavorites();
                TriggerLoadPlaylists();
            }
        }

        var favs = pendingFavTracks;
        if (favs != null)
        {
            pendingFavTracks = null;
            favTracks.Clear();
            favTracks.AddRange(favs);
            likedIds.Clear();
            foreach (var t in favTracks)
            {
                if (t.SongId > 0) likedIds.Add(t.SongId);
            }
            QueueChat($"我喜欢的音乐已加载（{favTracks.Count}首）");
        }

        var recs = pendingRecommendTracks;
        if (recs != null)
        {
            pendingRecommendTracks = null;
            recommendTracks.Clear();
            recommendTracks.AddRange(recs);
        }

        var pls = pendingPlaylists;
        if (pls != null)
        {
            pendingPlaylists = null;
            playlists.Clear();
            playlists.AddRange(pls);
        }

        var toggle = pendingFavToggle;
        if (toggle != null)
        {
            pendingFavToggle = null;
            if (!toggle.Success)
            {
                // 回滚本地乐观更新
                if (toggle.Liked) likedIds.Remove(toggle.SongId);
                else likedIds.Add(toggle.SongId);
            }
            QueueChat(toggle.Message);
        }

        var plCache = pendingPlaylistTrackCache;
        if (plCache != null)
        {
            // 歌单曲目加载完成：进缓存（切页浏览用），不再自动整单播放
            pendingPlaylistTrackCache = null;
            playlistTrackCache[plCache.DirId] = plCache.Tracks;
            playlistTrackLoading.Remove(plCache.DirId);
        }

        var url = pendingPlayUrl;
        if (url != null)
        {
            pendingPlayUrl = null;
            EnginePlay(url);
        }
    }

    // ==================================================================
    // 二维码贴图
    // ==================================================================
    private void EnsureQrTexture()
    {
        var bytes = pendingQrBytes;
        if (bytes == null || bytes.Length < 8) return;
        var key = $"{bytes.Length}|{bytes[0]:X2}{bytes[1]:X2}{bytes[2]:X2}{bytes[3]:X2}";
        if (qrTextureKey == key || qrRequestedKey == key) return;

        qrRequestedKey = key;
        _ = Task.Run(async () =>
        {
            try
            {
                var wrap = await DalamudServices.TextureProvider.CreateFromImageAsync(bytes);
                if (disposed)
                {
                    try { wrap.Dispose(); } catch { }
                    return;
                }
                pendingQrTex = wrap;
                pendingQrTexKey = key;
            }
            catch
            {
            }
        });
    }

    private void ConsumeQrTexture()
    {
        var tex = pendingQrTex;
        if (tex == null) return;
        pendingQrTex = null;

        if (disposed)
        {
            try { tex.Dispose(); } catch { }
            pendingQrTexKey = null;
            return;
        }

        // 换新二维码前先释放旧贴图
        try { if (qrTexture is IDisposable old) old.Dispose(); } catch { }
        qrTexture = tex;
        qrTextureKey = pendingQrTexKey ?? string.Empty;
        pendingQrTexKey = null;
    }

    // ==================================================================
    // 数据模型
    // ==================================================================
    [Serializable]
    public sealed class Track
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;         // songmid
        public string MediaMid { get; set; } = string.Empty;   // file.media_mid
        public long SongId { get; set; }                       // 数字 ID（收藏用）
        public bool IsVip { get; set; }                        // pay.payplay == 1
        public string CoverUrl { get; set; } = string.Empty;
    }

    private sealed record LrcLine(double Time, string Text);
}
