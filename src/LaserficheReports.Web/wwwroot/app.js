const $ = id => document.getElementById(id);
let storeKey = 'laserfiche-reports-chat-v1';
let chats = [];
let active = null;
let scan = null;
let scanning = false;
let pauseScan = false;
let scanKey = '';
let documentsPage = 1;
let hasMoreDocuments = false;
const save = () => localStorage.setItem(storeKey, JSON.stringify(chats.slice(0, 30)));
function el(tag, className, value) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (value) node.textContent = value;
  return node;
}
async function api(url, options) {
  const response = await fetch(url, options);
  const body = await response.json().catch(() => ({}));
  if (response.status === 401 && url !== '/api/session/login') $('login-layer').classList.remove('hidden');
  if (!response.ok) {
    const error = new Error(body.detail || body.message || body.error || `HTTP ${response.status}`);
    error.status = response.status;
    throw error;
  }
  return body;
}
function openSession(username) {
  storeKey = `laserfiche-reports-chat-v1:${username.toLowerCase()}`;
  scanKey = `laserfiche-reports-scan-v1:${username.toLowerCase()}`;
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
    await api('/api/session/login', { method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password: $('password').value }) });
    $('password').value = '';
    openSession(username);
  } catch (error) { $('login-error').textContent = `تعذر تسجيل الدخول: ${error.message}`; }
  finally { $('login-submit').disabled = false; }
};
$('logout').onclick = async () => {
  if (scanning) return;
  try { await api('/api/session/logout', { method: 'POST' }); }
  finally {
    localStorage.removeItem(storeKey);
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
    welcome.innerHTML = '<div class="welcome-icon">✦</div><h2>ما الذي تريد معرفته؟</h2><p>ابحث في محتوى الوثائق المفهرسة، وستظهر مصادر كل إجابة أسفلها.</p>';
    const suggestions = el('div', 'suggestions');
    for (const question of ['ما الوثائق المتعلقة بالتشغيل والصيانة؟', 'ما مواعيد التسليم المذكورة في حقول الوثائق؟', 'لخص أهم النقاط في الوثائق المفهرسة']) {
      const button = el('button', '', question);
      button.onclick = () => { $('question').value = question; $('question').focus(); };
      suggestions.append(button);
    }
    welcome.append(suggestions);
    container.append(welcome);
  } else {
    chat.messages.forEach(message => {
      const item = el('div', `message ${message.role}`);
      item.append(el('div', 'label', message.role === 'user' ? 'أنت' : 'Laserfiche Reports'));
      item.append(el('div', 'bubble', message.text));
      if (message.sources?.length) {
        const sources = el('div', 'sources');
        message.sources.forEach((source, index) => {
          const isMetadata = source.textSource === 'laserfiche-metadata';
          const card = el('details', 'source');
          card.append(el('summary', '',
            `[${index + 1}] ${source.documentName || 'وثيقة'} · ${isMetadata ? 'بيانات Laserfiche' : `صفحة ${source.pageNumber || '—'}`}`));
          card.append(el('small', 'source-path', source.path || `Entry ${source.entryId}`));
          card.append(el('p', 'source-text', source.text || ''));
          if (source.pageNumber) {
            const link = el('a', 'source-link', 'عرض صورة الصفحة ↗');
            link.href = `/api/laserfiche/documents/${source.entryId}/pages/${source.pageNumber}/image`;
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
$('ask-form').onsubmit = async event => {
  event.preventDefault();
  const question = $('question').value.trim();
  if (!question) return;
  if (!active) {
    active = crypto.randomUUID();
    chats.unshift({ id: active, title: question.slice(0, 45), messages: [] });
  }
  const chat = chats.find(c => c.id === active);
  chat.messages.push({ role: 'user', text: question });
  $('question').value = '';
  $('send').disabled = true;
  chat.messages.push({ role: 'assistant', text: 'جاري البحث في الوثائق وتحضير الإجابة...' });
  renderHistory(); renderMessages();
  try {
    const result = await api('/api/reports/chat', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ question }) });
    chat.messages[chat.messages.length - 1] = { role: 'assistant', text: result.answer, sources: result.sources };
  } catch (error) {
    chat.messages[chat.messages.length - 1] = { role: 'assistant', text: `تعذر إكمال السؤال: ${error.message}` };
  } finally { $('send').disabled = false; save(); renderMessages(); }
};
$('question').onkeydown = event => {
  if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); $('ask-form').requestSubmit(); }
};
async function refreshStatuses() {
  const appStatus = await api('/api/app/status').catch(() => ({}));
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
      const response = await fetch(url);
      const data = await response.json();
      healthy = response.ok && data.status !== 'unavailable' && data.isConnected !== false && data.authenticationSucceeded !== false;
    } } catch { /* rendered as unavailable */ }
    cards[index].append(el('span', deferred ? 'deferred' : healthy ? 'ok' : 'bad',
      deferred ? 'مؤجل' : healthy ? 'متصل' : 'غير متصل'));
  }));
}
async function loadDocuments() {
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
  } catch (error) { $('documents').replaceChildren(el('div', 'empty', error.message)); }
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
  $('ingest').disabled = true;
  $('ingest-result').textContent = 'جاري فهرسة الوثيقة، قد يستغرق ذلك عدة دقائق...';
  try {
    const result = await api(`/api/ingestion/laserfiche/${$('entry-id').value}`, { method: 'POST' });
    $('ingest-result').textContent = `تمت المعالجة: ${result.ingestionStatus}، المقاطع: ${result.chunkCount}`;
    loadDocuments();
  } catch (error) { $('ingest-result').textContent = `فشلت الفهرسة: ${error.message}`; }
  finally { $('ingest').disabled = false; }
};

function newScan() {
  return { folders: [0], documents: [], seenFolders: [], seenDocuments: [],
    foldersDone: 0, documentsDone: 0, skipped: 0, chunks: 0, failed: [], repositoryId: '', current: '', notice: '' };
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
  if (scanning) return;
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
          if (error.status === 401) { pauseScan = true; break; }
          if (error.status === 503) {
            pauseScan = true;
            scan.notice = `الخدمة المطلوبة غير متاحة: ${error.message}. عالج الاتصال ثم استأنف.`;
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
          if (error.status === 401) { pauseScan = true; break; }
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
  if (session.authenticated && session.username) openSession(session.username);
  else $('login-layer').classList.remove('hidden');
}).catch(() => $('login-layer').classList.remove('hidden'));
