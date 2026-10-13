using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Json;
using CefSharp;
using CefSharp.OffScreen;

namespace Yanjiu.BrowserHost;

internal static class Program
{
    private static readonly ConcurrentDictionary<string, Page> Pages = new();
    private static readonly object OutputLock = new();
    internal static void Emit(object value) { lock (OutputLock) { Console.Out.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); } }

    [STAThread]
    private static int Main(string[] args)
    {
        // Self-host all Chromium child processes; never load the native engine in the game.
        var childExit = CefSharp.BrowserSubprocess.SelfHost.Main(args);
        if (childExit >= 0) return childExit;
        try
        {
            var profileArg = args.FirstOrDefault(a => a.StartsWith("--yanjiu-profile=", StringComparison.Ordinal));
            if (profileArg == null) { Emit(new { Kind = "error", Error = "缺少浏览器配置目录参数，请更新模块和浏览器组件。" }); return 2; }
            var profile = Path.GetFullPath(profileArg.Substring("--yanjiu-profile=".Length));
            Directory.CreateDirectory(profile);
            var settings = new CefSettings
            {
                BrowserSubprocessPath = Environment.ProcessPath!,
                RootCachePath = profile, CachePath = Path.Combine(profile, "cache"),
                WindowlessRenderingEnabled = true, MultiThreadedMessageLoop = true,
                LogFile = Path.Combine(profile, "cef.log"), LogSeverity = LogSeverity.Error,
            };
            var proxy = args.FirstOrDefault(a => a.StartsWith("--yanjiu-proxy=", StringComparison.Ordinal))?.Substring("--yanjiu-proxy=".Length);
            if (!string.IsNullOrEmpty(proxy)) settings.CefCommandLineArgs["proxy-server"] = proxy;
            // When the game already runs elevated, Chromium's de-elevation restart loses our private IPC pipes.
            settings.CefCommandLineArgs["do-not-de-elevate"] = "1";
            settings.CefCommandLineArgs["crash-dumps-dir"] = Path.Combine(profile, "crash-dumps");
            // No fingerprint spoofing, automation flags, TLS bypass, or DevTools input injection.
            if (!Cef.Initialize(settings, performDependencyCheck: false, browserProcessHandler: null))
                throw new InvalidOperationException("CEF 初始化失败");
            Emit(new { Kind = "ready" });
            string? line;
            while ((line = Console.In.ReadLine()) != null)
            {
                if (line.Length > 65536) break;
                var message = JsonSerializer.Deserialize<Message>(line);
                if (message == null) continue;
                if (message.Kind == "shutdown") break;
                if (message.Kind == "create")
                {
                    if (Pages.Count >= 16 || !ValidUrl(message.Url) || message.Id.Length != 32 || !message.Id.All(Uri.IsHexDigit)) continue;
                    var page = new Page(message);
                    if (!Pages.TryAdd(message.Id, page)) { page.Dispose(); continue; }
                    page.Create();
                }
                else if (Pages.TryGetValue(message.Id, out var page))
                {
                    if (message.Kind == "close") { Pages.TryRemove(message.Id, out _); page.Dispose(); }
                    else page.Receive(message);
                }
            }
            return 0;
        }
        catch (Exception ex) { Emit(new { Kind = "error", Error = ex.Message }); return 1; }
        finally
        {
            foreach (var page in Pages.Values) page.Dispose();
            Pages.Clear();
            if (Cef.IsInitialized == true) Cef.Shutdown();
        }
    }
    internal static bool ValidUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;
}

internal sealed class Message
{
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public int Width { get; set; } = 1000;
    public int Height { get; set; } = 700;
    public int X { get; set; }
    public int Y { get; set; }
    public int Button { get; set; }
    public int Count { get; set; } = 1;
    public bool Up { get; set; }
    public bool Leave { get; set; }
    public bool Focus { get; set; }
    public int DeltaX { get; set; }
    public int DeltaY { get; set; }
    public int Modifiers { get; set; }
    public int Key { get; set; }
    public int NativeKey { get; set; }
    public bool Character { get; set; }
}

internal sealed class Page : IDisposable
{
    private const int Capacity = 64 + 2560 * 1600 * 4;
    private readonly Message initial;
    private readonly MemoryMappedFile memory;
    private readonly MemoryMappedViewAccessor view;
    private readonly Mutex gate;
    private readonly object pixelsLock = new();
    private byte[] pixels = Array.Empty<byte>();
    private int width, height, sequence;
    private bool disposed;
    private ChromiumWebBrowser? browser;
    internal Page(Message message)
    {
        initial = message;
        memory = MemoryMappedFile.OpenExisting("Local\\Yanjiu.Cef." + message.Id);
        view = memory.CreateViewAccessor(0, Capacity);
        gate = Mutex.OpenExisting("Local\\Yanjiu.Cef.Lock." + message.Id);
    }
    internal void Create()
    {
        browser = new ChromiumWebBrowser(initial.Url, browserSettings: new BrowserSettings { WindowlessFrameRate = 60 }, automaticallyCreateBrowser: false);
        browser.Size = new Size(Math.Clamp(initial.Width, 1, 2560), Math.Clamp(initial.Height, 1, 1600));
        browser.RenderHandler = new Renderer(browser, this);
        browser.LifeSpanHandler = new Popups(initial.Id);
        browser.Paint += Paint;
        browser.TitleChanged += (_, e) => Program.Emit(new { Kind = "title", Id = initial.Id, Title = e.Title });
        browser.AddressChanged += (_, e) => Program.Emit(new { Kind = "address", Id = initial.Id, Url = e.Address });
        browser.LoadingStateChanged += (_, e) => Program.Emit(new { Kind = "navigation", Id = initial.Id, e.CanGoBack, e.CanGoForward, e.IsLoading });
        browser.CreateBrowser();
    }
    private void Paint(object? sender, OnPaintEventArgs e)
    {
        e.Handled = true;
        // The view frame is copied once to a shared buffer. No PNG/JPEG encoding, polling screenshots, or growing frame queue.
        if (e.Width < 1 || e.Width > 2560 || e.Height < 1 || e.Height > 1600) return;
        lock (pixelsLock)
        {
            if (disposed) return;
            int count = checked(e.Width * e.Height * 4);
            if (e.IsPopup)
            {
                if (popup.Length != count) popup = new byte[count];
                Marshal.Copy(e.BufferHandle, popup, 0, count);
                popupWidth = e.Width; popupHeight = e.Height;
            }
            else
            {
                if (pixels.Length != count) pixels = new byte[count];
                Marshal.Copy(e.BufferHandle, pixels, 0, count);
                width = e.Width; height = e.Height;
            }
            Publish();
        }
    }
    private byte[] popup = Array.Empty<byte>(), composite = Array.Empty<byte>();
    private CefSharp.Structs.Rect popupRect;
    private int popupWidth, popupHeight;
    private bool popupVisible;
    private void Publish()
    {
            if (pixels.Length == 0) return;
            int count = pixels.Length;
            var output = pixels;
            if (popupVisible && popup.Length > 0)
            {
                if (composite.Length != count) composite = new byte[count];
                Buffer.BlockCopy(pixels, 0, composite, 0, count);
                for (int y = 0; y < popupHeight; y++)
                {
                    int destY = popupRect.Y + y;
                    if (destY < 0 || destY >= height) continue;
                    int left = Math.Max(0, popupRect.X), right = Math.Min(width, popupRect.X + popupWidth);
                    if (right > left) Buffer.BlockCopy(popup, (y * popupWidth + left - popupRect.X) * 4,
                        composite, (destY * width + left) * 4, (right - left) * 4);
                }
                output = composite;
            }
            bool acquired;
            try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            try
            {
                view.WriteArray(64, output, 0, count);
                view.Write(4, width); view.Write(8, height); view.Write(12, count);
                view.Write(0, ++sequence);
            }
            finally { gate.ReleaseMutex(); }
    }
    internal void Receive(Message m)
    {
        var b = browser;
        if (b == null || disposed) return;
        if (m.Kind == "resize") { b.Size = new Size(Math.Clamp(m.Width, 1, 2560), Math.Clamp(m.Height, 1, 1600)); return; }
        if (m.Kind == "navigate") { if (Program.ValidUrl(m.Url)) b.Load(m.Url); return; }
        if (!b.IsBrowserInitialized) return;
        var host = b.GetBrowser().GetHost();
        var mouse = new MouseEvent(m.X, m.Y, (CefEventFlags)m.Modifiers);
        switch (m.Kind)
        {
            case "back": b.Back(); break;
            case "forward": b.Forward(); break;
            case "reload": b.Reload(); break;
            case "focus": host.SetFocus(m.Focus); break;
            case "visible": host.WasHidden(!m.Focus); break;
            case "move": host.SendMouseMoveEvent(mouse, m.Leave); break;
            case "click": host.SendMouseClickEvent(mouse, m.Button switch { 1 => MouseButtonType.Right, 2 => MouseButtonType.Middle, _ => MouseButtonType.Left }, m.Up, Math.Clamp(m.Count, 1, 2)); break;
            case "wheel": host.SendMouseWheelEvent(mouse, m.DeltaX, m.DeltaY); break;
            case "key": host.SendKeyEvent(new KeyEvent { Type = m.Character ? KeyEventType.Char : m.Up ? KeyEventType.KeyUp : KeyEventType.RawKeyDown,
                WindowsKeyCode = m.Key, NativeKeyCode = m.NativeKey, Modifiers = (CefEventFlags)m.Modifiers,
                IsSystemKey = ((CefEventFlags)m.Modifiers & CefEventFlags.AltDown) != 0 }); break;
        }
    }
    public void Dispose()
    {
        ChromiumWebBrowser? old;
        lock (pixelsLock)
        {
            if (disposed) return;
            disposed = true;
            old = browser; browser = null;
            view.Dispose(); memory.Dispose(); gate.Dispose(); pixels = Array.Empty<byte>();
        }
        old?.Dispose();
    }
    private sealed class Renderer(ChromiumWebBrowser browser, Page page) : DefaultRenderHandler(browser)
    {
        public override void OnPopupShow(bool show)
        {
            lock (page.pixelsLock) { if (page.disposed) return; page.popupVisible = show; page.Publish(); }
        }
        public override void OnPopupSize(CefSharp.Structs.Rect rect)
        { lock (page.pixelsLock) page.popupRect = rect; }
    }
    private sealed class Popups(string id) : CefSharp.Handler.LifeSpanHandler
    {
        protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            string targetUrl, string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture,
            IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings,
            ref bool noJavascriptAccess, out IWebBrowser newBrowser)
        {
            newBrowser = null!;
            if (Program.ValidUrl(targetUrl)) Program.Emit(new { Kind = "popup", Id = id, Url = targetUrl });
            return true;
        }
    }
}
