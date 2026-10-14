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

File.Delete(path);Console.WriteLine(checks+" checks passed; no QQ or game messages sent.");

public class ChatProxy:DispatchProxy{public Dictionary<string,object> Values=new();protected override object? Invoke(MethodInfo? method,object?[]? args){if(method!=null&&Values.TryGetValue(method.Name,out var value))return value;return method?.ReturnType.IsValueType==true?Activator.CreateInstance(method.ReturnType):null;}}
