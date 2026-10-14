const fs=require('fs'),vm=require('vm'),assert=require('assert'),path=require('path');
const output=process.env.YANJIU_TEST_OUTPUT;if(output)fs.mkdirSync(output,{recursive:true});
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const root=path.resolve(__dirname,'..','..');
let html=fs.readFileSync(root+'/PhoneApp/android/assets/index.html','utf8');
const pages=fs.readFileSync(root+'/PhoneApp/android/assets/pages.js','utf8'),css=fs.readFileSync(root+'/PhoneApp/android/assets/pages.css','utf8');new vm.Script(pages,{filename:'pages.js'});html=html.replace('<script src="pages.js"></script>','<script>'+pages+'</script>').replace('<link rel="stylesheet" href="pages.css">','<style>'+css+'</style>');
for(const [i,match] of [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].entries())new vm.Script(match[1],{filename:'phone-script-'+i});
console.log('PASS all embedded scripts parse');
(async()=>{
const browser=await chromium.launch({headless:true,executablePath:process.env.YANJIU_BROWSER||undefined});const page=await browser.newPage({viewport:{width:412,height:880},deviceScaleFactor:1});
const errors=[];page.on('pageerror',e=>errors.push(e.message));
const mock=`window.calls=[];window.fixture={state:'已连接',messages:[{id:101,channel:'小队',sender:'测试玩家@太阳海岸',text:'招募测试',time:new Date().toISOString(),color:'#a9d4ff',parts:[{text:'测试符号 \\uE03F',color:'#ff00aa'}],recruitment:{listingId:'12345',title:'随机任务：拾级迷宫'}}],channels:['小队','悄悄话','国服','新人'],tabs:[{id:'native:0',name:'聊天',recordSource:'native'}],channelInfo:[],worldJoined:true,noviceJoined:true};window.Phone=new Proxy({appVersion:()=> '0.3.40',symbolFont:()=> '',displayAssets:()=>{},loadAppearance:()=>'{"theme":"white"}',loadNavigation:()=> '{}',snapshot:()=>JSON.stringify(fixture),snapshotSelected:()=>JSON.stringify(fixture),recruitment:(...args)=>calls.push(args.slice(0,3))},{get:(o,k)=>o[k]||(()=> '')});`;
await page.setContent(html.replace('<head>','<head><script>'+mock+'</script>'));
await page.waitForTimeout(500);assert.deepEqual(errors,[]);
await page.evaluate(()=>{current='channel:小队';view='channels';render()});
assert.equal(await page.locator('.inlineRecruitText').count(),1);
assert.equal(await page.locator('.gameSymbol').count(),1);
assert.equal(await page.locator('.lineBody span').first().evaluate(n=>getComputedStyle(n).color),'rgb(255, 0, 170)');
console.log('PASS symbols preserved and exact color retained on light theme');
await page.locator('.inlineRecruitText').click();assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['open',101,'']);
const details={ok:true,open:true,viewId:'view-test',leader:'布雷尼尔',duty:'随机任务：拾级迷宫',comment:'770HQ全职业 十件套130w',location:'太阳海岸',timeLeft:'剩余47分',itemLevel:'无',status:'剩余3人',categories:'',tellTarget:'布雷尼尔@太阳海岸',slots:[{filled:true,job:19},{filled:false,job:0}],canJoin:true,canTell:true};
await page.evaluate(d=>recruitmentResult('open',true,d),details);
assert.equal(await page.locator('#recruitJoin').isEnabled(),true);
if(output)await page.screenshot({path:path.join(output,'phone-recruit-0.3.39.png')});
await page.locator('#recruitJoin').click();assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['join',101,'view-test']);
await page.evaluate(d=>recruitmentResult('join',true,{detail:{...d,confirmation:{prompt:'确认参加布雷尼尔的小队？'}}}),details);
await page.getByRole('button',{name:'确认参加',exact:true}).click();assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['confirm-yes',101,'view-test']);
console.log('PASS open, join and explicit game confirmation callbacks');
await page.evaluate(d=>recruitmentResult('confirm-yes',true,d),details);
await page.getByRole('button',{name:'返回',exact:true}).click();assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['close',101,'view-test']);assert.equal(await page.locator('#recruitDialog').evaluate(n=>n.open),true);
await page.evaluate(()=>recruitmentResult('close',true,{ok:true}));assert.equal(await page.locator('#recruitDialog').evaluate(n=>n.open),false);
console.log('PASS close waits for game acknowledgement');
await page.locator('.inlineRecruitText').click();await page.evaluate(d=>recruitmentResult('open',true,d),details);await page.locator('#recruitTell').click();await page.evaluate(d=>recruitmentResult('tell',true,{detail:d}),details);
assert.equal(await page.locator('#target').inputValue(),'布雷尼尔@太阳海岸');assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['close',101,'view-test']);
console.log('PASS tell target comes from actual recruitment data');
await page.evaluate(()=>recruitmentResult('close',true,{ok:true}));await page.locator('.inlineRecruitText').click();await page.evaluate(d=>recruitmentResult('open',true,d),details);await page.evaluate(()=>recruitmentResult('refresh',true,{open:false,viewId:'view-test'}));assert.equal(await page.locator('#recruitDialog').evaluate(n=>n.open),false);
assert.deepEqual(errors,[]);console.log('PASS game-side dismissal and no startup errors');
const maps=await page.evaluate(()=>{const c=document.createElement('canvas');c.width=c.height=1;const x=c.getContext('2d');x.fillStyle='red';x.fillRect(0,0,1,1);const a=c.toDataURL();x.fillStyle='blue';x.fillRect(0,0,1,1);return {'1':a,'2':c.toDataURL()}});const images=Object.values(maps).slice(0,2);
await page.evaluate(({maps,images})=>{gameBitmapIcons=maps;gameStatusIcons={'1':images[0],'2':images[1]};Phone.recruitBoard=(...args)=>calls.push(['board',...args]);Phone.boardListing=(...args)=>calls.push(['listing',...args]);fixture.messages[0].channel='国服';fixture.messages[0].worldAlias=9;fixture.worldStates=[{alias:'9',statusIcon:1}];snapshot=fixture;current='channel:国服';render()}, {maps,images});
assert.equal(await page.locator('.name').count(),1);const before=await page.locator('#feed .inlineGameIcon').first().getAttribute('src');
await page.evaluate(()=>{fixture.worldStates[0].statusIcon=2});await page.waitForTimeout(900);const afterIcon=await page.locator('#feed .inlineGameIcon').first().getAttribute('src');assert.notEqual(before,afterIcon);
console.log('PASS current world status updates icons in already displayed messages');
await page.evaluate(()=>{fixture.channels.push('新人');fixture.noviceJoined=true;fixture.messages[0].channel='新人';fixture.messages[0].senderIdentity='测试玩家@太阳海岸';fixture.noviceStates=[{target:'测试玩家@太阳海岸',icons:[1]}];snapshot=fixture;current='channel:新人';render()});assert.equal(await page.locator('.playerName .inlineGameIcon').count(),1);
await page.evaluate(()=>{fixture.noviceStates[0].icons=[2];snapshot=fixture;render()});assert.equal(await page.locator('.playerName .inlineGameIcon').getAttribute('src'),images[1]);
console.log('PASS novice identity icons update independently of new messages');
await page.evaluate(()=>openRecruitBoard());assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['board','open','',0]);
const board={ok:true,open:true,viewId:'board-test',area:0,page:'1-7',count:'7',canNext:true,canPrevious:false,categories:[{index:0,label:'7',selected:true,nodes:[{kind:'text',text:'全部',parts:[{text:'全部'}],color:'#ffffff',x:0,y:0}]}],rows:[{listingId:'12345',nodes:[{kind:'text',text:'随机任务：拾级迷宫',parts:[{text:'随机任务：拾级迷宫'}],color:'#88d0ee',x:0,y:0},{kind:'image',image:images[0],x:170,y:0},{kind:'text',text:'770HQ全职业 十件套130w',parts:[{text:'770HQ全职业 十件套130w'}],color:'#dddddd',x:0,y:20}]}]};
await page.evaluate(d=>boardResult('open',true,d),board);if(output)await page.screenshot({path:path.join(output,'phone-board-0.3.39.png')});
await page.locator('.boardRecruitRow').click();assert.deepEqual(await page.evaluate(()=>calls.slice(-2)),[['listing','board-test','12345'],['open',0,'']]);
await page.evaluate(d=>recruitmentResult('open',true,d),details);await page.evaluate(()=>closeRecruitment());await page.evaluate(()=>recruitmentResult('close',true,{ok:true}));assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['board','read','board-test',0]);
await page.evaluate(d=>boardResult('read',true,d),board);await page.locator('#boardNext').click();assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['board','next','board-test',0]);await page.evaluate(d=>boardResult('next',true,d),board);
await page.evaluate(()=>closeBoard());assert.equal(await page.locator('#boardDialog').evaluate(n=>n.open),false);await page.evaluate(()=>boardResult('close',true,{ok:true}));assert.equal(await page.locator('#boardDialog').evaluate(n=>n.open),false);
console.log('PASS recruitment board, game pagination and child detail close synchronization');
await page.evaluate(()=>{Phone.accounts=()=>JSON.stringify([{id:'saved-one',name:'测试昵称',worldAuthorized:true,alias:'12345',address:'https://test.invalid:18765',active:false}]);Phone.switchAccount=id=>calls.push(['account',id]);openAccounts()});await page.getByRole('button',{name:'连接设备',exact:true}).click();assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['account','saved-one']);assert.match(await page.locator('#accountsList').textContent(),/测试昵称.*12345/);
console.log('PASS account selector displays identity and selects saved authorization');

await page.evaluate(()=>{document.querySelectorAll('dialog[open]').forEach(d=>d.close());navigatePage('chat')});
assert.deepEqual(await page.locator('.top button').allTextContents(),['☰ 聊天消息','频道']);
assert.equal(await page.locator('#connectionActions').getByRole('button',{name:'连接设备',exact:true,includeHidden:true}).count(),1);
assert.equal(await page.locator('#connectionActions').getByRole('button',{name:/切换账号|重新连接|队员招募/,includeHidden:true}).count(),0);
console.log('PASS requested toolbar order and consolidated connection menu');
await page.evaluate(()=>{snapshot.messages=fixture.messages;current='channel:新人';fixture.messages[0].recruitment={kind:'board'};render()});
assert.equal(await page.locator('.recruitLink').count(),0);await page.locator('.inlineRecruitText').click();assert.equal(await page.locator('#boardDialog').evaluate(n=>n.open),true);await page.evaluate(()=>{boardResult('open',true,{ok:true,open:true,viewId:'board-test',rows:[],categories:[],canNext:false,canPrevious:false});closeBoard();boardResult('close',true,{ok:true})});
await page.evaluate(()=>{openRecruitment(101);recruitmentResult('open',false,{error:'招募已取消'});closeRecruitment()});assert.equal(await page.locator('#recruitDialog').evaluate(n=>n.open),false);
console.log('PASS inline recruitment text and closing a cancelled recruitment without getting stuck');
await page.evaluate(()=>{Phone.worldBoard=(...args)=>calls.push(['world',...args]);openRecruitBoard();boardResult('open',true,{ok:true,open:true,viewId:'board-test',rows:[],categories:[],canNext:false,canPrevious:false});selectBoardMode('world');boardResult('close',true,{ok:true})});assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['world','open','',0]);
const worldPost={postId:'11111111-1111-4111-8111-111111111111',title:'国服招募回归',content:'招募内容',leader:'队长',alias:'9',slots:[[19,24]],world:'测试世界'};
await page.evaluate(p=>worldBoardResult('open',true,{rows:[p],offset:0,hasMore:false,pageSize:10}),worldPost);await page.locator('#boardRows .boardRecruitRow').click();assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['world','select',worldPost.postId,0]);await page.evaluate(p=>worldBoardResult('select',true,{rows:[p],selected:p,offset:0}),worldPost);assert.equal(await page.locator('#worldPostDialog').evaluate(n=>n.open),true);await page.evaluate(()=>{closeWorldPost();closeBoard()});
console.log('PASS game/world recruitment board switching and shared post selection');
await page.evaluate(()=>{Phone.party=(...args)=>calls.push(['party',...args]);openParty()});
const roster={members:[{id:'1',name:'自己',self:true,leader:true,job:19,level:100,world:'世界',target:'自己@世界'},{id:'2',name:'队员',self:false,leader:false,job:24,level:99,world:'世界',target:'队员@世界'}],inParty:true,isLeader:true};
await page.evaluate(d=>partyResult('read',true,d),roster);await page.evaluate(()=>partyPerson('2'));assert.equal(await page.getByRole('button',{name:'请离队伍',exact:true}).count(),1);await page.getByRole('button',{name:'发送悄悄话',exact:true}).click();assert.equal(await page.locator('#target').inputValue(),'队员@世界');
await page.evaluate(d=>{openParty();partyResult('read',true,{...d,confirmation:'确认离开小队？'})},roster);await page.locator('#partyConfirmation').getByRole('button',{name:'确认',exact:true}).click();assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['party','confirm-yes','']);await page.evaluate(()=>closeParty());
console.log('PASS roster actions, tell target and explicit game confirmation');
await page.evaluate(()=>{fixture.worldAuthorized=false});await page.waitForTimeout(900);assert.equal(await page.locator('#membershipActions button').first().isDisabled(),true);
console.log('PASS optional world authorization does not require a world account for device connection');


await page.evaluate(()=>{document.querySelectorAll('dialog[open]').forEach(d=>d.close());Phone.requestPairNamed=(...args)=>calls.push(['pairNamed',...args]);Phone.accounts=()=>JSON.stringify([{id:'ordinary',label:'普通游戏账号',name:'',alias:'',address:'https://test.invalid:18765',active:true}]);openAccounts()});
assert.match(await page.locator('#accountsList').textContent(),/普通游戏账号/);assert.doesNotMatch(await page.locator('#accountsList').textContent(),/测试昵称|12345/);assert.match(await page.locator('#accountsList').textContent(),/未附加授权/);await page.getByRole('button',{name:'＋ 添加账号',exact:true}).click();await page.locator('#newAccountName').fill('主账号');await page.locator('#connectionHost').fill('192.168.1.10');await page.locator('#connectionPort').fill('18765');await page.getByRole('button',{name:'请求授权',exact:true}).click();assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['pairNamed','192.168.1.10',18765,'主账号']);
console.log('PASS named connection authorization works without a world-channel identity');


await page.evaluate(()=>{Phone.cancelPair();document.getElementById('manualPairDialog').close();navigatePage('chat')});
await page.locator('#pageMenuButton').click();assert.equal(await page.locator('#pageDrawer').getAttribute('aria-hidden'),'false');assert.deepEqual(await page.locator('#pageDrawer nav button').allTextContents(),['账号设置','聊天消息','招募频道','队伍信息','应用设置']);
await page.locator('#pageDrawer').getByRole('button',{name:'账号设置',exact:true}).click();assert.deepEqual(await page.evaluate(()=>appPageState()),{page:'accounts',drawerOpen:false});for(const id of ['accountsDialog','boardDialog','partyDialog','appearanceDialog'])assert.equal(await page.locator('#'+id).evaluate(n=>n.tagName),'SECTION');
assert.equal(await page.locator('#accountsList .accountCard').count(),1);for(const name of ['连接设备','断开连接','重载数据','加入国服','退出国服','加入新人','退出新人'])assert.equal(await page.locator('#accountsList').getByRole('button',{name,exact:true}).count(),1);
await page.evaluate(()=>navigatePage('settings'));assert.equal(await page.locator('#appearanceDialog').isVisible(),true);assert.equal(await page.locator('dialog[open]').count(),0);await page.evaluate(()=>handleAppBack());assert.equal(await page.evaluate(()=>appPageState().page),'chat');
console.log('PASS drawer page navigation, independent sections, account card controls and Android back');
await page.evaluate(()=>{navigatePage('recruit',{mode:'world'});worldBoardResult('open',false,{error:'该账号未附加国服授权'})});assert.match(await page.locator('#boardRows').textContent(),/未附加国服授权/);assert.doesNotMatch(await page.locator('#boardRows').textContent(),/当前没有国服招募/);await page.locator('#boardRows').getByRole('button',{name:'查看账号权限',exact:true}).click();assert.equal(await page.evaluate(()=>appPageState().page),'accounts');
console.log('PASS world permission/load failures are distinct from an empty recruitment list');


await page.evaluate(()=>{window.liveJoined=false;fixture.worldJoined=false;fixture.worldAuthorized=true;snapshot=fixture;Phone.accounts=()=>JSON.stringify([{id:'fast-status',label:'状态账号',worldAuthorized:true,address:'https://test.invalid:18765',active:true,paused:false}]);Phone.statusSnapshot=()=>JSON.stringify({state:'已连接',worldAuthorized:true,worldJoined:liveJoined,noviceJoined:false});Phone.send=(...args)=>calls.push(['send',...args]);navigatePage('accounts');accountAction('fast-status','worldJoin')});assert.deepEqual(await page.evaluate(()=>calls.at(-1)),['send','频道操作','','国服加入']);assert.match(await page.locator('#accountsList').textContent(),/正在加入国服/);await page.evaluate(()=>{liveJoined=true;sent(true,'请求已提交')});assert.match(await page.locator('#accountsList').textContent(),/国服 已加入/);assert.doesNotMatch(await page.locator('#accountsList').textContent(),/正在加入国服/);
console.log('PASS membership cards wait for actual status and quickly reflect confirmed join');

if(output){await page.evaluate(()=>{fixture.worldAuthorized=true;fixture.gameAccount={name:'测试角色',world:'测试世界',job:19,level:100,loggedIn:true};snapshot=fixture;Phone.accounts=()=>JSON.stringify([{id:'card-main',label:'主账号',gameName:'测试角色',world:'测试世界',name:'测试昵称',alias:'9',worldAuthorized:true,address:'https://test.invalid:18765',active:true,paused:false},{id:'card-alt',label:'备用账号',gameName:'测试角色二',world:'测试世界',address:'https://other.invalid:18765',active:false,paused:false}]);navigatePage('accounts');updatePref('theme','white');accountsChanged()});await page.screenshot({path:path.join(output,'accounts-light.png')});await page.evaluate(()=>{updatePref('theme','black');openPageDrawer()});await page.waitForTimeout(220);await page.screenshot({path:path.join(output,'navigation-dark.png')})}
assert.deepEqual(errors,[]);await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
