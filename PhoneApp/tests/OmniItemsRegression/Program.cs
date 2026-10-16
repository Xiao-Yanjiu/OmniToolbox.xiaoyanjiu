using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
AssemblyLoadContext.Default.Resolving+=(c,n)=>{foreach(var path in args.Skip(1)){var f=Path.Combine(path,n.Name+".dll");if(File.Exists(f))return c.LoadFromAssemblyPath(f);}return null;};
var asm=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
var phone=asm.GetType("OmniToolbox.LocalModules.PhoneChat",true)!;
int checks=0;void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
var flags=BindingFlags.NonPublic|BindingFlags.Static;
var same=phone.GetMethod("SameItemListing",BindingFlags.Public|BindingFlags.Static)!;
bool Same(object a,object b)=>(bool)same.Invoke(null,[a,b])!;
var original=new Listing();var copy=new Listing();
Check(Same(original,copy),"exact matching listing identity and price are accepted");
foreach(var name in new[]{"ListingID","RetainerID","ItemID","UnitPrice","TotalTax","Quantity","ContainerIndex","TownID","MateriaCount"}){
 var changed=new Listing();var p=typeof(Listing).GetProperty(name)!;p.SetValue(changed,Convert.ChangeType(Convert.ToUInt64(p.GetValue(changed))+1,p.PropertyType));
 Check(!Same(original,changed),"changed "+name+" invalidates purchase review");
}
Check(!Same(original,new Listing{IsHQ=true}),"HQ change invalidates review");
Check(!Same(new Listing{Quantity=0},new Listing{Quantity=0}),"zero quantity cannot become purchasable");
Check(!Same(new object(),new object()),"missing public SDK fields cannot silently compare equal");
var call=phone.GetMethod("ItemCall",flags)!;var fake=new Service();
Check((string)call.Invoke(null,[fake,"Read",new object[]{7u}])! =="uint:7","public overload resolves by actual parameter type");
try{call.Invoke(null,[fake,"PrivateAction",Array.Empty<object>()]);throw new Exception("private method executed");}catch(TargetInvocationException e){Check(e.InnerException?.Message.Contains("公开操作")==true,"private implementation methods are never invoked");}
var tryGet=phone.GetMethod("ItemTryGet",flags)!;Check((string)tryGet.Invoke(null,[fake,"TryGet",7u])! =="item:7","public by-reference item model is returned");
var number=phone.GetMethod("ItemNumber",flags)!;using var root=JsonDocument.Parse("{\"page\":2,\"bad\":-1,\"fraction\":1.5}");
Check((int)number.Invoke(null,[root.RootElement,"page",0,0,100])! ==2,"bounded integer page is read");
foreach(var key in new[]{"bad","fraction"})try{number.Invoke(null,[root.RootElement,key,0,0,100]);throw new Exception("invalid accepted");}catch(TargetInvocationException){Check(true,"invalid numeric filter rejected: "+key);}
Check((int)number.Invoke(null,[root.RootElement,"missing",0,0,100])! ==0,"missing optional numeric filter uses default");
var readFactory=phone.GetMethod("CreatePhoneItemDataService",flags);
if(readFactory!=null){
 var owned=(ReadOnlyData)readFactory.Invoke(null,[typeof(ReadOnlyData),null])!;
 Check(owned.Map!=null&&owned.Pools.Count==0,"game-side read-only data service can be created without a host-registered instance");
 IReadOnlyDictionary<uint,IReadOnlyList<uint>> pools=new Dictionary<uint,IReadOnlyList<uint>>{{123,new uint[]{456}}};
 var indexed=(ReadOnlyData)readFactory.Invoke(null,[typeof(ReadOnlyData),pools])!;
 Check(ReferenceEquals(indexed.Pools,pools),"public source index is passed into the game-side data service unchanged");
 try{readFactory.Invoke(null,[typeof(Service),null]);throw new Exception("incompatible factory accepted");}catch(TargetInvocationException e){Check(e.InnerException?.Message.Contains("公开物品数据构造接口不兼容")==true,"incompatible public data constructor fails explicitly");}
 var raw=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(phone);var ownedServices=new Dictionary<string,object>();var disposable=new OwnedReadService();ownedServices.Add("owned",disposable);
 phone.GetField("phoneItemReadGate",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(raw,new object());
 phone.GetField("phoneItemReadServices",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(raw,ownedServices);
 var dispose=phone.GetMethod("DisposePhoneItemReadServices",BindingFlags.Instance|BindingFlags.NonPublic)!;dispose.Invoke(raw,null);dispose.Invoke(raw,null);
 Check(disposable.Disposals==1&&ownedServices.Count==0,"module-owned read-only services dispose once and clear on disable");
}
var pricesFactory=phone.GetMethod("CreatePhoneItemPriceService",flags);
if(pricesFactory!=null){
 var prices=(ReadOnlyPrices)pricesFactory.Invoke(null,[typeof(ReadOnlyPrices)])!;
 Check(prices.OfficeBaseURL=="https://office.youshuomni.com","price service receives the fixed official HTTPS endpoint, not a cache path or caller URL");
 try{pricesFactory.Invoke(null,[typeof(Service)]);throw new Exception("unsupported constructor accepted");}catch(TargetInvocationException e){Check(e.InnerException?.Message.Contains("价格查询构造接口不兼容")==true,"missing public pricing constructor produces an explicit compatibility error");}
}
var hostResolver=phone.GetMethod("PhoneItemHostType",flags);
if(hostResolver!=null){
 var isolated=new AssemblyLoadContext("OmniItemFakeHost",isCollectible:true);
 var fixtureAssembly=isolated.LoadFromAssemblyPath(typeof(FakeOmniHost.Manager).Assembly.Location);
 var manager=Activator.CreateInstance(fixtureAssembly.GetType("FakeOmniHost.Manager")!)!;
 var resolved=(Type)hostResolver.Invoke(null,[manager,"FakeOmniHost.Service"])!;
 Check(resolved.Assembly==fixtureAssembly&&resolved!=typeof(FakeOmniHost.Service),"service contract resolves from host load context instead of a same-named type in another context");
 var getService=phone.GetMethod("PhoneItemHostService",flags)!;
 Check(getService.Invoke(null,[manager,typeof(FakeOmniHost.Service)])==null,"same name in a different load context cannot be treated as an available host service");
 Check(getService.Invoke(null,[manager,resolved])?.GetType()==resolved,"host-identity service contract retrieves the existing host instance");
 isolated.Unload();
}
var levelsMethod=phone.GetMethod("PhoneItemSearchLevels",flags);
if(levelsMethod!=null){
 using var zeroLevels=JsonDocument.Parse("{\"levelMin\":0,\"levelMax\":0}");var levels=((int,int))levelsMethod.Invoke(null,[zeroLevels.RootElement,1,999])!;
 Check(levels==(1,999),"blank phone level fields use Omni's real defaults instead of filtering out normal items at maximum zero");
 using var range=JsonDocument.Parse("{\"levelMin\":100,\"levelMax\":750}");Check(((int,int))levelsMethod.Invoke(null,[range.RootElement,1,999])! ==(100,750),"explicit level range is preserved");
 using var inverse=JsonDocument.Parse("{\"levelMin\":100,\"levelMax\":50}");try{levelsMethod.Invoke(null,[inverse.RootElement,1,999]);throw new Exception("invalid range accepted");}catch(TargetInvocationException){Check(true,"inverted range is rejected explicitly");}
 var live=new FakeOmniHost.Service();var registeredManager=new FakeOmniHost.RegistryManager(live);
 var discover=phone.GetMethod("PhoneItemExistingService",flags)!;
 Check(ReferenceEquals(discover.Invoke(null,[registeredManager,new[]{"FakeOmniHost.Service"}]),live),"compatibility registry lookup returns the exact existing game service, not a new independent instance");
 var moduleManager=new FakeOmniHost.ModuleManager(new FakeOmniHost.ExistingModule(live));
 Check(ReferenceEquals(discover.Invoke(null,[moduleManager,new[]{"FakeOmniHost.Service"}]),live),"typed module dependency discovery reuses the game module's existing service");
 Check(ReferenceEquals(discover.Invoke(null,[new FakeOmniHost.ModuleManager(live),new[]{"FakeOmniHost.Service"}]),live),"public module itself can provide the requested existing preview service");
 var explicitService=new FakeOmniHost.ExplicitService();
 Check((string)call.Invoke(null,[explicitService,"Read",new object[]{7u}])! =="explicit:7","registered service can be called through its public interface even when implemented explicitly");
}

var collectionFilterFactory=phone.GetMethod("PhoneCollectionFilter",flags)!;
var tree=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(args[1],"OmniToolbox.Tree.dll"));
var filterType=tree.GetType("OmniToolbox.Collections.CollectionFilter",true)!;
var filterValues=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
foreach(var property in filterType.GetProperties(BindingFlags.Public|BindingFlags.Instance)){
 var t=property.PropertyType;
 object value;
 if(t==typeof(string))value="";
 else if(t.IsGenericType&&t.GetGenericTypeDefinition()==typeof(IReadOnlySet<>))value=Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(t.GetGenericArguments()))!;
 else if(t.Name=="ItemSubcategory")value=Activator.CreateInstance(t,"全部",Enum.ToObject(filterType.GetProperty("EquipmentCategory")!.PropertyType,0),0u,0u)!;
 else value=Activator.CreateInstance(t)!;
 filterValues[property.Name]=value;
}
filterValues["SelectedType"]=Enum.Parse(filterType.GetProperty("SelectedType")!.PropertyType,"Mount");
filterValues["ItemLevelMin"]=1;filterValues["ItemLevelMax"]=999;filterValues["Favorites"]=new HashSet<ulong>{18446744073709551600UL};
var collectionFilter=collectionFilterFactory.Invoke(null,[filterType,filterValues])!;
Check(filterType.IsInstanceOfType(collectionFilter),"real installed Omni CollectionFilter is constructed via its public constructor");
Check(filterType.GetProperty("SelectedType")!.GetValue(collectionFilter)!.ToString()=="Mount","named catalog type is preserved without guessed enum ordinals");
Check((int)filterType.GetProperty("ItemLevelMax")!.GetValue(collectionFilter)! ==999,"collection level maximum preserves normal items");
Check(((IReadOnlySet<ulong>)filterType.GetProperty("Favorites")!.GetValue(collectionFilter)!).Contains(18446744073709551600UL),"64-bit collection favorite keys remain exact on the game side");
var collectionMatch=phone.GetMethod("PhoneCollectionMatchesUnlock",flags)!;
var collectionEntry=new CollectionState{ID=42,Key=18446744073709551600UL,IsUnlockStateKnown=false,IsUnlocked=false};
bool Match(int value)=>(bool)collectionMatch.Invoke(null,[collectionEntry,value])!;
Check(Match(0)&&!Match(1)&&!Match(2)&&Match(3),"unknown unlock state is never incorrectly presented as uncollected");
collectionEntry.IsUnlockStateKnown=true;
Check(!Match(1)&&Match(2)&&!Match(3),"confirmed missing state is separate from unknown state");
collectionEntry.IsUnlocked=true;
Check(Match(1)&&!Match(2)&&!Match(3),"confirmed collected state is separate from missing and unknown");
var collectionDto=phone.GetMethod("PhoneCollectionRow",flags)!.Invoke(null,[collectionEntry]);
using(var dto=JsonDocument.Parse(JsonSerializer.Serialize(collectionDto))){
 Check(dto.RootElement.GetProperty("key").GetString()=="18446744073709551600","collection DTO sends 64-bit identity as a string to JavaScript");
 Check(dto.RootElement.GetProperty("known").GetBoolean()&&dto.RootElement.GetProperty("unlocked").GetBoolean(),"collection DTO retains independent known and unlocked flags");
}


var displayWorld=phone.GetMethod("PhoneDisplayWorld",flags)!;
bool World(string name,string current="水晶塔",string group="陆行鸟")=>(bool)displayWorld.Invoke(null,[name,current,group])!;
Check(!World("陆行鸟s-bella")&&!World("猫小胖s-haematina")&&!World("陆行鸟"),"CN staging names and data-center placeholder worlds are excluded");
Check(World("拉诺西亚")&&World("水晶塔")&&World("Cactuar","Cactuar","Aether")&&World("Adamantoise","Cactuar","Aether"),"real CN worlds and international English world names are preserved");
var mapPin=phone.GetMethod("PhoneMapPin",flags)!;
var pin=((double,double))mapPin.Invoke(null,[21.5f,21.5f,100u])!;
Check(Math.Abs(pin.Item1-.5)<.0001&&Math.Abs(pin.Item2-.5)<.0001,"game map coordinates at size factor 100 place the marker at the texture center");
pin=((double,double))mapPin.Invoke(null,[11.25f,11.25f,200u])!;
Check(Math.Abs(pin.Item1-.5)<.0001,"map size factor 200 uses the correct marker scale");
var shopKey=phone.GetMethod("PhoneShopKey",flags)!;
var buyable=phone.GetMethod("PhoneNPCOfferBuyable",flags)!;
var offer=new ShopOffer();var changedOffer=new ShopOffer{CostInfos=new[]{new ShopCost{Cost=101}}};
Check(shopKey.Invoke(null,[offer])!.ToString()!=shopKey.Invoke(null,[changedOffer])!.ToString(),"changed NPC cost invalidates the reviewed shop fingerprint");
Check((bool)buyable.Invoke(null,[new object[]{offer},offer])!,"unambiguous gil shop offer can be reviewed");
Check(!(bool)buyable.Invoke(null,[new object[]{offer,changedOffer},offer])!,"same vendor with ambiguous shop costs cannot dispatch a different purchase");
var exchange=new ShopOffer{CostInfos=new[]{new ShopCost{ItemID=28}}};
Check(!(bool)buyable.Invoke(null,[new object[]{exchange},exchange])!,"unsupported special exchange is shown read-only instead of pretending gil purchase support");

Console.WriteLine(checks+" public adapter and purchase-review checks; no real query, purchase, retrieval or game input executed.");
public class Listing{
 public ulong ListingID{get;set;}=18446744073709551600UL;public ulong RetainerID{get;set;}=123;public uint ItemID{get;set;}=46254;public uint UnitPrice{get;set;}=4555;public uint TotalTax{get;set;}=683;public uint Quantity{get;set;}=3;public ushort ContainerIndex{get;set;}=1;public byte TownID{get;set;}=1;public byte MateriaCount{get;set;}=0;public bool IsHQ{get;set;}
}
public class Service{public string Read(uint id)=>"uint:"+id;public string Read(string id)=>"string:"+id;private void PrivateAction()=>throw new Exception("must not invoke");public bool TryGet(uint id,out string item){item="item:"+id;return true;}}


public class ItemPatchMap {}
public class ReadOnlyData{public ItemPatchMap Map;public IReadOnlyDictionary<uint,IReadOnlyList<uint>> Pools;public ReadOnlyData(ItemPatchMap map,IReadOnlyDictionary<uint,IReadOnlyList<uint>> pools){Map=map;Pools=pools;}}
public class OwnedReadService:IDisposable{public int Disposals;public void Dispose()=>Disposals++;}

public class ReadOnlyPrices{public string OfficeBaseURL;public ReadOnlyPrices(string officeBaseURL){OfficeBaseURL=officeBaseURL;}}

namespace FakeOmniHost{public class Service{} public class Manager{private readonly Service service=new();public T? GetService<T>() where T:class=>service as T;}}

namespace FakeOmniHost{
 public class RegistryManager{private readonly IReadOnlyList<object> services;public RegistryManager(object service){services=new[]{service};}public T? GetService<T>() where T:class=>null;}
 public class ExistingModule{private readonly Service service;public ExistingModule(Service value){service=value;}}
 public class ModuleManager{public IReadOnlyList<object> Modules{get;}public ModuleManager(object module){Modules=new[]{module};}}
 public interface IReadService{string Read(uint id);}
 public class ExplicitService:IReadService{string IReadService.Read(uint id)=>"explicit:"+id;}
}

public class CollectionState{public uint ID{get;set;}public ulong Key{get;set;}public bool IsUnlockStateKnown{get;set;}public bool IsUnlocked{get;set;}}

public class ShopOffer{public uint ID{get;set;}=100;public string Name{get;set;}="商人";public string ShopName{get;set;}="商店";public ShopCost[] CostInfos{get;set;}=new[]{new ShopCost()};}
public class ShopCost{public uint ItemID{get;set;}=1;public uint Cost{get;set;}=100;public uint? Collectablity{get;set;}}
