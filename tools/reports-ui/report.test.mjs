import { Window } from 'happy-dom';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import assert from 'node:assert/strict';
const root = fileURLToPath(new URL('../../src/LaserficheReports.Web/wwwroot/', import.meta.url));
function setup() {
  const window = new Window({ url: 'http://localhost:5187', settings: { enableJavaScriptEvaluation: true, suppressInsecureJavaScriptEnvironmentWarning: true } });
  window.eval(readFileSync(root + 'report-format.js', 'utf8'));
  window.eval(readFileSync(root + 'report-download.js', 'utf8'));
  window.eval(readFileSync(root + 'office-export.js', 'utf8'));
  return window;
}
const message = {
  text: '# تقرير\n\n| البند | النتيجة | المرجع |\n| --- | --- | --- |\n| الحالة | تحت الإجراء | [1] |',
  generatedAt: '2026-10-03T18:00:00Z', scope: { detail: 'فُحصت 73 وثيقة؛ التقرير جزئي.' },
  sources: [{ entryId: 618, documentName: 'وثيقة أصلية', path: '\\قسم\\وثيقة', pageNumber: null,
              textSource: 'laserfiche-metadata-live', text: 'إجراء الوثيقة: تحت الإجراء' }]
};
test('downloaded HTML preserves tables, scope, question, UTC date without a sources appendix', () => {
  const window = setup();
  const exported = window.ReportsDownload.html(message, 'ما الحالة؟');
  const document = new Window().document; document.write(exported);
  assert.equal(document.querySelectorAll('table tbody tr').length, 1);
  assert.equal(document.documentElement.dir, 'rtl');
  assert(document.body.textContent.includes('ما الحالة؟'));
  assert(document.body.textContent.includes('2026-10-03T18:00:00.000Z'));
  assert(document.body.textContent.includes('التقرير جزئي'));
  assert.equal(document.querySelector('#export-source-1'),null);
  assert.equal(document.querySelector('a'),null);
  assert.equal(message.sources.length,1);
  assert.equal(document.querySelectorAll('button').length, 0);
});
test('untrusted question, model HTML, document names and source text cannot become executable markup', () => {
  const window = setup();
  const payload = '<img src="https://example.com" onerror="alert(1)"><script>alert(1)</script>';
  const exported = window.ReportsDownload.html({ ...message, text: message.text + '\n' + payload,
    sources: [{ ...message.sources[0], documentName: payload, text: payload }] }, payload);
  const document = new Window().document; document.write(exported);
  assert.equal(document.querySelectorAll('img,script,iframe,[onerror]').length, 0);
  assert(document.querySelector('meta[http-equiv="Content-Security-Policy"]').content.includes("default-src 'none'"));
  assert(document.body.textContent.includes(payload));
});
test('Markdown export omits evidence and never invents a generation timestamp for old history', () => {
  const window = setup();
  const content = window.ReportsDownload.markdown({ ...message, generatedAt: undefined }, 'السؤال');
  assert(content.includes('غير مسجل في المحادثة'));
  assert(content.includes('فُحصت 73 وثيقة'));
  assert(!content.includes('نصوص المصادر الأصلية'));
  assert(!content.includes('إجراء الوثيقة: تحت الإجراء'));
});
test('renderer handles escaped pipes and a final cell ending in a backslash, and keeps English LTR', () => {
  const window = setup();
  assert.deepEqual([...window.ReportsMarkdown.cells('| اسم \\| آخر | مسار \\\\|')], ['اسم | آخر', 'مسار \\']);
  const node = window.ReportsMarkdown.render('# Report\n| Name | Ref |\n| --- | --- |\n| Original | [1] |', 1);
  assert.equal(node.dir, 'ltr'); assert.equal(node.querySelectorAll('tbody tr').length, 1);
});
test('download builds a UTF-8 blob, supplies a safe filename and releases the object URL', () => {
  const window = setup(); let blob, filename, released;
  window.URL.createObjectURL = value => { blob = value; return 'blob:local-report'; };
  window.URL.revokeObjectURL = value => { released = value; };
  window.setTimeout = fn => fn();
  window.document.addEventListener('click', event => { filename = event.target.download; event.preventDefault(); });
  window.ReportsDownload.download(message, 'السؤال', 'html');
  assert.equal(blob.type, 'text/html;charset=utf-8'); assert(blob.size > 500);
  assert.match(filename, /^laserfiche-report-[\dTZ-]+\.html$/); assert.equal(released, 'blob:local-report');
  assert.throws(() => window.ReportsDownload.download(message, 'السؤال', 'exe'));
});
test('chat saves report time/scope and displays download for a valid empty result', async () => {
  const window = setup();
  window.document.write(readFileSync(root + 'index.html', 'utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g, ''));
  window.fetch = async url => ({ ok: true, status: 200, json: async () => url === '/api/session/status'
    ? { authenticated: true, username: 'tester', repository: 'RepoA', server: 'https://localhost' } : { ...message, sources: [] } });
  window.eval(readFileSync(root + 'app.js', 'utf8'));
  await new Promise(resolve => setTimeout(resolve, 20));
  window.document.getElementById('question').value = 'عدد الوثائق؟';
  window.document.getElementById('ask-form').dispatchEvent(new window.Event('submit', { cancelable: true }));
  await new Promise(resolve => setTimeout(resolve, 20));
  assert([...window.document.querySelectorAll('.report-actions button')].some(node => node.textContent === 'تحميل التقرير'));
  const saved = JSON.parse(window.localStorage.getItem('laserfiche-reports-chat-v2:https%3A%2F%2Flocalhost:repoa:tester'));
  assert.equal(saved[0].messages[1].generatedAt, message.generatedAt);
  assert.equal(saved[0].messages[1].scope.detail, message.scope.detail);
  await window.happyDOM.abort();
});
test('storage exhaustion keeps the received report downloadable and shows a warning', async () => {
  const window = setup();
  window.document.write(readFileSync(root + 'index.html', 'utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g, ''));
  window.fetch = async url => ({ ok: true, status: 200, json: async () => url === '/api/session/status'
    ? { authenticated: true, username: 'tester', repository: 'RepoA', server: 'https://localhost' } : { answer: message.text, ...message } });
  Object.defineProperty(window, 'localStorage', { value: {
    getItem: () => null, removeItem: () => {},
    setItem: () => { throw new Error('QuotaExceededError'); }
  } });
  window.eval(readFileSync(root + 'app.js', 'utf8'));
  await new Promise(resolve => setTimeout(resolve, 20));
  window.document.getElementById('question').value = 'اعرض الوثائق';
  window.document.getElementById('ask-form').dispatchEvent(new window.Event('submit', { cancelable: true }));
  await new Promise(resolve => setTimeout(resolve, 20));
  assert(window.document.querySelector('#messages [role="status"]').textContent.includes('حمّله'));
  assert([...window.document.querySelectorAll('.report-actions button')].some(node => node.textContent === 'تحميل التقرير'));
  assert.equal(window.document.getElementById('send').disabled, false);
  await window.happyDOM.abort();
});
test('semantic review status is visible and retained in HTML and Markdown exports', () => {
  const window = setup();
  const reviewed = { ...message, quality: { status: 'answered', quoteVerification: true,
    semanticReview: 'completed', promptVersion: 'reports-grounded-v2', modelCalls: 3 } };
  assert(window.ReportsDownload.qualityLabel(reviewed.quality).includes('مراجعة دلالية آلية'));
  assert(window.ReportsDownload.html(reviewed, 'السؤال').includes('مراجعة دلالية آلية'));
  assert(window.ReportsDownload.markdown(reviewed, 'السؤال').includes('مراجعة دلالية آلية'));
  const failed = { ...reviewed.quality, status: 'source_only', semanticReview: 'unavailable' };
  assert(window.ReportsDownload.qualityLabel(failed).includes('لم تكتمل'));
  assert.equal(window.ReportsDownload.qualityLabel(null), '');
});

test('repository histories and scan checkpoints never migrate across repositories', async () => {
  const window=setup();
  window.document.write(readFileSync(root+'index.html','utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g,''));
  window.fetch=async()=>({ok:true,status:200,json:async()=>({authenticated:true,username:'tester',repository:'RepoA',server:'https://localhost'})});
  window.localStorage.setItem('laserfiche-reports-chat-v1:tester',JSON.stringify([{id:'legacy',title:'unscoped data',messages:[]} ]));
  window.localStorage.setItem('laserfiche-reports-chat-v2:https%3A%2F%2Flocalhost:repoa:tester',JSON.stringify([{id:'a',title:'Repository A confidential',messages:[]} ]));
  window.eval(readFileSync(root+'app.js','utf8')); await new Promise(r=>setTimeout(r,20));
  assert(window.document.getElementById('history').textContent.includes('Repository A confidential'));
  assert(!window.document.getElementById('history').textContent.includes('unscoped'));
  window.eval("openSession('tester','RepoB','https://localhost','new-generation')");
  assert.equal(window.document.getElementById('history').textContent,'');
  assert.equal(window.document.getElementById('active-repository').textContent,'RepoB');
  await window.happyDOM.abort();
});

test('late report from another repository is not saved or displayed in the new session', async()=>{
  const window=setup();let resolveChat, sentHeaders;
  window.document.write(readFileSync(root+'index.html','utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g,''));
  window.fetch=async(url,options)=>url==='/api/session/status'
    ? {ok:true,status:200,json:async()=>({authenticated:true,username:'tester',repository:'RepoA',server:'https://localhost',generation:'first'})}
    : new Promise(resolve=>{resolveChat=resolve;sentHeaders=options.headers;});
  window.eval(readFileSync(root+'app.js','utf8'));await new Promise(r=>setTimeout(r,20));
  window.document.getElementById('question').value='سؤال';
  window.document.getElementById('ask-form').dispatchEvent(new window.Event('submit',{cancelable:true}));
  assert.equal(sentHeaders.get('X-Reports-Repository'),'RepoA');assert.equal(sentHeaders.get('X-Reports-Session'),'first');
  window.eval("openSession('tester','RepoB','https://localhost','second')");
  resolveChat({ok:true,status:200,json:async()=>({answer:'OLD PRIVATE RESULT',sources:[],generatedAt:message.generatedAt})});
  await new Promise(r=>setTimeout(r,20));
  assert(!window.document.getElementById('messages').textContent.includes('OLD PRIVATE RESULT'));
  assert.equal(window.localStorage.getItem('laserfiche-reports-chat-v2:https%3A%2F%2Flocalhost:repob:tester'),null);
  await window.happyDOM.abort();
});

test('Office downloads are ZIP packages and user content cannot create Excel formulas', async()=>{
  const window=setup();
  const malicious={...message,text:'| اسم | قيمة |\n| --- | --- |\n| =HYPERLINK("https://example.com") | '+ 'طويل'.repeat(300)+' |'};
  for(const format of ['docx','xlsx']){
    const blob=window.ReportsOffice[format](malicious,'السؤال');
    const bytes=new Uint8Array(await blob.arrayBuffer());assert.deepEqual([...bytes.slice(0,4)],[80,75,3,4]);
    const source=new TextDecoder().decode(bytes);assert(source.includes('السؤال'));
    if(format==='xlsx'){assert(source.includes('t="inlineStr"'));assert(!source.includes('<f>'));assert(source.includes('rightToLeft="1"'));}
    else {assert(source.includes('<w:bidi/>'));assert(source.includes('<w:tbl>'));}
  }
});

test('each table export contains only that table and retains report scope', async()=>{
  const window=setup();
  window.document.write(readFileSync(root+'index.html','utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g,''));
  window.fetch=async url=>({ok:true,status:200,json:async()=>url==='/api/session/status'
    ? {authenticated:true,username:'tester',repository:'RepoA',server:'https://localhost'}
    : {...message,answer:message.text+'\n\n| آخر | قيمة |\n| --- | --- |\n| مختلف | 42 |'}});
  window.eval(readFileSync(root+'app.js','utf8')); await new Promise(r=>setTimeout(r,20));
  window.document.getElementById('question').value='سؤال';
  window.document.getElementById('ask-form').dispatchEvent(new window.Event('submit',{cancelable:true}));
  await new Promise(r=>setTimeout(r,20));
  const buttons=window.document.querySelectorAll('.table-export-actions button');assert.equal(buttons.length,2);
  let exported;window.ReportsDownload.download=(result)=>{exported=result;};buttons[0].click();
  assert(exported.text.includes('تحت الإجراء'));assert(!exported.text.includes('مختلف'));assert.equal(exported.scope.detail,message.scope.detail);
  await window.happyDOM.abort();
});

test('multiple reports have separate rows, downloads and Web Client selections', async()=>{
  const window=setup();
  window.document.write(readFileSync(root+'index.html','utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g,''));
  const reports=[42,619].map((id,i)=>({
    title:i?'آخر إنشاء':'آخر تعديل',
    answer:'# تقرير\n\n| رقم الوثيقة | المسار | آخر تعديل | المرجع |\n| --- | --- | --- | --- |\n| '+id+' | \\قسم\\وثيقة | 2026-10-06 | [1] |',
    sources:[{...message.sources[0],entryId:id}],
    scope:{repositoryId:'RepoA',detail:'عُرضت **1** وثيقة.'},relatedEntryIds:[id]
  }));
  const requested=[],navigated=[],downloaded=[];let opened=0;
  window.open=()=>{opened++;return{closed:false,location:{replace:url=>navigated.push(url)},close(){this.closed=true;}};};
  window.fetch=async(url,options)=>({ok:true,status:200,json:async()=>{
    if(url==='/api/session/status')return{authenticated:true,username:'tester',repository:'RepoA',server:'https://localhost'};
    if(url==='/api/reports/laserfiche-links'){
      const ids=JSON.parse(options.body).entryIds;requested.push(ids);
      return{urls:['https://desktop-k1svi53/laserfiche/Browse.aspx?db=RepoA#?search='+ids[0]],documentCount:1};
    }
    return{answer:'تقريران',reports,sources:reports.flatMap(r=>r.sources),generatedAt:message.generatedAt};
  }});
  window.ReportsDownload.download=report=>downloaded.push(report);
  window.eval(readFileSync(root+'app.js','utf8'));await new Promise(r=>setTimeout(r,20));
  window.document.getElementById('question').value='آخر تعديل وآخر إنشاء';
  window.document.getElementById('ask-form').dispatchEvent(new window.Event('submit',{cancelable:true}));
  await new Promise(r=>setTimeout(r,20));
  const cards=[...window.document.querySelectorAll('.message.assistant')];assert.equal(cards.length,2);
  assert.equal(cards[0].querySelector('.label').textContent,'آخر تعديل');
  assert.equal(cards[0].querySelector('.report-scope strong').textContent,'1');
  assert.equal(cards[0].querySelector('.report-cell-path'),null);
  assert.equal(cards[0].querySelector('tbody .report-cell-date').dir,'ltr');
  for(const card of cards){
    [...card.querySelectorAll('button')].find(b=>b.textContent==='تحميل التقرير').click();
    [...card.querySelectorAll('button')].find(b=>b.textContent.startsWith('فتح وثائق')).click();
    await new Promise(r=>setTimeout(r,10));
  }
  assert.deepEqual(requested,[[42],[619]]);assert.equal(opened,1);
  assert.equal(navigated.length,2);assert(navigated.every(url=>url.startsWith('https://desktop-k1svi53/')));
  assert.deepEqual(downloaded.map(r=>r.sources[0].entryId),[42,619]);
  assert.equal(window.document.querySelectorAll('.sources').length,0);
  await window.happyDOM.abort();
});

test('chat keeps result tables and internal evidence without displaying sources', async()=>{
  const window=setup();
  window.document.write(readFileSync(root+'index.html','utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g,''));
  const sources=Array.from({length:8},(_,i)=>({...message.sources[0],entryId:100+i,documentName:`وثيقة ${i+1}`}));
  window.fetch=async url=>({ok:true,status:200,json:async()=>url==='/api/session/status'
    ?{authenticated:true,username:'tester',repository:'RepoA',server:'https://localhost'}
    :{...message,answer:'| رقم الوثيقة | عدد الصفحات | المسار | المرجع |\n| --- | --- | --- | --- |\n| 107 | 12 | \\قسم\\وثيقة | [8] |',sources}});
  window.eval(readFileSync(root+'app.js','utf8'));await new Promise(r=>setTimeout(r,20));
  window.document.getElementById('question').value='اعرض الوثائق';
  window.document.getElementById('ask-form').dispatchEvent(new window.Event('submit',{cancelable:true}));
  await new Promise(r=>setTimeout(r,20));
  assert.equal(window.document.querySelector('.sources-compact'),null);
  assert.equal(window.document.querySelector('.sources-more'),null);
  const table=window.document.querySelector('.report-table');
  assert.equal(table.querySelectorAll('th').length,3);
  assert.equal(table.querySelectorAll('tbody td')[1].textContent,'12');
  assert.equal(table.querySelector('.report-reference'),null);
  assert(table.textContent.includes('[8]'));
  const history=JSON.parse(window.localStorage.getItem('laserfiche-reports-chat-v2:https%3A%2F%2Flocalhost:repoa:tester'));
  assert.equal(history[0].messages.at(-1).sources.length,8);
  await window.happyDOM.abort();
});

test('path columns disappear from old reports without shifting cells or altering evidence',()=>{
  const window=setup();const report={...message,text:'| رقم الوثيقة | المسار | اسم الوثيقة | عدد الصفحات | المرجع |\n| --- | --- | --- | --- | --- |\n| 42 | \\قسم\\وثيقة | اسم \\| آخر | 7 | [1] |'};
  const table=window.ReportsMarkdown.render(report.text,1).querySelector('table');
  assert.deepEqual([...table.querySelectorAll('th')].map(x=>x.textContent),['رقم الوثيقة','اسم الوثيقة','عدد الصفحات','المرجع']);
  assert.deepEqual([...table.querySelectorAll('td')].map(x=>x.textContent),['42','اسم | آخر','7','[1]']);
  assert(!window.ReportsDownload.markdown(report,'سؤال').includes('| المسار |'));
  assert.equal(report.sources[0].path,message.sources[0].path);
});
test('chat deletion is scoped, persisted and a late answer cannot restore it',async()=>{
  const window=setup();window.document.write(readFileSync(root+'index.html','utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g,''));
  const key='laserfiche-reports-chat-v2:https%3A%2F%2Flocalhost:repoa:tester',other='laserfiche-reports-chat-v2:https%3A%2F%2Flocalhost:repob:tester';
  const saved=[{id:'one',title:'المحادثة الأولى',messages:[{role:'user',text:'سؤال قديم'}]},{id:'two',title:'المحادثة الثانية',messages:[]}];
  window.localStorage.setItem(key,JSON.stringify(saved));window.localStorage.setItem(other,JSON.stringify(saved));let complete;
  window.fetch=async url=>url==='/api/reports/chat'?new Promise(resolve=>{complete=resolve;}):({ok:true,status:200,json:async()=>({authenticated:true,username:'tester',repository:'RepoA',server:'https://localhost'})});
  window.eval(readFileSync(root+'app.js','utf8'));await new Promise(r=>setTimeout(r,20));
  window.document.querySelector('.history-open').click();window.document.getElementById('question').value='سؤال معلق';
  window.document.getElementById('ask-form').dispatchEvent(new window.Event('submit',{cancelable:true}));
  await new Promise(r=>setTimeout(r,5));window.document.querySelector('.history-delete').click();
  assert.equal(window.document.querySelectorAll('.history-row').length,1);
  assert.equal(JSON.parse(window.localStorage.getItem(key))[0].id,'two');assert.equal(JSON.parse(window.localStorage.getItem(other)).length,2);
  complete({ok:true,status:200,json:async()=>({answer:'نتيجة متأخرة',sources:[]})});await new Promise(r=>setTimeout(r,20));
  assert.equal(JSON.parse(window.localStorage.getItem(key)).length,1);assert(!window.document.getElementById('messages').textContent.includes('نتيجة متأخرة'));
  await window.happyDOM.abort();
});
test('Office reports include professional headers, printing without a sources appendix',async()=>{
  const window=setup();const doc=Buffer.from(await window.ReportsOffice.docx(message,'السؤال').arrayBuffer()).toString('utf8');
  assert(doc.includes('word/styles.xml'));assert(doc.includes('word/footer1.xml'));assert(doc.includes('w:tblHeader'));assert(doc.includes('NUMPAGES'));assert(!doc.includes('ملحق المصادر والأدلة'));
  assert(!doc.includes('إجراء الوثيقة: تحت الإجراء'));
  const sheet=Buffer.from(await window.ReportsOffice.xlsx(message,'السؤال').arrayBuffer()).toString('utf8');
  assert(sheet.includes('state="frozen"'));assert(sheet.includes('autoFilter'));assert(sheet.includes('_xlnm.Print_Titles'));assert(sheet.includes('orientation="landscape"'));assert(sheet.includes('FF0754CA'));assert(!sheet.includes('إجراء الوثيقة: تحت الإجراء'));
  assert(!sheet.includes('name="المصادر"'));
});
