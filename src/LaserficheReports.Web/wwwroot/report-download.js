/* Local exports preserve the displayed answer, scope and original evidence. */
(function (root) {
  const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  // Reports are dated in Saudi time, not UTC (2026-10-28).
  const saudiTime = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Riyadh', year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' });
  function timestamp(message) {
    const date = message.generatedAt && new Date(message.generatedAt);
    if (!date || !Number.isFinite(date.getTime())) return 'غير مسجل في المحادثة';
    const part = Object.fromEntries(saudiTime.formatToParts(date).map(p => [p.type, p.value]));
    return `${part.year}-${part.month}-${part.day} ${part.hour}:${part.minute}:${part.second}`;
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
    return `# Laserfiche Reports\n\nالسؤال: ${safe(question || 'غير مسجل')}\n\nتاريخ إعداد الإجابة (بتوقيت السعودية): ${timestamp(message)}\n\nنطاق التقرير: ${safe(message.scope?.detail || 'غير مسجل؛ راجع المصادر وحدود الإجابة.')}\n\n${safe(qualityLabel(message.quality))}\n\n${safe(root.ReportsMarkdown.withoutPaths(message.text || ''))}`;
  }
  // Exported reports carry the app's identity: navy band with the logo, gold rule, Readex Pro
  // (design أ, chosen 2026-10-28). The file opens from disk under a CSP that allows no requests, so the font and
  // logo are loaded once here and written into it; until they arrive it falls back to Segoe UI and no logo.
  const assets = {};
  function load(name, url, type) {
    if (typeof fetch !== 'function') return;
    fetch(url).then(response => response.ok ? response.arrayBuffer() : null).then(buffer => {
      if (!buffer) return;
      const bytes = new Uint8Array(buffer);
      let text = '';
      for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
      assets[name] = { bytes, url: `data:${type};base64,${btoa(text)}` };
    }).catch(() => {});
  }
  load('logo', '/isb-logo.jpg', 'image/jpeg');
  load('regular', '/brand/font/ReadexPro-Regular.ttf', 'font/ttf');
  load('semibold', '/brand/font/ReadexPro-SemiBold.ttf', 'font/ttf');
  function html(message, question) {
    const reportTitle = message.title || 'تقرير وثائق Laserfiche';
    const scopeText = root.ReportsMarkdown.render(message.scope?.detail || 'نطاق التقرير غير مسجل؛ راجع المصادر وحدود الإجابة.',0).textContent;
    const article = root.ReportsMarkdown.render(message.text || '', 0, 'export-source');
    const dir = /[؀-ۿ]/.test((question || '') + (message.text || '')) ? 'rtl' : 'ltr';
    const wide = [...article.querySelectorAll('tr')].some(row => row.children.length > 5);
    const repository = message.scope?.repositoryId || message.repositoryId;
    const quality = qualityLabel(message.quality);
    const fonts = [['regular', 400], ['semibold', 600]].filter(([name]) => assets[name])
      .map(([name, weight]) => `@font-face{font-family:'Readex Pro';font-weight:${weight};src:url(${assets[name].url}) format('truetype')}`).join('');
    const logo = assets.logo ? `<div class="report-logo"><img src="${assets.logo.url}" alt="ISB"></div>` : '';
    const style = `${fonts}*{box-sizing:border-box}body{font-family:'Readex Pro','Segoe UI',Tahoma,sans-serif;max-width:1100px;margin:32px auto;padding:0 24px;color:#142633;background:#fff;font-size:14px;line-height:1.85;-webkit-print-color-adjust:exact;print-color-adjust:exact}` +
      `.report-header{display:flex;align-items:center;gap:18px;padding:18px 24px;background:#0B3954;border-bottom:4px solid #C29A2E;border-radius:14px 14px 0 0;color:#fff}` +
      `.report-logo{flex:none;background:#fff;border-radius:10px;padding:5px 8px;line-height:0}.report-logo img{height:42px;width:auto}` +
      `.report-brand{margin:0;font-size:12px;color:rgba(255,255,255,.78)}h1{margin:0;font-size:22px;font-weight:600;line-height:1.55;color:#fff}` +
      `.report-info{display:grid;grid-template-columns:max-content minmax(0,1fr);gap:4px 18px;margin:0 0 18px;padding:14px 24px;background:#F0F5F8;border:1px solid #E3EAF0;border-top:0;border-radius:0 0 14px 14px;font-size:13px}.report-info dt{color:#4A5C69}.report-info dd{margin:0;overflow-wrap:anywhere}` +
      `.scope{margin:0 0 10px;padding:10px 14px;background:#F0F5F8;border:1px solid #E3EAF0;border-inline-start:3px solid #C29A2E;border-radius:8px;color:#4A5C69;font-size:12.5px}.quality{margin:0 0 10px;color:#4A5C69;font-size:12.5px}` +
      `h2{font-size:20px;font-weight:600;margin:22px 0 12px;line-height:1.5}h3{font-size:16px;font-weight:600;margin:24px 0 10px;padding-inline-start:12px;border-inline-start:4px solid #C29A2E}p{margin:8px 0}strong{font-weight:600}code{background:#F0F5F8;border-radius:4px;padding:1px 5px}` +
      `.report-table-wrap{overflow-x:auto;border:1px solid #D4DEE6;border-radius:10px;margin:14px 0}table{width:100%;border-collapse:collapse;font-size:12.5px;table-layout:auto}thead{display:table-header-group}` +
      `th{background:#E1EEF5;color:#0B3954;font-weight:600;text-align:start;vertical-align:top;padding:10px 12px;border-bottom:1px solid #D4DEE6}td{padding:9px 12px;text-align:start;vertical-align:top;border-bottom:1px solid #E3EAF0;overflow-wrap:anywhere;unicode-bidi:plaintext}` +
      `tbody tr:nth-child(even){background:#F0F5F8}tbody tr:last-child td{border-bottom:0}td.report-cell-date{white-space:nowrap;font-variant-numeric:tabular-nums}td[dir="rtl"]{text-align:right}td[dir="ltr"]{text-align:left}` +
      `footer{display:flex;justify-content:space-between;gap:12px;flex-wrap:wrap;border-top:1px solid #D4DEE6;padding-top:12px;margin-top:28px;color:#8595A2;font-size:11.5px}` +
      `@page{size:A4${wide ? ' landscape' : ''};margin:12mm}@media print{body{max-width:none;margin:0;padding:0}.report-table-wrap{overflow:visible}tr{break-inside:avoid}h2,h3{break-after:avoid}}`;
    const info = [['السؤال', escape(question || 'غير مسجل')], repository ? ['المستودع', `<bdi>${escape(repository)}</bdi>`] : null,
      ['وقت الإجابة (بتوقيت السعودية)', `<bdi dir="ltr">${escape(timestamp(message))}</bdi>`]].filter(Boolean)
      .map(([label, value]) => `<dt>${label}</dt><dd>${value}</dd>`).join('');
    return `<!doctype html><html lang="${dir === 'rtl' ? 'ar' : 'en'}" dir="${dir}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; base-uri 'none'; form-action 'none'"><title>${escape(reportTitle)}</title><style>${style}</style></head><body><header class="report-header">${logo}<div><p class="report-brand">تقارير ليزرفيش الذكية</p><h1>${escape(reportTitle)}</h1></div></header><dl class="report-info">${info}</dl><p class="scope">${escape(scopeText)}</p>${quality ? `<p class="quality">${escape(quality)}</p>` : ''}${article.outerHTML}<footer><span>تقارير ليزرفيش الذكية · Laserfiche Reports</span><bdi dir="ltr">${escape(timestamp(message))}</bdi></footer></body></html>`;
  }
  function download(message, question, format) {
    if (!['html', 'md', 'docx', 'xlsx', 'pdf'].includes(format)) throw new Error('صيغة التقرير غير مدعومة');
    // PDF prints from a hidden frame on this page. A print window opened from Reports shared its thread, so
    // Reports froze until that window's print dialog was closed (2026-10-28).
    if (format === 'pdf') {
      document.querySelector('iframe.report-print-frame')?.remove();
      const frame = document.createElement('iframe');
      frame.className = 'report-print-frame';
      frame.title = 'طباعة التقرير'; frame.tabIndex = -1; frame.setAttribute('aria-hidden', 'true');
      frame.style.cssText = 'position:fixed;width:0;height:0;border:0;visibility:hidden';
      frame.addEventListener('load', () => {
        const view = frame.contentWindow;
        view.addEventListener('afterprint', () => setTimeout(() => frame.remove()), { once: true });
        view.focus(); view.print();
      }, { once: true });
      frame.srcdoc = html(message, question);
      document.body.append(frame);
      return;
    }
    const data = format === 'html' ? html(message, question) : markdown(message, question);
    const url = URL.createObjectURL((['docx', 'xlsx'].includes(format) ? root.ReportsOffice[format](message, question) : new Blob(['\ufeff', data], { type: format === 'html' ? 'text/html;charset=utf-8' : 'text/markdown;charset=utf-8' })));
    const anchor = document.createElement('a'); anchor.href = url;
    anchor.download = `laserfiche-report-${new Date().toISOString().replace(/[:.]/g, '-')}.${format}`;
    document.body.append(anchor); anchor.click(); anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  root.ReportsDownload = { html, markdown, download, qualityLabel, timestamp, asset: name => assets[name]?.bytes };
})(window);
