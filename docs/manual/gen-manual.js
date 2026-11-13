// 把 WhatPlugin-用户手册.md 排成 A4 的 Word 说明书。
// 用法：npm install docx，然后 node gen-manual.js WhatPlugin-用户手册.md 输出.docx 版本号
// 再用 Word 打开，更新目录（引用 → 更新目录），另存为 PDF。
// 截图在 images/ 里，都是演示数据；dims.json 记着每张图的像素尺寸。
const fs = require('fs');
const path = require('path');
const {
  Document, Packer, Paragraph, TextRun, HeadingLevel, AlignmentType, Footer, Header, PageNumber,
  BorderStyle, ImageRun, Table, TableRow, TableCell, WidthType, ShadingType, LevelFormat,
  TableOfContents, PageBreak, TabStopType, TabStopPosition, VerticalAlign,
} = require('docx');

const [, , src, out, version] = process.argv;
const imgDir = path.join(__dirname, 'images');
const dims = JSON.parse(fs.readFileSync(path.join(imgDir, 'dims.json'), 'utf8'));
const lines = fs.readFileSync(src, 'utf8').split(/\r?\n/);

const FONT = 'Microsoft YaHei';
const MONO = 'Consolas';
const ACCENT = 'D9901A';     // 琥珀色，和软件主题一致
const INK = '1F2328';
const MUTED = '6B7280';
const RULE = 'E5E7EB';
const CONTENT_W = 9638;     // A4 宽 11906 减左右各 1134

// ---------- 行内格式：**粗体** 和 `代码` ----------
function runs(text, base = {}) {
  const parts = text.split(/(\*\*[^*]+\*\*|`[^`]+`)/).filter(Boolean);
  return parts.map(p => {
    if (p.startsWith('**')) return new TextRun({ ...base, text: p.slice(2, -2), bold: true });
    if (p.startsWith('`')) return new TextRun({ ...base, text: p.slice(1, -1), font: MONO, size: (base.size || 21) - 1, shading: { type: ShadingType.CLEAR, fill: 'F3F4F6', color: 'auto' } });
    return new TextRun({ ...base, text: p });
  });
}

const body = [];
let ch = 0, sec = 0, listInstance = 0, inNumbered = false, appendix = false;
let table = null;

function flushTable() {
  if (!table) return;
  const rows = table.filter(r => !/^\s*\|?\s*-{3}/.test(r)).map(r => r.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim()));
  const n = rows[0].length;
  // 第一列窄一些，其余平分
  const first = n === 2 ? Math.round(CONTENT_W * 0.32) : Math.round(CONTENT_W / n);
  const rest = n === 2 ? CONTENT_W - first : Math.round((CONTENT_W - first) / (n - 1));
  const widths = [first, ...Array(n - 1).fill(rest)];
  widths[n - 1] += CONTENT_W - widths.reduce((a, b) => a + b, 0);
  const border = { style: BorderStyle.SINGLE, size: 4, color: RULE };
  body.push(new Table({
    width: { size: CONTENT_W, type: WidthType.DXA },
    columnWidths: widths,
    rows: rows.map((cells, ri) => new TableRow({
      tableHeader: ri === 0,
      cantSplit: true,
      children: cells.map((c, ci) => new TableCell({
        width: { size: widths[ci], type: WidthType.DXA },
        borders: { top: border, bottom: border, left: border, right: border },
        shading: ri === 0 ? { type: ShadingType.CLEAR, fill: 'FDF3E1', color: 'auto' } : undefined,
        margins: { top: 70, bottom: 70, left: 110, right: 110 },
        verticalAlign: VerticalAlign.CENTER,
        children: [new Paragraph({ spacing: { before: 0, after: 0, line: 300 }, children: runs(c, { size: 19, bold: ri === 0 ? true : undefined, color: INK }) })],
      })),
    })),
  }));
  body.push(new Paragraph({ spacing: { before: 0, after: 120 }, children: [] }));
  table = null;
}

for (const raw of lines) {
  const line = raw.replace(/\s+$/, '');
  if (line.startsWith('|')) { (table ??= []).push(line); continue; }
  flushTable();
  if (!/^\s*\d+\.\s/.test(line) && !/^\s{2,}-\s/.test(line)) inNumbered = false;
  if (!line.trim()) continue;

  let m;
  if ((m = line.match(/^# (.+)/))) {
    appendix = m[1] === '附录';
    if (!appendix) ch++;
    sec = 0;
    body.push(new Paragraph({
      heading: HeadingLevel.HEADING_1,
      pageBreakBefore: true,
      children: appendix ? [new TextRun(m[1])] : [new TextRun({ text: `${ch}`, color: ACCENT }), new TextRun(`  ${m[1]}`)],
    }));
  } else if ((m = line.match(/^## (.+)/))) {
    sec++;
    body.push(new Paragraph({ heading: HeadingLevel.HEADING_2, children: [new TextRun(appendix ? `A.${sec}  ${m[1]}` : `${ch}.${sec}  ${m[1]}`)] }));
  } else if ((m = line.match(/^!\[(.*)\]\((.+)\)/))) {
    const file = m[2].replace(/\.png$/, '.jpg');
    const [w, h] = dims[file];
    const maxW = file === 'welcome.jpg' ? 470 : 640;
    const dw = Math.min(maxW, w), dh = Math.round(h * dw / w);
    body.push(new Paragraph({
      alignment: AlignmentType.CENTER, keepNext: true, spacing: { before: 160, after: 60 },
      children: [new ImageRun({ type: 'jpg', data: fs.readFileSync(path.join(imgDir, file)), transformation: { width: dw, height: dh },
        altText: { title: m[1], description: m[1], name: m[1] } })],
    }));
    body.push(new Paragraph({ alignment: AlignmentType.CENTER, spacing: { before: 0, after: 220 }, children: [new TextRun({ text: `图：${m[1]}`, size: 17, color: MUTED })] }));
  } else if ((m = line.match(/^> (.+)/))) {
    body.push(new Paragraph({
      spacing: { before: 120, after: 180, line: 320 },
      indent: { left: 120, right: 120 },
      border: { left: { style: BorderStyle.SINGLE, size: 24, color: ACCENT, space: 10 } },
      shading: { type: ShadingType.CLEAR, fill: 'FDF6EA', color: 'auto' },
      children: runs(m[1], { size: 20, color: INK }),
    }));
  } else if ((m = line.match(/^(\s*)- (.+)/))) {
    const level = m[1].length >= 2 ? 1 : 0;
    body.push(new Paragraph({
      numbering: inNumbered && level === 1 ? { reference: 'bullets', level: 1 } : { reference: 'bullets', level },
      spacing: { before: 30, after: 30, line: 320 },
      children: runs(m[2]),
    }));
  } else if ((m = line.match(/^\d+\.\s(.+)/))) {
    if (!inNumbered) { listInstance++; inNumbered = true; }
    body.push(new Paragraph({ numbering: { reference: 'steps', level: 0, instance: listInstance }, spacing: { before: 40, after: 40, line: 320 }, children: runs(m[1]) }));
  } else if (/^\*\*[^*]+\*\*$/.test(line)) {
    // 常见问题里的问题
    body.push(new Paragraph({ keepNext: true, spacing: { before: 200, after: 40 }, children: runs(line, { color: INK, size: 21 }) }));
  } else {
    body.push(new Paragraph({ spacing: { before: 60, after: 100, line: 330 }, children: runs(line) }));
  }
}
flushTable();

// ---------- 封面 ----------
const cover = [
  new Paragraph({ spacing: { before: 2600, after: 0 }, alignment: AlignmentType.LEFT,
    children: [new ImageRun({ type: 'png', data: fs.readFileSync(path.join(imgDir, 'logo.png')), transformation: { width: 96, height: 96 }, altText: { title: 'WhatPlugin', description: 'WhatPlugin 标志', name: 'logo' } })] }),
  new Paragraph({ spacing: { before: 360, after: 0 }, children: [new TextRun({ text: 'WhatPlugin', size: 80, bold: true, color: INK })] }),
  new Paragraph({ spacing: { before: 60, after: 0 }, children: [new TextRun({ text: '用户手册', size: 44, color: ACCENT })] }),
  new Paragraph({ spacing: { before: 240, after: 0 }, border: { top: { style: BorderStyle.SINGLE, size: 12, color: ACCENT, space: 12 } },
    children: [new TextRun({ text: '给音乐人和混音师的音频插件管理软件', size: 24, color: MUTED })] }),
  new Paragraph({ spacing: { before: 3400, after: 0 }, children: [new TextRun({ text: `版本 ${version}`, size: 20, color: MUTED })] }),
  new Paragraph({ spacing: { before: 40, after: 0 }, children: [new TextRun({ text: 'Windows 10 / 11（64 位）', size: 20, color: MUTED })] }),
];

const toc = [
  new Paragraph({ pageBreakBefore: true, spacing: { before: 0, after: 300 }, children: [new TextRun({ text: '目录', size: 36, bold: true, color: INK })] }),
  new TableOfContents('目录', { hyperlink: true, headingStyleRange: '1-2' }),
];

const footer = new Footer({ children: [new Paragraph({ alignment: AlignmentType.CENTER, children: [new TextRun({ children: [PageNumber.CURRENT], size: 17, color: MUTED })] })] });
const header = new Header({ children: [new Paragraph({
  border: { bottom: { style: BorderStyle.SINGLE, size: 4, color: RULE, space: 4 } },
  tabStops: [{ type: TabStopType.RIGHT, position: CONTENT_W }],
  children: [new TextRun({ text: 'WhatPlugin 用户手册', size: 16, color: MUTED }), new TextRun({ text: `\t版本 ${version}`, size: 16, color: MUTED })],
})] });

const doc = new Document({
  creator: 'WhatPlugin',
  title: 'WhatPlugin 用户手册',
  description: `WhatPlugin ${version} 用户手册`,
  features: { updateFields: true },
  styles: {
    default: { document: { run: { font: FONT, size: 21, color: '30363D' } } },
    paragraphStyles: [
      { id: 'Heading1', name: 'Heading 1', basedOn: 'Normal', next: 'Normal', quickFormat: true,
        run: { font: FONT, size: 40, bold: true, color: INK },
        paragraph: { spacing: { before: 0, after: 360 }, keepNext: true, outlineLevel: 0,
          border: { bottom: { style: BorderStyle.SINGLE, size: 8, color: ACCENT, space: 8 } } } },
      { id: 'Heading2', name: 'Heading 2', basedOn: 'Normal', next: 'Normal', quickFormat: true,
        run: { font: FONT, size: 27, bold: true, color: INK },
        paragraph: { spacing: { before: 360, after: 140 }, keepNext: true, outlineLevel: 1 } },
      { id: 'TOC1', name: 'toc 1', basedOn: 'Normal', next: 'Normal', run: { bold: true, size: 22 }, paragraph: { spacing: { before: 140, after: 40 } } },
      { id: 'TOC2', name: 'toc 2', basedOn: 'Normal', next: 'Normal', run: { size: 20, color: '4B5563' }, paragraph: { indent: { left: 420 }, spacing: { before: 20, after: 20 } } },
    ],
  },
  numbering: {
    config: [
      { reference: 'bullets', levels: [
        { level: 0, format: LevelFormat.BULLET, text: '•', alignment: AlignmentType.LEFT, style: { run: { color: ACCENT }, paragraph: { indent: { left: 420, hanging: 260 } } } },
        { level: 1, format: LevelFormat.BULLET, text: '–', alignment: AlignmentType.LEFT, style: { run: { color: MUTED }, paragraph: { indent: { left: 840, hanging: 260 } } } },
      ] },
      { reference: 'steps', levels: [
        { level: 0, format: LevelFormat.DECIMAL, text: '%1.', alignment: AlignmentType.LEFT, style: { run: { bold: true, color: ACCENT }, paragraph: { indent: { left: 420, hanging: 320 } } } },
        { level: 1, format: LevelFormat.BULLET, text: '–', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 840, hanging: 260 } } } },
      ] },
    ],
  },
  sections: [
    { properties: { page: { size: { width: 11906, height: 16838 }, margin: { top: 1134, bottom: 1134, left: 1134, right: 1134 } } },
      children: cover },
    { properties: { page: { size: { width: 11906, height: 16838 }, margin: { top: 1300, bottom: 1134, left: 1134, right: 1134, header: 600, footer: 500 }, pageNumbers: { start: 1 } } },
      headers: { default: header }, footers: { default: footer },
      children: [...toc, ...body] },
  ],
});

Packer.toBuffer(doc).then(b => { fs.writeFileSync(out, b); console.log('written', out, b.length); });
