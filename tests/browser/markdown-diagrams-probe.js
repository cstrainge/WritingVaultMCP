window.runMarkdownDiagramProbe = async () => {
  const results = [];
  const host = document.createElement('article'); host.className = 'record-prose'; document.body.append(host);
  const cases = {
    sequence: 'sequenceDiagram\n  Alice->>Bob: Hello Bob\n  Bob-->>Alice: Hello Alice',
    state: 'stateDiagram-v2\n  [*] --> Draft\n  Draft --> Published\n  Published --> [*]',
    class: 'classDiagram\n  class Character {\n    +name\n  }\n  Character --> Location',
    entity: 'erDiagram\n  CHARACTER ||--o{ SCENE : appears\n  CHARACTER {\n    string name\n  }',
    gantt: 'gantt\n  title Book plan\n  dateFormat YYYY-MM-DD\n  section Draft\n  Writing :2024-01-01, 10d',
    mindmap: 'mindmap\n  root((Lostville))\n    Characters\n    Places',
    pie: 'pie title Scenes\n  "Drafted" : 6\n  "Planned" : 4'
  };
  for (const [name, source] of Object.entries(cases)) {
    const note = document.createElement('section'); host.append(note);
    await WritingVaultMarkdown.renderMarkdown(note, '```mermaid\n' + source + '\n```');
    const svg = note.querySelector('.note-diagram > svg');
    if (!svg || note.querySelector('.note-render-error')) throw new Error(`${name} failed`);
    const labels = [...svg.querySelectorAll('text')].map(x=>x.textContent).join(' ');
    if (!labels.trim()) throw new Error(`${name} lost labels`);
    results.push({name, labels:labels.slice(0,120)});
  }
  if (document.querySelector('.note-diagram-staging')) throw new Error('Staging leaked');
  host.scrollIntoView();
  return {passed:true,diagrams:results};
};
