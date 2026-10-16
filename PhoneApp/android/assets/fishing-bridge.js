(()=>{
if(window.__ff14FishBridge)return;window.__ff14FishBridge=true;
let permission='__NATIVE_PERMISSION__',permissionWait=null;
let port=null,store=null,repo=null,timer=0,last='',planned=false;
const send=d=>port?.postMessage(JSON.stringify(d));
// Android WebView does not download blob URLs itself. Keep original bytes and ask the system where to save.
const blobs=new Map(),create=URL.createObjectURL.bind(URL),revoke=URL.revokeObjectURL.bind(URL);
URL.createObjectURL=b=>{const url=create(b);if(b instanceof Blob)blobs.set(url,b);return url};
URL.revokeObjectURL=url=>{blobs.delete(url);revoke(url)};
function exportAnchor(a){const blob=blobs.get(a.href);if(!port||!blob||!a.hasAttribute('download'))return false;
 if(blob.size>2*1024*1024){alert('导出文件超过2MB');return true}
 blob.arrayBuffer().then(buffer=>{const bytes=new Uint8Array(buffer);let data='';for(let i=0;i<bytes.length;i+=0x8000)data+=String.fromCharCode(...bytes.subarray(i,i+0x8000));send({action:'exportFile',name:a.download||'鱼糕设置.json',data:btoa(data)})}).catch(()=>alert('读取导出文件失败，请重试'));return true;
}
const anchorClick=HTMLAnchorElement.prototype.click;
HTMLAnchorElement.prototype.click=function(){if(!exportAnchor(this))return anchorClick.call(this)};
document.addEventListener('click',e=>{const a=e.target.closest?.('a[download]');if(a&&exportAnchor(a)){e.preventDefault();e.stopImmediatePropagation()}},true);

// The explicit website test must not depend on alarm selection, pause state or browser Notification handlers.
document.addEventListener('click',e=>{const button=e.target.closest?.('button');if(!button||! /^(测试通知|Test notification|Test Notification|通知テスト)$/.test(button.textContent.trim()))return;e.preventDefault();e.stopImmediatePropagation();if(port)send({action:'test'});else alert('手机提醒尚未连接，请刷新鱼糕后重试');},true);
const name=v=>typeof v==='string'?v:v?.chs||v?.['zh-CN']||v?.en||'鱼类';
function Notification(title,o={}){this.close=()=>{};if(!planned||/测试|test/i.test(title))send({action:'notify',title:String(title).slice(0,120),body:String(o.body||'').slice(0,500)})}
Object.defineProperty(Notification,'permission',{get:()=>permission});Notification.requestPermission=()=>new Promise(resolve=>{permissionWait=resolve;send({action:'permission'})});window.Notification=Notification;
function sync(force=false){try{
 const st=store.getState();if(st._persist&&!st._persist.rehydrated)return;
 let ids=st.fishing?.alarmFish||[];const profile=JSON.parse(localStorage.getItem('userData')||'null');
 if(profile?.toBeNotified&&!profile.syncedKey?.includes('toBeNotified'))ids=profile.toBeNotified.flatMap(id=>repo.getSpotFishesByFishId(id).map(f=>f.id));
 const before=Number(st.settings?.fishAlarmBefore??2),signature=JSON.stringify([ids,before,st.settings?.gameDataLocale]);
 if(!force&&signature===last)return;if(!Array.isArray(ids)||ids.length>300||!Number.isFinite(before)||before<0||before>1440)throw Error();
 const now=Date.now(),rows=[];
 for(const id of ids){const fish=repo.getSpotFish(id);if(!fish||fish.isEmpty)continue;
 const title=name(repo.getFish(fish.itemId)?.name),spot=name(fish.spot?.name);
 for(const w of repo.getFishWindows(fish,{windowCnt:32,from:now})){const start=Number(w[0]),end=Number(w[1]);if(!Number.isFinite(start)||!Number.isFinite(end)||end<=start||start<=now||start>now+30*86400000)continue;
 const body=title+' · '+spot+' · '+new Date(start).toLocaleString('zh-CN')+' 开窗';
 if(before>0&&start-before*60000>now)rows.push({key:id+':'+start+':before',at:start-before*60000,expires:end,title:title+' 即将可钓',body:'提前 '+before+' 分钟提醒：'+body});
 rows.push({key:id+':'+start+':start',at:start,expires:end,title:title+' 可以钓了',body});}}
 rows.sort((a,b)=>a.at-b.at);send({action:'sync',rows:rows.slice(0,1000)});last=signature;planned=ids.length>0;
}catch(e){send({action:'error'})}}
window.addEventListener('message',async event=>{
 if(event.source&&event.source!==window||event.data!=='ff14-fishing-notifications'||!event.ports?.[0])return;
 port=event.ports[0];port.onmessage=e=>{try{const d=JSON.parse(e.data);if(d.action==='permissionResult'){permission=d.permission;permissionWait?.(permission);permissionWait=null}}catch{}};port.start();if(store&&repo){sync(true);return}try{
 const url=[...document.scripts].find(s=>s.type==='module'&&s.src.includes('/assets/index-'))?.src;
 const ns=await import(url);store=Object.values(ns).find(v=>v?.getState&&v?.subscribe);repo=Object.values(ns).find(v=>v?.getSpotFishList&&v?.getFishWindows);
 if(!store||!repo)throw Error();store.subscribe(()=>{if(timer)return;timer=setTimeout(()=>{timer=0;sync()},300)});sync();setInterval(()=>sync(true),60000);
 }catch(e){send({action:'error'})}
});
})();
