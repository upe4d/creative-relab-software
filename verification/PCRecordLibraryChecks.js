'use strict';
// Дополнительный проверочный сценарий заменяет только тестовый драйвер в отдельной копии программы.
if(window.PCRecordVerifyControls)(async()=>{
 const rows=[],times=[];const wait=async(fn,ms=20000)=>{const end=Date.now()+ms;while(Date.now()<end){if(await fn())return;await new Promise(r=>setTimeout(r,50));}throw new Error('Истекло ожидание интерфейса');};
 const assert=(value,text)=>{if(!value)throw new Error(text);};
 const test=async(name,fn)=>{try{await fn();rows.push({test:name,status:'pass'});}catch(error){rows.push({test:name,status:'failed',error:error.message});}};
 let all=[];
 try{
  await wait(()=>document.querySelector('.status-tag').textContent==='Движок подключён');
  all=await PCBridge.request('library');
  await test('native-library-limit-1000-of-1005',async()=>{assert(all.length===1000,'Неверный лимит '+all.length);assert(!all.some(row=>row.name==='Файл 0000.png'),'Не исключён самый старый файл');});
  document.querySelector('[data-page="library"]').click();await wait(()=>document.querySelectorAll('#file-rows tr').length===50);
  await test('pagination-all-20-pages',async()=>{for(let page=0;page<20;page++){assert(document.querySelectorAll('#file-rows tr').length===50,'Не 50 строк на странице '+page);assert(document.querySelector('#file-page').textContent===(page*50+1)+'–'+(page*50+50)+' из 1000','Неверный диапазон '+page);if(page<19){const before=performance.now();document.querySelector('#next-files').click();await new Promise(requestAnimationFrame);times.push(performance.now()-before);}}assert(document.querySelector('#next-files').disabled,'Далее не отключено на последней странице');});
  await test('numeric-bytes-sort-all-1000',async()=>{document.querySelector('[data-sort="bytes"]').click();let previous=-1,count=0;const byName=new Map(all.map(row=>[row.name,row.bytes]));for(let page=0;page<20;page++){for(const row of document.querySelectorAll('#file-rows tr')){const size=byName.get(row.cells[0].textContent);assert(size>=previous,'Размеры отсортированы неверно '+size+' < '+previous);previous=size;count++;}if(page<19)document.querySelector('#next-files').click();}assert(count===1000,'Прочитаны не все строки');});
  await test('bytes-sort-descending-first-page',async()=>{document.querySelector('[data-sort="bytes"]').click();const names=[...document.querySelectorAll('#file-rows tr')].map(row=>row.cells[0].textContent),byName=new Map(all.map(row=>[row.name,row.bytes]));const sizes=names.map(name=>byName.get(name));assert(sizes.every((size,i)=>i===0||size<=sizes[i-1]),'Обратный порядок нарушен');});
  await test('case-insensitive-cyrillic-search',async()=>{const search=document.querySelector('#search');search.value='ФАЙЛ 0007';const before=performance.now();search.dispatchEvent(new Event('input'));await new Promise(requestAnimationFrame);times.push(performance.now()-before);assert(document.querySelectorAll('#file-rows tr').length===1,'Поиск не дал единственный файл');assert(document.querySelector('#file-rows tr').cells[0].textContent==='Файл 0007.png','Выбран другой файл');search.value='';search.dispatchEvent(new Event('input'));});
  await test('filter-empty-and-restore',async()=>{const filter=document.querySelector('#file-filter');filter.value='video';filter.dispatchEvent(new Event('change'));assert(!document.querySelector('#file-rows tr'),'Видео обнаружено среди PNG');assert(!document.querySelector('#library-empty').hidden,'Пустой результат скрыт');filter.value='image';filter.dispatchEvent(new Event('change'));assert(document.querySelector('#file-count').textContent==='Файлов: 1000','Не восстановлен фильтр изображений');});
  rows.push({test:'render-latency',status:'measurement',samples:times.length,minMs:Math.min(...times),maxMs:Math.max(...times),averageMs:times.reduce((a,b)=>a+b,0)/times.length,physicalWheelTested:false});
 }catch(error){rows.push({test:'library-harness',status:'failed',error:error.message});}
 await PCBridge.request('testResult',{cases:rows,scope:'1005 synthetic PNG fixtures on D; production native-app.js and production EXE unchanged'});
})();

