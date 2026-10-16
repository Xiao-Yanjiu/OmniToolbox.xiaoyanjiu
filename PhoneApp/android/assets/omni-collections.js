/* Collection catalog data is received exclusively through the paired game server. */
(()=>{
const page=$('omniCollectionsPage');
const types=[['Relic','特殊武器'],['Equipment','装备'],['Mount','坐骑'],['Minion','宠物'],['Emote','表情'],['Hairstyle','发型'],['TripleTriadCard','幻卡'],['Barding','陆行鸟装甲'],['Orchestrion','乐谱'],['Portrait','肖像教材'],['FashionAccessory','时尚配饰'],['Glasses','眼镜'],['BlueMageSpell','青魔法书'],['Bestiary','魔兽图鉴'],['BlindBox','盲盒']];
const equipment=[[0,'全部'],[1,'职业'],[2,'武器'],[3,'工具'],[4,'防具'],[5,'饰品'],[9,'套装']];
const sourceNames={Achievement:'成就',Quest:'任务',Shop:'商店',NPCShop:'商店',SpecialShop:'特殊商店',Duty:'副本',Crafting:'制作',Gathering:'采集',Fishing:'钓鱼',Fate:'危命任务',TreasureHunt:'寻宝',Market:'市场板',Event:'活动',Other:'其他'};
page.innerHTML='<nav id="collectionTypes" aria-label="收录分类"></nav><div id="collectionEquipment"><nav id="collectionEquipmentCategories" aria-label="装备分类"></nav><select id="collectionSubcategory" aria-label="装备子分类"><option value="0">全部</option></select></div><form id="collectionSearchForm"><input id="collectionQuery" maxlength="100" placeholder="搜索物品名称或 ID…" aria-label="搜索收录名称或ID"><button>搜索</button><button type="button" id="collectionFilterToggle" aria-expanded="false">筛选</button><button type="button" id="collectionRefresh">刷新</button></form><div id="collectionFilters" hidden><label>收录<select id="collectionUnlock"><option value="0">全部</option><option value="1">已收录</option><option value="2">未收录</option><option value="3">状态未知</option></select></label><label>交易<select id="collectionMarket"><option value="0">全部</option><option value="1">可交易</option><option value="2">不可交易</option><option value="3">可购买</option></select></label><label><input id="collectionFavoritesOnly" type="checkbox">仅收藏</label><label><input id="collectionAllTypes" type="checkbox">跨分类搜索</label><label>来源<select id="collectionSource"><option value="">全部</option></select></label><label>版本<select id="collectionPatch"><option value="0">全部</option>'+Array.from({length:8},(_,i)=>'<option value="'+(i+1)+'">'+(i+1)+'.x</option>').join('')+'</select></label><div id="collectionEquipmentFilters"><label><input id="collectionDresser" type="checkbox">可放入投影台</label><label><input id="collectionArmoire" type="checkbox">可放入收藏柜</label><label><input id="collectionSameModel" type="checkbox">相同主模型</label><label>品级<input id="collectionLevelMin" type="number" min="0" max="9999" value="0">～<input id="collectionLevelMax" type="number" min="0" max="9999" value="0"></label></div></div><div class="collectionSummary"><span id="collectionProgress"></span><span id="collectionStatus" role="status"></span></div><div id="collectionGrid" role="list" aria-label="收录物品"></div><footer class="omniPagination"><button id="collectionPrevious">上一页</button><span id="collectionPageNumber"></span><button id="collectionNext">下一页</button><input id="collectionJump" type="number" min="1" value="1" aria-label="收录页码"><button id="collectionJumpButton">跳转</button></footer>';
const dialog=document.createElement('dialog');dialog.id='collectionDialog';dialog.innerHTML='<div class="dialogTop"><strong>收录信息</strong><button id="collectionClose">关闭</button></div><div id="collectionDetail"></div><div id="collectionDetailStatus" role="status"></div>';document.body.append(dialog);
let type='Equipment',category=0,index=0,total=0,size=60,sequence=0,generation=0,searchSequence=0,detailSequence=0,current=null,rows=[],favorites=[],favoriteKey='';
const requests=new Map();
const account=()=>{try{return String(Phone.itemAccountKey?.()||snapshot.gameId||'default')}catch{return 'default'}};
function loadFavorites(){const key='omni-collection-favorites:'+account();if(key===favoriteKey)return;favoriteKey=key;try{favorites=JSON.parse(localStorage.getItem(key)||'[]').filter(k=>typeof k==='string'&&/^\d+$/.test(k)).slice(0,5000)}catch{favorites=[]}}
function favorite(key){return favorites.includes(key)}
function toggleFavorite(key){favorites=favorite(key)?favorites.filter(k=>k!==key):[...favorites.slice(-4999),key];try{localStorage.setItem(favoriteKey,JSON.stringify(favorites))}catch{}}
function call(action,params={},scope='search'){
 const id=++sequence;if(scope==='search')for(const [key,r] of requests)if(r.scope==='search')requests.delete(key);requests.set(id,{action,generation,account:account(),scope,key:current?.key});if(scope==='search')searchSequence=id;else if(scope==='detail')detailSequence=id;
 if(typeof Phone.omniCollections!=='function'){window.omniCollectionsResult(id,action,false,{error:'请同步更新App与游戏内服务端'});return}Phone.omniCollections(action,JSON.stringify(params),id);
}
function nav(){
 $('collectionTypes').innerHTML=types.map(([id,label])=>'<button data-collection-type="'+id+'" class="'+(id===type?'on':'')+'">'+label+'</button>').join('');
 $('collectionEquipment').hidden=type!=='Equipment';$('collectionEquipmentFilters').hidden=type!=='Equipment';
 $('collectionEquipmentCategories').innerHTML=equipment.map(([id,label])=>'<button data-collection-category="'+id+'" class="'+(id===category?'on':'')+'">'+label+'</button>').join('');
}
function search(){
 loadFavorites();$('collectionStatus').textContent='正在读取游戏内Omni收录…';
 call('collection-search',{type,text:$('collectionQuery').value.trim(),page:index,category:type==='Equipment'?category:0,subcategory:type==='Equipment'?Number($('collectionSubcategory').value):0,
 unlock:Number($('collectionUnlock').value),market:Number($('collectionMarket').value),favorites,favoritesOnly:$('collectionFavoritesOnly').checked,
 allTypes:$('collectionAllTypes').checked,source:$('collectionSource').value,patches:Number($('collectionPatch').value)?[Number($('collectionPatch').value)]:[],
 dresserOnly:type==='Equipment'&&$('collectionDresser').checked,armoireOnly:type==='Equipment'&&$('collectionArmoire').checked,sameModel:type==='Equipment'&&$('collectionSameModel').checked,
 levelMin:type==='Equipment'?Number($('collectionLevelMin').value):0,levelMax:type==='Equipment'?Number($('collectionLevelMax').value):0});
}
const typeLabel=t=>types.find(([id])=>id===t)?.[1]||t;
function renderGrid(data){
 rows=data.rows||[];index=data.page||0;total=data.total||0;size=data.pageSize||60;
 const progress=data.progress;$('collectionProgress').textContent=progress?'已收录 '+Number(progress.unlocked).toLocaleString()+' / '+Number(progress.total).toLocaleString():'';
 $('collectionStatus').textContent='筛选结果 '+total.toLocaleString()+' 项';
 const sub=$('collectionSubcategory'),value=sub.value;sub.innerHTML='<option value="0">全部</option>'+(data.subcategories||[]).map(s=>'<option value="'+s.index+'">'+esc(s.label)+'</option>').join('');sub.value=[...sub.options].some(o=>o.value===value)?value:'0';
 const source=$('collectionSource'),sourceValue=source.value;source.innerHTML='<option value="">全部</option>'+(data.sources||[]).map(s=>'<option value="'+esc(s)+'">'+esc(sourceNames[s]||s)+'</option>').join('');source.value=[...source.options].some(o=>o.value===sourceValue)?sourceValue:'';
 $('collectionGrid').innerHTML=rows.length?rows.map((r,i)=>'<button role="listitem" class="collectionTile '+(r.known?(r.unlocked?'collected':'missing'):'unknown')+'" data-collection-index="'+i+'" aria-label="'+esc(r.name)+'，'+(r.known?(r.unlocked?'已收录':'未收录'):'收录状态未知')+'" title="'+esc(r.name)+'"><span class="collectionIcon">'+(r.iconId?'<img data-game-icon="'+Number(r.iconId)+'" alt="">':'<span class="collectionNoIcon">?</span>')+'</span>'+(r.known&&r.unlocked?'<span class="collectionCheck" aria-hidden="true">✓</span>':'')+(favorite(r.key)?'<span class="collectionStar" aria-hidden="true">♥</span>':'')+'<span class="collectionTileName">'+esc(r.name)+'</span></button>').join(''):'<p class="pageEmpty">没有找到任何结果。</p>';
 $('collectionPageNumber').textContent=(index+1)+' / '+Math.max(1,Math.ceil(total/size));$('collectionJump').value=index+1;
 $('collectionPrevious').disabled=index===0;$('collectionNext').disabled=(index+1)*size>=total;
 observeCharacterIcons();
}
function open(entry){current=entry;$('collectionDetail').textContent='正在读取收录信息…';$('collectionDetailStatus').textContent='';if(!dialog.open)dialog.showModal();call('collection-detail',{type:entry.type,key:entry.key},'detail')}
function close(){current=null;detailSequence=0;dialog.close()}
function locationText(value){return String(value||'').split(',').map(v=>({None:'未持有',Inventory:'背包',Armory:'兵装库',Saddlebag:'陆行鸟鞍囊',Retainer:'雇员',GlamourDresser:'投影台',Armoire:'收藏柜',Equipped:'已装备',FreeCompanyChest:'部队储物柜',RetainerMarket:'雇员出售',HousingStoreroom:'储藏室',Gearset:'装备套装'}[v.trim()]||v.trim())).join('／')}
function itemLink(id,label){return '<button class="collectionTextLink" data-collection-item="'+Number(id)+'">'+esc(label)+'</button>'}
function renderDetail(data){
 const e=data.entry;current=e;
 const state=e.known?(e.unlocked?'✓ 已收录':'未收录'):'收录状态尚未确认';
 const item=data.item||{};
 $('collectionDetail').innerHTML='<div class="collectionHeading"><span class="collectionDetailIcon">'+(e.iconId?'<img data-game-icon="'+Number(e.iconId)+'" alt="">':'')+'</span><div><strong>'+esc(e.name)+' ['+Number(e.id)+']</strong><p>'+esc(typeLabel(e.type))+(item.jobs?' · '+esc(item.jobs):'')+(item.requiredLevel?' · '+Number(item.requiredLevel)+'级':'')+(e.level?' · 物品品级 '+Number(e.level):'')+(e.patch?' · '+esc(String(e.patch))+'版本':'')+'</p><p class="'+(e.known&&e.unlocked?'collectionUnlocked':'')+'">'+state+'</p><p>'+esc(({Purchasable:'可购买',Obtainable:'可获得',Unobtainable:'暂不可获得'})[e.availability]||'')+' · '+(e.tradable?'可交易':'不可交易')+'</p></div></div><div class="collectionActions"><button id="collectionFavorite">'+(favorite(e.key)?'♥ 已收藏':'♡ 收藏')+'</button><button id="collectionCopyName">复制名称</button><button id="collectionCopyId">复制ID</button>'+(e.itemId?'<button id="collectionBuy">物品信息／购买</button>':'')+(data.canPreview?'<button id="collectionPreview">游戏内预览</button>':'')+'</div>'+(data.description?'<p class="collectionProse">'+symbolText(data.description)+'</p>':'')+(data.detail?'<p class="collectionProse">'+symbolText(data.detail)+'</p>':'')+(item.category?'<p>'+esc(item.category)+'</p>':'')+(data.owned&&e.itemId?'<p>持有 '+Number(data.owned.total)+'<br>位置：'+esc(locationText(data.owned.locations)||'未持有')+(data.owned.partial?'（当前已加载库存）':'')+'</p>':'')+(data.sets||[]).map(s=>'<section class="collectionSet"><h3>属于：'+esc(s.name)+'</h3>'+s.items.map(i=>'<div class="'+(i.unlocked?'collectionUnlocked':'')+'">'+(i.unlocked?'✓ ':'○ ')+itemLink(i.id,i.name)+'</div>').join('')+'</section>').join('')+'<section class="collectionSources"><h3>获取途径</h3>'+((data.sources||[]).map(s=>'<article><div>'+symbolText(s.description||sourceNames[s.category]||s.category)+'</div>'+(s.detail?'<p>'+symbolText(s.detail)+'</p>':'')+(s.location?'<p>'+esc(s.locationName||'区域 '+Number(s.location.TerritoryID))+'（'+Number(s.location.X).toFixed(1)+', '+Number(s.location.Y).toFixed(1)+'）</p>':'')+(s.canMap?'<button data-collection-map="'+Number(s.index)+'">游戏内地图</button>':'')+(s.targetItemId?itemLink(s.targetItemId,'查看关联物品'):'')+'</article>').join('')||'<p>Omni暂无公开获取途径。</p>')+'</section>';
 $('collectionFavorite').onclick=()=>{toggleFavorite(e.key);$('collectionFavorite').textContent=favorite(e.key)?'♥ 已收藏':'♡ 收藏';const tile=rows.findIndex(r=>r.key===e.key);if(tile>=0){const node=$('collectionGrid').querySelector('[data-collection-index="'+tile+'"]');node?.querySelector('.collectionStar')?.remove();if(favorite(e.key))node?.insertAdjacentHTML('beforeend','<span class="collectionStar" aria-hidden="true">♥</span>')}};
 $('collectionCopyName').onclick=()=>Phone.copyItemText(e.name);$('collectionCopyId').onclick=()=>Phone.copyItemText(String(e.itemId||e.id));
 if($('collectionBuy'))$('collectionBuy').onclick=()=>{close();window.openOmniItemFromCollection(e.itemId)};
 if($('collectionPreview'))$('collectionPreview').onclick=()=>{ $('collectionPreview').disabled=true;call('collection-preview',{type:e.type,key:e.key},'operation') };
 $('collectionDetail').querySelectorAll('[data-collection-item]').forEach(b=>b.onclick=()=>{const id=Number(b.dataset.collectionItem);close();window.openOmniItemFromCollection(id)});
 $('collectionDetail').querySelectorAll('[data-collection-map]').forEach(b=>b.onclick=()=>{$('collectionDetailStatus').textContent='正在读取游戏地图…';call('collection-map',{type:e.type,key:e.key,sourceIndex:Number(b.dataset.collectionMap)},'operation')});
 observeCharacterIcons();
}
$('collectionTypes').onclick=e=>{const b=e.target.closest('[data-collection-type]');if(!b)return;type=b.dataset.collectionType;category=0;index=0;$('collectionSubcategory').value='0';nav();search()};
$('collectionEquipmentCategories').onclick=e=>{const b=e.target.closest('[data-collection-category]');if(!b)return;category=Number(b.dataset.collectionCategory);index=0;$('collectionSubcategory').value='0';nav();search()};
$('collectionSubcategory').onchange=()=>{index=0;search()};
$('collectionSearchForm').onsubmit=e=>{e.preventDefault();index=0;search()};$('collectionRefresh').onclick=search;
$('collectionFilterToggle').onclick=()=>{const hidden=!$('collectionFilters').hidden;$('collectionFilters').hidden=hidden;$('collectionFilterToggle').setAttribute('aria-expanded',String(!hidden))};
$('collectionFilters').onchange=()=>{index=0;search()};
$('collectionGrid').onclick=e=>{const b=e.target.closest('[data-collection-index]');if(b)open(rows[Number(b.dataset.collectionIndex)])};
$('collectionPrevious').onclick=()=>{index=Math.max(0,index-1);search()};$('collectionNext').onclick=()=>{index++;search()};
$('collectionJumpButton').onclick=()=>{index=Math.max(0,Math.min(Math.max(0,Math.ceil(total/size)-1),(Number($('collectionJump').value)||1)-1));search()};
$('collectionClose').onclick=close;dialog.addEventListener('cancel',e=>{e.preventDefault();close()});
window.openOmniCollectionsPage=()=>{generation++;requests.clear();close();loadFavorites();nav();search()};
window.closeOmniCollectionsPage=()=>{generation++;requests.clear();close()};
window.omniCollectionsResult=(id,action,ok,data)=>{
 const r=requests.get(id);requests.delete(id);
 if(!r||r.action!==action||r.generation!==generation||r.account!==account()||appPageState().page!=='collections'||(r.scope==='search'?id!==searchSequence:!current||r.key!==current.key||r.scope==='detail'&&id!==detailSequence))return;
 const status=r.scope==='search'?$('collectionStatus'):$('collectionDetailStatus');
 if(!ok){status.textContent=data.error||'Omni收录读取失败';if($('collectionPreview'))$('collectionPreview').disabled=false;return}
 if(action==='collection-search')renderGrid(data);else if(action==='collection-detail')renderDetail(data);else if(action==='collection-map'){openOmniGameMap(data);status.textContent=''}else{status.textContent=data.status||'请求已交给Omni';if($('collectionPreview'))$('collectionPreview').disabled=false}
};
nav();
})();
