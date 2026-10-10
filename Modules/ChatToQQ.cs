
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.OmenService;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using OmniToolbox.UI.Theme;

namespace OmniToolbox.LocalModules;

public sealed class ChatToQQ : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "游戏聊天同步 QQ",
        Description = "",
        Category    = ModuleCategory.Automation,
        Author      = "小烟酒",
    };

    [Serializable]
    private sealed class Config
    {
        public string OneBotURL   { get; set; } = "http://127.0.0.1:3000";
        public string AccessToken { get; set; } = "";
        public string GroupIDs    { get; set; } = "";

        public string AtRules     { get; set; } = "";
        public string DefaultAtQQ { get; set; } = "";

        public bool MirrorEnabled { get; set; } = true;
        public bool AtOnTell      { get; set; } = true;
        public bool AtOnTargeted  { get; set; } = true;

        public HashSet<string> MirrorChannels { get; set; } = new()
        {
            "悄悄话", "说话", "小队", "团队", "呼喊", "喊话",
            "部队", "新人", "表情", "通讯贝1-8", "跨服贝1-8"
        };

        public bool AtOnPartyInvite { get; set; } = true;
        public bool AtOnTrade       { get; set; } = true;
        public bool AtOnFriendReq   { get; set; } = true;

        public bool   QQToGameEnabled { get; set; } = false;
        public string TriggerPrefix   { get; set; } = "#";
        public string GameChannel     { get; set; } = "/p";
        public string BotQQ           { get; set; } = "";

        public bool   SyncWorldChat { get; set; } = true;
        public string WorldChatTag  { get; set; } = "国服";
    }

    private Config config = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private const string CommandTogglePanel = "/chattoqq";

    private static string ConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncherCN", "pluginConfigs", "OmniChatToQQ.json");

    private static readonly string DebugLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncherCN", "pluginConfigs", "ChatToQQ.debug.log");

    private static void LogDebug(string line)
    {
        try
        {
            var fi = new FileInfo(DebugLogPath);
            if (fi.Exists && fi.Length > 256 * 1024) File.WriteAllText(DebugLogPath, "");
            File.AppendAllText(DebugLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
        }
        catch { }
    }

    private readonly ConcurrentQueue<string> sendQueue = new();
    private CancellationTokenSource? senderCts;
    private IChatGui? chatGui;
    private IAddonLifecycle? addonLifecycle;
    private IFramework? framework;
    private IObjectTable? objectTable;

    private bool windowOpen;

    private readonly HashSet<string> lastTargetingNames = [];
    private long lastTargetScanAt;

    private CancellationTokenSource? pollCts;
    private readonly HashSet<long> seenMsgIDs = [];
    private bool firstPollDone = false;

    private object?    worldChatClient;
    private EventInfo? worldChatEvent;
    private Delegate?  worldChatHandler;
    private long       worldChatRetryAt;
    private string     worldChatStatus = "未连接";

    private volatile string statusText = "运行中";

    private string urlInput     = "";
    private string tokenInput   = "";
    private string groupsInput  = "";
    private string atQQInput    = "";
    private string botQQInput   = "";
    private string triggerInput = "#";

    private ICommandManager? commandManager;
    private readonly HashSet<string> registeredCommands = [];

    private static readonly string[] MirrorChannelKeys =
    [
        "悄悄话", "说话", "小队", "团队", "呼喊", "喊话",
        "部队", "新人", "表情", "通讯贝1-8", "跨服贝1-8"
    ];

    private static readonly string[] LegacyMirrorKeys =
    [
        "喊话(区域)", "密语发送", "新人频道", "队伍", "联盟", "LS1-8"
    ];

    private static string? MirrorChannelKey(XivChatType t) => t switch
    {
        XivChatType.Say             => "说话",
        XivChatType.Shout           => "喊话",
        XivChatType.Yell            => "呼喊",
        XivChatType.Party           => "小队",
        XivChatType.Alliance        => "团队",
        XivChatType.FreeCompany     => "部队",
        XivChatType.CustomEmote     => "表情",
        XivChatType.StandardEmote   => "表情",
        XivChatType.NoviceNetwork   => "新人",
        XivChatType.TellOutgoing    => "悄悄话",
        XivChatType.Ls1 or XivChatType.Ls2 or XivChatType.Ls3 or XivChatType.Ls4 or
        XivChatType.Ls5 or XivChatType.Ls6 or XivChatType.Ls7 or XivChatType.Ls8 => "通讯贝1-8",
        XivChatType.CrossLinkShell1 or XivChatType.CrossLinkShell2 or XivChatType.CrossLinkShell3 or
        XivChatType.CrossLinkShell4 or XivChatType.CrossLinkShell5 or XivChatType.CrossLinkShell6 or
        XivChatType.CrossLinkShell7 or XivChatType.CrossLinkShell8 => "跨服贝1-8",
        _                           => null
    };

    private static readonly string[] PartyInviteKeywords = ["发来的入队邀请", "入队邀请", "邀请你加入", "希望加入你的队伍", "希望加入你的部队", "希望加入你的小队"];
    private static readonly string[] TradeKeywords       = ["希望与你交易", "希望与你进行交易", "请求与你交易", "发来的交易请求", "交易请求", "交易申请"];
    private static readonly string[] FriendKeywords      = ["发来的好友申请", "发来的好友请求", "好友申请", "好友请求", "请求添加你为好友", "请求成为好友", "添加你为好友"];

    private long lastPartyNotifyTicks  = 0;
    private long lastTradeNotifyTicks  = 0;
    private long lastFriendNotifyTicks = 0;

    private static bool TryStampNotify(ref long ticks)
    {
        var now = Environment.TickCount64;
        if (now - ticks < 10_000) return false;
        ticks = now;
        return true;
    }

    private static bool IsPartyInvite(string s) => PartyInviteKeywords.Any(s.Contains);
    private static bool IsTradeReq(string s)    => TradeKeywords.Any(s.Contains);
    private static bool IsFriendReq(string s)   => FriendKeywords.Any(s.Contains);

    public ChatToQQ()
    {
        LoadOwnConfig();
    }

    protected override void OnEnable()
    {
        urlInput     = config.OneBotURL;
        tokenInput   = config.AccessToken;
        groupsInput  = config.GroupIDs;
        atQQInput    = config.DefaultAtQQ;
        botQQInput   = config.BotQQ;
        triggerInput = config.TriggerPrefix;

        AttachChat();

        commandManager = DalamudServices.CommandManager ??
                         GetService<ICommandManager>("Dalamud.Game.Command.CommandManager");
        RegisterCommands();

        AttachWorldChat();

        if (DalamudServices.PluginInterface != null)
            DalamudServices.PluginInterface.UiBuilder.Draw += DrawPanel;

        senderCts = new CancellationTokenSource();
        _ = Task.Run(() => SenderLoop(senderCts.Token));

        statusText = $"运行中 (群: {(config.GroupIDs.Length > 0 ? "已配置" : "未配置")})";
    }

    protected override void OnDisable()
    {
        DetachWorldChat();
        try { if (chatGui != null) chatGui.ChatMessage -= OnChatMessage; } catch { }
        try { addonLifecycle?.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoPopup); } catch { }
        try { if (framework != null) framework.Update -= OnFrameworkUpdate; } catch { }
        try { if (DalamudServices.PluginInterface != null) DalamudServices.PluginInterface.UiBuilder.Draw -= DrawPanel; } catch { }

        UnregisterCommands();

        try { pollCts?.Cancel(); pollCts?.Dispose(); } catch { }
        pollCts = null;
        senderCts?.Cancel();
        senderCts?.Dispose();
        senderCts = null;
        commandManager = null;
    }

    protected override void OnDispose()
    {
        sendQueue.Clear();
    }

    private void LoadOwnConfig()
    {
        try
        {
            var path = ConfigFilePath;
            if (File.Exists(path))
            {
                var json   = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Config>(json);
                if (loaded != null) config = loaded;

                if (LegacyMirrorKeys.Any(k => config.MirrorChannels.Contains(k)))
                    config.MirrorChannels = new HashSet<string>(MirrorChannelKeys);
            }
        }
        catch { }
    }

    private void SaveOwnConfig()
    {
        try
        {
            var path = ConfigFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void RegisterCommands()
    {
        if (commandManager == null) return;
        AddCommand(CommandTogglePanel, new CommandInfo((command, arguments) => TogglePanel()));
    }

    private void AddCommand(string cmd, CommandInfo info)
    {
        try
        {
            if (!registeredCommands.Contains(cmd) && commandManager!.AddHandler(cmd, info))
                registeredCommands.Add(cmd);
        }
        catch { }
    }

    private void UnregisterCommands()
    {
        if (commandManager == null) return;
        foreach (var cmd in registeredCommands.ToList())
        {
            try { commandManager.RemoveHandler(cmd); } catch { }
        }
        registeredCommands.Clear();
    }

    private void TogglePanel()
    {
        windowOpen = !windowOpen;
        statusText = windowOpen ? "面板已打开" : "面板已关闭";
    }

    private void AttachChat()
    {
        try
        {
            var dalamudAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Dalamud");
            if (dalamudAsm == null) { statusText = "未找到 Dalamud 程序集"; return; }

            var chatGuiImplType = dalamudAsm.GetType("Dalamud.Game.Gui.ChatGui", true);
            var serviceDefType  = dalamudAsm.GetType("Dalamud.Service`1", true);
            if (chatGuiImplType == null || serviceDefType == null) { statusText = "未找到聊天服务类型"; return; }

            var serviceType = serviceDefType.MakeGenericType(chatGuiImplType);
            var getMethod   = serviceType.GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
            var instance    = getMethod?.Invoke(null, null);
            if (instance == null) { statusText = "无法获取聊天服务实例"; return; }

            chatGui = (IChatGui)instance;
            chatGui.ChatMessage += OnChatMessage;
        }
        catch (Exception e) { statusText = $"挂接聊天事件失败: {e.Message}"; }

        try
        {
            addonLifecycle = GetService<IAddonLifecycle>("Dalamud.Plugin.Services.IAddonLifecycle");
            addonLifecycle?.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoPopup);
        }
        catch { }

        try
        {
            framework = GetService<IFramework>("Dalamud.Game.Framework");
            if (framework != null) framework.Update += OnFrameworkUpdate;
            LogDebug($"Framework 服务获取: {(framework != null ? "成功" : "失败(null)")}");
        }
        catch (Exception e) { LogDebug($"Framework 服务获取异常: {e}"); }

        try
        {
            objectTable = GetService<IObjectTable>("Dalamud.Game.ClientState.Objects.ObjectTable");
            LogDebug($"ObjectTable 服务获取: {(objectTable != null ? "成功" : "失败(null)")}");
        }
        catch (Exception e) { LogDebug($"ObjectTable 服务获取异常: {e}"); }

        pollCts = new CancellationTokenSource();
        _ = Task.Run(() => PollQQMessages(pollCts.Token));
    }

    private void RunOnGameThread(System.Action action)
    {
        try
        {
            if (framework != null) { framework.RunOnFrameworkThread(action); return; }
        }
        catch (Exception e) { LogDebug($"RunOnFrameworkThread 派发失败: {e}"); }

        try { action(); }
        catch (Exception e) { LogDebug($"发送动作执行失败: {e}"); }
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        var now = Environment.TickCount64;

        if (config.SyncWorldChat && worldChatClient == null && now - worldChatRetryAt > 5_000)
        {
            worldChatRetryAt = now;
            AttachWorldChat();
        }

        if (now - lastTargetScanAt < 500) return;
        lastTargetScanAt = now;
        ScanTargeting();
    }

    private void ScanTargeting()
    {
        try
        {
            if (objectTable == null) return;
            var local = objectTable.LocalPlayer;
            if (local == null) return;
            var localId = local.GameObjectId;

            var current = new HashSet<string>();
            for (var i = 0; i < objectTable.Length; i++)
            {
                var obj = objectTable[i];
                if (obj is not IPlayerCharacter player) continue;
                if (player.ObjectIndex == local.ObjectIndex) continue;
                if (player.TargetObjectId != localId) continue;

                var name = player.Name.TextValue ?? "";
                if (name.Length == 0) continue;
                current.Add(name);

                if (!config.AtOnTargeted) continue;
                if (lastTargetingNames.Contains(name)) continue;

                Enqueue(ResolveAt(name), $"🎯 {name} 正在选中你");
            }

            lastTargetingNames.Clear();
            foreach (var n in current) lastTargetingNames.Add(n);
        }
        catch { }
    }

    private unsafe void OnSelectYesnoPopup(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!config.AtOnPartyInvite && !config.AtOnTrade && !config.AtOnFriendReq) return;

            var addon = (AddonSelectYesno*)args.Addon.Address;
            if (addon == null || addon->PromptText == null) return;

            var textPtr = addon->PromptText->GetText();
            if (!textPtr.HasValue) return;

            var prompt = textPtr.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(prompt)) return;

            var hit = (config.AtOnPartyInvite && IsPartyInvite(prompt) && TryStampNotify(ref lastPartyNotifyTicks)) ||
                      (config.AtOnTrade       && IsTradeReq(prompt)    && TryStampNotify(ref lastTradeNotifyTicks))  ||
                      (config.AtOnFriendReq   && IsFriendReq(prompt)   && TryStampNotify(ref lastFriendNotifyTicks));
            if (!hit) return;

            Enqueue(null, $"⚠️ 游戏提醒  {prompt}\n(请在游戏中处理该申请)");
        }
        catch { }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            var content = message.Message.TextValue;
            if (string.IsNullOrWhiteSpace(content)) return;

            var sender = (message.Sender?.TextValue ?? "").Trim();
            var kind   = message.LogKind;

            if (kind == XivChatType.TellIncoming && config.AtOnTell)
            {
                Enqueue(ResolveAt(sender), $"💬 收到私聊  {DecorateSender(sender)}: {content}");
                return;
            }

            if (config.AtOnPartyInvite || config.AtOnTrade || config.AtOnFriendReq)
            {
                string? catText = null;
                if (config.AtOnPartyInvite && IsPartyInvite(content))
                {
                    if (TryStampNotify(ref lastPartyNotifyTicks)) catText = "组队邀请";
                }
                else if (config.AtOnTrade && IsTradeReq(content))
                {
                    if (TryStampNotify(ref lastTradeNotifyTicks)) catText = "交易申请";
                }
                else if (config.AtOnFriendReq && IsFriendReq(content))
                {
                    if (TryStampNotify(ref lastFriendNotifyTicks)) catText = "好友请求";
                }

                if (catText != null)
                {
                    Enqueue(ResolveAt(sender), $"⚠️ 收到{catText}  {content}");
                    return;
                }
            }

            if (config.MirrorEnabled)
            {
                var key = MirrorChannelKey(kind);
                if (key != null && config.MirrorChannels.Contains(key))
                    Enqueue(null, $"[{ChannelLabel(kind)}] {(sender.Length > 0 ? DecorateSender(sender) + ": " : "")}{content}");
            }
        }
        catch { }
    }

    private string homeWorldName  = "";
    private long   homeWorldTicks = 0;

    private string GetHomeWorldName()
    {
        var now = Environment.TickCount64;
        if (homeWorldName.Length > 0 && now - homeWorldTicks < 60_000) return homeWorldName;
        try
        {
            var w = objectTable?.LocalPlayer?.HomeWorld.ValueNullable?.Name.ExtractText() ?? "";
            if (!string.IsNullOrWhiteSpace(w)) { homeWorldName = w; homeWorldTicks = now; }
            return w;
        }
        catch { return ""; }
    }

    private string DecorateSender(string sender)
    {
        if (sender.Length == 0 || sender.Contains('@')) return sender;
        var w = GetHomeWorldName();
        return w.Length > 0 ? $"{sender}@{w}" : sender;
    }

    private string? ResolveAt(string senderName)
    {
        var baseName = BaseName(senderName);
        var rules    = config.AtRules ?? "";

        foreach (var line in rules.Split('\n'))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var name = line[..idx].Trim();
            var qq   = line[(idx + 1)..].Trim();
            if (name.Length == 0 || qq.Length == 0) continue;
            if (baseName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(baseName, StringComparison.OrdinalIgnoreCase))
                return qq;
        }

        return string.IsNullOrWhiteSpace(config.DefaultAtQQ) ? null : config.DefaultAtQQ.Trim();
    }

    private static string BaseName(string sender)
    {
        var idx = sender.IndexOfAny(['@', '<', ' ']);
        return idx > 0 ? sender[..idx].Trim() : sender.Trim();
    }

    private static string ChannelLabel(XivChatType type) => type switch
    {
        XivChatType.Say             => "说话",
        XivChatType.Shout           => "喊话",
        XivChatType.Yell            => "呼喊",
        XivChatType.Party           => "小队",
        XivChatType.Alliance        => "团队",
        XivChatType.TellIncoming    => "私聊",
        XivChatType.TellOutgoing    => "悄悄话",
        XivChatType.FreeCompany     => "部队",
        XivChatType.CustomEmote     => "表情",
        XivChatType.StandardEmote   => "表情",
        XivChatType.NoviceNetwork   => "新人",
        XivChatType.Ls1             => "通讯贝1",
        XivChatType.Ls2             => "通讯贝2",
        XivChatType.Ls3             => "通讯贝3",
        XivChatType.Ls4             => "通讯贝4",
        XivChatType.Ls5             => "通讯贝5",
        XivChatType.Ls6             => "通讯贝6",
        XivChatType.Ls7             => "通讯贝7",
        XivChatType.Ls8             => "通讯贝8",
        XivChatType.CrossLinkShell1 => "跨服贝1",
        XivChatType.CrossLinkShell2 => "跨服贝2",
        XivChatType.CrossLinkShell3 => "跨服贝3",
        XivChatType.CrossLinkShell4 => "跨服贝4",
        XivChatType.CrossLinkShell5 => "跨服贝5",
        XivChatType.CrossLinkShell6 => "跨服贝6",
        XivChatType.CrossLinkShell7 => "跨服贝7",
        XivChatType.CrossLinkShell8 => "跨服贝8",
        _                           => type.ToString()
    };

    private static readonly (string Kw, string Cmd)[] ChannelKeywords =
    [
        ("说话", "/s"),
        ("小队", "/p"),
        ("队伍", "/p"),
        ("团队", "/a"),
        ("呼喊", "/y"),
        ("喊话", "/sh"),
        ("部队", "/fc"),
        ("新人", "/n"),
    ];

    private async Task PollQQMessages(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);

                if (!config.QQToGameEnabled) continue;

                var groups = ParseGroups();
                if (groups.Count == 0) continue;

                var base_ = (config.OneBotURL ?? "").TrimEnd('/');
                if (base_.Length == 0) continue;

                foreach (var gid in groups)
                {
                    if (ct.IsCancellationRequested) break;

                    using var req = new HttpRequestMessage(HttpMethod.Post, base_ + "/get_group_msg_history");
                    req.Content = new StringContent(
                        JsonSerializer.Serialize(new { group_id = gid, count = 20 }),
                        Encoding.UTF8, "application/json");
                    var token = (config.AccessToken ?? "").Trim();
                    if (token.Length > 0)
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) continue;

                    var body = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
                    if (!data.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array) continue;

                    if (!firstPollDone)
                        LogDebug($"首轮轮询成功: 群 {gid} 拉到 {msgs.GetArrayLength()} 条历史消息");

                    foreach (var m in msgs.EnumerateArray())
                    {
                        var msgID = m.TryGetProperty("message_id", out var mid) && mid.ValueKind == JsonValueKind.Number ? mid.GetInt64() : 0;
                        if (msgID == 0) continue;

                        lock (seenMsgIDs)
                        {
                            if (seenMsgIDs.Contains(msgID)) continue;
                            seenMsgIDs.Add(msgID);
                            if (seenMsgIDs.Count > 1000) seenMsgIDs.Clear();
                        }

                        if (!firstPollDone) continue;

                        var userID = m.TryGetProperty("user_id", out var uid) && uid.ValueKind == JsonValueKind.Number ? uid.GetInt64() : 0;

                        var botQQ = (config.BotQQ ?? "").Trim();
                        if (botQQ.Length > 0 && userID.ToString() == botQQ) continue;

                        var trig = (config.TriggerPrefix ?? "").Trim();
                        if (trig.Length == 0) continue;

                        var text = m.TryGetProperty("message", out var mm) ? ExtractQQText(mm) : "";
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        if (!text.StartsWith(trig)) continue;
                        text = text[trig.Length..].TrimStart();

                        var cmd   = (config.GameChannel ?? "/p").Trim();
                        var parts = text.Split([' ', '　'], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            var kw = parts[0];
                            if (kw == "悄悄话")
                            {
                                if (parts.Length >= 3)
                                {
                                    cmd  = "/t " + parts[1];
                                    text = string.Join(' ', parts.Skip(2));
                                }
                                else continue;
                            }
                            else
                            {
                                var hit = false;
                                foreach (var (k, c) in ChannelKeywords)
                                {
                                    if (kw == k) { cmd = c; text = string.Join(' ', parts.Skip(1)); hit = true; break; }
                                }
                                if (!hit)
                                {
                                    var cwl = Regex.Match(kw, @"^跨服贝([1-8])?$");
                                    if (cwl.Success)
                                    {
                                        cmd  = "/cwl" + (cwl.Groups[1].Success ? cwl.Groups[1].Value : "1");
                                        text = string.Join(' ', parts.Skip(1));
                                    }
                                    else
                                    {
                                        var ls = Regex.Match(kw, @"^通讯贝([1-8])?$");
                                        if (ls.Success)
                                        {
                                            cmd  = "/ls" + (ls.Groups[1].Success ? ls.Groups[1].Value : "1");
                                            text = string.Join(' ', parts.Skip(1));
                                        }
                                    }
                                }
                            }
                        }
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        if (text.Length > 120) text = text[..120];

                        var sendText = $"{cmd} {text}";
                        try
                        {
                            RunOnGameThread(() => ChatManager.Instance().SendMessage(sendText));
                            statusText = $"上次同步 {DateTime.Now:HH:mm:ss}: {sendText}";
                            LogDebug($"命中触发, 发送: {sendText}");
                        }
                        catch (Exception sendEx)
                        {
                            statusText = $"入队发送失败: {sendEx.Message}";
                            LogDebug($"入队发送失败: {sendEx}");
                        }
                    }
                }

                firstPollDone = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                statusText = $"轮询异常: {e.Message}";
                LogDebug($"轮询异常: {e}");
            }
        }
    }

    private static string ExtractQQText(JsonElement message)
    {
        if (message.ValueKind == JsonValueKind.String)
        {
            var s = message.GetString() ?? "";
            return Regex.Replace(s, @"\[CQ:[^\]]*\]", "").Trim();
        }

        if (message.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var seg in message.EnumerateArray())
            {
                if (seg.ValueKind != JsonValueKind.Object) continue;
                if (seg.TryGetProperty("type", out var t) && t.GetString() == "text")
                {
                    if (seg.TryGetProperty("data", out var d) && d.TryGetProperty("text", out var txt))
                        sb.Append(txt.GetString() ?? "");
                }
            }
            return sb.ToString().Trim();
        }

        return "";
    }

    private void Enqueue(string? atQQ, string text)
    {
        var groups = ParseGroups();
        if (groups.Count == 0) return;

        foreach (var gid in groups)
        {
            var segments = new JsonArray();
            if (!string.IsNullOrEmpty(atQQ))
                segments.Add(new JsonObject { ["type"] = "at", ["data"] = new JsonObject { ["qq"] = atQQ } });
            segments.Add(new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = " " + text } });

            var payload = new JsonObject { ["group_id"] = gid, ["message"] = segments }.ToJsonString();
            sendQueue.Enqueue(payload);
        }

        while (sendQueue.Count > 60 && sendQueue.TryDequeue(out _)) { }
    }

    private List<long> ParseGroups()
    {
        var result = new List<long>();
        var raw    = (config.GroupIDs ?? "").Split([',', '，', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in raw)
            if (long.TryParse(part.Trim(), out var gid) && !result.Contains(gid))
                result.Add(gid);
        return result;
    }

    private async Task SenderLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (sendQueue.TryDequeue(out var payload))
                {
                    await SendOnce(payload);
                    await Task.Delay(300, ct);
                }
                else
                {
                    await Task.Delay(200, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task SendOnce(string payload)
    {
        try
        {
            var base_ = (config.OneBotURL ?? "").TrimEnd('/');
            if (base_.Length == 0) return;

            using var req = new HttpRequestMessage(HttpMethod.Post, base_ + "/send_group_msg");
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            var token = (config.AccessToken ?? "").Trim();
            if (token.Length > 0)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                statusText = $"发送失败: HTTP {(int)resp.StatusCode} (检查 NapCat 是否运行/端口/token)";
        }
        catch (Exception e)
        {
            statusText = $"发送失败: {e.Message} (检查 NapCat 是否运行/端口)";
        }
    }

    public override bool HasSettings => true;

    public override bool DrawSettings()
    {
        if (ImGui.Button(windowOpen ? "关闭独立面板" : "打开独立面板"))
            TogglePanel();

        return false;
    }

    private void DrawPanel()
    {
        if (!windowOpen) return;

        IDisposable? fontHandle = null;
        try { fontHandle = OmniFonts.GetUIFont().Push(); } catch { }

        try
        {
            ImGui.SetNextWindowSize(OmniTheme.Scale(new Vector2(640f, 620f)), ImGuiCond.FirstUseEver);

            using var theme = new ComicStyleScope();

            if (!ImGui.Begin("游戏聊天同步 QQ###OmniChatToQQ", ref windowOpen))
            {
                ImGui.End();
                return;
            }

            try
            {
                DrawConfigContent();
            }
            finally
            {
                ImGui.End();
            }
        }
        catch { }
        finally
        {
            fontHandle?.Dispose();
        }
    }

    private void DrawConfigContent()
    {
        ImGui.TextDisabled(statusText);

        ImGui.Separator();
        ImGui.Spacing();

        var changed = false;

        var mirror = config.MirrorEnabled;
        if (ImGui.Checkbox("同步全部聊天到群里", ref mirror)) { config.MirrorEnabled = mirror; SaveOwnConfig(); }

        if (config.MirrorEnabled)
        {
            ImGui.Indent();
            ImGui.TextDisabled("筛选发送到 QQ 的频道:");
            DrawMirrorChannelToggles();
            ImGui.Unindent();
        }

        var qqToGame = config.QQToGameEnabled;
        if (ImGui.Checkbox("把 QQ 群消息同步进游戏 (双向)", ref qqToGame)) { config.QQToGameEnabled = qqToGame; SaveOwnConfig(); }

        var world = config.SyncWorldChat;
        if (ImGui.Checkbox("同步 Omni 国服频道到群里", ref world))
        {
            config.SyncWorldChat = world;
            SaveOwnConfig();
            if (world) { DetachWorldChat(); AttachWorldChat(); }
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"国服频道: {worldChatStatus}");

        ImGui.Separator();
        ImGui.TextUnformatted("什么消息需要 @我 (自行勾选):");

        var atTell = config.AtOnTell;
        if (ImGui.Checkbox("收到私聊时", ref atTell)) { config.AtOnTell = atTell; SaveOwnConfig(); }

        var atParty = config.AtOnPartyInvite;
        if (ImGui.Checkbox("被邀请组队时", ref atParty)) { config.AtOnPartyInvite = atParty; SaveOwnConfig(); }

        var atTrade = config.AtOnTrade;
        if (ImGui.Checkbox("被请求交易时", ref atTrade)) { config.AtOnTrade = atTrade; SaveOwnConfig(); }

        var atFriend = config.AtOnFriendReq;
        if (ImGui.Checkbox("被请求加好友时", ref atFriend)) { config.AtOnFriendReq = atFriend; SaveOwnConfig(); }

        var atTargeted = config.AtOnTargeted;
        if (ImGui.Checkbox("被人选中(以你为目标)时", ref atTargeted)) { config.AtOnTargeted = atTargeted; SaveOwnConfig(); }

        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-130f);
        changed |= ImGui.InputText("OneBot 地址", ref urlInput, 200);
        ImGui.SetNextItemWidth(-130f);
        changed |= ImGui.InputText("Access Token", ref tokenInput, 200);
        ImGui.SetNextItemWidth(-130f);
        changed |= ImGui.InputText("QQ 群号 (多个用英文逗号分隔)", ref groupsInput, 300);

        if (config.QQToGameEnabled)
        {
            ImGui.SetNextItemWidth(-130f);
            if (ImGui.InputText("机器人 QQ号", ref botQQInput, 32))
            {
                config.BotQQ = botQQInput.Trim();
                SaveOwnConfig();
            }

            ImGui.SetNextItemWidth(-130f);
            if (ImGui.InputText("触发符号", ref triggerInput, 8))
            {
                config.TriggerPrefix = triggerInput;
                SaveOwnConfig();
            }

            ImGui.SetNextItemWidth(-130f);
            var channels = new[] { ("小队 (/p)", "/p"), ("说话 (/s)", "/s"), ("团队 (/a)", "/a"), ("呼喊 (/y)", "/y"), ("喊话 (/sh)", "/sh"), ("部队 (/fc)", "/fc"), ("新人 (/n)", "/n"), ("通讯贝 (/ls1)", "/ls1"), ("跨服贝1 (/cwl1)", "/cwl1") };
            var chIdx    = Array.FindIndex(channels, c => c.Item2 == (config.GameChannel ?? "/p"));
            if (chIdx < 0) chIdx = 0;
            if (ImGui.BeginCombo("默认频道", channels[chIdx].Item1))
            {
                for (var i = 0; i < channels.Length; i++)
                {
                    if (ImGui.Selectable(channels[i].Item1, i == chIdx))
                    {
                        config.GameChannel = channels[i].Item2;
                        SaveOwnConfig();
                    }
                }
                ImGui.EndCombo();
            }
        }

        ImGui.SetNextItemWidth(-130f);
        if (ImGui.InputText("固定@ QQ号", ref atQQInput, 64)) changed = true;

        ImGui.Spacing();

        if (ImGui.Button("📤 发送测试消息", new Vector2(-1f, 28f)))
            Enqueue(null, "✅ 测试消息: 游戏聊天同步模块工作正常");

        ImGui.Spacing();
        ImGui.TextWrapped("前提: 本机运行 NapCatQQ Desktop 并登录 QQ, OneBot 地址形如 http://127.0.0.1:3000");

        if (changed)
        {
            config.OneBotURL   = urlInput;
            config.AccessToken = tokenInput;
            config.GroupIDs    = groupsInput;
            config.DefaultAtQQ = atQQInput;
            SaveOwnConfig();
            statusText = $"运行中 (群: {(ParseGroups().Count > 0 ? "已配置" : "未配置")})";
        }
    }

    private void DrawMirrorChannelToggles()
    {
        ImGui.Columns(3, "###MirrorChannels", false);
        foreach (var key in MirrorChannelKeys)
        {
            var on = config.MirrorChannels.Contains(key);
            if (ImGui.Checkbox(key, ref on))
            {
                if (on) config.MirrorChannels.Add(key);
                else config.MirrorChannels.Remove(key);
                SaveOwnConfig();
            }
            ImGui.NextColumn();
        }
        ImGui.Columns(1);
    }

    private void AttachWorldChat()
    {
        if (worldChatClient != null) return;
        try
        {
            var client = FindOmniWorldChatClient();
            if (client == null) { worldChatStatus = "等待 Omni 模块"; return; }

            var type    = client.GetType();
            var evt     = type.GetEvent("MessageReceived", BindingFlags.Public | BindingFlags.Instance);
            var msgType = type.GetNestedType("ChannelMessage", BindingFlags.Public | BindingFlags.NonPublic);
            if (evt == null || msgType == null)
            {
                worldChatStatus = "接口不匹配";
                LogDebug($"国服频道: 事件={(evt != null)} 消息类型={(msgType != null)}");
                return;
            }

            var hook = typeof(ChatToQQ).GetMethod(nameof(HookWorldChatEvent), BindingFlags.NonPublic | BindingFlags.Instance);
            if (hook == null) { worldChatStatus = "挂接方法缺失"; return; }

            worldChatHandler = (Delegate?)hook.MakeGenericMethod(msgType).Invoke(this, new object[] { client, evt });
            worldChatEvent   = evt;
            worldChatClient  = client;
            worldChatStatus  = "已连接";

            var connected = ReadValue(type, client, "Connected")?.ToString() ?? "?";
            var members   = (ReadValue(type, client, "Members") as System.Collections.ICollection)?.Count;
            LogDebug($"国服频道: 已挂接 OmniWorldChatClient.MessageReceived (Connected={connected}, Members={members?.ToString() ?? "?"})");

            if (connected == "False")
            {
                try
                {
                    var join = type.GetMethod("Join", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (join != null) { join.Invoke(client, null); LogDebug("国服频道: 已调用 Join()"); }
                }
                catch (Exception e) { LogDebug($"国服频道: Join() 失败 {e.Message}"); }
            }
        }
        catch (Exception e)
        {
            worldChatStatus = "挂接失败";
            LogDebug($"国服频道挂接异常: {e}");
        }
    }

    private void DetachWorldChat()
    {
        try
        {
            if (worldChatClient != null && worldChatEvent != null && worldChatHandler != null)
                worldChatEvent.RemoveEventHandler(worldChatClient, worldChatHandler);
        }
        catch { }

        worldChatClient  = null;
        worldChatEvent   = null;
        worldChatHandler = null;
        worldChatStatus  = "未连接";
    }

    private Delegate HookWorldChatEvent<T>(object client, EventInfo evt)
    {
        Action<T> handler = message => OnWorldChatMessage(message);
        evt.AddEventHandler(client, handler);
        return handler;
    }

    private void OnWorldChatMessage(object? message)
    {
        try
        {
            if (message == null || !config.SyncWorldChat) return;

            var type = message.GetType();
            var text = ReadMember(type, message, "Text");
            if (text.Length == 0) return;

            var name  = ReadMember(type, message, "DisplayName");
            var world = ReadMember(type, message, "HomeWorldName");
            var sender = name.Length == 0 ? "" : (name.Contains('@') || world.Length == 0 ? name : name + "@" + world);

            var tag = (config.WorldChatTag ?? "").Trim();
            if (tag.Length == 0) tag = "国服";

            Enqueue(null, $"[{tag}] {(sender.Length > 0 ? sender + ": " : "")}{text}");
        }
        catch (Exception e) { LogDebug($"国服频道消息处理异常: {e.Message}"); }
    }

    private static string ReadMember(Type type, object instance, string property)
    {
        return (ReadValue(type, instance, property) as string ?? "").Trim();
    }

    private static object? ReadValue(Type type, object instance, string property)
    {
        try { return type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance); }
        catch { return null; }
    }

    private object? FindOmniWorldChatClient()
    {
        var manager = FindOmniTreeHouseManager();
        if (manager == null) return null;

        try
        {
            var modules = manager.GetType().GetProperty("Modules", BindingFlags.Public | BindingFlags.Instance)?.GetValue(manager) as System.Collections.IEnumerable;
            if (modules == null) return null;

            foreach (var module in modules)
            {
                if (module == null || module.GetType().Name != "ChatFrameOptimization") continue;
                var client = FirstFieldOfType(module, "OmniWorldChatClient");
                if (client != null) return client;
            }
        }
        catch (Exception e) { LogDebug($"国服频道: 读取 Omni 模块列表失败: {e.Message}"); }

        return null;
    }

    private object? FindOmniTreeHouseManager()
    {
        try
        {
            var commands = DalamudServices.CommandManager?.Commands;
            if (commands != null)
            {
                foreach (var entry in commands)
                {
                    object? target = null;
                    try { target = entry.Value?.Handler?.Target; } catch { }
                    if (target == null) continue;

                    var router = target.GetType().Name == "OmniCommandRouter" ? target : FirstFieldOfType(target, "OmniCommandRouter");
                    if (router == null) continue;

                    var manager = FirstFieldOfType(router, "TreeHouseManager");
                    if (manager != null) return manager;
                }
            }
        }
        catch (Exception e) { LogDebug($"国服频道: 指令表探测失败: {e.Message}"); }

        try
        {
            var pluginInterface = DalamudServices.PluginInterface;
            var localPlugin     = pluginInterface == null ? null : FirstFieldOfType(pluginInterface, "LocalPlugin");
            var plugin          = localPlugin == null ? null : FirstFieldOfType(localPlugin, "IDalamudPlugin");
            if (plugin != null) return FindFieldValueOfType(plugin, "TreeHouseManager", 3);
        }
        catch (Exception e) { LogDebug($"国服频道: 插件实例探测失败: {e.Message}"); }

        return null;
    }

    private static object? FirstFieldOfType(object owner, string typeName)
    {
        try
        {
            foreach (var field in owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (field.FieldType.Name != typeName) continue;
                var value = field.GetValue(owner);
                if (value != null) return value;
            }
        }
        catch { }
        return null;
    }

    private static object? FindFieldValueOfType(object root, string typeName, int depth)
    {
        var seen  = new HashSet<object>(ReferenceEqualityComparer.Instance) { root };
        var level = new List<object> { root };

        for (var d = 0; d < depth && level.Count > 0; d++)
        {
            var next = new List<object>();
            foreach (var node in level)
            {
                FieldInfo[] fields;
                try { fields = node.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); }
                catch { continue; }

                foreach (var field in fields)
                {
                    object? value;
                    try { value = field.GetValue(node); } catch { continue; }
                    if (value == null || value is string) continue;

                    var type = value.GetType();
                    if (type.IsPrimitive) continue;
                    if (type.Name == typeName) return value;

                    var name = type.Assembly.GetName().Name ?? "";
                    if (name.StartsWith("OmniToolbox") && seen.Add(value) && next.Count < 256) next.Add(value);
                }
            }
            level = next;
        }

        return null;
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
                .GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
            return (T?)get?.Invoke(null, null);
        }
        catch { return null; }
    }
}

