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
var helper=compiled.Assembly.GetType("OmniToolbox.LocalModules.PhoneLLBot",true)!;
var token=helper.GetMethod("ValidToken")!;
foreach(var fixture in new[]{("",false),("valid-test",true),("secret\n",false),(new string('x',4097),false)})Check((bool)token.Invoke(null,[fixture.Item1])! ==fixture.Item2,"token validation");
var account=helper.GetMethod("ValidAccount")!;
foreach(var fixture in new[]{("",true),("123456",true),("123;cmd",false),("abc",false)})Check((bool)account.Invoke(null,[fixture.Item1])! ==fixture.Item2,"bot account validation");
var path=helper.GetMethod("SafeArchivePath")!;
foreach(var fixture in new[]{("bin/llbot/node.exe",true),("../escape",false),("C:/escape",false),("/escape",false),("bin/../../escape",false)})Check((bool)path.Invoke(null,[fixture.Item1])! ==fixture.Item2,"archive traversal validation");
var instance=RuntimeHelpers.GetUninitializedObject(type);
var connect=type.GetMethod("ConnectQQ",BindingFlags.Instance|BindingFlags.NonPublic)!;
var status=type.GetField("qqStatus",BindingFlags.Instance|BindingFlags.NonPublic)!;
connect.Invoke(instance,[true]);Check(((string)status.GetValue(instance)!).Contains("启用"),"disabled module feedback");
type.GetField("enabled",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(instance,true);
var settings=type.GetField("settings",BindingFlags.Instance|BindingFlags.NonPublic)!;
settings.SetValue(instance,Activator.CreateInstance(settings.FieldType,true));
connect.Invoke(instance,[true]);Check(((string)status.GetValue(instance)!).Contains("授权"),"empty auth feedback");
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
Console.WriteLine("PASS: module compiled, LLBot inputs, immediate feedback, QR validation, event parsing and archive safety");
// Local mock validates actual send headers, message segments and business error handling.
var client=Activator.CreateInstance(helper,BindingFlags.Instance|BindingFlags.Public,null,[Path.Combine(Path.GetTempPath(),"llbot-unused"),(Action<string>)(_=>{}),(Action<byte[]>)(_=>{}),(Action<uint,uint,string,string>)((_,_,_,_)=>{}),(Action<string>)(_=>{})],null)!;
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
Console.WriteLine("PASS: LLBot HTTP authorization, literal text and mention segments, success/failure checks");

if(args.Length==4){
 string runtime=args[3];
 await (Task)helper.GetMethod("Prepare")!.Invoke(null,[runtime,(Action<string>)(value=>Console.WriteLine(value)),CancellationToken.None])!;
 string home=Path.Combine(runtime,"runtime-8.3.2","bin","llbot"),data=Path.Combine(home,"data");Directory.CreateDirectory(data);
 var reserve=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);reserve.Start();int webPort=((System.Net.IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
 string secret=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
 File.WriteAllText(Path.Combine(data,"webui_token.txt"),secret);
 File.WriteAllText(Path.Combine(home,"default_config.json"),System.Text.Json.JsonSerializer.Serialize(new{webui=new{enable=true,host="127.0.0.1",port=webPort},ob11=new{enable=false},milky=new{enable=false},satori=new{enable=false},log=false,onlyLocalhost=true}));
 var info=new System.Diagnostics.ProcessStartInfo(Path.Combine(home,"node.exe")){WorkingDirectory=home,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(Path.Combine(home,"llbot.js"));info.Environment.Remove("AUTH_TOKEN");info.Environment["TMP"]=Path.Combine(runtime,"temp");info.Environment["TEMP"]=info.Environment["TMP"];Directory.CreateDirectory(info.Environment["TMP"]!);
 using var proc=new System.Diagnostics.Process{StartInfo=info};proc.OutputDataReceived+=(_,_)=>{};proc.ErrorDataReceived+=(_,_)=>{};
 var probe=Activator.CreateInstance(helper,BindingFlags.Instance|BindingFlags.Public,null,[runtime,(Action<string>)(_=>{}),(Action<byte[]>)(_=>{}),(Action<uint,uint,string,string>)((_,_,_,_)=>{}),(Action<string>)(_=>{})],null)!;
 helper.GetField("webPort",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(probe,webPort);helper.GetField("webToken",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(probe,secret);
 try{
  Check(proc.Start(),"real LLBot starts");proc.BeginOutputReadLine();proc.BeginErrorReadLine();
  bool ready=false;for(int retry=0;retry<60;retry++){if(proc.HasExited)throw new Exception("LLBot exited before WebUI ready: "+proc.ExitCode);try{var json=await (Task<System.Text.Json.JsonElement>)helper.GetMethod("Web",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(probe,["/api/login-info",null,CancellationToken.None])!;Check(!json.GetProperty("online").GetBoolean(),"no token cannot fake QQ online");ready=true;break;}catch(System.Net.Http.HttpRequestException){await Task.Delay(500);}}
  Check(ready,"real LLBot web interface ready");
  var state=await (Task<System.Text.Json.JsonElement>)helper.GetMethod("Web",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(probe,["/api/auth-token/status",null,CancellationToken.None])!;
  Check(!state.GetProperty("hasToken").GetBoolean(),"auth status reports absent token");
  Check(state.GetProperty("validation").GetString()=="idle","waiting for personal auth, no account logged in");
  Console.WriteLine("PASS: pinned official LLBot 8.3.2 started hidden in direct mode; authenticated login/status API verified with no real QQ login");
 }finally{int pid=proc.Id;helper.GetField("process",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(probe,proc);((IDisposable)probe).Dispose();bool running;try{using var check=System.Diagnostics.Process.GetProcessById(pid);running=!check.HasExited;}catch(ArgumentException){running=false;}Check(!running,"Dispose terminates only the owned LLBot process");Console.WriteLine("PASS: backend disposal terminates owned process and clears temporary API credentials");}
}