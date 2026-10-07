/* Local exports preserve the displayed answer, scope and original evidence. */
(function (root) {
  const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  function timestamp(message) {
    const date = message.generatedAt && new Date(message.generatedAt);
    return date && Number.isFinite(date.getTime()) ? date.toISOString() : 'غير مسجل في المحادثة';
  }
  function qualityLabel(quality) {
    if (!quality) return '';
    const label = { answered: 'إجابة مدعومة بالمقتطفات', insufficient: 'الأدلة غير كافية لإجابة كاملة',
      conflicting: 'تعارض محتمل يحتاج مراجعة', source_only: 'مقتطفات للمراجعة' }[quality.status] || 'راجع أدلة التقرير';
    return label + (quality.semanticReview === 'completed' ? ' · أُجريت مراجعة دلالية آلية' :
      quality.semanticReview === 'unavailable' ? ' · لم تكتمل المراجعة الدلالية' : '');
  }
  function markdown(message, question) {
    const safe = value => String(value ?? '').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/!\[/g, '!\\[');
    return `# Laserfiche Reports\n\nالسؤال: ${safe(question || 'غير مسجل')}\n\nتاريخ إعداد الإجابة (UTC): ${timestamp(message)}\n\nنطاق التقرير: ${safe(message.scope?.detail || 'غير مسجل؛ راجع المصادر وحدود الإجابة.')}\n\n${safe(qualityLabel(message.quality))}\n\n${safe(root.ReportsMarkdown.withoutPaths(message.text || ''))}`;
  }
  function html(message, question) {
    const reportTitle = message.title || 'تقرير وثائق Laserfiche';
    const scopeText = root.ReportsMarkdown.render(message.scope?.detail || 'نطاق التقرير غير مسجل؛ راجع المصادر وحدود الإجابة.',0).textContent;
    const article = root.ReportsMarkdown.render(message.text || '', 0, 'export-source');
    const dir = /[\u0600-\u06ff]/.test((question || '') + (message.text || '')) ? 'rtl' : 'ltr';
    return `<!doctype html><html lang="${dir === 'rtl' ? 'ar' : 'en'}" dir="${dir}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'"><title>Laserfiche Reports</title><style>body{font-family:Tahoma,Arial,sans-serif;max-width:1100px;margin:35px auto;padding:0 20px;color:#19283c;line-height:1.8}h1,h2,h3{color:#0754ca}h1{font-size:26px;margin:0 0 8px}.report-header{border-bottom:3px solid #0754ca;padding-bottom:14px;margin-bottom:20px}.report-brand{font-size:12px;color:#64748b;margin:0}.report-info{background:#f7f9fd;border:1px solid #dbe7ed;border-radius:8px;padding:12px 18px;margin:16px 0}thead{display:table-header-group}th{font-weight:bold}footer{border-top:1px solid #dbe7ed;padding-top:12px;margin-top:24px;color:#64748b;font-size:12px}table{table-layout:auto;width:100%;border-collapse:collapse;font-size:13px}th,td{text-align:start;padding:10px;border:1px solid #dbe7ed;vertical-align:top;overflow-wrap:anywhere;unicode-bidi:plaintext}th{background:#edf3ff}tr:nth-child(even){background:#f8fbfc}.scope{background:#f0f6f8;padding:12px;border-inline-start:3px solid #0754ca}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:inherit;font-size:13px;background:#f8fbfc;padding:12px}a{color:#0754ca}.evidence{border-top:1px solid #dbe7ed;margin-top:25px}.report-table-wrap{overflow-x:auto}@media print{body{max-width:none;margin:0;padding:0}tr{break-inside:avoid}h2,h3{break-after:avoid}.report-table-wrap{overflow:visible}}</style></head><body><header class="report-header"><p class="report-brand">تقارير ليزرفيش الذكية</p><h1>${escape(reportTitle)}</h1></header><div class="report-info"><p><strong>السؤال:</strong> ${escape(question || 'غير مسجل')}</p><p>تاريخ إعداد الإجابة (UTC): ${escape(timestamp(message))}</p></div><p class="scope">${escape(scopeText)}</p><p>${escape(qualityLabel(message.quality))}</p>${article.outerHTML}<footer>Laserfiche Reports · التقرير</footer></body></html>`;
  }
  function download(message, question, format) {
    if (!['html', 'md', 'docx', 'xlsx', 'pdf'].includes(format)) throw new Error('صيغة التقرير غير مدعومة');
    if (format === 'pdf') {
      const preview = window.open('', '_blank');
      if (!preview) throw new Error('اسمح بفتح نافذة التقرير لحفظ PDF.');
      preview.document.write(html(message, question)); preview.document.close();
      preview.addEventListener('load', () => { preview.focus(); preview.print(); }, { once: true });
      return;
    }
    const data = format === 'html' ? html(message, question) : markdown(message, question);
    const url = URL.createObjectURL((['docx', 'xlsx'].includes(format) ? root.ReportsOffice[format](message, question) : new Blob(['\ufeff', data], { type: format === 'html' ? 'text/html;charset=utf-8' : 'text/markdown;charset=utf-8' })));
    const anchor = document.createElement('a'); anchor.href = url;
    anchor.download = `laserfiche-report-${new Date().toISOString().replace(/[:.]/g, '-')}.${format}`;
    document.body.append(anchor); anchor.click(); anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  root.ReportsDownload = { html, markdown, download, qualityLabel };
})(window);
