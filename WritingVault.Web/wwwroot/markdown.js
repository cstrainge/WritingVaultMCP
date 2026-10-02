(() => {
  'use strict';
  const deps = window.WritingVaultMarkdownDeps;
  const assets = new URL('vendor/markdown/', document.currentScript.src);
  const safeUrl = value => {
    try {
      const url = new URL(value);
      return ['https:', 'http:'].includes(url.protocol) ? url.href : null;
    } catch { return null; }
  };
  const semanticRef = value => /^[a-z][a-z0-9-]*:[a-z0-9-]+~[A-Za-z0-9_-]+$/i.test(value);
  const vaultTarget = (value, prefix) => {
    if (!value?.startsWith(prefix)) return null;
    const target = value.slice(prefix.length), pinned = target.match(/^(.+)\?v=([1-9]\d*)$/);
    const reference = pinned ? pinned[1] : target, revision = pinned ? Number(pinned[2]) : null;
    return semanticRef(reference) && (revision === null || Number.isSafeInteger(revision)) ? { reference, revision } : null;
  };
  let handlers = Object.freeze({});
  let sequence = 0, diagramSequence = 0, mermaidLoading;
  const configure = value => { handlers = Object.freeze({ ...value }); };
  const md = new deps.MarkdownIt({ html: true, linkify: true, breaks: false, typographer: false,
    highlight: (code, language) => {
      if (!language || !deps.highlight.getLanguage(language)) return '';
      try { return deps.highlight.highlight(code, { language, ignoreIllegals: true }).value; }
      catch { return ''; }
    }
  });
  const escape = md.utils.escapeHtml;
  md.use(deps.footnote).use(deps.deflist).use(deps.abbr).use(deps.sub).use(deps.sup)
    .use(deps.ins).use(deps.mark).use(deps.emoji, { shortcuts: {} })
    .use(deps.tasks, { enabled: false })
    .use(deps.tables, { multiline: true, rowspan: true, headerless: true, multibody: false, autolabel: false })
    .use(deps.attrs, { allowedAttributes: ['id', 'class', 'width', 'height', 'title'] })
    .use(deps.anchor, { tabIndex: '-1', failOnNonUnique: false }).use(deps.alerts)
    .use(deps.texmath, { delimiters: ['dollars', 'brackets', 'beg_end'],
      engine: { renderToString: (source, options) => deps.katex.renderToString(source,
        { ...options, macros: {}, trust: false, throwOnError: false, maxExpand: 1000, maxSize: 20 }) }
    });

  for (const kind of ['note', 'tip', 'important', 'warning', 'caution', 'details', 'spoiler']) {
    md.use(deps.container, kind, {
      validate: params => params.trim().split(/\s+/)[0] === kind,
      render: (tokens, index) => {
        const disclosure = kind === 'details' || kind === 'spoiler';
        if (tokens[index].nesting < 0) return disclosure ? '</details>\n' : '</aside>\n';
        const title = tokens[index].info.trim().slice(kind.length).trim() ||
          (disclosure ? 'Show details' : kind[0].toUpperCase() + kind.slice(1));
        return disclosure ? `<details class="note-disclosure"><summary>${escape(title)}</summary>\n` :
          `<aside class="markdown-alert markdown-alert-${kind}"><p class="markdown-alert-title">${escape(title)}</p>\n`;
      }
    });
  }

  // Custom destinations remain data until the authenticated Vault handlers
  // resolve them. Neither the parser nor sanitizer fetches these destinations.
  const normalLink = md.renderer.rules.link_open || ((tokens, index, options, env, renderer) => renderer.renderToken(tokens, index, options));
  md.renderer.rules.link_open = (tokens, index, options, env, renderer) => {
    const token = tokens[index], target = vaultTarget(token.attrGet('href'), 'vault-record:');
    if (target) {
      token.attrSet('data-vault-record', target.reference);
      if (target.revision !== null) token.attrSet('data-vault-revision', String(target.revision));
      token.attrSet('href', '#'); token.attrJoin('class', 'note-record-link');
    }
    return normalLink(tokens, index, options, env, renderer);
  };
  const normalImage = md.renderer.rules.image;
  md.renderer.rules.image = (tokens, index, options, env, renderer) => {
    const token = tokens[index], source = token.attrGet('src') || '';
    const target = vaultTarget(source, 'vault-image:');
    if (source.startsWith('vault-image:') && !target) return escape(`![${token.content}](${source})`);
    if (target) {
      token.attrSet('data-vault-image', target.reference); token.attrSet('src', '');
      if (target.revision !== null) token.attrSet('data-vault-revision', String(target.revision));
    }
    return normalImage(tokens, index, options, env, renderer);
  };
  const normalFence = md.renderer.rules.fence;
  // MultiMarkdown tables preserve escaped pipes in code tokens; GFM treats
  // the escape as a table delimiter escape, not part of the displayed code.
  md.core.ruler.after('inline', 'vault_table_code_pipes', state => {
    let depth = 0;
    for (const token of state.tokens) {
      if (token.type === 'table_open') depth++;
      if (depth && token.children) for (const child of token.children)
        if (child.type === 'code_inline') child.content = child.content.replace(/\\\|/g, '|');
      if (token.type === 'table_close') depth--;
    }
  });
  md.renderer.rules.fence = (tokens, index, options, env, renderer) => {
    const token = tokens[index], language = token.info.trim().split(/\s+/)[0].toLowerCase();
    if (language === 'mermaid') return `<pre class="note-diagram-source"><code>${escape(token.content)}</code></pre>\n`;
    if (language === 'math' || language === 'latex') return `<div class="note-math">${deps.katex.renderToString(token.content,
      { displayMode: true, throwOnError: false, trust: false, macros: {}, maxExpand: 1000, maxSize: 20 })}</div>\n`;
    return normalFence(tokens, index, options, env, renderer);
  };
  md.core.ruler.after('inline', 'vault_toc', state => {
    for (let i = 1; i < state.tokens.length - 1; i++) {
      const token = state.tokens[i];
      if (token.type === 'inline' && /^\[\[?toc\]?\]$/i.test(token.content.trim()) &&
          state.tokens[i - 1].type === 'paragraph_open' && state.tokens[i + 1].type === 'paragraph_close') {
        token.type = 'html_inline'; token.content = '<nav class="note-toc" aria-label="Table of contents"></nav>';
      }
    }
  });

  const sanitize = html => deps.purify.sanitize(html, {
    RETURN_DOM_FRAGMENT: true, ADD_TAGS: ['eq', 'eqn'],
    FORBID_TAGS: ['script', 'style', 'iframe', 'object', 'embed', 'form', 'button', 'textarea', 'select', 'option', 'link', 'meta', 'base', 'video', 'audio', 'source'],
    FORBID_ATTR: ['srcset', 'form', 'formaction', 'autofocus', 'contenteditable', 'srcdoc', 'ping'],
    ALLOW_DATA_ATTR: true
  });
  const safeDimension = value => /^(?:[1-9]\d{0,3})(?:px|%)?$/.test(value || '') &&
    Number.parseInt(value) <= (value.endsWith('%') ? 100 : 2048);
  const safeHref = value => value?.startsWith('#') || safeUrl(value) || /^mailto:[^\s<>]+$/i.test(value || '');
  function prepareFragment(fragment, prefix) {
    const targets = new Map(), used = new Set();
    for (const node of fragment.querySelectorAll('[id]')) {
      const original = node.id;
      let scoped = `${prefix}${original}`;
      if (used.has(scoped)) scoped += `-${++sequence}`;
      used.add(scoped); if (!targets.has(original)) targets.set(original, scoped);
      node.id = scoped;
    }
    for (const node of fragment.querySelectorAll('*')) {
      node.removeAttribute('name');
      for (const attribute of ['aria-labelledby', 'aria-describedby', 'for']) {
        const references = node.getAttribute(attribute);
        if (references) node.setAttribute(attribute, references.split(/\s+/).map(id => targets.get(id) || id).join(' '));
      }
      // Retain passive layout values needed by math and tables, never CSS URLs
      // or fixed/absolute overlays supplied by a note's raw HTML.
      const saved = [...node.style].map(property => [property, node.style.getPropertyValue(property)]);
      node.removeAttribute('style');
      for (const [property, value] of saved) {
        if (property === 'text-align' && /^(left|right|center)$/.test(value) ||
            property === 'position' && value === 'relative' ||
            property === 'color' && /^(#[\da-f]{3,8}|[a-z]+|rgba?\([\d.,%\s]+\))$/i.test(value) ||
            /^(height|width|min-width|font-size|top|bottom|left|right|margin-(left|right|top|bottom)|padding-(left|right|top|bottom)|vertical-align|border-(top|bottom|left|right)-width)$/.test(property) &&
            /^-?(?:\d+(?:\.\d+)?|\.\d+)(em|ex|px|rem|%)?$/.test(value)) node.style.setProperty(property, value);
      }
      if (node.matches('input')) {
        if (node.type === 'checkbox') { node.disabled = true; node.removeAttribute('id'); }
        else node.remove();
      }
      if (node.hasAttribute('href')) {
        const href = node.getAttribute('href');
        if (href.startsWith('#') && targets.has(href.slice(1))) node.setAttribute('href', `#${targets.get(href.slice(1))}`);
        else if (!safeHref(href)) node.removeAttribute('href');
        if (/^https?:/i.test(href)) { node.setAttribute('target', '_blank'); node.setAttribute('rel', 'noopener noreferrer'); }
      }
      for (const attribute of ['xlink:href', 'filter', 'fill', 'stroke']) {
        const value = node.getAttribute(attribute);
        if (value && (/url\(/i.test(value) && !/^url\(#[\w-]+\)$/.test(value) || attribute === 'xlink:href' && !value.startsWith('#'))) node.removeAttribute(attribute);
      }
    }
    for (const link of fragment.querySelectorAll('[data-vault-record]')) {
      const reference = link.dataset.vaultRecord, revision = link.dataset.vaultRevision ? Number(link.dataset.vaultRevision) : null;
      if (semanticRef(reference) && (revision === null || Number.isSafeInteger(revision) && revision > 0) && handlers.record)
        handlers.record(link, reference, revision);
      else link.removeAttribute('href');
    }
    for (const image of fragment.querySelectorAll('img')) {
      image.loading = 'lazy'; image.decoding = 'async'; image.referrerPolicy = 'no-referrer';
      const reference = image.dataset.vaultImage, revision = image.dataset.vaultRevision ? Number(image.dataset.vaultRevision) : null;
      const width = image.getAttribute('width'), height = image.getAttribute('height');
      image.removeAttribute('width'); image.removeAttribute('height');
      if (safeDimension(width)) image.style.width = /^\d+$/.test(width) ? `${width}px` : width;
      if (safeDimension(height)) image.style.height = /^\d+$/.test(height) ? `${height}px` : height;
      if (reference && semanticRef(reference) && (revision === null || Number.isSafeInteger(revision) && revision > 0) && handlers.image) {
        image.removeAttribute('src');
        const link = document.createElement('a'); link.className = 'note-image-link';
        // An explicitly linked image keeps its author-supplied destination.
        // A detached handler link still lets the image loader supply its bytes.
        if (!image.closest('a')) { image.replaceWith(link); link.append(image); }
        handlers.image(link, image, reference, revision);
      } else if (!safeUrl(image.getAttribute('src'))) image.replaceWith(document.createTextNode(image.alt || 'Image unavailable'));
    }
    for (const table of fragment.querySelectorAll('table')) {
      table.classList.add('note-table');
      const wrapper = document.createElement('div'); wrapper.className = 'note-table-wrap'; wrapper.tabIndex = 0;
      wrapper.setAttribute('role', 'region'); wrapper.setAttribute('aria-label', 'Note table; scroll horizontally for more columns');
      table.replaceWith(wrapper); wrapper.append(table);
      for (const heading of table.querySelectorAll('thead th')) heading.scope = 'col';
    }
    for (const toc of fragment.querySelectorAll('.note-toc')) {
      const list = document.createElement('ul');
      for (const heading of fragment.querySelectorAll('h1[id],h2[id],h3[id],h4[id],h5[id],h6[id]')) {
        const item = document.createElement('li'), link = document.createElement('a');
        item.className = `note-toc-level-${heading.tagName.slice(1)}`;
        link.textContent = heading.textContent; link.href = `#${heading.id}`; item.append(link); list.append(item);
      }
      toc.replaceChildren(list);
    }
    for (const link of fragment.querySelectorAll('a[href^="#"]:not([data-vault-record])')) {
      link.addEventListener('click', event => {
        const id = link.getAttribute('href').slice(1), target = document.getElementById(id);
        if (target && id.startsWith(prefix)) { event.preventDefault(); target.scrollIntoView({ block: 'start' }); target.focus({ preventScroll: true }); }
      });
    }
  }

  function loadMermaid() {
    if (!mermaidLoading) mermaidLoading = new Promise((resolve, reject) => {
      const script = document.createElement('script'); script.src = new URL('mermaid.js', assets).href;
      script.onload = () => resolve(window.WritingVaultMermaid.default);
      script.onerror = () => { mermaidLoading = null; script.remove(); reject(new Error('The local diagram renderer could not load.')); };
      document.head.append(script);
    });
    return mermaidLoading;
  }
  let diagramQueue = Promise.resolve();
  async function renderDiagram(sourceNode) {
    if (!sourceNode.isConnected) return;
    const source = sourceNode.textContent;
    const figure = document.createElement('figure'); figure.className = 'note-diagram';
    const disclosure = document.createElement('details'), summary = document.createElement('summary');
    summary.textContent = 'Diagram source'; disclosure.append(summary); sourceNode.replaceWith(figure); disclosure.append(sourceNode); figure.append(disclosure);
    let staging;
    try {
      const mermaid = await loadMermaid();
      if (!figure.isConnected) return;
      const colors = getComputedStyle(document.documentElement);
      mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', suppressErrorRendering: true,
        maxTextSize: 100000, maxEdges: 1000, theme: 'base', htmlLabels: false,
        flowchart: { htmlLabels: false },
        themeVariables: { primaryColor: colors.getPropertyValue('--surface-2').trim(),
          primaryTextColor: colors.getPropertyValue('--ink').trim(),
          primaryBorderColor: colors.getPropertyValue('--accent').trim(),
          lineColor: colors.getPropertyValue('--ink').trim(), fontFamily: 'system-ui, sans-serif' },
        secure: ['securityLevel', 'startOnLoad', 'maxTextSize', 'maxEdges', 'suppressErrorRendering', 'themeCSS', 'themeVariables', 'htmlLabels', 'flowchart'] });
      staging = document.createElement('div'); staging.className = 'note-diagram-staging'; document.body.append(staging);
      const result = await mermaid.render(`vault-diagram-${++diagramSequence}`, source, staging);
      if (!figure.isConnected) return;
      const drawing = deps.purify.sanitize(result.svg, { RETURN_DOM_FRAGMENT: true, USE_PROFILES: { svg: true, svgFilters: true },
        ADD_TAGS: ['style'], FORBID_TAGS: ['foreignObject', 'script', 'a', 'image', 'use'] });
      const svg = drawing.querySelector('svg');
      if (!svg) throw new Error('No diagram was produced.');
      svg.setAttribute('role', 'img');
      if (!svg.hasAttribute('aria-label') && !svg.hasAttribute('aria-labelledby')) svg.setAttribute('aria-label', 'Mermaid diagram');
      figure.prepend(drawing);
    } catch {
      const message = document.createElement('p'); message.className = 'note-render-error';
      message.textContent = 'This diagram could not be rendered. Its source is available below.';
      figure.prepend(message); disclosure.open = true;
    } finally { staging?.remove(); }
  }
  const renderMarkdown = (container, source) => {
    const prefix = `vault-note-${++sequence}-`;
    let fragment;
    try { fragment = sanitize(md.render(String(source), {})); prepareFragment(fragment, prefix); }
    catch {
      const fallback = document.createElement('pre'); fallback.textContent = String(source);
      fallback.className = 'note-render-error'; container.append(fallback); return Promise.resolve();
    }
    const diagrams = [...fragment.querySelectorAll('.note-diagram-source')];
    container.append(fragment);
    for (const diagram of diagrams) diagramQueue = diagramQueue.then(() => renderDiagram(diagram));
    return diagramQueue;
  };
  window.WritingVaultMarkdown = Object.freeze({ safeUrl, renderMarkdown, configure });
})();
