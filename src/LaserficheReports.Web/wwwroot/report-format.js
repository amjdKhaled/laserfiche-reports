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
          if (source) { source.open = true; source.scrollIntoView({ block: 'nearest', behavior: 'smooth' }); }
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
  function render(text, sourceCount = 0, sourcePrefix = 'report-source') {
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
      if (header.length > 1 && i + 1 < lines.length && divider(lines[i + 1], header.length)) {
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
  root.ReportsMarkdown = { render, cells };
})(window);
