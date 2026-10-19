/* Current recycle-bin metadata, read with the signed-in repository account. */
let recycleLoadedFor = '';
async function loadRecycleSettings() {
  const epoch = sessionEpoch;
  const data = await api('/api/recycle-bin/settings');
  if (epoch !== sessionEpoch) return;
  $('recycle-enabled').checked = data.binding.enabled;
  $('recycle-secure').checked = data.binding.secure;
  $('recycle-server').value = data.binding.serverName || '';
  $('recycle-repository').value = data.binding.repositoryName || '';
  $('recycle-feedback').textContent = data.reason || 'الربط مهيأ؛ اعرض الوثائق للتحقق من الاتصال.';
  recycleLoadedFor = `${sessionGeneration}:${sessionRepository}`;
}
$('recycle-setup').addEventListener('toggle', () => {
  if ($('recycle-setup').open && recycleLoadedFor !== `${sessionGeneration}:${sessionRepository}`)
    loadRecycleSettings().catch(error => { $('recycle-feedback').textContent = error.message; });
});
document.addEventListener('reports-session-cleared', () => {
  recycleLoadedFor = '';
  $('recycle-result').replaceChildren();
  $('recycle-feedback').textContent = '';
});
document.addEventListener('reports-session-opened', () => {
  recycleLoadedFor = '';
  $('recycle-result').replaceChildren();
  if ($('recycle-setup').open) loadRecycleSettings().catch(error => { $('recycle-feedback').textContent = error.message; });
});
async function recycleAction(view) {
  const epoch = sessionEpoch;
  $('recycle-save').disabled = $('recycle-view').disabled = true;
  $('recycle-result').replaceChildren();
  $('recycle-feedback').textContent = view ? 'جارٍ قراءة سلة المحذوفات…' : 'جارٍ حفظ الربط…';
  try {
    await api('/api/recycle-bin/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
      enabled: $('recycle-enabled').checked, secure: $('recycle-secure').checked,
      serverName: $('recycle-server').value.trim(), repositoryName: $('recycle-repository').value.trim() }) });
    if (epoch !== sessionEpoch) return;
    if (view) {
      const result = await api(`/api/reports/recycle-bin?entryType=document&page=${$('recycle-page').value}&limit=50`);
      if (epoch !== sessionEpoch) return;
      $('recycle-result').append(ReportsMarkdown.render(result.answer, 0));
      $('recycle-feedback').textContent = 'اكتملت القراءة بصلاحيات حسابك الحالي.';
    } else $('recycle-feedback').textContent = 'حُفظ الربط. اعرض الوثائق للتحقق من الاتصال.';
  } catch (error) { if (epoch === sessionEpoch) $('recycle-feedback').textContent = error.message; }
  finally { $('recycle-save').disabled = $('recycle-view').disabled = false; }
}
$('recycle-form').onsubmit = event => { event.preventDefault(); recycleAction(false); };
$('recycle-view').onclick = () => recycleAction(true);
