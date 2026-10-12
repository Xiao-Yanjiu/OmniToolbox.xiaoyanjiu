// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Yanjiu.Modules;

if(args.Length!=3)throw new ArgumentException("Pass module source, Yanjiu library folder and Dalamud API folder");
AssemblyLoadContext.Default.Resolving+=(context,name)=>{
 foreach(var folder in args.Skip(1)){var file=Path.Combine(folder,name.Name+".dll");if(File.Exists(file))return context.LoadFromAssemblyPath(file);}return null;
};
var compiler=new SourceCompiler();
using var compiled=compiler.Compile(File.ReadAllBytes(args[0]),args[0]);
var type=compiled.ModuleType;
void Check(bool condition,string label){if(!condition)throw new Exception(label);}
var validate=type.GetMethod("TryQqSignAddress",BindingFlags.NonPublic|BindingFlags.Static)!;
foreach(var fixture in new[]{("",false),("http://remote.example/sign",false),("https://user:pass@example.com/sign",false),("https://example.com/sign",true),("http://127.0.0.1:8080/sign",true)}){
 object?[] input=[fixture.Item1,null,null];Check((bool)validate.Invoke(null,input)! ==fixture.Item2,"sign address validation");if(!fixture.Item2)Check(!string.IsNullOrEmpty((string?)input[2]),"invalid input feedback");
}
var instance=RuntimeHelpers.GetUninitializedObject(type);
var connect=type.GetMethod("ConnectQQ",BindingFlags.Instance|BindingFlags.NonPublic)!;
var status=type.GetField("qqStatus",BindingFlags.Instance|BindingFlags.NonPublic)!;
connect.Invoke(instance,[true]);Check(((string)status.GetValue(instance)!).Contains("启用"),"disabled module feedback");
type.GetField("enabled",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(instance,true);
var settings=type.GetField("settings",BindingFlags.Instance|BindingFlags.NonPublic)!;
settings.SetValue(instance,Activator.CreateInstance(settings.FieldType,true));
connect.Invoke(instance,[true]);Check(((string)status.GetValue(instance)!).Contains("签名"),"empty sign address feedback");
Check((int)type.GetField("qqLoginRunning",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(instance)! ==0,"invalid input does not start login");
var error=(string)type.GetMethod("QQLoginError",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[new Exception("failed https://example.com/sign?token=test-secret"),"https://example.com/sign?token=test-secret"])!;
Check(!error.Contains("test-secret")&&error.Contains("登录失败"),"credential redaction");
Console.WriteLine("PASS: QQ scan source compiled, validation and immediate feedback verified; no real account login attempted");
