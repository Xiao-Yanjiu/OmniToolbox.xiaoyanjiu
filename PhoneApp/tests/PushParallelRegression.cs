using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Reflection;
var roots=args;
AssemblyLoadContext.Default.Resolving+=(context,name)=>{foreach(var root in roots){var path=Path.Combine(root,name.Name+".dll");if(File.Exists(path))return context.LoadFromAssemblyPath(path);}return null;};
await Tests.Run();
static class Tests
{
 [MethodImpl(MethodImplOptions.NoInlining)]
 public static async Task Run()
 {
  var profile=new OmniToolbox.LocalModules.PhoneChat.PushProfile{provider=1,qqEnabled=true,wx="SPT_testkey",qq="123456"};
  var cloudGate=new TaskCompletionSource<OmniToolbox.LocalModules.PhoneChat.PushResult>(TaskCreationOptions.RunContinuationsAsynchronously);
  int cloud=0,qq=0;
  using var router=new OmniToolbox.LocalModules.PhoneChat.PushRouter(()=>profile,_=>{},_=>{},transport:(provider,key,body,ct)=>{Interlocked.Increment(ref cloud);return cloudGate.Task;},start:false,qqSend:(key,text,ct)=>{Interlocked.Increment(ref qq);return Task.FromResult(new OmniToolbox.LocalModules.PhoneChat.PushResult{ok=true});});
  var entry=new OmniToolbox.LocalModules.PhoneChat.ChatEntry{channel="提醒",text="并行测试"};
  var delivery=router.Deliver(entry,profile,CancellationToken.None);
  if(cloud!=1||qq!=1||delivery.IsCompleted)throw new Exception("QQ must send before the pending cloud request completes");
  Console.WriteLine("PASS cloud and QQ start independently rather than fallback order");
  cloudGate.SetResult(new(){ok=false,status="fixture unavailable"});var result=await delivery;
  if(!result.ok||!result.status.Contains("fixture unavailable")||!result.status.Contains("个人QQ接口已接收"))throw new Exception("Partial success status inaccurate");
  Console.WriteLine("PASS cloud failure does not suppress QQ or hide partial failure");
  await router.Deliver(entry,profile,CancellationToken.None);
  if(cloud!=1||qq!=2)throw new Exception("Cloud cooldown must not suppress QQ");
  Console.WriteLine("PASS failed provider cooldown leaves QQ available");
  profile.provider=0;await router.Deliver(entry,profile,CancellationToken.None);if(qq!=3)throw new Exception("QQ only disabled incorrectly");
  Console.WriteLine("PASS QQ works with cloud push disabled");
  profile.direct=true;await router.Deliver(entry,profile,CancellationToken.None);if(qq!=3)throw new Exception("Direct receiver suppression changed");
  Console.WriteLine("PASS online phone retains direct notification policy");
 }
}
