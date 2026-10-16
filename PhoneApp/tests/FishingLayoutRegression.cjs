const fs=require('fs');const {chromium}=require(process.env.PLAYWRIGHT_MODULE);
(async()=>{const browser=await chromium.launch({executablePath:process.env.YANJIU_BROWSER,headless:true});try{
const page=await browser.newPage({viewport:{width:393,height:800}});
await page.goto('https://fish.ffmomola.com/#/fishing',{waitUntil:'domcontentloaded',timeout:90000});
await page.addScriptTag({content:fs.readFileSync('PhoneApp/android/assets/fishing-layout.js','utf8')});
await page.waitForSelector('table[data-ff14-compact] tbody tr',{timeout:30000});
for(const width of [393,320]){await page.setViewportSize({width,height:800});await page.waitForTimeout(400);
const bounds=await page.locator('table[data-ff14-compact]').first().evaluate(t=>({left:t.getBoundingClientRect().left,right:t.getBoundingClientRect().right,cells:Array.from(t.tHead.rows[0].cells).filter(c=>getComputedStyle(c).display!=='none').map(c=>({text:c.textContent,right:c.getBoundingClientRect().right}))}));
if(bounds.left<0||bounds.right>width+1||bounds.cells.some(c=>c.right>width+1))throw Error(JSON.stringify({width,bounds}));console.log('PASS table fits '+width+'px '+JSON.stringify(bounds));
}
await page.keyboard.press('Escape');await page.waitForTimeout(300);const alert=page.locator('[role=alert][data-slot=alert]').first();if(await alert.count()){await alert.dispatchEvent('click');if(!await alert.evaluate(e=>e.hasAttribute('data-ff14-expanded')))throw Error('Alert expand failed');console.log('PASS announcement expands');}
await page.screenshot({path:'D:/Yanjiu.Dalamud/tests/fishing-layout-073.png',fullPage:false});
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exit(1)});
