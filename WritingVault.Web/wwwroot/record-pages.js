(() => {
  'use strict';
  window.WritingVaultRecordPages = context => {
    const { state, $, api, label, clear, setText, show, setFreshness, renderMarkdown, safeUrl,
      displayValue, referenceRow, appendNoteCards, noteBodyWithoutRepeatedTitle,
      revokeImages, openRecord, openImageDetail, fail, formatTime } = context;
    function ageMeasure(measure) {
      if (!measure) return 'Unavailable';
      if (measure.display) return measure.display;
      const number = value => new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(value);
      if (Number.isFinite(measure.exactYears)) {
        const unit = new Intl.PluralRules('en', { maximumFractionDigits: 2 }).select(measure.exactYears) === 'one' ? 'year' : 'years';
        return `${number(measure.exactYears)} ${unit}`;
      }
      if (Number.isFinite(measure.minimumYears) && Number.isFinite(measure.maximumYears))
        return `${number(measure.minimumYears)}–${number(measure.maximumYears)} years`;
      if (measure.status === 'BirthDateUnknown') return 'Birth date unknown';
      if (measure.status === 'TimelineUnset') return 'Story time is unset';
      return label(measure.status || 'Unavailable');
    }
    function renderAge(host, age) {
      const section = document.createElement('section'); section.className = 'record-section age-section';
      const heading = document.createElement('h2'); heading.textContent = 'Age'; section.append(heading);
      if (age.status === 'TimelineUnset') {
        const message = document.createElement('p'); message.className = 'age-context';
        message.textContent = 'Story time is unset. Set a time for this tab or the continuity clock to calculate age.';
        section.append(message);
      } else {
        const groups = new Map();
        for (const [name, value] of [['Calendar', age.calendar], ['Legal', age.legal],
          ['Biological', age.biological], ['Experienced', age.experienced]]) {
          const displayed = ageMeasure(value);
          if (!groups.has(displayed)) groups.set(displayed, []);
          groups.get(displayed).push(name);
        }
        const allUnknown = groups.size === 1 && groups.has('Birth date unknown');
        const oneAge = groups.size === 1;
        const statusOnly = oneAge && ['NotYetBorn', 'InvalidChronology'].includes(age.status);
        if ((age.asOf || age.asOfDate) && !allUnknown) {
          const asOf = document.createElement('p'); asOf.className = 'age-context';
          try {
            const clock = state?.session?.clock;
            if (age.asOfDate && clock?.status === 'DateOnly')
              asOf.textContent = `During ${window.WritingVaultStoryDates.clockTime(clock)}`;
            else if (clock?.source === 'session' && clock.currentTime &&
                clock.referenceTimeZoneId === age.referenceTimeZoneId &&
                new Date(clock.currentTime).getTime() === new Date(age.asOf).getTime())
              asOf.textContent = `As of ${window.WritingVaultStoryDates.clockTime(clock)}`;
            else asOf.textContent = `As of ${new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short',
              timeZone: age.referenceTimeZoneId || 'UTC' }).format(new Date(age.asOf))} (${age.referenceTimeZoneId || 'UTC'})`;
          } catch { asOf.textContent = `As of ${age.asOf}`; }
          section.append(asOf);
        }
        const statusNotes = {
          Deceased: 'Age is measured at death.',
          PossiblyDeceased: 'The death date is uncertain; age may be measured at death.',
          NotYetBorn: 'This character is not yet born at the selected story time.',
          PossiblyNotYetBorn: 'This character may not yet be born at the selected story time.',
          InvalidChronology: 'Birth and death dates do not form a valid chronology.'
        };
        if (statusNotes[age.status] && !allUnknown && !statusOnly) {
          const note = document.createElement('p'); note.className = 'age-context';
          note.textContent = statusNotes[age.status]; section.append(note);
        }
        if (allUnknown) {
          const message = document.createElement('p'); message.className = 'age-unavailable';
          message.textContent = 'Birth date unknown. Age cannot be calculated.';
          section.append(message);
        } else if (oneAge) {
          const summary = document.createElement('p'); summary.className = 'age-summary';
          summary.textContent = statusOnly ? statusNotes[age.status] : groups.keys().next().value;
          section.append(summary);
        } else {
          const measures = document.createElement('dl'); measures.className = 'age-measures';
          for (const [displayed, names] of groups) {
            const item = document.createElement('div');
            const term = document.createElement('dt'); term.textContent = names.join(', ');
            const detail = document.createElement('dd'); detail.textContent = displayed;
            item.append(term, detail); measures.append(item);
          }
          section.append(measures);
        }
        if (age.appliedEffects?.length) {
          const effects = document.createElement('div'); effects.className = 'age-effects';
          const title = document.createElement('h3'); title.textContent = 'Aging effects'; effects.append(title);
          const list = document.createElement('div'); list.className = 'result-list';
          age.appliedEffects.forEach(effect => list.append(referenceRow(effect)));
          effects.append(list); section.append(effects);
        }
        const routineWarnings = new Set([
          'The birth date is unknown; age cannot be calculated.',
          'Temporal aging is disabled; biological and experienced age equal calendar age.'
        ]);
        const visibleWarnings = (age.warnings || []).filter(warning => !routineWarnings.has(warning));
        if (visibleWarnings.length) {
          const list = document.createElement('ul'); list.className = 'age-warnings';
          visibleWarnings.forEach(warning => { const item = document.createElement('li'); item.textContent = warning; list.append(item); });
          section.append(list);
        }
      }
      host.append(section);
    }
    function renderTemporalProfile(host, profile) {
      if (!profile?.exists || profile.isDeleted) return;
      const section = document.createElement('section'); section.className = 'record-section';
      const heading = document.createElement('h2'); heading.textContent = 'Temporal profile';
      const status = document.createElement('p');
      status.textContent = profile.enabled ? 'Custom aging enabled. Legal age follows calendar age.' :
        'Custom aging disabled. All age measures follow calendar age.';
      section.append(heading, status);
      if (profile.notes?.trim()) {
        const notes = document.createElement('div'); notes.className = 'record-prose';
        renderMarkdown(notes, profile.notes); section.append(notes);
      }
      host.append(section);
    }
    function renderCurrentTemporalState(host, temporalState) {
      if (temporalState?.status === 'DateOnly') {
        const section = document.createElement('section'); section.className = 'record-section';
        const heading = document.createElement('h2'); heading.textContent = 'Current state';
        const note = document.createElement('p'); note.className = 'age-context';
        note.textContent = 'This state may change during the selected story day. Set a clock time to see an exact state.';
        section.append(heading, note); host.append(section);
        return;
      }
      if (temporalState?.status !== 'Set') return;
      const active = Object.entries(temporalState.activeRecords || {}).filter(([, rows]) =>
        Array.isArray(rows) && rows.length);
      if (!active.length) return;
      const section = document.createElement('section'); section.className = 'record-section';
      const heading = document.createElement('h2'); heading.textContent = 'Current state'; section.append(heading);
      const list = document.createElement('ul'); list.className = 'current-state-list';
      for (const [category, rows] of active) {
        const item = document.createElement('li');
        const title = document.createElement('strong'); title.textContent = label(category);
        const detail = document.createElement('span');
        const labels = rows.map(row => row.label || row.Label || row.name || row.Name || row.title || row.Title).filter(value =>
          typeof value === 'string' && value.trim()).slice(0, 3);
        detail.textContent = `${rows.length} active${labels.length ? ` · ${labels.join(', ')}${rows.length > labels.length ? ', …' : ''}` : ''}`;
        item.append(title, detail); list.append(item);
      }
      section.append(list); host.append(section);
    }
    const storyDateParts = ['Kind', 'LowerBound', 'UpperBound', 'LowerInclusive', 'UpperInclusive',
      'OriginalText', 'CalendarId'];
    const storyDateKeys = new Set(['birth', 'death', 'event', 'period'].flatMap(prefix =>
      storyDateParts.map(part => `${prefix}${part}`)));
    function isUnsetDetail(value) {
      if (value === null || value === undefined) return true;
      if (typeof value === 'string')
        return /^(?:unknown|unset|not set|unspecified|n\/a|—)?$/i.test(value.trim());
      if (Array.isArray(value)) return value.length === 0 || value.every(isUnsetDetail);
      if (typeof value === 'object') {
        const preferred = value.display || value.name;
        if (preferred && isUnsetDetail(preferred)) return true;
        return Object.values(value).every(isUnsetDetail);
      }
      return false;
    }
    function appendStoryDate(host, date) {
      host.textContent = date.description;
      if (date.duration) {
        const duration = document.createElement('small'); duration.className = 'story-date-duration';
        duration.textContent = date.duration; host.append(duration);
      }
      if (date.context) {
        const context = document.createElement('small'); context.className = 'story-date-context';
        context.textContent = date.context; host.append(context);
      }
    }
    const namePart = value => typeof value === 'string' ? value.trim().replace(/\s+/g, ' ') : '';
    function characterFullName(fields, fallback) {
      let name = namePart(fields?.givenName);
      const family = namePart(fields?.familyName);
      if (family && (name.toLocaleLowerCase() === family.toLocaleLowerCase() ||
          name.toLocaleLowerCase().endsWith(` ${family.toLocaleLowerCase()}`)))
        name = name.slice(0, -family.length).trimEnd();
      for (const part of [namePart(fields?.middleNames), family]) {
        const current = name.toLocaleLowerCase(), ending = part.toLocaleLowerCase();
        if (part && current !== ending && !current.endsWith(` ${ending}`))
          name = name ? `${name} ${part}` : part;
      }
      return name || fallback;
    }
    function renderCharacterHeader(record) {
      const character = record.summary.kind === 'Character';
      const fullName = character ? characterFullName(record.fields, record.summary.label) : record.summary.label;
      setText('record-title', fullName);
      document.title = `${fullName} · Writer's Vault`;
      const preferred = character ? namePart(record.fields?.preferredName) : '';
      setText('record-preferred-name', preferred);
      $('record-preferred').hidden = !preferred;
      const aka = $('record-aka'), list = $('record-aka-list'), status = $('record-aka-status');
      clear(list); status.hidden = true; status.textContent = ''; aka.hidden = true;
      if (!character) return;
      const aliases = record.sections?.aliases;
      if (!aliases) return;
      const seenNames = new Set();
      const appendAliases = items => {
        for (const item of items) {
          const name = namePart(item.label);
          if (!name || item.isDeleted || seenNames.has(name.toLocaleLowerCase())) continue;
          seenNames.add(name.toLocaleLowerCase());
          const entry = document.createElement('li'); entry.textContent = name; list.append(entry);
        }
        aka.hidden = !list.childElementCount;
      };
      appendAliases(aliases.items || []);
      if (aliases.hasMore && !aliases.nextCursor) {
        status.textContent = 'Some aliases could not be loaded.';
        status.hidden = false; aka.hidden = false;
      } else if (aliases.hasMore) {
        const load = state.recordLoad;
        loadRemainingCharacterAliases(record, aliases.nextCursor, appendAliases, status).catch(error => {
          if (state.recordRef !== record.summary.ref || state.recordLoad !== load) return;
          status.textContent = `More aliases could not be loaded: ${error.message}`;
          status.hidden = false; aka.hidden = false;
        });
      }
    }
    async function loadRemainingCharacterAliases(record, initialCursor, appendAliases, status) {
      const reference = record.summary.ref, load = state.recordLoad;
      const seenCursors = new Set(); let cursor = initialCursor;
      while (cursor && state.recordRef === reference && state.recordLoad === load) {
        if (seenCursors.has(cursor)) throw new Error('The alias page cursor repeated.');
        seenCursors.add(cursor);
        const query = new URLSearchParams({ reference, relation: 'aliases', cursor, deletionState: 'All' });
        const page = await api(`/api/related?${query}`);
        if (state.recordRef !== reference || state.recordLoad !== load) return;
        if (page.observedRevision !== record.observedRevision) { await openRecord(reference, false); return; }
        if (page.hasMore && !page.nextCursor) throw new Error('An alias page did not include its next cursor.');
        appendAliases(page.items || []);
        cursor = page.nextCursor;
      }
      if (state.recordRef === reference && state.recordLoad === load) status.hidden = true;
    }
    function renderRecord(record, snapshot = null) {
      state.record = record; state.recordSnapshot = snapshot;
      if (!snapshot) state.cursor = record.observedRevision;
      state.lastRefresh = new Date(); revokeImages();
      const summary = record.summary;
      setText('record-kind', label(summary.kind)); renderCharacterHeader(record);
      $('record-timeline').hidden = !!snapshot || !['Character', 'Project', 'WorldEvent', 'Location', 'Organization', 'Object', 'Species'].includes(summary.kind);
      setText('record-context', summary.context || ''); $('record-deleted').hidden = !summary.isDeleted;
      const main = $('record-main'); clear(main);
      const fields = $('record-fields'); clear(fields);
      const proseKeys = new Set(['description', 'body', 'content', 'claimText', 'notes', 'impact', 'outcome', 'caption']);
      if (summary.kind === 'Character' && record.fields?.age) renderAge(main, record.fields.age);
      renderCurrentTemporalState(main, record.fields?.currentTemporalState);
      if (summary.kind === 'Character') renderTemporalProfile(main, record.fields?.temporalProfile);
      const hasMembershipTransitions = summary.kind === 'RelationshipMembershipPeriod' &&
        (record.fields?.joined || record.fields?.left);
      if (hasMembershipTransitions) {
        for (const [key, title] of [['joined', 'Joined'], ['left', 'Left']]) {
          const value = record.fields[key];
          if (!value) continue;
          const date = window.WritingVaultStoryDates.describe(value);
          if (date.description === 'Date unknown') continue;
          const term = document.createElement('dt'); term.textContent = title;
          const detail = document.createElement('dd'); appendStoryDate(detail, date);
          fields.append(term, detail);
        }
      }
      for (const [key, value] of Object.entries(record.fields || {})) {
        if (['continuityName', 'version', 'createdAtUtc', 'updatedAtUtc',
          'deletedAtUtc', 'deletedOperationId'].includes(key) || /Reference$/.test(key) ||
            key.toLowerCase() === 'isdeleted') continue;
        if (summary.kind === 'Note' && key === 'title') continue;
        if (key === 'age' || key === 'temporalProfile' || key === 'currentTemporalState' ||
            summary.kind === 'Character' && key === 'preferredName') continue;
        if (hasMembershipTransitions && storyDateKeys.has(key) && key.startsWith('period')) continue;
        if (hasMembershipTransitions && (key === 'joined' || key === 'left')) continue;
        if (storyDateKeys.has(key)) {
          if (!key.endsWith('Kind') || isUnsetDetail(value)) continue;
          const prefix = key.slice(0, -4);
          const date = window.WritingVaultStoryDates.fromFields(record.fields, prefix);
          if (date.description === 'Date unknown') continue;
          const term = document.createElement('dt'); term.textContent = label(prefix);
          const detail = document.createElement('dd');
          appendStoryDate(detail, date);
          fields.append(term, detail); continue;
        }
        if (['storyRange', 'storyBegins', 'storyEnds'].includes(key)) {
          if (value) {
            const term = document.createElement('dt'); term.textContent = label(key);
            const detail = document.createElement('dd'); detail.textContent = value.display;
            fields.append(term, detail);
          }
          continue;
        }
        if (isUnsetDetail(value)) continue;
        if (proseKeys.has(key) && typeof value === 'string' && value.trim()) {
          if (summary.kind === 'Note' && key === 'body') {
            const article = document.createElement('article'); article.className = 'vault-card note-card note-page-body';
            const prose = document.createElement('div'); prose.className = 'record-prose';
            renderMarkdown(prose, noteBodyWithoutRepeatedTitle(value, summary.label));
            article.append(prose); main.append(article); continue;
          }
          const section = document.createElement('section'); section.className = 'record-section';
          const heading = document.createElement('h2'); heading.textContent = label(key);
          const prose = document.createElement('div'); prose.className = 'record-prose'; renderMarkdown(prose, value);
          section.append(heading, prose); main.append(section); continue;
        }
        const term = document.createElement('dt'); term.textContent = label(key);
        const detail = document.createElement('dd');
        const href = /url$/i.test(key) && typeof value === 'string' ? safeUrl(value) : null;
        if (key === 'species' && value?.ref) detail.append(referenceRow(value));
        else if (href) { const link = document.createElement('a'); link.href = href; link.target = '_blank'; link.rel = 'noopener noreferrer'; link.textContent = value; detail.append(link); }
        else if (/AtUtc$/.test(key) && typeof value === 'string')
          detail.textContent = window.WritingVaultStoryDates.utcTimestamp(value);
        else detail.textContent = displayValue(value);
        fields.append(term, detail);
      }
      fields.closest('.record-details').hidden = !fields.childElementCount;
      const sectionHost = $('record-sections'); clear(sectionHost);
      for (const [relation, section] of Object.entries(record.sections || {})) {
        if (summary.kind === 'Character' && relation.toLowerCase() === 'aliases') continue;
        if (section.items.length || section.hasMore) renderRecordSection(sectionHost, relation, section);
      }
      if (!sectionHost.childElementCount) { const empty = document.createElement('p'); empty.className = 'empty-section'; empty.textContent = 'No connected records yet.'; sectionHost.append(empty); }
      $('record-image-section').hidden = true; clear($('record-gallery'));
      clear($('record-history'));
      show('record'); setFreshness(state.watcherHealthy ? 'live' : 'stale', 'The change watcher is reconnecting.');
    }
    function renderRecordSection(host, relation, page) {
      const block = document.createElement('section'); block.className = 'record-section';
      const heading = document.createElement('h2'); heading.textContent = label(relation); block.append(heading);
      let appendItems;
      if (relation.toLowerCase() === 'notes') {
        const reference = state.recordRef, load = state.recordLoad;
        const documentBox = document.createElement('article'); documentBox.className = 'vault-card note-document';
        const prose = document.createElement('div'); prose.className = 'record-prose';
        documentBox.append(prose); block.append(documentBox);
        appendItems = createNoteDocument(prose, () => state.recordRef === reference && state.recordLoad === load);
      } else {
        const list = document.createElement('div'); list.className = 'result-list'; block.append(list);
        appendItems = items => items.forEach(item => list.append(referenceRow(item)));
      }
      if (!page.items.length) {
        const empty = document.createElement('p'); empty.className = 'empty-section'; empty.textContent = 'No records yet.';
        block.append(empty);
      } else appendItems(page.items);
      if (relation.toLowerCase() === 'notes' && page.hasMore && page.nextCursor) {
        const progress = document.createElement('p'); progress.className = 'note-document-progress';
        progress.textContent = 'Loading remaining notes…'; block.append(progress);
        loadRemainingNotes(relation, appendItems, page.nextCursor, progress);
      } else if (page.hasMore && page.nextCursor) {
        const more = document.createElement('button'); more.type = 'button'; more.className = 'quiet-button'; more.textContent = `Load more ${label(relation).toLowerCase()}`;
        more.dataset.cursor = page.nextCursor;
        more.addEventListener('click', () => loadMoreRelated(relation, appendItems, more).catch(fail)); block.append(more);
      }
      host.append(block);
    }
    function createNoteDocument(host, isCurrent) {
      const pending = [];
      let active = 0;
      const pump = () => {
        while (active < 4 && pending.length && isCurrent()) {
          const { item, body } = pending.shift();
          active++;
          (async () => {
            try {
              const note = await api(`/api/overview?${new URLSearchParams({ reference: item.ref, includeDeleted: 'true' })}`);
              if (!isCurrent()) return;
              clear(body);
              const markdown = note.fields?.body;
              if (typeof markdown === 'string' && markdown.trim())
                renderMarkdown(body, noteBodyWithoutRepeatedTitle(markdown, item.label || 'Untitled note'));
            } catch {
              if (isCurrent()) body.textContent = 'Note text unavailable. Open the note to try again.';
            } finally {
              active--;
              pump();
            }
          })();
        }
      };
      return items => {
        for (const item of items) {
          if (host.childElementCount) {
            const divider = document.createElement('hr'); divider.className = 'note-document-divider'; host.append(divider);
          }
          const heading = document.createElement('h3');
          if (state.recordSnapshot) heading.textContent = item.label || 'Untitled note';
          else {
            const open = document.createElement('button'); open.type = 'button'; open.className = 'note-card-title';
            open.textContent = item.label || 'Untitled note';
            open.addEventListener('click', () => openRecord(item.ref, true).catch(fail)); heading.append(open);
          }
          const body = document.createElement('div'); body.className = 'note-document-body';
          body.textContent = 'Loading note…'; host.append(heading, body);
          if (state.recordSnapshot) {
            const saved = state.recordSnapshot.notes?.[item.ref];
            const markdown = saved?.fields?.body;
            clear(body);
            if (typeof markdown === 'string' && markdown.trim())
              renderMarkdown(body, noteBodyWithoutRepeatedTitle(markdown, item.label || 'Untitled note'));
            else body.textContent = 'Saved note text is unavailable.';
          } else if (item.kind === 'Note') pending.push({ item, body });
          else body.textContent = 'Open this record to view it.';
        }
        pump();
      };
    }
    async function loadRemainingNotes(relation, appendItems, initialCursor, progress) {
      const reference = state.recordRef, load = state.recordLoad;
      const seen = new Set(); let cursor = initialCursor;
      try {
        while (cursor && state.recordRef === reference && state.recordLoad === load) {
          if (seen.has(cursor)) throw new Error('The note page cursor repeated.');
          seen.add(cursor);
          const query = new URLSearchParams({ reference, relation, cursor, deletionState: 'All' });
          const page = await api(`/api/related?${query}`);
          if (state.recordRef !== reference || state.recordLoad !== load) return;
          if (page.observedRevision !== state.record.observedRevision) { await openRecord(reference, false); return; }
          if (page.hasMore && !page.nextCursor) throw new Error('A note page did not include its next cursor.');
          appendItems(page.items);
          cursor = page.nextCursor;
        }
        if (state.recordRef === reference && state.recordLoad === load) progress.remove();
      } catch (error) {
        if (state.recordRef !== reference || state.recordLoad !== load) return;
        progress.textContent = `Some notes could not be loaded: ${error.message} `;
        const retry = document.createElement('button'); retry.type = 'button'; retry.className = 'quiet-button';
        retry.textContent = 'Retry';
        retry.addEventListener('click', () => {
          progress.textContent = 'Loading remaining notes…';
          loadRemainingNotes(relation, appendItems, cursor, progress);
        });
        progress.append(retry);
      }
    }
    async function loadMoreRelated(relation, appendItems, button) {
      if (button.disabled) return;
      button.disabled = true;
      const reference = state.recordRef; const load = state.recordLoad;
      try {
        // Record overviews are opened with includeDeleted=true, so their section cursors use the All scope.
        const query = new URLSearchParams({ reference, relation, cursor: button.dataset.cursor, deletionState: 'All' });
        const page = await api(`/api/related?${query}`);
        if (state.recordRef !== reference || state.recordLoad !== load) return;
        if (page.observedRevision !== state.record.observedRevision) { await openRecord(reference, false); return; }
        appendItems(page.items);
        button.hidden = !page.nextCursor; button.dataset.cursor = page.nextCursor || '';
      } finally { button.disabled = false; }
    }
    async function loadRecordHistory(reference, load) {
      const host = $('record-history');
      const heading = document.createElement('h2'); heading.textContent = 'History'; host.append(heading);
      let cursor = null;
      const more = document.createElement('button'); more.type = 'button'; more.className = 'quiet-button'; more.textContent = 'Load more history';
      const fetchPage = async () => {
        const query = new URLSearchParams({ reference }); if (cursor) query.set('cursor', cursor);
        const page = await api(`/api/history?${query}`); if (state.recordRef !== reference || state.recordLoad !== load) return;
        if (page.observedRevision !== state.record.observedRevision) { await openRecord(reference, false); return; }
        page.items.forEach(item => {
          const entry = document.createElement('div'); entry.className = 'history-entry';
          const action = document.createElement('strong'); action.textContent = `${label(item.action)} · ${item.clientLabel || 'Vault'}`;
          const time = document.createElement('time'); time.textContent = formatTime(item.changedAtUtc); entry.append(action, time); host.insertBefore(entry, more);
        });
        cursor = page.nextCursor; more.hidden = !cursor;
      };
      more.addEventListener('click', () => fetchPage().catch(fail)); host.append(more); await fetchPage();
    }
    async function imageBytes(imageRef, size = 'Thumbnail', revision = null) {
      const headers = { 'X-WritingVault-Session': state.sessionId, 'X-WritingVault-Request': '1' };
      const query = new URLSearchParams({ imageRef, size });
      if (revision !== null) query.set('revision', String(revision));
      const response = await fetch(`/api/image?${query}`, { headers, cache: 'no-store' });
      if (!response.ok) throw new Error(`Image unavailable (${response.status}).`);
      const blob = await response.blob();
      if (!['image/png', 'image/jpeg', 'image/webp'].includes(blob.type)) throw new Error('Unexpected image format.');
      return URL.createObjectURL(blob);
    }
    async function fillGallery(gallery, page, isCurrent) {
      gallery.hidden = !page.items.length;
      for (const item of page.items) {
        if (!isCurrent()) return;
        const figure = document.createElement('figure'); const image = document.createElement('img');
        image.alt = item.altText || item.title || 'Vault image'; image.loading = 'lazy';
        const caption = document.createElement('figcaption'); caption.textContent = item.caption || item.title || item.role || 'Image';
        const link = document.createElement('a');
        link.href = `/c/${encodeURIComponent(state.continuity)}?image=${encodeURIComponent(item.ref)}`;
        link.setAttribute('aria-label', `View ${item.title || item.altText || 'image'} at full size`);
        link.append(image);
        link.addEventListener('click', event => {
          event.preventDefault(); openImageDetail(item.ref, true).catch(fail);
        });
        figure.append(link, caption); gallery.append(figure);
        try { const url = await imageBytes(item.ref); if (!isCurrent()) { URL.revokeObjectURL(url); return; } image.src = url; state.imageUrls.push(url); }
        catch { caption.textContent += ' · Image unavailable'; }
      }
      if (page.hasMore) {
        const note = document.createElement('p'); note.textContent = 'More images are available through the Images section.'; gallery.append(note);
      }
    }
    async function loadRecordImages(reference, load, includeDeleted) {
      const page = await api(`/api/images?${new URLSearchParams({ target: reference, includeDeleted: String(includeDeleted) })}`);
      if (state.recordRef !== reference || state.recordLoad !== load) return;
      if (page.observedRevision !== state.record.observedRevision) { await openRecord(reference, false); return; }
      $('record-image-section').hidden = !page.items.length;
      await fillGallery($('record-gallery'), page,
        () => state.recordRef === reference && state.recordLoad === load);
    }
    async function loadContinuityImages(reference, load) {
      const page = await api(`/api/images?${new URLSearchParams({ target: reference })}`);
      if (state.overviewLoad !== load || state.recordRef || state.timeline || state.searching || state.imageSearching) return;
      if (page.observedRevision !== state.overview.observedRevision) return;
      $('overview-image-section').hidden = !page.items.length;
      await fillGallery($('overview-gallery'), page,
        () => state.overviewLoad === load && !state.recordRef && !state.timeline && !state.searching && !state.imageSearching);
    }
    async function loadSourceText(reference, load, includeDeleted) {
      const section = document.createElement('section'); section.className = 'record-section';
      const heading = document.createElement('h2'); heading.textContent = 'Cached source text';
      const pre = document.createElement('pre'); pre.className = 'source-text';
      section.append(heading, pre); $('record-main').append(section);
      let cursor = null;
      try {
        do {
          const query = new URLSearchParams({ snapshotRef: reference, includeDeleted: String(includeDeleted) }); if (cursor) query.set('cursor', cursor);
          const page = await api(`/api/source-text?${query}`);
          if (state.recordRef !== reference || state.recordLoad !== load) return;
          if (page.observedRevision !== state.record.observedRevision) { await openRecord(reference, false); return; }
          pre.textContent += page.text; cursor = page.nextCursor;
        } while (cursor);
      } catch {
        if (state.recordRef !== reference || state.recordLoad !== load) return;
        pre.textContent = 'Cached source text is unavailable or failed verification. The snapshot details and history remain visible.';
      }
    }
    return Object.freeze({ renderRecord, renderAge, isUnsetDetail,
      loadRecordHistory, loadRecordImages, loadContinuityImages, loadSourceText, imageBytes });
  };
})();
