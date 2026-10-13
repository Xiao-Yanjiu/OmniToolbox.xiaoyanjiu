// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Yanjiu.Modules;

if(args.Length is not (3 or 4))throw new ArgumentException("Pass module source, Yanjiu library folder and Dalamud API folder");
AssemblyLoadContext.Default.Resolving+=(context,name)=>{
 foreach(var folder in args.Skip(1)){var file=Path.Combine(folder,name.Name+".dll");if(File.Exists(file))return context.LoadFromAssemblyPath(file);}return null;
};
var compiler=new SourceCompiler();
using var compiled=compiler.Compile(File.ReadAllBytes(args[0]),args[0]);
var type=compiled.ModuleType;
void Check(bool condition,string label){if(!condition)throw new Exception(label);}
var helper=compiled.Assembly.GetType("OmniToolbox.LocalModules.PhoneNapCat",true)!;
Check(type.GetField("qqAuthToken",BindingFlags.Instance|BindingFlags.NonPublic)==null,"no user authorization token required");
var account=helper.GetMethod("ValidAccount")!;
foreach(var fixture in new[]{("",true),("123456",true),("123;cmd",false),("abc",false)})Check((bool)account.Invoke(null,[fixture.Item1])! ==fixture.Item2,"bot account validation");
var path=helper.GetMethod("SafeArchivePath")!;
foreach(var fixture in new[]{("node.exe",true),("../escape",false),("C:/escape",false),("/escape",false),("bin/../../escape",false)})Check((bool)path.Invoke(null,[fixture.Item1])! ==fixture.Item2,"archive traversal validation");
var instance=RuntimeHelpers.GetUninitializedObject(type);
var connect=type.GetMethod("ConnectQQ",BindingFlags.Instance|BindingFlags.NonPublic)!;
var status=type.GetField("qqStatus",BindingFlags.Instance|BindingFlags.NonPublic)!;
connect.Invoke(instance,[true]);Check(((string)status.GetValue(instance)!).Contains("启用"),"disabled module feedback");
type.GetField("enabled",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(instance,true);
var settings=type.GetField("settings",BindingFlags.Instance|BindingFlags.NonPublic)!;
settings.SetValue(instance,Activator.CreateInstance(settings.FieldType,true));
var botField=settings.FieldType.GetProperty("QQBotAccount")!;botField.SetValue(settings.GetValue(instance),"invalid");connect.Invoke(instance,[true]);Check(((string)status.GetValue(instance)!).Contains("格式"),"invalid bot feedback");
Check((int)type.GetField("qqLoginRunning",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(instance)! ==0,"invalid input does not start login");
var error=(string)type.GetMethod("QQLoginError",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[new Exception("test-secret https://example.com?token=test-secret"),"test-secret"])!;
Check(!error.Contains("test-secret")&&error.Contains("登录失败"),"credential redaction");
var parse=helper.GetMethod("ParseEvent")!;
Check(parse.Invoke(null,["{\"post_type\":\"message\",\"self_id\":1,\"user_id\":2,\"group_id\":3,\"message\":[{\"type\":\"text\",\"data\":{\"text\":\"#test\"}}]}"])!=null,"text command event accepted");
Check(parse.Invoke(null,["{\"post_type\":\"message\",\"self_id\":2,\"user_id\":2,\"message\":\"#test\"}"])==null,"self events ignored");
Check(parse.Invoke(null,["{\"post_type\":\"message\",\"user_id\":2,\"message\":\"normal chat\"}"])==null,"ordinary chat ignored");
Check(parse.Invoke(null,["{\"post_type\":\"message\",\"user_id\":2,\"message\":[{\"type\":\"image\",\"data\":{\"text\":\"#test\"}}]}"])==null,"nontext segments cannot execute replies");
var decode=helper.GetMethod("DecodeQr")!;
try{decode.Invoke(null,["data:image/png;base64,"+Convert.ToBase64String([1,2,3,4])]);throw new Exception("invalid QR accepted");}catch(TargetInvocationException e){Check(e.InnerException is IOException,"QR must be PNG");}
Console.WriteLine("PASS: module compiled, NapCat inputs, immediate feedback, QR validation, event parsing and archive safety");
// Local mock validates actual send headers, message segments and business error handling.
var client=Activator.CreateInstance(helper,BindingFlags.Instance|BindingFlags.Public,null,[Path.Combine(Path.GetTempPath(),"llbot-unused"),(Action<string>)(_=>{}),(Action<string,byte[]>)((_,_)=>{}),(Action<uint,uint,string,string>)((_,_,_,_)=>{}),(Action<string>)(_=>{})],null)!;
var tcp=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);tcp.Start();
int port=((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
helper.GetField("apiPort",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(client,port);
helper.GetField("apiToken",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(client,"test-only-token");
helper.GetProperty("Online")!.SetValue(client,true);
async Task Serve(string response,string expectedPath,bool inspect){using var peer=await tcp.AcceptTcpClientAsync();using var stream=peer.GetStream();using var input=new StreamReader(stream,System.Text.Encoding.UTF8,false,4096,true);var first=await input.ReadLineAsync();Check(first!.Contains(expectedPath),"send API route");int size=0;bool auth=false;string? line;while(!string.IsNullOrEmpty(line=await input.ReadLineAsync())){if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))size=int.Parse(line.Split(':')[1]);if(line=="Authorization: Bearer test-only-token")auth=true;}Check(auth,"local API uses bearer credential");var body=new char[size];await input.ReadBlockAsync(body,0,size);if(inspect){var json=System.Text.Json.JsonDocument.Parse(new string(body));Check(json.RootElement.GetProperty("group_id").GetUInt32()==123456,"group id");var message=json.RootElement.GetProperty("message");Check(message[0].GetProperty("type").GetString()=="at","mention segment");Check(message[1].GetProperty("data").GetProperty("text").GetString()=="[CQ:at,qq=1] plain text","CQ-like text remains literal");}var bytes=System.Text.Encoding.UTF8.GetBytes(response);var header=System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+bytes.Length+"\r\nConnection: close\r\n\r\n");await stream.WriteAsync(header);await stream.WriteAsync(bytes);}
try{
 var serve=Serve("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":123}}","/send_group_msg",true);
 var result=await (Task<string>)helper.GetMethod("Send")!.Invoke(client,[123456u,true,"[CQ:at,qq=1] plain text",654321u,CancellationToken.None])!;await serve;Check(result=="","send success");
 serve=Serve("{\"status\":\"failed\",\"retcode\":100,\"data\":null}","/send_private_msg",false);
 result=await (Task<string>)helper.GetMethod("Send")!.Invoke(client,[123456u,false,"test",0u,CancellationToken.None])!;await serve;Check(result.Length>0,"business failure cannot report success");
}finally{tcp.Stop();((IDisposable)client).Dispose();}
Console.WriteLine("PASS: NapCat HTTP authorization, literal text and mention segments, success/failure checks");

if(args.Length==4){
 string runtime=args[3];Directory.CreateDirectory(runtime);
 var png=(byte[])helper.GetMethod("QrPng")!.Invoke(null,["https://qrcode.qq.com/pcweb/login?d=test-only"])!;
 Check(png.Length>100,"QR encoding produced PNG");File.WriteAllBytes(Path.Combine(runtime,"test-qr.png"),png);
 foreach(var invalid in new[]{"http://qrcode.qq.com/test","https://qrcode.qq.com.evil.example/test","https://user@qrcode.qq.com/test"})Check(!(bool)helper.GetMethod("ValidQrUrl")!.Invoke(null,[invalid])!,"untrusted QR URL rejected");
 string qrFile=Path.Combine(runtime,"live-qr.png"),lastStatus="";
 var probe=Activator.CreateInstance(helper,BindingFlags.Instance|BindingFlags.Public,null,[runtime,(Action<string>)(value=>{lastStatus=value;}),(Action<string,byte[]>)((_,bytes)=>File.WriteAllBytes(qrFile,bytes)),(Action<uint,uint,string,string>)((_,_,_,_)=>{}),(Action<string>)(_=>{})],null)!;
 var web=helper.GetMethod("Web",BindingFlags.NonPublic|BindingFlags.Instance)!;
 try{
  await (Task)helper.GetMethod("Start")!.Invoke(probe,["",true,true])!;
  var state=await (Task<System.Text.Json.JsonElement>)web.Invoke(probe,["/api/QQLogin/CheckLoginStatus",new{},CancellationToken.None])!;
  Check(!state.GetProperty("isLogin").GetBoolean(),"no account cannot report online");
  Check(state.GetProperty("loginPhase").GetString()!=null,"real login phase received");
  bool qrReady=false;for(int retry=0;retry<45;retry++){if(File.Exists(qrFile)){qrReady=true;break;}await Task.Delay(1000);}
  if(!qrReady){var diag=await (Task<System.Text.Json.JsonElement>)web.Invoke(probe,["/api/QQLogin/CheckLoginStatus",new{},CancellationToken.None])!;Console.WriteLine("Native startup phase: "+diag.GetProperty("loginPhase").GetString()+"; module status: "+lastStatus);Console.WriteLine("Private profile initialized: "+Directory.Exists(Path.Combine(runtime,"qq-data","nt_qq","global")));}if(!qrReady){var diag=await (Task<System.Text.Json.JsonElement>)web.Invoke(probe,["/api/QQLogin/CheckLoginStatus",new{},CancellationToken.None])!;var url=diag.GetProperty("qrcodeurl").GetString()??"";if(Uri.TryCreate(url,UriKind.Absolute,out var address))Console.WriteLine("QR address scheme/host: "+address.Scheme+" / "+address.Host);Console.WriteLine("QR length: "+url.Length);if(url.Length>0){try{helper.GetMethod("QrPng")!.Invoke(null,[url]);}catch(TargetInvocationException e){Console.WriteLine("QR render failure type: "+e.InnerException?.GetType().Name);Console.WriteLine("QR render failure: "+e.InnerException?.Message);}}}Check(qrReady,"real QQ QR is generated without authorization review");
  var image=File.ReadAllBytes(qrFile);helper.GetMethod("DecodeQr")!.Invoke(null,[Convert.ToBase64String(image)]);
  Check(!(bool)helper.GetProperty("Online")!.GetValue(probe)!,"QR generation is not a logged-in account");
  var bootFile=Path.Combine(runtime,"bootstrap.cjs");Check(File.ReadAllText(bootFile).Contains("qq-data"),"profile isolation adapter generated");
  Check(Directory.Exists(Path.Combine(runtime,"qq-data","nt_qq","global")),"native QQ profile initialized inside private directory");
  Console.WriteLine("PASS: official NapCat 4.18.38 starts hidden, local API login/status and real QQ QR generated without personal authorization token");
 }finally{
  var proc=(System.Diagnostics.Process?)helper.GetField("process",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(probe);int? pid=null;try{pid=proc?.Id;}catch{}
  ((IDisposable)probe).Dispose();if(pid.HasValue){bool running;try{using var check=System.Diagnostics.Process.GetProcessById(pid.Value);running=!check.HasExited;}catch(ArgumentException){running=false;}Check(!running,"owned process stopped");}
  Check(!File.Exists(Path.Combine(runtime,"work","config","webui.json")),"temporary WebUI password removed before releasing lease");
  Console.WriteLine("PASS: process ownership cleanup and private local API credential cleanup");
 }
}