/* Local exports preserve the displayed answer, scope and original evidence. */
(function (root) {
  const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  function timestamp(message) {
    const date = message.generatedAt && new Date(message.generatedAt);
    return date && Number.isFinite(date.getTime()) ? date.toISOString() : 'غير مسجل في المحادثة';
  }
  function markdown(message, question) {
    const sources = message.sources || [];
    const safe = value => String(value ?? '').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/!\[/g, '!\\[');
    return `# Laserfiche Reports\n\nالسؤال: ${safe(question || 'غير مسجل')}\n\nتاريخ إعداد الإجابة (UTC): ${timestamp(message)}\n\nنطاق التقرير: ${safe(message.scope?.detail || 'غير مسجل؛ راجع المصادر وحدود الإجابة.')}\n\n${safe(message.text || '')}\n\n## نصوص المصادر الأصلية\n\n` + sources.map((source, i) =>
      `[${i + 1}] رقم الوثيقة: ${source.entryId}\nاسم الوثيقة: ${source.documentName || 'غير مذكور'}\nالمسار: ${source.path || 'غير مذكور'}\nالصفحة: ${source.pageNumber ?? 'غير مذكورة'}\nنوع المصدر: ${source.textSource || 'غير مذكور'}\n\n${String(source.text || '').split('\n').map(line => '    ' + line).join('\n')}`).join('\n\n---\n\n');
  }
  function html(message, question) {
    const sources = message.sources || [];
    const article = root.ReportsMarkdown.render(message.text || '', sources.length, 'export-source');
    article.querySelectorAll('.report-reference').forEach(button => {
      const link = document.createElement('a'); link.textContent = button.textContent;
      link.href = `#export-source-${Number(button.textContent.slice(1, -1))}`; button.replaceWith(link);
    });
    const dir = /[\u0600-\u06ff]/.test((question || '') + (message.text || '')) ? 'rtl' : 'ltr';
    const evidence = sources.map((source, i) => `<section class="evidence" id="export-source-${i + 1}"><h3>[${i + 1}] ${escape(source.documentName || 'غير مذكور')}</h3><p>رقم الوثيقة: ${escape(source.entryId)} · الصفحة: ${escape(source.pageNumber ?? 'غير مذكورة')} · نوع المصدر: ${escape(source.textSource || 'غير مذكور')}</p><p>${escape(source.path || 'غير مذكور')}</p><pre>${escape(source.text || '')}</pre></section>`).join('');
    return `<!doctype html><html lang="${dir === 'rtl' ? 'ar' : 'en'}" dir="${dir}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'"><title>Laserfiche Reports</title><style>body{font-family:Tahoma,Arial,sans-serif;max-width:1100px;margin:35px auto;padding:0 20px;color:#19283c;line-height:1.8}h1,h2,h3{color:#164764}table{width:100%;border-collapse:collapse;font-size:13px}th,td{text-align:start;padding:10px;border:1px solid #dbe7ed;vertical-align:top;overflow-wrap:anywhere;unicode-bidi:plaintext}th{background:#edf5f6}tr:nth-child(even){background:#f8fbfc}.scope{background:#f0f6f8;padding:12px;border-inline-start:3px solid #148f7e}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:inherit;font-size:13px;background:#f8fbfc;padding:12px}a{color:#148f7e}.evidence{border-top:1px solid #dbe7ed;margin-top:25px}.report-table-wrap{overflow-x:auto}@media print{body{max-width:none;margin:0;padding:0}tr{break-inside:avoid}h2,h3{break-after:avoid}.report-table-wrap{overflow:visible}}</style></head><body><h1>Laserfiche Reports</h1><p><strong>السؤال:</strong> ${escape(question || 'غير مسجل')}</p><p>تاريخ إعداد الإجابة (UTC): ${escape(timestamp(message))}</p><p class="scope">${escape(message.scope?.detail || 'نطاق التقرير غير مسجل؛ راجع المصادر وحدود الإجابة.')}</p>${article.outerHTML}<h2>نصوص المصادر الأصلية</h2>${evidence}</body></html>`;
  }
  function download(message, question, format) {
    if (!['html', 'md'].includes(format)) throw new Error('صيغة التقرير غير مدعومة');
    const data = format === 'html' ? html(message, question) : markdown(message, question);
    const url = URL.createObjectURL(new Blob(['\ufeff', data], { type: format === 'html' ? 'text/html;charset=utf-8' : 'text/markdown;charset=utf-8' }));
    const anchor = document.createElement('a'); anchor.href = url;
    anchor.download = `laserfiche-report-${new Date().toISOString().replace(/[:.]/g, '-')}.${format}`;
    document.body.append(anchor); anchor.click(); anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  root.ReportsDownload = { html, markdown, download };
})(window);
