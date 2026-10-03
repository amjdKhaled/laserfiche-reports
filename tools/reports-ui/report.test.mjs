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
  return window;
}
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
    ? { authenticated: true, username: 'tester' } : { ...message, sources: [] } });
  window.eval(readFileSync(root + 'app.js', 'utf8'));
  await new Promise(resolve => setTimeout(resolve, 20));
  window.document.getElementById('question').value = 'عدد الوثائق؟';
  window.document.getElementById('ask-form').dispatchEvent(new window.Event('submit', { cancelable: true }));
  await new Promise(resolve => setTimeout(resolve, 20));
  assert([...window.document.querySelectorAll('.report-actions button')].some(node => node.textContent === 'تحميل التقرير'));
  const saved = JSON.parse(window.localStorage.getItem('laserfiche-reports-chat-v1:tester'));
  assert.equal(saved[0].messages[1].generatedAt, message.generatedAt);
  assert.equal(saved[0].messages[1].scope.detail, message.scope.detail);
  await window.happyDOM.abort();
});
test('storage exhaustion keeps the received report downloadable and shows a warning', async () => {
  const window = setup();
  window.document.write(readFileSync(root + 'index.html', 'utf8').replace(/<script[^>]*>[\s\S]*?<\/script>/g, ''));
  window.fetch = async url => ({ ok: true, status: 200, json: async () => url === '/api/session/status'
    ? { authenticated: true, username: 'tester' } : { answer: message.text, ...message } });
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
