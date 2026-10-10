using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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

    private const int  GWL_HWNDPARENT = -8;
    private const int  SW_HIDE        = 0;
    private const int  SW_SHOWNA      = 8;
    private const int  SW_RESTORE     = 9;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint WM_CLOSE       = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

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

        SyncCommands();

        lock (launchGate) { anyLaunching = false; pendingLaunches.Clear(); }

        watchCts = new CancellationTokenSource();
        _ = Task.Run(() => WatchLoop(watchCts.Token));
    }

    protected override void OnDisable()
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
        catch {   }
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

    private void WatchLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var dead = windows.Where(kv => kv.Value.Hwnds.Count > 0 && kv.Value.Hwnds.All(h => !IsWindow(h)))
                                  .Select(kv => kv.Key).ToList();
                foreach (var k in dead) windows.Remove(k);

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

                if (Environment.TickCount64 < focusGameUntilMs) FocusGame();
            }
            catch {   }

            Thread.Sleep(200);
        }
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

        var game = FindGameHwnd();
        if (game == IntPtr.Zero) return;

        try
        {
            SetWindowLongPtr(hwnd, GWL_HWNDPARENT, game);

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

    private static void FocusGame()
    {
        var g = FindGameHwnd();
        if (g != IntPtr.Zero)
            try { SetForegroundWindow(g); } catch {   }
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
            if (WinOpen(win) && win.Visible)
            {
                HideBrowser(win);
                win.Visible = false;
                return;
            }

            if (!WinOpen(win))
            {
                LaunchPage(page, key, win);
                return;
            }

            foreach (var h in win.Hwnds)
            {
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                else ShowWindow(h, SW_SHOWNA);
            }
            win.Visible = true;
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
            try { ShowWindow(h, SW_HIDE); } catch {   }
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
                ShowWindow(h, SW_HIDE);
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
                FocusGame();
                foreach (var h in win.Hwnds) PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                focusGameUntilMs = Environment.TickCount64 + 1500;
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

    public override bool HasSettings => true;

    public override bool DrawSettings()
    {
        var changed = false;

        int removeIndex = -1;

        if (config.Pages.Count > 0)
        {
            var wRight = ImGui.GetWindowWidth();
            ImGui.TextDisabled("名字");
            ImGui.SameLine(108f);      ImGui.TextDisabled("宏命令");
            ImGui.SameLine(206f);      ImGui.TextDisabled("网址");
            ImGui.SameLine(wRight - 305f); ImGui.TextDisabled("代理");
            ImGui.SameLine(wRight - 248f); ImGui.TextDisabled("无边框");
            ImGui.SameLine(wRight - 176f); ImGui.TextDisabled("开关");
            ImGui.SameLine(wRight - 120f); ImGui.TextDisabled("删除");
        }

        for (var i = 0; i < config.Pages.Count; i++)
        {
            var page = config.Pages[i];
            var key  = NormalizeCommand(page.Command);
            var open = windows.TryGetValue(key, out var w) && w.Hwnds.Any(IsWindow);

            ImGui.PushID(i);

            var name = page.Name;
            ImGui.SetNextItemWidth(100f);
            var nameEdited = ImGui.InputText("##Name", ref name, 64);
            if (name.Length == 0)
                DrawPlaceholder("如: 百度");
            if (nameEdited) { page.Name = name; changed = true; }

            ImGui.SameLine();
            var cmd = page.Command;
            ImGui.SetNextItemWidth(90f);
            var cmdEdited = ImGui.InputText("##Cmd", ref cmd, 64);
            if (cmd.Length == 0)
                DrawPlaceholder("如: /baidu");
            if (cmdEdited)
            {
                page.Command = cmd;
                changed = true;
                SyncCommands();
            }

            ImGui.SameLine();
            var url = page.Url;
            ImGui.SetNextItemWidth(-336f);
            var urlEdited = ImGui.InputText("##Url", ref url, 512);
            if (url.Length == 0)
                DrawPlaceholder("如: https://www.baidu.com");
            if (urlEdited) { page.Url = url; changed = true; }

            ImGui.SameLine();
            var useProxy = page.UseProxy;
            if (ImGui.Checkbox("代理", ref useProxy)) { page.UseProxy = useProxy; changed = true; }

            ImGui.SameLine();
            var useApp = page.UseAppMode;
            if (ImGui.Checkbox("无边框", ref useApp)) { page.UseAppMode = useApp; changed = true; }

            ImGui.SameLine();
            if (ImGui.Button(open ? "关闭" : "打开", new System.Numerics.Vector2(56f, 0f)))
            {
                if (open) ClosePage(page);
                else      TogglePage(page);
            }

            ImGui.SameLine();
            if (ImGui.Button("删除", new System.Numerics.Vector2(56f, 0f))) removeIndex = i;

            ImGui.PopID();
        }

        if (removeIndex >= 0)
        {
            var page = config.Pages[removeIndex];
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
            if (windows.TryGetValue(cmd, out var rw) && rw.Hwnds.Count > 0)
            {
                FocusGame();
                foreach (var h in rw.Hwnds)
                    try { PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
                focusGameUntilMs = Environment.TickCount64 + 1500;
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
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("左边距");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60f);
            var offX = config.OffsetX;
            if (ImGui.InputInt("##OffX", ref offX)) { config.OffsetX = offX; changed = true; }

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("上边距");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60f);
            var offY = config.OffsetY;
            if (ImGui.InputInt("##OffY", ref offY)) { config.OffsetY = offY; changed = true; }

            ImGui.SameLine();
            if (ImGui.Button("恢复默认窗口"))
            {
                config.WinWidth  = DefaultWinWidth;
                config.WinHeight = DefaultWinHeight;
                config.OffsetX   = DefaultOffsetX;
                config.OffsetY   = DefaultOffsetY;
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("应用到已打开的窗口"))
                ApplyWindowGeometryToOpen();

            ImGui.Spacing();
            ImGui.TextDisabled("浏览器路径");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("###BrowserPath", ref browserPathInput, 512))
            {
                config.BrowserPath = browserPathInput;
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
        }

        if (statusMessage.Length > 0)
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.4f, 1f), statusMessage);

        if (changed) SaveOwnConfig();
        return changed;
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

