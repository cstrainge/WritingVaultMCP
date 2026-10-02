# Browser Markdown dependencies

The viewer uses the checked-in bundles and fonts in
`WritingVault.Web/wwwroot/vendor/markdown`. Normal .NET builds need neither Node
nor an internet connection. Mermaid is loaded on demand.

To reproduce the assets, use Node.js 24 LTS from this directory:

```powershell
npm ci --ignore-scripts
npm audit
npm run build
```

Direct versions are exact, transitive versions and package integrity are pinned
by `package-lock.json`. The build records the lockfile hash and every generated
asset hash in `manifest.json`, and collects runtime dependency licenses in
`THIRD-PARTY-NOTICES.txt`. Esbuild's installed platform binary works without its
postinstall script. Do not copy CDN scripts into the viewer or update the
generated JavaScript by hand.

`vendor.mjs` selects the parser, extensions, sanitizer, math renderer, and all
Highlight.js languages. `mermaid.mjs` selects the diagram library. The build also
copies the canonical user guide from `docs/MARKDOWN.md` into the served root.

The integration is in `wwwroot/markdown.js`; its public `configure`, `safeUrl`,
and `renderMarkdown` API is retained. `renderMarkdown` inserts prose synchronously
and returns a promise that settles after queued diagrams finish. Callers that
only need prose can continue to ignore that promise.

Security: DOMPurify sanitizes the rendered DOM, then the integration scopes IDs,
filters links, limits CSS to passive values, disables form controls, and resolves
Vault destinations through the existing authenticated handlers. External HTTP(S)
images are lazy-loaded without referrers. KaTeX has `trust: false` with per-formula
macros. Mermaid uses strict mode; clickable links and active SVG embeds are
removed. The CSP allows inline presentation styles required by math and diagrams
but keeps scripts and fonts local. No note can supply a stylesheet or script.

Compatibility: `@peaceroad/markdown-it-multimd-table` supports markdown-it 15;
the original table plugin uses removed parser utilities. `multibody` is disabled
so blank lines continue to separate existing cover tables. A small token rule
normalizes escaped pipes inside table code spans. Blank-line paragraph handling
and heading levels now follow Markdown, rather than the old custom parser.

Browser checks:

- `tests/browser/markdown-features-probe.html` and its JS: common and rare syntax,
  Vault integration, note-local anchors, sanitization, math, diagrams, fallbacks.
- `tests/browser/markdown-diagrams-probe.js`: multiple Mermaid renderers and labels.
- `tests/browser/markdown-table-probe.html`: table escaping, sizing, and long alt text.
- `tests/browser/pinned-markdown-probe.html`: pinned/current images and invalid revisions.

Use `tools/Capture-WritingVaultPreview.ps1` with a disposable Debug viewer and
`-ScriptFileToEvaluate tests/browser/markdown-features-probe.js
-ProbeExpression 'runMarkdownFeatureProbe()'` to exercise the actual HTTP CSP.
Run the diagram probe the same way with `runMarkdownDiagramProbe()`.
Verify `markdown-help.html` and the live cover note after deployment; do not seed
test notes in the production database.
