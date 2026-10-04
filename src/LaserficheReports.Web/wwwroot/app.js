const $ = id => document.getElementById(id);
let storeKey = '';
let sessionRepository = '';
let sessionServer = '';
let sessionGeneration = '';
let sessionEpoch = 0;
let pendingOperations = 0;
let chats = [];
let active = null;
let scan = null;
let scanning = false;
let pauseScan = false;
let scanKey = '';
let documentsPage = 1;
let hasMoreDocuments = false;
let historySaved = true;
function save() {
  try { localStorage.setItem(storeKey, JSON.stringify(chats.slice(0, 30))); historySaved = true; }
  catch { historySaved = false; }
}
function el(tag, className, value) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (value) node.textContent = value;
  return node;
}
function selectedRepository() {
  return $('repository-id').value === '__manual__' ? $('repository-manual').value.trim() : $('repository-id').value;
}
function setRepositorySelection(id) {
  const select = $('repository-id');
  if (id && !Array.from(select.options).some(option => option.value === id)) {
    const option = el('option', '', id); option.value = id;
    select.insertBefore(option, select.lastElementChild);
  }
  select.value = id || '';
  $('repository-manual').classList.add('hidden');
  $('repository-manual').required = false;
}
$('repository-id').onchange = () => {
  const manual = $('repository-id').value === '__manual__';
  $('repository-manual').classList.toggle('hidden', !manual);
  $('repository-manual').required = manual;
  if (manual) $('repository-manual').focus();
};
async function api(url, options) {
  const epoch = sessionEpoch;
  const headers = new Headers(options?.headers || {});
  if (sessionRepository) headers.set('X-Reports-Repository', sessionRepository);
  if (sessionGeneration) headers.set('X-Reports-Session', sessionGeneration);
  const response = await fetch(url, { ...options, headers });
  const body = await response.json().catch(() => ({}));
  if (epoch !== sessionEpoch) throw new Error('تغيّرت جلسة المستودع؛ تم تجاهل نتيجة الطلب السابق.');
  if ([401, 409].includes(response.status) && url !== '/api/session/login') $('login-layer').classList.remove('hidden');
  if (!response.ok) {
    const error = new Error(body.detail || body.message || body.error ||
      `HTTP ${response.status} — راجع سجل التطبيق لمعرفة السبب`);
    error.status = response.status;
    error.diagnosticId = body.diagnosticId;
    throw error;
  }
  return body;
}
function openSession(username, repository, server, generation) {
  sessionGeneration = generation || '';
  sessionEpoch++;
  sessionRepository = repository || '';
  sessionServer = server || '';
  $('active-repository').textContent = sessionRepository || 'غير محدد';
  setRepositorySelection(sessionRepository);
  documentsPage = 1;
  $('documents').replaceChildren();
  $('statuses').replaceChildren();
  $('ingest-result').textContent = '';
  const identity = [sessionServer, sessionRepository, username].map(value => encodeURIComponent(value.toLowerCase())).join(':');
  historySaved = true;
  storeKey = `laserfiche-reports-chat-v2:${identity}`;
  scanKey = `laserfiche-reports-scan-v2:${identity}`;
  try { chats = JSON.parse(localStorage.getItem(storeKey) || '[]'); }
  catch { chats = []; }
  try { scan = JSON.parse(localStorage.getItem(scanKey) || 'null'); }
  catch { scan = null; }
  active = null;
  $('login-layer').classList.add('hidden');
  renderHistory();
  renderMessages();
  renderScan();
}
$('login-form').onsubmit = async event => {
  event.preventDefault();
  $('login-submit').disabled = true;
  $('login-error').textContent = '';
  $('login-error').classList.remove('success');
  try {
    const username = $('username').value.trim();
    const session = await api('/api/session/login', { method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password: $('password').value, repositoryId: selectedRepository() }) });
    $('password').value = '';
    openSession(session.username || username, session.repository, session.server, session.generation);
  } catch (error) { $('login-error').textContent = `تعذر تسجيل الدخول: ${error.message}`; }
  finally { $('login-submit').disabled = false; }
};
$('logout').onclick = async () => {
  if (scanning || pendingOperations) return;
  try { await api('/api/session/logout', { method: 'POST' }); }
  finally {
    sessionEpoch++;
    chats = []; active = null;
    pauseScan = true;
    scan = null;
    $('login-layer').classList.remove('hidden');
    renderHistory(); renderMessages();
  }
};
function renderHistory() {
  $('history').replaceChildren();
  chats.forEach(chat => {
    const button = el('button', chat.id === active ? 'selected' : '', chat.title);
    button.onclick = () => { active = chat.id; renderHistory(); renderMessages(); showTab('chat'); };
    $('history').append(button);
  });
}
function renderMessages() {
  const container = $('messages');
  container.replaceChildren();
  const chat = chats.find(c => c.id === active);
  if (!chat || !chat.messages.length) {
    const welcome = el('div', 'welcome');
    welcome.innerHTML = '<div class="welcome-icon">✦</div><h2>ما التقرير الذي تريد إعداده؟</h2><p>اسأل عن المستودع أو حدد رقم وثيقة. تظهر النتائج كتقرير وجداول مع مصادرها.</p>';
    const suggestions = el('div', 'suggestions');
    for (const question of ['ماهي الوثائق الموجود فيها إجراء الوثيقة يساوي تحت الاجراء', 'اعرض جميع الوثائق في المستودع', 'لخص أهم النقاط في الوثيقة 618 كتقرير']) {
      const button = el('button', '', question);
      button.onclick = () => { $('question').value = question; $('question').focus(); };
      suggestions.append(button);
    }
    welcome.append(suggestions);
    container.append(welcome);
  } else {
    chat.messages.forEach((message, messageIndex) => {
      const item = el('div', `message ${message.role}`);
      item.append(el('div', 'label', message.role === 'user' ? 'أنت' : 'تقارير ليزرفيش الذكية'));
      const sourcePrefix = `source-${chat.id}-${messageIndex}`;
      const bubble = el('div', 'bubble');
      if (message.role === 'assistant') {
        bubble.append(ReportsMarkdown.render(message.text || '', message.sources?.length || 0, sourcePrefix));
        const reportQuestion = chat.messages.slice(0, messageIndex).reverse().find(previous => previous.role === 'user')?.text;
        bubble.querySelectorAll('.report-table-wrap').forEach((wrap, tableIndex) => {
          const tableActions = el('div', 'report-actions table-export-actions');
          const tableFormat = el('select', 'report-format'); tableFormat.setAttribute('aria-label', `صيغة تنزيل الجدول ${tableIndex + 1}`);
          for (const [value,label] of [['docx','Word'],['xlsx','Excel'],['pdf','PDF (حفظ عبر الطباعة)']]) {
            const option=el('option','',label); option.value=value; tableFormat.append(option);
          }
          const tableDownload=el('button','','تنزيل هذا الجدول'); tableDownload.type='button';
          tableDownload.onclick=()=>{
            const rows=[...wrap.querySelectorAll('tr')].map(row=>[...row.children].map(cell=>cell.textContent.replace(/\\/g,'\\\\').replace(/\|/g,'\\|').replace(/\r?\n/g,' ')));
            const text=rows.length ? '| '+rows[0].join(' | ')+' |\n| '+rows[0].map(()=> '---').join(' | ')+' |\n'+rows.slice(1).map(row=>'| '+row.join(' | ')+' |').join('\n') : '';
            try { ReportsDownload.download({...message,text},reportQuestion,tableFormat.value); }
            catch(error) { tableDownload.textContent=error.message; }
          };
          tableActions.append(tableDownload,tableFormat); wrap.after(tableActions);
        });
        const qualityLabel = ReportsDownload.qualityLabel(message.quality);
        if (qualityLabel) bubble.prepend(el('div', 'report-scope report-qual…2760 tokens truncated…u0627لاتصال المحلي شغّل scripts\\configure-database.ps1 من مجلد المشروع، ثم أعد تشغيل التطبيق وحدّث الحالة.'));
    }
  }));
}
async function loadDocuments() {
  const epoch = sessionEpoch;
  $('documents').replaceChildren(el('div', 'empty', 'جاري تحميل الوثائق...'));
  try {
    const search = $('document-search').value.trim();
    const result = await api(`/api/reports/documents?page=${documentsPage}&search=${encodeURIComponent(search)}`);
    $('documents').replaceChildren();
    hasMoreDocuments = result.hasMore;
    $('previous-docs').disabled = documentsPage === 1;
    $('next-docs').disabled = !hasMoreDocuments;
    $('documents-page').textContent = `صفحة ${documentsPage}`;
    if (!result.items.length) $('documents').append(el('div', 'empty', 'لا توجد وثائق مفهرسة متاحة لهذه الصفحة أو البحث.'));
    result.items.forEach(doc => {
      const row = el('div', 'doc'), detail = el('div');
      detail.append(el('strong', '', `${doc.name} · #${doc.entryId}`));
      detail.append(el('small', '', doc.path));
      const status = doc.status === 'metadata-indexed' ? 'بيانات مفهرسة' : doc.status === 'content-indexed' ? 'محتوى وبيانات مفهرسة' : doc.status;
      row.append(detail, el('span', 'tag', `${doc.chunkCount} مقطع · ${status}`));
      $('documents').append(row);
    });
  } catch (error) { if (epoch === sessionEpoch) $('documents').replaceChildren(el('div', 'empty', error.message)); }
}
$('refresh').onclick = refreshStatuses;
$('reload-docs').onclick = loadDocuments;
$('search-docs').onclick = () => { documentsPage = 1; loadDocuments(); };
$('document-search').onkeydown = event => {
  if (event.key === 'Enter') { event.preventDefault(); documentsPage = 1; loadDocuments(); }
};
$('previous-docs').onclick = () => { if (documentsPage > 1) { documentsPage--; loadDocuments(); } };
$('next-docs').onclick = () => { if (hasMoreDocuments) { documentsPage++; loadDocuments(); } };
$('ingest-form').onsubmit = async event => {
  event.preventDefault();
  pendingOperations++;
  $('ingest').disabled = true;
  $('ingest-result').textContent = 'جاري فهرسة الوثيقة، قد يستغرق ذلك عدة دقائق...';
  try {
    const result = await api(`/api/ingestion/laserfiche/${$('entry-id').value}`, { method: 'POST' });
    $('ingest-result').textContent = `تمت المعالجة: ${result.ingestionStatus}، المقاطع: ${result.chunkCount}`;
    loadDocuments();
  } catch (error) { $('ingest-result').textContent = `فشلت الفهرسة: ${error.message}`; }
  finally { pendingOperations--; $('ingest').disabled = false; }
};

function newScan() {
  return { folders: [0], documents: [], seenFolders: [], seenDocuments: [],
    foldersDone: 0, documentsDone: 0, skipped: 0, chunks: 0, failed: [], repositoryId: sessionRepository, current: '', notice: '' };
}
function saveScan() {
  try { localStorage.setItem(scanKey, JSON.stringify(scan)); }
  catch { $('scan-progress').textContent = 'تعذر حفظ نقطة الاستئناف في المتصفح. اترك الصفحة مفتوحة حتى تنتهي العملية.'; }
}
function renderScan() {
  $('scan-start').disabled = scanning;
  $('scan-reset').disabled = scanning;
  $('logout').disabled = scanning;
  $('scan-start').textContent = scan?.folders?.length || scan?.documents?.length || scan?.failed?.length
    ? 'استئناف الفهرسة' : 'بدء الفهرسة الشاملة';
  $('scan-pause').disabled = !scanning;
  if (!scan) {
    $('scan-progress').textContent = 'لم تبدأ الفهرسة الشاملة بعد.';
    $('scan-errors').replaceChildren();
    return;
  }
  $('scan-progress').textContent =
    `${scanning ? 'جارية' : 'متوقفة'} · ${scan.foldersDone} مجلد · ${scan.documentsDone} وثيقة · ${scan.skipped || 0} دون تغيير · ` +
    `${scan.chunks} مقطع · ${scan.folders.length} مجلد و${scan.documents.length} وثيقة في الانتظار` +
    (scan.current ? ` · الآن: ${scan.current}` : '') +
    (scan.notice ? ` · ${scan.notice}` : '');
  $('scan-errors').replaceChildren();
  if (scan.failed.length) {
    $('scan-errors').append(el('strong', '', `${scan.failed.length} إخفاق؛ يمكنك الاستئناف لإعادة المحاولة:`));
    scan.failed.slice(-10).forEach(item => $('scan-errors').append(el('div', '',
      `${item.type === 'folder' ? 'مجلد' : 'وثيقة'} #${item.id}: ${item.message}`)));
  }
}
$('scan-pause').onclick = () => { pauseScan = true; $('scan-pause').disabled = true; };
$('scan-reset').onclick = () => {
  if (scanning) return;
  scan = null;
  localStorage.removeItem(scanKey);
  renderScan();
};
$('scan-start').onclick = async () => {
  if (scanning || pendingOperations) return;
  if (!scan || (!scan.folders.length && !scan.documents.length && !scan.failed.length)) scan = newScan();
  else if (!scan.folders.length && !scan.documents.length && scan.failed.length) {
    scan.failed.forEach(item => (item.type === 'folder' ? scan.folders : scan.documents).push(item.id));
    scan.failed = [];
  }
  pauseScan = false;
  scanning = true;
  scan.notice = '';
  renderScan();
  const seenFolders = new Set(scan.seenFolders);
  const seenDocuments = new Set(scan.seenDocuments);
  try {
    while (!pauseScan && (scan.folders.length || scan.documents.length)) {
      if (scan.documents.length) {
        const id = scan.documents[0];
        scan.current = `فهرسة الوثيقة ${id}`; renderScan();
        try {
          const result = await api(`/api/ingestion/laserfiche/${id}`, { method: 'POST' });
          scan.chunks += result.chunkCount || 0;
          scan.documentsDone++;
          if (result.wasSkipped) scan.skipped = (scan.skipped || 0) + 1;
        } catch (error) {
          if (error.status === 401 || error.status === 409) { pauseScan = true; scan.notice = error.message; break; }
          if (error.status === 503) {
            pauseScan = true;
            scan.notice = `الخدمة المطلوبة غير متاحة: ${error.message}. عالج الاتصال ثم استأنف.`;
            break;
          }
          if (error.status === 500 || error.status === 502) {
            pauseScan = true;
            scan.notice = `توقفت الفهرسة عند الوثيقة ${id}: ${error.message}${error.diagnosticId ? ` (رمز التشخيص ${error.diagnosticId})` : ''}. راجع سجل التطبيق ثم استأنف.`;
            break;
          }
          scan.failed.push({ type: 'document', id, message: error.message });
        }
        scan.documents.shift();
      } else {
        const id = scan.folders[0];
        scan.current = `فحص المجلد ${id || 'الجذر'}`; renderScan();
        try {
          const result = await api(`/api/reports/repository/folders/${id}/children`);
          if (scan.repositoryId && scan.repositoryId.toLowerCase() !== result.repositoryId.toLowerCase()) {
            pauseScan = true;
            scan.notice = 'تغيّر المستودع؛ اضغط فحص جديد لبدء فهرسة المستودع الحالي.';
            saveScan(); renderScan();
            break;
          }
          scan.repositoryId = result.repositoryId;
          for (const folder of result.folders) {
            if (!seenFolders.has(folder.id)) { seenFolders.add(folder.id); scan.folders.push(folder.id); }
          }
          for (const doc of result.documents) {
            if (!seenDocuments.has(doc.id)) { seenDocuments.add(doc.id); scan.documents.push(doc.id); }
          }
          scan.foldersDone++;
        } catch (error) {
          if (error.status === 401 || error.status === 409) { pauseScan = true; scan.notice = error.message; break; }
          if (error.status >= 500) {
            pauseScan = true;
            scan.notice = `تعذر فحص المجلد ${id}: ${error.message}. راجع سجل التطبيق ثم استأنف.`;
            break;
          }
          scan.failed.push({ type: 'folder', id, message: error.message });
        }
        scan.folders.shift();
      }
      scan.seenFolders = [...seenFolders];
      scan.seenDocuments = [...seenDocuments];
      saveScan(); renderScan();
    }
  } finally {
    scanning = false;
    scan.current = '';
    saveScan(); renderScan();
    loadDocuments();
  }
};
api('/api/session/status').then(session => {
  setRepositorySelection(session.repository || '');
  if (session.authenticated && session.username) openSession(session.username, session.repository, session.server, session.generation);
  else $('login-layer').classList.remove('hidden');
}).catch(() => $('login-layer').classList.remove('hidden'));

$('switch-repository').onclick = () => {
  if (scanning || pendingOperations) { $('active-repository').title='أوقف الفهرسة وانتظر اكتمال الطلب قبل التبديل'; return; }
  $('login-layer').classList.remove('hidden');
  $('password').value=''; $('repository-id').focus();
};
$('discover-repositories').onclick = async () => {
  $('discover-repositories').disabled=true;
  $('login-error').classList.remove('success');
  try {
    const repositories=await api('/api/session/repositories',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify({username:$('username').value.trim(),password:$('password').value,repositoryId:selectedRepository()})});
    const previous = selectedRepository();
    const select = $('repository-id');
    select.replaceChildren();
    const prompt = el('option', '', 'اختر المستودع'); prompt.value = ''; select.append(prompt);
    repositories.forEach(repo=>{const option=el('option','',repo.name || repo.id);option.value=repo.id;select.append(option);});
    const manual = el('option', '', 'إدخال مستودع آخر…'); manual.value = '__manual__'; select.append(manual);
    setRepositorySelection(previous);
    $('login-error').classList.add('success');
    $('login-error').textContent=`تم العثور على ${repositories.length} مستودع. اختر من خانة المستودع ثم سجل الدخول.`;
  } catch(error) { $('login-error').textContent=error.message; }
  finally { $('discover-repositories').disabled=false; }
};
