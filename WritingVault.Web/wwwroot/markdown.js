(() => {
  'use strict';
  const safeUrl = value => {
    try {
      const url = new URL(value);
      return ['https:', 'http:'].includes(url.protocol) ? url.href : null;
    } catch { return null; }
  };
  const semanticRef = value => /^[a-z][a-z0-9-]*:[a-z0-9-]+~[A-Za-z0-9_-]+$/i.test(value);
  let handlers = Object.freeze({});
  const configure = value => { handlers = Object.freeze({ ...value }); };
  const renderInline = (node, source, context) => {
    const pattern = /!\[([^\]\n]*)\]\(([^)\s]{1,2048})\)(\{[^}\n]*\})?|\[([^\]\n]{1,300})\]\(([^)\s]{1,2048})\)|`([^`\n]+)`|\*\*([^*\n]+)\*\*|\*([^*\n]+)\*/g;
    let offset = 0; let match;
    while ((match = pattern.exec(source)) !== null) {
      node.append(document.createTextNode(source.slice(offset, match.index)));
      if (match[1] !== undefined) {
        const imageTarget = match[2].startsWith('vault-image:') ? match[2].slice('vault-image:'.length) : null;
        const pinned = imageTarget?.match(/^(.+)\?v=([1-9]\d*)$/);
        const reference = pinned ? pinned[1] : imageTarget;
        const revision = pinned ? Number(pinned[2]) : null;
        const width = match[3] ? /^\{width=(\d{1,4})(px|%)\}$/.exec(match[3]) : null;
        const size = width ? Number(width[1]) : null;
        const validWidth = !match[3] || (width && size > 0 &&
          (width[2] === 'px' ? size <= 2048 : size <= 100));
        if (reference && semanticRef(reference) && (revision === null || Number.isSafeInteger(revision)) &&
            match[1].trim() && validWidth &&
            context.imageCount < 24 && typeof handlers.image === 'function') {
          context.imageCount++;
          const link = document.createElement('a'); const image = document.createElement('img');
          image.alt = match[1]; image.loading = 'lazy';
          image.style.maxWidth = '100%'; image.style.height = 'auto';
          if (width) image.style.width = `${size}${width[2]}`;
          link.className = 'note-image-link'; link.append(image); node.append(link);
          handlers.image(link, image, reference, revision);
        } else node.append(document.createTextNode(match[0]));
      } else if (match[4] !== undefined) {
        const recordTarget = match[5].startsWith('vault-record:') ? match[5].slice('vault-record:'.length) : null;
        const pinned = recordTarget?.match(/^(.+)\?v=([1-9]\d*)$/);
        const reference = pinned ? pinned[1] : recordTarget;
        const revision = pinned ? Number(pinned[2]) : null;
        const href = safeUrl(match[5]);
        if (reference && semanticRef(reference) && (revision === null || Number.isSafeInteger(revision)) &&
            context.recordCount < 50 &&
            typeof handlers.record === 'function') {
          context.recordCount++;
          const link = document.createElement('a'); link.textContent = match[4];
          link.className = 'note-record-link'; node.append(link);
          handlers.record(link, reference, revision);
        } else if (href) {
          const link = document.createElement('a');
          link.href = href; link.target = '_blank'; link.rel = 'noopener noreferrer';
          link.textContent = match[4]; node.append(link);
        } else node.append(document.createTextNode(match[0]));
      } else {
        const tag = match[6] !== undefined ? 'code' : match[7] !== undefined ? 'strong' : 'em';
        const element = document.createElement(tag);
        element.textContent = match[6] ?? match[7] ?? match[8]; node.append(element);
      }
      offset = pattern.lastIndex;
    }
    node.append(document.createTextNode(source.slice(offset)));
  };
  const tableCells = line => {
    // Escaped pipes belong to the cell, including pipes inside inline code.
    const cells = []; let cell = ''; let separated = false;
    for (let i = 0; i < line.length; i++) {
      if (line[i] === '\\' && i + 1 < line.length) {
        const next = line[++i];
        cell += next === '|' ? '|' : `\\${next}`;
      } else if (line[i] === '|') {
        cells.push(cell.trim()); cell = ''; separated = true;
      } else cell += line[i];
    }
    cells.push(cell.trim());
    if (line.trimStart().startsWith('|')) cells.shift();
    if (cells[cells.length - 1] === '' && /\|\s*$/.test(line)) cells.pop();
    return separated ? cells : null;
  };
  const renderMarkdown = (container, source) => {
    const lines = String(source).split(/\r?\n/);
    let list = null; let code = null; const context = { imageCount: 0, recordCount: 0 };
    for (let index = 0; index < lines.length; index++) {
      const line = lines[index];
      if (/^\s*```/.test(line)) {
        if (code) { container.append(code); code = null; }
        else { code = document.createElement('pre'); code.append(document.createElement('code')); }
        list = null; continue;
      }
      if (code) { code.firstChild.textContent += `${line}\n`; continue; }
      if (!line.trim()) { list = null; continue; }
      const headers = tableCells(line);
      const separators = tableCells(lines[index + 1] || '');
      if (headers?.length && separators?.length === headers.length &&
          separators.every(cell => /^:?-{3,}:?$/.test(cell))) {
        list = null;
        const wrapper = document.createElement('div'); wrapper.className = 'note-table-wrap';
        wrapper.tabIndex = 0; wrapper.setAttribute('role', 'region');
        wrapper.setAttribute('aria-label', 'Note table; scroll horizontally for more columns');
        const table = document.createElement('table'); table.className = 'note-table';
        const head = document.createElement('thead'), body = document.createElement('tbody');
        const addRow = (parent, cells, tag) => {
          const row = document.createElement('tr');
          headers.forEach((_, column) => {
            const cell = document.createElement(tag);
            if (tag === 'th') cell.scope = 'col';
            const marker = separators[column];
            if (marker.endsWith(':')) cell.style.textAlign = marker.startsWith(':') ? 'center' : 'right';
            else if (marker.startsWith(':')) cell.style.textAlign = 'left';
            renderInline(cell, cells[column] || '', context); row.append(cell);
          });
          parent.append(row);
        };
        addRow(head, headers, 'th'); index++;
        while (index + 1 < lines.length) {
          const cells = tableCells(lines[index + 1]);
          if (!cells || !lines[index + 1].trim()) break;
          addRow(body, cells, 'td'); index++;
        }
        table.append(head, body); wrapper.append(table); container.append(wrapper);
        continue;
      }
      const heading = line.match(/^(#{1,3})\s+(.+)$/);
      const bullet = line.match(/^\s*[-*+]\s+(.+)$/);
      let element;
      if (heading) { element = document.createElement(`h${Math.min(heading[1].length + 1, 4)}`); renderInline(element, heading[2], context); list = null; }
      else if (bullet) { if (!list) { list = document.createElement('ul'); container.append(list); } element = document.createElement('li'); renderInline(element, bullet[1], context); }
      else if (line.startsWith('> ')) { element = document.createElement('blockquote'); renderInline(element, line.slice(2), context); list = null; }
      else { element = document.createElement('p'); renderInline(element, line, context); list = null; }
      (bullet ? list : container).append(element);
    }
    if (code) container.append(code);
  };
  window.WritingVaultMarkdown = Object.freeze({ safeUrl, renderMarkdown, configure });
})();
