/* Audit setup stays scoped to the active repository; no background exports. */
let auditLoadedFor = '';
async function loadAuditSettings() {
  const epoch = sessionEpoch;
  const data = await api('/api/audit-trail/settings');
  if (epoch !== sessionEpoch) return;
  const report = data.report || {};
  $('audit-enabled').checked = data.enabled;
  $('audit-url').value = data.baseUrl || '';
  $('audit-source').value = report.dataSource || '';
  $('audit-report-id').value = report.reportId || '';
  $('audit-export').value = report.exportDirectory || '';
  $('audit-scope').value = report.scopeDescription || '';
  $('audit-date-format').value = report.dateFormat || '';
  $('audit-columns').value = JSON.stringify(report.columns || {}, null, 2);
  $('audit-labels').value = JSON.stringify(report.eventLabels || {}, null, 2);
  $('audit-user').value = data.windowsUsername || '';
  $('audit-password').value = '';
  $('audit-state').textContent = data.configured ? 'مهيأ — افحص الاتصال' : 'الربط غير مهيأ';
  $('audit-feedback').textContent = '';
  auditLoadedFor = `${sessionGeneration}:${sessionRepository}`;
}
$('audit-setup').addEventListener('toggle', () => {
  if ($('audit-setup').open && auditLoadedFor !== `${sessionGeneration}:${sessionRepository}`)
    loadAuditSettings().catch(error => { $('audit-feedback').textContent = error.message; });
});
document.addEventListener('reports-session-opened', () => {
  auditLoadedFor = '';
  $('audit-state').textContent = '—';
  $('audit-password').value = ''; $('audit-user').value = '';
  if ($('audit-setup').open) loadAuditSettings().catch(error => { $('audit-feedback').textContent = error.message; });
});
function auditRequest() {
  const mapping = id => {
    const value = JSON.parse($(id).value || '{}');
    if (!value || Array.isArray(value) || typeof value !== 'object' || Object.values(value).some(v => typeof v !== 'string'))
      throw new Error('أدخل خرائط الأعمدة والأحداث كأسماء وقيم نصية بصيغة JSON.');
    return value;
  };
  return { enabled: $('audit-enabled').checked, baseUrl: $('audit-url').value.trim(), report: {
    dataSource: $('audit-source').value.trim(), reportId: $('audit-report-id').value.trim(),
    exportDirectory: $('audit-export').value.trim(), scopeDescription: $('audit-scope').value.trim(),
    dateFormat: $('audit-date-format').value.trim() || null,
    columns: mapping('audit-columns'), eventLabels: mapping('audit-labels') } };
}
async function saveAuditSettings() {
  const request = auditRequest();
  const username = $('audit-user').value.trim(), password = $('audit-password').value;
  if (password && !username) throw new Error('أدخل اسم حساب Windows مع كلمة المرور.');
  await api('/api/audit-trail/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request) });
  if (password) {
    await api('/api/audit-trail/credentials', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username, password }) });
    $('audit-password').value = '';
  }
  $('audit-state').textContent = request.enabled ? 'مهيأ — افحص الاتصال' : 'الربط غير مفعّل';
}
async function auditAction(test) {
  $('audit-save').disabled = true; $('audit-test').disabled = true;
  $('audit-feedback').textContent = test ? 'جارٍ حفظ الإعدادات وتصدير التقرير للتحقق من الاتصال…' : 'جارٍ الحفظ…';
  try {
    await saveAuditSettings();
    if (test) {
      const result = await api('/api/audit-trail/test', { method: 'POST' });
      $('audit-state').textContent = 'متصل';
      $('audit-feedback').textContent = `نجح الاتصال والتصدير: ${result.eventCount} حدث ضمن نطاق التقرير. ${result.scope}`;
    } else $('audit-feedback').textContent = 'حُفظت إعدادات الربط. استخدم فحص الاتصال للتحقق من التصدير والقراءة.';
  } catch (error) { $('audit-feedback').textContent = error instanceof SyntaxError ? 'صيغة JSON غير صحيحة في خريطة الأعمدة أو الأحداث.' : error.message; }
  finally { $('audit-save').disabled = false; $('audit-test').disabled = false; }
}
$('audit-form').onsubmit = event => { event.preventDefault(); auditAction(false); };
$('audit-test').onclick = () => auditAction(true);
