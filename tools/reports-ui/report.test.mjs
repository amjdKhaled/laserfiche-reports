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
test('Web Client button mounts next to Dashboard, encodes Arabic repository and avoids duplicates', async () => {
  const window = new Window({ url: 'https://lf.local/laserfiche/Browse.aspx', settings: { enableJavaScriptEvaluation: true, suppressInsecureJavaScriptEnvironmentWarning: true } });
  window.document.write('<input id="WebAccessRepositoryName" value="مستودع & اختبار"><ul id="rightNavbar"><li class="nav-item"><a class="nav-link" id="dashboard" href="http://localhost:5000">Dashboard</a></li><li>ADMIN</li></ul>');
  const script = readFileSync(new URL('../../integrations/laserfiche-webclient/lf-reports-button.js', import.meta.url), 'utf8')
    .replace('__LF_REPORTS_URL_JSON__', JSON.stringify('http://localhost:5187/'));
  window.eval(script);
  window.document.dispatchEvent(new window.Event('DOMContentLoaded'));
  const anchor = window.document.getElementById('lf-smart-reports-button');
  assert(anchor);
  assert.equal(anchor.className, 'nav-link');
  assert.equal(anchor.style.color, '#ffffff');
  assert.equal(anchor.style.getPropertyPriority('color'), 'important');
  assert.equal(anchor.parentNode.nextElementSibling.querySelector('a').id, 'dashboard');
  assert.equal(new URL(anchor.href).searchParams.get('repository'), 'مستودع & اختبار');
  assert.equal(new URL(anchor.href).searchParams.get('source'), 'webclient');
  assert.equal(anchor.target, '_blank');
  assert(anchor.rel.includes('noopener'));
  window.eval(script);
  assert.equal(window.document.querySelectorAll('#lf-smart-reports-button').length, 1);
  window.document.getElementById('WebAccessRepositoryName').value = 'OtherRepo';
  anchor.addEventListener('click', event => event.preventDefault());
  anchor.dispatchEvent(new window.MouseEvent('click', { cancelable: true }));
  assert.equal(new URL(anchor.href).searchParams.get('repository'), 'OtherRepo');
  window.document.getElementById('rightNavbar').innerHTML = '<li><a id="dashboard">Dashboard</a></li>';
  await new Promise(resolve => setTimeout(resolve, 150));
  assert.equal(window.document.querySelectorAll('#lf-smart-reports-button').length, 1);
  await window.happyDOM.abort();
});
test('Web Client launch preselects its repository and preserves normal login controls', async () => {
  const window = setup();
  window.location.href = 'http://localhost:5187/?repository=RepoB&source=webclient';
  window.document.write(readFileSync(root + 'index.html', 'utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g, ''));
  window.fetch = async () => ({ ok: true, status: 200, json: async () => ({authenticated:true,username:'tester',repository:'RepoA'}) });
  window.eval(readFileSync(root + 'app.js', 'utf8'));
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.equal(window.document.getElementById('repository-id').value, 'RepoB');
  assert(!window.document.getElementById('repository-id').disabled);
  assert(!window.document.getElementById('repository-id').classList.contains('hidden'));
  assert(!window.document.getElementById('discover-repositories').classList.contains('hidden'));
  assert(!window.document.getElementById('switch-repository').disabled);
  assert(!window.document.getElementById('login-layer').classList.contains('hidden'));
  assert.notEqual(window.document.getElementById('active-repository').textContent, 'RepoA');
  await window.happyDOM.abort();
});
test('Web Client login submits the chosen repository through the restored normal form', async () => {
  const window = setup(); let loginBody;
  window.location.href = 'http://localhost:5187/?repository=RepoB&source=webclient';
  window.document.write(readFileSync(root + 'index.html', 'utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g, ''));
  window.fetch = async (url, options) => {
    if (url === '/api/session/login') {
      loginBody = JSON.parse(options.body);
      return { ok:true, status:200, json:async()=>({ authenticated:true, username:'tester', repository:loginBody.repositoryId }) };
    }
    return { ok:true, status:200, json:async()=>({ authenticated:false, repository:'ConfiguredRepo' }) };
  };
  window.eval(readFileSync(root + 'app.js', 'utf8'));
  await new Promise(resolve => setTimeout(resolve, 20));
  window.document.getElementById('repository-id').value = '__manual__';
  window.document.getElementById('repository-id').dispatchEvent(new window.Event('change'));
  window.document.getElementById('repository-manual').value = 'OtherRepo';
  window.document.getElementById('username').value = 'tester';
  window.document.getElementById('password').value = 'password';
  window.document.getElementById('login-form').dispatchEvent(new window.Event('submit', { cancelable:true }));
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.equal(loginBody.repositoryId, 'OtherRepo');
  assert.equal(window.document.getElementById('active-repository').textContent, 'OtherRepo');
  assert(window.document.getElementById('login-layer').classList.contains('hidden'));
  assert(!window.document.getElementById('switch-repository').disabled);
  await window.happyDOM.abort();
});
test('deleting a chat persists deletion, clears active messages and leaves other repositories untouched', async () => {
  const window = setup();
  window.document.write(readFileSync(root + 'index.html', 'utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g, ''));
  window.fetch = async () => ({ ok:true, status:200, json:async()=>({ authenticated:true,username:'tester',repository:'RepoA' }) });
  window.eval(readFileSync(root + 'app.js', 'utf8'));
  await new Promise(resolve => setTimeout(resolve, 20));
  window.localStorage.setItem('laserfiche-reports-chat-v2::repoa:tester', JSON.stringify([
    {id:'a',title:'محادثة أولى',messages:[{role:'user',text:'نص خاص'}]},
    {id:'b',title:'محادثة ثانية',messages:[]} ]));
  window.eval("openSession('tester','RepoA','','');");
  window.document.querySelector('.history-open').click();
  const otherKey = 'laserfiche-reports-chat-v2::repob:tester';
  window.localStorage.setItem(otherKey, JSON.stringify([{id:'other',title:'Other repository'}]));
  window.document.querySelector('.history-delete').click();
  assert.equal(window.document.querySelectorAll('.history-row').length, 1);
  assert(!window.document.getElementById('messages').textContent.includes('نص خاص'));
  assert.equal(JSON.parse(window.localStorage.getItem(otherKey))[0].id, 'other');
  window.eval("openSession('tester','RepoA','','');");
  assert.equal(window.document.querySelectorAll('.history-row').length, 1);
  assert(window.document.getElementById('history').textContent.includes('محادثة ثانية'));
  window.document.querySelector('.history-open').click();
  let resolveChat;
  window.fetch = async () => await new Promise(resolve => { resolveChat = resolve; });
  window.document.getElementById('question').value = 'سؤال جديد';
  window.document.getElementById('ask-form').dispatchEvent(new window.Event('submit', { cancelable:true }));
  const pendingDelete = window.document.querySelector('.history-delete');
  assert(pendingDelete.disabled);
  pendingDelete.click();
  assert.equal(window.document.querySelectorAll('.history-row').length, 1);
  resolveChat({ ok:true, status:200, json:async()=>({ answer:'تقرير جديد', sources:[] }) });
  await new Promise(resolve => setTimeout(resolve, 20));
  assert(!window.document.querySelector('.history-delete').disabled);
  await window.happyDOM.abort();
});
const message = {
  text: '# تقرير\n\n| البند | النتيجة | المرجع |\n| --- | --- | --- |\n| الحالة | تحت الإجراء | [1] |',
  generatedAt: '2026-10-03T18:00:00Z', scope: { detail: 'فُحصت 73 وثيقة؛ التقرير جزئي.' },
  sources: [{ entryId: 618, documentName: 'وثيقة أصلية', path: '\\قسم\\وثيقة', pageNumber: null,
              textSource: 'laserfiche-metadata-live', text: 'إجراء الوثيقة: تحت الإجراء' }]
};
test('downloaded HTML preserves tables, scope, question, UTC date and original evidence', () => {
  const window = setup();
  const exported = window.ReportsDownload.html(message, 'ما الحالة؟');
  const document = new Window().document; document.write(exported);
  assert.equal(document.querySelectorAll('table tbody tr').length, 1);
  assert.equal(document.documentElement.dir, 'rtl');
  assert(document.body.textContent.includes('ما الحالة؟'));
  assert(document.body.textContent.includes('2026-10-03T18:00:00.000Z'));
  assert(document.body.textContent.includes('التقرير جزئي'));
  assert(document.querySelector('#export-source-1 pre').textContent.includes('إجراء الوثيقة: تحت الإجراء'));
  assert.equal(document.querySelector('a').getAttribute('href'), '#export-source-1');
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
test('Markdown export includes evidence and never invents a generation timestamp for old history', () => {
  const window = setup();
  const content = window.ReportsDownload.markdown({ ...message, generatedAt: undefined }, 'السؤال');
  assert(content.includes('غير مسجل في المحادثة'));
  assert(content.includes('فُحصت 73 وثيقة'));
  assert(content.includes('إجراء الوثيقة: تحت الإجراء'));
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
    : url === '/api/reports/chat/stream' ? new Promise(resolve=>{resolveChat=resolve;sentHeaders=options.headers;}) : {ok:true,status:200,json:async()=>({isConnected:true})};
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


test('repository select discovers IDs and submits an explicit manual repository', async()=>{
  const window=setup();let loginBody;
  window.document.write(readFileSync(root+'index.html','utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g,''));
  window.fetch=async(url,options)=>{
    let body={};
    if(url==='/api/session/status') body={authenticated:false,repository:'ConfiguredRepo'};
    if(url==='/api/session/repositories') body=[{id:'RepoA',name:'اسم المستودع الطويل جدًا'},{id:'RepoB',name:'مستودع آخر'}];
    if(url==='/api/session/login'){loginBody=JSON.parse(options.body);body={username:'tester',repository:loginBody.repositoryId};}
    return {ok:true,status:200,json:async()=>body};
  };
  window.eval(readFileSync(root+'app.js','utf8'));await new Promise(r=>setTimeout(r,20));
  const select=window.document.getElementById('repository-id');
  assert.equal(select.tagName,'SELECT');assert.equal(select.value,'ConfiguredRepo');
  window.document.getElementById('discover-repositories').click();await new Promise(r=>setTimeout(r,20));
  assert([...select.options].some(option=>option.value==='RepoA' && option.textContent==='اسم المستودع الطويل جدًا'));
  select.value='__manual__';select.dispatchEvent(new window.Event('change'));
  const manual=window.document.getElementById('repository-manual');assert(manual.required);assert(!manual.classList.contains('hidden'));
  manual.value='CustomRepo';window.document.getElementById('username').value='tester';window.document.getElementById('password').value='password';
  window.document.getElementById('login-form').dispatchEvent(new window.Event('submit',{cancelable:true}));await new Promise(r=>setTimeout(r,20));
  assert.equal(loginBody.repositoryId,'CustomRepo');
  assert.equal(window.document.title,'تقارير ليزرفيش الذكية');
  await window.happyDOM.abort();
});
test('live status checks only Laserfiche and AI and indexing controls are removed', async()=>{
  const window=setup();window.document.write(readFileSync(root+'index.html','utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g,''));
  const called=[];
  window.fetch=async url=>{called.push(url);return {ok:true,status:200,json:async()=>({authenticated:true,username:'tester',repository:'RepoA',isConnected:true})};};
  window.eval(readFileSync(root+'app.js','utf8'));await new Promise(r=>setTimeout(r,20));
  await window.eval('refreshStatuses()');
  assert(called.includes('/api/laserfiche/status'));assert(called.includes('/api/ai/status'));
  assert(!called.some(url=>/database|embeddings|ocr|graph|ingestion/.test(url)));
  assert.equal(window.document.getElementById('scan-start'),null);
  assert.equal(window.document.getElementById('ingest-form'),null);
  await window.happyDOM.abort();
});

test('top tabs respond while startup status is pending and late status cannot reopen login', async () => {
  const window = setup(); let finishStatus;
  window.document.write(readFileSync(root + 'index.html', 'utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g, ''));
  window.fetch = async url => url === '/api/session/status'
    ? await new Promise(resolve => { finishStatus = resolve; })
    : { ok:true, status:200, json:async()=>({username:'tester',repository:'RepoA',documents:[]}) };
  window.eval(readFileSync(root + 'app.js', 'utf8'));
  window.document.getElementById('tab-docs').click();
  assert(!window.document.getElementById('docs-view').classList.contains('hidden'));
  window.document.getElementById('tab-chat').click();
  assert(!window.document.getElementById('chat-view').classList.contains('hidden'));
  window.document.getElementById('username').value = 'tester';
  window.document.getElementById('password').value = 'password';
  window.document.getElementById('login-form').dispatchEvent(new window.Event('submit', {cancelable:true}));
  await new Promise(resolve => setTimeout(resolve, 20));
  finishStatus({ok:true,status:200,json:async()=>({authenticated:false})});
  await new Promise(resolve => setTimeout(resolve, 20));
  assert(window.document.getElementById('login-layer').classList.contains('hidden'));
  assert.equal(window.document.getElementById('active-repository').textContent, 'RepoA');
  await window.happyDOM.abort();
});
test('AI stream is the main answer and Laserfiche actions stay above it without cancellation UI', async () => {
  const window = setup();
  window.document.write(readFileSync(root + 'index.html', 'utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g, ''));
  const events = [
    ['status', {message:'جارٍ توليد إجابة الذكاء الاصطناعي...'}],
    ['result', {answer:'',sources:[],relatedEntryIds:[608],scope:{repositoryId:'RepoA',detail:'الوثائق الحالية'},generatedAt:message.generatedAt}],
    ['delta', {text:'وجدت وثيقة مطابقة لسؤالك: وثيقة الموظف 608.'}]
  ];
  let linkRequest;
  const opened = {opener:null,document:{body:{}},location:{replace:url=>{opened.url=url;}},close:()=>{}};
  window.open = () => opened;
  window.fetch = async (url, options) => {
    if (url === '/api/session/status') return {ok:true,status:200,json:async()=>({authenticated:true,username:'tester',repository:'RepoA',server:'https://localhost'})};
    if (url === '/api/reports/chat/stream') {
      let read = false;
      return {ok:true,body:{getReader:()=>({read:async()=>read ? {done:true} : (read=true,{done:false,value:new TextEncoder().encode(events.map(([kind,data])=>`event: ${kind}\ndata: ${JSON.stringify(data)}\n\n`).join(''))}),releaseLock:()=>{}})}};
    }
    if (url === '/api/reports/laserfiche-links') {
      linkRequest=JSON.parse(options.body);
      return {ok:true,status:200,json:async()=>({urls:['https://lf.local/laserfiche/Browse.aspx?db=RepoA#search=608'],documentCount:1})};
    }
    return {ok:true,status:200,json:async()=>({isConnected:true})};
  };
  window.eval(readFileSync(root + 'app.js', 'utf8'));
  await new Promise(resolve=>setTimeout(resolve,20));
  assert.equal(window.document.getElementById('cancel-question'),null);
  window.document.getElementById('question').value='اعرض وثائق الموظفين';
  window.document.getElementById('ask-form').dispatchEvent(new window.Event('submit',{cancelable:true}));
  await new Promise(resolve=>setTimeout(resolve,20));
  const bubble=window.document.querySelector('.message.assistant .bubble');
  assert.equal(bubble.firstElementChild.className,'report-actions');
  assert(bubble.querySelector('.report-body').textContent.includes('وجدت وثيقة مطابقة'));
  assert(!bubble.textContent.includes('تقرير Laserfiche'));
  const open=bubble.querySelector('.report-actions button');
  assert(open.textContent.includes('فتح وثائق التقرير'));
  assert.equal(open.disabled,false);
  open.click();
  await new Promise(resolve=>setTimeout(resolve,20));
  assert.deepEqual(linkRequest,{repositoryId:'RepoA',entryIds:[608]});
  assert.equal(opened.url,'https://lf.local/laserfiche/Browse.aspx?db=RepoA#search=608');
  assert.equal(window.document.getElementById('send').disabled,false);
  await window.happyDOM.abort();
});
