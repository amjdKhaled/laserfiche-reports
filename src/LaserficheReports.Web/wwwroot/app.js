let reportResultsWindow = null;
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
async function api(url, options) {
  const epoch = sessionEpoch;
  const headers = new Headers(options?.headers || {});
  if (sessionRepository) headers.set('X-Reports-Repository', sessionRepository);
  if (sessionGeneration) headers.set('X-Reports-Session', sessionGeneration);
  const response = await fetch(url, { ...options, headers });
  const body = await response.json().catch(() => ({}));
  if (epoch !== sessionEpoch) throw new Error('تغيّرت جلسة المستودع؛ تم تجاهل نتيجة الطلب السابق.');
  if ((response.status === 401 || response.status === 409 && body.error === 'session_scope_changed') && url !== '/api/session/login') $('login-layer').classList.remove('hidden');
  if (!response.ok) {
    const error = new Error(body.detail || body.message || body.error ||
      `HTTP ${response.status} — راجع سجل التطبيق لمعرفة السبب`);
    error.status = response.status;
    error.diagnosticId = body.diagnosticId;
    error.code = body.error;
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
  $('repository-id').value = sessionRepository;
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
  try {
    const username = $('username').value.trim();
    const session = await api('/api/session/login', { method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password: $('password').value, repositoryId: $('repository-id').value.trim() }) });
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
    button.classList.add('history-open');
    const row = el('div', chat.id === active ? 'history-row selected' : 'history-row');
    const remove = el('button', 'history-delete', '×');
    remove.type = 'button'; remove.title = 'حذف المحادثة';
    remove.setAttribute('aria-label', `حذف المحادثة: ${chat.title}`);
    remove.onclick = () => {
      chats = chats.filter(item => item.id !== chat.id);
      if (active === chat.id) active = null;
      save(); renderHistory(); renderMessages();
    };
    row.append(button, remove); $('history').append(row);
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
    chat.messages.forEach((original, messageIndex) => {
      const parts = original.role === 'assistant' && original.reports?.length
        ? original.reports.map(report => ({...original, ...report, text: report.answer, reports: null})) : [original];
      parts.forEach((message, partIndex) => {
      const item = el('div', `message ${message.role}`);
      item.append(el('div', 'label', message.role === 'user' ? 'أنت' : (message.title || 'Laserfiche Reports')));
      const sourcePrefix = `source-${chat.id}-${messageIndex}-${partIndex}`;
      const bubble = el('div', 'bubble');
      if (message.role === 'assistant') {
        bubble.append(ReportsMarkdown.render(message.text || '', message.sources?.length || 0, sourcePrefix));
        if (message.sources?.length) {
          const sources = el('div', 'sources sources-compact'); sources.dir = 'rtl';
          const more = el('details', 'sources-more');
          more.append(el('summary', '', `عرض المزيد من المراجع (${Math.max(0,message.sources.length - 3)})`));
          const extra = el('div', 'sources-extra'); more.append(extra);
          message.sources.forEach((source, index) => {
            const row = el('details', 'source'); row.id = `${sourcePrefix}-${index + 1}`;
            row.append(el('summary', '', `[${index + 1}] ${source.documentName || 'وثيقة'} — ${source.entryId}${source.pageNumber ? ` · الصفحة ${source.pageNumber}` : ''}`));
            row.append(el('p', '', source.textSource?.startsWith('laserfiche-metadata') ? 'المصدر: بيانات Laserfiche الحية وقت التقرير.' : 'المصدر: النص المفهرس للوثيقة؛ راجع الأصل للتحقق.'));
            row.append(el('pre', '', source.text || 'لا يوجد مقتطف نصي لهذا المرجع.'));
            const link = el('button', 'source-link', 'فتح الوثيقة في Laserfiche ↗'); link.type = 'button';
            link.onclick = async () => {
              const epoch = sessionEpoch;
              const preview = window.open('about:blank', 'laserfiche-report-results');
              link.disabled = true;
              try {
                const result = await api('/api/reports/laserfiche-links', {method:'POST', headers:{'Content-Type':'application/json'},
                  body:JSON.stringify({repositoryId:message.scope?.repositoryId || message.repositoryId, entryIds:[source.entryId], openSingle:true})});
                if (epoch !== sessionEpoch) { preview?.close(); return; }
                if (preview && result.urls?.[0]) preview.location.replace(result.urls[0]);
                else { preview?.close(); link.textContent = 'اسمح بفتح نافذة الوثيقة وأعد المحاولة'; }
              } catch(error) { preview?.close(); link.textContent = error.message; }
              finally { link.disabled = false; }
            };
            row.append(link); (index < 3 ? sources : extra).append(row);
          });
          if (message.sources.length > 3) sources.append(more);
          bubble.append(sources);
        }
        const originalQuestion = chat.messages.slice(0, messageIndex).reverse().find(previous => previous.role === 'user')?.text;
        const reportQuestion = message.title ? message.title + ' — ' + originalQuestion : originalQuestion;
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
          const scope = el('div', 'report-scope'); scope.append(ReportsMarkdown.render(message.scope.detail || '', 0));
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
            try { ReportsDownload.download(message, reportQuestion, format.value); }
            catch (error) { download.textContent = error.message || 'تعذر التحميل'; }
          };
          actions.append(download, format, copy, print);
          const open = el('button', '', 'فتح وثائق التقرير في Laserfiche ↗'); open.type = 'button';
          open.disabled = !message.relatedEntryIds?.length;
          open.title = open.disabled ? 'لا توجد وثائق مرتبطة مؤكدة بهذه النتيجة' : 'عرض الوثائق المرتبطة في Web Client';
          open.onclick = async () => {
            open.disabled = true;
            const epoch = sessionEpoch;
            const reusedWindow = reportResultsWindow && !reportResultsWindow.closed;
            const preview = reusedWindow ? reportResultsWindow : window.open('about:blank', 'laserfiche-report-results');
            reportResultsWindow = preview;
            try {
              const result = await api('/api/reports/laserfiche-links', {method:'POST', headers:{'Content-Type':'application/json'},
                body:JSON.stringify({repositoryId:message.scope?.repositoryId || message.repositoryId,entryIds:message.relatedEntryIds,openSingle:true})});
              if (epoch !== sessionEpoch) { if (!reusedWindow) preview?.close(); return; }
              const links = el('div', 'report-open-links');
              result.urls.forEach((url,i) => {
                const a=el('a','source-link',result.urls.length===1 ? `عرض ${result.documentCount} وثيقة في Laserfiche ↗` : `فتح المجموعة ${i+1} من ${result.urls.length} ↗`);
                a.href=url; a.target='laserfiche-report-results'; a.rel='noopener noreferrer'; links.append(a);
              });
              bubble.querySelector('.report-open-links')?.remove(); bubble.append(links);
              if (result.urls.length===1 && preview) preview.location.replace(result.urls[0]);
              else if (!reusedWindow) preview?.close();
            } catch(error) { if (!reusedWindow) preview?.close(); open.textContent=error.message; }
            finally { open.disabled=false; }
          };
          actions.append(open); bubble.append(actions);
        }
      } else bubble.textContent = message.text;
      item.append(bubble);
      container.append(item);
      });
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
  if (tab === 'docs') { refreshStatuses(); loadDocuments(); }
}
$('tab-chat').onclick = () => showTab('chat');
$('tab-docs').onclick = () => showTab('docs');
$('new-chat').onclick = () => { active = null; renderHistory(); renderMessages(); showTab('chat'); };
function conversationHistory(messages) {
  const completed = [];
  for (const message of messages) {
    // A failed exchange establishes no new context. Preserve the pending
    // clarification so another attempt can answer it without signing in again.
    if (message.role === 'assistant' && (message.kind === 'error' || message.text?.startsWith('تعذر إكمال السؤال:'))) {
      if (completed.at(-1)?.role === 'user') completed.pop();
      continue;
    }
    completed.push(message);
  }
  return completed.slice(-8).map(message => ({ role: message.role,
    text: (message.text || '').slice(0, message.role === 'user' ? 2000 : 1000),
    ...(message.kind ? { kind: message.kind } : {}),
    ...(message.clarificationQuestion ? { clarificationQuestion: message.clarificationQuestion.slice(0, 2000) } : {}) }));
}
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
  chat.messages.push({ role: 'user', text: question });
  $('question').value = '';
  $('send').disabled = true;
  chat.messages.push({ role: 'assistant', text: 'جاري البحث في الوثائق وتحضير الإجابة...' });
  renderHistory(); renderMessages();
  try {
    const result = await api('/api/reports/chat', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ question, history: conversationHistory(chat.messages.slice(0, -2)) }) });
    if (epoch !== sessionEpoch || !chats.includes(chat)) return;
    chat.messages[chat.messages.length - 1] = { role: 'assistant', kind: result.isClarification ? 'clarification' : 'answer', clarificationQuestion: result.clarificationQuestion, repositoryId: sessionRepository, relatedEntryIds: result.relatedEntryIds, reports: result.reports, text: result.answer, sources: result.sources, scope: result.scope, generatedAt: result.generatedAt, quality: result.quality };
  } catch (error) {
    chat.messages[chat.messages.length - 1] = { role: 'assistant', kind: 'error', errorCode: error.code, text: `تعذر إكمال السؤال: ${error.message}` };
  } finally { pendingOperations--; $('send').disabled = pendingOperations > 0; if (epoch === sessionEpoch) { save(); renderMessages(); } }
};
$('question').onkeydown = event => {
  if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); $('ask-form').requestSubmit(); }
};
async function refreshStatuses() {
  const epoch = sessionEpoch;
  const appStatus = await api('/api/app/status').catch(() => ({}));
  if (epoch !== sessionEpoch) return;
  const services = [['Laserfiche', '/api/laserfiche/status'], ['Supabase', '/api/database/status'],
    ['Ollama Embeddings', '/api/embeddings/status'], ['LangGraph', '/api/graph/status'], ['OCR', '/api/ocr/status']];
  $('statuses').replaceChildren();
  const cards = services.map(([name]) => {
    const card = el('div', 'status-card');
    card.append(el('strong', '', name));
    $('statuses').append(card);
    return card;
  });
  await Promise.all(services.map(async ([name, url], index) => {
    const deferred = name === 'OCR' && appStatus.ocrEnabled === false;
    let healthy = false;
    try { if (!deferred) {
      const data = await api(url);
      healthy = data.status !== 'unavailable' && data.isConnected !== false && data.authenticationSucceeded !== false;
    } } catch { /* rendered as unavailable */ }
    cards[index].append(el('span', deferred ? 'deferred' : healthy ? 'ok' : 'bad',
      deferred ? 'مؤجل' : healthy ? 'متصل' : 'غير متصل'));
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
  $('repository-id').value = session.repository || '';
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
  try {
    const repositories=await api('/api/session/repositories',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify({username:$('username').value.trim(),password:$('password').value,repositoryId:$('repository-id').value.trim()})});
    $('repository-options').replaceChildren();
    repositories.forEach(repo=>{const option=el('option','',repo.name);option.value=repo.id;$('repository-options').append(option);});
    $('login-error').textContent=`تم العثور على ${repositories.length} مستودع. اختر من خانة المستودع ثم سجل الدخول.`;
  } catch(error) { $('login-error').textContent=error.message; }
  finally { $('discover-repositories').disabled=false; }
};
