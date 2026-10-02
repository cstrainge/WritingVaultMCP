(async () => {
  const headers = { 'X-WritingVault-Session': sessionStorage.getItem('writing-vault-session'),
    'X-WritingVault-Request': '1' };
  const session = await fetch('/api/session', { method: 'POST',
    headers: { ...headers, 'Content-Type': 'application/json' },
    body: JSON.stringify({ continuityName: 'Lostville', timeAction: 'Set',
      currentLocalDate: '2026-10-01', referenceTimeZoneId: 'America/Vancouver' }) });
  if (!session.ok) throw new Error(await session.text());
  const view = new window.WritingVaultTimeline(async (url, options) => {
    const response = await fetch(url, { ...options, headers });
    if (!response.ok) throw new Error(await response.text());
    return response.json();
  }, () => {});
  const dated = value => ({ occurred: { lower: value, upper: value } });
  const clock = { status: 'DateOnly', currentDate: '2026-10-01' };
  if (view.recurrenceTableEnd([dated('2024-09-23')], clock) !== '2026-10-01' ||
      view.recurrenceTableEnd([dated('2030-01-01')], clock) !== '2030-01-01' ||
      view.recurrenceTableEnd([], { status: 'Unset' }) !== null ||
      view.recurrenceTableEnd([{ occurred: { lower: '2024-01-01', upper: '2025-01-01', upperInclusive: false } }], {}) !== '2024-12-31')
    throw new Error('Incorrect recurrence horizon');
  await view.reload();
  const dates = () => view.tableItems.filter(item => item.title === 'Arrival Day').map(item => item.occurred.lower);
  const expected = ['2024-09-23', '2025-09-23', '2026-09-23'];
  if (JSON.stringify(dates()) !== JSON.stringify(expected)) throw new Error(`Missing Arrival Days: ${dates()}`);
  view.viewport = [Date.UTC(2024, 8, 1), Date.UTC(2024, 9, 1)];
  await view.reload();
  if (JSON.stringify(dates()) !== JSON.stringify(expected)) throw new Error('Graph zoom restricted the table');
  view.range = ['2025-01-01', '2025-12-31'];
  await view.reload();
  if (JSON.stringify(dates()) !== JSON.stringify(['2025-09-23'])) throw new Error('Explicit range was ignored');
  view.range = null; view.viewport = null;
  await view.reload();
  const rows = [...document.querySelectorAll('#timeline-rows tr')].filter(row => row.querySelector('.timeline-event')?.textContent === 'Arrival Day');
  if (rows.length !== 3) throw new Error('Repeated events are missing from the rendered table');
  view.dispose();
  return { passed: true, arrivalDays: dates(), renderedRows: rows.map(row => row.textContent) };
})()
