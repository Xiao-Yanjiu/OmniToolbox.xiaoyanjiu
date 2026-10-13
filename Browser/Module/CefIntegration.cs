using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace OmniToolbox.LocalModules;

public sealed unsafe partial class GameWebBrowser
{
    private bool CefMode => config.RenderingBackend != 1;
    private CefInstaller? cefInstaller;
    private static readonly string CefRoot = Path.Combine(Path.GetDirectoryName(ConfigFilePath)!, "YanjiuBrowser", "cef-154.0.34");
    private string cefFocusId = "";
    private readonly bool[] cefKeys = new bool[256];
    private readonly long[] cefRepeat = new long[256];

    private void DrawCefSettings()
    {
        cefInstaller ??= new CefInstaller(CefRoot);
        ImGui.TextDisabled(cefInstaller.Ready ? "CEF 已安装 · 154.0.34" : cefInstaller.NativeInstalled ? "CEF 已安装，需要更新浏览器组件" : "CEF 未安装");
        if (cefInstaller.Busy)
        {
            var downloaded = cefInstaller.Bytes / 1048576d;
            var total = cefInstaller.Total / 1048576d;
            ImGui.ProgressBar(cefInstaller.Total > 0 ? (float)cefInstaller.Bytes / cefInstaller.Total : 0,
                new Vector2(-1, 0), cefInstaller.Total > 0 ? $"{downloaded:F1} / {total:F1} MB" : $"{downloaded:F1} MB");
            ImGui.TextWrapped(cefInstaller.Status);
            if (ImGui.Button("取消下载")) cefInstaller.Cancel();
        }
        else
        {
            if (!cefInstaller.Ready && ImGui.Button(cefInstaller.NativeInstalled ? "更新浏览器组件" : "下载 CEF")) cefInstaller.Start();
            if (cefInstaller.Status.Length > 0) ImGui.TextWrapped(cefInstaller.Status);
        }
    }
    private bool CefCanOpen()
    {
        cefInstaller ??= new CefInstaller(CefRoot);
        if (cefInstaller.Ready) return true;
        statusMessage = cefInstaller.NativeInstalled ? "请在 CEF 模式下点击“更新浏览器组件”。" : "请在 CEF 模式下点击“下载 CEF”。";
        return false;
    }
    private void CefOpenTab(BrowserWin win, BrowserTab tab)
    {
        win.Cef ??= new CefPeer(CefRoot, Path.Combine(Path.GetDirectoryName(ConfigFilePath)!, "YanjiuBrowser", "profiles",
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(win.Key + "|" + win.Proxy))).ToLowerInvariant()),
            win.Proxy, message => gameQueue.Enqueue(() => CefEvent(win, message)));
        tab.Cef = new CefSurface();
        tab.Wv2Busy = false;
        tab.Pending = null;
        win.Cef.Send(new { Kind = "create", Id = tab.Cef.Id, Url = tab.Url,
            Width = Math.Clamp((int)win.PageW, 1, 2560), Height = Math.Clamp((int)win.PageH, 1, 1600) });
    }
    private void CefEvent(BrowserWin win, string json)
    {
        if (win.Closed) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            var kind = m.GetProperty("Kind").GetString();
            if (kind == "error") { statusMessage = "CEF：" + m.GetProperty("Error").GetString(); win.CapFailReason = statusMessage; return; }
            if (!m.TryGetProperty("Id", out var id)) return;
            var tab = win.TabsSnapshot().FirstOrDefault(t => t.Cef?.Id == id.GetString());
            if (tab == null) return;
            if (kind == "title") tab.Title = m.GetProperty("Title").GetString() ?? "";
            if (kind == "address") tab.Url = m.GetProperty("Url").GetString() ?? tab.Url;
            if (kind == "navigation")
            {
                tab.CanBack = m.GetProperty("CanGoBack").GetBoolean(); tab.CanFwd = m.GetProperty("CanGoForward").GetBoolean();
                tab.Wv2Busy = m.GetProperty("IsLoading").GetBoolean();
            }
            if (kind == "load-error" && m.GetProperty("Main").GetBoolean())
            {
                var code = m.GetProperty("Code").GetString();
                if (code != "Aborted")
                {
                    var detail = m.GetProperty("Error").GetString() ?? "未知错误";
                    win.CapFailReason = $"CEF 网页加载失败：{detail}（{code}）";
                    statusMessage = win.CapFailReason;
                }
            }
            if (kind == "popup" && TryWebUrl(m.GetProperty("Url").GetString(), out var url)) OpenTab(win, url, true);
        }
        catch (Exception ex) { Wv2Log("CEF 状态异常：" + ex.Message); }
    }
    private void CefCommand(BrowserTab tab, string kind, string url = "") =>
        tab.Win?.Cef?.Send(new { Kind = kind, Id = tab.Cef?.Id ?? "", Url = url });

    private void CefPoll(BrowserWin win)
    {
        var tab = win.ActiveTabSafe();
        var frame = tab?.Cef;
        if (frame == null) return;
        int width = Math.Clamp((int)win.PageW, 1, 2560), height = Math.Clamp((int)win.PageH, 1, 1600);
        if (frame.RequestWidth != width || frame.RequestHeight != height)
        {
            frame.RequestWidth = width; frame.RequestHeight = height;
            win.Cef?.Send(new { Kind = "resize", Id = frame.Id, Width = width, Height = height });
        }
        if (!frame.TryRead(out var pixels, out width, out height))
        {
            if (frame.Srv == IntPtr.Zero && Environment.TickCount64 - win.LaunchStartMs > 12000 && win.CapFailReason.Length == 0)
                win.CapFailReason = "CEF 没有收到网页画面。请尝试刷新；若只有此网站如此，可在模块设置中改用独立浏览器。";
            return;
        }
        if (win.CapFailReason.StartsWith("CEF 没有收到网页画面", StringComparison.Ordinal)) win.CapFailReason = "";
        var device = GetGameD3D11Device();
        if (device == IntPtr.Zero) return;
        if (frame.Texture == IntPtr.Zero || frame.Width != width || frame.Height != height)
        {
            frame.ReleaseTexture();
            var desc = new CefTextureDesc { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = 87, SampleCount = 1, Usage = 2, BindFlags = 8, CpuAccessFlags = 65536 };
            if (FnRaw<CefCreateTexture>(device, 5)(device, ref desc, IntPtr.Zero, out frame.Texture) < 0) return;
            frame.Srv = WgcCreateSrv(frame.Texture);
            frame.Width = width; frame.Height = height;
        }
        if (frame.Srv == IntPtr.Zero) return;
        FnRaw<CefGetContext>(device, 40)(device, out var context);
        if (context == IntPtr.Zero) return;
        try
        {
            if (FnRaw<CefMap>(context, 14)(context, frame.Texture, 0, 4, 0, out var mapped) < 0) return;
            try
            {
                fixed (byte* src = pixels)
                    for (int y = 0; y < height; y++) Buffer.MemoryCopy(src + y * width * 4,
                        (byte*)mapped.Data + y * mapped.RowPitch, mapped.RowPitch, width * 4);
            }
            finally { FnRaw<CefUnmap>(context, 15)(context, frame.Texture, 0); }
            win.CapFrames++; win.CapState = 1;
            win.CapW = width; win.CapH = height;
        }
        finally { ReleaseRaw(context); }
    }
    private void CefMouse(BrowserWin win, Vector2 mouse)
    {
        var tab = win.ActiveTabSafe(); var frame = tab?.Cef;
        if (frame == null) return;
        var inside = mouse.X >= win.PageX && mouse.Y >= win.PageY + win.InputRowHeight && mouse.X < win.PageX + win.PageW && mouse.Y < win.PageY + win.PageH;
        var io = ImGui.GetIO();
        var modifiers = CefModifiers();
        var x = (int)((mouse.X - win.PageX) * frame.RequestWidth / MathF.Max(1, win.PageW));
        var y = (int)((mouse.Y - win.PageY) * frame.RequestHeight / MathF.Max(1, win.PageH));
        if (inside || frame.Buttons.Any(b => b))
        {
            if (x != frame.MouseX || y != frame.MouseY || !frame.Inside)
                win.Cef?.Send(new { Kind = "move", Id = frame.Id, X = x, Y = y, Modifiers = modifiers, Leave = false });
            frame.MouseX = x; frame.MouseY = y; frame.Inside = true;
            for (int b = 0; b < 3; b++)
            {
                bool down = ImGui.IsMouseDown((ImGuiMouseButton)b);
                if (down == frame.Buttons[b] || (down && !inside)) continue;
                if (down)
                {
                    frame.Clicks[b] = ImGui.IsMouseDoubleClicked((ImGuiMouseButton)b) ? 2 : 1;
                    wgcKbOwnerKey = win.Key; wgcMouseOwnerKey = win.Key;
                    wgcImeAnchor = mouse; wgcImeNeedsFocus = true;
                    win.Cef?.Send(new { Kind = "focus", Id = frame.Id, Focus = true });
                }
                frame.Buttons[b] = down;
                win.Cef?.Send(new { Kind = "click", Id = frame.Id, X = x, Y = y, Button = b,
                    Up = !down, Count = frame.Clicks[b], Modifiers = modifiers });
                win.CapMouseSends++;
            }
            if (inside && (io.MouseWheel != 0 || io.MouseWheelH != 0))
                win.Cef?.Send(new { Kind = "wheel", Id = frame.Id, X = x, Y = y, Modifiers = modifiers,
                    DeltaX = (int)(io.MouseWheelH * 120), DeltaY = (int)(io.MouseWheel * 120) });
            if (!frame.Buttons.Any(b => b) && wgcMouseOwnerKey == win.Key) wgcMouseOwnerKey = "";
        }
        else if (frame.Inside)
        {
            frame.Inside = false;
            win.Cef?.Send(new { Kind = "move", Id = frame.Id, X = x, Y = y, Leave = true, Modifiers = modifiers });
        }
    }
    private static int CefModifiers()
    {
        int m = 0;
        if ((GetAsyncKeyState(16) & 0x8000) != 0) m |= 2;
        if ((GetAsyncKeyState(17) & 0x8000) != 0) m |= 4;
        if ((GetAsyncKeyState(18) & 0x8000) != 0) m |= 8;
        if ((GetAsyncKeyState(1) & 0x8000) != 0) m |= 16;
        if ((GetAsyncKeyState(4) & 0x8000) != 0) m |= 32;
        if ((GetAsyncKeyState(2) & 0x8000) != 0) m |= 64;
        if ((CefGetKeyState(20) & 1) != 0) m |= 1;
        if ((CefGetKeyState(144) & 1) != 0) m |= 256;
        if ((GetAsyncKeyState(91) & 0x8000) != 0 || (GetAsyncKeyState(92) & 0x8000) != 0) m |= 128;


        return m;
    }
    private void CefKeyboard(BrowserWin? owner)
    {
        var tab = owner?.ActiveTabSafe();
        var id = tab?.Cef?.Id ?? "";
        if (cefFocusId != id)
        {
            foreach (var win in windows.Values)
            {
                var old = win.TabsSnapshot().FirstOrDefault(t => t.Cef?.Id == cefFocusId);
                if (old == null) continue;
                for (int key = 8; key < 256; key++) if (cefKeys[key])
                    win.Cef?.Send(new { Kind = "key", Id = cefFocusId, Key = key, Up = true });
                win.Cef?.Send(new { Kind = "focus", Id = cefFocusId, Focus = false });
            }
            Array.Clear(cefKeys); cefFocusId = id; wgcImeNeedsFocus = true;
            if (id.Length > 0) owner?.Cef?.Send(new { Kind = "focus", Id = id, Focus = true });
        }
        if (owner == null || id.Length == 0) return;
        var composing = WgcImeComposing();
        wgcImeFrameText = "";
        WgcDrawImeInput();
        var modifiers = CefModifiers();
        for (int key = 8; key < 256; key++)
        {
            var down = (GetAsyncKeyState(key) & 0x8000) != 0;
            if (down == cefKeys[key])
            {
                if (!down || key is not (8 or 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 46) || Environment.TickCount64 < cefRepeat[key]) continue;
                cefRepeat[key] = Environment.TickCount64 + 33;
            }
            else cefRepeat[key] = Environment.TickCount64 + 400;
            cefKeys[key] = down;
            if (key is 16 or 17 or 18 || key < 8 || (composing && down)) continue;
            int scan = (int)MapVirtualKey((uint)key, 0);
            int native = 1 | (scan << 16) | (!down ? unchecked((int)0xC0000000) : 0);
            if (key is 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 45 or 46 or 111 or 144) native |= 1 << 24;
            if ((modifiers & 8) != 0) native |= 1 << 29;
            owner.Cef?.Send(new { Kind = "key", Id = id, Key = key, NativeKey = native, Up = !down, Modifiers = modifiers });
        }
        // ImGui's input widget commits OS text (including IME) instead of synthesizing DOM keyboard events.
        foreach (char c in wgcImeFrameText)
            owner.Cef?.Send(new { Kind = "key", Id = id, Key = (int)c, Character = true, Modifiers = modifiers });
    }
    [StructLayout(LayoutKind.Sequential)] private struct CefTextureDesc
    { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct CefMapped { public IntPtr Data; public uint RowPitch, DepthPitch; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CefCreateTexture(IntPtr self, ref CefTextureDesc desc, IntPtr data, out IntPtr tex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CefGetContext(IntPtr self, out IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CefMap(IntPtr self, IntPtr tex, uint index, uint type, uint flags, out CefMapped mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CefUnmap(IntPtr self, IntPtr tex, uint index);
    [DllImport("user32.dll", EntryPoint = "GetKeyState")] private static extern short CefGetKeyState(int key);

    private sealed class CefSurface : IDisposable
    {
        public string Id = Guid.NewGuid().ToString("N");
        private const int Capacity = 64 + 2560 * 1600 * 4;
        private readonly MemoryMappedFile memory;
        private readonly MemoryMappedViewAccessor view;
        private readonly Mutex gate;
        private byte[] pixels = Array.Empty<byte>();
        private int sequence;
        public int RequestWidth, RequestHeight, Width, Height, MouseX = int.MinValue, MouseY;
        public IntPtr Texture, Srv;
        public bool Inside;
        public readonly bool[] Buttons = new bool[3];
        public readonly int[] Clicks = { 1, 1, 1 };
        public CefSurface()
        {
            memory = MemoryMappedFile.CreateNew("Local\\Yanjiu.Cef." + Id, Capacity);
            view = memory.CreateViewAccessor();
            gate = new Mutex(false, "Local\\Yanjiu.Cef.Lock." + Id);
        }
        public bool TryRead(out byte[] data, out int w, out int h)
        {
            data = pixels; w = h = 0;
            bool acquired;
            try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return false;
            try
            {
                int seq = view.ReadInt32(0);
                if (seq == 0 || seq == sequence) return false;
                w = view.ReadInt32(4); h = view.ReadInt32(8); int count = view.ReadInt32(12);
                if (w < 1 || w > 2560 || h < 1 || h > 1600 || count != w * h * 4) return false;
                if (pixels.Length != count) pixels = new byte[count];
                view.ReadArray(64, pixels, 0, count); sequence = seq; data = pixels;
                return true;
            }
            finally { gate.ReleaseMutex(); }
        }
        public void ReleaseTexture()
        {
            var srv = Srv; var texture = Texture; Srv = Texture = IntPtr.Zero;
            if (srv == IntPtr.Zero && texture == IntPtr.Zero) return;
            // The current ImGui draw list may still reference this SRV until the frame is submitted.
            if (OmniToolbox.Host.DalamudServices.Framework != null)
                _ = OmniToolbox.Host.DalamudServices.Framework.RunOnTick(() => { ReleaseRaw(srv); ReleaseRaw(texture); }, delayTicks: 2);
            else { ReleaseRaw(srv); ReleaseRaw(texture); }
        }
        public void Dispose() { ReleaseTexture(); view.Dispose(); memory.Dispose(); gate.Dispose(); pixels = Array.Empty<byte>(); }
    }
}

internal sealed class CefPeer : IDisposable
{
    private readonly Channel<string> queue = Channel.CreateBounded<string>(512);
    private readonly CancellationTokenSource stop = new();
    private readonly Action<string> output;
    private Process? process;
    private Task? writerTask;
    private int disposed;
    public CefPeer(string root, string profile, string proxy, Action<string> onOutput)
    {
        output = onOutput;
        _ = Run(root, profile, proxy);
    }
    public void Send(object message)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (!queue.Writer.TryWrite(JsonSerializer.Serialize(message)))
        { output(JsonSerializer.Serialize(new { Kind = "error", Error = "输入队列已满，请关闭并重新打开网页。" })); Dispose(); }
    }
    private async Task Run(string root, string profile, string proxy)
    {
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(Path.Combine(root, "Yanjiu.BrowserHost.exe"))
            { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--yanjiu-profile=" + profile); start.ArgumentList.Add("--yanjiu-proxy=" + proxy);
            process = Process.Start(start) ?? throw new IOException("无法启动 CEF 进程");
            if (stop.IsCancellationRequested) { try { process.Kill(entireProcessTree: true); } catch { } return; }
            var errors = new ConcurrentQueue<string>();
            var stderr = DrainErrors(process, stop.Token, errors);
            var writer = writerTask = Task.Run(async () =>
            {
                await foreach (var line in queue.Reader.ReadAllAsync(stop.Token))
                { await process.StandardInput.WriteLineAsync(line); await process.StandardInput.FlushAsync(stop.Token); }
            }, stop.Token);
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(stop.Token)) != null)
            { if (line.Length <= 65536) output(line); }
            if (!stop.IsCancellationRequested)
            {
                await process.WaitForExitAsync(stop.Token);
                await stderr;
                var detail = string.Join("\n", errors);
                output(JsonSerializer.Serialize(new { Kind = "error", Error = $"浏览器进程已退出（0x{process.ExitCode:X8}）。" +
                    (detail.Length > 0 ? "\n" + detail : "请关闭并重新打开网页。") }));
            }
            Dispose();
            try { await writer; } catch (OperationCanceledException) { }
            await stderr;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!stop.IsCancellationRequested) output(JsonSerializer.Serialize(new { Kind = "error", Error = ex.Message })); Dispose(); }
    }
    private static async Task DrainErrors(Process p, CancellationToken token, ConcurrentQueue<string> errors)
    { try { string? line; while ((line = await p.StandardError.ReadLineAsync(token)) != null)
        { errors.Enqueue(line.Length > 500 ? line.Substring(0, 500) : line); while (errors.Count > 8) errors.TryDequeue(out _); }
    } catch (OperationCanceledException) { } }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); queue.Writer.TryComplete();
        _ = Task.Run(async () =>
        {
            try
            {
                if (writerTask != null) { try { await writerTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { } }
                var owned = process;
                if (owned == null || owned.HasExited) return;
                // EOF lets Cef.Shutdown flush cookies and storage; a stuck helper is terminated as a fallback.
                owned.StandardInput.Close();
                try { await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { if (!owned.HasExited) owned.Kill(entireProcessTree: true); }
            }
            catch { try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { } }
        });
    }
}

internal sealed class CefInstaller
{
    // The base runtime package contains locales/runtime.json only. The RID package includes both binaries and locales.
    private const string NativeUrl = "https://api.nuget.org/v3-flatcontainer/chromiumembeddedframework.runtime.win-x64/154.0.34/chromiumembeddedframework.runtime.win-x64.154.0.34.nupkg";
    private const string NativeSha512 = "MnHgTXjzGD4nuaNByVcPCcOnVVDBJ5c/cA4BDVwdm5eBnChDTpaUp/uYpozffVOkvRBjXwCpfmsI8OSdfLH3ig==";
    private const string HostUrl = "https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/download/browser-v1.4.3/BrowserHost.zip";
    private const string HostSha256 = "1eac3d4675bfc982c4d883628a15439ad1e5e885673a4dff49658e34003af54a";
    private const string HostDllSha256 = "77dc7a54fb0250c6a2fed3c8782ebd78eb604faf8d2def594d9360bf98b37919";
    private readonly string root;
    private CancellationTokenSource? cancel;
    public volatile bool Busy;
    public volatile string Status = "";
    private long bytes, total;
    public long Bytes => Interlocked.Read(ref bytes);
    public long Total => Interlocked.Read(ref total);
    public bool NativeInstalled => File.Exists(Path.Combine(root, "verified.json")) && File.Exists(Path.Combine(root, "libcef.dll")) && File.Exists(Path.Combine(root, "locales", "zh-CN.pak"));
    public bool Ready
    {
        get
        {
            if (!NativeInstalled || !File.Exists(Path.Combine(root, "Yanjiu.BrowserHost.exe"))) return false;
            try { var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "verified.json")));
                return hashes != null && hashes.TryGetValue("Yanjiu.BrowserHost.dll", out var hash) && hash.Equals(HostDllSha256, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
    public CefInstaller(string installRoot) { root = installRoot; }
    public void Start() { if (Busy) return; Busy = true; cancel = new CancellationTokenSource(); _ = Install(cancel.Token); }
    public void Cancel() => cancel?.Cancel();
    private async Task Install(CancellationToken token)
    {
        var staging = root + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            var native = Path.Combine(staging, "cef.nupkg"); var host = Path.Combine(staging, "host.zip");
            bool bridgeOnly = NativeInstalled;
            if (!bridgeOnly)
            {
                await Download(NativeUrl, native, "正在下载 CEF 内核", token);
                using var file = File.OpenRead(native);
                if (!CryptographicOperations.FixedTimeEquals(await SHA512.HashDataAsync(file, token), Convert.FromBase64String(NativeSha512))) throw new IOException("CEF 校验失败，安装已取消");
            }
            await Download(HostUrl, host, "正在下载浏览器桥接程序", token);
            using (var file = File.OpenRead(host))
                if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, token), Convert.FromHexString(HostSha256))) throw new IOException("桥接程序校验失败，安装已取消");
            Status = "正在解压和校验，首次安装可能需要稍等";
            var install = Path.Combine(staging, "install"); Directory.CreateDirectory(install);
            if (!bridgeOnly) Extract(native, install, nativeOnly: true, token);
            Extract(host, install, nativeOnly: false, token);
            var engineRoot = bridgeOnly ? root : install;
            foreach (var file in new[] { "libcef.dll", "icudtl.dat", "resources.pak", "chrome_elf.dll" })
                if (!File.Exists(Path.Combine(engineRoot, file))) throw new IOException("安装包缺少 " + file);
            foreach (var file in new[] { "Yanjiu.BrowserHost.exe", "Yanjiu.BrowserHost.dll", "CefSharp.Core.Runtime.dll" })
                if (!File.Exists(Path.Combine(install, file))) throw new IOException("安装包缺少 " + file);
            foreach (var locale in new[] { "zh-CN.pak", "en-US.pak" })
                if (!File.Exists(Path.Combine(engineRoot, "locales", locale))) throw new IOException("安装包缺少 locales/" + locale);
            var hashes = bridgeOnly ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "verified.json")))! : new Dictionary<string, string>();
            foreach (var file in Directory.EnumerateFiles(install, "*", SearchOption.AllDirectories))
            { using var stream = File.OpenRead(file); hashes[Path.GetRelativePath(install, file)] = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)); }
            await File.WriteAllTextAsync(Path.Combine(install, "verified.json"), JsonSerializer.Serialize(hashes), token);
            token.ThrowIfCancellationRequested();
            if (bridgeOnly) ReplaceBridge(install, root, Path.Combine(staging, "rollback"));
            else
            {
                if (Directory.Exists(root)) throw new IOException("目标目录已存在，请关闭浏览器并移除未完成的安装目录后重试");
                Directory.Move(install, root);
            }
            Status = bridgeOnly ? "浏览器组件已更新，原 CEF 内核和登录资料已保留。" : "CEF 下载完成，可以打开网页了。";
        }
        catch (OperationCanceledException) { Status = "下载已取消，可重新点击下载。"; }
        catch (Exception ex) { Status = "下载失败：" + ex.Message; }
        finally
        { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } Busy = false; }
    }
    private async Task Download(string url, string path, string label, CancellationToken token)
    {
        Status = label; Interlocked.Exchange(ref bytes, 0); Interlocked.Exchange(ref total, 0);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("YanjiuBrowser/1.4.3");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        Interlocked.Exchange(ref total, response.Content.Headers.ContentLength ?? 0);
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        var buffer = new byte[65536]; var clock = Stopwatch.StartNew();
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), token); Interlocked.Add(ref bytes, count);
            if (Bytes > 1073741824) throw new IOException("下载包超过允许大小");
            Status = $"{label} · {Bytes / 1048576d / Math.Max(0.1, clock.Elapsed.TotalSeconds):F1} MB/s";
        }
        if (Total > 0 && Bytes != Total) throw new IOException("下载不完整");
    }
    internal static void Extract(string archive, string target, bool nativeOnly, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        long expanded = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/');
            if (nativeOnly)
            {
                var prefix = new[] { "runtimes/win-x64/native/", "CEF/win-x64/" }
                    .FirstOrDefault(p => relative.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (prefix == null)
                {
                    if (relative.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase)) relative = "CEF.LICENSE.txt";
                    else continue;
                }
                else relative = relative.Substring(prefix.Length);
            }
            if (relative.Length == 0 || relative.EndsWith('/')) continue;
            if (relative.Split('/').Any(p => p is "." or ".." || p.Contains(':')) || Path.IsPathRooted(relative)) throw new IOException("安装包路径无效");
            var full = Path.GetFullPath(Path.Combine(target, relative));
            if (!full.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !seen.Add(full)) throw new IOException("安装包路径重复或越界");
            expanded += entry.Length; if (expanded > 2147483648) throw new IOException("解压内容过大");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!); entry.ExtractToFile(full, false);
        }
    }
    internal static void ReplaceBridge(string install, string target, string rollback)
    {
        var files = Directory.GetFiles(install, "*", SearchOption.AllDirectories)
            .OrderBy(p => Path.GetFileName(p) == "verified.json" ? 1 : 0).ToArray();
        var written = new List<(string Destination, string Backup, bool Existed)>();
        try
        {
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(install, file);
                var destination = Path.Combine(target, relative); var backup = Path.Combine(rollback, relative);
                var existed = File.Exists(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(destination, backup); }
                written.Add((destination, backup, existed));
                File.Copy(file, destination, true);
            }
        }
        catch
        {
            foreach (var item in written.AsEnumerable().Reverse())
                try { if (item.Existed) File.Copy(item.Backup, item.Destination, true); else File.Delete(item.Destination); } catch { }
            throw;
        }
    }
}
