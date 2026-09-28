const $ = id => document.getElementById(id);
let storeKey = 'laserfiche-reports-chat-v1';
let chats = [];
let active = null;
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
  if (!response.ok) throw new Error(body.detail || body.message || body.error || `HTTP ${response.status}`);
  return body;
}
function openSession(username) {
  storeKey = `laserfiche-reports-chat-v1:${username.toLowerCase()}`;
  try { chats = JSON.parse(localStorage.getItem(storeKey) || '[]'); }
  catch { chats = []; }
  active = null;
  $('login-layer').classList.add('hidden');
  renderHistory();
  renderMessages();
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
  try { await api('/api/session/logout', { method: 'POST' }); }
  finally {
    localStorage.removeItem(storeKey);
    chats = []; active = null;
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
    for (const question of ['ما تصنيف الوثيقة 618؟', 'ما اسم الوثيقة 618 وموعد تسليمها؟', 'لخص أهم النقاط في الوثائق المفهرسة']) {
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
  const services = [['Laserfiche', '/api/laserfiche/status'], ['Supabase', '/api/database/status'], ['LangGraph', '/api/graph/status'], ['OCR (اختياري)', '/api/ocr/status']];
  $('statuses').replaceChildren();
  await Promise.all(services.map(async ([name, url]) => {
    let healthy = false;
    try {
      const response = await fetch(url);
      const data = await response.json();
      healthy = response.ok && data.status !== 'unavailable' && data.isConnected !== false && data.authenticationSucceeded !== false;
    } catch { /* rendered as unavailable */ }
    const card = el('div', 'status-card');
    card.append(el('strong', '', name));
    card.append(el('span', healthy ? 'ok' : 'bad', healthy ? 'متصل' : 'غير متصل'));
    $('statuses').append(card);
  }));
}
async function loadDocuments() {
  $('documents').replaceChildren(el('div', 'empty', 'جاري تحميل الوثائق...'));
  try {
    const documents = await api('/api/reports/documents');
    $('documents').replaceChildren();
    if (!documents.length) $('documents').append(el('div', 'empty', 'لا توجد وثائق مفهرسة متاحة للاتصال الحالي.'));
    documents.forEach(doc => {
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
api('/api/session/status').then(session => {
  if (session.authenticated && session.username) openSession(session.username);
  else $('login-layer').classList.remove('hidden');
}).catch(() => $('login-layer').classList.remove('hidden'));
