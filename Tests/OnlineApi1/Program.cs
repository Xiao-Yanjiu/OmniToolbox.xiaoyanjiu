using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Collections;
using System.Xml;
var root=Path.GetFullPath(args[0]);
if (args.Length != 3) throw new ArgumentException("Pass repository root, Omni directory and Dalamud directory");
var omni=Path.GetFullPath(args[1]);
var hooks=Path.GetFullPath(args[2]);
AssemblyLoadContext.Default.Resolving+=(_,name)=>
{
    foreach(var dir in new[]{omni,hooks})
    {
        var file=Path.Combine(dir,name.Name+".dll");
        if(File.Exists(file))return AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
    }
    return null;
};
var common=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(omni,"OmniToolbox.Common.dll"));
var contract=common.GetType("OmniToolbox.Common.Module.Runtime.ModuleContract",true)!;
var validate=contract.GetMethod("ValidateType",BindingFlags.Static|BindingFlags.NonPublic)!;
var moduleBase=common.GetType("OmniToolbox.Common.Module.Abstractions.OmniModuleBase",true)!;
var language=common.GetType("OmniToolbox.Config.UILanguage",true)!;
int count=0;
foreach(var name in new[]{"MusicPlayer","GameWebBrowser","AutoDiscardJunk","ToolbarIconPlus","IChingModule","CommandCatalog","PhoneChat"})
{
    var file=Path.Combine(root,".artifacts/api1",name,"bin/Release/net10.0-windows",name+".dll");
    var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
    var type=assembly.GetType("OmniToolbox.LocalModules."+name,true)!;
    validate.Invoke(null,new object[]{type});count++;
    if(assembly.GetTypes().Count(t=>!t.IsAbstract&&moduleBase.IsAssignableFrom(t))!=1)throw new Exception(name+" multiple modules");count++;
    var text=assembly.GetType("OmniToolbox.LocalModules."+name+"Text",true)!;
    var catalog=text.GetField("Catalog",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
    var catalogs=(IEnumerable)catalog.GetType().GetProperty("Catalogs")!.GetValue(catalog)!;
    int strings=0;
    foreach(var entry in catalogs)
    {
        var dict=(IEnumerable)entry.GetType().GetProperty("Value")!.GetValue(entry)!;
        foreach(var pair in dict)
        {
            var value=(string)pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            if(string.IsNullOrWhiteSpace(value))throw new Exception(name+" empty text");
            XmlConvert.VerifyXmlChars(value);strings++;count++;
        }
    }
    var complete=catalog.GetType().GetMethod("IsComplete")!;
    foreach(var lang in new[]{"SimplifiedChinese","TraditionalChinese"})
    {
        if(!(bool)complete.Invoke(catalog,new[]{Enum.Parse(language,lang)})!)throw new Exception(name+" incomplete "+lang);
        count++;
    }
    Console.WriteLine(name+": host contract passed; "+strings+" simplified/traditional entries valid");
}
Console.WriteLine(count+" runtime contract/localization checks passed; no module constructor or game API executed");
