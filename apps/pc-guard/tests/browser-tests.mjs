// Проверка реального локального API и браузерного интерфейса; защитные режимы не переключаются.
import {spawn} from 'node:child_process';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=process.argv[2]||path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const runtime=JSON.parse(await readFile(path.join(root,'Data','test-runtime.json'),'utf8'));
const base=`http://127.0.0.1:${runtime.port}`;
const checks=[];const errors=[];
function check(condition,name){if(!condition)throw new Error(name);checks.push(name);}
async function request(action,args={},headers={}){const response=await fetch(base+'/api',{method:'POST',headers:{'Content-Type':'application/json','X-PCGuard-Token':runtime.token,...headers},body:JSON.stringify({action,...args})});return {status:response.status,...await response.json()};}
async function run(action,args={}){const response=await request(action,args);if(!response.ok)throw new Error(response.error);if(!response.data.job)return response.data;const deadline=Date.now()+120000;while(Date.now()<deadline){const status=await request('job',{id:response.data.job});if(!status.ok)throw new Error(status.error);if(['done','error'].includes(status.data.state))return status.data;await new Promise(r=>setTimeout(r,350));}throw new Error('Задание не ответило: '+action);}
check((await request('meta',{}, {'X-PCGuard-Token':'wrong'})).status===403,'API отвергает неверный ключ');
check((await request('meta',{}, {'Origin':'https://example.invalid'})).status===403,'API отвергает чужой Origin');
check((await fetch(base+'/Data/runtime.json')).status===404,'Файлы данных не опубликованы');
check((await fetch(base+'/api')).status===403,'Чтение API без ключа запрещено');
check(!(await request('startup_disable',{confirmed:true,id:'../invalid'})).ok,'Недопустимый ID изменения отклонён');
check(!(await request('scan',{type:'QuickScan'})).ok,'Проверка без подтверждения отклонена');
const status=await run('status');check(status.state==='done'&&status.data.defender?.mode,'Прочитано реальное состояние Defender');
const beforeMode=status.data.defender.mode;
if(!status.data.defender.canScan){const blocked=await run('scan',{type:'QuickScan',confirmed:true});check(blocked.state==='error'&&blocked.message.includes('не запускалась'),'В пассивном режиме сканирование не запускается');}
for(const action of ['startup','network','threats']){const result=await run(action);check(result.state==='done',`Реальное чтение ${action}`);}
const analysis=await run('file',{path:path.join(root,'README.txt')});check(analysis.state==='done'&&analysis.data.file.sha256?.length===64,'Получен SHA-256 собственного README');
await request('snapshot');await run('network');const comparison=await request('compare');check(comparison.ok&&comparison.data.changes.length===0,'Повторный аудит совпадает со снимком');
const exported=await request('export');check(exported.ok&&exported.data.path.startsWith(root),'Отчёт сохраняется на D');
check((await run('status')).data.defender.mode===beforeMode,'Режим Defender не изменён');

const profile=path.join(root,'Data','Tests','browser-'+Date.now());await mkdir(profile,{recursive:true});
const browser=spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',['--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--disable-background-networking','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
let ws;
try{
  const deadline=Date.now()+20000;let port;
  while(Date.now()<deadline){try{port=Number((await readFile(path.join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0]);if(port)break;}catch{}await new Promise(r=>setTimeout(r,100));}
  if(!port)throw new Error('Не запущен тестовый Chrome');
  const targets=await(await fetch(`http://127.0.0.1:${port}/json`)).json();ws=new WebSocket(targets.find(x=>x.type==='page').webSocketDebuggerUrl);
  await new Promise((resolve,reject)=>{ws.addEventListener('open',resolve,{once:true});ws.addEventListener('error',reject,{once:true});});
  let serial=0;const pending=new Map();
  ws.addEventListener('message',event=>{const message=JSON.parse(event.data);if(message.id){const request=pending.get(message.id);pending.delete(message.id);if(message.error)request.reject(new Error(JSON.stringify(message.error)));else request.resolve(message.result);}else if(message.method==='Runtime.exceptionThrown')errors.push(message.params.exceptionDetails.exception?.description||message.params.exceptionDetails.text);});
  const send=(method,params={})=>new Promise((resolve,reject)=>{const id=++serial;pending.set(id,{resolve,reject});ws.send(JSON.stringify({id,method,params}));});
  const evaluate=async expression=>{const response=await send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(response.exceptionDetails)throw new Error(response.exceptionDetails.exception?.description||response.exceptionDetails.text);return response.result.value;};
  async function waitFor(expression){const deadline=Date.now()+120000;while(Date.now()<deadline){try{if(await evaluate(expression))return;}catch(error){if(!error.message.includes('context'))throw error;}await new Promise(r=>setTimeout(r,150));}throw new Error('Ожидание интерфейса: '+expression);}
  await send('Runtime.enable');await send('Page.enable');await send('Emulation.setDeviceMetricsOverride',{width:1160,height:900,deviceScaleFactor:1,mobile:false});
  await send('Page.navigate',{url:base+'/#'+runtime.token});
  await waitFor('typeof Guard!=="undefined" && Boolean(Guard.state.cache.status)');
  check(await evaluate('document.querySelector("h1").textContent==="Защита без лишнего"'),'Показан настоящий обзор');
  await evaluate('Guard.setPage("startup")');await waitFor('Boolean(document.querySelector("#startup-table tbody tr")) && Boolean(Guard.state.cache.startup)');
  check(await evaluate('Guard.state.tables[0].rows().count()>0'),'Реальные точки запуска отображаются в DataTables');
  await evaluate('document.getElementById("startup-state").value="off";document.getElementById("startup-state").dispatchEvent(new Event("change",{bubbles:true}))');
  check(await evaluate('Guard.state.tables[0].rows({search:"applied"}).data().toArray().every(x=>x.state==="off")'),'Фильтр выключенного состояния');
  await evaluate('document.getElementById("startup-state").value="";document.getElementById("startup-state").dispatchEvent(new Event("change",{bubbles:true}));Guard.state.tables[0].order([3,"asc"]).draw();void 0');
  check(await evaluate('(()=>{const a=Guard.state.tables[0].rows({order:"applied",search:"applied"}).data().toArray().map(x=>x.size).filter(x=>x!==null);return a.every((v,i)=>!i||v>=a[i-1]);})()'),'Размеры сортируются численно');
  await evaluate('document.getElementById("startup-search").value="IObit";document.getElementById("startup-search").dispatchEvent(new Event("input",{bubbles:true}))');
  check(await evaluate('Guard.state.tables[0].rows({search:"applied"}).count()>0'),'Поиск находит установленные IObit-записи');
  await writeFile(path.join(root,'Data','Tests','startup-real.png'),Buffer.from((await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true})).data,'base64'));
  for(const page of ['scan','threats','network','file','history','about','overview']){await evaluate(`Guard.setPage(${JSON.stringify(page)})`);await waitFor('document.querySelector("h1")!==null');check(await evaluate('document.querySelector("h1").textContent.length>0'),`Экран ${page}`);}
  await waitFor('Guard.state.jobs.size===0');
  await evaluate('document.getElementById("theme-button").click()');await waitFor('document.documentElement.dataset.bsTheme==="light"');check(true,'Переключается светлая тема');
  await evaluate('document.getElementById("theme-button").click()');await waitFor('document.documentElement.dataset.bsTheme==="dark"');check(true,'Переключается тёмная тема');
  await writeFile(path.join(root,'Data','Tests','overview-real.png'),Buffer.from((await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true})).data,'base64'));
  for(const width of [736,375]){await send('Emulation.setDeviceMetricsOverride',{width,height:1000,deviceScaleFactor:1,mobile:false});check(await evaluate('document.documentElement.scrollWidth<=document.documentElement.clientWidth+1'),`Нет внешнего переполнения ${width}px`);}
  // Проверяем все экраны со списками, включая фокус, обе темы и масштаб 200%.
  const dropdownMetrics=[];
  for(const theme of ['dark','light'])for(const scale of [1,2])for(const width of [1160,736,375]){
    await send('Emulation.setDeviceMetricsOverride',{width,height:1000,deviceScaleFactor:scale,mobile:false});await evaluate(`document.documentElement.dataset.bsTheme=${JSON.stringify(theme)}`);
    for(const page of ['startup','threats','history']){
      await evaluate(`Guard.setPage(${JSON.stringify(page)})`);await waitFor('Boolean(document.querySelector("select.form-select"))');
      const metrics=await evaluate(`(()=>{const canvas=document.createElement('canvas'),ctx=canvas.getContext('2d');return [...document.querySelectorAll('select.form-select')].map(el=>{el.focus();const css=getComputedStyle(el);ctx.font=css.font;const text=ctx.measureText(el.selectedOptions[0]?.textContent||'').width;const right=parseFloat(css.paddingRight),left=parseFloat(css.paddingLeft),rect=el.getBoundingClientRect();return {id:el.id,width:rect.width,text,right,left,arrow:css.backgroundImage!=='none',fits:el.clientWidth-left-right>=text-1};});})()`);
      dropdownMetrics.push({theme,scale,width,page,controls:metrics});check(metrics.every(x=>x.right>=40&&x.arrow&&x.fits),`Текст отделён от стрелки: ${page}, ${theme}, ${width}px, x${scale}`);
      check(await evaluate('document.documentElement.scrollWidth<=document.documentElement.clientWidth+1'),`Нет внешнего переполнения со списками: ${page}, ${width}px, x${scale}`);
    }
  }
  await send('Emulation.setDeviceMetricsOverride',{width:1160,height:900,deviceScaleFactor:2,mobile:false});await evaluate('document.documentElement.dataset.bsTheme="dark";Guard.setPage("startup")');await waitFor('Boolean(document.querySelector("#startup-type"))');
  await writeFile(path.join(root,'Data','Tests','dropdowns-200.png'),Buffer.from((await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true})).data,'base64'));
  await writeFile(path.join(root,'Data','Tests','dropdown-layout.json'),JSON.stringify(dropdownMetrics,null,2));
  // Проверяем рекламу как локальный ресурс, внешнюю ссылку и отсутствие наложений.
  const partnerResponse=await fetch(base+'/assets/zabivki-logo.png');check(partnerResponse.ok&&partnerResponse.headers.get('content-type')==='image/png','Логотип рекламы отдаётся локально с верным MIME');
  for(const theme of ['dark','light'])for(const width of [1160,736,375]){
    await send('Emulation.setDeviceMetricsOverride',{width,height:1000,deviceScaleFactor:2,mobile:false});await evaluate(`document.documentElement.dataset.bsTheme=${JSON.stringify(theme)}`);
    await waitFor('document.querySelector(".partner-logo").complete&&document.querySelector(".partner-logo").naturalWidth>0');
    check(await evaluate('(()=>{const block=document.querySelector(".partner-strip"),link=block.querySelector("a"),img=block.querySelector("img"),main=document.querySelector("main"),footer=document.querySelector("footer"),b=block.getBoundingClientRect(),m=main.getBoundingClientRect(),f=footer.getBoundingClientRect(),r=link.getBoundingClientRect(),i=img.getBoundingClientRect();return link.href==="https://zabivki.ru/"&&link.target==="_blank"&&link.rel.includes("noopener")&&link.rel.includes("noreferrer")&&i.width>100&&i.right<=r.right+1&&b.top>=m.bottom-1&&b.bottom<=f.top+1&&b.height<115&&document.documentElement.scrollWidth<=document.documentElement.clientWidth+1;})()'),`Реклама читается и не перекрывает приложение: ${theme}, ${width}px`);
    if(width===1160||width===375){await evaluate('document.querySelector(".partner-strip").scrollIntoView({block:"end"})');await writeFile(path.join(root,'Data','Tests',`partner-${theme}-${width}.png`),Buffer.from((await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:false})).data,'base64'));}
  }
  check(errors.length===0,'Нет исключений браузерного JavaScript');
  await Promise.race([send('Browser.close').catch(()=>{}),new Promise(resolve=>setTimeout(resolve,1000))]);
}finally{ws?.close();if(browser.exitCode===null)browser.kill();}
await writeFile(path.join(root,'Data','Tests','browser-results.json'),JSON.stringify({checks:checks.length,results:checks,errors,modeBefore:beforeMode,modeAfter:(await run('status')).data.defender.mode,hostRuntime:'.NET Framework; standalone host on D; PHP is not required'},null,2));
console.log(JSON.stringify({checks:checks.length,errors,modeBefore:beforeMode}));
