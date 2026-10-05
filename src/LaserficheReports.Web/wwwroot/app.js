const $ = id => document.getElementById(id);
// Web Client links preselect a repository; the normal login form remains available.
const launchRepository = new URLSearchParams(window.location.search).get('repository')?.trim() || '';
const pendingChats = new Set();
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
let currentChatRequest = null;
let statusLoadVersion = 0;
let documentLoadVersion = 0;
let indexedAvailable = null;
let scanCheckpointSaved = true;
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
  const { timeoutMs, ...requestOptions } = options || {};
  const duration = timeoutMs ?? (url === '/api/reports/chat' ? 14400000 :
    url.startsWith('/api/ingestion/') ? 1800000 : url.startsWith('/api/reports/documents') ? 60000 :
    url.includes('/folders/') ? 120000 : url === '/api/session/login' || url === '/api/session/repositories' ? 120000 : 15000);
  const timeout = AbortSignal.timeout(duration);
  const signal = requestOptions.signal ? AbortSignal.any([requestOptions.signal, timeout]) : timeout;
  let response, body;
  try {
    response = await fetch(url, { ...requestOptions, signal, headers });
    try { body = await response.json(); }
    catch (error) {
      if (signal.aborted) throw error;
      if (response.ok) throw new Error('تعذر قراءة رد الخادم. أعد المحاولة وتحقق من سجل التطبيق.');
      body = {};
    }
  } catch (error) {
    if (timeout.aborted) throw new Error('انتهت مهلة تحميل المعلومات. يمكنك متابعة استخدام الواجهة والمحاولة مجددًا.');
    if (requestOptions.signal?.aborted) throw new Error('تم إلغاء الطلب. يمكنك إعادة السؤال.');
    throw error;
  }
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
  indexedAvailable = null;
  scanCheckpointSaved = true;
  $('data-state').textContent = 'جارٍ تحميل حالة الخدمات والوثائق...';
  $('request-state').classList.add('hidden');
  const identity = [sessionServer, sessionRepository, username].map(value => encodeURIComponent(value.toLowerCase())).join(':');
  historySaved = true;
  storeKey = `laserfiche-reports-chat-v2:${identity}`;
  scanKey = `laserfiche-reports-scan-v2:${identity}`;
  try { chats = JSON.parse(localStorage.getItem(storeKey) || '[]'); }
  catch { chats = []; }
  if (!Array.isArray(chats)) chats = [];
  chats.forEach(chat => chat.messages?.forEach(message => {
    if (message.pending) { message.pending = false; message.text = 'انقطع انتظار التقرير عند إغلاق الصفحة. أعد إرسال السؤال للحصول على نتيجة جديدة.'; }
  }));
  try { scan = JSON.parse(localStorage.getItem(scanKey) || 'null'); }
  catch { scan = null; }
  if (scan && !['folders', 'documents', 'seenFolders', 'seenDocuments', 'failed'].every(key => Array.isArray(scan[key]))) scan = null;
  active = null;
  $('login-layer').classList.add('hidden');
  renderHistory();
  renderMessages();
  renderScan();
  refreshStatuses();
  loadDocuments();
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
  catch (error) { $('login-error').textContent = `تعذر إنهاء جلسة الخادم: ${error.message}`; }
  finally {
    sessionEpoch++;
    sessionRepository = ''; sessionServer = ''; sessionGeneration = '';
    $('active-repository').textContent = '—';
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
    const row = el('div', chat.id === active ? 'history-row selected' : 'history-row');
    const button = el('button', chat.id === active ? 'history-open selected' : 'history-open', chat.title);
    button.type = 'button';
    button.onclick = () => { active = chat.id; renderHistory(); renderMessages(); showTab('chat'); };
    const remove = el('button', 'history-delete', '×');
    remove.type = 'button';
    remove.title = 'حذف المحادثة';
    remove.setAttribute('aria-label', `حذف المحادثة: ${chat.title}`);
    remove.disabled = pendingChats.has(chat.id);
    remove.onclick = event => {
      event.stopPropagation();
      if (pendingChats.has(chat.id)) return;
      chats = chats.filter(item => item.id !== chat.id);
      if (active === chat.id) active = null;
      save(); renderHistory(); renderMessages();
    };
    row.append(button, remove);
    $('history').append(row);
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
        if (qualityLabel) bubble.prepend(el('div', 'report-scope report-quality', qualityLabel));
        if (message.scope) {
          const scope = el('div', 'report-scope', message.scope.detail);
          scope.setAttribute('role', 'note'); bubble.prepend(scope);
        }
        if (message.generatedAt || message.sources?.length) {
          const actions = el('div', 'report-actions');
          const copy = el('button', '', 'نسخ التقرير'); copy.type = 'button';
          copy.onclick = async () => {
            try { await navigator.clipboard.writeText(message.text); copy.textContent = 'تم النسخ'; }
            catch { copy.textContent = 'تعذر النسخ'; }
          };
          const print = el('button', '', 'طباعة التقرير'); print.type = 'button';
          print.onclick = () => {
            document.querySelectorAll('.print-report').forEach(node => node.classList.remove('print-report'));
            item.classList.add('print-report'); window.print();
          };
          const format = el('select', 'report-format');
          format.setAttribute('aria-label', 'صيغة تحميل التقرير');
          for (const [value, label] of [['docx', 'Word (.docx)'], ['xlsx', 'Excel (.xlsx)'], ['pdf', 'PDF (حفظ عبر الطباعة)'], ['html', 'تقرير HTML'], ['md', 'نص Markdown']]) {
            const option = el('option', '', label); option.value = value; format.append(option);
          }
          const download = el('button', '', 'تحميل التقرير'); download.type = 'button';
          download.onclick = () => {
            const question = chat.messages.slice(0, messageIndex).reverse().find(previous => previous.role === 'user')?.text;
            try { ReportsDownload.download(message, question, format.value); }
            catch (error) { download.textContent = error.message || 'تعذر التحميل'; }
          };
          actions.append(download, format, copy, print);
          const open = el('button', '', 'فتح وثائق التقرير في Laserfiche ↗'); open.type = 'button';
          open.disabled = !message.relatedEntryIds?.length;
          open.title = open.disabled ? 'لا توجد وثائق مرتبطة مؤكدة بهذه النتيجة' : 'عرض الوثائق المرتبطة في Web Client';
          open.onclick = async () => {
            open.disabled = true;
            const epoch = sessionEpoch;
            const preview = window.open('', '_blank');
            if (preview) { preview.opener = null; preview.document.title = 'فتح وثائق التقرير'; preview.document.body.textContent = 'جاري التحقق من الوثائق...'; }
            try {
              const result = await api('/api/reports/laserfiche-links', {method:'POST', headers:{'Content-Type':'application/json'},
                body:JSON.stringify({repositoryId:message.scope?.repositoryId || message.repositoryId,entryIds:message.relatedEntryIds})});
              if (epoch !== sessionEpoch) { preview?.close(); return; }
              const links = el('div', 'report-open-links');
              result.urls.forEach((url,i) => {
                const a=el('a','source-link',result.urls.length===1 ? `عرض ${result.documentCount} وثيقة في Laserfiche ↗` : `فتح المجموعة ${i+1} من ${result.urls.length} ↗`);
                a.href=url; a.target='_blank'; a.rel='noopener noreferrer'; links.append(a);
              });
              bubble.querySelector('.report-open-links')?.remove(); bubble.append(links);
              if (result.urls.length===1 && preview) preview.location.replace(result.urls[0]);
              else preview?.close();
            } catch(error) { preview?.close(); open.textContent=error.message; }
            finally { open.disabled=false; }
          };
          actions.append(open); bubble.append(actions);
        }
      } else bubble.textContent = message.text;
      item.append(bubble);
      if (message.sources?.length) {
        const sources = el('div', 'sources');
        message.sources.forEach((source, index) => {
          const isMetadata = source.textSource?.startsWith('laserfiche-metadata');
          const card = el('details', 'source');
          card.id = `${sourcePrefix}-${index + 1}`;
          card.append(el('summary', '',
            `[${index + 1}] ${source.documentName || 'وثيقة'} · ${isMetadata ? 'بيانات Laserfiche' : `صفحة ${source.pageNumber || '—'}`}`));
          card.append(el('small', 'source-path', source.path || `Entry ${source.entryId}`));
          card.append(el('p', 'source-text', source.text || ''));
          if (source.pageNumber) {
            const link = el('a', 'source-link', 'عرض صورة الصفحة ↗');
            link.href = `/api/laserfiche/documents/${source.entryId}/pages/${source.pageNumber}/image?repositoryId=${encodeURIComponent(message.scope?.repositoryId || message.repositoryId || sessionRepository)}&sessionGeneration=${encodeURIComponent(sessionGeneration)}`;
            link.target = '_blank'; link.rel = 'noopener';
            card.append(link);
          }
          sources.append(card);
        });
        item.append(sources);
      }
      container.append(item);
    });
  }
  if (!historySaved) {
    const warning = el('p', 'report-scope', 'تعذر حفظ المحادثة في المتصفح. التقرير متاح الآن؛ حمّله قبل إغلاق الصفحة.');
    warning.setAttribute('role', 'status'); container.append(warning);
  }
  container.scrollTop = container.scrollHeight;
}
function showTab(tab) {
  $('chat-view').classList.toggle('hidden', tab !== 'chat');
  $('docs-view').classList.toggle('hidden', tab !== 'docs');
  $('tab-chat').classList.toggle('active', tab === 'chat');
  $('tab-docs').classList.toggle('active', tab === 'docs');
  if (tab === 'docs' && !$('statuses').children.length) { refreshStatuses(); loadDocuments(); }
}
$('tab-chat').onclick = () => showTab('chat');
$('tab-docs').onclick = () => showTab('docs');
$('new-chat').onclick = () => { active = null; renderHistory(); renderMessages(); showTab('chat'); };
$('ask-form').onsubmit = async event => {
  event.preventDefault();
  if ($('send').disabled) return;
  const question = $('question').value.trim();
  if (!question) return;
  if (!active) {
    active = crypto.randomUUID();
    chats.unshift({ id: active, title: question.slice(0, 45), messages: [] });
  }
  const epoch = sessionEpoch;
  const chat = chats.find(c => c.id === active);
  pendingOperations++;
  pendingChats.add(chat.id);
  const controller = new AbortController();
  currentChatRequest = controller;
  const startedAt = Date.now();
  $('request-state').classList.remove('hidden');
  const updateProgress = () => { if (epoch === sessionEpoch) $('request-progress').textContent = `جارٍ إعداد التقرير · ${Math.floor((Date.now() - startedAt) / 1000)} ثانية · يمكنك التنقل بين التبويبات`; };
  updateProgress();
  const progressTimer = setInterval(updateProgress, 1000);
  chat.messages.push({ role: 'user', text: question });
  $('question').value = '';
  $('send').disabled = true;
  chat.messages.push({ role: 'assistant', pending: true, text: 'جاري تحليل الوثائق وتجهيز التقرير. قد يستغرق ذلك وقتًا بحسب حجم الأدلة وسرعة النموذج؛ اترك الصفحة مفتوحة...' });
  save(); renderHistory(); renderMessages();
  try {
    const result = await api('/api/reports/chat', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ question }), signal: controller.signal });
    if (epoch !== sessionEpoch) return;
    if (typeof result.answer !== 'string' || !result.answer.trim()) throw new Error('لم ترجع خدمة التحليل تقريرًا. أعد المحاولة وتحقق من LangGraph وOllama.');
    chat.messages[chat.messages.length - 1] = { role: 'assistant', repositoryId: sessionRepository, relatedEntryIds: result.relatedEntryIds, text: result.answer, sources: result.sources, scope: result.scope, generatedAt: result.generatedAt, quality: result.quality };
  } catch (error) {
    chat.messages[chat.messages.length - 1] = { role: 'assistant', text: `تعذر إكمال السؤال: ${error.message}` };
  } finally { clearInterval(progressTimer); if (currentChatRequest === controller) { currentChatRequest = null; $('request-state').classList.add('hidden'); } pendingOperations--; pendingChats.delete(chat.id); $('send').disabled = false; if (epoch === sessionEpoch) { save(); renderHistory(); renderMessages(); } }
};
$('cancel-chat').onclick = () => currentChatRequest?.abort();
$('question').onkeydown = event => {
  if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); $('ask-form').requestSubmit(); }
};
async function refreshStatuses() {
  const epoch = sessionEpoch, version = ++statusLoadVersion;
  const services = [['Laserfiche', '/api/laserfiche/status'], ['Supabase', '/api/database/status'],
    ['Ollama Embeddings', '/api/embeddings/status'], ['LangGraph', '/api/graph/status'], ['OCR', '/api/ocr/status']];
  $('statuses').replaceChildren();
  const cards = services.map(([name]) => {
    const card = el('div', 'status-card');
    card.append(el('strong', '', name), el('span', 'status-loading', 'جارٍ التحقق...'));
    $('statuses').append(card); return card;
  });
  const appStatus = api('/api/app/status').catch(() => ({}));
  let unavailable = 0;
  await Promise.all(services.map(async ([name, url], index) => {
    let diagnostic = '', label = 'متصل', className = 'ok';
    try {
      const deferred = name === 'OCR' && (await appStatus).ocrEnabled === false;
      if (deferred) { label = 'مؤجل'; className = 'deferred'; }
      else {
        const data = await api(url);
        if (data.status === 'unavailable' || data.isConnected === false || data.authenticationSucceeded === false)
          throw new Error('الخدمة غير متاحة حاليًا.');
      }
    } catch (error) { diagnostic = error.message; label = 'غير متصل'; className = 'bad'; unavailable++; }
    if (epoch !== sessionEpoch || version !== statusLoadVersion) return;
    cards[index].querySelector('span').replaceWith(el('span', className, label));
    if (diagnostic) cards[index].append(el('p', 'service-diagnostic', diagnostic));
    if (name === 'Supabase' && diagnostic)
      cards[index].append(el('p', 'service-hint', 'لضبط الاتصال المحلي شغّل scripts\\configure-database.ps1 من مجلد المشروع.'));
  }));
  if (epoch === sessionEpoch && version === statusLoadVersion)
    $('data-state').textContent = unavailable ? `اكتمل الفحص؛ ${unavailable} خدمة غير متاحة. يمكنك استخدام الواجهة وإعادة المحاولة من تحديث.` : 'اكتمل تحميل حالة الخدمات. الواجهة جاهزة.';
}
async function loadDocuments() {
  const epoch = sessionEpoch, version = ++documentLoadVersion;
  $('documents').replaceChildren(el('div', 'empty', 'جاري تحميل الوثائق...'));
  try {
    const search = $('document-search').value.trim();
    const result = await api(`/api/reports/documents?page=${documentsPage}&search=${encodeURIComponent(search)}`);
    if (epoch !== sessionEpoch || version !== documentLoadVersion) return;
    if (!Array.isArray(result.items)) throw new Error('تعذر قراءة قائمة الوثائق من الخادم.');
    indexedAvailable = result.items.length > 0;
    renderScan();
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
  } catch (error) { if (epoch === sessionEpoch && version === documentLoadVersion) $('documents').replaceChildren(el('div', 'empty', error.message)); }
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
  try { localStorage.setItem(scanKey, JSON.stringify(scan)); scanCheckpointSaved = true; }
  catch { scanCheckpointSaved = false; }
}
function renderScan() {
  $('scan-start').disabled = scanning;
  $('scan-reset').disabled = scanning;
  $('logout').disabled = scanning;
  $('scan-start').textContent = scan?.folders?.length || scan?.documents?.length || scan?.failed?.length
    ? 'استئناف الفهرسة' : 'بدء الفهرسة الشاملة';
  $('scan-pause').disabled = !scanning;
  if (!scan) {
    $('scan-progress').textContent = indexedAvailable ? 'توجد وثائق مفهرسة في الخادم. لا يوجد سجل محفوظ للفحص الشامل في هذا المتصفح والحساب؛ هذا لا يعني أن المستودع لم يُفهرس.' : 'لا يوجد سجل محفوظ للفحص الشامل في هذا المتصفح والحساب. راجع قائمة الوثائق لمعرفة حالة الفهرس.';
    $('scan-errors').replaceChildren();
    return;
  }
  const complete = !scan.folders.length && !scan.documents.length && !scan.failed.length;
  $('scan-start').textContent = complete ? 'إعادة الفهرسة الشاملة' : scanning ? 'الفهرسة جارية' : 'استئناف الفهرسة';
  $('scan-progress').textContent =
    `${scanning ? 'جارية' : complete ? 'مكتملة' : 'متوقفة'} · ${scan.foldersDone} مجلد · ${scan.documentsDone} وثيقة · ${scan.skipped || 0} دون تغيير · ` +
    `${scan.chunks} مقطع · ${scan.folders.length} مجلد و${scan.documents.length} وثيقة في الانتظار` +
    (scan.current ? ` · الآن: ${scan.current}` : '') +
    (scan.notice ? ` · ${scan.notice}` : '') +
    (!scanCheckpointSaved ? ' · تعذر حفظ نقطة الاستئناف في المتصفح. اترك الصفحة مفتوحة حتى تنتهي العملية.' : '');
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
  scan.startedAt ||= new Date().toISOString();
  saveScan(); renderScan();
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
    if (!scan.folders.length && !scan.documents.length && !scan.failed.length) scan.completedAt = new Date().toISOString();
    saveScan(); renderScan();
    loadDocuments();
  }
};
setRepositorySelection(launchRepository);
const startupEpoch = sessionEpoch;
api('/api/session/status', { timeoutMs: 10000 }).then(session => {
  if (sessionEpoch !== startupEpoch) return;
  setRepositorySelection(launchRepository || session.repository || '');
  const matchesLaunch = !launchRepository || launchRepository.toLowerCase() === (session.repository || '').toLowerCase();
  if (session.authenticated && session.username && matchesLaunch)
    openSession(session.username, session.repository, session.server, session.generation);
  else $('login-layer').classList.remove('hidden');
}).catch(() => {
  if (sessionEpoch !== startupEpoch) return;
  setRepositorySelection(launchRepository);
  $('login-layer').classList.remove('hidden');
  $('data-state').textContent = 'تعذر فحص الجلسة. يمكنك تسجيل الدخول والمحاولة مجددًا.';
}).finally(() => $('startup-loading').classList.add('hidden'));

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
