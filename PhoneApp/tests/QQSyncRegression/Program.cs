using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Collections;
var roots=args.Skip(1).Take(2).ToArray();
AssemblyLoadContext.Default.Resolving+=(ctx,name)=>{foreach(var root in roots){var file=Path.Combine(root,name.Name+".dll");if(File.Exists(file))return ctx.LoadFromAssemblyPath(file);}return null;};
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
var phone=assembly.GetType("OmniToolbox.LocalModules.PhoneChat",true)!;
int checks=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
var temp=Path.GetFullPath(args[3]);Directory.CreateDirectory(temp);var path=Path.Combine(temp,"replay-"+Guid.NewGuid()+".json");
var gateType=phone.GetNestedType("QQReplayGate")!;
object NewGate()=>Activator.CreateInstance(gateType,[path])!;
var originalTime=gateType.GetMethod("IsFreshTimestamp")!;var begin=gateType.GetMethod("BeginSession")!;var accept=gateType.GetMethod("TryAccept")!;
var start=DateTimeOffset.FromUnixTimeSeconds(2000000000).AddMilliseconds(500);
bool Accept(object gate,string bot,uint group,string id,long sent,DateTimeOffset now)=>(bool)accept.Invoke(gate,[bot,group,id,sent,now])!;
var gate=NewGate();begin.Invoke(gate,[start]);
Check(!(bool)originalTime.Invoke(gate,[start.ToUnixTimeSeconds()-10,start.AddSeconds(2)])!,"original send timestamp rejects old messages even when event delivery is new");
Check(!Accept(gate,"123456",9,"10",start.ToUnixTimeSeconds()-50,start),"pre-connection QQ backlog is rejected");
Check(!Accept(gate,"123456",9,"",start.ToUnixTimeSeconds()+1,start.AddSeconds(2)),"missing QQ message identity is rejected");
Check(Accept(gate,"123456",9,"11",start.ToUnixTimeSeconds()+1,start.AddSeconds(2)),"fresh QQ event is durably claimed before dispatch");
Check(!Accept(gate,"123456",9,"11",start.ToUnixTimeSeconds()+1,start.AddSeconds(2)),"same event cannot dispatch twice on one connection");
var restarted=NewGate();begin.Invoke(restarted,[start.AddSeconds(10)]);
Check(!Accept(restarted,"123456",9,"11",start.ToUnixTimeSeconds()+11,start.AddSeconds(12)),"replayed event stays rejected after process restart even with a rewritten timestamp");
Check(Accept(restarted,"654321",9,"11",start.ToUnixTimeSeconds()+11,start.AddSeconds(12)),"message IDs are scoped to robot account and group");
Check(!File.ReadAllText(path).Contains("123456"),"durable replay record contains hashes rather than account/message content");
File.WriteAllText(path,"broken");var damaged=NewGate();begin.Invoke(damaged,[start]);Check(!Accept(damaged,"123456",9,"12",start.ToUnixTimeSeconds()+1,start.AddSeconds(2)),"unreadable replay state fails closed instead of replaying commands");
var settingsType=phone.GetNestedType("Settings",BindingFlags.NonPublic)!;var settings=Activator.CreateInstance(settingsType,true)!;
var channels=(IList)settingsType.GetProperty("QQChannels")!.GetValue(settings)!;Check(channels.Count==0,"new settings do not preselect QQ synchronization channels");
var instance=RuntimeHelpers.GetUninitializedObject(phone);void Set(string name,object value)=>phone.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(instance,value);
Set("settings",settings);Set("enabled",true);Set("qqGate",new object());Set("qqMirrorSince",start);Set("qqMirrorReady",true);
settingsType.GetProperty("QQMirror")!.SetValue(settings,true);settingsType.GetProperty("QQGroups")!.SetValue(settings,"123456");
var nap=assembly.GetType("OmniToolbox.LocalModules.PhoneNapCat",true)!;var bridge=RuntimeHelpers.GetUninitializedObject(nap);nap.GetField("<Online>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(bridge,true);Set("qqBridge",bridge);
var queueField=phone.GetField("qqMirrorQueue",BindingFlags.Instance|BindingFlags.NonPublic)!;var queue=Activator.CreateInstance(queueField.FieldType)!;queueField.SetValue(instance,queue);int Count()=>(int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
var mirror=phone.GetMethod("QueueQQEvent",BindingFlags.Instance|BindingFlags.NonPublic)!;
mirror.Invoke(instance,["小队","测试玩家","未选择频道",start.AddSeconds(2)]);Check(Count()==0,"unselected channels never enter the QQ send queue");
channels.Add("小队");mirror.Invoke(instance,["小队","测试玩家","历史消息",start.AddSeconds(-10)]);Check(Count()==0,"old message timestamps cannot enter live synchronization");
mirror.Invoke(instance,["小队","测试玩家","新消息",start.AddSeconds(2)]);Check(Count()==1,"selected fresh live event enters the queue without reading a history database");
var match=phone.GetMethod("IsRecruitmentFailure")!;Check((bool)match.Invoke(null,["无法获取招募者的情报。"])!,"real game recruitment error text is recognized");Check(!(bool)match.Invoke(null,["普通聊天内容"])!,"ordinary text is not classified as expired recruitment");

var dalamud=Assembly.Load("Dalamud");var chatInterface=dalamud.GetType("Dalamud.Game.Chat.IChatMessage",true)!;var payloadType=dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payload",true)!;var textType=dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload",true)!;var seType=dalamud.GetType("Dalamud.Game.Text.SeStringHandling.SeString",true)!;var enumType=dalamud.GetType("Dalamud.Game.Text.XivChatType",true)!;
var payloads=Array.CreateInstance(payloadType,1);payloads.SetValue(Activator.CreateInstance(textType,"无法获取招募者的情报。"),0);var se=Activator.CreateInstance(seType,[payloads])!;
var chatProxy=(ChatProxy)DispatchProxy.Create(chatInterface,typeof(ChatProxy));chatProxy.Values["get_Message"]=se;chatProxy.Values["get_LogKind"]=Enum.Parse(enumType,"ErrorMessage");
var failure=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);Set("recruitmentFailure",failure);Set("recruitmentRequestedAt",Environment.TickCount64);
var observe=phone.GetMethod("ObserveRecruitmentFailure",BindingFlags.Instance|BindingFlags.NonPublic)!;var elapsed=System.Diagnostics.Stopwatch.StartNew();observe.Invoke(instance,[chatProxy]);
Check(failure.Task.IsCompletedSuccessfully&&elapsed.ElapsedMilliseconds<200,"real IChatMessage error payload wakes a pending recruitment immediately");
var ordinary=new TaskCompletionSource<string>();Set("recruitmentFailure",ordinary);chatProxy.Values["get_LogKind"]=Enum.Parse(enumType,"Say");observe.Invoke(instance,[chatProxy]);Check(!ordinary.Task.IsCompleted,"a player quoting the same error text cannot cancel recruitment");

// Direct recruitment cache tests do not install a native hook or issue game requests.
var cacheType=phone.GetNestedType("DirectBoardCache")!;var listingType=phone.GetNestedType("DirectListing")!;
var cache=Activator.CreateInstance(cacheType)!;
object Listing(ulong id,long expires){var row=Activator.CreateInstance(listingType)!;listingType.GetField("id")!.SetValue(row,id);listingType.GetField("expires")!.SetValue(row,expires);return row;}
void CacheCall(string method,params object[] values)=>cacheType.GetMethod(method)!.Invoke(cache,values);
Array Rows(params object[] values){var result=Array.CreateInstance(listingType,values.Length);for(int i=0;i<values.Length;i++)result.SetValue(values[i],i);return result;}
bool Complete(long now)=>(bool)cacheType.GetMethod("Complete")!.Invoke(cache,[now])!;
Array Snapshot(long now)=>(Array)cacheType.GetMethod("Snapshot")!.Invoke(cache,[now])!;
CacheCall("Begin",1000L);Check(!Complete(2000L),"missing recruitment response is not mistaken for an empty board");
CacheCall("Packet",Rows(Listing(11,10000)),1100L);Check(!Complete(1300L),"waits for fragmented recruitment response to settle");
CacheCall("Packet",Rows(Listing(12,10000),Listing(11,10000)),1300L);Check(Complete(1700L)&&Snapshot(1700L).Length==2,"received segments are merged and duplicate listing IDs are removed");
CacheCall("Begin",2000L);Check(Snapshot(2100L).Length==2,"previous complete data remains available while a refresh is pending");CacheCall("Packet",Rows(),2100L);Check(Complete(2500L)&&Snapshot(2500L).Length==0,"an actual empty packet clears the previous board");
CacheCall("Begin",3000L);CacheCall("Packet",Rows(Listing(13,3500)),3100L);Complete(3450L);Check(Snapshot(3501L).Length==0,"expired recruitment entries are removed locally");
CacheCall("Begin",4000L);CacheCall("Cancel");CacheCall("Packet",Rows(Listing(14,10000)),4100L);Check(!Complete(4500L),"cancelled board requests cannot publish late packets");
CacheCall("Clear");Check(Snapshot(4500L).Length==0&&(long)cacheType.GetProperty("UpdatedAt")!.GetValue(cache)! == 0,"logout clears recruitment data from the previous character");

var receivedAt=Environment.TickCount64;CacheCall("Begin",receivedAt);var detailListing=Listing(99,receivedAt+60000);listingType.GetField("leader")!.SetValue(detailListing,"测试招募人");listingType.GetField("comment")!.SetValue(detailListing,"测试说明");listingType.GetField("area")!.SetValue(detailListing,(byte)8);CacheCall("Packet",Rows(detailListing),receivedAt+1);Complete(receivedAt+351);Set("directBoard",cache);Set("recruitmentListing",99UL);Set("recruitmentView","fixture-details");Set("recruitmentNativeOpened",false);
var readDetail=phone.GetMethod("ReadRecruitment",BindingFlags.NonPublic|BindingFlags.Instance)!;var detail=System.Text.Json.JsonSerializer.SerializeToElement(readDetail.Invoke(instance,null));Check(detail.GetProperty("open").GetBoolean()&&detail.GetProperty("source").GetString()=="game-network"&&detail.GetProperty("leader").GetString()=="测试招募人","detail is readable from real received records without a native game window or framework");
var areaMatch=phone.GetMethod("MatchesDirectArea")!;Check((bool)areaMatch.Invoke(null,[detailListing,1])!&&!(bool)areaMatch.Invoke(null,[detailListing,2])!,"server-only filter uses received world flags without making another game request");listingType.GetField("area")!.SetValue(detailListing,(byte)2);Check((bool)areaMatch.Invoke(null,[detailListing,2])!&&!(bool)areaMatch.Invoke(null,[detailListing,0])!,"password recruitment filters actual private flags");

File.Delete(path);Console.WriteLine(checks+" checks passed; no QQ or game messages sent.");

public class ChatProxy:DispatchProxy{public Dictionary<string,object> Values=new();protected override object? Invoke(MethodInfo? method,object?[]? args){if(method!=null&&Values.TryGetValue(method.Name,out var value))return value;return method?.ReturnType.IsValueType==true?Activator.CreateInstance(method.ReturnType):null;}}
