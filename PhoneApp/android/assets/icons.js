(()=>{
const cache=new Map(),waiting=new Set(),queue=new Set();let timer=0;const observer=new IntersectionObserver(entries=>{for(const e of entries)if(e.isIntersecting){const id=e.target.dataset.gameIcon;if(cache.has(id))e.target.src=cache.get(id);else if(!waiting.has(id)){queue.add(Number(id));clearTimeout(timer);timer=setTimeout(send,40)}}},{root:null,rootMargin:'100px'});
function send(){if(typeof Phone.icons!=='function')return;const ids=[...queue].slice(0,12);if(!ids.length)return;for(const id of ids){queue.delete(id);waiting.add(String(id))}Phone.icons(JSON.stringify(ids));if(queue.size)timer=setTimeout(send,80)}
window.observeCharacterIcons=()=>{observer.disconnect();for(const image of document.querySelectorAll('[data-game-icon]')){const id=image.dataset.gameIcon;if(cache.has(id))image.src=cache.get(id);else observer.observe(image)}};
window.characterIconsResult=data=>{for(const id of data.failed||[])waiting.delete(String(id));for(const [id,url] of Object.entries(data.icons||{})){waiting.delete(id);if(!url?.startsWith('data:image/png;base64,'))continue;cache.set(id,url);for(const image of document.querySelectorAll('[data-game-icon="'+Number(id)+'"]'))image.src=url}};
})();
