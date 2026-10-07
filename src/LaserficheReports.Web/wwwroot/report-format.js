/* Small local Markdown renderer: document/model text is never assigned to innerHTML. */
(function (root) {
  function inline(parent, value, sourceCount, sourcePrefix) {
    const tokens = /\*\*([^*]+)\*\*|`([^`]+)`|\[(\d+)\]/g;
    let offset = 0;
    for (const match of value.matchAll(tokens)) {
      parent.append(document.createTextNode(value.slice(offset, match.index)));
      const reference = Number(match[3]);
      if (match[1]) {
        const strong = document.createElement('strong'); strong.textContent = match[1]; parent.append(strong);
      } else if (match[2]) {
        const code = document.createElement('code'); code.textContent = match[2]; parent.append(code);
      } else if (reference > 0 && reference <= sourceCount) {
        const button = document.createElement('button');
        button.type = 'button'; button.className = 'report-reference'; button.textContent = match[0];
        button.setAttribute('aria-label', `عرض المصدر ${reference}`);
        button.onclick = () => {
          const source = document.getElementById(`${sourcePrefix}-${reference}`);
          if (source) { const more = source.closest('.sources-more'); if (more) more.open = true; source.open = true; source.scrollIntoView({ block: 'nearest', behavior: 'smooth' }); }
        };
        parent.append(button);
      } else parent.append(document.createTextNode(match[0]));
      offset = match.index + match[0].length;
    }
    parent.append(document.createTextNode(value.slice(offset)));
  }
  function cells(line) {
    const parts = []; let value = '';
    for (let i = 0; i < line.length; i++) {
      if (line[i] === '\\' && (line[i + 1] === '|' || line[i + 1] === '\\')) value += line[++i];
      else if (line[i] === '|') { parts.push(value.trim()); value = ''; }
      else value += line[i];
    }
    parts.push(value.trim());
    if (line.trimStart().startsWith('|')) parts.shift();
    if (line.trimEnd().endsWith('|') && parts[parts.length - 1] === '') parts.pop();
    return parts;
  }
  function divider(line, columns) {
    const values = cells(line);
    return values.length === columns && values.every(cell => /^:?-{3,}:?$/.test(cell));
  }
  // Apply to old history as well as newly generated reports and every export.
  // Remove the complete column, keeping header/value indices aligned.
  function withoutPaths(text) {
    const lines = String(text || '').replace(/\r\n?/g, '\n').replace(/\n\n## الوثائق والمصادر\n\n[\s\S]*$/, '').split('\n');
    const output = [];
    for (let i = 0; i < lines.length; i++) {
      const header = cells(lines[i]);
      const keep = header.map((name,index) => /^(?:المسار|مسار الوثيقة|مسار المستند|path|full path)$/i.test(name.trim()) ? -1 : index).filter(index => index >= 0);
      if (header.length > 1 && keep.length > 0 && keep.length < header.length && i + 1 < lines.length && divider(lines[i+1],header.length)) {
        const row = values => '| ' + keep.map(index => String(values[index]).replace(/\\/g,'\\\\').replace(/\|/g,'\\|')).join(' | ') + ' |';
        output.push(row(header), row(cells(lines[++i])));
        while (i + 1 < lines.length && lines[i+1].trim() && cells(lines[i+1]).length === header.length) output.push(row(cells(lines[++i])));
      } else output.push(lines[i]);
    }
    return output.join('\n');
  }
  function render(text, sourceCount = 0, sourcePrefix = 'report-source') {
    text = withoutPaths(text);
    const article = document.createElement('article'); article.className = 'report-body';
    article.dir = /[\u0600-\u06ff]/.test(text) ? 'rtl' : 'ltr';
    const lines = String(text).replace(/\r\n?/g, '\n').split('\n');
    let i = 0;
    while (i < lines.length) {
      if (!lines[i].trim()) { i++; continue; }
      const headings = /^(#{1,3})\s+(.+)$/.exec(lines[i]);
      if (headings) {
        const heading = document.createElement(headings[1].length === 1 ? 'h2' : 'h3');
        inline(heading, headings[2], sourceCount, sourcePrefix); article.append(heading); i++; continue;
      }
      const header = cells(lines[i]);
      if ((header.length > 1 || header.length === 1 && /^\s*\|.*\|\s*$/.test(lines[i])) && i + 1 < lines.length && divider(lines[i + 1], header.length)) {
        const wrap = document.createElement('div'); wrap.className = 'report-table-wrap';
        wrap.tabIndex = 0; wrap.setAttribute('aria-label', 'جدول نتائج التقرير');
        const table = document.createElement('table'); table.className = 'report-table';
        const head = document.createElement('thead'), body = document.createElement('tbody');
        const headRow = document.createElement('tr');
        header.forEach(value => {
          const th = document.createElement('th'); th.scope = 'col';
          inline(th, value, sourceCount, sourcePrefix); headRow.append(th);
        });
        head.append(headRow); i += 2;
        while (i < lines.length && lines[i].trim() && cells(lines[i]).length === header.length) {
          const tr = document.createElement('tr');
          cells(lines[i]).forEach(value => {
            const td = document.createElement('td'); inline(td, value, sourceCount, sourcePrefix); tr.append(td);
          });
          body.append(tr); i++;
        }
        const names = header.map(value => value.replace(/\s+/g, ' ').trim());
        for (let column = 0; column < names.length; column++) {
          const kind = /(?:المسار|path)/i.test(names[column]) ? 'path' : /(?:تاريخ|تعديل|date|modified)/i.test(names[column]) ? 'date' : /(?:رقم|مرجع|عدد الصفحات|reference|\bid\b)/i.test(names[column]) ? 'compact' : 'text';
          for (const row of [headRow, ...body.children]) {
            const cell = row.children[column]; cell.classList.add('report-cell-' + kind);
            cell.dir = article.dir;
            if (row !== headRow && (kind === 'date' || kind === 'compact')) {
              const value = document.createElement('bdi'); value.dir = 'ltr';
              while (cell.firstChild) value.append(cell.firstChild);
              cell.append(value);
            }
          }
        }
        table.append(head, body); wrap.append(table); article.append(wrap); continue;
      }
      if (/^(?:[-*]|\d+\.)\s+/.test(lines[i])) {
        const list = document.createElement(/^\d+\./.test(lines[i]) ? 'ol' : 'ul');
        while (i < lines.length && /^(?:[-*]|\d+\.)\s+/.test(lines[i])) {
          const item = document.createElement('li');
          inline(item, lines[i].replace(/^(?:[-*]|\d+\.)\s+/, ''), sourceCount, sourcePrefix);
          list.append(item); i++;
        }
        article.append(list); continue;
      }
      const paragraph = document.createElement('p');
      inline(paragraph, lines[i], sourceCount, sourcePrefix); article.append(paragraph); i++;
    }
    return article;
  }
  root.ReportsMarkdown = { render, cells, withoutPaths };
})(window);
