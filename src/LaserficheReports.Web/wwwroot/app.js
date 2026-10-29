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
const activeRequests = new Set();
let signingOut = false;
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
  const controller = new AbortController();
  const timeout = AbortSignal.timeout(3600000);
  activeRequests.add(controller);
  let response, body;
  try {
    response = await fetch(url, { ...options, headers, signal: options?.signal
      ? AbortSignal.any([controller.signal, options.signal, timeout]) : AbortSignal.any([controller.signal, timeout]) });
    body = await response.json().catch(() => ({}));
  } catch (error) {
    if (timeout.aborted) throw new Error('انتهت مهلة الطلب المحددة بساعة. أعد المحاولة أو ضيّق نطاق التقرير.');
    throw error;
  } finally { activeRequests.delete(controller); }
  if (epoch !== sessionEpoch) throw new Error('تغيّرت جلسة المستودع؛ تم تجاهل نتيجة الطلب السابق.');
  // A question signs in again itself and is resent; any other request signs in again silently when it can.
  if ((response.status === 401 || response.status === 409 && body.error === 'session_scope_changed') && url !== '/api/session/login' &&
    !(response.status === 401 && url !== '/api/reports/chat' && signInAgainSilently())) showLogin();
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
  document.dispatchEvent(new CustomEvent('reports-session-cleared'));
  sessionRepository = repository || '';
  sessionServer = server || '';
  $('active-repository').textContent = sessionRepository || 'غير محدد';
  $('signed-in-user').textContent = username || '—';
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
  document.dispatchEvent(new CustomEvent('reports-session-opened'));
  renderHistory();
  renderMessages();
  renderScan();
}
// LFDS single sign-on: the server redirects through the Repository API and back.
let ssoEnabled = false;
const ssoErrors = {
  provider: 'رفض Laserfiche طلب الدخول الموحد. حاول مرة أخرى أو ادخل بحساب آخر.',
  state: 'انتهت صلاحية محاولة الدخول. اضغط زر الدخول مرة أخرى.',
  exchange: 'تعذر إكمال الدخول الموحد مع Laserfiche. راجع سجل التطبيق.',
  configuration: 'إعدادات الدخول الموحد غير مكتملة. راجع appsettings.Local.json.',
  repository: 'اسم المستودع غير صالح.'
};
function startSso(otherAccount) {
  location.href = '/login/StartSso?repository=' + encodeURIComponent($('repository-id').value.trim()) +
    (otherAccount ? '&forceLogin=true' : '');
}
// At most one automatic sign-in a minute, so a failing sign-in cannot loop.
function allowAutoSso() {
  let last = 0;
  try { last = Number(sessionStorage.getItem('reports-sso-auto')) || 0; } catch {}
  if (Date.now() - last <= 60000) return false;
  try { sessionStorage.setItem('reports-sso-auto', String(Date.now())); } catch {}
  return true;
}
// A Repository API sign-in ends about 30 minutes after it starts (15-minute tokens whose renewals keep the
// first expiry), and a site restart ends every session. LFDS is still signed in, so signing in again is
// silent; the page comes back to the same chat with the unsent text (sign-in screen at 08:22, 2026-10-28).
function signInAgainSilently() {
  if (!ssoEnabled || !allowAutoSso()) return false;
  try { sessionStorage.setItem('reports-resume', JSON.stringify({ chat: active, draft: $('question').value })); } catch {}
  save();
  startSso(false);
  return true;
}
// The sign-in card is also the repository picker. Opened from the repository button over a working session it
// offers a way back; signed out or after a session ended there is nothing to go back to.
function showLogin(switching = false) {
  $('login-layer').classList.remove('hidden');
  $('login-back').classList.toggle('hidden', !switching);
  $('login-title').textContent = switching ? 'تغيير المستودع' : 'الدخول إلى Laserfiche Reports';
  $('sso-login').textContent = switching ? 'فتح هذا المستودع' : 'الدخول بحساب Laserfiche';
  $('login-hint').textContent = switching ? `المستودع الحالي: ${sessionRepository}. اختر مستودعًا آخر، أو ارجع بدون تغيير.`
    : 'استخدم حساب Laserfiche للوصول إلى الوثائق المسموح لك بها.';
}
function applyLoginMode() {
  $('sso-panel').classList.toggle('hidden', !ssoEnabled);
  $('password-panel').classList.toggle('hidden', ssoEnabled);
  $('username').required = $('password').required = !ssoEnabled;
}
$('sso-other-account').onclick = () => startSso(true);
$('login-form').onsubmit = async event => {
  event.preventDefault();
  if (ssoEnabled) { startSso(false); return; }
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
  if (signingOut) return;
  signingOut = true;
  $('logout').disabled = true;
  pauseScan = true;
  for (const controller of activeRequests) controller.abort();
  try {
    await api('/api/session/logout', { method: 'POST' });
    sessionEpoch++;
    document.dispatchEvent(new CustomEvent('reports-session-cleared'));
    sessionRepository = ''; sessionGeneration = ''; sessionServer = '';
    chats = []; active = null; scan = null;
    $('password').value = '';
    if ($('audit-password')) $('audit-password').value = '';
    if (reportResultsWindow && !reportResultsWindow.closed) reportResultsWindow.close();
    renderHistory(); renderMessages();
    window.close();
    window.location.replace('/signed-out.html');
  } catch (error) {
    $('login-error').textContent = `تعذر تأكيد تسجيل الخروج: ${error.message}. حاول مرة أخرى.`;
    showLogin();
    signingOut = false;
    $('logout').disabled = false;
  }
};

// The white sidebar (chosen 2026-10-29): search, ready reports by group, the latest questions, and a footer with
// every conversation and «حذف الباقي». A ready report runs at once in a new conversation.
const readyReports = [
  ['الوثائق', [
    ['table', 'الوثائق التي أنشئت هذا الشهر', 'اعرض الوثائق التي أنشئت هذا الشهر', 'جدول'],
    ['table', 'الوثائق التي تم تعديلها الشهر الماضي', 'اعرض الوثائق التي تم تعديلها الشهر الماضي', 'جدول'],
    ['table', 'الوثائق في مجلد الوارد', 'اعرض الوثائق في مجلد الوارد', 'جدول'],
    ['hash', 'كم عدد الوثائق في المستودع؟', 'كم عدد الوثائق في المستودع؟', 'عدد']]],
  // Tag and template filters are left out: the planner returned the whole repository for them (2026-10-29).
  ['الأرشيف والسجلات', [
    ['chart', 'عدد الوثائق حسب القالب', 'كم عدد الوثائق حسب القالب؟', 'إحصائية'],
    ['chart', 'عدد الوثائق في كل شهر', 'كم عدد الوثائق التي أنشئت في كل شهر؟', 'إحصائية'],
    ['trash', 'الوثائق المحذوفة هذا الأسبوع', 'ما الوثائق التي تم حذفها هذا الأسبوع؟', 'جدول'],
    ['file', 'الحقول والقوالب المتاحة للبحث', 'ما الحقول والقوالب المتاحة للبحث؟', 'قائمة']]]
];
const RECENT_LIMIT = 5;
let showAllChats = false;
function sideIcon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg'), use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  svg.setAttribute('class', 'icon'); svg.setAttribute('aria-hidden', 'true');
  use.setAttribute('href', `#i-${name}`); svg.append(use);
  return svg;
}
function whenAsked(time) {
  if (!time) return '';
  const day = date => new Date(date.getFullYear(), date.getMonth(), date.getDate());
  const days = Math.round((day(new Date()) - day(new Date(time))) / 86400000);
  return days <= 0 ? 'اليوم' : days === 1 ? 'أمس' : days === 2 ? 'منذ يومين' : days <= 10 ? `منذ ${days} أيام` : new Date(time).toLocaleDateString('ar-EG', { day: 'numeric', month: 'short' });
}
function runReadyReport(question) {
  if (stopQuestion) return;
  active = null;
  $('side-search').value = '';
  showTab('chat');
  $('question').value = question;
  $('ask-form').requestSubmit();
}
function renderHistory() {
  const search = arabicKey($('side-search').value);
  const matches = text => !search || arabicKey(text).includes(search);
  const opened = chats.find(chat => chat.id === active)?.messages.find(message => message.role === 'user')?.text;
  $('ready-reports').replaceChildren();
  for (const [group, reports] of readyReports) {
    const shown = reports.filter(([, label, question]) => matches(label) || matches(question));
    if (!shown.length) continue;
    $('ready-reports').append(el('h3', 'side-group', group));
    for (const [icon, label, question, kind] of shown) {
      const button = el('button', question === opened ? 'ready-report selected' : 'ready-report');
      button.type = 'button'; button.title = question;
      button.append(sideIcon(icon), el('span', 'side-label', label), el('span', 'side-meta', kind));
      button.onclick = () => runReadyReport(question);
      $('ready-reports').append(button);
    }
  }
  $('history').replaceChildren();
  const found = chats.filter(chat => matches(chat.title));
  const listed = search || showAllChats ? found : found.slice(0, RECENT_LIMIT);
  $('history-heading').textContent = showAllChats && !search ? 'كل المحادثات' : 'أسئلتك الأخيرة';
  $('history-heading').classList.toggle('hidden', !listed.length);
  $('side-empty').classList.toggle('hidden', !search || listed.length > 0 || $('ready-reports').childElementCount > 0);
  const all = $('all-chats');
  all.textContent = showAllChats ? 'عرض الأحدث فقط' : `كل المحادثات (${chats.length})`;
  all.classList.toggle('hidden', chats.length <= RECENT_LIMIT && !showAllChats);
  listed.forEach(chat => {
    const button = el('button', chat.id === active ? 'selected' : '');
    button.append(sideIcon('clock'), el('span', 'side-label', chat.title), el('span', 'side-meta', whenAsked(chat.at)));
    button.onclick = () => { active = chat.id; renderHistory(); renderMessages(); showTab('chat'); };
    button.classList.add('history-open');
    button.title = chat.title;
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
  const clear = $('clear-history');
  clearTimeout(clearConfirmTimer);
  delete clear.dataset.confirm;
  clear.classList.toggle('hidden', !chats.some(chat => chat.id !== active));
  clear.textContent = active ? 'حذف الباقي' : 'حذف الكل';
  clear.title = active ? 'حذف كل المحادثات ما عدا المفتوحة الآن' : 'حذف كل المحادثات';
}
// Deletes every conversation except the open one. The first click asks, the second deletes (2026-10-28).
let clearConfirmTimer = 0;
$('clear-history').onclick = () => {
  const clear = $('clear-history');
  if (!clear.dataset.confirm) {
    clear.dataset.confirm = 'true';
    clear.textContent = `تأكيد الحذف (${chats.filter(chat => chat.id !== active).length})`;
    clearConfirmTimer = setTimeout(renderHistory, 4000);
    return;
  }
  chats = chats.filter(chat => chat.id === active);
  showAllChats = false;
  save(); renderHistory();
};
$('all-chats').onclick = () => { showAllChats = !showAllChats; renderHistory(); };
$('side-search').addEventListener('input', () => renderHistory());
const TABLE_PAGE_SIZE = 50;
// Long result tables show 50 rows at a time. Rows are only hidden, so table and report
// downloads, copy and print still carry every row.
// The pager is a bar attached under the table (design ج, chosen 2026-10-28); in RTL «التالي» moves left, so it points left.
function pageTable(wrap) {
  const rows = [...wrap.querySelectorAll('tbody tr')];
  if (rows.length <= TABLE_PAGE_SIZE) return;
  const pages = Math.ceil(rows.length / TABLE_PAGE_SIZE);
  const pager = el('div', 'table-pager'), nav = el('div', 'table-pager-nav');
  const previous = el('button', '', '›'), next = el('button', '', '‹'), label = el('span'), position = el('span', 'table-pager-position');
  previous.type = next.type = 'button';
  previous.title = 'السابق'; next.title = 'التالي';
  previous.setAttribute('aria-label', 'الصفحة السابقة'); next.setAttribute('aria-label', 'الصفحة التالية');
  let page = 1;
  const show = target => {
    page = Math.min(Math.max(target, 1), pages);
    rows.forEach((row, index) => { row.hidden = Math.floor(index / TABLE_PAGE_SIZE) + 1 !== page; });
    const first = (page - 1) * TABLE_PAGE_SIZE + 1, last = Math.min(page * TABLE_PAGE_SIZE, rows.length);
    label.textContent = `عرض ${first}–${last} من ${rows.length}`;
    position.textContent = `${page} من ${pages}`;
    previous.disabled = page === 1; next.disabled = page === pages;
  };
  previous.onclick = () => { show(page - 1); wrap.scrollIntoView({ block: 'start', behavior: 'smooth' }); };
  next.onclick = () => { show(page + 1); wrap.scrollIntoView({ block: 'start', behavior: 'smooth' }); };
  nav.append(previous, position, next);
  pager.append(label, nav);
  wrap.classList.add('is-paged');
  wrap.after(pager);
  show(1);
}
const SVG_NS = 'http://www.w3.org/2000/svg';
function icon(name) {
  const svg = document.createElementNS(SVG_NS, 'svg'), use = document.createElementNS(SVG_NS, 'use');
  svg.setAttribute('class', 'icon'); svg.setAttribute('aria-hidden', 'true');
  use.setAttribute('href', '#i-' + name); svg.append(use);
  return svg;
}
function button(className, text, iconName) {
  const node = el('button', className); node.type = 'button';
  if (iconName) node.append(icon(iconName));
  node.append(text);
  return node;
}
// A count question gets one number when its plan asked for a count and the total is exact (2026-10-28).
function answerCount(message) {
  if (message.resultType !== 'count' || !message.scope?.exhaustive) return null;
  const count = message.scope.eventCount ?? message.scope.documentCount;
  return Number.isInteger(count) ? count : null;
}
function countNoun(count, mode) {
  const [one, two, few] = mode === 'audit-saved-report' ? ['حدث', 'حدثان', 'أحداث']
    : mode === 'live-recycle-bin' ? ['عنصر', 'عنصران', 'عناصر'] : ['وثيقة', 'وثيقتان', 'وثائق'];
  const tail = count % 100;
  return count === 2 ? two : tail >= 3 && tail <= 10 ? few : one;
}
function rowsNoun(mode) {
  return mode === 'audit-saved-report' ? 'الأحداث' : mode === 'live-recycle-bin' ? 'العناصر' : 'الوثائق';
}
// The card header carries the title, so a leading heading that repeats it is dropped from the body.
function answerTitle(message, article) {
  const first = article.firstElementChild;
  const heading = first?.tagName === 'H2' ? first.textContent.trim() : '';
  if (heading && (!message.title || heading === message.title)) first.remove();
  return message.title || heading || 'تقرير';
}
function answerMeta(message) {
  const meta = el('p', 'answer-meta');
  const repository = message.scope?.repositoryId || message.repositoryId;
  if (repository) meta.append(`المستودع ${repository}`);
  if (message.generatedAt) {
    const time = el('bdi', '', ReportsDownload.timestamp(message).slice(0, 16)); time.dir = 'ltr';
    meta.append(repository ? ' · ' : '', time);
  }
  return meta;
}
function countValue(count, mode, className) {
  const value = el('p', className);
  value.append(el('b', '', count.toLocaleString('en-US')), countNoun(count, mode));
  return value;
}
function pendingCard(message) {
  const card = el('div', 'message assistant answer-card is-pending');
  const head = el('div', 'answer-head'), badge = el('span', 'answer-badge'), heading = el('div', 'answer-heading');
  card.setAttribute('role', 'status');
  badge.append(el('span', 'spinner'));
  heading.append(el('h2', 'answer-title', message.text), el('p', 'answer-meta', 'يمكنك إيقاف السؤال من زر الإيقاف بجانب خانة السؤال.'));
  head.append(badge, heading); card.append(head);
  return card;
}
// Answers are cards (three-pane design, chosen 2026-10-28): a count card for one number and a report card for
// tables and text. Downloads, Laserfiche links and the scope open in the details drawer, pinned on the left.
const answers = new Map();
const shownRows = new Set();
let details = null;
function renderMessages() {
  const container = $('messages');
  container.replaceChildren();
  answers.clear();
  const chat = chats.find(c => c.id === active);
  if (!chat || !chat.messages.length) {
    const welcome = el('div', 'welcome');
    welcome.innerHTML = '<div class="welcome-icon">✦</div><h2>ما التقرير الذي تريد إعداده؟</h2><p>اسأل عن المستودع أو حدد رقم وثيقة. تظهر النتائج كتقرير وجداول مع مصادرها.</p>';
    const suggestions = el('div', 'suggestions');
    for (const question of ['اعرض الوثائق التي أنشئت هذا الشهر', 'كم عدد الوثائق في المستودع؟', 'ما الحقول والقوالب المتاحة للبحث؟']) {
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
      if (message.role === 'user') {
        const item = el('div', 'message user');
        item.append(el('div', 'bubble', message.text));
        container.append(item);
        return;
      }
      if (message.pending && stopQuestion) { container.append(pendingCard(message)); return; }
      const sourcePrefix = `source-${chat.id}-${messageIndex}-${partIndex}`;
      const bubble = el('div', 'bubble');
      const article = ReportsMarkdown.render(message.text || '', message.sources?.length || 0, sourcePrefix);
      bubble.append(article);
      // A reference number opens its document in Laserfiche; there is no source list under the answer (2026-10-28).
      article.querySelectorAll('.report-reference').forEach(reference => {
        const source = message.sources?.[Number(reference.dataset.reference) - 1];
        if (!source) return;
        reference.title = `فتح «${source.documentName || 'الوثيقة'}» في Laserfiche${source.pageNumber ? ` · الصفحة ${source.pageNumber}` : ''}`;
        reference.setAttribute('aria-label', `فتح المرجع ${reference.dataset.reference} في Laserfiche`);
        reference.onclick = async () => {
          const epoch = sessionEpoch;
          const preview = window.open('about:blank', 'laserfiche-report-results');
          const problem = text => {
            const note = bubble.querySelector('.reference-error') || el('p', 'report-scope reference-error');
            note.setAttribute('role', 'alert'); note.textContent = text; bubble.append(note);
          };
          reference.disabled = true;
          try {
            const result = await api('/api/reports/laserfiche-links', {method:'POST', headers:{'Content-Type':'application/json'},
              body:JSON.stringify({repositoryId:message.scope?.repositoryId || message.repositoryId, entryIds:[source.entryId], openSingle:true})});
            if (epoch !== sessionEpoch) { preview?.close(); return; }
            if (preview && result.urls?.[0]) preview.location.replace(result.urls[0]);
            else { preview?.close(); problem('اسمح بفتح نافذة الوثيقة وأعد المحاولة.'); }
          } catch(error) { preview?.close(); problem(error.message); }
          finally { reference.disabled = false; }
        };
      });
      // No per-table download: the drawer's downloads already export every table (2026-10-28).
      bubble.querySelectorAll('.report-table-wrap').forEach(pageTable);
      const qualityLabel = ReportsDownload.qualityLabel(message.quality);
      if (qualityLabel) bubble.prepend(el('div', 'report-scope report-quality', qualityLabel));
      // Live reports already end their summary with the scope line, so the note shows only what the answer does not say.
      const scopeDetail = message.scope?.detail && ReportsMarkdown.render(message.scope.detail, 0);
      const flat = node => node.textContent.replace(/\s+/g, ' ').trim();
      if (scopeDetail && !flat(article).includes(flat(scopeDetail))) {
        const scope = el('div', 'report-scope'); scope.append(scopeDetail);
        scope.setAttribute('role', 'note'); bubble.prepend(scope);
      }
      // Errors, clarifications and plain replies stay a simple bubble with nothing to download.
      if (!(message.generatedAt || message.sources?.length) || message.kind === 'error' || message.kind === 'clarification' ||
          message.resultType === 'clarification') {
        const item = el('div', 'message assistant');
        item.append(bubble);
        container.append(item);
        return;
      }
      const key = `${messageIndex}-${partIndex}`;
      const originalQuestion = chat.messages.slice(0, messageIndex).reverse().find(previous => previous.role === 'user')?.text;
      const view = { key, message, count: answerCount(message), title: answerTitle(message, article), body: bubble,
        reportQuestion: message.title ? message.title + ' — ' + originalQuestion : originalQuestion };
      const card = view.card = el('div', `message assistant answer-card ${view.count === null ? 'report-card' : 'count-card'}`);
      const head = el('div', 'answer-head'), badge = el('span', 'answer-badge'), heading = el('div', 'answer-heading');
      badge.append(icon(view.count === null ? 'table' : 'hash'));
      heading.append(el('h2', 'answer-title', view.title));
      const open = view.detailsButton = button('answer-action answer-details', view.count === null ? 'التفاصيل والتحميل' : 'التفاصيل', 'panel');
      open.setAttribute('aria-controls', 'details'); open.setAttribute('aria-expanded', 'false');
      open.onclick = () => details?.key === key ? closeDetails() : openDetails(key);
      if (view.count === null) {
        heading.append(answerMeta(message));
        head.append(badge, heading, open);
      } else {
        const mode = message.scope.mode;
        heading.append(countValue(view.count, mode, 'count-value'), answerMeta(message));
        const actions = el('div', 'answer-actions');
        // A live count also lists what it counted; the rows stay folded under the number until asked for.
        bubble.id = `answer-rows-${chat.id}-${key}`;
        bubble.hidden = true;
        if (view.count > 0 && bubble.querySelector('.report-table tbody tr')) {
          const toggle = button('answer-action', '');
          toggle.setAttribute('aria-controls', bubble.id);
          const rowsId = `${chat.id}:${key}`;
          view.rowsLabel = () => bubble.hidden ? `اعرض ${rowsNoun(mode)}` : `إخفاء ${rowsNoun(mode)}`;
          view.setRows = show => {
            bubble.hidden = !show;
            if (show) shownRows.add(rowsId); else shownRows.delete(rowsId);
            toggle.replaceChildren(icon('table'), view.rowsLabel());
            toggle.setAttribute('aria-expanded', String(show));
            if (view.drawerToggle?.isConnected) view.drawerToggle.replaceChildren(icon('table'), view.rowsLabel());
          };
          toggle.onclick = () => view.setRows(bubble.hidden);
          view.setRows(shownRows.has(rowsId));
          actions.append(toggle);
        }
        actions.append(open);
        head.append(badge, heading, actions);
      }
      card.append(head, bubble);
      // With the drawer open, clicking another answer shows that answer's details.
      card.addEventListener('click', event => {
        if (!details || details.key === key || event.target.closest('button, a, summary, select, .report-table-wrap')) return;
        openDetails(key, false);
      });
      answers.set(key, view);
      container.append(card);
      });
    });
  }
  if (!historySaved) {
    const warning = el('p', 'report-scope', 'تعذر حفظ المحادثة في المتصفح. التقرير متاح الآن؛ حمّله قبل إغلاق الصفحة.');
    warning.setAttribute('role', 'status'); container.append(warning);
  }
  if (details && details.chat === active && answers.has(details.key)) showDetails(details.key);
  else if (details) closeDetails(false);
  container.scrollTop = container.scrollHeight;
}
function openDetails(key, focus = true) {
  details = { chat: active, key };
  showDetails(key);
  $('details').classList.add('open');
  $('chat-view').classList.add('details-open');
  if (focus) $('details').querySelector('.details-close').focus({ preventScroll: true });
}
function closeDetails(restoreFocus = true) {
  const opener = details && answers.get(details.key)?.detailsButton;
  details = null;
  $('details').classList.remove('open');
  $('chat-view').classList.remove('details-open');
  answers.forEach(view => { view.card.classList.remove('is-selected'); view.detailsButton.setAttribute('aria-expanded', 'false'); });
  if (restoreFocus && opener?.isConnected) opener.focus({ preventScroll: true });
}
function showDetails(key) {
  answers.forEach((view, other) => {
    view.card.classList.toggle('is-selected', other === key);
    view.detailsButton.setAttribute('aria-expanded', String(other === key));
  });
  fillDetails(answers.get(key));
}
function fillDetails(view) {
  const { message, count } = view;
  const drawer = $('details');
  drawer.replaceChildren();
  const feedback = el('p', 'details-feedback'); feedback.setAttribute('role', 'status');
  const say = text => { feedback.textContent = text; };
  const section = (title, iconName) => {
    const node = el('section', 'details-section');
    if (title) { const heading = el('h3'); heading.append(icon(iconName), title); node.append(heading); }
    drawer.append(node);
    return node;
  };
  const copy = (text, done) => async () => {
    try { await navigator.clipboard.writeText(text); say(done); } catch { say('تعذر النسخ'); }
  };
  const cover = el('div', 'details-cover');
  const close = button('details-close', '', 'close');
  close.title = 'إغلاق (Esc)'; close.setAttribute('aria-label', 'إغلاق التفاصيل');
  close.onclick = () => closeDetails();
  cover.append(close, el('h2', '', view.title));
  if (count !== null) cover.append(countValue(count, message.scope.mode, 'details-count'));
  cover.append(answerMeta(message));
  drawer.append(cover);
  const laserfiche = (className, label) => {
    const open = button(className, label, 'ext');
    const links = el('div', 'details-open-links');
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
        links.replaceChildren(...result.urls.map((url, i) => {
          const a = el('a', 'source-link', result.urls.length === 1 ? `عرض ${result.documentCount} وثيقة في Laserfiche ↗` : `فتح المجموعة ${i + 1} من ${result.urls.length} ↗`);
          a.href = url; a.target = 'laserfiche-report-results'; a.rel = 'noopener noreferrer';
          return a;
        }));
        if (result.urls.length === 1 && preview) preview.location.replace(result.urls[0]);
        else if (!reusedWindow) preview?.close();
      } catch (error) { if (!reusedWindow) preview?.close(); say(error.message); }
      finally { open.disabled = false; }
    };
    return [open, links];
  };
  const rows = count === null || view.setRows;
  if (count !== null) {
    const actions = section();
    if (view.setRows) {
      const show = view.drawerToggle = button('details-primary', view.rowsLabel(), 'table');
      show.onclick = () => { view.setRows(view.body.hidden); if (!view.body.hidden) view.card.scrollIntoView({ block: 'nearest', behavior: 'smooth' }); };
      actions.append(show);
    }
    actions.append(...laserfiche(view.setRows ? 'details-ghost' : 'details-primary', 'فتح في Laserfiche'));
    const links = el('div', 'details-links');
    const copyNumber = button('', 'نسخ الرقم'); copyNumber.onclick = copy(String(count), 'تم نسخ الرقم');
    links.append(copyNumber); actions.append(links);
  }
  if (rows) {
    const downloads = section(count === null ? 'تحميل التقرير كاملًا' : `تحميل قائمة ${rowsNoun(message.scope.mode)}`, 'down');
    const download = format => {
      try { ReportsDownload.download(message, view.reportQuestion, format); say(''); }
      catch (error) { say(error.message || 'تعذر التحميل'); }
    };
    const formats = el('div', 'details-formats');
    for (const [format, label] of [['docx', 'Word'], ['xlsx', 'Excel'], ['pdf', 'PDF'], ['html', 'HTML']]) {
      const item = button('', label);
      item.setAttribute('aria-label', `تحميل التقرير بصيغة ${label}`);
      if (format === 'pdf') item.title = 'يفتح نافذة الطباعة؛ اختر الحفظ بصيغة PDF';
      item.onclick = () => download(format);
      formats.append(item);
    }
    const links = el('div', 'details-links');
    const copyReport = button('', 'نسخ التقرير'); copyReport.onclick = copy(message.text, 'تم نسخ التقرير');
    // Printing uses the same designed document as PDF, with every table row (2026-10-28).
    const print = button('', 'طباعة'); print.onclick = () => download('pdf');
    const markdown = button('', 'Markdown'); markdown.onclick = () => download('md');
    links.append(copyReport, print, markdown);
    downloads.append(formats, links, feedback);
  }
  if (count === null) section().append(...laserfiche('details-primary', 'فتح وثائق التقرير في Laserfiche'));
  const scope = section(count === null ? 'نطاق التقرير' : 'ماذا عُدّ بالضبط', 'search');
  const text = el('div', 'details-text');
  if (count === null) { if (message.scope?.detail) text.append(ReportsMarkdown.render(message.scope.detail, 0)); }
  else view.body.querySelectorAll('.report-body > p').forEach(paragraph => text.append(paragraph.cloneNode(true)));
  if (text.childNodes.length) scope.append(text);
  const facts = el('dl');
  const fact = (term, value) => {
    if (!value) return;
    const dd = el('dd'); dd.append(value);
    facts.append(el('dt', '', term), dd);
  };
  fact('المستودع', message.scope?.repositoryId || message.repositoryId);
  if (message.generatedAt) { const time = el('bdi', '', ReportsDownload.timestamp(message)); time.dir = 'ltr'; fact('وقت الإعداد', time); }
  fact('الوثائق المرتبطة', message.relatedEntryIds?.length ? String(message.relatedEntryIds.length) : '');
  fact('المراجع', message.sources?.length ? String(message.sources.length) : '');
  fact('التقييم', ReportsDownload.qualityLabel(message.quality));
  scope.append(facts);
  if (message.scope?.mode !== 'audit-saved-report') {
    const note = el('p', 'details-permission');
    note.append(icon('shield'), `تُحسب النتائج والأعداد بصلاحيات حساب ${$('signed-in-user').textContent} فقط؛ قد يرى مستخدم آخر نتائج مختلفة.`);
    section().append(note);
  }
  if (!rows) {
    const note = section(); note.append(el('p', 'details-note', 'الإجابة رقم واحد، فلا يوجد ملف للتحميل.'), feedback);
  }
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
// ── Deleted documents ──────────────────────────────────────────────────────
// The Repository API cannot read the Recycle Bin, and the model must never guess deletions
// (it once answered with the documents modified today). Reports opened by the Web Client
// button asks that Web Client tab, which reads the Recycle Bin with the user's own session:
// each user gets their own deletions, administrators everything (see lf-reports-button.js).
function arabicKey(text) {
  return String(text || '').normalize('NFC').replace(/[ؐ-ًؚ-ٰٟـ]/g, '')
    .replace(/[أإآ]/g, 'ا').replace(/ة/g, 'ه').replace(/ى/g, 'ي')
    .replace(/[٠-٩]/g, digit => '٠١٢٣٤٥٦٧٨٩'.indexOf(digit)).replace(/\s+/g, ' ').trim().toLowerCase();
}
const WORD_START = '(?<![\\p{L}\\p{N}])[وفبل]{0,2}', WORD_END = '(?![\\p{L}\\p{N}])';
function isDeletedQuestion(question) {
  return new RegExp(WORD_START + '(?:ال)?(?:محذوف|حذف|تحذف|يحذف|انحذف|اتحذف|اتمسح|سله المحذوفات)', 'u').test(arabicKey(question)) ||
    /\b(?:deleted|recycle bin)\b/i.test(question);
}
// One deletion period named in the question: null when none, undefined when several.
function deletedPeriod(question, today = new Date()) {
  const text = arabicKey(question);
  const y = today.getFullYear(), m = today.getMonth(), d = today.getDate();
  const day = offset => [new Date(y, m, d + offset), new Date(y, m, d + offset + 1)];
  const week = offset => [new Date(y, m, d - today.getDay() + 7 * offset), new Date(y, m, d - today.getDay() + 7 * offset + 7)];
  const month = offset => [new Date(y, m + offset, 1), new Date(y, m + offset + 1, 1)];
  const year = offset => [new Date(y + offset, 0, 1), new Date(y + offset + 1, 0, 1)];
  const phrases = [
    ['أول أمس', day(-2), '(?:اول (?:من )?(?:امس|امبارح)|قبل امس)'],
    ['أمس', day(-1), '(?:(?:ال)?(?:امس|بارحه)|امبارح)'],
    ['اليوم', day(0), '(?:اليوم|النهارده|انهارده)'],
    ['الأسبوع الماضي', week(-1), 'الاسبوع (?:الماضي|السابق|الفائت|اللي فات)'],
    ['هذا الأسبوع', week(0), '(?:(?:هذا|هاذا) الاسبوع|الاسبوع (?:الحالي|الجاري|ده))'],
    ['الشهر الماضي', month(-1), 'الشهر (?:الماضي|السابق|الفائت|اللي فات)'],
    ['هذا الشهر', month(0), '(?:(?:هذا|هاذا) الشهر|الشهر (?:الحالي|الجاري|ده))'],
    ['السنة الماضية', year(-1), '(?:السنه|العام) (?:الماضي|الماضيه|السابق|السابقه|الفائت|الفائته|اللي فاتت)'],
    ['هذه السنة', year(0), '(?:(?:هذه|هذي|هاذي) السنه|(?:هذا|هاذا) العام|(?:السنه|العام) (?:الحالي|الحاليه|الجاري|الجاريه|دي))'],
  ];
  let rest = text;
  const found = [];
  for (const [label, bounds, pattern] of phrases) {
    const phrase = new RegExp(WORD_START + pattern + WORD_END, 'u');
    if (phrase.test(rest)) { found.push({ label, bounds }); rest = rest.replace(phrase, ' '); }
  }
  // An explicit date is day/month/year, or ISO year-month-day.
  const explicit = /(?<!\d)(\d{1,2})\/(\d{1,2})\/(\d{4})(?!\d)|(?<!\d)(\d{4})-(\d{1,2})-(\d{1,2})(?!\d)/.exec(rest);
  if (explicit) {
    const [dd, mm, yyyy] = explicit[1] ? [+explicit[1], +explicit[2], +explicit[3]] : [+explicit[6], +explicit[5], +explicit[4]];
    const start = new Date(yyyy, mm - 1, dd);
    if (start.getMonth() === mm - 1 && start.getDate() === dd)
      found.push({ label: `${yyyy}-${String(mm).padStart(2, '0')}-${String(dd).padStart(2, '0')}`, bounds: [start, new Date(yyyy, mm - 1, dd + 1)] });
  }
  if (found.length > 1) return undefined;
  return found.length ? { label: found[0].label, start: found[0].bounds[0], end: found[0].bounds[1] } : null;
}
// Recycle Bin dates arrive as M/D/YYYY h:mm AM/PM (the bridge requests the en-US locale).
function parseRecycleDate(text) {
  const match = /^(\d{1,2})\/(\d{1,2})\/(\d{4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?\s*(AM|PM)?$/i.exec(String(text || '').trim());
  if (!match) return null;
  const hour = match[7] ? +match[4] % 12 + (/pm/i.test(match[7]) ? 12 : 0) : +match[4];
  return new Date(+match[3], +match[1] - 1, +match[2], hour, +match[5], +(match[6] || 0));
}
function formatRecycleDate(date) {
  const two = value => String(value).padStart(2, '0');
  return date ? `${date.getFullYear()}-${two(date.getMonth() + 1)}-${two(date.getDate())} ${two(date.getHours())}:${two(date.getMinutes())}` : '';
}
function requestRecycleBin() {
  return new Promise((resolve, reject) => {
    const source = window.opener;
    if (!source || source.closed)
      return reject(new Error('لعرض الوثائق المحذوفة افتح التقارير من زر Reports في Laserfiche Web Client، وأبقِ نافذة Web Client مفتوحة؛ سلة المحذوفات تُقرأ من خلالها فقط.'));
    // Web Client and Reports run on the same server; only that window may answer.
    const webClientOrigin = `${location.protocol}//${location.hostname}`;
    const requestId = crypto.randomUUID();
    const timer = setTimeout(() => { window.removeEventListener('message', listener);
      reject(new Error('لم تستجب نافذة Laserfiche Web Client. تأكد أنها ما زالت مفتوحة على المستودع ثم أعد السؤال.')); }, 60000);
    function listener(event) {
      if (event.source !== source || event.origin !== webClientOrigin || event.data?.type !== 'lf-reports:recycle-bin-result' ||
          event.data.requestId !== requestId) return;
      clearTimeout(timer);
      window.removeEventListener('message', listener);
      if (event.data.ok) { resolve(event.data); return; }
      const errors = {
        unauthorized: 'انتهت جلسة Laserfiche Web Client. سجّل الدخول فيها ثم أعد السؤال.',
        repository_mismatch: `نافذة Web Client مفتوحة على المستودع «${event.data.repository}». افتح المستودع نفسه ثم أعد السؤال.`,
        no_repository: 'افتح المستودع في Laserfiche Web Client ثم أعد السؤال.',
        too_many: 'سلة المحذوفات تحتوي أكثر من 10000 عنصر؛ حدد فترة أو اسم من حذفها.'
      };
      reject(new Error(errors[event.data.error] || `تعذر قراءة سلة المحذوفات من Web Client (${event.data.error})، فلم تُعرض نتائج غير مؤكدة.`));
    }
    window.addEventListener('message', listener);
    source.postMessage({ type: 'lf-reports:recycle-bin', requestId, repository: sessionRepository }, webClientOrigin);
  });
}
async function deletedDocumentsReport(question) {
  const period = deletedPeriod(question);
  if (period === undefined) throw new Error('حدد فترة واحدة للحذف، مثل اليوم أو هذا الأسبوع أو هذا الشهر.');
  const listing = await requestRecycleBin();
  const text = arabicKey(question);
  const escaped = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  // «من مجلد الوارد» names an original location; any folder of that name, at any depth.
  const segments = [...new Set(listing.rows.flatMap(row => String(row.location || '').split('\\').filter(Boolean)))];
  const folderMention = name => new RegExp('مجلد\\s+' + escaped(arabicKey(name)) + WORD_END, 'u');
  const folder = segments.sort((a, b) => b.length - a.length).find(name => folderMention(name).test(text));
  const typeText = folder ? text.replace(folderMention(folder), ' ') : text;
  const documents = /(?:وثيق|وثائق|مستند|ملف|معامل)/.test(typeText), folders = /مجلد/.test(typeText);
  const type = folders && !documents ? 'Folder' : documents && !folders ? 'Document' : null;
  const deleter = [...new Set(listing.rows.map(row => row.deletedBy).filter(Boolean))]
    .find(name => new RegExp(WORD_START + escaped(arabicKey(name)) + WORD_END, 'u').test(text));
  const rows = [];
  for (const row of listing.rows) {
    if (type && row.type !== type) continue;
    if (deleter && arabicKey(row.deletedBy) !== arabicKey(deleter)) continue;
    if (folder && !String(row.location || '').split('\\').some(part => arabicKey(part) === arabicKey(folder))) continue;
    const deletedAt = parseRecycleDate(row.deleted);
    if (period) {
      if (!deletedAt) throw new Error('تعذر قراءة تاريخ الحذف من سلة المحذوفات، فلم تُعرض نتائج غير مؤكدة.');
      if (deletedAt < period.start || deletedAt >= period.end) continue;
    }
    rows.push({ ...row, deletedAt });
  }
  rows.sort((a, b) => (b.deletedAt?.getTime() || 0) - (a.deletedAt?.getTime() || 0));
  const title = [type === 'Folder' ? 'المجلدات المحذوفة' : type === 'Document' ? 'الوثائق المحذوفة' : 'العناصر المحذوفة',
    folder ? 'من مجلد ' + folder : null, period?.label, deleter ? 'حذفها ' + deleter : null].filter(Boolean).join(' — ');
  const detail = `عدد النتائج المطابقة: **${rows.length}** — يشمل عدد الوثائق لك صلاحية الوصول لها.`;
  const cell = value => String(value ?? '').trim()
    ? String(value).replace(/\\/g, '\\\\').replace(/\|/g, '\\|').replace(/\r?\n/g, ' ') : '—';
  const table = '| رقم الوثيقة | الاسم | النوع | المكان الأصلي | تاريخ الحذف | حذفها | تاريخ الإنشاء |\n| --- | --- | --- | --- | --- | --- | --- |\n' +
    (rows.length ? rows.map(row => `| ${cell(row.id)} | ${cell(row.name)} | ${row.type === 'Folder' ? 'مجلد' : 'وثيقة'} | ${cell(row.location)} | ` +
      `${cell(formatRecycleDate(row.deletedAt) || row.deleted)} | ${cell(row.deletedBy)} | ${cell(formatRecycleDate(parseRecycleDate(row.created)) || row.created)} |`).join('\n')
      : '| — | لا توجد عناصر محذوفة مطابقة | — | — | — | — | — |');
  return { role: 'assistant', kind: 'answer', repositoryId: sessionRepository, generatedAt: new Date().toISOString(),
    scope: { detail }, text: `# ${title}\n\n${detail}\n\n${table}` };
}
// While a question runs, the send button is a stop button. Stopping cancels the request, and the server
// stops the search with it (the chat endpoint passes the request's cancellation token on).
let stopQuestion = null;
function setAnswering(answering) {
  const send = $('send');
  send.classList.toggle('is-stop', answering);
  send.type = answering ? 'button' : 'submit';
  send.title = answering ? 'إيقاف الإجابة' : 'إرسال';
  send.setAttribute('aria-label', send.title);
}
$('send').addEventListener('click', () => stopQuestion?.abort());
$('ask-form').onsubmit = async event => {
  event.preventDefault();
  if (stopQuestion) return;
  const question = $('question').value.trim();
  if (!question) return;
  if (!active) {
    active = crypto.randomUUID();
    chats.unshift({ id: active, title: question.slice(0, 45), messages: [] });
  }
  const epoch = sessionEpoch;
  const chat = chats.find(c => c.id === active);
  pendingOperations++;
  chat.at = Date.now();
  chat.messages.push({ role: 'user', text: question });
  $('question').value = '';
  const stop = stopQuestion = new AbortController();
  setAnswering(true);
  chat.messages.push({ role: 'assistant', pending: true, text: 'جاري البحث في الوثائق وتحضير الإجابة...' });
  renderHistory(); renderMessages();
  try {
    if (isDeletedQuestion(question)) {
      const stopped = new Promise((_, reject) => stop.signal.addEventListener('abort', () => reject(stop.signal.reason), { once: true }));
      const report = await Promise.race([deletedDocumentsReport(question), stopped]);
      if (epoch === sessionEpoch && chats.includes(chat)) chat.messages[chat.messages.length - 1] = report;
      return;
    }
    const result = await api('/api/reports/chat', { method: 'POST', headers: { 'Content-Type': 'application/json' }, signal: stop.signal, body: JSON.stringify({ question, history: conversationHistory(chat.messages.slice(0, -2)) }) });
    if (epoch !== sessionEpoch || !chats.includes(chat)) return;
    chat.messages[chat.messages.length - 1] = { role: 'assistant', kind: result.isClarification ? 'clarification' : 'answer', clarificationQuestion: result.clarificationQuestion, repositoryId: sessionRepository, relatedEntryIds: result.relatedEntryIds, reports: result.reports, text: result.answer, sources: result.sources, scope: result.scope, generatedAt: result.generatedAt, quality: result.quality };
  } catch (error) {
    // A stopped question is an error turn, so the next question does not take it as context.
    if (stop.signal.aborted) {
      chat.messages[chat.messages.length - 1] = { role: 'assistant', kind: 'error', text: 'تم إيقاف السؤال قبل اكتمال الإجابة.' };
      return;
    }
    // An expired LFDS session signs in again silently, then this question is sent again.
    if (error.status === 401 && ssoEnabled && epoch === sessionEpoch && allowAutoSso()) {
      chat.messages.splice(-2, 2);
      try { sessionStorage.setItem('reports-pending-question', JSON.stringify({ chat: chat.id, question })); } catch {}
      save();
      startSso(false);
      return;
    }
    chat.messages[chat.messages.length - 1] = { role: 'assistant', kind: 'error', errorCode: error.code, text: `تعذر إكمال السؤال: ${error.message}` };
  } finally {
    pendingOperations--;
    if (stopQuestion === stop) { stopQuestion = null; setAnswering(false); }
    if (epoch === sessionEpoch) { save(); renderMessages(); }
  }
};
function resendPendingQuestion() {
  let pending = null, resume = null;
  try { pending = JSON.parse(sessionStorage.getItem('reports-pending-question') || 'null'); sessionStorage.removeItem('reports-pending-question'); } catch {}
  try { resume = JSON.parse(sessionStorage.getItem('reports-resume') || 'null'); sessionStorage.removeItem('reports-resume'); } catch {}
  if (resume?.chat && chats.some(c => c.id === resume.chat)) { active = resume.chat; renderHistory(); renderMessages(); }
  if (!pending?.question) { if (resume?.draft) $('question').value = resume.draft; return; }
  if (chats.some(c => c.id === pending.chat)) { active = pending.chat; renderHistory(); renderMessages(); }
  $('question').value = pending.question;
  $('ask-form').requestSubmit();
}
// Keep the Laserfiche session open while Reports is in use: the Repository API closes
// sessions idle for 15 minutes. Stops after an hour without activity, freeing the license.
let lastActivity = Date.now();
['pointerdown', 'keydown'].forEach(type => document.addEventListener(type, () => { lastActivity = Date.now(); }, { passive: true }));
setInterval(() => {
  if (!$('login-layer').classList.contains('hidden') || Date.now() - lastActivity > 3600000) return;
  api('/api/session/keepalive', { method: 'POST' }).catch(() => {});
}, 300000);
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
  $('logout').disabled = signingOut;
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
  const epoch = sessionEpoch;
  const currentScan = scan;
  scan.notice = '';
  renderScan();
  const seenFolders = new Set(scan.seenFolders);
  const seenDocuments = new Set(scan.seenDocuments);
  try {
    while (epoch === sessionEpoch && scan === currentScan && !pauseScan && (scan.folders.length || scan.documents.length)) {
      if (scan.documents.length) {
        const id = scan.documents[0];
        scan.current = `فهرسة الوثيقة ${id}`; renderScan();
        try {
          const result = await api(`/api/ingestion/laserfiche/${id}`, { method: 'POST' });
          if (epoch !== sessionEpoch || scan !== currentScan) break;
          scan.chunks += result.chunkCount || 0;
          scan.documentsDone++;
          if (result.wasSkipped) scan.skipped = (scan.skipped || 0) + 1;
        } catch (error) {
          if (signingOut || epoch !== sessionEpoch || scan !== currentScan) break;
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
          if (epoch !== sessionEpoch || scan !== currentScan) break;
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
          if (signingOut || epoch !== sessionEpoch || scan !== currentScan) break;
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
    if (epoch === sessionEpoch && scan === currentScan) {
      scan.current = '';
      saveScan(); renderScan();
      if (!signingOut) loadDocuments();
    }
  }
};
const launch = new URLSearchParams(location.search);
const ssoError = launch.get('sso_error');
if (ssoError || launch.has('repository')) history.replaceState(null, '', '/');
api('/api/session/status').then(session => {
  ssoEnabled = session.sso === true;
  applyLoginMode();
  $('repository-id').value = launch.get('repository') || session.repository || '';
  if (session.authenticated && session.username) {
    openSession(session.username, session.repository, session.server, session.generation);
    resendPendingQuestion();
    return;
  }
  showLogin();
  if (ssoError) { $('login-error').textContent = ssoErrors[ssoError] || ssoErrors.exchange; return; }
  // Sign in automatically like the Dashboard.
  if (ssoEnabled && allowAutoSso()) startSso(false);
}).catch(() => showLogin());

$('switch-repository').onclick = () => {
  if (scanning || pendingOperations) { $('active-repository').title='أوقف الفهرسة وانتظر اكتمال الطلب قبل التبديل'; return; }
  showLogin(true);
  $('password').value=''; $('repository-id').focus(); $('repository-id').select();
};
// Back from the repository picker keeps the open session as it was: the back button, Escape, or a click outside the card.
function closeRepositoryPicker() {
  if ($('login-back').classList.contains('hidden')) return;
  $('login-layer').classList.add('hidden');
  $('repository-id').value = sessionRepository;
  $('login-error').textContent = '';
  $('switch-repository').focus();
}
$('login-back').onclick = closeRepositoryPicker;
$('login-layer').addEventListener('click', event => { if (event.target === $('login-layer')) closeRepositoryPicker(); });
document.addEventListener('keydown', event => {
  if (event.key !== 'Escape') return;
  if (!$('login-layer').classList.contains('hidden')) closeRepositoryPicker();
  else if (details) closeDetails();
});
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
