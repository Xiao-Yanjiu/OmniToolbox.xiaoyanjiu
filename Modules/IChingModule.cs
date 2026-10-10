#nullable enable
// IChing -> Omni local module, 2026-10-10.
// Adapted from the user-supplied IChing fragments; original authors retain their rights.
// Historical signatures are NOT verified against the running game. All features default OFF.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI.Theme;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace OmniToolbox.LocalModules;

public sealed unsafe class IChingModule : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "IChing",
        Description = "",
        Category = ModuleCategory.Combat,
        Author = "小烟酒（基于 IChing 原作者源码）",
        Commands = [new("显示调试面板", "/omni IChingModule ui"),
            new("鼠标附近最近敌人", "/omni IChingModule nearest"),
            new("鼠标附近敌群中心", "/omni IChingModule most")],
    };
    private IChingModuleConfig config = new();
    private IChingModuleServices services = null!;
    private readonly Dictionary<string, List<IDisposable>> resources = new();
    private readonly Dictionary<string, string> errors = new();
    private List<IDisposable>? building;
    private List<System.Action>? enabling;
    private System.Action? saveHostConfig;
    private bool running, panel;
    private int actionInput, statusInput;
    private Vector3 destination;
    private float facing, scale = 1;
    private string notice = "尚未执行操作";
    private CanAttackDelegate? canAttack;
    private readonly List<RadarEntry> radar = new();
    private long nextScan;
    public override bool HasSettings => true;

    protected override void OnEnable()
    {
        services = DalamudServices.PluginInterface.Create<IChingModuleServices>()
            ?? throw new InvalidOperationException("无法取得 Dalamud 服务。");
        config ??= new();
        config.BlockedTimelines ??= [];
        config.TargetStatuses ??= [];
        config.FollowedPlayers ??= [];
        saveHostConfig = typeof(ModuleBase).GetProperty("SaveHostConfig", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(this) as System.Action;
        running = true;
        try
        {
            services.Framework.Update += Update;
            DalamudServices.PluginInterface.UiBuilder.Draw += Draw;
            SyncFeatures();
        }
        catch { Stop(); throw; }
    }
    protected override void OnDisable() => Stop();
    protected override void OnDispose() => Stop();
    private void Stop()
    {
        running = false;
        if (services != null) services.Framework.Update -= Update;
        if (DalamudServices.PluginInterface != null)
            DalamudServices.PluginInterface.UiBuilder.Draw -= Draw;
        foreach (var key in resources.Keys.ToArray()) Release(key);
        canAttack = null;
        radar.Clear();
        errors.Clear();
        panel = false;
        saveHostConfig = null;
    }
    private void Release(string key)
    {
        if (!resources.Remove(key, out var list)) return;
        for (int i = list.Count - 1; i >= 0; i--)
            try { list[i].Dispose(); }
            catch (Exception e) { DalamudServices.PluginLog.Error(e, $"IChing 释放 {key} 失败"); }
    }
    private void Feature(string key, bool requested, System.Action create)
    {
        if (!requested) { Release(key); errors.Remove(key); return; }
        if (resources.ContainsKey(key) || errors.ContainsKey(key)) return;
        var list = new List<IDisposable>();
        building = list;
        enabling = new();
        try
        {
            create();
            foreach (var enable in enabling) enable();
            resources.Add(key, list);
        }
        catch (Exception e)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                try { list[i].Dispose(); } catch { }
            errors[key] = e.Message;
            DalamudServices.PluginLog.Error(e, $"IChing 初始化 {key} 失败");
        }
        finally { building = null; enabling = null; }
    }
    // CALL-site patterns begin with E8: resolve rel32 instead of hooking the CALL instruction.
    private nint Find(string signature)
    {
        var address = services.Scanner.ScanText(signature);
        if (signature.StartsWith("E8 ", StringComparison.OrdinalIgnoreCase))
            address = address + 5 + Marshal.ReadInt32(address + 1);
        if (address == 0) throw new InvalidOperationException("签名没有匹配地址。");
        return address;
    }
    private Hook<T> Hook<T>(string signature, T detour) where T : Delegate
    {
        var hook = services.Interop.HookFromAddress(Find(signature), detour);
        building!.Add(hook); // register before Enable: failed initialization still releases it
        enabling!.Add(hook.Enable); // all delegate fields assigned before any detour can run
        return hook;
    }
    private bool Active => running && services.Objects.LocalPlayer != null;

    private void SyncFeatures()
    {
        Feature("移速", config.Speed, () =>
        {
            speedHook = Hook<SpeedDelegate>(config.SpeedSignature, Speed);
            accelerationHook = Hook<AccelerationDelegate>("40 ?? 48 ?? ?? ?? 80 79 ?? ?? 48 ?? ?? 0F 84 ?? ?? ?? ?? 48 89 7C 24 ?? 48 ?? ?? ??", Accelerate);
        });
        Feature("强制移动", config.MovePermission, () => moveHook = Hook<MoveDelegate>(config.MoveSignature, Move));
        Feature("防击退", config.AntiKnock, () => knockHook = Hook<KnockDelegate>("48 ?? ?? 57 48 ?? ?? ?? ?? ?? ?? 0F 29 70 ?? 0F ?? ??", Knock));
        Feature("掉落无伤", config.NoFallDamage, () => fallHook = Hook<FallDelegate>("48 89 5C 24 ?? 57 48 ?? ?? ?? 8B ?? 48 ?? ?? 33 ?? E8 ?? ?? ?? ?? 84 ??", Fall));
        Feature("无掉落", config.NoFall, () => noFallHook = Hook<NoFallDelegate>("E8 ?? ?? ?? ?? 8B ?? 85 ?? 0F 88 ?? ?? ?? ?? 48 ?? ?? ?? F3 ?? ?? ?? ?? ?? ?? ??", NoFall));
        Feature("技能距离", config.ActionRange, () => rangeHook = Hook<RangeDelegate>("48 89 5C 24 ?? 57 48 ?? ?? ?? 48 ?? ?? ?? ?? ?? ?? 8B ?? 0F 29 74 24 20", Range));
        Feature("目标圈", config.ActorRadius, () => radiusHook = Hook<RadiusDelegate>("E8 ?? ?? ?? ?? F3 ?? ?? ?? 48 ?? ?? ?? ?? F3 ?? ?? ?? ?? F3 ?? ?? ?? F3 ?? ?? ?? ?? F3 0F 11 74 24 ??", Radius));
        Feature("后摇移动", config.NoBackswing, () => backswingHook = Hook<BackswingDelegate>("48 ?? ?? ?? 48 ?? ?? ?? 45 ?? ?? 33 ?? E8 ?? ?? ?? ?? 84 ?? 74 ??", Backswing));
        Feature("突进无位移", config.NoActionMove, () => actionMoveHook = Hook<ActionMoveDelegate>("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F1 0F 29 74 24 ?? 48 8B 89 ?? ?? ?? ?? 0F 28 F3", ActionMove));
        Feature("复唱缩减", config.Recast, () =>
        {
            recastHook = Hook<RecastDelegate>("48 89 5C 24 ?? 48 89 74 24 ?? 55 57 41 ?? 41 ?? 41 ?? 48 ?? ?? 48 ?? ?? ?? 4C ?? ?? ?? ?? ?? ??", Recast);
            syncHook = Hook<SyncDelegate>("40 ?? 48 ?? ?? ?? 0F 29 74 24 ?? 41 ?? ?? 0F ?? ?? E8 ?? ?? ?? ??", SyncRecast);
        });
        Feature("咏唱缩减", config.Cast, () =>
        {
            castHook = Hook<CastDelegate>("E8 ?? ?? ?? ?? 45 ?? ?? 33 ?? 48 ?? ?? 66 ?? ?? ??", Cast);
            // Resolve the original global before creating the second hook.
            castTime = (float*)services.Scanner.GetStaticAddressFromSig("F3 44 0F 2C C0 BA ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? F3 44 0F 10 1D", 0x12);
            if (castTime == null) throw new InvalidOperationException("CastTimeCurrent 地址为空。");
            castInfoHook = Hook<CastInfoDelegate>("48 89 5C 24 ?? 57 48 83 EC ?? 48 8B F9 0F 29 74 24 ?? 0F B6 49", CastInfo);
        });
        Feature("可攻击目标判定", config.Radar, EnsureCanAttack);
    }

    private delegate float SpeedDelegate(long a1);
    private delegate void AccelerationDelegate(long a1);
    private delegate long MoveDelegate(long a1, uint a2, int a3, int a4);
    private delegate long KnockDelegate(long actor, float angle, float distance, float time, byte a5, long a6);
    private delegate nint FallDelegate(nuint a1, uint flag);
    private delegate long NoFallDelegate(long a1, long a2, long a3);
    private delegate float RangeDelegate(uint action);
    private delegate float RadiusDelegate(ulong a1, byte a2);
    private delegate long BackswingDelegate(long a1);
    private delegate ulong ActionMoveDelegate(ulong a1, byte a2, ulong a3, float a4, nint a5);
    private delegate long RecastDelegate(int type, int key, char extra);
    private delegate long SyncDelegate(long a1, long type, int key, float maximum);
    private delegate int CastDelegate(ActionType type, uint id, bool apply, byte* proc);
    private delegate uint CastInfoDelegate(nint data, uint id, float total, float start);
    private delegate int CanAttackDelegate(int arg, nint address);
    private Hook<SpeedDelegate> speedHook = null!;
    private Hook<AccelerationDelegate> accelerationHook = null!;
    private Hook<MoveDelegate> moveHook = null!;
    private Hook<KnockDelegate> knockHook = null!;
    private Hook<FallDelegate> fallHook = null!;
    private Hook<NoFallDelegate> noFallHook = null!;
    private Hook<RangeDelegate> rangeHook = null!;
    private Hook<RadiusDelegate> radiusHook = null!;
    private Hook<BackswingDelegate> backswingHook = null!;
    private Hook<ActionMoveDelegate> actionMoveHook = null!;
    private Hook<RecastDelegate> recastHook = null!;
    private Hook<SyncDelegate> syncHook = null!;
    private Hook<CastDelegate> castHook = null!;
    private Hook<CastInfoDelegate> castInfoHook = null!;
    private float* castTime;
    private static readonly HashSet<uint> SpeedStatuses = [14,67,181,240,436,484,502,623,674,709,1073,1107,1114,1141,1147,1259,1344,1394,1595,1790,1796,1935,2099,2158,2391,2551,2662,2731,3167,3284,3472,3473,3548,3943,3948,4334,4341];
    private float Speed(long a1)
    {
        var original = speedHook.Original(a1);
        if (!Active) return original;
        var player = services.Objects.LocalPlayer!;
        if ((services.Client.IsPvP || services.Condition[ConditionFlag.InDeepDungeon]) && player.StatusList.Any(s => SpeedStatuses.Contains(s.StatusId))) return original;
        return original + Math.Clamp(config.SpeedAdd, 0, 20);
    }
    private void Accelerate(long a1)
    {
        // Temporary write: restore the original acceleration after the original function returns.
        if (!Active || !config.MaxAcceleration || a1 == 0) { accelerationHook.Original(a1); return; }
        float* value = (float*)(a1 + 0x44);
        var saved = *value;
        try { *value = 100; accelerationHook.Original(a1); }
        finally { *value = saved; }
    }
    private long Move(long a1, uint a2, int a3, int a4) => Active && (a2 is 96 or 97 or 98 or 99 or 0x3E9 or 0x3EE or 0x3EF or 0x3F0) ? 1 : moveHook.Original(a1, a2, a3, a4);
    private long Knock(long actor, float angle, float distance, float time, byte a5, long a6)
    {
        // Only alter the local player's knockback, not every actor's animation.
        if (Active && actor == (long)services.Objects.LocalPlayer!.Address)
        { distance = Math.Clamp(config.KnockDistance, 0, 100); time = Math.Clamp(config.KnockTime, 0, 10); }
        return knockHook.Original(actor, angle, distance, time, a5, a6);
    }
    private nint Fall(nuint a1, uint flag)
    {
        if (Active && (flag & 0b11100000000) != 0) flag = (flag & ~0b11100000000u) | 2;
        return fallHook.Original(a1, flag);
    }
    private long NoFall(long a1, long a2, long a3) => Active ? -1 : noFallHook.Original(a1, a2, a3);
    private float Range(uint id)
    {
        var original = rangeHook.Original(id);
        if (!Active || id is 34675 or 3573 or 2262 or 29513) return original;
        var row = services.Data.GetExcelSheet<LuminaAction>().GetRowOrDefault(id);
        if (row == null) return original;
        var add = Math.Clamp(config.RangeAdd, 0, 40);
        if (id != 29066 && !(row.Value.CanTargetHostile && row.Value.AffectsPosition) && add >= 2) add = 3;
        return original + add;
    }
    private float Radius(ulong a1, byte a2)
    {
        var original = radiusHook.Original(a1, a2);
        return Active ? Math.Max(original + Math.Clamp(config.RadiusAdd, 0, 5), 0) : original;
    }
    private long Backswing(long a1) => Active ? a1 : backswingHook.Original(a1);
    private ulong ActionMove(ulong a1, byte a2, ulong a3, float a4, nint a5)
    {
        if (Active && a5 != 0)
        {
            bool listed = config.BlockedTimelines.Contains((uint)Marshal.ReadInt32(a5));
            if (config.BlockAllExceptListed ? !listed : listed) return 0;
        }
        return actionMoveHook.Original(a1, a2, a3, a4, a5);
    }
    private bool Mudra(int key) => config.Mudra && key is 18805 or 18806 or 18807 or 2259 or 2261 or 2263;
    private long Recast(int type, int key, char extra)
    {
        var original = recastHook.Original(type, key, extra);
        if (!Active || type != 1 || original == 0) return original;
        return Mudra(key) ? 0 : Math.Max(original - (long)(Math.Clamp(config.RecastSeconds, 0, 60) * 1000), 0);
    }
    private long SyncRecast(long a1, long type, int key, float maximum)
    {
        if (Active && type == 57 && maximum != 0)
            maximum = Mudra(key) ? 0 : Math.Max(maximum - Math.Clamp(config.RecastSeconds, 0, 60), 0);
        return syncHook.Original(a1, type, key, maximum);
    }
    private int Cast(ActionType type, uint id, bool apply, byte* proc)
    {
        var original = castHook.Original(type, id, apply, proc);
        return Active ? Math.Max(original - (int)(Math.Clamp(config.CastSeconds, 0, 10) * 1000), 0) : original;
    }
    private uint CastInfo(nint data, uint id, float total, float start)
    {
        if (Active && data != 0 && *(uint*)(data + 4) == id && castTime != null)
        { total = Math.Max(total - Math.Clamp(config.CastSeconds, 0, 10), 0); *castTime = total; }
        return castInfoHook.Original(data, id, total, start);
    }

    private void EnsureCanAttack() => canAttack ??= Marshal.GetDelegateForFunctionPointer<CanAttackDelegate>(Find("48 89 5C 24 ?? 57 48 83 EC 20 48 8B DA 8B F9 E8 ?? ?? ?? ?? 4C 8B C3"));
    private void Update(IFramework framework)
    {
        if (!Active) { radar.Clear(); return; }
        if (config.PvpAnimationLock && services.Client.IsPvP)
        {
            var manager = ActionManager.Instance();
            if (manager != null) manager->AnimationLock = Math.Min(manager->AnimationLock, Math.Clamp(config.AnimationLock, 0, 1));
        }
        if (config.ClearTarget && services.Targets.Target is IBattleChara target && target.StatusList.Any(s => config.TargetStatuses.Contains(s.StatusId))) services.Targets.Target = null;
        if (Environment.TickCount64 < nextScan) return;
        nextScan = Environment.TickCount64 + 150;
        radar.Clear();
        if (!config.Radar || !services.Client.IsPvP || canAttack == null || errors.ContainsKey("可攻击目标判定")) return;
        var player = services.Objects.LocalPlayer!;
        foreach (var obj in services.Objects)
        {
            if (obj is not IPlayerCharacter pc || pc.GameObjectId == player.GameObjectId || !pc.IsTargetable || pc.CurrentHp == 0) continue;
            var distance = Vector3.Distance(player.Position, pc.Position);
            if (distance > Math.Clamp(config.RadarDistance, 1, 500) || canAttack(142, pc.Address) != 1) continue;
            uint battleStatus = 0;
            foreach (var status in pc.StatusList) if (status.StatusId is >= 2131 and <= 2135) battleStatus = status.StatusId;
            radar.Add(new(pc.Name.ToString(), pc.Position, distance, pc.ClassJob.RowId, battleStatus, config.FollowedPlayers.Contains(pc.Name.ToString())));
        }
    }
    private sealed record RadarEntry(string Name, Vector3 Position, float Distance, uint Job, uint BattleStatus, bool Followed);
    private void Draw()
    {
        if (!running) return;
        if (panel)
        {
            ImGui.SetNextWindowSize(new Vector2(680, 720), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(430, 460), new Vector2(float.MaxValue));
            ImGui.PushStyleColor(ImGuiCol.WindowBg, OmniTheme.Tokens.Background);
            if (ImGui.Begin("IChing###IChingModule", ref panel) && DrawSettings())
            {
                try
                {
                    if (saveHostConfig != null) saveHostConfig();
                    else notice = "此宿主未提供面板保存入口；请在 Omni 模块设置里修改以保存配置。";
                }
                catch (Exception e) { notice = $"配置保存失败：{e.Message}"; }
            }
            ImGui.End();
            ImGui.PopStyleColor();
        }
        if (!Active || !config.Radar || !services.Client.IsPvP) return;
        var draw = ImGui.GetBackgroundDrawList();
        var viewport = ImGui.GetMainViewport();
        var origin = viewport.Pos + new Vector2(viewport.Size.X / 2, viewport.Size.Y);
        if (services.Gui.WorldToScreen(services.Objects.LocalPlayer!.Position, out var screen)) origin = screen;
        foreach (var entry in radar)
        {
            if (!services.Gui.WorldToScreen(entry.Position, out var p)) continue;
            uint color = entry.Followed ? 0xFF40FF40u : 0xFF4080FFu;
            draw.AddCircleFilled(p, 3, color);
            if (config.RadarLines || entry.Followed) draw.AddLine(origin, p, color, 1);
            draw.AddText(p + new Vector2(0, 22), color, $"{entry.Name}  {entry.Distance:F0}m");
            if (config.JobIcons) Icon(62000 + entry.Job, p, new Vector2(20));
            if (config.BattleIcons && entry.BattleStatus != 0)
            {
                var status = services.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>().GetRowOrDefault(entry.BattleStatus);
                if (status != null) Icon(status.Value.Icon, p - new Vector2(24, 0), new Vector2(24));
            }
        }
    }
    private void Icon(uint id, Vector2 p, Vector2 size)
    {
        var texture = services.Textures.GetFromGameIcon(id).GetWrapOrDefault();
        if (texture != null) ImGui.GetBackgroundDrawList().AddImage(texture.Handle, p, p + size);
    }

    public override bool TryHandleCommand(string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "ui": panel = !panel; return true;
            case "nearest": Run(() => SelectEnemy(false)); return true;
            case "most": Run(() => SelectEnemy(true)); return true;
            default: return false;
        }
    }
    private static float UiScale => Math.Clamp(ImGui.GetTextLineHeight() / 18f, 0.8f, 2.5f);
    private static Vector4 Muted => OmniTheme.Tokens.Text with { W = 0.64f };
    private static void Hint(string text) => ImGui.TextColored(Muted, text);
    private static void WrapHint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
    // Local style only: never changes the host's global theme or font.
    private sealed class PanelStyle : IDisposable
    {
        public PanelStyle()
        {
            var t = OmniTheme.Tokens;
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6 * UiScale);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10 * UiScale);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 7) * UiScale);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10, 8) * UiScale);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12) * UiScale);
            ImGui.PushStyleColor(ImGuiCol.Text, t.Text);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Lerp(t.Background, t.Surface, 0.55f) with { W = 1 });
            ImGui.PushStyleColor(ImGuiCol.Border, t.Border with { W = 0.24f });
            ImGui.PushStyleColor(ImGuiCol.FrameBg, t.Border with { W = 0.10f });
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, t.Border with { W = 0.20f });
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, t.Border with { W = 0.28f });
            ImGui.PushStyleColor(ImGuiCol.Button, t.Border with { W = 0.16f });
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, t.Border with { W = 0.30f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, t.Border with { W = 0.42f });
            ImGui.PushStyleColor(ImGuiCol.SliderGrab, OmniTheme.ControlAccent);
            ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, t.Secondary);
            ImGui.PushStyleColor(ImGuiCol.CheckMark, OmniTheme.ControlAccent);
            ImGui.PushStyleColor(ImGuiCol.Tab, t.Border with { W = 0.08f });
            ImGui.PushStyleColor(ImGuiCol.TabHovered, t.Border with { W = 0.24f });
            ImGui.PushStyleColor(ImGuiCol.TabActive, t.Border with { W = 0.28f });
        }
        public void Dispose() { ImGui.PopStyleColor(15); ImGui.PopStyleVar(5); }
    }
    private sealed class Card : IDisposable
    {
        private static readonly Dictionary<uint, float> Heights = new();
        private readonly uint cacheID;
        private readonly bool visible;
        public Card(string id, string title, string description)
        {
            cacheID = ImGui.GetID(id);
            ImGui.PushID(id);
            // The user's ImGui binding predates ImGuiChildFlags.AutoResizeY.
            // Measure content locally to keep card height adaptive on this API.
            float height = Heights.TryGetValue(cacheID, out var measured) ? measured : 180 * UiScale;
            visible = ImGui.BeginChild("card", new Vector2(0, height), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            ImGui.TextUnformatted(title);
            if (description.Length != 0) WrapHint(description);
            ImGui.Spacing();
        }
        public void Dispose()
        {
            if (visible) Heights[cacheID] = Math.Max(40 * UiScale, ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y);
            ImGui.EndChild(); ImGui.PopID(); ImGui.Spacing();
        }
    }
    private bool Toggle(string label, ref bool value, string? key = null)
    {
        var t = OmniTheme.Tokens;
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(100, ImGui.GetContentRegionAvail().X), ImGui.GetTextLineHeight() + 18 * UiScale);
        bool clicked = ImGui.InvisibleButton(label, size);
        if (clicked) value = !value;
        bool hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        if (hovered) draw.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(t.Border with { W = 0.08f }), 6 * UiScale);
        var switchSize = new Vector2(38, 21) * UiScale;
        var switchPos = pos + new Vector2(size.X - switchSize.X - 4 * UiScale, (size.Y - switchSize.Y) / 2);
        var fill = value ? OmniTheme.ControlAccent : t.Border with { W = 0.35f };
        draw.AddRectFilled(switchPos, switchPos + switchSize, ImGui.ColorConvertFloat4ToU32(fill), switchSize.Y / 2);
        var knob = new Vector2(value ? switchPos.X + switchSize.X - switchSize.Y / 2 : switchPos.X + switchSize.Y / 2, switchPos.Y + switchSize.Y / 2);
        draw.AddCircleFilled(knob, switchSize.Y / 2 - 3 * UiScale, 0xFFFFFFFF);
        draw.AddText(pos + new Vector2(4 * UiScale, (size.Y - ImGui.GetTextLineHeight()) / 2), ImGui.ColorConvertFloat4ToU32(t.Text), label);
        if (key != null)
        {
            bool failed = value && errors.ContainsKey(key);
            string state = !value ? "关闭" : failed ? "初始化失败" : !running ? "待启用" : !Active ? "等待登录" : "已开启";
            var color = failed ? t.Error : value ? t.Success : Muted;
            var textSize = ImGui.CalcTextSize(state);
            // Keep narrow host panels readable; status remains available through the tooltip.
            if (ImGui.CalcTextSize(label).X + textSize.X + switchSize.X + 44 * UiScale < size.X)
                draw.AddText(new Vector2(switchPos.X - textSize.X - 14 * UiScale, pos.Y + (size.Y - textSize.Y) / 2), ImGui.ColorConvertFloat4ToU32(color), state);
            if (hovered && failed) ImGui.SetTooltip(errors[key]);
        }
        return clicked;
    }
    private static bool Slider(string label, ref float value, float min, float max, string format, bool enabled)
    {
        ImGui.BeginDisabled(!enabled);
        Hint(label);
        ImGui.SetNextItemWidth(-1);
        ImGui.SliderFloat($"##{label}", ref value, min, max, format);
        bool committed = ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("拖动调整，Ctrl + 单击可直接输入数值。");
        ImGui.EndDisabled();
        return committed;
    }
    private static bool ActionButton(string label)
    {
        float available = ImGui.GetContentRegionAvail().X;
        return ImGui.Button(label, new Vector2(Math.Min(available, Math.Max(130 * UiScale, ImGui.CalcTextSize(label).X + 28 * UiScale)), 0));
    }
    public override bool DrawSettings()
    {
        using var style = new PanelStyle();
        ImGui.PushID("IChingSettings");
        try { return DrawPanelContent(); }
        finally { ImGui.PopID(); }
    }
    private bool DrawPanelContent()
    {
        bool changed = false;
        ImGui.TextUnformatted("IChing");
        ImGui.Spacing();
        if (ImGui.BeginTabBar("sections", ImGuiTabBarFlags.FittingPolicyScroll))
        {
            if (ImGui.BeginTabItem("移动")) { changed |= DrawMovement(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("战斗")) { changed |= DrawCombat(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("雷达")) { changed |= DrawRadarSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("目标")) { changed |= DrawTargetSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("工具")) { DrawTools(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("诊断")) { changed |= DrawDiagnostics(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.Separator();
        WrapHint(notice);
        if (changed && running) SyncFeatures();
        return changed;
    }
    private bool DrawMovement()
    {
        bool changed = false;
        using (new Card("movement", "移动速度", "调整移动速度与加速度。"))
        {
            changed |= Toggle("移速调整", ref config.Speed, "移速");
            changed |= Slider("增加速度", ref config.SpeedAdd, 0, 20, "+ %.2f", config.Speed);
            ImGui.BeginDisabled(!config.Speed);
            changed |= Toggle("最大加速度", ref config.MaxAcceleration);
            ImGui.EndDisabled();
            changed |= Toggle("强制移动", ref config.MovePermission, "强制移动");
        }
        using (new Card("knock", "击退控制", "击退距离与时间为调整后的值。"))
        {
            changed |= Toggle("防击退", ref config.AntiKnock, "防击退");
            changed |= Slider("击退距离", ref config.KnockDistance, 0, 100, "%.1f m", config.AntiKnock);
            changed |= Slider("击退时间", ref config.KnockTime, 0, 10, "%.2f s", config.AntiKnock);
        }
        using (new Card("fall", "掉落控制", "两项功能分别控制掉落伤害和掉落检测。"))
        {
            changed |= Toggle("掉落无伤", ref config.NoFallDamage, "掉落无伤");
            changed |= Toggle("无掉落", ref config.NoFall, "无掉落");
        }
        return changed;
    }
    private bool DrawCombat()
    {
        bool changed = false;
        using (new Card("range", "范围与动作", "技能距离沿用原逻辑；多数非突进技能增加值达到 2 后按 +3 处理。"))
        {
            changed |= Toggle("技能距离", ref config.ActionRange, "技能距离");
            changed |= Slider("距离增加", ref config.RangeAdd, 0, 40, "+ %.1f m", config.ActionRange);
            changed |= Toggle("目标圈大小", ref config.ActorRadius, "目标圈");
            changed |= Slider("目标圈增加", ref config.RadiusAdd, 0, 5, "+ %.1f m", config.ActorRadius);
            changed |= Toggle("后摇可移动", ref config.NoBackswing, "后摇移动");
        }
        using (new Card("cast", "咏唱与复唱", "单位为秒；快速结印需要开启复唱缩减。"))
        {
            changed |= Toggle("复唱缩减", ref config.Recast, "复唱缩减");
            changed |= Slider("复唱缩减", ref config.RecastSeconds, 0, 60, "- %.2f s", config.Recast);
            ImGui.BeginDisabled(!config.Recast);
            changed |= Toggle("快速结印", ref config.Mudra);
            ImGui.EndDisabled();
            changed |= Toggle("咏唱缩减", ref config.Cast, "咏唱缩减");
            changed |= Slider("咏唱缩减", ref config.CastSeconds, 0, 10, "- %.2f s", config.Cast);
            changed |= Toggle("PVP 动画锁", ref config.PvpAnimationLock, "PVP 动画锁");
            changed |= Slider("动画锁上限", ref config.AnimationLock, 0, 1, "%.2f s", config.PvpAnimationLock);
        }
        using (new Card("timeline", "突进位移管理", "通过技能的结束动画识别位移；多个技能可能共用同一动画。"))
        {
            changed |= Toggle("突进无位移", ref config.NoActionMove, "突进无位移");
            ImGui.BeginDisabled(!config.NoActionMove);
            changed |= Toggle("列表作为允许位移的例外", ref config.BlockAllExceptListed);
            WrapHint(config.BlockAllExceptListed ? "屏蔽列表以外的全部动画。" : "仅屏蔽列表中的动画。空列表不会拦截位移。");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputInt("##actionID", ref actionInput);
            ImGui.BeginDisabled(!running || actionInput <= 0);
            if (ActionButton("按技能 ID 添加"))
            {
                var row = services.Data.GetExcelSheet<LuminaAction>().GetRowOrDefault((uint)actionInput);
                if (row != null && row.Value.AnimationEnd.RowId != 0)
                { changed |= config.BlockedTimelines.Add(row.Value.AnimationEnd.RowId); notice = $"已添加 {row.Value.Name} / 动画 {row.Value.AnimationEnd.RowId}"; }
                else notice = "技能不存在或没有结束动画。";
            }
            ImGui.EndDisabled();
            changed |= DrawIdList("timelines", config.BlockedTimelines, true);
            ImGui.EndDisabled();
        }
        return changed;
    }
    private bool DrawRadarSettings()
    {
        bool changed = false;
        using (new Card("radar", "战场雷达", running && Active && services.Client.IsPvP ? $"当前扫描到 {radar.Count} 个敌方玩家。" : "进入 PVP 区域后开始显示敌方玩家。"))
        {
            changed |= Toggle("启用战场雷达", ref config.Radar, "可攻击目标判定");
            changed |= Slider("探测距离", ref config.RadarDistance, 1, 500, "%.0f m", config.Radar);
            ImGui.BeginDisabled(!config.Radar);
            changed |= Toggle("连线", ref config.RadarLines);
            changed |= Toggle("职业图标", ref config.JobIcons);
            changed |= Toggle("战意图标", ref config.BattleIcons);
            ImGui.EndDisabled();
        }
        using (new Card("follow", "关注名单", "关注玩家用绿色标记；当前按姓名匹配。"))
        {
            ImGui.BeginDisabled(!running || services.Targets.Target is not IPlayerCharacter);
            if (ActionButton("关注当前玩家目标") && services.Targets.Target is IPlayerCharacter pc) changed |= config.FollowedPlayers.Add(pc.Name.ToString());
            ImGui.EndDisabled();
            if (config.FollowedPlayers.Count == 0) WrapHint("尚无关注玩家。选中玩家后点击上方按钮。" );
            if (config.FollowedPlayers.Count > 0 && ImGui.BeginTable("players", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("玩家");
                ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 70 * UiScale);
                foreach (var name in config.FollowedPlayers.OrderBy(n => n).ToArray())
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(name);
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton($"移除##follow{name}")) { config.FollowedPlayers.Remove(name); changed = true; }
                }
                ImGui.EndTable();
            }
        }
        return changed;
    }
    private bool DrawTargetSettings()
    {
        bool changed = false;
        using (new Card("status", "目标状态过滤", "当前目标含名单中的状态时，自动取消选中。"))
        {
            changed |= Toggle("自动取消选中", ref config.ClearTarget, "目标过滤");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputInt("##statusID", ref statusInput);
            ImGui.BeginDisabled(statusInput <= 0 || statusInput > ushort.MaxValue);
            if (ActionButton("按状态 ID 添加")) changed |= config.TargetStatuses.Add((uint)statusInput);
            ImGui.EndDisabled();
            changed |= DrawIdList("statuses", config.TargetStatuses, false);
        }
        using (new Card("selection", "鼠标选敌", "将下方指令放入宏或快捷键，鼠标停在游戏场景上执行。"))
        {
            WrapHint("最近敌人：选择距离鼠标世界坐标最近的可攻击目标。\n敌群中心：选择候选敌人的质心附近目标。");
            if (ActionButton("复制最近敌人指令")) { ImGui.SetClipboardText("/omni IChingModule nearest"); notice = "最近敌人指令已复制。"; }
            if (ActionButton("复制敌群中心指令")) { ImGui.SetClipboardText("/omni IChingModule most"); notice = "敌群中心指令已复制。"; }
            changed |= Slider("选敌搜索半径（与雷达共用）", ref config.RadarDistance, 1, 500, "%.0f m", true);
        }
        return changed;
    }
    private bool DrawIdList(string id, HashSet<uint> values, bool timeline)
    {
        bool changed = false;
        if (values.Count == 0) { WrapHint("名单为空。输入 ID 后点击添加。" ); return false; }
        if (ImGui.BeginTable(id, 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 72 * UiScale);
            ImGui.TableSetupColumn(timeline ? "类型" : "状态名称");
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 70 * UiScale);
            ImGui.TableHeadersRow();
            foreach (var value in values.OrderBy(v => v).ToArray())
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(value.ToString());
                ImGui.TableNextColumn();
                string name = timeline ? "技能结束动画" : running ? services.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>().GetRowOrDefault(value)?.Name.ToString() ?? "未知状态" : "状态";
                ImGui.TextWrapped(name);
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"删除##{id}{value}")) { values.Remove(value); changed = true; }
            }
            ImGui.EndTable();
        }
        return changed;
    }
    private void DrawTools()
    {
        using (new Card("position", "坐标与面向", "读取当前位置后调整 X / Y / Z。Y 为高度；面向单位为弧度。"))
        {
            ImGui.BeginDisabled(!Active);
            if (ActionButton("读取当前位置")) { destination = services.Objects.LocalPlayer!.Position; facing = services.Objects.LocalPlayer.Rotation; }
            Hint("世界坐标 X / Y / Z");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputFloat3("##destination", ref destination);
            if (ActionButton("应用坐标")) Run(Teleport);
            Slider("面向", ref facing, -MathF.PI, MathF.PI, "%.3f rad", true);
            if (ActionButton("应用面向")) Run(SetFacing);
            ImGui.EndDisabled();
        }
        using (new Card("model", "目标模型", "仅调整本地显示。停用模块不会自动恢复一次性写入。"))
        {
            bool hasTarget = Active && services.Targets.Target != null;
            WrapHint(hasTarget ? $"当前目标：{services.Targets.Target!.Name}" : "请先选中目标。" );
            Slider("缩放倍数", ref scale, 0.1f, 5, "%.2f x", hasTarget);
            ImGui.BeginDisabled(!hasTarget);
            if (ActionButton("应用目标缩放")) Run(SetScale);
            if (ActionButton("恢复缩放为 1")) { scale = 1; Run(SetScale); }
            ImGui.EndDisabled();
        }
    }
    private bool DrawDiagnostics()
    {
        using (new Card("diagnosis", "运行诊断", "已初始化表示创建成功；实际效果仍需游戏内验证。"))
        {
            if (errors.Count == 0) WrapHint("暂无初始化错误。" );
            foreach (var pair in errors)
            {
                ImGui.TextColored(OmniTheme.Tokens.Error, pair.Key);
                ImGui.TextWrapped(pair.Value);
                ImGui.Separator();
            }
            ImGui.BeginDisabled(!running || errors.Count == 0);
            if (ActionButton("重试失败的功能")) { errors.Clear(); SyncFeatures(); }
            ImGui.EndDisabled();
            if (ActionButton("复制诊断信息"))
            {
                ImGui.SetClipboardText($"IChing 0.2 | running={running} | initialized={resources.Count}\n" + string.Join("\n", errors.Select(p => p.Key + ": " + p.Value)));
                notice = "诊断信息已复制。";
            }
        }
        using (new Card("migration", "未迁移功能", "功能补齐情况见随附的本地调试说明。"))
        {
            WrapHint("可继续重写：魅惑/恐惧处理、部分数值显示伪装、PVP 战绩本地显示、地图旗标传送。\n需要补充或重新研究：飞天遁地、深层迷宫回地面、移动读条、滑冰状态屏蔽、最大军衔、钓鱼动画、伤害飘字伪装。" );
        }
        using (new Card("reset", "配置维护", "关闭所有功能并清空名单；一次性坐标与缩放写入不会撤销。"))
        {
            if (ActionButton("恢复默认并关闭全部功能")) return ResetSettings();
        }
        return false;
    }
    public override bool ResetSettings()
    {
        config = new();
        if (running) SyncFeatures();
        radar.Clear();
        notice = "已恢复默认设置。";
        return true;
    }
    private void Run(System.Action action)
    {
        try
        {
            if (!Active) throw new InvalidOperationException("请先启用模块并登录角色。");
            action();
        }
        catch (Exception e) { notice = e.Message; DalamudServices.PluginLog.Error(e, "IChing 工具操作失败"); }
    }
    private void Teleport()
    {
        if (!float.IsFinite(destination.X) || !float.IsFinite(destination.Y) || !float.IsFinite(destination.Z)) throw new InvalidOperationException("坐标必须是有限数值。");
        var player = (GameObject*)services.Objects.LocalPlayer!.Address;
        if (player == null) return;
        player->Position.X = destination.X; player->Position.Y = destination.Y; player->Position.Z = destination.Z;
        if (player->IsReadyToDraw()) { player->DisableDraw(); player->EnableDraw(); }
        notice = $"已写入本地坐标：{destination}（服务器可能纠正）";
    }
    private void SetFacing()
    {
        var player = (GameObject*)services.Objects.LocalPlayer!.Address;
        if (player != null) player->Rotation = facing;
        notice = $"已写入本地面向：{facing:F3}";
    }
    private delegate void ScaleDelegate(nint address, float value);
    private void SetScale()
    {
        var target = services.Targets.Target ?? throw new InvalidOperationException("请先选中目标。");
        var call = Marshal.GetDelegateForFunctionPointer<ScaleDelegate>(Find("F3 0F 11 89 ?? ?? ?? ?? E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC"));
        call(target.Address, Math.Clamp(scale, 0.1f, 5));
        notice = "已修改目标本地模型缩放。";
    }
    private void SelectEnemy(bool cluster)
    {
        EnsureCanAttack();
        if (!services.Gui.ScreenToWorld(ImGui.GetIO().MousePos, out var mouse, 1)) throw new InvalidOperationException("鼠标位置无法转换为世界坐标。");
        var candidates = services.Objects.Where(o => o.GameObjectId != services.Objects.LocalPlayer!.GameObjectId && o.IsTargetable && Vector3.DistanceSquared(o.Position, mouse) <= config.RadarDistance * config.RadarDistance && canAttack!(142, o.Address) == 1).ToList();
        if (candidates.Count == 0) { notice = "鼠标附近没有可攻击目标。"; return; }
        var center = cluster ? candidates.Aggregate(Vector3.Zero, (sum, o) => sum + o.Position) / candidates.Count : mouse;
        var selected = candidates.OrderBy(o => Vector3.DistanceSquared(o.Position, center)).First();
        services.Targets.Target = selected;
        notice = $"已选中 {selected.Name}";
    }
}

public sealed class IChingModuleServices
{
    [PluginService] public IObjectTable Objects { get; private set; } = null!;
    [PluginService] public ITargetManager Targets { get; private set; } = null!;
    [PluginService] public IClientState Client { get; private set; } = null!;
    [PluginService] public ICondition Condition { get; private set; } = null!;
    [PluginService] public IFramework Framework { get; private set; } = null!;
    [PluginService] public IGameGui Gui { get; private set; } = null!;
    [PluginService] public IDataManager Data { get; private set; } = null!;
    [PluginService] public ITextureProvider Textures { get; private set; } = null!;
    [PluginService] public ISigScanner Scanner { get; private set; } = null!;
    [PluginService] public IGameInteropProvider Interop { get; private set; } = null!;
}

public sealed class IChingModuleConfig
{
    public bool Speed, MaxAcceleration, MovePermission, AntiKnock, NoFallDamage, NoFall;
    public float SpeedAdd = 0.01f, KnockDistance, KnockTime;
    public bool ActionRange, ActorRadius, NoBackswing, NoActionMove, BlockAllExceptListed;
    public float RangeAdd, RadiusAdd;
    public HashSet<uint> BlockedTimelines = [];
    public bool Recast, Mudra, Cast, PvpAnimationLock;
    public float RecastSeconds, CastSeconds, AnimationLock = 0.6f;
    public bool Radar, RadarLines, JobIcons = true, BattleIcons = true, ClearTarget;
    public float RadarDistance = 100;
    public HashSet<uint> TargetStatuses = [];
    public HashSet<string> FollowedPlayers = [];
    public string SpeedSignature = "40 57 48 83 EC ?? 48 8B F9 48 8B 49 ?? 48 8B 01 FF 90 ?? ?? ?? ?? 48 85 C0 75";
    public string MoveSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 54 41 56 41 57 48 83 EC ?? 33 DB 41 8B E9 48 39 1D";
}
