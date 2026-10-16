/* Original game map image and target are supplied by the paired game server. */
(()=>{
const dialog=document.createElement('dialog');dialog.id='gameMapDialog';
dialog.innerHTML='<div class="dialogTop"><strong id="gameMapTitle">地图</strong><button id="gameMapClose">关闭</button></div><div class="gameMapTools"><span id="gameMapPosition"></span><button id="gameMapOut" aria-label="缩小地图">－</button><button id="gameMapIn" aria-label="放大地图">＋</button><button id="gameMapCenter">定位</button></div><div id="gameMapViewport"><div id="gameMapCanvas"><img id="gameMapImage" alt=""><span id="gameMapPin" aria-label="目标位置">⚑</span></div></div><p class="prefNote">拖动查看地图，使用＋／－缩放。</p>';document.body.append(dialog);
let zoom=1,pinX=.5,pinY=.5;
function center(){const v=$('gameMapViewport'),c=$('gameMapCanvas');v.scrollLeft=pinX*c.clientWidth-v.clientWidth/2;v.scrollTop=pinY*c.clientHeight-v.clientHeight/2}
function resize(){const v=$('gameMapViewport');const side=Math.max(300,Math.min(700,v.clientWidth))*zoom;$('gameMapCanvas').style.width=side+'px';$('gameMapCanvas').style.height=side+'px';requestAnimationFrame(center)}
window.openOmniGameMap=data=>{
 if(!/^data:image\/(?:jpeg|png);base64,[A-Za-z0-9+/=]+$/.test(data.image||'')){toast('地图图像不可用');return}
 zoom=1;pinX=Number(data.pinX);pinY=Number(data.pinY);if(!Number.isFinite(pinX)||!Number.isFinite(pinY))return;
 $('gameMapTitle').textContent=data.name||'地图';$('gameMapPosition').textContent='X: '+Number(data.x).toFixed(1)+'  Y: '+Number(data.y).toFixed(1);
 const pin=$('gameMapPin');pin.style.left=pinX*100+'%';pin.style.top=pinY*100+'%';pin.hidden=pinX<0||pinX>1||pinY<0||pinY>1;
 $('gameMapImage').onload=center;$('gameMapImage').src=data.image;$('gameMapImage').alt=data.name||'游戏地图';
 if(!dialog.open)dialog.showModal();resize();
};
$('gameMapIn').onclick=()=>{zoom=Math.min(4,zoom+.5);resize()};$('gameMapOut').onclick=()=>{zoom=Math.max(1,zoom-.5);resize()};$('gameMapCenter').onclick=center;$('gameMapClose').onclick=()=>dialog.close();
let drag=null;const v=$('gameMapViewport');
v.addEventListener('pointerdown',e=>{if(e.pointerType==='touch')return;drag={x:e.clientX,y:e.clientY,left:v.scrollLeft,top:v.scrollTop};v.setPointerCapture(e.pointerId)});
v.addEventListener('pointermove',e=>{if(drag){v.scrollLeft=drag.left+drag.x-e.clientX;v.scrollTop=drag.top+drag.y-e.clientY}});
for(const event of ['pointerup','pointercancel','lostpointercapture'])v.addEventListener(event,()=>drag=null);
})();
