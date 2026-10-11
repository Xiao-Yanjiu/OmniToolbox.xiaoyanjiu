#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI.Theme;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace OmniToolbox.LocalModules;

public sealed unsafe class GameWebBrowser : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "游戏内浏览器",
        Description = "",
        Category    = ModuleCategory.Interface,
        Author      = "小烟酒",
    };

    private const int  SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX;
        public int    ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    // 客户区原点 → 屏幕坐标。宿主是无边框**顶层**窗口，摆位要用屏幕坐标
    // （之前做成子窗口时用的是父窗口客户区坐标，两套坐标系不能混）
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    // 屏幕坐标 → 客户区坐标（把系统光标换算成 ImGui 坐标，Alt 拖动用）
    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // 键盘状态（ToUnicode 要用）与 VK→扫描码
    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // VK + 当前键盘状态 → 实际会打出来的字符（能正确处理 Shift / CapsLock / 中文键盘布局）
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
                                        [Out] char[] pwszBuff, int cchBuff, uint wFlags);

    // 取窗口的顶层祖先。前台窗口可能是 WebView2 的内部子窗口，
    // 往上找根窗口才能判断「前台是不是我们自己」
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    // 清理「上一次模块加载遗留的宿主窗口」用（见 DestroyOrphanHosts）
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    // 诊断用：鼠标**真正**下面那个窗口是谁（一击命中「点击到底进了谁的窗口」）
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    // 宿主窗口所在显示器的 DPI 缩放（把物理像素换算成页面 CSS 像素，见 HostDpr）
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // 键盘焦点操作（贴图模式下把焦点交给网页子窗口，见 WgcGiveKeyboard）
    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    // 动态改顶层窗口的 owner（贴图模式去掉 owner，原生模式加回 owner —— 见 ApplyHostGeometry）
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- 浏览器宿主窗口（**无边框顶层窗口**，owner = 游戏窗口；标题栏/边框全部由 ImGui 自绘） ----
    //
    // ★ 为什么不是 WS_CHILD 子窗口（2026-10-10 实测结论）
    //   一开始把宿主做成游戏窗口的 WS_CHILD，想让系统替我们裁剪（「网页不出游戏窗口」）。
    //   但 FFXIV 用 DXGI 的 flip model 呈现画面：swapchain 的内容会**直接覆盖**该窗口客户区里
    //   的一切 GDI 内容，子窗口画在上面也看不见。所以子窗口这条路在这个游戏里走不通。
    //   改用「无边框顶层窗口 + owner = 游戏窗口」：
    //     · owner 关系让浏览器窗口**永远盖在游戏之上**，且游戏最小化时跟着隐藏、不进任务栏；
    //     · 窗口矩形只覆盖「网页画面」那一块，标题栏留在游戏窗口上由 ImGui 画 ⇒ 互不遮挡；
    //     · 窗口整体**严格钳在游戏客户区内**，任何情况下都不允许越出游戏窗口
    //       （2026-10-10 用户要求，见 ClampInsideGame）。
    private const uint WS_POPUP        = 0x80000000;   // 无边框（没有系统标题栏）
    private const uint WS_VISIBLE      = 0x10000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;  // 不进任务栏、不进 Alt+Tab 列表
    private const int  SW_HIDE    = 0;
    private const int  SW_SHOW    = 5;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SIZE    = 0x0005;
    private const uint WM_CLOSE   = 0x0010;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    // ★ 宿主窗口归泵线程所有，而 ApplyHostGeometry 是**游戏线程**每帧调的。
    //   不加这个标志的话，SetWindowPos 会同步等窗口线程处理完（SendMessage 语义），
    //   万一泵线程正忙，游戏就会跟着卡一下。加上它 = 跨线程时改为异步投递，绝不阻塞游戏。
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private const uint SWP_SHOWWINDOW     = 0x0040;
    private const uint SWP_HIDEWINDOW     = 0x0080;
    private const uint GW_OWNER       = 0x0004;   // GetWindow：取 owner
    private const uint GW_HWNDPREV    = 0x0003;   // GetWindow：取 z 序里「在本窗口之上」的那个窗口
    private const uint GW_HWNDNEXT    = 0x0002;   // GetWindow：取 z 序里「在本窗口之下」的那个窗口
    private const int  GWLP_HWNDPARENT = -8;      // SetWindowLongPtr：顶层窗口用它改 owner
    private const uint GA_ROOT        = 0x0002;   // GetAncestor：取顶层宿主窗口

    // 「在网页画面上也能拖动窗口」用的系统级按键轮询（网页是原生窗口，
    // 鼠标压上去时游戏窗口收不到任何消息，ImGui 的点击事件完全失效）
    private const int  VK_LBUTTON = 0x01;
    private const int  VK_MENU    = 0x12;   // Alt
    private const int  HostMinW = 360, HostMinH = 240;

    // 自绘外壳尺寸（都是「未乘缩放」的基准值，用 OmniTheme.Scale 换算）
    private const float TabRowHeight    = 26f;   // 标签条高度
    // ★ 导航条高度已归零（2026-10-10）：导航按钮并进第一排、标题文字删除，第二排整排取消。
    //   保留常量（=0）是为了 ApplyPageRect / DrawChromeContent 的几何公式不用改，天然兼容。
    private const float NavRowHeight    = 0f;
    private const float FrameThickness  = 5f;    // 网页四周那圈 Omni 边框厚度
    private const float TitleBarRadius  = 9f;    // 外框圆角
    private const float EdgeGrab        = 8f;    // 边缘/四角拖拽热区宽度（比边框略宽，好抓）

    // 窗口一律钳在游戏客户区内部（不允许越出），没有额外的「留边」参数。

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint   style;
        public IntPtr lpfnWndProc;
        public int    cbClsExtra;
        public int    cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    // ★ 必须 SetLastError=true：否则失败时 Marshal.GetLastWin32Error() 恒为 0，
    //   「为什么注册失败」这条最关键的线索就丢了（本次踩过）。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height,
        IntPtr owner, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    // 自绘标题栏拖拽/缩放时把鼠标「钉」在游戏窗口上：
    // 否则光标一旦划到网页那个子窗口上方，游戏窗口就再也收不到 WM_MOUSEMOVE，
    // ImGui 会以为左键一直按着（ActiveId 永不释放）。SetCapture 必须在窗口所属线程调用，
    // 而 UiBuilder.Draw 回调本来就在游戏主线程上，正好合法。
    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    // 给宿主窗口套圆角区域（WebView2 是它的子窗口，会被一起裁掉，圆角才真的生效）
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr id);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

    private delegate IntPtr WndProcDlg(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int DefaultWinWidth  = 1100;
    private const int DefaultWinHeight = 720;

    private static string ConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncherCN", "pluginConfigs", "OmniGameWebBrowser.json");

    private Config config = new();
    private RepositoryNativeBrowser? repositoryBrowser;
    private bool RepositoryMode => config.RenderingBackend == 1;
    private string dialogKind = "", dialogWindowKey = "", dialogAddress = "", dialogName = "", dialogError = "";
    private bool dialogRequested;
    private static volatile bool browserDialogActive;

    // 一个「标签页」= 一套 Controller + CoreWebView2。
    // 同一个窗口里的多个标签共用一份 Environment（同一套 userData 目录），
    // 所以 Cookie / 登录态 / localStorage 都是共享的 —— 和真实浏览器多标签的行为一致。
    private sealed class BrowserTab
    {
        public BrowserWin? Win;

        public IntPtr Wv2Controller;
        public IntPtr Wv2Core;
        public bool   Wv2Busy;
        public bool   Wv2Closed;
        public volatile bool CloseRequested;   // 页面/内核请求关闭这个标签（由游戏线程执行）
        public volatile bool CanBack;          // 由泵线程刷新，游戏线程只读
        public volatile bool CanFwd;
        public string Title = "";
        public string Url   = "";
        public Wv2Pending? Pending;
        public IntPtr NewWindowArgs, NewWindowDeferral;

        public bool Ready => Wv2Controller != IntPtr.Zero && Wv2Core != IntPtr.Zero;
    }

    // 一个「浏览器窗口」：一个盖在游戏上的无边框宿主窗口 + 一排标签 + 全套 ImGui 自绘外壳
    private sealed class BrowserWin
    {
        public string Key      = "";     // 宏命令，窗口唯一键
        public string PageName = "";

        public bool Visible;             // false = 完全隐藏（最小化走的就是这条）
        public bool Maximized;
        public volatile bool CloseRequested;   // 泵线程只置标志，完整拆除交给游戏线程
        public volatile bool Closed;           // 窗口已整体拆除（迟到的 COM 回调据此自杀）
        public long LaunchStartMs;

        // 宿主窗口几何，单位 = ImGui 坐标（= 游戏客户区像素经过 uiScale 换算后的空间）
        public IntPtr HostHwnd;
        public float  X, Y, W, H;                 // 整个窗口（含自绘外壳）
        public float  RestoreX, RestoreY, RestoreW, RestoreH;
        // 网页画面那一块（宿主窗口真正的矩形）：由自绘外壳布局算出
        public float  PageX, PageY, PageW, PageH;
        public int    PageValid;
        public int    HostDirty;      // 需要把几何同步到宿主窗口（多次改动合并成一次）
        public int    HostGeometryPending;
        public int    HostRounded;    // 已经应用过的圆角半径，避免每帧重设窗口区域
        // 上一次真正应用到宿主窗口的**屏幕像素**矩形，用来做脏检查（顶层窗口每帧同步的开销就靠它压住）
        public int    HostX, HostY, HostW, HostH;
        public int    HostShown = 1;  // 宿主窗口当前是否可见（游戏不在前台时跟着隐掉）

        // ---- 贴图渲染（渲染自由）的抓帧状态：见文件末尾 Wgc* 区域 ----
        // CapState: 0=未启动 1=抓帧运行中 2=抓帧失败/已回退原生窗口
        public int    CapState;
        public bool   CapFailed;
        public string CapFailReason = "";            // 失败原因（直接显示在设置页，免去翻日志）
        public IntPtr CapItem, CapPool, CapSession;   // WinRT 对象（泵线程创建，游戏线程轮询取帧）
        public IntPtr CapWrtDev;                      // WinRT IDirect3DDevice（CreateFreeThreaded 真正吃的句柄）
        public int    CapW, CapH;                     // 抓帧池当前的像素尺寸
        public IntPtr CapSrv, CapSrv1, CapSrv2;       // 最近三代 SRV（延迟释放，防 GPU 还在用）
        public IntPtr CapInputHwnd;                   // WebView2 的输入子窗口（转发鼠标用）
        public int    CapMouseSends;                  // 累计向网页投递的点击次数（设置页可见，便于判断"转发有没有在跑"）
        public long   CapInputLogMs;                  // 找输入子窗口失败时的重试限流
        public long   CapPrimed;                      // 上次给 Chromium 补激活消息的时间（见 WgcPrimePage）
        public int    CapClickCount = 1;              // 本次点击的 clickCount（1=单击 2=双击；press/release 要一致）
        public float  CapCssX = -1e9f;                // 上次投给页面的 CSS 坐标（用来跳过没变化的 mouseMoved）
        public float  CapCssY = -1e9f;
        public int    CapCssMask = -1;                // 上次投给页面的按钮位掩码
        public long   CapStartMs;
        public int    CapFrames;                      // 累计取到的帧数（用于「是否有画面」判断）
        public int    CapNoFrameStreak;
        public long   CapLastLogMs;
        public readonly object CaptureLock = new();
        public int    CapRestart;                     // 1=需要重建抓帧会话（宿主窗口刚从隐藏恢复时置位）
        public int    HostZMode;                      // 已应用的 z 序意图：0=未设 1=游戏之下(贴图模式) 2=置顶(原生/回退)

        // 该窗口的全部标签共用一份 Environment
        public IntPtr Wv2Environment;
        public string UserData = "";
        public string Proxy    = "";
        public bool UseProxy;
        public bool FavoritesOpen;
        public float InputRowHeight;
        public int FavoriteOffset;
        public bool   EnvCreating;
        public bool   EnvReady;

        public readonly List<BrowserTab> Tabs = new();
        public int Active;

        // Tabs 会被两个线程碰：游戏线程（宏命令 / 自绘 UI）和 STA 泵线程（回调里补建设备）。
        // 所有增删遍历都走下面的封装，绝不裸访问这个 List。
        public readonly object TabLock = new();

        public BrowserTab[] TabsSnapshot() { lock (TabLock) return Tabs.ToArray(); }

        public int TabCount { get { lock (TabLock) return Tabs.Count; } }

        public void AddTab(BrowserTab t, bool activate)
        {
            lock (TabLock)
            {
                Tabs.Add(t);
                if (activate) Active = Tabs.Count - 1;
            }
        }

        public bool RemoveTab(BrowserTab t)
        {
            lock (TabLock)
            {
                var idx = Tabs.IndexOf(t);
                if (idx < 0) return false;
                Tabs.RemoveAt(idx);
                if (Active >= Tabs.Count) Active = Tabs.Count - 1;
                if (Active < 0) Active = 0;
                return true;
            }
        }

        public BrowserTab[] TakeAllTabs()
        {
            lock (TabLock)
            {
                var a = Tabs.ToArray();
                Tabs.Clear();
                Active = 0;
                return a;
            }
        }

        public int ActiveIndex { get { lock (TabLock) return Active; } }

        public BrowserTab? ActiveTabSafe()
        {
            lock (TabLock)
                return Active >= 0 && Active < Tabs.Count ? Tabs[Active] : null;
        }
    }

    // 一个「标签页」正在创建时的占位信息（控制器建好后就清掉）
    private sealed class Wv2Pending
    {
        public string Url = "";
    }

    private readonly Dictionary<string, BrowserWin> windows = new();
    private CancellationTokenSource? watchCts;

    private string statusMessage = "";

    // ---- ImGui 自绘外壳的拖拽/缩放状态（同一时刻只会有一个） ----
    private const int EdgeLeft = 1, EdgeRight = 2, EdgeTop = 4, EdgeBottom = 8;

    private static int     dragEdges;      // 正在拖的边（位掩码，0 = 不动边）
    private static int     dragMove;       // 1 = 正在拖动整个窗口
    private static string  dragKey = "";
    private static Vector2 dragStartMouse;
    private static float   dragX, dragY, dragW, dragH;
    private static int     dragCaptureOwned;
    private static int     dragByAlt;      // 1 = 这次拖动是「按住 Alt 在网页画面上」发起的

    // 「游戏窗口是不是最小化了」。★只用于最小化场景（2026-10-10 起）：
    // 贴图模式下宿主窗口**故意没有 owner**，不会跟着游戏一起最小化，
    // 游戏最小化时必须把网页也藏起来，否则桌面上留一块孤零零的网页窗口。
    // 普通「游戏失焦」**不再**触发隐藏（用户明确要求：切出去浏览器必须还在）。
    private static bool    gameMinimized;

    // 「客户区像素 → ImGui 坐标」的换算系数。平时两者 1:1；
    // 只有「游戏渲染分辨率 ≠ 窗口客户区尺寸」时才不是 1。
    private static float uiScaleX = 1f, uiScaleY = 1f;

    private ICommandManager? commandManager;
    private readonly HashSet<string> registeredCommands = new();

    private long focusGameUntilMs;

    public GameWebBrowser()
    {
        LoadOwnConfig();
    }

    protected override void OnEnable()
    {
        commandManager = GetService<ICommandManager>("Dalamud.Game.Command.CommandManager");
        if (commandManager == null)
            statusMessage = "命令服务获取失败，宏命令无法使用";
        else
            statusMessage = "";

        ImportConfiguredFavorites();
        config.TextureRender = true;
        texRenderOn = config.TextureRender;
        SaveOwnConfig();
        SyncCommands();

        if (RepositoryMode)
        {
            repositoryBrowser = new RepositoryNativeBrowser();
            repositoryBrowser.Configure(JsonSerializer.Serialize(config));
            repositoryBrowser.Activate();
        }

        // 外壳（标题栏 + 边框 + 按钮）全部由 ImGui 自绘，所以要挂到每帧绘制回调上。
        // 这条路径和 MusicPlayer / ToolbarIconPlus 用的完全一样。
        try
        {
            if (DalamudServices.PluginInterface != null)
                DalamudServices.PluginInterface.UiBuilder.Draw += DrawChrome;
        }
        catch (Exception e) { statusMessage = "绘制钩子挂载失败：" + e.Message; }

        watchCts = new CancellationTokenSource();
        var activationToken = watchCts.Token;
        _ = Task.Run(() => WatchLoop(activationToken));

        // 自动检测 WebView2 运行时：后台跑，延迟一点避免和模块加载抢时间。
        // 本类声明为 unsafe，而 unsafe 上下文里不允许出现 await/async（CS4004），
        // 所以这里用同步 Sleep 起延时线程，不能用 Task.Delay + await。
        _ = Task.Run(() =>
        {

            if (!RepositoryMode && !activationToken.IsCancellationRequested) { EnsureWv2Thread(); Wv2Post(() => { if (!activationToken.IsCancellationRequested && !RepositoryMode) { EnsureWv2Loader(out _); StartWv2Probe(); } }); }
        });
    }

    protected override void OnDisable()
    {
        wgcMouseOwnerKey = "";
        repositoryBrowser?.Deactivate();
        repositoryBrowser = null;
        browserDialogActive = false;
        dialogKind = "";
        try
        {
            if (DalamudServices.PluginInterface != null)
                DalamudServices.PluginInterface.UiBuilder.Draw -= DrawChrome;
        }
        catch {   }

        if (commandManager != null)
        {
            foreach (var cmd in registeredCommands.ToList())
            {
                try { commandManager.RemoveHandler(cmd); } catch {   }
            }
        }
        registeredCommands.Clear();
        commandManager = null;

        try
        {
            if (dragCaptureOwned != 0) { ReleaseCapture(); dragCaptureOwned = 0; }
            foreach (var w in windows.Values.ToList())
                Wv2CloseWin(w);
            windows.Clear();
        }
        catch {   }

        watchCts?.Cancel();
        watchCts?.Dispose();
        watchCts = null;
    }

    protected override void OnDispose()
    {
        try { OnDisable(); } catch {   }
    }

    private void LoadOwnConfig()
    {
        try
        {
            var path = ConfigFilePath;
            if (File.Exists(path))
            {
                var json     = File.ReadAllText(path);
                var loaded   = JsonSerializer.Deserialize<Config>(json);
                if (loaded != null) config = loaded;
                texRenderOn = config.TextureRender;   // 静态镜像（见 TextureModeActive）
            }
        }
        catch {   }

        config.Pages ??= new();
        config.Favorites ??= new();
        config.Favorites = config.Favorites.Where(f => f != null && TryWebUrl(f.Url, out _))
            .GroupBy(f => BookmarkKey(f.Url), StringComparer.Ordinal).Select(g => g.First()).ToList();
        if ((config.Proxy ?? "").Length == 0)
            foreach (var p in config.Pages)
            {
                var px = (p.Proxy ?? "").Trim();
                if (px.Length > 0) { config.Proxy = px; break; }
            }
        foreach (var p in config.Pages)
            if ((p.Proxy ?? "").Trim().Length > 0) { p.UseProxy = true; p.Proxy = ""; }
    }

    private void ImportConfiguredFavorites()
    {
        foreach (var page in config.Pages)
        {
            if (!TryWebUrl(page.Url, out var url) || config.Favorites.Any(f => BookmarkKey(f.Url) == url)) continue;
            config.Favorites.Add(new FavoriteEntry { Name = string.IsNullOrWhiteSpace(page.Name) ? new Uri(url).Host : page.Name.Trim(), Url = url });
        }
    }

    private void SaveOwnConfig()
    {
        try
        {
            var path = ConfigFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            });
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
            repositoryBrowser?.Configure(json);
        }
        catch (Exception e) { statusMessage = "配置保存失败：" + e.Message; }
    }

    private static string NormalizeCommand(string cmd)
    {
        var c = (cmd ?? "").Trim();
        if (c.Length == 0) return "";
        if (!c.StartsWith("/")) c = "/" + c;
        return c;
    }

    private void SyncCommands()
    {
        if (commandManager == null) return;

        var wanted = new HashSet<string>();
        foreach (var page in config.Pages)
        {
            var cmd = NormalizeCommand(page.Command);
            if (cmd.Length > 0) wanted.Add(cmd);
        }

        foreach (var cmd in registeredCommands.ToList())
        {
            if (wanted.Contains(cmd)) continue;
            try { commandManager.RemoveHandler(cmd); } catch {   }
            registeredCommands.Remove(cmd);
        }

        foreach (var cmd in wanted)
        {
            if (registeredCommands.Contains(cmd)) continue;
            var page = config.Pages.First(p => NormalizeCommand(p.Command) == cmd);
            try
            {
                var ok = commandManager.AddHandler(cmd, new CommandInfo((string command, string arguments) => TogglePage(page)));
                if (ok) registeredCommands.Add(cmd);
                else statusMessage = $"命令 {cmd} 注册失败（可能已被占用）";
            }
            catch (Exception e)
            {
                statusMessage = $"命令 {cmd} 注册异常: {e.Message}";
            }
        }
    }

    // 注意：这里刻意不碰 windows 字典。绘制回调（游戏主线程）才是唯一读写它的地方，
    // 免得两个线程同时动同一个 Dictionary 把它弄坏（关闭/超时的处理都挪到 DrawChrome 里了）。
    private void WatchLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Environment.TickCount64 < focusGameUntilMs) FocusGameNoRestore();
                Wv2ProbeRetryTick();
            }
            catch {   }

            Thread.Sleep(200);
        }
    }

    // ============================================================
    //  浏览器宿主窗口（**无边框顶层窗口**，owner = 游戏窗口）
    //  · 用顶层窗口是因为 FFXIV 的 DXGI flip model 会盖掉同窗口客户区里的子窗口
    //    （详见上面 WS_POPUP 那段注释）；
    //  · owner 关系 ⇒ 永远盖在游戏之上、随游戏一起最小化、不进任务栏；
    //  · 「网页永远不会跑到游戏窗口外面」**不是**系统保证的，而是靠两层钳位：
    //    ① ApplyDrag 拖拽/缩放时把 win.X/Y/W/H 夹在游戏客户区内；
    //    ② ApplyHostGeometry 每帧按客户区屏幕原点摆位。
    //  · 标题栏、那圈边框、最小化/最大化/关闭/前进/后退/刷新 全部由 ImGui 自绘（见 DrawChrome），
    //    系统边框一个都不用；宿主窗口**只覆盖网页画面那块**，标题栏留在游戏窗口上，互不遮挡；
    //  · 窗口矩形每帧由游戏线程同步（ApplyHostGeometry 内部有脏检查），
    //    创建/销毁仍走泵线程；
    //  · WndProc 是原生回调，里面任何异常都不能逸出（逸出 = 进程终止），全部吞掉。
    // ============================================================

    private const string HostClassName = "FFXIV_GWB_HostWnd";
    private static string hostClassName = HostClassName;   // 实际使用的类名（冲突时会换成唯一名）
    private static ushort hostClassAtom;

    // 静态持有 WndProc 委托：GC 一旦回收，原生回调 = 野指针，进程直接崩
    private static readonly WndProcDlg HostWndProcThunk = HostWndProc;
    private static readonly Dictionary<IntPtr, BrowserWin> hostByHwnd = new();

    // ★★ 窗口类注册（2026-10-10 实机「不显示网页」的真因）
    //
    // 现象：模块重载后，日志固定出现 `RegisterClassW 失败 winerr=0`，
    //       于是每次 CreateHostWindow 都在第一步就放弃，网页永远不显示。
    //
    // 真因：窗口类一旦注册，**在整个进程生命周期内都有效**（除非显式 UnregisterClass）。
    //       TreeHouse 重载模块 = 换一个新程序集，我们这些静态字段会被重置为 0，
    //       于是 EnsureHostClass 又拿**同一个类名**去注册 —— 而进程里上一版注册的那个类
    //       还活着，重名必然失败（ERROR_CLASS_ALREADY_EXISTS = 1410）。
    //       同一模块实例内反复开新页面不会触发（hostClassAtom != 0 直接复用），
    //       所以现象是「第一次能用，重载后就打不开」。
    //       winerr 显示 0 是因为 RegisterClassW 的 P/Invoke 原先漏了 SetLastError，
    //       拿不到真实错误码（已补）。
    //
    // 对策：固定类名失败时，改用「进程内唯一」的类名再注册一次。
    //       重复注册只是多几个无用的窗口类，Windows 在进程退出时统一回收，无害。
    private static void EnsureHostClass()
    {
        if (hostClassAtom != 0) return;

        hostClassAtom = RegisterHostClass(hostClassName);
        if (hostClassAtom != 0) return;

        var err = Marshal.GetLastWin32Error();
        hostClassName = HostClassName + "_" + Environment.TickCount64.ToString("X");
        hostClassAtom = RegisterHostClass(hostClassName);
        Wv2Log($"RegisterClassW 固定类名失败 winerr={err}（1410=类已存在）；" +
               $"改用唯一类名 {hostClassName} -> atom={hostClassAtom}");
    }

    private static ushort RegisterHostClass(string name)
    {
        var wc = new WNDCLASSW
        {
            style         = 0,
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(HostWndProcThunk),
            hInstance     = GetModuleHandleW(null),
            hCursor       = LoadCursorW(IntPtr.Zero, (IntPtr)32512),   // IDC_ARROW
            hbrBackground = GetStockObject(4),                          // BLACK_BRUSH：网页加载前不闪白
            lpszClassName = name,
        };
        return RegisterClassW(ref wc);
    }

    // ★ 清理「上一次模块加载遗留的宿主窗口」
    //
    // 为什么需要：Wv2CloseWin 里销毁宿主窗口是**投递到泵线程**做的，而 OnDisable 紧接着就
    // Cancel 了泵线程，存在「来不及销毁」的窗口期。顶层窗口来不及销毁比子窗口严重得多：
    //   · 它会变成一块浮在游戏上的空窗口（用户会看到莫名其妙的黑方块）；
    //   · 它的 WndProc 指向**已经卸载的程序集** —— 系统再给它发一条消息就是进程崩溃。
    // 所以每次创建新宿主窗口之前，把「owner 是游戏窗口 + 类名带我们前缀」的窗口先全清掉。
    private static readonly EnumWindowsProc HostOrphanScanThunk = HostOrphanScan;

    private static void DestroyOrphanHosts(IntPtr game, IntPtr keep)
    {
        if (game == IntPtr.Zero) return;
        try { EnumWindows(HostOrphanScanThunk, keep); }
        catch {   }
    }

    private static bool HostOrphanScan(IntPtr h, IntPtr lParam)
    {
        // 原生回调：任何异常都不能逸出（逸出 = 进程终止）
        try
        {
            if (h == IntPtr.Zero || h == lParam || !IsWindow(h)) return true;
            // ★ 本会话仍然登记在册的宿主窗口**不是孤儿**（典型场景：正在开第二个浏览器窗口，
            //   此时新窗口的 HostHwnd 还是 0，keep 挡不住旧窗口）。只有「上次模块加载遗留、
            //   没人认领」的窗口（hostByHwnd 在模块重载后已清空）才需要清理。
            if (hostByHwnd.ContainsKey(h)) return true;

            // 类名是我们独有的前缀 —— 这是最可靠的判据。
            // 不再附加「owner 必须是游戏窗口」：贴图模式下宿主窗口**故意没有 owner**
            // （被拥有的窗口永远在 owner 之上，藏不到游戏背后），加了那条判断就漏掉这一批。
            // 调用点在 CreateHostWindow 建新窗口**之前**，此刻窗口表里没有的同类窗口必是遗留物。
            var sb = new System.Text.StringBuilder(80);
            if (GetClassNameW(h, sb, sb.Capacity) <= 0) return true;
            var cn = sb.ToString();
            if (!cn.StartsWith(HostClassName, StringComparison.Ordinal)) return true;

            Wv2Log($"清理上一次遗留的宿主窗口 hwnd=0x{h:X} class={cn}");
            DestroyWindow(h);
        }
        catch {   }
        return true;
    }

    // ---- 自绘外壳的尺寸（全部走 Omni 的缩放，跟着用户的界面缩放走） ----
    private static float OmniScale(float v)
    {
        try { var r = OmniTheme.Scale(v); return r > 0.01f ? r : v; }
        catch { return v; }
    }

    private static float MinWinW    => OmniScale(HostMinW);
    private static float MinWinH    => OmniScale(HostMinH);
    private static float ChromePad  => OmniScale(FrameThickness);   // 网页四周那圈边框厚度
    private static float TabRowH    => OmniScale(TabRowHeight);
    private static float NavRowH    => OmniScale(NavRowHeight);
    private static float FrameGrab  => OmniScale(EdgeGrab);

    // 一次取齐所有外壳用色。万一 Omni 主题还没初始化好，用一组安全兜底色，
    // 保证外壳永远画得出来（不至于因为取色失败整个浏览器变瞎）。
    private struct ChromeSkin
    {
        public Vector4 Background, Surface, Text, Border, Accent, Error, Shadow, Hover, Active, AccentCtl;
        public float   Radius, BorderThickness, HighlightStrength;
    }

    private static ChromeSkin GetSkin()
    {
        var s = new ChromeSkin
        {
            Background      = new Vector4(0.09f, 0.10f, 0.12f, 1f),
            Surface         = new Vector4(0.14f, 0.15f, 0.18f, 1f),
            Text            = new Vector4(0.92f, 0.93f, 0.95f, 1f),
            Border          = new Vector4(0.28f, 0.30f, 0.34f, 1f),
            Accent          = new Vector4(0.26f, 0.56f, 0.94f, 1f),
            Error           = new Vector4(0.90f, 0.30f, 0.30f, 1f),
            Shadow          = new Vector4(0f, 0f, 0f, 0.45f),
            Hover           = new Vector4(0.22f, 0.24f, 0.28f, 1f),
            Active          = new Vector4(0.30f, 0.33f, 0.38f, 1f),
            AccentCtl       = new Vector4(0.26f, 0.56f, 0.94f, 1f),
            Radius          = 8f,
            BorderThickness = 1f,
            HighlightStrength = 0.10f,
        };

        try
        {
            var t = OmniTheme.Tokens;
            if (t.Text.W > 0.05f)
            {
                s.Background = t.Background;
                s.Surface    = t.Surface;
                s.Text       = t.Text;
                s.Border     = t.Border;
                s.Accent     = t.Accent;
                s.Error      = t.Error;
                if (t.Shadow.W > 0.001f) s.Shadow = t.Shadow;
                s.Hover      = OmniTheme.HoverBackground;
                s.Active     = OmniTheme.ActiveBackground;
                s.AccentCtl  = OmniTheme.ControlAccent;
                if (t.BorderRadius > 1f)      s.Radius          = t.BorderRadius;
                if (t.BorderThickness > 0.4f) s.BorderThickness = t.BorderThickness;
                s.HighlightStrength = t.HighlightStrength;
            }
        }
        catch {   }

        return s;
    }

    // 首次打开时给窗口一个合理的初始矩形：优先用记住的位置/大小，否则在游戏客户区里居中。
    // ★ 必须是非静态：它读实例字段 config。它只在游戏线程的 DrawOneWindow 里被调用，
    //   内部用 ImGui.GetIO() 也只有在游戏线程才合法，所以实例方法是对的。
    private void InitWinRect(BrowserWin win)
    {
        var io = ImGui.GetIO();
        var cw = MathF.Max(MinWinW + 80f, io.DisplaySize.X > 1f ? io.DisplaySize.X : 1280f);
        var ch = MathF.Max(MinWinH + 80f, io.DisplaySize.Y > 1f ? io.DisplaySize.Y : 720f);

        var w = config.WinWidth  > 0 ? config.WinWidth  : OmniScale(DefaultWinWidth);
        var h = config.WinHeight > 0 ? config.WinHeight : OmniScale(DefaultWinHeight);
        w = Math.Clamp(w, MinWinW, cw);
        h = Math.Clamp(h, MinWinH, ch);

        var x = config.WinX == int.MinValue ? (cw - w) * 0.5f : config.WinX;
        var y = config.WinY == int.MinValue ? (ch - h) * 0.5f : config.WinY;

        win.X = Math.Clamp(x, 0f, MathF.Max(0f, cw - w));
        win.Y = Math.Clamp(y, 0f, MathF.Max(0f, ch - h));
        win.W = w;
        win.H = h;
        win.RestoreX = win.X; win.RestoreY = win.Y;
        win.RestoreW = win.W; win.RestoreH = win.H;
        // Do not overwrite the window's explicit direct/proxy choice during initial geometry setup.
    }

    // 游戏客户区左上角在**屏幕**上的坐标（顶层宿主窗口摆位要以它为原点）。
    // 注意别和 ImGui 坐标混：这里拿到的才是像素。
    private static bool GetGameClientOrigin(out int ox, out int oy)
    {
        ox = oy = 0;
        var game = FindGameHwnd();
        if (game == IntPtr.Zero) return false;
        var pt = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(game, ref pt)) return false;
        ox = pt.X;
        oy = pt.Y;
        return true;
    }

    // 自绘外壳的布局：由整个窗口矩形算出「网页画面」那一块的矩形。
    // 这块矩形就是宿主窗口的矩形 —— 标题栏不在其中，所以两者永远不会互相遮挡。
    private static void ApplyPageRect(BrowserWin win)
    {
        var pad = ChromePad;
        var px  = win.X + pad;
        var py  = win.Y + pad + TabRowH + NavRowH;
        var pw  = MathF.Max(60f, win.W - pad * 2f - FavoritePanelWidth(win));
        var ph  = MathF.Max(60f, win.H - pad * 2f - TabRowH - NavRowH);

        if (MathF.Abs(win.PageX - px) < 0.5f && MathF.Abs(win.PageY - py) < 0.5f &&
            MathF.Abs(win.PageW - pw) < 0.5f && MathF.Abs(win.PageH - ph) < 0.5f) return;

        win.PageX = px; win.PageY = py; win.PageW = pw; win.PageH = ph;
        win.PageValid = 1;
        // 这里不再投递泵线程任务：DrawOneWindow 每帧都会调 ApplyHostGeometry（自带脏检查），
        // 既省一次跨线程投递，拖拽时也没有「画面比鼠标慢一帧」的延迟。
    }

    private void CreateHostWindow(BrowserWin win)
    {
        EnsureHostClass();
        if (hostClassAtom == 0) { Wv2FailWin(win, "宿主窗口类注册失败，无法显示网页"); return; }

        var game = FindGameHwnd();
        if (game == IntPtr.Zero) { Wv2FailWin(win, "未找到游戏窗口"); return; }

        // 先清掉上一次模块加载没能销毁的孤儿宿主窗口（防浮窗 + 防悬垂 WndProc 崩溃）
        DestroyOrphanHosts(game, win.HostHwnd);

        // ★ 本方法跑在泵线程上，绝对不能碰 ImGui（GetIO 是线程绑定的，跨线程 = 原生崩溃）。
        //   正常情况下自绘外壳已经算过几何（PageValid=1）；没算过就给一个保守值，
        //   游戏线程下一帧会用真实尺寸把它纠正过来。
        if (win.W <= 1f || win.H <= 1f)
        {
            win.W = MathF.Max(MinWinW, OmniScale(DefaultWinWidth));
            win.H = MathF.Max(MinWinH, OmniScale(DefaultWinHeight));
            win.X = 40f; win.Y = 40f;
            win.RestoreX = win.X; win.RestoreY = win.Y;
            win.RestoreW = win.W; win.RestoreH = win.H;
        }
        if (win.PageValid == 0) ApplyPageRect(win);

        if (!GetGameClientOrigin(out var ox, out var oy))
        {
            Wv2FailWin(win, "无法定位游戏窗口客户区，无法显示网页");
            return;
        }

        // 自绘布局算出来的是 ImGui 坐标，顶层窗口要的是**屏幕像素**
        var sx = uiScaleX > 0.05f ? uiScaleX : 1f;
        var sy = uiScaleY > 0.05f ? uiScaleY : 1f;

        var px = ox + (int)(win.PageX / sx);
        var py = oy + (int)(win.PageY / sy);
        var pw = (int)MathF.Max(60f, win.PageW / sx);
        var ph = (int)MathF.Max(60f, win.PageH / sy);

        // owner = 游戏窗口 ⇒ 永远盖在游戏之上、随游戏最小化、不进任务栏（配合 WS_EX_TOOLWINDOW）
        // ★ 贴图模式下必须**不要 owner**：被拥有的窗口永远在 owner 之上，就没法把原生画面藏到游戏背后。
        var owner = TextureModeActive(win) ? IntPtr.Zero : game;
        var hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, hostClassName, win.PageName,
                                   WS_POPUP | WS_VISIBLE,
                                   px, py, pw, ph,
                                   owner, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Wv2Log($"CreateWindowExW 失败 winerr={Marshal.GetLastWin32Error()}");
            Wv2FailWin(win, "宿主窗口创建失败，无法显示网页");
            return;
        }

        hostByHwnd[hwnd]    = win;
        win.HostHwnd        = hwnd;
        win.HostDirty       = 0;      // 创建时就已经落在正确矩形上，别再留一个「待同步」标记把后续同步堵死
        win.HostRounded     = 0;
        win.HostX = px; win.HostY = py; win.HostW = pw; win.HostH = ph;
        ApplyHostGeometry(win);
        // 注意：日志里之前打的是 game（让人误以为贴图模式也带着 owner），改成打真正的 owner
        Wv2Log($"宿主顶层窗口已创建 hwnd=0x{hwnd:X} 屏幕=({px},{py}) {pw}x{ph} owner=0x{owner:X}（游戏=0x{game:X}）");
    }

    // 几何变了：只记一个标记，真正的 SetWindowPos 由泵线程做（子窗口属于泵线程）
    private static void MarkHostDirty(BrowserWin win)
    {
        if (Interlocked.Exchange(ref win.HostDirty, 1) == 1) return;
        Wv2Post(() =>
        {
            // 先清标记再应用：应用期间新来的改动会再投递一次，绝不会丢
            Interlocked.Exchange(ref win.HostDirty, 0);
            ApplyHostGeometry(win);
        });
    }

    // 把「网页画面」那一块的矩形同步到宿主窗口，并让 WebView2 重新铺满客户区。
    //
    // 宿主是无边框**顶层**窗口 ⇒ 目标是**屏幕坐标** = 游戏客户区屏幕原点 + 客户区内的偏移。
    // 本方法只调用跨线程安全的 API（ClientToScreen / SetWindowRgn / SetWindowPos），
    // 所以游戏线程每帧都可以直接调（拖拽时画面零延迟），泵线程也能调。
    // ★ 唯独 put_Bounds 例外：WebView2 的对象都是 STA 的，从游戏线程直接调它的 vtable
    //   会被 WebView2 拒绝（日志里那上千条 hr=0x802A000C 就是这么来的），必须回泵线程执行。
    // 脏检查保证几何没变时不会每帧白调 SetWindowPos。
    private static void ApplyHostGeometry(BrowserWin win)
    {
        if (Interlocked.Exchange(ref win.HostGeometryPending, 1) != 0) return;
        Wv2Post(() =>
        {
            try
            {
                if (win.Visible && !win.Closed && !gameMinimized) ApplyHostGeometryCore(win);
            }
            finally { Interlocked.Exchange(ref win.HostGeometryPending, 0); }
        });
    }

    // 所有窗口修改在 HWND 所属的 STA 线程执行，避免跨线程窗口消息重入。
    private static void ApplyHostGeometryCore(BrowserWin win)
    {

        var host = win.HostHwnd;
        if (host == IntPtr.Zero || !IsWindow(host)) return;
        if (win.PageValid == 0) return;
        if (!GetGameClientOrigin(out var ox, out var oy)) return;

        var sx = uiScaleX > 0.05f ? uiScaleX : 1f;
        var sy = uiScaleY > 0.05f ? uiScaleY : 1f;

        var pw = (int)MathF.Max(60f, win.PageW / sx);
        var ph = (int)MathF.Max(60f, win.PageH / sy);
        var px = ox + (int)(win.PageX / sx);
        var py = oy + (int)(win.PageY / sy);

        // 尺寸变了必须重设圆角区域（SetWindowRgn 用的是窗口自身的 0,0 坐标系）
        if (win.HostW != pw || win.HostH != ph) win.HostRounded = 0;

        // 圆角：SetWindowRgn 对顶层窗口同样有效；WebView2 是它的子窗口，会被一起裁掉
        var r = (int)MathF.Round(OmniScale(MathF.Max(3f, TitleBarRadius - FrameThickness)));
        if (r < 0) r = 0;
        if (win.HostRounded != r)
        {
            var rgn = CreateRoundRectRgn(0, 0, pw + 1, ph + 1, r * 2, r * 2);
            if (rgn != IntPtr.Zero)
            {
                if (SetWindowRgn(host, rgn, true) == 0) { try { DeleteObject(rgn); } catch {   } }
                else win.HostRounded = r;
            }
        }

        var texMode = TextureModeActive(win);
        var zMode   = texMode ? 1 : 2;

        var game = FindGameHwnd();

        // ★ z 序自愈：贴图模式下宿主必须**紧贴游戏之下**。任何外力把它顶到游戏之上
        //   （Chromium 处理激活消息时把自己置顶、别的插件置顶窗口、Alt+Tab 的副作用……）
        //   都会让原生画面冒出来盖住游戏；而几何脏检查只看矩形，会认为「什么都没变」不去压回来。
        //   把「宿主上面那个窗口是不是游戏」也纳入脏检查 ⇒ 每帧自愈，最多差一帧。
        var zOk = !texMode || game == IntPtr.Zero || GetWindow(host, GW_HWNDPREV) == game;

        if (win.HostX == px && win.HostY == py && win.HostW == pw && win.HostH == ph &&
            win.HostShown != 0 && win.HostZMode == zMode && zOk) return;

        // SWP_ASYNCWINDOWPOS：本方法在游戏线程跑，窗口却属于泵线程，异步投递才不会拖住游戏
        var flags = SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS;
        var wasHidden = win.HostShown == 0;
        flags |= SWP_SHOWWINDOW;

        // ★ 贴图模式：把宿主窗口插到游戏窗口「之后」（= z 序更低、被游戏完全遮住），
        //   用户看不到原生画面，而截帧不受遮挡影响（WGC 抓的就是它自己的内容）。
        //   原生模式/回退：置顶，保持老行为（原生画面盖在游戏之上）。

        // owner 也必须跟着模式切：被拥有的窗口永远在 owner 之上，贴图模式下留着 owner
        // 就永远藏不到游戏背后。用 SetWindowLongPtr 动态改而不是重建窗口 ——
        // 重建宿主窗口会连带拆掉 WebView2 的控制器（要重新初始化内核）。
        // 放在脏检查之后：模式切换时 HostZMode 与 zMode 必然不等，一定会走到这里。
        if (game != IntPtr.Zero)
        {
            var wantOwner = texMode ? IntPtr.Zero : game;
            if (GetWindow(host, GW_OWNER) != wantOwner)
            {
                try { SetWindowLongPtrW(host, GWLP_HWNDPARENT, wantOwner); }
                catch {   }
            }
        }

        if (zMode == 1 && game != IntPtr.Zero)
        {
            SetWindowPos(host, game, px, py, pw, ph, flags);
        }
        else if (GetWindow(host, GW_OWNER) != IntPtr.Zero)
        {
            // HWND_TOP：从游戏后面恢复时必须显式提升，设置 owner 并不能替代 z 序更新。
            SetWindowPos(host, IntPtr.Zero, px, py, pw, ph, flags);
        }
        else
        {
            SetWindowPos(host, IntPtr.Zero, px, py, pw, ph, flags);   // 无 owner（贴图模式退回来的）⇒ 显式置顶
        }
        win.HostX = px; win.HostY = py; win.HostW = pw; win.HostH = ph;
        win.HostShown = 1;
        win.HostZMode = zMode;

        // 宿主窗口刚从隐藏恢复：窗口隐藏期间 WGC 可能已经停止交付画面且不会自愈，
        // 置一个重启标记，由游戏线程转交泵线程重建会话（几毫秒），保证画面立刻跟上。
        if (wasHidden && texMode) win.CapRestart = 1;

        // put_Bounds 回泵线程执行（见方法头注释）。SetWindowPos 是异步投递、
        // 这一条只是入队，两者都不会拖住游戏线程。
        var tabsNow = win.TabsSnapshot();
        if (tabsNow.Length > 0)
            Wv2Post(() => { foreach (var t in tabsNow) PutBoundsToClientNow(t); });
    }

    // 把宿主窗口藏起来（不销毁）。游戏不在前台、或窗口被宏命令收起时用，
    // 这样「ImGui 外壳」和「原生网页」的显隐永远一致，不会出现半截画面浮在游戏上。
    private static void HideHost(BrowserWin win)
    {
        var host = win.HostHwnd;
        if (host == IntPtr.Zero || !IsWindow(host)) return;
        if (win.HostShown == 0) return;

        win.HostShown = 0;
        try
        {
            SetWindowPos(host, IntPtr.Zero, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE |
                         SWP_ASYNCWINDOWPOS | SWP_HIDEWINDOW);
        }
        catch {   }
    }

    // 「游戏窗口是不是最小化了」。每帧一次（FindWindowW 很便宜）。
    // ★ 2026-10-10 起只判 IsIconic：前台与否不再影响浏览器可见性（用户要求失焦不隐藏）。
    private void CheckGameForeground()
    {
        var game = FindGameHwnd();
        gameMinimized = game != IntPtr.Zero && IsIconic(game);
    }

    private static IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            BrowserWin? win = null;
            hostByHwnd.TryGetValue(hWnd, out win);

            switch (msg)
            {
                case WM_SIZE:
                    // wParam==SIZE_MINIMIZED(1) 时不重排，其余把网页铺满客户区
                    if (win != null && wParam != (IntPtr)1)
                        foreach (var t in win.TabsSnapshot()) PutBoundsToClientNow(t);
                    return IntPtr.Zero;

                case WM_CLOSE:
                    // 自绘标题栏上没有系统 X，这条只作为兜底（例如外部 DestroyWindow）
                    if (win != null) win.CloseRequested = true;
                    return IntPtr.Zero;

                case WM_DESTROY:
                    hostByHwnd.Remove(hWnd);
                    if (win != null) win.HostHwnd = IntPtr.Zero;
                    return IntPtr.Zero;
            }
        }
        catch {   }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // 游戏线程：刷新游戏客户区尺寸 + 「客户区像素 ↔ ImGui 坐标」换算系数。
    // 平时两者就是 1:1；只有「游戏渲染分辨率 ≠ 窗口客户区尺寸」时才会不是 1，
    // 这时候自绘外壳依旧能严丝合缝地套在网页外面。
    private void RefreshUiMetrics()
    {
        var game = FindGameHwnd();
        if (game == IntPtr.Zero) return;
        if (!GetClientRect(game, out var cr)) return;
        if (cr.Right <= 0 || cr.Bottom <= 0) return;

        var io = ImGui.GetIO();
        var dw = io.DisplaySize.X > 1f ? io.DisplaySize.X : cr.Right;
        var dh = io.DisplaySize.Y > 1f ? io.DisplaySize.Y : cr.Bottom;
        uiScaleX = dw / cr.Right;
        uiScaleY = dh / cr.Bottom;
        if (uiScaleX <= 0.05f || uiScaleX > 8f) uiScaleX = 1f;
        if (uiScaleY <= 0.05f || uiScaleY > 8f) uiScaleY = 1f;
    }

    // 拖完 / 改完大小：把几何记进配置（最大化期间不记，免得把最大化尺寸当成默认值）
    private void SaveGeometryNow(BrowserWin win)
    {
        if (win.Maximized) return;
        config.WinX      = (int)win.X;
        config.WinY      = (int)win.Y;
        config.WinWidth  = (int)win.W;
        config.WinHeight = (int)win.H;
        SaveOwnConfig();
    }

    // 把某个窗口重新拉出来（宏命令再次按下时）
    private void ShowWinAtGame(BrowserWin win)
    {
        win.Visible = true;
        win.Closed  = false;

        // 宿主窗口的创建/显示走泵线程（窗口归属泵线程，显示时统一处理）。
        // 首次打开时宿主窗口还不存在（由控制器创建流程顺带建出来），这一投递只会摆个位置。
        Wv2Post(() =>
        {
            if (win.Closed) return;
            try
            {
                if (win.HostHwnd != IntPtr.Zero && IsWindow(win.HostHwnd))
                    ShowWindow(win.HostHwnd, SW_SHOW);
            }
            catch {   }
            win.HostShown = 1;
            ApplyHostGeometry(win);
        });

        // 只让活动标签可见，其余标签保持隐藏
        Wv2ApplyActiveTab(win);
    }

    private static IntPtr FindGameHwnd() => FindWindowW("FFXIVGAME", null);

    private static void FocusGame() => FocusGameCore(true);

    private static void FocusGameNoRestore() => FocusGameCore(false);

    private static void FocusGameCore(bool restoreIfMinimized)
    {
        var g = FindGameHwnd();
        if (g == IntPtr.Zero) return;
        try
        {
            // 游戏已最小化时：需要恢复的场景（关闭/隐藏浏览器后）先恢复；否则直接不动作，
            // 免得持续抢焦点反过来干扰用户手动最小化游戏
            if (IsIconic(g))
            {
                if (!restoreIfMinimized) return;
                ShowWindow(g, SW_RESTORE);
            }
            var fore = GetForegroundWindow();
            var foreThread = fore != IntPtr.Zero ? GetWindowThreadProcessId(fore, out _) : 0;
            var cur = GetCurrentThreadId();
            var attached = foreThread != 0 && foreThread != cur && AttachThreadInput(cur, foreThread, true);
            try
            {
                BringWindowToTop(g);
                SetForegroundWindow(g);
            }
            finally
            {
                if (attached) AttachThreadInput(cur, foreThread, false);
            }
        }
        catch {   }
    }

    private static void DrawPlaceholder(string hint)
    {
        var mn = ImGui.GetItemRectMin();
        var mx = ImGui.GetItemRectMax();
        var fs = ImGui.GetFontSize();
        var ty = mn.Y + (mx.Y - mn.Y - fs) * 0.5f;
        ImGui.GetWindowDrawList().AddText(
            new System.Numerics.Vector2(mn.X + ImGui.GetStyle().FramePadding.X, ty),
            ImGui.GetColorU32(ImGuiCol.TextDisabled), hint);
    }

    private void TogglePage(PageEntry page)
    {
        if (RepositoryMode)
        {
            if (!TryWebUrl(page.Url, out var url)) { statusMessage = "请输入有效的网页地址。"; return; }
            if (!TryProxy(page.UseProxy, config.Proxy, out _, out var error)) { statusMessage = error; return; }
            page.Url = url;
            repositoryBrowser?.Configure(JsonSerializer.Serialize(config));
            repositoryBrowser?.Toggle(JsonSerializer.Serialize(page));
            return;
        }
        var key = NormalizeCommand(page.Command);
        if (key.Length == 0) return;

        windows.TryGetValue(key, out var win);

        try
        {
            // 已经开着的窗口：这一下就是「显示 / 完全隐藏」的开关。
            // 「最小化」走的也是这条 —— 隐藏之后游戏里不留任何东西，也没有任务栏按钮。
            if (win != null && win.Visible)
            {
                win.Visible = false;
                Wv2HideWin(win);
                statusMessage = "已隐藏，再按一次宏命令即可重新显示";
                return;
            }

            var url = NormalizeUrl(page.Url);
            if (url.Length == 0 && (win == null || win.TabCount == 0))
            {
                statusMessage = "该网页的网址为空，请先在设置里填写";
                return;
            }

            if (win == null)
            {
                win = new BrowserWin { Key = key, PageName = page.Name ?? "" };
                windows[key] = win;
            }

            win.PageName  = page.Name ?? "";
            win.Maximized = false;

            if (win.TabCount == 0)
            {
                if (!TryProxy(page.UseProxy, config.Proxy, out var proxy, out var error))
                { statusMessage = error; return; }
                win.UseProxy = page.UseProxy;
                win.Proxy = proxy;
                win.UserData = ProfileForProxy(key, proxy);
                ShowWinAtGame(win);
                OpenTab(win, url, true);
            }
            else
            {
                ShowWinAtGame(win);
            }

            FocusGame();
        }
        catch (Exception e) { statusMessage = $"操作失败: {e.Message}"; }
    }

    private static string NormalizeUrl(string? raw)
    {
        return TryWebUrl(raw, out var normalized) ? normalized : "";
    }

    private static bool TryWebUrl(string? input, out string normalized)
    {
        normalized = "";
        var text = (input ?? "").Trim();
        if (text.Length == 0 || text.Any(char.IsControl)) return false;
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.Host.Length == 0 || uri.UserInfo.Length != 0) return false;
        normalized = uri.AbsoluteUri;
        return true;
    }
    private static string BookmarkKey(string url) => TryWebUrl(url, out var normalized) ? normalized : url;
    private static bool TryProxy(bool enabled, string? input, out string proxy, out string error)
    {
        proxy = ""; error = "";
        if (!enabled) return true;
        var text = (input ?? "").Trim();
        if (text.Length == 0) { error = "请先在模块设置的网络代理中填写地址。"; return false; }
        if (text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '"'))
        { error = "代理地址不能包含空白、引号或附加启动参数。"; return false; }
        if (!text.Contains("://")) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks4" or "socks5") ||
            uri.Host.Length == 0 || uri.Port < 1 || uri.Port > 65535 || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        { error = "代理格式不正确。示例：127.0.0.1:7890 或 socks5://127.0.0.1:1080；不支持在地址中填写账号密码。"; return false; }
        proxy = uri.Scheme + "://" + uri.Authority;
        return true;
    }
    private static string ProfileForProxy(string key, string proxy)
    {
        if (proxy.Length == 0) return Wv2ProfileDir(key);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(proxy))).Substring(0, 16);
        return Path.Combine(Wv2ProfileDir(key), "proxy-" + hash);
    }
    private void ChangeWindowProxy(BrowserWin old, bool enabled)
    {
        if (!TryProxy(enabled, config.Proxy, out var proxy, out var error)) { statusMessage = error; return; }
        if (old.UseProxy == enabled && old.Proxy == proxy) return;
        var tabs = old.TabsSnapshot();
        var active = old.ActiveIndex;
        var replacement = new BrowserWin
        {
            Key = old.Key, PageName = old.PageName, UseProxy = enabled, Proxy = proxy,
            UserData = ProfileForProxy(old.Key, proxy), Visible = old.Visible, Maximized = old.Maximized,
            X = old.X, Y = old.Y, W = old.W, H = old.H,
            RestoreX = old.RestoreX, RestoreY = old.RestoreY, RestoreW = old.RestoreW, RestoreH = old.RestoreH,
            FavoritesOpen = old.FavoritesOpen, FavoriteOffset = old.FavoriteOffset,
        };
        Wv2CloseWin(old); // mark old callbacks stale; close work precedes creation on the STA queue
        windows[old.Key] = replacement;
        foreach (var tab in tabs)
        {
            var url = NormalizeUrl(tab.Url);
            if (url.Length == 0) continue;
            OpenTab(replacement, url, false);
        }
        replacement.Active = Math.Clamp(active, 0, Math.Max(0, replacement.TabCount - 1));
        var page = config.Pages.FirstOrDefault(p => NormalizeCommand(p.Command) == old.Key);
        if (page != null) page.UseProxy = enabled;
        SaveOwnConfig();
        statusMessage = enabled ? "已切换到代理，正在重新加载标签页。" : "已关闭代理，正在重新加载标签页。";
    }
    private static float FavoritePanelWidth(BrowserWin win) => win.FavoritesOpen ? MathF.Min(OmniScale(260), MathF.Max(120, win.W * 0.42f)) : 0;
    private void AddFavorite(BrowserTab? tab)
    {
        if (tab == null || !TryWebUrl(tab.Url, out var url)) { statusMessage = "当前页面没有可收藏的网址。"; return; }
        if (config.Favorites.Any(f => BookmarkKey(f.Url) == url)) { statusMessage = "该网页已经在收藏夹中。"; return; }
        config.Favorites.Add(new FavoriteEntry { Name = string.IsNullOrWhiteSpace(tab.Title) ? new Uri(url).Host : tab.Title, Url = url });
        SaveOwnConfig();
        statusMessage = "已收藏当前网页。";
    }

    // 隐藏整个窗口（最小化 / 宏命令收起）：子窗口连 WebView2 一起消失。
    // 完全隐藏，游戏里不留任何痕迹，也不会出现在任务栏。
    private void Wv2HideWin(BrowserWin win)
    {
        // 先打标记：下次 ApplyHostGeometry 会补一次 SWP_SHOWWINDOW 把窗口显回来
        win.HostShown = 0;
        Wv2Post(() =>
        {
            try
            {
                if (win.HostHwnd != IntPtr.Zero && IsWindow(win.HostHwnd))
                    ShowWindow(win.HostHwnd, SW_HIDE);
            }
            catch {   }
        });
        foreach (var t in win.TabsSnapshot()) Wv2SetVisible(t, false);
    }

    // 新开一个标签页；同一个窗口的标签共用 Environment（Cookie / 登录态共享）
    private void OpenTab(BrowserWin win, string url, bool activate, IntPtr popupArgs = default, IntPtr popupDeferral = default)
    {
        var tab = new BrowserTab
        {
            Win     = win,
            Url     = url,
            Wv2Busy = true,
            Pending = new Wv2Pending { Url = popupArgs == IntPtr.Zero ? url : "" },
            NewWindowArgs = popupArgs, NewWindowDeferral = popupDeferral,
        };
        win.AddTab(tab, activate);
        win.LaunchStartMs = Environment.TickCount64;

        if (win.EnvReady) Wv2Post(() => Wv2CreateController(win, tab));
        else              StartEnv(win);
    }

    private void StartEnv(BrowserWin win)
    {
        if (win.EnvReady || win.EnvCreating) return;

        if (!EnsureWv2Thread())
        {
            Wv2FailWin(win, "内置内核线程启动失败，WebView2 不可用");
            return;
        }

        if (wv2RuntimeVersion.Length == 0)
        {
            // Queue the probe and creation on the STA thread rather than waiting on the game draw thread.
            win.EnvCreating = true;
            Wv2Post(() =>
            {
                if (win.Closed) return;
                var version = ProbeWv2Runtime();
                if (version == null)
                {
                    Volatile.Write(ref wv2ProbeState, (int)Wv2ProbeState.Missing);
                    wv2ProbeNextRetryMs = Environment.TickCount64 + 3_000;
                    Wv2FailWin(win, "未检测到 WebView2 运行时，请先安装 Microsoft Edge WebView2 Runtime");
                    return;
                }
                wv2RuntimeVersion = version;
                Volatile.Write(ref wv2ProbeState, (int)Wv2ProbeState.Ready);
                Wv2CreateEnvironment(win);
            });
            return;
        }

        if (win.UserData.Length == 0) win.UserData = Wv2ProfileDir(win.Key);
        win.EnvCreating = true;
        Wv2Post(() => Wv2CreateEnvironment(win));
    }

    private void ClosePage(PageEntry page)
    {
        if (RepositoryMode) { repositoryBrowser?.Close(page.Command); return; }
        var key = NormalizeCommand(page.Command);
        if (key.Length == 0) return;
        if (!windows.TryGetValue(key, out var win)) return;

        try
        {
            Wv2CloseWin(win);
            FocusGameNoRestore();
            windows.Remove(key);
        }
        catch (Exception e) { statusMessage = $"关闭失败: {e.Message}"; }
    }

    private static string SanitizeKey(string key)
    {
        var s = key.Replace("/", "").Replace("\\", "").Replace(" ", "_");
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
        if (s.Length == 0) s = "default";
        return s;
    }

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

    public override bool HasSettings => true;

    public override bool DrawSettings()
    {
        var changed = false;
        var backend = config.RenderingBackend;
        ImGui.SetNextItemWidth(OmniScale(260));
        if (ImGui.Combo("渲染版本", ref backend, "抽帧渲染（WebView2）\0浏览器原生（Edge / Chrome）\0"))
        {
            OnDisable();
            config.RenderingBackend = backend;
            config.TextureRender = true;
            SaveOwnConfig();
            OnEnable();
            changed = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("切换会关闭当前浏览器窗口。网页、宏命令、代理和收藏共用；两种浏览器的登录资料分别保存。");
        ImGui.Spacing();

        int removeIndex = -1;

        var rowX = ImGui.GetCursorPosX();
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var pageNameWidth = OmniScale(100);
        var commandWidth = OmniScale(90);
        var buttonWidth = MathF.Max(OmniScale(56), ImGui.CalcTextSize("删除").X + ImGui.GetStyle().FramePadding.X * 2);
        // OMNI 自绘复选框有独立尺寸，不能用文本输入框高度代替其宽度。
        var checkboxWidth = MathF.Max(ImGui.GetFrameHeight(), OmniTheme.CheckboxSize());
        var proxyWidth = checkboxWidth + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize("代理").X;
        var appWidth = RepositoryMode ? checkboxWidth + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize("无边框").X : 0;
        var commandX = rowX + pageNameWidth + gap;
        var urlX = commandX + commandWidth + gap;
        var clipRight = ImGui.GetWindowDrawList().GetClipRectMax().X - (ImGui.GetCursorScreenPos().X - rowX);
        var rowRight = MathF.Min(rowX + ImGui.GetContentRegionAvail().X, clipRight);
        var deleteX = rowRight - buttonWidth - MathF.Max(OmniScale(24), ImGui.GetStyle().FramePadding.X * 2 + gap);
        var toggleX = deleteX - buttonWidth - gap;
        var appX = RepositoryMode ? toggleX - appWidth - gap : toggleX;
        var proxyX = appX - proxyWidth - gap;
        var urlWidth = MathF.Max(1, proxyX - urlX - gap);

        if (config.Pages.Count > 0)
        {
            ImGui.PushTextWrapPos(-1);
            ImGui.TextDisabled("名字");
            ImGui.SameLine(commandX); ImGui.TextDisabled("宏命令");
            ImGui.SameLine(urlX); ImGui.TextDisabled("网址");
            ImGui.SameLine(proxyX + (proxyWidth - ImGui.CalcTextSize("代理").X) / 2); ImGui.TextDisabled("代理");
            if (RepositoryMode) { ImGui.SameLine(appX + (appWidth - ImGui.CalcTextSize("无边框").X) / 2); ImGui.TextDisabled("无边框"); }
            ImGui.SameLine(toggleX + (buttonWidth - ImGui.CalcTextSize("开关").X) / 2); ImGui.TextDisabled("开关");
            ImGui.SameLine(deleteX + (buttonWidth - ImGui.CalcTextSize("删除").X) / 2); ImGui.TextDisabled("删除");
            ImGui.PopTextWrapPos();
        }

        for (var i = 0; i < config.Pages.Count; i++)
        {
            var page = config.Pages[i];
            var key  = NormalizeCommand(page.Command);
            var open = RepositoryMode ? repositoryBrowser?.IsOpen(key) == true : windows.TryGetValue(key, out var w) && w.Visible && w.TabCount > 0;

            ImGui.PushID(i);

            var name = page.Name;
            ImGui.SetNextItemWidth(pageNameWidth);
            var nameEdited = ImGui.InputText("##Name", ref name, 64);
            if (name.Length == 0)
                DrawPlaceholder("如: 百度");
            if (nameEdited) { page.Name = name; changed = true; }

            ImGui.SameLine(commandX);
            var cmd = page.Command;
            ImGui.SetNextItemWidth(commandWidth);
            var cmdEdited = ImGui.InputText("##Cmd", ref cmd, 64);
            if (cmd.Length == 0)
                DrawPlaceholder("如: /baidu");
            if (cmdEdited)
            {
                page.Command = cmd;
                changed = true;
                SyncCommands();
            }

            ImGui.SameLine(urlX);
            var url = page.Url;
            ImGui.SetNextItemWidth(urlWidth);
            var urlEdited = ImGui.InputText("##Url", ref url, 512);
            if (url.Length == 0)
                DrawPlaceholder("如: https://www.baidu.com");
            if (urlEdited) { page.Url = url; changed = true; }

            ImGui.SameLine(proxyX);
            var useProxy = page.UseProxy;
            if (ImGui.Checkbox("代理", ref useProxy))
            {
                if (windows.TryGetValue(key, out var proxyWin) && proxyWin.TabCount > 0)
                    ChangeWindowProxy(proxyWin, useProxy);
                else if (TryProxy(useProxy, config.Proxy, out _, out var proxyError)) { page.UseProxy = useProxy; changed = true; }
                else statusMessage = proxyError;
            }

            if (RepositoryMode)
            {
                ImGui.SameLine(appX);
                var appMode = page.UseAppMode;
                if (ImGui.Checkbox("无边框", ref appMode)) { page.UseAppMode = appMode; changed = true; }
            }

            ImGui.SameLine(toggleX);
            if (ImGui.Button(open ? "关闭" : "打开", new System.Numerics.Vector2(buttonWidth, 0f)))
            {
                if (open) ClosePage(page);
                else      TogglePage(page);
            }

            ImGui.SameLine(deleteX);
            if (ImGui.Button("删除", new System.Numerics.Vector2(buttonWidth, 0f))) removeIndex = i;

            ImGui.PopID();
        }

        if (removeIndex >= 0)
        {
            var page = config.Pages[removeIndex];
            if (RepositoryMode) repositoryBrowser?.Close(page.Command);
            var cmd  = NormalizeCommand(page.Command);
            try
            {
                if (commandManager != null && cmd.Length > 0 && registeredCommands.Contains(cmd))
                {
                    commandManager.RemoveHandler(cmd);
                    registeredCommands.Remove(cmd);
                }
            }
            catch { }
            if (windows.TryGetValue(cmd, out var rw))
            {
                Wv2CloseWin(rw);
                FocusGame();
                focusGameUntilMs = Environment.TickCount64 + 2000;
            }
            windows.Remove(cmd);
            config.Pages.RemoveAt(removeIndex);
            changed = true;
        }

        ImGui.Spacing();
        if (ImGui.Button("＋ 添加网页"))
        {
            config.Pages.Add(new PageEntry
            {
                Name    = "",
                Url     = "",
                Command = "",
            });
            changed = true;
            SyncCommands();
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("设置"))
        {
            if (RepositoryMode)
            {
                var browserPath = config.BrowserPath;
                ImGui.TextDisabled("浏览器路径");
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##NativeBrowserPath", "留空自动查找 Edge / Chrome，或填写浏览器 exe 路径", ref browserPath, 1024))
                { config.BrowserPath = browserPath; changed = true; }
                var offsetX = config.OffsetX; var offsetY = config.OffsetY;
                ImGui.SetNextItemWidth(100);
                if (ImGui.InputInt("水平偏移", ref offsetX)) { config.OffsetX = offsetX; changed = true; }
                ImGui.SameLine(); ImGui.SetNextItemWidth(100);
                if (ImGui.InputInt("垂直偏移", ref offsetY)) { config.OffsetY = offsetY; changed = true; }
            }
            var lastErr = RepositoryMode ? "" : wv2LastError;
            if (lastErr.Length > 0)
            {
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.55f, 0.55f, 1f), "上次错误：" + lastErr);
            }

            ImGui.Spacing();
            ImGui.TextDisabled("窗口大小与位置");
            ImGui.Spacing();

            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("宽度");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60f);
            var winW = config.WinWidth > 0 ? config.WinWidth : DefaultWinWidth;
            if (ImGui.InputInt("##WinW", ref winW))
            {
                config.WinWidth = Math.Max(320, Math.Min(winW, 7680));
                changed = true;
            }

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("高度");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60f);
            var winH = config.WinHeight > 0 ? config.WinHeight : DefaultWinHeight;
            if (ImGui.InputInt("##WinH", ref winH))
            {
                config.WinHeight = Math.Max(240, Math.Min(winH, 4320));
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.Button("恢复默认窗口"))
            {
                config.WinWidth  = DefaultWinWidth;
                config.WinHeight = DefaultWinHeight;
                config.WinX      = int.MinValue;   // 位置回到游戏里居中
                config.WinY      = int.MinValue;
                changed = true;
            }

            ImGui.Spacing();
            ImGui.TextDisabled("网络代理");
            ImGui.SetNextItemWidth(-1f);
            var proxyInput = config.Proxy ?? "";
            if (ImGui.InputText("###GlobalProxy", ref proxyInput, 256))
            {
                config.Proxy = proxyInput;
                changed = true;
            }
            ImGui.TextDisabled("示例：127.0.0.1:7890 / socks5://127.0.0.1:1080");

        }

        if (ImGui.CollapsingHeader("收藏夹"))
        {
            int removeFavorite = -1;
            for (int i = 0; i < config.Favorites.Count; i++)
            {
                var favorite = config.Favorites[i];
                if (TryWebUrl(favorite.Url, out _) && config.Pages.Any(p => BookmarkKey(p.Url) == BookmarkKey(favorite.Url))) continue;
                ImGui.PushID("favorite" + i);
                var rowWidth = ImGui.GetContentRegionAvail().X;
                var spacing = ImGui.GetStyle().ItemSpacing.X;
                var deleteWidth = ImGui.CalcTextSize("删除").X + ImGui.GetStyle().FramePadding.X * 2;
                var nameWidth = MathF.Min(OmniScale(180), MathF.Max(60, rowWidth * 0.22f));
                string name = favorite.Name;
                ImGui.SetNextItemWidth(nameWidth);
                if (ImGui.InputTextWithHint("##favoriteName", "名称，如：百度", ref name, 128)) favorite.Name = name;
                changed |= ImGui.IsItemDeactivatedAfterEdit();
                ImGui.SameLine();
                string url = favorite.Url;
                ImGui.SetNextItemWidth(MathF.Max(80, rowWidth - nameWidth - deleteWidth * 2 - spacing * 3));
                if (ImGui.InputTextWithHint("##favoriteUrl", "网址，如：https://www.baidu.com", ref url, 2048)) favorite.Url = url;
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    if (TryWebUrl(favorite.Url, out var normalized)) favorite.Url = normalized;
                    else statusMessage = "收藏网址无效，请填写 HTTP/HTTPS 网址。";
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.Button("打开")) OpenSavedFavorite(favorite);
                ImGui.SameLine();
                if (ImGui.Button("删除")) removeFavorite = i;
                ImGui.Separator();
                ImGui.PopID();
            }
            if (removeFavorite >= 0) { config.Favorites.RemoveAt(removeFavorite); changed = true; }
            if (ImGui.Button("＋ 添加收藏"))
            {
                config.Favorites.Add(new FavoriteEntry { Name = "", Url = "" });
                changed = true;
            }
        }

        if (RepositoryMode && repositoryBrowser?.Status.Length > 0) ImGui.TextWrapped(repositoryBrowser.Status);
        if (statusMessage.Length > 0)
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.4f, 1f), statusMessage);

        if (changed) { ImportConfiguredFavorites(); SaveOwnConfig(); }
        return changed;
    }

    private void OpenSavedFavorite(FavoriteEntry favorite)
    {
        if (!TryWebUrl(favorite.Url, out var url)) { statusMessage = "请输入有效的收藏网址。"; return; }
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BookmarkKey(url)))).Substring(0, 16);
        TogglePage(new PageEntry { Name = favorite.Name, Url = url, Command = "/omni-favorite-" + id });
    }

    // ============================================================
    //  内置 WebView2 内核
    //  · loader 以 gzip+base64 内嵌在文件末尾，运行期释放到本地目录再 LoadLibrary，
    //    不依赖本机已装插件，也不额外分发任何文件；
    //  · 所有 COM 接口按 vtable 槽位手写调用（单文件模块无法引用托管 WebView2 SDK），
    //    槽位顺序取自 Microsoft.Web.WebView2.Core.dll 元数据；
    //    ★ 元数据里的下标不含 IUnknown 的 3 槽，真实槽号 = 下标 + 3（见 IUnkSlots）；
    //  · WebView2 必须创建在带消息泵的 STA 线程上，这里用一条常驻线程承载全部调用；
    //  · 控制器以「模块自建的顶层宿主窗口」为父窗口（owner=游戏窗口）：
    //    标题栏可拖动、边缘可缩放、可最小化、右上角 X 可关闭，Win11 自动圆角，
    //    窗口被钳在显示器工作区内拖不出屏幕，游戏最小化时跟着隐藏。
    // ============================================================

    private const int  Wv2LoaderRawSize = 164192;
    private const uint WM_WV2_WORK      = 0x8000 + 1;
    private const int  RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    private static readonly object wv2Gate = new();
    private static readonly ManualResetEventSlim wv2ThreadReady = new(false);
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> wv2Queue = new();

    // 泵线程 → 游戏线程 的动作队列。
    // 贴图模式下的 SRV（GPU 资源）只能在游戏线程释放：ImGui 把纹理指针记进了 draw list，
    // 泵线程在游戏线程正渲染这一帧时 Release 掉就是 use-after-free。
    // 统一放到「下一帧开始前」执行，保证上一帧的渲染已经提交完毕。
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> gameQueue = new();
    private static void GamePost(Action act) => gameQueue.Enqueue(act);

    private static Thread? wv2Thread;
    private static volatile int  wv2ThreadId;
    private static volatile bool wv2ThreadFailed;
    private static IntPtr wv2LoaderModule = IntPtr.Zero;
    private static Wv2CreateEnvFn?  wv2CreateEnvFn;
    private static Wv2GetVersionFn? wv2GetVersionFn;
    private static string wv2RuntimeVersion = "";

    // WebView2 运行时探测：全自动，用户不需要点任何按钮。
    // Idle → Running → Ready / Missing；Missing 时由 WatchLoop 定时自动重试，
    // 中途装好运行时也会在一个轮询周期内自动变成 Ready。
    private static int  wv2ProbeState;
    private static long wv2ProbeNextRetryMs;

    // 消息泵里被吞掉的异常记在这里，设置面板会显示出来，便于定位而不是静默失败
    private static string wv2LastError = "";

    private enum Wv2ProbeState { Idle = 0, Running = 1, Ready = 2, Missing = 3 }

    private static IntPtr vtblCompleted;
    private static IntPtr vtblEvent;
    private static IntPtr vtblOptions;

    private enum Wv2CbKind { EnvDone, CtrlDone, NavStarting, NavCompleted, DocTitle, NewWindow, WinClose, Options }

    // 手工搭的 COM 对象：Self 指向一段非托管内存 [vtbl][gcHandle]
    private sealed class Wv2Callback
    {
        public IntPtr Self;
        public GCHandle Handle;
        public Guid Iid;
        public Wv2CbKind Kind;
        public string Args = "";
        // 直接持有窗口/标签对象：回调线程（STA 消息泵）不去碰 windows 字典，
        // 免得和绘制线程并发读写同一个 Dictionary
        public BrowserWin? Target;
        public BrowserTab? Tab;
        public WeakReference<GameWebBrowser>? Owner;
        public int RefCount = 1;
    }

    private static class Wv2Iid
    {
        public static readonly Guid Unknown         = new("00000000-0000-0000-C000-000000000046");
        public static readonly Guid EnvDoneHandler  = new("4E8A3389-C9D8-4BD2-B6B5-124FEE6CC14D");
        public static readonly Guid CtrlDoneHandler = new("6C4819F3-C9B7-4260-8127-C9F5BDE7F68C");
        public static readonly Guid NavStarting     = new("9ADBE429-F36D-432B-9DDC-F8881FBD76E3");
        public static readonly Guid NavCompleted    = new("D33A35BF-1C49-4F98-93AB-006E0533FE1C");
        public static readonly Guid DocTitle        = new("F5F2B923-953E-4042-9F95-F3A118E1AFD4");
        public static readonly Guid NewWindow       = new("D4C185FE-C81C-4989-97AF-2D3FA7AB5651");
        public static readonly Guid WinClose        = new("5C19E9E0-092F-486B-AFFA-CA8231913039");
        public static readonly Guid EnvOptions      = new("2FDE08A8-1E9A-4766-8C05-95A9CEB9D1C5");
        public static readonly Guid Settings2       = new("EE9A0F68-F46C-4E32-AC23-EF8CAC224D2A");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    // 调 COM 之前用它确认「指针确实指向已提交的内存 / 可执行页」。
    // 空指针或野指针一旦解引用就是访问违例，托管 catch 拦不住，进程直接终止 ——
    // 这是本模块唯一真正能防住崩溃的手段。
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint   AllocationProtect;
        public IntPtr RegionSize;
        public uint   State;
        public uint   Protect;
        public uint   Type;
    }

    private const uint MEM_COMMIT      = 0x1000;
    private const uint PAGE_NOACCESS   = 0x01;
    private const uint PAGE_GUARD      = 0x100;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

    // ---- 手写 COM 调用所需的委托签名（x64 只有一种调用约定） ----
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Wv2CreateEnvFn([MarshalAs(UnmanagedType.LPWStr)] string? browserExecutableFolder,
                                        [MarshalAs(UnmanagedType.LPWStr)] string? userDataFolder,
                                        IntPtr environmentOptions, IntPtr environmentCreatedHandler);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Wv2GetVersionFn([MarshalAs(UnmanagedType.LPWStr)] string? browserExecutableFolder,
                                         out IntPtr versionInfo);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DlgQueryInterface(IntPtr self, IntPtr riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DlgAddRef(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DlgRelease(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DlgInvokeHr(IntPtr self, int hr, IntPtr arg);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DlgInvokeArgs(IntPtr self, IntPtr sender, IntPtr args);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DlgGetPtr(IntPtr self, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DlgGetInt(IntPtr self, out int value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DlgGetStr(IntPtr self, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DlgSetInt(IntPtr self, int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DlgSetPtr(IntPtr self, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DlgSetStr(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DlgNoArg(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DlgSetBounds(IntPtr self, IntPtr rect);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DlgAddEvent(IntPtr self, IntPtr handler, out long token);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DlgCreateController(IntPtr self, IntPtr parentWindow, IntPtr handler);

    // ICoreWebView2::CallDevToolsProtocolMethod（方法索引 33；ICoreWebView2 是普通 COM 接口，
    // 继承 IUnknown ⇒ 用 Fn 补 3 槽）。签名：
    //   HRESULT CallDevToolsProtocolMethod(LPCWSTR method, LPCWSTR paramsJson, ICompletedHandler* handler)
    // handler 传 NULL = fire-and-forget，我们不需要返回值（输入事件没有回执）。
    private delegate int DlgCdpCall(IntPtr self, IntPtr methodName, IntPtr parametersAsJson, IntPtr handler);

    private static readonly DlgQueryInterface fnQueryInterface = ThunkQueryInterface;
    private static readonly DlgAddRef         fnAddRef         = ThunkAddRef;
    private static readonly DlgRelease        fnRelease        = ThunkRelease;
    private static readonly DlgInvokeHr       fnEnvDone        = ThunkEnvDone;
    private static readonly DlgInvokeHr       fnCtrlDone       = ThunkCtrlDone;
    private static readonly DlgInvokeArgs     fnEvent          = ThunkEvent;
    private static readonly DlgGetStr         fnOptGetArgs     = ThunkOptGetArgs;
    private static readonly DlgSetStr         fnOptSetArgs     = ThunkOptSetArgs;
    private static readonly DlgGetStr         fnOptGetEmpty    = ThunkOptGetEmpty;
    private static readonly DlgGetStr         fnOptGetVersion  = ThunkOptGetVersion;
    private static readonly DlgSetStr         fnOptSetIgnore   = ThunkOptSetIgnore;
    private static readonly DlgGetInt         fnOptGetZero     = ThunkOptGetZero;
    private static readonly DlgSetInt         fnOptSetIgnoreI  = ThunkOptSetIgnoreI;

    // ---------------------------------------------------------------
    //  COM 回调对象基础设施
    // ---------------------------------------------------------------

    private static Wv2Callback? FromSelf(IntPtr self)
    {
        if (self == IntPtr.Zero) return null;
        var h = Marshal.ReadIntPtr(self, IntPtr.Size);
        if (h == IntPtr.Zero) return null;
        try { return GCHandle.FromIntPtr(h).Target as Wv2Callback; }
        catch { return null; }
    }

    private static IntPtr BuildVtbl(params Delegate[] fns)
    {
        var p = Marshal.AllocHGlobal(IntPtr.Size * fns.Length);
        for (var i = 0; i < fns.Length; i++)
            Marshal.WriteIntPtr(p, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(fns[i]));
        return p;
    }

    private static Wv2Callback Wv2NewCallback(GameWebBrowser owner, BrowserWin? win, BrowserTab? tab,
                                              Wv2CbKind kind, Guid iid, IntPtr vtbl)
    {
        var cb = new Wv2Callback { Iid = iid, Kind = kind, Target = win, Tab = tab };
        cb.Owner  = new WeakReference<GameWebBrowser>(owner);
        cb.Handle = GCHandle.Alloc(cb);
        cb.Self   = Marshal.AllocHGlobal(IntPtr.Size * 2);
        Marshal.WriteIntPtr(cb.Self, 0, vtbl);
        Marshal.WriteIntPtr(cb.Self, IntPtr.Size, GCHandle.ToIntPtr(cb.Handle));
        return cb;
    }

    private static void EnsureVtables()
    {
        if (vtblCompleted != IntPtr.Zero) return;
        vtblCompleted = BuildVtbl(fnQueryInterface, fnAddRef, fnRelease, fnEnvDone);
        vtblEvent     = BuildVtbl(fnQueryInterface, fnAddRef, fnRelease, fnEvent);
        vtblOptions   = BuildVtbl(fnQueryInterface, fnAddRef, fnRelease,
                                  fnOptGetArgs, fnOptSetArgs,      // AdditionalBrowserArguments
                                  fnOptGetEmpty, fnOptSetIgnore,   // Language
                                  fnOptGetVersion, fnOptSetIgnore, // TargetCompatibleBrowserVersion
                                  fnOptGetZero, fnOptSetIgnoreI);  // AllowSingleSignOnUsingOSPrimaryAccount
    }

    private static int ThunkQueryInterface(IntPtr self, IntPtr riid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        var o = FromSelf(self);
        if (o == null || riid == IntPtr.Zero) return unchecked((int)0x80004003);   // E_POINTER
        var iid = Marshal.PtrToStructure<Guid>(riid);
        if (iid == Wv2Iid.Unknown || iid == o.Iid)
        {
            ppv = self;
            Interlocked.Increment(ref o.RefCount);
            return 0;
        }
        return unchecked((int)0x80004002);   // E_NOINTERFACE
    }

    private static uint ThunkAddRef(IntPtr self)
    {
        var o = FromSelf(self);
        if (o == null) return 1;
        return (uint)Interlocked.Increment(ref o.RefCount);
    }

    private static uint ThunkRelease(IntPtr self)
    {
        var o = FromSelf(self);
        if (o == null) return 0;
        var n = Interlocked.Decrement(ref o.RefCount);
        // 刻意不释放非托管内存：单次游戏会话内创建的实例很少（每页 6~7 个），
        // 与其冒 use-after-free 崩溃的风险，不如让它们活到进程结束
        return n < 0 ? 0u : (uint)n;
    }

    private static int ThunkEnvDone(IntPtr self, int hr, IntPtr arg) => DispatchCallback(self, hr, arg, IntPtr.Zero);
    private static int ThunkCtrlDone(IntPtr self, int hr, IntPtr arg) => DispatchCallback(self, hr, arg, IntPtr.Zero);
    private static int ThunkEvent(IntPtr self, IntPtr sender, IntPtr args) => DispatchCallback(self, 0, sender, args);

    private static int DispatchCallback(IntPtr self, int hr, IntPtr a, IntPtr b)
    {
        try
        {
            var o = FromSelf(self);
            if (o?.Owner != null && o.Owner.TryGetTarget(out var owner))
                owner.OnWv2Callback(o, hr, a, b);
        }
        catch {   }
        return 0;
    }

    // ---- 环境参数对象（用于给浏览器进程传 --proxy-server 等附加参数） ----

    private static int ThunkOptGetArgs(IntPtr self, out IntPtr value)
    {
        value = IntPtr.Zero;
        var o = FromSelf(self);
        if (o == null) return unchecked((int)0x80004003);
        try { value = Marshal.StringToCoTaskMemUni(o.Args ?? ""); } catch { value = IntPtr.Zero; }
        return 0;
    }

    private static int ThunkOptSetArgs(IntPtr self, string value)
    {
        var o = FromSelf(self);
        if (o == null) return unchecked((int)0x80004003);
        o.Args = value ?? "";
        return 0;
    }

    // Language：必须回一个「真实分配的空串」。运行时取字符串是按
    // 「返回 S_OK 就当有值」处理的，回 nullptr 会被判成参数非法（实测立即 0x80070057）
    private static int ThunkOptGetEmpty(IntPtr self, out IntPtr value)
    {
        value = IntPtr.Zero;
        try { value = Marshal.StringToCoTaskMemUni(""); } catch { value = IntPtr.Zero; }
        return 0;
    }

    // TargetCompatibleBrowserVersion：这个最坑。在本机（Runtime 153.0.4234.48）实测：
    //   回 ""             → CreateCoreWebView2EnvironmentWithOptions 立即 0x80070057 (E_INVALIDARG)
    //   回 "1.0.4078.44"  → 立即 0x80070002（找不到该版本体系的内核）
    //   回 "15x.0.4234.48"→ 创建成功
    // 结论：这里必须给一个「与已装内核同一版本体系」的版本号，直接回探测到的真实内核版本最稳。
    private static int ThunkOptGetVersion(IntPtr self, out IntPtr value)
    {
        value = IntPtr.Zero;
        var v = wv2RuntimeVersion;
        if (v.Length == 0) return unchecked((int)0x8000FFFF);   // E_UNEXPECTED：宁可不带参数，也不回空串
        try { value = Marshal.StringToCoTaskMemUni(v); } catch { value = IntPtr.Zero; }
        return 0;
    }

    private static int ThunkOptSetIgnore(IntPtr self, string value) => 0;
    private static int ThunkOptGetZero(IntPtr self, out int value) { value = 0; return 0; }
    private static int ThunkOptSetIgnoreI(IntPtr self, int value) => 0;

    // ---------------------------------------------------------------
    //  裸 vtable 调用
    // ---------------------------------------------------------------

    // ★ COM 对象的 vtable 前 3 槽固定是 IUnknown 的 QueryInterface / AddRef / Release。
    //   托管 SDK 的 Raw 接口声明靠 InterfaceType(IsIUnknown) 自动补这 3 槽，
    //   所以「接口自有方法的下标」必须 +3 才是真实槽号。
    //   少加这 3 个槽会直接调到 QueryInterface 上：实参被当成 GUID 指针解引用，
    //   进程当场终止（这个坑已经踩过一次，改这里务必先数 IUnknown）。
    private const int IUnkSlots = 3;

    private static bool IsMemOk(IntPtr p, bool needExec)
    {
        if (p == IntPtr.Zero) return false;
        try
        {
            if (VirtualQuery(p, out var mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero)
                return false;
            if (mbi.State != MEM_COMMIT) return false;
            var prot = mbi.Protect;
            if ((prot & (PAGE_NOACCESS | PAGE_GUARD)) != 0) return false;

            const uint PAGE_EXECUTE           = 0x10;
            const uint PAGE_EXECUTE_READ      = 0x20;
            const uint PAGE_EXECUTE_READWRITE = 0x40;
            const uint PAGE_EXECUTE_WRITECOPY = 0x80;
            const uint EXEC   = PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
            const uint READ   = 0x02 | 0x04 | 0x08 | EXEC;

            return (prot & (needExec ? EXEC : READ)) != 0;
        }
        catch { return false; }
    }

    // 对象指针与它的 vtable 指针都必须可读
    private static bool ComObjectOk(IntPtr obj)
    {
        if (!IsMemOk(obj, false)) return false;
        try { return IsMemOk(Marshal.ReadIntPtr(obj), false); }
        catch { return false; }
    }

    // 真实槽号（只有 IUnknown 自己的方法才该用这个）
    private static T FnRaw<T>(IntPtr obj, int slot) where T : Delegate
    {
        if (!ComObjectOk(obj)) throw new InvalidOperationException("COM 对象指针无效");
        var vtbl = Marshal.ReadIntPtr(obj);
        var fn   = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        if (!IsMemOk(fn, true)) throw new InvalidOperationException($"COM 槽位 {slot} 不是可执行地址");
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    // 接口方法下标（不含 IUnknown 的 3 槽）→ 真实槽号
    private static T Fn<T>(IntPtr obj, int methodIndex) where T : Delegate
        => FnRaw<T>(obj, methodIndex + IUnkSlots);

    private static void ReleaseRaw(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        try { FnRaw<DlgRelease>(obj, 2)(obj); } catch {   }
    }

    // ★ 回调里收到的接口指针都是「借用引用」：回调一返回，运行时就会释放它。
    //   想在回调之外继续用（本项目要长期持有 environment / controller / CoreWebView2），
    //   必须在回调内立刻 AddRef。实测不 AddRef 就在回调返回后调
    //   CreateCoreWebView2Controller，轻则 0x80040205 失败，重则直接崩进程。
    private static void AddRefRaw(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        try { FnRaw<DlgAddRef>(obj, 1)(obj); } catch {   }
    }

    // 出错时落一份文件日志：ImGui 状态栏会被后来的消息覆盖，事后没法追
    private static void Wv2Log(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FFXIVGameBrowser", "wv2");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "gwblog.txt"),
                DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
        }
        catch {   }
    }

    private static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        if (obj == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var fn = FnRaw<DlgQueryInterface>(obj, 0);
            var g  = iid;
            return fn(obj, (IntPtr)(&g), out var p) == 0 ? p : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    private static IntPtr GetPtr(IntPtr obj, int methodIndex)
    {
        if (obj == IntPtr.Zero) return IntPtr.Zero;
        try { return Fn<DlgGetPtr>(obj, methodIndex)(obj, out var p) == 0 ? p : IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    private static int GetInt(IntPtr obj, int methodIndex)
    {
        if (obj == IntPtr.Zero) return 0;
        try { return Fn<DlgGetInt>(obj, methodIndex)(obj, out var v) == 0 ? v : 0; }
        catch { return 0; }
    }

    private static string ReadStr(IntPtr obj, int methodIndex)
    {
        if (obj == IntPtr.Zero) return "";
        try
        {
            if (Fn<DlgGetStr>(obj, methodIndex)(obj, out var p) != 0) return "";
            if (!IsMemOk(p, false)) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; }
            finally { CoTaskMemFree(p); }
        }
        catch { return ""; }
    }

    // ---------------------------------------------------------------
    //  常驻 STA 消息泵线程
    // ---------------------------------------------------------------

    private static bool EnsureWv2Thread()
    {
        if (wv2Thread is { IsAlive: true } && wv2ThreadId != 0) return !wv2ThreadFailed;

        lock (wv2Gate)
        {
            if (wv2Thread is { IsAlive: true } && wv2ThreadId != 0) return !wv2ThreadFailed;
            try
            {
                wv2ThreadReady.Reset();
                wv2ThreadFailed = false;
                var t = new Thread(Wv2PumpLoop) { IsBackground = true, Name = "GWB.WebView2" };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                wv2Thread = t;
                if (!wv2ThreadReady.Wait(5000)) wv2ThreadFailed = true;
            }
            catch { wv2ThreadFailed = true; }
        }
        return !wv2ThreadFailed && wv2ThreadId != 0;
    }

    private static void Wv2PumpLoop()
    {
        try
        {
            var hr = CoInitializeEx(IntPtr.Zero, 2);   // COINIT_APARTMENTTHREADED
            if (hr == RPC_E_CHANGED_MODE)
            {
                wv2ThreadFailed = true;
                wv2ThreadReady.Set();
                return;
            }

            wv2ThreadId = (int)GetCurrentThreadId();
            wv2ThreadReady.Set();
            Wv2Log($"STA 消息泵线程已起 tid={wv2ThreadId}");

            while (true)
            {
                var r = GetMessage(out var msg, IntPtr.Zero, 0, 0);
                if (r <= 0) break;
                // 只认「线程消息」（hwnd 为空）：WebView2/Chromium 内部也用 WM_APP 区间的消息，
                // 不加这道判断会把它们的窗口消息吞掉，导致异步回调永远不来
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_WV2_WORK) { Wv2Drain(); continue; }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception e) { wv2LastError = "消息泵异常：" + e.Message; Wv2Log("消息泵异常: " + e); }
        finally
        {
            wv2ThreadId = 0;
            try { wv2ThreadReady.Set(); } catch {   }
        }
    }

    private static void Wv2Drain()
    {
        while (wv2Queue.TryDequeue(out var work))
        {
            try { work(); }
            catch (Exception e) { wv2LastError = e.Message; Wv2Log("Drain 异常: " + e); }
        }
    }

    private static void Wv2Post(Action work)
    {
        wv2Queue.Enqueue(work);
        var id = wv2ThreadId;
        if (id != 0) PostThreadMessageW((uint)id, WM_WV2_WORK, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool Wv2InvokeSync(Func<bool> work, int timeoutMs)
    {
        if ((int)GetCurrentThreadId() == wv2ThreadId) return work();
        var done = new ManualResetEventSlim(false);
        Wv2Post(() => { try { work(); } catch {   } finally { done.Set(); } });
        try { return done.Wait(timeoutMs); } catch { return false; }
    }

    // ---------------------------------------------------------------
    //  loader 释放与运行时探测
    // ---------------------------------------------------------------

    private static bool EnsureWv2Loader(out string error)
    {
        error = "";
        lock (wv2Gate)
        {
            if (wv2CreateEnvFn != null) return true;
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FFXIVGameBrowser", "wv2");
                Directory.CreateDirectory(dir);
                var dll = Path.Combine(dir, "WebView2Loader.x64.dll");

                var need = true;
                try { need = !File.Exists(dll) || new FileInfo(dll).Length != Wv2LoaderRawSize; } catch { need = true; }

                if (need)
                {
                    var gz = Convert.FromBase64String(Wv2LoaderB64);
                    byte[] raw;
                    using (var ms = new MemoryStream(gz))
                    using (var gs = new GZipStream(ms, CompressionMode.Decompress))
                    using (var os = new MemoryStream(Wv2LoaderRawSize))
                    {
                        gs.CopyTo(os);
                        raw = os.ToArray();
                    }
                    var tmp = dll + ".tmp";
                    File.WriteAllBytes(tmp, raw);
                    if (File.Exists(dll)) File.Delete(dll);
                    File.Move(tmp, dll);
                }

                wv2LoaderModule = LoadLibraryW(dll);
                if (wv2LoaderModule == IntPtr.Zero)
                {
                    error = $"内置 WebView2Loader 加载失败（错误码 {Marshal.GetLastWin32Error()}）";
                    return false;
                }

                var fp = GetProcAddress(wv2LoaderModule, "CreateCoreWebView2EnvironmentWithOptions");
                if (fp == IntPtr.Zero) { error = "内置 WebView2Loader 导出函数缺失"; return false; }
                wv2CreateEnvFn = Marshal.GetDelegateForFunctionPointer<Wv2CreateEnvFn>(fp);

                var vp = GetProcAddress(wv2LoaderModule, "GetAvailableCoreWebView2BrowserVersionString");
                if (vp != IntPtr.Zero) wv2GetVersionFn = Marshal.GetDelegateForFunctionPointer<Wv2GetVersionFn>(vp);

                return true;
            }
            catch (Exception e)
            {
                error = "内置 WebView2Loader 释放失败: " + e.Message;
                return false;
            }
        }
    }

    // 在 STA 线程上同步问一次本机运行时版本；返回 null 表示运行时不可用
    private static string? ProbeWv2Runtime()
    {
        if (!EnsureWv2Loader(out _)) return null;
        // 探测本身要在这个 STA 消息泵线程上跑，否则 Wv2Post 投不出去，会假报「未检测到运行时」
        if (!EnsureWv2Thread()) return null;

        string? result = null;
        Wv2InvokeSync(() =>
        {
            try
            {
                var fn = wv2GetVersionFn;
                if (fn == null) return true;
                if (fn(null, out var p) != 0 || p == IntPtr.Zero) return true;
                try { result = Marshal.PtrToStringUni(p); } finally { CoTaskMemFree(p); }
            }
            catch {   }
            return true;
        }, 5000);

        return string.IsNullOrEmpty(result) ? null : result;
    }

    // 后台自动探测一次（幂等：已在跑就直接返回）
    private static void StartWv2Probe()
    {
        if (Interlocked.CompareExchange(ref wv2ProbeState,
                (int)Wv2ProbeState.Running, (int)Wv2ProbeState.Idle) != (int)Wv2ProbeState.Idle)
            return;

        wv2ProbeNextRetryMs = Environment.TickCount64 + 3_000;

        _ = Task.Run(() =>
        {
            var v = "";
            try { v = ProbeWv2Runtime() ?? ""; } catch { v = ""; }
            if (v.Length > 0) wv2RuntimeVersion = v;
            Volatile.Write(ref wv2ProbeState,
                v.Length > 0 ? (int)Wv2ProbeState.Ready : (int)Wv2ProbeState.Missing);
        });
    }

    // 没检测到就过一会儿再自动试一次（装运行时不用重启游戏）
    private static void Wv2ProbeRetryTick()
    {
        if (Volatile.Read(ref wv2ProbeState) != (int)Wv2ProbeState.Missing) return;
        if (Environment.TickCount64 < Volatile.Read(ref wv2ProbeNextRetryMs)) return;
        Volatile.Write(ref wv2ProbeState, (int)Wv2ProbeState.Idle);
        StartWv2Probe();
    }

    private static string Wv2ProfileDir(string key) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FFXIVGameBrowser", "wv2profiles", SanitizeKey(key));

    // ---------------------------------------------------------------
    //  实例侧：标签创建 / 显示 / 隐藏 / 关闭（一切都围绕 BrowserTab）
    // ---------------------------------------------------------------

    private void Wv2FailWin(BrowserWin win, string message)
    {
        foreach (var t in win.TabsSnapshot())
        {
            t.Wv2Busy = false;
            t.Pending = null;
        }
        win.EnvCreating = false;
        statusMessage = message;
        Wv2Log("FAIL(窗口) " + message);
    }

    // All event argument access and completion stay on the WebView2 STA thread.
    private static void FinishNewWindow(BrowserTab tab, IntPtr core = default)
    {
        var args = tab.NewWindowArgs;
        var deferral = tab.NewWindowDeferral;
        tab.NewWindowArgs = tab.NewWindowDeferral = IntPtr.Zero;
        try
        {
            if (args != IntPtr.Zero)
            {
                if (core != IntPtr.Zero) Fn<DlgSetPtr>(args, 1)(args, core);
                Fn<DlgSetInt>(args, 3)(args, 1);
            }
        }
        finally
        {
            try { if (deferral != IntPtr.Zero) Fn<DlgNoArg>(deferral, 0)(deferral); }
            finally { ReleaseRaw(deferral); ReleaseRaw(args); }
        }
    }

    private void Wv2Fail(BrowserTab tab, string message, bool setStatus = true)
    {
        Wv2Post(() => FinishNewWindow(tab));
        tab.Wv2Busy = false;
        tab.Pending = null;
        if (setStatus) statusMessage = message;
        Wv2Log("FAIL " + message);
    }

    private void Wv2CreateEnvironment(BrowserWin win)
    {
        if (win.Closed) return;
        // 上一次尝试可能残留了 environment（我们 AddRef 过），先释放再重建，避免引用泄漏
        if (win.Wv2Environment != IntPtr.Zero)
        {
            ReleaseRaw(win.Wv2Environment);
            win.Wv2Environment = IntPtr.Zero;
        }
        win.EnvReady = false;

        if (FindGameHwnd() == IntPtr.Zero) { win.EnvCreating = false; Wv2FailWin(win, "未找到游戏窗口"); return; }
        if (win.UserData.Length == 0) win.UserData = Wv2ProfileDir(win.Key);
        try { Directory.CreateDirectory(win.UserData); } catch {   }

        var proxy = win.Proxy;

        var options = IntPtr.Zero;
        // ★ 贴图模式下宿主窗口会被藏到游戏背后（=被遮挡）。Chromium 默认会对「被遮挡窗口」降频
        //   甚至完全停渲染，表现为「一开始有画面、几秒后卡死不动」，必须显式关掉这几项节流。
        const string OcclusionArgs =
            "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding " +
            "--disable-background-timer-throttling --disable-features=CalculateNativeWinOcclusion";
        var argList = "";
        argList = proxy.Length > 0 ? "--proxy-server=" + proxy : "--no-proxy-server";
        if (config.TextureRender)
            argList = argList.Length > 0 ? argList + " " + OcclusionArgs : OcclusionArgs;

        // 探测不到内核版本时不挂 options：那会让 TargetCompatibleBrowserVersion 无处可取，
        // 而回空串必被拒（0x80070057）。宁可这次不带代理，也不要整个环境创建失败。
        if (proxy.Length > 0 && wv2RuntimeVersion.Length == 0)
        { win.EnvCreating = false; Wv2FailWin(win, "无法取得内核版本，代理环境未创建；不会自动直连。"); return; }
        if (argList.Length > 0 && wv2RuntimeVersion.Length > 0)
        {
            EnsureVtables();
            var oc = Wv2NewCallback(this, win, null, Wv2CbKind.Options, Wv2Iid.EnvOptions, vtblOptions);
            oc.Args = argList;
            options = oc.Self;
        }

        EnsureVtables();
        var cb = Wv2NewCallback(this, win, null, Wv2CbKind.EnvDone, Wv2Iid.EnvDoneHandler, vtblCompleted);
        var hr = wv2CreateEnvFn!(null, win.UserData, options, cb.Self);
        Wv2Log($"CreateCoreWebView2EnvironmentWithOptions 立即返回 hr=0x{hr:X8} userData={win.UserData} " +
               $"args={(argList.Length > 0 ? argList : "(无)")} ver={wv2RuntimeVersion}");
        if (hr >= 0) return;

        win.EnvCreating = false;
        Wv2FailWin(win, $"内置内核环境创建失败（0x{hr:X8}）" + (win.UseProxy ? "；代理仍开启，请检查代理地址。" : ""));
    }

    private void Wv2CreateController(BrowserWin win, BrowserTab tab)
    {
        var env = win.Wv2Environment;
        if (env == IntPtr.Zero || tab.Wv2Closed) return;
        if (!ComObjectOk(env)) { Wv2Fail(tab, "内置内核环境对象异常，已中止（不会影响游戏）"); return; }

        // 宿主窗口必须在：控制器以它为父窗口，没有父窗口网页就没有地方显示
        if (win.HostHwnd == IntPtr.Zero || !IsWindow(win.HostHwnd))
        {
            CreateHostWindow(win);
            if (win.HostHwnd == IntPtr.Zero) return;
        }

        var cb = Wv2NewCallback(this, win, tab, Wv2CbKind.CtrlDone, Wv2Iid.CtrlDoneHandler, vtblCompleted);
        try
        {
            // 槽位 0 = ICoreWebView2Environment::CreateCoreWebView2Controller（Fn 里补 IUnknown 3 槽）
            var hr = Fn<DlgCreateController>(env, 0)(env, win.HostHwnd, cb.Self);
            Wv2Log($"CreateCoreWebView2Controller 立即返回 hr=0x{hr:X8} parent=0x{win.HostHwnd:X}");
            if (hr < 0) Wv2Fail(tab, $"内置内核控制器创建失败（0x{hr:X8}）");
        }
        catch (Exception e) { Wv2Fail(tab, "内置内核控制器创建异常：" + e.Message); }
    }

    private void Wv2Configure(BrowserTab tab)
    {
        var win  = tab.Win;
        var ctrl = tab.Wv2Controller;
        var core = tab.Wv2Core;
        // 空指针一旦解引用是 AccessViolation，catch 拦不住，直接崩游戏
        if (win == null || ctrl == IntPtr.Zero || core == IntPtr.Zero) return;

        try { Fn<DlgSetInt>(ctrl, 1)(ctrl, 0); } catch {   }   // 先藏起来，配好再显示，避免白屏闪一下

        var settings = GetPtr(core, 0);                          // ICoreWebView2::get_Settings
        if (ComObjectOk(settings))
        {
            try { Fn<DlgSetInt>(settings, 7)(settings, 0);  } catch {   }  // IsStatusBarEnabled
            try { Fn<DlgSetInt>(settings, 15)(settings, 0); } catch {   }  // IsZoomControlEnabled
            try { Fn<DlgSetInt>(settings, 9)(settings, 1);  } catch {   }  // AreDevToolsEnabled
            try { Fn<DlgSetInt>(settings, 11)(settings, 1); } catch {   }  // AreDefaultContextMenusEnabled

            // 抹掉 UA 里的 Edg/ 标记：部分站点会针对 Edge 走兼容分支
            var s2 = QueryInterface(settings, Wv2Iid.Settings2);
            if (s2 != IntPtr.Zero)
            {
                var ua = ReadStr(s2, 18);                                 // get_UserAgent
                if (ua.Length > 0)
                {
                    var clean = System.Text.RegularExpressions.Regex.Replace(ua, @"\s*Edg/[\d.]+", "");
                    if (clean != ua) { try { Fn<DlgSetStr>(s2, 19)(s2, clean); } catch {   } }
                }
                ReleaseRaw(s2);
            }
            ReleaseRaw(settings);
        }

        Wv2AddEvent(win, tab, core,  4, Wv2Iid.NavStarting,  Wv2CbKind.NavStarting);
        Wv2AddEvent(win, tab, core, 12, Wv2Iid.NavCompleted, Wv2CbKind.NavCompleted);
        Wv2AddEvent(win, tab, core, 43, Wv2Iid.DocTitle,     Wv2CbKind.DocTitle);
        Wv2AddEvent(win, tab, core, 41, Wv2Iid.NewWindow,    Wv2CbKind.NewWindow);
        Wv2AddEvent(win, tab, core, 56, Wv2Iid.WinClose,     Wv2CbKind.WinClose);

        PutBoundsToClientNow(tab);
    }

    private void Wv2AddEvent(BrowserWin win, BrowserTab tab, IntPtr core, int slot, Guid iid, Wv2CbKind kind)
    {
        try
        {
            var cb = Wv2NewCallback(this, win, tab, kind, iid, vtblEvent);
            Fn<DlgAddEvent>(core, slot)(core, cb.Self, out _);
        }
        catch {   }
    }

    // 把网页铺满宿主窗口客户区。泵线程直调（WM_SIZE 与创建流程都在泵线程上）
    private static void PutBoundsToClientNow(BrowserTab tab)
    {
        var ctrl = tab.Wv2Controller;
        var host = tab.Win?.HostHwnd ?? IntPtr.Zero;
        if (ctrl == IntPtr.Zero || host == IntPtr.Zero) return;
        if (!GetClientRect(host, out var rc)) return;

        var r = new RECT { Left = 0, Top = 0, Right = rc.Right, Bottom = rc.Bottom };
        try
        {
            var hr = Fn<DlgSetBounds>(ctrl, 3)(ctrl, (IntPtr)(&r));
            if (hr < 0) LogBoundsError(hr, rc);
        }
        catch (Exception e) { Wv2Log("put_Bounds 异常: " + e.Message); }
    }

    // put_Bounds 失败日志限流：同一个错误码 5 秒内只记一条。
    // 拖拽时几何每帧都在变，这个方法会被高频调用，不节流的话几秒钟就能把日志撑到几百 KB。
    private static long boundsErrLogMs;
    private static int  boundsErrLastHr = int.MinValue;

    private static void LogBoundsError(int hr, RECT rc)
    {
        var now = Environment.TickCount64;
        if (hr == boundsErrLastHr && now - boundsErrLogMs < 5000) return;
        boundsErrLastHr = hr;
        boundsErrLogMs  = now;
        Wv2Log($"put_Bounds hr=0x{hr:X8}（客户区 {rc.Right}x{rc.Bottom}）");
    }

    // 显隐一律交给泵线程执行：WebView2 的对象都是 STA 的，
    // 从游戏线程直接调 vtable 属于跨单元调用，能避就避。
    private void Wv2SetVisible(BrowserTab tab, bool visible)
    {
        var ctrl = tab.Wv2Controller;
        if (ctrl == IntPtr.Zero) return;

        Wv2Post(() =>
        {
            // 排到队里时控制器可能已经被换掉/销毁了，先确认还认得这个指针
            if (tab.Wv2Controller != ctrl) return;
            try { Fn<DlgSetInt>(ctrl, 1)(ctrl, visible ? 1 : 0); } catch {   }
            if (!visible) return;
            PutBoundsToClientNow(tab);
            // 父窗口位置变了要通知一下，否则 WebView2 的命中区域会错位
            try { Fn<DlgNoArg>(ctrl, 20)(ctrl); } catch {   }
        });
    }

    // 只让活动标签可见：同一个窗口里的标签共用一块客户区，切换就是显隐互换
    private void Wv2ApplyActiveTab(BrowserWin win)
    {
        var tabs   = win.TabsSnapshot();
        var active = win.ActiveIndex;
        for (var i = 0; i < tabs.Length; i++)
        {
            var t = tabs[i];
            if (t.Wv2Controller == IntPtr.Zero) continue;
            Wv2SetVisible(t, win.Visible && i == active);
        }
    }

    private void Wv2Navigate(BrowserTab tab, string url)
    {
        var core = tab.Wv2Core;
        if (core == IntPtr.Zero) return;
        tab.Url = url;
        Wv2Post(() => { try { Fn<DlgSetStr>(core, 2)(core, url); } catch {   } });
    }

    // 37 / 38 / 28 = ICoreWebView2 元数据里的 GoBack / GoForward / Reload（已按 _wv2_all.txt 核对）
    private void Wv2GoBack(BrowserTab tab)
    {
        var core = tab.Wv2Core;
        if (core == IntPtr.Zero) return;
        Wv2Post(() => { try { Fn<DlgNoArg>(core, 37)(core); } catch {   } });
    }

    private void Wv2GoForward(BrowserTab tab)
    {
        var core = tab.Wv2Core;
        if (core == IntPtr.Zero) return;
        Wv2Post(() => { try { Fn<DlgNoArg>(core, 38)(core); } catch {   } });
    }

    private void Wv2Reload(BrowserTab tab)
    {
        var core = tab.Wv2Core;
        if (core == IntPtr.Zero) return;
        Wv2Post(() => { try { Fn<DlgNoArg>(core, 28)(core); } catch {   } });
    }

    private static bool Wv2CanGoBack(BrowserTab tab)    => tab.Wv2Core != IntPtr.Zero && GetInt(tab.Wv2Core, 35) != 0;
    private static bool Wv2CanGoForward(BrowserTab tab) => tab.Wv2Core != IntPtr.Zero && GetInt(tab.Wv2Core, 36) != 0;

    // 关掉一个标签页。控制器必须在泵线程上销毁，这里只摘引用。
    private void Wv2CloseTab(BrowserTab tab)
    {
        var win  = tab.Win;
        var ctrl = tab.Wv2Controller;
        var core = tab.Wv2Core;

        tab.Wv2Controller = IntPtr.Zero;
        tab.Wv2Core       = IntPtr.Zero;
        tab.Wv2Busy       = false;
        tab.Wv2Closed     = true;
        tab.Pending       = null;

        if (win != null) win.RemoveTab(tab);

        if (ctrl == IntPtr.Zero && core == IntPtr.Zero && tab.NewWindowArgs == IntPtr.Zero) return;

        Wv2Post(() =>
        {
            FinishNewWindow(tab);
            try { if (core != IntPtr.Zero) Fn<DlgNoArg>(core, 40)(core); } catch {   }   // Stop
            try
            {
                if (ctrl != IntPtr.Zero)
                {
                    Fn<DlgSetInt>(ctrl, 1)(ctrl, 0);   // 隐藏后再销毁，避免留残影
                    Fn<DlgNoArg>(ctrl, 21)(ctrl);      // Close
                }
            }
            catch {   }
            ReleaseRaw(core);
            ReleaseRaw(ctrl);
        });
    }

    // 关掉整个窗口：所有标签 + 宿主子窗口 + Environment
    private void Wv2CloseWin(BrowserWin win)
    {
        win.Visible = false;
        win.Closed  = true;

        var env  = win.Wv2Environment;
        var host = win.HostHwnd;
        win.Wv2Environment = IntPtr.Zero;
        win.HostHwnd       = IntPtr.Zero;
        win.EnvReady       = false;
        win.EnvCreating    = false;

        var tabs = win.TakeAllTabs();

        foreach (var t in tabs)
        {
            t.Wv2Closed = true;
            t.Wv2Busy   = false;
            t.Pending   = null;
        }

        if (dragKey.Length > 0 && dragKey == win.Key) ResetDrag(true);

        // ★ 同步先隐藏：真正的 DestroyWindow 是投递到泵线程做的，而 OnDisable 紧接着就会
        //   Cancel 掉泵线程，存在「来不及销毁」的窗口期。先同步藏起来，屏幕上就不会残留
        //   一块孤儿窗口（顺带把悬垂 WndProc 被触发的机会降到最低）。
        try { if (host != IntPtr.Zero && IsWindow(host)) ShowWindow(host, SW_HIDE); } catch {   }

        Wv2Post(() =>
        {
            // 先把抓帧会话停掉：GraphicsCaptureItem 还握着宿主窗口的引用，
            // 窗口一旦 DestroyWindow 而会话还在跑，轻则取帧失败刷屏，重则崩在 DWM 那边
            WgcStop(win);

            // 再拆宿主窗口（窗口没了，WebView2 的画面框也不会残留）
            if (host != IntPtr.Zero)
            {
                hostByHwnd.Remove(host);
                try { if (IsWindow(host)) DestroyWindow(host); } catch {   }
            }

            foreach (var t in tabs)
            {
                FinishNewWindow(t);
                var ctrl = t.Wv2Controller;
                var core = t.Wv2Core;
                t.Wv2Controller = IntPtr.Zero;
                t.Wv2Core       = IntPtr.Zero;
                try { if (core != IntPtr.Zero) Fn<DlgNoArg>(core, 40)(core); } catch {   }
                try
                {
                    if (ctrl != IntPtr.Zero)
                    {
                        Fn<DlgSetInt>(ctrl, 1)(ctrl, 0);
                        Fn<DlgNoArg>(ctrl, 21)(ctrl);
                    }
                }
                catch {   }
                ReleaseRaw(core);
                ReleaseRaw(ctrl);
            }
            ReleaseRaw(env);
        });
    }

    // ---------------------------------------------------------------
    //  回调分发（全部在 STA 线程上执行）
    // ---------------------------------------------------------------

    private void OnWv2Callback(Wv2Callback cb, int hr, IntPtr a, IntPtr b)
    {
        var win = cb.Target;
        if (win == null) return;

        // 窗口已被整体拆除，但 COM 回调仍可能迟到：把迟到创建出来的对象立刻销毁，
        // 否则会在游戏窗口上留下一个关不掉的游离子窗口
        if (win.Closed || cb.Tab?.Wv2Closed == true)
        {
            if (cb.Tab != null) FinishNewWindow(cb.Tab);
            if (hr >= 0 && a != IntPtr.Zero)
            {
                if (cb.Kind == Wv2CbKind.CtrlDone)
                {
                    var orphan = a;
                    AddRefRaw(orphan);   // 下面在回调之外用它，先 AddRef（闭包内 Release 配对）
                    Wv2Post(() =>
                    {
                        try { Fn<DlgSetInt>(orphan, 1)(orphan, 0); Fn<DlgNoArg>(orphan, 21)(orphan); } catch {   }
                        ReleaseRaw(orphan);
                    });
                }
                else if (cb.Kind == Wv2CbKind.EnvDone)
                {
                    AddRefRaw(a);
                    var orphanEnv = a;
                    Wv2Post(() => ReleaseRaw(orphanEnv));
                }
            }
            return;
        }

        var tab = cb.Tab;

        switch (cb.Kind)
        {
            case Wv2CbKind.EnvDone:
            {
                if (hr < 0 || a == IntPtr.Zero)
                {
                    win.EnvCreating = false;
                    Wv2FailWin(win, $"内置内核环境创建失败（0x{hr:X8}）" + (win.UseProxy ? "；代理仍开启，请检查代理地址。" : ""));
                    return;
                }

                // ★ environment 是借用引用，必须先 AddRef 才能留到回调之外使用
                AddRefRaw(a);
                win.Wv2Environment = a;

                var v = ReadStr(a, 2);                    // ICoreWebView2Environment::get_BrowserVersionString
                Wv2Log($"EnvDone hr=0x{hr:X8} env=0x{a:X} version={v}");
                // 接口自检门：拿不到形如 153.0.4234.48 的版本号，说明 COM 契约对不上，
                // 宁可不开浏览器也绝不继续往下调（继续调就是拿进程赌运气）
                if (!System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+\.\d+"))
                {
                    win.EnvCreating = false;
                    Wv2FailWin(win, "内置内核接口自检未通过，已中止（不会影响游戏）");
                    return;
                }
                wv2RuntimeVersion = v;

                win.EnvReady    = true;
                win.EnvCreating = false;

                // 环境就绪：把还没有控制器的标签全部补上。
                // 同一个窗口的多标签共用这一份 Environment（同一个 userData 目录），
                // 所以 Cookie / 登录态 / localStorage 都是共享的。
                foreach (var t in win.TabsSnapshot())
                {
                    if (t.Wv2Closed || t.Ready) continue;
                    var tt = t;
                    Wv2Post(() => Wv2CreateController(win, tt));
                }
                return;
            }

            case Wv2CbKind.CtrlDone:
            {
                if (tab == null) return;

                if (hr < 0 || a == IntPtr.Zero)
                {
                    Wv2Fail(tab, $"内置内核控制器创建失败（0x{hr:X8}）");
                    tab.CloseRequested = true;      // 由自绘外壳在游戏线程上把死标签摘掉
                    return;
                }

                // ★ 控制器与 CoreWebView2 同样是借用引用，而我们要长期持有它们
                //   （改尺寸、显隐、导航、关闭），必须在回调内 AddRef；
                //   配对的 Release 在 Wv2CloseTab / Wv2CloseWin 里
                AddRefRaw(a);
                var ctrl = a;
                var core = GetPtr(ctrl, 22);              // ICoreWebView2Controller::get_CoreWebView2
                if (!ComObjectOk(core))
                {
                    ReleaseRaw(ctrl);
                    Wv2Fail(tab, "内置内核初始化失败（未取到 CoreWebView2）");
                    tab.CloseRequested = true;
                    return;
                }
                AddRefRaw(core);
                Wv2Log($"CtrlDone hr=0x{hr:X8} ctrl=0x{ctrl:X} core=0x{core:X}");

                tab.Wv2Controller = ctrl;
                tab.Wv2Core       = core;

                var url = tab.Pending?.Url ?? "";
                if (url.Length > 0) tab.Url = url;

                Wv2Configure(tab);

                tab.Wv2Busy   = false;
                tab.Wv2Closed = false;
                tab.Pending   = null;

                // 标题先用网址兜一下，DocumentTitle 回来会覆盖它，标签栏不至于空着
                if (tab.Title.Length == 0 && url.Length > 0) tab.Title = url;

                if (tab.NewWindowArgs != IntPtr.Zero) FinishNewWindow(tab, core);
                else if (url.Length > 0) Wv2Navigate(tab, url);

                // ★ 必须显式置为可见。Wv2Configure 为了不闪白屏，一进来就把控制器
                //   put_IsVisible(0) 藏了起来；不补这一刀，控制器虽然建好了、位置也算好了，
                //   网页却永远不会显示出来。
                //   这里按「谁是活动标签」统一分配可见性：新开的标签立刻显示，其余藏起来。
                Wv2ApplyActiveTab(win);
                Wv2Log($"已请求显示 ctrl=0x{ctrl:X} url={url}");

                // 贴图渲染：控制器建好了、宿主窗口也在 ⇒ 现在可以开始抓帧了。
                // 放泵线程做（WgcStart 里要调 RoGetActivationFactory 和 D3D 接口，
                // 不在游戏线程上做能保证万一卡住也不会冻结渲染）。
                if (config.TextureRender) Wv2Post(() => WgcStart(win));

                // 显示动作都是 Wv2Post 异步执行的，这里再排一个自检：
                // 直接回读 get_IsVisible，把「到底可不可见」写进日志，免得下次又靠猜
                Wv2Post(() =>
                {
                    try
                    {
                        if (Fn<DlgGetInt>(ctrl, 0)(ctrl, out var vis) != 0)
                        {
                            Wv2Log("显示后自检：get_IsVisible 调用失败");
                            return;
                        }
                        Wv2Log(vis != 0 ? "显示后自检：get_IsVisible=1（已可见）"
                                        : "显示后自检：get_IsVisible=0（仍然不可见！）");
                    }
                    catch (Exception e) { Wv2Log("显示后自检异常: " + e.Message); }
                });

                if (win.ActiveTabSafe() == tab)
                {
                    statusMessage = wv2RuntimeVersion.Length > 0
                        ? $"已用内置内核 {wv2RuntimeVersion} 打开；标题栏可拖动 / 缩放"
                        : "已用内置内核打开；标题栏可拖动 / 缩放";
                }

                // 建好后把前台交还游戏：这样宏命令随时可用，用户点一下页面才把输入焦点交给网页
                FocusGameNoRestore();
                return;
            }

            case Wv2CbKind.NavStarting:
                if (tab != null && win.ActiveTabSafe() == tab) statusMessage = "内置内核加载中…";
                return;

            case Wv2CbKind.NavCompleted:
            {
                if (tab == null || tab.Wv2Core == IntPtr.Zero) return;

                // 事件参数同样是 b（a 在事件回调里是 sender）。
                // get_IsSuccess 在 ICoreWebView2NavigationCompletedEventArgs 里是元数据下标 0。
                // （以前写成 GetInt(a, 0) 是去调 ICoreWebView2::get_Settings，返回值无意义）
                var ok  = b != IntPtr.Zero && GetInt(b, 0) != 0;
                var ti  = ReadStr(tab.Wv2Core, 45);       // get_DocumentTitle
                var src = ReadStr(tab.Wv2Core, 1);        // get_Source
                if (ti.Length  > 0) tab.Title = ti;
                if (src.Length > 0) tab.Url   = src;
                if (win.ActiveTabSafe() == tab)
                    statusMessage = ok ? "" : "页面加载失败，可检查网址或代理设置";
                return;
            }

            case Wv2CbKind.DocTitle:
                if (tab == null || tab.Wv2Core == IntPtr.Zero) return;
                var docTitle = ReadStr(tab.Wv2Core, 45);  // get_DocumentTitle
                if (docTitle.Length > 0) tab.Title = docTitle;
                return;

            case Wv2CbKind.NewWindow:
            {
                // Only NewWindowRequested creates a tab; normal navigation is untouched.
                // Retain event args and defer until an unnavigated Core on the same environment is ready.
                var args = b; // a is the sender, never the event arguments.
                if (args == IntPtr.Zero) return;
                var uri = ReadStr(args, 0);
                var deferral = GetPtr(args, 6);
                if (deferral == IntPtr.Zero)
                {
                    Fn<DlgSetInt>(args, 3)(args, 1);
                    statusMessage = "网页新窗口请求无法延迟，请重试。";
                    return;
                }
                AddRefRaw(args);
                GamePost(() =>
                {
                    if (!win.Closed && windows.TryGetValue(win.Key, out var live) && ReferenceEquals(live, win))
                        OpenTab(win, uri, true, args, deferral);
                    else
                        Wv2Post(() => FinishNewWindow(new BrowserTab { NewWindowArgs = args, NewWindowDeferral = deferral }));
                });
                return;
            }

            case Wv2CbKind.WinClose:
                // 页面 JS 调 window.close()：只关掉这个标签页；
                // 关掉最后一个标签后，自绘外壳会把整个窗口收起
                if (tab != null) tab.CloseRequested = true;
                return;
        }
    }

    // ============================================================
    //  ImGui 自绘外壳
    //  · 标题栏 / 标签条 / 导航条 / 那圈边框 / 三个窗口按钮 全部自己画，
    //    完全不用系统那套非客户区；
    //  · 网页画面由宿主窗口承载，它的矩形由自绘布局算出来，再按游戏客户区的屏幕原点摆放；
    //    窗口坐标本身被 ApplyDrag 钳在客户区内 ⇒ 画面不会越出游戏窗口；
    //  · 最小化 = 整个窗口（连网页）完全隐藏，不在游戏里留任何东西；
    //  · 最大化 = 铺满游戏客户区，而不是占满整个显示器。
    // ============================================================

    private static void ResetDrag(bool releaseCapture)
    {
        dragMove  = 0;
        dragEdges = 0;
        dragKey   = "";
        dragByAlt = 0;
        if (releaseCapture && dragCaptureOwned != 0)
        {
            try { ReleaseCapture(); } catch {   }
            dragCaptureOwned = 0;
        }
    }

    private static void ResetDragIfMine(BrowserWin win)
    {
        if (dragKey.Length > 0 && dragKey == win.Key) ResetDrag(true);
    }

    // 每帧绘制回调（游戏主线程）
    private void DrawChrome()
    {
        if (RepositoryMode)
        {
            while (gameQueue.TryDequeue(out var release)) { try { release(); } catch { } }
            return;
        }
        try
        {
            RefreshUiMetrics();
            CheckGameForeground();

            // 上一帧的渲染已经提交完毕 ⇒ 现在释放泵线程交还的 GPU 资源（贴图 SRV）是安全的
            while (gameQueue.TryDequeue(out var act)) { try { act(); } catch {   } }

            if (windows.Count > 0)
            {
                HandlePendingRequests();
                PollNavFlags();

                // ★ 多窗口叠放的 z 序与输入归属（贴图模式下「渲染自由」的配套）：
                //   所有窗口的视觉都画在同一张 ForegroundDrawList 上，谁后画谁在上面 ——
                //   所以「谁在上面」这件事从此由我们自己决定，不再受原生窗口的 DWM 层级摆布。
                //   winZ 的尾巴就是最上面那个；点谁谁提到尾巴（见 DrawChromeContent）。
                SyncWinZ();
                inputTopKey = "";

                foreach (var win in OrderedWins())
                {

                    try { DrawOneWindow(win); }
                    catch (Exception e) { Wv2Log("绘制外壳异常: " + e); }
                }

                // ★ 键盘：归属规则跟浏览器一致 —— 点一下网页画面，键盘归网页；点一下浏览器之外，
                //   键盘还给游戏。归属期间由我们把 ImGui 的 WantCaptureKeyboard 钉死为真
                //   （Dalamud 后端据它决定要不要把按键吞掉），按键本身再经 CDP 注入网页
                //   —— 这是贴图模式下唯一能打字的路（宿主窗口永远不是活动窗口，真实键盘焦点拿不到）。
                WgcUpdateKbOwner();
                // 归属的窗口已经关了 ⇒ 立刻交还，别让游戏一直收不到键盘
                if (wgcKbOwnerKey.Length > 0 && !windows.ContainsKey(wgcKbOwnerKey)) wgcKbOwnerKey = "";
                var kbWin = config.TextureRender && wgcKbOwnerKey.Length > 0 && windows.TryGetValue(wgcKbOwnerKey, out var kw) && TextureModeActive(kw) ? kw : null;
                if (kbWin != null)
                {
                    var kbIo = ImGui.GetIO();
                    kbIo.WantCaptureKeyboard = true;
                    ImGui.SetNextFrameWantCaptureKeyboard(true);
                }
                WgcForwardKeys(kbWin?.ActiveTabSafe()?.Wv2Core ?? IntPtr.Zero, kbWin != null);
            }
        }
        catch (Exception e) { Wv2Log("DrawChrome 异常: " + e); }
        DrawBrowserDialog();

        try { HandleDragRelease(); } catch {   }
    }

    // ★ 为什么**不再**用 ImGuiConfigFlags.NoMouse 挡点击（走过的弯路，别再试）
    //
    // 语义上它是刚好合用的。imgui.cpp 的 UpdateHoveredWindowAndCaptureFlags 里：
    //     // Disabled mouse hovering (we don't currently clear MousePos, we could)
    //     if (io.ConfigFlags & ImGuiConfigFlags_NoMouse) clear_hovered_windows = true;
    //     ...
    //     if (clear_hovered_windows)
    //         g.HoveredWindow = g.HoveredWindowUnderMovingWindow = NULL;
    // 只把 HoveredWindow 清成 NULL，**不动 MousePos / MouseDown / MouseWheel**
    // （源码注释原文就是 "we don't currently clear MousePos"）。
    //
    // 但卫月主动禁用了这个标志，而且每帧清一次：
    //     Dalamud/Interface/Internal/InterfaceManager.cs
    //         private void OnNewInputFrame()
    //         {
    //             var io = ImGui.GetIO();
    //             // Prevent setting the footgun from ImGui Demo; the Space key isn't
    //             // removing the flag at the moment.
    //             io.ConfigFlags &= ~ImGuiConfigFlags.NoMouse;
    // OnNewInputFrame 挂在后端的 NewInputFrame 事件上，而 Dx11Win32Backend.Render() 的顺序是：
    //     imguiInput.NewFrame();  NewInputFrame?.Invoke();  ImGui.NewFrame();  BuildUi?.Invoke();
    // 清标志在前、NewFrame 读标志在后 ⇒ 我们在这里置的位永远来不及被读到。
    // 实机日志也印证了：`ImGui 鼠标屏蔽：开` 每帧都在刷，而 `关` 一条都没有 ——
    // 说明每次进 DrawChrome 时那一位都已经没了（1062 条「开」/ 0 条「关」）。
    //
    // 所以改成「让浏览器窗口自己成为 ImGui 的 HoveredWindow」，
    // 见 DrawOneWindow 里 ImGui.Begin 之后那段 BringWindowToDisplayFront。

    // 窗口叠放顺序：末尾 = 最上层。默认（未登记过的）按可见窗口补到末尾。
    private static readonly List<string> winZ = new();
    private static string inputTopKey = "";
    private static string wgcMouseOwnerKey = "";
    // 当前正在绘制的窗口是否有权处理输入（只有鼠标正下方最上面那个才有）
    private static bool uiInputOn = true;

    private void SyncWinZ()
    {
        foreach (var kv in windows)
        {
            if (!kv.Value.Visible) continue;
            if (!winZ.Contains(kv.Key)) winZ.Add(kv.Key);
        }
        winZ.RemoveAll(k => !windows.TryGetValue(k, out var w) || !w.Visible);

        // 不可见的窗口不再参与「鼠标压在谁身上」的判定，否则点空处会被它吃掉
        foreach (var k in winKeyCache.Keys.ToList())
            if (!windows.TryGetValue(k, out var sw) || !sw.Visible) winKeyCache.Remove(k);
    }

    private static void RaiseWin(string key)
    {
        if (winZ.Count > 0 && winZ[winZ.Count - 1] == key) return;
        winZ.Remove(key);
        winZ.Add(key);
    }

    private IEnumerable<BrowserWin> OrderedWins()
    {
        foreach (var k in winZ.ToList())
            if (windows.TryGetValue(k, out var w)) yield return w;
        foreach (var kv in windows)
            if (!winZ.Contains(kv.Key)) yield return kv.Value;
    }

    // 鼠标正下方最上面那个窗口：从 winZ 末尾往前找第一个矩形命中的。
    // 找不到（鼠标在任何浏览器窗口之外）就返回空串 —— 此时谁也不该处理输入。
    private static string TopKeyAtMouse()
    {
        var m = ImGui.GetIO().MousePos;
        for (var i = winZ.Count - 1; i >= 0; i--)
        {
            if (!winKeyCache.TryGetValue(winZ[i], out var r)) continue;
            if (m.X >= r.X && m.X <= r.X + r.Z && m.Y >= r.Y && m.Y <= r.Y + r.W) return winZ[i];
        }
        return "";
    }

    // 窗口矩形缓存（TopKeyAtMouse 要在绘制之前判定归属，那时本帧的几何还没算，
    // 用上一帧的矩形足够 —— 拖拽时最多差一帧，不会点错窗口）
    private static readonly Dictionary<string, Vector4> winKeyCache = new();

    // 泵线程 / 内核留下的请求，统一在游戏线程上执行（避免两个线程同时改标签列表）
    private void HandlePendingRequests()
    {
        var now  = Environment.TickCount64;
        List<BrowserWin>? dead = null;

        foreach (var kv in windows.ToList())
        {
            var win = kv.Value;

            // 标签级的关闭请求：页面 window.close() / 控制器创建失败
            foreach (var t in win.TabsSnapshot())
                if (t.CloseRequested) Wv2CloseTab(t);

            // 启动超时保护：25 秒还没建出来的标签摘掉，别让窗口一直白着
            if (win.Visible && win.LaunchStartMs > 0 && now - win.LaunchStartMs > 25_000)
            {
                win.LaunchStartMs = 0;
                foreach (var t in win.TabsSnapshot())
                {
                    if (t.Ready || t.Wv2Closed) continue;
                    Wv2Fail(t, "内置内核启动超时：请检查 WebView2 运行时与代理设置");
                    Wv2CloseTab(t);
                }
            }

            // 整个窗口的关闭请求（自绘的 ✕ / 系统兜底 WM_CLOSE）
            if (win.CloseRequested)
            {
                win.CloseRequested = false;
                if (dead == null) dead = new List<BrowserWin>();
                dead.Add(win);
            }
        }

        if (dead == null) return;

        foreach (var w in dead)
        {
            ResetDragIfMine(w);
            Wv2CloseWin(w);
            windows.Remove(w.Key);
        }
        FocusGameNoRestore();
    }

    // 后退/前进是否可用：这两个属性读的是 COM，必须在泵线程（STA）上取，
    // 取回来只写一个 bool，游戏线程读它来画按钮的亮/灰。
    private void PollNavFlags()
    {
        var now = Environment.TickCount64;
        if (now < navPollNextMs) return;
        navPollNextMs = now + 250;

        foreach (var win in windows.Values.ToList())
        {
            if (!win.Visible) continue;
            foreach (var t in win.TabsSnapshot())
            {
                if (!t.Ready) continue;
                var tt = t;
                Wv2Post(() =>
                {
                    if (tt.Wv2Core == IntPtr.Zero) return;
                    tt.CanBack = Wv2CanGoBack(tt);
                    tt.CanFwd  = Wv2CanGoForward(tt);
                });
            }
        }
    }

    private void HandleDragRelease()
    {
        if (dragCaptureOwned == 0) return;

        // Alt 拖动是在网页画面上按下的，ImGui 那边可能压根没看到这次按下，
        // 松手判断也必须走系统按键状态，否则会一直以为还按着、拖拽「粘住」。
        var holding = dragByAlt != 0 ? IsLButtonDown() : ImGui.IsMouseDown(ImGuiMouseButton.Left);
        if (holding) return;

        var key = dragKey;
        ResetDrag(true);

        if (key.Length > 0 && windows.TryGetValue(key, out var w)) SaveGeometryNow(w);
    }

    private void DrawOneWindow(BrowserWin win)
    {
        var tabs = win.TabsSnapshot();

        if (!win.Visible || tabs.Length == 0)
        {
            ResetDragIfMine(win);
            HideHost(win);
            return;
        }

        // ★ 游戏最小化时把网页一起藏起来：贴图模式下宿主**故意没有 owner**，
        //   不会跟着游戏最小化，不藏的话桌面上会留一块孤零零的原生网页窗口。
        //   普通「失焦」**不再隐藏**（2026-10-10 用户明确要求）——切出去查攻略浏览器必须还在；
        //   游戏被别的窗口挡住时它跟着被挡、切走后仍浮在游戏画面上，行为自洽。
        //   拖动过程中不参与判断，免得手一抖把窗口藏掉。
        if (gameMinimized && dragMove == 0 && dragEdges == 0)
        {
            ResetDragIfMine(win);
            HideHost(win);
            return;
        }

        if (win.W <= 1f || win.H <= 1f) InitWinRect(win);

        var io = ImGui.GetIO();

        // 最大化：在游戏窗口内铺满。DisplaySize 就是游戏客户区尺寸，
        // 它本身就是「游戏窗口以内」，所以「全屏」永远不会占满整个显示器。
        if (win.Maximized)
        {
            win.X = 0f;
            win.Y = 0f;
            win.W = MathF.Max(MinWinW, io.DisplaySize.X);
            win.H = MathF.Max(MinWinH, io.DisplaySize.Y);
        }

        // 拖拽/缩放的几何要先算好，再拿它去开 ImGui 窗口，免得画面比鼠标慢一帧
        ApplyDrag(win);
        ApplyPageRect(win);
        // 宿主是无边框顶层窗口，不会自己跟着游戏跑 ⇒ 每帧同步一次它的屏幕位置。
        // 游戏窗口被拖动/改大小、或上面刚改了几何，宿主都会立刻跟上；
        // 方法内部有脏检查，几何没变时不会真的去调 SetWindowPos。
        ApplyHostGeometry(win);

        // 贴图渲染：每帧向抓帧池要一帧（池是自由线程的，游戏线程直接轮询是安全的）。
        // 拿到的新画面会做成本帧的 SRV，DrawChromeContent 里按贴图画出来。
        // 内部有失败计数：启动 5 秒还一帧都没有 ⇒ 自动回退原生窗口（保证浏览器永远能用）。
        WgcPollFrame(win);

        var mn = new Vector2(win.X, win.Y);
        var mx = new Vector2(win.X + win.W, win.Y + win.H);

        // 这个 ImGui 窗口本身**不画任何东西**（NoBackground + 边框 0），
        // 所有视觉都由 DrawChromeContent 画在 ForegroundDrawList 上（永远最上层）。
        // 它在这儿干两件事：
        //   ① 给 ImGui 留一个「鼠标确实压在浏览器上」的载体 —— 鼠标落在它里面时，
        //      ImGui 自己算出来的 WantCaptureMouse / hover 链是通的；
        //   ② 鼠标压着它时被提到窗口栈最前面（见下面 Begin 之后的 BringWindowToDisplayFront），
        //      于是 ImGui 认定的 HoveredWindow 就是它，点击只落到它身上 ——
        //      压在浏览器底下的 Omni / 卫月窗口既不聚焦、也不会被拖走。
        //
        // 注意：**不要**给它加 ImGuiWindowFlags.NoInputs（= NoMouseInputs）。
        // 加了之后 ImGui 在找 HoveredWindow 时会直接跳过这个窗口（FindHoveredWindowEx 里
        // 有 `if (window->Flags & ImGuiWindowFlags_NoMouseInputs) continue;`），
        // 它就不再是那个载体，上面两条全部失效。
        //
        // NoBringToFrontOnFocus 继续保留：它只影响 FocusWindow 的自动置顶，
        // 而我们要的置顶是自己显式调的 BringWindowToDisplayFront —— 那条路不受此标志影响，
        // 也就顺带避开了「抢焦点 / 关别人的弹窗 / 清别人的 ActiveId」这些副作用。
        ImGuiWindowFlags wf =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

        ImGui.SetNextWindowPos(mn);
        ImGui.SetNextWindowSize(new Vector2(win.W, win.H));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(48f, 48f));

        bool pointerInInputRow = win.InputRowHeight > 0 && IsMouseInRect(
            new Vector2(win.X + ChromePad, win.Y + ChromePad + TabRowH + NavRowH), new Vector2(win.X + win.W - ChromePad, win.Y + ChromePad + TabRowH + NavRowH + win.InputRowHeight));
        if (pointerInInputRow) wf |= ImGuiWindowFlags.NoMouseInputs;
        var open = ImGui.Begin("##gwb_win_" + win.Key, wf);

        // 使用 ImGui 的实际 hover 层级；被其他窗口遮挡时不接收网页输入。
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows);
        if (hovered) inputTopKey = win.Key;
        uiInputOn = hovered || win.Key == dragKey || win.Key == wgcMouseOwnerKey;
        try   { if (open) DrawChromeContent(win, tabs, mn, mx); }
        catch (Exception e) { Wv2Log("外壳内容绘制异常: " + e); }
        uiInputOn = true;
        ImGui.End();
        ImGui.PopStyleVar(4);

        // 记下本帧矩形，给下一帧的 TopKeyAtMouse 判定「鼠标压在谁身上」用
        winKeyCache[win.Key] = new Vector4(mn.X, mn.Y, win.W, win.H);
    }

    // 拖拽中的几何更新（在布局之前调用）
    private static void ApplyDrag(BrowserWin win)
    {
        if (dragKey != win.Key) return;
        if (dragMove == 0 && dragEdges == 0) return;

        var io = ImGui.GetIO();
        var ds = io.DisplaySize;

        // Alt 拖动是在**网页画面**上发起的：那一刻游戏窗口收不到鼠标消息，
        // ImGui 的 MousePos 会停在旧位置，必须改成直接向系统要光标坐标。
        var mouse = io.MousePos;
        if (dragByAlt != 0 && TryGetMouseInGameUi(out var mp)) mouse = mp;

        var d = mouse - dragStartMouse;

        if (dragMove != 0)
        {
            // 窗口必须完整留在游戏客户区内（2026-10-10 用户要求，见 ClampInsideGame）。
            win.W = dragW;
            win.H = dragH;
            win.X = dragX + d.X;
            win.Y = dragY + d.Y;
            ClampInsideGame(win, ds);
            return;
        }

        var x0 = dragX;            var y0 = dragY;
        var x1 = dragX + dragW;    var y1 = dragY + dragH;

        // 缩放：允许边拖到任意位置，最终由 ClampInsideGame 把越出的部分收回来
        if ((dragEdges & EdgeLeft)   != 0) x0 = MathF.Min(x0 + d.X, x1 - MinWinW);
        if ((dragEdges & EdgeRight)  != 0) x1 = MathF.Max(x1 + d.X, x0 + MinWinW);
        if ((dragEdges & EdgeTop)    != 0) y0 = MathF.Min(y0 + d.Y, y1 - MinWinH);
        if ((dragEdges & EdgeBottom) != 0) y1 = MathF.Max(y1 + d.Y, y0 + MinWinH);

        win.X = x0; win.Y = y0;
        win.W = x1 - x0; win.H = y1 - y0;
        ClampInsideGame(win, ds);
    }

    // 窗口必须**完整**留在游戏客户区（ImGui 坐标 = ds）内，一点都不能越出游戏窗口。
    // 移动时只钳位置；缩放时连尺寸一起钳 —— 边拖出游戏多少，就往回收多少。
    private static void ClampInsideGame(BrowserWin win, Vector2 ds)
    {
        // 窗口比游戏客户区还大（异常配置 / 拖缩放越过对面边界）：先把尺寸压回去
        if (win.W > ds.X) win.W = MathF.Max(MinWinW, ds.X);
        if (win.H > ds.Y) win.H = MathF.Max(MinWinH, ds.Y);

        var maxX = MathF.Max(0f, ds.X - win.W);
        var maxY = MathF.Max(0f, ds.Y - win.H);

        win.X = Math.Clamp(win.X, 0f, maxX);
        win.Y = Math.Clamp(win.Y, 0f, maxY);
    }

    // 系统光标在 ImGui 坐标系里的位置。
    // 网页画面是原生窗口，鼠标压在上面时游戏窗口收不到消息，ImGui 的 MousePos 不可信，
    // 所以这里绕过 ImGui，直接从系统取「屏幕坐标 → 游戏客户区像素 → ImGui 坐标」。
    private static bool TryGetMouseInGameUi(out Vector2 pos)
    {
        pos = default;
        if (!GetCursorPos(out var pt)) return false;

        var game = FindGameHwnd();
        if (game == IntPtr.Zero) return false;
        if (!ScreenToClient(game, ref pt)) return false;

        var sx = uiScaleX > 0.05f ? uiScaleX : 1f;
        var sy = uiScaleY > 0.05f ? uiScaleY : 1f;
        pos = new Vector2(pt.X * sx, pt.Y * sy);
        return true;
    }

    private static bool IsLButtonDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    private static bool IsAltDown()     => (GetAsyncKeyState(VK_MENU)    & 0x8000) != 0;

    // ============================================================
    //  自绘内容
    // ============================================================

    private enum GlyphKind { Min, Max, Restore, Close, Plus, Back, Forward, Reload }

    private void DrawChromeContent(BrowserWin win, BrowserTab[] tabs, Vector2 mn, Vector2 mx)
    {
        var io   = ImGui.GetIO();
        // ★ 画到 ForegroundDrawList 上，而不是当前窗口的 WindowDrawList。
        //   Foreground 是 ImGui 每帧**最后**渲染的那个 drawlist，永远压在其它插件的
        //   ImGui 窗口之上 —— 这就是「别的插件面板把浏览器外壳盖住」的解法。
        //   （窗口本身仍然保留，见 DrawOneWindow：它现在只负责让 ImGui 置起
        //     WantCaptureMouse，这样点外壳时鼠标消息不会同时漏给游戏。）
        var dl   = ImGui.GetWindowDrawList();
        var skin = GetSkin();

        // 点谁谁上来：外壳视觉全部画在同一张 Foreground 列表上，**绘制顺序就是层级顺序**。
        // 以前层级由原生窗口的 DWM 决定，点 A 的标题栏压根不会把 A 提到前面；
        // 现在层级完全由自己掌控，点一下就把它挪到 winZ 末尾（下一帧起在最上面）。
        if (uiInputOn && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) RaiseWin(win.Key);

        var pad  = ChromePad;
        var tabH = TabRowH;
        var rad  = MathF.Max(4f, skin.Radius);
        var bth  = MathF.Max(1f, skin.BorderThickness);

        var cSurface = ImGui.GetColorU32(skin.Surface);
        var cBg      = ImGui.GetColorU32(skin.Background);
        var cBorder  = ImGui.GetColorU32(skin.Border);
        var cText    = ImGui.GetColorU32(skin.Text);
        var cDim     = ImGui.GetColorU32(new Vector4(skin.Text.X, skin.Text.Y, skin.Text.Z, skin.Text.W * 0.50f));
        var cAccent  = ImGui.GetColorU32(skin.Accent);
        var cHover   = ImGui.GetColorU32(skin.Hover);
        var cActive  = ImGui.GetColorU32(skin.Active);
        var cErr     = ImGui.GetColorU32(skin.Error);
        var cHi      = ImGui.GetColorU32(new Vector4(skin.Text.X, skin.Text.Y, skin.Text.Z,
                                                     Math.Clamp(skin.HighlightStrength, 0f, 0.30f)));

        // ---- 外框：底板 + Omni 边框，网页画面之外那一圈就是它 ----
        if (win.InputRowHeight > 0)
        {
            var rowTop = win.Y + ChromePad + TabRowH + NavRowH;
            dl.AddRectFilled(mn, new Vector2(mx.X, rowTop), cSurface, rad);
            dl.AddRectFilled(new Vector2(mn.X, rowTop + win.InputRowHeight), mx, cSurface, rad);
        }
        else dl.AddRectFilled(mn, mx, cSurface, rad);

        // 外壳（标签条 + 导航条）落在暗一档的底上，和网页画面区分开
        dl.AddRectFilled(new Vector2(mn.X + bth, mn.Y + bth),
                         new Vector2(mx.X - bth, win.PageY - pad), cBg, MathF.Max(1f, rad - bth));

        // ---- 网页画面那圈边框：描边的内沿正好压在画面边界上，外侧留出 Omni 的底色 ----
        dl.AddRect(new Vector2(win.PageX - bth, win.PageY - bth),
                   new Vector2(win.PageX + win.PageW + bth, win.PageY + win.PageH + bth),
                   cBorder, rad * 0.45f, ImDrawFlags.None, bth * 2f);

        dl.AddRect(mn, mx, cBorder, rad, ImDrawFlags.None, bth);

        // 顶部一道高光，Omni 的玻璃质感就靠它
        if (skin.HighlightStrength > 0.01f)
            dl.AddLine(new Vector2(mn.X + rad, mn.Y + bth * 0.5f),
                       new Vector2(mx.X - rad, mn.Y + bth * 0.5f), cHi, bth);

        // ---- 网页画面：贴图模式下它只是**一块 ImGui 贴图**（渲染自由的核心），
        //      画在外壳底板之上、标签条/导航条之下，所以它天然会被任何后来的 ImGui 内容盖住/裁剪。
        var pageMn = new Vector2(win.PageX, win.PageY);
        var pageMx = new Vector2(win.PageX + win.PageW, win.PageY + win.PageH);
        if (config.TextureRender)
        {
            dl.PushClipRect(pageMn + new Vector2(0, win.InputRowHeight), pageMx, true);
            DrawPageTexture(dl, win, pageMn, pageMx);
            dl.PopClipRect();
        }

        var consumed = false;

        // ================= 第一行：标签条 + 右上角三个窗口按钮 =================
        var rowY0 = mn.Y + pad;
        var rowY1 = rowY0 + tabH;

        var stripX0 = mn.X + pad;
        var stripX1 = mx.X - pad;

        var btnH  = MathF.Max(14f, tabH - 7f);
        var btnW  = MathF.Max(24f, btnH * 1.5f);
        var btnY0 = rowY0 + (tabH - btnH) * 0.5f;
        var btnY1 = btnY0 + btnH;

        var closeX1 = stripX1;
        var closeX0 = closeX1 - btnW;
        var maxX1   = closeX0 - 2f;  var maxX0 = maxX1 - btnW;
        var minX1   = maxX0  - 2f;   var minX0 = minX1 - btnW;

        // ---- ★ 导航按钮（后退/前进/刷新）：从第二排挪到第一排最左（2026-10-10 UI 调整） ----
        //   原第二排只剩这三个按钮 + 一段标题文字；按钮上移、标题删除后第二排整排取消，
        //   网页画面从原来 navH 的位置直接上移（NavRowHeight 已置 0，见常量定义处）。
        var btnRad = rad * 0.55f;
        var navT    = win.ActiveTabSafe();
        var canBack = navT != null && navT.CanBack;
        var canFwd  = navT != null && navT.CanFwd;

        var nx = stripX0 + 2f;
        var backMn = new Vector2(nx, btnY0);
        var backMx = new Vector2(nx + btnW, btnY1);
        nx = backMx.X + 2f;
        var fwdMn = new Vector2(nx, btnY0);
        var fwdMx = new Vector2(nx + btnW, btnY1);
        nx = fwdMx.X + 2f;
        var relMn = new Vector2(nx, btnY0);
        var relMx = new Vector2(nx + btnW, btnY1);

        var clickBack = NavBtn(dl, "##gbback", backMn, backMx, btnRad, cHover, cActive,
                               canBack ? cText : cDim, canBack ? cText : cDim, GlyphKind.Back);
        var clickFwd = NavBtn(dl, "##gbfwd", fwdMn, fwdMx, btnRad, cHover, cActive,
                              canFwd ? cText : cDim, canFwd ? cText : cDim, GlyphKind.Forward);
        var clickRel = NavBtn(dl, "##gbrel", relMn, relMx, btnRad, cHover, cActive, cText, cText, GlyphKind.Reload);
        consumed |= clickBack || clickFwd || clickRel;

        var tabsX0 = relMx.X + 6f;
        var proxyWidth = MathF.Max(48f, ImGui.CalcTextSize("代理").X + btnH + 10f);
        var favoritesWidth = MathF.Max(44f, ImGui.CalcTextSize("收藏").X + 12f);
        var addressWidth = MathF.Max(30f, ImGui.CalcTextSize("网址").X + 10f);
        var addressMx = new Vector2(minX0 - 4f, btnY1);
        var addressMn = new Vector2(addressMx.X - addressWidth, btnY0);
        var favoritesMx = new Vector2(addressMn.X - 4f, btnY1);
        var favoritesMn = new Vector2(favoritesMx.X - favoritesWidth, btnY0);
        var proxyMx = new Vector2(favoritesMn.X - 4f, btnY1);
        var proxyMn = new Vector2(proxyMx.X - proxyWidth, btnY0);
        var renderLabel = TextureModeActive(win) ? "抽帧" : "原生";
        var renderWidth = MathF.Max(44f, ImGui.CalcTextSize(renderLabel).X + 12f);
        var renderMx = new Vector2(proxyMn.X - 4f, btnY1);
        var renderMn = new Vector2(renderMx.X - renderWidth, btnY0);
        var tabsX1 = MathF.Max(tabsX0 + 20f, renderMn.X - 4f);
        consumed |= IsMouseInRect(renderMn, renderMx);
        ChromeTextButton(dl, renderMn, renderMx, renderLabel, cDim, cHover);
        if (IsMouseInRect(renderMn, renderMx))
            ImGui.SetTooltip("在 OMNI 设置面板的“渲染版本”下拉框中选择抽帧或仓库原生浏览器。");
        var proxyHover = IsMouseInRect(proxyMn, proxyMx);
        var proxyClick = proxyHover && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        consumed |= proxyHover;
        var checkSide = MathF.Min(btnH - 2, 15f);
        var check = new Vector2(proxyMn.X + 3, (btnY0 + btnY1 - checkSide) / 2);
        dl.AddRect(check, check + new Vector2(checkSide), win.UseProxy ? cAccent : cDim, 2, ImDrawFlags.None, 1.2f);
        if (win.UseProxy)
        {
            dl.AddLine(check + new Vector2(3, checkSide * 0.52f), check + new Vector2(checkSide * 0.44f, checkSide - 3), cText, 1.8f);
            dl.AddLine(check + new Vector2(checkSide * 0.44f, checkSide - 3), check + new Vector2(checkSide - 2, 3), cText, 1.8f);
        }
        dl.AddText(new Vector2(check.X + checkSide + 4, (btnY0 + btnY1 - ImGui.GetFontSize()) / 2), proxyHover ? cText : cDim, "代理");
        if (proxyHover && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) ShowBrowserDialog("proxy", win);
        consumed |= IsMouseInRect(addressMn, addressMx);
        if (ChromeTextButton(dl, addressMn, addressMx, "网址", cText, cHover)) ShowBrowserDialog("address", win);
        var favoriteClick = ChromeTextButton(dl, favoritesMn, favoritesMx, "收藏", cText, win.FavoritesOpen ? cActive : cHover);
        consumed |= IsMouseInRect(favoritesMn, favoritesMx);
        if (favoriteClick) { win.FavoritesOpen = !win.FavoritesOpen; ApplyPageRect(win); }

        // ---- 标签页 ----
        var tabCount = tabs.Length;
        var gap      = 3f;
        var tabW     = MathF.Min(OmniScale(190f), (tabsX1 - tabsX0 - gap * (tabCount - 1)) / MathF.Max(1, tabCount));
        tabW = MathF.Max(54f, tabW);

        var active = win.ActiveIndex;
        var switchTo = -1;
        var closeTab = -1;

        dl.PushClipRect(new Vector2(tabsX0, rowY0), new Vector2(tabsX1, rowY1), true);
        for (var i = 0; i < tabCount; i++)
        {
            var tx0 = tabsX0 + i * (tabW + gap);
            if (tx0 >= tabsX1) break;
            var tx1 = MathF.Min(tx0 + tabW, tabsX1);

            var t  = tabs[i];
            var isAct = i == active;

            var tmn = new Vector2(tx0 + 1f, rowY0 + 2f);
            var tmx = new Vector2(tx1 - 1f, rowY1 - 1f);

            // 关标签的小叉占右侧一小块，主体按钮只占左边，两者不重叠
            var cxx   = tmx.X - MathF.Min(13f, (tmx.X - tmn.X) * 0.22f);
            var bodyX = MathF.Max(tmn.X + 2f, cxx - 10f);

            // 命中测试同样自算（理由见 ChromeBtn）：外壳画在 Foreground 上，
            // ImGui 那套按窗口层级的 hover 判定已经不可靠了
            var tHov = IsMouseInRect(tmn, new Vector2(MathF.Max(tmn.X + 2f, bodyX), MathF.Max(tmn.Y + 2f, tmx.Y)));
            var tCli = tHov && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
            consumed |= tHov || tCli;

            var closeHov = false;
            var closeCli = false;
            if (tmx.X - tmn.X > 52f)
            {
                var cmn = new Vector2(cxx - 7f, (tmn.Y + tmx.Y) * 0.5f - 7f);
                var cmx = new Vector2(cmn.X + 14f, cmn.Y + 14f);
                closeHov = IsMouseInRect(cmn, cmx);
                closeCli = closeHov && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
                consumed |= closeHov || closeCli;
            }

            // 底
            var fill = isAct ? cSurface : (tHov ? cHover : 0u);
            if (fill != 0u) dl.AddRectFilled(tmn, tmx, fill, rad * 0.45f);
            if (isAct)
                dl.AddLine(new Vector2(tmn.X + 4f, tmx.Y), new Vector2(tmx.X - 4f, tmx.Y), cAccent, MathF.Max(1.5f, bth * 1.6f));

            // 标题
            var title = t.Title.Length > 0 ? t.Title : (t.Url.Length > 0 ? t.Url : win.PageName);
            if (title.Length == 0) title = "新标签页";
            if (!t.Ready) title = "载入中… " + title;

            dl.PushClipRect(new Vector2(tmn.X + 6f, tmn.Y), new Vector2(bodyX - 3f, tmx.Y), true);
            var tsz = ImGui.CalcTextSize(title);
            dl.AddText(new Vector2(tmn.X + 6f, (tmn.Y + tmx.Y - tsz.Y) * 0.5f),
                       isAct ? cText : cDim, title);
            dl.PopClipRect();

            // 小叉
            if (tmx.X - tmn.X > 52f)
            {
                var cc = new Vector2(cxx, (tmn.Y + tmx.Y) * 0.5f);
                if (closeHov) dl.AddCircleFilled(cc, 7.5f, cHover);
                DrawGlyph(dl, GlyphKind.Close, cc, 3.4f, closeHov ? cText : cDim, MathF.Max(1.1f, bth));
            }

            if (tCli) switchTo = i;
            if (closeCli) closeTab = i;
        }
        dl.PopClipRect();

        // ---- 右上角三个按钮：最小化 / 最大化(还原) / 关闭，全部重画 ----
        // （btnRad 已在上方导航按钮处声明，这里直接复用）
        var clickMin = ChromeBtn(dl, "##gbmin", new Vector2(minX0, btnY0), new Vector2(minX1, btnY1),
                                 btnRad, cHover, cActive, cText, cText, GlyphKind.Min);
        var clickMax = ChromeBtn(dl, "##gbmax", new Vector2(maxX0, btnY0), new Vector2(maxX1, btnY1),
                                 btnRad, cHover, cActive, cText, cText,
                                 win.Maximized ? GlyphKind.Restore : GlyphKind.Max);
        var clickClose = ChromeBtn(dl, "##gbclose", new Vector2(closeX0, btnY0), new Vector2(closeX1, btnY1),
                                   btnRad, cErr, cErr, cText, cText, GlyphKind.Close);
        consumed |= clickMin || clickMax || clickClose;

        // ================= 第二排：已取消（2026-10-10 UI 调整） =================
        // 导航按钮挪去了第一排最左、标题文字删除，整排不再占高度 —— NavRowHeight 置 0，
        // ApplyPageRect 里 py/ph 的公式不变，网页画面自动上移补齐这块空间。
        // （旧代码：navY0/navY1 三个按钮 + 右侧 title2 文字，整段删除。）

        // ================= 交互动作 =================
        if (proxyClick)
        {
            if (!win.UseProxy && string.IsNullOrWhiteSpace(config.Proxy)) ShowBrowserDialog("proxy", win);
            else ChangeWindowProxy(win, !win.UseProxy);
            return;
        }
        if (win.FavoritesOpen) consumed |= DrawFavoritePanel(dl, win, navT, mx, cText, cDim, cHover, cBorder);
        if (switchTo >= 0 && switchTo != active)
        {
            win.Active = switchTo;
            Wv2ApplyActiveTab(win);
        }

        if (closeTab >= 0)
        {
            Wv2CloseTab(tabs[closeTab]);
            if (win.TabCount == 0)
            {
                win.Visible = false;
                Wv2HideWin(win);
                statusMessage = "标签页已全部关闭，再按一次宏命令即可重新打开";
            }
            return;
        }

        if (clickMin)
        {
            // 「最小化」= 完全隐藏：网页连外壳一起消失，游戏里不留任何痕迹
            ResetDragIfMine(win);
            win.Visible = false;
            Wv2HideWin(win);
            statusMessage = "已隐藏，再按一次宏命令即可重新显示";
            return;
        }

        if (clickMax)
        {
            if (win.Maximized)
            {
                win.Maximized = false;
                win.W = MathF.Max(MinWinW, win.RestoreW);
                win.H = MathF.Max(MinWinH, win.RestoreH);
                win.X = win.RestoreX;
                win.Y = win.RestoreY;
                ClampInsideGame(win, io.DisplaySize);    // 还原位置也压回游戏客户区内
            }
            else
            {
                win.RestoreX = win.X; win.RestoreY = win.Y;
                win.RestoreW = win.W; win.RestoreH = win.H;
                win.Maximized = true;
                win.X = 0f; win.Y = 0f;
                win.W = MathF.Max(MinWinW, io.DisplaySize.X);
                win.H = MathF.Max(MinWinH, io.DisplaySize.Y);
            }
            ResetDragIfMine(win);
            ApplyPageRect(win);
        }

        if (clickClose)
        {
            win.CloseRequested = true;   // 下一帧由 HandlePendingRequests 完整拆除
            return;
        }

        if (clickBack && navT != null)    Wv2GoBack(navT);
        if (clickFwd && navT != null)     Wv2GoForward(navT);
        if (clickRel && navT != null)     Wv2Reload(navT);

        // 右下角缩放手柄先处理输入，避免按下时同时点到网页。
        if (!win.Maximized)
        {
            var handleSize = OmniScale(24);
            var corner = mx - new Vector2(MathF.Max(2, bth));
            var handleMin = corner - new Vector2(handleSize);
            var resizing = dragKey == win.Key && (dragEdges & (EdgeRight | EdgeBottom)) == (EdgeRight | EdgeBottom);
            var handleHover = IsMouseInRect(handleMin, corner) && wgcMouseOwnerKey.Length == 0;
            var shade = ImGui.GetColorU32(new Vector4(skin.Text.X, skin.Text.Y, skin.Text.Z, skin.Text.W * 0.22f));
            var a = new Vector2(corner.X - handleSize, corner.Y);
            var b = new Vector2(corner.X, corner.Y - handleSize);
            dl.AddTriangleFilled(a + new Vector2(1), b + new Vector2(1), corner + new Vector2(1), 0x30000000);
            dl.AddTriangleFilled(a, b, corner, resizing ? cActive : handleHover ? cAccent : shade);
            for (var line = 0; line < 2; line++)
            {
                var inset = OmniScale(5 + line * 5);
                dl.AddLine(new Vector2(corner.X - inset - OmniScale(6), corner.Y - inset),
                           new Vector2(corner.X - inset, corner.Y - inset - OmniScale(6)), cDim, MathF.Max(1, bth));
            }
            consumed |= handleHover || (dragKey == win.Key && dragEdges != 0);
            if (handleHover && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && dragKey.Length == 0)
                StartDrag(win, io.MousePos, EdgeRight | EdgeBottom, false);
        }

        // ================= 贴图模式的鼠标转发 =================
        // 贴图模式下网页不再是原生窗口：鼠标消息不会被系统送给 WebView2，
        // 得由这里把「光标位置 / 左键 / 右键 / 滚轮」翻译成 WM_* 投递给网页的子窗口，
        // 网页才点得动、滚得动。放在 consumed 判断之前 —— 外壳自己吃掉的点击
        // （标签条、导航条按钮）不该转发给网页；网页画面区域本身不在 consumed 里。
        // 同样只归「鼠标下最上面那个窗口」：被压着的窗口不该收到鼠标（转发也别做）
        if (wgcMouseOwnerKey == win.Key || (uiInputOn && !consumed)) WgcForwardMouse(win, io.MousePos);

        // ================= 拖拽 / 缩放热区 =================
        if (!uiInputOn) return;        // 被压在下面的窗口：不响应鼠标（见 IsMouseInRect 注释）
        if (win.Maximized) return;
        if (dragMove != 0 || dragEdges != 0) return;

        // ① 「按住 Alt + 左键」= 在**任意位置**拖动窗口，网页画面上也管用。
        //    网页是原生窗口，鼠标压上去时游戏窗口收不到任何消息、ImGui 完全失效，
        //    所以这条不走 ImGui，直接轮询系统按键 + 系统光标。
        if (IsAltDown() && IsLButtonDown() &&
            TryGetMouseInGameUi(out var altMouse) &&
            altMouse.X >= mn.X && altMouse.X <= mx.X &&
            altMouse.Y >= mn.Y && altMouse.Y <= mx.Y)
        {
            StartDrag(win, altMouse, 0, true);
            return;
        }

        if (consumed) return;
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;

        var mouse = io.MousePos;
        if (mouse.X < mn.X || mouse.X > mx.X || mouse.Y < mn.Y || mouse.Y > mx.Y) return;

        var inPage = mouse.X >= win.PageX && mouse.X <= win.PageX + win.PageW &&
                     mouse.Y >= win.PageY && mouse.Y <= win.PageY + win.PageH;
        if (inPage) return;   // 网页画面上的点击归网页自己（想拖窗口用 Alt）

        var edges = 0;
        if (mouse.X <= mn.X + FrameGrab) edges |= EdgeLeft;
        if (mouse.X >= mx.X - FrameGrab) edges |= EdgeRight;
        if (mouse.Y <= mn.Y + FrameGrab) edges |= EdgeTop;
        if (mouse.Y >= mx.Y - FrameGrab) edges |= EdgeBottom;

        // ② 整个外壳都是拖动热区：标签条、导航条的空白处、四周那圈边框，
        //    不再只有标题栏那一小条能拖。
        StartDrag(win, mouse, edges, false);
    }

    // 开始一次拖动 / 缩放，并把鼠标钉在游戏窗口上
    private static void StartDrag(BrowserWin win, Vector2 mouse, int edges, bool byAlt)
    {
        dragKey        = win.Key;
        dragStartMouse = mouse;
        dragX = win.X; dragY = win.Y; dragW = win.W; dragH = win.H;

        dragEdges = edges;
        dragMove  = edges == 0 ? 1 : 0;
        dragByAlt = byAlt ? 1 : 0;

        if (dragCaptureOwned != 0) return;

        // ★ 必须把鼠标钉在游戏窗口上：光标一旦划到网页（原生窗口）上方，
        //   游戏窗口就再也收不到 WM_MOUSEMOVE，拖拽会「粘住」。
        //   SetCapture 只能在窗口所属线程调用 —— UiBuilder.Draw 回调本来就在游戏主线程上。
        try
        {
            var g = FindGameHwnd();
            if (g != IntPtr.Zero) { SetCapture(g); dragCaptureOwned = 1; }
            else ResetDrag(false);
        }
        catch { ResetDrag(false); }
    }

    // 自绘按钮。★ 这里不再用 ImGui.InvisibleButton，命中测试改成自己算：
    //   外壳现在画在 ForegroundDrawList 上，而 ImGui 的 IsItemHovered 是按**窗口层级**
    //   从上往下找的 —— 只要别的插件窗口压在上面，外壳窗口就 hover 不到，按钮会「看得见但点不着」。
    //   自己读全局鼠标状态就完全不受层级影响，视觉和可点击区域始终一致。
    //   （点外壳时不让游戏同时响应，靠的是外壳窗口置起的 WantCaptureMouse，见 DrawOneWindow。）
    private static bool ChromeBtn(ImDrawListPtr dl, string id, Vector2 mn, Vector2 mx, float rounding,
                                  uint hoverBg, uint activeBg, uint glyphCol, uint glyphColHover, GlyphKind kind)
    {
        var w = MathF.Max(2f, mx.X - mn.X);
        var h = MathF.Max(2f, mx.Y - mn.Y);

        var hov = IsMouseInRect(mn, mx);
        var act = hov && ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var cli = hov && ImGui.IsMouseClicked(ImGuiMouseButton.Left);

        if (act || hov) dl.AddRectFilled(mn, mx, act ? activeBg : hoverBg, rounding);

        var c = new Vector2((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f);
        DrawGlyph(dl, kind, c, MathF.Min(w, h) * 0.21f, hov ? glyphColHover : glyphCol,
                  MathF.Max(1.2f, MathF.Min(w, h) * 0.075f));
        return cli;
    }

    private static bool ChromeTextButton(ImDrawListPtr dl, Vector2 mn, Vector2 mx, string text, uint color, uint hoverColor)
    {
        bool hovered = IsMouseInRect(mn, mx);
        if (hovered) dl.AddRectFilled(mn, mx, hoverColor, 4);
        dl.PushClipRect(mn, mx, true);
        var measured = ImGui.CalcTextSize(text);
        dl.AddText(new Vector2(mn.X + 5, (mn.Y + mx.Y - measured.Y) / 2), color, text);
        dl.PopClipRect();
        return hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }
    private void CloseBrowserInput()
    {
        browserDialogActive = false; dialogKind = "";
        foreach (var win in windows.Values) { win.InputRowHeight = 0; ApplyPageRect(win); }
    }
    private void ShowBrowserDialog(string kind, BrowserWin win)
    {
        if (browserDialogActive && dialogWindowKey == win.Key && dialogKind == kind) { CloseBrowserInput(); return; }
        dialogKind = kind;
        dialogWindowKey = win.Key;
        dialogAddress = kind == "proxy" ? config.Proxy : kind == "address" ? win.ActiveTabSafe()?.Url ?? "" : "";
        dialogName = ""; dialogError = ""; dialogRequested = true;
        browserDialogActive = true;
        wgcKbOwnerKey = "";
        wgcMouseOwnerKey = "";
        foreach (var window in windows.Values) window.InputRowHeight = 0;
        win.InputRowHeight = OmniScale(kind == "address" || kind == "favorite" ? 94 : 78);
        ApplyPageRect(win);
        FocusGameNoRestore();
    }
    private void DrawBrowserDialog()
    {
        if (!browserDialogActive) return;
        if (!windows.TryGetValue(dialogWindowKey, out var owner) || owner.Closed || !owner.Visible)
        { CloseBrowserInput(); return; }
        using var theme = new ComicStyleScope();
        bool inlineAddress = true;
        ImGui.SetNextWindowPos(new Vector2(owner.X + ChromePad, owner.Y + ChromePad + TabRowH + NavRowH), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(owner.W - ChromePad * 2, owner.InputRowHeight), ImGuiCond.Always);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, GetSkin().Background with { W = 1 });
        if (dialogRequested) ImGui.SetNextWindowFocus();
        bool open = true;
        if (ImGui.Begin("##BrowserInlineInput", ref open, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar))
        {
            // 输入行遵循普通 ImGui 窗口层级。
            if (dialogRequested) { ImGui.SetKeyboardFocusHere(); dialogRequested = false; }
            if (!inlineAddress) ImGui.TextUnformatted(dialogKind == "proxy" ? "网络代理" : dialogKind == "favorite" ? "添加收藏" : "输入网址");
            if (dialogKind == "proxy")
                ImGui.TextWrapped("示例：127.0.0.1:7890 或 socks5://127.0.0.1:1080。应用后重载当前窗口标签；不同连接方式分别保存登录状态。");
            if (dialogKind == "favorite")
            { ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##favoriteName", "收藏名称（可留空）", ref dialogName, 128); }
            ImGui.SetNextItemWidth(inlineAddress ? MathF.Max(120, ImGui.GetContentRegionAvail().X - 110) : -1);
            bool enter = ImGui.InputTextWithHint("##browserAddress", dialogKind == "proxy" ? "代理地址" : "https://example.com", ref dialogAddress, 2048, ImGuiInputTextFlags.EnterReturnsTrue);
            if (dialogError.Length > 0) ImGui.TextWrapped(dialogError);
            if (inlineAddress) ImGui.SameLine();
            if (ImGui.Button(dialogKind == "favorite" ? "添加收藏" : dialogKind == "proxy" ? "应用并使用代理" : "前往") || enter)
            {
                bool done = false;
                if (dialogKind == "favorite")
                {
                    if (TryWebUrl(dialogAddress, out var url))
                    {
                        if (!config.Favorites.Any(f => BookmarkKey(f.Url) == url))
                            config.Favorites.Add(new FavoriteEntry { Name = string.IsNullOrWhiteSpace(dialogName) ? new Uri(url).Host : dialogName.Trim(), Url = url });
                        SaveOwnConfig(); done = true;
                    }
                    else dialogError = "请输入有效的 HTTP/HTTPS 网页地址。";
                }
                else if (windows.TryGetValue(dialogWindowKey, out var window) && !window.Closed)
                {
                    if (dialogKind == "proxy")
                    {
                        if (TryProxy(true, dialogAddress, out var proxy, out var error))
                        { config.Proxy = proxy; ChangeWindowProxy(window, true); SaveOwnConfig(); done = true; }
                        else dialogError = error;
                    }
                    else if (TryWebUrl(dialogAddress, out var url))
                    {
                        var tab = window.ActiveTabSafe();
                        if (tab != null && tab.Ready) Wv2Navigate(tab, url); else OpenTab(window, url, true);
                        done = true;
                    }
                    else dialogError = "请输入有效的 HTTP/HTTPS 网页地址。";
                }
                else dialogError = "浏览器窗口已关闭。";
                if (done) { CloseBrowserInput(); }
            }
            ImGui.SameLine();
            if (ImGui.Button("取消")) { CloseBrowserInput(); }
        }
        ImGui.End();
        if (!open || ImGui.IsKeyPressed(ImGuiKey.Escape)) CloseBrowserInput();
        ImGui.PopStyleColor();
    }
    private bool DrawFavoritePanel(ImDrawListPtr dl, BrowserWin win, BrowserTab? current, Vector2 outerMax,
        uint text, uint dim, uint hover, uint border)
    {
        var pad = ChromePad;
        var mn = new Vector2(win.PageX + win.PageW + pad, win.PageY);
        var mx = new Vector2(outerMax.X - pad, win.PageY + win.PageH);
        dl.AddRectFilled(mn, mx, ImGui.GetColorU32(GetSkin().Background with { W = 1 }));
        dl.AddLine(new Vector2(mn.X - pad / 2, mn.Y), new Vector2(mn.X - pad / 2, mx.Y), border, 1);
        bool consumed = IsMouseInRect(mn, mx);
        float row = MathF.Max(24, ImGui.GetFontSize() + 12);
        dl.PushClipRect(mn, mx, true);
        dl.AddText(mn + new Vector2(5, 4), text, "收藏夹");
        float y = mn.Y + row;
        if (ChromeTextButton(dl, new Vector2(mn.X, y), new Vector2(mx.X, y + row), "+ 收藏当前网页", text, hover)) AddFavorite(current);
        y += row + 8;
        if (ChromeTextButton(dl, new Vector2(mn.X, y), new Vector2(mx.X, y + row), "+ 手动添加网页", text, hover)) ShowBrowserDialog("favorite", win);
        y += row + 8;
        int visibleRows = Math.Max(1, (int)((mx.Y - y - row * 2) / row));
        win.FavoriteOffset = Math.Clamp(win.FavoriteOffset, 0, Math.Max(0, config.Favorites.Count - visibleRows));
        if (consumed && ImGui.GetIO().MouseWheel != 0)
            win.FavoriteOffset = Math.Clamp(win.FavoriteOffset - Math.Sign(ImGui.GetIO().MouseWheel) * 3, 0, Math.Max(0, config.Favorites.Count - visibleRows));
        int end = Math.Min(config.Favorites.Count, win.FavoriteOffset + visibleRows);
        if (config.Favorites.Count == 0) dl.AddText(new Vector2(mn.X + 5, y + 5), dim, "暂无收藏");
        for (int i = win.FavoriteOffset; i < end; i++)
        {
            var favorite = config.Favorites[i];
            var left = new Vector2(mn.X, y);
            var deleteLeft = new Vector2(mx.X - row, y);
            var right = new Vector2(mx.X - row - 2, y + row);
            string name = string.IsNullOrWhiteSpace(favorite.Name) ? favorite.Url : favorite.Name;
            if (ChromeTextButton(dl, left, right, name, text, hover))
            {
                if (TryWebUrl(favorite.Url, out var url)) OpenTab(win, url, true);
                else statusMessage = "收藏网址无效，请到模块设置中修改。";
            }
            if (IsMouseInRect(left, right)) ImGui.SetTooltip(favorite.Url);
            if (ChromeBtn(dl, "favoriteDelete" + i, deleteLeft, new Vector2(mx.X, y + row), 4, hover, hover, dim, text, GlyphKind.Close))
            { config.Favorites.RemoveAt(i); SaveOwnConfig(); statusMessage = "已删除收藏。"; break; }
            y += row;
        }
        float footerY = mx.Y - row * 2;
        if (config.Favorites.Count > visibleRows)
        {
            float middle = (mn.X + mx.X) / 2;
            if (ChromeTextButton(dl, new Vector2(mn.X, footerY), new Vector2(middle, footerY + row), "上一页", dim, hover)) win.FavoriteOffset = Math.Max(0, win.FavoriteOffset - visibleRows);
            if (ChromeTextButton(dl, new Vector2(middle, footerY), new Vector2(mx.X, footerY + row), "下一页", dim, hover)) win.FavoriteOffset = Math.Min(Math.Max(0, config.Favorites.Count - visibleRows), win.FavoriteOffset + visibleRows);
        }
        dl.AddText(new Vector2(mn.X + 5, mx.Y - row), dim, $"{config.Favorites.Count} 项收藏");
        dl.PopClipRect();
        return consumed;
    }

    // 自算命中：直接读 ImGui 的全局鼠标位置，与窗口层级无关。
    // ★ 但必须受 uiInputOn 约束：多个浏览器窗口叠在一起时，「鼠标位置」对每个窗口都是同一份，
    //   不加这道闸，点一下会被叠着的所有窗口一起响应（这就是之前「诡异」的来源之一）。
    //   现在只有鼠标正下方最上面那个窗口（以及正在被拖拽的那个）能通过。
    private static bool IsMouseInRect(Vector2 mn, Vector2 mx)
    {
        if (!uiInputOn) return false;
        var m = ImGui.GetIO().MousePos;
        return m.X >= mn.X && m.X <= mx.X && m.Y >= mn.Y && m.Y <= mx.Y;
    }

    private static bool NavBtn(ImDrawListPtr dl, string id, Vector2 mn, Vector2 mx, float rounding,
                               uint hoverBg, uint activeBg, uint glyphCol, uint glyphColHover, GlyphKind kind)
        => ChromeBtn(dl, id, mn, mx, rounding, hoverBg, activeBg, glyphCol, glyphColHover, kind);

    // 全部用矢量画，不依赖字体里有没有对应字符
    private static void DrawGlyph(ImDrawListPtr dl, GlyphKind kind, Vector2 c, float r, uint col, float th)
    {
        var l  = new Vector2(c.X - r, c.Y);
        var rr = new Vector2(c.X + r, c.Y);

        switch (kind)
        {
            case GlyphKind.Min:
                dl.AddLine(l, rr, col, th);
                break;

            case GlyphKind.Max:
                dl.AddRect(new Vector2(c.X - r, c.Y - r), new Vector2(c.X + r, c.Y + r),
                           col, 1.5f, ImDrawFlags.None, th);
                break;

            case GlyphKind.Restore:
            {
                var h = r * 0.62f;
                var o = r * 0.38f;
                dl.AddRect(new Vector2(c.X - h - o, c.Y - h + o), new Vector2(c.X + h - o, c.Y + h + o),
                           col, 1.2f, ImDrawFlags.None, th);
                dl.AddRectFilled(new Vector2(c.X - h + o, c.Y - h - o), new Vector2(c.X + h + o, c.Y + h - o),
                                 col, 1.2f);
                break;
            }

            case GlyphKind.Close:
                dl.AddLine(new Vector2(c.X - r, c.Y - r), new Vector2(c.X + r, c.Y + r), col, th);
                dl.AddLine(new Vector2(c.X + r, c.Y - r), new Vector2(c.X - r, c.Y + r), col, th);
                break;

            case GlyphKind.Plus:
                dl.AddLine(l, rr, col, th);
                dl.AddLine(new Vector2(c.X, c.Y - r), new Vector2(c.X, c.Y + r), col, th);
                break;

            case GlyphKind.Back:
                dl.PathLineTo(new Vector2(c.X + r * 0.45f, c.Y - r * 0.85f));
                dl.PathLineTo(new Vector2(c.X - r * 0.5f,  c.Y));
                dl.PathLineTo(new Vector2(c.X + r * 0.45f, c.Y + r * 0.85f));
                dl.PathStroke(col, ImDrawFlags.None, th);
                break;

            case GlyphKind.Forward:
                dl.PathLineTo(new Vector2(c.X - r * 0.45f, c.Y - r * 0.85f));
                dl.PathLineTo(new Vector2(c.X + r * 0.5f,  c.Y));
                dl.PathLineTo(new Vector2(c.X - r * 0.45f, c.Y + r * 0.85f));
                dl.PathStroke(col, ImDrawFlags.None, th);
                break;

            case GlyphKind.Reload:
            {
                dl.PathArcTo(c, r * 0.78f, -2.45f, 1.35f, 24);
                dl.PathStroke(col, ImDrawFlags.None, th);

                var ang = -2.45f;
                var tip = new Vector2(c.X + MathF.Cos(ang) * r * 0.78f, c.Y + MathF.Sin(ang) * r * 0.78f);
                var arm = r * 0.42f;
                dl.AddLine(tip, new Vector2(tip.X + arm, tip.Y - arm * 0.15f), col, th);
                dl.AddLine(tip, new Vector2(tip.X + arm * 0.15f, tip.Y + arm), col, th);
                break;
            }
        }
    }

    private static long navPollNextMs;

    private class PageEntry
    {
        public string Name    { get; set; } = "";
        public string Url     { get; set; } = "";
        public string Command { get; set; } = "";
        public string Proxy   { get; set; } = "";
        public bool   UseProxy { get; set; } = false;
        public bool   UseAppMode { get; set; } = false;
    }

    private sealed class FavoriteEntry
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
    }

    // ============================================================
    //  贴图渲染（「渲染自由」）：Windows.Graphics.Capture → 游戏 D3D11 SRV → ImGui 贴图
    //
    //  为什么必须这么干：宿主窗口的原生画面由 DWM 合成在游戏画面**之上**，
    //  ImGui 画的外壳既压不住它、也裁不了它 —— 于是出现「壳被别的 ImGui 窗口盖住、
    //  网页却盖着别人」的割裂感。把网页抓成贴图交给 ImGui 画，网页就是一个普通 ImGui 元素：
    //  任意层级、任意裁剪、任意缩放叠加，和别的窗口一视同仁。
    //
    //  关键点（全部查证/实测过，改动前务必先读）：
    //   · WinRT 接口的 IID 不写在 winmd 里（是名字推导的），只能用系统投影读出来 → 硬编码在 WgcIid；
    //   · WinRT 接口 vtable = IUnknown 3 槽 + IInspectable 3 槽 + 方法 ⇒ 槽号要 +6（FnW），
    //     而 IGraphicsCaptureItemInterop 是**普通 COM 接口**（只 +3），混用必崩；
    //   · 取帧用 TryGetNextFrame **轮询**，不订阅 FrameArrived —— 不必手写 WinRT 事件委托；
    //   · ImTextureID 能直接包原生 SRV 指针（Dalamud 后端是 DX11）⇒ 零拷贝，不经过 CPU；
    //   · 宿主窗口被藏到游戏 z 序之下 ⇒ 必须关掉 Chromium 的遮挡节流（见 Wv2CreateEnvironment）。
    // ============================================================

    private static class WgcIid
    {
        public static readonly Guid Item           = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
        public static readonly Guid ItemInterop    = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        public static readonly Guid PoolStatics2   = new("589b103f-6bbc-5df5-a991-02e28b3b66d5");
        public static readonly Guid D3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        public static readonly Guid D3D11Device    = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
        // ★ CreateFreeThreaded 的 device 参数在 ABI 层是 WinRT 的 IDirect3DDevice，
        //   不是裸 ID3D11Device*。喂错会得到 0x80004002 E_NOINTERFACE（已体外实测复现）。
        public static readonly Guid XgiDevice      = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
        public static readonly Guid WinrtD3DDevice = new("a37624ab-8d5f-4650-9d3e-9eae3d9bc670"); // WinRT IDirect3DDevice
        public static readonly Guid DxgiAccess     = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1"); // WinRT IDirect3DDxgiInterfaceAccess

        // 抓帧会话的两个可选开关（都是 WinRT 接口 ⇒ 方法槽 = 下标 + 6）
        //   IGraphicsCaptureSession2.IsCursorCaptureEnabled：**默认 true**，会把鼠标指针一起画进抓到的画面里。
        //     我们的宿主窗口就压在网页区域上，光标也就在那儿 ⇒ 纹理里会多出一个"跟着走但慢半拍"的鼠标。
        //   IGraphicsCaptureSession3.IsBorderRequired：Win10 上会给被抓的窗口套一圈黄框，一并关掉。
        public static readonly Guid Session2       = new("2c39ae40-7d2e-5044-804e-8b6799d4cf9e");
        public static readonly Guid Session3       = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");
    }

    private const int  WrtSlots       = 6;    // IUnknown(3) + IInspectable(3)
    private const int  DxgiBgra8      = 87;   // DXGI_FORMAT_B8G8R8A8_UNORM
    private const uint WM_MOUSEMOVE_  = 0x0200;
    private const uint WM_LBUTTONDOWN_= 0x0201;
    private const uint WM_LBUTTONUP_  = 0x0202;
    private const uint WM_RBUTTONDOWN_= 0x0204;
    private const uint WM_RBUTTONUP_  = 0x0205;
    private const uint WM_MOUSEWHEEL_ = 0x020A;

    // WinRT 接口方法槽（真实槽号 = 方法下标 + 6）
    private static T FnW<T>(IntPtr obj, int methodIndex) where T : Delegate
        => FnRaw<T>(obj, methodIndex + WrtSlots);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string src, int len, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    // ★ 把 IDXGIDevice 包装成 WinRT 的 IDirect3DDevice —— WGC 抓帧池要的就是这个东西。
    //   这是 d3d11.dll 的正式导出，专为此用途提供（不是 WinRT 激活，直接 P/Invoke 即可）。
    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);

    // 鼠标转发要用：输入子窗口相对宿主客户区的偏移（未必是 0,0）
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // 鼠标转发日志限流（游戏线程访问，无需跨线程同步）
    private static long wgcFwdLogMs;
    private static long wgcKbLogMs;
    // 最近一次 Input.dispatchMouseEvent 的返回值（泵线程写、游戏线程读；只为诊断，不需要原子性）
    private static volatile int wgcLastCdpHr = -1;
    // CDP 调用一旦报错（hr<0）就永久退回老的 WM_* 投递 —— 保证「输入永远有一条路能走」
    private static volatile bool wgcCdpBroken;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WgcCreateForWindowFn(IntPtr self, IntPtr hwnd, ref Guid riid, out IntPtr item);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WgcCreateFreeThreadedFn(IntPtr self, IntPtr device, int format, int buffers,
                                                 long size, out IntPtr pool);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WgcRecreateFn(IntPtr self, IntPtr device, int format, int buffers, long size);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WgcTryGetFrameFn(IntPtr self, out IntPtr frame);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WgcCreateSessionFn(IntPtr self, IntPtr item, out IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WgcStartFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WgcGetSizeFn(IntPtr self, out long size);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WgcSrvFn(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr srv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WgcDeviceChildFn(IntPtr self, ref Guid riid, out IntPtr device);

    // ID3D11DeviceChild::GetDevice(ID3D11Device** ppDevice)
    // 注意：**只有一个出参、没有 IID**。别和 IUnknown::QueryInterface 混（写错会拿到垃圾返回值，
    // 因为它其实是 void，寄存器里的残留会被当成 HRESULT —— 体外探针第一版就是这么被骗的）。
    // 这是拿「真 ID3D11Device」的正路：Device.D3D11Forwarder 只是 vtable 长得像 ID3D11Device 的
    // CID3D11Forwarder，QI 不到 IDXGIDevice，喂给 WGC 必失败。
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WgcGetDeviceFn(IntPtr self, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WgcSwapChainBufferFn(IntPtr self, uint index, ref Guid riid, out IntPtr surface);

    private static IntPtr gameD3d11Device;

    private static long PackSize(float w, float h)
    {
        var iw = (uint)Math.Max(1f, w);
        var ih = (uint)Math.Max(1f, h);
        return (long)iw | ((long)ih << 32);
    }

    private static string WgcFailText(int hr) => $"0x{hr:X8}";

    private static IntPtr RoActivate(string className, Guid iid)
    {
        try
        {
            if (WindowsCreateString(className, className.Length, out var hs) != 0) return IntPtr.Zero;
            try
            {
                var g = iid;
                var hr = RoGetActivationFactory(hs, ref g, out var f);
                if (hr != 0) { Wv2Log($"RoGetActivationFactory 失败 {className} hr={WgcFailText(hr)}"); return IntPtr.Zero; }
                return f;
            }
            finally { WindowsDeleteString(hs); }
        }
        catch (Exception e) { Wv2Log("RoGetActivationFactory 异常 " + className + ": " + e.Message); return IntPtr.Zero; }
    }

    // 从游戏交换链的后备缓冲反查 ID3D11Device（这是游戏 ImGui 用的同一个设备，SRV 才能直接用）。
    private static IntPtr GetGameD3D11Device()
    {
        if (gameD3d11Device != IntPtr.Zero && ComObjectOk(gameD3d11Device)) return gameD3d11Device;
        gameD3d11Device = IntPtr.Zero;
        try
        {
            var dev = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
            if (dev == null) { Wv2Log("拿不到游戏 Graphics.Kernel.Device"); return IntPtr.Zero; }

            // ① 正路：从游戏的 D3D11 DeviceContext 反查真设备（ID3D11DeviceChild::GetDevice = 槽 3）。
            //    必须是「真 ID3D11Device」：WGC 建池时会对它 QI 到 IDXGIDevice，
            //    而 Device.D3D11Forwarder 只是 vtable 伪装成 ID3D11Device 的 CID3D11Forwarder，
            //    QI 不到 IDXGIDevice ⇒ 抓帧池创建直接 0x80004002（这就是本次「抓帧不可用」的根因）。
            var gameCtx = (IntPtr)dev->D3D11DeviceContext;
            if (ComObjectOk(gameCtx))
            {
                try
                {
                    FnRaw<WgcGetDeviceFn>(gameCtx, 3)(gameCtx, out var d0);
                    if (d0 != IntPtr.Zero && ComObjectOk(d0))
                    {
                        gameD3d11Device = d0;
                        Wv2Log($"已取得游戏真 D3D11 设备（DeviceContext.GetDevice）0x{d0:X}");
                        return d0;
                    }
                }
                catch (Exception e) { Wv2Log("DeviceContext 反查设备异常: " + e.Message); }
            }

            var sc = dev->SwapChain;
            if (sc != null && sc->DXGISwapChain != null)
            {
                var dxgi = (IntPtr)sc->DXGISwapChain;
                var iidTex = WgcIid.D3D11Texture2D;
                // IDXGISwapChain::GetBuffer = 槽 8（IUnknown 3 + IDXGIObject 4 + Present 1）
                var hr = FnRaw<WgcSwapChainBufferFn>(dxgi, 8)(dxgi, 0, ref iidTex, out var tex);
                if (hr == 0 && tex != IntPtr.Zero)
                {
                    try
                    {
                        // ID3D11DeviceChild::GetDevice = 槽 3
                        var iidDev = WgcIid.D3D11Device;
                        if (FnRaw<WgcDeviceChildFn>(tex, 3)(tex, ref iidDev, out var d) == 0 && d != IntPtr.Zero)
                        {
                            gameD3d11Device = d;
                            Wv2Log($"已取得游戏 D3D11 设备（交换链后备缓冲）0x{d:X}");
                            return d;
                        }
                    }
                    finally { ReleaseRaw(tex); }
                }
                else Wv2Log($"GetBuffer(0) 失败 hr={WgcFailText(hr)}");
            }

            // 退路：Device.D3D11Forwarder 直接 QI ID3D11Device（不是设备时 QI 失败，无副作用）
            var fwd = (IntPtr)dev->D3D11Forwarder;
            if (ComObjectOk(fwd))
            {
                var d2 = QueryInterface(fwd, WgcIid.D3D11Device);
                if (d2 != IntPtr.Zero)
                {
                    gameD3d11Device = d2;
                    Wv2Log($"已取得游戏 D3D11 设备（D3D11Forwarder）0x{d2:X}");
                    return d2;
                }
            }
        }
        catch (Exception e) { Wv2Log("取游戏 D3D11 设备异常: " + e.Message); }
        return IntPtr.Zero;
    }

    // 用游戏设备给抓到的纹理建 SRV（CreateShaderResourceView = ID3D11Device 槽 7）
    private static IntPtr WgcCreateSrv(IntPtr tex)
    {
        var dev = GetGameD3D11Device();
        if (dev == IntPtr.Zero || !ComObjectOk(tex)) return IntPtr.Zero;
        try
        {
            var hr = FnRaw<WgcSrvFn>(dev, 7)(dev, tex, IntPtr.Zero, out var srv);
            if (hr != 0 || srv == IntPtr.Zero) { Wv2Log($"CreateShaderResourceView 失败 hr={WgcFailText(hr)}"); return IntPtr.Zero; }
            return srv;
        }
        catch (Exception e) { Wv2Log("建 SRV 异常: " + e.Message); return IntPtr.Zero; }
    }

    // WGC owns its surface: copy before returning the frame to the pool.
    // A retained SRV alone does not prevent the pool from overwriting the original texture.
    private static volatile bool texRenderOn = true;
    private static bool TextureModeActive(BrowserWin win) => texRenderOn && !win.CapFailed;

    // ---- 启动抓帧（泵线程上调用：宿主窗口必须已经存在） ----
    private void WgcStart(BrowserWin win)
    {
        lock (win.CaptureLock) WgcStartCore(win);
    }

    private void WgcStartCore(BrowserWin win)
    {
        if (!config.TextureRender || win.CapFailed) return;
        if (win.CapSession != IntPtr.Zero || win.CapPool != IntPtr.Zero) return;

        var host = win.HostHwnd;
        if (host == IntPtr.Zero || !IsWindow(host)) return;
        if (GetClientRect(host, out var rc) && (rc.Right <= 0 || rc.Bottom <= 0)) return;

        var dev = GetGameD3D11Device();
        if (dev == IntPtr.Zero) { WgcFail(win, "拿不到游戏 D3D11 设备"); return; }

        // ★★ 本次「抓帧不可用」的根因就在这一步：
        //    CreateFreeThreaded 的第一个参数在 ABI 层是 **WinRT 的 IDirect3DDevice**
        //    （Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice），不是裸 ID3D11Device*。
        //    直接喂 ID3D11Device* 一律 0x80004002 E_NOINTERFACE（体外探针已复现并验证修复）。
        //    正解：先 QI 出 IDXGIDevice，再用 d3d11.dll 的 CreateDirect3D11DeviceFromDXGIDevice 包一层。
        if (win.CapWrtDev == IntPtr.Zero)
        {
            var xgi = QueryInterface(dev, WgcIid.XgiDevice);
            if (xgi == IntPtr.Zero)
            { WgcFail(win, "设备不支持 IDXGIDevice（拿到的是转发器而不是真设备）"); return; }
            try
            {
                var hrw = CreateDirect3D11DeviceFromDXGIDevice(xgi, out var wr);
                if (hrw != 0 || wr == IntPtr.Zero)
                { WgcFail(win, $"包装 WinRT Direct3DDevice 失败 hr={WgcFailText(hrw)}"); return; }
                win.CapWrtDev = wr;
                Wv2Log($"已包装 WinRT Direct3DDevice 0x{wr:X}");
            }
            finally { ReleaseRaw(xgi); }
        }

        var interop = RoActivate("Windows.Graphics.Capture.GraphicsCaptureItem", WgcIid.ItemInterop);
        if (interop == IntPtr.Zero) { WgcFail(win, "抓帧接口激活失败"); return; }

        try
        {
            var iidItem = WgcIid.Item;
            // IGraphicsCaptureItemInterop 是普通 COM 接口 ⇒ 第一个方法在槽 3
            var hr = FnRaw<WgcCreateForWindowFn>(interop, 3)(interop, host, ref iidItem, out var item);
            if (hr != 0 || item == IntPtr.Zero) { WgcFail(win, $"CreateForWindow 失败 hr={WgcFailText(hr)}"); return; }
            win.CapItem = item;

            // 抓帧尺寸：优先向 item 要（get_Size = 槽 [1]），拿不到就用宿主客户区尺寸
            long size = 0;
            try { if (FnW<WgcGetSizeFn>(item, 1)(item, out size) != 0) size = 0; } catch { size = 0; }
            if (size == 0 && GetClientRect(host, out var cr)) size = PackSize(cr.Right, cr.Bottom);
            if (size == 0) { WgcFail(win, "抓帧尺寸为 0"); return; }
            win.CapW = (int)(size & 0xFFFFFFFF);
            win.CapH = (int)(size >> 32);

            var statics = RoActivate("Windows.Graphics.Capture.Direct3D11CaptureFramePool", WgcIid.PoolStatics2);
            if (statics == IntPtr.Zero) { WgcFail(win, "FramePool 激活失败"); return; }
            try
            {
                // CreateFreeThreaded = Statics2 槽 [0]（自由线程池：游戏线程可以直接轮询取帧）
                // 注意传的是 win.CapWrtDev（WinRT 包装），不是裸 ID3D11Device
                var hr2 = FnW<WgcCreateFreeThreadedFn>(statics, 0)(statics, win.CapWrtDev, DxgiBgra8, 2, size, out var pool);
                if (hr2 != 0 || pool == IntPtr.Zero) { WgcFail(win, $"CreateFreeThreaded 失败 hr={WgcFailText(hr2)}"); return; }
                win.CapPool = pool;

                // pool.CreateCaptureSession(item) = 槽 [4]
                var hr3 = FnW<WgcCreateSessionFn>(pool, 4)(pool, item, out var sess);
                if (hr3 != 0 || sess == IntPtr.Zero) { WgcFail(win, $"CreateCaptureSession 失败 hr={WgcFailText(hr3)}"); return; }
                win.CapSession = sess;

                // ★ 关掉「把鼠标画进画面」。IsCursorCaptureEnabled 默认是 true ——
                //   而我们的宿主窗口正好压在网页区域上、光标也在那儿，于是抓到的每一帧里
                //   都会带一个系统鼠标指针 ⇒ 用户看到"浏览器里多出一个鼠标"（还慢半拍）。
                //   IsCursorCaptureEnabled = Session2 槽 [0] get / [1] put ⇒ put 用 MethodIndex 1。
                WgcSessionSetBool(sess, WgcIid.Session2, 1, 0, "IsCursorCaptureEnabled");
                // 顺手关掉抓帧窗口的黄框（Win10 上会画，Win11 新版不画，关掉无副作用）
                WgcSessionSetBool(sess, WgcIid.Session3, 1, 0, "IsBorderRequired");

                // session.StartCapture() = 槽 [0]
                var hr4 = FnW<WgcStartFn>(sess, 0)(sess);
                if (hr4 != 0) { WgcFail(win, $"StartCapture 失败 hr={WgcFailText(hr4)}"); return; }

                win.CapState   = 1;
                win.CapFrames  = 0;
                win.CapStartMs = Environment.TickCount64;
                Wv2Log($"抓帧已启动 win={win.Key} 尺寸={win.CapW}x{win.CapH} device=0x{dev:X} hwnd=0x{host:X}");
            }
            finally { ReleaseRaw(statics); }
        }
        catch (Exception e) { WgcFail(win, "启动抓帧异常: " + e.Message); }
        finally { ReleaseRaw(interop); }
    }

    // 给抓帧会话设一个布尔开关（Session2/Session3 上那些可选项）。
    // WinRT 的 boolean 在 ABI 里占 1 字节（读的是寄存器低字节），所以委托声明成 int 传 0/1 是对的；
    // 老接口可能不存在（Win10 早期版本）⇒ QI 不到就静默跳过，绝不因此让抓帧失败。
    private static void WgcSessionSetBool(IntPtr session, Guid iid, int methodIndex, int value, string name)
    {
        if (session == IntPtr.Zero) return;

        var opt = IntPtr.Zero;
        try
        {
            opt = QueryInterface(session, iid);
            if (opt == IntPtr.Zero) { Wv2Log($"抓帧会话不支持 {name}（系统版本较老），跳过"); return; }

            var hr = FnW<DlgSetInt>(opt, methodIndex)(opt, value);
            if (hr != 0) Wv2Log($"{name} = {value} 失败 hr={WgcFailText(hr)}");
        }
        catch {   }
        finally { if (opt != IntPtr.Zero) ReleaseRaw(opt); }
    }

    // ---- 轮询取帧（游戏线程调用，每帧一次；池是自由线程的，安全） ----
    private void WgcPollFrame(BrowserWin win)
    {
        if (!TextureModeActive(win)) return;
        // 绘制不等待关闭/重建；取得锁后再次确认模式，才可使用 COM 指针。
        if (!Monitor.TryEnter(win.CaptureLock)) return;
        try
        {
            if (TextureModeActive(win)) WgcPollFrameCore(win);
        }
        finally { Monitor.Exit(win.CaptureLock); }
    }

    private void WgcPollFrameCore(BrowserWin win)
    {
        // 宿主窗口刚从隐藏恢复：窗口隐藏期间 WGC 可能已经停止交付画面且不会自愈，
        // 重建一次会话（泵线程做，几毫秒）比「猜它会不会自己好」稳。
        if (win.CapRestart != 0)
        {
            win.CapRestart = 0;
            // 限流：同一窗口 2 秒内最多重建一次。宿主窗口若在「隐藏/恢复」之间抖动，
            // 不节流会把 WinRT 会话反复拆建（日志实测几秒内刷出 40+ 条「抓帧已启动」）。
            var nowMs = Environment.TickCount64;
            if (TextureModeActive(win) && nowMs - win.CapStartMs > 2000)
                Wv2Post(() => { WgcStop(win); WgcStart(win); });
        }

        if (win.CapState != 1 || win.CapPool == IntPtr.Zero) return;

        // 宿主客户区尺寸变了 → 重建帧池（否则画面会被拉伸/裁切）
        try
        {
            var host = win.HostHwnd;
            if (host != IntPtr.Zero && GetClientRect(host, out var rc) &&
                rc.Right > 0 && rc.Bottom > 0 && (rc.Right != win.CapW || rc.Bottom != win.CapH))
            {
                var dev = GetGameD3D11Device();
                if (dev != IntPtr.Zero && win.CapWrtDev != IntPtr.Zero)
                {
                    var ns = PackSize(rc.Right, rc.Bottom);
                    // ★ 和 CreateFreeThreaded 一样，Recreate 的 device 参数也是 WinRT IDirect3DDevice，
                    //   传裸设备会静默失败（这里只看 hr==0，失败就当没发生 → 画面停在旧尺寸）。
                    var hr = FnW<WgcRecreateFn>(win.CapPool, 0)(win.CapPool, win.CapWrtDev, DxgiBgra8, 2, ns);
                    if (hr == 0) { win.CapW = rc.Right; win.CapH = rc.Bottom; Wv2Log($"抓帧池已重建 {win.CapW}x{win.CapH}"); }
                    else if (Environment.TickCount64 - win.CapLastLogMs > 3000)
                    { win.CapLastLogMs = Environment.TickCount64; Wv2Log($"抓帧池重建失败 hr={WgcFailText(hr)} win={win.Key}"); }
                }
            }
        }
        catch {   }

        try
        {
            var hr = FnW<WgcTryGetFrameFn>(win.CapPool, 1)(win.CapPool, out var frame);
            if (hr != 0) { WgcCountNoFrame(win, $"TryGetNextFrame hr={WgcFailText(hr)}"); return; }
            if (frame == IntPtr.Zero) { WgcCountNoFrame(win, null); return; }

            try
            {
                // frame.get_Surface() = 槽 [0] → IDirect3DSurface（WinRT 对象；getter 给了引用，用完要放）
                var surf = IntPtr.Zero;
                try { if (FnW<DlgGetPtr>(frame, 0)(frame, out surf) != 0) surf = IntPtr.Zero; } catch { surf = IntPtr.Zero; }
                if (surf == IntPtr.Zero) { WgcCountNoFrame(win, "get_Surface 返回空"); return; }

                // ★ 标准路径：WinRT 的 IDirect3DSurface 不能直接 QI 成 ID3D11Texture2D，
                //   要先 QI 到 IDirect3DDxgiInterfaceAccess，再调它的 GetInterface(IID_ID3D11Texture2D)。
                //   ⚠ IDirect3DDxgiInterfaceAccess 是**互操作 COM 接口（继承 IUnknown，不是 IInspectable）**，
                //     所以它的第一个方法在**槽 3**，必须用 FnRaw —— 用 FnW（+6）会调到越界槽位的未知函数：
                //     既拿不到纹理，又会破坏对象的引用计数，随后 ReleaseRaw 直接访问违规崩掉进程。
                //     （2026-10-10 实机崩溃就是这么来的，体外探针已复现并验证修复。）
                var tex = IntPtr.Zero;
                var access = QueryInterface(surf, WgcIid.DxgiAccess);
                if (access != IntPtr.Zero)
                {
                    try
                    {
                        var iidTex2 = WgcIid.D3D11Texture2D;
                        if (FnRaw<DlgQueryInterface>(access, 3)(access, (IntPtr)(&iidTex2), out var t0) == 0) tex = t0;
                    }
                    catch {   }
                    finally { ReleaseRaw(access); }
                }
                if (tex == IntPtr.Zero) tex = QueryInterface(surf, WgcIid.D3D11Texture2D);   // 兜底：直接 QI
                ReleaseRaw(surf);
                if (tex == IntPtr.Zero) { WgcCountNoFrame(win, "画面不是 ID3D11Texture2D"); return; }

                var srv = WgcCreateSrv(tex);
                ReleaseRaw(tex);            // SRV 自己持有资源引用，纹理可以放了
                if (srv == IntPtr.Zero) { WgcCountNoFrame(win, "建 SRV 失败"); return; }

                // 延迟释放：保留最近三代，避免 GPU 这一帧还在采样就被释放
                var old = win.CapSrv2;
                win.CapSrv2 = win.CapSrv1;
                win.CapSrv1 = win.CapSrv;
                win.CapSrv  = srv;
                if (old != IntPtr.Zero) ReleaseRaw(old);

                win.CapFrames++;
                win.CapNoFrameStreak = 0;
            }
            finally { ReleaseRaw(frame); }
        }
        catch (Exception e) { WgcCountNoFrame(win, "取帧异常: " + e.Message); }
    }

    private void WgcCountNoFrame(BrowserWin win, string? why)
    {
        win.CapNoFrameStreak++;
        var now = Environment.TickCount64;
        if (why != null && now - win.CapLastLogMs > 3000)
        {
            win.CapLastLogMs = now;
            Wv2Log($"取帧异常 win={win.Key}: {why}（连续 {win.CapNoFrameStreak} 次）");
        }
        // 启动后 5 秒一帧都没拿到 ⇒ 判定抓帧不可用，回退原生窗口，保证浏览器还能用
        if (win.CapFrames == 0 && win.CapStartMs > 0 && now - win.CapStartMs > 5000)
            WgcFail(win, "5 秒内没有抓到任何画面（自动回退原生窗口模式）");
    }

    private void WgcFail(BrowserWin win, string why)
    {
        win.CapFailed     = true;
        win.CapState      = 2;
        win.CapFailReason = why;
        Wv2Log($"抓帧失败，已回退原生窗口 win={win.Key}: {why}");
        statusMessage = "贴图抓帧失败，已回退原生窗口（详见 gwblog.txt）";
        Wv2Post(() => WgcStop(win));
        // 回退后要让宿主窗口重新回到游戏之上，否则网页会「不见了」
        win.HostZMode = 0;
        win.HostShown = 0;
        HideHost(win);
    }

    private static void WgcStop(BrowserWin win)
    {
        lock (win.CaptureLock) WgcStopCore(win);
    }

    private static void WgcStopCore(BrowserWin win)
    {
        try { ReleaseRaw(win.CapSession); } catch {   }
        try { ReleaseRaw(win.CapPool);    } catch {   }
        try { ReleaseRaw(win.CapItem);    } catch {   }
        try { ReleaseRaw(win.CapWrtDev);  } catch {   }
        win.CapSession = IntPtr.Zero;
        win.CapPool    = IntPtr.Zero;
        win.CapItem    = IntPtr.Zero;
        win.CapWrtDev  = IntPtr.Zero;
        if (win.CapState == 1) win.CapState = 0;
        // SRV 的释放推回游戏线程（见 gameQueue 注释：ImGui 可能正拿着它渲染）
        GamePost(() => WgcReleaseSrv(win));
    }

    // SRV 释放只能在游戏线程做（ImGui 可能正拿着它画这一帧）
    private static void WgcReleaseSrv(BrowserWin win)
    {
        try { ReleaseRaw(win.CapSrv);  } catch {   }
        try { ReleaseRaw(win.CapSrv1); } catch {   }
        try { ReleaseRaw(win.CapSrv2); } catch {   }
        win.CapSrv = win.CapSrv1 = win.CapSrv2 = IntPtr.Zero;
    }

    private void SetPresentationMode(bool texture)
    {
        var keyboardCore = wgcKbOwnerKey.Length > 0 && windows.TryGetValue(wgcKbOwnerKey, out var owner)
            ? owner.ActiveTabSafe()?.Wv2Core ?? IntPtr.Zero : IntPtr.Zero;
        WgcForwardKeys(keyboardCore, false);
        wgcKbOwnerKey = "";
        ResetDrag(true);
        config.TextureRender = texture;
        texRenderOn = texture;
        RebuildPresentation();
        SaveOwnConfig();
    }

    // 切换渲染模式：重建所有窗口的 z 序意图 + 抓帧生命周期
    private void RebuildPresentation()
    {
        foreach (var win in windows.Values.ToList())
        {
            try
            {
                var w = win;
                win.CapFailed = false;
                win.CapFailReason = "";
                win.CapRestart = 0;
                win.CapCssX = win.CapCssY = -1e9f;
                win.CapCssMask = -1;
                win.HostZMode = 0;
                win.HostDirty = 1;
                Wv2Post(() =>
                {
                    WgcStop(w);
                    ApplyHostGeometryCore(w);
                    Wv2ApplyActiveTab(w);
                    if (texRenderOn && !w.CloseRequested) WgcStart(w);
                });
            }
            catch {   }
        }
        statusMessage = config.TextureRender
            ? "已切换到抽帧渲染（可自由叠放）"
            : "已切换到原生窗口渲染";
    }

    // 设置页里的抓帧状态行
    private void DrawCaptureStatus()
    {
        var any = false;
        foreach (var win in windows.Values.ToList())
        {
            any = true;
            string line;
            if (!config.TextureRender) line = "（贴图渲染已关闭）";
            else if (win.CapFailed)    line = "已回退原生窗口（抓帧不可用）";
            else if (win.CapState == 1) line = $"运行中，已取 {win.CapFrames} 帧 {win.CapW}x{win.CapH}，点击转发 {win.CapMouseSends} 次";
            else                        line = "启动中…";
            ImGui.TextDisabled($"  {win.PageName}（{win.Key}）：{line}");
            if (win.CapFailed && win.CapFailReason.Length > 0)
                ImGui.TextDisabled($"      原因：{win.CapFailReason}");
        }
        if (!any) ImGui.TextDisabled("  当前没有打开的浏览器窗口");
    }

    // ---- 贴图绘制：把抓到的一帧画进外壳的「网页画面」区域 ----
    private void DrawPageTexture(ImDrawListPtr dl, BrowserWin win, Vector2 pmn, Vector2 pmx)
    {
        if (config.TextureRender && !win.CapFailed && win.CapSrv != IntPtr.Zero)
        {
            // 圆角跟外壳那圈描边对齐（见 DrawChromeContent 的 rad * 0.45f），
            // 否则画面四个角会戳出描边外面。
            dl.AddImageRounded(new ImTextureID(win.CapSrv), pmn, pmx,
                               new Vector2(0f, 0f), new Vector2(1f, 1f),
                               0xFFFFFFFFu, MathF.Max(4f, GetSkin().Radius) * 0.45f, ImDrawFlags.None);
            return;
        }

        // 还没有画面：给个占位，别让人以为壳坏了
        var hint = !config.TextureRender ? "原生窗口模式" :
                   (win.CapFailed ? "抓帧失败，已回退原生窗口" : "正在接通画面…");
        var sz = ImGui.CalcTextSize(hint);
        dl.AddText(new Vector2((pmn.X + pmx.X - sz.X) * 0.5f, (pmn.Y + pmx.Y - sz.Y) * 0.5f),
                   ImGui.GetColorU32(ImGuiCol.TextDisabled), hint);
    }

    // ---- 在宿主窗口下找出「真正接收鼠标」的那个子窗口 ----
    // WebView2 在 HWND 模式下把窗口层建成：host → Chrome_WidgetWin_0 → (Chrome_RenderWidgetHostHWND …)。
    // ★ 鼠标消息必须投给 **Chrome_WidgetWin_x** 那个顶层 widget：它才是 Chromium 的 HWNDMessageHandler，
    //   会把事件转给 renderer。而 Chrome_RenderWidgetHostHWND 只做 IME / 无障碍，收到鼠标消息会走
    //   DefWindowProc 直接丢掉 —— **子窗口的消息不会冒泡给父窗口**，所以投错了就是"点了没反应"。
    //   之前用 FindWindowExW(host,0,null,null) 只取"第一个子窗口"，拿到谁全看 z 序，不可靠。
    private IntPtr FindPageInputWindow(IntPtr host)
    {
        var first = IntPtr.Zero;
        var after = IntPtr.Zero;

        for (var i = 0; i < 64; i++)
        {
            var h = FindWindowExW(host, after, null, null);
            if (h == IntPtr.Zero) break;
            if (first == IntPtr.Zero) first = h;

            var sb = new System.Text.StringBuilder(128);
            var cn = GetClassNameW(h, sb, sb.Capacity) > 0 ? sb.ToString() : "?";
            Wv2Log($"  宿主子窗口 #{i} 0x{h:X} class={cn}");

            if (cn.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return h;
            after = h;
        }

        return first;
    }

    // ==================== CDP 输入通道：贴图模式下唯一走得通的输入正解 ====================
    // ★ 为什么 PostMessage 那条路走不通（本轮从 Dalamud 源码 + 实机日志确认）：
    //   Dalamud 的 Win32InputHandler.ProcessWndProcW 在 io.WantCaptureMouse 为真时，
    //   会先 SetCapture(游戏窗口)、再 return 0 把 WM_LBUTTONDOWN 吞掉。
    //   而 Chromium 的 HWNDMessageHandler 处理鼠标消息时要求自己的窗口处于「活动 / 被捕获」状态；
    //   贴图模式下宿主窗口被刻意压在游戏之下、永远不是活动窗口，鼠标捕获还在游戏窗口手上
    //   ⇒ 我们 PostMessage 过去的合成点击被它直接丢弃。表现就是「画面画得出来，却点不动」。
    //   正解：走 WebView2 的 DevTools 协议 —— Input.dispatchMouseEvent 把事件直接注入渲染进程，
    //   完全绕开窗口激活 / 焦点 / z 序 / 捕获（Puppeteer、Playwright 用的就是这条路）。
    private const int CdpCallDevToolsProtocolMethod = 33;   // ICoreWebView2 方法索引（不含 IUnknown 的 3 槽）

    private static int CdpCall(IntPtr core, string method, string json)
    {
        if (!ComObjectOk(core)) return unchecked((int)0x80004003);   // E_POINTER
        IntPtr m = IntPtr.Zero, p = IntPtr.Zero;
        try
        {
            m = Marshal.StringToHGlobalUni(method);
            p = Marshal.StringToHGlobalUni(json);
            return Fn<DlgCdpCall>(core, CdpCallDevToolsProtocolMethod)(core, m, p, IntPtr.Zero);
        }
        catch { return unchecked((int)0x80004005); }                 // E_FAIL
        finally
        {
            if (m != IntPtr.Zero) { try { Marshal.FreeHGlobal(m); } catch {   } }
            if (p != IntPtr.Zero) { try { Marshal.FreeHGlobal(p); } catch {   } }
        }
    }

    // 物理像素 → 页面 CSS 像素的换算系数。WebView2 的 devicePixelRatio 默认跟随宿主窗口的
    // DPI 缩放（没动过 RasterizationScale 的前提下就是 DPI/96）。
    private static double HostDpr(IntPtr host)
    {
        try
        {
            var dpi = GetDpiForWindow(host);
            if (dpi == 0) dpi = 96;
            return Math.Clamp(dpi / 96.0, 0.5, 4.0);
        }
        catch { return 1.0; }
    }

    private static string CdpNum(double v)
        => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    // 一次鼠标事件。type ∈ mouseMoved / mousePressed / mouseReleased / mouseWheel
    // （CDP 的 buttons 是位掩码：1=左 2=右 4=中；modifiers：1=Alt 2=Ctrl 4=Meta 8=Shift）
    private static string WgcMouseMoveButton(int buttons)
        => (buttons & 1) != 0 ? "left" : (buttons & 2) != 0 ? "right" : (buttons & 4) != 0 ? "middle" : "none";

    private static int CdpMouse(IntPtr core, string type, double x, double y,
                                string button, int buttons, int clickCount, int modifiers, double deltaY)
    {
        var sb = new System.Text.StringBuilder(192);
        sb.Append("{\"type\":\"").Append(type).Append("\",\"x\":").Append(CdpNum(x))
          .Append(",\"y\":").Append(CdpNum(y))
          .Append(",\"button\":\"").Append(button).Append("\",\"buttons\":").Append(buttons);
        if (type != "mouseMoved") sb.Append(",\"clickCount\":").Append(clickCount);
        if (modifiers != 0)       sb.Append(",\"modifiers\":").Append(modifiers);
        if (type == "mouseWheel") sb.Append(",\"deltaX\":0,\"deltaY\":").Append(CdpNum(deltaY));
        sb.Append('}');
        return CdpCall(core, "Input.dispatchMouseEvent", sb.ToString());
    }

    private static int CdpModifiers()
    {
        var io = ImGui.GetIO();
        var m  = 0;
        if (io.KeyAlt)   m |= 1;
        if (io.KeyCtrl)  m |= 2;
        if (io.KeySuper) m |= 4;
        if (io.KeyShift) m |= 8;
        return m;
    }

    // 窗口类名（诊断用）
    private static string WinClass(IntPtr h)
    {
        if (h == IntPtr.Zero) return "0";
        try
        {
            if (!IsWindow(h)) return "?";
            var sb = new System.Text.StringBuilder(96);
            return GetClassNameW(h, sb, sb.Capacity) > 0 ? sb.ToString() : "?";
        }
        catch { return "?"; }
    }

    // ---- 鼠标转发：贴图模式下网页不再有原生窗口吃掉鼠标，得自己把事件喂回 WebView2 ----
    private void WgcForwardMouse(BrowserWin win, Vector2 mouse)
    {
        if (!TextureModeActive(win) || win.CapState != 1) return;

        var host = win.HostHwnd;
        if (host == IntPtr.Zero || !IsWindow(host)) return;

        // ★ 只认「网页画面」那一块，不能认整个窗口：
        //   外壳（标题条 / 标签条 / 导航条）压在 PageY 之上，点它们的背景时鼠标仍在窗口矩形内，
        //   旧代码会照转不误 —— 结果 y 被 clamp 成 0，变成"在网页最顶上一行乱点"
        //   （日志里那些 ly=0 的点击就是这么来的）。
        var mouseOwned = wgcMouseOwnerKey == win.Key;
        if (!mouseOwned && (mouse.X < win.PageX || mouse.X > win.PageX + win.PageW ||
            mouse.Y < win.PageY + win.InputRowHeight || mouse.Y > win.PageY + win.PageH)) return;
        if (wgcMouseOwnerKey.Length > 0 && !mouseOwned) return;

        if (win.CapInputHwnd == IntPtr.Zero || !IsWindow(win.CapInputHwnd))
        {
            // 控制器的窗口是异步建出来的，早期可能还没有子窗口 —— 找不到时节流重试，
            // 否则每帧都枚举一遍子窗口 + 打一遍日志。
            if (Environment.TickCount64 - win.CapInputLogMs < 1000) return;
            win.CapInputLogMs = Environment.TickCount64;

            Wv2Log($"查找网页输入子窗口（宿主 0x{host:X}）…");
            win.CapInputHwnd = FindPageInputWindow(host);
            if (win.CapInputHwnd != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(128);
                var cn = GetClassNameW(win.CapInputHwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "?";
                Wv2Log($"已找到网页输入子窗口 0x{win.CapInputHwnd:X} class={cn}");
                WgcPrimePage(win, win.CapInputHwnd);
                win.CapPrimed = Environment.TickCount64;
            }
            else Wv2Log("没找到网页输入子窗口（宿主下没有子窗口？）");
        }
        var input = win.CapInputHwnd;
        if (input == IntPtr.Zero) return;

        var sx = uiScaleX > 0.05f ? uiScaleX : 1f;
        var sy = uiScaleY > 0.05f ? uiScaleY : 1f;
        // 投递给网页的必须是「相对输入窗口客户区」的像素坐标。
        // (mouse - PageX) 是 ImGui 逻辑像素，除以 UI 缩放得到物理像素。
        // ★ 但输入窗口未必与宿主客户区原点对齐（日志里会打印它的真实矩形），
        //   所以再减掉输入窗口相对宿主的偏移 —— 拿不到就算 0。
        // input 是 host 的子窗口，理论上与 host 客户区左上角对齐（put_Bounds 传的是 0,0,客户区大小），
        // 但为稳妥按真实屏幕位置算一次偏移：input 的窗口矩形 − host 客户区原点的屏幕坐标。
        var ox = 0; var oy = 0;
        var org = new POINT { X = 0, Y = 0 };
        if (GetWindowRect(input, out var wr) && ClientToScreen(host, ref org))
        {
            ox = wr.Left - org.X; oy = wr.Top - org.Y;
        }
        var lx = (int)Math.Clamp((mouse.X - win.PageX) / sx - ox, 0f, MathF.Max(1f, win.PageW / sx));
        var ly = (int)Math.Clamp((mouse.Y - win.PageY) / sy - oy, 0f, MathF.Max(1f, win.PageH / sy));
        var lp = (IntPtr)((ly << 16) | (lx & 0xFFFF));

        // CDP 走的是页面 CSS 像素（= 物理像素 / devicePixelRatio）
        var dpr = HostDpr(host);
        var cx  = lx / dpr;
        var cy  = ly / dpr;

        var io = ImGui.GetIO();

        // 统一算一遍按钮状态（CDP 的 buttons 是位掩码：1=左 2=右 4=中）
        var downL = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var downR = ImGui.IsMouseDown(ImGuiMouseButton.Right);
        var downM = ImGui.IsMouseDown(ImGuiMouseButton.Middle);
        var mask  = (downL ? 1 : 0) | (downR ? 2 : 0) | (downM ? 4 : 0);

        var clickL = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var clickR = ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        var relL   = ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        var relR   = ImGui.IsMouseReleased(ImGuiMouseButton.Right);
        var wheel  = io.MouseWheel;
        if (clickL || clickR) wgcMouseOwnerKey = win.Key;
        if (mouseOwned && !downL && !downR) wgcMouseOwnerKey = "";

        // 点在「网页画面」矩形里 ⇒ 键盘归这个窗口（判据见 WgcForwardKeys；
        // 交还发生在用户点到浏览器之外时，见 WgcUpdateKbOwner）
        if (clickL) wgcKbOwnerKey = win.Key;

        var core   = win.ActiveTabSafe()?.Wv2Core ?? IntPtr.Zero;
        var mods   = CdpModifiers();
        var viaCdp = false;

        if (ComObjectOk(core) && !wgcCdpBroken)
        {
            viaCdp = true;

            // 页面的 DOM 焦点（点输入框能不能打字）依旧依赖宿主窗口「被激活」，
            // 所以点击时补一发激活消息 —— 只骗页面，不改系统前台窗口、不动 z 序。
            if ((clickL || clickR) && Environment.TickCount64 - win.CapPrimed > 1000)
            {
                WgcPrimePage(win, input);
                win.CapPrimed = Environment.TickCount64;
            }

            // 按下时定下本次点击的 clickCount，松开时沿用同一个值（press/release 必须成对）
            if (clickL) win.CapClickCount = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) ? 2 : 1;

            // 位置/按钮都没变的帧不必重复投递（省掉无谓的 CDP 往返）
            var moved = MathF.Abs((float)cx - win.CapCssX) > 0.4f ||
                        MathF.Abs((float)cy - win.CapCssY) > 0.4f ||
                        mask != win.CapCssMask;
            var whl = wheel;
            var mCx = cx; var mCy = cy; var mMask = mask; var mMods = mods; var mCc = win.CapClickCount;
            var mCl = clickL; var mCr = clickR; var mRl = relL; var mRr = relR; var mCore = core;
            win.CapCssX = (float)cx; win.CapCssY = (float)cy; win.CapCssMask = mask;

            if (moved || mCl || mCr || mRl || mRr || MathF.Abs(whl) > 0.01f)
            {
                // ★ 必须回泵线程调：WebView2 的对象都是 STA 的，游戏线程直接调它的 vtable 会被
                //   拒绝（模块里那上千条 hr=0x802A000C 就是这么来的）。
                //   整帧的事件放进**同一个投递**里 —— 队列是 FIFO，顺序（先 move 后 press）天然保住。
                Wv2Post(() =>
                {
                    try
                    {
                        if (moved) wgcLastCdpHr = CdpMouse(mCore, "mouseMoved", mCx, mCy, WgcMouseMoveButton(mMask), mMask, 0, mMods, 0);
                        if (mCl)   wgcLastCdpHr = CdpMouse(mCore, "mousePressed",  mCx, mCy, "left",  mMask | 1, mCc, mMods, 0);
                        if (mCr)   wgcLastCdpHr = CdpMouse(mCore, "mousePressed",  mCx, mCy, "right", mMask | 2, 1, mMods, 0);
                        if (mRl)   wgcLastCdpHr = CdpMouse(mCore, "mouseReleased", mCx, mCy, "left",  mMask & ~1, mCc, mMods, 0);
                        if (mRr)   wgcLastCdpHr = CdpMouse(mCore, "mouseReleased", mCx, mCy, "right", mMask & ~2, 1, mMods, 0);
                        if (MathF.Abs(whl) > 0.01f)
                                   wgcLastCdpHr = CdpMouse(mCore, "mouseWheel", mCx, mCy, "none", mMask, 0, mMods, -whl * 120f);
                    }
                    catch {   }
                    // 任何一次调用报错（E_* 都是负数）就永久退回 WM_* 投递，保证输入永远有路可走
                    if (wgcLastCdpHr < 0) wgcCdpBroken = true;
                });
            }
        }
        else
        {
            // 兜底：万一拿不到 ICoreWebView2，退回老的 WM_* 投递（原生窗口模式下这条路是有效的）
            PostMessageW(input, WM_MOUSEMOVE_, downL ? (IntPtr)1 : IntPtr.Zero, lp);
            if (clickL || clickR) WgcGiveKeyboard(win);
            if (clickL) PostMessageW(input, WM_LBUTTONDOWN_, (IntPtr)1, lp);
            if (clickR) PostMessageW(input, WM_RBUTTONDOWN_, (IntPtr)2, lp);
            if (relL)   PostMessageW(input, WM_LBUTTONUP_, IntPtr.Zero, lp);
            if (relR)   PostMessageW(input, WM_RBUTTONUP_, IntPtr.Zero, lp);
            if (MathF.Abs(wheel) > 0.01f)
            {
                var pt = new POINT { X = 0, Y = 0 };
                try { GetCursorPos(out pt); } catch {   }
                var wlp = (IntPtr)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
                var wp  = (IntPtr)((int)(wheel * 120f) << 16);
                PostMessageW(input, WM_MOUSEWHEEL_, wp, wlp);
            }
        }

        // 诊断打点：一次点击把「点不动」的所有可能原因一次打全，省得一轮轮猜。
        //   cdp=0x0 说明 DevTools 注入被接受（这是现在的输入主路）；
        //   鼠标下= 是**系统层面鼠标真正落在哪个窗口** —— 一击命中「点击是不是漏给游戏了」；
        //   host上/host下 = 宿主窗口的 z 序邻居（宿主应当紧贴在游戏下方）。
        if ((clickL || clickR) && Environment.TickCount64 - wgcFwdLogMs > 700)
        {
            wgcFwdLogMs = Environment.TickCount64;
            win.CapMouseSends++;

            var gw  = FindGameHwnd();
            var pr  = GetWindow(host, GW_HWNDPREV);
            var nx  = GetWindow(host, GW_HWNDNEXT);
            var ptc = new POINT { X = 0, Y = 0 };
            var under = IntPtr.Zero;
            try { if (GetCursorPos(out ptc)) under = WindowFromPoint(ptc); } catch {   }
            var underRoot = under != IntPtr.Zero ? GetAncestor(under, GA_ROOT) : IntPtr.Zero;
            Wv2Log($"转发鼠标 {lx},{ly}→css {cx:F0},{cy:F0} dpr={dpr:F2} " +
                   $"{(viaCdp ? $"CDP hr=0x{wgcLastCdpHr:X8}" : "PostMessage(兜底)")} " +
                   $"ImGui{(int)mouse.X},{(int)mouse.Y} " +
                   $"host上={WinClass(pr)} host下={WinClass(nx)}(游戏={WinClass(gw)}) " +
                   $"鼠标下={WinClass(under)}↑{WinClass(underRoot)}#0x{under:X} 焦点={GetFocus() == input}");
        }
    }

    // ==================== CDP 键盘：贴图模式下「在网页里打字」 ====================
    // 为什么用 GetAsyncKeyState 轮询而不是 ImGui：贴图模式下宿主窗口永远不是活动窗口，
    // 键盘焦点压根不在它身上；轮询物理按键状态是唯一不受焦点/激活影响的来源。
    private static readonly bool[] wgcKeyDown = new bool[256];
    private static bool   wgcKeysOwned;          // 当前是否正在往网页转发按键
    private static string wgcKbOwnerKey = "";    // 键盘归属的窗口 Key（空 = 游戏）
    private static bool   wgcPrevLBtn;
    private static volatile int wgcLastKeyHr = -1;

    private static readonly Dictionary<int, string> WgcDomKey = new Dictionary<int, string>
    {
        { 0x08, "Backspace" }, { 0x09, "Tab" },        { 0x0D, "Enter" },    { 0x10, "Shift" },
        { 0x11, "Control" },   { 0x12, "Alt" },        { 0x14, "CapsLock" }, { 0x1B, "Escape" },
        { 0x20, " " },         { 0x21, "PageUp" },     { 0x22, "PageDown" }, { 0x23, "End" },
        { 0x24, "Home" },      { 0x25, "ArrowLeft" },  { 0x26, "ArrowUp" },
        { 0x27, "ArrowRight" },{ 0x28, "ArrowDown" },  { 0x2D, "Insert" },
        { 0x2E, "Delete" },    { 0x5B, "Meta" },       { 0x5C, "Meta" },
    };

    // VK → DOM KeyboardEvent.key（认不出来的返回空串，交给 ToUnicode 定字符）
    private static string WgcDomKeyName(int vk)
    {
        if (WgcDomKey.TryGetValue(vk, out var s)) return s;
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x6F);   // F1..F12
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x41 && vk <= 0x5A) return ((char)(vk + 0x20)).ToString();
        return "";
    }

    // VK → DOM KeyboardEvent.code
    private static string WgcDomCode(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return "Key" + (char)vk;
        if (vk >= 0x30 && vk <= 0x39) return "Digit" + (char)vk;
        return "";
    }

    private static string WgcJsonEsc(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static int CdpKey(IntPtr core, int vk, bool down, string text, string key, int modifiers)
        => CdpCall(core, "Input.dispatchKeyEvent", BuildCdpKeyJson(vk, down, text, key, modifiers));

    private static string BuildCdpKeyJson(int vk, bool down, string text, string key, int modifiers)
    {
        var sb = new System.Text.StringBuilder(224);
        sb.Append("{\"type\":\"").Append(down ? (text.Length > 0 ? "keyDown" : "rawKeyDown") : "keyUp").Append('"');
        sb.Append(",\"windowsVirtualKeyCode\":").Append(vk);
        sb.Append(",\"nativeVirtualKeyCode\":").Append(vk);
        var cd = WgcDomCode(vk);
        if (cd.Length > 0)  sb.Append(",\"code\":\"").Append(cd).Append('"');
        if (key.Length > 0) sb.Append(",\"key\":\"").Append(WgcJsonEsc(key)).Append('"');
        if (text.Length > 0)
            sb.Append(",\"text\":\"").Append(WgcJsonEsc(text))
              .Append("\",\"unmodifiedText\":\"").Append(WgcJsonEsc(text)).Append('"');
        if (modifiers != 0) sb.Append(",\"modifiers\":").Append(modifiers);
        var command = down ? WgcEditCommand(vk, modifiers) : "";
        if (command.Length > 0) sb.Append(",\"commands\":[\"").Append(command).Append("\"]");
        sb.Append('}');
        return sb.ToString();
    }

    private static string WgcEditCommand(int vk, int modifiers)
    {
        if ((modifiers & 5) != 0) return ""; // Alt / Meta 不应被当成 Ctrl 编辑组合。
        if ((modifiers & 2) != 0)
            return vk switch
            {
                0x41 => "selectAll", 0x43 or 0x2D => "copy", 0x58 => "cut", 0x56 => "paste",
                0x5A => (modifiers & 8) != 0 ? "redo" : "undo", 0x59 => "redo", _ => ""
            };
        if ((modifiers & 8) != 0) return vk switch { 0x2D => "paste", 0x2E => "cut", _ => "" };
        return "";
    }

    private static int WgcKeyboardModifiers(byte[] state)
        => ((state[0x12] & 0x80) != 0 ? 1 : 0) | ((state[0x11] & 0x80) != 0 ? 2 : 0)
         | (((state[0x5B] | state[0x5C]) & 0x80) != 0 ? 4 : 0) | ((state[0x10] & 0x80) != 0 ? 8 : 0);

    // 键盘归属：点浏览器之外 → 还给游戏。点进网页画面则由 WgcForwardMouse 认领。
    private void WgcUpdateKbOwner()
    {
        bool lbtn;
        try { lbtn = (GetAsyncKeyState(0x01) & 0x8000) != 0; } catch { lbtn = false; }
        var rising = lbtn && !wgcPrevLBtn;
        wgcPrevLBtn = lbtn;
        if (rising && inputTopKey.Length == 0) wgcKbOwnerKey = "";
    }

    private void WgcForwardKeys(IntPtr core, bool owned)
    {
        if (!owned)
        {
            // 交还键盘时把还按着的键补一串 keyUp，免得在页面里留一堆"卡住"的键
            if (wgcKeysOwned)
            {
                wgcKeysOwned = false;
                var stuck = new List<int>();
                for (var vk = 0; vk < 256; vk++)
                    if (wgcKeyDown[vk]) { wgcKeyDown[vk] = false; stuck.Add(vk); }
                if (stuck.Count > 0 && ComObjectOk(core))
                {
                    var c = core; var list = stuck;
                    Wv2Post(() => { try { foreach (var vk in list) CdpKey(c, vk, false, "", WgcDomKeyName(vk), 0); } catch {   } });
                }
            }
            return;
        }

        if (!ComObjectOk(core)) return;
        wgcKeysOwned = true;

        var st = new byte[256];
        try { GetKeyboardState(st); } catch {   }
        // 取同一份物理状态；ImGui 的修饰键状态可能落后一帧。
        for (var vk = 0x08; vk <= 0xFE; vk++)
            st[vk] = (byte)((st[vk] & 1) | ((GetAsyncKeyState(vk) & 0x8000) != 0 ? 0x80 : 0));
        var modifiers = WgcKeyboardModifiers(st);

        var ev = new List<(int Vk, bool Down, string Text, string Key)>();

        for (var vk = 0x08; vk <= 0xFE; vk++)
        {
            bool isDown;
            isDown = (st[vk] & 0x80) != 0;
            if (isDown == wgcKeyDown[vk]) continue;

            var key  = WgcDomKeyName(vk);
            var text = "";
            if (isDown && (modifiers & 7) == 0)
            {
                var buf = new char[8];
                int n;
                try { n = ToUnicode((uint)vk, MapVirtualKey((uint)vk, 0), st, buf, buf.Length, 0); }
                catch { n = 0; }
                if (n == 1 && buf[0] >= ' ' && buf[0] != (char)0x7F) text = buf[0].ToString();
            }
            // 既没有 DOM 名字、也打不出字符的键（媒体键、厂商键……）不转发
            if (key.Length == 0 && text.Length == 0 && !wgcKeyDown[vk]) continue;

            wgcKeyDown[vk] = isDown;
            ev.Add((vk, isDown, text, key.Length > 0 ? key : text));
        }

        if (ev.Count == 0) return;
        var c2 = core; var list2 = ev; var m2 = modifiers;
        Wv2Post(() =>
        {
            try { foreach (var e in list2) wgcLastKeyHr = CdpKey(c2, e.Vk, e.Down, e.Text, e.Key, m2); }
            catch {   }
        });
    }

    // 给网页的输入窗口补一发「你现在是活动窗口」的消息。
    // ★ 为什么需要：Chromium 有个标准行为 —— 点一个**未激活**的窗口时，第一下只用来激活它，
    //   不转给网页内容。贴图模式下宿主窗口被刻意压在游戏之下、永远不可能是活动窗口，
    //   于是用户会碰到「第一次点网页没反应，得再点一下」。这里用合成消息把 Chromium 内部的
    //   激活状态骗成"真"（PostMessage 而已：不改系统前台窗口、不动 z 序、不抢游戏焦点）。
    //   顺序有意义：三条消息按序进同一个消息队列，处理完才轮到后面那条点击。
    private static void WgcPrimePage(BrowserWin win, IntPtr input)
    {
        if (input == IntPtr.Zero || !IsWindow(input)) return;

        const uint WM_MOUSEACTIVATE_ = 0x0021;
        const uint WM_ACTIVATE_      = 0x0006;
        const uint WM_SETFOCUS_      = 0x0007;
        const uint WA_ACTIVE         = 1;
        const uint HTCLIENT          = 1;

        try
        {
            var hit = (IntPtr)((WM_LBUTTONDOWN_ << 16) | HTCLIENT);
            PostMessageW(input, WM_MOUSEACTIVATE_, win.HostHwnd, hit);
            PostMessageW(input, WM_ACTIVATE_, (IntPtr)WA_ACTIVE, IntPtr.Zero);
            PostMessageW(input, WM_SETFOCUS_, IntPtr.Zero, IntPtr.Zero);
            Wv2Log($"已向网页补发激活消息（修「第一次点击无效」）input=0x{input:X}");
        }
        catch {   }
    }

    // 把键盘焦点交给网页的输入子窗口，**不改变前台窗口、也不动 z 序**。
    // ★ 关键：SetFocus 只认「调用线程输入队列里」的窗口，而 input 是**泵线程**创建的原生窗口。
    //   所以必须把「输入窗口所属线程(tHost)」的输入队列挂到本线程上，SetFocus 才会生效。
    //   旧代码挂的是**游戏线程** —— 而本方法就跑在游戏线程上（等于自己挂自己），SetFocus 必然失败，
    //   接着退路 SetForegroundWindow(host) 会把宿主顶到游戏之上：既破坏贴图模式（原生画面盖住游戏），
    //   又让下一帧 ApplyHostGeometry 以为「窗口刚从隐藏恢复」而重建抓帧会话 —— 点一次、重建一次。
    //   ★ 因此这里**绝不** SetForegroundWindow：贴图模式的宿主必须一直留在游戏后面。
    private void WgcGiveKeyboard(BrowserWin win)
    {
        var input = win.CapInputHwnd;
        if (input == IntPtr.Zero || !IsWindow(input)) return;

        try
        {
            if (GetFocus() == input) return;                       // 已经是它了，别反复折腾

            var cur   = GetCurrentThreadId();
            var tHost = GetWindowThreadProcessId(input, out _);     // 输入窗口所属线程（= 泵线程）

            var attached = false;
            var ok       = false;
            try
            {
                if (tHost != 0 && tHost != cur) attached = AttachThreadInput(tHost, cur, true);
                SetFocus(input);
                // ★ 必须在**还挂着**的时候问：AttachThreadInput 一摘，本线程看到的焦点就回到
                //   游戏那边的窗口了，此时 GetFocus() 必然 != input —— 旧代码就是这样把自己
                //   写成"焦点没拿到"的（真拿到了也会误报），把日志的可信度毁掉了。
                ok = GetFocus() == input;
            }
            finally
            {
                if (attached) AttachThreadInput(tHost, cur, false);
            }

            if (!ok && Environment.TickCount64 - wgcKbLogMs > 5000)
            {
                wgcKbLogMs = Environment.TickCount64;
                Wv2Log($"网页键盘焦点未取得（input=0x{input:X}）—— 键盘可能打不了字；鼠标点击不受影响");
            }
        }
        catch {   }
    }

    private class Config
    {
        public int RenderingBackend { get; set; } = 0;
        public string BrowserPath { get; set; } = "";
        public List<PageEntry> Pages { get; set; } = new List<PageEntry>();
        public List<FavoriteEntry> Favorites { get; set; } = new();

        public string Proxy { get; set; } = "";

        public int WinWidth  { get; set; } = DefaultWinWidth;
        public int WinHeight { get; set; } = DefaultWinHeight;

        // 宿主窗口位置（屏幕坐标，用户拖完自动记住）；int.MinValue = 未设置 → 首次在游戏里居中
        public int WinX { get; set; } = int.MinValue;
        public int WinY { get; set; } = int.MinValue;

        // 贴图渲染：把网页抓成贴图交给 ImGui 画（渲染自由 —— 可任意层级/裁剪/缩放/叠加）。
        // 关闭或抓帧失败时回退「原生窗口模式」（老行为）。
        public bool TextureRender { get; set; } = true;

        // 旧版「子窗口偏移」字段：已废弃，保留只为兼容旧配置文件
        public int OffsetX   { get; set; } = 0;
        public int OffsetY   { get; set; } = 0;
    }

    // 内嵌 WebView2Loader.dll：x64 / 1.0.4078.44 / 164192 字节，gzip+base64
    // 原始文件 sha256: 239a9d6614a6cfade62b47cdb0be36e3069eedc438c3e0647a9e7b7c84ef6ea0
    private const string Wv2LoaderB64 =
        "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" +
        "X026us9QwLcKClqpCqVfYzWQLmgqqpsUBFCMgXIBOUMiJFQcW4QcNhbnm1/5KrBIY3EqLryu/INqwT+QyJk6hubhrUiDNoQJ5e4TNyEC4laK/XI/9As/tEdfRcE7FOa3ovC5XyYJn3OShc+z61DAuJ234uxQ/S/zjijFj9j4G7R9g9+254Ey8es/wf+P7dEFkGyPLoC8" + "jO6v2SVszbrRr1ftCPYCJEwPNzPj3hgJm+djH5BQHE1RXrDS405ScVAUnB2+8F1rCJKV83n7MIbpcsXA2gI5vM+emBbnb2bU4ixUhBrnVHNcjXOsWahxvm2OC4BCZ4H4PYAZQiusMF4W5tS0wFVe2KlIf5O4U53XSgFPCc42SR39v6CbM5vjdHMV0s0JzTrdHNecQDfX" + "v6bTzU0/i24+H6ebHbu76Obh3Trd/HL3OXSzINpFN82HBd10Id3sF6ObfqSbqr8DtjbeFwhvjHY+Eqedld1p5wJ8tKwDaOdkfFRFWj11RodOO1+HOesj1QWr5z3/m2jn60g7yxNoZ6MU6p1CtLPapw5uLcKazy53h++03ynZinOILgJde9JZL61GeQfSSBiT1aONuCYm" + "2+B9T+pVz0WEvxug6sAFoTeUQdCTe5FzQscfeL0DyZ9k8ysYqukE3Jh1Aomm1NvfQkRx8D9wGShmopKmzUTd6IH5H7if2nM8VV8tv16IHUtz+NH/jtPKJp1WfnSYaOVoSiOjozVaGcE15FyxGWvJcz8XThc6uYQaSlWIah6dvrKqI0Ysp+OInEstPWt0YmkwxonlkzFi" +
        "2dRFLKnf+X+ZsWfvRKo9uxy6d6UD6zvNCsTTy3ZSJ+uo1Qj3mWQNIiA8FcK7TkStntzdbCQB7jOMVoXTrBewoekGWx0uzvWq480+BKtm6QgrGp5kB5LeKD1ogtUrA7cXmOoVwQknvyJm7GMo0R+DU27hteUfdAga+YQhNvdovt0Zp5EwHELVtV30xr3QG7zhk0QSOQ9J" + "5PdpRCKrz08i62lK+J/kn35DJPLFO4lEPsmPPINEsN92/P8fn+gkcu4nOonUtuH9+7ZRrG06f6ANTbIiYY6PCY2onx1EAxpVsfnw4PqJl23l17+AY7VKZ0A7FPYqcZ5+DLLnYxsU1oQ9EzgmjARIu1s6O4M9YGUj+JW4G60iLRbse/iW1+kt23FnIgXZKnqv0NHCCdqF" + "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" +
        "NqHP0Jwl1K3r/wMjiLkGYIECAA==";
}

internal sealed unsafe class RepositoryNativeBrowser
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> requests = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> openStates = new();
    public string Status => statusMessage;
    public bool IsOpen(string command) => openStates.TryGetValue(NormalizeCommand(command), out var open) && open;
    public void Configure(string json)
    {
        var next = JsonSerializer.Deserialize<Config>(json) ?? new Config();
        requests.Enqueue(() =>
        {
            // 保留待启动请求引用的 PageEntry，编辑共用设置不会使排队的网页失效。
            for (var i = 0; i < next.Pages.Count; i++)
            {
                var value = next.Pages[i];
                var old = config.Pages.FirstOrDefault(p => NormalizeCommand(p.Command) == NormalizeCommand(value.Command));
                if (old == null) continue;
                old.Name = value.Name; old.Url = value.Url; old.Proxy = value.Proxy;
                old.UseProxy = value.UseProxy; old.UseAppMode = value.UseAppMode;
                next.Pages[i] = old;
            }
            config = next;
        });
    }
    public void Toggle(string json)
    {
        var value = JsonSerializer.Deserialize<PageEntry>(json);
        if (value == null) return;
        requests.Enqueue(() =>
        {
            var page = config.Pages.FirstOrDefault(p => NormalizeCommand(p.Command) == NormalizeCommand(value.Command));
            if (page == null) { page = value; config.Pages.Add(page); }
            TogglePage(page);
        });
    }
    public void Close(string command)
    {
        requests.Enqueue(() => { ClosePage(new PageEntry { Command = command }); openStates.TryRemove(NormalizeCommand(command), out _); });
    }

    private const int  GWL_HWNDPARENT = -8;
    private const int  GWL_STYLE      = -16;
    private const int  GWL_EXSTYLE    = -20;
    private const int  SW_HIDE        = 0;
    private const int  SW_SHOWNOACTIVATE = 4;
    private const int  SW_MINIMIZE    = 6;
    private const int  SW_SHOWNA      = 8;
    private const int  SW_RESTORE     = 9;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint WM_CLOSE       = 0x0010;
    private const uint WM_QUIT        = 0x0012;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_MINIMIZE       = 0x20000000;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND   = 0x0017;
    private const uint WINEVENT_OUTOFCONTEXT      = 0x0000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int   length;
        public int   flags;
        public int   showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT  rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX;
        public int    ptY;
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    private const int DefaultWinWidth  = 1100;
    private const int DefaultWinHeight = 720;
    private const int DefaultOffsetX   = 120;
    private const int DefaultOffsetY   = 100;

    private int WinW => config.WinWidth  > 0 ? config.WinWidth  : DefaultWinWidth;
    private int WinH => config.WinHeight > 0 ? config.WinHeight : DefaultWinHeight;

    private static string ConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncherCN", "pluginConfigs", "OmniGameWebBrowser.json");

    private Config config = new();

    private class BrowserWin
    {
        public HashSet<IntPtr> Hwnds = new();
        public bool Visible;
        public bool Launching;
        public long LaunchStartMs;
        public long VisibleSinceMs;

        // 因「游戏窗口被最小化」而被动隐藏：游戏恢复时自动还原（用户手动隐藏的不还原）
        public bool AutoHiddenByGame;
    }

    private readonly Dictionary<string, BrowserWin> windows = new();
    private HashSet<IntPtr> windowSnapshot = new();
    private string? activeLaunchKey;
    private CancellationTokenSource? watchCts;

    private sealed class PendingLaunch
    {
        public string    Key  = "";
        public PageEntry Page = new PageEntry();
    }

    private readonly object launchGate = new();
    private readonly Queue<PendingLaunch> pendingLaunches = new();
    private volatile bool anyLaunching;

    private RECT  lastGameRect;
    private bool  hasLastGameRect;
    private string statusMessage = "";

    private ICommandManager? commandManager;
    private readonly HashSet<string> registeredCommands = new();

    private string browserPathInput = "";

    private long focusGameUntilMs;
    private bool gameMinimized;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, byte> managedHwnds = new();
    private static ITaskbarList? taskbarList;
    private static readonly Guid TaskbarListClsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
    private Thread?           winEventThread;
    private IntPtr            winEventHook = IntPtr.Zero;
    private WinEventDelegate? winEventProc;
    private uint              winEventThreadId;

    public RepositoryNativeBrowser()
    {

    }

    public void Activate()
    {
        commandManager = GetService<ICommandManager>("Dalamud.Game.Command.CommandManager");
        if (commandManager == null)
            statusMessage = "命令服务获取失败，宏命令无法使用";
        else
            statusMessage = "";



        lock (launchGate) { anyLaunching = false; pendingLaunches.Clear(); }

        watchCts = new CancellationTokenSource();
        var token = watchCts.Token;
        _ = Task.Run(() => WatchLoop(token));
        StartWinEventWatch(watchCts.Token);
    }

    public void Deactivate()
    {
        if (commandManager != null)
        {
            foreach (var cmd in registeredCommands.ToList())
            {
                try { commandManager.RemoveHandler(cmd); } catch {   }
            }
        }
        registeredCommands.Clear();
        commandManager = null;

        try { if (winEventThreadId != 0) PostThreadMessageW(winEventThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch {   }
        winEventThreadId = 0;

        watchCts?.Cancel();
        watchCts?.Dispose();
        watchCts = null;
    }

    private void LoadOwnConfig()
    {
        try
        {
            var path = ConfigFilePath;
            if (File.Exists(path))
            {
                var json     = File.ReadAllText(path);
                var loaded   = JsonSerializer.Deserialize<Config>(json);
                if (loaded != null) config = loaded;
            }
        }
        catch {   }
        browserPathInput = config.BrowserPath;

        if ((config.Proxy ?? "").Length == 0)
            foreach (var p in config.Pages)
                if ((p.Proxy ?? "").Trim().Length > 0) { config.Proxy = p.Proxy.Trim(); break; }
        foreach (var p in config.Pages)
            if ((p.Proxy ?? "").Trim().Length > 0) { p.UseProxy = true; p.Proxy = ""; }
    }

    private void SaveOwnConfig() { }

    private static string NormalizeCommand(string cmd)
    {
        var c = (cmd ?? "").Trim();
        if (c.Length == 0) return "";
        if (!c.StartsWith("/")) c = "/" + c;
        return c;
    }

    private void SyncCommands()
    {
        if (commandManager == null) return;

        var wanted = new HashSet<string>();
        foreach (var page in config.Pages)
        {
            var cmd = NormalizeCommand(page.Command);
            if (cmd.Length > 0) wanted.Add(cmd);
        }

        foreach (var cmd in registeredCommands.ToList())
        {
            if (wanted.Contains(cmd)) continue;
            try { commandManager.RemoveHandler(cmd); } catch {   }
            registeredCommands.Remove(cmd);
        }

        foreach (var cmd in wanted)
        {
            if (registeredCommands.Contains(cmd)) continue;
            var page = config.Pages.First(p => NormalizeCommand(p.Command) == cmd);
            try
            {
                var ok = commandManager.AddHandler(cmd, new CommandInfo((string command, string arguments) => TogglePage(page)));
                if (ok) registeredCommands.Add(cmd);
                else statusMessage = $"命令 {cmd} 注册失败（可能已被占用）";
            }
            catch (Exception e)
            {
                statusMessage = $"命令 {cmd} 注册异常: {e.Message}";
            }
        }
    }

    private void WatchLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var fastPoll = false;
            try
            {
                while (requests.TryDequeue(out var request)) { if (ct.IsCancellationRequested) break; request(); }
                var nowMs = Environment.TickCount64;
                fastPoll = windows.Values.Any(w => w.Visible);

                var dead = windows.Where(kv => kv.Value.Hwnds.Count > 0 && kv.Value.Hwnds.All(h => !IsWindow(h)))
                                  .Select(kv => kv.Key).ToList();
                if (dead.Count > 0)
                {
                    foreach (var k in dead) windows.Remove(k);
                    FocusGame();
                    focusGameUntilMs = nowMs + 2000;
                }

                if (activeLaunchKey != null && windows.TryGetValue(activeLaunchKey, out var launching) &&
                    launching.Launching && launching.Hwnds.Count == 0)
                {
                    var newWins = FindNewBrowserWindows();
                    if (newWins.Count > 0)
                    {
                        foreach (var hwnd in newWins) AttachBrowser(launching, hwnd);
                        launching.Launching = false;
                        activeLaunchKey = null;
                        anyLaunching = false;
                        statusMessage = "";
                    }
                    else if (Environment.TickCount64 - launching.LaunchStartMs > 30_000)
                    {
                        launching.Launching = false;
                        activeLaunchKey = null;
                        anyLaunching = false;
                        statusMessage = "未捕获到浏览器窗口（30 秒超时）";
                    }
                }

                if (!anyLaunching)
                {
                    PendingLaunch? next;
                    lock (launchGate)
                    {
                        next = pendingLaunches.Count > 0 ? pendingLaunches.Dequeue() : null;
                    }
                    if (next != null && config.Pages.Contains(next.Page))
                    {
                        if (!windows.TryGetValue(next.Key, out var nextWin))
                        {
                            nextWin = new BrowserWin();
                            windows[next.Key] = nextWin;
                        }
                        LaunchPage(next.Page, next.Key, nextWin);
                    }
                }

                FollowGameWindow();

                SyncBrowserWithGame();

                var minimizeHidden = false;
                foreach (var w in windows.Values.ToList())
                {
                    if (!w.Visible) continue;

                    var gone = false;
                    foreach (var h in w.Hwnds)
                    {
                        if (h == IntPtr.Zero || !IsWindow(h)) continue;
                        StripToolWindowStyle(h);
                        if (IsIconic(h)) { gone = true; break; }
                        if (!IsWindowVisible(h) && Environment.TickCount64 - w.VisibleSinceMs > 600) { gone = true; break; }
                    }
                    if (!gone) continue;

                    HideBrowser(w);
                    w.Visible = false;
                    minimizeHidden = true;
                }

                if (minimizeHidden)
                {
                    // 这里绝不能用 FocusGame()（含 SW_RESTORE）：游戏最小化时会把游戏立刻拉回来，
                    // 表现为「点游戏最小化，结果最小化的是浏览器」
                    FocusGameNoRestore();
                    focusGameUntilMs = Environment.TickCount64 + 2000;
                    statusMessage = "浏览器已最小化隐藏，再按一次宏命令即可重新显示";
                }

                if (Environment.TickCount64 < focusGameUntilMs) FocusGameNoRestore();
            }
            catch {   }

            foreach (var kv in windows.ToList()) openStates[kv.Key] = kv.Value.Visible && kv.Value.Hwnds.Any(h => IsWindow(h) && IsWindowVisible(h) && !IsIconic(h));
            Thread.Sleep(fastPoll ? 50 : 200);
        }

        foreach (var w in windows.Values.ToList()) foreach (var h in w.Hwnds.ToList()) if (IsWindow(h)) PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        windows.Clear(); openStates.Clear();
    }

    // 让浏览器表现得像游戏自带的一部分：游戏窗口最小化 → 一起收起；游戏恢复 → 一起回来
    private void SyncBrowserWithGame()
    {
        var game = FindGameHwnd();
        if (game == IntPtr.Zero) return;

        var iconic = false;
        try { iconic = IsIconic(game); } catch {   }
        if (iconic == gameMinimized) return;

        if (iconic)
        {
            gameMinimized = true;
            foreach (var w in windows.Values.ToList())
            {
                if (!w.Visible) continue;
                w.AutoHiddenByGame = true;
                HideBrowser(w);
                w.Visible = false;
            }
        }
        else
        {
            gameMinimized = false;
            foreach (var w in windows.Values.ToList())
            {
                if (!w.AutoHiddenByGame) continue;
                w.AutoHiddenByGame = false;
                ShowWinAtGame(w);
            }
        }
    }

    // 按「游戏窗口位置 + 设置里的偏移」把某个网页的窗口显示出来（无焦点）
    private void ShowWinAtGame(BrowserWin win)
    {
        var x = config.OffsetX;
        var y = config.OffsetY;
        var game = FindGameHwnd();
        if (game != IntPtr.Zero && GetWindowRect(game, out var gr))
        {
            x = gr.Left + config.OffsetX;
            y = gr.Top  + config.OffsetY;
        }

        foreach (var h in win.Hwnds) ShowBrowserWindow(h, x, y);
        win.Visible        = true;
        win.VisibleSinceMs = Environment.TickCount64;
    }

    private void FollowGameWindow()
    {
        var game = FindGameHwnd();
        if (game == IntPtr.Zero || !GetWindowRect(game, out var gameRect))
        {
            hasLastGameRect = false;
            return;
        }

        if (hasLastGameRect)
        {
            var dx = gameRect.Left - lastGameRect.Left;
            var dy = gameRect.Top  - lastGameRect.Top;
            if (dx != 0 || dy != 0)
            {
                foreach (var w in windows.Values)
                {
                    if (!w.Visible) continue;
                    foreach (var h in w.Hwnds)
                    {
                        if (h == IntPtr.Zero || !IsWindow(h)) continue;
                        if (GetWindowRect(h, out var br))
                        {
                            SetWindowPos(h, IntPtr.Zero,
                                br.Left + dx, br.Top + dy, 0, 0,
                                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                        }
                    }
                }
            }
        }
        lastGameRect    = gameRect;
        hasLastGameRect = true;
    }

    private void AttachBrowser(BrowserWin win, IntPtr hwnd)
    {
        try
        {
            foreach (var other in windows.Values)
            {
                if (!ReferenceEquals(other, win) && other.Hwnds.Contains(hwnd)) return;
            }
            if (win.Hwnds.Contains(hwnd)) return;
        }
        catch {   }

        win.Hwnds.Add(hwnd);
        win.Visible = true;
        win.VisibleSinceMs = Environment.TickCount64;
        managedHwnds[hwnd] = 1;

        var game = FindGameHwnd();
        if (game == IntPtr.Zero) return;

        try
        {
            SetWindowLongPtr(hwnd, GWL_HWNDPARENT, game);

            // 摘掉 Chrome 给 --app 窗口加的 WS_EX_TOOLWINDOW：
            // 工具窗口最小化会在屏幕左下角留下幽灵框（受控实验确认），普通窗口则正常缩到屏幕外
            StripToolWindowStyle(hwnd);
            RemoveTaskbarButton(hwnd);

            if (GetWindowRect(game, out var gameRect))
            {
                SetWindowPos(hwnd, IntPtr.Zero,
                    gameRect.Left + config.OffsetX,
                    gameRect.Top  + config.OffsetY,
                    WinW, WinH,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
        catch (Exception e) { statusMessage = $"窗口绑定异常: {e.Message}"; }
    }

    private void ApplyWindowGeometryToOpen()
    {
        var game = FindGameHwnd();
        if (game == IntPtr.Zero) { statusMessage = "未找到游戏窗口"; return; }
        if (!GetWindowRect(game, out var gameRect)) { statusMessage = "读取游戏窗口位置失败"; return; }

        List<IntPtr> hwnds;
        try { hwnds = windows.Values.SelectMany(w => w.Hwnds).ToList(); }
        catch { hwnds = new List<IntPtr>(); }

        var n = 0;
        foreach (var h in hwnds)
        {
            if (h == IntPtr.Zero || !IsWindow(h)) continue;
            try
            {
                SetWindowPos(h, IntPtr.Zero,
                    gameRect.Left + config.OffsetX,
                    gameRect.Top  + config.OffsetY,
                    WinW, WinH,
                    SWP_NOZORDER | SWP_NOACTIVATE);
                n++;
            }
            catch {   }
        }
        statusMessage = n > 0 ? $"已应用窗口位置与大小（{n} 个窗口）" : "当前没有已打开的窗口";
    }

    private static IntPtr FindGameHwnd() => FindWindowW("FFXIVGAME", null);

    private static void FocusGame() => FocusGameCore(true);

    private static void FocusGameNoRestore() => FocusGameCore(false);

    private static void FocusGameCore(bool restoreIfMinimized)
    {
        var g = FindGameHwnd();
        if (g == IntPtr.Zero) return;
        try
        {
            // 游戏已最小化时：需要恢复的场景（关闭/隐藏浏览器后）先恢复；否则直接不动作，
            // 免得持续抢焦点反过来干扰用户手动最小化游戏
            if (IsIconic(g))
            {
                if (!restoreIfMinimized) return;
                ShowWindow(g, SW_RESTORE);
            }
            var fore = GetForegroundWindow();
            var foreThread = fore != IntPtr.Zero ? GetWindowThreadProcessId(fore, out _) : 0;
            var cur = GetCurrentThreadId();
            var attached = foreThread != 0 && foreThread != cur && AttachThreadInput(cur, foreThread, true);
            try
            {
                BringWindowToTop(g);
                SetForegroundWindow(g);
            }
            finally
            {
                if (attached) AttachThreadInput(cur, foreThread, false);
            }
        }
        catch {   }
    }

    private void StartWinEventWatch(CancellationToken ct)
    {
        if (winEventThread != null && winEventThread.IsAlive) return;
        try
        {
            winEventThread = new Thread(() =>
            {
                try
                {
                    winEventThreadId = GetCurrentThreadId();
                    winEventProc = OnWinEvent;
                    winEventHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND,
                        IntPtr.Zero, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
                    if (winEventHook == IntPtr.Zero) return;

                    while (!ct.IsCancellationRequested && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                }
                catch {   }
                finally
                {
                    try { if (winEventHook != IntPtr.Zero) UnhookWinEvent(winEventHook); } catch {   }
                    winEventHook = IntPtr.Zero;
                    winEventThreadId = 0;
                }
            });
            winEventThread.IsBackground = true;
            winEventThread.Name = "GWB.WinEvent";
            winEventThread.Start();
        }
        catch {   }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (eventType != EVENT_SYSTEM_MINIMIZESTART) return;
            if (hwnd == IntPtr.Zero) return;
            if (!managedHwnds.ContainsKey(hwnd)) return;

            // 最小化瞬间抢先隐藏，避免有主窗口在屏幕左下角留下幽灵框
            HideWindowHard(hwnd);
            FocusGame();
        }
        catch {   }
    }

    private void ShowBrowserWindow(IntPtr h, int x, int y)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return;
        try
        {
            // ① 先强制退出「最小化态」：只有 SW_RESTORE 会清掉 WS_MINIMIZE
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);

            // ② 用 WindowPlacement 无焦点显示到目标矩形（显式写 rcNormalPosition，免被最小化位置污染）
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(h, ref wp))
            {
                wp.showCmd          = SW_SHOWNOACTIVATE;
                wp.flags            = 0;
                wp.rcNormalPosition = new RECT { Left = x, Top = y, Right = x + WinW, Bottom = y + WinH };
                SetWindowPlacement(h, ref wp);
            }

            // ③ 精确落点并确保真的可见
            SetWindowPos(h, IntPtr.Zero, x, y, WinW, WinH, SWP_NOZORDER | SWP_NOACTIVATE);
            if (!IsWindowVisible(h)) ShowWindow(h, SW_SHOWNOACTIVATE);

            StripToolWindowStyle(h);
            RemoveTaskbarButton(h);
        }
        catch {   }
    }

    private static void HideWindowHard(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return;
        try { ShowWindow(h, SW_HIDE); } catch {   }

        // 隐藏后彻底清掉最小化位：否则窗口内部仍是「最小化态」，
        // 下次显示时会以最小化态出现（左下角幽灵框），且被轮询立即再次隐藏
        try
        {
            var style = (long)GetWindowLongPtr(h, GWL_STYLE);
            if ((style & (long)WS_MINIMIZE) != 0)
            {
                SetWindowLongPtr(h, GWL_STYLE, (IntPtr)(style & ~(long)WS_MINIMIZE));
                SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }
        catch {   }
    }

    private static void StripToolWindowStyle(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return;
        try
        {
            var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
            if ((ex & (long)WS_EX_TOOLWINDOW) == 0) return;
            SetWindowLongPtr(h, GWL_EXSTYLE, (IntPtr)(ex & ~(long)WS_EX_TOOLWINDOW));
            SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch {   }
    }

    private static void RemoveTaskbarButton(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return;
        try
        {
            if (taskbarList == null)
            {
                var t = Type.GetTypeFromCLSID(TaskbarListClsid);
                if (t == null) return;
                var obj = Activator.CreateInstance(t) as ITaskbarList;
                if (obj == null) return;
                obj.HrInit();
                taskbarList = obj;
            }
            taskbarList.DeleteTab(h);
        }
        catch {   }
    }

    private static void DrawPlaceholder(string hint)
    {
        var mn = ImGui.GetItemRectMin();
        var mx = ImGui.GetItemRectMax();
        var fs = ImGui.GetFontSize();
        var ty = mn.Y + (mx.Y - mn.Y - fs) * 0.5f;
        ImGui.GetWindowDrawList().AddText(
            new System.Numerics.Vector2(mn.X + ImGui.GetStyle().FramePadding.X, ty),
            ImGui.GetColorU32(ImGuiCol.TextDisabled), hint);
    }

    private List<IntPtr> FindNewBrowserWindows()
    {
        var ownedHwnds = new HashSet<IntPtr>();
        var ownedPids  = new HashSet<uint>();
        try
        {
            foreach (var w in windows.Values)
            {
                foreach (var h in w.Hwnds)
                {
                    ownedHwnds.Add(h);
                    if (h == IntPtr.Zero || !IsWindow(h)) continue;
                    GetWindowThreadProcessId(h, out var p);
                    if (p != 0) ownedPids.Add(p);
                }
            }
        }
        catch {   }

        var list = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (windowSnapshot.Contains(hWnd)) return true;
                if (ownedHwnds.Contains(hWnd)) return true;
                var cls = new StringBuilder(256);
                GetClassName(hWnd, cls, 256);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == 0) return true;
                if (ownedPids.Contains(pid)) return true;
                string name;
                try { name = Process.GetProcessById((int)pid).ProcessName; }
                catch { return true; }
                if (name is "msedge" or "chrome") list.Add(hWnd);
            }
            catch {   }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static HashSet<IntPtr> SnapshotBrowserWindows()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            try
            {
                var cls = new StringBuilder(256);
                GetClassName(hWnd, cls, 256);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == 0) return true;
                string name;
                try { name = Process.GetProcessById((int)pid).ProcessName; }
                catch { return true; }
                if (name is "msedge" or "chrome") set.Add(hWnd);
            }
            catch {   }
            return true;
        }, IntPtr.Zero);
        return set;
    }

    private bool WinOpen(BrowserWin win)
    {
        win.Hwnds.RemoveWhere(h => !IsWindow(h));
        return win.Hwnds.Count > 0;
    }

    private void TogglePage(PageEntry page)
    {
        var key = NormalizeCommand(page.Command);
        if (key.Length == 0) return;

        if (!windows.TryGetValue(key, out var win))
        {
            win = new BrowserWin();
            windows[key] = win;
        }

        try
        {
            var hasWin      = WinOpen(win);
            var realVisible = win.Hwnds.Any(h => IsWindow(h) && IsWindowVisible(h) && !IsIconic(h));

            if (hasWin && win.Visible && realVisible)
            {
                HideBrowser(win);
                win.Visible = false;
                win.AutoHiddenByGame = false;   // 用户主动隐藏：游戏恢复时不要再自动弹回来
                return;
            }

            if (!hasWin)
            {
                LaunchPage(page, key, win);
                return;
            }

            ShowWinAtGame(win);

            // 打开/恢复后把前台交还游戏：否则浏览器会一直占着激活态，
            // 用户从任务栏最小化游戏时被最小化的反而是浏览器
            FocusGame();
        }
        catch (Exception e) { statusMessage = $"操作失败: {e.Message}"; }
    }

    private void HideBrowser(BrowserWin win)
    {
        var pids = new HashSet<uint>();
        foreach (var h in win.Hwnds)
        {
            if (h == IntPtr.Zero || !IsWindow(h)) continue;
            try { GetWindowThreadProcessId(h, out var p); if (p != 0) pids.Add(p); } catch {   }
        }

        foreach (var h in win.Hwnds)
        {
            managedHwnds[h] = 1;
            HideWindowHard(h);
        }

        if (pids.Count == 0) return;
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                var cls = new StringBuilder(256);
                GetClassName(h, cls, 256);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                GetWindowThreadProcessId(h, out var pid);
                if (pid == 0 || !pids.Contains(pid)) return true;
                if (win.Hwnds.Contains(h)) return true;
                managedHwnds[h] = 1;
                HideWindowHard(h);
            }
            catch {   }
            return true;
        }, IntPtr.Zero);
    }

    private void LaunchPage(PageEntry page, string key, BrowserWin win)
    {
        if (win.Launching || WinOpen(win)) return;

        var exe = (config.BrowserPath ?? "").Trim();
        if (exe.Length == 0 || !File.Exists(exe)) exe = FindBrowserExe() ?? "";
        if (exe.Length == 0)
        {
            statusMessage = "未找到 Edge / Chrome，请在设置中手动填写浏览器路径";
            return;
        }

        var url = (page.Url ?? "").Trim();
        if (url.Length == 0)
        {
            statusMessage = "该网页的网址为空，请先在设置里填写";
            return;
        }
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "https://" + url;

        lock (launchGate)
        {
            if (anyLaunching)
            {
                pendingLaunches.Enqueue(new PendingLaunch { Key = key, Page = page });
                statusMessage = $"「{page.Name}」已排队，等上一个网页启动完成后自动打开";
                return;
            }
            anyLaunching = true;
        }

        var proxy = (page.UseProxy ? config.Proxy : "").Trim();

        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVGameBrowser", "profiles", SanitizeKey(key));
        try { Directory.CreateDirectory(profileDir); } catch {   }

        // 启动参数里就带上目标坐标：否则窗口会先在屏幕左上角出现，再被绑定时移到游戏内（肉眼可见的一次闪跳）
        var posX = config.OffsetX;
        var posY = config.OffsetY;
        var game = FindGameHwnd();
        if (game != IntPtr.Zero && GetWindowRect(game, out var gameRect))
        {
            posX = gameRect.Left + config.OffsetX;
            posY = gameRect.Top  + config.OffsetY;
        }

        var args = new StringBuilder();
        if (page.UseAppMode)
        {
            args.Append("--app=\"").Append(url).Append("\" ");
        }
        else
        {
            args.Append("--new-window \"").Append(url).Append("\" ");
        }
        if (proxy.Length > 0)
            args.Append("--proxy-server=\"").Append(proxy).Append("\" ");
        args.Append("--window-size=").Append(WinW)
            .Append(',').Append(WinH).Append(' ');
        args.Append("--window-position=").Append(posX)
            .Append(',').Append(posY).Append(' ');
        args.Append("--user-data-dir=\"").Append(profileDir).Append("\" ");
        args.Append("--no-first-run --no-default-browser-check --disable-session-crashed-bubble");

        windowSnapshot = SnapshotBrowserWindows();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = exe,
                Arguments       = args.ToString(),
                UseShellExecute = false,
            });
            win.Launching     = true;
            win.LaunchStartMs = Environment.TickCount64;
            activeLaunchKey   = key;
            statusMessage     = $"正在启动 {page.Name} …";
        }
        catch (Exception e)
        {
            statusMessage = $"浏览器启动失败: {e.Message}";
            lock (launchGate) { anyLaunching = false; }
        }
    }

    private void ClosePage(PageEntry page)
    {
        var key = NormalizeCommand(page.Command);
        if (key.Length == 0) return;
        if (!windows.TryGetValue(key, out var win)) return;

        try
        {
            if (WinOpen(win))
            {
                HideBrowser(win);
                win.Visible = false;
                FocusGame();
                foreach (var h in win.Hwnds) PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                focusGameUntilMs = Environment.TickCount64 + 4000;
            }
            windows.Remove(key);
        }
        catch (Exception e) { statusMessage = $"关闭失败: {e.Message}"; }
    }

    private static string SanitizeKey(string key)
    {
        var s = key.Replace("/", "").Replace("\\", "").Replace(" ", "_");
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
        if (s.Length == 0) s = "default";
        return s;
    }

    private static string? FindBrowserExe()
    {
        var pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] candidates =
        {
            Path.Combine(pf86,  @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf,    @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(local, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf,    @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86,  @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

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

    private class PageEntry
    {
        public string Name    { get; set; } = "";
        public string Url     { get; set; } = "";
        public string Command { get; set; } = "";
        public string Proxy   { get; set; } = "";
        public bool   UseProxy   { get; set; } = false;
        public bool   UseAppMode { get; set; } = false;
    }

    private class Config
    {
        public List<PageEntry> Pages { get; set; } = new List<PageEntry>();

        public string BrowserPath { get; set; } = "";

        public string Proxy { get; set; } = "";

        public int WinWidth  { get; set; } = DefaultWinWidth;
        public int WinHeight { get; set; } = DefaultWinHeight;
        public int OffsetX   { get; set; } = DefaultOffsetX;
        public int OffsetY   { get; set; } = DefaultOffsetY;
    }
}




