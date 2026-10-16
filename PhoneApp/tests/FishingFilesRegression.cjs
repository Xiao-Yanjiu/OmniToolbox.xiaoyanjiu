const fs=require('fs'),assert=require('assert');const {chromium}=require(process.env.PLAYWRIGHT_MODULE);
(async()=>{const b=await chromium.launch({executablePath:process.env.YANJIU_BROWSER,headless:true});try{
const p=await b.newPage();p.on('dialog',d=>d.accept());
await p.goto('https://fish.ffmomola.com/#/settings',{waitUntil:'networkidle'});
const known=p.getByRole('button',{name:'知道了',exact:true});if(await known.count())await known.click();
await p.evaluate(fs.readFileSync('PhoneApp/android/assets/fishing-bridge.js','utf8').replace('__NATIVE_PERMISSION__','granted'));
await p.evaluate(()=>{window.packets=[];const c=new MessageChannel();c.port1.onmessage=e=>packets.push(JSON.parse(e.data));c.port1.start();window.postMessage('ff14-fishing-notifications',location.origin,[c.port2]);});
await p.waitForFunction(()=>packets.some(x=>x.action==='sync'));
const selected=await p.evaluate(async()=>{const ns=await import([...document.scripts].find(s=>s.type==='module'&&s.src.includes('/assets/index-')).src);const st=Object.values(ns).find(v=>v?.getState&&v?.subscribe),repo=Object.values(ns).find(v=>v?.getSpotFishList&&v?.getFishWindows);const id=repo.getSpotFishList().find(f=>repo.getFishCountDown(f).type==='Waiting').id;st.dispatch({type:'fishing/setFishAlarm',payload:id});return id;});
await p.waitForTimeout(500);
await p.getByRole('button',{name:'导出',exact:true}).click();
await p.waitForFunction(()=>packets.some(x=>x.action==='exportFile'),{timeout:10000});
const output=await p.evaluate(()=>packets.find(x=>x.action==='exportFile'));const bytes=Buffer.from(output.data,'base64');assert(bytes.length>10);assert(JSON.parse(bytes.toString('utf8')).alarmFish.includes(selected));console.log('PASS actual settings export intercepted: '+output.name+' '+bytes.length+' bytes');
await p.evaluate(async id=>{const ns=await import([...document.scripts].find(s=>s.type==='module'&&s.src.includes('/assets/index-')).src);Object.values(ns).find(v=>v?.getState&&v?.subscribe).dispatch({type:'fishing/setFishUnAlarm',payload:id})},selected);
const chooser= p.waitForEvent('filechooser');await p.getByRole('button',{name:'导入',exact:true}).click();await (await chooser).setFiles({name:output.name,mimeType:'application/json',buffer:bytes});
await p.waitForLoadState('domcontentloaded');await p.waitForFunction(()=>document.body.innerText.includes('鱼糕'));await p.waitForFunction(async id=>{const url=[...document.scripts].find(s=>s.type==='module'&&s.src.includes('/assets/index-'))?.src;if(!url)return false;const ns=await import(url);return Object.values(ns).find(v=>v?.getState&&v?.subscribe)?.getState().fishing?.alarmFish?.includes(id)},selected);
console.log('PASS import reload restores actual selected fish alarm');
await p.screenshot({path:'D:/Yanjiu.Dalamud/tests/fishing-files-075.png'});
}finally{await b.close()}})().catch(e=>{console.error(e);process.exit(1)});
