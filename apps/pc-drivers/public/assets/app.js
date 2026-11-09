/* Интерфейс получает реальные результаты; текст из системы выводится только как текст. */
'use strict';
const tokenKey='pc-drivers-token';
const token=location.hash.slice(1)||sessionStorage.getItem(tokenKey)||'';
if(token)sessionStorage.setItem(tokenKey,token);
history.replaceState(null,'','/');
let devices=[], selected=null, busy=false;
const $id=id=>document.getElementById(id), text=DataTable.render.text();
const lang={emptyTable:'Нет данных',zeroRecords:'Ничего не найдено',info:'Показано _START_–_END_ из _TOTAL_',infoEmpty:'Нет записей',infoFiltered:'(всего _MAX_)',lengthMenu:'По _MENU_ записей',paginate:{first:'В начало',last:'В конец',next:'Далее',previous:'Назад'}};
const table=new DataTable('#devices',{data:[],columns:[{data:'Name',render:text},{data:'Class',render:text},{data:'Status',render:text},{data:'Provider',render:text},{data:'Version',render:(v,t)=>t==='sort'?v.split('.').map(n=>n.padStart(5,'0')).join('.'):text.display(v)},{data:'Date',render:(v,t)=>t==='sort'?v.split('.').reverse().join('-'):text.display(v)},{data:'Inf',render:text}],language:lang,layout:{topStart:null,topEnd:null,bottomStart:'info',bottomEnd:'paging'},pageLength:10,scrollX:true,order:[[0,'asc']],createdRow:(row,d)=>{row.classList.add(d.Problem===0?'ok':d.Problem===null?'unknown':'problem');row.tabIndex=0;row.title=d.Id;}});
const candidates=new DataTable('#candidates',{data:[],columns:[{data:'Description',render:text},{data:'Provider',render:text},{data:'Version',render:(v,t)=>t==='sort'?v.split('.').map(n=>n.padStart(5,'0')).join('.'):text.display(v)},{data:'Date',render:(v,t)=>t==='sort'?v.split('.').reverse().join('-'):text.display(v)},{data:'Rank',render:(v,t)=>t==='display'?'0x'+v.toString(16).padStart(8,'0'):v}],language:lang,layout:{topStart:null,topEnd:null,bottomStart:'info',bottomEnd:'paging'},scrollX:true,order:[[4,'asc']]});
const backups=new DataTable('#backup-table',{data:[],columns:[{data:'name',render:text},{data:'path',render:text},{data:'manifest',render:v=>v?'Сохранён':'Нет манифеста'}],language:lang,layout:{topStart:null,topEnd:null,bottomStart:'info',bottomEnd:'paging'},scrollX:true,order:[[0,'desc']]});
function result(s){$id('result').textContent=s;$id('result').title=s;}
async function api(action,more={}){const r=await fetch('/api',{method:'POST',headers:{'Content-Type':'application/json','X-PCDrivers-Token':token},body:JSON.stringify({action,...more})});const j=await r.json();if(!r.ok||!j.ok)throw Error(j.error||'Ошибка запроса');return j.data;}
async function run(fn){if(busy)return;busy=true;$id('busy').hidden=false;document.querySelectorAll('button').forEach(b=>b.disabled=true);try{await fn();}catch(e){result('Ошибка: '+e.message);}finally{busy=false;$id('busy').hidden=true;document.querySelectorAll('button').forEach(b=>b.disabled=false);selectionButtons();}}
function page(name){document.querySelectorAll('.page').forEach(p=>p.hidden=p.id!==name);document.querySelectorAll('.nav').forEach(b=>b.classList.toggle('active',b.dataset.page===name));table.columns.adjust();candidates.columns.adjust();backups.columns.adjust();if(name==='backups')run(loadBackups);}
function selectionButtons(){$id('to-package').disabled=busy||!selected;$id('backup').disabled=busy||!selected||!/^oem[0-9]+\.inf$/i.test(selected.Inf);}
function select(d){selected=d;$id('selected-name').textContent=d.Name;$id('selected-desc').textContent=[d.Id,d.Inf,d.ReadError].filter(Boolean).join(' · ');$id('package-device').textContent=d.Name;$id('package-ids').textContent=['Hardware ID:',...d.HardwareIds,'','Compatible ID:',...d.CompatibleIds].join('\n');candidates.clear().draw();$id('match-status').textContent='Выберите INF для этого устройства';selectionButtons();}
document.querySelectorAll('.nav').forEach(b=>b.onclick=()=>page(b.dataset.page));
$('#devices tbody').on('click keydown','tr',function(e){if(e.type==='keydown'&&e.key!=='Enter'&&e.key!==' ')return;const d=table.row(this).data();if(!d)return;$('#devices tbody tr').removeClass('selected');this.classList.add('selected');select(d);});
$id('search').oninput=e=>table.search(e.target.value).draw();
table.search.fixed('status',(_search,d)=>{const s=$id('status').value;return s==='all'||s==='ok'&&d.Problem===0||s==='problem'&&d.Problem!==null&&d.Problem!==0||s==='unknown'&&d.Problem===null;});
$id('status').onchange=()=>table.draw();
$id('scan').onclick=()=>run(async()=>{result('Чтение оборудования…');devices=await api('scan');selected=null;table.clear().rows.add(devices).draw();$id('total').textContent=devices.length;$id('problems').textContent=devices.filter(d=>d.Problem!==null&&d.Problem!==0).length;$id('third').textContent=new Set(devices.map(d=>d.Inf).filter(v=>/^oem[0-9]+\.inf$/i.test(v))).size;$id('scanned').textContent=new Date().toLocaleTimeString('ru-RU');$id('selected-name').textContent='Выберите устройство в таблице';$id('selected-desc').textContent='Идентификаторы и установленный пакет появятся здесь';$id('package-device').textContent='Устройство не выбрано';$id('package-ids').textContent='Выберите устройство в разделе «Оборудование»';candidates.clear().draw();result('Прочитано устройств: '+devices.length);});
$id('report').onclick=()=>run(async()=>{const r=await api('report');result('Отчёт сохранён: '+r.path);});
$id('to-package').onclick=()=>page('packages');
$id('pick').onclick=()=>run(async()=>{const p=await api('pickInf');if(p)$id('inf').value=p;});
$id('match').onclick=()=>run(async()=>{if(!selected)throw Error('Сначала выберите устройство');const rows=await api('match',{id:selected.Id,inf:$id('inf').value.trim()});candidates.clear().rows.add(rows).draw();$id('match-status').textContent=rows.length?'Совместимых моделей: '+rows.length+'. Меньший ранг Windows предпочтительнее; установка не выполнялась':'Windows не нашла совместимых моделей в этом INF';result('Проверка INF завершена');});
$id('backup').onclick=()=>run(async()=>{if(!selected)return;if(!confirm('Сохранить резервную копию пакета '+selected.Inf+' на D?'))return;result('Экспорт пакета…');const r=await api('backup',{id:selected.Id});result('Копия сохранена: '+r.path+' · файлов: '+r.files);});
async function loadBackups(){const rows=await api('backups');backups.clear().rows.add(rows).draw();result('Резервных копий: '+rows.length);}
$id('refresh-backups').onclick=()=>run(loadBackups);
$id('data').onclick=()=>run(()=>api('openData'));
function theme(v){document.documentElement.dataset.theme=v;$id('theme').textContent=v==='dark'?'Светлая тема':'Тёмная тема';localStorage.setItem('pc-drivers-theme',v);}
$id('theme').onclick=()=>theme(document.documentElement.dataset.theme==='dark'?'light':'dark');theme(localStorage.getItem('pc-drivers-theme')||'dark');
$id('quit').onclick=()=>run(async()=>{if(!confirm('Завершить PC Drivers?'))return;await api('stop');result('Приложение завершено. Окно можно закрыть');});
run(async()=>{await api('ping');result('Готово · нажмите «Проверить оборудование»');});
