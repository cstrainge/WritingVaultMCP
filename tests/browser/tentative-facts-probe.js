(async () => {
  const waitFor = async predicate => {
    for (let i = 0; i < 150; i++) { if (predicate()) return; await new Promise(resolve => setTimeout(resolve, 100)); }
    throw new Error('Timed out waiting for tentative story facts');
  };
  await waitFor(() => document.querySelector('#continuity')?.value === 'Tentative books' && document.body.innerText.includes('CONTINUITY OVERVIEW'));
  document.querySelector('[data-view="Project"]').click();
  await waitFor(() => [...document.querySelectorAll('#result-list strong')].some(n => n.textContent === 'Provisional book'));
  [...document.querySelectorAll('#result-list strong')].find(n => n.textContent === 'Provisional book').closest('button,a').click();
  await waitFor(() => document.querySelector('#record-title')?.textContent === 'Provisional book');
  if (!document.querySelector('#record-main .fact-status-badge')?.textContent.includes('Tentative story ending')) throw new Error('Missing project certainty badge');
  document.querySelector('#record-timeline').click();
  await waitFor(() => [...document.querySelectorAll('#timeline-rows .fact-status-badge')].some(n => n.textContent === 'Tentative'));
  await waitFor(() => [...document.querySelectorAll('.timeline-card-title')].some(n => n.textContent.includes('Tentative')));
  const row = [...document.querySelectorAll('#timeline-rows tr')].find(n => n.textContent.includes('Guessed ending'));
  if (!row?.textContent.includes('Tentative') || !row.textContent.includes('2025')) throw new Error('Exact-date event lost tentative marker');
  return { passed: true, projectBadge: 'Tentative story ending', timelineBadge: 'Tentative', exactDatePreserved: true };
})()
