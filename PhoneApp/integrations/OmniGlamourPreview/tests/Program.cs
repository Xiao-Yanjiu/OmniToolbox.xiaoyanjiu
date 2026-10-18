using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Module = OmniToolbox.LocalGlamourPreview.BetterGlamourPortraitPreview;

class Program
{
    static void Main()
    {
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            foreach (var dir in new[] { "C:/Users/63377/AppData/Roaming/XIVLauncherCN/addon/Hooks/26-10-17-01", "C:/Users/63377/AppData/Roaming/XIVLauncherCN/installedPlugins/OmniToolbox/1.1.7.2" })
            { var p = Path.Combine(dir, name.Name + ".dll"); if (File.Exists(p)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(p); }
            return null;
        };
        Tests.Run();
    }
}
public class Proxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Call = (_, _) => null;
    protected override object? Invoke(MethodInfo? m, object?[]? a) => Call(m!, a);
    public static T Make<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    { var p = Create<T, Proxy>(); ((Proxy)(object)p).Call = call; return p; }
}
static class Tests
{
    static int checks, registrations, removals;
    static void Check(bool b, string name) { if (!b) throw new Exception(name); checks++; }
    public static T Gate<T>() where T : class => Proxy.Make<T>((m, _) => { if (m.Name == "RegisterFunc") registrations++; if (m.Name == "UnregisterFunc") removals++; return null; });
    static void Reject(Action a, string name) { bool rejected = false; try { a(); } catch { rejected = true; } Check(rejected, name); }
    public static unsafe void Run()
    {
        string fixture = """
        {"Config":{"Presets":[{"Name":"fixture","GlassesID":301,"HairstyleID":1778,"Items":[
        {"Slot":0,"ItemID":0},{"Slot":3,"ItemID":36833,"Stain0":6,"Stain1":101},
        {"Slot":6,"ItemID":38216,"Stain0":4},{"Slot":11,"ItemID":47300},{"Slot":12,"ItemID":47301}]}]}}
        """;
        Check(Module.IsExpectedTryonMapping(2, 242), "native TryOn slot uses CN object 242");
        Check(!Module.IsExpectedTryonMapping(8, 248), "do not enable unsupported ninth slot");
        Check(!Module.IsExpectedTryonMapping(2, 42), "reject old client mapping");
        FFXIVClientStructs.FFXIV.Client.UI.Misc.CharaViewModelData packedModel = default;
        Module.SetPackedModel(ref packedModel, new Module.Part { Slot=3, Dye0=6, Dye1=101 }, 500UL | (3UL << 16));
        Check(packedModel.EquipmentModelIds[1].Id == 500 && packedModel.EquipmentModelIds[1].Variant == 3, "selected body packed model merged");
        Check(packedModel.EquipmentModelIds[1].Stain0 == 6 && packedModel.EquipmentModelIds[1].Stain1 == 101, "selected body both stains merged");
        Module.SetPackedModel(ref packedModel, new Module.Part { Slot=0, Dye0=4, Dye1=8 }, 123UL | (2UL << 16) | (3UL << 32));
        Check(packedModel.WeaponModelIds[0].Stain0 == 4 && packedModel.WeaponModelIds[0].Stain1 == 8, "selected main hand both stains merged");
        Module.SetPackedModel(ref packedModel, new Module.Part { Slot=1, Dye0=9 }, 234UL);
        Check(packedModel.WeaponModelIds[1].Stain0 == 9 && packedModel.WeaponModelIds[0].Stain0 == 4, "offhand does not overwrite main hand");
        var nativeCache = new FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentTryon.TryOnItem[14];
        nativeCache[0].Id = 999; nativeCache[13].Stain1Id = 100;
        Module.ResetNativeTryonItems(nativeCache);
        Check(nativeCache.All(x => x.Id == 0 && x.Stain1Id == 0 && x.EquipSlotCategory == 14), "new outfit clears prior cache using native empty-category sentinel");
        Check(Module.CanPresentNativeFrame(6, true, true), "present native ready texture independently of transient CharacterLoaded or outfit verification flags");
        Check(!Module.CanPresentNativeFrame(6, true, false), "do not present missing native actor");
        Check(!Module.CanPresentNativeFrame(6, false, true), "do not publish missing texture");
        Check(!Module.CanPresentNativeFrame(1, true, true), "do not publish before native ready state");
        nativeCache[0].Id = 222; nativeCache[0].EquipSlotCategory = 4;
        Module.AddGlassesCache(nativeCache, 301, 1234);
        Check(nativeCache[0].Id == 222 && nativeCache[1].Id == 301 && nativeCache[1].EquipSlotCategory == 13 && nativeCache[1].IconId == 1234, "face accessory uses category 13 without overwriting clothes");
        Module.AddGlassesCache(nativeCache, 0, 0);
        Check(nativeCache.Count(x => x.Id != 0) == 2, "empty face accessory does not consume cache entry");
        foreach (ref var entry in nativeCache.AsSpan()) entry.Id = 10;
        Reject(() => Module.AddGlassesCache(nativeCache, 301, 1), "full face accessory cache rejected");
        Check(Module.CanCustomizePreview(true, 2, 242, 200, 100), "isolated owned TryOn actor may customize");
        Check(!Module.CanCustomizePreview(true, 2, 242, 100, 100), "live player can never customize through preview");
        Check(!Module.CanCustomizePreview(false, 2, 242, 200, 100), "unowned preview cannot customize");
        Check(!Module.CanCustomizePreview(true, 6, 246, 200, 100), "other portrait actors cannot customize");
        Check(Module.RotationPixels(0.2f) == 20, "camera radians converted to native pixels");
        var plans = Module.ParseOmni(fixture);
        Check(plans.Length == 1 && plans[0].Items.Length == 5, "preserve full plan including empty slots");
        Check(plans[0].Items[1].Dye0 == 6 && plans[0].Items[1].Dye1 == 101, "both stains retained");
        Check(plans[0].Glasses == 301 && plans[0].Hairstyle == 1778, "glasses and hair retained");
        Check(plans[0].Items[3].Slot == 11 && plans[0].Items[4].Slot == 12, "separate rings");
        Check(Module.ModelSlot(6) == 3 && Module.ModelSlot(12) == 9 && Module.ModelSlot(0) == -1, "inventory/model mapping");
        Reject(() => Module.ParseOmni(fixture.Replace("\"Stain1\":101", "\"Stain1\":999")), "reject overflow stain");
        Reject(() => Module.ParseOmni(fixture.Replace("\"Slot\":12", "\"Slot\":11")), "reject duplicate slots");
        Reject(() => Module.ParseOmni(fixture.Replace("\"Slot\":12", "\"Slot\":99")), "reject unknown slots");
        Reject(() => Module.ParseOmni("{}"), "reject unknown file schema");
        Reject(() => Module.ValidateRequest(new Module.Request { Command = "applyToPlayer" }), "no scene apply command");
        Reject(() => Module.ValidateRequest(new Module.Request { Command = "camera", Zoom = float.NaN }), "reject non-finite camera");
        Reject(() => Module.ValidateRequest(new Module.Request { Command = "camera", PanX = 100 }), "reject excessive camera");
        Reject(() => Module.ValidateRequest(new Module.Request { Command = "preview", PresetIndex = -1 }), "reject invalid index");
        var clipboardPreset = new OmniToolbox.LocalGlamourPreview.BetterGlamourPreset { Name="双染+面饰+发型", GearsetIndex=2, GlassesID=301, HairstyleID=1778,
            Items=[new() {Slot=3, ItemID=36833, Stain0=6, Stain1=101}, new(){Slot=11,ItemID=47300},new(){Slot=12,ItemID=47301}] };
        string stableRevision=OmniToolbox.LocalGlamourPreview.OmniGlamourPreview.Revision(new[]{clipboardPreset});
        Check(stableRevision==OmniToolbox.LocalGlamourPreview.OmniGlamourPreview.Revision(new[]{clipboardPreset}),"unchanged snapshots have stable revision tokens");
        clipboardPreset.Items[0].Stain1=102;
        Check(stableRevision!=OmniToolbox.LocalGlamourPreview.OmniGlamourPreview.Revision(new[]{clipboardPreset}),"second dye changes invalidate stale writes");
        clipboardPreset.Items[0].Stain1=101;
        var share=OmniToolbox.LocalGlamourPreview.GlamourClipboard.Export(clipboardPreset);
        var decoded=OmniToolbox.LocalGlamourPreview.GlamourClipboard.Import(share);
        Check(decoded.Items[0].Stain0==6 && decoded.Items[0].Stain1==101 && decoded.GlassesID==301 && decoded.HairstyleID==1778, "clipboard round trip preserves both stains, hairstyle and glasses");
        var again=OmniToolbox.LocalGlamourPreview.GlamourClipboard.Import(share);
        Check(decoded.Id!=again.Id && decoded.Id!=clipboardPreset.Id,"every clipboard import creates a new stable identity");
        var phoneFrame=new OmniToolbox.LocalGlamourPreview.RemotePreviewFrames();
        using(var emptyFrame=JsonDocument.Parse(phoneFrame.Read("{\"ready\":false}"))) Check(emptyFrame.RootElement.GetProperty("image").GetString()=="","phone preview cannot fabricate a frame before GPU readback");
        phoneFrame.Reset();
        Check(decoded.GearsetIndex==-1 && clipboardPreset.GearsetIndex==2, "import/export cannot carry auto-apply binding or mutate source");
        Check(decoded.Items[1].Slot==11 && decoded.Items[2].Slot==12, "clipboard preserves separate rings");
        Reject(()=>OmniToolbox.LocalGlamourPreview.GlamourClipboard.Import(share.Replace("\"Version\": 1","\"Version\": 2")),"reject unsupported clipboard schema version");
        Reject(()=>OmniToolbox.LocalGlamourPreview.GlamourClipboard.Import(share.Replace("\"Slot\": 12","\"Slot\": 11")),"reject clipboard duplicate slots");
        Reject(()=>OmniToolbox.LocalGlamourPreview.GlamourClipboard.Import(share.Replace("\"Stain1\": 101","\"Stain1\": 256")),"reject clipboard stain overflow");
        Reject(()=>OmniToolbox.LocalGlamourPreview.GlamourClipboard.Import(new string('x',65537)),"reject oversized clipboard input");
        Reject(()=>OmniToolbox.LocalGlamourPreview.GlamourClipboard.Import("https://ff14risingstones.web.sdo.com/pc/index.html#/glamour/detail/296451"),"a bare website URL is not silently imported");
        var websitePreset=OmniToolbox.LocalGlamourPreview.GlamourClipboard.Import(File.ReadAllText("work/omni-glamour-integration/risingstones-export/春溪清茶-296451.omni.json"));
        Check(websitePreset.Name=="春溪清茶" && websitePreset.Items.Count==12,"real website export imports as one full preset");
        Check(websitePreset.Items.Single(x=>x.Slot==3).ItemID==42597 && websitePreset.Items.Single(x=>x.Slot==3).Stain1==60,"website and native clipboard formats agree on item and second dye IDs");
        var path = "C:/Users/63377/AppData/Roaming/XIVLauncherCN/pluginConfigs/OmniToolbox/TreeHouse/BetterGlamourManagement.json";
        var before = File.ReadAllBytes(path);
        var actual = Module.ParseOmni(File.ReadAllText(path));
        Check(actual.Length == 4, "read actual installed preset format");
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "actual config bytes unchanged");
        FFXIVClientStructs.FFXIV.Client.UI.Misc.CharaViewModelData expected = default;
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human human = default;
        var mask = new bool[10]; mask[1] = true;
        expected.EquipmentModelIds[1].Id = 500; expected.EquipmentModelIds[1].Stain1 = 101;
        Check(!Module.Matches(ref expected, &human, mask, false), "do not publish empty/wrong preview");
        human.Top = expected.EquipmentModelIds[1];
        Check(Module.Matches(ref expected, &human, mask, false), "publish only matching requested clothes");
        human.Top.Stain1 = 6;
        Check(!Module.Matches(ref expected, &human, mask, false), "second-dye mismatch blocks ready");
        int adds = 0, subs = 0, drawRegistrations = 0, drawRemovals = 0;
        var pi = Proxy.Make<Dalamud.Plugin.IDalamudPluginInterface>((m, _) => {
            if (m.Name == "GetIpcProvider") return typeof(Tests).GetMethod(nameof(Gate))!.MakeGenericMethod(m.ReturnType).Invoke(null, null);
            return null;
        });
        var fw = Proxy.Make<Dalamud.Plugin.Services.IFramework>((m, _) => {
            if(m.Name == "add_Update") adds++;
            if(m.Name == "remove_Update") subs++;
            return m.Name == "get_IsInFrameworkUpdateThread" ? true : null;
        });
        var c = new OmniToolbox.LocalGlamourPreview.OmniPreviewContext();
        typeof(OmniToolbox.LocalGlamourPreview.OmniPreviewContext).GetProperty("PluginInterface")!.SetValue(c, pi);
        typeof(OmniToolbox.LocalGlamourPreview.OmniPreviewContext).GetProperty("Framework")!.SetValue(c, fw);
        var lifecycle = Proxy.Make<Dalamud.Plugin.Services.IAddonLifecycle>((m, _) => {
            if (m.Name == "RegisterListener") drawRegistrations++;
            if (m.Name == "UnregisterListener") drawRemovals++;
            return null;
        });
        typeof(OmniToolbox.LocalGlamourPreview.OmniPreviewContext).GetProperty("AddonLifecycle")!.SetValue(c, lifecycle);
        var module = new Module();
        Check(module.Submit("{} ").Contains("disabled"), "disabled bridge rejects work");
        module.Initialize(c); module.Enable(); module.Enable();
        Check(adds == 1 && registrations == 3, "one framework hook and three IPC funcs");
        Check(module.Submit("{\"command\":\"stop\"}").Contains("true"), "IPC acknowledges queued request only");
        Check(module.Submit("{\"command\":\"applyToPlayer\"}").Contains("false"), "bridge rejects scene mutation");
        Check(module.Submit(new string('x', 65537)).Contains("false"), "bridge bounds payload");
        module.Update(TimeSpan.Zero); // queued Stop only: no native renderer exists
        module.Disable(); module.Disable();
        Check(subs == 1 && removals == 3, "all events and funcs released once");
        Check(drawRegistrations == 2 && drawRemovals == 1, "native draw/finalize guard registered and released once");
        using var state = JsonDocument.Parse(module.Status());
        Check(!state.RootElement.GetProperty("ready").GetBoolean(), "disabled state cannot be ready");
        var preset = new OmniToolbox.LocalGlamourPreview.BetterGlamourPreset { Name = "A" };
        module.QueuePreset(preset);
        preset.Name = "B";
        var pending = typeof(Module).GetField("pendingPreset", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check(((string)pending.GetValue(module)!).Contains("A"), "selection captures immutable snapshot");
        module.QueuePreset(preset);
        using var queued = JsonDocument.Parse((string)pending.GetValue(module)!);
        Check(queued.RootElement.GetProperty("plan").GetProperty("name").GetString() == "B", "latest selection replaces queued selection");
        Check(module.Texture == 0, "pending selection cannot display stale texture");
        module.CancelPreview();
        Check(pending.GetValue(module) == null && module.Texture == 0, "cancel clears pending selection");
        module.Dispose();
        var local = typeof(OmniToolbox.LocalGlamourPreview.OmniGlamourPreview);
        var entries = local.Assembly.GetTypes().Where(t => t.IsPublic && !t.IsAbstract && typeof(OmniToolbox.Common.Module.Abstractions.ModuleBase).IsAssignableFrom(t)).ToArray();
        Check(entries.Length == 1 && entries[0] == local, "exactly one public module entry");
        Check(local.GetConstructor(Type.EmptyTypes) != null, "public parameterless local loader constructor");
        Check(local.Name == "OmniGlamourPreview", "entry matches import filename");
        var imported = OmniToolbox.LocalGlamourPreview.OmniGlamourPreview.DecodeLocalPresets(fixture, true);
        Check(imported.Presets.Count == 1 && imported.Presets[0].GearsetIndex == -1, "first import cannot auto apply gearset");
        var persisted = OmniToolbox.LocalGlamourPreview.OmniGlamourPreview.DecodeLocalPresets(fixture.Replace("\"Name\":\"fixture\"", "\"Name\":\"fixture\",\"GearsetIndex\":2"), false);
        Check(persisted.Presets[0].GearsetIndex == 2, "own saved bindings retained");
        Check(!typeof(Module).Assembly.GetReferencedAssemblies().Any(a => a.Name == "Chrysalism"), "no Chrysalism dependency");
        var safeFrames=new OmniToolbox.LocalGlamourPreview.RemotePreviewFrames();
        using(var frame=JsonDocument.Parse(safeFrames.Read("{\"ready\":false}"))){Check(frame.RootElement.GetProperty("image").GetString()=="","new CPU pipeline begins without stale image");}
        var spec=Dalamud.Interface.Textures.RawImageSpecification.Bgra32(4,4);var pixels=new byte[4*4*4];for(int i=0;i<pixels.Length;i+=4){pixels[i+2]=255;pixels[i+3]=0;}
        byte[] jpeg=OmniToolbox.LocalGlamourPreview.RemotePreviewFrames.Encode(spec,pixels,System.Threading.CancellationToken.None);
        using(var stream=new MemoryStream(jpeg))using(var bitmap=new System.Drawing.Bitmap(stream)){var pixel=bitmap.GetPixel(1,1);Check(pixel.R>220&&pixel.B<40,"CPU BGRA snapshot encodes opaque red without alpha blanking");Check(bitmap.Width==4&&bitmap.Height==4,"CPU output retains small snapshot size");}
        var rgba=Dalamud.Interface.Textures.RawImageSpecification.Rgba32(4,4);var rgbaPixels=new byte[64];for(int i=0;i<64;i+=4)rgbaPixels[i]=255;
        jpeg=OmniToolbox.LocalGlamourPreview.RemotePreviewFrames.Encode(rgba,rgbaPixels,System.Threading.CancellationToken.None);
        using(var stream=new MemoryStream(jpeg))using(var bitmap=new System.Drawing.Bitmap(stream)){Check(bitmap.GetPixel(1,1).R>220&&bitmap.GetPixel(1,1).B<40,"CPU RGBA channel conversion preserves colors");}
        bool invalid=false;try{OmniToolbox.LocalGlamourPreview.RemotePreviewFrames.Encode(spec,new byte[1],System.Threading.CancellationToken.None);}catch(InvalidDataException){invalid=true;}Check(invalid,"invalid snapshot cannot read outside owned pixel bytes");
        using(var cancel=new System.Threading.CancellationTokenSource()){cancel.Cancel();bool stopped=false;try{OmniToolbox.LocalGlamourPreview.RemotePreviewFrames.Encode(spec,new byte[64],cancel.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped,"CPU encoder honours cancellation before accessing image memory");}
        Console.WriteLine($"{checks} managed checks passed. Read {actual.Length} Omni presets without modifying them. No game/render/native methods executed.");
    }
}
