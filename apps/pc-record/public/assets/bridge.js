'use strict';
// Единственный канал к системе — локальный WebView2-мост с перечнем команд на стороне C#.
window.PCBridge=(()=>{
 const pending=new Map();let serial=0;
 const native=Boolean(window.chrome&&window.chrome.webview);
 if(native)window.chrome.webview.addEventListener('message',event=>{
  const message=event.data;
  if(message.event){document.dispatchEvent(new CustomEvent(message.event,{detail:message}));return;}
  const item=pending.get(message.id);if(!item)return;pending.delete(message.id);clearTimeout(item.timer);
  if(message.ok)item.resolve(message.result);else item.reject(new Error(message.error||'Ошибка системной команды'));
 });
 function request(method,args={},timeout=125000){
  if(!native)return Promise.reject(new Error('Откройте PC Record.exe — системная часть недоступна в обычном браузере'));
  const id=String(++serial);
  return new Promise((resolve,reject)=>{
   const timer=setTimeout(()=>{pending.delete(id);reject(new Error('Превышено время ожидания команды: '+method));},timeout);
   pending.set(id,{resolve,reject,timer});window.chrome.webview.postMessage({id,method,args});
  });
 }
 return {native,request};
})();