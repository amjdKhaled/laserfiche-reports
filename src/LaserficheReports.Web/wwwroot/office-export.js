/* Standards-based DOCX/XLSX packages, generated locally with no network dependencies. */
(function(root) {
  const xml = value => String(value ?? '').replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, '')
    .replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&apos;'}[c]));
  const enc = new TextEncoder();
  const crcTable = Array.from({length:256}, (_,n) => { for(let i=0;i<8;i++) n = (n&1) ? 0xedb88320^(n>>>1) : n>>>1; return n>>>0; });
  function crc(bytes) { let n=0xffffffff; for(const b of bytes) n=crcTable[(n^b)&255]^(n>>>8); return (n^0xffffffff)>>>0; }
  function zip(files, type) {
    const parts=[], central=[]; let offset=0, size=0;
    for(const [path,content] of Object.entries(files)) {
      const name=enc.encode(path), data=enc.encode(content), sum=crc(data);
      const header=new Uint8Array(30+name.length), view=new DataView(header.buffer);
      view.setUint32(0,0x04034b50,true); view.setUint16(4,20,true); view.setUint16(6,0x800,true);
      view.setUint32(14,sum,true); view.setUint32(18,data.length,true); view.setUint32(22,data.length,true);
      view.setUint16(26,name.length,true); header.set(name,30); parts.push(header,data);
      const c=new Uint8Array(46+name.length), v=new DataView(c.buffer);
      v.setUint32(0,0x02014b50,true); v.setUint16(4,20,true); v.setUint16(6,20,true); v.setUint16(8,0x800,true);
      v.setUint32(16,sum,true); v.setUint32(20,data.length,true); v.setUint32(24,data.length,true);
      v.setUint16(28,name.length,true); v.setUint32(42,offset,true); c.set(name,46);
      central.push(c); size+=c.length; offset+=header.length+data.length;
    }
    const end=new Uint8Array(22), v=new DataView(end.buffer);
    v.setUint32(0,0x06054b50,true); v.setUint16(8,central.length,true); v.setUint16(10,central.length,true);
    v.setUint32(12,size,true); v.setUint32(16,offset,true);
    return new Blob([...parts,...central,end],{type});
  }
  const rel = 'http://schemas.openxmlformats.org/package/2006/relationships';
  const office = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships';
  function metadata(message, question) {
    return [['السؤال',question || 'غير مسجل'],['المستودع',message.scope?.repositoryId || message.repositoryId || 'غير مسجل'],
      ['وقت الإجابة',message.generatedAt || 'غير مسجل'],['نطاق التقرير',message.scope?.detail || 'غير مسجل'],
      ['مراجعة التقرير',root.ReportsDownload.qualityLabel(message.quality)]];
  }
  function blocks(message) {
    const article=root.ReportsMarkdown.render(message.text || '',0);
    return [...article.children].map(node=>node.querySelector('table')
      ? {rows:[...node.querySelectorAll('tr')].map(row=>[...row.children].map(c=>c.textContent))}
      : {text:node.textContent});
  }
  function docx(message, question) {
    const w='http://schemas.openxmlformats.org/wordprocessingml/2006/main';
    const para = text => String(text ?? '').split('\n').map(line=>`<w:p><w:pPr><w:bidi/><w:jc w:val="right"/></w:pPr><w:r><w:rPr><w:rFonts w:ascii="Arial" w:hAnsi="Arial" w:cs="Arial"/><w:rtl/><w:sz w:val="22"/><w:szCs w:val="22"/></w:rPr><w:t xml:space="preserve">${xml(line)}</w:t></w:r></w:p>`).join('');
    const table = rows => {
      const count=Math.max(1,...rows.map(r=>r.length)), width=Math.floor(14400/count);
      return `<w:tbl><w:tblPr><w:bidiVisual/><w:tblW w:w="14400" w:type="dxa"/><w:tblLayout w:type="fixed"/><w:tblBorders>${['top','left','bottom','right','insideH','insideV'].map(side=>`<w:${side} w:val="single" w:sz="4" w:color="DCE3F0"/>`).join('')}</w:tblBorders></w:tblPr><w:tblGrid>${Array.from({length:count},()=>`<w:gridCol w:w="${width}"/>`).join('')}</w:tblGrid>${rows.map((row,i)=>`<w:tr>${i===0?'<w:trPr><w:tblHeader/></w:trPr>':''}${row.map(cell=>`<w:tc><w:tcPr><w:tcW w:w="${width}" w:type="dxa"/>${i===0?'<w:shd w:fill="EAF1FF"/>':''}</w:tcPr>${para(cell)}</w:tc>`).join('')}</w:tr>`).join('')}</w:tbl>`;
    };
    let body=para('تقارير ليزرفيش الذكية')+metadata(message,question).map(r=>para(r.join(': '))).join('');
    body+=blocks(message).map(b=>b.rows?table(b.rows):para(b.text)).join('');
    body+=para('نصوص المصادر الأصلية')+(message.sources||[]).map((s,i)=>para(`[${i+1}] ${s.documentName} · ${s.entryId} · ${s.pageNumber??'—'}`)+para(s.path)+para(s.text)).join('');
    return zip({'[Content_Types].xml':`<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>`,
      '_rels/.rels':`<Relationships xmlns="${rel}"><Relationship Id="rId1" Type="${office}/officeDocument" Target="word/document.xml"/></Relationships>`,
      'word/document.xml':`<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="${w}"><w:body>${body}<w:sectPr><w:pgSz w:w="16838" w:h="11906" w:orient="landscape"/><w:pgMar w:top="720" w:right="720" w:bottom="720" w:left="720"/></w:sectPr></w:body></w:document>`},'application/vnd.openxmlformats-officedocument.wordprocessingml.document');
  }
  function xlsx(message, question) {
    const sections=blocks(message), sheets=[{name:'التقرير',rows:[...metadata(message,question),...sections.filter(b=>!b.rows).flatMap(b=>b.text.split('\n').map(t=>[t]))]}];
    sections.filter(b=>b.rows).forEach((b,i)=>sheets.push({name:`جدول ${i+1}`,rows:b.rows}));
    sheets.push({name:'المصادر',rows:[['المرجع','رقم الوثيقة','اسم الوثيقة','المسار','الصفحة','النص'],...(message.sources||[]).map((s,i)=>[i+1,s.entryId,s.documentName,s.path,s.pageNumber??'',s.text])]});
    const files={'_rels/.rels':`<Relationships xmlns="${rel}"><Relationship Id="rId1" Type="${office}/officeDocument" Target="xl/workbook.xml"/></Relationships>`};
    const column=n=>{let s='';do{s=String.fromCharCode(65+n%26)+s;n=Math.floor(n/26)-1;}while(n>=0);return s;};
    sheets.forEach((sheet,i)=>{
      // Excel limits cell text to 32767 characters; continuation rows preserve it.
      const rows=sheet.rows.flatMap(row=>{
        const cells=row.map(v=>Array.from(String(v??''))); const count=Math.max(1,...cells.map(c=>Math.ceil(c.length/15000)));
        return Array.from({length:count},(_,part)=>cells.map(c=>c.slice(part*15000,(part+1)*15000).join('')));
      });
      const count=Math.max(1,...rows.map(r=>r.length));
      files[`xl/worksheets/sheet${i+1}.xml`]=`<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0" rightToLeft="1"/></sheetViews><cols><col min="1" max="${count}" width="35" customWidth="1"/></cols><sheetData>${rows.map((r,j)=>`<row r="${j+1}">${r.map((c,k)=>`<c r="${column(k)}${j+1}" t="inlineStr" s="1"><is><t xml:space="preserve">${xml(c)}</t></is></c>`).join('')}</row>`).join('')}</sheetData></worksheet>`;
    });
    files['xl/styles.xml']='<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Arial"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf/><xf applyAlignment="1"><alignment wrapText="1" vertical="top" readingOrder="2"/></xf></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>';
    files['xl/workbook.xml']=`<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="${office}"><sheets>${sheets.map((s,i)=>`<sheet name="${xml(s.name)}" sheetId="${i+1}" r:id="rId${i+1}"/>`).join('')}</sheets></workbook>`;
    files['xl/_rels/workbook.xml.rels']=`<Relationships xmlns="${rel}">${sheets.map((s,i)=>`<Relationship Id="rId${i+1}" Type="${office}/worksheet" Target="worksheets/sheet${i+1}.xml"/>`).join('')}<Relationship Id="styles" Type="${office}/styles" Target="styles.xml"/></Relationships>`;
    files['[Content_Types].xml']=`<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>${sheets.map((s,i)=>`<Override PartName="/xl/worksheets/sheet${i+1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>`).join('')}</Types>`;
    return zip(files,'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet');
  }
  root.ReportsOffice={docx,xlsx};
})(window);
