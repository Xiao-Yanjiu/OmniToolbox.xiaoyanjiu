#nullable enable
// IChing -> Omni local module, DLL feature restoration 0.4, 2026-10-11.
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
using OmniToolbox.UI;
using LuminaAction = Lumina.Excel.Sheets.Action;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;
using System.Globalization;
using Dalamud.Game.Command;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmniToolbox.LocalModules;

[ModuleApiVersion(1)]
public sealed unsafe partial class IChingModule : OmniModuleBase
{
    public override OmniToolbox.UI.ModuleLocalization Localization => IChingModuleText.Catalog;

    public override ModuleInfo Info { get; } = new()
    {


        Category = ModuleCategory.Combat,
        Author = "小烟酒（基于 IChing 原作者源码）",
        Commands = [],
    };
    private Settings config = new();
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
    private string notice = IChingModuleText.Get("Text0001");
    private CanAttackDelegate? canAttack;
    private readonly List<RadarEntry> radar = new();
    private long nextScan;
    public override bool HasSettings => true;

    protected override void OnStart(TreeTools.Lifecycle.FeatureLifetime activation)
    {
        IChingModuleText.Bind(this, activation);

        api1Stopped = false;

        activation.OnStopping(StopActivation);

        services = DalamudServices.PluginInterface.Create<IChingModuleServices>()
            ?? throw new InvalidOperationException(IChingModuleText.Get("Text0002"));
        config ??= new();
        config.AllowLegacyHooks = true;
        config.ActorRadius = false; // Old radius hook could crash; never auto-resume a saved enabled state.
        config.BlockedTimelines ??= [];
        config.TargetStatuses ??= [];
        config.FollowedPlayers ??= [];
        config.FollowedPlayerRecords ??= [];
        config.FollowedPlayerRecords.RemoveAll(e => e == null || string.IsNullOrEmpty(e.Name));
        config.DisconnectText ??= "连接暂时中断，请稍后重试。";
        saveHostConfig = typeof(OmniModuleBase).GetProperty("SaveHostConfig", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(this) as System.Action;
        running = true;
        try
        {
            services.Framework.Update += Update;
            services.FlyText.FlyTextCreated += FakeFlyText;
            DalamudServices.PluginInterface.UiBuilder.Draw += Draw;
            RegisterPanelCommands();
            SyncFeatures();
        }
        catch { Stop(); throw; }
    }
    private bool api1Stopped = true;
    private void StopActivation() { if (api1Stopped) return; api1Stopped = true; StopCore(); }
    protected override void OnStop() => StopActivation();
    private void StopCore() => Stop();
    protected override void OnDestroy() => Stop();
    // Unsubscribe first; a managed failure while restoring one feature must not
    // leave the remaining hooks, commands or frame callbacks running.
    internal static void RunCleanupSteps(IEnumerable<System.Action> steps, System.Action<Exception> report)
    {
        foreach (var step in steps)
            try { step(); }
            catch (Exception error) { try { report(error); } catch { /* Continue cleanup even if logging fails. */ } }
    }
    private void Stop()
    {
        running = false;
        var steps = new List<System.Action>();
        if (services != null)
        {
            steps.Add(() => services.Framework.Update -= Update);
            steps.Add(() => services.FlyText.FlyTextCreated -= FakeFlyText);
            foreach (var command in panelCommands.ToArray())
                steps.Add(() => services.Commands.RemoveHandler(command));
        }
        steps.Add(panelCommands.Clear);
        steps.Add(() => { if (DalamudServices.PluginInterface != null) DalamudServices.PluginInterface.UiBuilder.Draw -= Draw; });
        steps.Add(RestoreFishingTimeline);
        steps.Add(RestoreTargetRadius);
        steps.Add(() => opcodeCancellation?.Cancel());
        steps.Add(() => opcodeCancellation?.Dispose());
        steps.Add(() => { opcodeCancellation = null; opcodeTask = null; });
        steps.Add(RestoreDisplay);
        foreach (var key in resources.Keys.ToArray()) steps.Add(() => Release(key));
        steps.Add(() => { canAttack = null; radar.Clear(); });
        RunCleanupSteps(steps, error => DalamudServices.PluginLog.Error(error, IChingModuleText.Get("Text0003")));
    }
    private void Release(string key)
    {
        if (!resources.Remove(key, out var list)) return;
        for (int i = list.Count - 1; i >= 0; i--)
            try { list[i].Dispose(); }
            catch (Exception e) { DalamudServices.PluginLog.Error(e, string.Format(IChingModuleText.Get("Text0004"), key)); }
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
            DalamudServices.PluginLog.Error(e, string.Format(IChingModuleText.Get("Text0005"), key));
        }
        finally { building = null; enabling = null; }
    }
    // Dalamud ScanText already resolves leading CALL/JMP signatures to the target.
    private nint Find(string signature)
    {
        var address = services.Scanner.ScanText(signature);
        ValidateFunctionAddress(address);
        return address;
    }
    private void ValidateFunctionAddress(nint address)
    {
        nint start = services.Scanner.Module.BaseAddress + (nint)services.Scanner.TextSectionOffset;
        long size = services.Scanner.TextSectionSize;
        if (address == 0 || start == 0 || size <= 0 || address < start || (long)(address - start) >= size)
            throw new InvalidOperationException(IChingModuleText.Get("Text0006"));
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
        Feature("移速", config.Speed, () => speedHook = Hook<SpeedDelegate>(config.SpeedSignature, Speed));
        Feature("最大加速度", config.MaxAcceleration, () => accelerationHook = Hook<AccelerationDelegate>("40 ?? 48 ?? ?? ?? 80 79 ?? ?? 48 ?? ?? 0F 84 ?? ?? ?? ?? 48 89 7C 24 ?? 48 ?? ?? ??", Accelerate));
        Feature("强制移动", config.MovePermission, () => moveHook = Hook<MoveDelegate>(config.MoveSignature, Move));
        Feature("防击退", config.AntiKnock, () => knockHook = Hook<KnockDelegate>("48 ?? ?? 57 48 ?? ?? ?? ?? ?? ?? 0F 29 70 ?? 0F ?? ??", Knock));
        Feature("掉落无伤", config.NoFallDamage, () => fallHook = Hook<FallDelegate>("48 89 5C 24 ?? 57 48 ?? ?? ?? 8B ?? 48 ?? ?? 33 ?? E8 ?? ?? ?? ?? 84 ??", Fall));
        Feature("无掉落", config.NoFall, () => noFallHook = Hook<NoFallDelegate>("E8 ?? ?? ?? ?? 8B ?? 85 ?? 0F 88 ?? ?? ?? ?? 48 ?? ?? ?? F3 ?? ?? ?? ?? ?? ?? ??", NoFall));
        Feature("技能距离", config.ActionRange, () => rangeHook = HookAddress<RangeDelegate>((nint)ActionManager.MemberFunctionPointers.GetActionRange, Range));
        if (!config.ActorRadius) RestoreTargetRadius();
        Feature("后摇移动", config.NoBackswing, () => backswingHook = Hook<BackswingDelegate>("48 ?? ?? ?? 48 ?? ?? ?? 45 ?? ?? 33 ?? E8 ?? ?? ?? ?? 84 ?? 74 ??", Backswing));
        Feature("突进无位移", config.NoActionMove, () => actionMoveHook = Hook<ActionMoveDelegate>("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F1 0F 29 74 24 ?? 48 8B 89 ?? ?? ?? ?? 0F 28 F3", ActionMove));
        Feature("复唱缩减", config.Recast || config.Mudra, () => recastHook = HookAddress<RecastDelegate>((nint)ActionManager.MemberFunctionPointers.GetAdjustedRecastTime, Recast));
        Feature("咏唱缩减", config.Cast, () =>
        {
            castHook = HookAddress<CastDelegate>((nint)ActionManager.MemberFunctionPointers.GetAdjustedCastTime, Cast);
            castBarHook = HookAddress<CastBarDelegate>((nint)ActionManager.MemberFunctionPointers.OpenCastBar, OpenCastBar);
        });
        Feature("可攻击目标判定", config.Radar, EnsureCanAttack);
        SyncExtendedFeatures();
    }

    private delegate float SpeedDelegate(long a1);
    private delegate void AccelerationDelegate(long a1);
    private delegate long MoveDelegate(long a1, uint a2, int a3, int a4);
    private delegate long KnockDelegate(long actor, float angle, float distance, float time, byte a5, long a6);
    private delegate nint FallDelegate(nuint a1, uint flag);
    private delegate long NoFallDelegate(long a1, long a2, long a3);
    private delegate float RangeDelegate(uint action);
    private delegate long BackswingDelegate(long a1);
    private delegate ulong ActionMoveDelegate(ulong a1, byte a2, ulong a3, float a4, nint a5);
    private delegate int RecastDelegate(ActionType type, uint key, bool applyClassMechanics);
    private delegate int CastDelegate(ActionType type, uint id, bool apply, ActionManager.CastTimeProc* proc);
    private delegate void CastBarDelegate(ActionManager* manager, BattleChara* character, ActionType type, uint id, uint spellId, uint extra, float elapsed, float total);
    private delegate bool CanAttackDelegate(uint arg, nint address);
    private Hook<SpeedDelegate> speedHook = null!;
    private Hook<AccelerationDelegate> accelerationHook = null!;
    private Hook<MoveDelegate> moveHook = null!;
    private Hook<KnockDelegate> knockHook = null!;
    private Hook<FallDelegate> fallHook = null!;
    private Hook<NoFallDelegate> noFallHook = null!;
    private Hook<RangeDelegate> rangeHook = null!;
    private Hook<BackswingDelegate> backswingHook = null!;
    private Hook<ActionMoveDelegate> actionMoveHook = null!;
    private Hook<RecastDelegate> recastHook = null!;
    private Hook<CastDelegate> castHook = null!;
    private Hook<CastBarDelegate> castBarHook = null!;
    private static readonly HashSet<uint> SpeedStatuses = [14,67,181,240,436,484,502,623,674,709,1073,1107,1114,1141,1147,1259,1344,1394,1595,1790,1796,1935,2099,2158,2391,2551,2662,2731,3167,3284,3472,3473,3548,3943,3948,4334,4341];
    private float Speed(long a1)
    {
        var original = speedHook.Original(a1);
        if (!Active) return original;
        var player = services.Objects.LocalPlayer!;
        if ((services.Client.IsPvP || services.Condition[ConditionFlag.InDeepDungeon]) && player.StatusList.Any(s => SpeedStatuses.Contains(s.StatusId))) return original;
        return original + ClampFinite(config.SpeedAdd, 0, 20);
    }
    private void Accelerate(long a1)
    {
        if (!Active || !config.MaxAcceleration || a1 == 0) { accelerationHook.Original(a1); return; }
        // This is the movement integrator's current speed, not a temporary
        // acceleration parameter. The original function reads AND writes it.
        // Restoring the pre-call value discards the computed output and pins
        // a character starting from rest to zero on every update.
        *(float*)(a1 + 0x44) = 100;
        accelerationHook.Original(a1);
    }
    private long Move(long a1, uint a2, int a3, int a4) => Active && (a2 is 96 or 97 or 98 or 99 or 0x3E9 or 0x3EE or 0x3EF or 0x3F0) ? 1 : moveHook.Original(a1, a2, a3, a4);
    private long Knock(long actor, float angle, float distance, float time, byte a5, long a6)
    {
        // Only alter the local player's knockback, not every actor's animation.
        if (Active && actor == (long)services.Objects.LocalPlayer!.Address)
        { distance = ClampFinite(config.KnockDistance, 0, 100); time = ClampFinite(config.KnockTime, 0, 10); }
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
        var add = ClampFinite(config.RangeAdd, 0, 40);
        if (id != 29066 && !(row.Value.CanTargetHostile && row.Value.AffectsPosition) && add >= 2) add = 3;
        return original + add;
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
    private bool Mudra(uint key) => config.Mudra && key is 18805 or 18806 or 18807 or 2259 or 2261 or 2263;
    private int Recast(ActionType type, uint key, bool applyClassMechanics)
    {
        var original = recastHook.Original(type, key, applyClassMechanics);
        if (!Active || type != ActionType.Action || original <= 0) return original;
        return AdjustRecastMilliseconds(original, config.Recast, Mudra(key), config.RecastSeconds);
    }
    internal static int AdjustRecastMilliseconds(int original, bool reduceRecast, bool isFastMudra, float seconds)
    {
        if (original <= 0) return original;
        if (isFastMudra) return 0;
        return reduceRecast ? Math.Max(original - (int)(ClampFinite(seconds, 0, 60) * 1000), 0) : original;
    }
    private int Cast(ActionType type, uint id, bool apply, ActionManager.CastTimeProc* proc)
    {
        var original = castHook.Original(type, id, apply, proc);
        return Active && type == ActionType.Action && original > 0 ? Math.Max(original - (int)(ClampFinite(config.CastSeconds, 0, 10) * 1000), 0) : original;
    }
    private void OpenCastBar(ActionManager* manager, BattleChara* character, ActionType type, uint id, uint spellId, uint extra, float elapsed, float total)
    {
        if (Active && type == ActionType.Action && (nint)character == services.Objects.LocalPlayer!.Address && float.IsFinite(total))
        {
            total = Math.Max(total - ClampFinite(config.CastSeconds, 0, 10), 0);
            elapsed = Math.Clamp(elapsed, 0, total);
        }
        castBarHook.Original(manager, character, type, id, spellId, extra, elapsed, total);
    }

    private void EnsureCanAttack()
    {
        if (ActionManager.MemberFunctionPointers.CanUseActionOnTarget == null) throw new InvalidOperationException(IChingModuleText.Get("Text0007"));
        canAttack ??= (arg, address) => address != 0 && ActionManager.CanUseActionOnTarget(arg, (GameObject*)address);
    }
    private void Update(IFramework framework)
    {
        if (!Active) { RestoreFishingTimeline(); radar.Clear(); deepFloor = 0; RestoreTargetRadius(); RestoreDisplay(); return; }
        UpdateTargetRadius();
        UpdateExtended();
        if (config.PvpAnimationLock && services.Client.IsPvP)
        {
            var manager = ActionManager.Instance();
            if (manager != null) manager->AnimationLock = Math.Min(manager->AnimationLock, ClampFinite(config.AnimationLock, 0, 1));
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
            if (distance > ClampFinite(config.RadarDistance, 1, 500) || !canAttack(142, pc.Address)) continue;
            uint battleStatus = 0;
            foreach (var status in pc.StatusList) if (status.StatusId is >= 2131 and <= 2135) battleStatus = status.StatusId;
            radar.Add(new(pc.Name.ToString(), pc.Position, distance, pc.ClassJob.RowId, battleStatus, IsFollowedPlayer(pc)));
        }
    }
    private sealed record RadarEntry(string Name, Vector3 Position, float Distance, uint Job, uint BattleStatus, bool Followed);
    private void Draw()
    {
        if (!running) return;
        if (panel)
        {
            using var font = TreeToolKit.Fonts.GetUIFont().Push();
            using var style = new ComicStyleScope();
            ImGui.SetNextWindowSize(new Vector2(760, 520), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(580, 340), new Vector2(float.MaxValue));
            if (ImGui.Begin("IChing###IChingModule", ref panel) && DrawStandaloneSettings())
            {
                try
                {
                    if (saveHostConfig != null) saveHostConfig();
                    else notice = IChingModuleText.Get("Text0008");
                }
                catch (Exception e) { notice = string.Format(IChingModuleText.Get("Text0009"), e.Message); }
            }
            ImGui.End();
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
        if (TryExtendedCommand(arguments, out bool handled)) return handled;
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "ui": panel = !panel; return true;
            case "nearest": Run(() => SelectEnemy(false)); return true;
            case "most": Run(() => SelectEnemy(true)); return true;
            case "flag": Run(() => { ReadFlag(); Teleport(); }); return true;
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
    private sealed class Card : IDisposable
    {
        public Card(string id, string title, string description)
        {
            ImGui.PushID(id); ImGui.Spacing(); ImGui.Separator();
            ImGui.TextColored(Muted, title);
            if (description.Length != 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(description);
            ImGui.BeginGroup();
        }
        public void Dispose() { ImGui.EndGroup(); ImGui.PopID(); }
    }
    private bool Toggle(string label, ref bool value, string? key = null)
    {
        bool changed = ImGui.Checkbox(label, ref value);
        if (key != null && value && errors.TryGetValue(key, out var error))
        {
            ImGui.SameLine(); ImGui.TextColored(OmniTheme.Tokens.Error, "!");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(error);
        }
        return changed;
    }
    private static bool Slider(string label, ref float value, float min, float max, string format, bool enabled)
    {
        ImGui.BeginDisabled(!enabled);
        Hint(label);
        ImGui.SetNextItemWidth(-1);
        ImGui.SliderFloat($"##{label}", ref value, min, max, format);
        bool committed = ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(IChingModuleText.Get("Text0010"));
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
        if (ImGui.Button(IChingModuleText.Get("Text0011"))) panel = true;
        ImGui.SameLine(); ImGui.TextDisabled(string.Format(IChingModuleText.Get("Text0012"), PanelWakeCommand));
        if (errors.Count > 0 && ImGui.Button(string.Format(IChingModuleText.Get("Text0013"), errors.Count))) { panel = true; selectedSection = 6; }
        return false;
    }
    private bool DrawStandaloneSettings()
    {
        ImGui.PushID("IChingStandalone");
        try { return DrawPanelContent(); }
        finally { ImGui.PopID(); }
    }
    private int selectedSection;
    private bool DrawPanelContent()
    {
        bool changed = false;
        string[] sections = [IChingModuleText.Get("Ui2001"), IChingModuleText.Get("Ui2002"), IChingModuleText.Get("Ui2003"), IChingModuleText.Get("Ui2004"), IChingModuleText.Get("Ui2005"), IChingModuleText.Get("Ui2006"), IChingModuleText.Get("Ui2007")];
        if (ImGui.BeginChild("sidebar", new Vector2(120 * UiScale, -ImGui.GetFrameHeightWithSpacing()), true))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 9 * UiScale);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 7 * UiScale));
            try
            {
                for (int i = 0; i < sections.Length; i++)
                {
                    bool selected = selectedSection == i;
                    ImGui.PushStyleColor(ImGuiCol.Button, selected ? OmniTheme.ActiveBackground : Vector4.Zero);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, OmniTheme.HoverBackground);
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, OmniTheme.ActiveBackground);
                    try
                    {
                        if (ImGui.Button(sections[i], new Vector2(ImGui.GetContentRegionAvail().X, Math.Max(36 * UiScale, ImGui.GetFrameHeight()))))
                            selectedSection = i;
                    }
                    finally { ImGui.PopStyleColor(3); }
                }
            }
            finally { ImGui.PopStyleVar(2); }
        }
        ImGui.EndChild(); ImGui.SameLine();
        if (ImGui.BeginChild("body", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), false))
        {
            switch (selectedSection)
            {
                case 0: changed |= DrawMovement(); break;
                case 1: changed |= DrawCombat(); break;
                case 2: changed |= DrawRadarSettings(); break;
                case 3: changed |= DrawTargetSettings(); break;
                case 4: changed |= DrawRestoredTools(); DrawTools(); break;
                case 5: changed |= DrawDisplay(); break;
                case 6: changed |= DrawDiagnostics(); break;
            }
        }
        ImGui.EndChild(); ImGui.Separator();
        ImGui.TextDisabled(errors.Count == 0 ? string.Format(IChingModuleText.Get("Text0014"), PanelWakeCommand) : string.Format(IChingModuleText.Get("Text0015"), errors.Count));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(notice);
        if (changed && running) SyncFeatures();
        return changed;
    }
    private bool DrawMovement()
    {
        bool changed = false;
        using (new Card("movement", IChingModuleText.Get("Ui2008"), ""))
        {
            changed |= CompactRow(IChingModuleText.Get("Ui2009"), ref config.Speed, ref config.SpeedAdd, 0, 20, "+ %.2f", IChingModuleText.Get("Ui2010"));
            changed |= CompactPair(IChingModuleText.Get("Ui2011"), ref config.MaxAcceleration, IChingModuleText.Get("Ui2012"), ref config.MovePermission, IChingModuleText.Get("Ui2013"), IChingModuleText.Get("Ui2014"));
            changed |= CompactDualRow(IChingModuleText.Get("Ui2015"), ref config.AntiKnock, ref config.KnockDistance, ref config.KnockTime, IChingModuleText.Get("Ui2016"));
            changed |= CompactPair(IChingModuleText.Get("Ui2017"), ref config.NoFallDamage, IChingModuleText.Get("Ui2018"), ref config.NoFall, IChingModuleText.Get("Ui2019"), IChingModuleText.Get("Ui2020"));
        }
        changed |= DrawExtendedMovement();
        return changed;
    }
    private bool DrawCombat()
    {
        bool changed = false;
        using (new Card("range", IChingModuleText.Get("Ui2021"), ""))
        {
            changed |= CompactRow(IChingModuleText.Get("Ui2022"), ref config.ActionRange, ref config.RangeAdd, 0, 40, "+ %.1f m", IChingModuleText.Get("Ui2023"));
            changed |= CompactRow(IChingModuleText.Get("Ui2024"), ref config.ActorRadius, ref config.RadiusAdd, 0, 5, "+ %.1f m", null);
            changed |= CompactPair(IChingModuleText.Get("Ui2025"), ref config.NoBackswing, IChingModuleText.Get("Ui2026"), ref config.LocalLineOfSight, IChingModuleText.Get("Ui2027"), IChingModuleText.Get("Ui2028"));
        }
        using (new Card("cast", IChingModuleText.Get("Ui2029"), ""))
        {
            changed |= CompactRow(IChingModuleText.Get("Ui2030"), ref config.Recast, ref config.RecastSeconds, 0, 60, "- %.2f s", IChingModuleText.Get("Ui2031"));
            changed |= Toggle(IChingModuleText.Get("Ui2032"), ref config.Mudra, "复唱缩减");
            changed |= CompactRow(IChingModuleText.Get("Ui2033"), ref config.Cast, ref config.CastSeconds, 0, 10, "- %.2f s", IChingModuleText.Get("Ui2034"));
            changed |= CompactRow(IChingModuleText.Get("Ui2035"), ref config.PvpAnimationLock, ref config.AnimationLock, 0, 1, "%.2f s", null);
        }
        if (!ImGui.CollapsingHeader(IChingModuleText.Get("Text0016"))) return changed;
        using (new Card("timeline", IChingModuleText.Get("Ui2036"), IChingModuleText.Get("Ui2037")))
        {
            changed |= Toggle(IChingModuleText.Get("Ui2038"), ref config.NoActionMove, "突进无位移");
            ImGui.BeginDisabled(!config.NoActionMove);
            changed |= Toggle(IChingModuleText.Get("Ui2039"), ref config.BlockAllExceptListed);
            WrapHint(config.BlockAllExceptListed ? IChingModuleText.Get("Ui2040") : IChingModuleText.Get("Ui2041"));
            ImGui.SetNextItemWidth(-1);
            ImGui.InputInt("##actionID", ref actionInput);
            ImGui.BeginDisabled(!running || actionInput <= 0);
            if (ActionButton(IChingModuleText.Get("Text0017")))
            {
                var row = services.Data.GetExcelSheet<LuminaAction>().GetRowOrDefault((uint)actionInput);
                if (row != null && row.Value.AnimationEnd.RowId != 0)
                { changed |= config.BlockedTimelines.Add(row.Value.AnimationEnd.RowId); notice = string.Format(IChingModuleText.Get("Text0018"), row.Value.Name, row.Value.AnimationEnd.RowId); }
                else notice = IChingModuleText.Get("Text0019");
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
        using (new Card("radar", IChingModuleText.Get("Ui2042"), running && Active && services.Client.IsPvP ? string.Format(IChingModuleText.Get("Ui2043"), radar.Count) : IChingModuleText.Get("Ui2044")))
        {
            changed |= CompactRow(IChingModuleText.Get("Ui2045"), ref config.Radar, ref config.RadarDistance, 1, 500, "%.0f m", IChingModuleText.Get("Ui2046"));
            ImGui.BeginDisabled(!config.Radar);
            changed |= Toggle(IChingModuleText.Get("Ui2047"), ref config.RadarLines);
            changed |= Toggle(IChingModuleText.Get("Ui2048"), ref config.JobIcons);
            changed |= Toggle(IChingModuleText.Get("Ui2049"), ref config.BattleIcons);
            ImGui.EndDisabled();
        }
        using (new Card("follow", IChingModuleText.Get("Ui2050"), IChingModuleText.Get("Ui2051")))
        {
            ImGui.BeginDisabled(!running || services.Targets.Target is not IPlayerCharacter);
            if (ActionButton(IChingModuleText.Get("Text0020")) && services.Targets.Target is IPlayerCharacter pc) changed |= FollowPlayer(pc);
            ImGui.EndDisabled();
            if (config.FollowedPlayers.Count + config.FollowedPlayerRecords.Count == 0) WrapHint(IChingModuleText.Get("Ui2052") );
            if (config.FollowedPlayers.Count + config.FollowedPlayerRecords.Count > 0 && ImGui.BeginTable("players", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn(IChingModuleText.Get("Text0021"));
                ImGui.TableSetupColumn(IChingModuleText.Get("Text0022"), ImGuiTableColumnFlags.WidthFixed, 70 * UiScale);
                foreach (var entry in config.FollowedPlayerRecords.OrderBy(e => e.Name).ToArray())
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(string.Format(IChingModuleText.Get("Text0023"), entry.Name, entry.HomeWorld));
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton(string.Format(IChingModuleText.Get("Text0024"), entry.ContentId, entry.Name, entry.HomeWorld))) { config.FollowedPlayerRecords.Remove(entry); changed = true; }
                }
                foreach (var name in config.FollowedPlayers.OrderBy(n => n).ToArray())
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(name);
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton(string.Format(IChingModuleText.Get("Text0025"), name))) { config.FollowedPlayers.Remove(name); changed = true; }
                }
                ImGui.EndTable();
            }
        }
        return changed;
    }
    private bool DrawTargetSettings()
    {
        DrawObjectives();
        bool changed = false;
        using (new Card("status", IChingModuleText.Get("Ui2053"), IChingModuleText.Get("Ui2054")))
        {
            changed |= Toggle(IChingModuleText.Get("Ui2055"), ref config.ClearTarget, "目标过滤");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputInt("##statusID", ref statusInput);
            ImGui.BeginDisabled(statusInput <= 0 || statusInput > ushort.MaxValue);
            if (ActionButton(IChingModuleText.Get("Text0026"))) changed |= config.TargetStatuses.Add((uint)statusInput);
            ImGui.EndDisabled();
            changed |= DrawIdList("statuses", config.TargetStatuses, false);
        }
        using (new Card("selection", IChingModuleText.Get("Ui2056"), IChingModuleText.Get("Ui2057")))
        {
            WrapHint(IChingModuleText.Get("Ui2058"));
            if (ActionButton(IChingModuleText.Get("Text0027"))) { ImGui.SetClipboardText("/omni IChingModule nearest"); notice = IChingModuleText.Get("Text0028"); }
            if (ActionButton(IChingModuleText.Get("Text0029"))) { ImGui.SetClipboardText("/omni IChingModule most"); notice = IChingModuleText.Get("Text0030"); }
            changed |= Slider(IChingModuleText.Get("Ui2059"), ref config.RadarDistance, 1, 500, "%.0f m", true);
        }
        return changed;
    }
    private bool DrawIdList(string id, HashSet<uint> values, bool timeline)
    {
        bool changed = false;
        if (values.Count == 0) { WrapHint(IChingModuleText.Get("Ui2060") ); return false; }
        if (ImGui.BeginTable(id, 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 72 * UiScale);
            ImGui.TableSetupColumn(timeline ? IChingModuleText.Get("Text0031") : IChingModuleText.Get("Text0032"));
            ImGui.TableSetupColumn(IChingModuleText.Get("Text0022"), ImGuiTableColumnFlags.WidthFixed, 70 * UiScale);
            ImGui.TableHeadersRow();
            foreach (var value in values.OrderBy(v => v).ToArray())
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(value.ToString());
                ImGui.TableNextColumn();
                string name = timeline ? IChingModuleText.Get("Ui2061") : running ? services.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>().GetRowOrDefault(value)?.Name.ToString() ?? IChingModuleText.Get("Ui2062") : IChingModuleText.Get("Ui2063");
                ImGui.TextWrapped(name);
                ImGui.TableNextColumn();
                if (ImGui.SmallButton(string.Format(IChingModuleText.Get("Text0033"), id, value))) { values.Remove(value); changed = true; }
            }
            ImGui.EndTable();
        }
        return changed;
    }
    private void DrawTools()
    {
        using (new Card("position", IChingModuleText.Get("Ui2064"), IChingModuleText.Get("Ui2065")))
        {
            ImGui.BeginDisabled(!Active);
            if (ActionButton(IChingModuleText.Get("Text0034"))) Run(Teleport);
            ImGui.SameLine();
            if (ActionButton(IChingModuleText.Get("Text0035"))) { destination = services.Objects.LocalPlayer!.Position; facing = services.Objects.LocalPlayer.Rotation; }
            Hint(IChingModuleText.Get("Ui2066"));
            ImGui.SetNextItemWidth(-1);
            ImGui.InputFloat3("##destination", ref destination);
            Slider(IChingModuleText.Get("Ui2067"), ref facing, -MathF.PI, MathF.PI, "%.3f rad", true);
            if (ActionButton(IChingModuleText.Get("Text0036"))) Run(SetFacing);
            if (ActionButton(IChingModuleText.Get("Text0037"))) Run(ReadFlag);
            if (ActionButton(IChingModuleText.Get("Text0038"))) Run(() => { ReadFlag(); Teleport(); });
            ImGui.EndDisabled();
        }
        using (new Card("model", IChingModuleText.Get("Ui2068"), IChingModuleText.Get("Ui2069")))
        {
            bool hasTarget = Active && services.Targets.Target != null;
            WrapHint(hasTarget ? string.Format(IChingModuleText.Get("Ui2070"), services.Targets.Target!.Name) : IChingModuleText.Get("Ui2071") );
            Slider(IChingModuleText.Get("Ui2072"), ref scale, 0.1f, 5, "%.2f x", hasTarget);
            ImGui.BeginDisabled(!hasTarget);
            if (ActionButton(IChingModuleText.Get("Text0039"))) Run(SetScale);
            if (ActionButton(IChingModuleText.Get("Text0040"))) { scale = 1; Run(SetScale); }
            ImGui.EndDisabled();
        }
    }
    private bool DrawDiagnostics()
    {
        bool changed = false;
        using (new Card("diagnosis", IChingModuleText.Get("Ui2073"), IChingModuleText.Get("Ui2074")))
        {
            if (errors.Count == 0) WrapHint(IChingModuleText.Get("Ui2075") );
            foreach (var pair in errors)
            {
                ImGui.TextColored(OmniTheme.Tokens.Error, pair.Key);
                ImGui.TextWrapped(pair.Value);
                ImGui.Separator();
            }
            ImGui.BeginDisabled(!running || errors.Count == 0);
            if (ActionButton(IChingModuleText.Get("Text0041"))) { errors.Clear(); SyncFeatures(); }
            ImGui.EndDisabled();
            if (ActionButton(IChingModuleText.Get("Text0042")))
            {
                ImGui.SetClipboardText($"IChing local-audit-20261019 | running={running} | initialized={resources.Count}\n" + string.Join("\n", errors.Select(p => p.Key + ": " + p.Value)));
                notice = IChingModuleText.Get("Text0043");
            }
        }
        using (new Card("migration", IChingModuleText.Get("Ui2076"), IChingModuleText.Get("Ui2077")))
        {
            WrapHint(IChingModuleText.Get("Ui2078") );
        }
        if (ImGui.CollapsingHeader(IChingModuleText.Get("Text0044"))) DrawOpcodeDiagnostics();
        using (new Card("reset", IChingModuleText.Get("Ui2079"), IChingModuleText.Get("Ui2080")))
        {
            if (ActionButton(IChingModuleText.Get("Text0045"))) return ResetSettings();
        }
        return changed;
    }
    public override bool ResetSettings()
    {
        config = new();
        if (running) SyncFeatures();
        radar.Clear();
        notice = IChingModuleText.Get("Text0046");
        return true;
    }
    private void Run(System.Action action)
    {
        try
        {
            if (!Active) throw new InvalidOperationException(IChingModuleText.Get("Text0047"));
            action();
        }
        catch (Exception e) { notice = e.Message; DalamudServices.PluginLog.Error(e, IChingModuleText.Get("Text0048")); }
    }
    private void Teleport()
    {
        if (!float.IsFinite(destination.X) || !float.IsFinite(destination.Y) || !float.IsFinite(destination.Z)) throw new InvalidOperationException(IChingModuleText.Get("Text0049"));
        var player = (GameObject*)services.Objects.LocalPlayer!.Address;
        if (player == null) return;
        player->Position.X = destination.X; player->Position.Y = destination.Y; player->Position.Z = destination.Z;
        if (player->IsReadyToDraw()) { player->DisableDraw(); player->EnableDraw(); }
        notice = string.Format(IChingModuleText.Get("Text0050"), destination);
    }
    private void SetFacing()
    {
        var player = (GameObject*)services.Objects.LocalPlayer!.Address;
        if (player != null) player->Rotation = facing;
        notice = string.Format(IChingModuleText.Get("Text0051"), facing);
    }
    private void SetScale()
    {
        var target = services.Targets.Target ?? throw new InvalidOperationException(IChingModuleText.Get("Text0052"));
        var live = services.Objects.FirstOrDefault(o => o.Address == target.Address && o.GameObjectId == target.GameObjectId);
        if (live == null || !float.IsFinite(scale)) throw new InvalidOperationException(IChingModuleText.Get("Text0053"));
        ((GameObject*)live.Address)->Scale = Math.Clamp(scale, 0.1f, 5);
        notice = IChingModuleText.Get("Text0054");
    }
    private void SelectEnemy(bool cluster)
    {
        EnsureCanAttack();
        if (!services.Gui.ScreenToWorld(ImGui.GetIO().MousePos, out var mouse, 1)) throw new InvalidOperationException(IChingModuleText.Get("Text0055"));
        var candidates = services.Objects.Where(o => o.GameObjectId != services.Objects.LocalPlayer!.GameObjectId && o.IsTargetable && Vector3.DistanceSquared(o.Position, mouse) <= config.RadarDistance * config.RadarDistance && canAttack!(142, o.Address)).ToList();
        if (candidates.Count == 0) { notice = IChingModuleText.Get("Text0056"); return; }
        var center = cluster ? candidates.Aggregate(Vector3.Zero, (sum, o) => sum + o.Position) / candidates.Count : mouse;
        var selected = candidates.OrderBy(o => Vector3.DistanceSquared(o.Position, center)).First();
        services.Targets.Target = selected;
        notice = string.Format(IChingModuleText.Get("Text0057"), selected.Name);
    }
}

public sealed class IChingModuleServices
{
    [PluginService] public ICommandManager Commands { get; private set; } = null!;
    [PluginService] public IFlyTextGui FlyText { get; private set; } = null!;
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

public sealed class Settings
{
    public bool HeightOffset, DeepDungeonReturn, IgnoreCharm, FilterMovementStatuses, CastMovement;
    public float HeightAdd = -7, CastMoveWindow = 0.5f;
    public bool HideHudValues, FakeDamage, ReplaceDisconnectText, MaxGrandCompanyRank;
    public bool AllowLegacyHooks;
    public bool LocalLineOfSight, FastFishingAnimation, PauseFishingNearPlayers = true;
    // FishingAnimationSpeed is retained only to read old configs; the checkbox uses a fixed multiplier.
    public float FishingAnimationSpeed = 5, FishingPlayerDistance = 20;
    public int DisplayDamage = 99999;
    public string DisconnectText = "连接暂时中断，请稍后重试。";
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
    public List<IChingFollowedPlayer> FollowedPlayerRecords = [];
    public string SpeedSignature = "40 57 48 83 EC ?? 48 8B F9 48 8B 49 ?? 48 8B 01 FF 90 ?? ?? ?? ?? 48 85 C0 75";
    public string MoveSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 54 41 56 41 57 48 83 EC ?? 33 DB 41 8B E9 48 39 1D";
}

public sealed unsafe partial class IChingModule
{
    private readonly HashSet<string> panelCommands = [];
    private string PanelWakeCommand => panelCommands.Contains("/iching") ? "/iching" : "/omniiching";
    private void RegisterPanelCommands()
    {
        foreach (var command in new[] { "/iching", "/omniiching" })
        {
            if (services.Commands.AddHandler(command, new CommandInfo((_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args)) panel = !panel;
                else if (args.Trim().Equals("show", StringComparison.OrdinalIgnoreCase)) panel = true;
                else if (args.Trim().Equals("hide", StringComparison.OrdinalIgnoreCase)) panel = false;
                else if (!TryHandleCommand(args)) notice = IChingModuleText.Get("Text0058");
            }) { HelpMessage = IChingModuleText.Get("Text0059") })) panelCommands.Add(command);
        }
    }
    private void RowStatus(string? key)
    {
        if (key != null && errors.TryGetValue(key, out var message))
        {
            ImGui.TextColored(OmniTheme.Tokens.Error, IChingModuleText.Get("Text0060"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(message);
        }
    }
    private bool CompactRow(string label, ref bool enabled, ref float value, float min, float max, string format, string? key)
    {
        bool changed = false;
        ImGui.PushID(label);
        if (ImGui.BeginTable("row", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("toggle", ImGuiTableColumnFlags.WidthFixed, 145 * UiScale);
            ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("state", ImGuiTableColumnFlags.WidthFixed, 34 * UiScale);
            ImGui.TableNextRow(); ImGui.TableNextColumn(); changed |= ImGui.Checkbox(label, ref enabled);
            ImGui.TableNextColumn(); ImGui.BeginDisabled(!enabled);
            ImGui.SetNextItemWidth(-1); ImGui.SliderFloat("##value", ref value, min, max, format);
            changed |= ImGui.IsItemDeactivatedAfterEdit();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
            ImGui.EndDisabled(); ImGui.TableNextColumn(); RowStatus(key); ImGui.EndTable();
        }
        ImGui.PopID(); return changed;
    }
    private bool CompactDualRow(string label, ref bool enabled, ref float distance, ref float time, string key)
    {
        bool changed = false; ImGui.PushID(label);
        // Equal, explicit weights prevent auto-fit feedback from full-width sliders.
        if (ImGui.BeginTable("row", 4, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("toggle", ImGuiTableColumnFlags.WidthFixed, 145 * UiScale);
            ImGui.TableSetupColumn("distance", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("time", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("state", ImGuiTableColumnFlags.WidthFixed, 34 * UiScale);
            ImGui.TableNextRow(); ImGui.TableNextColumn(); changed |= ImGui.Checkbox(label, ref enabled);
            ImGui.BeginDisabled(!enabled);
            ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1); ImGui.SliderFloat("##distance", ref distance, 0, 100, IChingModuleText.Get("Text0061")); changed |= ImGui.IsItemDeactivatedAfterEdit();
            ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1); ImGui.SliderFloat("##time", ref time, 0, 10, IChingModuleText.Get("Text0062")); changed |= ImGui.IsItemDeactivatedAfterEdit();
            ImGui.EndDisabled(); ImGui.TableNextColumn(); RowStatus(key); ImGui.EndTable();
        }
        ImGui.PopID(); return changed;
    }
    private bool CompactPair(string first, ref bool a, string second, ref bool b, string? keyA = null, string? keyB = null)
    {
        bool changed = false; ImGui.PushID(first);
        if (ImGui.BeginTable("pair", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn(); changed |= Toggle(first, ref a, keyA);
            ImGui.TableNextColumn(); changed |= Toggle(second, ref b, keyB); ImGui.EndTable();
        }
        ImGui.PopID(); return changed;
    }
    private bool IsFollowedPlayer(IPlayerCharacter player)
    {
        var native = (Character*)player.Address;
        if (native == null) return false;
        ulong id = native->ContentId;
        ushort world = native->HomeWorld;
        string name = player.Name.ToString();
        return config.FollowedPlayerRecords.Any(e => e.ContentId != 0 && id != 0 ? e.ContentId == id : e.Name == name && e.HomeWorld == world) || config.FollowedPlayers.Contains(name);
    }
    private bool FollowPlayer(IPlayerCharacter player)
    {
        var live = services.Objects.FirstOrDefault(o => o.Address == player.Address && o.GameObjectId == player.GameObjectId);
        if (live is not IPlayerCharacter) return false;
        var native = (Character*)live.Address;
        ulong id = native->ContentId; ushort world = native->HomeWorld; string name = player.Name.ToString();
        if (config.FollowedPlayerRecords.Any(e => id != 0 && e.ContentId != 0 ? e.ContentId == id : e.Name == name && e.HomeWorld == world)) return false;
        config.FollowedPlayerRecords.Add(new() { Name = name, ContentId = id, HomeWorld = world });
        config.FollowedPlayers.Remove(name);
        return true;
    }
    // No global hook or retained native object reference. Resolve the saved identity
    // through the live object table before restoring a write.
    private sealed record TargetRadiusSnapshot(nint Address, ulong ObjectId, uint Territory, float Original, float Written);
    private TargetRadiusSnapshot? targetRadiusSnapshot;
    private void RestoreTargetRadius()
    {
        var saved = targetRadiusSnapshot;
        targetRadiusSnapshot = null;
        if (saved == null || services == null || services.Client.TerritoryType != saved.Territory) return;
        var obj = services.Objects.FirstOrDefault(o => o.Address == saved.Address && o.GameObjectId == saved.ObjectId);
        if (obj == null) return;
        var native = (GameObject*)obj.Address;
        // Preserve an intervening update made by the game or another plugin.
        if (native->HitboxRadius == saved.Written) native->HitboxRadius = saved.Original;
    }
    private void UpdateTargetRadius()
    {
        RestoreTargetRadius();
        if (!config.ActorRadius || !float.IsFinite(config.RadiusAdd) || config.RadiusAdd <= 0) return;
        var target = services.Targets.Target;
        if (target is not IBattleChara || target.Address == 0) return;
        var live = services.Objects.FirstOrDefault(o => o.Address == target.Address && o.GameObjectId == target.GameObjectId);
        if (live == null) return;
        var native = (GameObject*)live.Address;
        float original = native->HitboxRadius;
        if (!float.IsFinite(original) || original <= 0 || original > 100) return;
        float written = original + ClampFinite(config.RadiusAdd, 0, 5);
        targetRadiusSnapshot = new(live.Address, live.GameObjectId, services.Client.TerritoryType, original, written);
        native->HitboxRadius = written;
    }
    private const string NormalMoveSignature = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9 41 8B D8";
    private const string CombatMoveSignature = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9 41 8B F0";
    private delegate nint PositionPacketDelegate(nuint context, nint data, uint arg);
    private delegate nint CharmDelegate(GameObject* obj, float x, float y, float z, int arg, nint extra);
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool SendInternalDelegate(nint client, nint packet, uint arg3, uint arg4, bool priority);
    private Hook<PositionPacketDelegate> normalPositionHook = null!, combatPositionHook = null!;
    private Hook<CharmDelegate> charmHook = null!;
    private Hook<SendInternalDelegate> sendInternalHook = null!;
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool SetStatusDelegate(StatusManager* manager, int index, ushort statusId, float remaining, ushort param, GameObjectId source, bool refresh);
    private delegate byte GrandRankDelegate(PlayerState* player);
    private Hook<SetStatusDelegate> setStatusHook = null!;
    private Hook<GrandRankDelegate> grandRankHook = null!;
    private ushort normalMovementOpcode, instanceMovementOpcode;
    private int deepFloor;
    private static readonly HashSet<uint> MovementStatuses = [142,149,604,905,911,1579,1580,1257,1681,3629,3719,3737,1293,1294,1295,1296,1958,1959,1960,1961,2161,2162,2163,2164,2381,2382,2383,2384,2538,2539,2540,2541,3698,3699,3700,3701,3715,3716,3717,3718,1422,2936,3694,3909];
    private void SyncExtendedFeatures()
    {
        nextDisplayUpdate = 0; // Settings changes are applied on the next framework tick.
        if (!config.FastFishingAnimation) RestoreFishingTimeline();
        Feature("本地视线判定", config.LocalLineOfSight, () => lineOfSightHook = HookAddress<LineOfSightDelegate>((nint)ActionManager.MemberFunctionPointers.GetActionInRangeOrLoS, LocalLineOfSight));
        Feature("钓鱼动画加速", config.FastFishingAnimation, () => ValidateFunctionAddress((nint)FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer.MemberFunctionPointers.SetSlotSpeed));
        Feature("状态过滤", config.FilterMovementStatuses, () =>
            setStatusHook = HookAddress<SetStatusDelegate>((nint)StatusManager.MemberFunctionPointers.SetStatus, SetMovementStatus));
        Feature("最大军衔", config.MaxGrandCompanyRank, () =>
            grandRankHook = HookAddress<GrandRankDelegate>((nint)PlayerState.MemberFunctionPointers.GetGrandCompanyRank, GrandRank));
        Feature("高度偏移", config.HeightOffset, () =>
        {
            normalPositionHook = Hook<PositionPacketDelegate>(NormalMoveSignature, NormalPosition);
            combatPositionHook = Hook<PositionPacketDelegate>(CombatMoveSignature, CombatPosition);
        });
        Feature("魅惑与恐惧", config.IgnoreCharm, () => charmHook = Hook<CharmDelegate>("E8 ?? ?? ?? ?? B0 ?? C7 43 ?? ?? ?? ?? ?? EB ??", Charm));
        Feature("移动读条", config.CastMovement, () =>
        {
            // Match both opcodes before installing a sender hook. Never fall back to stale values.
            uint instance = unchecked((uint)Marshal.ReadInt32(services.Scanner.ScanText("41 B8 ?? ?? ?? ?? F6 C2") + 2));
            uint normal = unchecked((uint)Marshal.ReadInt32(services.Scanner.ScanText(NormalMoveSignature) + 0x51));
            if (normal is 0 or > ushort.MaxValue || instance is 0 or > ushort.MaxValue || normal == instance)
                throw new InvalidOperationException(IChingModuleText.Get("Text0063"));
            normalMovementOpcode = (ushort)normal; instanceMovementOpcode = (ushort)instance;
            sendInternalHook = Hook<SendInternalDelegate>("E8 ?? ?? ?? ?? 84 ?? 74 ?? 48 ?? ?? C7 87 ?? ?? ?? ?? ?? ?? ?? ??", SendInternal);
        });
        if (!config.HideHudValues && !config.ReplaceDisconnectText) RestoreDisplay();
    }
    private Hook<T> HookAddress<T>(nint address, T callback) where T : Delegate
    {
        ValidateFunctionAddress(address);
        var hook = services.Interop.HookFromAddress(address, callback);
        building!.Add(hook); enabling!.Add(hook.Enable); return hook;
    }
    private bool SetMovementStatus(StatusManager* manager, int index, ushort statusId, float remaining, ushort param, GameObjectId source, bool refresh)
    {
        if (Active && config.FilterMovementStatuses && manager != null && manager->Owner != null &&
            (nint)manager->Owner == services.Objects.LocalPlayer!.Address && MovementStatuses.Contains(statusId)) return false;
        return setStatusHook.Original(manager, index, statusId, remaining, param, source, refresh);
    }
    private byte GrandRank(PlayerState* player)
    {
        var result = grandRankHook.Original(player);
        return Active && config.MaxGrandCompanyRank && player != null && player == PlayerState.Instance() && player->GrandCompany != 0 ? (byte)11 : result;
    }
    private bool ShouldOffset => Active && config.HeightOffset && float.IsFinite(config.HeightAdd) &&
        (!config.DeepDungeonReturn || deepFloor > 0 && deepFloor % 10 != 0 &&
         !(deepFloor == 99 && services.Client.TerritoryType is 1108 or 1290));
    private nint NormalPosition(nuint context, nint data, uint arg)
    {
        if (!ShouldOffset || data == 0) return normalPositionHook.Original(context, data, arg);
        var values = (float*)data; float saved = values[3];
        try { values[3] = saved + ClampFinite(config.HeightAdd, -100, 100); return normalPositionHook.Original(context, data, arg); }
        finally { values[3] = saved; }
    }
    private nint CombatPosition(nuint context, nint data, uint arg)
    {
        if (!ShouldOffset || data == 0) return combatPositionHook.Original(context, data, arg);
        var values = (float*)data; float saved4 = values[4], saved7 = values[7];
        try
        {
            float add = ClampFinite(config.HeightAdd, -100, 100);
            values[4] = saved4 + add; values[7] = saved7 + add;
            return combatPositionHook.Original(context, data, arg);
        }
        finally { values[4] = saved4; values[7] = saved7; }
    }
    private nint Charm(GameObject* obj, float x, float y, float z, int arg, nint extra) =>
        Active && (nint)obj == services.Objects.LocalPlayer!.Address ? 0 : charmHook.Original(obj, x, y, z, arg, extra);
    private bool SendInternal(nint client, nint packet, uint arg3, uint arg4, bool priority)
    {
        if (Active && config.CastMovement && packet != 0)
        {
            var player = services.Objects.LocalPlayer!;
            ushort opcode = *(ushort*)packet;
            float remaining = player.TotalCastTime - player.CurrentCastTime;
            if (player.IsCasting && remaining >= 0 && remaining <= ClampFinite(config.CastMoveWindow, 0, 10) &&
                (opcode == normalMovementOpcode || opcode == instanceMovementOpcode)) return true;
        }
        return sendInternalHook.Original(client, packet, arg3, arg4, priority);
    }
    private void UpdateExtended()
    {
        UpdateFishingState();
        deepFloor = 0;
        if (config.HeightOffset && config.DeepDungeonReturn && services.Condition[ConditionFlag.InDeepDungeon])
        {
            var events = EventFramework.Instance();
            var deep = events == null ? null : events->GetInstanceContentDeepDungeon();
            if (deep != null) deepFloor = deep->Floor;
        }
        if (config.FilterMovementStatuses && resources.ContainsKey("状态过滤"))
        {
            var character = (BattleChara*)services.Objects.LocalPlayer!.Address;
            var manager = character->GetStatusManager();
            if (manager != null)
            {
                var entries = manager->Status;
                for (int i = 0; i < entries.Length; i++)
                    if (MovementStatuses.Contains(entries[i].StatusId) && StatusManager.MemberFunctionPointers.RemoveStatus != null) manager->RemoveStatus(i, 0);
            }
        }
        UpdateDisplay();
    }
    private bool DrawExtendedMovement()
    {
        bool changed = false;
        using (new Card("height", IChingModuleText.Get("Ui2081"), ""))
        {
            changed |= CompactRow(IChingModuleText.Get("Ui2082"), ref config.HeightOffset, ref config.HeightAdd, -100, 100, "%.1f m", IChingModuleText.Get("Ui2083"));
            changed |= Toggle(IChingModuleText.Get("Ui2084"), ref config.DeepDungeonReturn);
            if (config.DeepDungeonReturn && deepFloor > 0) { ImGui.SameLine(); ImGui.TextDisabled(string.Format(IChingModuleText.Get("Text0064"), deepFloor)); }
        }
        using (new Card("control", IChingModuleText.Get("Ui2085"), ""))
        {
            changed |= CompactPair(IChingModuleText.Get("Ui2086"), ref config.IgnoreCharm, IChingModuleText.Get("Ui2087"), ref config.FilterMovementStatuses, IChingModuleText.Get("Ui2088"), IChingModuleText.Get("Ui2089"));
            changed |= CompactRow(IChingModuleText.Get("Ui2090"), ref config.CastMovement, ref config.CastMoveWindow, 0, 10, IChingModuleText.Get("Ui2091"), IChingModuleText.Get("Ui2092"));
        }
        return changed;
    }
    private int objectiveArea, objectivePoint;
    private static readonly string[] GrassPoints = Enumerable.Range(1, 13).Select(i => i.ToString("00")).ToArray();
    private static readonly string[] RuinPoints = ["a1","a2","a3","a4","b1","b2","b3","b4","c1","c2","c4","d1","d2"];
    private static readonly uint[] RuinIds = [0xf01cc,0xf01cd,0xf01ce,0xf01cf,0xf01d0,0xf01d2,0xf01d1,0xf01d3,0xf01d9,0xf01d4,0xf01d7,0xf01da,0xf01d5];
    private void DrawObjectives()
    {
        using (new Card("objectives", IChingModuleText.Get("Ui2093"), IChingModuleText.Get("Ui2094")))
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##objectiveArea", ref objectiveArea, IChingModuleText.Get("Text0065"))) objectivePoint = 0;
            var labels = objectiveArea == 0 ? GrassPoints : RuinPoints;
            ImGui.SetNextItemWidth(-1); ImGui.Combo("##objectivePoint", ref objectivePoint, string.Join("\0", labels) + "\0");
            ImGui.BeginDisabled(!Active);
            if (ActionButton(IChingModuleText.Get("Text0066"))) Run(() =>
            {
                if (!services.Client.IsPvP) throw new InvalidOperationException(IChingModuleText.Get("Text0067"));
                var index = Math.Clamp(objectivePoint, 0, labels.Length - 1);
                uint id = objectiveArea == 0 ? 0xf0338u + (uint)index : RuinIds[index];
                var target = services.Objects.FirstOrDefault(o => o.BaseId == id && o.IsTargetable);
                if (target == null) throw new InvalidOperationException(IChingModuleText.Get("Text0068"));
                services.Targets.Target = target; notice = string.Format(IChingModuleText.Get("Text0069"), labels[index]);
            });
            ImGui.EndDisabled();
        }
    }
    private void ReadFlag()
    {
        var map = AgentMap.Instance();
        if (map == null || map->FlagMapMarkers.IsEmpty) throw new InvalidOperationException(IChingModuleText.Get("Text0070"));
        var marker = map->FlagMapMarkers[0];
        if (marker.TerritoryId != services.Client.TerritoryType) throw new InvalidOperationException(IChingModuleText.Get("Text0071"));
        Vector3 point = new(marker.XFloat, -1, marker.YFloat);
        Vector3? floor = null;
        try
        {
            floor = DalamudServices.PluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor").InvokeFunc(point, true, 0);
            floor ??= DalamudServices.PluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint").InvokeFunc(point, 5, 10000);
        }
        catch (Exception) { /* Optional IPC: use current player height and ask for manual adjustment. */ }
        destination = floor ?? new Vector3(point.X, services.Objects.LocalPlayer!.Position.Y, point.Z);
        if (floor == null) throw new InvalidOperationException(IChingModuleText.Get("Text0072"));
        notice = IChingModuleText.Get("Text0073");
    }
    private void FakeFlyText(ref FlyTextKind kind, ref int value1, ref int value2, ref SeString text1, ref SeString text2,
        ref uint color, ref uint icon, ref uint damageTypeIcon, ref float yOffset, ref bool handled)
    {
        if (!Active || !config.FakeDamage || handled) return;
        var name = kind.ToString();
        if (name.Contains("Damage", StringComparison.OrdinalIgnoreCase) || name.Contains("Heal", StringComparison.OrdinalIgnoreCase))
        { if (value1 > 0) value1 = Math.Clamp(config.DisplayDamage, 1, 999999999); if (value2 > 0) value2 = Math.Clamp(config.DisplayDamage, 1, 999999999); }
    }
    private record DisplaySnapshot(nint Node, string Original, string Written);
    private readonly Dictionary<(string Addon, uint Component, uint Node), DisplaySnapshot> displaySnapshots = new();
    private static AtkTextNode* TextNode(AtkUnitBase* addon, uint component, uint node)
    {
        if (addon == null) return null;
        if (component == 0) return addon->GetTextNodeById(node);
        var componentNode = addon->GetComponentNodeById(component);
        if (componentNode == null) return null;
        var value = componentNode->GetComponent();
        return value == null ? null : value->GetTextNodeById(node);
    }
    private void DisplayText(string addonName, uint component, uint nodeId, string text, bool counter = false)
    {
        var addon = (AtkUnitBase*)services.Gui.GetAddonByName(addonName).Address;
        if (addon == null) return;
        var key = (addonName, component, nodeId);
        nint ptr; string original;
        if (counter)
        {
            var node = addon->GetNodeById(nodeId); if (node == null) return;
            var count = node->GetAsAtkCounterNode(); if (count == null) return;
            ptr = (nint)count; original = count->NodeText.ToString(); count->NodeText.SetString(text);
        }
        else
        {
            var node = TextNode(addon, component, nodeId); if (node == null) return;
            ptr = (nint)node; original = node->NodeText.ToString(); node->SetText(text);
        }
        if (displaySnapshots.TryGetValue(key, out var old) && old.Node == ptr && old.Written == original) original = old.Original;
        displaySnapshots[key] = new(ptr, original, text);
    }
    private void RestoreDisplay()
    {
        if (services == null) return;
        foreach (var item in displaySnapshots.ToArray())
        {
            var addon = (AtkUnitBase*)services.Gui.GetAddonByName(item.Key.Addon).Address;
            if (addon == null) continue;
            if (item.Key.Addon == "_Money")
            {
                var node = addon->GetNodeById(item.Key.Node); if (node == null) continue;
                var count = node->GetAsAtkCounterNode();
                if (count != null && (nint)count == item.Value.Node && count->NodeText.ToString() == item.Value.Written) count->NodeText.SetString(item.Value.Original);
            }
            else
            {
                var node = TextNode(addon, item.Key.Component, item.Key.Node);
                if (node != null && (nint)node == item.Value.Node && node->NodeText.ToString() == item.Value.Written) node->SetText(item.Value.Original);
            }
        }
        displaySnapshots.Clear();
    }
    private long nextDisplayUpdate;
    private void UpdateDisplay()
    {
        if (!config.HideHudValues && !config.ReplaceDisconnectText && displaySnapshots.Count == 0) return;
        if (Environment.TickCount64 < nextDisplayUpdate) return;
        nextDisplayUpdate = Environment.TickCount64 + 100;
        // Ten updates per second are sufficient for masking text; don't restore
        // and allocate replacement strings on every rendered frame.
        RestoreDisplay();
        if (config.HideHudValues)
        {
            DisplayText("_Money", 0, 2, "---", true);
            DisplayText("_ParameterWidget", 3, 3, "--- / ---");
            DisplayText("_ParameterWidget", 4, 3, "--- / ---");
            DisplayText("_Exp", 0, 4, IChingModuleText.Get("Text0074"));
        }
        if (config.ReplaceDisconnectText)
        {
            var addon = (AtkUnitBase*)services.Gui.GetAddonByName("Dialogue").Address;
            var code = TextNode(addon, 0, 5); var message = TextNode(addon, 0, 3);
            if (code != null && message != null && (code->NodeText.ToString() is "90002" or "90001" or "2002" or "5003"))
                DisplayText("Dialogue", 0, 3, config.DisconnectText);
        }
    }
    private bool DrawDisplay()
    {
        bool changed = false;
        using (new Card("privacy", IChingModuleText.Get("Ui2095"), IChingModuleText.Get("Ui2096")))
        {
            changed |= Toggle(IChingModuleText.Get("Ui2097"), ref config.MaxGrandCompanyRank, "最大军衔");
            changed |= Toggle(IChingModuleText.Get("Ui2098"), ref config.HideHudValues);
            changed |= Toggle(IChingModuleText.Get("Ui2099"), ref config.FakeDamage);
            ImGui.BeginDisabled(!config.FakeDamage);
            Hint(IChingModuleText.Get("Ui2100"));
            ImGui.SetNextItemWidth(-1);
            changed |= ImGui.InputInt("##displayDamage", ref config.DisplayDamage);
            config.DisplayDamage = Math.Clamp(config.DisplayDamage, 1, 999999999);
            ImGui.EndDisabled();
            changed |= Toggle(IChingModuleText.Get("Ui2101"), ref config.ReplaceDisconnectText);
            ImGui.BeginDisabled(!config.ReplaceDisconnectText);
            ImGui.SetNextItemWidth(-1);
            changed |= ImGui.InputTextWithHint("##disconnect", IChingModuleText.Get("Text0075"), ref config.DisconnectText, 256);
            ImGui.EndDisabled();
        }
        if (ImGui.CollapsingHeader(IChingModuleText.Get("Text0076"))) DrawPvpDisplay();
        return changed;
    }
    private int pvpMode, pvpWins, pvpSecond, pvpThird, pvpMatches, pvpCredit;
    private bool pvpWeekly, pvpSetCrystalRank;
    private void DrawPvpDisplay()
    {
        WrapHint(IChingModuleText.Get("Ui2102"));
        ImGui.SetNextItemWidth(-1); ImGui.Combo("##pvpMode", ref pvpMode, IChingModuleText.Get("Text0077"));
        if (pvpMode != 0) ImGui.Checkbox(IChingModuleText.Get("Text0078"), ref pvpWeekly);
        CountInput(pvpMode == 0 ? IChingModuleText.Get("Ui2103") : pvpMode == 1 ? IChingModuleText.Get("Ui2104") : IChingModuleText.Get("Ui2105"), ref pvpWins);
        if (pvpMode != 2) CountInput(pvpMode == 0 ? IChingModuleText.Get("Ui2106") : IChingModuleText.Get("Ui2107"), ref pvpSecond);
        if (pvpMode == 1) CountInput(IChingModuleText.Get("Ui2108"), ref pvpThird);
        if (pvpMode != 0) CountInput(IChingModuleText.Get("Ui2109"), ref pvpMatches);
        else
        {
            ImGui.Checkbox(IChingModuleText.Get("Text0079"), ref pvpSetCrystalRank);
            ImGui.BeginDisabled(!pvpSetCrystalRank);
            CountInput(IChingModuleText.Get("Ui2110"), ref pvpCredit);
            ImGui.EndDisabled();
        }
        ImGui.BeginDisabled(!Active);
        if (ActionButton(IChingModuleText.Get("Text0080"))) Run(() => PvpDisplay(false));
        if (ActionButton(IChingModuleText.Get("Text0081"))) Run(() => PvpDisplay(true));
        ImGui.EndDisabled();
    }
    private static void CountInput(string label, ref int value)
    {
        Hint(label); ImGui.SetNextItemWidth(-1); ImGui.InputInt("##" + label, ref value); value = Math.Clamp(value, 0, 65535);
    }
    private void PvpDisplay(bool write)
    {
        var profile = PvPProfile.Instance(); if (profile == null) throw new InvalidOperationException(IChingModuleText.Get("Text0082"));
        if (write && pvpMode == 1 && pvpWeekly && pvpWins + pvpSecond + pvpThird > ushort.MaxValue)
            throw new InvalidOperationException(IChingModuleText.Get("Text0083"));
        if (pvpMode == 0)
        {
            if (write)
            {
                profile->CrystallineConflictCasualMatchesWon = (ushort)pvpWins;
                profile->CrystallineConflictRankedMatchesWon = (ushort)pvpSecond;
                if (pvpSetCrystalRank)
                {
                    profile->CrystallineConflictHighestRank = 6;
                    profile->CrystallineConflictHighestCrystalCredit = (ushort)pvpCredit;
                }
            }
            else { pvpWins = profile->CrystallineConflictCasualMatchesWon; pvpSecond = profile->CrystallineConflictRankedMatchesWon; pvpCredit = profile->CrystallineConflictHighestCrystalCredit; }
        }
        else if (pvpMode == 1)
        {
            if (write)
            {
                if (pvpWeekly) { profile->FrontlineWeeklyFirstPlace = (ushort)pvpWins; profile->FrontlineWeeklySecondPlace = (ushort)pvpSecond; profile->FrontlineWeeklyThirdPlace = (ushort)pvpThird; profile->FrontlineWeeklyMatches = (ushort)Math.Max(pvpMatches, pvpWins + pvpSecond + pvpThird); }
                else { profile->FrontlineTotalFirstPlace = (uint)pvpWins; profile->FrontlineTotalSecondPlace = (uint)pvpSecond; profile->FrontlineTotalThirdPlace = (uint)pvpThird; profile->FrontlineTotalMatches = (uint)Math.Max(pvpMatches, pvpWins + pvpSecond + pvpThird); }
            }
            else { pvpWins = (int)(pvpWeekly ? profile->FrontlineWeeklyFirstPlace : profile->FrontlineTotalFirstPlace); pvpSecond = (int)(pvpWeekly ? profile->FrontlineWeeklySecondPlace : profile->FrontlineTotalSecondPlace); pvpThird = (int)(pvpWeekly ? profile->FrontlineWeeklyThirdPlace : profile->FrontlineTotalThirdPlace); pvpMatches = (int)(pvpWeekly ? profile->FrontlineWeeklyMatches : profile->FrontlineTotalMatches); }
        }
        else
        {
            if (write)
            {
                if (pvpWeekly) { profile->RivalWingsWeeklyMatchesWon = (uint)pvpWins; profile->RivalWingsWeeklyMatches = (uint)Math.Max(pvpMatches, pvpWins); }
                else { profile->RivalWingsTotalMatchesWon = (uint)pvpWins; profile->RivalWingsTotalMatches = (uint)Math.Max(pvpMatches, pvpWins); }
            }
            else { pvpWins = (int)(pvpWeekly ? profile->RivalWingsWeeklyMatchesWon : profile->RivalWingsTotalMatchesWon); pvpMatches = (int)(pvpWeekly ? profile->RivalWingsWeeklyMatches : profile->RivalWingsTotalMatches); }
        }
        notice = write ? IChingModuleText.Get("Text0084") : IChingModuleText.Get("Text0085");
    }

    private Task<OpcodeResult>? opcodeTask;
    private CancellationTokenSource? opcodeCancellation;
    private OpcodeResult? opcodeResult;
    private string opcodeFilter = "";
    private void DrawOpcodeDiagnostics()
    {
        WrapHint(IChingModuleText.Get("Ui2111"));
        if (opcodeTask is { IsCompletedSuccessfully: true }) { opcodeResult = opcodeTask.Result; opcodeTask = null; opcodeCancellation?.Dispose(); opcodeCancellation = null; }
        ImGui.BeginDisabled(!running || opcodeTask != null);
        if (ActionButton(IChingModuleText.Get("Text0086")))
        {
            var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (framework != null)
            {
                string version = framework->GameVersionString;
                opcodeCancellation = new(); opcodeTask = IChingOpcodeLoader.FetchOpcodes(version, opcodeCancellation.Token);
            }
        }
        ImGui.EndDisabled();
        if (opcodeTask != null) Hint(IChingModuleText.Get("Ui2112"));
        if (opcodeResult == null) return;
        Hint(string.Format(IChingModuleText.Get("Ui2113"), opcodeResult.Version, opcodeResult.Entries.Count));
        if (opcodeResult.Error != null) { WrapHint(opcodeResult.Error); return; }
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##opcodeFilter", IChingModuleText.Get("Text0087"), ref opcodeFilter, 128);
        if (ImGui.BeginChild("opcodeList", new Vector2(0, 220 * UiScale)))
        {
            foreach (var entry in opcodeResult.Entries.Where(e => string.IsNullOrWhiteSpace(opcodeFilter) || e.Name.Contains(opcodeFilter, StringComparison.OrdinalIgnoreCase) || e.Opcode.ToString("X4").Contains(opcodeFilter, StringComparison.OrdinalIgnoreCase)).Take(200))
                ImGui.TextUnformatted($"0x{entry.Opcode:X4}   {entry.Name}");
        }
        ImGui.EndChild();
    }
}

public sealed unsafe partial class IChingModule
{
    private delegate uint LineOfSightDelegate(uint actionId, GameObject* source, GameObject* target);
    private Hook<LineOfSightDelegate> lineOfSightHook = null!;
    [ThreadStatic] private static bool checkingLineOfSightTarget;
    private uint LocalLineOfSight(uint actionId, GameObject* source, GameObject* target)
    {
        var result = lineOfSightHook.Original(actionId, source, target);
        if (result == 0 || checkingLineOfSightTarget || !Active || !config.LocalLineOfSight || source == null || target == null ||
            (nint)source != services.Objects.LocalPlayer!.Address || actionId == 0 || ActionManager.MemberFunctionPointers.CanUseActionOnTarget == null) return result;
        // Typed action check only: no engine collision / world raycast hooks.
        try
        {
            checkingLineOfSightTarget = true;
            return ActionManager.CanUseActionOnTarget(actionId, target) ? 0u : result;
        }
        finally { checkingLineOfSightTarget = false; }
    }
    // SetSlotSpeed marks the sequencer dirty so the game applies the value to
    // the animation. Writing OverallSpeed after CalculateAndApplyOverallSpeed
    // returns only changes its cache, not the speed already sent to the model.
    private const float FishingAnimationMultiplier = 10f;
    private bool fishingReady, fishingPlayersNearby;
    private long nextFishingCheck;
    private sealed class FishingSnapshot(nint address, ulong objectId, uint territory)
    {
        public readonly nint Address = address;
        public readonly ulong ObjectId = objectId;
        public readonly uint Territory = territory;
        public readonly float[] Original = new float[2], Written = new float[2];
        public readonly bool[] Owned = new bool[2];
    }
    private FishingSnapshot? fishingSnapshot;

    internal static bool TryFishingSlotSpeed(float current, bool owned, float original, float written,
        out float baseline, out float desired)
    {
        baseline = owned && current == written ? original : current;
        desired = baseline;
        // Paused animations must stay paused; never amplify our previous write.
        if (!float.IsFinite(baseline) || baseline <= 0 || baseline > 20) return false;
        desired = Math.Min(baseline * FishingAnimationMultiplier, 20f);
        return true;
    }

    private void RestoreFishingTimeline()
    {
        fishingReady = false;
        var saved = fishingSnapshot; fishingSnapshot = null;
        if (saved == null || services == null || services.Client.TerritoryType != saved.Territory) return;
        var obj = services.Objects.FirstOrDefault(o => o.Address == saved.Address && o.GameObjectId == saved.ObjectId);
        if (obj is not IBattleChara) return;
        var sequencer = &((Character*)obj.Address)->Timeline.TimelineSequencer;
        for (uint slot = 0; slot < 2; slot++)
            if (saved.Owned[slot] && sequencer->TimelineSpeeds[(int)slot] == saved.Written[slot])
                sequencer->SetSlotSpeed(slot, saved.Original[slot]);
    }

    private void UpdateFishingState()
    {
        var player = services.Objects.LocalPlayer;
        bool fishing = player != null && config.FastFishingAnimation && resources.ContainsKey("钓鱼动画加速") &&
            player.ClassJob.RowId == 18 && services.Condition[ConditionFlag.Gathering];
        if (!fishing || player == null) { RestoreFishingTimeline(); fishingPlayersNearby = false; nextFishingCheck = 0; return; }
        if (Environment.TickCount64 >= nextFishingCheck)
        {
            nextFishingCheck = Environment.TickCount64 + 200;
            float distance = ClampFinite(config.FishingPlayerDistance, 1, 100);
            fishingPlayersNearby = services.Objects.Any(o => o is IPlayerCharacter && o.GameObjectId != player!.GameObjectId && Vector3.DistanceSquared(o.Position, player.Position) <= distance * distance);
        }
        if (config.PauseFishingNearPlayers && fishingPlayersNearby) { RestoreFishingTimeline(); return; }
        if (fishingSnapshot is { } previous && (previous.Address != player!.Address || previous.ObjectId != player.GameObjectId || previous.Territory != services.Client.TerritoryType))
            RestoreFishingTimeline();
        var saved = fishingSnapshot ??= new(player!.Address, player.GameObjectId, services.Client.TerritoryType);
        var sequencer = &((Character*)player.Address)->Timeline.TimelineSequencer;
        // Base and upper-body only. Never speed up other players, mounts, facial
        // animation, or global particle timing.
        for (uint slot = 0; slot < 2; slot++)
        {
            float current = sequencer->TimelineSpeeds[(int)slot];
            if (!TryFishingSlotSpeed(current, saved.Owned[slot], saved.Original[slot], saved.Written[slot], out float baseline, out float desired))
            { saved.Owned[slot] = false; continue; }
            saved.Original[slot] = baseline;
            saved.Written[slot] = desired;
            saved.Owned[slot] = true;
            if (current != desired) sequencer->SetSlotSpeed(slot, desired);
        }
        fishingReady = true;
    }
    private int commandMode, commandInternal;
    private string commandParameter = "", generatedCommand = "";
    private static readonly string[] GeneratedNames = ["最近敌人","敌群中心","地图旗标","坐标","面向","目标缩放","移速","高度偏移","咏唱缩减","复唱缩减","移动读条","功能开关","收起/拔出武器","下坐骑","中断咏唱","切换待机姿势"];
    private static readonly string[] GeneratedVerbs = ["nearest","most","flag","teleport","facing","scale","speed","height","cast","recast","castmove","set","execute 1","execute 101","execute 105","execute 505"];
    private bool DrawRestoredTools()
    {
        bool changed = false;
        using (new Card("fishing", IChingModuleText.Get("Ui2114"), IChingModuleText.Get("Ui2115")))
        {
            changed |= ImGui.Checkbox(IChingModuleText.Get("Text0088"), ref config.FastFishingAnimation);
            ImGui.BeginDisabled(!config.FastFishingAnimation);
            changed |= CompactRow(IChingModuleText.Get("Ui2116"), ref config.PauseFishingNearPlayers, ref config.FishingPlayerDistance, 1, 100, "%.0f m", null);
            ImGui.EndDisabled();
            string fishingStatus = !config.FastFishingAnimation ? IChingModuleText.Get("Text0089") :
                errors.TryGetValue(IChingModuleText.Get("Text0088"), out var fishingError) ? IChingModuleText.Get("Text0090") + fishingError :
                fishingReady ? IChingModuleText.Get("Text0091") :
                config.PauseFishingNearPlayers && fishingPlayersNearby && services.Condition[ConditionFlag.Gathering] ? IChingModuleText.Get("Text0092") :
                IChingModuleText.Get("Text0093");
            WrapHint(fishingStatus);
        }
        if (ImGui.CollapsingHeader(IChingModuleText.Get("Text0094")))
        {
            ImGui.SetNextItemWidth(-1); ImGui.Combo("##commandMode", ref commandMode, string.Join("\0", GeneratedNames) + "\0");
            var mode = Math.Clamp(commandMode, 0, GeneratedVerbs.Length - 1);
            string hint = mode switch { 3 => IChingModuleText.Get("Ui2117"), 4 => IChingModuleText.Get("Ui2118"), 5 => IChingModuleText.Get("Ui2119"), 6 => IChingModuleText.Get("Ui2120"), 7 => IChingModuleText.Get("Ui2121"), 8 => IChingModuleText.Get("Ui2122"), 9 => IChingModuleText.Get("Ui2123"), 10 => IChingModuleText.Get("Ui2124"), 11 => IChingModuleText.Get("Ui2125"), _ => IChingModuleText.Get("Ui2126") };
            bool needsParameter = mode is >= 3 and <= 11;
            if (needsParameter) { ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##commandParameter", hint, ref commandParameter, 256); }
            generatedCommand = "/omni IChingModule " + GeneratedVerbs[mode] + (needsParameter && !string.IsNullOrWhiteSpace(commandParameter) ? " " + commandParameter.Trim() : "");
            WrapHint(generatedCommand);
            if (ActionButton(IChingModuleText.Get("Text0095"))) { ImGui.SetClipboardText(generatedCommand); notice = IChingModuleText.Get("Text0096"); }
            ImGui.BeginDisabled(!Active);
            if (ActionButton(IChingModuleText.Get("Text0097"))) TryHandleCommand(generatedCommand.Substring("/omni IChingModule ".Length));
            ImGui.EndDisabled();
            if (mode == 11) WrapHint(IChingModuleText.Get("Ui2127"));
        }
        if (ImGui.CollapsingHeader(IChingModuleText.Get("Text0098")))
        {
            WrapHint(IChingModuleText.Get("Ui2128"));
            ImGui.SetNextItemWidth(-1);
            var current = InternalCommandCatalog[Math.Clamp(commandInternal, 0, InternalCommandCatalog.Length - 1)];
            if (ImGui.BeginCombo("##internalCatalog", $"{current.Id} · {current.Name}"))
            {
                for (int i = 0; i < InternalCommandCatalog.Length; i++)
                {
                    var item = InternalCommandCatalog[i];
                    ImGui.PushID(i);
                    bool selected = commandInternal == i;
                    if (ImGui.Selectable($"{item.Id} · {item.Name}", selected)) commandInternal = i;
                    if (selected) ImGui.SetItemDefaultFocus();
                    ImGui.SameLine(); DrawCommandAnnotation(item.Description);
                    ImGui.PopID();
                }
                ImGui.EndCombo();
            }
            DrawCommandAnnotation(InternalCommandCatalog[Math.Clamp(commandInternal, 0, InternalCommandCatalog.Length - 1)].Description);
            var entry = InternalCommandCatalog[Math.Clamp(commandInternal, 0, InternalCommandCatalog.Length - 1)];
            bool executable = CanExecuteInternal(entry.Id);
            ImGui.BeginDisabled(!Active || !executable);
            if (ActionButton(IChingModuleText.Get("Text0099"))) TryHandleCommand($"execute {entry.Id}");
            ImGui.EndDisabled();
            ImGui.SameLine();
            Hint(!executable ? IChingModuleText.Get("Ui2129") : !Active ? IChingModuleText.Get("Ui2130") : IChingModuleText.Get("Ui2131"));
        }
        return changed;
    }
    private static void DrawCommandAnnotation(string text)
    {
        float gray = OmniTheme.UsesDarkPalette ? 0.68f : 0.42f;
        ImGui.TextColored(new Vector4(gray, gray, gray, 1f), $"（{text}）");
    }
    private static readonly (string Name, int Id, string Description)[] InternalCommandCatalog = [
        ("DrawOrSheatheWeapon", 1, "拔出或收起武器"),
        ("AutoAttack", 2, "自动攻击"),
        ("Target", 3, "选择目标"),
        ("PVPQuickChat", 5, "PvP 快捷聊天"),
        ("Dismount", 101, "下坐骑"),
        ("SummonPet", 102, "召唤宠物"),
        ("WithdrawPet", 103, "收回宠物"),
        ("StatusOff", 104, "取消状态"),
        ("CancelCast", 105, "中断咏唱"),
        ("RidePillion", 106, "乘坐多人坐骑"),
        ("WithdrawParasol109", 109, "收起阳伞，调用区别未核对"),
        ("WithdrawParasol110", 110, "收起阳伞，调用区别未核对"),
        ("Revive", 200, "复活"),
        ("TerritoryTransport", 201, "区域移动"),
        ("Teleport", 202, "传送"),
        ("AcceptTeleportOffer", 203, "接受传送邀请"),
        ("RequestFriendHouseTeleport", 210, "请求好友住宅传送"),
        ("TeleportToFriendHouse", 211, "传送到好友住宅"),
        ("ReturnIfNotLalafell", 213, "非拉拉菲尔时返回，按名称推测"),
        ("InstantReturn", 214, "立即返回，条件未核对"),
        ("Inspect", 300, "查看装备"),
        ("ChangeTitle", 302, "更改称号"),
        ("RequestCutscene307", 307, "请求过场动画，场景未核对"),
        ("RequestContentsNoteCategory", 310, "请求挑战笔记分类"),
        ("ClearFieldMarkers", 313, "清除场地标点"),
        ("AssignBLUActionToSlot", 315, "设置青魔法技能栏"),
        ("RequestWorldTravel", 316, "请求跨世界移动"),
        ("PlaceFieldMarker", 317, "放置场地标点"),
        ("RemoveFieldMarker", 318, "移除场地标点"),
        ("ResetStrikingDummy", 319, "重置木人战斗"),
        ("SetRetainerMarketPrice", 400, "设置雇员出售价格"),
        ("RequestInventory", 405, "请求背包数据"),
        ("EnterMateriaAttachState", 408, "进入魔晶石镶嵌状态"),
        ("LeaveMateriaAttachState", 410, "退出魔晶石镶嵌状态"),
        ("CancelMateriaMeldRequest", 419, "取消魔晶石镶嵌请求"),
        ("RequestCabinet", 424, "请求收藏柜数据"),
        ("StoreToCabinet", 425, "存入收藏柜"),
        ("RestoreFromCabinet", 426, "从收藏柜取出"),
        ("RepairItem", 434, "修理指定物品"),
        ("RepairEquippedItems", 435, "修理已装备物品"),
        ("RepairAllItems", 436, "修理所有物品"),
        ("ExtractMateria", 437, "精制魔晶石"),
        ("GearsetChange", 441, "切换套装"),
        ("RequestSaddleBag", 444, "请求陆行鸟鞍囊"),
        ("RequestReconstrcutionBuyBack445", 445, "请求重建回购数据，区别未核对"),
        ("RequestReconstrcutionBuyBack446", 446, "请求重建回购数据，区别未核对"),
        ("SendRepairRequest", 450, "发送修理请求"),
        ("CancelRepairRequest", 453, "取消修理请求"),
        ("InterruptEmote", 502, "中断表情动作"),
        ("InterruptEmoteSpecial", 503, "中断特殊表情动作"),
        ("IdlePostureChange", 505, "切换待机姿势"),
        ("IdlePostureEnter", 506, "进入待机姿势"),
        ("IdlePostureExit", 507, "退出待机姿势"),
        ("GimmickJumpEnd", 602, "结束场景机关跳跃"),
        ("EnterSwim", 608, "进入游泳状态"),
        ("LeaveSwim", 609, "退出游泳状态"),
        ("DisableMounting", 612, "禁用坐骑乘坐，按名称推测"),
        ("EnterFlight", 616, "进入飞行状态"),
        ("Craft", 700, "开始制作"),
        ("Fish", 701, "开始钓鱼"),
        ("GimmickJumpStart", 706, "开始场景机关跳跃"),
        ("LoadCraftLog", 710, "加载制作笔记"),
        ("ExitCraft", 711, "退出制作"),
        ("AbandonQuest", 800, "放弃任务"),
        ("RefreshLeveQuest", 801, "刷新理符任务"),
        ("AbandonLeveQuest", 802, "放弃理符任务"),
        ("StartLeveQuest", 804, "开始理符任务"),
        ("Content", 808, "副本相关操作，具体用途未核对"),
        ("FateStart", 809, "开始临危受命，按名称推测"),
        ("FateLoad", 810, "加载临危受命数据"),
        ("FateEnter", 812, "进入临危受命"),
        ("FateLevelSync", 813, "临危受命等级同步"),
        ("FateMobSpawn", 814, "临危受命怪物生成，按名称推测"),
        ("TerritoryTransportFinish", 816, "完成区域移动"),
        ("LeaveDuty", 819, "离开副本"),
        ("StartSoloQuestBattle", 823, "开始单人任务战斗"),
        ("QuestRedo", 824, "任务回顾相关操作"),
        ("InventoryRefresh", 830, "刷新背包"),
        ("RequestCutscene831", 831, "请求过场动画，场景未核对"),
        ("RequestAchievement", 1000, "请求指定成就"),
        ("RequestAllAchievement", 1001, "请求全部成就"),
        ("RequestNearCompletionAchievement", 1002, "请求接近完成的成就"),
        ("RequestLotteryData", 1105, "请求住宅抽签数据"),
        ("RequestPlacardData", 1106, "请求住宅门牌数据"),
        ("RequestHousingAreaData", 1107, "请求住宅区数据"),
        ("StoreFurniture", 1112, "存放家具"),
        ("RestoreFurniture", 1113, "取出家具"),
        ("RequestHousingName", 1114, "请求住宅名称"),
        ("RequestHousingGreeting", 1115, "请求住宅欢迎语"),
        ("RequestHousingGuestAccess", 1117, "请求访客权限"),
        ("SaveHousingGuestAccess", 1118, "保存访客权限"),
        ("RequestHousingEstateTag", 1119, "请求住宅标签"),
        ("SaveHousingEstateTag", 1120, "保存住宅标签"),
        ("MoveToHouseFrontGate", 1122, "移动到住宅门前"),
        ("FurnishState", 1123, "家具布置状态相关操作"),
        ("ViewHouseDetail", 1126, "查看住宅详情"),
        ("AdjustHouseLight", 1137, "调整住宅灯光"),
        ("RefreshFCMaterialDelivery", 1143, "刷新工房材料提交数据"),
        ("RefreshSubmarineInfo", 1144, "刷新潜水艇数据"),
        ("SetHouseBackgroundMusic", 1145, "设置住宅背景音乐"),
        ("Furnish", 1150, "布置家具"),
        ("RepairSubmarinePart", 1153, "修理潜水艇部件"),
        ("HouseInteriorDesignRequest", 1169, "请求住宅内装"),
        ("HouseInteriorDesignChange", 1170, "更换住宅内装"),
        ("CollectTrophyCrystal", 1200, "领取奖杯水晶"),
        ("SelectPVPRoleAction", 1201, "选择 PvP 职能技能"),
        ("RequestContentsNote", 1301, "请求挑战笔记"),
        ("RequestRetainerVentureTime", 1400, "请求雇员探险时间"),
        ("RepairItemNPC", 1600, "NPC 修理指定物品"),
        ("RepairAllItemsNPC", 1601, "NPC 修理所有物品"),
        ("RepairEquippedItemsNPC", 1602, "NPC 修理已装备物品"),
        ("BuddyAction", 1700, "搭档陆行鸟行动"),
        ("BuddyEquip", 1701, "更换搭档装备"),
        ("BuddyLearnSkill", 1702, "学习搭档技能"),
        ("RequestGSGeneral", 1850, "请求金碟通用数据"),
        ("RequestGSChocobo", 1900, "请求陆行鸟竞赛数据"),
        ("StartDutyRecord", 1980, "开始副本录像"),
        ("EndDutyRecord", 1981, "结束副本录像"),
        ("RequestGSLordofVerminion", 2010, "请求萌宠之王数据"),
        ("EnableAutoJoinNoviceNetwork", 2102, "自动加入新人频道"),
        ("SendDuel", 2200, "发送决斗邀请"),
        ("RequestDuel", 2201, "请求决斗，阶段未核对"),
        ("ConfirmDuel", 2202, "确认决斗"),
        ("WondrousTailsConfirm", 2253, "天书奇谈确认"),
        ("WondrousTailsOperate", 2253, "天书奇谈操作"),
        ("RequestPrismBox", 2350, "请求幻化柜数据"),
        ("RestorePrsimBoxItem", 2352, "取出幻化柜物品"),
        ("RequestGlamourPlates", 2355, "请求幻化模板"),
        ("EnterGlamourPlateState", 2356, "进入幻化模板编辑"),
        ("ApplyGlamourPlate", 2357, "应用幻化模板"),
        ("FashionCheckEntryReward", 2450, "领取时尚品鉴参与奖励"),
        ("FashionCheckBonusReward", 2451, "领取时尚品鉴额外奖励"),
        ("BuybackReconstrcutionItem", 2501, "回购重建物品，按名称推测"),
        ("RequestGSMahjong", 2550, "请求多玛方城战数据"),
        ("RequstAOZNotebook", 2601, "请求青魔法冒险录"),
        ("RequestTrustedFriend", 2651, "请求亲信战友数据"),
        ("RequestDutySupport", 2653, "请求任务支援数据"),
        ("SendDutySupport", 2654, "提交任务支援操作"),
        ("Desynthesize", 2800, "分解物品"),
        ("BozjaUseFromHolster", 2950, "使用失传技能储存箱技能"),
        ("RequestPortraits", 3200, "请求肖像数据"),
        ("MJISetMode", 3250, "切换无人岛模式"),
        ("MJISetModeParam", 3251, "设置无人岛模式参数"),
        ("MJISettingPanelToggle", 3252, "切换无人岛设置面板"),
        ("MJIWorkshopRequest", 3254, "请求无人岛工坊数据"),
        ("MJIWorkshopRequestItem", 3258, "请求无人岛工坊物品"),
        ("MJIWorkshopAssign", 3259, "安排无人岛工坊生产"),
        ("MJIWorkshopCancel", 3260, "取消无人岛工坊生产"),
        ("MJISetRestCycles", 3261, "设置工坊休息日"),
        ("MJIGranaryCollect", 3262, "领取无人岛采集物"),
        ("MJIGranaryViewDestinations", 3263, "查看无人岛采集目的地"),
        ("MJIGranaryAssign", 3264, "安排无人岛采集探索"),
        ("MJIReleaseMinion", 3265, "放出无人岛宠物"),
        ("MJIReleaseAnimal", 3268, "放生无人岛动物"),
        ("MJICollectAnimalLeavings", 3269, "收取指定动物产物"),
        ("MJICollectAllAnimalLeavings", 3271, "收取所有动物产物"),
        ("MJIEntrustAnimal", 3272, "委托照料动物"),
        ("MJIRecallMinion", 3277, "召回无人岛宠物"),
        ("MJIFarmEntrustSingle", 3279, "委托照料指定农田"),
        ("MJIFarmDismiss", 3280, "解除农田照料委托"),
        ("MJIFarmCollectSingle", 3281, "收获指定农田"),
        ("MJIFarmCollectAll", 3282, "收获所有农田"),
        ("MJIFavorStateRequest", 3292, "请求无人岛特殊委托状态"),
        ("WKSChangeMode", 3400, "切换宇宙探索模式"),
        ("WKSEndInteraction1", 3401, "结束宇宙探索交互，区别未核对"),
        ("WKSEndInteraction2", 3402, "结束宇宙探索交互，区别未核对"),
        ("WKSStartMission", 3440, "开始宇宙探索任务"),
        ("WKSCompleteMission", 3441, "完成宇宙探索任务"),
        ("WKSAbandonMission", 3442, "放弃宇宙探索任务"),
        ("WKSStartLottery", 3450, "开始宇宙探索抽奖"),
        ("WKSChooseLottery", 3451, "选择抽奖选项"),
        ("WKSEndLottery", 3452, "结束宇宙探索抽奖"),
        ("WKSRequestSuccesses", 3460, "请求宇宙探索成果"),
        ("WKSRequestMecha", 3478, "请求宇宙探索机甲操作"),
        ("RollDice", 9000, "掷骰子"),
        ("Retainer", 9003, "雇员相关操作，具体用途未核对"),
        ("AroundRangeSetMode", 9005, "设置周围范围模式，具体用途未核对")
    ];
    private static bool CanExecuteInternal(int id) => id is 1 or 101 or 105 or 505;
    private bool TryExtendedCommand(string arguments, out bool handled)
    {
        handled = true;
        var args = arguments.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (args.Length == 0) return false;
        string verb = args[0].ToLowerInvariant();
        if (verb is not ("teleport" or "facing" or "scale" or "speed" or "height" or "cast" or "recast" or "castmove" or "set" or "execute")) return false;
        Run(() =>
        {
            if (verb == "set")
            {
                if (args.Length != 3 || args[2].ToLowerInvariant() is not ("on" or "off")) throw new InvalidOperationException(IChingModuleText.Get("Text0100"));
                SetCommandFeature(args[1].ToLowerInvariant(), args[2].Equals("on", StringComparison.OrdinalIgnoreCase));
                SaveCommandSettings(); return;
            }
            if (verb == "execute")
            {
                if (args.Length != 2 || !int.TryParse(args[1], out int id) || !CanExecuteInternal(id)) throw new InvalidOperationException(IChingModuleText.Get("Text0101"));
                if (GameMain.MemberFunctionPointers.ExecuteCommand == null) throw new InvalidOperationException(IChingModuleText.Get("Text0102"));
                if (id == 101 && !services.Condition[ConditionFlag.Mounted]) throw new InvalidOperationException(IChingModuleText.Get("Text0103"));
                if (id == 105 && !services.Objects.LocalPlayer!.IsCasting) throw new InvalidOperationException(IChingModuleText.Get("Text0104"));
                notice = GameMain.ExecuteCommand(id) ? IChingModuleText.Get("Text0105") : IChingModuleText.Get("Text0106"); return;
            }
            if (verb == "teleport")
            {
                if (args.Length != 4) throw new InvalidOperationException(IChingModuleText.Get("Text0107"));
                destination = new(ParseCommandNumber(args[1], -100000, 100000), ParseCommandNumber(args[2], -100000, 100000), ParseCommandNumber(args[3], -100000, 100000)); Teleport(); return;
            }
            if (args.Length != 2) throw new InvalidOperationException(IChingModuleText.Get("Text0108"));
            switch (verb)
            {
                case "facing": facing = ParseCommandNumber(args[1], -MathF.PI, MathF.PI); SetFacing(); return;
                case "scale": scale = ParseCommandNumber(args[1], 0.1f, 5); SetScale(); return;
                case "speed": config.SpeedAdd = ParseCommandNumber(args[1], 0, 20); config.Speed = true; break;
                case "height": config.HeightAdd = ParseCommandNumber(args[1], -100, 100); config.HeightOffset = true; break;
                case "cast": config.CastSeconds = ParseCommandNumber(args[1], 0, 10); config.Cast = true; break;
                case "recast": config.RecastSeconds = ParseCommandNumber(args[1], 0, 60); config.Recast = true; break;
                case "castmove": config.CastMoveWindow = ParseCommandNumber(args[1], 0, 10); config.CastMovement = true; break;
            }
            SaveCommandSettings();
        });
        return true;
    }
    private static float ClampFinite(float value, float min, float max) => float.IsFinite(value) ? Math.Clamp(value, min, max) : min <= 0 && max >= 0 ? 0 : min;
    private static float ParseCommandNumber(string text, float min, float max)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value) || value < min || value > max)
            throw new InvalidOperationException(string.Format(IChingModuleText.Get("Text0109"), min, max));
        return value;
    }
    private void SaveCommandSettings()
    {
        SyncFeatures();
        saveHostConfig?.Invoke();
        notice = errors.Count == 0 ? IChingModuleText.Get("Text0110") : IChingModuleText.Get("Text0111");
    }
    private void SetCommandFeature(string name, bool value)
    {
        switch (name)
        {
            case "speed": config.Speed = value; break;
            case "height": config.HeightOffset = value; break;
            case "deep": config.DeepDungeonReturn = value; break;
            case "move": config.MovePermission = value; break;
            case "knock": config.AntiKnock = value; break;
            case "fall": config.NoFallDamage = value; break;
            case "nofall": config.NoFall = value; break;
            case "range": config.ActionRange = value; break;
            case "radius": config.ActorRadius = value; break;
            case "backswing": config.NoBackswing = value; break;
            case "dash": config.NoActionMove = value; break;
            case "cast": config.Cast = value; break;
            case "recast": config.Recast = value; break;
            case "mudra": config.Mudra = value; break;
            case "castmove": config.CastMovement = value; break;
            case "charm": config.IgnoreCharm = value; break;
            case "status": config.FilterMovementStatuses = value; break;
            case "radar": config.Radar = value; break;
            case "cleartarget": config.ClearTarget = value; break;
            case "grand": config.MaxGrandCompanyRank = value; break;
            case "privacy": config.HideHudValues = value; break;
            case "damage": config.FakeDamage = value; break;
            case "dialogue": config.ReplaceDisconnectText = value; break;
            case "los": config.LocalLineOfSight = value; break;
            case "fishing": config.FastFishingAnimation = value; break;
            default: throw new InvalidOperationException(IChingModuleText.Get("Text0112"));
        }
    }
}

internal sealed record OpcodeResult(string Version, List<(string Name, int Opcode)> Entries, string? Error);
public sealed class IChingFollowedPlayer
{
    public string Name { get; set; } = "";
    public ulong ContentId { get; set; }
    public ushort HomeWorld { get; set; }
}
internal static class IChingOpcodeLoader
{
    internal static async Task<OpcodeResult> FetchOpcodes(string version, CancellationToken cancellation)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 5 * 1024 * 1024 };
            var json = await client.GetStringAsync("https://raw.githubusercontent.com/extrant/FFXIV.EXE/main/Opcode/all_opcodes.json", cancellation).ConfigureAwait(false);
            return ParseOpcodes(version, json);
        }
        catch (OperationCanceledException) { return new(version, [], "读取已取消或超时。"); }
        catch (Exception e) { return new(version, [], "读取失败：" + e.Message); }
    }
    internal static OpcodeResult ParseOpcodes(string version, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(version, out var values)) return new(version, [], "在线映射未包含当前版本，未使用其他版本的数据。");
            var entries = new List<(string Name, int Opcode)>();
            foreach (var property in values.EnumerateObject())
            {
                if (!(property.Name.StartsWith("UP_") || property.Name.StartsWith("DOWN_")) || property.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var value in property.Value.EnumerateArray())
                    if (value.TryGetInt32(out int id) && id > 0 && id <= ushort.MaxValue) entries.Add((property.Name, id));
            }
            return new(version, entries.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Opcode).ToList(), null);
        }
        catch (Exception e) { return new(version, [], "映射格式无效：" + e.Message); }
    }
}

internal static class IChingModuleText
{
    internal static readonly OmniToolbox.UI.ModuleLocalization Catalog = OmniToolbox.UI.ModuleLocalization.Create(
        new System.Collections.Generic.Dictionary<string,string>
        {
            ["Text0001"] = "尚未执行操作",
            ["Text0002"] = "无法取得 Dalamud 服务。",
            ["Text0003"] = "IChing 停用清理失败，继续释放其余功能",
            ["Text0004"] = "IChing 释放 {0} 失败",
            ["Text0005"] = "IChing 初始化 {0} 失败",
            ["Text0006"] = "函数地址不在当前客户端代码段，未安装 Hook。",
            ["Text0007"] = "目标判定函数尚未解析。",
            ["Text0008"] = "此宿主未提供面板保存入口；请在 Omni 模块设置里修改以保存配置。",
            ["Text0009"] = "配置保存失败：{0}",
            ["Text0010"] = "拖动调整，Ctrl + 单击可直接输入数值。",
            ["Text0011"] = "打开 IChing 独立面板",
            ["Text0012"] = "宏：{0}",
            ["Text0013"] = "查看 {0} 项问题",
            ["Text0014"] = "就绪 · {0} 唤醒面板",
            ["Text0015"] = "{0} 项初始化问题 · 详情见诊断",
            ["Text0016"] = "突进位移管理",
            ["Text0017"] = "按技能 ID 添加",
            ["Text0018"] = "已添加 {0} / 动画 {1}",
            ["Text0019"] = "技能不存在或没有结束动画。",
            ["Text0020"] = "关注当前玩家目标",
            ["Text0021"] = "玩家",
            ["Text0022"] = "操作",
            ["Text0023"] = "{0} · 世界 {1}",
            ["Text0024"] = "移除##id{0}-{1}-{2}",
            ["Text0025"] = "移除##follow{0}",
            ["Text0026"] = "按状态 ID 添加",
            ["Text0027"] = "复制最近敌人指令",
            ["Text0028"] = "最近敌人指令已复制。",
            ["Text0029"] = "复制敌群中心指令",
            ["Text0030"] = "敌群中心指令已复制。",
            ["Text0031"] = "类型",
            ["Text0032"] = "状态名称",
            ["Text0033"] = "删除##{0}{1}",
            ["Text0034"] = "应用坐标",
            ["Text0035"] = "读取当前位置",
            ["Text0036"] = "应用面向",
            ["Text0037"] = "读取地图旗标",
            ["Text0038"] = "前往地图旗标",
            ["Text0039"] = "应用目标缩放",
            ["Text0040"] = "恢复缩放为 1",
            ["Text0041"] = "重试失败的功能",
            ["Text0042"] = "复制诊断信息",
            ["Text0043"] = "诊断信息已复制。",
            ["Text0044"] = "网络 Opcode",
            ["Text0045"] = "恢复默认并关闭全部功能",
            ["Text0046"] = "已恢复默认设置。",
            ["Text0047"] = "请先启用模块并登录角色。",
            ["Text0048"] = "IChing 工具操作失败",
            ["Text0049"] = "坐标必须是有限数值。",
            ["Text0050"] = "已写入本地坐标：{0}（服务器可能纠正）",
            ["Text0051"] = "已写入本地面向：{0:F3}",
            ["Text0052"] = "请先选中目标。",
            ["Text0053"] = "目标已卸载或缩放值无效。",
            ["Text0054"] = "已修改目标本地模型缩放。",
            ["Text0055"] = "鼠标位置无法转换为世界坐标。",
            ["Text0056"] = "鼠标附近没有可攻击目标。",
            ["Text0057"] = "已选中 {0}",
            ["Text0058"] = "未知指令，请使用面板的指令生成器。",
            ["Text0059"] = "打开 IChing 独立面板；show/hide 显示/隐藏，其他参数同 /omni IChingModule。",
            ["Text0060"] = "失败",
            ["Text0061"] = "距离 %.1f m",
            ["Text0062"] = "时间 %.2f s",
            ["Text0063"] = "当前版本的移动 Opcode 未能确认，功能未启用。",
            ["Text0064"] = "{0} 层",
            ["Text0065"] = "草原\u241f尘封\u241f",
            ["Text0066"] = "选中点位",
            ["Text0067"] = "请先进入对应战场。",
            ["Text0068"] = "此点位尚未加载或当前不可选中。",
            ["Text0069"] = "已选中点位 {0}。",
            ["Text0070"] = "请先在地图放置旗标。",
            ["Text0071"] = "旗标位于其他区域，请先进入对应区域。",
            ["Text0072"] = "已填入旗标 X/Z 与当前高度；未取得地面高度，请确认 Y 后点击应用坐标。",
            ["Text0073"] = "已读取旗标及地面高度。",
            ["Text0074"] = "IChing · 隐私显示",
            ["Text0075"] = "仅在本地显示的断线提示",
            ["Text0076"] = "PVP 战绩 · 本地显示",
            ["Text0077"] = "水晶冲突\u241f纷争前线\u241f烈羽争锋\u241f",
            ["Text0078"] = "本周",
            ["Text0079"] = "同时设置最高段位为水晶",
            ["Text0080"] = "读取当前战绩",
            ["Text0081"] = "应用本地显示",
            ["Text0082"] = "战绩数据尚未就绪。",
            ["Text0083"] = "本周前三名次数合计不能超过 65535。",
            ["Text0084"] = "已应用本地战绩显示。",
            ["Text0085"] = "已读取当前战绩。",
            ["Text0086"] = "读取当前版本映射",
            ["Text0087"] = "筛选名称或十六进制编号",
            ["Text0088"] = "钓鱼动画加速",
            ["Text0089"] = "未启用",
            ["Text0090"] = "初始化失败：",
            ["Text0091"] = "已应用到本人的钓鱼动画",
            ["Text0092"] = "已暂停：附近有其他玩家",
            ["Text0093"] = "等待本人进入钓鱼状态",
            ["Text0094"] = "指令生成器",
            ["Text0095"] = "复制指令",
            ["Text0096"] = "已复制指令。",
            ["Text0097"] = "执行",
            ["Text0098"] = "原版内部指令目录（参考）",
            ["Text0099"] = "执行所选指令",
            ["Text0100"] = "用法：set 功能 on/off",
            ["Text0101"] = "已核对的内部指令：1、101、105、505。其他指令未开放。",
            ["Text0102"] = "内部指令函数尚未解析。",
            ["Text0103"] = "当前没有骑乘坐骑。",
            ["Text0104"] = "当前没有咏唱。",
            ["Text0105"] = "内部指令已受理。",
            ["Text0106"] = "当前状态不允许此操作。",
            ["Text0107"] = "用法：teleport X Y Z",
            ["Text0108"] = "此指令需要一个数值参数。",
            ["Text0109"] = "数值范围为 {0}～{1}，不接受 NaN/Infinity。",
            ["Text0110"] = "已应用并保存设置。",
            ["Text0111"] = "设置已保存；初始化失败的项目见诊断。",
            ["Text0112"] = "未知功能名，请查看指令生成器中的名称列表。",
            ["Title"] = "IChing",
            ["Description"] = "IChing 功能与游戏界面辅助。"
        ,
            ["Ui2001"] = "移动",
            ["Ui2002"] = "战斗",
            ["Ui2003"] = "雷达",
            ["Ui2004"] = "目标",
            ["Ui2005"] = "工具",
            ["Ui2006"] = "显示",
            ["Ui2007"] = "诊断",
            ["Ui2008"] = "移动",
            ["Ui2009"] = "移速调整",
            ["Ui2010"] = "移速",
            ["Ui2011"] = "最大加速度",
            ["Ui2012"] = "强制移动",
            ["Ui2013"] = "最大加速度",
            ["Ui2014"] = "强制移动",
            ["Ui2015"] = "防击退",
            ["Ui2016"] = "防击退",
            ["Ui2017"] = "掉落无伤",
            ["Ui2018"] = "无掉落",
            ["Ui2019"] = "掉落无伤",
            ["Ui2020"] = "无掉落",
            ["Ui2021"] = "范围与动作",
            ["Ui2022"] = "技能距离",
            ["Ui2023"] = "技能距离",
            ["Ui2024"] = "当前目标圈",
            ["Ui2025"] = "后摇可移动",
            ["Ui2026"] = "本地视线判定",
            ["Ui2027"] = "后摇移动",
            ["Ui2028"] = "本地视线判定",
            ["Ui2029"] = "咏唱与复唱",
            ["Ui2030"] = "复唱缩减",
            ["Ui2031"] = "复唱缩减",
            ["Ui2032"] = "快速结印",
            ["Ui2033"] = "咏唱缩减",
            ["Ui2034"] = "咏唱缩减",
            ["Ui2035"] = "PVP 动画锁",
            ["Ui2036"] = "突进位移管理",
            ["Ui2037"] = "通过技能的结束动画识别位移；多个技能可能共用同一动画。",
            ["Ui2038"] = "突进无位移",
            ["Ui2039"] = "列表作为允许位移的例外",
            ["Ui2040"] = "屏蔽列表以外的全部动画。",
            ["Ui2041"] = "仅屏蔽列表中的动画。空列表不会拦截位移。",
            ["Ui2042"] = "战场雷达",
            ["Ui2043"] = "当前扫描到 {0} 个敌方玩家。",
            ["Ui2044"] = "进入 PVP 区域后开始显示敌方玩家。",
            ["Ui2045"] = "战场雷达",
            ["Ui2046"] = "可攻击目标判定",
            ["Ui2047"] = "连线",
            ["Ui2048"] = "职业图标",
            ["Ui2049"] = "战意图标",
            ["Ui2050"] = "关注名单",
            ["Ui2051"] = "优先按角色 ContentId 匹配；缺少 ID 时使用姓名与服务器。旧姓名名单继续兼容。",
            ["Ui2052"] = "尚无关注玩家。选中玩家后点击上方按钮。",
            ["Ui2053"] = "目标状态过滤",
            ["Ui2054"] = "当前目标含名单中的状态时，自动取消选中。",
            ["Ui2055"] = "自动取消选中",
            ["Ui2056"] = "鼠标选敌",
            ["Ui2057"] = "将下方指令放入宏或快捷键，鼠标停在游戏场景上执行。",
            ["Ui2058"] = "最近敌人：选择距离鼠标世界坐标最近的可攻击目标。\n敌群中心：选择候选敌人的质心附近目标。",
            ["Ui2059"] = "选敌搜索半径（与雷达共用）",
            ["Ui2060"] = "名单为空。输入 ID 后点击添加。",
            ["Ui2061"] = "技能结束动画",
            ["Ui2062"] = "未知状态",
            ["Ui2063"] = "状态",
            ["Ui2064"] = "坐标与面向",
            ["Ui2065"] = "读取当前位置后调整 X / Y / Z。Y 为高度；面向单位为弧度。",
            ["Ui2066"] = "世界坐标 X / Y / Z",
            ["Ui2067"] = "面向",
            ["Ui2068"] = "目标模型",
            ["Ui2069"] = "仅调整本地显示。停用模块不会自动恢复一次性写入。",
            ["Ui2070"] = "当前目标：{0}",
            ["Ui2071"] = "请先选中目标。",
            ["Ui2072"] = "缩放倍数",
            ["Ui2073"] = "运行诊断",
            ["Ui2074"] = "已初始化表示创建成功；实际效果仍需游戏内验证。",
            ["Ui2075"] = "暂无初始化错误。",
            ["Ui2076"] = "未迁移功能",
            ["Ui2077"] = "功能补齐情况见随附的本地调试说明。",
            ["Ui2078"] = "钓鱼动画通过原生动画槽更新实现本地快速播放，未照搬 DLL 的资源替换。世界射线、InvulnerableMode 与未知参数的内部指令未启用；方法体受保护，不能确认行为。\n部分旧版 Hook 尚需游戏内核验；详见功能还原说明。",
            ["Ui2079"] = "配置维护",
            ["Ui2080"] = "关闭所有功能并清空名单；一次性坐标与缩放写入不会撤销。",
            ["Ui2081"] = "高度",
            ["Ui2082"] = "高度偏移",
            ["Ui2083"] = "高度偏移",
            ["Ui2084"] = "深层迷宫自动回地面",
            ["Ui2085"] = "移动限制",
            ["Ui2086"] = "忽略魅惑恐惧",
            ["Ui2087"] = "滑冰状态过滤",
            ["Ui2088"] = "魅惑与恐惧",
            ["Ui2089"] = "状态过滤",
            ["Ui2090"] = "移动读条",
            ["Ui2091"] = "末尾 %.2f s",
            ["Ui2092"] = "移动读条",
            ["Ui2093"] = "战场点位",
            ["Ui2094"] = "选中已加载的对应点位；不会自动移动或交互。",
            ["Ui2095"] = "本地显示",
            ["Ui2096"] = "仅修改当前客户端的显示，不修改服务器记录。",
            ["Ui2097"] = "最大军衔（本地）",
            ["Ui2098"] = "隐藏金币、HP/MP 与经验数值",
            ["Ui2099"] = "替换伤害与治疗飘字",
            ["Ui2100"] = "飘字数值",
            ["Ui2101"] = "自定义断线提示",
            ["Ui2102"] = "一次性修改本地战绩；重新取得服务器数据后会覆盖。水晶组两项分别是练习赛与排位赛胜场。",
            ["Ui2103"] = "练习赛胜场",
            ["Ui2104"] = "第一名",
            ["Ui2105"] = "胜场",
            ["Ui2106"] = "排位赛胜场",
            ["Ui2107"] = "第二名",
            ["Ui2108"] = "第三名",
            ["Ui2109"] = "总场次",
            ["Ui2110"] = "最高水晶积分",
            ["Ui2111"] = "按当前游戏版本读取作者提供的在线映射，仅用于诊断，不自动发送数据包。移动读条使用本地签名扫描的 Opcode。",
            ["Ui2112"] = "正在读取…",
            ["Ui2113"] = "游戏版本：{0} · {1} 条映射",
            ["Ui2114"] = "钓鱼动画",
            ["Ui2115"] = "勾选后快速播放本人的钓鱼动作；不改变服务器咬钩时间。",
            ["Ui2116"] = "附近玩家暂停",
            ["Ui2117"] = "X Y Z，例如 10 0 20",
            ["Ui2118"] = "弧度，例如 1.57",
            ["Ui2119"] = "缩放倍数，0.1～5",
            ["Ui2120"] = "增加速度，0～20",
            ["Ui2121"] = "高度偏移，-100～100",
            ["Ui2122"] = "缩减秒数，0～10",
            ["Ui2123"] = "缩减秒数，0～60",
            ["Ui2124"] = "末尾窗口秒数，0～10",
            ["Ui2125"] = "功能 on/off，例如 radar on",
            ["Ui2126"] = "此指令不需要参数",
            ["Ui2127"] = "功能名：speed、height、deep、move、knock、fall、nofall、range、radius、backswing、dash、cast、recast、mudra、castmove、charm、status、radar、cleartarget、grand、privacy、damage、dialogue、los、fishing。",
            ["Ui2128"] = "DLL 中的完整枚举仅作参考。已核对的四项可执行；其他指令缺少上下文与参数校验，不直接调用。",
            ["Ui2129"] = "暂不可用",
            ["Ui2130"] = "请先进入游戏",
            ["Ui2131"] = "可执行"
});
    private static System.WeakReference<OmniToolbox.Common.Module.Abstractions.OmniModuleBase>? owner;
    private static TreeTools.Lifecycle.FeatureLifetime? lifetime;
internal static void Bind(OmniToolbox.Common.Module.Abstractions.OmniModuleBase module, TreeTools.Lifecycle.FeatureLifetime scope)
{
    owner = new(module);
    lifetime = scope;
}
internal static System.Threading.Tasks.Task Track(System.Threading.Tasks.Task task)
{
    var scope = lifetime;
    if (!task.IsCompleted && scope is { IsStopping: false })
    {
        try { scope.Track(task); }
        catch (System.InvalidOperationException) when (scope.IsStopping) { }
    }
    return task;
}
internal static System.Threading.Tasks.Task<T> Track<T>(System.Threading.Tasks.Task<T> task)
{
    Track((System.Threading.Tasks.Task)task);
    return task;
}
    internal static string Get(string key)
    {
        if (owner != null && owner.TryGetTarget(out var module))
            return OmniToolbox.UI.OmniLoc.Get(module, key).Replace('\u241f', '\0');
        return Catalog.Get(key, OmniToolbox.UI.OmniLoc.CurrentLanguage).Replace('\u241f', '\0');
    }
}
