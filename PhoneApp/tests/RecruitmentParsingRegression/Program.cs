using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
var paths=args.Skip(1).ToArray();
AssemblyLoadContext.Default.Resolving+=(ctx,name)=>{foreach(var root in paths){var file=Path.Combine(root,name.Name+".dll");if(File.Exists(file))return ctx.LoadFromAssemblyPath(file);}return null;};
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
var phone=assembly.GetType("OmniToolbox.LocalModules.PhoneChat",true)!;
int checks=0;void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS "+label);checks++;}
var dalamud=Assembly.Load("Dalamud");var payload=dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payload",true)!;
var se=dalamud.GetType("Dalamud.Game.Text.SeStringHandling.SeString",true)!;
var text=dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload",true)!;
var pf=dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payloads.PartyFinderPayload",true)!;
var linkKind=pf.GetNestedType("PartyFinderLinkType")!;
var values=Array.CreateInstance(payload,2);values.SetValue(Activator.CreateInstance(text,"测试符号 \ue03f"),0);values.SetValue(Activator.CreateInstance(pf,(uint)321,Enum.Parse(linkKind,"NotSpecified")),1);
var message=Activator.CreateInstance(se,[values])!;
var native=phone.GetMethod("NativeRecruitment",BindingFlags.Static|BindingFlags.NonPublic)!;
var link=native.Invoke(null,[message])!;Check((string)link.GetType().GetProperty("listingId")!.GetValue(link)! =="321","actual native PartyFinder payload yields exact listing ID");
var format=phone.GetMethod("FormatParts",BindingFlags.Static|BindingFlags.NonPublic)!;
var parts=(object[])format.Invoke(null,[message,"#ff00aa"])!;
using(var json=JsonDocument.Parse(JsonSerializer.Serialize(parts))){Check(json.RootElement[0].GetProperty("text").GetString()=="测试符号 \ue03f","private-use game glyphs survive rich-text transport");Check(json.RootElement[0].GetProperty("color").GetString()=="#ff00aa","RGB color preserved without substitution");}
var empty=Activator.CreateInstance(se,[Array.CreateInstance(payload,0)])!;Check(native.Invoke(null,[empty])==null,"ordinary message does not acquire fake recruitment link");
var world=phone.GetMethod("WorldRecruitment",BindingFlags.Static|BindingFlags.NonPublic)!;
object[] input={new Message{Recruitment=new Recruitment{ListingID=987,DutyName="随机任务",Comment="招募说明"}},"encoded"};
var worldLink=world.Invoke(null,input)!;Check((string)worldLink.GetType().GetProperty("listingId")!.GetValue(worldLink)! =="987","Omni structured recruitment retains server-provided game listing ID");Check(input[1].ToString()=="[队伍招募] 随机任务 · 招募说明","structured recruitment renders descriptive text instead of encoded blob");
object[] invalid={new Message{Recruitment=new Recruitment{ListingID=0}},"unchanged"};Check(world.Invoke(null,invalid)==null&&invalid[1].ToString()=="unchanged","invalid recruitment ID is not clickable or rewritten");
object[] encoded={new Message(),"fixture-encoded-recruit"};var encodedLink=world.Invoke(null,encoded)!;Check(encodedLink!=null&&(string)encodedLink.GetType().GetProperty("listingId")!.GetValue(encodedLink)! =="2468","three-parameter public decoder is recognized");
object[] shared={new SharedMessage{SharedPostID=Guid.NewGuid().ToString()},"shared post"};var sharedLink=world.Invoke(null,shared)!;Check((string)sharedLink.GetType().GetProperty("kind")!.GetValue(sharedLink)! =="worldpost","shared world post is not mistaken for a numeric game listing");
var itemType=dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload",true)!;var rawType=dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payloads.RawPayload",true)!;var terminator=rawType.GetField("LinkTerminator",BindingFlags.Public|BindingFlags.Static)?.GetValue(null)??rawType.GetProperty("LinkTerminator",BindingFlags.Public|BindingFlags.Static)!.GetValue(null);var itemValues=Array.CreateInstance(payload,4);itemValues.SetValue(Activator.CreateInstance(itemType,(uint)123,true,"测试物品"),0);itemValues.SetValue(Activator.CreateInstance(text,"测试物品"),1);itemValues.SetValue(terminator,2);itemValues.SetValue(Activator.CreateInstance(text,"普通后缀"),3);var itemMessage=Activator.CreateInstance(se,[itemValues])!;using(var json=JsonDocument.Parse(JsonSerializer.Serialize(format.Invoke(null,[itemMessage,"#ffffff"])))){Check(json.RootElement[0].GetProperty("itemId").GetUInt32()==123&&json.RootElement[0].GetProperty("itemHq").GetBoolean(),"native item ID and HQ survive text link transport");Check(json.RootElement[1].GetProperty("itemId").GetUInt32()==0,"item link terminator prevents unrelated text becoming clickable");}
var rawPhone=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(phone);var decodeLive=phone.GetMethod("DecodeWorldLive",BindingFlags.Instance|BindingFlags.NonPublic)!;var live=new LiveMember{Recruitment=new LiveState{ListingID="3456",ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()}};var liveDecoded=decodeLive.Invoke(rawPhone,[live])!;Check((ulong)liveDecoded.GetType().GetProperty("ListingID")!.GetValue(liveDecoded)! ==3456,"real-time numeric listing remains usable when task metadata was not published");Check(liveDecoded.GetType().GetProperty("Category")!.GetValue(liveDecoded)==null,"unknown real-time category is not invented as other");live.Recruitment.ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();Check(decodeLive.Invoke(rawPhone,[live])==null,"expired world member recruitment is rejected before showing a clickable detail");
var realPostID=Guid.NewGuid();var secondPostID=Guid.NewGuid();
phone.GetField("worldLivePosts",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(rawPhone,new object[]{new BoardPost{PostID=realPostID,AliasID=123,Content="测试",Mode="realtime"},new BoardPost{PostID=secondPostID,AliasID=123,Content="第二条",Mode="realtime"}});
var liveRows=(object[])phone.GetMethod("WorldLiveRows",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(rawPhone,null)!;
Check(liveRows.Length==2,"SDK realtime posts remain visible without a game party-finder listing and preserve multiple posts by one author");
using(var rowsJson=JsonDocument.Parse(JsonSerializer.Serialize(liveRows))){Check(rowsJson.RootElement[0].GetProperty("postId").GetString()==realPostID.ToString()&&rowsJson.RootElement[0].GetProperty("content").GetString()=="测试","SDK realtime post retains its exact detail ID and content");}
var listingType=phone.GetNestedType("DirectListing")!;var lists=Array.CreateInstance(listingType.MakeArrayType(),3);
for(int area=0;area<3;area++){var list=Array.CreateInstance(listingType,area+1);for(int i=0;i<=area;i++){var row=Activator.CreateInstance(listingType)!;listingType.GetField("id")!.SetValue(row,(ulong)(100*area+i+1));listingType.GetField("expires")!.SetValue(row,10000L);list.SetValue(row,i);}lists.SetValue(list,area);}
phone.GetField("directAreaRows",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(rawPhone,lists);
var snapshotArea=phone.GetMethod("DirectAreaSnapshot",BindingFlags.Instance|BindingFlags.NonPublic)!;
for(int area=0;area<3;area++)Check(((Array)snapshotArea.Invoke(rawPhone,[area,1000L])!).Length==area+1,"independent server area "+area+" retains its own complete count without inferring join flags");
Check(((Array)snapshotArea.Invoke(rawPhone,[2,11000L])!).Length==0,"expired listings disappear from all area counts");
var findListing=phone.GetMethod("FindDirectListing",BindingFlags.Instance|BindingFlags.NonPublic)!;
Check(findListing.Invoke(rawPhone,[201UL,1000L])!=null,"password recruitment details resolve from its independent area snapshot");
Check(findListing.Invoke(rawPhone,[999UL,1000L])==null,"unknown recruitment is not invented");
Console.WriteLine(checks+" parsing checks passed. No game calls executed.");
public class Message{public Recruitment? Recruitment{get;set;}}
public class Recruitment{public ulong ListingID{get;set;}public string DutyName{get;set;}="";public string Comment{get;set;}="";}

public class SharedMessage{public string SharedPostID{get;set;}="";}
namespace OmniToolbox.TreePublic{public class OmniWorldChatRecruitment{public ulong ListingID{get;set;}public string DutyName{get;set;}="";public string Comment{get;set;}="";public static bool TryDecode(string text,out OmniWorldChatRecruitment? result,bool worldHidden){result=text=="fixture-encoded-recruit"?new OmniWorldChatRecruitment{ListingID=2468,DutyName="公开接口回归"}:null;return result!=null;}}}

public class LiveMember{public LiveState Recruitment{get;set;}=new();}
public class LiveState{public string ListingID{get;set;}="";public long ExpiresAt{get;set;}}

public class BoardPost{public Guid PostID{get;set;}public int AliasID{get;set;}public string Content{get;set;}="";public string Mode{get;set;}="";}
