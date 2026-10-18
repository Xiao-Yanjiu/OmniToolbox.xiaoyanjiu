// SPDX-License-Identifier: MIT
// Native TryOn slot 2 preview. The agent owns its view and rendering lifecycle.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Helpers;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;
using Item = Lumina.Excel.Sheets.Item;
using CharaMakeCustomize = Lumina.Excel.Sheets.CharaMakeCustomize;

namespace OmniToolbox.LocalGlamourPreview;

internal sealed unsafe class BetterGlamourPortraitPreview : IDisposable
{
    private OmniPreviewContext? context;
    private bool currentTextureValid;
    private readonly RemotePreviewFrames phoneFrames = new();
    private string cachedPhoneStatus="{\"ready\":false,\"message\":\"正在准备预览\"}";
    public string PhoneFrame() => phoneFrames.Read(Status());
    public string CachedPhoneFrame()=>phoneFrames.Read(System.Threading.Volatile.Read(ref cachedPhoneStatus));
    private bool enabled, reading, keepEmpty = true, hideHead = false, hideWeapon = false, visor = false, ears = false, drawn = false;
    private int generation, selected;
    private readonly ConcurrentQueue<Action> commands = new();
    private Plan[] plans = Array.Empty<Plan>();
    private CharaView* view;
    private AgentTryon* tryon;
    private bool ownsTryon, modelSubmitted, outfitVerified, previousSaveOutfit, previousDisplayGear;
    private AgentTryon.TryOnItem[]? savedTryonItems;
    private nint player, texture, image, hiddenTryonRoot;
    private bool savedDisableShowOnOpen, savedDisableFocusOnShow;
    private bool closingTryon;
    private byte savedTryonAlpha;
    private nint guardedTryonAddon;
    private int suppressedDraws;
    private uint renderIndex;
    private bool shown;
    private int frameWidth, frameHeight, verifiedFrames;
    private double loadingSeconds, customizationRetryAt;
    private int customizationAttempts;
    private float ratio = 0.75f, yaw, pitch, zoom, panX, panY;
    private CharaViewModelData desired;
    private readonly bool[] mask = new bool[10];
    private string phase = "idle", message = "读取 Omni 方案，选择后预览；无需先在 Omni 中应用。", currentPlan = "";
    private long requestId;
    private ICallGateProvider<string>? listIpc, statusIpc;
    private ICallGateProvider<string, string>? requestIpc;
    private ICallGateProvider<nint, int, int, long, object>? frameIpc;
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public sealed class Part { public int Slot; public uint ItemId; public byte Dye0, Dye1; }
    public sealed class Plan { public string Name = ""; public ushort Glasses; public uint Hairstyle; public Part[] Items = Array.Empty<Part>(); }
    public sealed class Request
    {
        public string Command = "";
        public int PresetIndex = 0;
        public Plan? Plan;
        public float Yaw = 0, Pitch = 0, Zoom = 0, PanX = 0, PanY = 0;
    }

    public void Initialize(OmniPreviewContext c) => context = c;
    public void Enable()
    {
        if (enabled || context == null) return;
        enabled = true; generation++;
        try
        {
            listIpc = context.PluginInterface.GetIpcProvider<string>("Omni.BetterGlamourPreview.List.V1");
            statusIpc = context.PluginInterface.GetIpcProvider<string>("Omni.BetterGlamourPreview.Status.V1");
            requestIpc = context.PluginInterface.GetIpcProvider<string, string>("Omni.BetterGlamourPreview.Request.V1");
            frameIpc = context.PluginInterface.GetIpcProvider<nint, int, int, long, object>("Omni.BetterGlamourPreview.Frame.V1");
            listIpc.RegisterFunc(() => JsonSerializer.Serialize(plans, Json));
            statusIpc.RegisterFunc(Status);
            requestIpc.RegisterFunc(Submit);
            context.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, new[] { "Tryon", "FittingRoom" }, OnTryonLifecycle);
            context.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, new[] { "Tryon", "FittingRoom" }, OnTryonLifecycle);
            context.Framework.Update += OnFrame;
        }
        catch { Disable(); throw; }
    }
    public void Disable()
    {
        if (!enabled) return;
        enabled = false; generation++; reading = false; pendingPreset = null;
        if (context != null) context.Framework.Update -= OnFrame;
        listIpc?.UnregisterFunc(); statusIpc?.UnregisterFunc(); requestIpc?.UnregisterFunc();
        listIpc = null; statusIpc = null; requestIpc = null; frameIpc = null;
        while (commands.TryDequeue(out _)) { }
        if (context != null && !context.Framework.IsInFrameworkUpdateThread)
            context.Framework.RunOnFrameworkThread(() => { Stop(); context.AddonLifecycle.UnregisterListener(OnTryonLifecycle); });
        else { Stop(); context?.AddonLifecycle.UnregisterListener(OnTryonLifecycle); }
    }
    public void Dispose() => Disable();
    public bool TryHandleCommand(string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "open": return true;
            case "reload": commands.Enqueue(ReadPlans); return true;
            case "preview": commands.Enqueue(() => PreviewSelected()); return true;
            case "stop": commands.Enqueue(Stop); return true;
            default: return false;
        }
    }

    public string Status() => JsonSerializer.Serialize(new { version = "1.4.4", phase = pendingPreset == null ? phase : "loading", message = Message, currentPlan,
        requestId, ready = currentTextureValid && pendingPreset == null && shown && image != 0 && outfitVerified, displayReady = shown && image != 0, outfitVerified, modelSubmitted, renderIndex, width = frameWidth, height = frameHeight,
        appliesToPlayer = false, usesNativeTryOn = true, closingTryon, suppressedDraws }, Json);

    // All external controls pass through this bounded, framework-thread command queue.
    public string Submit(string json)
    {
        if (!enabled) return "{\"accepted\":false,\"error\":\"disabled\"}";
        try
        {
            if (json.Length > 64 * 1024 || commands.Count >= 32) throw new InvalidDataException("请求过大或队列已满。");
            var r = JsonSerializer.Deserialize<Request>(json, Json) ?? throw new InvalidDataException("空请求。");
            ValidateRequest(r);
            int ticket = generation;
            commands.Enqueue(() => {
                if (!enabled || ticket != generation) return;
                switch (r.Command)
                {
                    case "reload": ReadPlans(); break;
                    case "preview": if (r.Plan != null) Begin(r.Plan); else { selected = r.PresetIndex; PreviewSelected(); } break;
                    case "stop": Stop(); break;
                    case "camera": yaw += RotationPixels(r.Yaw); pitch += RotationPixels(r.Pitch); zoom += r.Zoom * 5; panX += r.PanX * 100; panY += r.PanY * 100; break;
                    case "resetCamera": if (view != null && shown) view->ResetPositions(); break;
                }
            });
            return "{\"accepted\":true,\"completed\":false}";
        }
        catch (Exception e) { return JsonSerializer.Serialize(new { accepted = false, error = e.Message }, Json); }
    }
    internal static void ValidateRequest(Request r)
    {
        if (r.Command != "reload" && r.Command != "preview" && r.Command != "stop" && r.Command != "camera" && r.Command != "resetCamera")
            throw new InvalidDataException("未知命令。");
        foreach (var x in new[] { r.Yaw, r.Pitch, r.Zoom, r.PanX, r.PanY })
            if (!float.IsFinite(x) || Math.Abs(x) > 2) throw new InvalidDataException("相机增量超出范围。");
        if (r.PresetIndex < 0) throw new InvalidDataException("方案索引无效。");
        if (r.Plan != null) ValidatePlan(r.Plan);
    }
    internal static void ValidatePlan(Plan p)
    {
        if (p.Name == null || p.Name.Length > 256 || p.Items == null || p.Items.Length > 12) throw new InvalidDataException("方案大小不合法。");
        var seen = new HashSet<int>();
        foreach (var part in p.Items)
            if (part == null || !ValidSlot(part.Slot) || !seen.Add(part.Slot) || part.ItemId >= 2000000)
                throw new InvalidDataException("方案包含无效或重复部位/物品 ID。");
        if (!p.Items.Any(p => p.ItemId != 0)) throw new InvalidDataException("方案没有装备数据。");
    }
    internal const uint PreviewRenderSlot = 2;
    internal const ushort PreviewObjectId = 242;
    internal static bool IsExpectedTryonMapping(uint slot, uint id) => slot == PreviewRenderSlot && id == PreviewObjectId;
    internal static void SetPackedModel(ref CharaViewModelData model, Part part, ulong packed)
    {
        if (part.Slot is 0 or 1)
        {
            model.WeaponModelIds[part.Slot] = *(WeaponModelId*)&packed;
            model.WeaponModelIds[part.Slot].Stain0 = part.Dye0;
            model.WeaponModelIds[part.Slot].Stain1 = part.Dye1;
        }
        int slot = ModelSlot(part.Slot);
        if (slot >= 0) model.EquipmentModelIds[slot] = new EquipmentModelId {
            Id = (ushort)packed, Variant = (byte)(packed >> 16), Stain0 = part.Dye0, Stain1 = part.Dye1 };
    }
    internal static void ResetNativeTryonItems(Span<AgentTryon.TryOnItem> items)
    {
        items.Clear();
        // Native reset routine uses category 14 for empty cache entries, not zero.
        for (int i = 0; i < items.Length; i++) items[i].EquipSlotCategory = 14;
    }
    // Category 13 is the native face-accessory cache entry; 14 denotes unused storage.
    internal static void AddGlassesCache(Span<AgentTryon.TryOnItem> items, ushort id, uint icon)
    {
        if (id == 0) return;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].Id != 0) continue;
            items[i] = new AgentTryon.TryOnItem { EquipSlotCategory = 13, Id = id, IconId = icon };
            return;
        }
        throw new InvalidOperationException("试衣间面部装饰缓存已满。");
    }
    internal static bool CanCustomizePreview(bool owned, uint slot, uint id, nint actor, nint localPlayer)
        => owned && IsExpectedTryonMapping(slot, id) && actor != 0 && localPlayer != 0 && actor != localPlayer;
    internal static bool CanPresentNativeFrame(uint state, bool hasTexture, bool nativeActorReady)
        => state == 6 && hasTexture && nativeActorReady;
    internal static bool ValidSlot(int s) => s is 0 or 1 or 2 or 3 or 4 or 6 or 7 or 8 or 9 or 10 or 11 or 12;
    internal static int ModelSlot(int s) => s switch { 2 => 0, 3 => 1, 4 => 2, >= 6 and <= 12 => s - 3, _ => -1 };
    private static uint Normalize(uint id) => id switch { > 1000000 => id - 1000000, > 500000 => id - 500000, _ => id };
    internal static Plan[] ParseOmni(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("Config", out var config)) root = config;
        if (!root.TryGetProperty("Presets", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 1000)
            throw new InvalidDataException("不支持的 Omni 配置结构。");
        var result = new List<Plan>();
        foreach (var entry in entries.EnumerateArray())
        {
            var p = new Plan { Name = entry.TryGetProperty("Name", out var n) ? n.GetString() ?? "未命名" : "未命名",
                Glasses = entry.TryGetProperty("GlassesID", out var g) ? g.GetUInt16() : (ushort)0,
                Hairstyle = entry.TryGetProperty("HairstyleID", out var h) ? h.GetUInt32() : 0 };
            p.Items = entry.GetProperty("Items").EnumerateArray().Select(i => new Part {
                Slot = i.GetProperty("Slot").GetInt32(), ItemId = i.GetProperty("ItemID").GetUInt32(),
                Dye0 = i.TryGetProperty("Stain0", out var d0) ? d0.GetByte() : (byte)0,
                Dye1 = i.TryGetProperty("Stain1", out var d1) ? d1.GetByte() : (byte)0 }).ToArray();
            ValidatePlan(p); result.Add(p);
        }
        return result.ToArray();
    }
    private void ReadPlans()
    {
        if (reading || context == null) return;
        var root = context.PluginInterface.ConfigDirectory.Parent ?? throw new IOException("找不到配置根目录。");
        string path = Path.Combine(root.FullName, "OmniToolbox", "TreeHouse", "OmniGlamourPreview.presets.json");
        if (!File.Exists(path)) path = Path.Combine(root.FullName, "OmniToolbox", "TreeHouse", "BetterGlamourManagement.json");
        int ticket = generation; reading = true;
        _ = Task.Run(() => {
            Plan[] result = Array.Empty<Plan>(); string error = "";
            try { var file = new FileInfo(path); if (file.Length > 4 * 1024 * 1024) throw new IOException("方案文件过大。"); result = ParseOmni(File.ReadAllText(path)); }
            catch (Exception e) { error = e.Message; }
            if (!enabled || ticket != generation) return;
            commands.Enqueue(() => { if (ticket != generation) return; reading = false;
                if (error != "") { phase = "error"; message = "读取失败：" + error; }
                else { plans = result; selected = 0; message = "已读取 " + plans.Length + " 个方案。选择后仅创建预览人物。"; }
            });
        });
    }
    private bool Safe() => context != null && context.ClientState.IsLoggedIn && context.ObjectTable.LocalPlayer != null
        && !context.Condition[ConditionFlag.BetweenAreas] && !context.Condition[ConditionFlag.BetweenAreas51]
        && !context.Condition[ConditionFlag.OccupiedInCutSceneEvent];
    private bool PortraitUiOpen()
    {
        foreach (var name in new[] { "GearSetPreview" })
            if (context!.GameGui.GetAddonByName(name).IsVisible) return true;
        return false;
    }
    private void PreviewSelected()
    {
        if (selected < 0 || selected >= plans.Length) { message = "请先读取并选择 Omni 方案。"; return; }
        Begin(plans[selected]);
    }
    private void Begin(Plan plan)
    {
        // Switching presets keeps the owned hidden agent alive. A full Stop restores
        // the visual tree and closes the addon, causing the visible close/open flash.
        bool reuse = ownsTryon && tryon != null && tryon->IsAgentActive() && Safe()
            && context!.ObjectTable.LocalPlayer!.Address == player;
        if (!reuse) Stop();
        else
        {
            shown = false; image = 0;
            var oldTexture = (KernelTexture*)texture; texture = 0;
            if (oldTexture != null) oldTexture->DecRef();
            yaw = pitch = zoom = panX = panY = 0;
            HideTryonChrome();
        }
        ValidatePlan(plan);
        if (!Safe()) throw new InvalidOperationException("请登录并等待切换场景结束。");
        if (PortraitUiOpen()) throw new InvalidOperationException("请先关闭游戏中的职业套装预览。");
        player = context!.ObjectTable.LocalPlayer!.Address;
        // Build all desired data BEFORE allocating or rendering a preview character.

        desired = default; desired.CopyFromCharacter((Character*)player);
        Array.Fill(mask, false);
        foreach (var part in plan.Items)
        {
            int slot = ModelSlot(part.Slot);
            if (part.ItemId == 0 && keepEmpty) continue;
            uint id = Normalize(part.ItemId);
            if (id != 0)
            {
                var row = context.DataManager.GetExcelSheet<Item>().GetRowOrDefault(id);
                if (!row.HasValue) throw new InvalidDataException("不存在物品 " + id);
                SetPackedModel(ref desired, part, row.Value.ModelMain);
                if (slot >= 0) mask[slot] = true;
            }
            else if (slot >= 0) { desired.EquipmentModelIds[slot] = default; mask[slot] = true; }
            if (id == 0 && part.Slot is 0 or 1) desired.WeaponModelIds[part.Slot] = default;
        }
        desired.GlassesIds[0] = plan.Glasses;
        if (plan.Hairstyle != 0)
        {
            var hair = context.DataManager.GetExcelSheet<CharaMakeCustomize>().GetRowOrDefault(plan.Hairstyle);
            if (!hair.HasValue || hair.Value.FeatureID > 255) throw new InvalidDataException("发型数据无效。");
            desired.CustomizeData.Hairstyle = (byte)hair.Value.FeatureID;
        }
        desired.HeadgearHidden = hideHead; desired.WeaponHidden = hideWeapon;
        desired.VisorClosed = visor; desired.VieraEarsHidden = ears;
        tryon = AgentTryon.Instance();
        if (tryon == null) throw new InvalidOperationException("原生试衣间不可用。");
        if (!reuse && tryon->IsAgentActive()) throw new InvalidOperationException("请先关闭游戏中已打开的试衣间，再选择方案预览。");
        var seed = plan.Items.First(x => x.ItemId != 0);
        if (!reuse)
        {
            savedTryonItems = tryon->TryOnItems.ToArray();
            previousSaveOutfit = tryon->SaveDeleteOutfit;
            previousDisplayGear = tryon->DisplayGear;
            ownsTryon = true;
            if (!AgentTryon.TryOn(0, Normalize(seed.ItemId), seed.Dye0, seed.Dye1))
                throw new InvalidOperationException("游戏拒绝打开此装备的试衣间。");
            HideTryonChrome();
        }
        ResetNativeTryonItems(tryon->TryOnItems);
        tryon->SaveDeleteOutfit = true;
        tryon->DisplayGear = false;
        foreach (var part in plan.Items.Where(x => x.ItemId != 0).OrderBy(x => x.Slot))
        {
            if (!AgentTryon.TryOn(0, Normalize(part.ItemId), part.Dye0, part.Dye1))
                throw new InvalidOperationException($"试衣间拒绝装备：部位 {part.Slot} / 物品 {part.ItemId}。");
            HideTryonChrome();
        }
        if (plan.Glasses != 0)
        {
            var glasses = context.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Glasses>().GetRowOrDefault(plan.Glasses);
            if (!glasses.HasValue) throw new InvalidDataException("面部装饰不存在。");
            AddGlassesCache(tryon->TryOnItems, plan.Glasses, (uint)glasses.Value.Icon);
        }
        customizationAttempts = 0; customizationRetryAt = 0;
        tryon->TryOnItemsChanged = true;
        tryon->CharaView.HideOtherEquipment = !keepEmpty;
        tryon->CharaView.DoUpdate = true;
        HideTryonChrome();
        view = &tryon->CharaView.CharaView;
        renderIndex = PreviewRenderSlot;
        if (view->State != 0 && !IsExpectedTryonMapping(view->ClientObjectIndex, view->ClientObjectId))
            throw new InvalidOperationException($"试衣间映射异常：槽 {view->ClientObjectIndex}/对象 {view->ClientObjectId}。");
        modelSubmitted = false; outfitVerified = false; verifiedFrames = 0;
        currentPlan = plan.Name; phase = "loading"; loadingSeconds = 0; requestId++;
        message = "正在通过原生试衣间槽位 2 加载方案。原生窗口在后台运行。";
    }

    public void Update(TimeSpan elapsed)
    {
        if (!enabled || context == null) return;
        try
        {
            int batch = 0;
            while (batch++ < 8 && commands.TryDequeue(out var command)) command();
            if (view == null) return;
            if (!Safe() || context.ObjectTable.LocalPlayer!.Address != player || PortraitUiOpen()) { Stop(); message = "角色或共享试衣间环境变化，预览已停止。"; return; }
            if (tryon == null || !ownsTryon || !tryon->IsAgentActive())
            { Stop(); message = "游戏试衣间已关闭，预览已停止。"; return; }
            HideTryonChrome();
            // The native agent updates/renders this view. Never run a second Render or Update.
            if (view->State != 0 && !IsExpectedTryonMapping(view->ClientObjectIndex, view->ClientObjectId))
                throw new InvalidOperationException("试衣间槽位已变化，停止预览。");
            bool requestProcessed = !tryon->TryOnItemsChanged && !tryon->GearItemsChanged && !tryon->CharaView.DoUpdate;
            modelSubmitted = requestProcessed;
            loadingSeconds += Math.Clamp(elapsed.TotalSeconds, 0, 0.25);
            var character = view->GetCharacter();
            var draw = character == null ? null : (CharacterBase*)character->DrawObject;
            bool humanReady = character != null && (nint)character != player && draw != null && draw->GetModelType() == CharacterBase.ModelType.Human;
            if (requestProcessed && humanReady && view->State == 6)
                ApplyPreviewCustomization(character, (Human*)draw);
            bool extraMatched = humanReady && ((Human*)draw)->Customize.Hairstyle == desired.CustomizeData.Hairstyle
                && character->DrawData.GlassesIds[0] == desired.GlassesIds[0];
            outfitVerified = requestProcessed && humanReady && extraMatched && Matches(ref desired, (Human*)draw, mask, tryon->CharaView.HeadgearHidden);
            var target = RenderTargetManager.Instance();
            var nativeTexture = tryon->Texture;
            if (nativeTexture == null && target != null) nativeTexture = target->GetCharaViewTexture(renderIndex);
            bool hasTexture = nativeTexture != null && nativeTexture->D3D11ShaderResourceView != null && nativeTexture->ActualWidth > 0 && nativeTexture->ActualHeight > 0;
            if (CanPresentNativeFrame(view->State, hasTexture, humanReady))
            {
                verifiedFrames++;
                if (verifiedFrames >= 2)
                {
                    var previous = (KernelTexture*)texture;
                    if(previous != nativeTexture)nativeTexture->IncRef();
                    texture = (nint)nativeTexture; image = (nint)nativeTexture->D3D11ShaderResourceView;
                    frameWidth = (int)nativeTexture->ActualWidth; frameHeight = (int)nativeTexture->ActualHeight;
                    ratio = (float)frameWidth / frameHeight;
                    if (previous != null && previous != nativeTexture) previous->DecRef();
                    currentTextureValid = true;
                    shown = true;
                }
            }
            else verifiedFrames = 0;
            if (shown)
            {
                phase = outfitVerified ? "ready" : requestProcessed ? "mismatch" : "loading";
                message = outfitVerified ? "衣装、发型及面部装饰已核对。左拖旋转，右拖平移，滚轮缩放。"
                    : !requestProcessed ? "原生试衣间画面已连接，正在处理方案缓存。"
                    : !humanReady ? "等待预览人物重新加载。"
                    : !extraMatched ? $"{(customizationAttempts >= 3 ? "发型/面饰未匹配，请重选方案" : "正在核对发型及面部装饰")}（{customizationAttempts}/3）；发型 {((Human*)draw)->Customize.Hairstyle}/{desired.CustomizeData.Hairstyle}，面饰 {character->DrawData.GlassesIds[0]}/{desired.GlassesIds[0]}。"
                    : "原生试衣间画面已连接；衣装仍未匹配。" + MismatchDetails(ref desired, character, mask);
            }
            else
            {
                message = $"等待试衣间纹理：状态 {view->State}，缓存处理 {requestProcessed}，纹理 {hasTexture}，人物 {humanReady}。";
                if (loadingSeconds > 12) throw new InvalidOperationException(message);
            }
            if (shown)
            {
                if (yaw != 0 || pitch != 0) view->SetCameraYawAndPitch(Math.Clamp(yaw, -100, 100), Math.Clamp(pitch, -100, 100));
                if (zoom != 0) view->SetCameraDistance(Math.Clamp(zoom, -20, 20));
                if (panX != 0 || panY != 0) view->SetCameraXAndY(Math.Clamp(panX, -100, 100), Math.Clamp(panY, -100, 100));
                yaw = pitch = zoom = panX = panY = 0;
            }
        }
        catch (Exception e)
        {
            try { Stop(); } catch (Exception cleanup) { context.Log.Error(cleanup, "OmniOutfitPreview cleanup failed"); }
            phase = "error"; message = e.Message; context.Log.Error(e, "OmniOutfitPreview stopped");
        }
    }
    private void ApplyPreviewCustomization(Character* character, Human* human)
    {
        // Only the agent-owned TryOn actor may change; never pass the live player to these APIs.
        if (view == null || !CanCustomizePreview(ownsTryon, view->ClientObjectIndex, view->ClientObjectId, (nint)character, player)) return;
        bool hairChanged = human->Customize.Hairstyle != desired.CustomizeData.Hairstyle;
        bool glassesChanged = character->DrawData.GlassesIds[0] != desired.GlassesIds[0];
        if ((!hairChanged && !glassesChanged) || customizationAttempts >= 3 || loadingSeconds < customizationRetryAt) return;
        customizationAttempts++; customizationRetryAt = loadingSeconds + 0.75;
        if (glassesChanged) character->DrawData.SetGlasses(0, desired.GlassesIds[0]);
        if (hairChanged)
        {
            // Same public API used by Omni's hairstyle service, targeting the isolated preview.
            // skipEquipmentArrays preserves the entire native TryOn outfit and its dyes.
            var customize = character->DrawData.CustomizeData;
            customize.Hairstyle = desired.CustomizeData.Hairstyle;
            var data = new Human.DrawData { CustomizeData = customize };
            if (human->UpdateDrawData(&data, true)) character->DrawData.CustomizeData = customize;
        }
    }
    internal static bool Matches(ref CharaViewModelData expected, Human* actual, bool[] slots, bool hiddenHead)
    {
        for (int i = 0; i < 10; i++)
        {
            if (!slots[i] || (i == 0 && hiddenHead)) continue;
            var a = expected.EquipmentModelIds[i]; var b = actual->EquipmentModels[i];
            if (a.Id != b.Id || a.Variant != b.Variant || a.Stain0 != b.Stain0 || a.Stain1 != b.Stain1) return false;
        }
        return true;
    }
    private static string MismatchDetails(ref CharaViewModelData expected, Character* character, bool[] slots)
    {
        if (character == null) return "预览人物尚未创建。";
        var draw = (CharacterBase*)character->DrawObject;
        if (draw == null || draw->GetModelType() != CharacterBase.ModelType.Human) return "人物模型尚未加载。";
        var human = (Human*)draw;
        for (int i = 0; i < 10; i++) if (slots[i])
        {
            var wanted = expected.EquipmentModelIds[i]; var actual = human->EquipmentModels[i];
            if (wanted.Id != actual.Id || wanted.Variant != actual.Variant || wanted.Stain0 != actual.Stain0 || wanted.Stain1 != actual.Stain1)
                return $"部位 {i}：期望 {wanted.Id}/{wanted.Variant}/{wanted.Stain0}/{wanted.Stain1}，实际 {actual.Id}/{actual.Variant}/{actual.Stain0}/{actual.Stain1}。";
        }
        return "衣装匹配，仍在等待原生加载标记或纹理。";
    }
    private void Stop()
    {
        phoneFrames.Reset();
        image = 0; shown = false; yaw = pitch = zoom = panX = panY = 0;
        view = null;
        var oldAgent = tryon; tryon = null;
        bool closeOwned = ownsTryon; ownsTryon = false;
        if (closeOwned && oldAgent != null)
        {
            // Arm render-time suppression BEFORE native Hide/Close can re-enable the UI tree.
            closingTryon = hiddenTryonRoot != 0;
            var beforeClose = GetTryonAddon();
            if (beforeClose != null && (nint)beforeClose == guardedTryonAddon)
            {
                beforeClose->SetAlpha(0);
                if (beforeClose->RootNode != null) beforeClose->RootNode->ToggleVisibility(false);
            }
            if (oldAgent->IsAgentActive()) oldAgent->Hide();
            // Hiding alone retains the addon tree and permits deferred transitions to show it again.
            // Close the owned addon with its root still hidden; never re-enable that root on stop.
            CloseOwnedTryonAddon();
            if (savedTryonItems != null) savedTryonItems.AsSpan().CopyTo(oldAgent->TryOnItems);
            oldAgent->SaveDeleteOutfit = previousSaveOutfit;
            oldAgent->DisplayGear = previousDisplayGear;
        }
        savedTryonItems = null; modelSubmitted = false; outfitVerified = false; verifiedFrames = 0;
        var t = (KernelTexture*)texture; texture = 0; if (t != null) t->DecRef();
        player = 0; renderIndex = 0; frameWidth = frameHeight = 0;
        phase = "idle"; currentPlan = "";
    }

    private string? pendingPreset;
    private long due;
    public nint Texture => pendingPreset == null && shown ? texture : 0;
    public float AspectRatio => ratio > 0 ? ratio : 0.75f;
    public string Message => pendingPreset == null ? message : "正在准备所选方案的预览…";
    public void QueuePreset(BetterGlamourPreset preset)
    {
        var plan = new Plan { Name = preset.Name, Glasses = preset.GlassesID, Hairstyle = preset.HairstyleID,
            Items = preset.Items.Select(x => new Part { Slot = x.Slot, ItemId = x.ItemID, Dye0 = x.Stain0, Dye1 = x.Stain1 }).ToArray() };
        pendingPreset = JsonSerializer.Serialize(new Request { Command = "preview", Plan = plan }, Json);
        due = Environment.TickCount64 + 150;
    }
    public void CancelPreview()
    {
        pendingPreset = null;
        while (commands.TryDequeue(out _)) { }
        Stop();
    }
    internal static float RotationPixels(float radians) => radians * 100f;
    public void Camera(float rotation, float distance) { yaw += RotationPixels(rotation); zoom += distance * 5; }
    public void CameraPixels(float rotation, float x, float y, float wheel)
    {
        if (!shown || view == null) return;
        yaw += Math.Clamp(rotation, -100, 100); panX += Math.Clamp(x, -100, 100); panY += Math.Clamp(y, -100, 100);
        zoom -= wheel * 2.5f;
    }
    private static AtkUnitBase* GetTryonAddon()
    {
        AtkUnitBase* addon;
        if (!AddonHelper.TryGetByName("Tryon", out addon) && !AddonHelper.TryGetByName("FittingRoom", out addon)) return null;
        return addon;
    }
    private void HideTryonChrome()
    {
        if (!ownsTryon || tryon == null || !tryon->IsAgentActive()) return;
        var addon = GetTryonAddon();
        if (addon == null || addon->RootNode == null) return;
        var root = addon->RootNode;
        if (hiddenTryonRoot != (nint)root)
        {
            hiddenTryonRoot = (nint)root;
            guardedTryonAddon = (nint)addon;
            savedTryonAlpha = addon->Alpha;
            closingTryon = false;
            savedDisableShowOnOpen = addon->DisableShowOnOpen;
            savedDisableFocusOnShow = addon->DisableFocusOnShow;
        }
        addon->DisableShowOnOpen = true;
        addon->DisableFocusOnShow = true;
        // Keep the addon and agent active: closing/hiding the addon itself stops native rendering.
        // Only its visual/collision tree is suppressed; native buttons remain addressable.
        if (root->IsVisible())
        {
            root->ToggleVisibility(false);
            addon->UpdateCollisionNodeList(true);
        }
    }
    private void CloseOwnedTryonAddon()
    {
        if (hiddenTryonRoot == 0) return;
        var addon = GetTryonAddon();
        if (addon != null && addon->RootNode != null && (nint)addon->RootNode == hiddenTryonRoot)
        {
            addon->RootNode->ToggleVisibility(false);
            addon->UpdateCollisionNodeList(true);
            addon->DisableShowOnOpen = savedDisableShowOnOpen;
            addon->DisableFocusOnShow = savedDisableFocusOnShow;
            addon->Hide(true, false, 0);
            // Let the normal native addon finalization dispose its hidden tree. A later user-open
            // builds a fresh visible tree. false avoids invoking the agent close callback twice.
            addon->Close(false);
        }
        // Keep the draw guard alive through deferred native finalization.
        // Do not clear the tracked root merely because Close has returned.
    }
    private void OnTryonLifecycle(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || (nint)addon != guardedTryonAddon || hiddenTryonRoot == 0
            || addon->RootNode == null || (nint)addon->RootNode != hiddenTryonRoot) return;
        if (type == AddonEvent.PreFinalize)
        {
            // The hidden tree is now being destroyed, so it cannot flash on a later draw.
            addon->DisableShowOnOpen = savedDisableShowOnOpen;
            addon->DisableFocusOnShow = savedDisableFocusOnShow;
            addon->SetAlpha(savedTryonAlpha);
            hiddenTryonRoot = guardedTryonAddon = 0;
            closingTryon = false;
            return;
        }
        if (type != AddonEvent.PreDraw || (!ownsTryon && !closingTryon)) return;
        // Framework.Update is too early: native transitions may reset visibility afterwards.
        // Suppress again at the actual draw boundary, including after the manager has closed.
        addon->RootNode->ToggleVisibility(false);
        if (closingTryon) addon->SetAlpha(0);
        suppressedDraws++;
    }
    private AtkComponentButton* GetNativeButton(uint nodeId)
    {
        if (!ownsTryon || tryon == null || !tryon->IsAgentActive()) return null;
        var addon = GetTryonAddon();
        if (addon == null || addon->UldManager.NodeList == null) return null;
        for (int i = 0; i < Math.Min((int)addon->UldManager.NodeListCount, 512); i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node != null && node->NodeId == nodeId && (ushort)node->Type >= 1000)
                return (AtkComponentButton*)((AtkComponentNode*)node)->Component;
        }
        return null;
    }
    public bool NativeButtonChecked(uint nodeId)
    {
        var button = GetNativeButton(nodeId);
        return nodeId != 39 && button != null && button->IsChecked;
    }
    public void ClickNativeButton(uint nodeId)
    {
        var button = GetNativeButton(nodeId);
        if (button == null || !button->IsEnabled || button->OwnerNode == null) return;
        var ev = button->OwnerNode->AtkResNode.AtkEventManager.Event;
        for (int limit = 0; ev != null && limit < 32; limit++, ev = ev->NextEvent)
        {
            if (ev->State.EventType != AtkEventType.ButtonClick || ev->Listener == null) continue;
            if (nodeId != 39) button->SetChecked(!button->IsChecked);
            AtkEventData data = default;
            customizationAttempts = 0; customizationRetryAt = loadingSeconds + 0.25;
            ev->Listener->ReceiveEvent(AtkEventType.ButtonClick, (int)ev->Param, ev, &data);
            return;
        }
        message = "无法找到原生试衣间按钮事件，请重新打开试衣间。";
    }
    public void ResetCamera() { if (view != null && shown) view->ResetPositions(); }
    public void Toggle(string feature)
    {
        switch (feature) { case "head": hideHead = !hideHead; break; case "weapon": hideWeapon = !hideWeapon; break;
            case "visor": visor = !visor; break; case "draw": drawn = !drawn; break; case "ears": ears = !ears; break; }
        if (view != null && shown && tryon != null)
        {
            tryon->CharaView.HeadgearHidden = hideHead;
            tryon->CharaView.WeaponHidden = hideWeapon;
            tryon->CharaView.VisorClosed = visor;
            tryon->CharaView.VieraEarsHidden = ears;
            tryon->CharaView.DrawWeapon = drawn;
            tryon->CharaView.DoUpdate = true;
        }
    }
    private void OnFrame(IFramework framework)
    {
        if (pendingPreset != null && Environment.TickCount64 >= due)
        {
            var json = pendingPreset; pendingPreset = null;
            using var ack = JsonDocument.Parse(Submit(json));
            if (!ack.RootElement.GetProperty("accepted").GetBoolean()) { phase = "error"; message = ack.RootElement.GetProperty("error").GetString() ?? "Invalid preview request"; }
        }
        currentTextureValid=false;
        Update(framework.UpdateDelta);
        System.Threading.Volatile.Write(ref cachedPhoneStatus,Status());
        if(context!=null&&currentTextureValid)phoneFrames.Update(context.TextureReadback,image,frameWidth,frameHeight,requestId,pendingPreset==null&&shown&&outfitVerified);
        // Borrowed process-local SRV; consumers must copy during this synchronous callback.
        if (currentTextureValid && pendingPreset == null && shown && image != 0 && outfitVerified)
        {
            try { frameIpc?.SendMessage(image, frameWidth, frameHeight, requestId); }
            catch (Exception error) { context?.Log.Warning(error, "Omni preview frame consumer failed"); }
        }
    }
}
