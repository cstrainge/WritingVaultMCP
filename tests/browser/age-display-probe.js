(() => {
  const pages = window.WritingVaultRecordPages({ state: {}, label: value => value });
  const check = (measure, expected) => {
    const host = document.createElement('div');
    pages.renderAge(host, { status: 'Alive', calendar: measure, legal: measure,
      biological: measure, experienced: measure, warnings: [], appliedEffects: [] });
    const actual = host.querySelector('.age-summary')?.textContent;
    if (actual !== expected) throw new Error(`Expected ${expected}, got ${actual}`);
  };
  check({ exactYears: 1 }, '1 year');
  check({ exactYears: 2 }, '2 years');
  check({ exactYears: 0, display: '1 month, 1 day' }, '1 month, 1 day');
  check({ exactYears: 0, display: '0 days' }, '0 days');
  check({ exactYears: 0, display: '1 day–1 month' }, '1 day–1 month');
  check({ minimumYears: 0.0027, maximumYears: 0.0055, display: '1–2 days' }, '1–2 days');
  return { passed: true, checks: 6 };
})()
