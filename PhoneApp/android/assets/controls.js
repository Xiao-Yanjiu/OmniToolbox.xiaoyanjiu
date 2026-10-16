/* Account rename and persisted navigation sorting use app UI rather than WebView prompts. */
(()=>{
const dialog=document.createElement('dialog');dialog.id='renameAccountDialog';dialog.innerHTML='<form class="renameForm"><div class="dialogTop"><strong>修改账号名称</strong><button type="button" onclick="closeAccountRename()">关闭</button></div><input id="renameAccountInput" maxlength="40" autocomplete="off" aria-label="账号名称"><p id="renameAccountError" role="status"></p><div class="tools"><button type="button" onclick="closeAccountRename()">取消</button><button id="renameAccountSave" type="submit">保存</button></div></form>';document.body.append(dialog);
let renaming='',saving=false;
window.openAccountRename=id=>{let accounts=[];try{accounts=JSON.parse(Phone.accounts()||'[]')}catch(e){}const account=accounts.find(a=>a.id===id);if(!account){toast('账号不存在，请重新连接');return}renaming=id;saving=false;$('renameAccountInput').value=account.label||account.name||'';$('renameAccountError').textContent='';$('renameAccountSave').disabled=false;dialog.showModal();$('renameAccountInput').focus();$('renameAccountInput').select()};
window.closeAccountRename=()=>{if(!saving)dialog.close()};
dialog.querySelector('form').onsubmit=e=>{e.preventDefault();if(saving)return;const name=$('renameAccountInput').value.trim();if(!name||name.length>40){$('renameAccountError').textContent='请输入1～40个字的账号名称';return}saving=true;$('renameAccountSave').disabled=true;$('renameAccountError').textContent='正在保存…';Phone.renameAccount(renaming,name)};
window.accountRenameDone=(ok,error)=>{saving=false;$('renameAccountSave').disabled=false;if(ok){dialog.close();accountsChanged();toast('账号名称已更新')}else $('renameAccountError').textContent=error||'保存失败，请重试'};
})();
