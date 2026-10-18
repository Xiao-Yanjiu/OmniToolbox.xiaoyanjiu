(()=>{
'use strict';window.__omniGlamourExport={pending:true};
const finish=data=>window.__omniGlamourExport={data};
const fail=error=>window.__omniGlamourExport={error:String(error.message||error)};
function fromModel(model){
 if(!model||!Array.isArray(model.equipments)||typeof model.title!=='string')throw Error('网站未返回完整幻化数据');
 const slots=[['MAIN_HAND','主手'],['OFF_HAND','副手'],['HEAD','头部'],['BODY','上衣'],['GLOVES','手部'],['LEGS','腿部'],['FEET','脚部'],['EARS','耳坠'],['NECK','项链'],['WRISTS','手镯'],['FINGER_LEFT','戒指'],['FINGER_RIGHT','戒指']];
 const parts=slots.map(([slot,part])=>{const matches=model.equipments.filter(x=>x.slot===slot);if(matches.length>1)throw Error('网站装备部位重复');const item=matches[0];if(!item||item.equipment_id===-1)return {part,name:'',dyes:[]};if(typeof item.name!=='string')throw Error('网站装备名称缺失');const dyes=(item.dye_ids||[]).map(id=>{if(id===-1||id===0)return '';const dye=(item.dyes||[]).find(d=>Number(d.id)===Number(id));if(!dye)throw Error('网站染色数据不完整');return String(dye.name||'').replace(/染剂$/,'').trim();});return {part,name:item.name.trim(),dyes};});
 const glasses=model.ortInfo;if(glasses&&Number(glasses.glasses_id)>0&&glasses.glasses_name)parts.push({part:'面部配饰',name:glasses.glasses_name,dyes:[]});
 return {name:model.title,parts};
}
function fromDom(){const nodes=[...document.querySelectorAll('.glamour__part')];if(nodes.length<12)throw Error('装备列表未加载完整');const scope=document.querySelector('.glamour__detail___info')||document;return {name:scope.querySelector('.info__title')?.textContent.trim()||'石之家幻化',parts:nodes.map(el=>({part:el.querySelector('.glamour__part___name')?.textContent.trim()||'',name:el.querySelector('.part__info___name')?.textContent.trim()||'',dyes:[...el.querySelectorAll('.info__dye___item')].map(x=>x.textContent.trim())}))};}
(async()=>{try{const match=location.hash.match(/\/glamour\/detail\/(\d+)/);if(!match)throw Error('请打开石之家幻化详情页');const controller=new AbortController(),timeout=setTimeout(()=>controller.abort(),10000);try{const response=await fetch('https://apiff14risingstones.web.sdo.com/api/home/glamour/glamourDetail?id='+match[1]+'&tempsuid='+encodeURIComponent(crypto.randomUUID?crypto.randomUUID():String(Date.now())),{credentials:'include',signal:controller.signal});if(!response.ok)throw Error('网站返回 '+response.status);const json=await response.json();if(Number(json.code)!==10000&&Number(json.code)!==10002)throw Error(json.msg||'请先登录石之家');finish(fromModel(json.data));}finally{clearTimeout(timeout);}}catch(e){try{finish(fromDom());}catch(domError){fail(Error((e.message||'读取失败')+'；'+domError.message));}}})();
})();
