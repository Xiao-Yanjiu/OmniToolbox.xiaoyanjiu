(()=>{
let request=0,hq=false;const dialog=document.createElement('dialog');dialog.id='itemInfoDialog';dialog.innerHTML='<div class="dialogTop"><strong>物品信息</strong><button onclick="closeItemInfo()">关闭</button></div><div id="itemInfoContent"></div>';document.body.append(dialog);
window.openItemInfo=(id,quality=false)=>{if(!Number.isInteger(Number(id))||id<=0)return;hq=!!quality;const next=++request;$('itemInfoContent').textContent='正在读取物品资料…';if(!dialog.open)dialog.showModal();if(typeof Phone.item==='function')Phone.item(Number(id),next);else itemResult(next,false,{error:'请更新 App 与服务端'})};
window.closeItemInfo=()=>{request++;dialog.close()};
window.itemResult=(id,ok,data)=>{if(id!==request||!dialog.open)return;$('itemInfoContent').innerHTML=ok?'<div class="itemHeading">'+(data.icon?iconImage(data.icon,''):'')+'<strong>'+esc(data.name)+(hq?' HQ':'')+'</strong></div><div class="itemCategory">'+esc(data.category)+'<span>物品等级 '+data.level+'</span></div><hr><p>'+symbolText(data.description)+'</p><div class="itemFacts">普通品质收购价格：'+esc(String(data.price))+' 金币<br>最大堆叠：'+data.stackSize+'</div>':'<p class="pageEmpty">'+esc(data.error||'物品资料读取失败')+'</p>'};
})();
