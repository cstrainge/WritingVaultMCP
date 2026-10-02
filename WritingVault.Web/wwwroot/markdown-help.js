(() => {
  'use strict';
  const guide = document.getElementById('markdown-guide');
  fetch('/markdown-help.md').then(response => {
    if (!response.ok) throw new Error('Guide unavailable');
    return response.text();
  }).then(source => {
    guide.replaceChildren();
    return window.WritingVaultMarkdown.renderMarkdown(guide, source);
  }).catch(() => { guide.textContent = 'The Markdown guide could not load. Refresh to try again.'; });
})();
