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

namespace OmniToolbox.LocalModules;

public sealed class MusicPlayer : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "游戏内音乐播放器",
        Description = "",
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

    private const string QQ = "QQ音乐";
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

        ImGui.TextWrapped("宏命令：/omni MusicPlayer 打开/关闭悬浮窗。");

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
            began = ImGui.Begin("QQ音乐播放器###OmniMusicMain", ref open);
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
        for (var i = 0; i < tops.Length; i++)
        {
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

    private bool DrawFavoriteTab()
    {
        var changed = false;

        if (string.IsNullOrEmpty(config.QQMusicId))
        {
            ImGui.TextWrapped("收藏内容需要登录 QQ音乐。请先在上方「账号登录」里扫码登录，登录成功后这里会自动加载。");
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
            var pi = favViewIdx - 1;
            if (pi >= playlists.Count)
            {
                favViewIdx = 0;
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

    private void Draw()
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
                    url = null;
                    playError = "未登录只能获取 m4a 音源（本机播放器不支持），请扫码登录后播放";
                }
                pendingPlayUrl = url;
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
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return ((int)resp.StatusCode, Encoding.UTF8.GetString(bytes));
    }

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

        try { if (qrTexture is IDisposable old) old.Dispose(); } catch { }
        qrTexture = tex;
        qrTextureKey = pendingQrTexKey ?? string.Empty;
        pendingQrTexKey = null;
    }

    [Serializable]
    public sealed class Track
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string MediaMid { get; set; } = string.Empty;
        public long SongId { get; set; }
        public bool IsVip { get; set; }
        public string CoverUrl { get; set; } = string.Empty;
    }

    private sealed record LrcLine(double Time, string Text);
}

