
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
        public bool HelpEnabled { get; set; } = true;
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
    private readonly Dictionary<(long Group, long User), HelpSession> helpSessions = [];
    private sealed record HelpSession(string Query, int Page, long LastReply);
    private List<(string Command, string Description)>? gameHelpCommands;
    private readonly List<MembershipReply> membershipReplies = [];
    private sealed record MembershipReply(object Client, bool Join, long? Group, long Deadline);
    private CancellationTokenSource? senderCts;
    private IChatGui? chatGui;
    private IAddonLifecycle? addonLifecycle;
    private IFramework? framework;
    private IObjectTable? objectTable;

    private bool windowOpen;
    private bool helpWindowOpen;
    private string helpSearch = "", helpCommandText = "";
    private int helpPage;
    private long helpRefreshAt;

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
        if (DalamudServices.PluginInterface != null)
            DalamudServices.PluginInterface.UiBuilder.Draw += DrawHelpPanel;

        senderCts = new CancellationTokenSource();
        _ = Task.Run(() => SenderLoop(senderCts.Token));

        statusText = $"运行中 (群: {(config.GroupIDs.Length > 0 ? "已配置" : "未配置")})";
    }

    protected override void OnDisable()
    {
        helpSessions.Clear();
        gameHelpCommands = null;
        membershipReplies.Clear();
        DetachWorldChat();
        try { if (chatGui != null) chatGui.ChatMessage -= OnChatMessage; } catch { }
        try { addonLifecycle?.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoPopup); } catch { }
        try { if (framework != null) framework.Update -= OnFrameworkUpdate; } catch { }
        try { if (DalamudServices.PluginInterface != null) DalamudServices.PluginInterface.UiBuilder.Draw -= DrawPanel; } catch { }
        try { if (DalamudServices.PluginInterface != null) DalamudServices.PluginInterface.UiBuilder.Draw -= DrawHelpPanel; } catch { }
        helpWindowOpen = false;

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
        AddCommand("/qqworld", new CommandInfo((_, text) => RunOnGameThread(() => SendWorldChat(text))) { HelpMessage = "发送到 Omni 国服频道：/qqworld 消息" });
        AddCommand("/qqworldjoin", new CommandInfo((_, _) => RunOnGameThread(() => ChangeWorldChatMembership(true))) { HelpMessage = "加入 Omni 国服频道" });
        AddCommand("/qqworldleave", new CommandInfo((_, _) => RunOnGameThread(() => ChangeWorldChatMembership(false))) { HelpMessage = "退出 Omni 国服频道" });
        AddCommand("/qqmacro", new CommandInfo((_, text) => RunOnGameThread(() => ExecuteRemoteCommand(text))) { HelpMessage = "执行一条游戏或插件宏指令：/qqmacro /指令 参数" });
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

    private void RunOnGameThread(System.Action action, long? replyGroup = null)
    {
        try
        {
            if (framework != null) { framework.RunOnFrameworkThread(action); return; }
        }
        catch (Exception e) { LogDebug($"RunOnFrameworkThread 派发失败: {e}"); }

        // Never run game APIs on the QQ polling thread if dispatch is unavailable.
        ReplyMembership(replyGroup, "❌ 游戏线程不可用，未执行指令。");
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        var now = Environment.TickCount64;
        CheckMembershipReplies(now);

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
        var initializedGroups = new HashSet<long>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);

                if (!config.QQToGameEnabled && !config.HelpEnabled) continue;

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

                        if (!initializedGroups.Contains(gid)) continue;

                        var userID = m.TryGetProperty("user_id", out var uid) && uid.ValueKind == JsonValueKind.Number ? uid.GetInt64() : 0;

                        var botQQ = (config.BotQQ ?? "").Trim();
                        if (botQQ.Length > 0 && userID.ToString() == botQQ) continue;

                        var text = m.TryGetProperty("message", out var mm) ? ExtractQQText(mm) : "";
                        if (config.HelpEnabled && m.TryGetProperty("message", out var helpMessage) && IsBotMention(helpMessage, botQQ))
                        {
                            var helpText = text;
                            RunOnGameThread(() => HandleQQHelp(gid, userID, helpText), gid);
                            continue;
                        }
                        if (!config.QQToGameEnabled) continue;
                        var trig = (config.TriggerPrefix ?? "").Trim();
                        if (trig.Length == 0) continue;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        if (!text.StartsWith(trig)) continue;
                        text = text[trig.Length..].TrimStart();

                        if (TryQueueRemoteOperation(text, gid)) continue;

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
                    initializedGroups.Add(gid);
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

    private static bool IsBotMention(JsonElement message, string botQQ)
    {
        if (!long.TryParse(botQQ, out var bot) || bot <= 0) return false;
        if (message.ValueKind == JsonValueKind.String)
            return Regex.Matches(message.GetString() ?? "", @"\[CQ:at,qq=(\d+)(?:,[^\]]*)?\]")
                .Any(m => long.TryParse(m.Groups[1].Value, out var id) && id == bot);
        if (message.ValueKind != JsonValueKind.Array) return false;
        foreach (var segment in message.EnumerateArray())
            if (segment.ValueKind == JsonValueKind.Object && segment.TryGetProperty("type", out var type) && type.GetString() == "at" &&
                segment.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("qq", out var qq) &&
                long.TryParse(qq.ToString(), out var id) && id == bot) return true;
        return false;
    }

    private void HandleQQHelp(long group, long user, string text)
    {
        if (!config.HelpEnabled || senderCts == null || senderCts.IsCancellationRequested) return;
        var key = (group, user);
        long now = Environment.TickCount64;
        helpSessions.TryGetValue(key, out var session);
        if (session != null && now - session.LastReply < 2000) return;
        foreach (var expired in helpSessions.Where(e => now - e.Value.LastReply > 600000).Select(e => e.Key).ToList()) helpSessions.Remove(expired);
        if (helpSessions.Count > 500) helpSessions.Clear();
        string request = text.Trim();
        bool recognized = request == "帮助" || request.Equals("help", StringComparison.OrdinalIgnoreCase) ||
            request == "菜单" || request is "1" or "2" or "3" or "4" or "5" or "6" or "下一页" or "上一页" ||
            request.StartsWith("搜索 ") || request.StartsWith("搜索　");
        if (session != null && !recognized) return;
        string query = "";
        int page = 0;
        string prefix = string.IsNullOrWhiteSpace(config.TriggerPrefix) ? "#" : config.TriggerPrefix;
        string result;
        if (request == "3" || request.StartsWith("搜索 ") || request.StartsWith("搜索　") || request == "下一页" || request == "上一页")
        {
            query = request.StartsWith("搜索") ? request[2..].Trim() : request == "3" ? "" : session?.Query ?? "";
            page = request == "下一页" ? (session?.Page ?? 0) + 1 : request == "上一页" ? Math.Max(0, (session?.Page ?? 0) - 1) : 0;
            try { result = BuildCommandHelp(query, ref page); }
            catch (Exception e) { result = "指令目录暂不可用：" + e.Message; }
        }
        else result = GetHelpText(request, true);
        helpSessions[key] = new HelpSession(query, page, now);
        if (user <= 0) return;
        var segments = new JsonArray
        {
            new JsonObject { ["type"] = "at", ["data"] = new JsonObject { ["qq"] = user.ToString() } },
            new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = "\n" + result } }
        };
        sendQueue.Enqueue(new JsonObject { ["group_id"] = group, ["message"] = segments }.ToJsonString());
    }

    private string GetHelpText(string request, bool fromQQ = false)
    {
        string prefix = string.IsNullOrWhiteSpace(config.TriggerPrefix) ? "#" : config.TriggerPrefix;
        return request switch
        {
            "1" => "【安装与配置】\n1. 下载并安装 NapCatQQ Desktop，在程序中创建并启动机器人、登录 QQ：\nhttps://github.com/NapNeko/NapCatQQ-Desktop\n安装包在仓库 Releases 中下载。\n2. 在机器人网络配置中启用 OneBot HTTP 服务，记下地址、端口与 Token。\n3. OMNI 在线模块中安装并启用 ChatToQQ，填入 OneBot HTTP 地址、相同 Token、QQ群号和机器人 QQ号。\n4. 点击发送测试消息，打开群内帮助；需要发送游戏消息时再开启双向同步。\n地址示例：http://127.0.0.1:3000（须与实际 HTTP 服务端口一致，并非 WebUI 地址）。",
            "2" => $"【频道指令】\n{prefix}小队 消息 → 小队\n{prefix}团队 消息 → 团队\n{prefix}说话 消息 → 附近\n{prefix}喊话 消息 → 区域喊话\n{prefix}呼喊 消息 → 区域呼喊\n{prefix}部队 消息 → 部队\n{prefix}新人 消息 → 新人频道\n{prefix}通讯贝1 消息 / {prefix}跨服贝1 消息（编号 1～8）\n{prefix}悄悄话 姓名@服务器 消息\n{prefix}国服 消息 → OMNI 国服频道\n只写 {prefix}消息 时使用设置中的默认频道。",
            "4" => $"【国服频道】\n先启用 OMNI 聊天框优化。\n{prefix}加入国服：连接国服频道\n{prefix}退出国服：退出频道\n{prefix}国服 消息：发送消息\n加入、退出和发送结果会回复到本群。\n游戏宏：/qqworldjoin、/qqworldleave、/qqworld 消息。",
            "5" => BuildConnectionHelp(fromQQ),
            "6" => "【常见问题】\n不回复帮助：检查机器人 QQ号、群号、群内帮助开关及 NapCatQQ Desktop 的 OneBot HTTP 服务。\n不能向游戏发送：打开双向同步，确认触发符号正确、角色已登录。\n国服发送失败：启用聊天框优化，先加入频道并等待连接成功。\n宏未找到：启用提供该指令的插件或模块，再搜索指令。\n帮助和转发依赖游戏内 ChatToQQ 运行；模块停用或游戏关闭后不会回复。",
            _ => "【ChatToQQ 帮助】\n1 · 安装与配置\n2 · 聊天频道指令\n3 · 当前游戏与插件宏指令\n4 · 国服频道操作\n5 · 连接状态\n6 · 常见问题\n\n@我 + 数字 查看分项\n@我 搜索 关键词：搜索指令或说明\n@我 下一页 / 上一页：翻页\n@我 帮助：返回菜单"
        };
    }

    private string BuildConnectionHelp(bool fromQQ = false)
    {
        string world;
        try
        {
            var client = FindOmniWorldChatClient();
            world = client == null ? "聊天框优化未就绪" : client.GetType().GetProperty("Connected")?.GetValue(client) is true ? "已连接" :
                client.GetType().GetProperty("Connecting")?.GetValue(client) is true ? "连接中" : "未连接";
        }
        catch { world = "无法读取"; }
        return $"【当前状态】\nQQ 接收：{(fromQQ ? "本群消息已收到" : "请在群内 @机器人 5 检查连接")}\n游戏角色：{(objectTable?.LocalPlayer == null ? "未登录" : "已登录")}\n双向同步：{(config.QQToGameEnabled ? "开启" : "关闭")}\n国服频道：{world}\n触发符号：{config.TriggerPrefix}\n默认频道：{config.GameChannel}";
    }

#pragma warning disable PendingExcelSchema
    private string BuildCommandHelp(string query, ref int page)
    {
        if (gameHelpCommands == null)
        {
            var loaded = new List<(string Command, string Description)>();
            foreach (var row in DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Experimental.TextCommand>())
                foreach (var alias in new[] { row.Command.ExtractText(), row.ShortCommand.ExtractText(), row.Alias.ExtractText(), row.ShortAlias.ExtractText() })
                    if (alias.Trim().StartsWith('/')) loaded.Add((alias.Trim(), row.Description.ExtractText().Trim()));
            gameHelpCommands = loaded;
        }
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in gameHelpCommands) all[entry.Command] = entry.Description;
        foreach (var entry in commandManager!.Commands.ToArray()) all[entry.Key] = entry.Value.HelpMessage ?? "";
        var matches = all.Where(e => query.Length == 0 || e.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();
        int total = Math.Max(1, (matches.Count + 7) / 8);
        page = Math.Clamp(page, 0, total - 1);
        var result = new StringBuilder($"【宏指令目录】{matches.Count} 条 · {page + 1}/{total} 页\n");
        foreach (var entry in matches.Skip(page * 8).Take(8))
        {
            string description = entry.Value.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (description.Length == 0) description = "未提供说明";
            if (description.Length > 90) description = description[..90] + "…";
            result.AppendLine(entry.Key + "（" + description + "）");
        }
        if (matches.Count == 0) result.AppendLine("没有匹配的指令。");
        result.AppendLine("@我 搜索 关键词 · @我 下一页 / 上一页");
        result.Append("执行：" + config.TriggerPrefix + "宏 /指令 参数（须开启双向同步）");
        return result.ToString();
    }
#pragma warning restore PendingExcelSchema

    private bool TryQueueRemoteOperation(string text, long? replyGroup = null)
    {
        var split = text.IndexOfAny([' ', '　']);
        var verb = split < 0 ? text : text[..split];
        var body = split < 0 ? "" : text[(split + 1)..].Trim();
        if (verb == "加入国服") { RunOnGameThread(() => ChangeWorldChatMembership(true, replyGroup), replyGroup); return true; }
        if (verb == "退出国服") { RunOnGameThread(() => ChangeWorldChatMembership(false, replyGroup), replyGroup); return true; }
        if (verb == "国服") { RunOnGameThread(() => SendWorldChat(body, replyGroup), replyGroup); return true; }
        if (verb == "宏") { RunOnGameThread(() => ExecuteRemoteCommand(body, replyGroup), replyGroup); return true; }
        // Slash commands after the trigger prefix also bypass the normal chat channel.
        if (text.StartsWith('/')) { RunOnGameThread(() => ExecuteRemoteCommand(text, replyGroup), replyGroup); return true; }
        return false;
    }

    private object RequireWorldChatClient()
    {
        // Resolve each operation again: the host may reload its chat module.
        return FindOmniWorldChatClient() ?? throw new InvalidOperationException("请先启用 Omni 的聊天框优化模块。");
    }

    private void ChangeWorldChatMembership(bool join, long? replyGroup = null)
    {
        try
        {
            var client = RequireWorldChatClient();
            var name = join ? "Join" : "Leave";
            var method = client.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                ?? throw new InvalidOperationException("当前 Omni 版本没有 " + name + " 接口。");
            method.Invoke(client, null);
            foreach (var pending in membershipReplies.Where(p => p.Join != join).ToList())
            {
                ReplyMembership(pending.Group, "此前的国服频道操作已被新操作取消。");
                membershipReplies.Remove(pending);
            }
            membershipReplies.Add(new MembershipReply(client, join, replyGroup, Environment.TickCount64 + 30000));
            ReportOperation(join ? "正在加入国服频道…" : "正在退出国服频道…");
            CheckMembershipReplies(Environment.TickCount64);
        }
        catch (Exception e) { ReplyMembership(replyGroup, "国服频道操作失败：" + (e.InnerException ?? e).Message); }
    }

    private void CheckMembershipReplies(long now)
    {
        foreach (var pending in membershipReplies.ToList())
        {
            try
            {
                var type = pending.Client.GetType();
                bool connected = type.GetProperty("Connected")?.GetValue(pending.Client) is true;
                bool connecting = type.GetProperty("Connecting")?.GetValue(pending.Client) is true;
                if (pending.Join ? connected : !connected && !connecting)
                {
                    ReplyMembership(pending.Group, pending.Join ? "✅ 已成功加入国服频道。" : "✅ 已成功退出国服频道。");
                    membershipReplies.Remove(pending);
                }
                else if (now >= pending.Deadline)
                {
                    ReplyMembership(pending.Group, pending.Join ? "❌ 加入国服频道超时，请检查 Omni 连接状态。" : "❌ 退出国服频道超时，请检查 Omni 连接状态。");
                    membershipReplies.Remove(pending);
                }
            }
            catch (Exception e)
            {
                ReplyMembership(pending.Group, "❌ 无法确认国服频道状态：" + e.Message);
                membershipReplies.Remove(pending);
            }
        }
    }

    private void ReplyMembership(long? group, string text)
    {
        ReportOperation(text);
        if (group.HasValue)
            sendQueue.Enqueue(JsonSerializer.Serialize(new { group_id = group.Value, message = text }));
        else Enqueue(null, text);
    }

    private void SendWorldChat(string text, long? replyGroup = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("请输入要发送的消息。");
            var client = RequireWorldChatClient();
            if (client.GetType().GetProperty("Connected")?.GetValue(client) is not true)
                throw new InvalidOperationException("国服频道尚未连接，请先加入并等待连接完成。");
            var method = client.GetType().GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.Public, null, [typeof(string)], null)
                ?? throw new InvalidOperationException("当前 Omni 版本没有发送接口。");
            if (method.Invoke(client, [text.Trim()]) is not Task<bool> task)
                throw new InvalidOperationException("国服频道发送接口不匹配。");
            _ = ObserveWorldChatSend(task, replyGroup);
        }
        catch (Exception e) { ReplyMembership(replyGroup, "❌ 国服发送失败：" + (e.InnerException ?? e).Message); }
    }

    private async Task ObserveWorldChatSend(Task<bool> task, long? replyGroup)
    {
        try { ReplyMembership(replyGroup, await task ? "✅ 国服消息已发送。" : "❌ 国服消息未发送，请检查连接状态与发送频率。"); }
        catch (Exception e) { ReplyMembership(replyGroup, "❌ 国服发送失败：" + e.Message); }
    }

    private void ExecuteRemoteCommand(string text, long? replyGroup = null)
    {
        try
        {
            if (!text.StartsWith('/') || text.Contains('\n') || text.Contains('\r') || Encoding.UTF8.GetByteCount(text) > 500)
                throw new InvalidOperationException("宏指令须为一条以 / 开头的指令，且不超过 500 字节。");
            if (objectTable?.LocalPlayer == null) throw new InvalidOperationException("请先进入游戏。");
            var command = text.Split([' ', '\t', '　'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (command[0].Equals("/qqworldjoin", StringComparison.OrdinalIgnoreCase)) { ChangeWorldChatMembership(true, replyGroup); return; }
            if (command[0].Equals("/qqworldleave", StringComparison.OrdinalIgnoreCase)) { ChangeWorldChatMembership(false, replyGroup); return; }
            if (command[0].Equals("/qqworld", StringComparison.OrdinalIgnoreCase)) { SendWorldChat(command.Length == 2 ? command[1] : "", replyGroup); return; }
            if (command[0].Equals("/qqmacro", StringComparison.OrdinalIgnoreCase)) { ExecuteRemoteCommand(command.Length == 2 ? command[1].Trim() : "", replyGroup); return; }
            // Plugin commands must go through Dalamud; game commands use the native chat input path.
            if (commandManager?.ProcessCommand(text) == true)
                ReplyMembership(replyGroup, "✅ 已执行插件宏指令：" + text + "（处理器已受理）");
            else if (IsNativeCommand(command[0]))
            {
                ChatManager.Instance().SendMessage(text);
                ReplyMembership(replyGroup, "✅ 已提交游戏宏指令：" + text + "（具体执行结果以游戏提示为准）");
            }
            else ReplyMembership(replyGroup, "❌ 未找到宏指令：" + command[0]);
        }
        catch (Exception e) { ReplyMembership(replyGroup, "❌ 宏指令执行失败：" + e.Message); }
    }

    private HashSet<string>? nativeCommands;
#pragma warning disable PendingExcelSchema
    private bool IsNativeCommand(string command)
    {
        if (nativeCommands == null)
        {
            var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sheet = DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Experimental.TextCommand>();
            foreach (var row in sheet)
                foreach (var alias in new[] { row.Command.ExtractText(), row.ShortCommand.ExtractText(), row.Alias.ExtractText(), row.ShortAlias.ExtractText() })
                    if (!string.IsNullOrWhiteSpace(alias)) commands.Add(alias.Trim());
            if (commands.Count == 0) throw new InvalidOperationException("游戏指令表暂不可用，未执行指令。");
            nativeCommands = commands;
        }
        return nativeCommands.Contains(command);
    }
#pragma warning restore PendingExcelSchema

    private void ReportOperation(string text)
    {
        statusText = text;
        LogDebug(text);
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
                statusText = $"发送失败: HTTP {(int)resp.StatusCode} (检查 NapCatQQ Desktop 机器人是否运行/端口/token)";
        }
        catch (Exception e)
        {
            statusText = $"发送失败: {e.Message} (检查 NapCatQQ Desktop 机器人是否运行/端口)";
        }
    }

    public override bool HasSettings => true;

    public override bool DrawSettings()
    {
        if (ImGui.Button(windowOpen ? "关闭独立面板" : "打开独立面板"))
            TogglePanel();
        ImGui.SameLine();
        if (ImGui.Button("说明")) helpWindowOpen = true;

        return false;
    }

    private void DrawHelpPanel()
    {
        if (!helpWindowOpen) return;
        using var font = OmniFonts.GetUIFont().Push();
        using var theme = new ComicStyleScope();
        float scale = OmniTheme.ScaleValue;
        ImGui.SetNextWindowSize(new Vector2(740, 620) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(480, 340) * scale, new Vector2(float.MaxValue));
        bool visible = ImGui.Begin("ChatToQQ 使用说明###OmniChatToQQHelp", ref helpWindowOpen);
        try
        {
            if (!visible) return;
            foreach (var section in new[] { ("1", "安装与配置"), ("2", "聊天频道指令"), ("3", "当前游戏与插件宏指令"),
                ("4", "国服频道操作"), ("5", "连接状态"), ("6", "常见问题") })
            {
                if (!ImGui.CollapsingHeader(section.Item2 + "###help" + section.Item1, ImGuiTreeNodeFlags.DefaultOpen)) continue;
                if (section.Item1 != "3")
                {
                    string content = GetHelpText(section.Item1);
                    const string downloadLink = "https://github.com/NapNeko/NapCatQQ-Desktop";
                    if (section.Item1 == "1")
                    {
                        int linkAt = content.IndexOf(downloadLink, StringComparison.Ordinal);
                        if (linkAt >= 0)
                        {
                            ImGui.TextWrapped(content[..linkAt].TrimEnd());
                            ImGui.TextUnformatted(downloadLink);
                            if (ImGui.GetContentRegionAvail().X > ImGui.CalcTextSize(downloadLink).X + ImGui.CalcTextSize("复制链接").X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X)
                                ImGui.SameLine();
                            if (ImGui.Button("复制链接")) ImGui.SetClipboardText(downloadLink);
                            ImGui.TextWrapped(content[(linkAt + downloadLink.Length)..].TrimStart());
                        }
                        else ImGui.TextWrapped(content);
                    }
                    else ImGui.TextWrapped(content);
                    ImGui.Spacing();
                    continue;
                }
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##helpSearch", "搜索指令或说明…", ref helpSearch, 256)) { helpPage = 0; helpRefreshAt = 0; }
                ImGui.BeginDisabled(helpPage == 0);
                if (ImGui.Button("上一页")) { helpPage--; helpRefreshAt = 0; }
                ImGui.EndDisabled(); ImGui.SameLine();
                if (ImGui.Button("下一页")) { helpPage++; helpRefreshAt = 0; }
                if (Environment.TickCount64 >= helpRefreshAt)
                {
                    try { helpCommandText = BuildCommandHelp(helpSearch.Trim(), ref helpPage); }
                    catch (Exception e) { helpCommandText = "指令目录暂不可用：" + e.Message; }
                    helpRefreshAt = Environment.TickCount64 + 1000;
                }
                ImGui.TextWrapped(helpCommandText);
                ImGui.Spacing();
            }
        }
        finally { ImGui.End(); }
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
        var helpEnabled = config.HelpEnabled;
        if (ImGui.Checkbox("群内 @机器人帮助菜单", ref helpEnabled)) { config.HelpEnabled = helpEnabled; SaveOwnConfig(); }

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

        if (config.QQToGameEnabled || config.HelpEnabled)
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

