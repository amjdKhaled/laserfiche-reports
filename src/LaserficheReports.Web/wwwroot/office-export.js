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
  const plain = text => root.ReportsMarkdown.render(String(text || ''),0).textContent;
  const title = message => message.title || 'تقرير وثائق Laserfiche';
  function metadata(message, question) {
    return [['السؤال',question || 'غير مسجل'],['المستودع',message.scope?.repositoryId || message.repositoryId || 'غير مسجل'],
      ['وقت الإجابة (UTC)',message.generatedAt && Number.isFinite(new Date(message.generatedAt).getTime()) ? new Date(message.generatedAt).toISOString().replace('T',' ').replace(/\.\d+Z$/, ' UTC') : 'غير مسجل'],['نطاق التقرير',plain(message.scope?.detail || 'غير مسجل')],
      ['مراجعة التقرير',root.ReportsDownload.qualityLabel(message.quality)]];
  }
  function blocks(message) {
    const article=root.ReportsMarkdown.render(message.text || '',0);
    return [...article.children].map(node=>node.querySelector('table')
      ? {rows:[...node.querySelectorAll('tr')].map(row=>[...row.children].map(c=>c.textContent))}
      : {text:node.textContent, heading:/^H[123]$/.test(node.tagName)});
  }
  function widths(headers,total) {
    const weights=headers.map(name=>/رقم|مرجع|عدد الصفحات|الصفحة/.test(name)?1:/تاريخ|تعديل/.test(name)?2.2:/النص|تفاصيل/.test(name)?5:3);
    const sum=weights.reduce((a,b)=>a+b,0);return weights.map(w=>Math.floor(total*w/sum));
  }
  function docx(message, question) {
    const w='http://schemas.openxmlformats.org/wordprocessingml/2006/main';
    const sections=blocks(message),landscape=sections.some(b=>b.rows?.[0]?.length>4),width=landscape?14400:9360;
    const para=(text,style='Normal',align='right')=>String(text??'').split('\n').map(line=>{
      const arabic=/[\u0600-\u06ff]/.test(line);
      // Bidi paragraphs interpret left/right relative to the writing direction.
      // Keep dates, IDs and Latin file names in explicit LTR runs.
      const justification=arabic&&align==='right'?'left':align;
      return `<w:p><w:pPr><w:pStyle w:val="${style}"/><w:bidi w:val="${arabic?1:0}"/><w:jc w:val="${justification}"/></w:pPr><w:r><w:rPr><w:rtl w:val="${arabic?1:0}"/></w:rPr><w:t xml:space="preserve">${xml(line)}</w:t></w:r></w:p>`;
    }).join('');
    const table=(rows,meta=false)=>{
      const count=Math.max(1,...rows.map(r=>r.length)),sizes=meta?[Math.floor(width*.2),Math.floor(width*.8)]:widths(rows[0],width);
      return `<w:tbl><w:tblPr><w:bidiVisual/><w:tblW w:w="${width}" w:type="dxa"/><w:tblLayout w:type="fixed"/><w:tblCellMar><w:top w:w="90" w:type="dxa"/><w:bottom w:w="90" w:type="dxa"/><w:left w:w="100" w:type="dxa"/><w:right w:w="100" w:type="dxa"/></w:tblCellMar><w:tblBorders>${['top','left','bottom','right','insideH','insideV'].map(side=>`<w:${side} w:val="single" w:sz="4" w:color="DCE3F0"/>`).join('')}</w:tblBorders></w:tblPr><w:tblGrid>${sizes.map(size=>`<w:gridCol w:w="${size}"/>`).join('')}</w:tblGrid>${rows.map((row,i)=>`<w:tr><w:trPr><w:cantSplit/>${i===0&&!meta?'<w:tblHeader/>':''}</w:trPr>${Array.from({length:count},(_,j)=>{
        const header=!meta&&i===0,fill=header?'0754CA':meta&&j===0?'EAF1FF':i%2===0?'F5F8FD':'FFFFFF';
        const numeric=!meta&&/رقم|مرجع|عدد الصفحات|الصفحة/.test(rows[0][j]);
        return `<w:tc><w:tcPr><w:tcW w:w="${sizes[j]}" w:type="dxa"/><w:shd w:fill="${fill}"/><w:vAlign w:val="center"/></w:tcPr>${para(row[j]??'',header?'TableHeader':meta&&j===0?'MetadataLabel':'TableText',numeric?'center':'right')}</w:tc>`;
      }).join('')}</w:tr>`).join('')}</w:tbl>`;
    };
    let body=para(title(message),'Title')+para('تقارير ليزرفيش الذكية','Subtitle')+table(metadata(message,question).filter(r=>r[1]),true)+para('');
    body+=sections.map(b=>b.rows?table(b.rows)+para(''):para(b.text,b.heading?'Heading1':'Normal')).join('');
    const style=(id,name,size,color,bold=false,extra='')=>`<w:style w:type="paragraph" w:styleId="${id}"><w:name w:val="${name}"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="${id==='Title'?180:100}" w:line="280" w:lineRule="auto"/>${extra}</w:pPr><w:rPr>${bold?'<w:b/><w:bCs/>':''}<w:color w:val="${color}"/><w:sz w:val="${size}"/><w:szCs w:val="${size}"/></w:rPr></w:style>`;
    const files={
      '_rels/.rels':`<Relationships xmlns="${rel}"><Relationship Id="rId1" Type="${office}/officeDocument" Target="word/document.xml"/></Relationships>`,
      'word/document.xml':`<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="${w}" xmlns:r="${office}"><w:body>${body}<w:sectPr><w:headerReference w:type="default" r:id="header"/><w:footerReference w:type="default" r:id="footer"/><w:pgSz w:w="${landscape?16838:11906}" w:h="${landscape?11906:16838}"${landscape?' w:orient="landscape"':''}/><w:pgMar w:top="1000" w:right="1000" w:bottom="1000" w:left="1000" w:header="480" w:footer="480"/></w:sectPr></w:body></w:document>`,
      'word/styles.xml':`<w:styles xmlns:w="${w}"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Arial" w:hAnsi="Arial" w:cs="Arial"/><w:lang w:val="ar-SA" w:bidi="ar-SA"/><w:sz w:val="22"/><w:szCs w:val="22"/></w:rPr></w:rPrDefault></w:docDefaults><w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:pPr><w:spacing w:after="120" w:line="300" w:lineRule="auto"/></w:pPr><w:rPr><w:color w:val="24384B"/></w:rPr></w:style>${style('Title','Report Title',36,'0754CA',true,'<w:keepNext/>')}${style('Subtitle','Report Subtitle',20,'64748B')}${style('Heading1','Heading 1',28,'0754CA',true,'<w:keepNext/>')}${style('Heading2','Heading 2',24,'154680',true,'<w:keepNext/>')}${style('TableHeader','Table Header',20,'FFFFFF',true,'<w:keepNext/>')}${style('TableText','Table Text',20,'24384B')}${style('MetadataLabel','Metadata Label',20,'154680',true)}</w:styles>`,
      'word/header1.xml':`<w:hdr xmlns:w="${w}">${para('Laserfiche Reports  |  تقارير ليزرفيش الذكية','Subtitle')}</w:hdr>`,
      'word/footer1.xml':`<w:ftr xmlns:w="${w}"><w:p><w:pPr><w:jc w:val="center"/><w:bidi/></w:pPr><w:r><w:t>صفحة </w:t></w:r><w:fldSimple w:instr="PAGE"><w:r><w:t>1</w:t></w:r></w:fldSimple><w:r><w:t xml:space="preserve"> من </w:t></w:r><w:fldSimple w:instr="NUMPAGES"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p></w:ftr>`,
      'word/settings.xml':`<w:settings xmlns:w="${w}"><w:updateFields w:val="true"/></w:settings>`,
      'word/_rels/document.xml.rels':`<Relationships xmlns="${rel}">${[['styles','styles.xml'],['settings','settings.xml'],['header','header1.xml'],['footer','footer1.xml']].map(([id,target])=>`<Relationship Id="${id}" Type="${office}/${id}" Target="${target}"/>`).join('')}</Relationships>`
    };
    files['[Content_Types].xml']=`<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>${[['document','document.main'],['styles','styles'],['settings','settings'],['header1','header'],['footer1','footer']].map(([file,type])=>`<Override PartName="/word/${file}.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.${type}+xml"/>`).join('')}</Types>`;
    return zip(files,'application/vnd.openxmlformats-officedocument.wordprocessingml.document');
  }
  function xlsx(message, question) {
    const sections=blocks(message),sheets=[{name:'التقرير',summary:true,rows:[[title(message)],...metadata(message,question).filter(r=>r[1]),[''],...sections.filter(b=>!b.rows).map(b=>[b.text])]}];
    const info=`المستودع: ${message.scope?.repositoryId||message.repositoryId||'غير مسجل'}  |  ${metadata(message,question)[2].join(': ')}`;
    sections.filter(b=>b.rows).forEach((b,i)=>sheets.push({name:`جدول ${i+1}`,rows:[[message.title||`نتائج التقرير — جدول ${i+1}`],[info],[],...b.rows]}));
    const files={'_rels/.rels':`<Relationships xmlns="${rel}"><Relationship Id="rId1" Type="${office}/officeDocument" Target="xl/workbook.xml"/></Relationships>`};
    const column=n=>{let s='';do{s=String.fromCharCode(65+n%26)+s;n=Math.floor(n/26)-1;}while(n>=0);return s;};
    sheets.forEach((sheet,i)=>{
      // Continuation rows retain long evidence without exceeding Excel's cell limit.
      const rows=sheet.rows.flatMap((row,index)=>{
        const cells=row.map(v=>Array.from(String(v??''))),count=Math.max(1,...cells.map(c=>Math.ceil(c.length/15000)));
        return Array.from({length:count},(_,part)=>({cells:cells.map(c=>c.slice(part*15000,(part+1)*15000).join('')),header:!sheet.summary&&index===3,title:index===0,metadata:sheet.summary&&index>0&&index<=metadata(message,question).filter(r=>r[1]).length}));
      });
      const count=sheet.summary?6:Math.max(1,...rows.map(r=>r.cells.length)),end=column(count-1),headers=sheet.rows[3]||[];
      const cols=Array.from({length:count},(_,k)=>sheet.summary?(k===0?23:18):/رقم|مرجع|الصفحة|عدد الصفحات/.test(headers[k]||'')?14:/تاريخ|تعديل/.test(headers[k]||'')?26:/النص/.test(headers[k]||'')?80:42);
      const merges=[`A1:${end}1`];if(sheet.summary)rows.forEach((r,j)=>{if(j>0&&r.cells.length===2)merges.push(`B${j+1}:${end}${j+1}`);else if(j>0&&r.cells.length===1&&r.cells[0])merges.push(`A${j+1}:${end}${j+1}`);});else merges.push(`A2:${end}2`);
      files[`xl/worksheets/sheet${i+1}.xml`]=`<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetPr><pageSetUpPr fitToPage="1"/></sheetPr><dimension ref="A1:${end}${rows.length}"/><sheetViews><sheetView workbookViewId="0" rightToLeft="1"><pane ySplit="${sheet.summary?1:4}" topLeftCell="A${sheet.summary?2:5}" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><sheetFormatPr defaultRowHeight="22"/><cols>${cols.map((width,k)=>`<col min="${k+1}" max="${k+1}" width="${width}" customWidth="1"/>`).join('')}</cols><sheetData>${rows.map((r,j)=>`<row r="${j+1}"${r.title?' ht="36" customHeight="1"':r.header?' ht="30" customHeight="1"':''}>${r.cells.map((c,k)=>{
        const style=r.title?2:r.header?3:r.metadata&&k===0?5:j%2===0?4:1;
        const numeric=!sheet.summary&&j>=4&&/رقم الوثيقة|عدد الصفحات|المرجع/.test(headers[k]||'')&&/^\d{1,15}$/.test(c)&&!/^0\d/.test(c);
        return `<c r="${column(k)}${j+1}" s="${numeric?6:style}"${numeric?'':' t="inlineStr"'}>${numeric?`<v>${c}</v>`:`<is><t xml:space="preserve">${xml(c)}</t></is>`}</c>`;
      }).join('')}</row>`).join('')}</sheetData>${!sheet.summary&&rows.length>4?`<autoFilter ref="A4:${end}${rows.length}"/>`:''}<mergeCells count="${merges.length}">${merges.map(ref=>`<mergeCell ref="${ref}"/>`).join('')}</mergeCells><printOptions horizontalCentered="1"/><pageMargins left="0.3" right="0.3" top="0.5" bottom="0.5" header="0.2" footer="0.2"/><pageSetup paperSize="9" orientation="${count>4?'landscape':'portrait'}" fitToWidth="1" fitToHeight="0"/><headerFooter><oddFooter>&amp;Cصفحة &amp;P / &amp;N</oddFooter></headerFooter></worksheet>`;
    });
    const xf=(font,fill,border,alignment)=>`<xf numFmtId="0" fontId="${font}" fillId="${fill}" borderId="${border}" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment wrapText="1" vertical="center" horizontal="${alignment}" readingOrder="2"/></xf>`;
    files['xl/styles.xml']=`<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="4"><font><sz val="11"/><color rgb="FF24384B"/><name val="Arial"/></font><font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Arial"/></font><font><b/><sz val="18"/><color rgb="FFFFFFFF"/><name val="Arial"/></font><font><b/><sz val="11"/><color rgb="FF154680"/><name val="Arial"/></font></fonts><fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF0754CA"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFF0F5FF"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="2"><border/><border>${['left','right','top','bottom'].map(side=>`<${side} style="thin"><color rgb="FFDCE3F0"/></${side}>`).join('')}</border></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="7">${xf(0,0,0,'right')}${xf(0,0,1,'right')}${xf(2,2,0,'center')}${xf(1,2,1,'center')}${xf(0,3,1,'right')}${xf(3,3,1,'right')}${xf(0,0,1,'center')}</cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>`;
    files['xl/workbook.xml']=`<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="${office}"><sheets>${sheets.map((s,i)=>`<sheet name="${xml(s.name)}" sheetId="${i+1}" r:id="rId${i+1}"/>`).join('')}</sheets><definedNames>${sheets.map((s,i)=>`<definedName name="_xlnm.Print_Titles" localSheetId="${i}">'${xml(s.name)}'!$1:$${s.summary?1:4}</definedName>`).join('')}</definedNames></workbook>`;
    files['xl/_rels/workbook.xml.rels']=`<Relationships xmlns="${rel}">${sheets.map((s,i)=>`<Relationship Id="rId${i+1}" Type="${office}/worksheet" Target="worksheets/sheet${i+1}.xml"/>`).join('')}<Relationship Id="styles" Type="${office}/styles" Target="styles.xml"/></Relationships>`;
    files['[Content_Types].xml']=`<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>${sheets.map((s,i)=>`<Override PartName="/xl/worksheets/sheet${i+1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>`).join('')}</Types>`;
    return zip(files,'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet');
  }
  root.ReportsOffice={docx,xlsx};
})(window);
