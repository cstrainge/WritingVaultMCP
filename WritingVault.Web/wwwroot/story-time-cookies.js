(() => {
  'use strict';
  const prefix = 'writing_vault_story_time_v1_';
  const maxAgeSeconds = 365 * 24 * 60 * 60;
  const instantPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/;
  const localPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?$/;
  const datePattern = /^\d{4}-\d{2}-\d{2}$/;

  async function cookieName(continuityName) {
    const bytes = new TextEncoder().encode(continuityName);
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
    return prefix + Array.from(digest.slice(0, 16), byte => byte.toString(16).padStart(2, '0')).join('');
  }

  function cookieValue(name) {
    const entry = document.cookie.split(';').map(part => part.trim()).find(part => part.startsWith(`${name}=`));
    return entry?.slice(name.length + 1) || null;
  }

  function valid(value) {
    const exact = value?.precision !== 'Date' && typeof value?.instant === 'string' &&
      instantPattern.test(value.instant) && Number.isFinite(Date.parse(value.instant)) &&
      typeof value.local === 'string' && localPattern.test(value.local);
    const date = value?.version === 2 && value.precision === 'Date' &&
      typeof value.local === 'string' && datePattern.test(value.local) &&
      Number.isFinite(Date.parse(`${value.local}T00:00:00Z`)) &&
      new Date(`${value.local}T00:00:00Z`).toISOString().slice(0, 10) === value.local;
    return [1, 2].includes(value?.version) && (exact || date) &&
      typeof value.zone === 'string' && value.zone.length > 0 && value.zone.length <= 128 &&
      ['Earlier', 'Later'].includes(value.ambiguous);
  }

  async function read(continuityName) {
    const name = await cookieName(continuityName);
    const raw = cookieValue(name);
    if (!raw) return null;
    try {
      const value = JSON.parse(decodeURIComponent(raw));
      if (valid(value)) return value;
    } catch { }
    document.cookie = `${name}=; Max-Age=0; Path=/; SameSite=Strict`;
    return null;
  }

  async function write(continuityName, value) {
    const saved = { version: 2, precision: value.precision === 'Date' ? 'Date' : 'Instant',
      instant: value.instant || null, local: value.local,
      zone: value.zone, ambiguous: value.ambiguous };
    if (!valid(saved)) return false;
    const name = await cookieName(continuityName);
    const encoded = encodeURIComponent(JSON.stringify(saved));
    document.cookie = `${name}=${encoded}; Max-Age=${maxAgeSeconds}; Path=/; SameSite=Strict`;
    return cookieValue(name) === encoded;
  }

  async function clear(continuityName) {
    const name = await cookieName(continuityName);
    document.cookie = `${name}=; Max-Age=0; Path=/; SameSite=Strict`;
    return cookieValue(name) === null;
  }

  window.WritingVaultStoryTimeCookies = Object.freeze({ read, write, clear });
})();
