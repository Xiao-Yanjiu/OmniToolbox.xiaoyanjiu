// ============================================================================
// GameWebBrowser.Omni.cs —— Omni 妙妙屋 本地模块（TreeHouse）「游戏内浏览器」 v1
//
// 导入方法：
//   Omni 妙妙屋 → 本地模块 → 填入本文件绝对路径
// ============================================================================

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
        Description = "在游戏窗口内打开一个或多个网页浏览器（Edge/Chrome 带标签页窗口），用于 Discord、鱼糕等网站。\n" +
                      "每个网页可单独设置网址、宏命令和网络代理（留空直连），点击对应命令即可打开 / 最小化。网页内点链接在窗口内开新标签页。",
        Category    = ModuleCategory.Interface,
        Author      = "小烟酒",
    };

    // ------------------------------ Win32 ------------------------------

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

    // ------------------------------ 常量 ------------------------------

    // 窗口默认大小 / 位置（可在设置界面里改，这里只是默认值）
    private const int DefaultWinWidth  = 1100;
    private const int DefaultWinHeight = 720;
    private const int DefaultOffsetX   = 120;
    private const int DefaultOffsetY   = 100;

    // 生效值：配置里为 0（或旧配置没这两个字段）时回落到默认常量
    private int WinW => config.WinWidth  > 0 ? config.WinWidth  : DefaultWinWidth;
    private int WinH => config.WinHeight > 0 ? config.WinHeight : DefaultWinHeight;

    private static string ConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncherCN", "pluginConfigs", "OmniGameWebBrowser.json");

    // ------------------------------ 状态 ------------------------------

    private Config config = new();

    private class BrowserWin
    {
        // Chromium 可能产生多个顶层窗口（主窗口 + 附属小窗），全部跟踪才能隐藏干净
        public HashSet<IntPtr> Hwnds = new();
        public bool Visible;
        public bool Launching;
        public long LaunchStartMs;
    }

    private readonly Dictionary<string, BrowserWin> windows = new();
    private HashSet<IntPtr> windowSnapshot = new();
    private string? activeLaunchKey;
    private CancellationTokenSource? watchCts;

    private RECT  lastGameRect;
    private bool  hasLastGameRect;
    private string statusMessage = "";

    private ICommandManager? commandManager;
    private readonly HashSet<string> registeredCommands = new();

    private string browserPathInput = "";

    // ------------------------------ 生命周期 ------------------------------

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
            statusMessage = "命令服务获取成功";

        SyncCommands();

        watchCts = new CancellationTokenSource();
        _ = Task.Run(() => WatchLoop(watchCts.Token));
    }

    protected override void OnDisable()
    {
        if (commandManager != null)
        {
            foreach (var cmd in registeredCommands.ToList())
            {
                try { commandManager.RemoveHandler(cmd); } catch { /* 忽略 */ }
            }
        }
        registeredCommands.Clear();
        commandManager = null;

        watchCts?.Cancel();
        watchCts?.Dispose();
        watchCts = null;
        // 浏览器是独立进程，停用模块不关闭它们
    }

    // ------------------------------ 配置自持久化 ------------------------------

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
        catch { /* 损坏则用默认配置 */ }
        browserPathInput = config.BrowserPath;
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
        catch { /* 保存失败静默 */ }
    }

    // ------------------------------ 命令 ------------------------------

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
            try { commandManager.RemoveHandler(cmd); } catch { /* 忽略 */ }
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

    // ------------------------------ 主循环（后台线程） ------------------------------

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
                        statusMessage = "";
                    }
                    else if (Environment.TickCount64 - launching.LaunchStartMs > 30_000)
                    {
                        launching.Launching = false;
                        activeLaunchKey = null;
                        statusMessage = "未捕获到浏览器窗口（30 秒超时）";
                    }
                }

                FollowGameWindow();
            }
            catch { /* 后台线程异常静默 */ }

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
        win.Hwnds.Add(hwnd);
        win.Visible = true;

        var game = FindGameHwnd();
        if (game == IntPtr.Zero) return;

        try
        {
            SetWindowLongPtr(hwnd, GWL_HWNDPARENT, game);   // 归属游戏窗口，不是 SetParent

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

    /// <summary>把当前「窗口位置与大小」设置立刻套用到所有已打开的浏览器窗口。</summary>
    private void ApplyWindowGeometryToOpen()
    {
        var game = FindGameHwnd();
        if (game == IntPtr.Zero) { statusMessage = "未找到游戏窗口"; return; }
        if (!GetWindowRect(game, out var gameRect)) { statusMessage = "读取游戏窗口位置失败"; return; }

        List<IntPtr> hwnds;
        try { hwnds = windows.Values.SelectMany(w => w.Hwnds).ToList(); }
        catch { hwnds = new List<IntPtr>(); }   // 后台线程正在改集合时退化为空快照

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
            catch { /* 单个窗口失败忽略 */ }
        }
        statusMessage = n > 0 ? $"已应用窗口位置与大小（{n} 个窗口）" : "当前没有已打开的窗口";
    }

    private static IntPtr FindGameHwnd() => FindWindowW("FFXIVGAME", null);

    private List<IntPtr> FindNewBrowserWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (windowSnapshot.Contains(hWnd)) return true;
                var cls = new StringBuilder(256);
                GetClassName(hWnd, cls, 256);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == 0) return true;
                string name;
                try { name = Process.GetProcessById((int)pid).ProcessName; }
                catch { return true; }
                if (name is "msedge" or "chrome") list.Add(hWnd);
            }
            catch { /* 忽略 */ }
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
            catch { /* 忽略 */ }
            return true;
        }, IntPtr.Zero);
        return set;
    }

    // ------------------------------ 打开 / 关闭 ------------------------------

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
        foreach (var h in win.Hwnds)
        {
            try { ShowWindow(h, SW_HIDE); } catch { /* 忽略 */ }
        }

        var game = FindGameHwnd();
        if (game == IntPtr.Zero) return;
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                var cls = new StringBuilder(256);
                GetClassName(h, cls, 256);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                if (GetWindowLongPtr(h, GWL_HWNDPARENT) != game) return true;
                if (win.Hwnds.Contains(h)) return true;
                ShowWindow(h, SW_HIDE);
            }
            catch { /* 忽略 */ }
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

        var proxy = (page.Proxy ?? "").Trim();

        // 每个网页独立 profile 目录，登录态隔离
        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVGameBrowser", "profiles", SanitizeKey(key));
        try { Directory.CreateDirectory(profileDir); } catch { /* 忽略 */ }

        var args = new StringBuilder();
        if (page.UseAppMode)
        {
            // 无边框 app 模式：点链接/target=_blank 会被甩给系统默认浏览器
            args.Append("--app=\"").Append(url).Append("\" ");
        }
        else
        {
            // 普通窗口模式（带标签页）：网页内点链接在窗口内开新标签页
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
                foreach (var h in win.Hwnds) PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
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

    // ------------------------------ 反射 ------------------------------

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

    // ------------------------------ UI ------------------------------

    public override bool HasSettings => true;

    public override bool DrawSettings()
    {
        var changed = false;

        // ---- 状态 ----
        var openCount = windows.Values.Count(w => w.Hwnds.Any(IsWindow));
        ImGui.TextUnformatted($"命令服务: {(commandManager != null ? "正常" : "失败")}   ·   已打开窗口: {openCount}");
        if (statusMessage.Length > 0)
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.4f, 1f), statusMessage);
        ImGui.Separator();

        // ---- 网页列表 ----
        ImGui.TextUnformatted("网页列表（每个网页一个宏命令）:");
        ImGui.Spacing();

        int removeIndex = -1;
        for (var i = 0; i < config.Pages.Count; i++)
        {
            var page = config.Pages[i];

            ImGui.PushID(i);
            ImGui.Separator();

            var name  = page.Name;
            var url   = page.Url;
            var cmd   = page.Command;
            var proxy = page.Proxy;

            ImGui.TextUnformatted($"#{i + 1}  命令: {NormalizeCommand(cmd)}");
            var state = windows.TryGetValue(NormalizeCommand(cmd), out var w) && w.Hwnds.Any(IsWindow)
                ? (w.Visible ? "显示中" : "已最小化")
                : "未打开";
            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.85f, 0.5f, 1f), state);

            ImGui.TextDisabled("名字");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("##Name", ref name, 64)) { page.Name = name; changed = true; }

            ImGui.TextDisabled("网址");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("##Url", ref url, 512)) { page.Url = url; changed = true; }

            ImGui.TextDisabled("宏命令 (以 / 开头)");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("##Cmd", ref cmd, 64))
            {
                page.Command = cmd;
                changed = true;
                SyncCommands();
            }

            ImGui.TextDisabled("网络代理 (留空直连)");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("##Proxy", ref proxy, 256)) { page.Proxy = proxy; changed = true; }

            var useApp = page.UseAppMode;
            if (ImGui.Checkbox("无边框 app 模式", ref useApp)) { page.UseAppMode = useApp; changed = true; }

            if (ImGui.Button("打开 / 最小化")) TogglePage(page);
            ImGui.SameLine();
            if (ImGui.Button("关闭")) ClosePage(page);
            ImGui.SameLine();
            if (ImGui.Button("删除此网页")) removeIndex = i;

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
            catch { /* 忽略 */ }
            if (windows.TryGetValue(cmd, out var w) && w.Hwnds.Count > 0)
                foreach (var h in w.Hwnds)
                    try { PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { /* 忽略 */ }
            windows.Remove(cmd);
            config.Pages.RemoveAt(removeIndex);
            changed = true;
        }

        ImGui.Spacing();
        if (ImGui.Button("＋ 添加网页"))
        {
            config.Pages.Add(new PageEntry
            {
                Name    = "新网页",
                Url     = "",
                Command = "/new",
                Proxy   = "",
            });
            changed = true;
            SyncCommands();
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ---- 窗口位置与大小（全局）----
        ImGui.TextUnformatted("窗口位置与大小（所有网页窗口共用）:");
        ImGui.Spacing();

        ImGui.TextDisabled("宽度");
        ImGui.SetNextItemWidth(120f);
        var winW = config.WinWidth > 0 ? config.WinWidth : DefaultWinWidth;
        if (ImGui.InputInt("##WinW", ref winW))
        {
            config.WinWidth = Math.Max(320, Math.Min(winW, 7680));
            changed = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("高度");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        var winH = config.WinHeight > 0 ? config.WinHeight : DefaultWinHeight;
        if (ImGui.InputInt("##WinH", ref winH))
        {
            config.WinHeight = Math.Max(240, Math.Min(winH, 4320));
            changed = true;
        }

        ImGui.TextDisabled("左边距（相对游戏窗口，可为负）");
        ImGui.SetNextItemWidth(120f);
        var offX = config.OffsetX;
        if (ImGui.InputInt("##OffX", ref offX)) { config.OffsetX = offX; changed = true; }

        ImGui.SameLine();
        ImGui.TextDisabled("上边距");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        var offY = config.OffsetY;
        if (ImGui.InputInt("##OffY", ref offY)) { config.OffsetY = offY; changed = true; }

        if (ImGui.Button("恢复默认窗口 (1100×720，偏移 120,100)"))
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

        ImGui.TextWrapped("位置是相对游戏窗口左上角的偏移，浏览器窗口会跟随游戏窗口一起移动。改完想立刻见效：点「应用到已打开的窗口」，或把窗口关掉重开。");

        ImGui.Spacing();
        ImGui.Separator();

        // ---- 浏览器路径 ----
        ImGui.TextWrapped("浏览器路径 (留空自动搜索 Edge / Chrome):");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("###BrowserPath", ref browserPathInput, 512))
        {
            config.BrowserPath = browserPathInput;
            changed = true;
        }

        ImGui.Separator();

        ImGui.TextWrapped("用法: 每个网页对应一个宏命令（如 /discord），游戏里新建用户宏填入该命令、右键图标选个图标拖到快捷栏即可像技能一样点击；也可直接在聊天框输入命令。");
        ImGui.TextWrapped("宏图标: 只能用游戏内已有图标（宏界面右键选择），Discord / 鱼糕的 logo 无法作为宏图标。");
        ImGui.TextWrapped("代理: Discord 需在其「网络代理」一栏填代理（如 socks5://127.0.0.1:10808），鱼糕等国内站留空直连。每个网页登录目录独立。");

        if (changed) SaveOwnConfig();
        return changed;
    }

    // ------------------------------ 数据模型 ------------------------------

    private class PageEntry
    {
        public string Name    { get; set; } = "";
        public string Url     { get; set; } = "";
        public string Command { get; set; } = "";
        public string Proxy   { get; set; } = "";
        public bool   UseAppMode { get; set; } = false;
    }

    private class Config
    {
        public List<PageEntry> Pages { get; set; } = new List<PageEntry>();

        public string BrowserPath { get; set; } = "";

        // ---- 窗口位置与大小（全局，所有网页窗口共用）----
        // 位置为「相对游戏窗口左上角的偏移」，浏览器窗口会跟随游戏窗口一起移动。
        public int WinWidth  { get; set; } = DefaultWinWidth;
        public int WinHeight { get; set; } = DefaultWinHeight;
        public int OffsetX   { get; set; } = DefaultOffsetX;
        public int OffsetY   { get; set; } = DefaultOffsetY;
    }
}
