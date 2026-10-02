(async () => {
  const waitFor = async predicate => {
    for (let i = 0; i < 150; i++) { if (predicate()) return; await new Promise(resolve => setTimeout(resolve, 100)); }
    throw new Error('Timed out waiting for the Species viewer');
  };
  const tab = document.querySelector('[data-view="Species"]');
  if (tab?.querySelector('.nav-label')?.textContent !== 'Beastariry') throw new Error('Missing Beastariry tab');
  tab.click();
  await waitFor(() => [...document.querySelectorAll('#result-list strong')].some(node => node.textContent === 'Mountain dragon'));
  if (location.hash !== '#species') throw new Error('Wrong species route');
  [...document.querySelectorAll('#result-list strong')].find(node => node.textContent === 'Mountain dragon').closest('button,a').click();
  await waitFor(() => document.querySelector('#record-title')?.textContent === 'Mountain dragon');
  await waitFor(() => [...document.querySelectorAll('#record img')].some(img => img.complete && img.naturalWidth > 0));
  const text = document.querySelector('#record').textContent;
  for (const expected of ['Field notes', 'Mountain caves', 'First recorded sighting', 'Ember', 'Flying', 'Field guide', 'Mountain tales', 'Dragons have wings'])
    if (!text.includes(expected)) throw new Error(`Species page missing ${expected}`);
  const headers = { 'X-WritingVault-Session': sessionStorage.getItem('writing-vault-session'), 'X-WritingVault-Request': '1' };
  const page = await (await fetch('/api/search?kinds=Character&text=Ember', { headers })).json();
  const character = await (await fetch('/api/overview?reference=' + encodeURIComponent(page.items[0].ref), { headers })).json();
  if (character.fields.species?.label !== 'Mountain dragon' || character.fields.race !== 'Mountain') throw new Error('Character species/race fields missing');
  [...document.querySelectorAll('#record strong')].find(node => node.textContent === 'Ember').closest('button,a').click();
  await waitFor(() => document.querySelector('#record-title')?.textContent === 'Ember');
  const speciesLink = [...document.querySelectorAll('#record strong')].find(node => node.textContent === 'Mountain dragon');
  if (!speciesLink || !document.querySelector('#record').textContent.includes('Mountain')) throw new Error('Character species link is not rendered');
  speciesLink.closest('button,a').click();
  await waitFor(() => document.querySelector('#record-title')?.textContent === 'Mountain dragon');
  return { passed: true, tab: tab.querySelector('.nav-label').textContent,
    species: document.querySelector('#record-title').textContent, character: character.fields.species };
})()
