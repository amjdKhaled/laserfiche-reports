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
let documentsPage = 1;
let hasMoreDocuments = false;
let historySaved = true;
let sessionRequests = new AbortController();
let questionRequest = null;
let documentsRequest = null;
let documentSequence = 0;
let statusRequest = null;
function cancelSessionRequests() { sessionRequests.abort(); sessionRequests = new AbortController(); statusRequest = null; }
function requestSignal(signal, milliseconds) {
  return AbortSignal.any([sessionRequests.signal, AbortSignal.timeout(milliseconds), ...(signal ? [signal] : [])]);
}
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
  const response = await fetch(url, { ...options, headers, signal: requestSignal(options?.signal, url.includes("/chat") ? 2000000 : url.endsWith("/status") ? 12000 : 130000) });
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
  cancelSessionRequests();
  sessionGeneration = generation || '';
  sessionEpoch++;
  sessionRepository = repository || '';
  sessionServer = server || '';
  $('active-repository').textContent = sessionRepository || 'غير محدد';
  setRepositorySelection(sessionRepository);
  documentsPage = 1;
  $('documents').replaceChildren();
  $('statuses').replaceChildren();
  const identity = [sessionServer, sessionRepository, username].map(value => encodeURIComponent(value.toLowerCase())).join(':');
  historySaved = true;
  storeKey = `laserfiche-reports-chat-v2:${identity}`;
  try { chats = JSON.parse(localStorage.getItem(storeKey) || '[]'); }
  catch { chats = []; }
  active = null;
  $('login-layer').classList.add('hidden');
  renderHistory();
  renderMessages();
  refreshStatuses();
}
$('login-form').onsubmit = async event => {
  event.preventDefault();
  cancelSessionRequests();
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
  cancelSessionRequests();
  try { await api('/api/session/logout', { method: 'POST' }); }
  catch (error) { $('login-error').textContent = `تعذر إنهاء جلسة الخادم: ${error.message}`; }
  finally {
    sessionEpoch++;
    sessionRepository = ''; sessionServer = ''; sessionGeneration = '';
    $('active-repository').textContent = '—';
    chats = []; active = null;
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
      if (message.pagination && message.query) {
        const pager = el('div', 'docs-pager');
        const go = page => {
          if ($('send').disabled) return;
          $('question').value = `اعرض الصفحة ${page}`;
          $('ask-form').requestSubmit();
        };
        const before = el('button', '', 'السابق'); before.type='button'; before.disabled = message.pagination.page <= 1;
        before.onclick = () => go(message.pagination.page - 1);
        const after = el('button', '', 'التالي'); after.type='button'; after.disabled = !message.pagination.hasMore;
        after.onclick = () => go(message.pagination.page + 1);
        pager.append(before, el('span', '', `صفحة ${message.pagination.page}`), after); bubble.append(pager);
      }
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
  if (tab === 'docs') { refreshStatuses(); loadDocuments(); }
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
  chat.messages.push({ role: 'user', text: question });
  $('question').value = '';
  $('send').disabled = true;
  chat.messages.push({ role: 'assistant', text: 'جارٍ فهم السؤال والبحث في Laserfiche...' });
  renderHistory(); renderMessages();
  questionRequest = new AbortController();
  $('cancel-question').classList.remove('hidden');
  try {
    const previousQuery = chat.messages.slice(0, -2).reverse().find(message => message.query)?.query;
    const result = await askStream(question, previousQuery, questionRequest.signal, message => {
      if (epoch !== sessionEpoch) return;
      chat.messages[chat.messages.length - 1].text = message;
      renderMessages();
    });
    if (epoch !== sessionEpoch) return;
    chat.messages[chat.messages.length - 1] = { role: 'assistant', repositoryId: sessionRepository, relatedEntryIds: result.relatedEntryIds, text: result.answer, sources: result.sources, scope: result.scope, generatedAt: result.generatedAt, quality: result.quality, query: result.query, pagination: result.pagination };
  } catch (error) {
    chat.messages[chat.messages.length - 1] = { role: 'assistant', text: error.name === "AbortError" ? "أُلغي السؤال." : `تعذر إكمال السؤال: ${error.message}` };
  } finally { pendingOperations--; pendingChats.delete(chat.id); $('send').disabled = false; $('cancel-question').classList.add('hidden'); if (epoch === sessionEpoch) { save(); renderHistory(); renderMessages(); } }
};
$('question').onkeydown = event => {
  if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); $('ask-form').requestSubmit(); }
};
async function refreshStatuses() {
  if (statusRequest) return statusRequest;
  const epoch = sessionEpoch;
  const services = [['Laserfiche', '/api/laserfiche/status'], ['الذكاء الاصطناعي', '/api/ai/status']];
  $('statuses').replaceChildren();
  const cards = services.map(([name]) => {
    const card = el('div', 'status-card'); card.append(el('strong', '', name), el('span', '', 'جارٍ الاتصال...'));
    $('statuses').append(card); return card;
  });
  const runningStatus = Promise.allSettled(services.map(async ([name, url], index) => {
    try {
      const data = await api(url);
      if (epoch !== sessionEpoch) return;
      const healthy = data.isConnected !== false && data.authenticationSucceeded !== false;
      cards[index].lastChild.textContent = healthy ? 'متصل' : 'تعذر الاتصال';
      cards[index].lastChild.className = healthy ? 'ok' : 'bad';
      if (data.model) cards[index].append(el('p', 'service-hint', `نموذج المحادثة: ${data.model}`));
    } catch (error) {
      if (epoch !== sessionEpoch) return;
      cards[index].lastChild.textContent = 'تعذر الاتصال'; cards[index].lastChild.className = 'bad';
      cards[index].append(el('p', 'service-diagnostic', error.name === 'TimeoutError' ? 'انتهت مهلة فحص الاتصال' : error.message));
    }
  })).finally(() => { if (statusRequest === runningStatus) statusRequest = null; });
  statusRequest = runningStatus;
  return runningStatus;
}
async function loadDocuments() {
  const epoch = sessionEpoch;
  documentsRequest?.abort();
  documentsRequest = new AbortController();
  const requestedPage = documentsPage;
  const sequence = ++documentSequence;
  $('documents').replaceChildren(el('div', 'empty', 'جاري تحميل الوثائق...'));
  try {
    const search = $('document-search').value.trim();
    const result = await api(`/api/reports/documents?page=${requestedPage}&search=${encodeURIComponent(search)}`, {signal: documentsRequest.signal});
    if (epoch !== sessionEpoch || requestedPage !== documentsPage || sequence !== documentSequence) return;
    $('documents').replaceChildren();
    hasMoreDocuments = result.hasMore;
    $('previous-docs').disabled = documentsPage === 1;
    $('next-docs').disabled = !hasMoreDocuments;
    $('documents-page').textContent = `صفحة ${documentsPage}${Number.isInteger(result.totalCount) ? ` من ${Math.max(1, Math.ceil(result.totalCount / 50))} · ${result.totalCount} وثيقة` : ''}`;
    if (!result.items.length) $('documents').append(el('div', 'empty', 'لا توجد وثائق مطابقة لهذه الصفحة أو البحث.'));
    result.items.forEach(doc => {
      const row = el('div', 'doc'), detail = el('div');
      detail.append(el('strong', '', `${doc.name} · #${doc.entryId}`));
      detail.append(el('small', '', doc.path));
      row.append(detail, el('span', 'tag', 'بيانات مباشرة من Laserfiche'));
      $('documents').append(row);
    });
  } catch (error) { if (epoch === sessionEpoch && requestedPage === documentsPage && sequence === documentSequence && error.name !== 'AbortError') $('documents').replaceChildren(el('div', 'empty', error.message)); }
}
$('refresh').onclick = refreshStatuses;
$('reload-docs').onclick = loadDocuments;
$('search-docs').onclick = () => { documentsPage = 1; loadDocuments(); };
$('document-search').onkeydown = event => {
  if (event.key === 'Enter') { event.preventDefault(); documentsPage = 1; loadDocuments(); }
};
$('previous-docs').onclick = () => { if (documentsPage > 1) { documentsPage--; loadDocuments(); } };
$('next-docs').onclick = () => { if (hasMoreDocuments) { documentsPage++; loadDocuments(); } };
const startupEpoch = sessionEpoch;
api('/api/session/status', { signal: AbortSignal.timeout(10000) }).then(session => {
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
});

$('switch-repository').onclick = () => {
  cancelSessionRequests();
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

$('cancel-question').onclick = () => questionRequest?.abort();
async function askStream(question, previousQuery, signal, progress) {
  const headers = new Headers({'Content-Type':'application/json', 'X-Reports-Repository':sessionRepository, 'X-Reports-Session':sessionGeneration});
  const response = await fetch('/api/reports/chat/stream', {method:'POST', headers,
    body:JSON.stringify({question, previousQuery}), signal:requestSignal(signal, 2000000)});
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(problem.message || problem.detail || 'تعذر تنفيذ السؤال.');
  }
  // Compatibility for non-streaming test doubles and reverse proxies.
  if (!response.body?.getReader) return response.json();
  const reader = response.body.getReader(); const decoder = new TextDecoder();
  let buffer = '', result = null;
  try {
    while (true) {
      const chunk = await reader.read();
      buffer += decoder.decode(chunk.value || new Uint8Array(), {stream:!chunk.done});
      let boundary;
      while ((boundary = buffer.indexOf('\n\n')) >= 0) {
        const block = buffer.slice(0,boundary); buffer = buffer.slice(boundary + 2);
        const kind = block.split('\n').find(line=>line.startsWith('event: '))?.slice(7);
        const data = JSON.parse(block.split('\n').find(line=>line.startsWith('data: '))?.slice(6) || '{}');
        if (kind === 'status') progress(data.message);
        if (kind === 'delta') { if (result) { result.answer += data.text; progress(result.answer); } }
        if (kind === 'result') { result = data; progress(result.answer); }
        if (kind === 'error') throw new Error(data.message);
      }
      if (chunk.done) break;
    }
  } finally { reader.releaseLock(); }
  if (!result) throw new Error('لم تصل نتيجة مكتملة من الخادم.');
  return result;
}
