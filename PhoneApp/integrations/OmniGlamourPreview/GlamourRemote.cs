using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Ipc;
using OmenTools;
using OmniToolbox.Host;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;

namespace OmniToolbox.LocalGlamourPreview;

public sealed unsafe partial class OmniGlamourPreview
{
    private ICallGateProvider<string,string>? remoteGate;
    private long remoteUntil;
    private ICallGateProvider<string,string>? frameCacheGate;
    private string phoneSelectedId="";
    private bool remoteActive;
    private static readonly JsonSerializerOptions RemoteJson=new(){PropertyNameCaseInsensitive=true};
    private void EnableRemote()
    {
        saveConfig(); // Persist stable IDs assigned during migration from earlier presets.
        remoteGate=DalamudServices.PluginInterface.GetIpcProvider<string,string>("Omni.Glamour.Management.V1");
        remoteGate.RegisterFunc(RemoteRequest);
        frameCacheGate=DalamudServices.PluginInterface.GetIpcProvider<string,string>("Omni.Glamour.FrameCache.V1");
        frameCacheGate.RegisterFunc(ReadFrameCache);
    }
    private void DisableRemote(){frameCacheGate?.UnregisterFunc();frameCacheGate=null;remoteGate?.UnregisterFunc();remoteGate=null;remoteActive=false;}
    private string ReadFrameCache(string request)
    {


        if(!request.StartsWith("frame:")||request[6..]!=System.Threading.Volatile.Read(ref phoneSelectedId))return "{\"ok\":true,\"changed\":true}";
        System.Threading.Interlocked.Exchange(ref remoteUntil,Environment.TickCount64+5000);
        return portraitPreview?.CachedPhoneFrame()??"{\"ok\":false,\"error\":\"预览未就绪\"}";
    }
    private void RemoteTick()
    {
        System.Threading.Volatile.Write(ref phoneSelectedId,selectedPresetIndex>=0&&selectedPresetIndex<config.Presets.Count?config.Presets[selectedPresetIndex].Id:"");
        if(remoteActive&&Environment.TickCount64>System.Threading.Interlocked.Read(ref remoteUntil)){remoteActive=false;if(managerNativeUI?.IsOpen!=true)portraitPreview?.CancelPreview();}
    }
    internal static string Revision(IEnumerable<BetterGlamourPreset> entries)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries))));
    private string RemoteRequest(string json)
    {
        try
        {
            if(!DService.Instance().Framework.IsInFrameworkUpdateThread)throw new InvalidOperationException("必须通过游戏线程访问幻化管理。");
            if(json.Length>65536)throw new InvalidDataException("请求过大。");
            using var doc=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=20});var r=doc.RootElement;
            string action=r.GetProperty("action").GetString()??"";
            string id=r.TryGetProperty("id",out var idValue)?idValue.GetString()??"":"";
            var preset=config.Presets.Find(p=>p.Id==id);
            if(action is "save" or "delete" or "apply" or "preview-draft")
            {
                if(!r.TryGetProperty("revision",out var revision)||revision.GetString()!=Revision(config.Presets))throw new InvalidOperationException("方案已在另一端更改，请刷新后重试。");
            }
            if(action is "select" or "save" or "delete" or "apply" or "export" or "preview-draft")
                if(preset==null)throw new InvalidOperationException("方案已删除，请刷新列表。");
            if(action is "camera" or "button")
                if(id.Length==0||selectedPresetIndex<0||selectedPresetIndex>=config.Presets.Count||config.Presets[selectedPresetIndex].Id!=id)throw new InvalidOperationException("另一端已切换预览，请重新选择方案。");
            if(action=="frame")
            {
                remoteUntil=Environment.TickCount64+5000;
                if(id.Length>0&&(selectedPresetIndex<0||selectedPresetIndex>=config.Presets.Count||config.Presets[selectedPresetIndex].Id!=id))return JsonSerializer.Serialize(new{ok=true,image="",changed=true});
                return portraitPreview!.PhoneFrame();
            }
            if(action=="search")
            {
                string kind=r.GetProperty("kind").GetString()??"equipment",query=r.GetProperty("query").GetString()??"";
                if(query.Length>100)throw new InvalidDataException("搜索过长。");
                var rows=kind=="hair"?SearchHairstyles(query):kind=="glasses"?SearchGlasses(query):SearchItems(query).Where(x=>x.ItemID==0||LuminaGetter.TryGetRow<Item>(x.ItemID,out var item)&&FitsSlot(item,r.GetProperty("slot").GetInt32())).ToList();
                return JsonSerializer.Serialize(new{ok=true,rows=rows.Take(60).Select(x=>new{id=x.ItemID,name=x.Name,iconId=x.IconID})});
            }
            if(action=="export")return JsonSerializer.Serialize(new{ok=true,text=GlamourClipboard.Export(preset!)});
            if(action=="import"||action=="import-website")
            {
                if(config.Presets.Count>=MaxPresetCount)throw new InvalidOperationException("幻化条目已满。");
                var incoming=action=="import"?GlamourClipboard.Import(r.GetProperty("text").GetString()??""):WebsitePreset(r.GetProperty("data"));
                ValidateRemotePreset(incoming);
                config.Presets.Add(incoming);try{saveConfig();}catch{config.Presets.Remove(incoming);throw;}
                managerNativeUI?.UpdateData(config.Presets,selectedPresetIndex=config.Presets.Count-1);
                SelectPreset(incoming);remoteActive=true;remoteUntil=Environment.TickCount64+5000;
            }
            else if(action=="select"){SelectPreset(preset!);managerNativeUI?.UpdateData(config.Presets,selectedPresetIndex);remoteActive=true;remoteUntil=Environment.TickCount64+5000;}
            else if(action=="preview-draft")
            {
                var incoming=GlamourClipboard.Import(r.GetProperty("preset").GetRawText());ValidateRemotePreset(incoming);
                if(selectedPresetIndex<0||config.Presets[selectedPresetIndex].Id!=id)throw new InvalidOperationException("另一端已切换预览，请重新选择方案。");
                QueuePortraitPreview(incoming);remoteActive=true;remoteUntil=Environment.TickCount64+5000;return "{\"ok\":true}";
            }
            else if(action=="save")
            {
                var incoming=GlamourClipboard.Import(r.GetProperty("preset").GetRawText());ValidateRemotePreset(incoming);
                int gear=r.TryGetProperty("gearsetIndex",out var gearValue)?gearValue.GetInt32():preset!.GearsetIndex;
                if(gear < -1||gear>99||gear>=0&&!TryGetGearsetName(gear,out _))throw new InvalidDataException("职业套装无效。");
                incoming.GearsetIndex=preset!.GearsetIndex;
                incoming.Id=preset!.Id;var oldName=preset.Name;var oldHair=preset.HairstyleID;var oldGlasses=preset.GlassesID;var oldItems=preset.Items;
                preset.Name=incoming.Name;preset.HairstyleID=incoming.HairstyleID;preset.GlassesID=incoming.GlassesID;preset.Items=incoming.Items;
                try{saveConfig();}catch{preset.Name=oldName;preset.HairstyleID=oldHair;preset.GlassesID=oldGlasses;preset.Items=oldItems;throw;}incoming=preset;
                SelectPreset(incoming);managerNativeUI?.UpdateData(config.Presets,selectedPresetIndex);remoteActive=true;remoteUntil=Environment.TickCount64+5000;
                if(gear!=incoming.GearsetIndex)ChangePresetGearset(incoming,gear);
            }
            else if(action=="delete")DeletePreset(preset!);
            else if(action=="apply")StartTryOn(preset!);
            else if(action=="restore")ClearTryOn();
            else if(action=="camera")
            {
                float Read(string key){float f=r.TryGetProperty(key,out var value)?value.GetSingle():0;if(!float.IsFinite(f)||Math.Abs(f)>100)throw new InvalidDataException("相机增量无效。");return f;}
                portraitPreview?.CameraPixels(Read("yaw"),Read("x"),Read("y"),Math.Clamp(Read("wheel"),-3,3));
                remoteUntil=Environment.TickCount64+5000;
                return "{\"ok\":true}";
            }
            else if(action=="button"){uint node=r.GetProperty("node").GetUInt32();if(!new uint[]{32,33,34,35,37,39}.Contains(node))throw new InvalidDataException("未知预览按钮。");portraitPreview?.ClickNativeButton(node);return "{\"ok\":true}";}
            else if(action=="stop"){remoteActive=false;if(managerNativeUI?.IsOpen!=true)portraitPreview?.CancelPreview();return "{\"ok\":true}";}
            else if(action!="read"&&action!="import"&&action!="import-website")throw new InvalidDataException("未知幻化操作。");
            return RemoteSnapshot(action=="read"&&(!r.TryGetProperty("assets",out var assets)||assets.ValueKind!=JsonValueKind.False));
        }
        catch(Exception e){return JsonSerializer.Serialize(new{ok=false,error=e.Message});}
    }
    private string RemoteSnapshot(bool assets=true)
    {
        var gearsets=new List<object>();for(int i=0;i<100;i++)if(TryGetGearsetName(i,out var name))gearsets.Add(new{id=i,name});
        var slots=new[]{0,1,2,3,4,6,7,8,9,10,11,12};
        return JsonSerializer.Serialize(new{ok=true,revision=Revision(config.Presets),selectedId=selectedPresetIndex>=0&&selectedPresetIndex<config.Presets.Count?config.Presets[selectedPresetIndex].Id:"",
            presets=config.Presets.Select(p=>new{id=p.Id,name=p.Name,gearsetIndex=p.GearsetIndex,glassesId=p.GlassesID,glassesName=GetGlassesName(p.GlassesID),glassesIcon=GetGlassesIcon(p.GlassesID),hairstyleId=p.HairstyleID,hairstyleName=GetHairstyleName(p.HairstyleID),hairstyleIcon=GetHairstyleIcon(p.HairstyleID),items=slots.Select(slot=>{var x=p.Items.Find(x=>x.Slot==slot)??new BetterGlamourItem{Slot=slot};LuminaGetter.TryGetRow<Item>(x.ItemID,out var row);return new{slot,itemId=x.ItemID,stain0=x.Stain0,stain1=x.Stain1,name=x.ItemID==0?"无":row.Name.ToString(),iconId=x.ItemID==0?0:row.Icon};})}),
            dyes=DyeOptions.Select(d=>new{id=d.ID,name=d.Name,color=$"#{(int)(d.Color.X*255):x2}{(int)(d.Color.Y*255):x2}{(int)(d.Color.Z*255):x2}"}),gearsets,buttonAtlas=assets?NativeButtonAtlas():"",
            preview=JsonSerializer.Deserialize<JsonElement>(portraitPreview!.Status()),buttons=new uint[]{32,33,34,35,37,39}.Select(node=>new{node,active=portraitPreview.NativeButtonChecked(node)})});
    }
    private string? nativeButtonAtlas;
    private string NativeButtonAtlas()
    {
        if(nativeButtonAtlas!=null)return nativeButtonAtlas;
        try
        {
            var tex=DalamudServices.DataManager.GetFile<Lumina.Data.Files.TexFile>("ui/uld/CircleButtons.tex");
            if(tex==null)return "";
            byte[] pixels=tex.ImageData;
            fixed(byte* data=pixels)
            {
                using var bitmap=new System.Drawing.Bitmap(tex.Header.Width,tex.Header.Height,tex.Header.Width*4,System.Drawing.Imaging.PixelFormat.Format32bppArgb,(nint)data);
                using var output=new MemoryStream();bitmap.Save(output,System.Drawing.Imaging.ImageFormat.Png);
                return nativeButtonAtlas="data:image/png;base64,"+Convert.ToBase64String(output.ToArray());
            }
        }
        catch{return "";}
    }
    private static void ValidateRemotePreset(BetterGlamourPreset preset)
    {
        foreach(var item in preset.Items.Where(x=>x.ItemID!=0))
        {
            if(!LuminaGetter.TryGetRow<Item>(item.ItemID,out var row)||!FitsSlot(row,item.Slot))throw new InvalidDataException("装备与部位不匹配："+item.ItemID);
            foreach(var dye in new[]{item.Stain0,item.Stain1})if(dye!=0&&!LuminaGetter.TryGetRow<Stain>(dye,out _))throw new InvalidDataException("染色不存在。");
        }
        if(preset.GlassesID!=0&&!LuminaGetter.TryGetRow<Glasses>(preset.GlassesID,out _))throw new InvalidDataException("面部配饰不存在。");
        if(preset.HairstyleID!=0&&!LuminaGetter.TryGetRow<CharaMakeCustomize>(preset.HairstyleID,out _))throw new InvalidDataException("发型不存在。");
    }
    private static bool FitsSlot(Item item,int slot){if(item.EquipSlotCategory.RowId==0)return false;var e=item.EquipSlotCategory.Value;return slot switch{0=>e.MainHand>0,1=>e.OffHand>0,2=>e.Head>0,3=>e.Body>0,4=>e.Gloves>0,6=>e.Legs>0,7=>e.Feet>0,8=>e.Ears>0,9=>e.Neck>0,10=>e.Wrists>0,11=>e.FingerR>0,12=>e.FingerL>0,_=>false};}
    private static BetterGlamourPreset WebsitePreset(JsonElement data)
    {
        var result=new BetterGlamourPreset{Name=data.GetProperty("name").GetString()??""};
        var parts=data.GetProperty("parts");if(parts.GetArrayLength()>16)throw new InvalidDataException("网页部位过多。");
        var map=new Dictionary<string,int>{{"主手",0},{"副手",1},{"头部",2},{"上衣",3},{"身体",3},{"手部",4},{"腿部",6},{"脚部",7},{"耳坠",8},{"耳饰",8},{"项链",9},{"手镯",10}};int rings=0;
        foreach(var part in parts.EnumerateArray())
        {
            string label=part.GetProperty("part").GetString()??"",name=part.GetProperty("name").GetString()??"";
            if(name.Length>150)throw new InvalidDataException("网页物品名过长。");
            if(label=="时尚配饰")continue;
            if(label=="面部配饰"){if(name.Length>0){var matches=LuminaGetter.Get<Glasses>().Where(x=>x.Name.ToString()==name).ToArray();if(matches.Length!=1)throw new InvalidDataException("面饰名称不唯一或不存在："+name);result.GlassesID=(ushort)matches[0].RowId;}continue;}
            int slot=label=="戒指"?(rings++==0?12:11):map.TryGetValue(label,out var found)?found:throw new InvalidDataException("未知部位："+label);
            var item=new BetterGlamourItem{Slot=slot};if(name.Length>0){var matches=LuminaGetter.Get<Item>().Where(x=>x.Name.ToString()==name&&FitsSlot(x,slot)).ToArray();if(matches.Length!=1)throw new InvalidDataException("装备名称不唯一或不存在："+name);item.ItemID=matches[0].RowId;}
            var dyes=part.GetProperty("dyes");if(dyes.GetArrayLength()>2)throw new InvalidDataException("染色数量无效。");
            byte Dye(int i){if(i>=dyes.GetArrayLength())return 0;string text=(dyes[i].GetString()??"").Replace("染剂","").Trim();if(text.Length==0||text is "无染色" or "未染色" or "不可染色")return 0;var match=DyeOptions.Where(x=>x.Name==text).ToArray();if(match.Length!=1)throw new InvalidDataException("染色无法识别："+text);return match[0].ID;}
            item.Stain0=Dye(0);item.Stain1=Dye(1);result.Items.Add(item);
        }
        if(result.Items.Count!=12||rings!=2)throw new InvalidDataException("请等待幻化详情装备列表加载完整。");
        return GlamourClipboard.Import(GlamourClipboard.Export(result));
    }
}
