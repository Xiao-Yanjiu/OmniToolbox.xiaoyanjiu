using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
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

namespace OmniToolbox.LocalModules
{

public sealed class MusicPlayer : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "游戏内音乐播放器",
        Description = "QQ音乐与网易云音乐：独立账号、曲库、队列和播放通道。",
        Category = ModuleCategory.Interface,
        Author = "小烟酒"
    };

    [Serializable]
    public sealed class MusicPlayerConfig
    {
        public bool WindowVisible { get; set; } = true;
        public bool WindowLocked { get; set; }
        public bool AnchorToHead { get; set; }
        public float HeadOffsetY { get; set; } = 2.3f;
        public float OverlayX { get; set; } = 200f;
        public float OverlayY { get; set; } = 200f;
        public float OverlayScale { get; set; } = 1.0f;
        public bool DynamicColor { get; set; } = true;
        public float OverlayOpacity { get; set; } = 0.86f;
        public float CoverOpacity { get; set; } = 1.0f;
        public bool ShowLyricInHead { get; set; } = true;
        public int ContentTab { get; set; }
        public int Volume { get; set; } = 80;
        public float LyricFontScale { get; set; } = 1.0f;
        public bool ShowMeta { get; set; } = true;
        public List<Track> Queue { get; set; } = new();
        public string QQCookie { get; set; } = string.Empty;
        public string QQNickname { get; set; } = string.Empty;
        public string QQMusicId { get; set; } = string.Empty;
        public int SelectedProvider { get; set; }
        // A separate config object owns the NetEase account, queue and UI settings.
        public MusicPlayerConfig? Netease { get; set; }
    }

    private MusicPlayerConfig config = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly HttpClientHandler AuthHandler = new()
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true,
        AllowAutoRedirect = true
    };

    private static readonly HttpClient AuthHttp = new(AuthHandler) { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly HttpClientHandler Ag1Handler = new()
    {
        CookieContainer = new CookieContainer(),
        UseCookies = false,
        AllowAutoRedirect = true
    };

    private static readonly HttpClient Ag1Http = new(Ag1Handler) { Timeout = TimeSpan.FromSeconds(20) };

    private const string UA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    private string QQ => PlatformName;
    private const int FavDirId = 201;

    private string searchInput = string.Empty;
    private readonly List<Track> searchResults = new();
    private bool searching;
    private string searchError = string.Empty;
    private volatile List<Track>? pendingSearchResults;

    private sealed record PlaylistInfo(string Id, string Name, int Count);

    private readonly List<Track> favTracks = new();
    private readonly HashSet<long> likedIds = new();
    private bool loadingFav;
    private string favError = string.Empty;
    private volatile List<Track>? pendingFavTracks;

    private readonly List<Track> recommendTracks = new();
    private bool loadingRecommend;
    private string recommendError = string.Empty;
    private volatile List<Track>? pendingRecommendTracks;
    private int recommendTopId = 26;
    private string recommendTopName = "热歌榜";
    private bool recommendInited;

    private readonly List<PlaylistInfo> playlists = new();
    private bool loadingPlaylists;
    private string playlistError = string.Empty;
    private volatile List<PlaylistInfo>? pendingPlaylists;

    private int favViewIdx;
    private bool favListsInited;
    private readonly Dictionary<string, List<Track>> playlistTrackCache = new();
    private readonly HashSet<string> playlistTrackLoading = new();
    private sealed class PlTracksResult { public string DirId = ""; public List<Track> Tracks = new(); }
    private volatile PlTracksResult? pendingPlaylistTrackCache;

    private sealed class FavToggleResult { public long SongId; public string Mid = ""; public bool Liked; public bool Success; public string Message = ""; }
    private volatile FavToggleResult? pendingFavToggle;

    private Track? currentTrack;
    private int currentQueueIndex = -1;
    private string? pendingPlayUrl;
    private bool resolvingPlay;
    private volatile string playError = string.Empty;
    private DateTime lastAutoNextTime = DateTime.MinValue;

    private readonly List<LrcLine> currentLyrics = new();
    private bool loadingLyric;
    private string lyricError = string.Empty;
    private int lyricCurrentIndex = -1;
    private volatile List<LrcLine>? pendingLyrics;

    private sealed class CoverBytes { public string Url = ""; public byte[] Bytes = Array.Empty<byte>(); }
    private sealed class TexResult { public string Url = ""; public IDalamudTextureWrap? Tex; }

    private volatile CoverBytes? pendingCoverBytes;
    private volatile TexResult? pendingCoverTex;
    private volatile TexResult? pendingBlurTex;
    private readonly Dictionary<string, IDalamudTextureWrap?> coverCache = new();
    private readonly HashSet<string> coverLoading = new();
    private readonly Queue<string> coverOrder = new();

    private readonly Dictionary<string, IDalamudTextureWrap?> blurCache = new();
    private readonly HashSet<string> blurLoading = new();

    private const string GlassGradientPngB64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAABACAYAAADbER1AAAAAhUlEQVR42hXEgWZCAQBA0b44kUSSSGISyUQSmUhmJJFEMpFEEkliEkkkiSSm3rncE3oFhYL/8cQDd9xwxQVnnHDEAXvs8IctNlhjhSUWmGOGKSYY4xcjDDFAHz100UEbP/hGC0008IU6aqiigjI+UUIRBeTxgRyyyCCNFJJIII4Yoogg/AYGeuzhjJiGwwAAAABJRU5ErkJggg==";
    private IDalamudTextureWrap? glassTexture;
    private volatile IDalamudTextureWrap? pendingGlassTex;
    private bool glassTexRequested;

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
    private bool loginDirty;

    private readonly object chatLock = new();
    private readonly List<string> pendingChats = new();

    private string searchDebug = string.Empty;

    private bool disposed;

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

    private void QueueChat(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        lock (chatLock) pendingChats.Add(message);
    }

    protected override void OnEnable()
    {
        disposed = false;
        workCancellation.Dispose(); workCancellation = new();
        EnsurePlatforms();
        neteasePlayer!.disposed = false;
        neteasePlayer.workCancellation.Dispose(); neteasePlayer.workCancellation = new();
        neteasePlayer.netCookie = config.Netease!.QQCookie;
        RestoreCookies(config.QQCookie);
        config.ContentTab = Math.Clamp(config.ContentTab, 0, 3);
        DalamudServices.PluginInterface.UiBuilder.Draw += Draw;

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

    private void SafeShutdown()
    {
        disposed = true;
        System.Threading.Interlocked.Increment(ref playGeneration);
        System.Threading.Interlocked.Increment(ref lyricGeneration);
        workCancellation.Cancel(); playCancellation?.Cancel();
        System.Threading.Interlocked.Increment(ref netSession);
        neteasePlayer?.SafeShutdown();
        netQr = null;
        loginRunning = false;
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

    public override bool DrawSettings()
    {
        try
        {
            EnsurePlatforms();
            return SelectedPlayer.DrawSettingsCore();
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

        ImGui.TextWrapped("宏命令：/omni MusicPlayer 切换悬浮窗；/omnimusic show 显示，/omnimusic hide 隐藏。");

        ImGui.Spacing();

        if (ImGui.Button(config.WindowVisible ? "关闭悬浮窗" : "显示悬浮窗"))
        {
            config.WindowVisible = !config.WindowVisible;
            if (config.WindowVisible) { overlaySuspended = false; drawErrorStreak = 0; }
            changed = true;
        }

        if (loginDirty || panelDirty)
        {
            loginDirty = false;
            panelDirty = false;
            changed = true;
        }

        return changed;
    }

    private bool mainPanelOpen;
    private bool settingsPanelOpen;
    private bool panelDirty;

    private void DrawMainPanel()
    {
        if (!mainPanelOpen) return;

        var io = ImGui.GetIO();
        ImGui.SetNextWindowSize(new Vector2(560f, 640f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(
            new Vector2(Math.Max(4f, (io.DisplaySize.X - 560f) * 0.5f),
                        Math.Max(4f, (io.DisplaySize.Y - 640f) * 0.5f)),
            ImGuiCond.FirstUseEver);

        using var scope = new ComicStyleScope();
        var open = true;
        var began = false;
        try
        {
            began = ImGui.Begin(PlatformName + "播放器###OmniMusicMain", ref open);
            if (began)
            {
                ClampCurrentWindow(ImGui.GetWindowSize());
                var changed = false;

                changed |= DrawLoginSection();
                ImGui.Separator();

                var tab = config.ContentTab;

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

                if (changed) panelDirty = true;
            }
        }
        catch (Exception e)
        {
            try { LogError("曲库窗口绘制异常：" + e); } catch { }
        }
        finally
        {
            if (began) { try { ImGui.End(); } catch { } }
        }
        if (!open) mainPanelOpen = false;
    }

    private void DrawSettingsWindow()
    {
        if (!settingsPanelOpen) return;

        var io = ImGui.GetIO();
        ImGui.SetNextWindowSize(new Vector2(460f, 580f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(
            new Vector2(Math.Max(4f, (io.DisplaySize.X - 460f) * 0.5f),
                        Math.Max(4f, (io.DisplaySize.Y - 580f) * 0.5f)),
            ImGuiCond.FirstUseEver);

        using var scope = new ComicStyleScope();
        var open = true;
        var began = false;
        try
        {
            began = ImGui.Begin("音乐播放器设置###OmniMusicSettings", ref open);
            if (began)
            {
                ClampCurrentWindow(ImGui.GetWindowSize());
                var changed = false;

                var anchor = config.AnchorToHead;
                if (ImGui.Checkbox("固定到角色头顶（跟随角色移动）", ref anchor))
                {
                    if (config.AnchorToHead && !anchor && lastOverlaySize.X > 1f)
                    {
                        config.OverlayX = lastOverlayPos.X;
                        config.OverlayY = lastOverlayPos.Y;
                    }
                    config.AnchorToHead = anchor;
                    if (anchor) smoothAnchorInit = false;
                    changed = true;
                    Log($"悬浮窗：取消/启用固定头顶 -> {config.AnchorToHead}");
                }

                if (ImGui.Button("把悬浮窗拉回屏幕中央##ovRecenter"))
                {
                    var w = lastOverlaySize.X > 1f ? lastOverlaySize.X : 340f * config.OverlayScale;
                    var h = lastOverlaySize.Y > 1f ? lastOverlaySize.Y : 148f * config.OverlayScale;
                    config.AnchorToHead = false;
                    smoothAnchorInit = false;
                    config.OverlayX = Math.Max(4f, (io.DisplaySize.X - w) * 0.5f);
                    config.OverlayY = Math.Max(4f, (io.DisplaySize.Y - h) * 0.5f);
                    config.WindowVisible = true;
                    overlaySuspended = false;
                    drawErrorStreak = 0;
                    overlayForcePos = true;
                    changed = true;
                    Log($"悬浮窗：位置重置到 ({config.OverlayX:F0},{config.OverlayY:F0})，屏幕 {io.DisplaySize.X:F0}x{io.DisplaySize.Y:F0}");
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("换了分辨率/窗口模式后如果不在顺手的位置，点这里把它摆回屏幕中央。\n（悬浮窗现在拖不出游戏窗口——贴到边界就停住，不会再丢。）");

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

                var headLyric = config.ShowLyricInHead;
                if (ImGui.Checkbox("悬浮窗显示当前歌词", ref headLyric)) { config.ShowLyricInHead = headLyric; changed = true; }

                if (changed) panelDirty = true;
            }
        }
        catch (Exception e)
        {
            try { LogError("设置窗口绘制异常：" + e); } catch { }
        }
        finally
        {
            if (began) { try { ImGui.End(); } catch { } }
        }
        if (!open) settingsPanelOpen = false;
    }

    public override bool ResetSettings()
    {
        SafeShutdown();
        config = new MusicPlayerConfig();
        neteasePlayer = null; platformReady = false;
        workCancellation.Dispose(); workCancellation = new(); disposed = false;
        EnsurePlatforms();
        return true;
    }

    public override bool TryHandleCommand(string arguments)
    {
        if (!string.IsNullOrWhiteSpace(arguments)) return false;
        return ToggleWindow();
    }

    public override bool TryHandleCommand(string command, string arguments)
    {
        if (!string.Equals(command?.Trim(), "MusicPlayer", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(arguments)) return false;
        return ToggleWindow();
    }

    private bool ToggleWindow()
    {
        var root = Root;
        root.EnsurePlatforms();
        root.ApplyPlatformSwitch();
        var player = root.SelectedPlayer;
        player.config.WindowVisible = !player.config.WindowVisible;
        if (player.config.WindowVisible)
        {
            player.overlaySuspended = false;
            player.drawErrorStreak = 0;
        }
        player.panelDirty = root.panelDirty = true;
        player.QueueChat($"{player.PlatformName}悬浮窗已{(player.config.WindowVisible ? "打开" : "关闭")}");
        return true;
    }

    private bool DrawLoginSection()
    {
        var changed = false;
        DrawPlatformPicker();
        if (isNetease) return DrawNeteaseLogin();

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

        if (!string.IsNullOrEmpty(searchDebug)) ImGui.TextDisabled(searchDebug);
        if (!string.IsNullOrEmpty(searchError))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), searchError);
        }

        ImGui.BeginChild("##musicResults", new Vector2(0, 230f), true);
        try
        {
            for (var i = 0; i < searchResults.Count; i++)
            {
                var song = searchResults[i];
                DrawSongRow($"sr{i}", song, searchResults, i, ref changed);
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        return changed;
    }

    private bool DrawRecommendTab()
    {
        var changed = false;

        if (!recommendInited && !loadingRecommend && recommendTracks.Count == 0)
        {
            recommendInited = true;
            TriggerLoadRecommend(recommendTopId, recommendTopName);
        }

        var tops = new (int Id, string Name)[]
        {
            (26, "热歌榜"), (4, "新歌榜"), (62, "飙升榜"), (27, "经典老歌"), (72, "情歌榜")
        };
        if (isNetease) tops = new[] { (3778678, "热歌榜"), (3779629, "新歌榜"), (19723756, "飙升榜"), (2884035, "原创榜") };
        float gap = 10f;
        float available = ImGui.GetContentRegionAvail().X - 5f;
        float sidebarWidth = Math.Max(96f, ImGui.CalcTextSize("经典老歌").X + 30f);
        float panelHeight = Math.Max(280f, ImGui.GetTextLineHeightWithSpacing() * 18f);
        float listWidth = Math.Max(1f, available - sidebarWidth - gap);

        void PanelFrame(Vector2 size)
        {
            var start = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(start + new Vector2(3f, 4f), start + size + new Vector2(3f, 4f), 0x24000000, 8f);
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        try
        {
            PanelFrame(new Vector2(sidebarWidth, panelHeight));
            bool sidebar = ImGui.BeginChild("##recCategories", new Vector2(sidebarWidth, panelHeight), true);
            try
            {
                if (sidebar)
                {
                    ImGui.Dummy(new Vector2(0f, 3f));
                    for (var i = 0; i < tops.Length; i++)
                    {
                        if (DrawPillButton($"##recTop{tops[i].Id}", tops[i].Name, recommendTopId == tops[i].Id,
                            Math.Max(1f, ImGui.GetContentRegionAvail().X - 3f), true))
                        {
                            recommendTopId = tops[i].Id;
                            recommendTopName = tops[i].Name;
                            TriggerLoadRecommend(recommendTopId, recommendTopName);
                        }
                        ImGui.Dummy(new Vector2(0f, 5f));
                    }
                }
            }
            finally { ImGui.EndChild(); }

            ImGui.SameLine(0f, gap);
            PanelFrame(new Vector2(listWidth, panelHeight));
            bool content = ImGui.BeginChild("##recContent", new Vector2(listWidth, panelHeight), true);
            try
            {
                if (content)
                {
                    if (ImGui.Button("刷新榜单##recReload") && !loadingRecommend)
                        TriggerLoadRecommend(recommendTopId, recommendTopName);
                    ImGui.SameLine();
                    if (recommendTracks.Count > 0 && ImGui.Button("整榜播放##recPlayAll"))
                    {
                        SetQueueAndPlay(recommendTracks, 0);
                        QueueChat($"已加载{recommendTopName}（{recommendTracks.Count}首），开始播放");
                        changed = true;
                    }
                    if (loadingRecommend) ImGui.TextDisabled("加载中...");
                    if (!string.IsNullOrEmpty(recommendError))
                        ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), recommendError);
                    ImGui.Separator();

                    bool list = ImGui.BeginChild("##recList", new Vector2(0f, 0f), false);
                    try
                    {
                        if (list)
                        {
                            for (var i = 0; i < recommendTracks.Count; i++)
                                DrawSongRow($"rec{i}", recommendTracks[i], recommendTracks, i, ref changed);
                            if (recommendTracks.Count == 0)
                                ImGui.TextWrapped("选择左侧榜单加载歌曲");
                        }
                    }
                    finally { ImGui.EndChild(); }
                }
            }
            finally { ImGui.EndChild(); }
        }
        finally { ImGui.PopStyleVar(); }
        ImGui.Dummy(new Vector2(0f, 5f));

        return changed;
    }

    private bool DrawFavoriteTab()
    {
        var changed = false;

        if (string.IsNullOrEmpty(config.QQMusicId))
        {
            ImGui.TextWrapped("收藏内容需要登录 " + PlatformName + "。请先在上方扫码登录，登录成功后这里会自动加载。");
            if (!loginRunning)
            {
                ImGui.NewLine();
                if (ImGui.Button("立即扫码登录##favGoLogin")) StartQQLogin();
            }
            return false;
        }

        if (!favListsInited && !loadingPlaylists && playlists.Count == 0)
        {
            favListsInited = true;
            TriggerLoadPlaylists();
        }

        favViewIdx = Math.Clamp(favViewIdx, 0, playlists.Count);
        if (isNetease && favViewIdx == 0 && playlists.Count > 0)
        {
            favViewIdx = 1;
            EnsurePlaylistTracks(playlists[0]);
        }
        float gap = 10f;
        float sidebarWidth = Math.Max(124f, ImGui.CalcTextSize("我喜欢的音乐").X + 26f);
        float available = ImGui.GetContentRegionAvail().X - 5f;
        float contentWidth = Math.Max(1f, available - sidebarWidth - gap);
        float panelHeight = Math.Max(320f, ImGui.GetTextLineHeightWithSpacing() * 20f);
        void PanelShadow(Vector2 size)
        {
            var start = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(start + new Vector2(3f, 4f), start + size + new Vector2(3f, 4f), 0x24000000, 8f);
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        try
        {
            PanelShadow(new Vector2(sidebarWidth, panelHeight));
            bool sidebar = ImGui.BeginChild("##favoriteSidebar", new Vector2(sidebarWidth, panelHeight), true);
            try
            {
                if (sidebar)
                {
                    if (ImGui.Button("刷新歌单##plReload") && !loadingPlaylists) TriggerLoadPlaylists();
                    if (loadingPlaylists) ImGui.TextDisabled("加载中...");
                    ImGui.Separator();
                    bool categories = ImGui.BeginChild("##favoriteCategories", new Vector2(0f, 0f), false);
                    try
                    {
                        if (categories)
                        {
                            float width = Math.Max(1f, ImGui.GetContentRegionAvail().X - 3f);
                            if (!isNetease)
                            {
                                if (DrawPillButton("##favViewFav", "我喜欢的音乐", favViewIdx == 0, width, true))
                                    favViewIdx = 0;
                                ImGui.Dummy(new Vector2(0f, 5f));
                            }
                            for (var i = 0; i < playlists.Count; i++)
                            {
                                var pl = playlists[i];
                                if (DrawPillButton($"##favViewPl{i}", Truncate(pl.Name, 7), favViewIdx == i + 1, width, true))
                                {
                                    favViewIdx = i + 1;
                                    EnsurePlaylistTracks(pl);
                                }
                                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{pl.Name}（{pl.Count}首）");
                                ImGui.Dummy(new Vector2(0f, 5f));
                            }
                        }
                    }
                    finally { ImGui.EndChild(); }
                }
            }
            finally { ImGui.EndChild(); }

            ImGui.SameLine(0f, gap);
            PanelShadow(new Vector2(contentWidth, panelHeight));
            bool content = ImGui.BeginChild("##favoriteContent", new Vector2(contentWidth, panelHeight), true);
            try
            {
                if (content && isNetease && playlists.Count == 0)
                {
                    ImGui.TextWrapped(loadingPlaylists ? "正在加载歌单..." : "暂无歌单，请点击左侧刷新歌单");
                    if (!string.IsNullOrEmpty(playlistError))
                        ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), playlistError);
                }
                else if (content)
                {
                    List<Track>? tracks;
                    bool loading;
                    string error;
                    if (favViewIdx == 0)
                    {
                        tracks = favTracks;
                        loading = loadingFav;
                        error = favError;
                        if (ImGui.Button("刷新歌曲##favReload") && !loadingFav) TriggerLoadFavorites();
                    }
                    else
                    {
                        var pl = playlists[favViewIdx - 1];
                        playlistTrackCache.TryGetValue(pl.Id, out tracks);
                        loading = playlistTrackLoading.Contains(pl.Id);
                        error = playlistError;
                        if (ImGui.Button("刷新歌曲##plTracksReload") && !loading)
                        {
                            playlistTrackCache.Remove(pl.Id);
                            EnsurePlaylistTracks(pl);
                        }
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("整单播放##favoritePlayAll") && tracks is { Count: > 0 })
                    {
                        SetQueueAndPlay(tracks, 0);
                        changed = true;
                    }
                    if (loading) { ImGui.SameLine(); ImGui.TextDisabled("加载中..."); }
                    if (!string.IsNullOrEmpty(error)) ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), error);
                    ImGui.Separator();
                    bool list = ImGui.BeginChild("##favoriteTracks", new Vector2(0f, 0f), false);
                    try
                    {
                        if (list)
                        {
                            if (tracks != null)
                                for (var i = 0; i < tracks.Count; i++)
                                    DrawSongRow($"favorite{i}", tracks[i], tracks, i, ref changed);
                            if (tracks == null || tracks.Count == 0)
                                ImGui.TextWrapped(loading ? "正在加载歌曲..." : "暂无歌曲，点击刷新歌曲重试");
                        }
                    }
                    finally { ImGui.EndChild(); }
                }
            }
            finally { ImGui.EndChild(); }
        }
        finally { ImGui.PopStyleVar(); }
        ImGui.Dummy(new Vector2(0f, 5f));

        return changed;
    }

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

    private void DrawSongRow(string idScope, Track song, List<Track> source, int index, ref bool changed)
    {
        ImGui.PushID(idScope);
        var doubleClicked = false;
        try
        {
            var isCurrent = (currentTrack != null && song.Id.Length > 0 && currentTrack.Id == song.Id)
                            || ReferenceEquals(song, currentTrack);
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
            ImGui.PopID();
        }

        if (doubleClicked)
        {
            Log($"双击播放：{song.Title}（列表 {source.Count} 首，index={index}）");
            if (source.Count > 0) SetQueueAndPlay(source, index);
            else EnqueueAndPlay(song);
            changed = true;
        }
    }

    private bool DrawPillButton(string id, string label, bool selected, float width, bool framed = false)
    {
        var height = ImGui.GetFrameHeight() * (framed ? 1.15f : 0.86f);
        ImGui.InvisibleButton(id, new Vector2(width, height));
        var clicked = ImGui.IsItemClicked();
        var hovered = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        float rounding = framed ? 7f : height * 0.5f;
        if (framed)
        {
            dl.AddRectFilled(min + new Vector2(2f, 2f), max + new Vector2(2f, 2f), 0x30000000, rounding);
            dl.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.Button), rounding);
            dl.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border), rounding);
        }
        if (selected)
        {
            dl.AddRectFilled(min, max, 0xFF6FA864, rounding);
            dl.AddRect(min, max, 0xCC8FD684, rounding);
        }
        else if (hovered)
        {
            dl.AddRectFilled(min, max, 0x33FFFFFF, rounding);
        }

        var ts = ImGui.CalcTextSize(label);
        var textCol = selected ? 0xFFF2FFF0 : hovered ? 0xFFF0F0E8 : ImGui.GetColorU32(ImGuiCol.Text);
        dl.PushClipRect(min + new Vector2(5f, 0f), max - new Vector2(5f, 0f), true);
        dl.AddText(new Vector2((min.X + max.X - ts.X) * 0.5f, (min.Y + max.Y - ts.Y) * 0.5f), textCol, label);
        dl.PopClipRect();
        return clicked;
    }

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

        if (ImGui.SmallButton(liked ? "已喜欢" : "收藏"))
        {
            Log($"点击收藏：{song.Title}（id={song.SongId}，like={!liked}）");
            ToggleFavorite(song, !liked);
            changed = true;
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(liked ? "点击取消喜欢" : "加入「我喜欢的音乐」");
    }

    private void DrawNowPlayingSection(ref bool changed)
    {
        if (currentTrack == null) return;

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

    }

    private void DrawInstance(bool visible)
    {
        if (disposed) return;

        try { FlushChats(); }
        catch (Exception e) { try { LogError("FlushChats 异常：" + e); } catch { } }

        try { ConsumePending(); }
        catch (Exception e) { try { LogError("ConsumePending 异常：" + e); } catch { } }

        try { PollPlayback(); }
        catch (Exception e) { try { LogError("PollPlayback 异常：" + e); } catch { } }

        try { EnsureCover(); }
        catch (Exception e) { try { LogError("EnsureCover 异常：" + e); } catch { } }

        try { UpdateLyricIndex(); }
        catch (Exception e) { try { LogError("UpdateLyricIndex 异常：" + e); } catch { } }

        if (!visible) return;
        if (config.WindowVisible && !overlaySuspended)
        {
            try
            {
                DrawOverlay();
            }
            catch (Exception e)
            {
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

        try { DrawMainPanel(); }
        catch (Exception e) { try { LogError("曲库窗口异常：" + e); } catch { } }

        try { DrawSettingsWindow(); }
        catch (Exception e) { try { LogError("设置窗口异常：" + e); } catch { } }
    }

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

    private Vector2 smoothAnchorPos;
    private bool smoothAnchorInit;
    private bool overlaySuspended;
    private int drawErrorStreak;
    private DateTime lastDrawErrorTime = DateTime.MinValue;

    private Vector2 lastOverlayPos;
    private Vector2 lastOverlaySize;
    private bool overlayForcePos;

    private const string BuildTag = "v16.0";
    private bool overlayDiagLogged;

    private static Vector2 ClampToGameViewport(Vector2 pos, Vector2 size)
    {
        var vp = ImGui.GetMainViewport();
        var workPos = vp.WorkPos;
        var workSize = vp.WorkSize;
        var fitSize = Vector2.Clamp(size, Vector2.One, Vector2.Max(Vector2.One, workSize));
        return Vector2.Clamp(pos, workPos, Vector2.Max(workPos, workPos + workSize - fitSize));
    }

    private static void ClampCurrentWindow(Vector2 size)
    {
        try
        {
            var cur = ImGui.GetWindowPos();
            Vector2 want;
            try
            {
                want = ClampToGameViewport(cur, size);
            }
            catch
            {
                var io = ImGui.GetIO();
                want = Vector2.Clamp(cur, Vector2.Zero,
                    new Vector2(Math.Max(0f, io.DisplaySize.X - size.X), Math.Max(0f, io.DisplaySize.Y - size.Y)));
            }
            if (Math.Abs(want.X - cur.X) <= 0.01f && Math.Abs(want.Y - cur.Y) <= 0.01f) return;

            ImGui.SetWindowPos(want, ImGuiCond.Always);
            if ((DateTime.UtcNow - lastClampLogTime).TotalSeconds >= 3.0)
            {
                lastClampLogTime = DateTime.UtcNow;
                Log($"位置钳制到游戏区域内：({cur.X:F0},{cur.Y:F0}) -> ({want.X:F0},{want.Y:F0})，窗口 {size.X:F0}x{size.Y:F0}");
            }
        }
        catch (Exception e)
        {
            try { LogError("窗口位置钳制异常：" + e.Message); } catch { }
        }
    }

    private static DateTime lastClampLogTime = DateTime.MinValue;

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
                    | ImGuiWindowFlags.NoBackground
                    | ImGuiWindowFlags.AlwaysUseWindowPadding;
        if (config.WindowLocked || config.AnchorToHead) flags |= ImGuiWindowFlags.NoMove;

        var anchored = config.AnchorToHead;
        if (anchored)
        {
            if (!TryGetHeadScreenPos(out var target)) return;

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
            }

            var io = ImGui.GetIO();
            var x = Math.Clamp(smoothAnchorPos.X - overlayW / 2f, 4f, Math.Max(4f, io.DisplaySize.X - overlayW - 4f));
            var y = Math.Clamp(smoothAnchorPos.Y - overlayH, 4f, Math.Max(4f, io.DisplaySize.Y - overlayH - 4f));
            ImGui.SetNextWindowPos(new Vector2((float)Math.Round(x), (float)Math.Round(y)));
            ImGui.SetNextWindowSize(new Vector2(overlayW, overlayH));
        }
        else
        {
            ImGui.SetNextWindowPos(new Vector2(config.OverlayX, config.OverlayY),
                overlayForcePos ? ImGuiCond.Always : ImGuiCond.FirstUseEver);
            overlayForcePos = false;
            ImGui.SetNextWindowSize(new Vector2(overlayW, overlayH), ImGuiCond.Always);
        }

        var radius = 24f * scale;

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, radius);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f * scale, 12f * scale));

        var began = false;
        try
        {
            began = ImGui.Begin("###OmniMusicOverlay", flags);
            if (began)
            {
                if (!anchored) ClampCurrentWindow(new Vector2(overlayW, overlayH));
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
        var dl = ImGui.GetForegroundDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();

        if (!overlayDiagLogged)
        {
            overlayDiagLogged = true;
            Log($"[构建]{BuildTag} scale={scale:F2}(config {config.OverlayScale:F2}) radius={radius:F1} " +
                $"window={(int)(max.X - min.X)}x{(int)(max.Y - min.Y)} opacity={config.OverlayOpacity:F2}");
        }

        const float expand = 10f;
        var vmin = min - new Vector2(expand, expand);
        var vmax = max + new Vector2(expand, expand);
        DrawOverlayBackdrop(dl, vmin, vmax, radius, currentTrack == null);

        var utilR = 11f * scale;
        var utilCx = min.X + 8f * scale + utilR;
        var utilCy = min.Y + 10f * scale + utilR;
        var utilStep = utilR * 2 + 6f * scale;
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

        var winR = 9f * scale;
        var winCy = min.Y + 12f * scale;
        var setCx = max.X - 14f * scale - winR;
        var libCx = setCx - (winR * 2 + 6f * scale);
        if (OverlayCircleButton(dl, "##ovLibBtn", "曲",
                new Vector2(libCx - winR, winCy - winR), new Vector2(libCx + winR, winCy + winR),
                mainPanelOpen, mainPanelOpen ? "关闭曲库窗口" : "打开曲库窗口（登录 / 搜索 / 榜单 / 收藏 / 播放列表）"))
        {
            mainPanelOpen = !mainPanelOpen;
            Log($"悬浮窗：曲库窗口 -> {mainPanelOpen}");
        }
        if (OverlayCircleButton(dl, "##ovSetBtn", "设",
                new Vector2(setCx - winR, winCy - winR), new Vector2(setCx + winR, winCy + winR),
                settingsPanelOpen, settingsPanelOpen ? "关闭设置窗口" : "打开设置窗口"))
        {
            settingsPanelOpen = !settingsPanelOpen;
            Log($"悬浮窗：设置窗口 -> {settingsPanelOpen}");
        }

        DrawOverlayVolume(dl, min, max, scale, utilCx, lockCy + utilR);

        var H = max.Y - min.Y;
        var pad = 14f * scale;
        var coverSize = 104f * scale;
        var coverMin = new Vector2(max.X - pad - coverSize, min.Y + (H - coverSize) * 0.5f);
        var coverMax = coverMin + new Vector2(coverSize, coverSize);
        var utilW = utilR * 2 + 8f * scale;
        var textLeft = min.X + pad + utilW;
        var textRight = coverMin.X - 10f * scale;

        if (currentTrack == null)
        {
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
                Col(new Vector3(1f, 1f, 1f), 0.55f), "点右上角「曲」打开曲库窗口");
        }
        else
        {
            DrawOverlayContent(dl, min, max, scale, radius, textLeft, textRight, coverMin, coverMax);
        }

        lastOverlayPos = min;
        lastOverlaySize = max - min;
        if (!anchored && !config.WindowLocked)
        {
            if (Math.Abs(min.X - config.OverlayX) > 0.5f || Math.Abs(min.Y - config.OverlayY) > 0.5f)
            {
                config.OverlayX = min.X;
                config.OverlayY = min.Y;
            }
        }
    }

    private void DrawOverlayContent(ImDrawListPtr dl, Vector2 min, Vector2 max, float scale, float radius,
        float textLeft, float textRight, Vector2 coverMin, Vector2 coverMax)
    {
        var track = currentTrack!;

        var cover = GetCurrentCoverTexture();
        var coverAlpha = CoverAlpha();
        if (coverAlpha <= 0.004f)
        {
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

        var titleY = min.Y + 11f * scale;
        dl.AddText(font, fsz * 1.34f * scale, new Vector2(textLeft, titleY),
            Col(new Vector3(1f, 1f, 1f), 0.98f),
            Truncate(track.Title, Math.Max(4, (int)(tw / (9.8f * scale)))));

        dl.AddText(font, fsz, new Vector2(textLeft, titleY + 30f * scale),
            Col(new Vector3(1f, 1f, 1f), 0.55f),
            Truncate(track.Artist, Math.Max(4, (int)(tw / (7.1f * scale)))));

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

        var barH = Math.Max(3f, 3.5f * scale);
        var barY = max.Y - 13f * scale - barH;
        var barW = textRight - textLeft;
        var frac = engineDuration > 0 ? Math.Clamp((float)(enginePosition / engineDuration), 0f, 1f) : 0f;
        var barMin = new Vector2(textLeft, barY);
        var barMax = new Vector2(textRight, barY + barH);

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

    private enum OvIcon { Prev, Play, Pause, Next }

    private bool OverlayVectorButton(ImDrawListPtr dl, string id, OvIcon icon, Vector2 minV, Vector2 maxV,
        bool interactive = true)
    {
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
        var s = (maxV.Y - minV.Y) * 0.30f;
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
                var tw = s * 0.92f;
                var step = tw * 0.72f;
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

    private bool OverlayCircleButton(ImDrawListPtr dl, string id, string glyph, Vector2 minV, Vector2 maxV,
        bool active, string tip, bool interactive = true)
    {
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

    private void DrawOverlayVolume(ImDrawListPtr dl, Vector2 min, Vector2 max, float scale, float cx, float topY)
    {
        var top = topY + 6f * scale;
        var bottom = max.Y - 20f * scale;
        var trackH = bottom - top;
        if (trackH < 24f * scale) return;

        var trackW = Math.Max(2.5f, 3.5f * scale);
        var hitW = Math.Max(12f, Math.Max(14f * scale, trackW * 3f));
        var knobR = Math.Max(2.5f, 4.5f * scale);

        var hitMin = new Vector2(cx - hitW * 0.5f, top - 4f * scale);
        var hitMax = new Vector2(cx + hitW * 0.5f, bottom + 4f * scale);
        ImGui.SetCursorScreenPos(hitMin);
        ImGui.InvisibleButton("##ovVol", hitMax - hitMin);
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();

        if (held)
        {
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
        FillRounded(dl, new Vector2(cx - trackW * 0.5f, top), new Vector2(cx + trackW * 0.5f, bottom),
            Col(new Vector3(1f, 1f, 1f), 0.16f), trackW);
        if (fracNow > 0.003f)
            FillRounded(dl, new Vector2(cx - trackW * 0.5f, fillTop), new Vector2(cx + trackW * 0.5f, bottom),
                Col(new Vector3(1f, 1f, 1f), held ? 0.95f : hovered ? 0.85f : 0.70f), trackW);
        dl.AddCircleFilled(new Vector2(cx, fillTop), knobR,
            Col(new Vector3(1f, 1f, 1f), hovered || held ? 0.98f : 0.85f));
    }

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

    private void DrawOverlayBackdrop(ImDrawListPtr dl, Vector2 min, Vector2 max, float radius, bool idle)
    {
        var opacity = Math.Clamp(config.OverlayOpacity, 0f, 1f);
        if (opacity <= 0.001f) return;
        var blur = GetCurrentCoverBlurTexture();
        EnsureGlassTexture();
        var grad = glassTexture;

        if (IsTexValid(blur))
        {
            DrawRoundedImage(dl, blur!.Handle, min, max,
                Col(new Vector3(1f, 1f, 1f), opacity * 0.95f), radius,
                new Vector2(0.30f, 0.30f), new Vector2(0.70f, 0.70f));
            DrawRoundedImage(dl, blur!.Handle, min, max,
                Col(new Vector3(1f, 1f, 1f), opacity * 0.40f), radius,
                new Vector2(0.43f, 0.43f), new Vector2(0.57f, 0.57f));
            if (IsTexValid(grad))
                DrawRoundedImage(dl, grad!.Handle, min, max,
                    Col(new Vector3(0.02f, 0.02f, 0.05f), 0.42f * opacity), radius,
                    Vector2.Zero, Vector2.One);
        }
        else if (IsTexValid(grad))
        {
            var (cTop, cBottom) = CurrentSongPalette();
            var mid = Lerp(cTop, cBottom, 0.35f);
            var a = idle ? opacity * 0.55f : opacity;
            DrawRoundedImage(dl, grad!.Handle, min, max, Col(mid, a), radius, Vector2.Zero, Vector2.One);
            DrawRoundedImage(dl, grad!.Handle, min, max, Col(cBottom, a * 0.55f), radius,
                new Vector2(0f, 1f), new Vector2(1f, 0f));
        }
        else
        {
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

    private static uint Col(Vector3 c, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, Math.Clamp(a, 0f, 1f)));

    private float CoverAlpha() => Math.Clamp(config.CoverOpacity, 0f, 1f);

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
            var dl0 = x - min.X;
            var dr0 = max.X - x;
            if (dl0 >= r && dr0 > r)
            {
                Emit(x, min.Y, max.X - r, max.Y, 1f);
                x = Math.Max(max.X - r, x + 0.01f);
                continue;
            }

            var x1 = Math.Min(x + 1f, max.X);
            var inset = Math.Max(CornerInset(dl0, r), CornerInset(max.X - x1, r));
            if (inset <= 0.001f)
            {
                Emit(x, min.Y, x1, max.Y, 1f);
            }
            else
            {
                var topF = min.Y + inset;
                var botF = max.Y - inset;
                var nT = MathF.Floor(topF);
                var nB = MathF.Floor(botF);
                if (nB - nT < 1f)
                {
                    Emit(x, nT, x1, nT + 1f, botF - topF);
                }
                else
                {
                    Emit(x, nT, x1, nT + 1f, 1f - (topF - nT));
                    Emit(x, nT + 1f, x1, nB, 1f);
                    Emit(x, nB, x1, nB + 1f, botF - nB);
                }
            }
            x = x1;
        }
    }

    private static float CornerInset(float d, float r)
    {
        if (r <= 0f || d >= r) return 0f;
        var t = r - Math.Max(0f, d);
        return r - MathF.Sqrt(Math.Max(0f, r * r - t * t));
    }

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
        var h = (uint)StableHash(key) / (float)int.MaxValue;
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

    private bool mciActive;
    private volatile string? pendingMciFile;
    private DateTime lastPollTime = DateTime.MinValue;

    private volatile bool enginePlaying;
    private double enginePosition;
    private double engineDuration;

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
        int ticket = playGeneration;
        var cancel = playCancellation?.Token ?? workCancellation.Token;
        playError = string.Empty;
        MciClose();
        enginePlaying = false;
        enginePosition = 0;
        engineDuration = 0;

        if (string.IsNullOrEmpty(url)) return;

        var ext = isNetease || url.Contains(".mp3", StringComparison.OrdinalIgnoreCase) ? ".mp3" : ".m4a";
        _ = Task.Run(async () =>
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "OmniMusic", isNetease ? "Netease" : "QQ");
                Directory.CreateDirectory(dir);
                CleanupOldTracks(dir);
                var path = Path.Combine(dir, $"track_{Guid.NewGuid():N}{ext}");
                using var dlReq = new HttpRequestMessage(HttpMethod.Get, url);
                dlReq.Headers.Referrer = new Uri(isNetease ? "https://music.163.com/" : "https://y.qq.com/portal/player.html");
                dlReq.Headers.TryAddWithoutValidation("User-Agent", UA);
                using var dlResp = await Http.SendAsync(dlReq, cancel);
                dlResp.EnsureSuccessStatusCode();
                var bytes = await dlResp.Content.ReadAsByteArrayAsync(cancel);
                if (disposed || ticket != playGeneration) return;
                if (isNetease) path = DecodeNeteaseMp3(bytes, Path.ChangeExtension(path, ".wav"), cancel);
                else await File.WriteAllBytesAsync(path, bytes, cancel);
                if (disposed || ticket != playGeneration)
                {
                    try { File.Delete(path); } catch { }
                    return;
                }
                netCompletions.Enqueue(() => {
                    if (!disposed && ticket == playGeneration) pendingMciFile = path;
                    else { try { File.Delete(path); } catch { } }
                });
            }
            catch (Exception e)
            {
                if (!disposed && ticket == playGeneration && e is not OperationCanceledException) playError = "音频下载失败: " + e.Message;
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
                mciActive = true; activeAudioFile = mciFile;
                ApplyVolume();
            }
            else
            {
                playError = "本地音频播放失败（MCI 无法打开该文件）";
                try { File.Delete(mciFile); } catch { }
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
        if (activeAudioFile != null) { try { File.Delete(activeAudioFile); } catch { } activeAudioFile = null; }
    }

    private void PlayTrack(Track t)
    {
        int ticket = System.Threading.Interlocked.Increment(ref playGeneration);
        playCancellation?.Cancel(); playCancellation?.Dispose();
        playCancellation = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(workCancellation.Token);
        var token = playCancellation.Token;
        EngineStop();
        pendingPlayUrl = pendingMciFile = null;
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
                netOperationToken.Value = token;
                token.ThrowIfCancellationRequested();
                var (url, isMp3) = await ResolvePlayUrl(t);
                if (disposed || ticket != playGeneration) return;
                if (url != null && !isMp3 &&
                    string.IsNullOrEmpty(config.QQMusicId))
                {
                    url = null;
                    playError = "未登录只能获取 m4a 音源（本机播放器不支持），请扫码登录后播放";
                }
                netCompletions.Enqueue(() => { if (!disposed && ticket == playGeneration) pendingPlayUrl = url; });
                if (url == null && playError.Length == 0)
                {
                    var loggedIn = !string.IsNullOrEmpty(config.QQMusicId);
                    playError = !loggedIn
                        ? "VIP 歌曲：请先扫码登录（绿钻账号可播放）"
                        : "获取播放地址失败：该歌曲版权受限或接口异常（详情见日志）";
                }
            }
            catch (Exception e)
            {
                if (ticket == playGeneration) pendingPlayUrl = null;
                if (!disposed && ticket == playGeneration && e is not OperationCanceledException) playError = "解析播放地址失败: " + e.Message;
            }
            finally
            {
                if (ticket == playGeneration) resolvingPlay = false;
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
            // No global media keys: the other platform must remain independent.
            return;
        }
        PlayQueueIndex((currentQueueIndex + 1) % config.Queue.Count);
    }

    private void PlayPrev()
    {
        if (config.Queue.Count == 0)
        {
            // An empty queue never controls another player.
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

    private void TriggerSearch(string keyword)
    {
        if (isNetease)
        {
            keyword = keyword.Trim();
            if (keyword.Length == 0 || searching) return;
            searching = true; searchError = ""; searchResults.Clear();
            NetJob(async session => { var tracks = await NetSearch(keyword); NetPost(session, () => { pendingSearchResults = tracks; searching = false; }); }, error => { searching = false; searchError = error; });
            return;
        }
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
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return ((int)resp.StatusCode, Encoding.UTF8.GetString(bytes));
    }

    private void TriggerLoadRecommend(int topId, string topName)
    {
        if (isNetease) { recommendTopName = topName; NetRecommend(topId); return; }
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

            var mediaMid = FirstString(s, "strMediaMid", "media_mid", "mediaMid");
            if (mediaMid.Length == 0 && s.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
            {
                mediaMid = FirstString(file, "media_mid", "mediaMid");
            }
            if (mediaMid.Length == 0) mediaMid = mid;

            var albumMid = string.Empty;
            if (s.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
            {
                albumMid = FirstString(album, "mid", "pmid");
                if (albumMid.Length == 0) albumMid = FirstString(album, "albumMid");
            }
            if (albumMid.Length == 0) albumMid = FirstString(s, "albummid", "albumMid");

            var songId = FirstLong(s, "id", "songId", "song_id", "songid");

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

    private async Task<(string? Url, bool IsMp3)> ResolvePlayUrl(Track t)
    {
        if (isNetease) return await NetUrl(t);
        if (t.MediaMid.Length == 0 || t.MediaMid == t.Id)
        {
            await EnsureMediaMid(t);
        }

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

        var candidates = new List<string>
        {
            "https://isure.stream.qqmusic.qq.com/",
            "http://isure.stream.qqmusic.qq.com/",
            "http://dl.stream.qqmusic.qq.com/"
        };
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

    private void TriggerLyric(Track song)
    {
        int ticket = System.Threading.Interlocked.Increment(ref lyricGeneration);
        loadingLyric = true;
        lyricError = string.Empty;
        currentLyrics.Clear();
        lyricCurrentIndex = -1;

        _ = Task.Run(async () =>
        {
            try
            {
                var lrc = await GetQQLyric(song.Id);
                var parsed = ParseLrc(lrc);
                netCompletions.Enqueue(() => { if (!disposed && ticket == lyricGeneration) pendingLyrics = parsed; });

            }
            catch (Exception e)
            {
                LogError($"歌词获取失败：{song.Title} -> {e}");
                if (!disposed && ticket == lyricGeneration) lyricError = $"歌词获取失败: {e.Message}";
            }
            finally
            {
                if (ticket == lyricGeneration) loadingLyric = false;
            }
        });
    }

    private async Task<string> GetQQLyric(string songMid)
    {
        if (isNetease) return await NetLyric(songMid);
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

    private void TriggerCoverBlur(string coverUrl)
    {
        if (blurCache.ContainsKey(coverUrl) || blurLoading.Contains(coverUrl)) return;
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

    private IDalamudTextureWrap? GetCurrentCoverBlurTexture()
    {
        if (currentTrack == null || string.IsNullOrEmpty(currentTrack.CoverUrl)) return null;
        return blurCache.TryGetValue(currentTrack.CoverUrl, out var tex) ? tex : null;
    }

    private void TriggerLoadFavorites(bool silent = false)
    {
        if (isNetease) { NetFavorites(); return; }
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
        if (isNetease) { NetPlaylists(); return; }
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
                            if (dirId == 205 || dirId == 206) continue;
                            if (dirId == FavDirId) continue;
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

    private void EnsurePlaylistTracks(PlaylistInfo pl)
    {
        if (isNetease) { NetPlaylistTracks(pl); return; }
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
                    mids = await FetchLikedMids(dir, uin);
                }
                else
                {
                    mids = await FetchLikedMids(long.TryParse(dirId, out var d2) ? d2 : 0, uin);
                    if (mids.Count == 0)
                    {
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
        if (depth > 12) return false;
        if (el.ValueKind != JsonValueKind.Object) return false;

        foreach (var name in new[] { "v_songInfo", "songlist", "songs", "songList", "v_songinfo", "list" })
        {
            if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
            {
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

    private static readonly byte[] Ag1RequestKey =
    {
        0xbd, 0x30, 0x5f, 0x10, 0xd0, 0xff, 0x74, 0xb6,
        0xef, 0x54, 0xda, 0xb8, 0x35, 0xb5, 0xe1, 0xcf
    };

    private static readonly byte[] Ag1ResponseKey =
    {
        0x7a, 0x3f, 0x8c, 0x1d, 0x5e, 0x9b, 0x2f, 0x0a, 0x6c, 0x4d, 0x7e,
        0x8b, 0x1f, 0x3a, 0x5c, 0x9d, 0x0e, 0x2b, 0x6f, 0x4a, 0x81
    };

    private static readonly int[] Ag1SignPart1 = { 23, 14, 6, 36, 16, 7, 19 };

    private static readonly int[] Ag1SignPart2 = { 16, 1, 32, 12, 19, 27, 8, 5 };

    private static readonly byte[] Ag1SignScramble =
    {
        89, 39, 179, 150, 218, 82, 58, 252, 177, 52, 186, 123, 120, 64, 242, 133, 143, 161, 121, 179
    };

    private static string Ag1Encrypt(string payload)
    {
        var plain = Encoding.UTF8.GetBytes(payload);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(Ag1RequestKey, 16))
        {
            gcm.Encrypt(nonce, plain, cipher, tag, Array.Empty<byte>());
        }

        var buf = new byte[nonce.Length + cipher.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, buf, 0, nonce.Length);
        Buffer.BlockCopy(cipher, 0, buf, nonce.Length, cipher.Length);
        Buffer.BlockCopy(tag, 0, buf, nonce.Length + cipher.Length, tag.Length);
        return Convert.ToBase64String(buf);
    }

    private static string Ag1Decrypt(byte[] data)
    {
        var buf = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            buf[i] = (byte)(data[i] ^ Ag1ResponseKey[i % Ag1ResponseKey.Length]);
        }
        return Encoding.UTF8.GetString(buf);
    }

    private static string Ag1Sign(string payload)
    {
        byte[] hash;
        using (var sha1 = SHA1.Create())
        {
            hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(payload));
        }

        var hex = new char[40];
        const string digits = "0123456789ABCDEF";
        for (var i = 0; i < 20; i++)
        {
            hex[i * 2] = digits[hash[i] >> 4];
            hex[i * 2 + 1] = digits[hash[i] & 0x0F];
        }

        var mixed = new byte[Ag1SignScramble.Length];
        for (var i = 0; i < Ag1SignScramble.Length; i++)
        {
            var b = Convert.ToByte(new string(new[] { hex[i * 2], hex[i * 2 + 1] }), 16);
            mixed[i] = (byte)(Ag1SignScramble[i] ^ b);
        }

        var sb = new StringBuilder(64);
        sb.Append("zzc");
        foreach (var i in Ag1SignPart1) sb.Append(hex[i]);
        foreach (var ch in Convert.ToBase64String(mixed))
        {
            if (ch == '\\' || ch == '/' || ch == '+' || ch == '=') continue;
            sb.Append(ch);
        }
        foreach (var i in Ag1SignPart2) sb.Append(hex[i]);
        return sb.ToString().ToLowerInvariant();
    }

    private static string MusicCookieHeader()
    {
        var parts = new List<string>();
        try
        {
            foreach (var c in AuthHandler.CookieContainer.GetAllCookies().Cast<Cookie>())
            {
                if (string.IsNullOrEmpty(c.Name)) continue;
                var dom = c.Domain ?? string.Empty;
                if (!string.Equals(dom, "qq.com", StringComparison.OrdinalIgnoreCase) &&
                    !dom.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase)) continue;
                parts.Add(c.Name + "=" + c.Value);
            }
        }
        catch (Exception e)
        {
            LogError("导出 QQ 音乐 cookie 失败：" + e.Message);
        }
        return string.Join("; ", parts);
    }

    private async Task<string> Ag1Post(Dictionary<string, object> payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var body = Ag1Encrypt(json);
        var url = "https://u6.y.qq.com/cgi-bin/musics.fcg?_=" +
                  DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
                  "&encoding=ag-1&sign=" + Uri.EscapeDataString(Ag1Sign(json));

        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("User-Agent", UA);
        req.Headers.Referrer = new Uri("https://y.qq.com/portal/player.html");
        req.Headers.TryAddWithoutValidation("Cookie", MusicCookieHeader());

        var resp = await Ag1Http.SendAsync(req);
        var raw = await resp.Content.ReadAsByteArrayAsync();
        if (raw.Length == 0) return "{}";
        if (raw.Length > 8_000_000)
        {
            LogError($"musics 响应异常庞大（{raw.Length} 字节），已拒收防止解析崩溃");
            return "{}";
        }
        return Ag1Decrypt(raw);
    }

    private async Task<(long Code, JsonElement Data)> MusicuWriteAsync(string module, string method, object param)
    {
        var uin = string.IsNullOrEmpty(config.QQMusicId) ? "0" : config.QQMusicId.Trim();

        var payload = new Dictionary<string, object>
        {
            ["comm"] = new Dictionary<string, object>
            {
                ["ct"] = 24, ["cv"] = 4747474, ["v"] = 1300, ["uin"] = uin,
                ["g_tk"] = MusicGtk(), ["g_tk_new_20200303"] = MusicGtk(),
                ["format"] = "json", ["platform"] = "yqq.json", ["needNewCode"] = 1,
            },
            ["req_1"] = new Dictionary<string, object>
            {
                ["module"] = module,
                ["method"] = method,
                ["param"] = param,
            },
        };

        var text = string.Empty;
        try
        {
            text = await Ag1Post(payload);
            Log("musics 加密写请求[" + module + "." + method + "]：" + Snip(text, 220));
        }
        catch (Exception e)
        {
            LogError("加密通道写请求异常：" + e.Message);
        }

        var probe = text.TrimStart();
        if (probe.Length == 0 || probe[0] != '{')
        {
            LogError(probe.Length == 0 ? "加密通道无响应，回退明文 musicu" : "加密通道返回体不是 JSON，回退明文 musicu");
            text = await PostMusicu(payload);
            Log("musicu 明文写请求[" + module + "." + method + "]：" + Snip(text, 220));
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        long topCode = -1;
        if (root.TryGetProperty("code", out var tc) && tc.ValueKind == JsonValueKind.Number
            && tc.TryGetInt64(out var l0))
            topCode = l0;
        if (topCode != 0) return (topCode, default);
        if (!root.TryGetProperty("req_1", out var r1)) return (-2, default);

        long code = -1;
        if (r1.TryGetProperty("code", out var c1) && c1.ValueKind == JsonValueKind.Number
            && c1.TryGetInt64(out var l1))
            code = l1;
        var data = r1.TryGetProperty("data", out var d1) ? d1.Clone() : default;
        return (code, data);
    }

    private void ToggleFavorite(Track song, bool like)
    {
        if (isNetease) { NetLike(song, like); return; }
        if (song.SongId <= 0) return;
        if (string.IsNullOrEmpty(config.QQMusicId))
        {
            QueueChat("请先登录 QQ音乐再收藏歌曲");
            return;
        }

        var songId = song.SongId;
        var mid = song.Id;

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
                var (code, data) = await MusicuWriteAsync(
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
                if (like) likedIds.Remove(songId); else likedIds.Add(songId);
            }

            pendingFavToggle = result;
        });
    }

    private void StartQQLogin()
    {
        if (isNetease) { NetLogin(); return; }
        loginRunning = true;
        qqLoginStage = 1;
        loginMsg = "正在获取QQ登录二维码...";
        loginDebug = string.Empty;
        pendingQrBytes = null;
        qrTextureKey = string.Empty;
        qrRequestedKey = string.Empty;

        _ = Task.Run(async () =>
        {
            try
            {
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
                            bytes[2] == 0x4E && bytes[3] == 0x47)
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
                var useWinInet = false;
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

                if (redirectUrl != null)
                {
                    loginMsg = "扫码成功，正在换取音乐登录态…";

                    var creq = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
                    creq.Headers.TryAddWithoutValidation("User-Agent", UA);
                    var cresp = await AuthHttp.SendAsync(creq);
                    var finalUrl = cresp.RequestMessage?.RequestUri?.ToString() ?? redirectUrl;
                    Log($"QQ登录 check_sig 完成：{Snip(finalUrl, 220)}");

                    var pSkey = GetCookieValue("p_skey");
                    Log($"QQ登录 p_skey 长度={pSkey.Length}");

                    string authCode = string.Empty;
                    try
                    {
                        var gtkKey = GetCookieValue("qqmusic_key");
                        if (gtkKey.Length == 0) gtkKey = pSkey;
                        if (gtkKey.Length == 0) gtkKey = GetCookieValue("skey");
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
                        var uinCookie = AuthHandler.CookieContainer.GetCookies(new Uri("https://y.qq.com"))["uin"]?.Value;
                        if (!string.IsNullOrEmpty(uinCookie) &&
                            long.TryParse(uinCookie.TrimStart('o'), out var cookieId))
                        {
                            musicId = cookieId;
                        }
                    }

                    if (musicKey.Length > 0)
                    {
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

    private static uint Hash33(string s, uint seed = 0)
    {
        uint h = seed;
        foreach (var c in s) h += (h << 5) + c;
        return h & 0x7FFFFFFF;
    }

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

    private void ConsumePending()
    {
        while (netCompletions.TryDequeue(out var completion)) completion();
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

        var login = pendingLogin;
        if (login != null)
        {
            pendingLogin = null;
            if (login.Success) config.QQNickname = login.Nickname;
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
                if (toggle.Liked) likedIds.Remove(toggle.SongId);
                else likedIds.Add(toggle.SongId);
            }
            QueueChat(toggle.Message);
        }

        var plCache = pendingPlaylistTrackCache;
        if (plCache != null)
        {
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

    private void EnsureQrTexture()
    {
        var bytes = pendingQrBytes;
        if (bytes == null || bytes.Length < 8) return;
        var key = Convert.ToHexString(SHA256.HashData(bytes));
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

        try { if (qrTexture is IDisposable old) old.Dispose(); } catch { }
        qrTexture = tex;
        qrTextureKey = pendingQrTexKey ?? string.Empty;
        pendingQrTexKey = null;
    }

    [Serializable]
    public sealed class Track
    {
        public string Source { get; set; } = "qq";
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string MediaMid { get; set; } = string.Empty;
        public long SongId { get; set; }
        public bool IsVip { get; set; }
        public string CoverUrl { get; set; } = string.Empty;
    }

    // Each platform owns a complete player instance. Only the root is registered as an Omni module.
    private readonly bool isNetease;
    private readonly MusicPlayer? owner;
    private MusicPlayer? neteasePlayer;
    private int requestedProvider = -1;
    private MusicPlayer Root => owner ?? this;
    private string PlatformName => isNetease ? "网易云音乐" : "QQ音乐";
    private string MciAlias => isNetease ? "omniMusicPlayerNetease" : "omniMusicPlayerQQ";
    private int playGeneration;
    private string? activeAudioFile;
    private System.Threading.CancellationTokenSource? playCancellation;
    private int lyricGeneration;
    private System.Threading.CancellationTokenSource workCancellation = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> netCompletions = new();
    private int netSession;
    private string netCookie = "";
    private readonly string netDeviceId = Guid.NewGuid().ToString("N");
    private OmniMusicQr.QrCode? netQr;
    private bool netFavoriteWriting;
    private int netRecommendGeneration;
    private bool platformReady;

    public MusicPlayer() { }
    private MusicPlayer(MusicPlayer parent, MusicPlayerConfig settings)
    {
        isNetease = true;
        owner = parent;
        config = settings;
        netCookie = settings.QQCookie;
        recommendTopId = 3778678;
        platformReady = true;
    }

    private MusicPlayer SelectedPlayer => config.SelectedProvider == 1 ? neteasePlayer ?? this : this;

    private void EnsurePlatforms()
    {
        if (owner != null) return;
        config.SelectedProvider = Math.Clamp(config.SelectedProvider, 0, 1);
        config.Netease ??= new MusicPlayerConfig();
        neteasePlayer ??= new MusicPlayer(this, config.Netease);
        // The host may have restored the config object after construction.
        neteasePlayer.config = config.Netease;
        if (!platformReady)
        {
            platformReady = true;
            neteasePlayer.netCookie = config.Netease.QQCookie;
        }
    }

    private void ApplyPlatformSwitch()
    {
        if (requestedProvider >= 0)
        {
            var previous = SelectedPlayer;
            config.SelectedProvider = requestedProvider;
            requestedProvider = -1;
            var next = SelectedPlayer;
            if (!ReferenceEquals(previous, next))
            {
                next.mainPanelOpen = previous.mainPanelOpen;
                next.settingsPanelOpen = previous.settingsPanelOpen;
                previous.mainPanelOpen = previous.settingsPanelOpen = false;
                // Switching the page never closes or pauses either audio device.
            }
            panelDirty = true;
        }
    }

    private void DrawPlatformPicker()
    {
        ImGui.TextUnformatted("账号登录");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        if (ImGui.BeginCombo("##musicPlatform", PlatformName))
        {
            try
            {
                if (ImGui.Selectable("QQ音乐", !isNetease)) Root.requestedProvider = 0;
                if (ImGui.Selectable("网易云音乐", isNetease)) Root.requestedProvider = 1;
            }
            finally { ImGui.EndCombo(); }
        }
    }

    private void Draw()
    {
        if (disposed) return;
        EnsurePlatforms();
        ApplyPlatformSwitch();
        DrawInstance(config.SelectedProvider == 0);
        neteasePlayer!.DrawInstance(config.SelectedProvider == 1);
        config.Netease = neteasePlayer.config;
        if (panelDirty || loginDirty || neteasePlayer.panelDirty || neteasePlayer.loginDirty)
        {
            try
            {
                var save = typeof(ModuleBase).GetProperty("SaveHostConfig", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(this) as Action;
                if (save == null) return;
                save();
                panelDirty = loginDirty = neteasePlayer.panelDirty = neteasePlayer.loginDirty = false;
            }
            catch (Exception ex) { LogError("保存音乐设置失败：" + ex.Message); }
        }
    }

    private bool DrawNeteaseLogin()
    {
        bool changed = false;
        if (!string.IsNullOrEmpty(config.QQMusicId))
        {
            ImGui.TextColored(new Vector4(.4f, .85f, .45f, 1f), "已登录：" + config.QQNickname);
            ImGui.SameLine();
            if (ImGui.Button("刷新数据##netRefresh")) { NetFavorites(); NetPlaylists(); }
            ImGui.SameLine();
            if (ImGui.Button("退出登录##netLogout"))
            {
                ResetNetSession();
                netCookie = config.QQCookie = config.QQNickname = config.QQMusicId = "";
                favTracks.Clear(); likedIds.Clear(); playlists.Clear(); playlistTrackCache.Clear();
                playlistTrackLoading.Clear(); favViewIdx = 0; favListsInited = false;
                changed = true;
            }
        }
        else
        {
            ImGui.TextDisabled("未登录"); ImGui.SameLine();
            if (ImGui.Button(loginRunning ? "重新生成二维码##netLogin" : "网易云音乐扫码登录##netLogin")) NetLogin();
        }
        var qr = netQr;
        if (qr != null)
        {
            var pos = ImGui.GetCursorScreenPos();
            float unit = Math.Max(3f, (float)Math.Floor(200f / (qr.Size + 8)));
            float size = unit * (qr.Size + 8);
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(pos, pos + new Vector2(size), 0xFFFFFFFF);
            for (int y = 0; y < qr.Size; y++) for (int x = 0; x < qr.Size; x++)
                if (qr.GetModule(x, y))
                {
                    var p = pos + new Vector2((x + 4) * unit, (y + 4) * unit);
                    dl.AddRectFilled(p, p + new Vector2(unit), 0xFF000000);
                }
            ImGui.Dummy(new Vector2(size));
        }
        if (loginRunning && ImGui.Button("取消扫码##netCancel")) { ResetNetSession(); loginMsg = "已取消扫码"; }
        if (loginMsg.Length > 0) ImGui.TextWrapped(loginMsg);
        return changed;
    }

    private void ResetNetSession()
    {
        System.Threading.Interlocked.Increment(ref netSession);
        workCancellation.Cancel(); workCancellation.Dispose(); workCancellation = new();
        while (netCompletions.TryDequeue(out _)) { }
        netQr = null; loginRunning = false; loadingFav = loadingPlaylists = searching = loadingRecommend = netFavoriteWriting = false;
        pendingLogin = null; pendingFavTracks = null; pendingPlaylists = null; pendingSearchResults = null; pendingRecommendTracks = null;
    }

    private void NetPost(int session, Action action)
    {
        if (!disposed && session == System.Threading.Volatile.Read(ref netSession))
            netCompletions.Enqueue(() => { if (!disposed && session == netSession) action(); });
    }

    private void NetJob(Func<int, Task> job, Action<string> failed)
    {
        int session = netSession;
        var token = workCancellation.Token;
        _ = Task.Run(async () =>
        {
            try { netOperationToken.Value = token; token.ThrowIfCancellationRequested(); await job(session); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { NetPost(session, () => failed(ex.Message)); }
        });
    }

    // Decode owned MP3 bytes off the game thread. The playback device only sees PCM WAV.
    internal static string DecodeNeteaseMp3(byte[] bytes, string path, System.Threading.CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using var input = new MemoryStream(bytes, false);
            using var decoder = new OmniMusicMpeg.MpegFile(input);
            int channels = decoder.Channels, rate = decoder.SampleRate;
            if (channels is < 1 or > 2 || rate is < 8000 or > 96000) throw new InvalidDataException("不支持的 MP3 音频参数");
            using var output = File.Create(path);
            using var writer = new BinaryWriter(output, Encoding.ASCII, true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(0);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)channels); writer.Write(rate);
            writer.Write(rate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(0);
            var samples = new float[8192]; var pcm = new byte[samples.Length * 2];
            int total = 0;
            for (;;)
            {
                token.ThrowIfCancellationRequested();
                int count = decoder.ReadSamples(samples, 0, samples.Length);
                if (count <= 0) break;
                if ((long)total + count * 2 > 512 * 1024 * 1024) throw new InvalidDataException("音频过长，超过本地解码上限");
                for (int i = 0; i < count; i++)
                {
                    float sample = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1f, 1f) : 0;
                    short value = (short)(sample * short.MaxValue);
                    pcm[i * 2] = (byte)value; pcm[i * 2 + 1] = (byte)(value >> 8);
                }
                writer.Write(pcm, 0, count * 2); total += count * 2;
            }
            if (total == 0) throw new InvalidDataException("未解码出有效音频");
            output.Position = 4; writer.Write(total + 36); output.Position = 40; writer.Write(total);
            writer.Flush();
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    private sealed record NetReply(JsonElement Body, string Cookie);
    private static readonly HttpClient NetHttp = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };

    private readonly HttpClient netHttp = NetHttp;
    private static readonly System.Threading.AsyncLocal<System.Threading.CancellationToken> netOperationToken = new();

    private static Dictionary<string, string> NetCookies(string cookie)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string item in cookie.Split(';'))
        {
            int split = item.IndexOf('=');
            if (split > 0) result[item[..split].Trim()] = item[(split + 1)..].Trim();
        }
        return result;
    }

    private async Task<NetReply> NetApi(string path, Dictionary<string, object>? payload = null, string? cookie = null, bool requireSuccess = true)
    {
        var token = netOperationToken.Value.CanBeCanceled ? netOperationToken.Value : workCancellation.Token;
        var jar = NetCookies(cookie ?? netCookie);
        jar.TryAdd("os", "pc"); jar.TryAdd("appver", "3.0.18");
        var data = payload == null ? new Dictionary<string, object>() : new(payload);
        data["csrf_token"] = jar.GetValueOrDefault("__csrf", "");
        bool web = path is "w/nuser/account/get" or "radio/like";
        data["e_r"] = false;
        Dictionary<string, string> encrypted;
        string cookieHeader;
        if (web)
        {
            encrypted = NetEncrypt(JsonSerializer.Serialize(data));
            cookieHeader = string.Join("; ", jar.Select(x => x.Key + "=" + x.Value));
        }
        else
        {
            var header = new Dictionary<string, string> {
                ["os"] = "pc", ["osver"] = "Microsoft-Windows-10", ["appver"] = "3.1.29.205117",
                ["deviceId"] = netDeviceId, ["versioncode"] = "140", ["channel"] = "netease",
                ["buildver"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ["requestId"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + RandomNumberGenerator.GetInt32(1000).ToString("0000"),
                ["__csrf"] = jar.GetValueOrDefault("__csrf", "") };
            foreach (string name in new[] { "MUSIC_U", "MUSIC_A", "NMTID" }) if (jar.TryGetValue(name, out var value)) header[name] = value;
            data["header"] = header;
            encrypted = NetEapi("/api/" + path.TrimStart('/'), JsonSerializer.Serialize(data));
            cookieHeader = string.Join("; ", header.Select(x => x.Key + "=" + Uri.EscapeDataString(x.Value)));
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, (web ? "https://music.163.com/weapi/" : "https://interface.music.163.com/eapi/") + path.TrimStart('/'));
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", web ? UA : "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 Chrome/91.0.4472.164 NeteaseMusicDesktop/3.1.29.205117");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Content = new FormUrlEncodedContent(encrypted);
        using var response = await netHttp.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var body = doc.RootElement.Clone();
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            foreach (var set in cookies) foreach (var item in NetCookies(set.Split(';')[0])) jar[item.Key] = item.Value;
        long code = NetNumber(body, "code");
        if (requireSuccess && code != 200)
            throw new InvalidOperationException(code is 301 or 302 ? "网易云登录已失效，请重新扫码" : "网易云请求失败（" + code + "）：" + NetString(body, "message", NetString(body, "msg", "请稍后重试")));
        return new NetReply(body, string.Join("; ", jar.Select(x => x.Key + "=" + x.Value)));
    }

    // WEAPI request encoding. Native .NET cryptography; no external service receives credentials.
    internal static Dictionary<string, string> NetEncrypt(string json, string? fixedKey = null)
    {
        string key = fixedKey ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        if (key.Length != 16) throw new ArgumentException("Invalid key length");
        string Encrypt(string input, string secret)
        {
            using var aes = Aes.Create(); aes.Key = Encoding.UTF8.GetBytes(secret); aes.IV = Encoding.ASCII.GetBytes("0102030405060708");
            return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(input), aes.IV, PaddingMode.PKCS7));
        }
        const string publicKey = "-----BEGIN PUBLIC KEY-----\nMIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFbt7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZMldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB\n-----END PUBLIC KEY-----";
        using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey);
        var p = rsa.ExportParameters(false);
        var value = new BigInteger(Encoding.ASCII.GetBytes(new string(key.Reverse().ToArray())), true, true);
        var modulus = new BigInteger(p.Modulus!, true, true);
        var exponent = new BigInteger(p.Exponent!, true, true);
        string encryptedKey = Convert.ToHexString(BigInteger.ModPow(value, exponent, modulus).ToByteArray(true, true)).ToLowerInvariant().PadLeft(256, '0');
        return new() { ["params"] = Encrypt(Encrypt(json, "0CoJUm6Qyw8W8jud"), key), ["encSecKey"] = encryptedKey };
    }

    internal static Dictionary<string, string> NetEapi(string path, string json)
    {
        string hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("nobody" + path + "use" + json + "md5forencrypt"))).ToLowerInvariant();
        byte[] input = Encoding.UTF8.GetBytes(path + "-36cd479b6b5-" + json + "-36cd479b6b5-" + hash);
        using var aes = Aes.Create(); aes.Key = Encoding.ASCII.GetBytes("e82ckenh8dichen8");
        return new() { ["params"] = Convert.ToHexString(aes.EncryptEcb(input, PaddingMode.PKCS7)) };
    }

    private static string NetString(JsonElement e, string name, string fallback = "") => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    private static long NetNumber(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long value) ? value : 0;
    private static IEnumerable<JsonElement> NetArray(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
    internal static Track? NetTrack(JsonElement song)
    {
        long id = NetNumber(song, "id"); if (id <= 0) return null;
        var artists = NetArray(song, "ar").Concat(NetArray(song, "artists"));
        JsonElement album = default;
        if (!song.TryGetProperty("al", out album)) song.TryGetProperty("album", out album);
        return new Track { Source = "netease", Id = id.ToString(CultureInfo.InvariantCulture), SongId = id,
            Title = NetString(song, "name"), Artist = string.Join(" / ", artists.Select(x => NetString(x, "name"))),
            CoverUrl = NetString(album, "picUrl").Replace("http://", "https://"), IsVip = NetNumber(song, "fee") is 1 or 4 };
    }

    private async Task<List<Track>> NetDetails(IEnumerable<long> ids)
    {
        var result = new List<Track>();
        foreach (var batch in ids.Distinct().Where(x => x > 0).Chunk(200))
        {
            var reply = await NetApi("v3/song/detail", new() { ["c"] = JsonSerializer.Serialize(batch.Select(id => new { id })), ["ids"] = JsonSerializer.Serialize(batch) });
            var byId = NetArray(reply.Body, "songs").Select(NetTrack).Where(x => x != null).ToDictionary(x => x!.SongId);
            foreach (long id in batch) if (byId.TryGetValue(id, out var t)) result.Add(t!);
        }
        return result;
    }

    private async Task<List<Track>> NetPlaylist(string id)
    {
        var reply = await NetApi("v6/playlist/detail", new() { ["id"] = id, ["n"] = 100000, ["s"] = 0 });
        var playlist = reply.Body.GetProperty("playlist");
        return await NetDetails(NetArray(playlist, "trackIds").Select(x => NetNumber(x, "id")));
    }

    private async Task<List<Track>> NetSearch(string keyword)
    {
        var reply = await NetApi("cloudsearch/pc", new() { ["s"] = keyword, ["type"] = 1, ["limit"] = 60, ["offset"] = 0, ["total"] = true });
        return reply.Body.TryGetProperty("result", out var result) ? NetArray(result, "songs").Select(NetTrack).Where(x => x != null).Select(x => x!).ToList() : new();
    }

    private void NetRecommend(int id)
    {
        int ticket = ++netRecommendGeneration; loadingRecommend = true; recommendError = "";
        NetJob(async session => { var tracks = await NetPlaylist(id.ToString()); NetPost(session, () => { if (ticket == netRecommendGeneration) { pendingRecommendTracks = tracks; loadingRecommend = false; } }); },
            error => { if (ticket == netRecommendGeneration) { recommendError = error; loadingRecommend = false; } });
    }

    private void NetFavorites()
    {
        if (config.QQMusicId.Length == 0 || loadingFav) return;
        loadingFav = true; favError = ""; string uid = config.QQMusicId;
        NetJob(async session =>
        {
            var reply = await NetApi("song/like/get", new() { ["uid"] = uid });
            var ids = NetArray(reply.Body, "ids").Select(x => x.GetInt64()).ToArray();
            var tracks = await NetDetails(ids);
            NetPost(session, () => { pendingFavTracks = tracks; loadingFav = false; });
        }, error => { favError = error; loadingFav = false; });
    }

    private void NetPlaylists()
    {
        if (config.QQMusicId.Length == 0 || loadingPlaylists) return;
        loadingPlaylists = true; playlistError = ""; string uid = config.QQMusicId;
        NetJob(async session =>
        {
            var result = new List<PlaylistInfo>();
            for (int offset = 0; ; offset += 200)
            {
                var reply = await NetApi("user/playlist", new() { ["uid"] = uid, ["limit"] = 200, ["offset"] = offset, ["includeVideo"] = false });
                var rows = NetArray(reply.Body, "playlist").ToArray();
                result.AddRange(rows.Select(x => new PlaylistInfo(NetNumber(x, "id").ToString(), NetString(x, "name"), (int)NetNumber(x, "trackCount"))));
                if (rows.Length < 200 || !reply.Body.TryGetProperty("more", out var more) || more.ValueKind != JsonValueKind.True) break;
                if (offset >= 10000) throw new InvalidOperationException("歌单数量过多，请减少收藏后刷新");
            }
            NetPost(session, () => { pendingPlaylists = result; loadingPlaylists = false; });
        }, error => { playlistError = error; loadingPlaylists = false; });
    }

    private void NetPlaylistTracks(PlaylistInfo playlist)
    {
        if (playlistTrackCache.ContainsKey(playlist.Id) || !playlistTrackLoading.Add(playlist.Id)) return;
        NetJob(async session => { var tracks = await NetPlaylist(playlist.Id); NetPost(session, () => { playlistTrackCache[playlist.Id] = tracks; playlistTrackLoading.Remove(playlist.Id); }); },
            error => { playlistTrackLoading.Remove(playlist.Id); playlistError = error; });
    }

    private void NetLike(Track song, bool like)
    {
        if (netFavoriteWriting || config.QQMusicId.Length == 0) { QueueChat("请先登录网易云音乐，或等待上一项收藏操作完成"); return; }
        netFavoriteWriting = true;
        NetJob(async session =>
        {
            await NetApi("radio/like", new() { ["trackId"] = song.SongId, ["like"] = like, ["alg"] = "itembased", ["time"] = "3" });
            NetPost(session, () =>
            {
                netFavoriteWriting = false;
                if (like) likedIds.Add(song.SongId); else likedIds.Remove(song.SongId);
                QueueChat(like ? "已加入网易云「我喜欢的音乐」" : "已取消网易云喜欢"); NetFavorites();
            });
        }, error => { netFavoriteWriting = false; QueueChat("收藏失败：" + error); });
    }

    private async Task<(string? Url, bool IsMp3)> NetUrl(Track song)
    {
        var reply = await NetApi("song/enhance/player/url", new() { ["ids"] = JsonSerializer.Serialize(new[] { song.SongId }), ["br"] = 128000 });
        var data = NetArray(reply.Body, "data").FirstOrDefault();
        string url = NetString(data, "url");
        if (url.Length == 0) throw new InvalidOperationException("网易云未提供可播放音源：请检查版权或会员权益");
        if (data.TryGetProperty("freeTrialInfo", out var trial) && trial.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new InvalidOperationException("该歌曲仅返回试听片段，登录有相应权益的账号后重试");
        string type = NetString(data, "type", "mp3");
        if (!type.Equals("mp3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前本地播放后端暂不支持网易云返回的 " + type + " 音源");
        return (url.Replace("http://", "https://"), true);
    }

    private async Task<string> NetLyric(string id)
    {
        var reply = await NetApi("song/lyric", new() { ["id"] = id, ["lv"] = -1, ["kv"] = -1, ["tv"] = -1 });
        return reply.Body.TryGetProperty("lrc", out var lrc) ? NetString(lrc, "lyric") : "";
    }

    private void NetLogin()
    {
        ResetNetSession(); loginRunning = true; loginMsg = "正在获取网易云二维码…";
        NetJob(async session =>
        {
            var keyReply = await NetApi("login/qrcode/unikey", new() { ["type"] = 3 }, "");
            var body = keyReply.Body;
            string key = NetString(body, "unikey");
            if (key.Length == 0 && body.TryGetProperty("data", out var inner)) key = NetString(inner, "unikey");
            if (key.Length == 0) throw new InvalidOperationException("网易云未返回二维码 key");
            var qr = OmniMusicQr.QrCode.EncodeText("https://music.163.com/login?codekey=" + Uri.EscapeDataString(key), OmniMusicQr.QrCode.Ecc.Medium);
            NetPost(session, () => { netQr = qr; loginMsg = "请使用网易云音乐 App 扫码确认"; });
            var token = netOperationToken.Value;
            string cookie = keyReply.Cookie;
            for (int count = 0; count < 90; count++)
            {
                await Task.Delay(2000, token);
                var check = await NetApi("login/qrcode/client/login", new() { ["key"] = key, ["type"] = 3 }, cookie, false);
                cookie = check.Cookie;
                long code = NetNumber(check.Body, "code");
                if (code == 800) throw new InvalidOperationException("二维码已过期，请重新生成");
                if (code == 802) NetPost(session, () => loginMsg = "已扫码，请在网易云 App 中确认");
                if (code == 803)
                {
                    var account = await NetApi("w/nuser/account/get", new(), cookie);
                    if (!account.Body.TryGetProperty("profile", out var profile) || NetNumber(profile, "userId") == 0)
                        throw new InvalidOperationException("登录确认成功，但未获取到用户信息，请重试");
                    string uid = NetNumber(profile, "userId").ToString(); string nick = NetString(profile, "nickname"); string savedCookie = account.Cookie;
                    NetPost(session, () =>
                    {
                        netCookie = savedCookie; config.QQCookie = savedCookie; config.QQMusicId = uid; config.QQNickname = nick;
                        netQr = null; loginRunning = false; loginMsg = "网易云登录成功"; loginDirty = true;
                        NetFavorites(); NetPlaylists();
                    });
                    return;
                }
                if (code is not (801 or 802)) throw new InvalidOperationException("网易云扫码状态异常（" + code + "），请重试");
            }
            throw new InvalidOperationException("扫码已超时，请重新生成二维码");
        }, error => { netQr = null; loginRunning = false; loginMsg = error; });
    }

    private sealed record LrcLine(double Time, string Text);
}


}

#nullable disable
/* 
 * QR code generator library (.NET)
 *
 * Copyright (c) Manuel Bleichenbacher (MIT License)
 * https://github.com/manuelbl/QrCodeGenerator
 * Copyright (c) Project Nayuki (MIT License)
 * https://www.nayuki.io/page/qr-code-generator-library
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
 * IN THE SOFTWARE.
 */


namespace OmniMusicQr
{
using System;
using System.Collections;
    /// <summary>
    /// Extension methods for the <see cref="BitArray"/> class.
    /// </summary>
    public static class BitArrayExtensions
    {

        /// <summary>
        /// Appends the specified number bits of the specified value to this bit array.
        /// <para>
        /// The least significant bits of the specified value are added. They are appended in reverse order,
        /// from the most significant to the least significant one, i.e. bits 0 to <i>len-1</i>
        /// are appended in the order <i>len-1</i>, <i>len-2</i> ... 1, 0.
        /// </para>
        /// <para>
        /// Requires 0 &#x2264; len &#x2264; 31, and 0 &#x2264; val &lt; 2<sup>len</sup>.
        /// </para>
        /// </summary>
        /// <param name="bitArray">The BitArray instance that this method extends.</param>
        /// <param name="val">The value to append.</param>
        /// <param name="len">The number of low-order bits in the value to append.</param>
        /// <exception cref="ArgumentOutOfRangeException">Value or number of bits is out of range.</exception>
        public static void AppendBits(this BitArray bitArray, uint val, int len)
        {
            if (len < 0 || len > 31)
            {
                throw new ArgumentOutOfRangeException(nameof(len), "'len' out of range");
            }

            if (val >> len != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(val), "'val' out of range");
            }

            var bitLength = bitArray.Length;
            bitArray.Length = bitLength + len;
            var mask = 1U << (len - 1);
            for (var i = bitLength; i < bitLength + len; i++) // Append bit by bit
            {
                if ((val & mask) != 0)
                {
                    bitArray.Set(i, true);
                }

                mask >>= 1;
            }
        }


        /// <summary>
        /// Appends the content of the specified bit array to the end of this array.
        /// </summary>
        /// <param name="bitArray">The BitArray instance that this method extends.</param>
        /// <param name="otherArray">The bit array to append</param>
        /// <exception cref="ArgumentNullException">If <c>bitArray</c> is <c>null</c>.</exception>
        public static void AppendData(this BitArray bitArray, BitArray otherArray)
        {
            Objects.RequireNonNull(otherArray);
            var bitLength = bitArray.Length;
            bitArray.Length = bitLength + otherArray.Length;
            for (var i = 0; i < otherArray.Length; i++, bitLength++)  // Append bit by bit
            {
                if (otherArray[i])
                {
                    bitArray.Set(bitLength, true);
                }
            }
        }

    }
}


/* 
 * QR code generator library (.NET)
 *
 * Copyright (c) Manuel Bleichenbacher (MIT License)
 * https://github.com/manuelbl/QrCodeGenerator
 * Copyright (c) Project Nayuki (MIT License)
 * https://www.nayuki.io/page/qr-code-generator-library
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
 * IN THE SOFTWARE.
 */


namespace OmniMusicQr
{
using System;
using System.Collections.Generic;
    /// <summary>
    /// The exception that is thrown when the supplied data does not fit in the QR code.
    /// </summary>
    /// <remarks>
    /// Ways to handle this exception include:
    /// <ul>
    ///   <li>Decrease the error correction level (if it was greater than <see cref="QrCode.Ecc.Low"/>)</li>
    ///   <li>Increase the <c>maxVersion</c> argument (if it was less than <see cref="QrCode.MaxVersion"/>).
    ///       This advice applies to the advanced factory functions
    ///       <see cref="QrCode.EncodeSegments"/> and
    ///       <see cref="QrSegmentAdvanced.MakeSegmentsOptimally(string, QrCode.Ecc, int, int)"/> only.
    ///       Other factory functions automatically try all versions up to <see cref="QrCode.MaxVersion"/>.</li>
    ///   <li>Split the text into several segments and encode them using different encoding modes
    ///     (see <see cref="QrSegmentAdvanced.MakeSegmentsOptimally(string, QrCode.Ecc, int, int)"/>.)</li>
    ///   <li>Make the text or binary data shorter.</li>
    ///   <li>Change the text to fit the character set of a particular segment mode (e.g. alphanumeric).</li>
    ///   <li>Reject the data and notify the caller/user.</li>
    /// </ul>
    /// </remarks>
    /// <seealso cref="QrCode.EncodeText(string, QrCode.Ecc)"/>
    /// <seealso cref="QrCode.EncodeBinary(byte[], QrCode.Ecc)"/>
    /// <seealso cref="QrCode.EncodeSegments(List{QrSegment}, QrCode.Ecc, int, int, int, bool)"/>
    /// <seealso cref="QrSegmentAdvanced.MakeSegmentsOptimally(string, QrCode.Ecc, int, int)"/>
    public class DataTooLongException : ArgumentException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DataTooLongException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public DataTooLongException(string message)
            : base(message)
        { }
    }
}

/* 
 * QR code generator library (.NET)
 *
 * Copyright (c) Manuel Bleichenbacher (MIT License)
 * https://github.com/manuelbl/QrCodeGenerator
 * Copyright (c) Project Nayuki (MIT License)
 * https://www.nayuki.io/page/qr-code-generator-library
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
 * IN THE SOFTWARE.
 */


namespace OmniMusicQr
{
using System;
using System.Globalization;
using System.Text;
    internal class Graphics
    {
        internal Graphics(int size, bool[] modules)
        {
            _size = size;
            _modules = modules;
        }

        private readonly int _size;

        // The modules of this QR code (false = light, true = dark).
        // Immutable after constructor finishes.
        private readonly bool[] _modules;

        internal string ToSvgString(int border, string foreground, string background)
        {
            if (border < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(border), "Border must be non-negative");
            }

            var dim = _size + border * 2;
            var sb = new StringBuilder()
                .Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
                .Append("<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">\n")
                .Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" viewBox=\"0 0 {dim} {dim}\" stroke=\"none\">\n")
                .Append($"\t<rect width=\"100%\" height=\"100%\" fill=\"{background}\"/>\n")
                .Append("\t<path d=\"");

            // Work on copy as it is destructive
            var modules = CopyModules();
            CreatePath(sb, modules, border);

            return sb
                .Append($"\" fill=\"{foreground}\"/>\n")
                .Append("</svg>\n")
                .ToString();
        }

        internal string ToGraphicsPath(int border)
        {
            if (border < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(border), "Border must be non-negative");
            }

            // Work on copy as it is destructive
            var modules = CopyModules();
            var path = new StringBuilder();
            CreatePath(path, modules, border);
            return path.ToString();
        }

        internal byte[] ToBmpBitmap(int border, int scale, int foreground, int background)
        {
            if (scale < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be greater than 0.");
            }

            if (border < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(border), border, "Border must be non-negative.");
            }

            var dim = (_size + 2 * border) * scale;

            if (dim > short.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(scale), "Scale or border too large.");
            }

            // NOTE: Works for Size > 0
            // Modules to bytes
            // x >> 3 == x / 8
            var bytesToWrite = ((dim - 1) >> 3) + 1;

            // NOTE: Align to 4 bytes
            // This is a Bitmap requirement
            // (size + (align - 1)) & ~(align - 1)
            var aligned = (bytesToWrite + 3) & ~3;
            var fileSize = 62 + dim * aligned;

            var buf = new byte[fileSize];

            // NOTE: BMP file header
            buf[0] = (byte)'B';
            buf[1] = (byte)'M';

            buf[2] = (byte)fileSize;
            buf[3] = (byte)(fileSize >> 8);
            buf[4] = (byte)(fileSize >> 16);
            buf[5] = (byte)(fileSize >> 24);

            // NOTE: Offset to bitmap data
            buf[10] = 62;

            // NOTE: BMP info header
            buf[14] = 40;

            // NOTE: Image width
            buf[18] = (byte)dim;
            buf[19] = (byte)(dim >> 8);
            buf[20] = (byte)(dim >> 16);
            buf[21] = (byte)(dim >> 24);

            // NOTE: Image height
            buf[22] = buf[18];
            buf[23] = buf[19];
            buf[24] = buf[20];
            buf[25] = buf[21];

            // NOTE: Number of color planes (usually 1)
            // Must be non-zero
            buf[26] = 1;

            // NOTE: Number of bits per pixel (1 bpp)
            buf[28] = 1;

            // NOTE: Horizontal resolution (pixels/meter)
            // 3780 ppm (96 dpi)
            buf[38] = 196;
            buf[39] = 14;

            // NOTE: Vertical resolution (pixels/meter)
            // 3780 ppm (96 dpi)
            buf[42] = buf[38];
            buf[43] = buf[39];

            // NOTE: Color table
            // Alpha isn't useful here
            // Foreground - Dark
            buf[54] = (byte)foreground; // blue
            buf[55] = (byte)(foreground >> 8); // green
            buf[56] = (byte)(foreground >> 16); // red

            // Background - Light
            buf[58] = (byte)background; // blue
            buf[59] = (byte)(background >> 8); // green
            buf[60] = (byte)(background >> 16); // red;

            var scaledBorder = border * scale;

            int i;
            int y;
            byte px;

            if (border > 0)
            {
                var scaledSize = _size * scale;

                for (i = 0; i < aligned; ++i)
                {
                    px = 255;

                    if (i == bytesToWrite - 1)
                    {
                        px = (byte)(255 << ((bytesToWrite << 3) - dim));
                    }
                    else if (i >= bytesToWrite)
                    {
                        px = 0;
                    }

                    for (y = 0; y < scaledBorder; ++y)
                    {
                        buf[62 + i + y * aligned] = px;
                        buf[62 + i + (y + scaledSize + scaledBorder) * aligned] = px;
                    }
                }
            }

            for (y = 0; y < _size; ++y)
            {
                int j;
                var yOffset = y * scale + scaledBorder;

                for (i = 0; i < aligned; ++i)
                {
                    px = 0;

                    for (j = 0; j < 8; ++j)
                    {
                        var x = ((i << 3) + j) / scale;

                        if (x >= dim)
                        {
                            continue;
                        }

                        if (x < border || x >= _size + border)
                        {
                            px |= (byte)(1 << (7 - j));
                            continue;
                        }

                        px |= (byte)(_modules[x - border + _size * (_size - y - 1)] ? 0 : 1 << (7 - j));
                    }

                    buf[62 + i + yOffset * aligned] = px;
                }

                // NOTE: Copy rows when scaling
                for (i = 1; i <= scale - 1; ++i)
                {
                    for (j = 0; j < aligned; ++j)
                    {
                        buf[62 + j + (yOffset + i) * aligned] = buf[62 + j + yOffset * aligned];
                    }
                }
            }

            return buf;
        }

        // Append a SVG/XAML path for the QR code to the provided string builder
        private  void CreatePath(StringBuilder path, bool[,] modules, int border)
        {
            // Simple algorithms to reduce the number of rectangles for drawing the QR code
            // and reduce SVG/XAML size.
            var size = modules.GetLength(0);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (modules[y, x])
                    {
                        DrawLargestRectangle(path, modules, x, y, border);
                    }
                }
            }
        }

        // Find, draw and clear largest rectangle with (x, y) as the top left corner
        private static void DrawLargestRectangle(StringBuilder path, bool[,] modules, int x, int y, int border)
        {
            var size = modules.GetLength(0);

            var bestW = 1;
            var bestH = 1;
            var maxArea = 1;

            var xLimit = size;
            var iy = y;
            while (iy < size && modules[iy, x])
            {
                var w = 0;
                while (x + w < xLimit && modules[iy, x + w])
                {
                    w++;
                }

                var area = w * (iy - y + 1);
                if (area > maxArea)
                {
                    maxArea = area;
                    bestW = w;
                    bestH = iy - y + 1;
                }
                xLimit = x + w;
                iy++;
            }

            // append path command
            if (x != 0 || y != 0)
            {
                path.Append(" ");
            }

            // Different locales use different minus signs.
            FormattableString pathElement = $"M{x + border},{y + border}h{bestW}v{bestH}h{-bestW}z";
            path.Append(pathElement.ToString(CultureInfo.InvariantCulture));

            // clear processed modules
            ClearRectangle(modules, x, y, bestW, bestH);
        }

        // Clear a rectangle of modules
        private static void ClearRectangle(bool[,] modules, int x, int y, int width, int height)
        {
            for (var iy = y; iy < y + height; iy++)
            {
                for (var ix = x; ix < x + width; ix++)
                {
                    modules[iy, ix] = false;
                }
            }
        }

        // Create a copy of the modules (in row-major order)
        private bool[,] CopyModules()
        {
            var modules = new bool[_size, _size];
            var index = 0;
            for (var y = 0; y < _size; y++)
            {
                for (var x = 0; x < _size; x++)
                {
                    modules[y, x] = _modules[index];
                    index += 1;
                }
            }

            return modules;
        }
    }
}

/* 
 * QR code generator library (.NET)
 *
 * Copyright (c) Manuel Bleichenbacher (MIT License)
 * https://github.com/manuelbl/QrCodeGenerator
 * Copyright (c) Project Nayuki (MIT License)
 * https://www.nayuki.io/page/qr-code-generator-library
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
 * IN THE SOFTWARE.
 */


namespace OmniMusicQr
{
using System;
    /// <summary>
    /// Helper functions to check for valid arguments.
    /// </summary>
    internal static class Objects
    {
        /// <summary>
        /// Ensures that the specified argument is <i>not null</i>.
        /// <para>
        /// Throws a <see cref="ArgumentNullException"/> exception if the argument is <c>null</c>.
        /// </para>
        /// </summary>
        /// <typeparam name="T">The type of the argument.</typeparam>
        /// <param name="arg">The argument to check.</param>
        /// <returns>Argument passed to function.</returns>
        /// <exception cref="ArgumentNullException">The specified argument is <c>null</c>.</exception>
        internal static T RequireNonNull<T>(T arg)
        {
            if (arg == null)
            {
                throw new ArgumentNullException();
            }
            return arg;
        }
    }
}

/* 
 * QR code generator library (.NET)
 *
 * Copyright (c) Manuel Bleichenbacher (MIT License)
 * https://github.com/manuelbl/QrCodeGenerator
 * Copyright (c) Project Nayuki (MIT License)
 * https://www.nayuki.io/page/qr-code-generator-library
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
 * IN THE SOFTWARE.
 */


namespace OmniMusicQr
{
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
    /// <summary>
    /// Represents a QR code containing text or binary data.
    /// <para>
    /// Instances of this class represent an immutable square grid of dark and light pixels
    /// (called <i>modules</i> by the QR code specification).
    /// Static factory methods are provided to create QR codes from text or binary data.
    /// Some of the methods provide detailed control about the encoding parameters such a QR
    /// code size (called <i>version</i> by the standard), error correction level and mask.
    /// </para>
    /// <para>
    /// QR codes are a type of two-dimensional barcodes, invented by Denso Wave and
    /// described in the ISO/IEC 18004 standard.
    /// </para>
    /// <para>
    /// This class covers the QR Code Model 2 specification, supporting all versions (sizes)
    /// from 1 to 40, all 4 error correction levels, and 4 character encoding modes.</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// To create a QR code instance:
    /// </para>
    /// <ul>
    ///   <li>High level: Take the payload data and call <see cref="EncodeText(string, Ecc)"/>
    ///       or <see cref="EncodeBinary(byte[], Ecc)"/>.</li>
    ///   <li>Mid level: Custom-make a list of <see cref="QrSegment"/> instances and call
    ///       <see cref="EncodeSegments"/></li>
    ///   <li>Low level: Custom-make an array of data codeword bytes (including segment headers and
    ///       final padding, excluding error correction codewords), supply the appropriate version number,
    ///       and call the <see cref="QrCode(int, Ecc, byte[], int)"/>.</li>
    /// </ul>
    /// </remarks>
    /// <seealso cref="QrSegment"/>
    public class QrCode
    {
        #region Static factory functions (high level)

        /// <summary>
        /// Creates a QR code representing the specified text using the specified error correction level.
        /// <para>
        /// As a conservative upper bound, this function is guaranteed to succeed for strings with up to 738
        /// Unicode code points (not UTF-16 code units) if the low error correction level is used. The smallest possible
        /// QR code version (size) is automatically chosen. The resulting ECC level will be higher than the one
        /// specified if it can be achieved without increasing the size (version).
        /// </para>
        /// </summary>
        /// <param name="text">The text to be encoded. The full range of Unicode characters may be used.</param>
        /// <param name="ecl">The minimum error correction level to use.</param>
        /// <returns>The created QR code instance representing the specified text.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="ecl"/> is <c>null</c>.</exception>
        /// <exception cref="DataTooLongException">The text is too long to fit in the largest QR code size (version)
        /// at the specified error correction level.</exception>
        public static QrCode EncodeText(string text, Ecc ecl)
        {
            Objects.RequireNonNull(text);
            Objects.RequireNonNull(ecl);
            var segments = QrSegment.MakeSegments(text);
            return EncodeSegments(segments, ecl);
        }

        /// <summary>
        /// Creates a QR code representing the specified binary data using the specified error correction level.
        /// <para>
        /// This function encodes the data in the binary segment mode. The maximum number of
        /// bytes allowed is 2953. The smallest possible QR code version is automatically chosen.
        /// The resulting ECC level will be higher than the one specified if it can be achieved without increasing the size (version).
        /// </para>
        /// </summary>
        /// <param name="data">The binary data to encode.</param>
        /// <param name="ecl">The minimum error correction level to use.</param>
        /// <returns>The created QR code representing the specified data.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> or <paramref name="ecl"/> is <c>null</c>.</exception>
        /// <exception cref="DataTooLongException">The specified data is too long to fit in the largest QR code size (version)
        /// at the specified error correction level.</exception>
        public static QrCode EncodeBinary(byte[] data, Ecc ecl)
        {
            Objects.RequireNonNull(data);
            Objects.RequireNonNull(ecl);
            var seg = QrSegment.MakeBytes(data);
            return EncodeSegments(new List<QrSegment> { seg }, ecl);
        }

        #endregion


        #region Static factory functions (mid level)

        /// <summary>
        /// Creates a QR code representing the specified segments with the specified encoding parameters.
        /// <para>
        /// The smallest possible QR code version (size) is used. The range of versions can be
        /// restricted by the <paramref name="minVersion"/> and <paramref name="maxVersion"/> parameters.
        /// </para>
        /// <para>
        /// If <paramref name="boostEcl"/> is <c>true</c>, the resulting ECC level will be higher than the
        /// one specified if it can be achieved without increasing the size (version).
        /// </para>
        /// <para>
        /// The QR code mask is usually automatically chosen. It can be explicitly set with the <paramref name="mask"/>
        /// parameter by using a value between 0 to 7 (inclusive). -1 is for automatic mode (which may be slow).
        /// </para>
        /// <para>
        /// This function allows the user to create a custom sequence of segments that switches
        /// between modes (such as alphanumeric and byte) to encode text in less space and gives full control over all
        /// encoding parameters.
        /// </para>
        /// </summary>
        /// <remarks>
        /// This is a mid-level API; the high-level APIs are <see cref="EncodeText(string, Ecc)"/>
        /// and <see cref="EncodeBinary(byte[], Ecc)"/>.
        /// </remarks>
        /// <param name="segments">The segments to encode.</param>
        /// <param name="ecl">The minimal or fixed error correction level to use .</param>
        /// <param name="minVersion">The minimum version (size) of the QR code (between 1 and 40).</param>
        /// <param name="maxVersion">The maximum version (size) of the QR code (between 1 and 40).</param>
        /// <param name="mask">The mask number to use (between 0 and 7), or -1 for automatic mask selection.</param>
        /// <param name="boostEcl">If <c>true</c> the ECC level wil be increased if it can be achieved without increasing the size (version).</param>
        /// <returns>The created QR code representing the segments.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="segments"/>, any list element, or <paramref name="ecl"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">1 &#x2264; minVersion &#x2264; maxVersion &#x2264; 40
        /// or -1 &#x2264; mask &#x2264; 7 is violated.</exception>
        /// <exception cref="DataTooLongException">The segments are too long to fit in the largest QR code size (version)
        /// at the specified error correction level.</exception>
        public static QrCode EncodeSegments(List<QrSegment> segments, Ecc ecl, int minVersion = MinVersion, int maxVersion = MaxVersion, int mask = -1, bool boostEcl = true)
        {
            Objects.RequireNonNull(segments);
            Objects.RequireNonNull(ecl);
            if (minVersion < MinVersion || minVersion > maxVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(minVersion), "Invalid value");
            }
            if (maxVersion > MaxVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(maxVersion), "Invalid value");
            }
            if (mask < -1 || mask > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(mask), "Invalid value");
            }

            // Find the minimal version number to use
            int version, dataUsedBits;
            for (version = minVersion; ; version++)
            {
                var numDataBits = GetNumDataCodewords(version, ecl) * 8;  // Number of data bits available
                dataUsedBits = QrSegment.GetTotalBits(segments, version);
                if (dataUsedBits != -1 && dataUsedBits <= numDataBits)
                {
                    break;  // This version number is found to be suitable
                }

                if (version < maxVersion)
                {
                    continue;
                }

                // All versions in the range could not fit the given data
                var msg = "Segment too long";
                if (dataUsedBits != -1)
                {
                    msg = $"Data length = {dataUsedBits} bits, Max capacity = {numDataBits} bits";
                }

                throw new DataTooLongException(msg);
            }

            // Increase the error correction level while the data still fits in the current version number
            foreach (var newEcl in Ecc.AllValues)
            {  // From low to high
                if (boostEcl && dataUsedBits <= GetNumDataCodewords(version, newEcl) * 8)
                {
                    ecl = newEcl;
                }
            }

            // Concatenate all segments to create the data bit string
            var ba = new BitArray(0);
            foreach (var seg in segments)
            {
                ba.AppendBits(seg.EncodingMode.ModeBits, 4);
                ba.AppendBits((uint)seg.NumChars, seg.EncodingMode.NumCharCountBits(version));
                ba.AppendData(seg.GetData());
            }
            Debug.Assert(ba.Length == dataUsedBits);

            // Add terminator and pad up to a byte if applicable
            var dataCapacityBits = GetNumDataCodewords(version, ecl) * 8;
            Debug.Assert(ba.Length <= dataCapacityBits);
            ba.AppendBits(0, Math.Min(4, dataCapacityBits - ba.Length));
            ba.AppendBits(0, (8 - ba.Length % 8) % 8);
            Debug.Assert(ba.Length % 8 == 0);

            // Pad with alternating bytes until data capacity is reached
            for (uint padByte = 0xEC; ba.Length < dataCapacityBits; padByte ^= 0xEC ^ 0x11)
            {
                ba.AppendBits(padByte, 8);
            }

            // Pack bits into bytes in big endian
            var dataCodewords = new byte[ba.Length / 8];
            for (var i = 0; i < ba.Length; i++)
            {
                if (ba.Get(i))
                {
                    dataCodewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
                }
            }

            // Create the QR code object
            return new QrCode(version, ecl, dataCodewords, mask);
        }

        #endregion


        #region Public immutable properties

        /// <summary>
        /// The version (size) of this QR code (between 1 for the smallest and 40 for the biggest).
        /// </summary>
        /// <value>The QR code version (size).</value>
        public int Version { get; }

        /// <summary>
        /// The width and height of this QR code, in modules (pixels).
        /// The size is a value between 21 and 177.
        /// This is equal to version &#xD7; 4 + 17.
        /// </summary>
        /// <value>The QR code size.</value>
        public int Size { get; }

        /// <summary>
        /// The error correction level used for this QR code.
        /// </summary>
        /// <value>The error correction level.</value>
        public Ecc ErrorCorrectionLevel { get; }

        /// <summary>
        /// The index of the mask pattern used fort this QR code (between 0 and 7).
        /// <para>
        /// Even if a QR code is created with automatic mask selection (<c>mask</c> = 1),
        /// this property returns the effective mask used.
        /// </para>
        /// </summary>
        /// <value>The mask pattern index.</value>
        public int Mask { get; }

        #endregion


        #region Private grids of modules/pixels, with dimensions of size * size

        // The modules of this QR code (false = light, true = dark).
        // Immutable after constructor finishes. Accessed through GetModule().
        private readonly bool[] _modules;

        // Indicates function modules that are not subjected to masking. Discarded when constructor finishes.
        private readonly bool[] _isFunction;

        #endregion


        #region  Constructor (low level)

        /// <summary>
        /// Constructs a QR code with the specified version number,
        /// error correction level, data codeword bytes, and mask number.
        /// </summary>
        /// <remarks>
        /// This is a low-level API that most users should not use directly. A mid-level
        /// API is the <see cref="EncodeSegments"/> function.
        /// </remarks>
        /// <param name="version">The version (size) to use (between 1 to 40).</param>
        /// <param name="ecl">The error correction level to use.</param>
        /// <param name="dataCodewords">The bytes representing segments to encode (without ECC).</param>
        /// <param name="mask">The mask pattern to use (either -1 for automatic selection, or a value from 0 to 7 for fixed choice).</param>
        /// <exception cref="ArgumentNullException"><paramref name="ecl"/> or <paramref name="dataCodewords"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The version or mask value is out of range,
        /// or the data has an invalid length for the specified version and error correction level.</exception>
        public QrCode(int version, Ecc ecl, byte[] dataCodewords, int mask = -1)
        {
            // Check arguments and initialize fields
            if (version < MinVersion || version > MaxVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(version), "Version value out of range");
            }

            if (mask < -1 || mask > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(mask), "Mask value out of range");
            }

            Version = version;
            Size = version * 4 + 17;
            Objects.RequireNonNull(ecl);
            ErrorCorrectionLevel = ecl;
            Objects.RequireNonNull(dataCodewords);
            _modules = new bool[Size * Size];  // Initially all light
            _isFunction = new bool[Size * Size];

            // Compute ECC, draw modules, do masking
            DrawFunctionPatterns();
            var allCodewords = AddEccAndInterleave(dataCodewords);
            DrawCodewords(allCodewords);
            Mask = HandleConstructorMasking(mask);
            _isFunction = null;
        }


        #endregion


        #region Public methods

        /// <summary>
        /// Gets the color of the module (pixel) at the specified coordinates.
        /// <para>
        /// The top left corner has the coordinates (x=0, y=0). <i>x</i>-coordinates extend from left to right,
        /// <i>y</i>-coordinates extend from top to bottom.
        /// </para>
        /// <para>
        /// If coordinates outside the bounds of this QR code are specified, light (<c>false</c>) is returned.
        /// </para>
        /// </summary>
        /// <param name="x">The x coordinate.</param>
        /// <param name="y">The y coordinate.</param>
        /// <returns>The color of the specified module: <c>true</c> for dark modules and <c>false</c>
        /// for light modules (or if the coordinates are outside the bounds).</returns>
        public bool GetModule(int x, int y)
        {
            return 0 <= x && x < Size && 0 <= y && y < Size && _modules[y * Size + x];
        }

        /// <summary>
        /// Creates an SVG image of this QR code.
        /// <para>
        /// The images uses Unix newlines (\n), regardless of the platform.
        /// </para>
        /// </summary>
        /// <param name="border">The border width, as a factor of the module (QR code pixel) size</param>
        /// <returns>The SVG image as a string.</returns>
        public string ToSvgString(int border)
        {
            return ToSvgString(border, "#000000", "#ffffff");
        }

        /// <summary>
        /// Creates an SVG image of this QR code.
        /// <para>
        /// The images uses Unix newlines (\n), regardless of the platform.
        /// </para>
        /// <para>
        /// Colors are specified using CSS color data type. Examples of valid values are
        /// "#339966", "fuchsia", "rgba(137, 23, 89, 0.3)".
        /// </para>
        /// </summary>
        /// <param name="border">The border width, as a factor of the module (QR code pixel) size</param>
        /// <param name="foreground">The foreground color.</param>
        /// <param name="background">The background color.</param>
        public string ToSvgString(int border, string foreground, string background)
        {
            return AsGraphics().ToSvgString(border, foreground, background);
        }

        /// <summary>
        /// Creates a graphics path of this QR code valid in SVG or XAML.
        /// <para>
        /// The graphics path uses a coordinate system where each module is 1 unit wide and tall,
        /// and the top left module is offset vertically and horizontally by <i>border</i> units.
        /// </para>
        /// <para>
        /// Note that a border width other than 0 only make sense if the bounding box of the QR code
        /// is explicitly set by the graphics using this path. If the bounding box of this path is
        /// automatically derived, at least the right and bottom border will be missing.
        /// </para>
        /// <para>
        /// The path will look like this: <c>M3,3h7v1h-7z M12,3h1v4h-1z ... M70,71h1v1h-1z</c>. It
        /// is valid for SVG (<c>&lt;path d="M3,3h..." /&gt;</c>) and for XAML
        /// (<c>&lt;Path Data="M3,3h..." /&gt;</c>). For programmatic geometry creation in WPF see
        /// <a href="https://docs.microsoft.com/en-us/dotnet/api/system.windows.media.geometry.parse?view=windowsdesktop-6.0">Geometry.Parse(String)</a>.
        /// </para>
        /// </summary>
        /// <param name="border">The border width, as a factor of the module (QR code pixel) size</param>
        /// <returns>The graphics path</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if border is negative</exception>
        public string ToGraphicsPath(int border = 0)
        {
            return AsGraphics().ToGraphicsPath(border);
        }

        /// <summary>
        /// Creates a bitmap in the BMP format.
        /// <para>
        /// The bitmap uses 1 bit per pixel and a color table with 2 entries.
        /// </para>
        /// <para>
        /// Color values can be created with <see cref="RgbColor(byte, byte, byte)"/>.
        /// </para>
        /// </summary>
        /// <param name="border">The border width, as a factor of the module (QR code pixel) size.</param>
        /// <param name="scale">The width and height, in pixels, of each module.</param>
        /// <param name="foreground">The foreground color (dark modules), in RGB value (little endian).</param>
        /// <param name="background">The background color (light modules), in RGB value (little endian).</param>
        /// <returns>Bitmap data</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="border"/> is negative,
        /// <paramref name="scale"/> is less than 1 or the resulting image is wider than 32,768 pixels.</exception>
        public byte[] ToBmpBitmap(int border, int scale, int foreground, int background)
        {
            return AsGraphics().ToBmpBitmap(border, scale, foreground, background);
        }

        /// <summary>
        /// Creates bitmap in the BMP format data using black for dark modules and white for light modules.
        /// <para>
        /// The bitmap uses 1 bit per pixel and a color table with 2 entries.
        /// </para>
        /// </summary>
        /// <param name="border">The border width, as a factor of the module (QR code pixel) size.</param>
        /// <param name="scale">The width and height, in pixels, of each module.</param>
        /// <returns>Bitmap data</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="border"/> is negative,
        /// <paramref name="scale"/> is less than 1 or the resulting image is wider than 32,768 pixels.</exception>
        public byte[] ToBmpBitmap(int border = 0, int scale = 1)
        {
            return ToBmpBitmap(border, scale, 0x000000, 0xffffff);
        }

        /// <summary>
        /// Creates an RGB color value in little endian format.
        /// </summary>
        /// <param name="red">Red component.</param>
        /// <param name="green">Green component.</param>
        /// <param name="blue">Blue component.</param>
        /// <returns>RGB color value</returns>
        public int RgbColor(byte red, byte green, byte blue)
        {

            return (red << 16) | (green << 8) | blue;
        }

        #endregion


        #region Private helper methods for constructor: Drawing function modules

        // Reads this object's version field, and draws and marks all function modules.
        private void DrawFunctionPatterns()
        {
            // Draw horizontal and vertical timing patterns
            for (var i = 0; i < Size; i++)
            {
                SetFunctionModule(6, i, i % 2 == 0);
                SetFunctionModule(i, 6, i % 2 == 0);
            }

            // Draw 3 finder patterns (all corners except bottom right; overwrites some timing modules)
            DrawFinderPattern(3, 3);
            DrawFinderPattern(Size - 4, 3);
            DrawFinderPattern(3, Size - 4);

            // Draw numerous alignment patterns
            var alignPatPos = GetAlignmentPatternPositions();
            var numAlign = alignPatPos.Length;
            for (var i = 0; i < numAlign; i++)
            {
                for (var j = 0; j < numAlign; j++)
                {
                    // Don't draw on the three finder corners
                    if (!(i == 0 && j == 0 || i == 0 && j == numAlign - 1 || i == numAlign - 1 && j == 0))
                    {
                        DrawAlignmentPattern(alignPatPos[i], alignPatPos[j]);
                    }
                }
            }

            // Draw configuration data
            DrawFormatBits(0);  // Dummy mask value; overwritten later in the constructor
            DrawVersion();
        }


        // Draws two copies of the format bits (with its own error correction code)
        // based on the given mask and this object's error correction level field.
        private void DrawFormatBits(uint mask)
        {
            // Calculate error correction code and pack bits
            var data = (ErrorCorrectionLevel.FormatBits << 3) | mask;  // errCorrLvl is uint2, mask is uint3
            var rem = data;
            for (var i = 0; i < 10; i++)
            {
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            }

            var bits = ((data << 10) | rem) ^ 0x5412;  // uint15
            Debug.Assert(bits >> 15 == 0);

            // Draw first copy
            for (var i = 0; i <= 5; i++)
            {
                SetFunctionModule(8, i, GetBit(bits, i));
            }

            SetFunctionModule(8, 7, GetBit(bits, 6));
            SetFunctionModule(8, 8, GetBit(bits, 7));
            SetFunctionModule(7, 8, GetBit(bits, 8));
            for (var i = 9; i < 15; i++)
            {
                SetFunctionModule(14 - i, 8, GetBit(bits, i));
            }

            // Draw second copy
            for (var i = 0; i < 8; i++)
            {
                SetFunctionModule(Size - 1 - i, 8, GetBit(bits, i));
            }

            for (var i = 8; i < 15; i++)
            {
                SetFunctionModule(8, Size - 15 + i, GetBit(bits, i));
            }

            SetFunctionModule(8, Size - 8, true);  // Always dark
        }


        // Draws two copies of the version bits (with its own error correction code),
        // based on this object's version field, iff 7 <= version <= 40.
        private void DrawVersion()
        {
            if (Version < 7)
            {
                return;
            }

            // Calculate error correction code and pack bits
            var rem = (uint)Version;  // version is uint6, in the range [7, 40]
            for (var i = 0; i < 12; i++)
            {
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            }

            var bits = ((uint)Version << 12) | rem;  // uint18
            Debug.Assert(bits >> 18 == 0);

            // Draw two copies
            for (var i = 0; i < 18; i++)
            {
                var bit = GetBit(bits, i);
                var a = Size - 11 + i % 3;
                var b = i / 3;
                SetFunctionModule(a, b, bit);
                SetFunctionModule(b, a, bit);
            }
        }

        // Draws a 9*9 finder pattern including the border separator,
        // with the center module at (x, y). Modules can be out of bounds.
        private void DrawFinderPattern(int x, int y)
        {
            for (var dy = -4; dy <= 4; dy++)
            {
                for (var dx = -4; dx <= 4; dx++)
                {
                    var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));  // Chebyshev/infinity norm
                    int xx = x + dx, yy = y + dy;
                    if (0 <= xx && xx < Size && 0 <= yy && yy < Size)
                    {
                        SetFunctionModule(xx, yy, dist != 2 && dist != 4);
                    }
                }
            }
        }


        // Draws a 5*5 alignment pattern, with the center module
        // at (x, y). All modules must be in bounds.
        private void DrawAlignmentPattern(int x, int y)
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    SetFunctionModule(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            }
        }


        // Sets the color of a module and marks it as a function module.
        // Only used by the constructor. Coordinates must be in bounds.
        private void SetFunctionModule(int x, int y, bool isDark)
        {
            _modules[y * Size + x] = isDark;
            _isFunction[y * Size + x] = true;
        }

        private Graphics AsGraphics()
        {
            return new Graphics(Size, _modules);
        }

        #endregion


        #region Private helper methods for constructor: Codewords and masking

        // Returns a new byte string representing the given data with the appropriate error correction
        // codewords appended to it, based on this object's version and error correction level.
        private byte[] AddEccAndInterleave(byte[] data)
        {
            Objects.RequireNonNull(data);
            if (data.Length != GetNumDataCodewords(Version, ErrorCorrectionLevel))
            {
                throw new ArgumentOutOfRangeException();
            }

            // Calculate parameter numbers
            int numBlocks = NumErrorCorrectionBlocks[ErrorCorrectionLevel.Ordinal, Version];
            int blockEccLen = EccCodewordsPerBlock[ErrorCorrectionLevel.Ordinal, Version];
            var rawCodewords = GetNumRawDataModules(Version) / 8;
            var numShortBlocks = numBlocks - rawCodewords % numBlocks;
            var shortBlockLen = rawCodewords / numBlocks;

            // Split data into blocks and append ECC to each block
            var blocks = new byte[numBlocks][];
            var rs = new ReedSolomonGenerator(blockEccLen);
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                var dat = CopyOfRange(data, k, k + shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1));
                k += dat.Length;
                var block = CopyOf(dat, shortBlockLen + 1);
                var ecc = rs.GetRemainder(dat);
                Array.Copy(ecc, 0, block, block.Length - blockEccLen, ecc.Length);
                blocks[i] = block;
            }

            // Interleave (not concatenate) the bytes from every block into a single sequence
            var result = new byte[rawCodewords];
            for (int i = 0, k = 0; i < blocks[0].Length; i++)
            {
                for (var j = 0; j < blocks.Length; j++)
                {
                    // Skip the padding byte in short blocks
                    if (i == shortBlockLen - blockEccLen && j < numShortBlocks)
                    {
                        continue;
                    }

                    result[k] = blocks[j][i];
                    k++;
                }
            }
            return result;
        }


        // Draws the given sequence of 8-bit codewords (data and error correction) onto the entire
        // data area of this QR code. Function modules need to be marked off before this is called.
        private void DrawCodewords(byte[] data)
        {
            Objects.RequireNonNull(data);
            if (data.Length != GetNumRawDataModules(Version) / 8)
            {
                throw new ArgumentOutOfRangeException();
            }

            var i = 0;  // Bit index into the data
                        // Do the funny zigzag scan
            for (var right = Size - 1; right >= 1; right -= 2)
            {
                // Index of right column in each column pair
                if (right == 6)
                {
                    right = 5;
                }

                for (var vert = 0; vert < Size; vert++)
                {
                    // Vertical counter
                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;  // Actual x coordinate
                        var upward = ((right + 1) & 2) == 0;
                        var y = upward ? Size - 1 - vert : vert;  // Actual y coordinate
                        if (_isFunction[y * Size + x] || i >= data.Length * 8)
                        {
                            continue;
                        }

                        _modules[y * Size + x] = GetBit(data[(uint)i >> 3], 7 - (i & 7));
                        i++;
                        // If this QR code has any remainder bits (0 to 7), they were assigned as
                        // 0/false/light by the constructor and are left unchanged by this method
                    }
                }
            }
            Debug.Assert(i == data.Length * 8);
        }


        // XORs the codeword modules in this QR code with the given mask pattern.
        // The function modules must be marked and the codeword bits must be drawn
        // before masking. Due to the arithmetic of XOR, calling applyMask() with
        // the same mask value a second time will undo the mask. A final well-formed
        // QR code needs exactly one (not zero, two, etc.) mask applied.
        private void ApplyMask(uint mask)
        {
            if (mask > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(mask), "Mask value out of range");
            }

            var index = 0;
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var invert = false;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        case 7: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    _modules[index] ^= invert & !_isFunction[index];
                    index++;
                }
            }
        }


        // A messy helper function for the constructor. This QR code must be in an unmasked state when this
        // method is called. The given argument is the requested mask, which is -1 for auto or 0 to 7 for fixed.
        // This method applies and returns the actual mask chosen, from 0 to 7.
        private int HandleConstructorMasking(int mask)
        {
            if (mask == -1)
            {
                // Automatically choose best mask
                var minPenalty = int.MaxValue;
                for (uint i = 0; i < 8; i++)
                {
                    ApplyMask(i);
                    DrawFormatBits(i);
                    var penalty = GetPenaltyScore();
                    if (penalty < minPenalty)
                    {
                        mask = (int)i;
                        minPenalty = penalty;
                    }
                    ApplyMask(i);  // Undoes the mask due to XOR
                }
            }
            Debug.Assert(0 <= mask && mask <= 7);
            ApplyMask((uint)mask);  // Apply the final choice of mask
            DrawFormatBits((uint)mask);  // Overwrite old format bits
            return mask;  // The caller shall assign this value to the final-declared field
        }


        // Calculates and returns the penalty score based on state of this QR code's current modules.
        // This is used by the automatic mask choice algorithm to find the mask pattern that yields the lowest score.
        private int GetPenaltyScore()
        {
            var result = 0;
            var finderPenalty = new FinderPenalty(Size);

            // Adjacent modules in row having same color, and finder-like patterns
            var index = 0;
            for (var y = 0; y < Size; y++)
            {
                var runColor = false;
                var runX = 0;
                finderPenalty.Reset();
                for (var x = 0; x < Size; x++)
                {
                    if (_modules[index] == runColor)
                    {
                        runX++;
                        if (runX == 5)
                        {
                            result += PenaltyN1;
                        }
                        else if (runX > 5)
                        {
                            result++;
                        }
                    }
                    else
                    {
                        finderPenalty.AddHistory(runX);
                        if (!runColor)
                        {
                            result += finderPenalty.CountPatterns() * PenaltyN3;
                        }

                        runColor = _modules[index];
                        runX = 1;
                    }

                    index++;
                }
                result += finderPenalty.TerminateAndCount(runColor, runX) * PenaltyN3;
            }

            // Adjacent modules in column having same color, and finder-like patterns
            for (var x = 0; x < Size; x++)
            {
                index = x;
                var runColor = false;
                var runY = 0;
                finderPenalty.Reset();
                for (var y = 0; y < Size; y++)
                {
                    if (_modules[index] == runColor)
                    {
                        runY++;
                        if (runY == 5)
                        {
                            result += PenaltyN1;
                        }
                        else if (runY > 5)
                        {
                            result++;
                        }
                    }
                    else
                    {
                        finderPenalty.AddHistory(runY);
                        if (!runColor)
                        {
                            result += finderPenalty.CountPatterns() * PenaltyN3;
                        }

                        runColor = _modules[index];
                        runY = 1;
                    }

                    index += Size;
                }
                result += finderPenalty.TerminateAndCount(runColor, runY) * PenaltyN3;
            }

            // 2*2 blocks of modules having same color
            index = 0;
            for (var y = 0; y < Size - 1; y++)
            {
                for (var x = 0; x < Size - 1; x++)
                {
                    var color = _modules[index];
                    if (color == _modules[index + 1] &&
                          color == _modules[index + Size] &&
                          color == _modules[index + Size + 1])
                    {
                        result += PenaltyN2;
                    }

                    index++;
                }

                index++;
            }

            // Balance of dark and light modules
            var dark = 0;
            index = 0;
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    if (_modules[index])
                    {
                        dark++;
                    }

                    index++;
                }
            }
            var total = Size * Size;  // Note that size is odd, so dark/total != 1/2
                                      // Compute the smallest integer k >= 0 such that (45-5k)% <= dark/total <= (55+5k)%
            var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            result += k * PenaltyN4;
            return result;
        }


        #endregion


        #region Private helper functions

        // Returns an ascending list of positions of alignment patterns for this version number.
        // Each position is in the range [0,177), and are used on both the x and y axes.
        // This could be implemented as lookup table of 40 variable-length lists of unsigned bytes.
        private int[] GetAlignmentPatternPositions()
        {
            if (Version == 1)
            {
                return new int[] { };
            }

            var numAlign = Version / 7 + 2;
            int step;
            if (Version == 32)  // Special snowflake
            {
                step = 26;
            }
            else  // step = ceil[(size - 13) / (numAlign*2 - 2)] * 2
            {
                step = (Version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
            }

            var result = new int[numAlign];
            result[0] = 6;
            for (int i = result.Length - 1, pos = Size - 7; i >= 1; i--, pos -= step)
            {
                result[i] = pos;
            }

            return result;
        }

        // Returns the number of data bits that can be stored in a QR code of the given version number, after
        // all function modules are excluded. This includes remainder bits, so it might not be a multiple of 8.
        // The result is in the range [208, 29648]. This could be implemented as a 40-entry lookup table.
        private static int GetNumRawDataModules(int ver)
        {
            if (ver < MinVersion || ver > MaxVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(ver), "Version number out of range");
            }

            var size = ver * 4 + 17;
            var result = size * size;   // Number of modules in the whole QR code square
            result -= 8 * 8 * 3;        // Subtract the three finders with separators
            result -= 15 * 2 + 1;       // Subtract the format information and dark module
            result -= (size - 16) * 2;  // Subtract the timing patterns (excluding finders)
                                        // The five lines above are equivalent to: int result = (16 * ver + 128) * ver + 64;

            if (ver < 2)
            {
                return result;
            }

            var numAlign = ver / 7 + 2;
            result -= (numAlign - 1) * (numAlign - 1) * 25;  // Subtract alignment patterns not overlapping with timing patterns
            result -= (numAlign - 2) * 2 * 20;  // Subtract alignment patterns that overlap with timing patterns
            // The two lines above are equivalent to: result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7)
            {
                result -= 6 * 3 * 2;  // Subtract version information
            }
            Debug.Assert(208 <= result && result <= 29648);
            return result;
        }


        // Returns the number of 8-bit data (i.e. not error correction) codewords contained in any
        // QR code of the given version number and error correction level, with remainder bits discarded.
        // This stateless pure function could be implemented as a (40*4)-cell lookup table.
        internal static int GetNumDataCodewords(int ver, Ecc ecl)
        {
            return GetNumRawDataModules(ver) / 8
                - EccCodewordsPerBlock[ecl.Ordinal, ver]
                * NumErrorCorrectionBlocks[ecl.Ordinal, ver];
        }

        // Helper class for getPenaltyScore().
        // Internal the run history is organized in reverse order
        // (compared to Nayuki's code) to avoid the copying when
        // adding to the history.
        private struct FinderPenalty
        {
            private int _length;
            private readonly short[] _runHistory;
            private readonly int _size;

            internal FinderPenalty(int size)
            {
                _length = 0;
                _runHistory = new short[177];
                _size = size;
            }

            internal void Reset()
            {
                _length = 0;
            }

            // Can only be called immediately after a light run is added, and
            // returns either 0, 1, or 2.
            internal int CountPatterns()
            {
                if (_length < 7)
                {
                    return 0;
                }
                int n = _runHistory[_length - 6];
                Debug.Assert(n <= _size * 3);
                var core = n > 0
                           && _runHistory[_length - 5] == n
                           && _runHistory[_length - 4] == n * 3
                           && _runHistory[_length - 3] == n
                           && _runHistory[_length - 2] == n;
                return (core && _runHistory[_length - 7] >= n * 4 && _runHistory[_length - 1] >= n ? 1 : 0)
                     + (core && _runHistory[_length - 1] >= n * 4 && _runHistory[_length - 7] >= n ? 1 : 0);
            }

            // Pushes the given value to the front and drops the last value.
            internal int TerminateAndCount(bool currentRunColor, int currentRunLength)
            {
                if (currentRunColor)
                {
                    // Terminate dark run
                    AddHistory(currentRunLength);
                    currentRunLength = 0;
                }
                currentRunLength += _size;  // Add light border to final run
                AddHistory(currentRunLength);
                return CountPatterns();
            }

            // Adds the given value to the run history
            internal void AddHistory(int run)
            {
                _runHistory[_length] = (short)run;
                _length++;
            }
        }


        private static byte[] CopyOfRange(byte[] original, int from, int to)
        {
            var result = new byte[to - from];
            Array.Copy(original, from, result, 0, to - from);
            return result;
        }


        private static byte[] CopyOf(byte[] original, int newLength)
        {
            var result = new byte[newLength];
            Array.Copy(original, result, Math.Min(original.Length, newLength));
            return result;
        }


        // Returns true iff the i'th bit of x is set to 1.
        private static bool GetBit(uint x, int i)
        {
            return ((x >> i) & 1) != 0;
        }

        #endregion


        #region Constants and tables

        /// <summary>
        /// The minimum version (size) supported in the QR Code Model 2 standard – namely 1.
        /// </summary>
        /// <value>The minimum version.</value>
        public const int MinVersion = 1;

        /// <summary>
        /// The maximum version (size) supported in the QR Code Model 2 standard – namely 40.
        /// </summary>
        /// <value>The maximum version.</value>
        public const int MaxVersion = 40;


        // For use in getPenaltyScore(), when evaluating which mask is best.
        private const int PenaltyN1 = 3;
        private const int PenaltyN2 = 3;
        private const int PenaltyN3 = 40;
        private const int PenaltyN4 = 10;


        private static readonly byte[,] EccCodewordsPerBlock = {
		    // Version: (note that index 0 is for padding, and is set to an illegal value)
            //  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40     Error correction level		    { 255,  7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },  // Low
            { 255,  7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },  // Low
		    { 255, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },  // Medium
		    { 255, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },  // Quartile
		    { 255, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 }   // High
	    };

        private static readonly byte[,] NumErrorCorrectionBlocks = {
		    // Version: (note that index 0 is for padding, and is set to an illegal value)
		    //  0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40     Error correction level
		    { 255, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4,  4,  4,  4,  4,  6,  6,  6,  6,  7,  8,  8,  9,  9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },  // Low
		    { 255, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5,  5,  8,  9,  9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },  // Medium
		    { 255, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8,  8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },  // Quartile
		    { 255, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 }   // High
	    };

        #endregion


        #region Public helper enumeration

        /// <summary>
        /// Error correction level in QR code symbol.
        /// </summary>
        public sealed class Ecc
        {
            /// <summary>
            /// Low error correction level. The QR code can tolerate about 7% erroneous codewords.
            /// </summary>
            /// <value>Low error correction level.</value>
            public static readonly Ecc Low = new Ecc(0, 1);

            /// <summary>
            /// Medium error correction level. The QR code can tolerate about 15% erroneous codewords.
            /// </summary>
            /// <value>Medium error correction level.</value>
            public static readonly Ecc Medium = new Ecc(1, 0);

            /// <summary>
            /// Quartile error correction level. The QR code can tolerate about 25% erroneous codewords.
            /// </summary>
            /// <value>Quartile error correction level.</value>
            public static readonly Ecc Quartile = new Ecc(2, 3);

            /// <summary>
            /// High error correction level. The QR code can tolerate about 30% erroneous codewords.
            /// </summary>
            /// <value>High error correction level.</value>
            public static readonly Ecc High = new Ecc(3, 2);


            internal static readonly Ecc[] AllValues = { Low, Medium, Quartile, High };

            /// <summary>
            /// Ordinal number of error correction level (in the range 0 to 3).
            /// </summary>
            /// <remarks>
            /// Higher number represent a higher amount of error tolerance.
            /// </remarks>
            /// <value>Ordinal number.</value>
            public int Ordinal { get; }

            // In the range 0 to 3 (unsigned 2-bit integer).
            internal uint FormatBits { get; }

            // Constructor.
            private Ecc(int ordinal, uint fb)
            {
                Ordinal = ordinal;
                FormatBits = fb;
            }
        }


        #endregion
    }
}

/* 
 * QR code generator library (.NET)
 *
 * Copyright (c) Manuel Bleichenbacher (MIT License)
 * https://github.com/manuelbl/QrCodeGenerator
 * Copyright (c) Project Nayuki (MIT License)
 * https://www.nayuki.io/page/qr-code-generator-library
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
 * IN THE SOFTWARE.
 */


namespace OmniMusicQr
{
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
    /// <summary>
    /// Represents a segment of character/binary/control data in a QR code symbol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The easiest way to deal with QR code segments is to call
    /// <see cref="QrCode.EncodeText"/> or <see cref="QrCode.EncodeBinary"/>, and not
    /// to use instances of this class directly. The mid-level way is to take the payload
    /// data and call a static factory function such as <see cref="MakeNumeric(string)"/>.
    /// The low-level way is to custom-make the bit array and call the
    /// <see cref="QrSegment(Mode, int, BitArray)"/> constructor with appropriate values.
    /// </para>
    /// <para>
    /// This segment class imposes no length restrictions, but QR codes have restrictions.
    /// Even in the most favorable conditions, a QR code can only hold 7089 characters of data.
    /// Any segment longer than this is meaningless for the purpose of generating QR codes.
    /// </para>
    /// <para>
    /// This class can represent kanji mode segments, but provides no help in encoding them
    /// - see <see cref="QrSegmentAdvanced"/> for full kanji support.
    /// </para>
    /// <para>
    /// Instances of this class are immutable.
    /// </para>
    /// </remarks>
    public class QrSegment
    {
        #region Static factory functions (mid level)

        /// <summary>
        /// Creates a segment representing the specified binary data
        /// encoded in byte mode. All input byte arrays are acceptable.
        /// <para>
        /// Any text string can be converted to UTF-8 bytes (using <c>Encoding.UTF8.GetBytes(str)</c>)
        /// and encoded as a byte mode segment.
        /// </para>
        /// </summary>
        /// <param name="data">The binary data to encode.</param>
        /// <returns>The created segment containing the specified data.</returns>
        /// <exception cref="ArgumentNullException"><c>data</c> is <c>null</c>.</exception>
        public static QrSegment MakeBytes(byte[] data)
        {
            Objects.RequireNonNull(data);
            var ba = new BitArray(0);
            foreach (var b in data)
            {
                ba.AppendBits(b, 8);
            }

            return new QrSegment(Mode.Byte, data.Length, ba);
        }


        /// <summary>
        /// Creates a segment representing the specified string of decimal digits.
        /// The segment is encoded in numeric mode.
        /// </summary>
        /// <param name="digits">The text to encode, consisting of digits from 0 to 9 only.</param>
        /// <returns>The created segment containing the text.</returns>
        /// <exception cref="ArgumentNullException"><c>digits</c> is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><c>digits</c> contains non-digit characters</exception>
        public static QrSegment MakeNumeric(string digits)
        {
            Objects.RequireNonNull(digits);
            if (!IsNumeric(digits))
            {
                throw new ArgumentOutOfRangeException(nameof(digits), "String contains non-numeric characters");
            }

            var ba = new BitArray(0);
            for (var i = 0; i < digits.Length;)
            {
                // Consume up to 3 digits per iteration
                var n = Math.Min(digits.Length - i, 3);
                ba.AppendBits(uint.Parse(digits.Substring(i, n)), n * 3 + 1);
                i += n;
            }
            return new QrSegment(Mode.Numeric, digits.Length, ba);
        }


        /// <summary>
        /// Creates a segment representing the specified text string.
        /// The segment is encoded in alphanumeric mode.
        /// <para>
        /// Allowed characters are: 0 to 9, A to Z (uppercase only), space,
        /// dollar, percent, asterisk, plus, hyphen, period, slash, colon.
        /// </para>
        /// </summary>
        /// <param name="text">The text to encode, consisting of allowed characters only.</param>
        /// <returns>The created segment containing the text.</returns>
        /// <exception cref="ArgumentNullException"><c>text</c> is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><c>text</c> contains non-encodable characters.</exception>
        public static QrSegment MakeAlphanumeric(string text)
        {
            Objects.RequireNonNull(text);
            if (!IsAlphanumeric(text))
            {
                throw new ArgumentOutOfRangeException(nameof(text), "String contains unencodable characters in alphanumeric mode");
            }

            var ba = new BitArray(0);
            int i;
            for (i = 0; i <= text.Length - 2; i += 2)
            {
                // Process groups of 2
                var temp = (uint)AlphanumericCharset.IndexOf(text[i]) * 45;
                temp += (uint)AlphanumericCharset.IndexOf(text[i + 1]);
                ba.AppendBits(temp, 11);
            }
            if (i < text.Length)  // 1 character remaining
            {
                ba.AppendBits((uint)AlphanumericCharset.IndexOf(text[i]), 6);
            }

            return new QrSegment(Mode.Alphanumeric, text.Length, ba);
        }


        /// <summary>
        /// Creates a list of zero or more segments representing the specified text string.
        /// <para>
        /// The text may contain the full range of Unicode characters.
        /// </para>
        /// <para>
        /// The result may multiple segments with various encoding modes in order to minimize the length of the bit stream.
        /// </para>
        /// </summary>
        /// <param name="text">The text to be encoded.</param>
        /// <returns>The created mutable list of segments representing the specified text.</returns>
        /// <exception cref="ArgumentNullException"><c>text</c> is <c>null</c>.</exception>
        /// <remarks>
        /// The current implementation does not use multiple segments.
        /// </remarks>
        public static List<QrSegment> MakeSegments(string text)
        {
            Objects.RequireNonNull(text);

            // Select the most efficient segment encoding automatically
            var result = new List<QrSegment>();
            if (text == "")
            {
                // Leave result empty
            }
            else if (IsNumeric(text))
            {
                result.Add(MakeNumeric(text));
            }
            else if (IsAlphanumeric(text))
            {
                result.Add(MakeAlphanumeric(text));
            }
            else
            {
                result.Add(MakeBytes(Encoding.UTF8.GetBytes(text)));
            }

            return result;
        }


        /// <summary>
        /// Creates a segment representing an Extended Channel Interpretation
        /// (ECI) designator with the specified assignment value.
        /// </summary>
        /// <param name="assignVal">The ECI assignment number (see the AIM ECI specification).</param>
        /// <returns>The created segment containing the data.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><c>assignVal</c>is outside the range [0, 10<sup>6</sup>).</exception>
        public static QrSegment MakeEci(int assignVal)
        {
            var ba = new BitArray(0);
            if (assignVal < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(assignVal), "ECI assignment value out of range");
            }

            if (assignVal < 1 << 7)
            {
                ba.AppendBits((uint)assignVal, 8);
            }
            else if (assignVal < 1 << 14)
            {
                ba.AppendBits(2, 2);
                ba.AppendBits((uint)assignVal, 14);
            }
            else if (assignVal < 1_000_000)
            {
                ba.AppendBits(6, 3);
                ba.AppendBits((uint)assignVal, 21);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(assignVal), "ECI assignment value out of range");
            }

            return new QrSegment(Mode.Eci, 0, ba);
        }

        /// <summary>
        /// Tests whether the specified string can be encoded as a segment in numeric mode.
        /// <para>
        /// A string is encodable iff each character is in the range "0" to "9".
        /// </para>
        /// </summary>
        /// <param name="text">the string to test for encodability (not <c>null</c>)</param>
        /// <returns><c>true</c> iff each character is in the range "0" to "9".</returns>
        /// <exception cref="NullReferenceException">if the string is <c>null</c></exception>
        /// <seealso cref="MakeNumeric(string)"/>
        public static bool IsNumeric(string text)
        {
            return NumericRegex.IsMatch(text);
        }

        /// <summary>
        /// Tests whether the specified string can be encoded as a segment in alphanumeric mode.
        /// <para>
        /// A string is encodable iff each character is in the range "0" to "9", "A" to "Z" (uppercase only),
        /// space, dollar, percent, asterisk, plus, hyphen, period, slash, colon.
        /// </para>
        /// </summary>
        /// <param name="text">the string to test for encodability (not <c>null</c>)</param>
        /// <returns><c>true</c> iff each character is in the alphanumeric mode character set.</returns>
        /// <exception cref="NullReferenceException">if the string is <c>null</c></exception>
        /// <seealso cref="MakeAlphanumeric(string)"/>
        public static bool IsAlphanumeric(string text)
        {
            return AlphanumericRegex.IsMatch(text);
        }

        #endregion


        #region Instance fields

        /// <summary>The encoding mode of this segment.</summary>
        /// <value>Encoding mode.</value>
        public Mode EncodingMode { get; }

        /// <summary>
        /// The length of this segment's unencoded data.
        /// <para>
        /// Measured in characters for numeric/alphanumeric/kanji mode,
        /// bytes for byte mode, and 0 for ECI mode.
        /// </para>
        /// <para>
        /// Different from the data's bit length.
        /// </para>
        /// </summary>
        /// <value>Length of the segment's unencoded data.</value>
        public int NumChars { get; }

        // The data bits of this segment. Not null. Accessed through GetData().
        private readonly BitArray _data;

        #endregion


        #region Constructor (low level)

        /// <summary>
        /// Initializes a QR code segment with the specified attributes and data.
        /// <para>
        /// The character count <paramref name="numChars"/> must agree with the mode and the bit array length,
        /// but the constraint isn't checked. The specified bit array is cloned.
        /// </para>
        /// </summary>
        /// <param name="mode">The segment mode used to encode this segment.</param>
        /// <param name="numChars">The data length in characters or bytes (depending on the segment mode).</param>
        /// <param name="data">The data bits.</param>
        /// <exception cref="ArgumentNullException"><paramref name="mode"/> or <paramref name="data"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="numChars"/> is negative.</exception>
        public QrSegment(Mode mode, int numChars, BitArray data)
        {
            EncodingMode = Objects.RequireNonNull(mode);
            Objects.RequireNonNull(data);
            if (numChars < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(numChars), "Invalid value");
            }

            NumChars = numChars;
            _data = (BitArray)data.Clone();  // Make defensive copy
        }

        #endregion


        #region Methods

        /// <summary>
        /// Returns a copy of this segment's data bits.
        /// </summary>
        /// <returns>A copy of the data bits.</returns>
        public BitArray GetData()
        {
            return (BitArray)_data.Clone();  // Make defensive copy
        }


        // Calculates the number of bits needed to encode the given segments at the given version.
        // Returns a non-negative number if successful. Otherwise returns -1 if a segment has too
        // many characters to fit its length field, or the total bits exceeds int.MaxValue.
        internal static int GetTotalBits(List<QrSegment> segments, int version)
        {
            Objects.RequireNonNull(segments);
            long result = 0;
            foreach (var seg in segments)
            {
                Objects.RequireNonNull(seg);
                var ccBits = seg.EncodingMode.NumCharCountBits(version);
                if (seg.NumChars >= 1 << ccBits)
                {
                    return -1;  // The segment's length doesn't fit the field's bit width
                }

                result += 4L + ccBits + seg._data.Length;
                if (result > int.MaxValue)
                {
                    return -1;  // The sum will overflow an int type
                }
            }
            return (int)result;
        }

        #endregion


        #region Constants

        // Describes precisely all strings that are encodable in numeric mode.
        private static readonly Regex NumericRegex = new Regex("^[0-9]*$", RegexOptions.Compiled);

        // Describes precisely all strings that are encodable in alphanumeric mode.
        private static readonly Regex AlphanumericRegex = new Regex("^[A-Z0-9 $%*+./:-]*$", RegexOptions.Compiled);


        // The set of all legal characters in alphanumeric mode, where
        // each character value maps to the index in the string.
        internal const string AlphanumericCharset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

        #endregion


        #region Public helper enumeration

        /// <summary>
        /// Segment encoding mode.
        /// <para>
        /// Describes how text or binary data is encoded into bits.
        /// </para>
        /// </summary>
        public sealed class Mode
        {
            /// <summary>
            /// Numeric encoding mode.
            /// </summary>
            /// <value>Numeric encoding mode.</value>
            public static readonly Mode Numeric = new Mode(0x1, 10, 12, 14);

            /// <summary>
            /// Alphanumeric encoding mode.
            /// </summary>
            /// <value>Alphanumeric encoding mode.</value>
            public static readonly Mode Alphanumeric = new Mode(0x2, 9, 11, 13);

            /// <summary>
            /// Byte encoding mode.
            /// </summary>
            /// <value>Byte encoding mode.</value>
            public static readonly Mode Byte = new Mode(0x4, 8, 16, 16);

            /// <summary>
            /// Kanji encoding mode.
            /// </summary>
            /// <value>Kanji encoding mode.</value>
            public static readonly Mode Kanji = new Mode(0x8, 8, 10, 12);

            /// <summary>
            /// ECI encoding mode.
            /// </summary>
            /// <value>ECI encoding mode.</value>
            public static readonly Mode Eci = new Mode(0x7, 0, 0, 0);


            /// <summary>
            /// Mode indicator value.
            /// <para>
            /// 4 bit value in the QR segment header indicating the encoding mode.
            /// </para>
            /// </summary>
            /// <value>Mode indicator value</value>
            internal uint ModeBits { get; }


            /// <summary>
            /// Array of character count bit length.
            /// <para>
            /// Number of bits for character count in QR segment header.
            /// The three array values apply to versions 0 to 9, 10 to 26 and 27 to 40
            /// respectively. All array values are in the range [0, 16].
            /// </para>
            /// </summary>
            /// <value>Array of character count bit length</value>
            private int[] NumBitsCharCount { get; }


            /// <summary>
            /// Returns the bit length of the character count in the QR segment header
            /// for the specified QR code version. The result is in the range [0, 16].
            /// </summary>
            /// <param name="ver">the QR code version (between 1 and 40)</param>
            /// <returns></returns>
            internal int NumCharCountBits(int ver)
            {
                Debug.Assert(QrCode.MinVersion <= ver && ver <= QrCode.MaxVersion);
                return NumBitsCharCount[(ver + 7) / 17];
            }

            // private constructor to initializes the constants
            private Mode(uint modeBits, params int[] numBitsCharCount)
            {
                ModeBits = modeBits;
                NumBitsCharCount = numBitsCharCount;
            }
        }

        #endregion

    }

}

/* 
 * QR code generator library (.NET)
 *
 * Copyright (c) Manuel Bleichenbacher (MIT License)
 * https://github.com/manuelbl/QrCodeGenerator
 * Copyright (c) Project Nayuki (MIT License)
 * https://www.nayuki.io/page/qr-code-generator-library
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
 * IN THE SOFTWARE.
 */


namespace OmniMusicQr
{
using System;
using System.Diagnostics;
    /// <summary>
    /// Computes the Reed-Solomon error correction codewords for a sequence of data codewords at a given degree.
    /// <para>
    /// Instances are immutable, and the state only depends on the degree.
    /// This class is useful because all data blocks in a QR code share the same the divisor polynomial.
    /// </para>
    /// </summary>
    internal class ReedSolomonGenerator
    {
        #region Fields

        // Coefficients of the divisor polynomial, stored from highest to lowest power, excluding the leading term which
        // is always 1. For example the polynomial x^3 + 255x^2 + 8x + 93 is stored as the uint8 array {255, 8, 93}.
        private readonly byte[] _coefficients;

        #endregion


        #region Constructors

        /// <summary>
        /// Initializes a new Reed-Solomon ECC generator for the specified degree. 
        /// </summary>
        /// <remarks>
        /// This could be implemented as a lookup table over all possible parameter values, instead of as an algorithm.
        /// </remarks>
        /// <param name="degree">The divisor polynomial degree (between 1 and 255).</param>
        /// <exception cref="ArgumentOutOfRangeException"><c>degree</c> &lt; 1 or <c>degree</c> &gt; 255</exception>
        internal ReedSolomonGenerator(int degree)
        {
            if (degree < 1 || degree > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(degree), "Degree out of range");
            }

            // Start with the monomial x^0
            _coefficients = new byte[degree];
            _coefficients[degree - 1] = 1;

            // Compute the product polynomial (x - r^0) * (x - r^1) * (x - r^2) * ... * (x - r^{degree-1}),
            // drop the highest term, and store the rest of the coefficients in order of descending powers.
            // Note that r = 0x02, which is a generator element of this field GF(2^8/0x11D).
            uint root = 1;
            for (var i = 0; i < degree; i++)
            {
                // Multiply the current product by (x - r^i)
                for (var j = 0; j < _coefficients.Length; j++)
                {
                    _coefficients[j] = Multiply(_coefficients[j], root);
                    if (j + 1 < _coefficients.Length)
                    {
                        _coefficients[j] ^= _coefficients[j + 1];
                    }
                }
                root = Multiply(root, 0x02);
            }
        }

        #endregion


        #region Methods

        /// <summary>
        /// Computes the Reed-Solomon error correction codewords for the specified
        /// sequence of data codewords.
        /// <para>
        /// This method does not alter this object's state (as it is immutable).
        /// </para>
        /// </summary>
        /// <param name="data">The sequence of data codewords.</param>
        /// <returns>The Reed-Solomon error correction codewords, as a byte array.</returns>
        /// <exception cref="ArgumentNullException">If <c>data</c> is <c>null</c>.</exception>
        internal byte[] GetRemainder(byte[] data)
        {
            Objects.RequireNonNull(data);

            // Compute the remainder by performing polynomial division
            var result = new byte[_coefficients.Length];
            foreach (var b in data)
            {
                var factor = (uint)(b ^ result[0]);
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] ^= Multiply(_coefficients[i], factor);
                }
            }
            return result;
        }

        #endregion


        #region Static functions

        // Returns the product of the two given field elements modulo GF(2^8/0x11D). The arguments and result
        // are unsigned 8-bit integers. This could be implemented as a lookup table of 256*256 entries of uint8.
        private static byte Multiply(uint x, uint y)
        {
            Debug.Assert(x >> 8 == 0 && y >> 8 == 0);
            // Russian peasant multiplication
            uint z = 0;
            for (var i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            Debug.Assert(z >> 8 == 0);
            return (byte)z;
        }

        #endregion
    }
}

/*
Bundled MP3 decoder: https://github.com/naudio/NLayer
MIT License

Copyright (c) 2018 Mark Heath, Andrew Ward & Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

#nullable disable


namespace OmniMusicMpeg.Decoder
{
using System;
    class BitReservoir
    {
        // Per the spec, the maximum buffer size for layer III is 7680 bits, which is 960 bytes.
        // The only catch is if we're decoding a "free" frame, which could be a lot more (since
        //  some encoders allow higher bitrates to maintain audio transparency).
        byte[] _buf = new byte[8192];
        int _start = 0, _end = -1, _bitsLeft = 0;
        long _bitsRead = 0L;

        static int GetSlots(IMpegFrame frame)
        {
            var cnt = frame.FrameLength - 4;
            if (frame.HasCrc) cnt -= 2;

            if (frame.Version == MpegVersion.Version1 && frame.ChannelMode != MpegChannelMode.Mono) return cnt - 32;
            if (frame.Version > MpegVersion.Version1 && frame.ChannelMode == MpegChannelMode.Mono) return cnt - 9;
            return cnt - 17;

        }

        public bool AddBits(IMpegFrame frame, int overlap)
        {
            var originalEnd = _end;

            var slots = GetSlots(frame);
            while (--slots >= 0)
            {
                var temp = frame.ReadBits(8);
                if (temp == -1) throw new System.IO.InvalidDataException("Frame did not have enough bytes!");
                _buf[++_end] = (byte)temp;
                if (_end == _buf.Length - 1) _end = -1;
            }

            _bitsLeft = 8;
            if (originalEnd == -1)
            {
                // it's either the start of the stream or we've reset...  only return true if overlap says this frame is enough
                return overlap == 0;
            }
            else
            {
                // it's not the start of the stream so calculate _start based on whether we have enough bytes left

                // if we have enough bytes, reset start to match overlap
                if ((originalEnd + 1 - _start + _buf.Length) % _buf.Length >= overlap)
                {
                    _start = (originalEnd + 1 - overlap + _buf.Length) % _buf.Length;
                    return true;
                }
                // otherwise, just set start to match the start of the frame (we probably skipped a frame)
                else
                {
                    _start = originalEnd + overlap;
                    return false;
                }
            }
        }

        public int GetBits(int count)
        {
            int bitsRead;
            var bits = TryPeekBits(count, out bitsRead);
            if (bitsRead < count) throw new System.IO.InvalidDataException("Reservoir did not have enough bytes!");

            SkipBits(count);

            return bits;
        }

        public int Get1Bit()
        {
            // this is an optimized single-bit reader
            if (_bitsLeft == 0) throw new System.IO.InvalidDataException("Reservoir did not have enough bytes!");

            --_bitsLeft;
            ++_bitsRead;
            var val = (_buf[_start] >> _bitsLeft) & 1;

            if (_bitsLeft == 0 && (_start = (_start + 1) % _buf.Length) != _end + 1)
            {
                _bitsLeft = 8;
            }

            return val;
        }

        public int TryPeekBits(int count, out int readCount)
        {
            if (count < 0 || count > 32) throw new ArgumentOutOfRangeException("count", "Must return between 0 and 32 bits!");

            // if we don't have any bits left, just return no bits read
            if (_bitsLeft == 0 || count == 0)
            {
                readCount = 0;
                return 0;
            }

            // get bits from the current start of the reservoir
            var bits = (int)_buf[_start];
            if (count < _bitsLeft)
            {
                // just grab the bits, adjust the "left" count, and return
                bits >>= _bitsLeft - count;
                bits &= ((1 << count) - 1);
                readCount = count;
                return bits;
            }

            // we have to do it the hard way...
            bits &= ((1 << _bitsLeft) - 1);
            count -= _bitsLeft;
            readCount = _bitsLeft;

            var resStart = _start;

            // arg... gotta grab some more bits...
            while (count > 0)
            {
                // advance the start marker, and if we just advanced it past the end of the buffer, bail
                if ((resStart = (resStart + 1) % _buf.Length) == _end + 1)
                {
                    break;
                }

                // figure out how many bits to pull from it
                var bitsToRead = Math.Min(count, 8);

                // move the existing bits over
                bits <<= bitsToRead;
                bits |= (_buf[resStart] >> ((8 - bitsToRead) % 8));

                // update our count
                count -= bitsToRead;

                // update our remaining bits
                readCount += bitsToRead;
            }

            return bits;
        }

        public int BitsAvailable
        {
            get
            {
                if (_bitsLeft > 0)
                {
                    return (((_end + _buf.Length) - _start) % _buf.Length) * 8 + _bitsLeft;
                }
                return 0;
            }
        }

        public long BitsRead
        {
            get { return _bitsRead; }
        }

        public void SkipBits(int count)
        {
            if (count > 0)
            {
                // make sure we have enough bits to skip
                if (count > BitsAvailable) throw new ArgumentOutOfRangeException("count");

                // now calculate the new positions
                var offset = (8 - _bitsLeft) + count;
                _start = ((offset / 8) + _start) % _buf.Length;
                _bitsLeft = 8 - (offset % 8);

                _bitsRead += count;
            }
        }

        public void RewindBits(int count)
        {
            _bitsLeft += count;
            _bitsRead -= count;
            while (_bitsLeft > 8)
            {
                --_start;
                _bitsLeft -= 8;
            }
            while (_start < 0)
            {
                _start += _buf.Length;
            }
        }

        public void FlushBits()
        {
            if (_bitsLeft < 8)
            {
                SkipBits(_bitsLeft);
            }
        }

        public void Reset()
        {
            _start = 0;
            _end = -1;
            _bitsLeft = 0;
        }
    }
}


namespace OmniMusicMpeg.Decoder
{
using System;
    abstract class FrameBase
    {
        static int _totalAllocation = 0;
        static internal int TotalAllocation
        {
            get { return System.Threading.Interlocked.CompareExchange(ref _totalAllocation, 0, 0); }
        }

        internal long Offset { get; private set; }
        internal int Length { get; set; }

        MpegStreamReader _reader;

        byte[] _savedBuffer;

        protected FrameBase() { }

        internal bool Validate(long offset, MpegStreamReader reader)
        {
            Offset = offset;
            _reader = reader;

            var len = Validate();

            if (len > 0)
            {
                Length = len;
                return true;
            }
            return false;
        }

        protected int Read(int offset, byte[] buffer)
        {
            return Read(offset, buffer, 0, buffer.Length);
        }

        protected int Read(int offset, byte[] buffer, int index, int count)
        {
            if (_savedBuffer != null)
            {
                if (index < 0 || index + count > buffer.Length) return 0;   // check against caller's buffer
                if (offset < 0 || offset >= _savedBuffer.Length) return 0;  // check against saved buffer
                if (offset + count > _savedBuffer.Length) count = _savedBuffer.Length - index;  // twiddle the size as needed

                Array.Copy(_savedBuffer, offset, buffer, index, count);
                return count;
            }
            else
            {
                return _reader.Read(Offset + offset, buffer, index, count);
            }
        }

        protected int ReadByte(int offset)
        {
            if (_savedBuffer != null)
            {
                if (offset < 0) throw new ArgumentOutOfRangeException();
                if (offset >= _savedBuffer.Length) return -1;

                return (int)_savedBuffer[offset];
            }
            else
            {
                return _reader.ReadByte(Offset + offset);
            }
        }

        /// <summary>
        /// Called to validate the frame header
        /// </summary>
        /// <returns>The length of the frame, or -1 if frame is invalid</returns>
        abstract protected int Validate();

        internal void SaveBuffer()
        {
            _savedBuffer = new byte[Length];
            _reader.Read(Offset, _savedBuffer, 0, Length);
            System.Threading.Interlocked.Add(ref _totalAllocation, Length);
        }

        internal void ClearBuffer()
        {
            System.Threading.Interlocked.Add(ref _totalAllocation, -Length);
            _savedBuffer = null;
        }

        /// <summary>
        /// Called when the stream is not "seek-able"
        /// </summary>
        virtual internal void Parse() { }
    }
}


namespace OmniMusicMpeg.Decoder
{
using System;
using System.Collections.Generic;
using System.Linq;
    class Huffman
    {
        #region Hardcoded Tables (from the spec)

        static readonly byte[][,] _codeTables =
        {
            new byte[,] // 1
            {
                {0x02, 0x01}, {0x00, 0x00}, {0x02, 0x01}, {0x00, 0x10},
                {0x02, 0x01}, {0x00, 0x01}, {0x00, 0x11},
            },
            new byte[,] // 2
            {
                {0x02, 0x01}, {0x00, 0x00}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x10}, {0x00, 0x01}, {0x02, 0x01}, {0x00, 0x11},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x20}, {0x00, 0x21},
                {0x02, 0x01}, {0x00, 0x12}, {0x02, 0x01}, {0x00, 0x02},
                {0x00, 0x22},
            },
            new byte[,] // 3
            {
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x00}, {0x00, 0x01},
                {0x02, 0x01}, {0x00, 0x11}, {0x02, 0x01}, {0x00, 0x10},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x20}, {0x00, 0x21},
                {0x02, 0x01}, {0x00, 0x12}, {0x02, 0x01}, {0x00, 0x02},
                {0x00, 0x22},
            },
            new byte[,] // 5
            {
                {0x02, 0x01}, {0x00, 0x00}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x10}, {0x00, 0x01}, {0x02, 0x01}, {0x00, 0x11},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x20},
                {0x00, 0x02}, {0x02, 0x01}, {0x00, 0x21}, {0x00, 0x12},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x22},
                {0x00, 0x30}, {0x02, 0x01}, {0x00, 0x03}, {0x00, 0x13},
                {0x02, 0x01}, {0x00, 0x31}, {0x02, 0x01}, {0x00, 0x32},
                {0x02, 0x01}, {0x00, 0x23}, {0x00, 0x33},
            },
            new byte[,] // 6
            {
                {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x00},
                {0x00, 0x10}, {0x00, 0x11}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x01}, {0x02, 0x01}, {0x00, 0x20}, {0x00, 0x21},
                {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x12}, {0x02, 0x01},
                {0x00, 0x02}, {0x00, 0x22}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x31}, {0x00, 0x13}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x30}, {0x00, 0x32}, {0x02, 0x01}, {0x00, 0x23},
                {0x02, 0x01}, {0x00, 0x03}, {0x00, 0x33},
            },
            new byte[,] // 7
            {
                {0x02, 0x01}, {0x00, 0x00}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x10}, {0x00, 0x01}, {0x08, 0x01}, {0x02, 0x01},
                {0x00, 0x11}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x20},
                {0x00, 0x02}, {0x00, 0x21}, {0x12, 0x01}, {0x06, 0x01},
                {0x02, 0x01}, {0x00, 0x12}, {0x02, 0x01}, {0x00, 0x22},
                {0x00, 0x30}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x31},
                {0x00, 0x13}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x03},
                {0x00, 0x32}, {0x02, 0x01}, {0x00, 0x23}, {0x00, 0x04},
                {0x0a, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x40},
                {0x00, 0x41}, {0x02, 0x01}, {0x00, 0x14}, {0x02, 0x01},
                {0x00, 0x42}, {0x00, 0x24}, {0x0c, 0x01}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x33}, {0x00, 0x43},
                {0x00, 0x50}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x34},
                {0x00, 0x05}, {0x00, 0x51}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x15}, {0x02, 0x01}, {0x00, 0x52}, {0x00, 0x25},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x44}, {0x00, 0x35},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x53}, {0x00, 0x54},
                {0x02, 0x01}, {0x00, 0x45}, {0x00, 0x55},
            },
            new byte[,] // 8
            {
                {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x00}, {0x02, 0x01},
                {0x00, 0x10}, {0x00, 0x01}, {0x02, 0x01}, {0x00, 0x11},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x21}, {0x00, 0x12},
                {0x0e, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x20},
                {0x00, 0x02}, {0x02, 0x01}, {0x00, 0x22}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x30}, {0x00, 0x03}, {0x02, 0x01},
                {0x00, 0x31}, {0x00, 0x13}, {0x0e, 0x01}, {0x08, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x32}, {0x00, 0x23},
                {0x02, 0x01}, {0x00, 0x40}, {0x00, 0x04}, {0x02, 0x01},
                {0x00, 0x41}, {0x02, 0x01}, {0x00, 0x14}, {0x00, 0x42},
                {0x0c, 0x01}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x24},
                {0x02, 0x01}, {0x00, 0x33}, {0x00, 0x50}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x43}, {0x00, 0x34}, {0x00, 0x51},
                {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x15}, {0x02, 0x01},
                {0x00, 0x05}, {0x00, 0x52}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x25}, {0x02, 0x01}, {0x00, 0x44}, {0x00, 0x35},
                {0x02, 0x01}, {0x00, 0x53}, {0x02, 0x01}, {0x00, 0x45},
                {0x02, 0x01}, {0x00, 0x54}, {0x00, 0x55},
            },
            new byte[,] // 9
            {
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x00},
                {0x00, 0x10}, {0x02, 0x01}, {0x00, 0x01}, {0x00, 0x11},
                {0x0a, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x20},
                {0x00, 0x21}, {0x02, 0x01}, {0x00, 0x12}, {0x02, 0x01},
                {0x00, 0x02}, {0x00, 0x22}, {0x0c, 0x01}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x30}, {0x00, 0x03},
                {0x00, 0x31}, {0x02, 0x01}, {0x00, 0x13}, {0x02, 0x01},
                {0x00, 0x32}, {0x00, 0x23}, {0x0c, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x41}, {0x00, 0x14}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x40}, {0x00, 0x33}, {0x02, 0x01},
                {0x00, 0x42}, {0x00, 0x24}, {0x0a, 0x01}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x04}, {0x00, 0x50},
                {0x00, 0x43}, {0x02, 0x01}, {0x00, 0x34}, {0x00, 0x51},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x15},
                {0x00, 0x52}, {0x02, 0x01}, {0x00, 0x25}, {0x00, 0x44},
                {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x05},
                {0x00, 0x54}, {0x00, 0x53}, {0x02, 0x01}, {0x00, 0x35},
                {0x02, 0x01}, {0x00, 0x45}, {0x00, 0x55},
            },
            new byte[,] // 10
            {
                {0x02, 0x01}, {0x00, 0x00}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x10}, {0x00, 0x01}, {0x0a, 0x01}, {0x02, 0x01},
                {0x00, 0x11}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x20},
                {0x00, 0x02}, {0x02, 0x01}, {0x00, 0x21}, {0x00, 0x12},
                {0x1c, 0x01}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x22}, {0x00, 0x30}, {0x02, 0x01}, {0x00, 0x31},
                {0x00, 0x13}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x03}, {0x00, 0x32}, {0x02, 0x01}, {0x00, 0x23},
                {0x00, 0x40}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x41},
                {0x00, 0x14}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x04},
                {0x00, 0x33}, {0x02, 0x01}, {0x00, 0x42}, {0x00, 0x24},
                {0x1c, 0x01}, {0x0a, 0x01}, {0x06, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x50}, {0x00, 0x05}, {0x00, 0x60},
                {0x02, 0x01}, {0x00, 0x61}, {0x00, 0x16}, {0x0c, 0x01},
                {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x43},
                {0x00, 0x34}, {0x00, 0x51}, {0x02, 0x01}, {0x00, 0x15},
                {0x02, 0x01}, {0x00, 0x52}, {0x00, 0x25}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x26}, {0x00, 0x36}, {0x00, 0x71},
                {0x14, 0x01}, {0x08, 0x01}, {0x02, 0x01}, {0x00, 0x17},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x44}, {0x00, 0x53},
                {0x00, 0x06}, {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x35}, {0x00, 0x45}, {0x00, 0x62}, {0x02, 0x01},
                {0x00, 0x70}, {0x02, 0x01}, {0x00, 0x07}, {0x00, 0x64},
                {0x0e, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x72},
                {0x00, 0x27}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x63},
                {0x02, 0x01}, {0x00, 0x54}, {0x00, 0x55}, {0x02, 0x01},
                {0x00, 0x46}, {0x00, 0x73}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x37}, {0x00, 0x65}, {0x02, 0x01},
                {0x00, 0x56}, {0x00, 0x74}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x47}, {0x02, 0x01}, {0x00, 0x66}, {0x00, 0x75},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x57}, {0x00, 0x76},
                {0x02, 0x01}, {0x00, 0x67}, {0x00, 0x77},
            },
            new byte[,] // 11
            {
                {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x00}, {0x02, 0x01},
                {0x00, 0x10}, {0x00, 0x01}, {0x08, 0x01}, {0x02, 0x01},
                {0x00, 0x11}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x20},
                {0x00, 0x02}, {0x00, 0x12}, {0x18, 0x01}, {0x08, 0x01},
                {0x02, 0x01}, {0x00, 0x21}, {0x02, 0x01}, {0x00, 0x22},
                {0x02, 0x01}, {0x00, 0x30}, {0x00, 0x03}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x31}, {0x00, 0x13}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x32}, {0x00, 0x23}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x40}, {0x00, 0x04}, {0x02, 0x01},
                {0x00, 0x41}, {0x00, 0x14}, {0x1e, 0x01}, {0x10, 0x01},
                {0x0a, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x42},
                {0x00, 0x24}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x33},
                {0x00, 0x43}, {0x00, 0x50}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x34}, {0x00, 0x51}, {0x00, 0x61}, {0x06, 0x01},
                {0x02, 0x01}, {0x00, 0x16}, {0x02, 0x01}, {0x00, 0x06},
                {0x00, 0x26}, {0x02, 0x01}, {0x00, 0x62}, {0x02, 0x01},
                {0x00, 0x15}, {0x02, 0x01}, {0x00, 0x05}, {0x00, 0x52},
                {0x10, 0x01}, {0x0a, 0x01}, {0x06, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x25}, {0x00, 0x44}, {0x00, 0x60},
                {0x02, 0x01}, {0x00, 0x63}, {0x00, 0x36}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x70}, {0x00, 0x17}, {0x00, 0x71},
                {0x10, 0x01}, {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x07}, {0x00, 0x64}, {0x00, 0x72}, {0x02, 0x01},
                {0x00, 0x27}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x53},
                {0x00, 0x35}, {0x02, 0x01}, {0x00, 0x54}, {0x00, 0x45},
                {0x0a, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x46},
                {0x00, 0x73}, {0x02, 0x01}, {0x00, 0x37}, {0x02, 0x01},
                {0x00, 0x65}, {0x00, 0x56}, {0x0a, 0x01}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x55}, {0x00, 0x57},
                {0x00, 0x74}, {0x02, 0x01}, {0x00, 0x47}, {0x00, 0x66},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x75}, {0x00, 0x76},
                {0x02, 0x01}, {0x00, 0x67}, {0x00, 0x77},
            },
            new byte[,] // 12
            {
                {0x0c, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x10},
                {0x00, 0x01}, {0x02, 0x01}, {0x00, 0x11}, {0x02, 0x01},
                {0x00, 0x00}, {0x02, 0x01}, {0x00, 0x20}, {0x00, 0x02},
                {0x10, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x21},
                {0x00, 0x12}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x22},
                {0x00, 0x31}, {0x02, 0x01}, {0x00, 0x13}, {0x02, 0x01},
                {0x00, 0x30}, {0x02, 0x01}, {0x00, 0x03}, {0x00, 0x40},
                {0x1a, 0x01}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x32}, {0x00, 0x23}, {0x02, 0x01}, {0x00, 0x41},
                {0x00, 0x33}, {0x0a, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x14}, {0x00, 0x42}, {0x02, 0x01}, {0x00, 0x24},
                {0x02, 0x01}, {0x00, 0x04}, {0x00, 0x50}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x43}, {0x00, 0x34}, {0x02, 0x01},
                {0x00, 0x51}, {0x00, 0x15}, {0x1c, 0x01}, {0x0e, 0x01},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x52},
                {0x00, 0x25}, {0x02, 0x01}, {0x00, 0x53}, {0x00, 0x35},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x60}, {0x00, 0x16},
                {0x00, 0x61}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x62},
                {0x00, 0x26}, {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x05}, {0x00, 0x06}, {0x00, 0x44}, {0x02, 0x01},
                {0x00, 0x54}, {0x00, 0x45}, {0x12, 0x01}, {0x0a, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x63}, {0x00, 0x36},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x70}, {0x00, 0x07},
                {0x00, 0x71}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x17},
                {0x00, 0x64}, {0x02, 0x01}, {0x00, 0x46}, {0x00, 0x72},
                {0x0a, 0x01}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x27},
                {0x02, 0x01}, {0x00, 0x55}, {0x00, 0x73}, {0x02, 0x01},
                {0x00, 0x37}, {0x00, 0x56}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x65}, {0x00, 0x74}, {0x02, 0x01},
                {0x00, 0x47}, {0x00, 0x66}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x75}, {0x00, 0x57}, {0x02, 0x01}, {0x00, 0x76},
                {0x02, 0x01}, {0x00, 0x67}, {0x00, 0x77},
            },
            new byte[,] // 13
            {
                {0x02, 0x01}, {0x00, 0x00}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x10}, {0x02, 0x01}, {0x00, 0x01}, {0x00, 0x11},
                {0x1c, 0x01}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x20}, {0x00, 0x02}, {0x02, 0x01}, {0x00, 0x21},
                {0x00, 0x12}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x22}, {0x00, 0x30}, {0x02, 0x01}, {0x00, 0x03},
                {0x00, 0x31}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x13},
                {0x02, 0x01}, {0x00, 0x32}, {0x00, 0x23}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x40}, {0x00, 0x04}, {0x00, 0x41},
                {0x46, 0x01}, {0x1c, 0x01}, {0x0e, 0x01}, {0x06, 0x01},
                {0x02, 0x01}, {0x00, 0x14}, {0x02, 0x01}, {0x00, 0x33},
                {0x00, 0x42}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x24},
                {0x00, 0x50}, {0x02, 0x01}, {0x00, 0x43}, {0x00, 0x34},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x51}, {0x00, 0x15},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x05}, {0x00, 0x52},
                {0x02, 0x01}, {0x00, 0x25}, {0x02, 0x01}, {0x00, 0x44},
                {0x00, 0x53}, {0x0e, 0x01}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x60}, {0x00, 0x06}, {0x02, 0x01},
                {0x00, 0x61}, {0x00, 0x16}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x80}, {0x00, 0x08}, {0x00, 0x81}, {0x10, 0x01},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x35},
                {0x00, 0x62}, {0x02, 0x01}, {0x00, 0x26}, {0x00, 0x54},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x45}, {0x00, 0x63},
                {0x02, 0x01}, {0x00, 0x36}, {0x00, 0x70}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x07}, {0x00, 0x55},
                {0x00, 0x71}, {0x02, 0x01}, {0x00, 0x17}, {0x02, 0x01},
                {0x00, 0x27}, {0x00, 0x37}, {0x48, 0x01}, {0x18, 0x01},
                {0x0c, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x18},
                {0x00, 0x82}, {0x02, 0x01}, {0x00, 0x28}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x64}, {0x00, 0x46}, {0x00, 0x72},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x84},
                {0x00, 0x48}, {0x02, 0x01}, {0x00, 0x90}, {0x00, 0x09},
                {0x02, 0x01}, {0x00, 0x91}, {0x00, 0x19}, {0x18, 0x01},
                {0x0e, 0x01}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x73}, {0x00, 0x65}, {0x02, 0x01}, {0x00, 0x56},
                {0x00, 0x74}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x47},
                {0x00, 0x66}, {0x00, 0x83}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x38}, {0x02, 0x01}, {0x00, 0x75}, {0x00, 0x57},
                {0x02, 0x01}, {0x00, 0x92}, {0x00, 0x29}, {0x0e, 0x01},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x67},
                {0x00, 0x85}, {0x02, 0x01}, {0x00, 0x58}, {0x00, 0x39},
                {0x02, 0x01}, {0x00, 0x93}, {0x02, 0x01}, {0x00, 0x49},
                {0x00, 0x86}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0xa0},
                {0x02, 0x01}, {0x00, 0x68}, {0x00, 0x0a}, {0x02, 0x01},
                {0x00, 0xa1}, {0x00, 0x1a}, {0x44, 0x01}, {0x18, 0x01},
                {0x0c, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xa2},
                {0x00, 0x2a}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x95},
                {0x00, 0x59}, {0x02, 0x01}, {0x00, 0xa3}, {0x00, 0x3a},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x4a},
                {0x00, 0x96}, {0x02, 0x01}, {0x00, 0xb0}, {0x00, 0x0b},
                {0x02, 0x01}, {0x00, 0xb1}, {0x00, 0x1b}, {0x14, 0x01},
                {0x08, 0x01}, {0x02, 0x01}, {0x00, 0xb2}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x76}, {0x00, 0x77}, {0x00, 0x94},
                {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x87},
                {0x00, 0x78}, {0x00, 0xa4}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x69}, {0x00, 0xa5}, {0x00, 0x2b}, {0x0c, 0x01},
                {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x5a},
                {0x00, 0x88}, {0x00, 0xb3}, {0x02, 0x01}, {0x00, 0x3b},
                {0x02, 0x01}, {0x00, 0x79}, {0x00, 0xa6}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x6a}, {0x00, 0xb4},
                {0x00, 0xc0}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x0c},
                {0x00, 0x98}, {0x00, 0xc1}, {0x3c, 0x01}, {0x16, 0x01},
                {0x0a, 0x01}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x1c},
                {0x02, 0x01}, {0x00, 0x89}, {0x00, 0xb5}, {0x02, 0x01},
                {0x00, 0x5b}, {0x00, 0xc2}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x2c}, {0x00, 0x3c}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xb6}, {0x00, 0x6b}, {0x02, 0x01}, {0x00, 0xc4},
                {0x00, 0x4c}, {0x10, 0x01}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xa8}, {0x00, 0x8a}, {0x02, 0x01},
                {0x00, 0xd0}, {0x00, 0x0d}, {0x02, 0x01}, {0x00, 0xd1},
                {0x02, 0x01}, {0x00, 0x4b}, {0x02, 0x01}, {0x00, 0x97},
                {0x00, 0xa7}, {0x0c, 0x01}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0xc3}, {0x02, 0x01}, {0x00, 0x7a}, {0x00, 0x99},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xc5}, {0x00, 0x5c},
                {0x00, 0xb7}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x1d},
                {0x00, 0xd2}, {0x02, 0x01}, {0x00, 0x2d}, {0x02, 0x01},
                {0x00, 0x7b}, {0x00, 0xd3}, {0x34, 0x01}, {0x1c, 0x01},
                {0x0c, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x3d},
                {0x00, 0xc6}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x6c},
                {0x00, 0xa9}, {0x02, 0x01}, {0x00, 0x9a}, {0x00, 0xd4},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xb8},
                {0x00, 0x8b}, {0x02, 0x01}, {0x00, 0x4d}, {0x00, 0xc7},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x7c}, {0x00, 0xd5},
                {0x02, 0x01}, {0x00, 0x5d}, {0x00, 0xe0}, {0x0a, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xe1}, {0x00, 0x1e},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x0e}, {0x00, 0x2e},
                {0x00, 0xe2}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xe3}, {0x00, 0x6d}, {0x02, 0x01}, {0x00, 0x8c},
                {0x00, 0xe4}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xe5},
                {0x00, 0xba}, {0x00, 0xf0}, {0x26, 0x01}, {0x10, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xf1}, {0x00, 0x1f},
                {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xaa},
                {0x00, 0x9b}, {0x00, 0xb9}, {0x02, 0x01}, {0x00, 0x3e},
                {0x02, 0x01}, {0x00, 0xd6}, {0x00, 0xc8}, {0x0c, 0x01},
                {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x4e}, {0x02, 0x01},
                {0x00, 0xd7}, {0x00, 0x7d}, {0x02, 0x01}, {0x00, 0xab},
                {0x02, 0x01}, {0x00, 0x5e}, {0x00, 0xc9}, {0x06, 0x01},
                {0x02, 0x01}, {0x00, 0x0f}, {0x02, 0x01}, {0x00, 0x9c},
                {0x00, 0x6e}, {0x02, 0x01}, {0x00, 0xf2}, {0x00, 0x2f},
                {0x20, 0x01}, {0x10, 0x01}, {0x06, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xd8}, {0x00, 0x8d}, {0x00, 0x3f},
                {0x06, 0x01}, {0x02, 0x01}, {0x00, 0xf3}, {0x02, 0x01},
                {0x00, 0xe6}, {0x00, 0xca}, {0x02, 0x01}, {0x00, 0xf4},
                {0x00, 0x4f}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xbb}, {0x00, 0xac}, {0x02, 0x01}, {0x00, 0xe7},
                {0x00, 0xf5}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xd9},
                {0x00, 0x9d}, {0x02, 0x01}, {0x00, 0x5f}, {0x00, 0xe8},
                {0x1e, 0x01}, {0x0c, 0x01}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x6f}, {0x02, 0x01}, {0x00, 0xf6}, {0x00, 0xcb},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xbc}, {0x00, 0xad},
                {0x00, 0xda}, {0x08, 0x01}, {0x02, 0x01}, {0x00, 0xf7},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x7e}, {0x00, 0x7f},
                {0x00, 0x8e}, {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x9e}, {0x00, 0xae}, {0x00, 0xcc}, {0x02, 0x01},
                {0x00, 0xf8}, {0x00, 0x8f}, {0x12, 0x01}, {0x08, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xdb}, {0x00, 0xbd},
                {0x02, 0x01}, {0x00, 0xea}, {0x00, 0xf9}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x9f}, {0x00, 0xeb}, {0x02, 0x01},
                {0x00, 0xbe}, {0x02, 0x01}, {0x00, 0xcd}, {0x00, 0xfa},
                {0x0e, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xdd},
                {0x00, 0xec}, {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xe9}, {0x00, 0xaf}, {0x00, 0xdc}, {0x02, 0x01},
                {0x00, 0xce}, {0x00, 0xfb}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xbf}, {0x00, 0xde}, {0x02, 0x01},
                {0x00, 0xcf}, {0x00, 0xee}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xdf}, {0x00, 0xef}, {0x02, 0x01}, {0x00, 0xff},
                {0x02, 0x01}, {0x00, 0xed}, {0x02, 0x01}, {0x00, 0xfd},
                {0x02, 0x01}, {0x00, 0xfc}, {0x00, 0xfe},
            },
            new byte[,] // 15
            {
                {0x10, 0x01}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x00},
                {0x02, 0x01}, {0x00, 0x10}, {0x00, 0x01}, {0x02, 0x01},
                {0x00, 0x11}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x20},
                {0x00, 0x02}, {0x02, 0x01}, {0x00, 0x21}, {0x00, 0x12},
                {0x32, 0x01}, {0x10, 0x01}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x22}, {0x02, 0x01}, {0x00, 0x30}, {0x00, 0x31},
                {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x13}, {0x02, 0x01},
                {0x00, 0x03}, {0x00, 0x40}, {0x02, 0x01}, {0x00, 0x32},
                {0x00, 0x23}, {0x0e, 0x01}, {0x06, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x04}, {0x00, 0x14}, {0x00, 0x41},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x33}, {0x00, 0x42},
                {0x02, 0x01}, {0x00, 0x24}, {0x00, 0x43}, {0x0a, 0x01},
                {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x34}, {0x02, 0x01},
                {0x00, 0x50}, {0x00, 0x05}, {0x02, 0x01}, {0x00, 0x51},
                {0x00, 0x15}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x52},
                {0x00, 0x25}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x44},
                {0x00, 0x53}, {0x00, 0x61}, {0x5a, 0x01}, {0x24, 0x01},
                {0x12, 0x01}, {0x0a, 0x01}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x35}, {0x02, 0x01}, {0x00, 0x60}, {0x00, 0x06},
                {0x02, 0x01}, {0x00, 0x16}, {0x00, 0x62}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x26}, {0x00, 0x54}, {0x02, 0x01},
                {0x00, 0x45}, {0x00, 0x63}, {0x0a, 0x01}, {0x06, 0x01},
                {0x02, 0x01}, {0x00, 0x36}, {0x02, 0x01}, {0x00, 0x70},
                {0x00, 0x07}, {0x02, 0x01}, {0x00, 0x71}, {0x00, 0x55},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x17}, {0x00, 0x64},
                {0x02, 0x01}, {0x00, 0x72}, {0x00, 0x27}, {0x18, 0x01},
                {0x10, 0x01}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x46}, {0x00, 0x73}, {0x02, 0x01}, {0x00, 0x37},
                {0x00, 0x65}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x56},
                {0x00, 0x80}, {0x02, 0x01}, {0x00, 0x08}, {0x00, 0x74},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x81}, {0x00, 0x18},
                {0x02, 0x01}, {0x00, 0x82}, {0x00, 0x28}, {0x10, 0x01},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x47},
                {0x00, 0x66}, {0x02, 0x01}, {0x00, 0x83}, {0x00, 0x38},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x75}, {0x00, 0x57},
                {0x02, 0x01}, {0x00, 0x84}, {0x00, 0x48}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x90}, {0x00, 0x19},
                {0x00, 0x91}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x92},
                {0x00, 0x76}, {0x02, 0x01}, {0x00, 0x67}, {0x00, 0x29},
                {0x5c, 0x01}, {0x24, 0x01}, {0x12, 0x01}, {0x0a, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x85}, {0x00, 0x58},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x09}, {0x00, 0x77},
                {0x00, 0x93}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x39},
                {0x00, 0x94}, {0x02, 0x01}, {0x00, 0x49}, {0x00, 0x86},
                {0x0a, 0x01}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x68},
                {0x02, 0x01}, {0x00, 0xa0}, {0x00, 0x0a}, {0x02, 0x01},
                {0x00, 0xa1}, {0x00, 0x1a}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xa2}, {0x00, 0x2a}, {0x02, 0x01}, {0x00, 0x95},
                {0x00, 0x59}, {0x1a, 0x01}, {0x0e, 0x01}, {0x06, 0x01},
                {0x02, 0x01}, {0x00, 0xa3}, {0x02, 0x01}, {0x00, 0x3a},
                {0x00, 0x87}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x78},
                {0x00, 0xa4}, {0x02, 0x01}, {0x00, 0x4a}, {0x00, 0x96},
                {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x69},
                {0x00, 0xb0}, {0x00, 0xb1}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x1b}, {0x00, 0xa5}, {0x00, 0xb2}, {0x0e, 0x01},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x5a},
                {0x00, 0x2b}, {0x02, 0x01}, {0x00, 0x88}, {0x00, 0x97},
                {0x02, 0x01}, {0x00, 0xb3}, {0x02, 0x01}, {0x00, 0x79},
                {0x00, 0x3b}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x6a}, {0x00, 0xb4}, {0x02, 0x01}, {0x00, 0x4b},
                {0x00, 0xc1}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x98},
                {0x00, 0x89}, {0x02, 0x01}, {0x00, 0x1c}, {0x00, 0xb5},
                {0x50, 0x01}, {0x22, 0x01}, {0x10, 0x01}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x5b}, {0x00, 0x2c},
                {0x00, 0xc2}, {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x0b}, {0x00, 0xc0}, {0x00, 0xa6}, {0x02, 0x01},
                {0x00, 0xa7}, {0x00, 0x7a}, {0x0a, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xc3}, {0x00, 0x3c}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x0c}, {0x00, 0x99}, {0x00, 0xb6},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x6b}, {0x00, 0xc4},
                {0x02, 0x01}, {0x00, 0x4c}, {0x00, 0xa8}, {0x14, 0x01},
                {0x0a, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x8a},
                {0x00, 0xc5}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xd0},
                {0x00, 0x5c}, {0x00, 0xd1}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xb7}, {0x00, 0x7b}, {0x02, 0x01}, {0x00, 0x1d},
                {0x02, 0x01}, {0x00, 0x0d}, {0x00, 0x2d}, {0x0c, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xd2}, {0x00, 0xd3},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x3d}, {0x00, 0xc6},
                {0x02, 0x01}, {0x00, 0x6c}, {0x00, 0xa9}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x9a}, {0x00, 0xb8},
                {0x00, 0xd4}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x8b},
                {0x00, 0x4d}, {0x02, 0x01}, {0x00, 0xc7}, {0x00, 0x7c},
                {0x44, 0x01}, {0x22, 0x01}, {0x12, 0x01}, {0x0a, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xd5}, {0x00, 0x5d},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xe0}, {0x00, 0x0e},
                {0x00, 0xe1}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x1e},
                {0x00, 0xe2}, {0x02, 0x01}, {0x00, 0xaa}, {0x00, 0x2e},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xb9},
                {0x00, 0x9b}, {0x02, 0x01}, {0x00, 0xe3}, {0x00, 0xd6},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x6d}, {0x00, 0x3e},
                {0x02, 0x01}, {0x00, 0xc8}, {0x00, 0x8c}, {0x10, 0x01},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xe4},
                {0x00, 0x4e}, {0x02, 0x01}, {0x00, 0xd7}, {0x00, 0x7d},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xe5}, {0x00, 0xba},
                {0x02, 0x01}, {0x00, 0xab}, {0x00, 0x5e}, {0x08, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xc9}, {0x00, 0x9c},
                {0x02, 0x01}, {0x00, 0xf1}, {0x00, 0x1f}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xf0}, {0x00, 0x6e},
                {0x00, 0xf2}, {0x02, 0x01}, {0x00, 0x2f}, {0x00, 0xe6},
                {0x26, 0x01}, {0x12, 0x01}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xd8}, {0x00, 0xf3}, {0x02, 0x01},
                {0x00, 0x3f}, {0x00, 0xf4}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x4f}, {0x02, 0x01}, {0x00, 0x8d}, {0x00, 0xd9},
                {0x02, 0x01}, {0x00, 0xbb}, {0x00, 0xca}, {0x08, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xac}, {0x00, 0xe7},
                {0x02, 0x01}, {0x00, 0x7e}, {0x00, 0xf5}, {0x08, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x9d}, {0x00, 0x5f},
                {0x02, 0x01}, {0x00, 0xe8}, {0x00, 0x8e}, {0x02, 0x01},
                {0x00, 0xf6}, {0x00, 0xcb}, {0x22, 0x01}, {0x12, 0x01},
                {0x0a, 0x01}, {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x0f}, {0x00, 0xae}, {0x00, 0x6f}, {0x02, 0x01},
                {0x00, 0xbc}, {0x00, 0xda}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xad}, {0x00, 0xf7}, {0x02, 0x01}, {0x00, 0x7f},
                {0x00, 0xe9}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x9e}, {0x00, 0xcc}, {0x02, 0x01}, {0x00, 0xf8},
                {0x00, 0x8f}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xdb},
                {0x00, 0xbd}, {0x02, 0x01}, {0x00, 0xea}, {0x00, 0xf9},
                {0x10, 0x01}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x9f}, {0x00, 0xdc}, {0x02, 0x01}, {0x00, 0xcd},
                {0x00, 0xeb}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xbe},
                {0x00, 0xfa}, {0x02, 0x01}, {0x00, 0xaf}, {0x00, 0xdd},
                {0x0e, 0x01}, {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xec}, {0x00, 0xce}, {0x00, 0xfb}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xbf}, {0x00, 0xed}, {0x02, 0x01},
                {0x00, 0xde}, {0x00, 0xfc}, {0x06, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xcf}, {0x00, 0xfd}, {0x00, 0xee},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xdf}, {0x00, 0xfe},
                {0x02, 0x01}, {0x00, 0xef}, {0x00, 0xff},
            },
            new byte[,] // 16
            {
                {0x02, 0x01}, {0x00, 0x00}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x10}, {0x02, 0x01}, {0x00, 0x01}, {0x00, 0x11},
                {0x2a, 0x01}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x20}, {0x00, 0x02}, {0x02, 0x01}, {0x00, 0x21},
                {0x00, 0x12}, {0x0a, 0x01}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x22}, {0x02, 0x01}, {0x00, 0x30}, {0x00, 0x03},
                {0x02, 0x01}, {0x00, 0x31}, {0x00, 0x13}, {0x0a, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x32}, {0x00, 0x23},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x40}, {0x00, 0x04},
                {0x00, 0x41}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x14},
                {0x02, 0x01}, {0x00, 0x33}, {0x00, 0x42}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x24}, {0x00, 0x50}, {0x02, 0x01},
                {0x00, 0x43}, {0x00, 0x34}, {0x8a, 0x01}, {0x28, 0x01},
                {0x10, 0x01}, {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x05}, {0x00, 0x15}, {0x00, 0x51}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x52}, {0x00, 0x25}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x44}, {0x00, 0x35}, {0x00, 0x53},
                {0x0a, 0x01}, {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x60}, {0x00, 0x06}, {0x00, 0x61}, {0x02, 0x01},
                {0x00, 0x16}, {0x00, 0x62}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x26}, {0x00, 0x54}, {0x02, 0x01},
                {0x00, 0x45}, {0x00, 0x63}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x36}, {0x00, 0x70}, {0x00, 0x71}, {0x28, 0x01},
                {0x12, 0x01}, {0x08, 0x01}, {0x02, 0x01}, {0x00, 0x17},
                {0x02, 0x01}, {0x00, 0x07}, {0x02, 0x01}, {0x00, 0x55},
                {0x00, 0x64}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x72},
                {0x00, 0x27}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x46},
                {0x00, 0x65}, {0x00, 0x73}, {0x0a, 0x01}, {0x06, 0x01},
                {0x02, 0x01}, {0x00, 0x37}, {0x02, 0x01}, {0x00, 0x56},
                {0x00, 0x08}, {0x02, 0x01}, {0x00, 0x80}, {0x00, 0x81},
                {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x18}, {0x02, 0x01},
                {0x00, 0x74}, {0x00, 0x47}, {0x02, 0x01}, {0x00, 0x82},
                {0x02, 0x01}, {0x00, 0x28}, {0x00, 0x66}, {0x18, 0x01},
                {0x0e, 0x01}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x83}, {0x00, 0x38}, {0x02, 0x01}, {0x00, 0x75},
                {0x00, 0x84}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x48},
                {0x00, 0x90}, {0x00, 0x91}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x19}, {0x02, 0x01}, {0x00, 0x09}, {0x00, 0x76},
                {0x02, 0x01}, {0x00, 0x92}, {0x00, 0x29}, {0x0e, 0x01},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x85},
                {0x00, 0x58}, {0x02, 0x01}, {0x00, 0x93}, {0x00, 0x39},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xa0}, {0x00, 0x0a},
                {0x00, 0x1a}, {0x08, 0x01}, {0x02, 0x01}, {0x00, 0xa2},
                {0x02, 0x01}, {0x00, 0x67}, {0x02, 0x01}, {0x00, 0x57},
                {0x00, 0x49}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x94},
                {0x02, 0x01}, {0x00, 0x77}, {0x00, 0x86}, {0x02, 0x01},
                {0x00, 0xa1}, {0x02, 0x01}, {0x00, 0x68}, {0x00, 0x95},
                {0xdc, 0x01}, {0x7e, 0x01}, {0x32, 0x01}, {0x1a, 0x01},
                {0x0c, 0x01}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x2a},
                {0x02, 0x01}, {0x00, 0x59}, {0x00, 0x3a}, {0x02, 0x01},
                {0x00, 0xa3}, {0x02, 0x01}, {0x00, 0x87}, {0x00, 0x78},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xa4},
                {0x00, 0x4a}, {0x02, 0x01}, {0x00, 0x96}, {0x00, 0x69},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xb0}, {0x00, 0x0b},
                {0x00, 0xb1}, {0x0a, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x1b}, {0x00, 0xb2}, {0x02, 0x01}, {0x00, 0x2b},
                {0x02, 0x01}, {0x00, 0xa5}, {0x00, 0x5a}, {0x06, 0x01},
                {0x02, 0x01}, {0x00, 0xb3}, {0x02, 0x01}, {0x00, 0xa6},
                {0x00, 0x6a}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xb4},
                {0x00, 0x4b}, {0x02, 0x01}, {0x00, 0x0c}, {0x00, 0xc1},
                {0x1e, 0x01}, {0x0e, 0x01}, {0x06, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xb5}, {0x00, 0xc2}, {0x00, 0x2c},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xa7}, {0x00, 0xc3},
                {0x02, 0x01}, {0x00, 0x6b}, {0x00, 0xc4}, {0x08, 0x01},
                {0x02, 0x01}, {0x00, 0x1d}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x88}, {0x00, 0x97}, {0x00, 0x3b}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xd1}, {0x00, 0xd2}, {0x02, 0x01},
                {0x00, 0x2d}, {0x00, 0xd3}, {0x12, 0x01}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x1e}, {0x00, 0x2e},
                {0x00, 0xe2}, {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x79}, {0x00, 0x98}, {0x00, 0xc0}, {0x02, 0x01},
                {0x00, 0x1c}, {0x02, 0x01}, {0x00, 0x89}, {0x00, 0x5b},
                {0x0e, 0x01}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x3c},
                {0x02, 0x01}, {0x00, 0x7a}, {0x00, 0xb6}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x4c}, {0x00, 0x99}, {0x02, 0x01},
                {0x00, 0xa8}, {0x00, 0x8a}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x0d}, {0x02, 0x01}, {0x00, 0xc5}, {0x00, 0x5c},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x3d}, {0x00, 0xc6},
                {0x02, 0x01}, {0x00, 0x6c}, {0x00, 0x9a}, {0x58, 0x01},
                {0x56, 0x01}, {0x24, 0x01}, {0x10, 0x01}, {0x08, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x8b}, {0x00, 0x4d},
                {0x02, 0x01}, {0x00, 0xc7}, {0x00, 0x7c}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xd5}, {0x00, 0x5d}, {0x02, 0x01},
                {0x00, 0xe0}, {0x00, 0x0e}, {0x08, 0x01}, {0x02, 0x01},
                {0x00, 0xe3}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xd0},
                {0x00, 0xb7}, {0x00, 0x7b}, {0x06, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xa9}, {0x00, 0xb8}, {0x00, 0xd4},
                {0x02, 0x01}, {0x00, 0xe1}, {0x02, 0x01}, {0x00, 0xaa},
                {0x00, 0xb9}, {0x18, 0x01}, {0x0a, 0x01}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x9b}, {0x00, 0xd6},
                {0x00, 0x6d}, {0x02, 0x01}, {0x00, 0x3e}, {0x00, 0xc8},
                {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x8c},
                {0x00, 0xe4}, {0x00, 0x4e}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xd7}, {0x00, 0xe5}, {0x02, 0x01}, {0x00, 0xba},
                {0x00, 0xab}, {0x0c, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x9c}, {0x00, 0xe6}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x6e}, {0x00, 0xd8}, {0x02, 0x01}, {0x00, 0x8d},
                {0x00, 0xbb}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xe7}, {0x00, 0x9d}, {0x02, 0x01}, {0x00, 0xe8},
                {0x00, 0x8e}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xcb},
                {0x00, 0xbc}, {0x00, 0x9e}, {0x00, 0xf1}, {0x02, 0x01},
                {0x00, 0x1f}, {0x02, 0x01}, {0x00, 0x0f}, {0x00, 0x2f},
                {0x42, 0x01}, {0x38, 0x01}, {0x02, 0x01}, {0x00, 0xf2},
                {0x34, 0x01}, {0x32, 0x01}, {0x14, 0x01}, {0x08, 0x01},
                {0x02, 0x01}, {0x00, 0xbd}, {0x02, 0x01}, {0x00, 0x5e},
                {0x02, 0x01}, {0x00, 0x7d}, {0x00, 0xc9}, {0x06, 0x01},
                {0x02, 0x01}, {0x00, 0xca}, {0x02, 0x01}, {0x00, 0xac},
                {0x00, 0x7e}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xda},
                {0x00, 0xad}, {0x00, 0xcc}, {0x0a, 0x01}, {0x06, 0x01},
                {0x02, 0x01}, {0x00, 0xae}, {0x02, 0x01}, {0x00, 0xdb},
                {0x00, 0xdc}, {0x02, 0x01}, {0x00, 0xcd}, {0x00, 0xbe},
                {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xeb},
                {0x00, 0xed}, {0x00, 0xee}, {0x06, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xd9}, {0x00, 0xea}, {0x00, 0xe9},
                {0x02, 0x01}, {0x00, 0xde}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xdd}, {0x00, 0xec}, {0x00, 0xce}, {0x00, 0x3f},
                {0x00, 0xf0}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xf3},
                {0x00, 0xf4}, {0x02, 0x01}, {0x00, 0x4f}, {0x02, 0x01},
                {0x00, 0xf5}, {0x00, 0x5f}, {0x0a, 0x01}, {0x02, 0x01},
                {0x00, 0xff}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xf6},
                {0x00, 0x6f}, {0x02, 0x01}, {0x00, 0xf7}, {0x00, 0x7f},
                {0x0c, 0x01}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x8f},
                {0x02, 0x01}, {0x00, 0xf8}, {0x00, 0xf9}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x9f}, {0x00, 0xfa}, {0x00, 0xaf},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xfb},
                {0x00, 0xbf}, {0x02, 0x01}, {0x00, 0xfc}, {0x00, 0xcf},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xfd}, {0x00, 0xdf},
                {0x02, 0x01}, {0x00, 0xfe}, {0x00, 0xef},
            },
            new byte[,] // 24
            {
                {0x3c, 0x01}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x00}, {0x00, 0x10}, {0x02, 0x01}, {0x00, 0x01},
                {0x00, 0x11}, {0x0e, 0x01}, {0x06, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x20}, {0x00, 0x02}, {0x00, 0x21},
                {0x02, 0x01}, {0x00, 0x12}, {0x02, 0x01}, {0x00, 0x22},
                {0x02, 0x01}, {0x00, 0x30}, {0x00, 0x03}, {0x0e, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x31}, {0x00, 0x13},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x32}, {0x00, 0x23},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x40}, {0x00, 0x04},
                {0x00, 0x41}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x14}, {0x00, 0x33}, {0x02, 0x01}, {0x00, 0x42},
                {0x00, 0x24}, {0x06, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x43}, {0x00, 0x34}, {0x00, 0x51}, {0x06, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x50}, {0x00, 0x05},
                {0x00, 0x15}, {0x02, 0x01}, {0x00, 0x52}, {0x00, 0x25},
                {0xfa, 0x01}, {0x62, 0x01}, {0x22, 0x01}, {0x12, 0x01},
                {0x0a, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x44},
                {0x00, 0x53}, {0x02, 0x01}, {0x00, 0x35}, {0x02, 0x01},
                {0x00, 0x60}, {0x00, 0x06}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x61}, {0x00, 0x16}, {0x02, 0x01}, {0x00, 0x62},
                {0x00, 0x26}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x54}, {0x00, 0x45}, {0x02, 0x01}, {0x00, 0x63},
                {0x00, 0x36}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x71},
                {0x00, 0x55}, {0x02, 0x01}, {0x00, 0x64}, {0x00, 0x46},
                {0x20, 0x01}, {0x0e, 0x01}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x72}, {0x02, 0x01}, {0x00, 0x27}, {0x00, 0x37},
                {0x02, 0x01}, {0x00, 0x73}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x70}, {0x00, 0x07}, {0x00, 0x17}, {0x0a, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x65}, {0x00, 0x56},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x80}, {0x00, 0x08},
                {0x00, 0x81}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x74},
                {0x00, 0x47}, {0x02, 0x01}, {0x00, 0x18}, {0x00, 0x82},
                {0x10, 0x01}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x28}, {0x00, 0x66}, {0x02, 0x01}, {0x00, 0x83},
                {0x00, 0x38}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x75},
                {0x00, 0x57}, {0x02, 0x01}, {0x00, 0x84}, {0x00, 0x48},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x91},
                {0x00, 0x19}, {0x02, 0x01}, {0x00, 0x92}, {0x00, 0x76},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x67}, {0x00, 0x29},
                {0x02, 0x01}, {0x00, 0x85}, {0x00, 0x58}, {0x5c, 0x01},
                {0x22, 0x01}, {0x10, 0x01}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x93}, {0x00, 0x39}, {0x02, 0x01},
                {0x00, 0x94}, {0x00, 0x49}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x77}, {0x00, 0x86}, {0x02, 0x01}, {0x00, 0x68},
                {0x00, 0xa1}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xa2}, {0x00, 0x2a}, {0x02, 0x01}, {0x00, 0x95},
                {0x00, 0x59}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xa3},
                {0x00, 0x3a}, {0x02, 0x01}, {0x00, 0x87}, {0x02, 0x01},
                {0x00, 0x78}, {0x00, 0x4a}, {0x16, 0x01}, {0x0c, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xa4}, {0x00, 0x96},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x69}, {0x00, 0xb1},
                {0x02, 0x01}, {0x00, 0x1b}, {0x00, 0xa5}, {0x06, 0x01},
                {0x02, 0x01}, {0x00, 0xb2}, {0x02, 0x01}, {0x00, 0x5a},
                {0x00, 0x2b}, {0x02, 0x01}, {0x00, 0x88}, {0x00, 0xb3},
                {0x10, 0x01}, {0x0a, 0x01}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x90}, {0x02, 0x01}, {0x00, 0x09}, {0x00, 0xa0},
                {0x02, 0x01}, {0x00, 0x97}, {0x00, 0x79}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xa6}, {0x00, 0x6a}, {0x00, 0xb4},
                {0x0c, 0x01}, {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x1a},
                {0x02, 0x01}, {0x00, 0x0a}, {0x00, 0xb0}, {0x02, 0x01},
                {0x00, 0x3b}, {0x02, 0x01}, {0x00, 0x0b}, {0x00, 0xc0},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x4b}, {0x00, 0xc1},
                {0x02, 0x01}, {0x00, 0x98}, {0x00, 0x89}, {0x43, 0x01},
                {0x22, 0x01}, {0x10, 0x01}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x1c}, {0x00, 0xb5}, {0x02, 0x01},
                {0x00, 0x5b}, {0x00, 0xc2}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x2c}, {0x00, 0xa7}, {0x02, 0x01}, {0x00, 0x7a},
                {0x00, 0xc3}, {0x0a, 0x01}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x3c}, {0x02, 0x01}, {0x00, 0x0c}, {0x00, 0xd0},
                {0x02, 0x01}, {0x00, 0xb6}, {0x00, 0x6b}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xc4}, {0x00, 0x4c}, {0x02, 0x01},
                {0x00, 0x99}, {0x00, 0xa8}, {0x10, 0x01}, {0x08, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x8a}, {0x00, 0xc5},
                {0x02, 0x01}, {0x00, 0x5c}, {0x00, 0xd1}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xb7}, {0x00, 0x7b}, {0x02, 0x01},
                {0x00, 0x1d}, {0x00, 0xd2}, {0x09, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x2d}, {0x00, 0xd3}, {0x02, 0x01},
                {0x00, 0x3d}, {0x00, 0xc6}, {0x55, 0xfa}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x6c}, {0x00, 0xa9}, {0x02, 0x01},
                {0x00, 0x9a}, {0x00, 0xd4}, {0x20, 0x01}, {0x10, 0x01},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xb8},
                {0x00, 0x8b}, {0x02, 0x01}, {0x00, 0x4d}, {0x00, 0xc7},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x7c}, {0x00, 0xd5},
                {0x02, 0x01}, {0x00, 0x5d}, {0x00, 0xe1}, {0x08, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x1e}, {0x00, 0xe2},
                {0x02, 0x01}, {0x00, 0xaa}, {0x00, 0xb9}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x9b}, {0x00, 0xe3}, {0x02, 0x01},
                {0x00, 0xd6}, {0x00, 0x6d}, {0x14, 0x01}, {0x0a, 0x01},
                {0x06, 0x01}, {0x02, 0x01}, {0x00, 0x3e}, {0x02, 0x01},
                {0x00, 0x2e}, {0x00, 0x4e}, {0x02, 0x01}, {0x00, 0xc8},
                {0x00, 0x8c}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xe4},
                {0x00, 0xd7}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x7d},
                {0x00, 0xab}, {0x00, 0xe5}, {0x0a, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xba}, {0x00, 0x5e}, {0x02, 0x01},
                {0x00, 0xc9}, {0x02, 0x01}, {0x00, 0x9c}, {0x00, 0x6e},
                {0x08, 0x01}, {0x02, 0x01}, {0x00, 0xe6}, {0x02, 0x01},
                {0x00, 0x0d}, {0x02, 0x01}, {0x00, 0xe0}, {0x00, 0x0e},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xd8}, {0x00, 0x8d},
                {0x02, 0x01}, {0x00, 0xbb}, {0x00, 0xca}, {0x4a, 0x01},
                {0x02, 0x01}, {0x00, 0xff}, {0x40, 0x01}, {0x3a, 0x01},
                {0x20, 0x01}, {0x10, 0x01}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xac}, {0x00, 0xe7}, {0x02, 0x01},
                {0x00, 0x7e}, {0x00, 0xd9}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x9d}, {0x00, 0xe8}, {0x02, 0x01}, {0x00, 0x8e},
                {0x00, 0xcb}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xbc}, {0x00, 0xda}, {0x02, 0x01}, {0x00, 0xad},
                {0x00, 0xe9}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x9e},
                {0x00, 0xcc}, {0x02, 0x01}, {0x00, 0xdb}, {0x00, 0xbd},
                {0x10, 0x01}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xea}, {0x00, 0xae}, {0x02, 0x01}, {0x00, 0xdc},
                {0x00, 0xcd}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xeb},
                {0x00, 0xbe}, {0x02, 0x01}, {0x00, 0xdd}, {0x00, 0xec},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xce},
                {0x00, 0xed}, {0x02, 0x01}, {0x00, 0xde}, {0x00, 0xee},
                {0x00, 0x0f}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xf0},
                {0x00, 0x1f}, {0x00, 0xf1}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xf2}, {0x00, 0x2f}, {0x02, 0x01}, {0x00, 0xf3},
                {0x00, 0x3f}, {0x12, 0x01}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0xf4}, {0x00, 0x4f}, {0x02, 0x01},
                {0x00, 0xf5}, {0x00, 0x5f}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xf6}, {0x00, 0x6f}, {0x02, 0x01}, {0x00, 0xf7},
                {0x02, 0x01}, {0x00, 0x7f}, {0x00, 0x8f}, {0x0a, 0x01},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xf8}, {0x00, 0xf9},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x9f}, {0x00, 0xaf},
                {0x00, 0xfa}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0xfb}, {0x00, 0xbf}, {0x02, 0x01}, {0x00, 0xfc},
                {0x00, 0xcf}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0xfd},
                {0x00, 0xdf}, {0x02, 0x01}, {0x00, 0xfe}, {0x00, 0xef},
            },
            new byte[,] // 32
            {
                {0x02, 0x01}, {0x00, 0x00}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x08}, {0x00, 0x04}, {0x02, 0x01},
                {0x00, 0x01}, {0x00, 0x02}, {0x08, 0x01}, {0x04, 0x01},
                {0x02, 0x01}, {0x00, 0x0c}, {0x00, 0x0a}, {0x02, 0x01},
                {0x00, 0x03}, {0x00, 0x06}, {0x06, 0x01}, {0x02, 0x01},
                {0x00, 0x09}, {0x02, 0x01}, {0x00, 0x05}, {0x00, 0x07},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x0e}, {0x00, 0x0d},
                {0x02, 0x01}, {0x00, 0x0f}, {0x00, 0x0b},
            },
            new byte[,] // 33
            {
                {0x10, 0x01}, {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01},
                {0x00, 0x00}, {0x00, 0x01}, {0x02, 0x01}, {0x00, 0x02},
                {0x00, 0x03}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x04},
                {0x00, 0x05}, {0x02, 0x01}, {0x00, 0x06}, {0x00, 0x07},
                {0x08, 0x01}, {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x08},
                {0x00, 0x09}, {0x02, 0x01}, {0x00, 0x0a}, {0x00, 0x0b},
                {0x04, 0x01}, {0x02, 0x01}, {0x00, 0x0c}, {0x00, 0x0d},
                {0x02, 0x01}, {0x00, 0x0e}, {0x00, 0x0f},
            }
        };

        #endregion

        static readonly float[] _floatLookup;

        static Huffman()
        {
            _floatLookup = new float[8207];
            for (int i = 0; i < 8207; i++)
            {
                _floatLookup[i] = (float)Math.Pow(i, 4.0 / 3);
            }
        }

        static HuffmanListNode[] _llCache = new HuffmanListNode[_codeTables.Length];
        static int[] _llCacheMaxBits = new int[_codeTables.Length];

        static readonly int[] LIN_BITS = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 6, 8, 10, 13, 4, 5, 6, 7, 8, 9, 11, 13 };

        internal static void Decode(BitReservoir br, int table, out float x, out float y)
        {
            if (table == 0 || table == 4 || table == 14)
            {
                x = y = 0;
            }
            else
            {
                var val = DecodeSymbol(br, table);

                var ix = val >> 4;
                var iy = val & 15;

                int linBits = LIN_BITS[table];
                if (linBits > 0 && ix == 15) ix += br.GetBits(linBits);
                if (ix != 0 && br.Get1Bit() != 0)
                {
                    x = -_floatLookup[ix];
                }
                else
                {
                    x = _floatLookup[ix];
                }

                if (linBits > 0 && iy == 15) iy += br.GetBits(linBits);
                if (iy != 0 && br.Get1Bit() != 0)
                {
                    y = -_floatLookup[iy];
                }
                else
                {
                    y = _floatLookup[iy];
                }
            }
        }

        internal static void Decode(BitReservoir br, int table, out float x, out float y, out float v, out float w)
        {
            var val = DecodeSymbol(br, table);

            v = w = x = y = 0;

            if ((val & 0x8) != 0)
            {
                if (br.Get1Bit() == 1)
                {
                    v = -_floatLookup[1];
                }
                else
                {
                    v = _floatLookup[1];
                }
            }

            if ((val & 0x4) != 0)
            {
                if (br.Get1Bit() == 1)
                {
                    w = -_floatLookup[1];
                }
                else
                {
                    w = _floatLookup[1];
                }
            }

            if ((val & 0x2) != 0)
            {
                if (br.Get1Bit() == 1)
                {
                    x = -_floatLookup[1];
                }
                else
                {
                    x = _floatLookup[1];
                }
            }

            if ((val & 0x1) != 0)
            {
                if (br.Get1Bit() == 1)
                {
                    y = -_floatLookup[1];
                }
                else
                {
                    y = _floatLookup[1];
                }
            }
        }

        // In C#, bit operations are faster than the tree traversal the spec is written for.
        static byte DecodeSymbol(BitReservoir br, int table)
        {
            // get the huffman node for decoding
            int maxBits;
            var node = GetNode(table, out maxBits);

            // get some bits to work with
            int readBits;
            int bits = br.TryPeekBits(maxBits, out readBits);
            if (readBits < maxBits)
            {
                bits <<= maxBits - readBits;
            }

            // try to find the correct node
            while (node != null && node.Length <= readBits)
            {
                if ((bits & node.Mask) == node.Bits)
                {
                    // found it!  Advance the read counter
                    br.SkipBits(node.Length);
                    break;
                }
                node = node.Next;
            }

            // apply the value
            if (node != null && node.Length <= readBits)
            {
                return node.Value;
            }
            else
            {
                // should we advance the reader????  need to check the spec
                return 0;
            }
        }

        static HuffmanListNode GetNode(int table, out int maxBits)
        {
            var realIdx = table;
            if (realIdx > 16)
            {
                // these are "special"
                if (realIdx > 31)
                {
                    // last two entries in the table
                    realIdx -= 17;
                }
                else if (realIdx >= 24)
                {
                    // third last entry in the table
                    realIdx = 14;
                }
                else
                {
                    // fourth last entry in the table
                    realIdx = 13;
                }
            }
            else
            {
                if (realIdx > 13) --realIdx;
                if (realIdx > 3) --realIdx;
                --realIdx;
            }

            if (_llCache[realIdx] == null)
            {
                _llCache[realIdx] = InitTable(_codeTables[realIdx], out maxBits);
                _llCacheMaxBits[realIdx] = maxBits;
            }
            else
            {
                maxBits = _llCacheMaxBits[realIdx];
            }

            return _llCache[realIdx];
        }

        static HuffmanListNode InitTable(byte[,] tree, out int maxBits)
        {
            var values = new List<byte>();
            var lengths = new List<int>();
            var codes = new List<int>();

            var treeLen = tree.GetLength(0);

            for (int i = 0; i < treeLen; i++)
            {
                if (tree[i, 0] == 0)
                {
                    var bits = 0;
                    var len = 0;
                    var idx = i;
                    int bit;
                    do
                    {
                        idx = FindPreviousNode(tree, idx, out bit);
                        bits |= bit << len++;
                    } while (idx > 0);

                    values.Add(tree[i, 1]);
                    lengths.Add(len);
                    codes.Add(bits);
                }
            }

            return BuildLinkedList(values, lengths, codes, out maxBits);
        }

        static int FindPreviousNode(byte[,] tree, int idx, out int bit)
        {
            // look for a preceding value that matches
            for (int i = idx - 1; i >= 0; i--)
            {
                // if the "0" node is 0, it's a leaf... ignore it
                if (tree[i, 0] != 0)
                {
                    // check both nodes
                    for (int j = 0; j < 2; j++)
                    {
                        // if we've found the correct one...
                        if (i + tree[i, j] == idx)
                        {
                            // ... check whether it was a continuation
                            if (tree[i, j] >= 250)
                            {
                                // if so, find it's parent and return that...
                                var temp = FindPreviousNode(tree, i, out bit);
                                if (bit != j) throw new InvalidOperationException();
                                return temp;
                            }
                            // otherwise, return the bit and the index
                            bit = j;
                            return i;
                        }
                    }
                }
            }

            throw new InvalidOperationException();
        }

        static HuffmanListNode BuildLinkedList(List<byte> values, List<int> lengthList, List<int> codeList, out int maxBits)
        {
            var list = new HuffmanListNode[lengthList.Count];

            maxBits = lengthList.Max();

            for (int i = 0; i < list.Length; i++)
            {
                var shift = maxBits - lengthList[i];
                list[i] = new HuffmanListNode
                {
                    Value = values[i],
                    Length = lengthList[i],
                    Bits = codeList[i] << shift,
                    Mask = ((1 << lengthList[i]) - 1) << shift,
                };
            }

            // we don't care about equal-length entries (in fact, they should already be in the "correct" order)
            Array.Sort(list, (i1, i2) => i1.Length - i2.Length);

            for (int i = 1; i < list.Length && list[i].Length < 99999; i++)
            {
                list[i - 1].Next = list[i];
            }

            return list[0];
        }

        class HuffmanListNode
        {
            internal byte Value;

            internal int Length;
            internal int Bits;
            internal int Mask;

            internal HuffmanListNode Next;
        }
    }
}


namespace OmniMusicMpeg.Decoder
{
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
    class ID3Frame : FrameBase
    {
        internal static ID3Frame TrySync(uint syncMark)
        {
            if ((syncMark & 0xFFFFFF00U) == 0x49443300)
            {
                return new ID3Frame { _version = 2 };
            }

            if ((syncMark & 0xFFFFFF00U) == 0x54414700)
            {
                if ((syncMark & 0xFF) == 0x2B)
                {
                    return new ID3Frame { _version = 1 };
                }
                else
                {
                    return new ID3Frame { _version = 0 };
                }
            }

            return null;
        }

        int _version;

        ID3Frame()
        {

        }

        protected override int Validate()
        {
            switch (_version)
            {
                case 2:
                    // v2, yay!
                    var buf = new byte[7];
                    if (Read(3, buf) == 7)
                    {
                        byte flagsMask;
                        switch (buf[0])
                        {
                            case 2:
                                flagsMask = 0x3F;
                                break;
                            case 3:
                                flagsMask = 0x1F;
                                break;
                            case 4:
                                flagsMask = 0x0F;
                                break;
                            default:
                                return -1;
                        }

                        // ignore the flags (we don't need them for the validation)

                        // get the size (7 bits per byte [MSB cleared])
                        var size = (buf[3] << 21)
                                 | (buf[4] << 14)
                                 | (buf[5] << 7)
                                 | (buf[6]);

                        // finally, check to make sure that all the right bits are cleared
                        if (!(((buf[2] & flagsMask) | (buf[3] & 0x80) | (buf[4] & 0x80) | (buf[5] & 0x80) | (buf[6] & 0x80)) != 0 || buf[1] == 0xFF))
                        {
                            return size + 10;   // don't forget the sync, flag & size bytes!
                        }
                    }
                    break;
                case 1:
                    return 227 + 128;
                case 0:
                    return 128;
            }

            return -1;
        }

        internal override void Parse()
        {
            // assume we have to process it now or else...  we can still read the whole frame, so no biggie
            switch (_version)
            {
                case 2:
                    ParseV2();
                    break;
                case 1:
                    ParseV1Enh();
                    break;
                case 0:
                    ParseV1(3);
                    break;
            }
        }

        void ParseV1(int offset)
        {
            //var buffer = new byte[125];
            //if (Read(offset, buffer) == 125)
            //{
            //    // v1 tags use ASCII encoding... For now we'll use the built-in encoding, but for Win8 we'll have to build our own.
            //    var encoding = Encoding.ASCII;
            //
            //    // title (30)
            //    Title = encoding.GetString(buffer, 0, 30);
            //
            //    // artist (30)
            //    Artist = encoding.GetString(buffer, 30, 30);
            //
            //    // album (30)
            //    Album = encoding.GetString(buffer, 60, 30);
            //
            //    // year (4)
            //    Year = encoding.GetString(buffer, 90, 30);
            //
            //    // comment (30)*
            //    Comment = encoding.GetString(buffer, 94, 30);
            //
            //    if (buffer[122] == 0)
            //    {
            //        // track (1)*
            //        Track = (int)buffer[123];
            //    }
            //
            //    // genre (1)
            //    // ignore for now
            //
            //    // * if byte 29 of comment is 0, track is byte 30.  Otherwise, track is unknown.
            //}
        }

        void ParseV1Enh()
        {
            ParseV1(230);

            //var buffer = new byte[223];
            //if (Read(4, buffer) == 223)
            //{
            //    // v1 tags use ASCII encoding... For now we'll use the built-in encoding, but for Win8 we'll have to build our own.
            //    var encoding = Encoding.ASCII;
            //
            //    // title (60)
            //    Title += encoding.GetString(buffer, 0, 60);
            //
            //    // artist (60)
            //    Artist += encoding.GetString(buffer, 60, 60);
            //
            //    // album (60)
            //    Album += encoding.GetString(buffer, 120, 60);
            //
            //    // speed (1)
            //    //var speed = buffer[180];
            //
            //    // genre (30)
            //    Genre = encoding.GetString(buffer, 181, 30);
            //
            //    // start-time (6)
            //    // 211
            //
            //    // end-time (6)
            //    // 217
            //}
        }

        int _encoderDelay = -1;
        int _encoderPadding = -1;
        bool _v2Parsed;

        void ParseV2()
        {
            _v2Parsed = true;
            _encoderDelay = 0;
            _encoderPadding = 0;

            // Read version byte to determine frame size encoding
            var header = new byte[7];
            if (Read(3, header) != 7) return;

            int majorVersion = header[0];
            if (majorVersion != 3 && majorVersion != 4) return;

            // Tag content size (syncsafe integer, 7 bits per byte)
            int tagSize = (header[3] << 21) | (header[4] << 14) | (header[5] << 7) | header[6];

            // Walk ID3v2 frames looking for TXXX "iTunSMPB"
            int pos = 10; // skip 10-byte tag header
            var frameBuf = new byte[10];
            while (pos < tagSize + 10)
            {
                if (Read(pos, frameBuf, 0, 10) < 10) break;

                // Frame ID: 4 ASCII bytes; null means padding, stop
                if (frameBuf[0] == 0) break;

                // Frame size: plain 32-bit in v2.3, syncsafe in v2.4
                int frameSize;
                if (majorVersion == 4)
                    frameSize = (frameBuf[4] << 21) | (frameBuf[5] << 14) | (frameBuf[6] << 7) | frameBuf[7];
                else
                    frameSize = (frameBuf[4] << 24) | (frameBuf[5] << 16) | (frameBuf[6] << 8) | frameBuf[7];

                if (frameSize <= 0) break;

                bool isTxxx = frameBuf[0] == 'T' && frameBuf[1] == 'X' && frameBuf[2] == 'X' && frameBuf[3] == 'X';
                if (isTxxx && frameSize < 4096)
                {
                    var data = new byte[frameSize];
                    if (Read(pos + 10, data, 0, frameSize) == frameSize)
                        TryParseITunSMPB(data, frameSize);
                }

                if (_encoderDelay > 0 || _encoderPadding > 0) break; // found it, stop scanning

                pos += 10 + frameSize;
            }
        }

        void TryParseITunSMPB(byte[] data, int length)
        {
            if (length < 2) return;
            int enc = data[0];

            // Decode entire frame content (description + NUL + value) as a string
            string fullText;
            try
            {
                Encoding encoding = (enc == 1 || enc == 2) ? Encoding.Unicode : Encoding.UTF8;
                fullText = encoding.GetString(data, 1, length - 1);
            }
            catch { return; }

            // Strip leading BOM if present
            if (fullText.Length > 0 && fullText[0] == '\uFEFF')
                fullText = fullText.Substring(1);

            // Split on the NUL separator between description and value
            int nullIdx = fullText.IndexOf('\0');
            if (nullIdx < 0) return;

            string desc = fullText.Substring(0, nullIdx);
            if (desc != "iTunSMPB") return;

            // Value: " 00000000 DELAY PADDING SAMPLECOUNT ..."
            // UTF-16 frames carry a BOM before both the description and the value; strip it here too.
            string value = fullText.Substring(nullIdx + 1).Trim('\0');
            if (value.Length > 0 && value[0] == '\uFEFF')
                value = value.Substring(1);
            var parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return;

            if (int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out int delay) &&
                int.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out int padding))
            {
                _encoderDelay = delay;
                _encoderPadding = padding;
            }
        }

        internal void ParseEagerIfNeeded()
        {
            if (_version == 2 && !_v2Parsed) ParseV2();
        }

        internal int EncoderDelay
        {
            get { return _encoderDelay < 0 ? 0 : _encoderDelay; }
        }

        internal int EncoderPadding
        {
            get { return _encoderPadding < 0 ? 0 : _encoderPadding; }
        }

        internal int Version
        {
            get
            {
                if (_version == 0) return 1;
                return _version;
            }
        }

        //internal string Title { get; private set; }
        //internal string Artist { get; private set; }
        //internal string Album { get; private set; }
        //internal string Year { get; private set; }
        //internal string Comment { get; private set; }
        //internal int Track { get; private set; }
        //internal string Genre { get; private set; }
        // speed
        //public TimeSpan StartTime { get; private set; }
        //public TimeSpan EndTime { get; private set; }

        internal void Merge(ID3Frame newFrame)
        {
            // just save off the frame for parsing later
        }
    }
}

/*
 * OmniMusicMpeg - A C# MPEG1/2/2.5 audio decoder
 * 
 * Portions of this file are courtesy Fluendo, S.A.  They are dual licensed as Ms-PL
 * and under the following license:
 *
 *   Copyright <2005-2012> Fluendo S.A.
 *   
 *   Unless otherwise indicated, Source Code is licensed under MIT license.
 *   See further explanation attached in License Statement (distributed in the file
 *   LICENSE).
 *   
 *   Permission is hereby granted, free of charge, to any person obtaining a copy of
 *   this software and associated documentation files (the "Software"), to deal in
 *   the Software without restriction, including without limitation the rights to
 *   use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
 *   of the Software, and to permit persons to whom the Software is furnished to do
 *   so, subject to the following conditions:
 *   
 *   The above copyright notice and this permission notice shall be included in all
 *   copies or substantial portions of the Software.
 *   
 *   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 *   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 *   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 *   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 *   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 *   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 *   SOFTWARE.
 *
 */


namespace OmniMusicMpeg.Decoder
{
using System.Collections.Generic;
    abstract class LayerDecoderBase
    {
        protected const int SBLIMIT = 32;

        const float INV_SQRT_2 = 7.071067811865474617150084668537e-01f;

        #region Tables

        static float[] DEWINDOW_TABLE = {
                                         0.000000000f, -0.000015259f, -0.000015259f, -0.000015259f,
                                        -0.000015259f, -0.000015259f, -0.000015259f, -0.000030518f,
                                        -0.000030518f, -0.000030518f, -0.000030518f, -0.000045776f,
                                        -0.000045776f, -0.000061035f, -0.000061035f, -0.000076294f,
                                        -0.000076294f, -0.000091553f, -0.000106812f, -0.000106812f,
                                        -0.000122070f, -0.000137329f, -0.000152588f, -0.000167847f,
                                        -0.000198364f, -0.000213623f, -0.000244141f, -0.000259399f,
                                        -0.000289917f, -0.000320435f, -0.000366211f, -0.000396729f,
                                        -0.000442505f, -0.000473022f, -0.000534058f, -0.000579834f,
                                        -0.000625610f, -0.000686646f, -0.000747681f, -0.000808716f,
                                        -0.000885010f, -0.000961304f, -0.001037598f, -0.001113892f,
                                        -0.001205444f, -0.001296997f, -0.001388550f, -0.001480103f,
                                        -0.001586914f, -0.001693726f, -0.001785278f, -0.001907349f,
                                        -0.002014160f, -0.002120972f, -0.002243042f, -0.002349854f,
                                        -0.002456665f, -0.002578735f, -0.002685547f, -0.002792358f,
                                        -0.002899170f, -0.002990723f, -0.003082275f, -0.003173828f,
                                         0.003250122f,  0.003326416f,  0.003387451f,  0.003433228f,
                                         0.003463745f,  0.003479004f,  0.003479004f,  0.003463745f,
                                         0.003417969f,  0.003372192f,  0.003280640f,  0.003173828f,
                                         0.003051758f,  0.002883911f,  0.002700806f,  0.002487183f,
                                         0.002227783f,  0.001937866f,  0.001617432f,  0.001266479f,
                                         0.000869751f,  0.000442505f, -0.000030518f, -0.000549316f,
                                        -0.001098633f, -0.001693726f, -0.002334595f, -0.003005981f,
                                        -0.003723145f, -0.004486084f, -0.005294800f, -0.006118774f,
                                        -0.007003784f, -0.007919312f, -0.008865356f, -0.009841919f,
                                        -0.010848999f, -0.011886597f, -0.012939453f, -0.014022827f,
                                        -0.015121460f, -0.016235352f, -0.017349243f, -0.018463135f,
                                        -0.019577026f, -0.020690918f, -0.021789551f, -0.022857666f,
                                        -0.023910522f, -0.024932861f, -0.025909424f, -0.026840210f,
                                        -0.027725220f, -0.028533936f, -0.029281616f, -0.029937744f,
                                        -0.030532837f, -0.031005859f, -0.031387329f, -0.031661987f,
                                        -0.031814575f, -0.031845093f, -0.031738281f, -0.031478882f,
                                         0.031082153f,  0.030517578f,  0.029785156f,  0.028884888f,
                                         0.027801514f,  0.026535034f,  0.025085449f,  0.023422241f,
                                         0.021575928f,  0.019531250f,  0.017257690f,  0.014801025f,
                                         0.012115479f,  0.009231567f,  0.006134033f,  0.002822876f,
                                        -0.000686646f, -0.004394531f, -0.008316040f, -0.012420654f,
                                        -0.016708374f, -0.021179199f, -0.025817871f, -0.030609131f,
                                        -0.035552979f, -0.040634155f, -0.045837402f, -0.051132202f,
                                        -0.056533813f, -0.061996460f, -0.067520142f, -0.073059082f,
                                        -0.078628540f, -0.084182739f, -0.089706421f, -0.095169067f,
                                        -0.100540161f, -0.105819702f, -0.110946655f, -0.115921021f,
                                        -0.120697021f, -0.125259399f, -0.129562378f, -0.133590698f,
                                        -0.137298584f, -0.140670776f, -0.143676758f, -0.146255493f,
                                        -0.148422241f, -0.150115967f, -0.151306152f, -0.151962280f,
                                        -0.152069092f, -0.151596069f, -0.150497437f, -0.148773193f,
                                        -0.146362305f, -0.143264771f, -0.139450073f, -0.134887695f,
                                        -0.129577637f, -0.123474121f, -0.116577148f, -0.108856201f,
                                         0.100311279f,  0.090927124f,  0.080688477f,  0.069595337f,
                                         0.057617187f,  0.044784546f,  0.031082153f,  0.016510010f,
                                         0.001068115f, -0.015228271f, -0.032379150f, -0.050354004f,
                                        -0.069168091f, -0.088775635f, -0.109161377f, -0.130310059f,
                                        -0.152206421f, -0.174789429f, -0.198059082f, -0.221984863f,
                                        -0.246505737f, -0.271591187f, -0.297210693f, -0.323318481f,
                                        -0.349868774f, -0.376800537f, -0.404083252f, -0.431655884f,
                                        -0.459472656f, -0.487472534f, -0.515609741f, -0.543823242f,
                                        -0.572036743f, -0.600219727f, -0.628295898f, -0.656219482f,
                                        -0.683914185f, -0.711318970f, -0.738372803f, -0.765029907f,
                                        -0.791213989f, -0.816864014f, -0.841949463f, -0.866363525f,
                                        -0.890090942f, -0.913055420f, -0.935195923f, -0.956481934f,
                                        -0.976852417f, -0.996246338f, -1.014617920f, -1.031936646f,
                                        -1.048156738f, -1.063217163f, -1.077117920f, -1.089782715f,
                                        -1.101211548f, -1.111373901f, -1.120223999f, -1.127746582f,
                                        -1.133926392f, -1.138763428f, -1.142211914f, -1.144287109f,
                                         1.144989014f,  1.144287109f,  1.142211914f,  1.138763428f,
                                         1.133926392f,  1.127746582f,  1.120223999f,  1.111373901f,
                                         1.101211548f,  1.089782715f,  1.077117920f,  1.063217163f,
                                         1.048156738f,  1.031936646f,  1.014617920f,  0.996246338f,
                                         0.976852417f,  0.956481934f,  0.935195923f,  0.913055420f,
                                         0.890090942f,  0.866363525f,  0.841949463f,  0.816864014f,
                                         0.791213989f,  0.765029907f,  0.738372803f,  0.711318970f,
                                         0.683914185f,  0.656219482f,  0.628295898f,  0.600219727f,
                                         0.572036743f,  0.543823242f,  0.515609741f,  0.487472534f,
                                         0.459472656f,  0.431655884f,  0.404083252f,  0.376800537f,
                                         0.349868774f,  0.323318481f,  0.297210693f,  0.271591187f,
                                         0.246505737f,  0.221984863f,  0.198059082f,  0.174789429f,
                                         0.152206421f,  0.130310059f,  0.109161377f,  0.088775635f,
                                         0.069168091f,  0.050354004f,  0.032379150f,  0.015228271f,
                                        -0.001068115f, -0.016510010f, -0.031082153f, -0.044784546f,
                                        -0.057617187f, -0.069595337f, -0.080688477f, -0.090927124f,
                                         0.100311279f,  0.108856201f,  0.116577148f,  0.123474121f,
                                         0.129577637f,  0.134887695f,  0.139450073f,  0.143264771f,
                                         0.146362305f,  0.148773193f,  0.150497437f,  0.151596069f,
                                         0.152069092f,  0.151962280f,  0.151306152f,  0.150115967f,
                                         0.148422241f,  0.146255493f,  0.143676758f,  0.140670776f,
                                         0.137298584f,  0.133590698f,  0.129562378f,  0.125259399f,
                                         0.120697021f,  0.115921021f,  0.110946655f,  0.105819702f,
                                         0.100540161f,  0.095169067f,  0.089706421f,  0.084182739f,
                                         0.078628540f,  0.073059082f,  0.067520142f,  0.061996460f,
                                         0.056533813f,  0.051132202f,  0.045837402f,  0.040634155f,
                                         0.035552979f,  0.030609131f,  0.025817871f,  0.021179199f,
                                         0.016708374f,  0.012420654f,  0.008316040f,  0.004394531f,
                                         0.000686646f, -0.002822876f, -0.006134033f, -0.009231567f,
                                        -0.012115479f, -0.014801025f, -0.017257690f, -0.019531250f,
                                        -0.021575928f, -0.023422241f, -0.025085449f, -0.026535034f,
                                        -0.027801514f, -0.028884888f, -0.029785156f, -0.030517578f,
                                         0.031082153f,  0.031478882f,  0.031738281f,  0.031845093f,
                                         0.031814575f,  0.031661987f,  0.031387329f,  0.031005859f,
                                         0.030532837f,  0.029937744f,  0.029281616f,  0.028533936f,
                                         0.027725220f,  0.026840210f,  0.025909424f,  0.024932861f,
                                         0.023910522f,  0.022857666f,  0.021789551f,  0.020690918f,
                                         0.019577026f,  0.018463135f,  0.017349243f,  0.016235352f,
                                         0.015121460f,  0.014022827f,  0.012939453f,  0.011886597f,
                                         0.010848999f,  0.009841919f,  0.008865356f,  0.007919312f,
                                         0.007003784f,  0.006118774f,  0.005294800f,  0.004486084f,
                                         0.003723145f,  0.003005981f,  0.002334595f,  0.001693726f,
                                         0.001098633f,  0.000549316f,  0.000030518f, -0.000442505f,
                                        -0.000869751f, -0.001266479f, -0.001617432f, -0.001937866f,
                                        -0.002227783f, -0.002487183f, -0.002700806f, -0.002883911f,
                                        -0.003051758f, -0.003173828f, -0.003280640f, -0.003372192f,
                                        -0.003417969f, -0.003463745f, -0.003479004f, -0.003479004f,
                                        -0.003463745f, -0.003433228f, -0.003387451f, -0.003326416f,
                                         0.003250122f,  0.003173828f,  0.003082275f,  0.002990723f,
                                         0.002899170f,  0.002792358f,  0.002685547f,  0.002578735f,
                                         0.002456665f,  0.002349854f,  0.002243042f,  0.002120972f,
                                         0.002014160f,  0.001907349f,  0.001785278f,  0.001693726f,
                                         0.001586914f,  0.001480103f,  0.001388550f,  0.001296997f,
                                         0.001205444f,  0.001113892f,  0.001037598f,  0.000961304f,
                                         0.000885010f,  0.000808716f,  0.000747681f,  0.000686646f,
                                         0.000625610f,  0.000579834f,  0.000534058f,  0.000473022f,
                                         0.000442505f,  0.000396729f,  0.000366211f,  0.000320435f,
                                         0.000289917f,  0.000259399f,  0.000244141f,  0.000213623f,
                                         0.000198364f,  0.000167847f,  0.000152588f,  0.000137329f,
                                         0.000122070f,  0.000106812f,  0.000106812f,  0.000091553f,
                                         0.000076294f,  0.000076294f,  0.000061035f,  0.000061035f,
                                         0.000045776f,  0.000045776f,  0.000030518f,  0.000030518f,
                                         0.000030518f,  0.000030518f,  0.000015259f,  0.000015259f,
                                         0.000015259f,  0.000015259f,  0.000015259f,  0.000015259f
                                        };

        static float[] SYNTH_COS64_TABLE = {
                                           5.0060299823519627260e-01f, 5.0241928618815567820e-01f, 5.0547095989754364798e-01f, 5.0979557910415917998e-01f,
                                           5.1544730992262455249e-01f, 5.2249861493968885462e-01f, 5.3104259108978413284e-01f, 5.4119610014619701222e-01f,
                                           5.5310389603444454210e-01f, 5.6694403481635768927e-01f, 5.8293496820613388554e-01f, 6.0134488693504528634e-01f,
                                           6.2250412303566482475e-01f, 6.4682178335999007679e-01f, 6.7480834145500567800e-01f, 7.0710678118654746172e-01f,
                                           7.4453627100229857749e-01f, 7.8815462345125020249e-01f, 8.3934964541552681272e-01f, 8.9997622313641556513e-01f,
                                           9.7256823786196078263e-01f, 1.0606776859903470633e+00f, 1.1694399334328846596e+00f, 1.3065629648763763537e+00f,
                                           1.4841646163141661852e+00f, 1.7224470982383341955e+00f, 2.0577810099534108446e+00f, 2.5629154477415054814e+00f,
                                           3.4076084184687189804e+00f, 5.1011486186891552563e+00f, 1.0190008123548032870e+01f
                                           };

        #endregion

        List<float[]> _synBuf = new List<float[]>(2);
        List<int> _bufOffset = new List<int>(2);

        float[] _eq;

        internal LayerDecoderBase()
        {
            StereoMode = StereoMode.Both;
        }

        abstract internal int DecodeFrame(IMpegFrame frame, float[] ch0, float[] ch1);

        internal void SetEQ(float[] eq)
        {
            if (eq == null || eq.Length == 32)
            {
                _eq = eq;
            }
        }

        internal StereoMode StereoMode { get; set; }

        virtual internal void ResetForSeek()
        {
            _synBuf.Clear();
            _bufOffset.Clear();
        }

        float[] ippuv = new float[512];

        protected void InversePolyPhase(int channel, float[] data)
        {
            float[] synBuf;
            int k;
            GetBufAndOffset(channel, out synBuf, out k);

            if (_eq != null)
            {
                for (int i = 0; i < 32; i++)
                {
                    data[i] *= _eq[i];
                }
            }

            DCT32(data, synBuf, k);

            BuildUVec(ippuv, synBuf, k);

            DewindowOutput(ippuv, data);
        }

        void GetBufAndOffset(int channel, out float[] synBuf, out int k)
        {
            while (_synBuf.Count <= channel)
            {
                _synBuf.Add(new float[1024]);
            }

            while (_bufOffset.Count <= channel)
            {
                _bufOffset.Add(0);
            }

            synBuf = _synBuf[channel];
            k = _bufOffset[channel];

            k = (k - 32) & 511;
            _bufOffset[channel] = k;
        }

        float[] ei32 = new float[16], eo32 = new float[16], oi32 = new float[16], oo32 = new float[16];

        void DCT32(float[] _in, float[] _out, int k)
        {
            int i;

            for (i = 0; i < 16; i++)
            {
                ei32[i] = _in[i] + _in[31 - i];
                oi32[i] = (_in[i] - _in[31 - i]) * SYNTH_COS64_TABLE[2 * i];
            }

            DCT16(ei32, eo32);
            DCT16(oi32, oo32);

            for (i = 0; i < 15; i++)
            {
                _out[2 * i + k] = eo32[i];
                _out[2 * i + 1 + k] = oo32[i] + oo32[i + 1];
            }
            _out[30 + k] = eo32[15];
            _out[31 + k] = oo32[15];
        }

        float[] ei16 = new float[8], eo16 = new float[8], oi16 = new float[8], oo16 = new float[8];

        void DCT16(float[] _in, float[] _out)
        {
            float a, b;

            a = _in[0]; b = _in[15];
            ei16[0] = a + b;
            oi16[0] = (a - b) * SYNTH_COS64_TABLE[1];
            a = _in[1]; b = _in[14];
            ei16[1] = a + b;
            oi16[1] = (a - b) * SYNTH_COS64_TABLE[5];
            a = _in[2]; b = _in[13];
            ei16[2] = a + b;
            oi16[2] = (a - b) * SYNTH_COS64_TABLE[9];
            a = _in[3]; b = _in[12];
            ei16[3] = a + b;
            oi16[3] = (a - b) * SYNTH_COS64_TABLE[13];
            a = _in[4]; b = _in[11];
            ei16[4] = a + b;
            oi16[4] = (a - b) * SYNTH_COS64_TABLE[17];
            a = _in[5]; b = _in[10];
            ei16[5] = a + b;
            oi16[5] = (a - b) * SYNTH_COS64_TABLE[21];
            a = _in[6]; b = _in[9];
            ei16[6] = a + b;
            oi16[6] = (a - b) * SYNTH_COS64_TABLE[25];
            a = _in[7]; b = _in[8];
            ei16[7] = a + b;
            oi16[7] = (a - b) * SYNTH_COS64_TABLE[29];

            DCT8(ei16, eo16);
            DCT8(oi16, oo16);

            _out[0] = eo16[0];
            _out[1] = oo16[0] + oo16[1];
            _out[2] = eo16[1];
            _out[3] = oo16[1] + oo16[2];
            _out[4] = eo16[2];
            _out[5] = oo16[2] + oo16[3];
            _out[6] = eo16[3];
            _out[7] = oo16[3] + oo16[4];
            _out[8] = eo16[4];
            _out[9] = oo16[4] + oo16[5];
            _out[10] = eo16[5];
            _out[11] = oo16[5] + oo16[6];
            _out[12] = eo16[6];
            _out[13] = oo16[6] + oo16[7];
            _out[14] = eo16[7];
            _out[15] = oo16[7];
        }

        float[] ei8 = new float[4], tmp8 = new float[6], oi8 = new float[4], oo8 = new float[4];

        void DCT8(float[] _in, float[] _out)
        {
            /* Even indices */
            ei8[0] = _in[0] + _in[7];
            ei8[1] = _in[3] + _in[4];
            ei8[2] = _in[1] + _in[6];
            ei8[3] = _in[2] + _in[5];

            tmp8[0] = ei8[0] + ei8[1];
            tmp8[1] = ei8[2] + ei8[3];
            tmp8[2] = (ei8[0] - ei8[1]) * SYNTH_COS64_TABLE[7];
            tmp8[3] = (ei8[2] - ei8[3]) * SYNTH_COS64_TABLE[23];
            tmp8[4] = (float)((tmp8[2] - tmp8[3]) * INV_SQRT_2);

            _out[0] = tmp8[0] + tmp8[1];
            _out[2] = tmp8[2] + tmp8[3] + tmp8[4];
            _out[4] = (float)((tmp8[0] - tmp8[1]) * INV_SQRT_2);
            _out[6] = tmp8[4];

            /* Odd indices */
            oi8[0] = (_in[0] - _in[7]) * SYNTH_COS64_TABLE[3];
            oi8[1] = (_in[1] - _in[6]) * SYNTH_COS64_TABLE[11];
            oi8[2] = (_in[2] - _in[5]) * SYNTH_COS64_TABLE[19];
            oi8[3] = (_in[3] - _in[4]) * SYNTH_COS64_TABLE[27];

            tmp8[0] = oi8[0] + oi8[3];
            tmp8[1] = oi8[1] + oi8[2];
            tmp8[2] = (oi8[0] - oi8[3]) * SYNTH_COS64_TABLE[7];
            tmp8[3] = (oi8[1] - oi8[2]) * SYNTH_COS64_TABLE[23];
            tmp8[4] = tmp8[2] + tmp8[3];
            tmp8[5] = (float)((tmp8[2] - tmp8[3]) * INV_SQRT_2);

            oo8[0] = tmp8[0] + tmp8[1];
            oo8[1] = tmp8[4] + tmp8[5];
            oo8[2] = (float)((tmp8[0] - tmp8[1]) * INV_SQRT_2);
            oo8[3] = tmp8[5];

            _out[1] = oo8[0] + oo8[1];
            _out[3] = oo8[1] + oo8[2];
            _out[5] = oo8[2] + oo8[3];
            _out[7] = oo8[3];
        }

        void BuildUVec(float[] u_vec, float[] cur_synbuf, int k)
        {
            int i, j, uvp = 0;

            for (j = 0; j < 8; j++)
            {
                for (i = 0; i < 16; i++)
                {
                    /* Copy first 32 elements */
                    u_vec[uvp + i] = cur_synbuf[k + i + 16];
                    u_vec[uvp + i + 17] = -cur_synbuf[k + 31 - i];
                }

                /* k wraps at the synthesis buffer boundary  */
                k = (k + 32) & 511;

                for (i = 0; i < 16; i++)
                {
                    /* Copy next 32 elements */
                    u_vec[uvp + i + 32] = -cur_synbuf[k + 16 - i];
                    u_vec[uvp + i + 48] = -cur_synbuf[k + i];
                }
                u_vec[uvp + 16] = 0;

                /* k wraps at the synthesis buffer boundary  */
                k = (k + 32) & 511;
                uvp += 64;
            }
        }

        void DewindowOutput(float[] u_vec, float[] samples)
        {
            for (int i = 0; i < 512; i++)
            {
                u_vec[i] *= DEWINDOW_TABLE[i];
            }

            for (int i = 0; i < 32; i++)
            {
                float sum = u_vec[i];
                sum += u_vec[i + (1 << 5)];
                sum += u_vec[i + (2 << 5)];
                sum += u_vec[i + (3 << 5)];
                sum += u_vec[i + (4 << 5)];
                sum += u_vec[i + (5 << 5)];
                sum += u_vec[i + (6 << 5)];
                sum += u_vec[i + (7 << 5)];
                sum += u_vec[i + (8 << 5)];
                sum += u_vec[i + (9 << 5)];
                sum += u_vec[i + (10 << 5)];
                sum += u_vec[i + (11 << 5)];
                sum += u_vec[i + (12 << 5)];
                sum += u_vec[i + (13 << 5)];
                sum += u_vec[i + (14 << 5)];
                sum += u_vec[i + (15 << 5)];
                u_vec[i] = sum;
            }

            for (int i = 0; i < 32; i++)
            {
                samples[i] = u_vec[i];
            }
        }
    }
}

/*
 * OmniMusicMpeg - A C# MPEG1/2/2.5 audio decoder
 * 
 */


namespace OmniMusicMpeg.Decoder
{
using System;
    // Layer I is really just a special case of Layer II...  1 granule, 4 allocation bits per subband, 1 scalefactor per active subband, no grouping
    // That (of course) means we literally have no logic here
    class LayerIDecoder : LayerIIDecoderBase
    {
        static internal bool GetCRC(MpegFrame frame, ref uint crc)
        {
            return LayerIIDecoderBase.GetCRC(frame, _rateTable, _allocLookupTable, false, ref crc);
        }

        // this is simple: all 32 subbands have a 4-bit allocations, and positive allocation values are {bits per sample} - 1
        static readonly int[] _rateTable = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        static readonly int[][] _allocLookupTable = { new int[] { 4, 0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 } };

        internal LayerIDecoder() : base(_allocLookupTable, 1) { }

        protected override int[] GetRateTable(IMpegFrame frame)
        {
            return _rateTable;
        }

        protected override void ReadScaleFactorSelection(IMpegFrame frame, int[][] scfsi, int channels)
        {
            // this is a no-op since the base logic uses "2" as the "has energy" marker
        }
    }
}

/*
 * OmniMusicMpeg - A C# MPEG1/2/2.5 audio decoder
 * 
 */


namespace OmniMusicMpeg.Decoder
{
using System;
    // there's not much we have to do here... table selection, granule count, scalefactor selection
    class LayerIIDecoder : LayerIIDecoderBase
    {
        static internal bool GetCRC(MpegFrame frame, ref uint crc)
        {
            return LayerIIDecoderBase.GetCRC(frame, SelectTable(frame), _allocLookupTable, true, ref crc);
        }

        // figure out which rate table to use...  basically, high-rate full, high-rate limited, low-rate limited, low-rate minimal, and LSF.
        static int[] SelectTable(IMpegFrame frame)
        {
            var bitRatePerChannel = (frame.BitRate / (frame.ChannelMode == MpegChannelMode.Mono ? 1 : 2)) / 1000;

            if (frame.Version == MpegVersion.Version1)
            {
                if ((bitRatePerChannel >= 56 && bitRatePerChannel <= 80) || (frame.SampleRate == 48000 && bitRatePerChannel >= 56))
                {
                    return _rateLookupTable[0];   // high-rate, 27 subbands
                }
                else if (frame.SampleRate != 48000 && bitRatePerChannel >= 96)
                {
                    return _rateLookupTable[1];   // high-rate, 30 subbands
                }
                else if (frame.SampleRate != 32000 && bitRatePerChannel <= 48)
                {
                    return _rateLookupTable[2];   // low-rate, 8 subbands
                }
                else
                {
                    return _rateLookupTable[3];   // low-rate, 12 subbands
                }
            }
            else
            {
                return _rateLookupTable[4];   // lsf, 30 subbands
            }
        }

        // this table tells us which allocation lookup list to use for each subband
        // note that each row has the same number of elements as there are subbands for that type...
        static readonly int[][] _rateLookupTable = {
                                                       //          0  1  2  3  4  5  6  7  8  9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29
                                                       new int[] { 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0 },             // high-rate, 27 subbands
                                                       new int[] { 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 },    // high-rate, 30 subbands
                                                       new int[] { 4, 4, 5, 5, 5, 5, 5, 5 },                                                                      // low-rate, 7 subbands
                                                       new int[] { 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 },                                                          // low-rate, 12 subbands
                                                       new int[] { 6, 6, 6, 6, 5, 5, 5, 5, 5, 5, 5, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7 },    // lsf, 30 subbands
                                                   };

        // this tells the decode logic: a) how many bits per allocation, and b) how many bits per sample for the give allocation value
        //  if negative, read -x bits and handle as a group
        static readonly int[][] _allocLookupTable = {
                                                        //       bits   0   1   2   3   4   5   6   7   8   9  10  11  12  13  14  15
                                                        new int[] { 2,  0, -5, -7, 16 },                                                 // 0 (II)
                                                        new int[] { 3,  0, -5, -7,  3,-10,  4,  5, 16 },                                 // 1 (II)
                                                        new int[] { 4,  0, -5, -7,  3,-10,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 16 }, // 2 (II)
                                                        new int[] { 4,  0, -5,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16 }, // 3 (II)
                                                        new int[] { 4,  0, -5, -7,-10,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15 }, // 4 (II, 4, 4 bits per alloc)
                                                        new int[] { 3,  0, -5, -7,-10,  4,  5,  6,  7 },                                 // 5 (II, 4, 3 bits per alloc)
                                                        new int[] { 4,  0, -5, -7,  3,-10,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14 }, // 6 (II)
                                                        new int[] { 2,  0, -5, -7,-10 },                                                 // 7 (II, 4, 2 bits per alloc)
                                                    };

        internal LayerIIDecoder() : base(_allocLookupTable, 3) { }

        protected override int[] GetRateTable(IMpegFrame frame)
        {
            return SelectTable(frame);
        }

        protected override void ReadScaleFactorSelection(IMpegFrame frame, int[][] scfsi, int channels)
        {
            // we'll never have more than 30 active subbands
            for (int sb = 0; sb < 30; sb++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    if (scfsi[ch][sb] == 2)
                    {
                        scfsi[ch][sb] = frame.ReadBits(2);
                    }
                }
            }
        }
    }
}

/*
 * OmniMusicMpeg - A C# MPEG1/2/2.5 audio decoder
 * 
 */


namespace OmniMusicMpeg.Decoder
{
using System;
    // Layers I & II are basically identical...  Layer II adds sample grouping, per subband allocation schemes, and granules
    // Because of this fact, we can use the same decoder for both
    abstract class LayerIIDecoderBase : LayerDecoderBase
    {
        protected const int SSLIMIT = 12;

        static protected bool GetCRC(MpegFrame frame, int[] rateTable, int[][] allocLookupTable, bool readScfsiBits, ref uint crc)
        {
            // ugh...  we basically have to re-implement the allocation logic here.

            // keep up with how many active subbands we need to read selection info for
            var scfsiBits = 0;

            // only read as many subbands as we actually need; pay attention to the intensity stereo subbands
            var subbandCount = rateTable.Length;
            var jsbound = subbandCount;
            if (frame.ChannelMode == MpegChannelMode.JointStereo)
            {
                jsbound = frame.ChannelModeExtension * 4 + 4;
            }

            // read the full stereo subbands
            var channels = frame.ChannelMode == MpegChannelMode.Mono ? 1 : 2;
            var sb = 0;
            for (; sb < jsbound; sb++)
            {
                var bits = allocLookupTable[rateTable[sb]][0];
                for (int ch = 0; ch < channels; ch++)
                {
                    var alloc = frame.ReadBits(bits);
                    if (alloc > 0) scfsiBits += 2;

                    MpegFrame.UpdateCRC(alloc, bits, ref crc);
                }
            }

            // read the intensity stereo subbands
            for (; sb < subbandCount; sb++)
            {
                var bits = allocLookupTable[rateTable[sb]][0];

                var alloc = frame.ReadBits(bits);
                if (alloc > 0) scfsiBits += channels * 2;

                MpegFrame.UpdateCRC(alloc, bits, ref crc);
            }

            // finally, read the scalefac selection bits
            if (readScfsiBits)
            {
                while (scfsiBits >= 2)
                {
                    MpegFrame.UpdateCRC(frame.ReadBits(2), 2, ref crc);
                    scfsiBits -= 2;
                }
            }

            return true;
        }

        #region Lookup Tables

        // this is from the formula: C = 1 / (1 / (1 << (Bits / 2 + Bits % 2 - 1)) + .5f)
        // index by real bits (Bits / 2 + Bits % 2 - 1)
        static readonly float[] _groupedC = { 0, 0, 1.33333333333f, 1.60000000000f, 1.77777777777f };

        // these are always -0.5
        // index by real bits (Bits / 2 + Bits % 2 - 1)
        static readonly float[] _groupedD = { 0, 0, -0.5f, -0.5f, -0.5f };

        // this is from the formula: 1 / (1 - (1f / (1 << Bits)))
        // index by bits
        static readonly float[] _C = {
                                         0.00000000000f,
                                         0.00000000000f, 1.33333333333f, 1.14285714286f, 1.06666666666f, 1.03225806452f, 1.01587301587f, 1.00787401575f, 1.00392156863f,
                                         1.00195694716f, 1.00097751711f, 1.00048851979f, 1.00024420024f, 1.00012208522f, 1.00006103888f, 1.00003051851f, 1.00001525902f
                                     };

        // this is from the formula: 1f / (1 << Bits - 1) - 1
        // index by bits
        static readonly float[] _D = {
                                         0.00000000000f - 0f,
                                         0.00000000000f - 0f, 0.50000000000f - 1f, 0.25000000000f - 1f, 0.12500000000f - 1f, 0.062500000000f - 1f, 0.03125000000f - 1f, 0.01562500000f - 1f, 0.00781250000f - 1f,
                                         0.00390625000f - 1f, 0.00195312500f - 1f, 0.00097656250f - 1f, 0.00048828125f - 1f, 0.000244140630f - 1f, 0.00012207031f - 1f, 0.00006103516f - 1f, 0.00003051758f - 1f
                                     };

        // this is from a (really annoying) formula:
        //   x = Math.Pow(4, 1 / ((2 << (idx % 3) + 1) - (idx % 3))) / (1 << (idx / 3))
        // Basically...
        //   [0] = Math.Pow(4, 1 / 2), [1] = Math.Pow(4, 1 / 3), [2] = Math.Pow(4, 1 / 6)
        //   For every remaining element, calculate (in order): [idx] = [idx - 3] / 2
        static readonly float[] _denormalMultiplier = {
                                                          2.00000000000000f, 1.58740105196820f, 1.25992104989487f, 1.00000000000000f, 0.79370052598410f, 0.62996052494744f, 0.50000000000000f, 0.39685026299205f,
                                                          0.31498026247372f, 0.25000000000000f, 0.19842513149602f, 0.15749013123686f, 0.12500000000000f, 0.09921256574801f, 0.07874506561843f, 0.06250000000000f,
                                                          0.04960628287401f, 0.03937253280921f, 0.03125000000000f, 0.02480314143700f, 0.01968626640461f, 0.01562500000000f, 0.01240157071850f, 0.00984313320230f,
                                                          0.00781250000000f, 0.00620078535925f, 0.00492156660115f, 0.00390625000000f, 0.00310039267963f, 0.00246078330058f, 0.00195312500000f, 0.00155019633981f,
                                                          0.00123039165029f, 0.00097656250000f, 0.00077509816991f, 0.00061519582514f, 0.00048828125000f, 0.00038754908495f, 0.00030759791257f, 0.00024414062500f,
                                                          0.00019377454248f, 0.00015379895629f, 0.00012207031250f, 0.00009688727124f, 0.00007689947814f, 0.00006103515625f, 0.00004844363562f, 0.00003844973907f,
                                                          0.00003051757813f, 0.00002422181781f, 0.00001922486954f, 0.00001525878906f, 0.00001211090890f, 0.00000961243477f, 0.00000762939453f, 0.00000605545445f,
                                                          0.00000480621738f, 0.00000381469727f, 0.00000302772723f, 0.00000240310869f, 0.00000190734863f, 0.00000151386361f, 0.00000120155435f, 0.00000095367432f
                                                      };

        #endregion

        int _channels, _jsbound, _granuleCount;
        int[][] _allocLookupTable, _scfsi, _samples;
        int[][][] _scalefac;
        float[] _polyPhaseBuf;
        float[][] _chanBufs = new float[2][];

        int[][] _allocation;

        protected LayerIIDecoderBase(int[][] allocLookupTable, int granuleCount)
            : base()
        {
            _allocLookupTable = allocLookupTable;
            _granuleCount = granuleCount;

            _allocation = new int[][] { new int[SBLIMIT], new int[SBLIMIT] };
            _scfsi = new int[][] { new int[SBLIMIT], new int[SBLIMIT] };
            _samples = new int[][] { new int[SBLIMIT * SSLIMIT * _granuleCount], new int[SBLIMIT * SSLIMIT * _granuleCount] };

            // NB: ReadScaleFactors(...) requires all three granules, even in Layer I
            _scalefac = new int[][][] { new int[3][], new int[3][] };
            for (int i = 0; i < 3; i++)
            {
                _scalefac[0][i] = new int[SBLIMIT];
                _scalefac[1][i] = new int[SBLIMIT];
            }

            _polyPhaseBuf = new float[SBLIMIT];
        }

        internal override int DecodeFrame(IMpegFrame frame, float[] ch0, float[] ch1)
        {
            InitFrame(frame);

            var rateTable = GetRateTable(frame);

            ReadAllocation(frame, rateTable);

            for (int i = 0; i < _scfsi[0].Length; i++)
            {
                // Since Layer II has to know which subbands have energy, we use the "Layer I valid" selection to mark that energy is present.
                // That way Layer I doesn't have to do anything else.
                _scfsi[0][i] = _allocation[0][i] != 0 ? 2 : -1;
                _scfsi[1][i] = _allocation[1][i] != 0 ? 2 : -1;
            }
            ReadScaleFactorSelection(frame, _scfsi, _channels);

            ReadScaleFactors(frame);

            ReadSamples(frame);

            return DecodeSamples(ch0, ch1);
        }

        // this just reads the channel mode and set a few flags
        void InitFrame(IMpegFrame frame)
        {
            switch (frame.ChannelMode)
            {
                case MpegChannelMode.Mono:
                    _channels = 1;
                    _jsbound = SBLIMIT;
                    break;
                case MpegChannelMode.JointStereo:
                    _channels = 2;
                    _jsbound = frame.ChannelModeExtension * 4 + 4;
                    break;
                default:
                    _channels = 2;
                    _jsbound = SBLIMIT;
                    break;
            }
        }

        abstract protected int[] GetRateTable(IMpegFrame frame);

        void ReadAllocation(IMpegFrame frame, int[] rateTable)
        {
            var _subBandCount = rateTable.Length;
            if (_jsbound > _subBandCount) _jsbound = _subBandCount;

            Array.Clear(_allocation[0], 0, SBLIMIT);
            Array.Clear(_allocation[1], 0, SBLIMIT);

            int sb = 0;
            for (; sb < _jsbound; sb++)
            {
                var table = _allocLookupTable[rateTable[sb]];
                var bits = table[0];
                for (int ch = 0; ch < _channels; ch++)
                {
                    _allocation[ch][sb] = table[frame.ReadBits(bits) + 1];
                }
            }
            for (; sb < _subBandCount; sb++)
            {
                var table = _allocLookupTable[rateTable[sb]];
                _allocation[0][sb] = _allocation[1][sb] = table[frame.ReadBits(table[0]) + 1];
            }
        }

        abstract protected void ReadScaleFactorSelection(IMpegFrame frame, int[][] scfsi, int channels);

        void ReadScaleFactors(IMpegFrame frame)
        {
            for (int sb = 0; sb < SBLIMIT; sb++)
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    switch (_scfsi[ch][sb])
                    {
                        case 0:
                            // all three
                            _scalefac[ch][0][sb] = frame.ReadBits(6);
                            _scalefac[ch][1][sb] = frame.ReadBits(6);
                            _scalefac[ch][2][sb] = frame.ReadBits(6);
                            break;
                        case 1:
                            // only two (2 = 1)
                            _scalefac[ch][0][sb] =
                            _scalefac[ch][1][sb] = frame.ReadBits(6);
                            _scalefac[ch][2][sb] = frame.ReadBits(6);
                            break;
                        case 2:
                            // only one (3 = 2 = 1)
                            _scalefac[ch][0][sb] =
                            _scalefac[ch][1][sb] =
                            _scalefac[ch][2][sb] = frame.ReadBits(6);
                            break;
                        case 3:
                            // only two (3 = 2)
                            _scalefac[ch][0][sb] = frame.ReadBits(6);
                            _scalefac[ch][1][sb] =
                            _scalefac[ch][2][sb] = frame.ReadBits(6);
                            break;
                        default:
                            // none
                            _scalefac[ch][0][sb] = 63;
                            _scalefac[ch][1][sb] = 63;
                            _scalefac[ch][2][sb] = 63;
                            break;
                    }
                }
            }
        }

        void ReadSamples(IMpegFrame frame)
        {
            // load in all the data for this frame (1152 samples in this case)
            // NB: we flatten these into output order
            for (int ss = 0, idx = 0; ss < SSLIMIT; ss++, idx += SBLIMIT * (_granuleCount - 1))
            {
                for (int sb = 0; sb < SBLIMIT; sb++, idx++)
                {
                    for (int ch = 0; ch < _channels; ch++)
                    {
                        if (ch == 0 || sb < _jsbound)
                        {
                            var alloc = _allocation[ch][sb];
                            if (alloc != 0)
                            {
                                if (alloc < 0)
                                {
                                    // grouping (Layer II only, so we don't have to play with the granule count)
                                    // NB: 3 samples are packed in one -alloc-bit codeword; the digit base
                                    //     is nlevels (3, 5, or 9), one power of two below the C/D index.
                                    var val = frame.ReadBits(-alloc);
                                    var levels = (1 << (-alloc / 2 + -alloc % 2 - 2)) + 1;

                                    _samples[ch][idx] = val % levels;
                                    val /= levels;
                                    _samples[ch][idx + SBLIMIT] = val % levels;
                                    _samples[ch][idx + SBLIMIT * 2] = val / levels;
                                }
                                else
                                {
                                    // non-grouping
                                    for (int gr = 0; gr < _granuleCount; gr++)
                                    {
                                        _samples[ch][idx + SBLIMIT * gr] = frame.ReadBits(alloc);
                                    }
                                }
                            }
                            else
                            {
                                // no energy...  zero out the samples
                                for (int gr = 0; gr < _granuleCount; gr++)
                                {
                                    _samples[ch][idx + SBLIMIT * gr] = 0;
                                }
                            }
                        }
                        else
                        {
                            // copy chan 0 to chan 1
                            for (int gr = 0; gr < _granuleCount; gr++)
                            {
                                _samples[1][idx + SBLIMIT * gr] = _samples[0][idx + SBLIMIT * gr];
                            }
                        }
                    }
                }
            }
        }

        int DecodeSamples(float[] ch0, float[] ch1)
        {
            // do our stereo mode setup
            var chanBufs = _chanBufs;
            var startChannel = 0;
            var endChannel = _channels - 1;
            if (_channels == 1 || StereoMode == StereoMode.LeftOnly)
            {
                chanBufs[0] = ch0;
                endChannel = 0;
            }
            else if (StereoMode == StereoMode.RightOnly)
            {
                chanBufs[1] = ch0;  // this is correct... if there's only a single channel output, it goes in channel 0's buffer
                startChannel = 1;
            }
            else    // MpegStereoMode.Both or StereoMode.DownmixToMono
            {
                chanBufs[0] = ch0;
                chanBufs[1] = ch1;
            }

            int idx = 0;
            for (int ch = startChannel; ch <= endChannel; ch++)
            {
                idx = 0;
                for (int gr = 0; gr < _granuleCount; gr++)
                {
                    for (int ss = 0; ss < SSLIMIT; ss++)
                    {
                        for (int sb = 0; sb < SBLIMIT; sb++, idx++)
                        {
                            // do the dequant and the denorm; output to _polyPhaseBuf
                            // NB: Layers I & II use the same algorithm here...  Grouping changes the bit counts, but doesn't change the algo
                            //     - Up to 65534 possible values (65535 does not appear to be usable)
                            //     - All values can be handled with 16-bit logic as long as the correct C and D constants are used
                            //     - Make sure to normalize each sample to 16 bits!

                            var alloc = _allocation[ch][sb];
                            if (alloc != 0)
                            {
                                float[] c, d;
                                if (alloc < 0)
                                {
                                    alloc = -alloc / 2 + -alloc % 2 - 1;
                                    c = _groupedC;
                                    d = _groupedD;
                                }
                                else
                                {
                                    c = _C;
                                    d = _D;
                                }

                                // read sample; normalize, scale & center to [-0.999984741f..0.999984741f]; apply scalefactor
                                _polyPhaseBuf[sb] = c[alloc] * ((_samples[ch][idx] << (16 - alloc)) / 32768f + d[alloc]) * _denormalMultiplier[_scalefac[ch][gr][sb]];
                            }
                            else
                            {
                                // no transmitted energy...
                                _polyPhaseBuf[sb] = 0f;
                            }
                        }

                        // do the polyphase output for this channel, section, and granule
                        base.InversePolyPhase(ch, _polyPhaseBuf);
                        Array.Copy(_polyPhaseBuf, 0, chanBufs[ch], idx - SBLIMIT, SBLIMIT);
                    }
                }
            }

            if (_channels == 2 && StereoMode == OmniMusicMpeg.StereoMode.DownmixToMono)
            {
                for (int i = 0; i < idx; i++)
                {
                    ch0[i] = (ch0[i] + ch1[i]) / 2;
                }
            }

            return idx;
        }
    }
}

/*
 * OmniMusicMpeg - A C# MPEG1/2/2.5 audio decoder
 * 
 * Portions of this file are courtesy Fluendo, S.A.  They are dual licensed as Ms-PL
 * and under the following license:
 *
 *   Copyright <2005-2012> Fluendo S.A.
 *   
 *   Unless otherwise indicated, Source Code is licensed under MIT license.
 *   See further explanation attached in License Statement (distributed in the file
 *   LICENSE).
 *   
 *   Permission is hereby granted, free of charge, to any person obtaining a copy of
 *   this software and associated documentation files (the "Software"), to deal in
 *   the Software without restriction, including without limitation the rights to
 *   use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
 *   of the Software, and to permit persons to whom the Software is furnished to do
 *   so, subject to the following conditions:
 *   
 *   The above copyright notice and this permission notice shall be included in all
 *   copies or substantial portions of the Software.
 *   
 *   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 *   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 *   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 *   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 *   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 *   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 *   SOFTWARE.
 *
 */


namespace OmniMusicMpeg.Decoder
{
using System;
using System.Collections.Generic;
    /// <summary>
    /// Class Implementing Layer 3 Decoder.
    /// </summary>
    sealed class LayerIIIDecoder : LayerDecoderBase
    {
        const int SSLIMIT = 18;

        #region Child Classes

        // This class is based on the Fluendo hybrid logic.
        class HybridMDCT
        {
            const float PI = (float)Math.PI;

            static float[][] _swin;

            static HybridMDCT()
            {
                _swin = new float[][] { new float[36], new float[36], new float[36], new float[36] };

                int i;

                /* type 0 */
                for (i = 0; i < 36; i++)
                    _swin[0][i] = (float)Math.Sin(PI / 36 * (i + 0.5));

                /* type 1 */
                for (i = 0; i < 18; i++)
                    _swin[1][i] = (float)Math.Sin(PI / 36 * (i + 0.5));
                for (i = 18; i < 24; i++)
                    _swin[1][i] = 1.0f;
                for (i = 24; i < 30; i++)
                    _swin[1][i] = (float)Math.Sin(PI / 12 * (i + 0.5 - 18));
                for (i = 30; i < 36; i++)
                    _swin[1][i] = 0.0f;

                /* type 3 */
                for (i = 0; i < 6; i++)
                    _swin[3][i] = 0.0f;
                for (i = 6; i < 12; i++)
                    _swin[3][i] = (float)Math.Sin(PI / 12 * (i + 0.5 - 6));
                for (i = 12; i < 18; i++)
                    _swin[3][i] = 1.0f;
                for (i = 18; i < 36; i++)
                    _swin[3][i] = (float)Math.Sin(PI / 36 * (i + 0.5));

                /* type 2 */
                for (i = 0; i < 12; i++)
                    _swin[2][i] = (float)Math.Sin(PI / 12 * (i + 0.5));
                for (i = 12; i < 36; i++)
                    _swin[2][i] = 0.0f;
            }

            #region Tables

            static float[] icos72_table = {
                                          5.004763425816599609063928255636710673570632934570312500000000e-01f,
                                          5.019099187716736798492433990759309381246566772460937500000000e-01f,
                                          5.043144802900764167574720886477734893560409545898437500000000e-01f,
                                          5.077133059428725614381505693017970770597457885742187500000000e-01f,
                                          5.121397571572545714957414020318537950515747070312500000000000e-01f,
                                          5.176380902050414789528076653368771076202392578125000000000000e-01f,
                                          5.242645625704053236049162478593643754720687866210937500000000e-01f,
                                          5.320888862379560269033618169487453997135162353515625000000000e-01f,
                                          5.411961001461970122150546558259520679712295532226562500000000e-01f,
                                          5.516889594812458552652856269560288637876510620117187500000000e-01f,
                                          5.636909734331712051869089918909594416618347167968750000000000e-01f,
                                          5.773502691896257310588680411456152796745300292968750000000000e-01f,
                                          5.928445237170802961657045671017840504646301269531250000000000e-01f,
                                          6.103872943807280293526673631276935338973999023437500000000000e-01f,
                                          6.302362070051321651931175438221544027328491210937500000000000e-01f,
                                          6.527036446661392821155800447741057723760604858398437500000000e-01f,
                                          6.781708524546284921896699415810871869325637817382812500000000e-01f,
                                          7.071067811865474617150084668537601828575134277343750000000000e-01f,
                                          7.400936164611303658134033867099788039922714233398437500000000e-01f,
                                          7.778619134302061643992942663317080587148666381835937500000000e-01f,
                                          8.213398158522907666068135768000502139329910278320312500000000e-01f,
                                          8.717233978105488612087015098950359970331192016601562500000000e-01f,
                                          9.305794983517888807611484480730723589658737182617187500000000e-01f,
                                          9.999999999999997779553950749686919152736663818359375000000000e-01f,
                                          1.082840285100100219395358180918265134096145629882812500000000e+00f,
                                          1.183100791576249255498964885191526263952255249023437500000000e+00f,
                                          1.306562964876376353728915091778617352247238159179687500000000e+00f,
                                          1.461902200081543146126250576344318687915802001953125000000000e+00f,
                                          1.662754761711521034328598034335300326347351074218750000000000e+00f,
                                          1.931851652578135070115195048856548964977264404296875000000000e+00f,
                                          2.310113157672649020213384574162773787975311279296875000000000e+00f,
                                          2.879385241571815523542454684502445161342620849609375000000000e+00f,
                                          3.830648787770197127855453800293616950511932373046875000000000e+00f,
                                          5.736856622834929808618653623852878808975219726562500000000000e+00f,
                                          1.146279281302667207853573927422985434532165527343750000000000e+01f
                                          };

            #endregion

            List<float[]> _prevBlock;
            List<float[]> _nextBlock;

            internal HybridMDCT()
            {
                _prevBlock = new List<float[]>();
                _nextBlock = new List<float[]>();
            }

            internal void Reset()
            {
                _prevBlock.Clear();
                _nextBlock.Clear();
            }

            void GetPrevBlock(int channel, out float[] prevBlock, out float[] nextBlock)
            {
                while (_prevBlock.Count <= channel)
                {
                    _prevBlock.Add(new float[SSLIMIT * SBLIMIT]);
                }
                while (_nextBlock.Count <= channel)
                {
                    _nextBlock.Add(new float[SSLIMIT * SBLIMIT]);
                }
                prevBlock = _prevBlock[channel];
                nextBlock = _nextBlock[channel];

                // now swap them (see Apply(...) below)
                _nextBlock[channel] = prevBlock;
                _prevBlock[channel] = nextBlock;
            }

            internal void Apply(float[] fsIn, int channel, int blockType, bool doMixed)
            {
                // get the previous & next blocks so we can overlap correctly
                //  NB: we swap each pass so we can add the previous block in a single pass
                float[] prevblck, nextblck;
                GetPrevBlock(channel, out prevblck, out nextblck);

                // now we have a few options for processing blocks...
                int start = 0;
                if (doMixed)
                {
                    // a mixed block always has the first two subbands as blocktype 0
                    LongImpl(fsIn, 0, 2, nextblck, 0);
                    start = 2;
                }

                if (blockType == 2)
                {
                    // this is the only place we care about short blocks
                    ShortImpl(fsIn, start, nextblck);
                }
                else
                {
                    LongImpl(fsIn, start, SBLIMIT, nextblck, blockType);
                }

                // overlap
                for (int i = 0; i < SSLIMIT * SBLIMIT; i++)
                {
                    fsIn[i] += prevblck[i];
                }
            }

            float[] _imdctTemp = new float[SSLIMIT];
            float[] _imdctResult = new float[SSLIMIT * 2];

            float[] _longImdct_H = new float[17];
            float[] _longImdct_h = new float[18];
            float[] _longImdct_even = new float[9];
            float[] _longImdct_odd = new float[9];
            float[] _longImdct_evenIdct = new float[9];
            float[] _longImdct_oddIdct = new float[9];

            float[] _imdct9pt_even_idct = new float[5];
            float[] _imdct9pt_odd_idct = new float[4];

            float[] _shortImdct_H = new float[6];
            float[] _shortImdct_h = new float[6];
            float[] _shortImdct_evenIdct = new float[3];
            float[] _shortImdct_oddIdct = new float[3];

            void LongImpl(float[] fsIn, int sbStart, int sbLimit, float[] nextblck, int blockType)
            {
                for (int sb = sbStart, ofs = sbStart * SSLIMIT; sb < sbLimit; sb++)
                {
                    // IMDCT
                    Array.Copy(fsIn, ofs, _imdctTemp, 0, SSLIMIT);
                    LongIMDCT(_imdctTemp, _imdctResult);

                    // window
                    var win = _swin[blockType];
                    int i = 0;
                    for (; i < SSLIMIT; i++)
                    {
                        fsIn[ofs++] = _imdctResult[i] * win[i];
                    }
                    ofs -= 18;
                    for (; i < SSLIMIT * 2; i++)
                    {
                        nextblck[ofs++] = _imdctResult[i] * win[i];
                    }
                }
            }

            void LongIMDCT(float[] invec, float[] outvec)
            {
                int i;
                float[] H = _longImdct_H, h = _longImdct_h, even = _longImdct_even, odd = _longImdct_odd, even_idct = _longImdct_evenIdct, odd_idct = _longImdct_oddIdct;

                for (i = 0; i < 17; i++)
                    H[i] = invec[i] + invec[i + 1];

                even[0] = invec[0];
                odd[0] = H[0];
                var idx = 0;
                for (i = 1; i < 9; i++, idx += 2)
                {
                    even[i] = H[idx + 1];
                    odd[i] = H[idx] + H[idx + 2];
                }

                imdct_9pt(even, even_idct);
                imdct_9pt(odd, odd_idct);

                for (i = 0; i < 9; i++)
                {
                    odd_idct[i] *= ICOS36_A(i);
                    h[i] = (even_idct[i] + odd_idct[i]) * ICOS72_A(i);
                }
                for ( /* i = 9 */ ; i < 18; i++)
                {
                    h[i] = (even_idct[17 - i] - odd_idct[17 - i]) * ICOS72_A(i);
                }

                /* Rearrange the 18 values from the IDCT to the output vector */
                outvec[0] = h[9];
                outvec[1] = h[10];
                outvec[2] = h[11];
                outvec[3] = h[12];
                outvec[4] = h[13];
                outvec[5] = h[14];
                outvec[6] = h[15];
                outvec[7] = h[16];
                outvec[8] = h[17];

                outvec[9] = -h[17];
                outvec[10] = -h[16];
                outvec[11] = -h[15];
                outvec[12] = -h[14];
                outvec[13] = -h[13];
                outvec[14] = -h[12];
                outvec[15] = -h[11];
                outvec[16] = -h[10];
                outvec[17] = -h[9];

                outvec[35] = outvec[18] = -h[8];
                outvec[34] = outvec[19] = -h[7];
                outvec[33] = outvec[20] = -h[6];
                outvec[32] = outvec[21] = -h[5];
                outvec[31] = outvec[22] = -h[4];
                outvec[30] = outvec[23] = -h[3];
                outvec[29] = outvec[24] = -h[2];
                outvec[28] = outvec[25] = -h[1];
                outvec[27] = outvec[26] = -h[0];
            }

            static float ICOS72_A(int i)
            {
                return icos72_table[2 * i];
            }

            static float ICOS36_A(int i)
            {
                return icos72_table[4 * i + 1];
            }

            void imdct_9pt(float[] invec, float[] outvec)
            {
                int i;
                float[] even_idct = _imdct9pt_even_idct, odd_idct = _imdct9pt_odd_idct;
                float t0, t1, t2;

                /* BEGIN 5 Point IMDCT */
                t0 = invec[6] / 2.0f + invec[0];
                t1 = invec[0] - invec[6];
                t2 = invec[2] - invec[4] - invec[8];

                even_idct[0] = t0 + invec[2] * 0.939692621f
                    + invec[4] * 0.766044443f + invec[8] * 0.173648178f;

                even_idct[1] = t2 / 2.0f + t1;
                even_idct[2] = t0 - invec[2] * 0.173648178f
                    - invec[4] * 0.939692621f + invec[8] * 0.766044443f;

                even_idct[3] = t0 - invec[2] * 0.766044443f
                    + invec[4] * 0.173648178f - invec[8] * 0.939692621f;

                even_idct[4] = t1 - t2;
                /* END 5 Point IMDCT */

                /* BEGIN 4 Point IMDCT */
                {
                    float odd1, odd2;
                    odd1 = invec[1] + invec[3];
                    odd2 = invec[3] + invec[5];
                    t0 = (invec[5] + invec[7]) * 0.5f + invec[1];

                    odd_idct[0] = t0 + odd1 * 0.939692621f + odd2 * 0.766044443f;
                    odd_idct[1] = (invec[1] - invec[5]) * 1.5f - invec[7];
                    odd_idct[2] = t0 - odd1 * 0.173648178f - odd2 * 0.939692621f;
                    odd_idct[3] = t0 - odd1 * 0.766044443f + odd2 * 0.173648178f;
                }
                /* END 4 Point IMDCT */

                /* Adjust for non power of 2 IDCT */
                odd_idct[0] += invec[7] * 0.173648178f;
                odd_idct[1] -= invec[7] * 0.5f;
                odd_idct[2] += invec[7] * 0.766044443f;
                odd_idct[3] -= invec[7] * 0.939692621f;

                /* Post-Twiddle */
                odd_idct[0] *= 0.5f / 0.984807753f;
                odd_idct[1] *= 0.5f / 0.866025404f;
                odd_idct[2] *= 0.5f / 0.64278761f;
                odd_idct[3] *= 0.5f / 0.342020143f;

                for (i = 0; i < 4; i++)
                {
                    outvec[i] = even_idct[i] + odd_idct[i];
                }
                outvec[4] = even_idct[4];
                /* Mirror into the other half of the vector */
                for (i = 5; i < 9; i++)
                {
                    outvec[i] = even_idct[8 - i] - odd_idct[8 - i];
                }
            }

            void ShortImpl(float[] fsIn, int sbStart, float[] nextblck)
            {
                var win = _swin[2];

                for (int sb = sbStart, ofs = sbStart * SSLIMIT; sb < SBLIMIT; sb++, ofs += SSLIMIT)
                {
                    // rearrange vectors
                    for (int i = 0, tmpptr = 0; i < 3; i++)
                    {
                        var v = ofs + i;
                        for (int j = 0; j < 6; j++)
                        {
                            _imdctTemp[tmpptr + j] = fsIn[v];
                            v += 3;
                        }
                        tmpptr += 6;
                    }

                    // short blocks are fun...  3 separate IMDCT's with overlap in two different buffers

                    Array.Clear(fsIn, ofs, 6);

                    // do the first 6 samples
                    ShortIMDCT(_imdctTemp, 0, _imdctResult);
                    Array.Copy(_imdctResult, 0, fsIn, ofs + 6, 12);

                    // now the next 6
                    ShortIMDCT(_imdctTemp, 6, _imdctResult);
                    for (int i = 0; i < 6; i++)
                    {
                        // add the first half to tsOut
                        fsIn[ofs + i + 12] += _imdctResult[i];
                    }
                    Array.Copy(_imdctResult, 6, nextblck, ofs, 6);

                    // now the final 6
                    ShortIMDCT(_imdctTemp, 12, _imdctResult);
                    for (int i = 0; i < 6; i++)
                    {
                        // add the first half to nextblck
                        nextblck[ofs + i] += _imdctResult[i];
                    }
                    Array.Copy(_imdctResult, 6, nextblck, ofs + 6, 6);
                    Array.Clear(nextblck, ofs + 12, 6);
                }
            }

            const float sqrt32 = 0.8660254037844385965883020617184229195117950439453125f;

            void ShortIMDCT(float[] invec, int inIdx, float[] outvec)
            {
                int i;
                float[] H = _shortImdct_H, h = _shortImdct_h, even_idct = _shortImdct_evenIdct, odd_idct = _shortImdct_oddIdct;
                float t0, t1, t2;

                /* Preprocess the input to the two 3-point IDCT's */
                var idx = inIdx;
                for (i = 1; i < 6; i++)
                {
                    H[i] = invec[idx];
                    H[i] += invec[++idx];
                }

                /* 3-point IMDCT */
                t0 = H[4] / 2.0f + invec[inIdx];
                t1 = H[2] * sqrt32;
                even_idct[0] = t0 + t1;
                even_idct[1] = invec[inIdx] - H[4];
                even_idct[2] = t0 - t1;
                /* END 3-point IMDCT */

                /* 3-point IMDCT */
                t2 = H[3] + H[5];

                t0 = (t2) / 2.0f + H[1];
                t1 = (H[1] + H[3]) * sqrt32;
                odd_idct[0] = t0 + t1;
                odd_idct[1] = H[1] - t2;
                odd_idct[2] = t0 - t1;
                /* END 3-point IMDCT */

                /* Post-Twiddle */
                odd_idct[0] *= 0.51763809f;
                odd_idct[1] *= 0.707106781f;
                odd_idct[2] *= 1.931851653f;

                h[0] = (even_idct[0] + odd_idct[0]) * 0.50431448f;
                h[1] = (even_idct[1] + odd_idct[1]) * 0.5411961f;
                h[2] = (even_idct[2] + odd_idct[2]) * 0.630236207f;

                h[3] = (even_idct[2] - odd_idct[2]) * 0.821339816f;
                h[4] = (even_idct[1] - odd_idct[1]) * 1.306562965f;
                h[5] = (even_idct[0] - odd_idct[0]) * 3.830648788f;

                /* Rearrange the 6 values from the IDCT to the output vector */
                outvec[0] = h[3] * _swin[2][0];
                outvec[1] = h[4] * _swin[2][1];
                outvec[2] = h[5] * _swin[2][2];
                outvec[3] = -h[5] * _swin[2][3];
                outvec[4] = -h[4] * _swin[2][4];
                outvec[5] = -h[3] * _swin[2][5];
                outvec[6] = -h[2] * _swin[2][6];
                outvec[7] = -h[1] * _swin[2][7];
                outvec[8] = -h[0] * _swin[2][8];
                outvec[9] = -h[0] * _swin[2][9];
                outvec[10] = -h[1] * _swin[2][10];
                outvec[11] = -h[2] * _swin[2][11];
            }
        }

        #endregion

        static internal bool GetCRC(MpegFrame frame, ref uint crc)
        {
            var cnt = frame.GetSideDataSize();
            while (--cnt >= 0)
            {
                MpegFrame.UpdateCRC(frame.ReadBits(8), 8, ref crc);
            }
            return true;
        }

        HybridMDCT _hybrid = new HybridMDCT();
        BitReservoir _bitRes = new BitReservoir();
        float[][] _decodeFrameChanBufs = new float[2][];

        internal LayerIIIDecoder()
        {
            _tableSelect = new int[][][]
            {
                new int[][] { new int[3], new int[3] },
                new int[][] { new int[3], new int[3] },
            };

            _subblockGain = new float[][][]
            {
                new float[][] { new float[3], new float[3] },
                new float[][] { new float[3], new float[3] },
            };
        }

        internal override int DecodeFrame(IMpegFrame frame, float[] ch0, float[] ch1)
        {
            // load the frame information
            ReadSideInfo(frame);

            // load the frame's main data
            if (!_bitRes.AddBits(frame, _mainDataBegin))
            {
                return 0;
            }

            // prep the reusable tables
            PrepTables(frame);

            // do our stereo mode setup
            var chanBufs = _decodeFrameChanBufs;
            var startChannel = 0;
            var endChannel = _channels - 1;
            if (_channels == 1 || StereoMode == StereoMode.LeftOnly || StereoMode == StereoMode.DownmixToMono)
            {
                chanBufs[0] = ch0;
                endChannel = 0;
            }
            else if (StereoMode == StereoMode.RightOnly)
            {
                chanBufs[1] = ch0;  // this is correct... if there's only a single channel output, it goes in channel 0's buffer
                startChannel = 1;
            }
            else    // MpegStereoMode.Both
            {
                chanBufs[0] = ch0;
                chanBufs[1] = ch1;
            }

            // get the granule count
            int granules;
            if (frame.Version == MpegVersion.Version1)
            {
                granules = 2;
            }
            else
            {
                granules = 1;
            }

            // decode the audio data
            int offset = 0;
            for (var gr = 0; gr < granules; gr++)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    // read scale factors
                    int sfbits;
                    if (frame.Version == MpegVersion.Version1)
                    {
                        sfbits = ReadScalefactors(gr, ch);
                    }
                    else
                    {
                        sfbits = ReadLsfScalefactors(gr, ch, frame.ChannelModeExtension);
                    }

                    // huffman & dequant
                    ReadSamples(sfbits, gr, ch);
                }

                // stereo processing
                Stereo(frame.ChannelMode, frame.ChannelModeExtension, gr, frame.Version != MpegVersion.Version1);

                for (int ch = startChannel; ch <= endChannel; ch++)
                {
                    // pull some values so we don't have to index them again later
                    var buf = _samples[ch];
                    var blockType = _blockType[gr][ch];
                    var blockSplit = _blockSplitFlag[gr][ch];
                    var mixedBlock = _mixedBlockFlag[gr][ch];

                    // do the short/long/mixed logic here so it's only done once per channel per granule
                    if (blockSplit && blockType == 2)
                    {
                        if (mixedBlock)
                        {
                            // reorder & antialias mixed blocks
                            Reorder(buf, true);
                            AntiAlias(buf, true);
                        }
                        else
                        {
                            // reorder short blocks
                            Reorder(buf, false);
                        }
                    }
                    else
                    {
                        // antialias long blocks
                        AntiAlias(buf, false);
                    }

                    // hybrid processing
                    _hybrid.Apply(buf, ch, blockType, blockSplit && mixedBlock);

                    // frequency inversion
                    FrequencyInversion(buf);

                    // inverse polyphase
                    InversePolyphase(buf, ch, offset, chanBufs[ch]);
                }

                offset += SBLIMIT * SSLIMIT;
            }

            return offset;
        }

        internal override void ResetForSeek()
        {
            base.ResetForSeek();

            _hybrid.Reset();

            _bitRes.Reset();
        }

        #region Side Info

        #region Variables

        int _channels, _privBits, _mainDataBegin;

        int[][] _scfsi = { new int[4], new int[4] };                //     ch, scfsi_band
        int[][] _part23Length = { new int[2], new int[2] };         // gr, ch
        int[][] _bigValues = { new int[2], new int[2] };            // gr, ch
        float[][] _globalGain = { new float[2], new float[2] };     // gr, ch
        int[][] _scalefacCompress = { new int[2], new int[2] };     // gr, ch
        bool[][] _blockSplitFlag = { new bool[2], new bool[2] };    // gr, ch
        bool[][] _mixedBlockFlag = { new bool[2], new bool[2] };    // gr, ch
        int[][] _blockType = { new int[2], new int[2] };            // gr, ch
        int[][][] _tableSelect;                                     // gr, ch, region
        float[][][] _subblockGain;                                  // gr, ch, window
        int[][] _regionAddress1 = { new int[2], new int[2] };       // gr, ch
        int[][] _regionAddress2 = { new int[2], new int[2] };       // gr, ch
        int[][] _preflag = { new int[2], new int[2] };              // gr, ch
        float[][] _scalefacScale = { new float[2], new float[2] };  // gr, ch
        int[][] _count1TableSelect = { new int[2], new int[2] };    // gr, ch

        static float[] GAIN_TAB =
        {
            1.57009245868378E-16f, 1.86716512307887E-16f, 2.22044604925031E-16f, 2.64057024024816E-16f, 3.14018491736756E-16f, 3.73433024615774E-16f, 4.44089209850063E-16f, 5.28114048049630E-16f,
            6.28036983473509E-16f, 7.46866049231544E-16f, 8.88178419700125E-16f, 1.05622809609926E-15f, 1.25607396694702E-15f, 1.49373209846309E-15f, 1.77635683940025E-15f, 2.11245619219853E-15f,
            2.51214793389404E-15f, 2.98746419692619E-15f, 3.55271367880050E-15f, 4.22491238439706E-15f, 5.02429586778810E-15f, 5.97492839385238E-15f, 7.10542735760100E-15f, 8.44982476879408E-15f,
            1.00485917355761E-14f, 1.19498567877047E-14f, 1.42108547152020E-14f, 1.68996495375882E-14f, 2.00971834711523E-14f, 2.38997135754094E-14f, 2.84217094304040E-14f, 3.37992990751764E-14f,
            4.01943669423047E-14f, 4.77994271508190E-14f, 5.68434188608080E-14f, 6.75985981503528E-14f, 8.03887338846093E-14f, 9.55988543016378E-14f, 1.13686837721616E-13f, 1.35197196300706E-13f,
            1.60777467769219E-13f, 1.91197708603275E-13f, 2.27373675443232E-13f, 2.70394392601411E-13f, 3.21554935538437E-13f, 3.82395417206551E-13f, 4.54747350886464E-13f, 5.40788785202823E-13f,
            6.43109871076876E-13f, 7.64790834413101E-13f, 9.09494701772928E-13f, 1.08157757040564E-12f, 1.28621974215375E-12f, 1.52958166882621E-12f, 1.81898940354586E-12f, 2.16315514081129E-12f,
            2.57243948430750E-12f, 3.05916333765241E-12f, 3.63797880709171E-12f, 4.32631028162258E-12f, 5.14487896861500E-12f, 6.11832667530482E-12f, 7.27595761418343E-12f, 8.65262056324518E-12f,
            1.02897579372300E-11f, 1.22366533506096E-11f, 1.45519152283669E-11f, 1.73052411264903E-11f, 2.05795158744600E-11f, 2.44733067012193E-11f, 2.91038304567337E-11f, 3.46104822529806E-11f,
            4.11590317489199E-11f, 4.89466134024385E-11f, 5.82076609134674E-11f, 6.92209645059613E-11f, 8.23180634978400E-11f, 9.78932268048772E-11f, 1.16415321826935E-10f, 1.38441929011922E-10f,
            1.64636126995680E-10f, 1.95786453609754E-10f, 2.32830643653870E-10f, 2.76883858023845E-10f, 3.29272253991360E-10f, 3.91572907219509E-10f, 4.65661287307739E-10f, 5.53767716047690E-10f,
            6.58544507982719E-10f, 7.83145814439016E-10f, 9.31322574615479E-10f, 1.10753543209538E-09f, 1.31708901596544E-09f, 1.56629162887804E-09f, 1.86264514923096E-09f, 2.21507086419076E-09f,
            2.63417803193088E-09f, 3.13258325775607E-09f, 3.72529029846191E-09f, 4.43014172838152E-09f, 5.26835606386176E-09f, 6.26516651551212E-09f, 7.45058059692383E-09f, 8.86028345676304E-09f,
            1.05367121277235E-08f, 1.25303330310243E-08f, 1.49011611938477E-08f, 1.77205669135261E-08f, 2.10734242554471E-08f, 2.50606660620485E-08f, 2.98023223876953E-08f, 3.54411338270521E-08f,
            4.21468485108941E-08f, 5.01213321240971E-08f, 5.96046447753906E-08f, 7.08822676541044E-08f, 8.42936970217880E-08f, 1.00242664248194E-07f, 1.19209289550781E-07f, 1.41764535308209E-07f,
            1.68587394043576E-07f, 2.00485328496388E-07f, 2.38418579101562E-07f, 2.83529070616417E-07f, 3.37174788087152E-07f, 4.00970656992777E-07f, 4.76837158203125E-07f, 5.67058141232835E-07f,
            6.74349576174305E-07f, 8.01941313985553E-07f, 9.53674316406250E-07f, 1.13411628246567E-06f, 1.34869915234861E-06f, 1.60388262797110E-06f, 1.90734863281250E-06f, 2.26823256493134E-06f,
            2.69739830469722E-06f, 3.20776525594221E-06f, 3.81469726562500E-06f, 4.53646512986268E-06f, 5.39479660939444E-06f, 6.41553051188442E-06f, 7.62939453125000E-06f, 9.07293025972536E-06f,
            1.07895932187889E-05f, 1.28310610237688E-05f, 1.52587890625000E-05f, 1.81458605194507E-05f, 2.15791864375777E-05f, 2.56621220475377E-05f, 3.05175781250000E-05f, 3.62917210389014E-05f,
            4.31583728751555E-05f, 5.13242440950754E-05f, 6.10351562500000E-05f, 7.25834420778029E-05f, 8.63167457503110E-05f, 1.02648488190151E-04f, 1.22070312500000E-04f, 1.45166884155606E-04f,
            1.72633491500622E-04f, 2.05296976380301E-04f, 2.44140625000000E-04f, 2.90333768311211E-04f, 3.45266983001244E-04f, 4.10593952760603E-04f, 4.88281250000000E-04f, 5.80667536622423E-04f,
            6.90533966002488E-04f, 8.21187905521206E-04f, 9.76562500000000E-04f, 1.16133507324485E-03f, 1.38106793200498E-03f, 1.64237581104241E-03f, 1.95312500000000E-03f, 2.32267014648969E-03f,
            2.76213586400995E-03f, 3.28475162208482E-03f, 3.90625000000000E-03f, 4.64534029297938E-03f, 5.52427172801990E-03f, 6.56950324416964E-03f, 7.81250000000000E-03f, 9.29068058595876E-03f,
            1.10485434560398E-02f, 1.31390064883393E-02f, 1.56250000000000E-02f, 1.85813611719175E-02f, 2.20970869120796E-02f, 2.62780129766786E-02f, 3.12500000000000E-02f, 3.71627223438350E-02f,
            4.41941738241592E-02f, 5.25560259533572E-02f, 6.25000000000000E-02f, 7.43254446876701E-02f, 8.83883476483184E-02f, 1.05112051906714E-01f, 1.25000000000000E-01f, 1.48650889375340E-01f,
            1.76776695296637E-01f, 2.10224103813429E-01f, 2.50000000000000E-01f, 2.97301778750680E-01f, 3.53553390593274E-01f, 4.20448207626857E-01f, 5.00000000000000E-01f, 5.94603557501361E-01f,
            7.07106781186547E-01f, 8.40896415253715E-01f, 1.00000000000000E+00f, 1.18920711500272E+00f, 1.41421356237310E+00f, 1.68179283050743E+00f, 2.00000000000000E+00f, 2.37841423000544E+00f,
            2.82842712474619E+00f, 3.36358566101486E+00f, 4.00000000000000E+00f, 4.75682846001088E+00f, 5.65685424949238E+00f, 6.72717132202972E+00f, 8.00000000000000E+00f, 9.51365692002177E+00f,
            1.13137084989848E+01f, 1.34543426440594E+01f, 1.60000000000000E+01f, 1.90273138400435E+01f, 2.26274169979695E+01f, 2.69086852881189E+01f, 3.20000000000000E+01f, 3.80546276800871E+01f,
            4.52548339959390E+01f, 5.38173705762377E+01f, 6.40000000000000E+01f, 7.61092553601742E+01f, 9.05096679918781E+01f, 1.07634741152475E+02f, 1.28000000000000E+02f, 1.52218510720348E+02f,
            1.81019335983756E+02f, 2.15269482304951E+02f, 2.56000000000000E+02f, 3.04437021440696E+02f, 3.62038671967512E+02f, 4.30538964609902E+02f, 5.12000000000000E+02f, 6.08874042881393E+02f,
            7.24077343935025E+02f, 8.61077929219803E+02f, 1.02400000000000E+03f, 1.21774808576279E+03f, 1.44815468787005E+03f, 1.72215585843961E+03f, 2.04800000000000E+03f, 2.43549617152557E+03f,
        };

        #endregion

        void ReadSideInfo(IMpegFrame frame)
        {
            if (frame.Version == MpegVersion.Version1)
            {
                // main_data_begin      9
                _mainDataBegin = frame.ReadBits(9);

                // private_bits         3 or 5
                if (frame.ChannelMode == MpegChannelMode.Mono)
                {
                    _privBits = frame.ReadBits(5);
                    _channels = 1;
                }
                else
                {
                    _privBits = frame.ReadBits(3);
                    _channels = 2;
                }

                for (var ch = 0; ch < _channels; ch++)
                {
                    // scfsi[ch][0...3]     1 x4
                    _scfsi[ch][0] = frame.ReadBits(1);
                    _scfsi[ch][1] = frame.ReadBits(1);
                    _scfsi[ch][2] = frame.ReadBits(1);
                    _scfsi[ch][3] = frame.ReadBits(1);
                }

                for (var gr = 0; gr < 2; gr++)
                {
                    for (var ch = 0; ch < _channels; ch++)
                    {
                        // part2_3_length[gr][ch]        12
                        _part23Length[gr][ch] = frame.ReadBits(12);
                        // big_values[gr][ch]            9
                        _bigValues[gr][ch] = frame.ReadBits(9);
                        // global_gain[gr][ch]           8
                        _globalGain[gr][ch] = GAIN_TAB[frame.ReadBits(8)];
                        // scalefac_compress[gr][ch]     4
                        _scalefacCompress[gr][ch] = frame.ReadBits(4);
                        // blocksplit_flag[gr][ch]       1
                        _blockSplitFlag[gr][ch] = frame.ReadBits(1) == 1;
                        if (_blockSplitFlag[gr][ch])
                        {
                            //   block_type[gr][ch]              2
                            _blockType[gr][ch] = frame.ReadBits(2);
                            //   switch_point[gr][ch]            1
                            _mixedBlockFlag[gr][ch] = frame.ReadBits(1) == 1;
                            //   table_select[gr][ch][0..1]      5 x2
                            _tableSelect[gr][ch][0] = frame.ReadBits(5);
                            _tableSelect[gr][ch][1] = frame.ReadBits(5);
                            _tableSelect[gr][ch][2] = 0;
                            // set the region information
                            if (_blockType[gr][ch] == 2 && !_mixedBlockFlag[gr][ch])
                            {
                                _regionAddress1[gr][ch] = 8;
                            }
                            else
                            {
                                _regionAddress1[gr][ch] = 7;
                            }
                            _regionAddress2[gr][ch] = 20 - _regionAddress1[gr][ch];
                            //   subblock_gain[gr][ch][0..2]     3 x3
                            _subblockGain[gr][ch][0] = frame.ReadBits(3) * -2f;
                            _subblockGain[gr][ch][1] = frame.ReadBits(3) * -2f;
                            _subblockGain[gr][ch][2] = frame.ReadBits(3) * -2f;
                        }
                        else
                        {
                            //   table_select[0..2][gr][ch]      5 x3
                            _tableSelect[gr][ch][0] = frame.ReadBits(5);
                            _tableSelect[gr][ch][1] = frame.ReadBits(5);
                            _tableSelect[gr][ch][2] = frame.ReadBits(5);
                            //   region_address1[gr][ch]         4
                            _regionAddress1[gr][ch] = frame.ReadBits(4);
                            //   region_address2[gr][ch]         3
                            _regionAddress2[gr][ch] = frame.ReadBits(3);
                            // set the block type so it doesn't accidentally carry
                            _blockType[gr][ch] = 0;

                            // make subblock gain equal unity
                            _subblockGain[gr][ch][0] = 0;
                            _subblockGain[gr][ch][1] = 0;
                            _subblockGain[gr][ch][2] = 0;
                        }
                        // preflag[gr][ch]               1
                        _preflag[gr][ch] = frame.ReadBits(1);
                        // scalefac_scale[gr][ch]        1
                        _scalefacScale[gr][ch] = .5f * (1f + frame.ReadBits(1));
                        // count1table_select[gr][ch]    1
                        _count1TableSelect[gr][ch] = frame.ReadBits(1);
                    }
                }
            }
            else    // MPEG 2+
            {
                // main_data_begin      8
                _mainDataBegin = frame.ReadBits(8);

                // private_bits         1 or 2
                if (frame.ChannelMode == MpegChannelMode.Mono)
                {
                    _privBits = frame.ReadBits(1);
                    _channels = 1;
                }
                else
                {
                    _privBits = frame.ReadBits(2);
                    _channels = 2;
                }

                var gr = 0;
                for (var ch = 0; ch < _channels; ch++)
                {
                    // part2_3_length[gr][ch]        12
                    _part23Length[gr][ch] = frame.ReadBits(12);
                    // big_values[gr][ch]            9
                    _bigValues[gr][ch] = frame.ReadBits(9);
                    // global_gain[gr][ch]           8
                    _globalGain[gr][ch] = GAIN_TAB[frame.ReadBits(8)];
                    // scalefac_compress[gr][ch]     9
                    _scalefacCompress[gr][ch] = frame.ReadBits(9);
                    // blocksplit_flag[gr][ch]       1
                    _blockSplitFlag[gr][ch] = frame.ReadBits(1) == 1;
                    if (_blockSplitFlag[gr][ch])
                    {
                        //   block_type[gr][ch]              2
                        _blockType[gr][ch] = frame.ReadBits(2);
                        //   switch_point[gr][ch]            1
                        _mixedBlockFlag[gr][ch] = frame.ReadBits(1) == 1;
                        //   table_select[gr][ch][0..1]      5 x2
                        _tableSelect[gr][ch][0] = frame.ReadBits(5);
                        _tableSelect[gr][ch][1] = frame.ReadBits(5);
                        _tableSelect[gr][ch][2] = 0;
                        // set the region information
                        if (_blockType[gr][ch] == 2 && !_mixedBlockFlag[gr][ch])
                        {
                            _regionAddress1[gr][ch] = 8;
                        }
                        else
                        {
                            _regionAddress1[gr][ch] = 7;
                        }
                        _regionAddress2[gr][ch] = 20 - _regionAddress1[gr][ch];
                        //   subblock_gain[gr][ch][0..2]     3 x3
                        _subblockGain[gr][ch][0] = frame.ReadBits(3) * -2f;
                        _subblockGain[gr][ch][1] = frame.ReadBits(3) * -2f;
                        _subblockGain[gr][ch][2] = frame.ReadBits(3) * -2f;
                    }
                    else
                    {
                        //   table_select[0..2][gr][ch]      5 x3
                        _tableSelect[gr][ch][0] = frame.ReadBits(5);
                        _tableSelect[gr][ch][1] = frame.ReadBits(5);
                        _tableSelect[gr][ch][2] = frame.ReadBits(5);
                        //   region_address1[gr][ch]         4
                        _regionAddress1[gr][ch] = frame.ReadBits(4);
                        //   region_address2[gr][ch]         3
                        _regionAddress2[gr][ch] = frame.ReadBits(3);
                        // set the block type so it doesn't accidentally carry
                        _blockType[gr][ch] = 0;

                        // make subblock gain equal unity
                        _subblockGain[gr][ch][0] = 0;
                        _subblockGain[gr][ch][1] = 0;
                        _subblockGain[gr][ch][2] = 0;
                    }
                    // scalefac_scale[gr][ch]        1
                    _scalefacScale[gr][ch] = .5f * (1f + frame.ReadBits(1));
                    // count1table_select[gr][ch]    1
                    _count1TableSelect[gr][ch] = frame.ReadBits(1);
                }
            }
        }

        #endregion

        #region Precalc Table Prep

        #region Variables

        int[] _sfBandIndexL, _sfBandIndexS;

        // these are byte[] to save memory
        byte[] _cbLookupL = new byte[SSLIMIT * SBLIMIT], _cbLookupS = new byte[SSLIMIT * SBLIMIT], _cbwLookupS = new byte[SSLIMIT * SBLIMIT];
        int _cbLookupSR;

        static readonly int[][] _sfBandIndexLTable = {
                                                         // MPEG 1
                                                         // 44.1 kHz
                                                         new int[] { 0, 4, 8, 12, 16, 20, 24, 30, 36, 44, 52, 62, 74, 90, 110, 134, 162, 196, 238, 288, 342, 418, 576 },
                                                         // 48 kHz
                                                         new int[] { 0, 4, 8, 12, 16, 20, 24, 30, 36, 42, 50, 60, 72, 88, 106, 128, 156, 190, 230, 276, 330, 384, 576 },
                                                         // 32 kHz
                                                         new int[] { 0, 4, 8, 12, 16, 20, 24, 30, 36, 44, 54, 66, 82, 102, 126, 156, 194, 240, 296, 364, 448, 550, 576 },

                                                         // MPEG 2
                                                         // 22.05 kHz
                                                         new int[] { 0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576 },
                                                         // 24 kHz
                                                         new int[] { 0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 114, 136, 162, 194, 232, 278, 330, 394, 464, 540, 576 },
                                                         // 16 kHz
                                                         new int[] { 0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576 },

                                                         // MPEG 2.5
                                                         // 11.025 kHz
                                                         new int[] { 0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576 },
                                                         // 12 kHz
                                                         new int[] { 0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576 },
                                                         // 8 kHz
                                                         new int[] { 0, 12, 24, 36, 48, 60, 72, 88, 108, 132, 160, 192, 232, 280, 336, 400, 476, 566, 568, 570, 572, 574, 576 },
                                                     };

        static readonly int[][] _sfBandIndexSTable = {
                                                         // MPEG 1
                                                         // 44.1 kHz
                                                         new int[] { 0, 4, 8, 12, 16, 22, 30, 40, 52, 66, 84, 106, 136, 192 },
                                                         // 48 kHz
                                                         new int[] { 0, 4, 8, 12, 16, 22, 28, 38, 50, 64, 80, 100, 126, 192 },
                                                         // 32 kHz
                                                         new int[] { 0, 4, 8, 12, 16, 22, 30, 42, 58, 78, 104, 138, 180, 192 },

                                                         // MPEG 2
                                                         // 22.05 kHz
                                                         new int[] { 0, 4, 8, 12, 18, 24, 32, 42, 56, 74, 100, 132, 174, 192 },
                                                         // 24 kHz
                                                         new int[] { 0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 136, 180, 192 },
                                                         // 16 kHz
                                                         new int[] { 0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 134, 174, 192 },

                                                         // MPEG 2.5
                                                         // 11.025 kHz
                                                         new int[] { 0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 134, 174, 192 },
                                                         // 12 kHz
                                                         new int[] { 0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 134, 174, 192 },
                                                         // 8 kHz
                                                         new int[] { 0, 8, 16, 24, 36, 52, 72, 96, 124, 160, 162, 164, 166, 192 },
                                                     };

        #endregion

        void PrepTables(IMpegFrame frame)
        {
            if (_cbLookupSR != frame.SampleRate)
            {
                switch (frame.SampleRate)
                {
                    case 44100:
                        _sfBandIndexL = _sfBandIndexLTable[0];
                        _sfBandIndexS = _sfBandIndexSTable[0];
                        break;
                    case 48000:
                        _sfBandIndexL = _sfBandIndexLTable[1];
                        _sfBandIndexS = _sfBandIndexSTable[1];
                        break;
                    case 32000:
                        _sfBandIndexL = _sfBandIndexLTable[2];
                        _sfBandIndexS = _sfBandIndexSTable[2];
                        break;

                    case 22050:
                        _sfBandIndexL = _sfBandIndexLTable[3];
                        _sfBandIndexS = _sfBandIndexSTable[3];
                        break;
                    case 24000:
                        _sfBandIndexL = _sfBandIndexLTable[4];
                        _sfBandIndexS = _sfBandIndexSTable[4];
                        break;
                    case 16000:
                        _sfBandIndexL = _sfBandIndexLTable[5];
                        _sfBandIndexS = _sfBandIndexSTable[5];
                        break;

                    case 11025:
                        _sfBandIndexL = _sfBandIndexLTable[6];
                        _sfBandIndexS = _sfBandIndexSTable[6];
                        break;
                    case 12000:
                        _sfBandIndexL = _sfBandIndexLTable[7];
                        _sfBandIndexS = _sfBandIndexSTable[7];
                        break;
                    case 8000:
                        _sfBandIndexL = _sfBandIndexLTable[8];
                        _sfBandIndexS = _sfBandIndexSTable[8];
                        break;
                }

                // precalculate the critical bands per bucket
                int cbL = 0, cbS = 0;
                int next_cbL = _sfBandIndexL[1], next_cbS = _sfBandIndexS[1] * 3;
                for (int i = 0; i < 576; i++)
                {
                    if (i == next_cbL)
                    {
                        ++cbL;
                        next_cbL = _sfBandIndexL[cbL + 1];
                    }
                    if (i == next_cbS)
                    {
                        ++cbS;
                        next_cbS = _sfBandIndexS[cbS + 1] * 3;
                    }
                    _cbLookupL[i] = (byte)cbL;
                    _cbLookupS[i] = (byte)cbS;
                }

                // set up the short block windows
                int idx = 0;
                for (cbS = 0; cbS < 12; cbS++)
                {
                    var width = _sfBandIndexS[cbS + 1] - _sfBandIndexS[cbS];
                    for (int i = 0; i < 3; i++)
                    {
                        for (int j = 0; j < width; j++, idx++)
                        {
                            _cbwLookupS[idx] = (byte)i;
                        }
                    }
                }

                _cbLookupSR = frame.SampleRate;
            }
        }

        #endregion

        #region Scale Factors

        #region Variables

        int[][][] _scalefac = {   // ch, window, cb
                                  new int[][] { new int[13], new int[13], new int[13], new int[23] },
                                  new int[][] { new int[13], new int[13], new int[13], new int[23] }
                              };

        static readonly int[][] _slen = {
                                            new int[] { 0, 0, 0, 0, 3, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4 },
                                            new int[] { 0, 1, 2, 3, 0, 1, 2, 3, 1, 2, 3, 1, 2, 3, 2, 3 }
                                        };

        // Reused scratch for ReadLsfScalefactors so we don't allocate an int[4] and an
        // int[54] on every MPEG-2/2.5 granule. Both are fully cleared at the top of each
        // call, matching the zero-initialised fresh arrays they replaced.
        readonly int[] _lsfSlen = new int[4];
        readonly int[] _lsfScalefacBuf = new int[54];

        static readonly int[][][] _sfbBlockCntTab = {
                                                        new int[][] { new int[] { 6, 5, 5, 5 },   new int[] { 9, 9, 9, 9 },    new int[] { 6, 9, 9, 9 }   },
                                                        new int[][] { new int[] { 6, 5, 7, 3 },   new int[] { 9, 9, 12, 6 },   new int[] { 6, 9, 12, 6 }  },
                                                        new int[][] { new int[] { 11, 10, 0, 0 }, new int[] { 18, 18, 0, 0 },  new int[] { 15, 18, 0, 0 } },
                                                        new int[][] { new int[] { 7, 7, 7, 0 },   new int[] { 12, 12, 12, 0 }, new int[] { 6, 15, 12, 0 } },
                                                        new int[][] { new int[] { 6, 6, 6, 3 },   new int[] { 12, 9, 9, 6 },   new int[] { 6, 12, 9, 6 }  },
                                                        new int[][] { new int[] { 8, 8, 5, 0 },   new int[] { 15, 12, 9, 0 },  new int[] { 6, 18, 9, 0 }  },
                                                    };

        #endregion

        int ReadScalefactors(int gr, int ch)
        {
            var slen0 = _slen[0][_scalefacCompress[gr][ch]];
            var slen1 = _slen[1][_scalefacCompress[gr][ch]];
            int bits;

            int cb = 0;
            if (_blockSplitFlag[gr][ch] && _blockType[gr][ch] == 2)
            {
                if (slen0 > 0)
                {
                    bits = slen0 * 18;

                    if (_mixedBlockFlag[gr][ch])
                    {
                        // mixed has bands 0..7 of long, then 3..11 of short
                        for (; cb < 8; cb++)
                        {
                            _scalefac[ch][3][cb] = _bitRes.GetBits(slen0);
                        }
                        cb = 3;
                        bits -= slen0;  // mixed blocks need slen0 fewer bits
                    }

                    // short / mixed: just read from wherever cb happens to be through 11
                    for (; cb < 6; cb++)
                    {
                        _scalefac[ch][0][cb] = _bitRes.GetBits(slen0);
                        _scalefac[ch][1][cb] = _bitRes.GetBits(slen0);
                        _scalefac[ch][2][cb] = _bitRes.GetBits(slen0);
                    }
                }
                else
                {
                    Array.Clear(_scalefac[ch][3], 0, 8);
                    Array.Clear(_scalefac[ch][0], 0, 6);
                    Array.Clear(_scalefac[ch][1], 0, 6);
                    Array.Clear(_scalefac[ch][2], 0, 6);
                    bits = 0;
                }

                if (slen1 > 0)
                {
                    bits += slen1 * 18;

                    for (cb = 6; cb < 12; cb++)
                    {
                        _scalefac[ch][0][cb] = _bitRes.GetBits(slen1);
                        _scalefac[ch][1][cb] = _bitRes.GetBits(slen1);
                        _scalefac[ch][2][cb] = _bitRes.GetBits(slen1);
                    }
                }
                else
                {
                    Array.Clear(_scalefac[ch][0], 6, 6);
                    Array.Clear(_scalefac[ch][1], 6, 6);
                    Array.Clear(_scalefac[ch][2], 6, 6);
                }
            }
            else
            {
                // long: read if gr == 0, otherwise honor scfsi for the channel
                bits = 0;
                if (gr == 0 || _scfsi[ch][0] == 0)
                {
                    if (slen0 > 0)
                    {
                        bits += slen0 * 6;
                        _scalefac[ch][3][0] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][1] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][2] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][3] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][4] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][5] = _bitRes.GetBits(slen0);
                    }
                    else
                    {
                        Array.Clear(_scalefac[ch][3], 0, 6);
                    }
                }
                if (gr == 0 || _scfsi[ch][1] == 0)
                {
                    if (slen0 > 0)
                    {
                        bits += slen0 * 5;
                        _scalefac[ch][3][6] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][7] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][8] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][9] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][10] = _bitRes.GetBits(slen0);
                    }
                    else
                    {
                        Array.Clear(_scalefac[ch][3], 6, 5);
                    }
                }
                if (gr == 0 || _scfsi[ch][2] == 0)
                {
                    if (slen1 > 0)
                    {
                        bits += slen1 * 5;
                        _scalefac[ch][3][11] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][12] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][13] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][14] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][15] = _bitRes.GetBits(slen1);
                    }
                    else
                    {
                        Array.Clear(_scalefac[ch][3], 11, 5);
                    }
                }
                if (gr == 0 || _scfsi[ch][3] == 0)
                {
                    if (slen1 > 0)
                    {
                        bits += slen1 * 5;
                        _scalefac[ch][3][16] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][17] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][18] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][19] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][20] = _bitRes.GetBits(slen1);
                    }
                    else
                    {
                        Array.Clear(_scalefac[ch][3], 16, 5);
                    }
                }
            }

            return bits;
        }

        int ReadLsfScalefactors(int gr, int ch, int chanModeExt)
        {
            var sfc = _scalefacCompress[gr][ch];

            int blockTypeNumber;
            // block type number = 2 if mixed short, 1 if pure short, otherwise 0
            if (_blockType[gr][ch] == 2)
            {
                if (_mixedBlockFlag[gr][ch])
                {
                    blockTypeNumber = 2;
                }
                else
                {
                    blockTypeNumber = 1;
                }
            }
            else
            {
                blockTypeNumber = 0;
            }

            int[] slen = _lsfSlen;
            Array.Clear(slen, 0, 4);
            int blockNumber;
            if ((chanModeExt & 1) == 1 && ch == 1)
            {
                var tsfc = sfc >> 1;
                if (tsfc < 180)
                {
                    slen[0] = tsfc / 36;                    // <= 4, 15
                    slen[1] = (tsfc % 36) / 6;              // <= 5, 31
                    slen[2] = tsfc % 6;                     // <= 5, 31
                    slen[3] = 0;
                    _preflag[gr][ch] = 0;
                    blockNumber = 3;
                }
                else if (tsfc < 244)
                {
                    slen[0] = ((tsfc - 180) % 64) >> 4;     // <= 3, 7
                    slen[1] = ((tsfc - 180) % 16) >> 2;     // <= 3, 7
                    slen[2] = ((tsfc - 180) % 4);           // <= 3, 7
                    slen[3] = 0;
                    _preflag[gr][ch] = 0;
                    blockNumber = 4;
                }
                else if (tsfc < 255)
                {
                    slen[0] = (tsfc - 244) / 3;             // <= 3, 7
                    slen[1] = (tsfc - 244) % 3;             // <= 1, 1
                    slen[2] = 0;
                    slen[3] = 0;
                    _preflag[gr][ch] = 0;
                    blockNumber = 5;
                }
                else
                {
                    blockNumber = 0;
                }
            }
            else
            {
                //   if scalefac_comp < 400
                if (sfc < 400)
                {
                    slen[0] = (sfc >> 4) / 5;               // <= 4, 15
                    slen[1] = (sfc >> 4) % 5;               // <= 4, 15
                    slen[2] = (sfc & 15) >> 2;              // <= 3, 7
                    slen[3] = sfc & 3;                      // <= 3, 7
                    _preflag[gr][ch] = 0;
                    blockNumber = 0;
                }
                else if (sfc < 500)
                {
                    slen[0] = ((sfc - 400) >> 2) / 5;       // <= 4, 15
                    slen[1] = ((sfc - 400) >> 2) % 5;       // <= 4, 15
                    slen[2] = (sfc - 400) & 3;              // <= 3, 7
                    slen[3] = 0;
                    _preflag[gr][ch] = 0;
                    blockNumber = 1;
                }
                else if (sfc < 512)
                {
                    slen[0] = (sfc - 500) / 3;              // <= 3, 7
                    slen[1] = (sfc - 500) % 3;              // <= 2, 3
                    slen[2] = 0;
                    slen[3] = 0;
                    _preflag[gr][ch] = 1;
                    blockNumber = 2;
                }
                else
                {
                    blockNumber = 0;
                }
            }

            // now we populate our buffer...
            var buffer = _lsfScalefacBuf;
            Array.Clear(buffer, 0, 54);

            var k = 0;
            var blkCnt = _sfbBlockCntTab[blockNumber][blockTypeNumber];
            for (int i = 0; i < 4; i++)
            {
                if (slen[i] != 0)
                {
                    for (int j = 0; j < blkCnt[i]; j++, k++)
                    {
                        buffer[k] = _bitRes.GetBits(slen[i]);
                    }
                }
                else
                {
                    k += blkCnt[i];
                }
            }

            // now that we have that done, let's assign our scalefactors
            k = 0;
            int sfb = 0;
            if (_blockSplitFlag[gr][ch] && _blockType[gr][ch] == 2)
            {
                if (_mixedBlockFlag[gr][ch])
                {
                    for (; sfb < 8; sfb++)
                    {
                        _scalefac[ch][3][sfb] = buffer[k++];
                    }
                    sfb = 3;
                }

                for (; sfb < 12; sfb++)
                {
                    for (int window = 0; window < 3; window++)
                    {
                        _scalefac[ch][window][sfb] = buffer[k++];
                    }
                }
                _scalefac[ch][0][12] = 0;
                _scalefac[ch][1][12] = 0;
                _scalefac[ch][2][12] = 0;
            }
            else
            {
                for (; sfb < 21; sfb++)
                {
                    _scalefac[ch][3][sfb] = buffer[k++];
                }
                _scalefac[ch][3][22] = 0;
            }

            return slen[0] * blkCnt[0] + slen[1] * blkCnt[1] + slen[2] * blkCnt[2] + slen[3] * blkCnt[3];
        }

        #endregion

        #region Huffman & Dequantize

        #region Variables

        float[][] _samples = { new float[SSLIMIT * SBLIMIT + 3], new float[SSLIMIT * SBLIMIT + 3] };

        static readonly int[] PRETAB = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 3, 2, 0 };

        static readonly float[] POW2_TAB =
        {
            1.000000000000000E-00f, 7.071067811865470E-01f, 5.000000000000000E-01f, 3.535533905932740E-01f, 2.500000000000000E-01f, 1.767766952966370E-01f, 1.250000000000000E-01f, 8.838834764831840E-02f,
            6.250000000000000E-02f, 4.419417382415920E-02f, 3.125000000000000E-02f, 2.209708691207960E-02f, 1.562500000000000E-02f, 1.104854345603980E-02f, 7.812500000000000E-03f, 5.524271728019900E-03f,
            3.906250000000000E-03f, 2.762135864009950E-03f, 1.953125000000000E-03f, 1.381067932004980E-03f, 9.765625000000000E-04f, 6.905339660024880E-04f, 4.882812500000000E-04f, 3.452669830012440E-04f,
            2.441406250000000E-04f, 1.726334915006220E-04f, 1.220703125000000E-04f, 8.631674575031100E-05f, 6.103515625000000E-05f, 4.315837287515550E-05f, 3.051757812500000E-05f, 2.157918643757770E-05f,
            1.525878906250000E-05f, 1.078959321878890E-05f, 7.629394531250000E-06f, 5.394796609394440E-06f, 3.814697265625000E-06f, 2.697398304697220E-06f, 1.907348632812500E-06f, 1.348699152348610E-06f,
            9.536743164062500E-07f, 6.743495761743050E-07f, 4.768371582031250E-07f, 3.371747880871520E-07f, 2.384185791015620E-07f, 1.685873940435760E-07f, 1.192092895507810E-07f, 8.429369702178800E-08f,
            5.960464477539060E-08f, 4.214684851089410E-08f, 2.980232238769530E-08f, 2.107342425544710E-08f, 1.490116119384770E-08f, 1.053671212772350E-08f, 7.450580596923830E-09f, 5.268356063861760E-09f,
            3.725290298461910E-09f, 2.634178031930880E-09f, 1.862645149230960E-09f, 1.317089015965440E-09f, 9.313225746154790E-10f, 6.585445079827190E-10f, 4.656612873077390E-10f, 3.292722539913600E-10f,
        };

        #endregion

        void ReadSamples(int sfBits, int gr, int ch)
        {
            int region1Start, region2Start;
            if (_blockSplitFlag[gr][ch] && _blockType[gr][ch] == 2)
            {
                region1Start = 36;
                region2Start = 576;
            }
            else
            {
                region1Start = _sfBandIndexL[_regionAddress1[gr][ch] + 1];
                region2Start = _sfBandIndexL[Math.Min(_regionAddress1[gr][ch] + _regionAddress2[gr][ch] + 2, 22)];
            }

            var part3end = _bitRes.BitsRead - sfBits + _part23Length[gr][ch];

            int idx = 0, h = _tableSelect[gr][ch][0];

            // bigvalues section
            int bigValueCount = _bigValues[gr][ch] * 2;
            float x, y;
            while (idx < bigValueCount && idx < region1Start)
            {
                Huffman.Decode(_bitRes, h, out x, out y);
                _samples[ch][idx] = Dequantize(idx, x, gr, ch); ++idx;
                _samples[ch][idx] = Dequantize(idx, y, gr, ch); ++idx;
            }
            h = _tableSelect[gr][ch][1];
            while (idx < bigValueCount && idx < region2Start)
            {
                Huffman.Decode(_bitRes, h, out x, out y);
                _samples[ch][idx] = Dequantize(idx, x, gr, ch); ++idx;
                _samples[ch][idx] = Dequantize(idx, y, gr, ch); ++idx;
            }
            h = _tableSelect[gr][ch][2];
            while (idx < bigValueCount)
            {
                Huffman.Decode(_bitRes, h, out x, out y);
                _samples[ch][idx] = Dequantize(idx, x, gr, ch); ++idx;
                _samples[ch][idx] = Dequantize(idx, y, gr, ch); ++idx;
            }

            // count1 section
            h = _count1TableSelect[gr][ch] + 32;

            float v, w;
            // - 3 to ensure that we never get an out of range exception
            while (part3end > _bitRes.BitsRead && idx < SBLIMIT * SSLIMIT - 3)
            {
                Huffman.Decode(_bitRes, h, out x, out y, out v, out w);
                _samples[ch][idx] = Dequantize(idx, v, gr, ch); ++idx;
                _samples[ch][idx] = Dequantize(idx, w, gr, ch); ++idx;
                _samples[ch][idx] = Dequantize(idx, x, gr, ch); ++idx;
                _samples[ch][idx] = Dequantize(idx, y, gr, ch); ++idx;
            }

            // adjust the bit stream if we're off somehow
            if (_bitRes.BitsRead > part3end)
            {
                _bitRes.RewindBits((int)(_bitRes.BitsRead - part3end));

                idx -= 4;
                if (idx < 0) idx = 0;
            }

            if (_bitRes.BitsRead < part3end)
            {
                _bitRes.SkipBits((int)(part3end - _bitRes.BitsRead));
            }

            // zero out the highest samples (defined as 0 in the standard)
            if (idx < SBLIMIT * SSLIMIT)
            {
                Array.Clear(_samples[ch], idx, SBLIMIT * SSLIMIT + 3 - idx);
            }
        }

        float Dequantize(int idx, float val, int gr, int ch)
        {
            if (val != 0f)
            {
                int cb, window;

                if (_blockSplitFlag[gr][ch] && _blockType[gr][ch] == 2 && !(_mixedBlockFlag[gr][ch] && idx < _sfBandIndexL[8]))
                {
                    // short / mixed short section
                    cb = _cbLookupS[idx];
                    window = _cbwLookupS[idx];

                    return val * _globalGain[gr][ch] * POW2_TAB[(int)(-2 * (_subblockGain[gr][ch][window] - (_scalefacScale[gr][ch] * _scalefac[ch][window][cb])))];
                }
                else
                {
                    // long / mixed long section
                    cb = _cbLookupL[idx];

                    return val * _globalGain[gr][ch] * POW2_TAB[(int)(2 * _scalefacScale[gr][ch] * (_scalefac[ch][3][cb] + _preflag[gr][ch] * PRETAB[cb]))];
                }

            }
            return 0f;
        }

        #endregion

        #region Stereo

        #region Variables

        static readonly float[][] _isRatio = {
                                                 new float[] { 0f, 0.211324865405187f, 0.366025403784439f, 0.5f, 0.633974596215561f, 0.788675134594813f, 1f },
                                                 new float[] { 1f, 0.788675134594813f, 0.633974596215561f, 0.5f, 0.366025403784439f, 0.211324865405187f, 0f }
                                             };

        static readonly float[][][] _lsfRatio = {   // sfc%2, ch, isPos
                                                    new float[][]
                                                    {
                                                        new float[] { 1f, 0.840896415256f, 1f, 0.707106781190391f, 1f, 0.594603557506209f, 1f, 0.500000000005436f, 1f, 0.420448207632571f, 1f, 0.353553390599039f, 1f, 0.297301778756337f, 1f, 0.250000000005436f, 1f, 0.210224103818571f, 1f, 0.176776695301441f, 1f, 0.148650889379784f, 1f, 0.125000000004077f, 1f, 0.105112051910428f, 1f, 0.0883883476516816f, 1f, 0.0743254446907002f, 1f, 0.0625000000027179f },
                                                        new float[] { 1f, 1f, 0.840896415256f, 1f, 0.707106781190391f, 1f, 0.594603557506209f, 1f, 0.500000000005436f, 1f, 0.420448207632571f, 1f, 0.353553390599039f, 1f, 0.297301778756337f, 1f, 0.250000000005436f, 1f, 0.210224103818571f, 1f, 0.176776695301441f, 1f, 0.148650889379784f, 1f, 0.125000000004077f, 1f, 0.105112051910428f, 1f, 0.0883883476516816f, 1f, 0.0743254446907002f, 1f },
                                                    },
                                                    new float[][]
                                                    {
                                                        new float[] { 1f, 0.707106781188f, 1f, 0.500000000002054f, 1f, 0.353553390595452f, 1f, 0.250000000002054f, 1f, 0.176776695298452f, 1f, 0.125000000001541f, 1f, 0.0883883476495893f, 1f, 0.062500000001027f, 1f, 0.0441941738249762f, 1f, 0.0312500000006419f, 1f, 0.0220970869125789f, 1f, 0.0156250000003851f, 1f, 0.0110485434563348f, 1f, 0.00781250000022466f, 1f, 0.00552427172819011f, 1f, 0.00390625000012838f },
                                                        new float[] { 1f, 1f, 0.707106781188f, 1f, 0.500000000002054f, 1f, 0.353553390595452f, 1f, 0.250000000002054f, 1f, 0.176776695298452f, 1f, 0.125000000001541f, 1f, 0.0883883476495893f, 1f, 0.062500000001027f, 1f, 0.0441941738249762f, 1f, 0.0312500000006419f, 1f, 0.0220970869125789f, 1f, 0.0156250000003851f, 1f, 0.0110485434563348f, 1f, 0.00781250000022466f, 1f, 0.00552427172819011f, 1f },
                                                    },
                                                };

        #endregion

        void Stereo(MpegChannelMode channelMode, int chanModeExt, int gr, bool lsf)
        {
            // do the stereo decoding as needed...  This really only applies in two cases:
            //  1) Joint Stereo and one (or both) of the extensions are enabled, or
            //  2) We're doing a downmix to mono

            if (channelMode == MpegChannelMode.JointStereo && chanModeExt != 0)
            {
                var midSide = (chanModeExt & 0x2) == 2;

                if ((chanModeExt & 0x1) == 1)
                {
                    // do the intensity stereo processing

                    #region Common Processing

                    // find the highest sample index with a value in channel 1
                    //   - now each step only has to start from there
                    int lastValueIdx = -1;
                    for (int i = SBLIMIT * SSLIMIT - (SBLIMIT + 1); i >= 0; i--)
                    {
                        if (_samples[1][i] != 0f)
                        {
                            lastValueIdx = i;
                            break;
                        }
                    }

                    // figure up which passes we'll need and for which ranges
                    int lEnd = -1, sStart = -1;
                    if (_blockSplitFlag[gr][0] && _blockType[gr][0] == 2)
                    {
                        if (_mixedBlockFlag[gr][0])
                        {
                            // 0 through 8 of long, then 3 through 12 of short
                            if (lastValueIdx < _sfBandIndexL[8])
                            {
                                lEnd = 8;
                            }
                            sStart = 3;
                        }
                        else
                        {
                            // 0 through 12 of short
                            sStart = 0;
                        }
                    }
                    else
                    {
                        // 0 through 21 of long
                        lEnd = 21;
                    }

                    #endregion

                    #region Long Processing

                    // long processing is far simpler than short...  just process from the start of the scalefactor band after the last non-zero sample
                    // we also don't have to mess with "finding" again; it was done above
                    var sfb = 0;
                    if (lastValueIdx > -1)
                    {
                        sfb = _cbLookupL[lastValueIdx] + 1;
                    }

                    // make sure we do the mid/side processing on the lower bands (if needed)
                    if (sfb > 0 && sStart == -1)
                    {
                        if (midSide)
                        {
                            ApplyMidSide(0, _sfBandIndexL[sfb]);
                        }
                        else
                        {
                            ApplyFullStereo(0, _sfBandIndexL[sfb]);
                        }
                    }

                    // now process the intensity bands
                    for (; sfb < lEnd; sfb++)
                    {
                        var i = _sfBandIndexL[sfb];
                        var width = _sfBandIndexL[sfb + 1] - _sfBandIndexL[sfb];
                        var isPos = _scalefac[1][3][sfb];
                        if (isPos >= 7)
                        {
                            if (midSide)
                            {
                                ApplyMidSide(i, width);
                            }
                            else
                            {
                                ApplyFullStereo(i, width);
                            }
                        }
                        else if (lsf)
                        {
                            ApplyLsfIStereo(i, width, isPos, _scalefacCompress[gr][0]);
                        }
                        else
                        {
                            ApplyIStereo(i, width, isPos);
                        }
                    }

                    if (sStart <= -1)
                    {
                        // do final long processing
                        var isPos = _scalefac[1][3][20];
                        if (isPos >= 7)
                        {
                            if (midSide)
                            {
                                ApplyMidSide(_sfBandIndexL[21], 576 - _sfBandIndexL[21]);
                            }
                            else
                            {
                                ApplyFullStereo(_sfBandIndexL[21], 576 - _sfBandIndexL[21]);
                            }
                        }
                        else if (lsf)
                        {
                            ApplyLsfIStereo(_sfBandIndexL[21], 576 - _sfBandIndexL[21], isPos, _scalefacCompress[gr][0]);
                        }
                        else
                        {
                            ApplyIStereo(_sfBandIndexL[21], 576 - _sfBandIndexL[21], isPos);
                        }
                    }

                    #endregion

                    #region Short Processing

                    // short processing requires that each window be looked at separately... they are interleaved, so it gets really interesting...
                    // on the plus side, whichever window the {lastValueIdx} is in is already found... :)
                    else
                    {
                        // find where each window starts intensity processing
                        var sSfb = new int[] { -1, -1, -1 };
                        int window;
                        if (lastValueIdx > -1)
                        {
                            sfb = _cbLookupS[lastValueIdx];
                            window = _cbwLookupS[lastValueIdx];
                            sSfb[window] = sfb;
                        }
                        else
                        {
                            sfb = 12;
                            window = 3; // NB: 3 is correct!
                        }

                        window = (window - 1) % 3;
                        for (; sfb >= sStart && window >= 0; window = (window - 1) % 3)
                        {
                            if (sSfb[window] != -1)
                            {
                                if (sSfb[0] != -1 && sSfb[1] != -1 && sSfb[2] != -1)
                                {
                                    break;
                                }
                                continue;
                            }

                            var width = _sfBandIndexS[sfb + 1] - _sfBandIndexS[sfb];
                            var i = _sfBandIndexS[sfb] * 3 + width * (window + 1);

                            while (--width >= -1)
                            {
                                if (_samples[1][--i] != 0f)
                                {
                                    sSfb[window] = sfb;
                                    break;
                                }
                            }

                            if (window == 0)
                            {
                                --sfb;
                            }
                        }

                        // now apply the intensity processing for each window & scalefactor band
                        sfb = sStart;
                        for (; sfb < 12; sfb++)
                        {
                            var width = _sfBandIndexS[sfb + 1] - _sfBandIndexS[sfb];
                            var i = _sfBandIndexS[sfb] * 3;

                            for (window = 0; window < 3; window++)
                            {
                                if (sfb > sSfb[window])
                                {
                                    var isPos = _scalefac[1][window][sfb];
                                    if (isPos >= 7)
                                    {
                                        if (midSide)
                                        {
                                            ApplyMidSide(i, width);
                                        }
                                        else
                                        {
                                            ApplyFullStereo(i, width);
                                        }
                                    }
                                    else if (lsf)
                                    {
                                        ApplyLsfIStereo(i, width, isPos, _scalefacCompress[gr][0]);
                                    }
                                    else
                                    {
                                        ApplyIStereo(i, width, isPos);
                                    }
                                }
                                else if (midSide)
                                {
                                    ApplyMidSide(i, width);
                                }
                                else
                                {
                                    ApplyFullStereo(i, width);
                                }

                                i += width;
                            }
                        }

                        // do final short processing
                        var finalWidth = _sfBandIndexS[13] - _sfBandIndexS[12];
                        for (window = 0; window < 3; window++)
                        {
                            var isPos = _scalefac[1][window][11];
                            if (isPos >= 7)
                            {
                                if (midSide)
                                {
                                    ApplyMidSide(_sfBandIndexS[11] * 3 + finalWidth * window, finalWidth);
                                }
                                else
                                {
                                    ApplyFullStereo(_sfBandIndexS[11] * 3 + finalWidth * window, finalWidth);
                                }
                            }
                            else if (lsf)
                            {
                                ApplyLsfIStereo(_sfBandIndexS[11] * 3 + finalWidth * window, finalWidth, isPos, _scalefacCompress[gr][0]);
                            }
                            else
                            {
                                ApplyIStereo(_sfBandIndexS[11] * 3 + finalWidth * window, finalWidth, isPos);
                            }
                        }
                    }

                    #endregion
                }
                else if (midSide)
                {
                    // just do mid/side processing for everything
                    ApplyMidSide(0, SBLIMIT * SSLIMIT);
                }
                else
                {
                    // this is a no-op most of the time
                    ApplyFullStereo(0, SSLIMIT * SBLIMIT);
                }
            }
            else if (_channels != 1)
            {
                // this is a no-op most of the time
                ApplyFullStereo(0, SSLIMIT * SBLIMIT);
            }
        }

        void ApplyIStereo(int i, int sb, int isPos)
        {
            if (StereoMode == StereoMode.DownmixToMono)
            {
                for (; sb > 0; sb--, i++)
                {
                    _samples[0][i] /= 2f; // scale appropriately
                }
            }
            else
            {
                var ratio0 = _isRatio[0][isPos];
                var ratio1 = _isRatio[1][isPos];
                for (; sb > 0; sb--, i++)
                {
                    _samples[1][i] = _samples[0][i] * ratio1;
                    _samples[0][i] *= ratio0;
                }
            }
        }

        void ApplyLsfIStereo(int i, int sb, int isPos, int scalefacCompress)
        {
            var k0 = _lsfRatio[scalefacCompress % 2][0][isPos];
            var k1 = _lsfRatio[scalefacCompress % 2][1][isPos];
            if (StereoMode == OmniMusicMpeg.StereoMode.DownmixToMono)
            {
                var ratio = 1 / (k0 + k1);
                for (; sb > 0; sb--, i++)
                {
                    _samples[0][i] *= ratio;
                }
            }
            else
            {
                for (; sb > 0; sb--, i++)
                {
                    _samples[1][i] = _samples[0][i] * k1;
                    _samples[0][i] *= k0;
                }
            }
        }

        void ApplyMidSide(int i, int sb)
        {
            if (StereoMode == StereoMode.DownmixToMono)
            {
                for (; sb > 0; sb--, i++)
                {
                    _samples[0][i] *= 0.707106781f; // scale appropriately
                }
            }
            else
            {
                for (; sb > 0; sb--, i++)
                {
                    // apply the mid/side
                    var a = _samples[0][i];
                    var b = _samples[1][i];
                    _samples[0][i] = (a + b) * 0.707106781f;
                    _samples[1][i] = (a - b) * 0.707106781f;
                }
            }
        }

        void ApplyFullStereo(int i, int sb)
        {
            if (StereoMode == OmniMusicMpeg.StereoMode.DownmixToMono)
            {
                for (; sb > 0; sb--, i++)
                {
                    _samples[0][i] = (_samples[0][i] + _samples[1][i]) / 2f;
                }
            }
        }

        #endregion

        #region Reorder

        #region Variables

        float[] _reorderBuf = new float[SBLIMIT * SSLIMIT];

        #endregion

        void Reorder(float[] buf, bool mixedBlock)
        {
            // reorder into _reorderBuf, then copy back
            int sfb = 0;

            if (mixedBlock)
            {
                // mixed... copy the first two bands and reorder the rest
                Array.Copy(buf, 0, _reorderBuf, 0, SSLIMIT * 2);

                sfb = 3;
            }

            while (sfb < 13)
            {
                int sfb_start = _sfBandIndexS[sfb];
                int sfb_lines = _sfBandIndexS[sfb + 1] - sfb_start;

                for (int window = 0; window < 3; window++)
                {
                    for (int freq = 0; freq < sfb_lines; freq++)
                    {
                        var src_line = sfb_start * 3 + window * sfb_lines + freq;
                        var des_line = (sfb_start * 3) + window + (freq * 3);
                        _reorderBuf[des_line] = buf[src_line];
                    }
                }

                ++sfb;
            }

            Array.Copy(_reorderBuf, buf, SSLIMIT * SBLIMIT);
        }

        #endregion

        #region Anti-Alias

        #region Variables

        static readonly float[] _scs = {
                                        0.85749292571254400f,  0.88174199731770500f,  0.94962864910273300f,  0.98331459249179000f,
                                        0.99551781606758600f,  0.99916055817814800f,  0.99989919524444700f,  0.99999315507028000f,
                                       };

        static readonly float[] _sca = {
                                       -0.51449575542752700f, -0.47173196856497200f, -0.31337745420390200f, -0.18191319961098100f,
                                       -0.09457419252642070f, -0.04096558288530410f, -0.01419856857247120f, -0.00369997467376004f,
                                       };

        #endregion

        void AntiAlias(float[] buf, bool mixedBlock)
        {
            int sblim;
            if (mixedBlock)
            {
                sblim = 1;
            }
            else
            {
                sblim = SBLIMIT - 1;
            }

            for (int sb = 0, offset = 0; sb < sblim; sb++, offset += SSLIMIT)
            {
                for (int ss = 0, buOfs = offset + SSLIMIT - 1, bdOfs = offset + SSLIMIT; ss < 8; ss++, buOfs--, bdOfs++)
                {
                    var bu = buf[buOfs];
                    var bd = buf[bdOfs];
                    buf[buOfs] = (bu * _scs[ss]) - (bd * _sca[ss]);
                    buf[bdOfs] = (bd * _scs[ss]) + (bu * _sca[ss]);
                }
            }
        }

        #endregion

        #region Frequency Inversion

        void FrequencyInversion(float[] buf)
        {
            for (int ss = 1; ss < SSLIMIT; ss += 2)
            {
                for (int sb = 1; sb < SBLIMIT; sb += 2)
                {
                    buf[sb * SSLIMIT + ss] = -buf[sb * SSLIMIT + ss];
                }
            }
        }

        #endregion

        #region Inverse Polyphase

        #region Variables

        float[] _polyPhase = new float[SBLIMIT];

        #endregion

        // Layer III interleaves the samples, so we have to make them linear again
        void InversePolyphase(float[] buf, int ch, int ofs, float[] outBuf)
        {
            for (int ss = 0; ss < SSLIMIT; ss++, ofs += SBLIMIT)
            {
                for (int sb = 0; sb < SBLIMIT; sb++)
                {
                    _polyPhase[sb] = buf[sb * SSLIMIT + ss];
                }

                base.InversePolyPhase(ch, _polyPhase);
                Array.Copy(_polyPhase, 0, outBuf, ofs, SBLIMIT);
            }
        }

        #endregion
    }
}


namespace OmniMusicMpeg.Decoder
{
using System;
using System.Collections.Generic;
using System.Text;
    class MpegFrame : FrameBase, IMpegFrame
    {
        static readonly int[][][] _bitRateTable =
        {
            new int[][]
            {
                new int[] { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },
                new int[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },
                new int[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }
            },
            new int[][]
            {
                new int[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },
                new int[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
                new int[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 }
            },
        };

        internal static MpegFrame TrySync(uint syncMark)
        {
            if ((syncMark & 0xFFE00000) == 0xFFE00000   // frame sync
             && (syncMark & 0x00180000) != 0x00080000   // MPEG version != reserved
             && (syncMark & 0x00060000) != 0x00000000   // layer version != reserved
             && (syncMark & 0x0000F000) != 0x0000F000   // bitrate != bad
             && (syncMark & 0x00000C00) != 0x00000C00   // sample rate != reserved
               )
            {
                // now check the stereo modes
                switch ((syncMark >> 4) & 0xF)
                {
                    case 0x0:   // stereo
                    case 0x4:   // joint stereo
                    case 0x5:   // "
                    case 0x6:   // "
                    case 0x7:   // "
                    case 0x8:   // dual channel
                    case 0xC:   // mono
                        return new MpegFrame { _syncBits = (int)syncMark };
                }
            }

            return null;
        }

        // base: 2xIntPtr, 1x8, 1x4
        // total: 3xIntPtr, 4x8, 4x4
        // Long Mode: 72 bytes per instance
        // Prot Mode: 60 bytes per instance

        // IntPtr
        internal MpegFrame Next;
        // 4
        internal int Number;

        // 4
        int _syncBits;

        // 8
        int _readOffset, _bitsRead;
        // 8
        ulong _bitBucket = 0UL;
        // 8
        long _offset;
        // 4
        bool _isMuted;

        MpegFrame()
        {

        }

        protected override int Validate()
        {
            // TrySync has already validated version, layer, bitrate, and samplerate

            // if layer2, we have to verify the channel mode is valid for the bitrate selected
            if (Layer == MpegLayer.LayerII)
            {
                switch (BitRate)
                {
                    case 32000:
                    case 48000:
                    case 56000:
                    case 80000:
                        // don't allow anything except mono
                        if (ChannelMode != MpegChannelMode.Mono) return -1;
                        break;
                    case 224000:
                    case 256000:
                    case 320000:
                    case 384000:
                        // don't allow mono
                        if (ChannelMode == MpegChannelMode.Mono) return -1;
                        break;
                }
            }

            // calculate the frame's length
            int frameSize;
            if (BitRateIndex > 0)
            {
                if (Layer == MpegLayer.LayerI)
                {
                    frameSize = (12 * BitRate / SampleRate + Padding) * 4;
                }
                else
                {
                    // SampleCount/8 is 144 for MPEG-1 Layer II/III and Layer II generally,
                    // but 72 for MPEG-2/2.5 Layer III (576 samples per frame). Using a
                    // hard-coded 144 doubled the frame length for MPEG-2/2.5 Layer III,
                    // desyncing the parser and producing garbled output (see issues #10/#43/#33).
                    frameSize = SampleCount / 8 * BitRate / SampleRate + Padding;
                }
            }
            else
            {
                // "free" frame...  we have to calculate it later
                frameSize = _readOffset + GetSideDataSize() + Padding; // we know the frame will be at least this big...
            }

            // now check the crc if one is present
            if (HasCrc)
            {
                // prep for CRC reading
                _readOffset = 4 + (HasCrc ? 2 : 0);

                // check the CRC
                if (!ValidateCRC())
                {
                    // mute this frame
                    _isMuted = true;
                    return 6;   // header + crc...  force the reader to re-sync
                }
            }

            // prep for reading
            Reset();

            // finally, let our caller know how big this frame is (including the sync header)
            return frameSize;
        }

        internal int GetSideDataSize()
        {
            switch (Layer)
            {
                case MpegLayer.LayerI:
                    if (ChannelMode == MpegChannelMode.Mono)
                    {
                        // mono
                        return 16;
                    }
                    else if (ChannelMode == MpegChannelMode.Stereo || ChannelMode == MpegChannelMode.DualChannel)
                    {
                        // full stereo / dual channel
                        return 32;
                    }
                    else
                    {
                        // joint stereo...  ugh...
                        switch (ChannelModeExtension)
                        {
                            case 0:
                                return 18;
                            case 1:
                                return 20;
                            case 2:
                                return 22;
                            case 3:
                                return 24;
                        }
                    }
                    break;
                case MpegLayer.LayerII:
                    return 0;
                case MpegLayer.LayerIII:
                    if (ChannelMode == MpegChannelMode.Mono && Version >= MpegVersion.Version2)
                    {
                        return 9;
                    }
                    else if (ChannelMode != MpegChannelMode.Mono && Version < MpegVersion.Version2)
                    {
                        return 32;
                    }
                    else
                    {
                        return 17;
                    }
            }

            return 0;
        }

        bool ValidateCRC()
        {
            var crc = 0xFFFFU;

            // process the common bits...
            UpdateCRC(_syncBits, 16, ref crc);

            var apply = false;
            switch (Layer)
            {
                case MpegLayer.LayerI:
                    apply = LayerIDecoder.GetCRC(this, ref crc);
                    break;
                case MpegLayer.LayerII:
                    apply = LayerIIDecoder.GetCRC(this, ref crc);
                    break;
                case MpegLayer.LayerIII:
                    apply = LayerIIIDecoder.GetCRC(this, ref crc);
                    break;
            }

            if (apply)
            {
                var checkCrc = ReadByte(4) << 8 | ReadByte(5);
                return checkCrc == crc;
            }

            return true;
        }

        static internal void UpdateCRC(int data, int length, ref uint crc)
        {
            var masking = 1U << length;
            while ((masking >>= 1) != 0)
            {
                var carry = crc & 0x8000;
                crc <<= 1;
                if ((carry == 0) ^ ((data & masking) == 0))
                {
                    crc ^= 0x8005;
                }
            }
            crc &= 0xFFFF;
        }

        internal VBRInfo ParseVBR()
        {
            var buf = new byte[4];

            // Xing first
            int offset;
            if (Version == MpegVersion.Version1 && ChannelMode != MpegChannelMode.Mono)
            {
                offset = 32 + 4;
            }
            else if (Version > MpegVersion.Version1 && ChannelMode == MpegChannelMode.Mono)
            {
                offset = 9 + 4;
            }
            else
            {
                offset = 17 + 4;
            }

            if (Read(offset, buf) != 4) return null;
            if (buf[0] == 'X' && buf[1] == 'i' && buf[2] == 'n' && buf[3] == 'g'
             || buf[0] == 'I' && buf[1] == 'n' && buf[2] == 'f' && buf[3] == 'o')
            {
                return ParseXing(offset + 4);
            }

            // then VBRI (kinda rare)
            if (Read(36, buf) != 4) return null;
            if (buf[0] == 'V' && buf[1] == 'B' && buf[2] == 'R' && buf[3] == 'I')
            {
                return ParseVBRI();
            }

            return null;
        }

        VBRInfo ParseXing(int offset)
        {
            VBRInfo info = new VBRInfo();
            info.Channels = Channels;
            info.SampleRate = SampleRate;
            info.SampleCount = SampleCount;

            var buf = new byte[100];
            if (Read(offset, buf, 0, 4) != 4) return null;
            offset += 4;

            var flags = buf[0] << 24 | buf[1] << 16 | buf[2] << 8 | buf[3];

            // frame count
            if ((flags & 0x1) != 0)
            {
                if (Read(offset, buf, 0, 4) != 4) return null;
                offset += 4;
                info.VBRFrames = buf[0] << 24 | buf[1] << 16 | buf[2] << 8 | buf[3];
            }

            // byte count
            if ((flags & 0x2) != 0)
            {
                if (Read(offset, buf, 0, 4) != 4) return null;
                offset += 4;
                info.VBRBytes = buf[0] << 24 | buf[1] << 16 | buf[2] << 8 | buf[3];
            }

            // TOC
            if ((flags & 0x4) != 0)
            {
                // we're not using the TOC, so just discard it
                if (Read(offset, buf) != 100) return null;
                offset += 100;
            }

            // scale
            if ((flags & 0x8) != 0)
            {
                if (Read(offset, buf, 0, 4) != 4) return null;
                offset += 4;
                info.VBRQuality = buf[0] << 24 | buf[1] << 16 | buf[2] << 8 | buf[3];
            }

            // now look for a LAME header (note: if it isn't found, it doesn't fail the VBR parse)
            do
            {
                // LAME tag is 9 bytes identifier + 27 bytes of data = 36 bytes total
                // We need at least 24 bytes to reach the delay/padding field
                if (Read(offset, buf, 0, 24) != 24) break;

                // "LAME" identifier (first 4 bytes)
                if (buf[0] != 'L' || buf[1] != 'A' || buf[2] != 'M' || buf[3] != 'E') break;

                // Info tag revision (high nibble of byte 9): only 0 and 1 are valid
                if ((buf[9] & 0xF0) > 0x10) break;

                // Encoder delay and end padding are packed into bytes 21-23:
                //   encoder_delay  = high 12 bits
                //   end_padding    = low  12 bits
                info.EncoderDelay   = (buf[21] << 4) | (buf[22] >> 4);
                info.EncoderPadding = ((buf[22] & 0x0F) << 8) | buf[23];
            } while (false);

            return info;
        }

        VBRInfo ParseVBRI()
        {
            VBRInfo info = new VBRInfo();
            info.Channels = Channels;
            info.SampleRate = SampleRate;
            info.SampleCount = SampleCount;

            // VBRI is "fixed" size...  Yay. :)
            var buf = new byte[26];
            if (Read(36, buf) != 26) return null;

            // Version
            var version = buf[4] << 8 | buf[5];

            // Delay
            info.VBRDelay = buf[6] << 8 | buf[7];

            // Quality
            info.VBRQuality = buf[8] << 8 | buf[9];

            // Bytes
            info.VBRBytes = buf[10] << 24 | buf[11] << 16 | buf[12] << 8 | buf[13];

            // Frames
            info.VBRFrames = buf[14] << 24 | buf[15] << 16 | buf[16] << 8 | buf[17];

            // TOC
            // entries
            var tocEntries = buf[18] << 8 | buf[19];
            var tocScale = buf[20] << 8 | buf[21];
            var tocEntrySize = buf[22] << 8 | buf[23];
            var tocFramesPerEntry = buf[24] << 8 | buf[25];
            var tocSize = tocEntries * tocEntrySize;

            var toc = new byte[tocSize];
            if (Read(62, toc) != tocSize) return null;

            return info;
        }

        public int FrameLength
        {
            get { return base.Length; }
        }

        public MpegVersion Version
        {
            get
            {
                switch ((_syncBits >> 19) & 3)
                {
                    case 0:
                        return MpegVersion.Version25;
                    case 2:
                        return MpegVersion.Version2;
                    case 3:
                        return MpegVersion.Version1;
                }
                return MpegVersion.Unknown;
            }
        }
        public MpegLayer Layer
        {
            get
            {
                // the order is backwards, and "0" is invalid
                return (MpegLayer)((4 - ((_syncBits >> 17) & 3)) % 4);
            }
        }
        public bool HasCrc
        {
            get { return (_syncBits & 0x10000) == 0; }
        }
        public int BitRate
        {
            get
            {
                if (BitRateIndex > 0)
                {
                    return _bitRateTable[(int)Version / 10 - 1][(int)Layer - 1][BitRateIndex] * 1000;
                }
                else
                {
                    // bitrate is always an even multiple of 1000, so round
                    return ((((FrameLength * 8) * SampleRate) / SampleCount + 499) + 500) / 1000 * 1000;
                }
            }
        }
        public int BitRateIndex
        {
            get { return (_syncBits >> 12) & 0xF; }
        }
        public int SampleRate
        {
            get
            {
                int sr;
                switch (SampleRateIndex)
                {
                    case 0: sr = 44100; break;
                    case 1: sr = 48000; break;
                    case 2: sr = 32000; break;
                    default: sr = 0; break;
                }
                if (Version > MpegVersion.Version1)
                {
                    if (Version == MpegVersion.Version25)
                    {
                        sr /= 4;
                    }
                    else
                    {
                        sr /= 2;
                    }
                }
                return sr;
            }
        }
        public int SampleRateIndex
        {
            get { return (_syncBits >> 10) & 0x3; }
        }
        private int Padding
        {
            get { return (_syncBits >> 9) & 0x1; }
        }
        public MpegChannelMode ChannelMode
        {
            get { return (MpegChannelMode)((_syncBits >> 6) & 0x3); }
        }
        public int ChannelModeExtension
        {
            get { return (_syncBits >> 4) & 0x3; }
        }
        internal int Channels
        {
            get { return (ChannelMode == MpegChannelMode.Mono ? 1 : 2); }
        }
        public bool IsCopyrighted
        {
            get { return (_syncBits & 0x8) == 0x8; }
        }
        internal bool IsOriginal
        {
            get { return (_syncBits & 0x4) == 0x4; }
        }
        internal int EmphasisMode
        {
            get { return (_syncBits & 0x3); }
        }
        public bool IsCorrupted
        {
            get { return _isMuted; }
        }

        public int SampleCount
        {
            get
            {
                if (Layer == MpegLayer.LayerI) return 384;
                if (Layer == MpegLayer.LayerIII && Version > MpegVersion.Version1) return 576;
                return 1152;
            }
        }
        internal long SampleOffset
        {
            get { return _offset; }
            set { _offset = value; }
        }


        public void Reset()
        {
            _readOffset = 4 + (HasCrc ? 2 : 0);
            _bitBucket = 0UL;
            _bitsRead = 0;
        }

        public int ReadBits(int bitCount)
        {
            if (bitCount < 1 || bitCount > 32) throw new ArgumentOutOfRangeException("bitCount");
            if (_isMuted) return 0;

            while (_bitsRead < bitCount)
            {
                var b = ReadByte(_readOffset);
                if (b == -1) throw new System.IO.EndOfStreamException();
                
                ++_readOffset;
                
                _bitBucket <<= 8;
                _bitBucket |= (byte)(b & 0xFF);
                _bitsRead += 8;
            }

            var temp = (int)((_bitBucket >> (_bitsRead - bitCount)) & ((1UL << bitCount) - 1));
            _bitsRead -= bitCount;
            return temp;
        }

#if DEBUG
        public override string ToString()
        {
            // version
            var sb = new StringBuilder("MPEG");
            switch (Version)
            {
                case MpegVersion.Version1: sb.Append("1"); break;
                case MpegVersion.Version2: sb.Append("2"); break;
                case MpegVersion.Version25: sb.Append("2.5"); break;
            }

            // layer
            sb.Append(" Layer ");
            switch (Layer)
            {
                case MpegLayer.LayerI: sb.Append("I"); break;
                case MpegLayer.LayerII: sb.Append("II"); break;
                case MpegLayer.LayerIII: sb.Append("III"); break;
            }

            // bitrate
            sb.AppendFormat(" {0} kbps ", BitRate / 1000);

            // channel mode
            switch (ChannelMode)
            {
                case MpegChannelMode.Stereo:
                    sb.Append("Stereo");
                    break;
                case MpegChannelMode.JointStereo:
                    sb.Append("Joint Stereo");
                    switch (ChannelModeExtension)
                    {
                        case 1: sb.Append(" (I)"); break;
                        case 2: sb.Append(" (M/S)"); break;
                        case 3: sb.Append(" (M/S,I)"); break;
                    }
                    break;
                case MpegChannelMode.DualChannel:
                    sb.Append("Dual Channel");
                    break;
                case MpegChannelMode.Mono:
                    sb.Append("Mono");
                    break;
            }

            // sample rate
            sb.AppendFormat(" {0} KHz", (float)SampleRate / 1000);

            var flagList = new List<string>();
            // protection
            if (HasCrc) flagList.Add("CRC");

            // copyright
            if (IsCopyrighted) flagList.Add("Copyright");

            // original
            if (IsOriginal) flagList.Add("Original");

            // emphasis
            switch (EmphasisMode)
            {
                case 1:
                    flagList.Add("50/15 ms");
                    break;
                case 2:
                    flagList.Add("Invalid Emphasis");
                    break;
                case 3:
                    flagList.Add("CCIT J.17");
                    break;
            }

            if (flagList.Count > 0)
            {
                sb.AppendFormat(" ({0})", string.Join(",", flagList.ToArray()));
            }

            return sb.ToString();
        }
#endif
    }
}


namespace OmniMusicMpeg.Decoder
{
using System;
using System.IO;
    class MpegStreamReader
    {
        ID3Frame _id3Frame, _id3v1Frame;
        RiffHeaderFrame _riffHeaderFrame;

        VBRInfo _vbrInfo;
        MpegFrame _first, _current, _last, _lastFree;

        long _readOffset, _eofOffset;
        Stream _source;
        bool _canSeek, _endFound, _mixedFrameSize;
        object _readLock = new object();
        object _frameLock = new object();

        internal MpegStreamReader(Stream source)
        {
            _source = source;
            _canSeek = source.CanSeek;
            _readOffset = 0L;
            _eofOffset = long.MaxValue;

            // find the first Mpeg frame
            var frame = FindNextFrame();

            // Parse the ID3 tag now while the ReadBuffer is still near the start of the file.
            // iTunSMPB gapless data must be read eagerly; backward seeks via ReadBuffer are unreliable.
            _id3Frame?.ParseEagerIfNeeded();

            while (frame != null && !(frame is MpegFrame))
            {
                frame = FindNextFrame();
            }

            // if we still don't have a frame, we never sync'ed
            if (frame == null) throw new InvalidDataException("Not a valid MPEG file!");

            // the very next frame "should be" an mpeg frame
            frame = FindNextFrame();

            // if not, it's not a valid file
            if (frame == null || !(frame is MpegFrame)) throw new InvalidDataException("Not a valid MPEG file!");

            // seek to the first frame
            _current = _first;
        }

        FrameBase FindNextFrame()
        {
            // if we've found the end, don't bother looking for anything else
            if (_endFound) return null;

            var freeFrame = _lastFree;
            var lastFrameStart = _readOffset;

            lock (_frameLock)
            {
                // read 3 bytes
                var syncBuf = new byte[4];
                try
                {
                    if (Read(_readOffset, syncBuf, 0, 4) == 4)
                    {
                        // now loop until a frame is found
                        do
                        {
                            var sync = (uint)(syncBuf[0] << 24 | syncBuf[1] << 16 | syncBuf[2] << 8 | syncBuf[3]);

                            lastFrameStart = _readOffset;

                            // try ID3 first (for v2 frames)
                            if (_id3Frame == null)
                            {
                                var f = ID3Frame.TrySync(sync);
                                if (f != null)
                                {
                                    if (f.Validate(_readOffset, this))
                                    {
                                        if (!_canSeek) f.SaveBuffer();

                                        _readOffset += f.Length;
                                        DiscardThrough(_readOffset, true);

                                        return _id3Frame = f;
                                    }
                                }
                            }

                            // now look for a RIFF header
                            if (_first == null && _riffHeaderFrame == null)
                            {
                                var f = RiffHeaderFrame.TrySync(sync);
                                if (f != null)
                                {
                                    if (f.Validate(_readOffset, this))
                                    {
                                        _readOffset += f.Length;
                                        DiscardThrough(_readOffset, true);

                                        return _riffHeaderFrame = f;
                                    }
                                }
                            }

                            // finally, just try for an MPEG frame
                            var frame = MpegFrame.TrySync(sync);
                            if (frame != null)
                            {
                                if (frame.Validate(_readOffset, this)
                                   && !(freeFrame != null
                                       && (frame.Layer != freeFrame.Layer
                                          || frame.Version != freeFrame.Version
                                          || frame.SampleRate != freeFrame.SampleRate
                                          || frame.BitRateIndex > 0
                                          )
                                       )
                                   )
                                {
                                    if (!_canSeek)
                                    {
                                        frame.SaveBuffer();
                                        DiscardThrough(_readOffset + frame.FrameLength, true);
                                    }

                                    _readOffset += frame.FrameLength;

                                    if (_first == null)
                                    {
                                        if (_vbrInfo == null && (_vbrInfo = frame.ParseVBR()) != null)
                                        {
                                            return FindNextFrame();
                                        }
                                        else
                                        {
                                            frame.Number = 0;
                                            _first = _last = frame;
                                        }
                                    }
                                    else
                                    {
                                        if (frame.SampleCount != _first.SampleCount)
                                        {
                                            _mixedFrameSize = true;
                                        }

                                        frame.SampleOffset = _last.SampleCount + _last.SampleOffset;
                                        frame.Number = _last.Number + 1;
                                        _last = (_last.Next = frame);
                                    }

                                    if (frame.BitRateIndex == 0)
                                    {
                                        _lastFree = frame;
                                    }

                                    return frame;
                                }
                            }

                            // if we've read MPEG frames and can't figure out what frame type we have, try looking for a new ID3 tag
                            if (_last != null)
                            {
                                var f = ID3Frame.TrySync(sync);
                                if (f != null)
                                {
                                    if (f.Validate(_readOffset, this))
                                    {
                                        if (!_canSeek) f.SaveBuffer();

                                        // if it's a v1 tag, go ahead and parse it
                                        if (f.Version == 1)
                                        {
                                            _id3v1Frame = f;
                                        }
                                        else
                                        {
                                            // grrr...  the ID3 2.4 spec says tags can be anywhere in the file and that later tags can override earlier ones...  boo
                                            _id3Frame.Merge(f);
                                        }

                                        _readOffset += f.Length;
                                        DiscardThrough(_readOffset, true);

                                        return f;
                                    }
                                }
                            }

                            // well, we didn't find anything, so rinse and repeat with the next byte
                            ++_readOffset;
                            if (_first == null || !_canSeek) DiscardThrough(_readOffset, true);
                            Buffer.BlockCopy(syncBuf, 1, syncBuf, 0, 3);
                        } while (Read(_readOffset + 3, syncBuf, 3, 1) == 1);
                    }

                    // move the "end of frame" marker for the last free format frame (in case we have one)
                    // this is because we don't include the last four bytes otherwise
                    lastFrameStart += 4;

                    _endFound = true;
                    return null;
                }
                finally
                {
                    if (freeFrame != null)
                    {
                        freeFrame.Length = (int)(lastFrameStart - freeFrame.Offset);

                        if (!_canSeek)
                        {
                            // gotta finish filling the buffer!!
                            throw new InvalidOperationException("Free frames cannot be read properly from forward-only streams!");
                        }

                        // if _lastFree hasn't changed (we got a non-MPEG frame), clear it out
                        if (_lastFree == freeFrame)
                        {
                            _lastFree = null;
                        }
                    }
                }
            }
        }

        class ReadBuffer
        {
            public byte[] Data;
            public long BaseOffset;
            public int End;
            public int DiscardCount;

            object _localLock = new object();

            public ReadBuffer(int initialSize)
            {
                initialSize = 2 << (int)Math.Log(initialSize, 2);

                Data = new byte[initialSize];
            }

            public int Read(MpegStreamReader reader, long offset, byte[] buffer, int index, int count)
            {
                lock (_localLock)
                {
                    var startIdx = EnsureFilled(reader, offset, ref count);
                    
                    Buffer.BlockCopy(Data, startIdx, buffer, index, count);
                }
                return count;
            }

            public int ReadByte(MpegStreamReader reader, long offset)
            {
                lock (_localLock)
                {
                    var count = 1;
                    var startIdx = EnsureFilled(reader, offset, ref count);
                    if (count == 1)
                    {
                        return Data[startIdx];
                    }
                }
                return -1;
            }

            int EnsureFilled(MpegStreamReader reader, long offset, ref int count)
            {
                // if the offset & count are inside our buffer's range, just return the appropriate index
                var startIdx = (int)(offset - BaseOffset);
                int endIdx = startIdx + count;
                if (startIdx < 0 || endIdx > End)
                {
                    int readStart = 0, readCount = 0, moveCount = 0;
                    long readOffset = 0;

                    #region Decision-Making

                    if (startIdx < 0)
                    {
                        // if we can't seek, there's nothing we can do
                        if (!reader._source.CanSeek) throw new InvalidOperationException("Cannot seek backwards on a forward-only stream!");

                        // Seeking backwards before the start of the buffer.  The historic
                        // "keep the overlap" optimization tried an in-place backwards move of the
                        // buffered data, but both the move itself (a loop whose condition could
                        // never be true) and the surrounding bookkeeping (the subsequent read was
                        // written over the moved data instead of into the gap at the front) were
                        // broken, corrupting the buffer on backwards seeks and throwing/garbling
                        // audio (issues #7 and #38).  Just truncate and re-read from the requested
                        // offset, which is the same well-tested path already used when the request
                        // ends before the start of the buffer.
                        Truncate();

                        BaseOffset = offset;
                        readOffset = offset;
                        startIdx = 0;
                        endIdx = count;
                        readCount = count;
                    }
                    else // i.e., startIdx >= 0
                    {
                        // we only get to here if at least one byte of the request is past the end of the read data
                        // start with the simplest scenario and work our way up

                        // 1) We just need to fill the buffer a bit more
                        if (endIdx < Data.Length)
                        {
                            readCount = endIdx - End;
                            readStart = End;
                            readOffset = BaseOffset + readStart;
                        }
                        // 2) We need to discard some bytes, then fill the buffer
                        else if (endIdx - DiscardCount < Data.Length)
                        {
                            moveCount = DiscardCount;
                            readStart = End;
                            readCount = endIdx - readStart;
                            readOffset = BaseOffset + readStart;
                        }
                        // 3) We need to expand the buffer to hold all the existing & requested data
                        else if (Data.Length * 2 <= 16384)
                        {
                            // by definition, we discard
                            moveCount = DiscardCount;
                            readStart = End;
                            readCount = endIdx - End;
                            readOffset = BaseOffset + readStart;
                        }
                        // 4) We have to throw away some data that hasn't been discarded
                        else
                        {
                            // just truncate
                            Truncate();

                            // set up our read parameters
                            BaseOffset = offset;
                            readOffset = offset;
                            startIdx = 0;
                            endIdx = count;

                            // how much do we have to read?
                            readCount = count;
                        }
                    }

                    #endregion

                    #region Buffer Resizing & Data Moving

                    if (endIdx - moveCount > Data.Length || readStart + readCount - moveCount > Data.Length)
                    {
                        var newSize = Data.Length * 2;
                        while (newSize < endIdx - moveCount)
                        {
                            newSize *= 2;
                        }

                        var newBuf = new byte[newSize];

                        // forward or neutral copy (moveCount is always >= 0; it only ever
                        // carries the discard count now that backwards seeks truncate)
                        Buffer.BlockCopy(Data, moveCount, newBuf, 0, End - moveCount);

                        DiscardCount -= moveCount;
                        Data = newBuf;
                    }
                    else if (moveCount > 0)
                    {
                        // forward move
                        Buffer.BlockCopy(Data, moveCount, Data, 0, End - moveCount);

                        DiscardCount -= moveCount;
                    }

                    BaseOffset += moveCount;
                    readStart -= moveCount;
                    startIdx -= moveCount;
                    endIdx -= moveCount;
                    End -= moveCount;

                    #endregion

                    #region Buffer Filling

                    lock (reader._readLock)
                    {
                        if (readCount > 0 && reader._source.Position != readOffset && readOffset < reader._eofOffset)
                        {
                            if (reader._canSeek)
                            {
                                try
                                {
                                    reader._source.Position = readOffset;
                                }
                                catch (EndOfStreamException)
                                {
                                    reader._eofOffset = reader._source.Length;
                                    readCount = 0;
                                }
                            }
                            else
                            {
                                // ugh, gotta read bytes until we've reached the desired offset
                                var seekCount = readOffset - reader._source.Position;
                                while (--seekCount >= 0)
                                {
                                    if (reader._source.ReadByte() == -1)
                                    {
                                        reader._eofOffset = reader._source.Position;
                                        readCount = 0;
                                        break;
                                    }
                                }
                            }
                        }

                        while (readCount > 0 && readOffset < reader._eofOffset)
                        {
                            var temp = reader._source.Read(Data, readStart, readCount);
                            if (temp == 0)
                            {
                                break;
                            }
                            readStart += temp;
                            readOffset += temp;
                            readCount -= temp;
                        }

                        if (readStart > End)
                        {
                            End = readStart;
                        }

                        if (End < endIdx)
                        {
                            // we didn't get a full read...
                            count = Math.Max(0, End - startIdx);
                        }
                        // NB: if desired, switch to "minimal reads" by commenting-out this clause
                        else if (End < Data.Length)
                        {
                            // try to finish filling the buffer
                            var temp = reader._source.Read(Data, End, Data.Length - End);
                            End += temp;
                        }
                    }

                    #endregion
                }

                return startIdx;
            }

            public void DiscardThrough(long offset)
            {
                lock (_localLock)
                {
                    var count = (int)(offset - BaseOffset);
                    DiscardCount = Math.Max(count, DiscardCount);

                    if (DiscardCount >= Data.Length) CommitDiscard();
                }
            }

            void Truncate()
            {
                End = 0;
                DiscardCount = 0;
            }

            void CommitDiscard()
            {
                if (DiscardCount >= Data.Length || DiscardCount >= End)
                {
                    // we have been told to discard the entire buffer
                    BaseOffset += DiscardCount;
                    End = 0;
                }
                else
                {
                    // just discard the first part...
                    //Array.Copy(_readBuf, _readBufDiscardCount, _readBuf, 0, _readBufEnd - _readBufDiscardCount);
                    Buffer.BlockCopy(Data, DiscardCount, Data, 0, End - DiscardCount);
                    BaseOffset += DiscardCount;
                    End -= DiscardCount;
                }
                DiscardCount = 0;
            }
        }

        ReadBuffer _readBuf = new ReadBuffer(2048);

        internal int Read(long offset, byte[] buffer, int index, int count)
        {
            // make sure the offset is at least positive
            if (offset < 0L) throw new ArgumentOutOfRangeException("offset");

            // make sure the buffer is valid
            if (index < 0 || index + count > buffer.Length) throw new ArgumentOutOfRangeException("index");

            return _readBuf.Read(this, offset, buffer, index, count);
        }

        internal int ReadByte(long offset)
        {
            if (offset < 0L) throw new ArgumentOutOfRangeException("offset");

            return _readBuf.ReadByte(this, offset);
        }

        internal void DiscardThrough(long offset, bool minimalRead)
        {
            _readBuf.DiscardThrough(offset);
        }


        internal void ReadToEnd()
        {
            try
            {
                var maxAllocation = 40000;
                if (_id3Frame != null)
                {
                    maxAllocation += _id3Frame.Length;
                }

                while (!_endFound)
                {
                    FindNextFrame();

                    while (!_canSeek && FrameBase.TotalAllocation >= maxAllocation)
                    {
#if NET35 
                        System.Threading.Thread.Sleep(500);
#else
                        System.Threading.Tasks.Task.Delay(500).Wait(); //
#endif
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // in case the stream was disposed before we finished...
            }
        }


        internal bool CanSeek
        {
            get { return _canSeek; }
        }

        internal long SampleCount
        {
            get
            {
                if (_vbrInfo != null) return _vbrInfo.VBRStreamSampleCount;

                if (!_canSeek) return -1;

                ReadToEnd();
                return _last.SampleCount + _last.SampleOffset;
            }
        }

        internal int EncoderDelay
        {
            get
            {
                if (_vbrInfo != null && _vbrInfo.EncoderDelay > 0) return _vbrInfo.EncoderDelay;
                if (_id3Frame != null) return _id3Frame.EncoderDelay;
                return 0;
            }
        }

        internal int EncoderPadding
        {
            get
            {
                if (_vbrInfo != null && _vbrInfo.EncoderPadding > 0) return _vbrInfo.EncoderPadding;
                if (_id3Frame != null) return _id3Frame.EncoderPadding;
                return 0;
            }
        }

        internal int SampleRate
        {
            get
            {
                if (_vbrInfo != null) return _vbrInfo.SampleRate;
                return _first.SampleRate;
            }
        }

        internal int Channels
        {
            get
            {
                if (_vbrInfo != null) return _vbrInfo.Channels;
                return _first.Channels;
            }
        }

        internal int FirstFrameSampleCount
        {
            get { return (_first != null ? _first.SampleCount : 0); }
        }


        internal long SeekTo(long sampleNumber)
        {
            if (!_canSeek) throw new InvalidOperationException("Cannot seek!");

            // first try to "seek" by calculating the frame number
            var cnt = (int)(sampleNumber / _first.SampleCount);
            var frame = _first;
            if (_current != null && _current.Number <= cnt && _current.SampleOffset <= sampleNumber)
            {
                // if this fires, we can short-circuit things a bit...
                frame = _current;
                cnt -= frame.Number;
            }
            while (!_mixedFrameSize && --cnt >= 0 && frame != null)
            {
                // make sure we have more frames to look at
                if (frame == _last && !_endFound)
                {
                    do
                    {
                        FindNextFrame();
                    } while (frame == _last && !_endFound);
                }

                // if we've found a different frame size, fall through...
                if (_mixedFrameSize)
                {
                    break;
                }

                frame = frame.Next;
            }

            // this should not run unless we found mixed frames...
            while (frame != null && frame.SampleOffset + frame.SampleCount < sampleNumber)
            {
                if (frame == _last && !_endFound)
                {
                    do
                    {
                        FindNextFrame();
                    } while (frame == _last && !_endFound);
                }

                frame = frame.Next;
            }
            if (frame == null) return -1;
            return (_current = frame).SampleOffset;
        }

        internal MpegFrame NextFrame()
        {
            // if _current is null, we've returned the last frame already
            var frame = _current;
            if (frame != null)
            {
                if (_canSeek)
                {
                    frame.SaveBuffer();
                    DiscardThrough(frame.Offset + frame.FrameLength, false);
                }

                if (frame == _last && !_endFound)
                {
                    do
                    {
                        FindNextFrame();
                    } while (frame == _last && !_endFound);
                }

                _current = frame.Next;

                if (!_canSeek)
                {
                    // if we're in a forward-only stream, don't bother keeping the frames that have already been processed
                    lock (_frameLock)
                    {
                        var temp = _first;
                        _first = temp.Next;
                        temp.Next = null;
                    }
                }
            }
            return frame;
        }

        internal MpegFrame GetCurrentFrame()
        {
            return _current;
        }
    }
}


namespace OmniMusicMpeg.Decoder
{
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
    /// <summary>
    /// RIFF header reader
    /// </summary>
    class RiffHeaderFrame : FrameBase
    {
        internal static RiffHeaderFrame TrySync(uint syncMark)
        {
            if (syncMark == 0x52494646U)
            {
                return new RiffHeaderFrame();
            }

            return null;
        }

        RiffHeaderFrame()
        {

        }

        protected override int Validate()
        {
            var buf = new byte[4];

            // we expect this to be the "WAVE" chunk
            if (Read(8, buf) != 4) return -1;
            if (buf[0] != 'W' || buf[1] != 'A' || buf[2] != 'V' || buf[3] != 'E') return -1;

            // now the "fmt " chunk
            if (Read(12, buf) != 4) return -1;
            if (buf[0] != 'f' || buf[1] != 'm' || buf[2] != 't' || buf[3] != ' ') return -1;

            // we've found the fmt chunk, so look for the data chunk
            var offset = 16;
            while (true)
            {
                // read the length and seek forward
                if (Read(offset, buf) != 4) return -1;
                offset += 4 + BitConverter.ToInt32(buf, 0);

                // get the chunk ID
                if (Read(offset, buf) != 4) return -1;
                offset += 4;

                // if it's not the data chunk, try again
                if (buf[0] == 'd' && buf[1] == 'a' && buf[2] == 't' && buf[3] == 'a') break;
            }

            // ... and now we know exactly where the frame ends
            return offset + 4;
        }
    }
}

namespace OmniMusicMpeg.Decoder
{

    class VBRInfo
    {
        internal VBRInfo() { }

        internal int SampleCount { get; set; }
        internal int SampleRate { get; set; }
        internal int Channels { get; set; }
        internal int VBRFrames { get; set; }
        internal int VBRBytes { get; set; }
        internal int VBRQuality { get; set; }
        internal int VBRDelay { get; set; }
        internal int EncoderDelay { get; set; }
        internal int EncoderPadding { get; set; }

        internal long VBRStreamSampleCount
        {
            get
            {
                // we assume the entire stream is consistent wrt samples per frame
                return VBRFrames * SampleCount;
            }
        }

        internal int VBRAverageBitrate
        {
            get
            {
                return (int)((VBRBytes / (VBRStreamSampleCount / (double)SampleRate)) * 8);
            }
        }
    }
}


namespace OmniMusicMpeg
{
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
    public enum MpegVersion
    {
        Unknown = 0,
        Version1 = 10,
        Version2 = 20,
        Version25 = 25,
    }

    public enum MpegLayer
    {
        Unknown = 0,
        LayerI = 1,
        LayerII = 2,
        LayerIII = 3,
    }

    public enum MpegChannelMode
    {
        Stereo,
        JointStereo,
        DualChannel,
        Mono,
    }

    public enum StereoMode
    {
        Both,
        LeftOnly,
        RightOnly,
        DownmixToMono,
    }
}

namespace OmniMusicMpeg
{

    /// <summary>
    /// Defines a standard way of representing a MPEG frame to the decoder
    /// </summary>
    public interface IMpegFrame
    {
        /// <summary>
        /// Sample rate of this frame
        /// </summary>
        int SampleRate { get; }

        /// <summary>
        /// The samplerate index (directly from the header)
        /// </summary>
        int SampleRateIndex { get; }

        /// <summary>
        /// Frame length in bytes
        /// </summary>
        int FrameLength { get; }

        /// <summary>
        /// Bit Rate
        /// </summary>
        int BitRate { get; }

        /// <summary>
        /// MPEG Version
        /// </summary>
        MpegVersion Version { get; }

        /// <summary>
        /// MPEG Layer
        /// </summary>
        MpegLayer Layer { get; }

        /// <summary>
        /// Channel Mode
        /// </summary>
        MpegChannelMode ChannelMode { get; }

        /// <summary>
        /// The number of samples in this frame
        /// </summary>
        int ChannelModeExtension { get; }

        /// <summary>
        /// The channel extension bits
        /// </summary>
        int SampleCount { get; }

        /// <summary>
        /// The bitrate index (directly from the header)
        /// </summary>
        int BitRateIndex { get; }

        /// <summary>
        /// Whether the Copyright bit is set
        /// </summary>
        bool IsCopyrighted { get; }

        /// <summary>
        /// Whether a CRC is present
        /// </summary>
        bool HasCrc { get; }

        /// <summary>
        /// Whether the CRC check failed (use error concealment strategy)
        /// </summary>
        bool IsCorrupted { get; }

        /// <summary>
        /// Resets the bit reader so frames can be reused
        /// </summary>
        void Reset();

        /// <summary>
        /// Provides sequential access to the bitstream in the frame (after the header and optional CRC)
        /// </summary>
        /// <param name="bitCount">The number of bits to read</param>
        /// <returns>-1 if the end of the frame has been encountered, otherwise the bits requested</returns>
        int ReadBits(int bitCount);
    }
}

#if NET8_0_OR_GREATER
#endif

namespace OmniMusicMpeg
{
using System;
using System.Runtime.InteropServices;
    public class MpegFile : IDisposable
    {
        System.IO.Stream _stream;
        bool _closeStream, _eofFound;

        Decoder.MpegStreamReader _reader;
        MpegFrameDecoder _decoder;

        object _seekLock = new object();
        long _position;
        int _encoderDelay, _encoderPadding;
        bool _decoderDelaySkipped;

        /// <summary>
        /// Construct Mpeg file representation from filename.
        /// </summary>
        /// <param name="fileName">The file which contains Mpeg data.</param>
        public MpegFile(string fileName)
        {
            Init(System.IO.File.Open(fileName, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read), true);
        }

        /// <summary>
        /// Construct Mpeg file representation from stream.
        /// </summary>
        /// <param name="stream">The input stream which contains Mpeg data.</param>
        public MpegFile(System.IO.Stream stream)
        {
            Init(stream, false);
        }

        void Init(System.IO.Stream stream, bool closeStream)
        {
            _stream = stream;
            _closeStream = closeStream;

            _reader = new Decoder.MpegStreamReader(_stream);

            _decoder = new MpegFrameDecoder();

            _encoderDelay = _reader.EncoderDelay;
            _encoderPadding = _reader.EncoderPadding;
        }

        /// <summary>
        /// Implements IDisposable.Dispose.
        /// </summary>
        public void Dispose()
        {
            if (_closeStream)
            {
                _stream.Dispose();
                _closeStream = false;
            }
        }
        /// <summary>
        /// Sample rate of source Mpeg, in Hertz.
        /// </summary>
        public int SampleRate { get { return _reader.SampleRate; } }

        /// <summary>
        /// Channel count of decoded output. This is the source channel count for
        /// <see cref="OmniMusicMpeg.StereoMode.Both"/>, but 1 for the single-channel modes
        /// (<see cref="OmniMusicMpeg.StereoMode.LeftOnly"/>, <see cref="OmniMusicMpeg.StereoMode.RightOnly"/>
        /// and <see cref="OmniMusicMpeg.StereoMode.DownmixToMono"/>).
        /// </summary>
        public int Channels { get { return OutputChannels; } }

        // The number of channels actually produced by ReadSamples, taking StereoMode into
        // account. All of the byte/sample/position math below is in terms of output channels.
        int OutputChannels { get { return StereoMode == StereoMode.Both ? _reader.Channels : 1; } }

        /// <summary>
        /// Whether the Mpeg stream supports seek operation.
        /// </summary>
        public bool CanSeek { get { return _reader.CanSeek; } }

        /// <summary>
        /// Data length of decoded data, in PCM.
        /// </summary>
        public long Length
        {
            get
            {
                var count = _reader.SampleCount;
                if (count < 0) return -1;
                return (count - _encoderDelay - _encoderPadding) * OutputChannels * sizeof(float);
            }
        }

        /// <summary>
        /// Media duration of the Mpeg file.
        /// </summary>
        public TimeSpan Duration
        {
            get
            {
                var len = _reader.SampleCount;
                if (len == -1) return TimeSpan.Zero;
                return TimeSpan.FromSeconds((double)(len - _encoderDelay - _encoderPadding) / _reader.SampleRate);
            }
        }

        /// <summary>
        /// Current decode position, in number of sample. Calling the setter will result in a seeking operation.
        /// </summary>
        public long Position
        {
            get { return _position; }
            set
            {
                if (!_reader.CanSeek) throw new InvalidOperationException("Cannot Seek!");
                if (value < 0L) throw new ArgumentOutOfRangeException("value");

                // we're thinking in 4-byte samples, pcmStep interleaved...  adjust accordingly
                var samples = value / sizeof(float) / OutputChannels;
                var sampleOffset = 0;

                // seek to the frame preceding the one we want (unless we're seeking to the first frame)
                if (samples >= _reader.FirstFrameSampleCount)
                {
                    sampleOffset = _reader.FirstFrameSampleCount;
                    samples -= sampleOffset;
                }

                lock (_seekLock)
                {
                    // seek the stream
                    var newPos = _reader.SeekTo(samples);
                    if (newPos == -1) throw new ArgumentOutOfRangeException("value");

                    _decoder.Reset();

                    // if we have a sample offset, decode the next frame
                    if (sampleOffset != 0)
                    {
                        _decoder.DecodeFrame(_reader.NextFrame(), _readBuf, 0); // throw away a frame (but allow the decoder to resync)
                        newPos += sampleOffset;
                    }

                    _position = newPos * sizeof(float) * OutputChannels;
                    _eofFound = false;
                    _decoderDelaySkipped = (newPos > 0 || _encoderDelay == 0);

                    // clear the decoder & buffer
                    _readBufOfs = _readBufLen = 0;
                }
            }
        }

        /// <summary>
        /// Current decode position, represented by time. Calling the setter will result in a seeking operation.
        /// </summary>
        public TimeSpan Time
        {
            get { return TimeSpan.FromSeconds((double)_position / sizeof(float) / OutputChannels / _reader.SampleRate); }
            set { Position = (long)(value.TotalSeconds * _reader.SampleRate * OutputChannels * sizeof(float)); }
        }

        /// <summary>
        /// Set the equalizer.
        /// </summary>
        /// <param name="eq">The equalizer, represented by an array of 32 adjustments in dB.</param>
        public void SetEQ(float[] eq)
        {
            _decoder.SetEQ(eq);
        }

        /// <summary>
        /// Stereo mode used in decoding.
        /// </summary>
        public StereoMode StereoMode
        {
            get { return _decoder.StereoMode; }
            set { _decoder.StereoMode = value; }
        }

        /// <summary>
        /// Read specified samples into provided buffer. Do exactly the same as <see cref="ReadSamples(float[], int, int)"/>
        /// except that the data is written in type of byte, while still representing single-precision float (in local endian).
        /// </summary>
        /// <param name="buffer">Buffer to write. Floating point data will be actually written into this byte array.</param>
        /// <param name="index">Writing offset on the destination buffer.</param>
        /// <param name="count">Length of samples to be read, in bytes.</param>
        /// <returns>Sample size actually reads, in bytes.</returns>
        public int ReadSamples(byte[] buffer, int index, int count)
        {
            if (index < 0 || index + count > buffer.Length) throw new ArgumentOutOfRangeException("index");

            // make sure we're asking for an even number of samples
            count -= (count % sizeof(float));

            return ReadSamplesImpl(buffer, index, count, 32);
        }

        /// <summary>
        /// Read specified samples into provided buffer, as PCM format.
        /// Result varies with diffirent <see cref="StereoMode"/>:
        /// <list type="bullet">
        /// <item>
        /// <description>For <see cref="OmniMusicMpeg.StereoMode.Both"/>, sample data on both two channels will occur in turn (left first).</description>
        /// </item>
        /// <item>
        /// <description>For <see cref="OmniMusicMpeg.StereoMode.LeftOnly"/> and <see cref="OmniMusicMpeg.StereoMode.RightOnly"/>, only data on
        /// specified channel will occur.</description>
        /// </item>
        /// <item>
        /// <description>For <see cref="OmniMusicMpeg.StereoMode.DownmixToMono"/>, two channels will be down-mixed into single channel.</description>
        /// </item>
        /// </list>
        /// </summary>
        /// <param name="buffer">Buffer to write.</param>
        /// <param name="index">Writing offset on the destination buffer.</param>
        /// <param name="count">Count of samples to be read.</param>
        /// <returns>Sample count actually reads.</returns>
        public int ReadSamples(float[] buffer, int index, int count)
        {
            if (index < 0 || index + count > buffer.Length) throw new ArgumentOutOfRangeException("index");

            // ReadSampleImpl "thinks" in bytes, so adjust accordingly
            return ReadSamplesImpl(buffer, index * sizeof(float), count * sizeof(float), 32) / sizeof(float);
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Read samples into the provided span. Does exactly the same as
        /// <see cref="ReadSamples(Span{float})"/> except that the data is written as bytes,
        /// while still representing single-precision float (in local endian).
        /// </summary>
        /// <param name="buffer">Buffer to write. Floating point data will be actually written into this span.</param>
        /// <returns>Sample size actually read, in bytes.</returns>
        public int ReadSamples(Span<byte> buffer) => ReadSamplesImpl(buffer);

        /// <summary>
        /// Read samples into the provided span, as PCM format.
        /// Result varies with <see cref="StereoMode"/> exactly as for
        /// <see cref="ReadSamples(float[], int, int)"/>.
        /// </summary>
        /// <param name="buffer">Buffer to write.</param>
        /// <returns>Sample count actually read.</returns>
        public int ReadSamples(Span<float> buffer)
            => ReadSamplesImpl(MemoryMarshal.AsBytes(buffer)) / sizeof(float);
#endif

        public int ReadSamplesInt16(byte[] buffer, int index, int count)
        {
            if (index < 0 || index + count > buffer.Length * sizeof(short)) throw new ArgumentOutOfRangeException("index");

            return ReadSamplesImpl(buffer, index, count, 16) * sizeof(short) / sizeof(float);
        }

        public int ReadSamplesInt8(byte[] buffer, int index, int count)
        {
            if (index < 0 || index + count > buffer.Length * sizeof(float)) throw new ArgumentOutOfRangeException("index");

            return ReadSamplesImpl(buffer, index, count, 8) * sizeof(byte) / sizeof(float);
        }

        float[] _readBuf = new float[1152 * 2];
        int _readBufLen, _readBufOfs;

        // Total logical bytes of real audio (excludes encoder delay and end padding)
        long GetTotalBytes()
        {
            var rawSampleCount = _reader.SampleCount;
            return rawSampleCount >= 0 ? (rawSampleCount - _encoderDelay - _encoderPadding) * OutputChannels * sizeof(float) : long.MaxValue;
        }

        // Decode the next frame into _readBuf. Returns false once there is nothing
        // more to deliver. Caller must hold _seekLock.
        bool TryFillReadBuffer()
        {
            while (true)
            {
                if (_eofFound)
                {
                    return false;
                }

                var frame = _reader.NextFrame();
                if (frame == null)
                {
                    _eofFound = true;
                    return false;
                }

                try
                {
                    _readBufLen = _decoder.DecodeFrame(frame, _readBuf, 0) * sizeof(float);
                    _readBufOfs = 0;

                    // Skip encoder delay samples at the start of the stream
                    if (!_decoderDelaySkipped)
                    {
                        var skipBytes = _encoderDelay * OutputChannels * sizeof(float);
                        if (skipBytes > 0 && skipBytes <= _readBufLen)
                        {
                            _readBufOfs = skipBytes;
                        }
                        _decoderDelaySkipped = true;
                        // If delay exhausted the entire buffer, mark it empty so we refill next iteration
                        if (_readBufOfs >= _readBufLen)
                        {
                            _readBufLen = _readBufOfs = 0;
                        }
                    }
                }
                catch (System.IO.InvalidDataException)
                {
                    // bad frame...  try again...
                    _decoder.Reset();
                    _readBufOfs = _readBufLen = 0;
                    continue;
                }
                catch (System.IO.EndOfStreamException)
                {
                    // no more frames
                    _eofFound = true;
                    return false;
                }
                finally
                {
                    frame.ClearBuffer();
                }

                return true;
            }
        }

        // How many bytes of decoded audio can be handed out right now: whatever is
        // buffered, clamped to the request and to the logical end of the stream.
        // Returns 0 when the buffer is empty or the stream is spent.
        int AvailableBytes(long totalBytes, int count)
        {
            if (_readBufLen <= _readBufOfs) return 0;

            var temp = _readBufLen - _readBufOfs;
            if (temp > count) temp = count;

            // Don't deliver past the logical end. totalBytes is long.MaxValue when the
            // stream's length is unknown (a non-seekable source with no VBR header), so
            // stay in long arithmetic: truncating that subtraction to int wraps negative.
            var remaining = totalBytes - _position;
            if (temp > remaining) temp = (int)remaining;

            return temp;
        }

        // Book-keeping after `temp` bytes have been copied out of _readBuf.
        void AdvanceReadBuffer(int temp)
        {
            _position += temp;
            _readBufOfs += temp;

            // finally, mark the buffer as empty if we've read everything in it
            if (_readBufOfs == _readBufLen)
            {
                _readBufLen = 0;
            }
        }

        int ReadSamplesImpl(Array buffer, int index, int count, int bitDepth)
        {
            var cnt = 0;
            var totalBytes = GetTotalBytes();

            // lock around the entire read operation so seeking doesn't bork our buffers as we decode
            lock (_seekLock)
            {
                while (count > 0)
                {
                    // Trim end padding: stop once we've delivered all real audio
                    if (_position >= totalBytes)
                    {
                        _eofFound = true;
                        break;
                    }

                    var temp = AvailableBytes(totalBytes, count);
                    if (temp > 0)
                    {
                        if (bitDepth == 32)
                        {
                            Buffer.BlockCopy(_readBuf, _readBufOfs, buffer, index, temp);
                        }
                        else
                        {
                            // 8- and 16-bit output always targets a byte[] (the public
                            // ReadSamplesInt8/ReadSamplesInt16 overloads both take byte[]).
                            // Cast once and index directly; the previous Array.SetValue(object,...)
                            // calls boxed every byte written - one box per 8-bit sample, two per
                            // 16-bit sample. The arithmetic below is unchanged, so output is
                            // bit-for-bit identical.
                            var byteBuffer = (byte[])buffer;
                            var srcBase = _readBufOfs / sizeof(float);
                            var dstBase = index / sizeof(float);
                            var sampleCount = temp / sizeof(float);

                            if (bitDepth == 8)
                            {
                                for (int i = 0; i < sampleCount; i++)
                                {
                                    byteBuffer[dstBase + i] = (byte)Math.Round(127.5f * _readBuf[srcBase + i] + 127.5f);
                                }
                            }
                            else // bitDepth == 16
                            {
                                for (int i = 0; i < sampleCount; i++)
                                {
                                    var value = (int)Math.Round(32767.5f * _readBuf[srcBase + i] - 0.5f);
                                    if (value < 0)
                                    {
                                        value += 65536;
                                    }

                                    byteBuffer[2 * (dstBase + i)] = (byte)(value % 256);
                                    byteBuffer[2 * (dstBase + i) + 1] = (byte)(value / 256);
                                }
                            }
                        }

                        // now update our counters...
                        cnt += temp;

                        count -= temp;
                        index += temp;

                        AdvanceReadBuffer(temp);
                    }

                    // if the buffer is empty, try to fill it
                    //  NB: If we've already satisfied the read request, we'll still try to fill the buffer.
                    //      This ensures there's data in the pipe on the next call
                    if (_readBufLen == 0 && !TryFillReadBuffer())
                    {
                        break;
                    }
                }
            }
            return cnt;
        }

#if NET8_0_OR_GREATER
        int ReadSamplesImpl(Span<byte> buffer)
        {
            var cnt = 0;
            var totalBytes = GetTotalBytes();

            // make sure we're asking for an even number of samples
            var count = buffer.Length - (buffer.Length % sizeof(float));

            // lock around the entire read operation so seeking doesn't bork our buffers as we decode
            lock (_seekLock)
            {
                while (count > 0)
                {
                    // Trim end padding: stop once we've delivered all real audio
                    if (_position >= totalBytes)
                    {
                        _eofFound = true;
                        break;
                    }

                    var temp = AvailableBytes(totalBytes, count);
                    if (temp > 0)
                    {
                        MemoryMarshal.AsBytes(_readBuf.AsSpan()).Slice(_readBufOfs, temp).CopyTo(buffer.Slice(cnt));

                        // now update our counters...
                        cnt += temp;
                        count -= temp;

                        AdvanceReadBuffer(temp);
                    }

                    // if the buffer is empty, try to fill it
                    //  NB: If we've already satisfied the read request, we'll still try to fill the buffer.
                    //      This ensures there's data in the pipe on the next call
                    if (_readBufLen == 0 && !TryFillReadBuffer())
                    {
                        break;
                    }
                }
            }
            return cnt;
        }
#endif
    }
}

#if NET8_0_OR_GREATER
#endif

namespace OmniMusicMpeg
{
using System;
using System.Runtime.InteropServices;
    public class MpegFrameDecoder
    {
        Decoder.LayerIDecoder _layerIDecoder;
        Decoder.LayerIIDecoder _layerIIDecoder;
        Decoder.LayerIIIDecoder _layerIIIDecoder;

        float[] _eqFactors;

        // channel buffers for getting data out of the decoders...
        // we do it this way so the stereo interleaving code is in one place: DecodeFrameImpl(...)
        // if we ever add support for multi-channel, we'll have to add a pass after the initial
        //  stereo decode (since multi-channel basically uses the stereo channels as a reference)
        float[] _ch0, _ch1;

        public MpegFrameDecoder()
        {
            _ch0 = new float[1152];
            _ch1 = new float[1152];
        }

        /// <summary>
        /// Set the equalizer.
        /// </summary>
        /// <param name="eq">The equalizer, represented by an array of 32 adjustments in dB.</param>
        public void SetEQ(float[] eq)
        {
            if (eq != null)
            {
                var factors = new float[32];
                for (int i = 0; i < eq.Length; i++)
                {
                    // convert from dB -> scaling
                    factors[i] = (float)Math.Pow(2, eq[i] / 6);
                }
                _eqFactors = factors;
            }
            else
            {
                _eqFactors = null;
            }
        }

        /// <summary>
        /// Stereo mode used in decoding.
        /// </summary>
        public StereoMode StereoMode { get; set; }

        /// <summary>
        /// Decode the Mpeg frame into provided buffer. Do exactly the same as <see cref="DecodeFrame(IMpegFrame, float[], int)"/>
        /// except that the data is written in type as byte array, while still representing single-precision float (in local endian).
        /// </summary>
        /// <param name="frame">The Mpeg frame to be decoded.</param>
        /// <param name="dest">Destination buffer. Decoded PCM (single-precision floating point array) will be written into it.</param>
        /// <param name="destOffset">Writing offset on the destination buffer.</param>
        /// <returns></returns>
        public int DecodeFrame(IMpegFrame frame, byte[] dest, int destOffset)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            if (dest == null) throw new ArgumentNullException("dest");
            if (destOffset % 4 != 0) throw new ArgumentException("Must be an even multiple of 4", "destOffset");

            var bufferAvailable = (dest.Length - destOffset) / 4;
            if (bufferAvailable < RequiredSampleCount(frame))
            {
                throw new ArgumentException("Buffer not large enough!  Must be big enough to hold the frame's entire output.  This is up to 9,216 bytes.", "dest");
            }

            return DecodeFrameImpl(frame, dest, destOffset / 4) * 4;
        }

        /// <summary>
        /// Decode the Mpeg frame into provided buffer.
        /// Result varies with different <see cref="StereoMode"/>:
        /// <list type="bullet">
        /// <item>
        /// <description>For <see cref="OmniMusicMpeg.StereoMode.Both"/>, sample data on both two channels will occur in turn (left first).</description>
        /// </item>
        /// <item>
        /// <description>For <see cref="OmniMusicMpeg.StereoMode.LeftOnly"/> and <see cref="OmniMusicMpeg.StereoMode.RightOnly"/>, only data on
        /// specified channel will occur.</description>
        /// </item>
        /// <item>
        /// <description>For <see cref="OmniMusicMpeg.StereoMode.DownmixToMono"/>, two channels will be down-mixed into single channel.</description>
        /// </item>
        /// </list>
        /// </summary>
        /// <param name="frame">The Mpeg frame to be decoded.</param>
        /// <param name="dest">Destination buffer. Decoded PCM (single-precision floating point array) will be written into it.</param>
        /// <param name="destOffset">Writing offset on the destination buffer.</param>
        /// <returns></returns>
        public int DecodeFrame(IMpegFrame frame, float[] dest, int destOffset)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            if (dest == null) throw new ArgumentNullException("dest");

            if (dest.Length - destOffset < RequiredSampleCount(frame))
            {
                throw new ArgumentException("Buffer not large enough!  Must be big enough to hold the frame's entire output.  This is up to 2,304 elements.", "dest");
            }

            return DecodeFrameImpl(frame, dest, destOffset);
        }

        // Decodes the frame into _ch0/_ch1 and returns the per-channel sample count
        // (0 if the frame's layer isn't supported). Shared by every output path so
        // they differ only in how the channel buffers are copied out.
        int DecodeToChannels(IMpegFrame frame)
        {
            frame.Reset();

            Decoder.LayerDecoderBase curDecoder = null;
            switch (frame.Layer)
            {
                case MpegLayer.LayerI:
                    if (_layerIDecoder == null)
                    {
                        _layerIDecoder = new Decoder.LayerIDecoder();
                    }
                    curDecoder = _layerIDecoder;
                    break;
                case MpegLayer.LayerII:
                    if (_layerIIDecoder == null)
                    {
                        _layerIIDecoder = new Decoder.LayerIIDecoder();
                    }
                    curDecoder = _layerIIDecoder;
                    break;
                case MpegLayer.LayerIII:
                    if (_layerIIIDecoder == null)
                    {
                        _layerIIIDecoder = new Decoder.LayerIIIDecoder();
                    }
                    curDecoder = _layerIIIDecoder;
                    break;
            }

            if (curDecoder == null) return 0;

            curDecoder.SetEQ(_eqFactors);
            curDecoder.StereoMode = StereoMode;

            return curDecoder.DecodeFrame(frame, _ch0, _ch1);
        }

        // True when the decoded output is a single channel: either the source is mono,
        // or the caller asked for one channel (LeftOnly / RightOnly / DownmixToMono).
        // In every one of those cases the layer decoder has already placed the single
        // channel of output in _ch0.
        bool IsSingleChannel(IMpegFrame frame)
            => frame.ChannelMode == MpegChannelMode.Mono || StereoMode != StereoMode.Both;

        int DecodeFrameImpl(IMpegFrame frame, Array dest, int destOffset)
        {
            var cnt = DecodeToChannels(frame);
            if (cnt > 0)
            {
                if (IsSingleChannel(frame))
                {
                    // Emit just that one channel rather than interleaving the (stale) _ch1.
                    Buffer.BlockCopy(_ch0, 0, dest, destOffset * sizeof(float), cnt * sizeof(float));
                }
                else if (dest is float[] floatDest)
                {
                    // Full stereo into a float[] - the dominant path (MpegFile's internal
                    // buffer and the DecodeFrame(float[]) overload). Interleave by direct
                    // element assignment instead of a four-byte Buffer.BlockCopy per sample
                    // (which cost ~2,300 method calls per frame).
                    for (int i = 0; i < cnt; i++)
                    {
                        floatDest[destOffset++] = _ch0[i];
                        floatDest[destOffset++] = _ch1[i];
                    }
                    cnt *= 2;
                }
                else
                {
                    // Full stereo into a byte[] (the DecodeFrame(byte[]) overload): we don't
                    // have a float view of dest, so copy each sample's raw bytes.
                    for (int i = 0; i < cnt; i++)
                    {
                        Buffer.BlockCopy(_ch0, i * sizeof(float), dest, destOffset * sizeof(float), sizeof(float));
                        ++destOffset;
                        Buffer.BlockCopy(_ch1, i * sizeof(float), dest, destOffset * sizeof(float), sizeof(float));
                        ++destOffset;
                    }
                    cnt *= 2;
                }

                return cnt;
            }

            return 0;
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Decode the Mpeg frame into the provided span. Does exactly the same as
        /// <see cref="DecodeFrame(IMpegFrame, Span{float})"/> except that the data is written as
        /// bytes, while still representing single-precision float (in local endian).
        /// </summary>
        /// <param name="frame">The Mpeg frame to be decoded.</param>
        /// <param name="dest">Destination span. Decoded PCM (single-precision floating point) will be written into it.</param>
        /// <returns>The number of bytes written.</returns>
        public int DecodeFrame(IMpegFrame frame, Span<byte> dest)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            if (dest.Length / sizeof(float) < RequiredSampleCount(frame))
            {
                throw new ArgumentException("Buffer not large enough!  Must be big enough to hold the frame's entire output.  This is up to 9,216 bytes.", nameof(dest));
            }

            return DecodeFrameImpl(frame, MemoryMarshal.Cast<byte, float>(dest)) * sizeof(float);
        }

        /// <summary>
        /// Decode the Mpeg frame into the provided span.
        /// Result varies with <see cref="StereoMode"/> exactly as for
        /// <see cref="DecodeFrame(IMpegFrame, float[], int)"/>.
        /// </summary>
        /// <param name="frame">The Mpeg frame to be decoded.</param>
        /// <param name="dest">Destination span. Decoded PCM (single-precision floating point) will be written into it.</param>
        /// <returns>The number of samples written.</returns>
        public int DecodeFrame(IMpegFrame frame, Span<float> dest)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            if (dest.Length < RequiredSampleCount(frame))
            {
                throw new ArgumentException("Buffer not large enough!  Must be big enough to hold the frame's entire output.  This is up to 2,304 elements.", nameof(dest));
            }

            return DecodeFrameImpl(frame, dest);
        }

        int DecodeFrameImpl(IMpegFrame frame, Span<float> dest)
        {
            var cnt = DecodeToChannels(frame);
            if (cnt == 0) return 0;

            if (IsSingleChannel(frame))
            {
                // Emit just that one channel rather than interleaving the (stale) _ch1.
                _ch0.AsSpan(0, cnt).CopyTo(dest);
                return cnt;
            }

            for (int i = 0, j = 0; i < cnt; i++)
            {
                dest[j++] = _ch0[i];
                dest[j++] = _ch1[i];
            }
            return cnt * 2;
        }
#endif

        // The worst-case output of a frame, in samples. Deliberately ignores the
        // single-channel StereoModes: a stereo frame decoded to mono needs only half
        // this, but callers sizing a buffer from the frame header alone shouldn't have
        // to know that.
        static int RequiredSampleCount(IMpegFrame frame)
            => (frame.ChannelMode == MpegChannelMode.Mono ? 1 : 2) * frame.SampleCount;

        /// <summary>
        /// Reset the decoder.
        /// </summary>
        public void Reset()
        {
            // the synthesis filters need to be cleared
            if (_layerIDecoder != null)
            {
                _layerIDecoder.ResetForSeek();
            }
            if (_layerIIDecoder != null)
            {
                _layerIIDecoder.ResetForSeek();
            }
            if (_layerIIIDecoder != null)
            {
                _layerIIIDecoder.ResetForSeek();
            }
        }
    }
}

#nullable restore
