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
using DragDropVisibilityFlag = FFXIVClientStructs.FFXIV.Component.GUI.DragDropVisibilityFlag;
using IconComponentFlags = FFXIVClientStructs.FFXIV.Component.GUI.IconComponentFlags;
using AtkStage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage;
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

    private const string Tag = "[ToolbarIconPlus]";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";

    private static string AppDataDir => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string LocalAppDataDir => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string OwnConfigPath =>
        Path.Combine(AppDataDir, "XIVLauncherCN", "pluginConfigs", "OmniToolbarIconPlus.json");

    private static string IconCacheDir =>
        Path.Combine(LocalAppDataDir, "OmniToolbarIconPlus", "icons");

    public override ModuleInfo Info { get; } = new()
    {
        Title = "更好的图标管理",
        Description = "",
        Category = ModuleCategory.Interface,
        Author = "小烟酒",
    };

    private Config config = new();

    private string newInput = "";

    private string statusLine = "";
    private bool statusIsError;

    private bool deepProbe;

    private static object? managerCache;
    private static PropertyInfo? saveHostConfigPropCache;

    private const uint BrowserIdBase = 0x7F000000u;
    private const int BrowserIdCapacity = 256;
    private long lastBrowserEnsureTick;
    private long lastManagerAttemptTick;
    private string browserState = "";
    private static Type? texProviderTypeCache;
    private static object? texProviderCache;
    private static MethodInfo? getFromFileCache;
    private string lastInjectionDiag = "";

    private const string HarmonyId = "ToolbarIconPlus.Omni";
    private static Type? harmonyTypeCache;
    private static System.Reflection.Assembly? harmonyAsmCache;
    private static string harmonyLoadInfo = "未尝试";
    private static bool harmonyDiskTried;
    private static object? harmonyInstance;
    private static bool detoursInstalled;
    private static string detourState = "detour 未挂";
    private static string texProviderState = "未探测";
    private static ToolbarIconPlus? selfRef;
    private static readonly Dictionary<uint, object?> detourWrapCache = new Dictionary<uint, object?>();
    private static readonly Dictionary<uint, object?> detourSharedKeep = new Dictionary<uint, object?>();
    private static readonly Dictionary<uint, object?> browserSharedKeep = new Dictionary<uint, object?>();
    private const int DetourTargetCount = 5;
    private static readonly HashSet<string> detourPatched = new HashSet<string>(StringComparer.Ordinal);
    private static string detourLastLogged = "detour 未挂";
    private static long lastDetourRetryTick;
    private static bool startupApplied;
    private static long lastStartupApplyTick;
    private static int managerAttempts;
    private static bool autoDeepProbeDone;

    private static Delegate? frameworkTickDelegate;

    private const string MacroIconCommand = "/图库图标";
    private const string MacroIconCommandAlias = "/ticon";
    private const int MacroIconKernelSize = 80;
    private const int MacroIconWalkDepth = 24;
    private const int MacroIconNodeBudget = 8192;
    private const long MacroIconSweepBudgetMs = 4;
    private const int MacroIconSwapIntervalMs = 250;

    private static readonly uint[] MacroIconPlaceholders = { 62100, 62101, 60409, 1 };

    private static bool macroIconCommandRegistered;
    private static string macroIconCommandState = "未注册";
    private static readonly ConcurrentDictionary<uint, IntPtr> macroKernels = new();
    private static readonly ConcurrentDictionary<uint, byte> macroKernelPending = new();
    private static readonly ConcurrentDictionary<uint, byte> macroKernelFailed = new();
    private static readonly ConcurrentQueue<IDalamudTextureWrap> macroKernelKeeps = new();
    private static string macroIconKernelDiag = "";
    private static int macroSwapHits;
    private static int macroSwapUnits;
    private static long lastMacroSwapTick;
    private static int macroSwapCursor;

    private static Hook<AtkComponentIconLoadIconDelegate>? macroIconLoadHook;
    private static string macroIconHookState = "未挂";

    private static Hook<AtkTextureLoadIconTextureDelegate>? macroTexHook;
    private static string macroTexHookState = "未挂";
    private static int macroTexHookCalls;
    private static int macroTexHookHits;

    private static int macroSlotRebuilds;
    private static readonly Dictionary<nint, long> macroRebuildBackoff = new Dictionary<nint, long>();

    private static int macroSlotReverts;
    private static int macroRevertUnknown;
    private static readonly Dictionary<nint, long> macroRevertBackoff = new Dictionary<nint, long>();
    private static int macroEmptyCompSeen;
    private static int macroRebuildAttempts;
    private static int macroRebuildFails;

    private const int ActionBarSlotStride = 0xC8;
    private const int AbeVectorOff = 0x238;
    private const int AbeHotbarIdOff = 0x254;
    private const int AbeSlotCountOff = 0x256;
    private const int AbsIconNodeOff = 0xB8;
    private const int CompNodeCompOff = 0xC0;
    private const int HSlotIconIdOff = 0xD0;
    private const int HSlotCmdIdOff = 0xB8;
    private const int HSlotCmdTypeOff = 0xC7;
    private static int macroHotbarOwnSlots;
    private static int macroHotbarFixes;
    private static int macroHotbarBlindFixes;
    private static string macroHotbarDiag = "";
    private static string lastMacroHotbarDiagSig = "";
    private static readonly uint[] macroHbSlotIcons = new uint[32];
    private static readonly uint[] macroHbCompIds = new uint[32];
    private static readonly uint[] macroHbLibIds = new uint[32];
    private static readonly nint[] macroHbIconPtrs = new nint[32];
    private static readonly bool[] macroHbHasIcon = new bool[32];

    private static Hook<ExecuteMacroDelegate>? macroExecHook;
    private static string macroExecHookState = "未挂";
    private static int macroExecSeen;
    private static int macroExecHits;
    private static string macroExecLastDiag = "无";
    private static int macroIconHookCalls;
    private static int macroIconHookReserved;
    private static int macroIconHookRuled;
    private static int macroIconHookSwaps;
    private static long lastMacroHookLogTick;
    private static string lastMacroHookLogSig = "";
    private static int macroIconHookErrors;
    private static long lastMacroSweepLogTick;
    private static string lastMacroSweepLogSig = "";
    private static int macroSweepComps;
    private static int macroSweepReserved;
    private static int macroSweepGatedByMask;
    private static int macroSweepNotVisible;
    private static int macroSweepCompsMark;
    private static int macroSweepCompsDelta;
    private static int macroSweepRuledDelta;
    private static int macroSweepReservedDelta;
    private static int macroSweepRuledLast;
    private static int macroSweepReservedLast;
    private static int macroSweepRuled;

    private static readonly Dictionary<int, uint> macroPanelApplied = new();
    private static bool macroPanelWasVisible;
    private static uint macroPanelSetSeen = 9;
    private static uint macroPanelPageSeen = 9;
    private static nint macroPanelCompSeen;
    private static long lastHotbarSyncTick;
    private static int macroHotbarReloads;

    private static int macroLineSeenIndex = -1;
    private static string macroLineTextSeen = "";
    private static bool macroLinePrimed;
    private static string macroShellState = "未检测";
    private static int macroLineSeen;
    private static int macroLineLogged;
    private static int macroLineHits;
    private static string macroLineLastDiag = "无";

    private static long lastMacroTextSweepTick;
    private static int macroTextCmdSeen;
    private static int macroTextWrites;
    private static string macroTextLastDiag = "无";

    private static int macroUiRefreshes;
    private static int macroPanelFixes;

    private static int macroPanelScans;
    private static long lastFastMacroCheckTick;
    private static int fastMacroHits;
    private static long lastMacroOpenTick;
    private static int macroRunGuardHits;

    private static readonly Dictionary<int, uint> macroIconSlots = new Dictionary<int, uint>();
    private static long lastPanelSyncTick;

    private const int IconRuleMax = 512;
    private static readonly ConcurrentDictionary<uint, uint> iconRuleMap = new();
    private static readonly ConcurrentDictionary<IntPtr, IconSlotPatch> iconSlotPatches = new();
    private static int iconRuleSwaps;
    private static int iconRuleRestores;
    private static long lastIconPatchPruneTick;
    private static string iconRuleState = "0 条";
    private string ruleFromInput = "";
    private string ruleToInput = "";

    private sealed class IconSlotPatch
    {
        public uint From;
        public uint To;
        public IntPtr Kernel;
        public long LastSeen;
    }

    private volatile bool moduleActive;
    private volatile bool busy;
    private volatile string busyLabel = "";
    private volatile FetchResult? pendingFetch;

    private static readonly HttpClient Http = CreateHttpClient(false, "");

    public ToolbarIconPlus()
    {
        LoadOwnConfig();
        try { Directory.CreateDirectory(IconCacheDir); } catch {   }
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
            InstallDetours();
            RegisterMacroIconCommand();
            InstallMacroIconHook();
            InstallMacroTextureHook();
            InstallMacroExecHook();
            EnsureBrowserInjection(true);
            EnsureFrameworkTick();
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
            UninstallBrowserInjection();
            UninstallMacroTextureHook();
            RestoreAllIconPatches();
            UnregisterMacroIconCommand();
            UninstallMacroIconHook();
            UninstallMacroExecHook();
            UninstallDetours();
        }
        catch {   }
    }

    protected override void OnDispose()
    {
        try
        {
            moduleActive = false;
            pendingFetch = null;
            busy = false;
            UnhookFrameworkTick();
            UninstallBrowserInjection();
            UninstallMacroTextureHook();
            RestoreAllIconPatches();
            UnregisterMacroIconCommand();
            UninstallMacroIconHook();
            UninstallMacroExecHook();
            UninstallDetours();
        }
        catch {   }
    }

    private void LoadOwnConfig()
    {
        try
        {
            var path = OwnConfigPath;
            if (!File.Exists(path)) return;
            var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(path, Encoding.UTF8));
            if (loaded != null) config = loaded;
            if (config.UseProxy)
            {
                foreach (var b in config.BrowserImages) b.UseProxy = true;
                config.UseProxy = false;
            }
            NormalizeIconIdSpace();
            RebuildIconRuleMap();
        }
        catch {   }
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
        catch {   }
    }

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
                        InvalidateIconCaches(entry.Id);
                        SaveOwnConfig();
                        EnsureBrowserInjection(true);
                        SetStatus("favicon 已入库：" + fetched.Path, false);
                    }
                    else
                    {
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

        EnsureBrowserInjection(false);

        if (busy)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "处理中：" + busyLabel);
        else if (statusLine.Length > 0)
            ImGui.TextColored(statusIsError ? new Vector4(1f, 0.45f, 0.45f, 1f) : new Vector4(0.5f, 0.95f, 0.6f, 1f), statusLine);

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.Spacing();
        var newBuf = newInput;
        ImGui.SetNextItemWidth(340f);
        if (ImGui.InputTextWithHint("##newsrc", "输入网站网址或本地图片完整路径", ref newBuf, 1024))
            newInput = newBuf;
        ImGui.SameLine();
        if (ImGui.SmallButton("添加")) TryAddFromInput(newInput);

        for (var i = 0; i < config.BrowserImages.Count; i++)
        {
            var b = config.BrowserImages[i];
            var remove = false;

            ImGui.PushID("libimg_" + b.Id);

            ImGui.TextUnformatted($"ID {b.Id}");
            if (ImGui.IsItemHovered())
            {
                var tip = b.SourceUrl.Length > 0 ? "来源网址：" + b.SourceUrl : "来源文件：" + b.Path;
                if (!File.Exists(b.Path) && b.Path.Length > 0)
                    tip += "\n⚠ 文件缺失，浏览器里不会显示";
                ImGui.SetTooltip(tip);
            }
            ImGui.SameLine();

            var st = ImGui.GetStyle();
            var proxyW = ImGui.GetFrameHeight() + st.ItemInnerSpacing.X + ImGui.CalcTextSize("代理").X;
            var refreshW = ImGui.CalcTextSize("刷新").X + st.FramePadding.X * 2 + 2;
            var delW = ImGui.CalcTextSize("删除").X + st.FramePadding.X * 2 + 2;
            var blockW = proxyW + st.ItemSpacing.X + refreshW + st.ItemSpacing.X + delW;

            var maxNameW = ImGui.GetContentRegionAvail().X - blockW - st.ItemSpacing.X;
            string statusText;
            var statusColor = new Vector4();
            var useDisabled = false;
            if (b.SourceUrl.Length > 0 && b.Path.Length == 0)
            {
                statusText = "正在抓取…";
                statusColor = new Vector4(1f, 0.8f, 0.4f, 1f);
            }
            else if (File.Exists(b.Path))
            {
                statusText = b.Name.Length > 0 ? b.Name : Path.GetFileName(b.Path);
                useDisabled = true;
            }
            else if (b.Path.Length > 0)
            {
                statusText = "文件缺失";
                statusColor = new Vector4(1f, 0.45f, 0.45f, 1f);
            }
            else
            {
                statusText = "";
            }

            if (statusText.Length > 0)
            {
                statusText = TruncateForWidth(statusText, maxNameW);
                if (useDisabled) ImGui.TextDisabled(statusText);
                else ImGui.TextColored(statusColor, statusText);
                ImGui.SameLine();
            }

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - blockW);
            var useProxy = b.UseProxy;
            if (ImGui.Checkbox("代理##useproxy", ref useProxy))
            {
                b.UseProxy = useProxy;
                SaveOwnConfig();
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("刷新")) RefreshEntry(b);

            ImGui.SameLine();
            if (ImGui.SmallButton("删除")) remove = true;
            ImGui.PopID();

            if (remove)
            {
                var goneId = b.Id;
                InvalidateIconCaches(goneId);
                config.BrowserImages.RemoveAt(i);
                SaveOwnConfig();
                SetStatus("已删除该图标（编号 " + goneId + " 不会再被新图占用）", false);
                EnsureBrowserInjection(true);
                changed = true;
                i--;
            }
        }

        if (config.BrowserImages.Count == 0)
            ImGui.TextDisabled("图标库为空");

        ImGui.Spacing();
        if (ImGui.Button("打开图标浏览器##openbrowser"))
        {
            try
            {
                EnsureBrowserInjection(true);
                OpenIconBrowser(id => SetStatus(id == 0u ? "未选择图标" : $"已选择图标（编号 {id}）", false));
                SetStatus("已打开图标浏览器：请停在【第一页】，图标在最前面（左上角）", false);
            }
            catch (Exception e) { SetStatus("打开浏览器失败：" + e.Message, true); }
        }

        ImGui.SameLine();
        if (ImGui.Button("重新载入##reloadall"))
            ApplyEverything(false);

        DrawIconReplaceSection(ref changed);

        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("代理");
        ImGui.SameLine();
        var proxyBuf = config.ProxyUrl;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##proxyurl", ref proxyBuf, 256))
        {
            config.ProxyUrl = proxyBuf;
            changed = true;
        }

        return changed;
    }

    private void SetStatus(string msg, bool isErr)
    {
        statusLine = msg;
        statusIsError = isErr;
    }

    private uint AllocId()
    {
        var id = AllocIdInternal();
        if (id == 0) SetStatus("图标库已满（" + BrowserIdCapacity + " 张，编号不会回收）", true);
        return id;
    }

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
                if (!InReservedRange(b.Id) || !used.Add(b.Id)) b.Id = 0;
            }

            uint next = BrowserIdBase;
            foreach (var b in config.BrowserImages)
                if (b != null && b.Id >= next) next = b.Id + 1;
            if (config.NextIconId < next) config.NextIconId = next;

            foreach (var b in config.BrowserImages)
            {
                if (b == null || b.Id != 0) continue;
                b.Id = AllocIdInternal();
                if (b.Id == 0) break;
            }
        }
        catch {   }
    }

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
            try { host = new Uri(norm).Host; } catch {   }
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
        InvalidateIconCaches(b.Id);
        EnsureBrowserInjection(true);
        SetStatus("已重新注入：" + t, false);
    }

    private void EnsureBrowserInjection(bool force)
    {
        try
        {
            if (config.BrowserImages.Count == 0)
            {
                browserState = "";
                return;
            }

            if (force)
            {
                detourWrapCache.Clear();
                detourSharedKeep.Clear();
                browserSharedKeep.Clear();
            }

            var now = Environment.TickCount64;
            if (!force && now - lastBrowserEnsureTick < 2000) return;
            lastBrowserEnsureTick = now;

            var t = FindLoadedType("IconBrowser");
            if (t == null) { browserState = "未找到 IconBrowser 类型"; return; }

            var ids = config.BrowserImages
                .Where(b => b.Id >= BrowserIdBase && b.Id < BrowserIdBase + BrowserIdCapacity && File.Exists(b.Path))
                .Select(b => (int)b.Id).Distinct().ToList();
            if (ids.Count == 0) { browserState = "图标库没有有效条目（检查文件是否存在）"; return; }

            foreach (var b in config.BrowserImages)
            {
                if (b.Id < BrowserIdBase || b.Id >= BrowserIdBase + BrowserIdCapacity) continue;
                if (!File.Exists(b.Path)) continue;
                try { GetDetourWrap(b.Id); } catch {   }
            }

            var dictField = t.GetField("gameIconTextures", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var cacheField = t.GetField("tabCaches", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var isOpenField = t.GetField("<IsOpen>k__BackingField",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var instList = GetBrowserInstances().ToList();

            if (texProviderCache == null || getFromFileCache == null)
            {
                EnsureTexProvider();
            }
            var texProv = texProviderCache;
            var getFromFile = getFromFileCache;

            var favTabIndex = FindFavoriteTabIndex(t);
            if (favTabIndex < 0) favTabIndex = 0;

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

                            object? shared = null;
                            if (browserSharedKeep.TryGetValue(b.Id, out var kept) && SharedHandleAlive(kept))
                                shared = kept;
                            else
                                browserSharedKeep.Remove(b.Id);

                            if (shared == null)
                            {
                                try { shared = getFromFile.Invoke(texProv, new object[] { b.Path }); }
                                catch { shared = null; }
                                if (shared != null) browserSharedKeep[b.Id] = shared;
                            }

                            if (shared != null && SharedHandleAlive(shared)) { texDict[(int)b.Id] = shared; dictHit++; }
                            else { texDict.Remove((int)b.Id); browserSharedKeep.Remove(b.Id); }
                        }

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

            foreach (var inst in instList) InjectSearchNames(inst);

            var selfBrowser = GetFieldValue(this, "<HostIconBrowser>k__BackingField");
            var anyOpen = false;
            if (isOpenField != null)
            {
                foreach (var inst in instList)
                {
                    try { if (isOpenField.GetValue(inst) is bool ob && ob) { anyOpen = true; break; } }
                    catch {   }
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
        catch {   }
        return -1;
    }

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
                catch {   }
            }
            detourWrapCache.Clear();
            detourSharedKeep.Clear();
            browserSharedKeep.Clear();
            Log("浏览器反注入完成：清除引用 " + removed + " 处");
        }
        catch (Exception ex)
        {
            Log("UninstallBrowserInjection 异常: " + ex.Message, true);
        }
    }

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
        catch {   }
        return list;
    }

    private static void TryAddBrowserInstance(object module, List<object> list)
    {
        try
        {
            var v = GetFieldValue(module, "<HostIconBrowser>k__BackingField");
            if (v != null && !list.Any(x => ReferenceEquals(x, v))) list.Add(v);
        }
        catch {   }
    }

    private void EnsureFrameworkTick()
    {
        if (frameworkTickDelegate != null) return;
        try
        {
            object? fw = DalamudServices.Framework;
            if (fw == null)
            {
                var fwType = FindAnyLoadedType("IFramework");
                if (fwType == null) { Log("未找到 IFramework：保活仅在面板打开时进行"); return; }
                fw = GetDalamudService(fwType);
            }
            if (fw == null) { Log("IFramework 服务取不到：保活仅在面板打开时进行"); return; }

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
            catch {   }
            finally { frameworkTickDelegate = null; }
    }

    private void OnFrameworkTick(IFramework framework)
    {
        try
        {
            if (!moduleActive) return;

            TryStartupApply();

            if (detourPatched.Count < DetourTargetCount
                && Environment.TickCount64 - lastDetourRetryTick > 3000)
                InstallDetours();

            if (macroIconLoadHook == null
                && Environment.TickCount64 - lastDetourRetryTick > 3000)
                InstallMacroIconHook();

            if (macroTexHook == null
                && Environment.TickCount64 - lastDetourRetryTick > 3000)
                InstallMacroTextureHook();

            if (macroExecHook == null
                && Environment.TickCount64 - lastDetourRetryTick > 3000)
                InstallMacroExecHook();

            EnsureBrowserInjection(false);
            TickMacroIconSwap();
            PollMacroCommandLine();
            TickMacroIconFast();
            TickPanelIconSync();
            TickMacroTextSweep();
        }
        catch {   }
    }

    private void TryStartupApply()
    {
        if (startupApplied) return;
        var now = Environment.TickCount64;
        if (now - lastStartupApplyTick < 2000) return;
        lastStartupApplyTick = now;
        if (detourPatched.Count == 0) return;
        startupApplied = true;
        ApplyEverything(true);
    }

    private void ApplyEverything(bool auto)
    {
        try
        {
            LoadOwnConfig();
            RebuildIconRuleMap();
            InstallDetours();
            InstallMacroIconHook();
            InstallMacroTextureHook();
            InstallMacroExecHook();
            EnsureFrameworkTick();
            detourWrapCache.Clear();
            detourSharedKeep.Clear();
            browserSharedKeep.Clear();
            EnsureBrowserInjection(true);
            foreach (var b in config.BrowserImages)
                if (InReservedRange(b.Id)) EnsureMacroKernel(b.Id);
            SetStatus(auto
                ? $"已自动载入图库（{config.BrowserImages.Count} 张）"
                : $"已重新载入（图库 {config.BrowserImages.Count} 张）", false);
            Log("全量应用完成（" + (auto ? "启动自动" : "手动重载") + "）：图库 "
                + config.BrowserImages.Count + " 张 · detour " + detourPatched.Count + "/" + DetourTargetCount);
        }
        catch (Exception e)
        {
            SetStatus("重新载入失败：" + e.Message, true);
            Log("全量应用失败: " + e, true);
        }
    }

    private static bool InReservedRange(uint id)
        => id >= BrowserIdBase && id < BrowserIdBase + BrowserIdCapacity;

    private static Type? EnsureHarmonyType()
    {
        if (harmonyTypeCache != null) return harmonyTypeCache;

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
        catch {   }

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

    private static IEnumerable<System.Reflection.Assembly> SafeAssemblies()
    {
        System.Reflection.Assembly[] arr;
        try { arr = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return new System.Reflection.Assembly[0]; }
        return arr;
    }

    private static IEnumerable<string> HarmonySearchDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        try
        {
            var d = Path.GetDirectoryName(typeof(ImGui).Assembly.Location);
            if (!string.IsNullOrEmpty(d) && seen.Add(d!)) list.Add(d!);
        }
        catch {   }

        try
        {
            foreach (var a in SafeAssemblies())
            {
                string? loc = null;
                try { loc = a.Location; } catch {   }
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
        catch {   }

        try
        {
            var d = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(d) && seen.Add(d!)) list.Add(d!);
        }
        catch {   }

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
        catch {   }

        return list;
    }

    private static Type? FindImageHelperType()
    {
        foreach (var asm in SafeAssemblies())
        {
            try
            {
                var t = asm.GetType("OmenTools.OmenService.ImageHelper", false);
                if (t != null) return t;
            }
            catch {   }
        }
        return FindAnyLoadedType("ImageHelper");
    }

    private void InstallDetours()
    {
        selfRef = this;
        if (detourPatched.Count >= DetourTargetCount) return;

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

            var mPath = ibType.GetMethod("GetBuiltInIconPath", flags, null, new[] { typeof(uint) }, null);
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

            var fwObj = GetHostService("Framework");
            EnsureTexProvider();
            Log("贴图源探测：" + texProviderState + " · 宿主访问器 Framework=" + (fwObj == null ? "空" : "就绪"));
        }
        catch (Exception ex)
        {
            SetDetourState("detour 安装失败: " + ex.GetType().Name + " " + ex.Message + " || " + ex, true);
        }
    }

    private static void PatchOnce(Type hType, Type hmType, object harmony, string key,
                                 MethodBase? original, string prefixName)
    {
        if (original == null || detourPatched.Contains(key)) return;
        if (PatchWith(hType, hmType, harmony, original, prefixName)) detourPatched.Add(key);
    }

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
            detourPatched.Clear();
            detourLastLogged = "detour 未挂";
            detourWrapCache.Clear();
            detourSharedKeep.Clear();
            browserSharedKeep.Clear();
            detourState = "detour 未挂";
        }
    }

    private static bool PrefixGetBuiltInIconPath(uint __0, ref string __result)
    {
        if (!InReservedRange(__0)) return true;
        var path = FindPathForId(__0);
        if (path == null) return true;
        __result = path;
        return false;
    }

    private static bool PrefixIconExists(uint __0, ref bool __result)
    {
        if (!InReservedRange(__0)) return true;
        __result = FindPathForId(__0) != null;
        return false;
    }

    private static bool PrefixGetIconTexture(uint __0, ref IDalamudTextureWrap __result)
    {
        if (!InReservedRange(__0)) return true;
        var wrap = GetDetourWrap(__0) as IDalamudTextureWrap;
        if (wrap == null) return true;
        __result = wrap;
        return false;
    }

    private static bool PrefixGetGameIcon(uint __0, bool __1, ref IDalamudTextureWrap __result)
    {
        if (!InReservedRange(__0)) return true;
        var wrap = GetDetourWrap(__0) as IDalamudTextureWrap;
        if (wrap == null) return true;
        __result = wrap;
        return false;
    }

    private static bool PrefixTryGetGameIcon(uint __0, ref IDalamudTextureWrap __1, bool __2, ref bool __result)
    {
        if (!InReservedRange(__0)) return true;
        var wrap = GetDetourWrap(__0) as IDalamudTextureWrap;
        if (wrap == null) return true;
        __1 = wrap;
        __result = true;
        return false;
    }

    private static bool IsWrapAlive(object? wrap)
    {
        if (wrap == null) return false;
        try
        {
            if (wrap.GetType().Name == "UnknownTextureWrap") return false;
            var prop = wrap.GetType().GetProperty("Handle");
            if (prop == null) return true;
            return prop.GetValue(wrap) != null;
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
            return true;
        }
    }

    private static object? GetDetourWrap(uint id)
    {
        if (detourWrapCache.TryGetValue(id, out var cached))
        {
            if (IsWrapAlive(cached)) return cached;
            detourWrapCache.Remove(id);
            detourSharedKeep.Remove(id);
        }
        object? wrap = null;
        try
        {
            var path = FindPathForId(id);
            if (path != null && EnsureTexProvider() && getFromFileCache != null && texProviderCache != null)
            {
                var shared = getFromFileCache.Invoke(texProviderCache, new object[] { path });
                if (shared != null)
                {
                    detourSharedKeep[id] = shared;
                    wrap = shared.GetType().GetMethod("GetWrapOrDefault")?.Invoke(shared, new object?[] { null });
                }
            }
        }
        catch (Exception ex)
        {
            Log("加载贴图失败(ID " + id + "): " + ex.Message, true);
        }
        if (IsWrapAlive(wrap)) detourWrapCache[id] = wrap;
        else
        {
            detourWrapCache.Remove(id);
            detourSharedKeep.Remove(id);
        }
        return wrap;
    }

    private static bool SharedHandleAlive(object? shared)
    {
        if (shared == null) return false;
        try
        {
            var w = shared.GetType().GetMethod("GetWrapOrDefault")?.Invoke(shared, new object?[] { null });
            return IsWrapAlive(w);
        }
        catch { return false; }
    }

    private static bool EnsureTexProvider()
    {
        if (texProviderCache != null && getFromFileCache != null) return true;

        if (texProviderCache == null)
        {
            texProviderCache = GetHostService("TextureProvider");
            if (texProviderCache != null) texProviderState = "宿主访问器";
        }

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
        catch {   }
    }

    private object? FindTreeHouseManager()
    {
        if (managerCache != null) return managerCache;
        try
        {
            var now = Environment.TickCount64;
            if (!deepProbe && now - lastManagerAttemptTick < 5000) return null;
            lastManagerAttemptTick = now;

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

            found = FindManagerInStatics();
            if (found != null)
            {
                Log($"管理器已找到（来源：静态字段扫描）：{found.GetType().FullName}");
                managerCache = found;
                return found;
            }

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

            var deepState = "未尝试";
            managerAttempts++;
            var runDeep = deepProbe;
            if (!runDeep && !autoDeepProbeDone && managerAttempts >= 3)
            {
                runDeep = true;
                autoDeepProbeDone = true;
                Log("启动后自动深度探测一次（以后不再自动跑）");
            }
            if (runDeep)
            {
                deepProbe = false;
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

    private static bool SkipNamespace(string ns)
    {
        return ns.StartsWith("System", StringComparison.Ordinal)
            || ns.StartsWith("Newtonsoft", StringComparison.Ordinal)
            || ns.StartsWith("Microsoft", StringComparison.Ordinal)
            || ns.StartsWith("ImGui", StringComparison.Ordinal)
            || ns.StartsWith("Lumina", StringComparison.Ordinal)
            || ns.StartsWith("SixLabors", StringComparison.Ordinal);
    }

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
        if (t.Name.IndexOf("TreeHouseManager", StringComparison.OrdinalIgnoreCase) >= 0) return true;

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

    private static bool LooksRelevant(string name)
    {
        return name.IndexOf("TreeHouse", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Router", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Tab", StringComparison.OrdinalIgnoreCase) >= 0;
    }

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
        catch {   }
        return null;
    }

    private static Type? FindTypeByFullName(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            catch {   }
        }
        return null;
    }

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
        catch {   }
        return null;
    }

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
        catch {   }
        return null;
    }

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
        catch {   }
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
        catch {   }
        return null;
    }

    private static object? GetDalamudService(Type serviceType)
    {
        try
        {
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
        var useProxy = false;
        if (uint.TryParse(key, out var fetchId))
        {
            var entry = config.BrowserImages.FirstOrDefault(x => x.Id == fetchId);
            useProxy = entry != null && entry.UseProxy;
        }

        var http = Http;
        if (useProxy && !string.IsNullOrWhiteSpace(config.ProxyUrl))
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

    private async Task<(string path, string srcUrl)?> FetchFaviconAsync(HttpClient http, string input)
    {
        var url = NormalizeUrl(input);
        if (url == null) throw new Exception("网址格式不正确");

        var baseUri = new Uri(url);
        var origin = baseUri.GetLeftPart(UriPartial.Authority);
        var candidates = new List<string> { origin + "/favicon.ico" };

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
                    catch {   }
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
        if (t.IndexOf(':') >= 0 || t.IndexOf('\\') >= 0) return false;
        if (File.Exists(t)) return false;
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
                if (relLower.Contains("mask-icon")) score = 1;

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
        catch {   }

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
        catch {   }
        return null;
    }

    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
    private static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8;
    private static bool IsGif(byte[] b) => b.Length > 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46;
    private static bool IsWebp(byte[] b) => b.Length > 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50;
    private static bool IsBmp(byte[] b) => b.Length > 2 && b[0] == 0x42 && b[1] == 0x4D;

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

            if (IsPng(slice))
            {
                png = slice;
                return true;
            }

            if (TryDecodeDib(slice, true, out var w2, out var h2, out var rgba))
            {
                png = EncodePngRgba(w2, h2, rgba);
                return true;
            }
        }
        catch {   }
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

    private static byte[] EncodePngRgba(int width, int height, byte[] rgba)
    {
        using var outMs = new MemoryStream();
        outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

        var ihdr = new byte[13];
        WriteBe32(ihdr, 0, width);
        WriteBe32(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WritePngChunk(outMs, "IHDR", ihdr);

        var stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0;
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
        catch {   }
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
        catch {   }
    }

    private void WriteDiagnostics()
    {
        try
        {
            Log("========== 诊断开始 ==========");
            Log($"图标库条目={config.BrowserImages.Count}  勾选代理的条目={config.BrowserImages.Count(x => x.UseProxy)}  代理地址={config.ProxyUrl}");
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
            catch {   }

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
                AllowedInMacros = false,
            };

            var okMain = false;
            var okAlias = false;
            try { okMain = cm.AddHandler(MacroIconCommand, info); } catch {   }
            try { okAlias = cm.AddHandler(MacroIconCommandAlias, info); } catch {   }

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
                try { cm.RemoveHandler(MacroIconCommand); } catch {   }
                try { cm.RemoveHandler(MacroIconCommandAlias); } catch {   }
            }
        }
        catch {   }
        finally
        {
            macroIconCommandRegistered = false;
            macroIconCommandState = "已注销";
        }
    }

    private static void Chat(string msg)
    {
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
        catch {   }
    }

    private static void OnMacroIconCommand(string command, string args)
    {
        if (IsMacroRunning())
        {
            macroRunGuardHits++;
            Log("宏正在执行：标记指令「" + command + " " + (args ?? "") + "」按设计静默让行（不应用、不提示）");
            return;
        }

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
            Chat("已把" + (set == 0 ? "个人宏 " : "共享宏 ") + (index + 1)
                 + " 的图标设为「" + label + "」（编号 " + iconId + "）。"
                 + (IsMacroPanelOpen() ? "宏面板与热键栏已刷新。" : "热键栏已刷新；宏面板打开后即为新图标。"));
        }
        else
        {
            Chat("设置失败：读不到宏数据（宏模块还没就绪）。");
        }
    }

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
                            linePtr = linePtr + 1;
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
                            catch {   }
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

    private static unsafe int CurrentMacroLineIndex()
    {
        try
        {
            var shell = RaptureShellModule.Instance();
            return shell == null ? 0 : shell->MacroCurrentLine;
        }
        catch { return 0; }
    }

    private static string NormalizeMacroLine(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == '\uFF0F') sb.Append('/');
            else if (ch == '\u3000') sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

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
        if (line.Length > name.Length && line[name.Length] != ' ') return false;
        rest = line.Substring(name.Length).Trim();
        return true;
    }

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

    private static unsafe void PollMacroCommandLine()
    {
        try
        {
            var shell = RaptureShellModule.Instance();
            if (shell == null) { macroShellState = "空"; return; }
            macroShellState = "就绪";

            var index = shell->MacroCurrentLine;
            var raw = shell->MacroLineText.ToString() ?? "";

            if (!macroLinePrimed)
            {
                macroLinePrimed = true;
                macroLineSeenIndex = index;
                macroLineTextSeen = raw;
                return;
            }

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

            if (hit || macroLineLogged < 40)
            {
                macroLineLogged++;
                Log("宏行观察：行号=" + index + "「" + MacroLineForLog(raw, 80) + "」"
                    + (hit ? " → 命中本模块指令，参数=" + args + "；写入目标 → " + MacroDiagText() : ""));
            }

        }
        catch {   }
    }

    private void TickMacroTextSweep()
    {
        var now = Environment.TickCount64;
        if (now - lastMacroTextSweepTick < 2000) return;
        lastMacroTextSweepTick = now;
        try { SweepMacroIconCommands(); }
        catch (Exception e) { Log("宏文本扫描异常: " + e.Message, true); }
    }

    private unsafe void SweepMacroIconCommands()
    {
        var module = RaptureMacroModule.Instance();
        if (module == null) return;
        if (IsMacroRunning()) return;

        var foundThisRound = 0;
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

                if (!TryResolveMacroIconCommand(macro, set, i, out var targetSet, out var targetIndex,
                                                out var iconId, out var rawArgs))
                    continue;
                foundThisRound++;

                foundSlots[(int)targetSet * 100 + (int)targetIndex] = iconId;

                var target = module->GetMacro(targetSet, targetIndex);
                if (target == null) continue;
                if (target->IconId == iconId) continue;

                target->SetIcon(iconId);
                try { module->SetSavePendingFlag(true, targetSet); } catch {   }
                macroTextWrites++;
                macroTextLastDiag = (targetSet == 1 ? "共享" : "个人") + (targetIndex + 1) + " → " + iconId;
                Log("宏文本扫描：命中指令行，已写宏图标 " + macroTextLastDiag + "（参数=" + MacroLineForLog(rawArgs, 50) + "）");

                RefreshMacroIconUsers(targetSet, targetIndex, iconId);
            }
        }

        macroTextCmdSeen = foundThisRound;

        macroIconSlots.Clear();
        foreach (var kv in foundSlots) macroIconSlots[kv.Key] = kv.Value;
    }

    private static unsafe bool TryResolveMacroIconCommand(
        RaptureMacroModule.Macro* macro, uint ownSet, uint ownIndex,
        out uint targetSet, out uint targetIndex, out uint iconId, out string rawArgs)
    {
        targetSet = ownSet;
        targetIndex = ownIndex;
        iconId = 0;
        rawArgs = "";
        if (macro == null) return false;

        string? cmdArgs = null;
        try
        {
            var linePtr = &macro->Name;
            for (var ln = 0; ln < 15; ln++)
            {
                linePtr = linePtr + 1;
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

        uint resolved;
        try { resolved = ResolveLibraryIcon(iconText, out _); }
        catch { return false; }
        if (resolved == 0) return false;

        iconId = resolved;
        rawArgs = cmdArgs;
        return true;
    }

    private unsafe void TickMacroIconFast()
    {
        var now = Environment.TickCount64;
        if (now - lastFastMacroCheckTick < 150) return;
        lastFastMacroCheckTick = now;
        try
        {
            if (IsMacroRunning()) return;
            if (!TryGetSelectedMacro(out var set, out var index)) return;
            var module = RaptureMacroModule.Instance();
            if (module == null) return;
            var macro = module->GetMacro(set, index);
            if (macro == null) return;
            if (!TryResolveMacroIconCommand(macro, set, index, out var targetSet, out var targetIndex,
                                            out var iconId, out _))
                return;
            if (targetSet != set || targetIndex != index) return;
            if (macro->IconId == iconId) return;
            if (ApplyMacroIcon(set, index, iconId))
            {
                fastMacroHits++;
                Log("宏图标快通道：选中宏 " + (set == 1 ? "共享" : "个人") + (index + 1) + " 图标落位 → " + iconId);
            }
        }
        catch {   }
    }

    private static unsafe void TickPanelIconSync()
    {
        if (IsMacroRunning()) return;
        var now = Environment.TickCount64;
        if (now - lastPanelSyncTick < 250) return;
        lastPanelSyncTick = now;
        try { SyncMacroPanelIcons(); }
        catch {   }
        try { TickMacroHotbarSafety(); }
        catch {   }
    }

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

    private static unsafe void RefreshMacroIconUsers(uint set, uint index, uint iconId)
    {
        var needKernel = InReservedRange(iconId);
        if (needKernel) { try { EnsureMacroKernel(iconId); } catch {   } }
        var ready = !needKernel || macroKernels.ContainsKey(iconId) || macroKernelFailed.ContainsKey(iconId);

        if (ready)
        {
            try
            {
                var hb = RaptureHotbarModule.Instance();
                if (hb != null)
                {
                    hb->ReloadMacroSlots((byte)set, (byte)index);
                    hb->ReloadAllMacroSlots();
                    macroUiRefreshes++;
                    macroHotbarReloads++;
                }
            }
            catch {   }
        }

        if (!TryGetMacroAddon(set, out var addon)) return;

        if (ready)
        {
            if (ApplyIconToMacroPanelSlot(addon, set, index, iconId))
                macroPanelApplied[(int)set * 100 + (int)index] = iconId;
        }

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
        catch {   }
    }

    private static unsafe bool TryGetMacroAddon(uint set, out AddonMacro* addon)
    {
        addon = null;
        try
        {
            if (set > 1) return false;
            var agent = AgentMacro.Instance();
            if (agent == null) return false;
            if (agent->SelectedMacroSet != set) return false;

            var stage = AtkStage.Instance();
            if (stage == null) return false;
            var unitManager = stage->RaptureAtkUnitManager;
            if (unitManager == null) return false;
            addon = (AddonMacro*)unitManager->GetAddonByName("Macro", 1);
            return addon != null;
        }
        catch { return false; }
    }

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

            var created = addon->MacroCreated;
            if (index < (uint)created.Length) created[(int)index] = notEmpty;

            var icons = addon->MacroSetIcon;
            if (index < (uint)icons.Length)
                icons[(int)index] = (!notEmpty) ? 0 : (int)iconId;

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
            catch {   }

            comp->VisibilityFlags = (DragDropVisibilityFlag)(((int)comp->VisibilityFlags) & 253);
            comp->LoadIcon(iconId);
            try { comp->SetQuantityText(string.Empty); } catch {   }
            comp->SetIconEnabled(false);

            var inner = comp->AtkComponentIcon;
            if (inner != null)
            {
                inner->Flags = (IconComponentFlags)(((uint)inner->Flags) & 0xFFFF7FFFu);
                inner->LoadIcon(iconId);
                inner->SetIsMacro(true);
                inner->SetIconImageDisableState(false);
                inner->UpdateIndicator();
            }

            MarkMacroDragDropDirty(comp);

            macroPanelFixes++;
            return true;
        }
        catch { return false;   }
    }

    private static unsafe void MarkMacroDragDropDirty(AtkComponentDragDrop* comp)
    {
        if (comp == null) return;
        try
        {
            var owner = comp->OwnerNode;
            if (owner != null) owner->AtkResNode.DrawFlags |= 1u;
        }
        catch {   }

        try
        {
            var inner = comp->AtkComponentIcon;
            if (inner == null) return;
            var io = inner->OwnerNode;
            if (io != null) io->AtkResNode.DrawFlags |= 1u;
            if (inner->FrameContainer != null) inner->FrameContainer->DrawFlags |= 1u;
            if (inner->ComboBorder != null) inner->ComboBorder->DrawFlags |= 1u;
            if (inner->Frame != null) inner->Frame->DrawFlags |= 1u;
            if (inner->OuterResNode != null) inner->OuterResNode->DrawFlags |= 1u;
        }
        catch {   }
    }

    private static unsafe void SyncMacroPanelIcons()
    {
        if (macroIconSlots.Count == 0) return;
        var agent = AgentMacro.Instance();
        if (agent == null) return;
        var set = agent->SelectedMacroSet;
        if (set > 1) return;
        if (!TryGetMacroAddon(set, out var addon)) return;
        if (!addon->AtkUnitBase.IsVisible) { macroPanelWasVisible = false; return; }
        var module = RaptureMacroModule.Instance();
        if (module == null) return;

        var page = addon->SelectedPage;
        var firstComp = (nint)0;
        try
        {
            var cs = addon->DragDropComponent;
            if (cs.Length > 0 && cs[0].Value != null) firstComp = (nint)cs[0].Value;
        }
        catch {   }

        if (!macroPanelWasVisible || set != macroPanelSetSeen
            || page != macroPanelPageSeen || firstComp != macroPanelCompSeen)
        {
            macroPanelApplied.Clear();
            macroPanelWasVisible = true;
            macroPanelSetSeen = set;
            macroPanelPageSeen = page;
            macroPanelCompSeen = firstComp;
        }

        macroPanelScans++;
        var tableBase = (int)set * 100;

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
            if (offset < 0 || offset >= 100) continue;
            var index = (uint)offset;

            var desired = kv.Value;
            try
            {
                var macro = module->GetMacro(set, index);
                if (macro != null && macro->IconId != 0) desired = (uint)macro->IconId;
            }
            catch {   }
            if (desired == 0) continue;

            if (macroPanelApplied.TryGetValue(kv.Key, out var applied) && applied == desired) continue;

            if (InReservedRange(desired))
            {
                EnsureMacroKernel(desired);
                if (!macroKernels.ContainsKey(desired) && !macroKernelFailed.ContainsKey(desired))
                    continue;
            }

            if (ApplyIconToMacroPanelSlot(addon, set, index, desired))
            {
                macroPanelApplied[kv.Key] = desired;
                ReloadHotbarFor(set, index);
            }
        }
    }

    private static unsafe void ReloadHotbarFor(uint set, uint index)
    {
        try
        {
            var hb = RaptureHotbarModule.Instance();
            if (hb == null) return;
            hb->ReloadMacroSlots((byte)set, (byte)index);
            macroHotbarReloads++;
        }
        catch {   }
    }

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
        catch {   }
    }

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

    private static unsafe string MacroDiagText()
    {
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
            try { module->SetSavePendingFlag(true, set); } catch {   }

            macroIconSlots[(int)set * 100 + (int)index] = iconId;

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

    private void TickMacroIconSwap()
    {
        if (!config.MacroIconSwap) return;
        if (config.BrowserImages.Count == 0 && iconRuleMap.Count == 0) return;
        var now = Environment.TickCount64;
        if (now - lastMacroSwapTick < MacroIconSwapIntervalMs) return;
        lastMacroSwapTick = now;
        try { SweepMacroIcons(); }
        catch (Exception e) { Log("宏图标扫描异常: " + e.Message, true); }
    }

    private const string HotbarAddonNamePrefix = "_Action";

    private const int AubNameOff = 0x08;
    private const int AubNameLen = 32;

    private static unsafe bool UnitNameHasPrefix(AtkUnitBase* unit, string prefix)
    {
        var p = (byte*)unit + AubNameOff;
        var n = prefix.Length;
        if (n > AubNameLen) n = AubNameLen;
        for (var i = 0; i < n; i++)
            if (p[i] != (byte)prefix[i]) return false;
        return true;
    }

    private static unsafe string UnitNameText(AtkUnitBase* unit)
    {
        var p = (byte*)unit + AubNameOff;
        var len = 0;
        while (len < AubNameLen && p[len] != 0) len++;
        var chars = new char[len];
        for (var i = 0; i < len; i++) chars[i] = (char)p[i];
        return new string(chars);
    }

    private static bool IsHotbarCandidate(string nm)
    {
        if (string.IsNullOrEmpty(nm)) return false;
        if (!nm.StartsWith("_ActionBar", StringComparison.Ordinal)) return false;
        if (nm.StartsWith("_ActionBarEx", StringComparison.Ordinal)) return false;
        return true;
    }

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

    private static unsafe string SlotMaskOf(AtkComponentIcon* icon, uint libId)
    {
        if (icon == null) return "???";
        var kernel = macroKernels.TryGetValue(libId, out var k) ? k : IntPtr.Zero;
        var a = icon->IconImage != null ? SlotStateChar(GetImageNodeTexture(icon->IconImage), kernel) : '.';
        var b = icon->FrameIcon != null ? SlotStateChar(GetImageNodeTexture(icon->FrameIcon), kernel) : '.';
        var c = icon->Texture != null ? SlotStateChar(&icon->Texture->AtkTexture, kernel) : '.';
        return string.Concat(a, b, c);
    }

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

            if (sb.Length < 1000)
                sb.Append(' ').Append(addonName).Append(isMine ? "*" : "")
                  .Append(isVisible ? "(显 " : "(隐 ").Append(hbId).Append('/').Append(slotCount).Append(')');

            if (!isMine) continue;

            var vecFirst = *(byte**)(ab + AbeVectorOff);
            if (vecFirst == null || !IsUserPointer(vecFirst)) continue;

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
                catch {   }

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

            if (mismatches > matches)
            {
                sb.Append(' ').Append(addonName).Append("(栏").Append(hbId).Append(" 映射核对不通过 合")
                  .Append(matches).Append("/不合").Append(mismatches).Append(" 本轮不写)");
                continue;
            }

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

                var wrote = AttachKernelToIcon(icon, libId, libId);
                if (wrote > 0) fixes++;
                else if (!AnySlotIsOurKernel(icon, libId))
                {
                    if (compId == 0) blind++;
                    RebuildReservedIconSlot(icon, libId);
                }
                if (icon->IconId != libId) { try { icon->IconId = libId; } catch { } }
            }

            if (unitOwned > 0)
                sb.Append(" → 我方").Append(unitOwned)
                  .Append(" 核对合").Append(matches).Append("/不合").Append(mismatches);
        }

        macroHotbarOwnSlots = owned;
        macroHotbarFixes += fixes;
        macroHotbarBlindFixes += blind;
        if (sb.Length == 0)
            macroHotbarDiag = "_Action*共" + foundUnits + "个｜无（枚举到的都不是热键栏，或槽内容都不是我们的）";
        else
            sb.Insert(0, "_Action*共" + foundUnits + "个｜");
        if (sb.Length > 0) macroHotbarDiag = sb.ToString();
    }

    private unsafe void SweepMacroIcons()
    {
        if (!IsUiSafeToTouch()) { macroSweepGatedByMask++; return; }

        try { SweepHotbarMacroIcons(); }
        catch {   }

        if (macroRebuildBackoff.Count > 0)
        {
            var nowT = Environment.TickCount64;
            List<nint>? dead = null;
            foreach (var kv in macroRebuildBackoff)
                if (nowT >= kv.Value) (dead ??= new List<nint>()).Add(kv.Key);
            if (dead != null)
                foreach (var k in dead) macroRebuildBackoff.Remove(k);
        }

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

            if (!unit->IsVisible) { macroSweepNotVisible++; continue; }

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

        var nowMs = Environment.TickCount64;
        if (nowMs - lastMacroSweepLogTick >= 10000)
        {
            lastMacroSweepLogTick = nowMs;
            var diag = MacroDiagText();
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
                var hbSig = macroHotbarOwnSlots + "/" + macroHotbarFixes + "/" + macroHotbarBlindFixes + "/" + macroHotbarDiag;
                if (hbSig != lastMacroHotbarDiagSig)
                {
                    lastMacroHotbarDiagSig = hbSig;
                    Log("宏图标热键栏：我方槽=" + macroHotbarOwnSlots + " 补写=" + macroHotbarFixes
                        + "（其中空壳救回=" + macroHotbarBlindFixes + "）｜" + macroHotbarDiag);
                }
            }
        }

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

    private unsafe int WalkMacroIconNode(AtkResNode* node, int depth, long deadline)
    {
        if (node == null || !IsUserPointer(node)) return 0;
        if (depth > MacroIconWalkDepth) return 0;
        if (Environment.TickCount64 >= deadline) return 0;

        var hits = 0;
        try
        {
            if (node->Type == NodeType.Image)
                hits += SwapTextureIfOurs(GetImageNodeTexture((AtkImageNode*)node), 0);

            var raw = (uint)node->Type;
            if (raw >= 1000 && raw < 2000)
            {
                var comp = ((AtkComponentNode*)node)->Component;
                if (comp != null && ComponentAccessOk(comp, node))
                {
                    if (comp->GetComponentType() == ComponentType.Icon)
                    {
                        var icon = (AtkComponentIcon*)comp;
                        var id = icon->IconId;
                        macroSweepComps++;

                        if (icon->IconImage == null && icon->FrameIcon == null && icon->Texture == null)
                            macroEmptyCompSeen++;

                        var effId = InReservedRange(id) ? id : 0u;

                        if (effId != 0)
                        {
                            macroSweepReserved++;
                            var slotWrote = 0;
                            if (icon->IconImage != null)
                                slotWrote += SwapTextureIfOurs(GetImageNodeTexture(icon->IconImage), effId);
                            if (icon->FrameIcon != null)
                                slotWrote += SwapTextureIfOurs(GetImageNodeTexture(icon->FrameIcon), effId);
                            if (icon->Texture != null)
                                slotWrote += SwapTextureIfOurs(&icon->Texture->AtkTexture, effId);
                            hits += slotWrote;

                            if (slotWrote == 0 && !AnySlotIsOurKernel(icon, effId))
                                RebuildReservedIconSlot(icon, effId);
                        }
                        else
                        {
                            if (macroKernels.Count > 0) RevertIconSlotIfForeign(icon, id);

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

                    var nl2 = comp->UldManager.NodeList;
                    var c2 = comp->UldManager.NodeListCount;
                    if (nl2 != null && IsUserPointer(nl2) && c2 > 0 && c2 <= MacroIconNodeBudget)
                    {
                        for (var k = 0; k < c2; k++)
                        {
                            if (Environment.TickCount64 >= deadline) break;
                            var nn = nl2[k];
                            if (nn == null || !IsUserPointer(nn) || nn == node) continue;
                            hits += WalkMacroIconNode(nn, depth + 1, deadline);
                        }
                    }
                }
            }
        }
        catch {   }

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
        {
            if (Environment.TickCount64 >= deadline) break;
            if (!IsUserPointer(child)) break;
            hits += WalkMacroIconNode(child, depth + 1, deadline);
        }
        return hits;
    }

    private static unsafe bool ComponentAccessOk(AtkComponentBase* comp, AtkResNode* node)
    {
        if (comp == null) return false;

        var addr = (ulong)comp;
        if (addr <= 0x10000 || addr >= 0x00007FFFFFFF0000) return false;

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

    private static unsafe bool IsUserPointer(void* p)
    {
        var a = (ulong)p;
        return a > 0x10000 && a < 0x00007FFFFFFF0000;
    }

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
        if (parts == null || !IsUserPointer(parts)) return null;
        if (parts->Parts == null || !IsUserPointer(parts->Parts)) return null;
        if (parts->PartCount == 0 || parts->PartCount > 512) return null;

        var partId = image->PartId;
        if (partId >= parts->PartCount) partId = 0;

        var asset = parts->Parts[partId].UldAsset;
        if (asset == null || !IsUserPointer(asset)) return null;

        return &asset->AtkTexture;
    }

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
            EnsureMacroKernel(id);
            return 0;
        }

        if (texture->TextureType == TextureType.KernelTexture
            && (nint)texture->KernelTexture == kernel)
            return 0;

        IncRefKernel(kernel);
        texture->KernelTexture = (CsGameTexture*)kernel;
        texture->TextureType = TextureType.KernelTexture;
        return 1;
    }

    private static unsafe int SwapTextureToKernel(AtkTexture* texture, uint libId, uint fromId)
    {
        if (texture == null || !InReservedRange(libId)) return 0;

        var kernel = macroKernels.TryGetValue(libId, out var k) ? k : IntPtr.Zero;
        if (kernel == IntPtr.Zero)
        {
            EnsureMacroKernel(libId);
            return 0;
        }

        NoteIconSlot(texture, fromId, libId, kernel);

        if (texture->TextureType == TextureType.KernelTexture
            && (nint)texture->KernelTexture == kernel)
            return 0;

        IncRefKernel(kernel);
        texture->KernelTexture = (CsGameTexture*)kernel;
        texture->TextureType = TextureType.KernelTexture;
        return 1;
    }

    private static unsafe void IncRefKernel(IntPtr kernel)
    {
        if (kernel == IntPtr.Zero) return;
        try { ((CsGameTexture*)kernel)->IncRef(); } catch {   }
    }

    private static void InvalidateIconCaches(uint id)
    {
        if (!InReservedRange(id)) return;
        try
        {
            detourWrapCache.Remove(id);
            detourSharedKeep.Remove(id);
            browserSharedKeep.Remove(id);
            macroKernels.TryRemove(id, out _);
            macroKernelFailed.TryRemove(id, out _);
            macroKernelPending.TryRemove(id, out _);
        }
        catch {   }
    }

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

                try { unsafe { ((CsGameTexture*)kernel)->IncRef(); } } catch {   }

                macroKernelKeeps.Enqueue(fitted);
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
        catch {   }

        try
        {
            if (GetHostService("TextureProvider") is ITextureProvider typed) return typed;
        }
        catch {   }

        return null;
    }

    private unsafe delegate bool AtkComponentIconLoadIconDelegate(AtkComponentIcon* thisPtr, uint iconId);

    private unsafe delegate bool AtkTextureLoadIconTextureDelegate(AtkTexture* thisPtr, int iconId, IconSubFolder subFolder);

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

    private static IntPtr ResolveIconLoadAddress() =>
        ResolveFcsAddress(typeof(AtkComponentIcon), "LoadIcon");

    private static IntPtr ResolveAtkTextureLoadIconAddress() =>
        ResolveFcsAddress(typeof(AtkTexture), "LoadIconTexture");

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
        catch {   }

        try
        {
            if (GetDalamudService(typeof(IGameInteropProvider)) is IGameInteropProvider p2) return p2;
        }
        catch {   }

        return null;
    }

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
        try { macroTexHook?.Dispose(); } catch {   }
        macroTexHook = null;
        macroTexHookState = "未挂";
    }

    private static void UninstallMacroIconHook()
    {
        try { macroIconLoadHook?.Dispose(); } catch {   }
        macroIconLoadHook = null;
        macroIconHookState = "未挂";
    }

    private unsafe delegate void ExecuteMacroDelegate(RaptureShellModule* shell, RaptureMacroModule.Macro* macro);

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
        try { macroExecHook?.Dispose(); } catch {   }
        macroExecHook = null;
        macroExecHookState = "未挂";
    }

    private static unsafe void DetourExecuteMacro(RaptureShellModule* shell, RaptureMacroModule.Macro* macro)
    {
        var hook = macroExecHook;
        try { hook?.Original(shell, macro); } catch {   }

        try
        {
            if (macro == null) return;
            macroExecSeen++;

            var name = "";
            try { name = macro->Name.ToString() ?? ""; } catch {   }

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
                break;
            }

            if (macroExecHits == 0) macroExecLastDiag = "宏「" + MacroLineForLog(name, 16) + "」无命中";
        }
        catch {   }
    }

    private static unsafe bool LoadPlaceholderIcon(Hook<AtkTextureLoadIconTextureDelegate> hook,
                                                   AtkTexture* tex, IconSubFolder subFolder)
    {
        for (var i = 0; i < MacroIconPlaceholders.Length; i++)
        {
            try
            {
                if (hook.Original(tex, (int)MacroIconPlaceholders[i], subFolder)) return true;
            }
            catch {   }
        }
        return false;
    }

    private static unsafe bool DetourAtkTextureLoadIcon(AtkTexture* thisPtr, int iconId, IconSubFolder subFolder)
    {
        var hook = macroTexHook;
        if (hook == null) return false;
        if (thisPtr == null) return false;

        if (!InReservedRange((uint)iconId))
            return hook.Original(thisPtr, iconId, subFolder);

        macroTexHookCalls++;

        try
        {
            var libId = (uint)iconId;
            if (!macroKernels.TryGetValue(libId, out var kernel) || kernel == IntPtr.Zero)
            {
                EnsureMacroKernel(libId);
                return LoadPlaceholderIcon(hook, thisPtr, subFolder);
            }

            if (thisPtr->TextureType == TextureType.KernelTexture
                && (nint)thisPtr->KernelTexture == kernel)
                return true;

            if (thisPtr->TextureType == TextureType.KernelTexture)
            {
                thisPtr->KernelTexture = (CsGameTexture*)kernel;
                macroTexHookHits++;
                return true;
            }

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

    private static unsafe bool DetourIconLoadIcon(AtkComponentIcon* thisPtr, uint iconId)
    {
        macroIconHookCalls++;

        var hook = macroIconLoadHook;
        if (hook == null) return false;

        var srcIsLib = InReservedRange(iconId);
        if (srcIsLib) macroIconHookReserved++;

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
                    ok = hook.Original(thisPtr, iconId);
                    if (AttachKernelToIcon(thisPtr, ruleTo, iconId) > 0) iconRuleSwaps++;
                }
                else
                {
                    ok = hook.Original(thisPtr, iconId);
                    if (ok && OverlayIconTexture(thisPtr, iconId, ruleTo) > 0) iconRuleSwaps++;
                }
            }
            else if (srcIsLib)
            {
                ok = hook.Original(thisPtr, iconId);
                for (var i = 0; i < MacroIconPlaceholders.Length && !ok; i++)
                    ok = hook.Original(thisPtr, MacroIconPlaceholders[i]);
                if (AttachKernelToIcon(thisPtr, iconId, iconId) == 0)
                    LogIfSlotNotOurs(thisPtr, iconId, "uint");
                else if (thisPtr->IconId != iconId)
                    thisPtr->IconId = iconId;
            }
            else
            {
                ok = hook.Original(thisPtr, iconId);
            }
        }
        catch {   }

        if ((srcIsLib || hasRule) && !ok) macroIconHookErrors++;

        var now = Environment.TickCount64;
        if (now - lastMacroHookLogTick >= 15000)
        {
            lastMacroHookLogTick = now;
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

    private static unsafe void LogIfSlotNotOurs(AtkComponentIcon* icon, uint libId, string source)
    {
        try
        {
            if (icon == null) return;
            if (!macroKernels.TryGetValue(libId, out var kernel) || kernel == IntPtr.Zero)
                return;
            if (AnySlotIsKernel(icon, kernel)) return;
            var now = Environment.TickCount64;
            if (now - lastMacroDataDiagTick < 5000) return;
            var desc = DescribeIconState(icon);
            if (desc == lastMacroDataDiagSig) return;
            lastMacroDataDiagTick = now;
            lastMacroDataDiagSig = desc;
            Log("宏图标[" + source + "] 保留段未换槽：" + desc, true);
        }
        catch {   }
    }

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

    private static unsafe bool IconSlotIsKernel(AtkTexture* tex, IntPtr kernel)
    {
        if (tex == null || kernel == IntPtr.Zero) return false;
        try { return tex->TextureType == TextureType.KernelTexture && (nint)tex->KernelTexture == (nint)kernel; }
        catch { return false; }
    }

    private static unsafe bool AnySlotIsOurKernel(AtkComponentIcon* icon, uint id)
    {
        if (icon == null) return false;
        if (!macroKernels.TryGetValue(id, out var kernel) || kernel == IntPtr.Zero) return false;
        if (icon->IconImage != null && IconSlotIsKernel(GetImageNodeTexture(icon->IconImage), kernel)) return true;
        if (icon->FrameIcon != null && IconSlotIsKernel(GetImageNodeTexture(icon->FrameIcon), kernel)) return true;
        if (icon->Texture != null && IconSlotIsKernel(&icon->Texture->AtkTexture, kernel)) return true;
        return false;
    }

    private static unsafe void RevertIconSlotIfForeign(AtkComponentIcon* icon, uint id)
    {
        if (icon == null || InReservedRange(id)) return;
        if (macroKernels.Count == 0) return;
        if (!AnyForeignSlotIsOurs(icon)) return;
        if (id == 0) { macroRevertUnknown++; return; }

        var key = (nint)icon;
        var now = Environment.TickCount64;
        if (macroRevertBackoff.TryGetValue(key, out var until) && now < until) return;
        macroRevertBackoff[key] = now + 5000;

        try
        {
            var hook = macroIconLoadHook;
            if (hook != null) hook.Original(icon, id);
            else icon->LoadIcon(id);
            macroSlotReverts++;
        }
        catch {   }
    }

    private static unsafe bool AnyForeignSlotIsOurs(AtkComponentIcon* icon)
    {
        if (icon == null) return false;
        try
        {
            var t1 = icon->IconImage != null ? GetImageNodeTexture(icon->IconImage) : null;
            var t2 = icon->FrameIcon != null ? GetImageNodeTexture(icon->FrameIcon) : null;
            var t3 = icon->Texture != null ? &icon->Texture->AtkTexture : null;
            if (t1 == null && t2 == null && t3 == null) return false;

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
        catch {   }
        return false;
    }

    private static unsafe void RebuildReservedIconSlot(AtkComponentIcon* icon, uint id)
    {
        if (icon == null) return;
        var key = (nint)icon;
        var now = Environment.TickCount64;
        if (macroRebuildBackoff.TryGetValue(key, out var until) && now < until) return;
        if (!macroKernels.TryGetValue(id, out var kernel) || kernel == IntPtr.Zero)
        {
            EnsureMacroKernel(id);
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
                if (hook != null) ok = hook.Original(icon, ph);
                else { icon->LoadIcon(ph); ok = true; }
            }
            if (!ok) { macroRebuildFails++; macroRebuildBackoff[key] = now + 5000; return; }

            if (AttachKernelToIcon(icon, id, id) == 0)
            {
                macroRebuildFails++;
                macroRebuildBackoff[key] = now + 5000;
                return;
            }

            if (icon->IconId != id) icon->IconId = id;
            macroSlotRebuilds++;
            macroRebuildBackoff.Remove(key);
        }
        catch { macroRebuildFails++; macroRebuildBackoff[key] = now + 5000; }
    }

    private static long lastMacroDataDiagTick;
    private static string lastMacroDataDiagSig = "";

    private static unsafe int AttachKernelToIcon(AtkComponentIcon* icon, uint libId, uint fromId)
    {
        if (icon == null || !InReservedRange(libId)) return 0;

        if (InReservedRange(fromId))
        {
            try { icon->IconId = libId; } catch {   }
        }

        if (!macroKernels.TryGetValue(libId, out var kernel) || kernel == IntPtr.Zero)
        {
            EnsureMacroKernel(libId);
            return 0;
        }

        var wrote = 0;
        try
        {
            if (icon->IconImage != null)
                wrote += WriteKernelSlot(GetImageNodeTexture(icon->IconImage), fromId, libId, kernel);
            if (icon->FrameIcon != null)
                wrote += WriteKernelSlot(GetImageNodeTexture(icon->FrameIcon), fromId, libId, kernel);
            if (icon->Texture != null)
                wrote += WriteKernelSlot(&icon->Texture->AtkTexture, fromId, libId, kernel);
        }
        catch {   }

        if (wrote > 0) macroIconHookSwaps++;
        return wrote;
    }

    private static unsafe int WriteKernelSlot(AtkTexture* tex, uint fromId, uint libId, IntPtr kernel)
    {
        if (tex == null) return 0;
        try
        {
            if (tex->TextureType == TextureType.KernelTexture
                && (nint)tex->KernelTexture == kernel)
                return 0;

            NoteIconSlot(tex, fromId, libId, kernel);
            IncRefKernel(kernel);
            tex->KernelTexture = (CsGameTexture*)kernel;
            tex->TextureType = TextureType.KernelTexture;
            return 1;
        }
        catch { return 0; }
    }

    private static unsafe void NoteIconSlot(AtkTexture* tex, uint fromId, uint toId, IntPtr kernel)
    {
        if (tex == null || fromId == 0) return;
        try
        {
            var key = (IntPtr)tex;
            var rec = iconSlotPatches.GetOrAdd(key, _ => new IconSlotPatch());
            rec.From = fromId;
            rec.To = toId;
            rec.Kernel = kernel;
            rec.LastSeen = Environment.TickCount64;
        }
        catch {   }
    }

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
        catch {   }
        return wrote;
    }

    private static unsafe int OverlayIconSlot(AtkTexture* tex, uint fromId, uint toId)
    {
        if (tex == null || toId == 0) return 0;
        try
        {
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

            tex->LoadIconTexture(fromId, IconSubFolder.None);
            return 0;
        }
        catch { return 0; }
    }

    private static unsafe bool RestoreIconSlot(IntPtr slot, IconSlotPatch rec)
    {
        if (slot == IntPtr.Zero || rec == null) return false;
        var tex = (AtkTexture*)slot;
        if (tex == null) return false;

        try
        {
            if (rec.Kernel != IntPtr.Zero)
            {
                if (tex->TextureType != TextureType.KernelTexture) return false;
                if ((nint)tex->KernelTexture != rec.Kernel) return false;
            }
            else
            {
                if (tex->TextureType != TextureType.Resource) return false;
                var res0 = tex->Resource;
                if (res0 == null || res0->IconId != rec.To) return false;
            }

            if (rec.From != 0 && !InReservedRange(rec.From))
            {
                tex->LoadIconTexture(rec.From, IconSubFolder.None);
                if (tex->TextureType == TextureType.Resource && tex->Resource != null) return true;
            }

            tex->LoadIconTexture(MacroIconPlaceholders[0], IconSubFolder.None);
            return true;
        }
        catch { return false; }
    }

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
        catch {   }
        return n;
    }

    private static int RestoreIconPatchesFor(uint from) => RestoreIconPatches(f => f == from);

    private static int RestoreAllIconPatches() => RestoreIconPatches(_ => true);

    private void DrawIconReplaceSection(ref bool changed)
    {
        try
        {
            ImGui.Separator();

            var on = config.MacroIconSwap;
            if (ImGui.Checkbox("启用图标替换##macroswap", ref on))
            {
                config.MacroIconSwap = on;
                changed = true;
                SaveOwnConfig();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.InputText("##rulefrom", ref ruleFromInput, 16);
            ImGui.SameLine();
            ImGui.TextUnformatted("→");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.InputText("##ruleto", ref ruleToInput, 16);
            ImGui.SameLine();
            if (ImGui.Button("添加##ruleadd")) AddIconRuleFromInputs(ref changed);
            ImGui.SameLine();
            if (ImGui.Button("删除##ruledel")) RemoveIconRuleFromInputs(ref changed);
            ImGui.SameLine();
            if (ImGui.Button("全部清除##ruleclear")) ClearAllIconRules(ref changed);

            if (config.IconReplaceRules.Count == 0)
                return;

            for (var i = 0; i < config.IconReplaceRules.Count; i++)
            {
                var r = config.IconReplaceRules[i];
                if (r == null) continue;

                ImGui.PushID("iconrule_" + i);
                ImGui.TextUnformatted(r.From + "  →  " + r.To + "   " + DescribeIconId(r.To));
                ImGui.SameLine();
                var std = ImGui.GetStyle();
                var delW = ImGui.CalcTextSize("删除").X + std.FramePadding.X * 2 + 2;
                ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(),
                    ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - delW));
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

    private string DescribeIconId(uint id)
    {
        if (!InReservedRange(id)) return "（游戏图标）";
        var e = config.BrowserImages.FirstOrDefault(b => b.Id == id);
        return e == null ? "（图库里没这个编号！）" : "（图库：" + e.Name + "）";
    }

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

    private static string TruncateForWidth(string s, float maxW)
    {
        if (maxW <= 0 || s.Length == 0) return "";
        if (ImGui.CalcTextSize(s).X <= maxW) return s;
        while (s.Length > 0 && ImGui.CalcTextSize(s + "…").X > maxW)
            s = s[..^1];
        return s + "…";
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

    [Serializable]
    private sealed class Config
    {
        public bool UseProxy { get; set; }
        public string ProxyUrl { get; set; } = "http://127.0.0.1:10808";

        public bool MacroIconSwap { get; set; } = true;

        public List<IconReplaceRule> IconReplaceRules { get; set; } = new();

        public sealed class IconReplaceRule
        {
            public uint From { get; set; }

            public uint To { get; set; }
        }

        public List<BrowserImageEntry> BrowserImages { get; set; } = new();

        public uint NextIconId { get; set; }

        public sealed class BrowserImageEntry
        {
            public uint Id { get; set; }

            public string Path { get; set; } = "";

            public string Name { get; set; } = "";

            public string SourceUrl { get; set; } = "";

            public bool UseProxy { get; set; }
        }
    }
}

