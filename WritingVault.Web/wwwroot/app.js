(() => {
  'use strict';
  const { safeUrl, renderMarkdown } = window.WritingVaultMarkdown;
  const storyTimeCookies = window.WritingVaultStoryTimeCookies;
  const $ = id => document.getElementById(id);
  const sections = ['loading', 'error', 'welcome', 'overview', 'results', 'images', 'image-detail', 'record', 'timeline'];
  const state = {
    sessionId: sessionStorage.getItem('writing-vault-session'), continuities: [],
    continuity: null, session: null, overview: null, cursor: null, searching: false,
    searchText: '', searchKind: null, searchTitle: null, searchDeletion: 'Active', searchCursor: null, searchRevision: null, searchLoad: 0,
    imageSearching: false, imageSearchText: '', imageSearchCursor: null, imageSearchRevision: null, imageSearchLoad: 0,
    imageDetailRef: null, imageDetailRevision: null, imageDetailLoad: 0, imageZoom: 0,
    continuityCursor: null, continuityRevision: null, continuityLoad: 0, overviewLoad: 0, recordRef: null, record: null, recordLoad: 0, recordSnapshotVersion: null, recordSnapshot: null, imageUrls: [], imageEpoch: 0,
    watchAbort: null, lastRefresh: null, generation: 0, watcherHealthy: false, timeline: false,
    savedStoryTime: null
  };
  if (!/^[a-f0-9]{32}$/.test(state.sessionId || '')) {
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    state.sessionId = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
    sessionStorage.setItem('writing-vault-session', state.sessionId);
  }
  const tabNonce = Array.from(crypto.getRandomValues(new Uint8Array(8)), value => value.toString(16).padStart(2, '0')).join('');
  const sessionChannel = typeof BroadcastChannel === 'function' ? new BroadcastChannel('writing-vault-browser-sessions') : null;
  let selectionQueue = Promise.resolve();
  let timelineView = null;
  function closeTimeline() { timelineView?.dispose(); timelineView = null; state.timeline = false; }
  async function openTimeline(navigate, focusRef = null, focusLabel = null) {
    if (!state.continuity) return;
    revokeImages(); state.searching = false; state.imageSearching = false; state.recordRef = null; state.record = null;
    state.searchLoad++; state.recordLoad++; state.timeline = true;
    if (navigate) history.pushState({}, '', `${pathFor(state.continuity)}#timeline`);
    document.querySelectorAll('[data-view]').forEach(item => item.classList.toggle('active', item.dataset.view === 'timeline'));
    if (!timelineView) {
      timelineView = new window.WritingVaultTimeline(api, reference => openRecord(reference, true));
      if (!navigate) timelineView.restore();
    }
    if (focusRef) timelineView.focus(focusRef, focusLabel || focusRef);
    document.title = `${state.continuity} timeline · Writer's Vault`;
    show('timeline'); await timelineView.reload();
  }

  async function claimIndependentSession() {
    if (!sessionChannel) return;
    let collision = false;
    sessionChannel.onmessage = event => {
      const message = event.data || {};
      if (message.token !== state.sessionId || message.nonce === tabNonce) return;
      if (message.type === 'probe') sessionChannel.postMessage({ type: 'claimed', token: state.sessionId, nonce: tabNonce, forNonce: message.nonce });
      if (message.type === 'claimed' && message.forNonce === tabNonce) collision = true;
    };
    sessionChannel.postMessage({ type: 'probe', token: state.sessionId, nonce: tabNonce });
    await new Promise(resolve => setTimeout(resolve, 120));
    if (collision) {
      const bytes = crypto.getRandomValues(new Uint8Array(16));
      state.sessionId = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
      sessionStorage.setItem('writing-vault-session', state.sessionId);
    }
  }

  const api = async (path, options = {}) => {
    const headers = new Headers(options.headers || {});
    headers.set('X-WritingVault-Session', state.sessionId);
    headers.set('X-WritingVault-Request', '1');
    if (options.body) headers.set('Content-Type', 'application/json');
    const response = await fetch(path, { ...options, headers, cache: 'no-store' });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.message || `Viewer request failed (${response.status}).`);
    return body;
  };
  const show = id => {
    if (id !== 'image-detail') { state.imageDetailRef = null; state.imageDetailLoad++; }
    sections.forEach(name => $(name).hidden = name !== id);
  };
  const setText = (id, value) => $(id).textContent = value || '';
  const clear = node => { while (node.firstChild) node.removeChild(node.firstChild); };
  const setTimeStorageWarning = message => {
    setText('time-storage-warning', message);
    $('time-storage-warning').hidden = !message;
  };
  const formatTime = value => value ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) : 'Never';
  const pathFor = name => `/c/${encodeURIComponent(name)}`;
  const recordPath = (reference, revision = null) =>
    `${pathFor(state.continuity)}?ref=${encodeURIComponent(reference)}${revision === null ? '' : `&v=${revision}`}`;
  const routeRecord = () => new URLSearchParams(location.search).get('ref');
  const routeRecordVersion = () => {
    const value = new URLSearchParams(location.search).get('v');
    if (value === null) return null;
    const parsed = Number(value);
    return /^[1-9]\d*$/.test(value) && Number.isSafeInteger(parsed) ? parsed : NaN;
  };
  const routeImage = () => new URLSearchParams(location.search).get('image');
  const routeImageRevision = () => new URLSearchParams(location.search).get('v');
  const selectedFromPath = () => {
    const match = location.pathname.match(/^\/c\/([^/]+)/);
    return match ? decodeURIComponent(match[1]) : null;
  };
  const routeView = () => ({
    '#characters': ['Character', 'Characters'], '#relationships': ['Relationship', 'Relationships'], '#locations': ['Location', 'Locations'],
    '#projects': ['Project', 'Projects'], '#events': ['WorldEvent', 'Events'],
    '#organizations': ['Organization', 'Organizations'], '#objects': ['Object', 'Objects'],
    '#sources': ['Source', 'Sources'], '#tags': ['Tag', 'Tags'],
    '#deleted': [null, 'Deleted records', 'Deleted']
  }[location.hash.toLowerCase()]);
  const allKinds = ['Project', 'Location', 'Character', 'Organization', 'Object', 'WorldEvent',
    'Source', 'SourceSnapshot', 'Claim', 'Tag', 'Note', 'Image', 'VariantGroup', 'Relationship',
    'RelationshipType', 'Residence', 'Membership', 'OrganizationLocation', 'Ownership',
    'OwnershipPrincipal', 'Custody', 'ObjectLocation', 'EntityEvent', 'TemporalEffect'];

  function setFreshness(kind, detail) {
    const live = $('live-state');
    live.className = `live-state ${kind === 'live' ? 'live' : 'paused'}`;
    live.querySelector('strong').textContent = kind === 'live' ? 'Live' : kind === 'updating' ? 'Updating' : kind === 'idle' ? 'Select a continuity' : 'Updates paused';
    const stale = $('stale-banner');
    stale.hidden = kind !== 'stale';
    if (kind === 'stale') setText('stale-detail', `${detail || 'Trying to reconnect…'} Last refreshed ${formatTime(state.lastRefresh)}.`);
  }

  function card(title, detail, eyebrow, action) {
    const button = document.createElement('button');
    button.type = 'button'; button.className = 'vault-card';
    const meta = document.createElement('span'); meta.className = 'card-count'; meta.textContent = eyebrow;
    const heading = document.createElement('h3'); heading.textContent = title;
    const copy = document.createElement('p'); copy.textContent = detail || 'Open this part of the continuity.';
    button.append(meta, heading, copy); button.addEventListener('click', action);
    return button;
  }

  function renderContinuities(items, append = false) {
    state.continuities = append ? state.continuities.concat(items) : items;
    const select = $('continuity');
    clear(select);
    const placeholder = document.createElement('option'); placeholder.value = ''; placeholder.textContent = 'Choose a continuity…'; select.append(placeholder);
    const cards = $('continuity-cards'); clear(cards);
    state.continuities.forEach(item => {
      const option = document.createElement('option'); option.value = item.name; option.textContent = item.name; select.append(option);
      cards.append(card(item.name, item.description || 'A continuity in your Writer’s Vault.', item.clock?.status === 'Set' ? 'Story time set' : 'Story time unset', () => selectContinuity(item.name, true)));
    });
    if (state.continuity && !state.continuities.some(item => item.name === state.continuity)) {
      const chosen = document.createElement('option'); chosen.value = state.continuity; chosen.textContent = state.continuity; select.append(chosen);
    }
    if (state.continuity) select.value = state.continuity;
    if (!state.continuities.length) cards.append(card('No continuities yet', 'Create one through Claude or ChatGPT, and it will appear here automatically.', 'Empty vault', () => {}));
  }

  async function loadMoreContinuities() {
    if (!state.continuityCursor) return;
    const load = ++state.continuityLoad;
    const page = await api(`/api/continuities?cursor=${encodeURIComponent(state.continuityCursor)}`);
    if (state.continuityLoad !== load) return;
    if (state.continuityRevision && page.observedRevision !== state.continuityRevision) {
      const fresh = await api('/api/continuities');
      renderContinuities(fresh.items); state.continuityRevision = fresh.observedRevision;
      state.continuityCursor = fresh.nextCursor;
    } else {
      renderContinuities(page.items, true); state.continuityCursor = page.nextCursor;
    }
    $('more-continuities').hidden = !state.continuityCursor;
  }

  async function refreshContinuities() {
    const load = ++state.continuityLoad;
    const first = await api('/api/continuities');
    if (state.continuityLoad !== load) return;
    if (first.observedRevision === state.continuityRevision) {
      if (!state.continuity) setFreshness('idle');
      return;
    }
    const loaded = Math.max(100, state.continuities.length);
    const items = [...first.items];
    let cursor = first.nextCursor;
    while (cursor && items.length < loaded) {
      const page = await api(`/api/continuities?cursor=${encodeURIComponent(cursor)}`);
      if (state.continuityLoad !== load) return;
      if (page.observedRevision !== first.observedRevision) return refreshContinuities();
      items.push(...page.items); cursor = page.nextCursor;
    }
    renderContinuities(items);
    state.continuityRevision = first.observedRevision; state.continuityCursor = cursor;
    $('more-continuities').hidden = !cursor;
    if (!state.continuity) setFreshness('idle');
  }

  function renderOverview(record, session) {
    state.imageSearching = false;
    const imageLoad = ++state.overviewLoad;
    revokeImages(); clear($('overview-gallery')); $('overview-image-section').hidden = true;
    state.overview = record; state.cursor = record.observedRevision; state.lastRefresh = new Date();
    setText('overview-title', record.summary?.label || session.continuityName);
    const description = record.fields?.description;
    setText('overview-description', typeof description === 'string' ? description : 'A connected view of the people, places, projects, events, and source material in this continuity.');
    const clock = session.clock;
    setText('clock', ['Set', 'DateOnly'].includes(clock?.status) ? `Story time · ${window.WritingVaultStoryDates.clockTime(clock)}` : 'Set story time');
    $('clock').disabled = false;
    setText('refresh-time', `Refreshed ${formatTime(state.lastRefresh)}`);
    const grid = $('section-grid'); clear(grid);
    const visibleEntities = record.sections?.entities?.items || [];
    const entitiesClipped = record.sections?.entities?.hasMore === true;
    const display = [
      ['characters', 'Characters', 'People and their relationships'], ['locations', 'Locations', 'Places and nested geography'],
      ['projects', 'Projects', 'Stories and their major events'], ['worldEvents', 'World events', 'Continuity-wide history'],
      ['organizations', 'Organizations', 'Groups, roles, and membership'], ['objects', 'Objects', 'Ownership, custody, and location']
    ];
    display.forEach(([key, label, detail]) => {
      const kind = key === 'worldEvents' ? 'WorldEvent' : key.slice(0, -1)[0].toUpperCase() + key.slice(1, -1);
      const count = visibleEntities.filter(item => item.kind === kind).length;
      grid.append(card(label, detail, `${count}${entitiesClipped ? '+' : ''} visible`, () => runSearch('', kind, label, false, 'Active')));
    });
    const notePage = record.sections?.notes || { items: [], nextCursor: null };
    const noteList = $('continuity-note-list'); clear(noteList);
    if (!notePage.items.length) { const empty = document.createElement('p'); empty.className = 'empty-section'; empty.textContent = 'No continuity notes yet.'; noteList.append(empty); }
    appendContinuityNotes(notePage.items, noteList, state.generation, session.continuityName);
    state.continuityNoteCursor = notePage.nextCursor; state.continuityNoteRevision = record.observedRevision;
    $('more-continuity-notes').hidden = !state.continuityNoteCursor;
    $('search').disabled = false; setText('scope-name', session.continuityName); show('overview');
    loadContinuityImages(record.summary.ref, imageLoad).catch(fail);
  }

  function appendContinuityNotes(items, host, generation, continuityName) {
    appendNoteCards(items, host, () => state.generation === generation &&
      state.continuity === continuityName && !state.recordRef);
  }

  async function loadMoreContinuityNotes() {
    const cursor = state.continuityNoteCursor;
    if (!cursor) return;
    const page = await api(`/api/related?${new URLSearchParams({ relation: 'notes', cursor })}`);
    if (page.observedRevision !== state.continuityNoteRevision) { await refreshVisible(page.observedRevision); return; }
    appendContinuityNotes(page.items, $('continuity-note-list'), state.generation, state.continuity);
    state.continuityNoteCursor = page.nextCursor; $('more-continuity-notes').hidden = !page.nextCursor;
  }

  async function selectSessionWithSavedTime(name) {
    const keep = () => api('/api/session', {
      method: 'POST', body: JSON.stringify({ continuityName: name, timeAction: 'Keep' })
    });
    let saved = null;
    try { saved = await storyTimeCookies.read(name); }
    catch { return { payload: await keep(), saved: null,
      warning: 'Browser cookies are unavailable, so story time cannot be restored automatically.' }; }
    if (!saved) return { payload: await keep(), saved: null, warning: '' };
    try {
      const request = { continuityName: name, timeAction: 'Set', referenceTimeZoneId: saved.zone };
      if (saved.precision === 'Date') request.currentLocalDate = saved.local;
      else request.currentTime = saved.instant;
      return { payload: await api('/api/session', { method: 'POST', body: JSON.stringify(request) }),
        saved, warning: '' };
    } catch {
      const payload = await keep();
      return { payload, saved,
        warning: 'The saved story time could not be restored right now. This tab is using the continuity clock; the cookie was kept for a later retry.' };
    }
  }

  async function selectContinuity(name, navigate) {
    if (!name) {
      state.generation++; state.watchAbort?.abort(); state.watcherHealthy = false;
      revokeImages(); closeTimeline();
      state.continuity = null; state.session = null; state.overview = null; state.cursor = null;
      state.savedStoryTime = null; setTimeStorageWarning('');
      state.searching = false; state.imageSearching = false; state.searchText = ''; state.searchKind = null; state.searchTitle = null; state.recordRef = null; state.record = null;
      $('search').value = ''; $('search').disabled = true; $('clock').disabled = true; setText('scope-name', 'No continuity selected');
      if (navigate) history.pushState({}, '', '/');
      setFreshness('idle'); $('stale-banner').hidden = true; show('welcome'); return;
    }
    const selectionGeneration = ++state.generation;
    state.searchLoad++; state.recordLoad++; state.recordRef = null; state.record = null;
    state.watchAbort?.abort(); state.watcherHealthy = false; revokeImages(); closeTimeline(); setFreshness('updating'); show('loading');
    // Selection requests share one browser session. Keep their server-side order aligned with the last user choice.
    const pending = selectionQueue.then(() => selectSessionWithSavedTime(name));
    selectionQueue = pending.catch(() => {});
    const { payload, saved, warning } = await pending;
    if (selectionGeneration !== state.generation) return;
    state.continuity = name; state.session = payload.session; state.savedStoryTime = saved;
    setTimeStorageWarning(warning);
    if (![...$('continuity').options].some(option => option.value === name)) {
      const selected = document.createElement('option'); selected.value = name; selected.textContent = name; $('continuity').append(selected);
    }
    $('continuity').value = name;
    state.recordRef = null; state.record = null;
    if (navigate) {
      state.searching = false; state.searchText = ''; state.searchKind = null; state.searchTitle = null; $('search').value = '';
      history.pushState({ continuity: name }, '', pathFor(name));
      document.querySelectorAll('[data-view]').forEach(item => item.classList.toggle('active', item.dataset.view === 'overview'));
    }
    renderOverview(payload.overview, payload.session); state.watcherHealthy = true;
    if (!navigate) {
      if (routeRecord()) await openRecord(routeRecord(), false, routeRecordVersion());
      else if (routeImage()) await openImageDetail(routeImage(), false, routeImageRevision());
      else if (location.hash.toLowerCase() === '#timeline') await openTimeline(false);
      else if (location.hash.toLowerCase() === '#images') await openImages(false);
      else if (location.hash) {
        const restored = routeView();
        if (restored) await runSearch('', restored[0], restored[1], false, restored[2] || 'Active');
      }
    }
    if (selectionGeneration === state.generation) startWatch(selectionGeneration, false);
  }

  async function runSearch(text, kind, displayTitle, append = false, deletionState = state.searchDeletion) {
    if (!state.continuity) return;
    closeTimeline();
    const load = ++state.searchLoad;
    const continuity = state.continuity;
    revokeImages();
    state.searching = true; state.imageSearching = false; state.recordRef = null; state.record = null; state.searchText = text || ''; state.searchKind = kind || null; state.searchTitle = displayTitle || null; state.searchDeletion = deletionState; setFreshness('updating');
    $('result-kind').value = kind || ''; $('result-deletion').value = deletionState;
    const query = new URLSearchParams(); if (text) query.set('text', text);
    query.set('kinds', kind || allKinds.join(','));
    if (text) query.set('includeContent', 'true');
    if (deletionState !== 'Active') query.set('deletionState', deletionState);
    if (append && state.searchCursor) query.set('cursor', state.searchCursor);
    const page = await api(`/api/search?${query}`);
    if (state.searchLoad !== load || state.continuity !== continuity || !state.searching) return;
    state.cursor = page.observedRevision; state.lastRefresh = new Date();
    if (append && state.searchRevision && page.observedRevision !== state.searchRevision) return runSearch(text, kind, displayTitle, false, deletionState);
    state.searchCursor = page.nextCursor; state.searchRevision = page.observedRevision;
    setText('results-title', text ? `Results for “${text}”` : displayTitle || kind || 'All records');
    document.querySelectorAll('[data-view]').forEach(item => item.classList.toggle('active',
      Boolean(kind) && item.dataset.view === kind || !kind && deletionState === 'Deleted' && item.dataset.view === 'deleted'));
    const list = $('result-list'); if (!append) clear(list);
    if (!page.items.length && !append) {
      const empty = document.createElement('div'); empty.className = 'vault-card';
      const h = document.createElement('h3'); h.textContent = 'Nothing matched';
      const p = document.createElement('p'); p.textContent = 'Try a broader phrase or another record type.'; empty.append(h, p); list.append(empty);
    }
    page.items.forEach((item, index) => {
      const row = referenceRow(item);
      if (!append && index === 0) row.id = 'first-result';
      list.append(row);
    });
    $('more-results').hidden = !state.searchCursor;
    show('results'); setFreshness(state.watcherHealthy ? 'live' : 'stale', 'The change watcher is reconnecting.');
  }

  async function openImages(navigate, append = false) {
    if (!state.continuity) return;
    closeTimeline();
    state.searchLoad++; state.recordLoad++;
    state.searching = false; state.imageSearching = true;
    state.recordRef = null; state.record = null;
    const load = ++state.imageSearchLoad;
    if (navigate) history.pushState({}, '', `${pathFor(state.continuity)}#images`);
    const text = append ? state.imageSearchText : $('image-search').value.trim();
    state.imageSearchText = text;
    if (!append) {
      revokeImages(); clear($('image-results'));
      state.imageSearchCursor = null; state.imageSearchRevision = null;
    }
    document.querySelectorAll('[data-view]').forEach(item => item.classList.toggle('active', item.dataset.view === 'images'));
    show('images'); setFreshness('updating');
    setText('image-search-status', 'Finding images…');
    const query = new URLSearchParams({ acrossContinuities: 'true' });
    if (text) query.set('text', text);
    if (append && state.imageSearchCursor) query.set('cursor', state.imageSearchCursor);
    const page = await api(`/api/image-search?${query}`);
    if (load !== state.imageSearchLoad || !state.imageSearching) return;
    if (append && state.imageSearchRevision && page.observedRevision !== state.imageSearchRevision)
      return openImages(false);
    state.cursor = page.observedRevision; state.lastRefresh = new Date();
    state.imageSearchRevision = page.observedRevision; state.imageSearchCursor = page.nextCursor;
    const result = $('image-results');
    if (!page.items.length && !append) {
      const empty = document.createElement('p'); empty.className = 'empty-section';
      empty.textContent = 'No images matched. Try another title, caption, or alt text.';
      result.append(empty);
    }
    const loads = [];
    for (const item of page.items) {
      const card = document.createElement('article'); card.className = 'vault-card image-result';
      const image = document.createElement('img'); image.alt = item.altText || item.title || 'Vault image';
      const imageLink = document.createElement('a'); imageLink.className = 'image-result-link';
      imageLink.href = `${pathFor(state.continuity)}?image=${encodeURIComponent(item.ref)}`;
      imageLink.setAttribute('aria-label', `View ${item.title || item.altText || 'image'} at full size`);
      imageLink.append(image);
      imageLink.addEventListener('click', event => { event.preventDefault(); openImageDetail(item.ref, true).catch(fail); });
      const body = document.createElement('div'); body.className = 'image-result-body';
      const title = document.createElement('h2'); title.textContent = item.title || item.caption || 'Untitled image';
      const owner = document.createElement('p');
      owner.textContent = `${item.owner?.label || 'Story record'} · ${item.owner?.continuityName || 'Vault'}`;
      const copy = document.createElement('button'); copy.type = 'button'; copy.className = 'quiet-button';
      copy.textContent = 'Copy Markdown';
      copy.addEventListener('click', async () => {
        const alt = (item.altText || item.title || 'Vault image').replace(/[\[\]\r\n]/g, ' ').trim();
        try { await navigator.clipboard.writeText(`![${alt}](vault-image:${item.ref})`); setText('image-search-status', 'Markdown image link copied.'); }
        catch { setText('image-search-status', 'Could not copy the Markdown link.'); }
      });
      body.append(title, owner, copy); card.append(imageLink, body); result.append(card);
      loads.push(imageBytes(item.ref).then(url => {
        if (load !== state.imageSearchLoad || !state.imageSearching) { URL.revokeObjectURL(url); return; }
        image.src = url; state.imageUrls.push(url);
      }).catch(() => { image.alt = 'Image preview unavailable'; }));
    }
    $('image-more').hidden = !page.nextCursor;
    setText('image-search-status', `${result.querySelectorAll('.image-result').length} image${result.querySelectorAll('.image-result').length === 1 ? '' : 's'} shown${page.hasMore ? '; more available' : ''}.`);
    setFreshness(state.watcherHealthy ? 'live' : 'stale', 'The change watcher is reconnecting.');
    await Promise.allSettled(loads);
  }

  function setImageZoom(factor) {
    const image = $('image-detail-picture');
    const width = Number(image.dataset.width);
    if (!width || !image.src) return;
    const fit = Math.min(width, Math.max(200, $('image-detail-stage').clientWidth - 32));
    state.imageZoom = factor;
    image.style.width = `${Math.round(factor === 0 ? fit : width * factor)}px`;
    setText('image-zoom-level', factor === 0 ? 'Fit' : `${Math.round(factor * 100)}%`);
  }

  async function openImageDetail(reference, navigate, selectedRevision = null) {
    if (!state.continuity || !reference) return;
    closeTimeline(); state.searchLoad++; state.recordLoad++; state.imageSearchLoad++;
    state.searching = false; state.imageSearching = false; state.recordRef = null; state.record = null;
    state.imageDetailRef = reference;
    state.imageDetailRevision = selectedRevision;
    const load = ++state.imageDetailLoad;
    revokeImages();
    $('image-detail-picture').removeAttribute('src');
    $('image-detail-picture').alt = '';
    $('image-version-banner').hidden = true;
    clear($('image-detail-fields'));
    clear($('image-revisions'));
    setText('image-detail-title', 'Loading image…');
    setText('image-detail-context', '');
    setText('image-detail-status', 'Loading the full-resolution image…');
    if (navigate) history.pushState({}, '', `${pathFor(state.continuity)}?image=${encodeURIComponent(reference)}${selectedRevision === null ? '' : `&v=${encodeURIComponent(selectedRevision)}`}`);
    document.querySelectorAll('[data-view]').forEach(item => item.classList.remove('active'));
    show('image-detail'); setFreshness('updating');
    const current = () => state.imageDetailRef === reference && state.imageDetailLoad === load &&
      state.imageDetailRevision === selectedRevision;
    try {
      const query = new URLSearchParams({ imageRef: reference });
      if (selectedRevision !== null) query.set('revision', String(selectedRevision));
      const result = await api(`/api/image-info?${query}`);
      const info = result.metadata;
      if (!current()) return;
      state.imageDetailContentRevision = result.contentRevision;
      state.cursor = result.observedRevision; state.lastRefresh = new Date();
      const pinned = selectedRevision !== null;
      $('image-version-banner').hidden = !pinned;
      setText('image-version-text', pinned ?
        `Pinned to image content revision ${result.contentRevision}${result.isCurrentContent ? ' (currently latest)' : ' · older image'}.` : '');
      $('image-latest').href = `${pathFor(state.continuity)}?image=${encodeURIComponent(reference)}`;
      setText('image-detail-title', info.title || info.altText || 'Untitled image');
      setText('image-detail-context', `${info.owner?.label || 'Story record'} · ${info.owner?.continuityName || 'Vault'}`);
      document.title = `${info.title || 'Image'} · Writer's Vault`;
      const fields = $('image-detail-fields');
      const addField = (name, value) => {
        if (value === null || value === undefined || value === '') return;
        const row = document.createElement('div'); const term = document.createElement('dt');
        const description = document.createElement('dd');
        term.textContent = name; description.textContent = String(value);
        row.append(term, description); fields.append(row);
      };
      addField('Caption', info.caption); addField('Alt text', info.altText);
      addField('Role', info.role); addField('Canon status', info.canonStatus);
      addField('Media type', result.mediaType);
      addField('Dimensions', `${result.width} × ${result.height} pixels`);
      addField('Original size', `${new Intl.NumberFormat().format(result.byteCount)} bytes`);
      addField('Content revision', result.contentRevision);
      addField('Metadata version', info.version);
      addField('Added', info.createdAtUtc ? formatTime(info.createdAtUtc) : null);
      addField('Last updated', info.updatedAtUtc ? formatTime(info.updatedAtUtc) : null);
      addField('Vault reference', info.ref);
      if (info.source?.ref) {
        const row = document.createElement('div'); const term = document.createElement('dt');
        const description = document.createElement('dd'); const source = document.createElement('button');
        term.textContent = 'Source'; source.type = 'button'; source.className = 'quiet-button';
        source.textContent = info.source.label || 'Open source';
        source.addEventListener('click', () => openRecord(info.source.ref, true).catch(fail));
        description.append(source); row.append(term, description); fields.append(row);
      }
      if (info.owner?.ref) {
        const row = document.createElement('div'); const term = document.createElement('dt');
        const description = document.createElement('dd'); const owner = document.createElement('button');
        term.textContent = 'Attached to'; owner.type = 'button'; owner.className = 'quiet-button';
        owner.textContent = info.owner.label || 'Open owner';
        owner.addEventListener('click', async () => {
          try {
            if (info.owner.continuityName && info.owner.continuityName !== state.continuity)
              await selectContinuity(info.owner.continuityName, true);
            await openRecord(info.owner.ref, true);
          } catch (error) { fail(error); }
        });
        description.append(owner); row.append(term, description); fields.append(row);
      }
      const loadRevisions = async beforeRevision => {
          const query = new URLSearchParams({ imageRef: reference });
          if (beforeRevision !== null) query.set('beforeRevision', String(beforeRevision));
          const history = await api(`/api/image-revisions?${query}`);
          if (!current()) return;
          const container = $('image-revisions');
          if (beforeRevision === null) clear(container);
          for (const entry of history.revisions) {
            const link = document.createElement('a');
            link.href = `${pathFor(state.continuity)}?image=${encodeURIComponent(reference)}&v=${entry.revision}`;
            link.textContent = `Revision ${entry.revision}${entry.isCurrent ? ' · latest' : ''} · ${entry.width} × ${entry.height}`;
            link.addEventListener('click', event => {
              event.preventDefault(); openImageDetail(reference, true, entry.revision).catch(fail);
            });
            container.append(link);
          }
          if (history.hasMore && history.nextBeforeRevision) {
            const more = document.createElement('button');
            more.type = 'button'; more.className = 'quiet-button';
            more.textContent = 'Older revisions';
            more.addEventListener('click', () => {
              more.remove(); loadRevisions(history.nextBeforeRevision)
                .catch(() => { if (current()) setText('image-revisions', 'Revision history unavailable.'); });
            });
            container.append(more);
          }
        };
      loadRevisions(null).catch(() => { if (current()) setText('image-revisions', 'Revision history unavailable.'); });
      const url = await imageBytes(reference, 'Original', selectedRevision);
      if (!current()) { URL.revokeObjectURL(url); return; }
      const image = $('image-detail-picture'); image.dataset.width = String(result.width);
      image.alt = info.altText || info.title || 'Vault image'; image.src = url;
      state.imageUrls.push(url);
      setImageZoom(0); setText('image-detail-status', 'Original image loaded. Use the controls to zoom, then scroll to pan.');
      setFreshness(state.watcherHealthy ? 'live' : 'stale', 'The change watcher is reconnecting.');
    } catch (error) {
      if (!current()) return;
      setText('image-detail-status', `Image unavailable: ${error.message}`);
      setFreshness(state.watcherHealthy ? 'live' : 'stale', 'The change watcher is reconnecting.');
    }
  }

  const label = value => String(value).replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[-_]/g, ' ');
  function displayValue(value) {
    if (value === null || value === undefined || value === '') return '—';
    if (typeof value === 'boolean') return value ? 'Yes' : 'No';
    if (Array.isArray(value)) return value.map(displayValue).join(', ');
    if (typeof value === 'object') return value.display || value.name || Object.entries(value).filter(([, part]) => part !== null && part !== undefined).map(([key, part]) => `${label(key)}: ${displayValue(part)}`).join(' · ');
    return String(value);
  }
  function referenceRow(item) {
    const row = document.createElement('button'); row.type = 'button'; row.className = 'result';
    const title = document.createElement('strong'); title.textContent = item.label || item.ref;
    const kind = document.createElement('small'); kind.textContent = label(item.kind);
    row.append(title, kind);
    if (item.context || item.associationRole || item.associationNotes) {
      const meta = document.createElement('p'); meta.className = 'association-note';
      meta.textContent = [item.context, item.associationRole && `Role: ${item.associationRole}`, item.associationNotes].filter(Boolean).join(' · ');
      row.append(meta);
    }
    row.addEventListener('click', () => openRecord(item.ref, true).catch(fail));
    return row;
  }
  function noteBodyWithoutRepeatedTitle(body, title) {
    const opening = body.match(/^(?:[ \t]*\r?\n)*(?:#{1,3}[ \t]+)?([^\r\n]+)(?:\r?\n|$)/);
    if (!opening || opening[1].replace(/[ \t]+#+[ \t]*$/, '').trim().toLowerCase() !== title.trim().toLowerCase())
      return body;
    return body.slice(opening[0].length);
  }
  function appendNoteCards(items, host, isCurrent) {
    for (const item of items) {
      if (item.kind !== 'Note') { host.append(referenceRow(item)); continue; }
      const card = document.createElement('article'); card.className = 'vault-card note-card';
      const heading = document.createElement('h3');
      const open = document.createElement('button'); open.type = 'button'; open.className = 'note-card-title';
      open.textContent = item.label || 'Untitled note';
      open.addEventListener('click', () => openRecord(item.ref, true).catch(fail));
      heading.append(open);
      const prose = document.createElement('div'); prose.className = 'record-prose';
      prose.textContent = 'Loading note…';
      card.append(heading, prose); host.append(card);
      (async () => {
        try {
          const note = await api(`/api/overview?${new URLSearchParams({ reference: item.ref, includeDeleted: 'true' })}`);
          if (!isCurrent()) return;
          clear(prose);
          const body = note.fields?.body;
          if (typeof body === 'string' && body.trim())
            renderMarkdown(prose, noteBodyWithoutRepeatedTitle(body, note.summary.label));
          else prose.textContent = 'No note text yet.';
        } catch {
          if (isCurrent()) prose.textContent = 'Preview unavailable. Open this note to read it.';
        }
      })();
    }
  }
  function revokeImages() { state.imageEpoch++; state.imageUrls.forEach(url => URL.revokeObjectURL(url)); state.imageUrls = []; }
  const { renderRecord, loadRecordHistory, loadRecordImages, loadContinuityImages, loadSourceText, imageBytes } =
    window.WritingVaultRecordPages({ state, $, api, label, clear, setText, show, setFreshness,
      renderMarkdown, safeUrl, displayValue, referenceRow, appendNoteCards, noteBodyWithoutRepeatedTitle,
      revokeImages, openRecord, openImageDetail, fail, formatTime });
  window.WritingVaultMarkdown.configure({ record: (link, reference, revision) => {
    const located = api(`/api/record-locate?${new URLSearchParams({ reference, includeDeleted: String(revision !== null) })}`).then(target => {
      const name = target.continuityName || state.continuity;
      link.href = target.kind === 'Continuity' && revision === null ? pathFor(name) :
        `${pathFor(name)}?ref=${encodeURIComponent(target.ref)}${revision === null ? '' : `&v=${revision}`}`;
      return target;
    }).catch(() => {
      link.removeAttribute('href'); link.classList.add('unavailable-link');
      link.setAttribute('aria-label', `${link.textContent} (record unavailable)`);
      return null;
    });
    link.href = '#';
    link.addEventListener('click', async event => {
      event.preventDefault();
      const target = await located;
      if (!target) return;
      try {
        if (target.kind === 'Continuity' && revision === null) {
          await selectContinuity(target.continuityName, true);
          return;
        }
        if (target.continuityName && target.continuityName !== state.continuity)
          await selectContinuity(target.continuityName, true);
        await openRecord(target.ref, true, revision);
      } catch (error) { fail(error); }
    });
  }, image: (link, image, reference, revision) => {
    link.href = `${pathFor(state.continuity)}?image=${encodeURIComponent(reference)}${revision === null ? '' : `&v=${revision}`}`;
    link.setAttribute('aria-label', `Open full-size image: ${image.alt}`);
    link.addEventListener('click', event => {
      event.preventDefault(); openImageDetail(reference, true, revision).catch(fail);
    });
    const epoch = state.imageEpoch;
    imageBytes(reference, 'Display', revision).then(url => {
      if (epoch !== state.imageEpoch) { URL.revokeObjectURL(url); return; }
      image.src = url; state.imageUrls.push(url);
    }).catch(() => {
      if (epoch === state.imageEpoch) image.replaceWith(document.createTextNode(`Image unavailable: ${image.alt}`));
    });
  } });
  async function openRecord(reference, navigate, snapshotVersion = null) {
    if (!state.continuity || !reference) return;
    if (snapshotVersion !== null && (!Number.isSafeInteger(snapshotVersion) || snapshotVersion < 1))
      throw new Error('That historical page version is invalid.');
    closeTimeline();
    state.searchLoad++;
    const load = ++state.recordLoad;
    state.searching = false; state.imageSearching = false; state.recordRef = reference;
    state.recordSnapshotVersion = snapshotVersion;
    setFreshness('updating'); $('main').classList.add('updating');
    if (navigate) history.pushState({}, '', recordPath(reference, snapshotVersion));
    try {
      const query = new URLSearchParams({ reference, includeDeleted: 'true' });
      const snapshot = snapshotVersion === null ? null : await api(`/api/record-snapshot?${new URLSearchParams({ reference, version: String(snapshotVersion) })}`);
      const record = snapshot ? snapshot.overview : await api(`/api/overview?${query}`);
      if (state.recordRef !== reference || state.recordLoad !== load || state.recordSnapshotVersion !== snapshotVersion) return;
      renderRecord(record, snapshot);
      $('record-version-banner').hidden = !snapshot;
      $('record-history').hidden = !!snapshot;
      if (snapshot) {
        setText('record-version-text', `${snapshot.isBaseline ? 'Migration baseline' : 'Historical version'} ${snapshot.snapshotVersion} · saved ${formatTime(snapshot.savedAtUtc)}. This is a saved page, including its notes and connections.`);
        $('record-latest').href = recordPath(snapshot.latestRef);
      }
      $('main').classList.remove('updating');
      if (!snapshot) await Promise.all([
        loadRecordHistory(reference, load),
        ['Character', 'Project', 'WorldEvent', 'Location', 'Organization', 'Object',
          'Relationship', 'EntityEvent', 'RelationshipEvent'].includes(record.summary.kind) ?
          loadRecordImages(reference, load, record.summary.isDeleted) : Promise.resolve(),
        record.summary.kind === 'SourceSnapshot' ? loadSourceText(reference, load, record.summary.isDeleted) : Promise.resolve()
      ]);
    } finally { if (state.recordLoad === load) $('main').classList.remove('updating'); }
  }

  function openTimeDialog() {
    if (!state.continuity) return;
    const clock = state.session?.clock;
    const continuity = state.continuities.find(item => item.name === state.continuity);
    $('story-zone').value = state.savedStoryTime?.zone || clock?.referenceTimeZoneId || continuity?.defaultTimeZoneId || '';
    const local = state.savedStoryTime?.local || '';
    $('story-date').value = local.slice(0, 10);
    $('story-has-time').checked = !!local.includes('T');
    $('story-time').value = local.includes('T') ? local.slice(11, 16) : '';
    toggleStoryTimeFields();
    $('ambiguous-time').value = state.savedStoryTime?.ambiguous || 'Earlier';
    $('time-error').hidden = true;
    $('time-dialog').showModal();
    $('story-date').focus();
  }

  function toggleStoryTimeFields() {
    const hasTime = $('story-has-time').checked;
    $('story-time-fields').hidden = !hasTime;
    $('ambiguous-time-fields').hidden = !hasTime;
    $('story-time').required = hasTime;
  }

  async function updateStoryTime(timeAction) {
    if (!state.continuity) return;
    if (timeAction === 'Set' && $('story-has-time').checked &&
        !/^(?:[01][0-9]|2[0-3]):[0-5][0-9]$/.test($('story-time').value)) {
      setText('time-error', 'Enter a 24-hour time as HH:MM, such as 14:30.');
      $('time-error').hidden = false;
      $('story-time').focus();
      return;
    }
    const wasSearching = state.searching;
    const watcherWasHealthy = state.watcherHealthy;
    state.generation++; state.watchAbort?.abort(); state.watcherHealthy = false; setFreshness('updating');
    const request = { continuityName: state.continuity, timeAction };
    if (timeAction === 'Set') {
      if ($('story-has-time').checked) {
        request.currentLocalTime = `${$('story-date').value}T${$('story-time').value}`;
      } else request.currentLocalDate = $('story-date').value;
      request.referenceTimeZoneId = $('story-zone').value.trim();
      request.ambiguousTime = $('ambiguous-time').value;
    }
    try {
      const payload = await api('/api/session', { method: 'POST', body: JSON.stringify(request) });
      state.session = payload.session; state.overview = payload.overview; state.cursor = payload.overview.observedRevision;
      let storageWarning = '';
      if (timeAction === 'Set') {
        const saved = { instant: payload.session.clock?.currentTime || null,
          local: request.currentLocalDate || request.currentLocalTime,
          precision: request.currentLocalDate ? 'Date' : 'Instant',
          zone: request.referenceTimeZoneId, ambiguous: request.ambiguousTime };
        state.savedStoryTime = saved;
        let persisted = false;
        try { persisted = await storyTimeCookies.write(state.continuity, saved); } catch { }
        if (!persisted) storageWarning = 'Story time is set for this tab, but the browser did not save its cookie. Allow cookies for 127.0.0.1 to keep it between visits.';
      } else if (timeAction === 'Clear') {
        state.savedStoryTime = null;
        let cleared = false;
        try { cleared = await storyTimeCookies.clear(state.continuity); } catch { }
        if (!cleared) storageWarning = 'Story time is cleared for this tab, but its saved cookie could not be removed.';
      }
      setTimeStorageWarning(storageWarning);
      $('time-dialog').close();
      if (state.timeline) await timelineView.reload();
      else if (state.recordRef) await openRecord(state.recordRef, false, state.recordSnapshotVersion);
      else if (state.imageDetailRef) await openImageDetail(state.imageDetailRef, false, state.imageDetailRevision);
      else if (state.imageSearching) await openImages(false);
      else if (wasSearching) await runSearch(state.searchText, state.searchKind, state.searchTitle, false, state.searchDeletion);
      else renderOverview(payload.overview, payload.session);
      state.watcherHealthy = true; startWatch(state.generation, false);
    } catch (error) {
      setText('time-error', error.message); $('time-error').hidden = false;
      state.watcherHealthy = watcherWasHealthy;
      startWatch(state.generation, true);
    }
  }

  async function refreshVisible(cursorHint) {
    const main = $('main'); main.classList.add('updating'); setFreshness('updating');
    try {
      if (state.timeline) await timelineView.reload();
      else if (state.recordRef) await openRecord(state.recordRef, false, state.recordSnapshotVersion);
      else if (state.imageDetailRef) await openImageDetail(state.imageDetailRef, false, state.imageDetailRevision);
      else if (state.imageSearching) await openImages(false);
      else if (state.searching) await runSearch(state.searchText, state.searchKind, state.searchTitle, false, state.searchDeletion);
      else {
        const record = await api('/api/overview');
        renderOverview(record, state.session);
      }
      state.cursor = cursorHint || state.cursor; setFreshness('live');
    } finally { main.classList.remove('updating'); }
  }

  async function startWatch(generation, needsCatchup) {
    state.watchAbort = new AbortController();
    setFreshness(needsCatchup ? 'updating' : 'live');
    while (generation === state.generation && state.continuity) {
      try {
        const requestedCursor = state.cursor;
        const query = requestedCursor ? `?cursor=${encodeURIComponent(requestedCursor)}` : '';
        const changed = await api(`/api/watch${query}`, { signal: state.watchAbort.signal });
        if (generation !== state.generation) return;
        if (requestedCursor !== state.cursor) continue;
        state.watcherHealthy = true;
        if (changed.cursorExpired || changed.changes?.length || needsCatchup) await refreshVisible(changed.cursor);
        else { state.cursor = changed.cursor; state.lastRefresh = new Date(); setFreshness('live'); }
        needsCatchup = false;
      } catch (error) {
        if (error.name === 'AbortError' || generation !== state.generation) return;
        state.watcherHealthy = false;
        setFreshness('stale', error.message);
        await new Promise(resolve => setTimeout(resolve, 2500));
        if (generation !== state.generation) return;
        try {
          const continuity = state.continuity;
          const pending = selectionQueue.then(() => selectSessionWithSavedTime(continuity));
          selectionQueue = pending.catch(() => {});
          const { payload: restored, saved, warning } = await pending;
          if (generation !== state.generation) return;
          state.savedStoryTime = saved; setTimeStorageWarning(warning);
          state.cursor = restored.overview.observedRevision; state.overview = restored.overview; state.session = restored.session; state.watcherHealthy = true;
          await refreshVisible(state.cursor); needsCatchup = false;
        } catch (reconnectError) { setFreshness('stale', reconnectError.message); }
      }
    }
  }

  async function initialize() {
    show('loading');
    try {
      const data = await api('/api/bootstrap');
      renderContinuities(data.continuities.items);
      state.continuityCursor = data.continuities.nextCursor; state.continuityRevision = data.continuities.observedRevision;
      $('more-continuities').hidden = !state.continuityCursor;
      const requested = selectedFromPath();
      if (requested) await selectContinuity(requested, false);
      else if (data.session.continuityName) await selectContinuity(data.session.continuityName, false);
      else { show('welcome'); setFreshness('idle'); $('stale-banner').hidden = true; }
    } catch (error) { setText('error-message', error.message); show('error'); setFreshness('stale', error.message); }
  }

  async function restoreRoute() {
    const name = selectedFromPath();
    if (!name) { await selectContinuity('', false); return; }
    if (name !== state.continuity) { await selectContinuity(name, false); return; }
    const record = routeRecord();
    const restored = routeView();
    if (record) await openRecord(record, false, routeRecordVersion());
    else if (routeImage()) await openImageDetail(routeImage(), false, routeImageRevision());
    else if (location.hash.toLowerCase() === '#timeline') await openTimeline(false);
    else if (location.hash.toLowerCase() === '#images') await openImages(false);
    else if (restored) await runSearch('', restored[0], restored[1], false, restored[2] || 'Active');
    else {
      closeTimeline(); state.searching = false; state.recordRef = null; state.searchText = ''; state.searchKind = null; state.searchTitle = null; $('search').value = '';
      document.querySelectorAll('[data-view]').forEach(item => item.classList.toggle('active', item.dataset.view === 'overview'));
      renderOverview(state.overview, state.session);
    }
  }

  $('continuity').addEventListener('change', event => selectContinuity(event.target.value, true).catch(fail));
  $('more-continuities').addEventListener('click', () => loadMoreContinuities().catch(fail));
  $('more-continuity-notes').addEventListener('click', () => loadMoreContinuityNotes().catch(fail));
  $('more-results').addEventListener('click', () => runSearch(state.searchText, state.searchKind, state.searchTitle, true, state.searchDeletion).catch(fail));
  $('result-kind').addEventListener('change', event => runSearch(state.searchText, event.target.value || null, null, false, state.searchDeletion).catch(fail));
  $('result-deletion').addEventListener('change', event => runSearch(state.searchText, state.searchKind, null, false, event.target.value).catch(fail));
  $('record-timeline').addEventListener('click', () => {
    if (state.recordRef && state.record) openTimeline(true, state.recordRef, state.record.summary.label).catch(fail);
  });
  const copyFeedbackTimers = new WeakMap();
  async function copyRecordAction(button, value, successMessage) {
    const status = $('record-action-status'); status.textContent = '';
    try {
      await navigator.clipboard.writeText(value);
      button.dataset.tooltip = successMessage; status.textContent = successMessage;
    } catch {
      button.dataset.tooltip = 'Copy failed'; status.textContent = 'Could not copy to the clipboard.';
    }
    clearTimeout(copyFeedbackTimers.get(button));
    copyFeedbackTimers.set(button, setTimeout(() => { button.dataset.tooltip = button.getAttribute('aria-label'); }, 2400));
  }
  $('copy-page-link').addEventListener('click', () => {
    if (state.recordRef) copyRecordAction($('copy-page-link'), new URL(recordPath(state.recordRef, state.recordSnapshotVersion), location.origin).href, 'Page link copied');
  });
  $('record-latest').addEventListener('click', event => {
    event.preventDefault();
    if (state.recordRef) openRecord(state.recordSnapshot?.latestRef || state.recordRef, true).catch(fail);
  });
  $('copy-vault-ref').addEventListener('click', () => {
    if (state.recordRef) copyRecordAction($('copy-vault-ref'), state.recordRef, 'Vault reference copied');
  });
  $('clock').addEventListener('click', openTimeDialog);
  $('timeline-clock').addEventListener('click', openTimeDialog);
  $('time-form').addEventListener('submit', event => { event.preventDefault(); updateStoryTime('Set'); });
  $('story-has-time').addEventListener('change', toggleStoryTimeFields);
  $('clear-time').addEventListener('click', () => updateStoryTime('Clear'));
  $('cancel-time').addEventListener('click', () => $('time-dialog').close());
  $('search-form').addEventListener('submit', event => { event.preventDefault(); history.pushState({}, '', `${pathFor(state.continuity)}#search`); runSearch($('search').value.trim()).catch(fail); });
  $('image-search-form').addEventListener('submit', event => { event.preventDefault(); openImages(false).catch(fail); });
  $('image-more').addEventListener('click', () => openImages(false, true).catch(fail));
  $('image-fit').addEventListener('click', () => setImageZoom(0));
  $('image-native').addEventListener('click', () => setImageZoom(1));
  $('image-zoom-in').addEventListener('click', () => setImageZoom(Math.min(4, (state.imageZoom || 1) * 1.25)));
  $('image-zoom-out').addEventListener('click', () => setImageZoom(Math.max(.25, (state.imageZoom || 1) / 1.25)));
  $('image-latest').addEventListener('click', event => {
    event.preventDefault();
    if (state.imageDetailRef) openImageDetail(state.imageDetailRef, true).catch(fail);
  });
  const copyImageMarkdown = async pinned => {
    if (!state.imageDetailRef) return;
    const image = $('image-detail-picture');
    const alt = (image.alt || 'Vault image').replace(/[\[\]\r\n]/g, ' ').trim();
    const revision = pinned ? state.imageDetailContentRevision : null;
    if (pinned && (!Number.isSafeInteger(revision) || revision < 1)) return;
    const markdown = `![${alt}](vault-image:${state.imageDetailRef}${pinned ? `?v=${revision}` : ''})`;
    try { await navigator.clipboard.writeText(markdown); setText('image-detail-status', 'Markdown image link copied.'); }
    catch { setText('image-detail-status', 'Could not copy the Markdown link.'); }
  };
  $('image-copy-current').addEventListener('click', () => copyImageMarkdown(false));
  $('image-copy-pinned').addEventListener('click', () => copyImageMarkdown(true));
  window.addEventListener('resize', () => { if (state.imageDetailRef && state.imageZoom === 0) setImageZoom(0); });
  $('clear-search').addEventListener('click', () => {
    closeTimeline();
    $('search').value = ''; state.searching = false; state.recordRef = null; state.searchText = ''; state.searchKind = null; state.searchTitle = null; state.searchDeletion = 'Active';
    history.pushState({}, '', pathFor(state.continuity));
    document.querySelectorAll('[data-view]').forEach(item => item.classList.toggle('active', item.dataset.view === 'overview'));
    renderOverview(state.overview, state.session);
  });
  $('retry').addEventListener('click', initialize);
  setInterval(() => refreshContinuities().catch(error => {
    if (!state.continuity) setFreshness('stale', error.message);
  }), 10000);
  $('menu').addEventListener('click', () => {
    const opened = document.body.classList.toggle('menu-open');
    $('menu').setAttribute('aria-expanded', String(opened));
  });
  document.querySelectorAll('[data-view]').forEach(link => link.addEventListener('click', event => {
    event.preventDefault();
    if (!state.continuity) { $('continuity').focus(); return; }
    document.querySelectorAll('[data-view]').forEach(item => item.classList.toggle('active', item === link));
    document.body.classList.remove('menu-open');
    const view = link.dataset.view;
    if (view === 'overview') {
      revokeImages(); closeTimeline();
      state.searching = false; state.imageSearching = false; state.recordRef = null; state.searchText = ''; state.searchKind = null; state.searchTitle = null; state.searchDeletion = 'Active'; $('search').value = ''; history.pushState({}, '', pathFor(state.continuity));
      renderOverview(state.overview, state.session);
    } else if (view === 'timeline') {
      openTimeline(true).catch(fail);
    } else if (view === 'images') {
      openImages(true).catch(fail);
    } else {
      const hash = view === 'WorldEvent' ? 'events' : view === 'deleted' ? 'deleted' : `${view.toLowerCase()}s`;
      history.pushState({}, '', `${pathFor(state.continuity)}#${hash}`);
      runSearch('', view === 'deleted' ? null : view, link.querySelector('.nav-label').textContent.trim(), false,
        view === 'deleted' ? 'Deleted' : 'Active').catch(fail);
    }
  }));
  document.addEventListener('keydown', event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); $('search').focus(); }
    if (event.key === 'Escape' && document.body.classList.contains('menu-open')) { document.body.classList.remove('menu-open'); $('menu').setAttribute('aria-expanded', 'false'); $('menu').focus(); }
  });
  window.addEventListener('popstate', () => restoreRoute().catch(fail));
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { state.watchAbort?.abort(); state.watcherHealthy = false; return; }
    if (state.continuity) startWatch(++state.generation, true);
  });
  const themes = ['system', 'light', 'dark']; let theme = localStorage.getItem('writing-vault-theme') || 'system';
  const applyTheme = () => { document.documentElement.dataset.theme = theme; $('theme').textContent = `Theme: ${theme[0].toUpperCase()}${theme.slice(1)}`; };
  $('theme').addEventListener('click', () => { theme = themes[(themes.indexOf(theme) + 1) % themes.length]; localStorage.setItem('writing-vault-theme', theme); applyTheme(); }); applyTheme();
  function fail(error) { setText('error-message', error.message); show('error'); setFreshness('stale', error.message); }
  claimIndependentSession().then(initialize).catch(fail);
})();
