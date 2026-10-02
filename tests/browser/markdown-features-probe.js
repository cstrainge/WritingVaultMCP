window.runMarkdownFeatureProbe = async () => {
  const seen = [], checks = [];
  const assert = (condition, label) => { if (!condition) throw new Error(label); checks.push(label); };
  const host = document.createElement('article'); host.className = 'record-prose';
  host.style.maxWidth = '960px'; host.style.margin = '2rem auto'; host.style.padding = '1rem';
  (document.querySelector('main') || document.body).append(host);
  WritingVaultMarkdown.configure({
    image(link, image, reference, revision) { seen.push({ type: 'image', reference, revision, width: image.style.width }); },
    record(link, reference, revision) { seen.push({ type: 'record', reference, revision }); }
  });
  const longAlt = 'An accessible cover description. '.repeat(20);
  const source = [
    '# Markdown features', '', '[toc]', '',
    '## Prose and formatting', '',
    'A paragraph wraps across', 'two source lines without becoming two paragraphs.', '',
    '**Bold with _nested emphasis_**, __bold__, *italic*, ~~removed~~, ==highlighted==, ++inserted++, H~2~O, x^2^, :sparkles:.', '',
    'Setext heading', '--------------', '', '#### Fourth level', '##### Fifth level', '###### Sixth level', '',
    '1. Ordered item', '   - Nested bullet', '     - Deeper bullet', '2. Second item', '',
    '- [x] Checked task', '- [ ] Open task', '',
    '> Outer quote', '>', '> > Nested quote', '',
    '---', '',
    '## References', '',
    '[Reference link][manual] and <https://example.test/manual>.', '',
    '[manual]: https://example.test/manual "Manual title"', '',
    'A footnote[^one] and another inline note.^[Inline footnote text.]', '',
    '[^one]: Footnote **with formatting**.', '',
    '*[MCP]: Model Context Protocol', '', 'MCP connects the Vault.', '',
    'Continuity', ': The shared story world.', '',
    '[Go to references](#references)', '',
    '[Pinned friend](vault-record:character:friend~ABCDEFGH?v=2)', '',
    `![${longAlt}](vault-image:image:cover~ABCDEFGH?v=3){width=200px}`, '',
    '## Tables', '',
    '| Left | Center | Right |', '| :--- | :---: | ---: |', '| a \\| b | **bold** | 3 |', '',
    '| Group || Count |', '| --- | --- | --- |', '| wide || 2 |', '[Table caption]', '',
    '## Callouts and disclosures', '',
    '> [!NOTE]', '> A **formatted** callout.', '',
    '::: warning Continuity conflict', 'Check the dates.', ':::', '',
    '::: details Spoiler: reveal the answer', 'The answer is **forty-two**.', ':::', '',
    '<details><summary>HTML disclosure</summary><p>Safe <kbd>Ctrl</kbd> + <kbd>K</kbd>.</p></details>', '',
    '## Code', '', '```javascript', 'const answer = 42;', '```', '',
    '## Mathematics', '', 'Inline $x^2 + y^2 = z^2$ and \\(E=mc^2\\).', '',
    '$$', '\\frac{1}{2} + \\sqrt{4} = \\frac{5}{2}', '$$', '',
    '```math', '\\sum_{n=1}^{10} n = 55', '```', '',
    '## Diagram', '', '```mermaid', 'flowchart LR', '  A[Arrival Day] --> B[Annual celebration]', '```'
  ].join('\n');
  await WritingVaultMarkdown.renderMarkdown(host, source);
  assert(!host.querySelector('.note-render-error'), 'Feature document rendered without fallback');
  assert(host.querySelectorAll('h1,h2,h3,h4,h5,h6').length >= 10, 'All heading levels');
  assert(host.querySelector('strong em') && host.querySelector('s') && host.querySelector('mark') && host.querySelector('ins'), 'Nested and extended formatting');
  assert(host.querySelector('sub') && host.querySelector('sup'), 'Subscript and superscript');
  assert(host.textContent.includes('✨'), 'Emoji shortcode');
  assert([...host.querySelectorAll('p')].some(p=>p.textContent.includes('across\ntwo source lines')), 'Normal paragraph wrapping');
  assert(host.querySelector('ol ul ul'), 'Nested numbered and bullet lists');
  assert(host.querySelectorAll('input[type=checkbox]:disabled').length === 2 && host.querySelector('input:checked'), 'Task lists');
  assert(host.querySelector('blockquote blockquote') && host.querySelector('hr'), 'Nested quotes and horizontal rule');
  assert(host.querySelector('a[title="Manual title"]') && host.querySelectorAll('a[href="https://example.test/manual"]').length === 2, 'Reference links and autolinks');
  assert(host.querySelector('abbr[title="Model Context Protocol"]') && host.querySelector('dl dd'), 'Abbreviations and definitions');
  assert(host.querySelectorAll('.footnote-ref').length === 2, 'Named and inline footnotes');
  const footlink = host.querySelector('.footnote-ref a');
  assert(document.getElementById(footlink.getAttribute('href').slice(1)), 'Footnote destination exists');
  assert(host.querySelector('.note-toc a') && host.querySelector('a[href$="-references"]'), 'Table of contents and heading anchors');
  assert(seen.some(x=>x.type==='record'&&x.revision===2) && seen.some(x=>x.type==='image'&&x.revision===3&&x.width==='200px'), 'Pinned Vault record and sized image');
  assert(host.querySelector('img').alt === longAlt.trim(), 'Long image description');
  const linked = document.createElement('div'); host.append(linked);
  await WritingVaultMarkdown.renderMarkdown(linked, '[![Linked](vault-image:image:cover~ABCDEFGH)](https://example.test/book)');
  assert(linked.querySelectorAll('a').length === 1 && linked.querySelector('a').href === 'https://example.test/book', 'Linked Vault images preserve their explicit destination');
  linked.remove();
  assert(host.querySelectorAll('table').length === 2 && host.querySelector('td[colspan="2"]') && host.querySelector('caption'), 'Tables, merged cells and captions');
  assert(host.querySelector('th[style*="center"]'), 'Table alignment');
  assert(host.querySelectorAll('.markdown-alert').length === 2 && host.querySelectorAll('details').length >= 3 && host.querySelector('kbd'), 'Alerts, containers and safe HTML');
  assert(host.querySelector('code .hljs-keyword'), 'Code highlighting');
  assert(host.querySelectorAll('.katex').length === 4 && host.querySelector('math'), 'Inline, display and fenced math with accessible MathML');
  assert(host.querySelector('.note-diagram > svg'), 'Mermaid renders locally');
  assert(!document.querySelector('.note-diagram-staging'), 'Diagram staging cleaned up');

  const duplicate = document.createElement('div'); host.append(duplicate);
  await WritingVaultMarkdown.renderMarkdown(duplicate, '## References\n\nAgain[^one].\n\n[^one]: Another note.');
  const ids = [...host.querySelectorAll('[id]')].map(x=>x.id);
  assert(new Set(ids).size === ids.length, 'IDs do not collide across notes');
  const originalHash = location.hash; footlink.click();
  assert(location.hash === originalHash, 'Footnotes do not change the application route');

  const hostile = document.createElement('div'); host.append(hostile);
  window.markdownProbeInjected = false;
  await WritingVaultMarkdown.renderMarkdown(hostile,
    '<script>window.markdownProbeInjected=true</scr'+'ipt>\n\n'+
    '<img src="x" onerror="window.markdownProbeInjected=true">\n\n'+
    '<iframe src="https://example.test"></iframe><form><input autofocus></form>\n\n'+
    '<style>body{display:none}</style><div style="position:fixed;background:url(https://example.test/leak)">Visible</div>\n\n'+
    '[Bad](javascript:alert(1))\n\n'+
    '![Bad](vault-image:image:cover~ABCDEFGH?v=0)\n\n'+
    '<a href="javascript:alert(1)">Bad HTML</a>');
  assert(!window.markdownProbeInjected && !hostile.querySelector('script,style,iframe,form,[onerror],[autofocus],input'), 'Active HTML is removed');
  assert(!hostile.querySelector('[style*="fixed"],[style*="url("],a[href^="javascript"]'), 'Unsafe URLs and styles are removed');
  assert(hostile.textContent.includes('vault-image:image:cover~ABCDEFGH?v=0'), 'Invalid pinned image remains readable');
  hostile.remove(); duplicate.remove();

  const broken = document.createElement('div'); host.append(broken);
  await WritingVaultMarkdown.renderMarkdown(broken, '```mermaid\nnot a diagram\n```\n\nOther prose still renders.');
  assert(broken.querySelector('.note-render-error') && broken.textContent.includes('Other prose still renders.'), 'Invalid diagrams preserve source and surrounding prose');
  broken.remove();
  host.querySelector('h1').scrollIntoView({block:'start'});
  return {passed:true,checks,featureCount:checks.length,bodyWidth:document.documentElement.scrollWidth,viewport:document.documentElement.clientWidth};
};
