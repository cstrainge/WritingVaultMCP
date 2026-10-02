(() => {
  'use strict';
  const svgNs = 'http://www.w3.org/2000/svg';
  const byId = id => document.getElementById(id);
  const kinds = [
    ['WorldEvent', 'World events'], ['RelationshipEvent', 'Relationship events'],
    ['Project', 'Project spans'], ['ProjectEvent', 'Project events'],
    ['CharacterEvent', 'Character events'],
    ['LocationEvent', 'Location events'], ['OrganizationEvent', 'Organization events'],
    ['ObjectEvent', 'Object events'], ['Character', 'Characters'],
    ['Relationship', 'Relationships'], ['RelationshipMembershipPeriod', 'Relationship memberships'],
    ['Residence', 'Residences'], ['Membership', 'Memberships'],
    ['OrganizationLocation', 'Organization locations'], ['Ownership', 'Ownership'],
    ['Custody', 'Custody'], ['ObjectLocation', 'Object locations'], ['TemporalEffect', 'Temporal effects']
  ];
  const eventOwners = new Map([
    ['ProjectEvent', 'Project'], ['CharacterEvent', 'Character'], ['LocationEvent', 'Location'],
    ['OrganizationEvent', 'Organization'], ['ObjectEvent', 'Object']
  ]);
  const kindGroups = [
    ['Projects', ['Project', 'ProjectEvent']],
    ['Events', ['WorldEvent', 'RelationshipEvent', 'CharacterEvent', 'LocationEvent', 'OrganizationEvent', 'ObjectEvent']],
    ['People & relationships', ['Character', 'Relationship', 'RelationshipMembershipPeriod',
      'Residence', 'Membership', 'TemporalEffect']],
    ['Places & objects', ['OrganizationLocation', 'Ownership', 'Custody', 'ObjectLocation']]
  ];
  const kindLabels = new Map(kinds);
  const typeNames = new Map([
    ['WorldEvent', 'World event'], ['RelationshipEvent', 'Relationship event'],
    ['Character', 'Character'], ['Relationship', 'Relationship'],
    ['CharacterRelationship', 'Relationship'], ['RelationshipMembershipPeriod', 'Relationship membership'],
    ['CharacterResidence', 'Residence'],
    ['OrganizationMembership', 'Membership'], ['OrganizationLocation', 'Organization location'],
    ['ObjectOwnershipPeriod', 'Ownership'], ['ObjectCustodyPeriod', 'Custody'],
    ['ObjectLocationPeriod', 'Object location'], ['CharacterTemporalEffect', 'Temporal effect']
  ]);
  const humanType = item => {
    if (item.kind === 'RelationshipTransitionGroup' ||
        item.kind === 'RelationshipMembershipPeriod' && item.isMembershipTransition)
      return 'Relationship';
    if (item.kind === 'OrganizationTransitionGroup' ||
        item.kind === 'OrganizationMembership' && item.isMembershipTransition)
      return 'Membership';
    if (item.kind === 'EntityEvent') {
      const owner = (item.related || []).find(link =>
        ['Character', 'Location', 'Organization', 'Object', 'Project'].includes(link.kind));
      return owner ? `${owner.kind} event` : 'Entity event';
    }
    return typeNames.get(item.kind) || item.kind.replace(/([a-z])([A-Z])/g, '$1 $2');
  };
  const chronologyType = item => humanType(item).replace(/ event$/, '');
  const unknownRange = item => ['Before', 'After', 'Range'].includes(item.occurred?.kind) ||
    (['KnownRange', 'UncertainRange'].includes(item.occurred?.kind) &&
      (item.occurred.lower === null || item.occurred.upper === null));
  const uncertainDate = item => (!item.boundaryDate && [item.storyBegins, item.storyEnds].some(date => date && !['ExactDate', 'ExactInstant'].includes(date.kind))) || ['Circa', 'Before', 'After', 'Month', 'Year', 'Range', 'UncertainRange']
    .includes((item.boundaryDate || item.occurred)?.kind);
  const day = 86_400_000;
  const graphPlotStart = 56;
  const graphPlotEnd = 48;
  const minStoryDay = Date.UTC(100, 0, 1);
  const maxStoryDay = Date.UTC(9999, 11, 30);
  const dateValue = value => {
    const match = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2}))?)?/.exec(value || '');
    if (!match) return null;
    const date = new Date(0);
    const year = Number(match[1]), month = Number(match[2]), dayOfMonth = Number(match[3]);
    date.setUTCFullYear(year, month - 1, dayOfMonth);
    date.setUTCHours(Number(match[4] || 0), Number(match[5] || 0), Number(match[6] || 0), 0);
    if (year < 1 || date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 ||
        date.getUTCDate() !== dayOfMonth) return null;
    return date.getTime();
  };
  // Query bounds must stay inside the date range accepted by the Access-backed contract.
  const isoDay = value => new Date(Math.max(minStoryDay, Math.min(maxStoryDay, value))).toISOString().slice(0, 10);
  const bounds = item => {
    const date = item.occurred;
    const lower = dateValue(date.lower), upper = dateValue(date.upper);
    return [lower ?? upper, upper ?? lower];
  };
  const intersects = (item, ref) => item.ref === ref || (item.related || []).some(link => link.ref === ref);
  const entryKey = item => JSON.stringify([item.kind, item.ref, item.boundary,
    item.occurred?.kind, item.occurred?.lower, item.occurred?.upper,
    item.groupedRefs || null]);
  const node = (name, text, className) => {
    const element = document.createElement(name);
    if (text !== undefined) element.textContent = text;
    if (className) element.className = className;
    return element;
  };
  const svgNode = (name, attributes) => {
    const element = document.createElementNS(svgNs, name);
    for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, String(value));
    return element;
  };

  class Timeline {
    constructor(request, openRecord) {
      this.request = request;
      this.openRecord = openRecord;
      this.generation = 0;
      this.controller = null;
      this.graphGeneration = 0;
      this.graphController = null;
      this.mode = 'Calendar';
      this.layout = 'Combined';
      this.selectedKinds = new Set(kinds.map(([kind]) => kind));
      this.selectedEntities = new Map();
      this.highlightRef = null;
      this.range = null;
      this.viewport = null;
      this.graphItems = [];
      this.tableItems = [];
      this.undated = [];
      this.revision = null;
      this.clock = null;
      this.graphWidth = 1000;
      this.scrollAnchor = null;
      this.bind();
    }

    bind() {
      const choices = byId('timeline-kinds');
      choices.textContent = '';
      choices.append(node('legend', 'Record types'));
      const actions = node('div', undefined, 'timeline-kind-actions');
      for (const [label, selectAll] of [['Select all', true], ['Clear all', false]]) {
        const action = node('button', label, 'timeline-kind-action'); action.type = 'button';
        action.addEventListener('click', () => {
          this.selectedKinds = new Set(selectAll ? kinds.map(([kind]) => kind) : []);
          for (const check of choices.querySelectorAll('input[type="checkbox"]')) check.checked = selectAll;
          this.persist(); this.reload();
        });
        actions.append(action);
      }
      choices.append(actions);
      const groups = node('div', undefined, 'timeline-kind-groups');
      for (const [groupName, values] of kindGroups) {
        const group = node('div', undefined, 'timeline-kind-group');
        group.append(node('h3', groupName));
        for (const kind of values) {
          const wrap = node('label');
          const check = node('input'); check.type = 'checkbox'; check.value = kind; check.checked = true;
          check.addEventListener('change', () => {
            if (check.checked) this.selectedKinds.add(kind); else this.selectedKinds.delete(kind);
            this.persist(); this.reload();
          });
          wrap.append(check, document.createTextNode(kindLabels.get(kind))); group.append(wrap);
        }
        groups.append(group);
      }
      choices.append(groups);
      byId('timeline-mode').addEventListener('change', event => { this.mode = event.target.value; this.persist(); this.reload(); });
      byId('timeline-layout').addEventListener('change', event => {
        this.layout = event.target.value === 'Grouped' ? 'Grouped' : 'Combined';
        this.persist(); this.renderGraph();
      });
      byId('timeline-clear-range').addEventListener('click', () => {
        this.range = null; byId('timeline-range-from').value = ''; byId('timeline-range-to').value = '';
        this.persist(); this.reload();
      });
      byId('timeline-apply-range').addEventListener('click', () => {
        const from = byId('timeline-range-from').value, to = byId('timeline-range-to').value;
        if (dateValue(from) === null || dateValue(to) === null || dateValue(from) > dateValue(to)) {
          byId('timeline-status').textContent = 'Choose a valid From date no later than the To date.'; return;
        }
        this.range = [from, to]; this.persist(); this.reload();
      });
      byId('timeline-detail-close').addEventListener('click', () => { byId('timeline-detail').hidden = true; });
      byId('timeline-find-entity').addEventListener('click', () => this.findEntities());
      byId('timeline-entity-search').addEventListener('keydown', event => {
        if (event.key === 'Enter') { event.preventDefault(); this.findEntities(); }
      });
      byId('timeline-pan-left').addEventListener('click', () => this.scrollGraph(-1));
      byId('timeline-pan-right').addEventListener('click', () => this.scrollGraph(1));
      byId('timeline-zoom-in').addEventListener('click', () => this.changeViewport(.5, 0));
      byId('timeline-zoom-out').addEventListener('click', () => this.changeViewport(2, 0));
      byId('timeline-now-outside').addEventListener('click', () => this.centerStoryTime());
      const graph = byId('timeline-graph');
      const graphWrap = byId('timeline-graph-wrap');
      graphWrap.addEventListener('keydown', event => {
        if (event.target !== graphWrap || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
        event.preventDefault();
        graphWrap.scrollBy({ left: graphWrap.clientWidth * (event.key === 'ArrowLeft' ? -.7 : .7) });
      });
      let drag = null;
      const svgX = event => (event.clientX - graph.getBoundingClientRect().left) *
        graph.viewBox.baseVal.width / graph.getBoundingClientRect().width;
      graph.addEventListener('pointerdown', event => {
        if (event.pointerType === 'touch' || !this.viewport || event.target.closest('.timeline-mark')) return;
        drag = svgX(event); graph.setPointerCapture(event.pointerId);
      });
      graph.addEventListener('pointercancel', () => { drag = null; });
      graph.addEventListener('pointerup', event => {
        if (drag === null || !this.viewport) return;
        const start = drag; drag = null;
        const finish = svgX(event);
        if (Math.abs(finish - start) < 6) return;
        const pixel = x => Math.max(0, Math.min(1,
          (x - graphPlotStart) / (graph.viewBox.baseVal.width - graphPlotStart - graphPlotEnd)));
        const [left, right] = this.viewport;
        const dates = [start, finish].map(x => left + pixel(x) * (right - left)).sort((a, b) => a - b);
        this.range = dates.map(isoDay);
        byId('timeline-range-from').value = this.range[0];
        byId('timeline-range-to').value = this.range[1];
        this.persist(); this.reload();
      });
    }

    async query(params, signal) {
      const query = new URLSearchParams();
      for (const [key, value] of Object.entries(params)) if (value !== null && value !== undefined && value !== '')
        query.set(key, Array.isArray(value) ? value.join(',') : String(value));
      return this.request(`/api/timeline?${query}`, { signal });
    }

    async allEntries(filters, expectedRevision, signal, generation, label) {
      const items = [], undated = [], seenCursors = new Set();
      let cursor = null, clock = null;
      do {
        const page = await this.query({ ...filters, limit: 500, cursor }, signal);
        if (generation !== this.generation) return null;
        if (page.observedRevision !== expectedRevision) return null;
        items.push(...(page.items || []));
        undated.push(...(page.undated || []));
        clock = page.clock;
        cursor = page.nextCursor;
        if (page.hasMore && !cursor) throw new Error('The timeline has more entries but no continuation cursor.');
        if (cursor) {
          if (seenCursors.has(cursor)) throw new Error('The timeline repeated a cursor while loading.');
          seenCursors.add(cursor);
          byId('timeline-status').textContent = `Loading ${label}: ${(items.length + undated.length).toLocaleString()} entries…`;
        }
      } while (cursor);
      return { items, undated, clock, observedRevision: expectedRevision };
    }

    filters() {
      const selected = [...this.selectedKinds].filter(kind => !eventOwners.has(kind));
      const owners = [...this.selectedKinds].filter(kind => eventOwners.has(kind)).map(kind => eventOwners.get(kind));
      if (owners.length) selected.push('EntityEvent');
      return {
        mode: this.mode, kinds: this.selectedKinds.size === kinds.length ? null : selected,
        entityEventKinds: owners.length === eventOwners.size ? null : owners,
        focusRefs: [...this.selectedEntities.keys()]
      };
    }

    groupRelationshipTransitions(items, combine = true) {
      const normalized = items;
      if (!combine) return normalized;
      const groups = new Map();
      normalized.forEach((item, index) => {
        if (!['RelationshipMembershipPeriod', 'OrganizationMembership'].includes(item.kind) ||
            !item.isMembershipTransition || !['Start', 'End'].includes(item.boundary) ||
            !['ExactDate', 'ExactInstant'].includes(item.occurred?.kind) ||
            item.hasCustomDescription) return;
        const ownerKind = item.kind === 'OrganizationMembership' ? 'Organization' : 'Relationship';
        const owner = (item.related || []).find(link => link.kind === ownerKind);
        const characters = (item.related || []).filter(link => link.kind === 'Character');
        if (!owner || characters.length !== 1) return;
        const date = item.occurred;
        const key = JSON.stringify([item.kind, owner.ref, item.boundary, date.kind,
          date.lower, date.upper, date.calendarId, date.lowerInclusive,
          date.upperInclusive, date.originalText, item.isDeleted]);
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push({ index, item, character: characters[0], owner });
      });
      const first = new Map(), hidden = new Set();
      for (const candidates of groups.values()) {
        if (candidates.length < 2 ||
            new Set(candidates.map(candidate => candidate.character.ref)).size !== candidates.length)
          continue;
        const [head] = candidates;
        const names = candidates.map(candidate => candidate.character.label);
        const subject = names.length === 2 ? names.join(' and ') :
          `${names.slice(0, -1).join(', ')}, and ${names.at(-1)}`;
        const related = [head.owner, ...candidates.flatMap(candidate => candidate.item.related || [])]
          .filter((link, index, links) => links.findIndex(other => other.ref === link.ref) === index);
        const organization = head.item.kind === 'OrganizationMembership';
        const title = organization
          ? `${subject} ${head.item.boundary === 'Start' ? 'joined' : 'left'} organization ${head.owner.label}.`
          : `${subject} ${head.item.boundary === 'Start' ? 'entered' : 'left'} a relationship.`;
        first.set(head.index, { ...head.item, ref: head.owner.ref,
          kind: organization ? 'OrganizationTransitionGroup' : 'RelationshipTransitionGroup', boundary: null,
          title,
          related, containsHighlight: candidates.some(candidate => candidate.item.containsHighlight),
          groupedRefs: candidates.map(candidate => candidate.item.ref) });
        for (const candidate of candidates.slice(1)) hidden.add(candidate.index);
      }
      return normalized.flatMap((item, index) => first.has(index) ? [first.get(index)] :
        hidden.has(index) ? [] : [item]);
    }

    async reload(attempt = 0) {
      const generation = ++this.generation;
      this.controller?.abort(); this.controller = new AbortController();
      this.graphGeneration++; this.graphController?.abort();
      const signal = this.controller.signal;
      byId('timeline-status').textContent = 'Updating the chronology…';
      const loadingRow = node('tr');
      const loadingCell = node('td', 'Loading selected entries…'); loadingCell.colSpan = 7;
      loadingRow.append(loadingCell); byId('timeline-rows').replaceChildren(loadingRow);
      byId('timeline-table-count').textContent = 'Loading…';
      byId('timeline-undated').hidden = true;
      const graphHost = byId('timeline-graph');
      graphHost.textContent = '';
      graphHost.style.width = '100%';
      graphHost.setAttribute('viewBox', '0 0 1000 220');
      const graphLoading = svgNode('text', { x: graphPlotStart, y: 108, class: 'timeline-empty' });
      graphLoading.textContent = 'Loading chronology…'; graphHost.append(graphLoading);
      if (!this.selectedKinds.size) {
        this.graphItems = []; this.tableItems = []; this.undated = [];
        this.render(); byId('timeline-status').textContent = 'Select at least one record type.'; return;
      }
      try {
        const graphFilters = { ...this.filters(), resolution: 'Aggregate', includeUndated: false,
          highlightRef: this.highlightRef, expandRecurrences: !!this.viewport, limit: 500 };
        if (this.viewport) { graphFilters.from = isoDay(this.viewport[0]); graphFilters.to = isoDay(this.viewport[1]); }
        let graph = await this.query(graphFilters, signal);
        if (generation !== this.generation) return;
        if (!this.viewport) {
          this.clock = graph.clock;
          this.viewport = this.fullDomain(graph.items || []);
          if (this.viewport) {
            const fitted = await this.query({ ...graphFilters, expandRecurrences: true,
              from: isoDay(this.viewport[0]), to: isoDay(this.viewport[1]) }, signal);
            if (generation !== this.generation) return;
            if (fitted.observedRevision !== graph.observedRevision) {
              this.viewport = null;
              if (attempt < 2) return this.reload(attempt + 1);
              throw new Error('The timeline changed repeatedly while fitting its dates.');
            }
            graph = fitted;
          }
        }
        const tableFilters = { ...this.filters(), resolution: 'Detail', expandRanges: true,
          includeUndated: false };
        if (this.range) { tableFilters.from = this.range[0]; tableFilters.to = this.range[1]; }
        const table = await this.allEntries(tableFilters, graph.observedRevision, signal, generation, 'the chronology');
        if (generation !== this.generation) return;
        if (!table) {
          if (attempt < 2) return this.reload(attempt + 1);
          throw new Error('The timeline changed repeatedly while loading. It will retry on the next live update.');
        }
        const undated = this.range ? null : await this.allEntries({ ...this.filters(), resolution: 'Detail',
          undatedOnly: true, includeUndated: true }, graph.observedRevision, signal, generation, 'undated material');
        if (generation !== this.generation) return;
        if (!this.range && !undated) {
          if (attempt < 2) return this.reload(attempt + 1);
          throw new Error('The timeline changed repeatedly while loading. It will retry on the next live update.');
        }
        this.graphItems = this.groupRelationshipTransitions(graph.items || [], !graph.hasMore);
        this.graphTruncated = graph.hasMore;
        this.tableItems = this.groupRelationshipTransitions(table.items || []);
        this.undated = undated?.undated || [];
        this.revision = table.observedRevision; this.clock = table.clock;
        for (const item of this.tableItems)
          for (const link of item.related || []) if (this.selectedEntities.has(link.ref)) this.selectedEntities.set(link.ref, link.label);
        this.renderEntities();
        if (!this.viewport) this.viewport = this.fullDomain(this.graphItems);
        this.render();
        byId('timeline-status').textContent = graph.hasMore
          ? 'More graph clusters exist than this view can show. Narrow the date window or filters.'
          : this.range ? `Showing entries overlapping ${this.range[0]} through ${this.range[1]}.` : 'Showing the selected continuity chronology.';
      } catch (error) {
        if (error.name !== 'AbortError' && generation === this.generation) {
          if (attempt < 2 && /timeline changed|cursor (?:cannot be used|expired|invalid)/i.test(error.message))
            return this.reload(attempt + 1);
          byId('timeline-status').textContent = `Timeline unavailable: ${error.message}`;
          throw error;
        }
      }
    }

    async reloadGraph() {
      if (!this.revision || byId('timeline-table-count').textContent === 'Loading…') return this.reload();
      const generation = this.generation, graphGeneration = ++this.graphGeneration;
      this.graphController?.abort(); this.graphController = new AbortController();
      try {
        const filters = { ...this.filters(), resolution: 'Aggregate', includeUndated: false,
          highlightRef: this.highlightRef, limit: 500 };
        if (this.viewport) { filters.from = isoDay(this.viewport[0]); filters.to = isoDay(this.viewport[1]); }
        const graph = await this.query(filters, this.graphController.signal);
        if (generation !== this.generation || graphGeneration !== this.graphGeneration) return;
        if (graph.observedRevision !== this.revision) return this.reload();
        this.graphItems = this.groupRelationshipTransitions(graph.items || [], !graph.hasMore);
        this.graphTruncated = graph.hasMore;
        this.renderGraph();
        byId('timeline-status').textContent = graph.hasMore
          ? 'More graph clusters exist than this view can show. Narrow the date window or filters.'
          : this.range ? `Showing entries overlapping ${this.range[0]} through ${this.range[1]}.` : 'Showing the selected continuity chronology.';
      } catch (error) {
        if (error.name !== 'AbortError' && generation === this.generation && graphGeneration === this.graphGeneration)
          byId('timeline-status').textContent = `Timeline graph unavailable: ${error.message}`;
      }
    }

    async findEntities() {
      const term = byId('timeline-entity-search').value.trim();
      const results = byId('timeline-entity-results'); results.textContent = '';
      if (!term) return;
      try {
        const query = new URLSearchParams({ text: term, limit: '20' });
        const page = await this.request(`/api/search?${query}`);
        for (const item of page.items || []) {
          if (!['Character', 'Location', 'Project', 'Organization', 'Object', 'WorldEvent'].includes(item.kind)) continue;
          const button = node('button', `${item.label} · ${humanType(item)}`, 'quiet-button'); button.type = 'button';
          button.addEventListener('click', () => {
            if (!this.selectedEntities.has(item.ref) && this.selectedEntities.size >= 20) {
              byId('timeline-status').textContent = 'Select at most 20 individual entities.'; return;
            }
            this.selectedEntities.set(item.ref, item.label); results.textContent = '';
            byId('timeline-entity-search').value = ''; this.renderEntities(); this.persist(); this.reload();
          });
          results.append(button);
        }
        if (!results.childElementCount) results.append(node('p', 'No matching entities found.'));
      } catch (error) { results.append(node('p', error.message)); }
    }

    async highlightEntity(ref, rowRef) {
      this.highlightRef = ref;
      this.graphItems = this.graphItems.map(item => ({ ...item, containsHighlight: false }));
      this.renderGraph(); this.updateTableHighlight();
      const selectedRow = [...byId('timeline-rows').querySelectorAll('tr')]
        .find(row => row.dataset.ref === rowRef);
      [...(selectedRow?.querySelectorAll('.timeline-entity-link') || [])]
        .find(button => button.dataset.ref === ref)?.focus();
      await this.reloadGraph();
    }

    renderEntities() {
      const host = byId('timeline-selected-entities'); host.textContent = '';
      for (const [ref, label] of this.selectedEntities) {
        const button = node('button', `${label} ×`, 'timeline-chip'); button.type = 'button';
        button.setAttribute('aria-label', `Remove ${label} from timeline filter`);
        button.addEventListener('click', () => { this.selectedEntities.delete(ref); this.persist(); this.renderEntities(); this.reload(); });
        host.append(button);
      }
    }

    fullDomain(items) {
      const values = items.flatMap(item => bounds(item)).filter(Number.isFinite);
      if (!values.length && ['Set', 'DateOnly'].includes(this.clock?.status)) {
        const now = dateValue(window.WritingVaultStoryDates.clockLocalDateTime(this.clock));
        if (now !== null) values.push(now);
      }
      if (!values.length) return null;
      const start = Math.min(...values), end = Math.max(...values);
      const padding = Math.max(day * 7, (end - start) * .06);
      return [Math.max(minStoryDay, start - padding), Math.min(maxStoryDay, end + padding)];
    }

    centerStoryTime() {
      const now = dateValue(window.WritingVaultStoryDates.clockLocalDateTime(this.clock));
      if (now === null) return;
      const width = this.viewport ? this.viewport[1] - this.viewport[0] : day * 14;
      const lower = Math.max(minStoryDay, Math.min(maxStoryDay - width, now - width / 2));
      this.viewport = [lower, lower + width];
      this.scrollAnchor = now;
      this.persist(); this.reloadGraph();
    }

    changeViewport(scale, pan) {
      if (!this.viewport) return;
      const [start, end] = this.viewport;
      const scroller = byId('timeline-graph-wrap');
      const plotWidth = this.graphWidth - graphPlotStart - graphPlotEnd;
      const visibleCenterFraction = Math.max(0, Math.min(1,
        (scroller.scrollLeft + scroller.clientWidth / 2 - graphPlotStart) / plotWidth));
      const visibleCenter = start + (end - start) * visibleCenterFraction;
      const visibleSpan = (end - start) * Math.min(1, scroller.clientWidth / plotWidth);
      const width = Math.min(maxStoryDay - minStoryDay, Math.max(day, (end - start) * scale));
      const middle = scale === 1 ? (start + end) / 2 + visibleSpan * pan : visibleCenter;
      const lower = Math.max(minStoryDay, Math.min(maxStoryDay - width, middle - width / 2));
      this.viewport = [lower, lower + width];
      if (scale !== 1) this.scrollAnchor = visibleCenter;
      this.persist(); this.reloadGraph();
    }

    scrollGraph(direction) {
      const scroller = byId('timeline-graph-wrap');
      const before = scroller.scrollLeft;
      scroller.scrollLeft = Math.max(0, Math.min(scroller.scrollWidth - scroller.clientWidth,
        before + direction * scroller.clientWidth * .7));
      if (Math.abs(scroller.scrollLeft - before) < 1) this.changeViewport(1, direction * .7);
    }

    persist() {
      // The continuity path is retained; timeline controls are safe, local, bookmarkable URL state.
      const url = new URL(location.href);
      for (const name of ['timelineFrom', 'timelineTo', 'timelineMode', 'timelineLayout', 'timelineFocus', 'timelineKinds', 'timelineViewFrom', 'timelineViewTo']) url.searchParams.delete(name);
      if (this.range) { url.searchParams.set('timelineFrom', this.range[0]); url.searchParams.set('timelineTo', this.range[1]); }
      if (this.mode !== 'Calendar') url.searchParams.set('timelineMode', this.mode);
      if (this.layout !== 'Combined') url.searchParams.set('timelineLayout', this.layout);
      if (this.selectedEntities.size) url.searchParams.set('timelineFocus', [...this.selectedEntities.keys()].join(','));
      if (this.selectedKinds.size !== kinds.length) url.searchParams.set('timelineKinds', [...this.selectedKinds].join(','));
      if (this.viewport) { url.searchParams.set('timelineViewFrom', isoDay(this.viewport[0])); url.searchParams.set('timelineViewTo', isoDay(this.viewport[1])); }
      history.replaceState({}, '', url);
    }

    focus(ref, label) {
      this.selectedEntities.set(ref, label);
      this.renderEntities(); this.persist();
    }

    restore() {
      const params = new URLSearchParams(location.search);
      const from = params.get('timelineFrom'), to = params.get('timelineTo');
      if (dateValue(from) !== null && dateValue(to) !== null && dateValue(from) <= dateValue(to)) this.range = [from, to];
      if (this.range) { byId('timeline-range-from').value = this.range[0]; byId('timeline-range-to').value = this.range[1]; }
      this.mode = params.get('timelineMode') === 'Narrative' ? 'Narrative' : 'Calendar';
      byId('timeline-mode').value = this.mode;
      this.layout = params.get('timelineLayout') === 'Grouped' ? 'Grouped' : 'Combined';
      byId('timeline-layout').value = this.layout;
      if (params.has('timelineKinds')) {
        const valid = new Set(kinds.map(([kind]) => kind));
        this.selectedKinds = new Set((params.get('timelineKinds') || '').split(',').filter(kind => valid.has(kind)));
        for (const check of byId('timeline-kinds').querySelectorAll('input')) check.checked = this.selectedKinds.has(check.value);
      }
      const viewFrom = dateValue(params.get('timelineViewFrom'));
      const viewTo = dateValue(params.get('timelineViewTo'));
      if (viewFrom !== null && viewTo !== null && viewFrom < viewTo) this.viewport = [viewFrom, viewTo];
      // Focus refs are restored by label from the loaded chronology when available.
      for (const ref of (params.get('timelineFocus') || '').split(',').filter(Boolean)) this.selectedEntities.set(ref, ref);
      this.renderEntities();
    }

    render() {
      this.renderGraph(); this.renderTable();
      byId('timeline-clock').textContent = ['Set', 'DateOnly'].includes(this.clock?.status)
        ? `Story time · ${window.WritingVaultStoryDates.clockTime(this.clock)}` : 'Story time unset';
      byId('timeline-clear-range').hidden = !this.range;
    }

    renderGraph() {
      const graph = byId('timeline-graph'); graph.textContent = '';
      const domain = this.viewport;
      const now = ['Set', 'DateOnly'].includes(this.clock?.status)
        ? dateValue(window.WritingVaultStoryDates.clockLocalDateTime(this.clock)) : null;
      const nowInView = now !== null && domain && now >= domain[0] && now <= domain[1];
      const outside = byId('timeline-now-outside');
      outside.hidden = !domain || now === null || nowInView;
      if (!outside.hidden) {
        const direction = now < domain[0] ? 'earlier' : 'later';
        outside.textContent = `${direction === 'earlier' ? '← ' : ''}Now is ${direction} than this view · ` +
          `${window.WritingVaultStoryDates.clockTime(this.clock)} · Show now${direction === 'later' ? ' →' : ''}`;
      }
      const nowInset = nowInView ? 78 : 0;
      const visibleWidth = byId('timeline-graph-wrap').clientWidth;
      const spanDays = domain ? (domain[1] - domain[0]) / day : 0;
      // A bounded wide canvas keeps individual dates readable; zoom and pan cover spans
      // larger than the 16,000-pixel canvas without creating an unbounded DOM surface.
      const width = Math.min(16000, Math.max(1000, Math.ceil(visibleWidth * 2), Math.ceil(spanDays * 28)));
      this.graphWidth = width;
      graph.style.width = `${width}px`;
      if (!domain || (!this.graphItems.length && now === null)) {
        graph.setAttribute('viewBox', `0 0 ${width} 220`);
        this.scrollAnchor = null;
        const empty = svgNode('text', { x: graphPlotStart, y: 108, class: 'timeline-empty' });
        empty.textContent = 'No dated entries in this view.'; graph.append(empty);
        return;
      }
      const plotWidth = width - graphPlotStart - graphPlotEnd;
      const x = value => graphPlotStart + plotWidth * Math.max(0, Math.min(1,
        (value - domain[0]) / Math.max(1, domain[1] - domain[0])));
      const minCardWidth = 174, maxCardWidth = 320, minCardHeight = 70;
      const tierGap = 10, maxTiers = this.layout === 'Grouped' ? 4 : 6;
      // Measure the actual SVG fonts: character counts are not a reliable width for
      // wide names, long relationship labels, or words without spaces.
      const titleProbe = svgNode('text', { class: 'timeline-card-title' });
      const categoryProbe = svgNode('text', { class: 'timeline-card-category' });
      graph.append(titleProbe, categoryProbe);
      const measure = (probe, value) => {
        probe.textContent = value;
        return probe.getComputedTextLength();
      };
      const titleWidth = value => measure(titleProbe, value);
      const wrapTitle = (value, available) => {
        const lines = [];
        let line = '';
        for (const word of value.split(' ')) {
          const candidate = line ? `${line} ${word}` : word;
          if (titleWidth(candidate) <= available) { line = candidate; continue; }
          if (line) { lines.push(line); line = ''; }
          // A single long token must wrap too; never draw outside the card.
          for (const character of Array.from(word)) {
            if (line && titleWidth(line + character) > available) {
              lines.push(line); line = '';
            }
            line += character;
          }
        }
        if (line) lines.push(line);
        return lines.length ? lines : [''];
      };
      const tierEnds = [], tierHeights = [];
      const grouped = new Map();
      let compactCount = 0;
      const layouts = this.graphItems.map(item => {
        const [lower, upper] = bounds(item);
        const category = item.kind === 'Aggregate' ? 'CLUSTER' : humanType(item).toUpperCase();
        const title = item.title.replace(/\s+/g, ' ').trim();
        const cardWidth = Math.min(maxCardWidth, Math.max(minCardWidth,
          Math.ceil(titleWidth(title) + 30), Math.ceil(measure(categoryProbe, category) + 44)));
        const titleLines = wrapTitle(title, cardWidth - 30);
        const cardHeight = Math.max(minCardHeight, 42 + 17 * (titleLines.length - 1) + 16);
        return { item, lower, upper, anchor: lower, category, titleLines, cardWidth, cardHeight };
      }).filter(entry => Number.isFinite(entry.anchor) && Number.isFinite(entry.upper))
        .sort((a, b) => a.anchor - b.anchor || a.item.title.localeCompare(b.item.title));
      titleProbe.remove(); categoryProbe.remove();
      for (const entry of layouts) {
        const groupName = this.layout === 'Grouped' ? humanType(entry.item) : 'Combined';
        if (!grouped.has(groupName)) grouped.set(groupName,
          { name: groupName, ends: [], tierHeights: [], compact: 0 });
        const bucket = grouped.get(groupName);
        const ends = this.layout === 'Grouped' ? bucket.ends : tierEnds;
        entry.bucket = bucket;
        entry.anchorX = x(entry.anchor);
        entry.cardX = Math.max(8, Math.min(width - entry.cardWidth - 8,
          entry.anchorX - entry.cardWidth / 2));
        entry.tier = ends.findIndex(end => entry.cardX >= end + 10);
        if (entry.tier < 0) entry.tier = ends.length < maxTiers ? ends.length : -1;
        if (entry.tier >= 0) {
          ends[entry.tier] = entry.cardX + entry.cardWidth;
          const heights = this.layout === 'Grouped' ? bucket.tierHeights : tierHeights;
          heights[entry.tier] = Math.max(heights[entry.tier] || 0, entry.cardHeight);
        }
        else { entry.compactIndex = bucket.compact++; compactCount++; }
      }
      const tierSpace = heights => heights.reduce((sum, value) => sum + value, 0) +
        Math.max(0, heights.length - 1) * tierGap;
      const tierOffset = (heights, tier) => heights.slice(0, tier).reduce((sum, value) => sum + value + tierGap, 0);
      let height;
      if (this.layout === 'Grouped') {
        let top = 12 + nowInset;
        for (const bucket of grouped.values()) {
          bucket.top = top;
          bucket.height = Math.max(150, 52 + tierSpace(bucket.tierHeights) + (bucket.compact ? 28 : 0));
          bucket.baseline = top + bucket.height - 16;
          top += bucket.height + 12;
        }
        height = Math.max(220, top + 45);
      } else height = Math.max(220, 100 + nowInset + tierSpace(tierHeights) + (compactCount ? 28 : 0));
      const axisY = height - 46;
      graph.setAttribute('viewBox', `0 0 ${width} ${height}`);
      const patterns = svgNode('defs', {});
      for (const color of ['world', 'character', 'relationship', 'other']) {
        const pattern = svgNode('pattern', { id: `timeline-fuzzy-${color}`,
          width: 8, height: 8, patternUnits: 'userSpaceOnUse',
          class: `timeline-fuzzy-pattern ${color}` });
        pattern.append(svgNode('rect', { width: 8, height: 8,
          class: 'timeline-fuzzy-pattern-base' }),
        svgNode('path', { d: 'M -2 2 L 2 -2 M 0 8 L 8 0 M 6 10 L 10 6',
          class: 'timeline-fuzzy-pattern-stripe' }));
        patterns.append(pattern);
        const checker = svgNode('pattern', { id: `timeline-unknown-${color}`,
          width: 8, height: 8, patternUnits: 'userSpaceOnUse',
          class: `timeline-unknown-pattern ${color}` });
        checker.append(svgNode('rect', { width: 8, height: 8,
          class: 'timeline-unknown-pattern-base' }),
        svgNode('path', { d: 'M 0 0 H 4 V 4 H 0 Z M 4 4 H 8 V 8 H 4 Z',
          class: 'timeline-unknown-pattern-square' }));
        patterns.append(checker);
      }
      graph.append(patterns);
      graph.append(svgNode('rect', { x: 0, y: axisY, width, height: height - axisY,
        class: 'timeline-axis-band' }));
      const tickCount = Math.max(4, Math.floor(plotWidth / 120));
      const tickFormat = spanDays > 365 * 3 ? { year: 'numeric' } :
        spanDays > 180 ? { month: 'short', year: 'numeric' } :
          spanDays > 3 ? { month: 'short', day: 'numeric' } :
            { month: 'short', day: 'numeric', hour: 'numeric', hour12: true };
      const formatter = new Intl.DateTimeFormat('en-US', { ...tickFormat, timeZone: 'UTC' });
      let previousTick = '';
      for (let index = 0; index <= tickCount; index++) {
        const value = domain[0] + (domain[1] - domain[0]) * index / tickCount;
        const at = x(value), text = formatter.format(new Date(value));
        if (text === previousTick) continue;
        previousTick = text;
        graph.append(svgNode('line', { x1: at, y1: 18, x2: at, y2: axisY,
          class: 'timeline-gridline' }));
        graph.append(svgNode('line', { x1: at, y1: axisY, x2: at, y2: axisY + 9,
          class: 'timeline-axis-tick' }));
        const label = svgNode('text', { x: at, y: axisY + 27, class: 'timeline-axis-label',
          'text-anchor': 'middle' });
        label.textContent = text; graph.append(label);
      }
      graph.append(svgNode('line', { x1: graphPlotStart, y1: axisY,
        x2: width - graphPlotEnd, y2: axisY, class: 'timeline-axis-line' }));
      if (this.layout === 'Grouped') for (const bucket of grouped.values()) {
        const label = svgNode('text', { x: graphPlotStart, y: bucket.top + 13,
          class: 'timeline-group-label' });
        label.textContent = bucket.name; graph.append(label);
        graph.append(svgNode('line', { x1: graphPlotStart, y1: bucket.baseline,
          x2: width - graphPlotEnd, y2: bucket.baseline, class: 'timeline-group-line' }));
      }
      if (this.range) {
        const from = dateValue(this.range[0]), to = dateValue(this.range[1]);
        if (from !== null && to !== null)
          graph.append(svgNode('rect', { x: x(from), y: 12,
            width: Math.max(3, x(Math.min(domain[1], to + day)) - x(from)),
            height: axisY - 12, class: 'timeline-selection' }));
      }
      if (compactCount) {
        const note = svgNode('text', { x: graphPlotStart,
          y: this.layout === 'Grouped' ? axisY - 8 : 23, class: 'timeline-density-note' });
        note.textContent = `${compactCount} tightly packed entries appear as dots. Zoom in to separate them.`;
        graph.append(note);
      }
      if (nowInView) {
        const dateOnly = this.clock?.status === 'DateOnly';
        const dateLabel = new Intl.DateTimeFormat('en-US', {
          month: 'short', day: 'numeric', year: 'numeric', ...(dateOnly ? {} : { hour: 'numeric', minute: '2-digit' }),
          hour12: true, timeZone: 'UTC'
        }).format(new Date(now));
        const zoneLabel = this.clock.referenceTimeZoneId || 'UTC';
        const dateProbe = svgNode('text', { class: 'timeline-now-date' });
        const zoneProbe = svgNode('text', { class: 'timeline-now-zone' });
        dateProbe.textContent = dateLabel; zoneProbe.textContent = zoneLabel;
        graph.append(dateProbe, zoneProbe);
        const cardWidth = Math.min(width - 16, Math.max(200,
          Math.ceil(Math.max(dateProbe.getComputedTextLength(), zoneProbe.getComputedTextLength()) + 28)));
        dateProbe.remove(); zoneProbe.remove();
        const at = x(now), cardX = Math.max(8, Math.min(width - cardWidth - 8, at - cardWidth / 2));
        const marker = svgNode('g', { class: 'timeline-now-marker', tabindex: '0', role: 'button',
          'aria-label': `Center story time: ${window.WritingVaultStoryDates.clockTime(this.clock)}` });
        const title = svgNode('title', {});
        title.textContent = `Now · ${window.WritingVaultStoryDates.clockTime(this.clock)}`;
        if (dateOnly) marker.append(svgNode('rect', { x: at, y: 70,
          width: Math.max(2, x(now + day) - at), height: axisY - 70,
          class: 'timeline-now-day' }));
        else marker.append(svgNode('line', { x1: at, y1: 70, x2: at, y2: axisY,
          class: 'timeline-now' }), svgNode('circle', { cx: at, cy: axisY, r: 5,
          class: 'timeline-now-anchor' }));
        marker.append(svgNode('rect', { x: cardX, y: 10,
          width: cardWidth, height: 60, rx: 9, class: 'timeline-now-card' }),
        svgNode('rect', { x: cardX + 1, y: 11, width: 5, height: 58, rx: 2,
          class: 'timeline-now-accent' }));
        const heading = svgNode('text', { x: cardX + 14, y: 27, class: 'timeline-now-heading' });
        heading.textContent = dateOnly ? 'STORY DAY' : 'NOW';
        const date = svgNode('text', { x: cardX + 14, y: 46, class: 'timeline-now-date' });
        date.textContent = dateLabel;
        const zone = svgNode('text', { x: cardX + 14, y: 62, class: 'timeline-now-zone' });
        zone.textContent = zoneLabel;
        marker.append(heading, date, zone);
        marker.addEventListener('click', () => this.centerStoryTime());
        marker.addEventListener('keydown', event => {
          if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); this.centerStoryTime(); }
        });
        graph.append(marker);
      }
      for (const entry of layouts) {
        const { item, lower, upper, anchorX, cardX, cardWidth, cardHeight, tier } = entry;
        const markY = this.layout === 'Grouped' ? entry.bucket.baseline : axisY;
        const openStart = item.occurred.lower === null;
        const openEnd = item.occurred.upper === null;
        const unknown = unknownRange(item);
        const fuzzy = uncertainDate(item) && !unknown;
        const colorClass = item.kind === 'WorldEvent' || item.lane === 'WorldEvents' ? 'world' :
          item.kind === 'Character' || item.lane === 'Characters' ? 'character' :
            item.kind === 'CharacterRelationship' || item.kind === 'RelationshipEvent' ||
            item.kind === 'RelationshipMembershipPeriod' ||
            item.lane === 'Relationships' || item.lane === 'RelationshipEvents' ? 'relationship' : 'other';
        const group = svgNode('g', {
          class: `timeline-mark ${colorClass}${unknown ? ' unknown' : fuzzy ? ' fuzzy' : ''}${item.kind === 'Aggregate' ? ' aggregate' : ''}${tier < 0 ? ' compact' : ''}${this.highlightRef && (item.containsHighlight || intersects(item, this.highlightRef)) ? ' highlighted' : ''}`,
          tabindex: '0', role: 'button', 'data-ref': item.ref, 'data-key': entryKey(item),
          'aria-label': `${humanType(item)}; ${item.title}; ${this.dateText(item)}${openStart || openEnd ? '; open-ended' : ''}`
        });
        const title = svgNode('title', {}); title.textContent = `${item.title}: ${this.dateText(item)}`;
        group.append(title);
        const barStart = x(openStart ? domain[0] : lower);
        const barEnd = x(openEnd ? domain[1] : upper);
        if (fuzzy || unknown || item.kind === 'Aggregate' || barEnd - barStart > 5) {
          const barHeight = 10;
          const barY = markY - 8 - barHeight / 2 -
            (tier < 0 ? entry.compactIndex % 3 : tier % 3) * 5;
          const barCenter = barY + barHeight / 2;
          group.append(svgNode('rect', { x: barStart, y: barY,
            width: Math.max(10, barEnd - barStart), height: barHeight,
            rx: barHeight / 2, class: 'timeline-period' }));
          if (openStart) group.append(svgNode('path', { d: `M ${barStart + 2} ${barCenter - 7} L ${barStart - 6} ${barCenter} L ${barStart + 2} ${barCenter + 7}`, class: 'timeline-open-edge' }));
          if (openEnd) group.append(svgNode('path', { d: `M ${barEnd - 2} ${barCenter - 7} L ${barEnd + 6} ${barCenter} L ${barEnd - 2} ${barCenter + 7}`, class: 'timeline-open-edge' }));
        }
        const heights = this.layout === 'Grouped' ? entry.bucket.tierHeights : tierHeights;
        const cardY = tier < 0 ? markY - 36 - entry.compactIndex % 4 * 9 :
          markY - 24 - cardHeight - tierOffset(heights, tier);
        group.append(svgNode('line', { x1: anchorX, y1: tier < 0 ? cardY : cardY + cardHeight,
          x2: anchorX, y2: markY, class: 'timeline-stem' }));
        group.append(svgNode('circle', { cx: anchorX, cy: markY, r: 4.5, class: 'timeline-anchor' }));
        if (tier < 0) group.append(svgNode('circle', { cx: anchorX, cy: cardY, r: 5,
          class: 'timeline-compact-dot' }));
        else {
          group.append(svgNode('rect', { x: cardX, y: cardY, width: cardWidth,
            height: cardHeight, rx: 9, class: 'timeline-card' }));
          group.append(svgNode('rect', { x: cardX + 1, y: cardY + 1, width: 4,
            height: cardHeight - 2, rx: 2, class: 'timeline-card-accent' }));
          group.append(svgNode('circle', { cx: cardX + 17, cy: cardY + 18, r: 3.5,
            class: 'timeline-card-icon' }));
          const category = svgNode('text', { x: cardX + 27, y: cardY + 21,
            class: 'timeline-card-category' });
          category.textContent = entry.category;
          group.append(category);
          const titleText = svgNode('text', { x: cardX + 13, y: cardY + 42,
            class: 'timeline-card-title' });
          for (const [index, line] of entry.titleLines.entries()) {
            const span = svgNode('tspan', { x: cardX + 13, dy: index ? 17 : 0 });
            span.textContent = line;
            titleText.append(span);
          }
          group.append(titleText);
        }
        const select = () => {
          this.showDetail(item);
          if (item.kind === 'Aggregate') {
            if (item.occurred.lower && item.occurred.upper) {
              this.range = [item.occurred.lower.slice(0, 10), item.occurred.upper.slice(0, 10)];
              this.persist(); this.reload();
            }
            return;
          }
          this.highlightRef = item.ref; this.renderGraph(); this.updateTableHighlight();
          const row = [...byId('timeline-rows').querySelectorAll('tr[data-key]')]
            .find(entry => entry.dataset.key === entryKey(item));
          row?.querySelector('.timeline-event')?.focus();
        };
        group.addEventListener('click', select);
        group.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); select(); } });
        graph.append(group);
      }
      if (this.scrollAnchor !== null) {
        byId('timeline-graph-wrap').scrollLeft = x(this.scrollAnchor) - visibleWidth / 2;
        this.scrollAnchor = null;
      }
    }

    renderTable() {
      const body = byId('timeline-rows'); body.textContent = '';
      const rows = document.createDocumentFragment();
      const all = this.tableItems;
      const localNow = ['Set', 'DateOnly'].includes(this.clock?.status)
        ? window.WritingVaultStoryDates.clockLocalDateTime(this.clock) : null;
      const nowValue = dateValue(localNow);
      const showNow = nowValue !== null && (!this.range ||
        (localNow.slice(0, 10) >= this.range[0] && localNow.slice(0, 10) <= this.range[1]));
      byId('timeline-order-note').hidden = !all.some(uncertainDate);
      const tracks = this.rangeTracks(all);
      const spanByKey = new Map(tracks.spans.map(span => [span.key, span]));
      const trackWidth = Math.max(56, tracks.lanes * 18 + 38);
      byId('timeline-table').style.setProperty('--range-gutter-width', `${trackWidth}px`);
      const starts = new Map();
      for (const span of tracks.spans) {
        const index = span.start ?? 0;
        if (!starts.has(index)) starts.set(index, []);
        starts.get(index).push(span);
      }
      let activeSpans = [];
      let previousDate = null;
      let nowInserted = false;
      const appendDateParts = (row, date) => {
        const year = node('td', date.year, 'timeline-calendar-year');
        year.append(node('span', `Full date: ${date.full.description}`, 'sr-only'));
        const month = node('td', date.month, 'timeline-calendar-month');
        const dayCell = node('td', date.day, 'timeline-calendar-day');
        if (date.time) dayCell.append(node('small', date.time, 'timeline-date-time'));
        if (date.full.context) dayCell.append(node('small', date.full.context, 'timeline-date-context'));
        row.append(year, month, dayCell);
      };
      const appendNow = (index, crossingSpans) => {
        const row = node('tr', undefined, 'timeline-now-row');
        row.setAttribute('aria-label', `Now: ${window.WritingVaultStoryDates.clockTime(this.clock)}`);
        row.append(this.rangeGutter(all, crossingSpans, index - .5, trackWidth));
        const dateOnly = this.clock?.status === 'DateOnly';
        const date = window.WritingVaultStoryDates.chronologyDate({ kind: dateOnly ? 'ExactDate' : 'ExactInstant',
          lower: dateOnly ? localNow.slice(0, 10) : localNow,
          upper: dateOnly ? localNow.slice(0, 10) : localNow, calendarId: 'Gregorian' }, previousDate);
        previousDate = date.next;
        appendDateParts(row, date);
        row.append(node('td', dateOnly ? 'Story day' : 'Clock', 'timeline-type-cell'));
        const title = node('td');
        title.append(node('strong', dateOnly ? 'Now · all day' : 'Now', 'timeline-now-table-title'),
          node('small', dateOnly ? 'Time is unspecified; calculations cover the whole day' : this.mode === 'Narrative'
            ? `Clock reference · ${this.clock.referenceTimeZoneId || 'UTC'}; narrative order is unchanged`
            : `Clock reference · ${this.clock.referenceTimeZoneId || 'UTC'}`));
        row.append(title, node('td'));
        rows.append(row);
        nowInserted = true;
      };
      for (const [index, item] of all.entries()) {
        activeSpans = activeSpans.filter(span => (span.end ?? all.length - 1) >= index);
        const itemTime = bounds({ occurred: item.boundaryDate || item.occurred })[0];
        if (showNow && !nowInserted &&
            (this.mode === 'Narrative' || itemTime !== null && itemTime >= nowValue))
          appendNow(index, activeSpans);
        if (starts.has(index)) activeSpans.push(...starts.get(index));
        const row = node('tr');
        row.dataset.ref = item.ref;
        row.dataset.key = entryKey(item);
        if (item.boundary) row.classList.add(`timeline-boundary-${item.boundary.toLowerCase()}`);
        if (uncertainDate(item)) row.classList.add('timeline-uncertain');
        if (this.highlightRef && intersects(item, this.highlightRef)) row.classList.add('highlighted');
        row.append(this.rangeGutter(all, activeSpans, index, trackWidth));
        const date = window.WritingVaultStoryDates.chronologyDate(item.boundaryDate || item.occurred, previousDate);
        previousDate = date.next;
        if (date.kind === 'parts') {
          appendDateParts(row, date);
        } else {
          const period = node('td', undefined, 'timeline-period-cell'); period.colSpan = 3;
          this.appendDate(period, item, item.boundaryDate);
          if (uncertainDate(item)) {
            const isSpan = ['Range', 'UncertainRange'].includes(item.occurred.kind);
            const explanation = isSpan ? 'Period; nearby events may overlap' :
              'Date is uncertain; relative order may be unknown';
            const cue = node('span', isSpan ? '↔' : '≈', 'timeline-uncertainty-mark');
            cue.setAttribute('role', 'img');
            cue.setAttribute('aria-label', explanation);
            cue.title = explanation;
            period.append(cue);
          }
          row.append(period);
        }
        row.append(node('td', chronologyType(item), 'timeline-type-cell'));
        const title = node('td');
        const membershipTransition = item.isMembershipTransition;
        if (item.boundary && !membershipTransition) title.append(node('span', item.boundary === 'Start' ? 'Start' :
          item.boundary === 'End' ? 'End' : 'In progress', 'timeline-boundary-label'));
        const displayTitle = item.boundary === 'End' && !membershipTransition
          ? `End of ${item.title}` : item.title;
        const open = node('button', displayTitle, 'timeline-event'); open.type = 'button';
        open.addEventListener('click', () => this.openRecord(item.ref)); title.append(open);
        const locate = node('button', undefined, 'timeline-locate'); locate.type = 'button';
        locate.setAttribute('aria-label', `Locate ${item.title} on the graphical timeline`);
        locate.title = `Locate ${item.title} on the graphical timeline`;
        const icon = svgNode('svg', { viewBox: '0 0 24 24', 'aria-hidden': 'true', focusable: 'false' });
        icon.append(svgNode('path', { d: 'M3 19V5M3 19h18M7 15l4-5 4 2 5-7' }));
        icon.append(svgNode('circle', { cx: 20, cy: 5, r: 1.4 }));
        locate.append(icon);
        locate.addEventListener('click', () => {
          this.highlightRef = item.ref; this.showDetail(item); this.renderGraph(); this.updateTableHighlight();
          const mark = [...byId('timeline-graph').querySelectorAll('.timeline-mark')]
            .find(element => element.getAttribute('data-key') === entryKey(item));
          mark?.focus();
        });
        title.append(locate);
        if (!membershipTransition && (item.boundary === 'Start' || item.boundary === 'Ongoing')) {
          const full = item.kind === 'Project' ? { description: item.occurred.display } : window.WritingVaultStoryDates.describe(item.occurred);
          title.append(node('small', [full.description, full.duration, full.context].filter(Boolean).join(' '), 'timeline-range-full-date'));
        }
        if (item.recurrence) title.append(node('small', this.repeatText(item), 'timeline-repeat'));
        for (const warning of item.warnings || []) title.append(node('small', warning));
        if (item.boundary !== 'End' && item.kind !== 'Project' && item.summary) title.append(node('small', item.summary));
        const span = item.boundary ? spanByKey.get(this.rangeIdentity(item)) : null;
        if (this.range && span && item.boundary === 'Start' && span.end === null)
          title.append(node('small', 'End is outside the selected dates.'));
        if (this.range && span && item.boundary === 'End' && span.start === null)
          title.append(node('small', 'Start is outside the selected dates.'));
        row.append(title);
        if (item.boundary !== 'End') for (const warning of item.warnings || []) title.append(node('small', warning));
        const connected = node('td');
        for (const link of item.boundary === 'End' && !membershipTransition ? [] : item.related || []) {
          const wrap = node('span', undefined, 'timeline-connection');
          const button = node('button', link.label, 'timeline-entity-link'); button.type = 'button';
          button.dataset.ref = link.ref;
          button.setAttribute('aria-label', `Highlight all timeline entries for ${link.label}`);
          button.addEventListener('click', () => this.highlightEntity(link.ref, item.ref));
          wrap.append(button);
          if (link.associationRole) wrap.append(node('small', link.associationRole));
          if (link.associationNotes) wrap.append(node('small', link.associationNotes));
          connected.append(wrap);
        }
        row.append(connected); rows.append(row);
      }
      if (showNow && !nowInserted) appendNow(all.length,
        activeSpans.filter(span => (span.end ?? all.length - 1) >= all.length));
      if (!all.length) { const row = node('tr'); const cell = node('td', 'No dated entries match these filters.'); cell.colSpan = 7; row.append(cell); rows.append(row); }
      body.append(rows);
      byId('timeline-table-count').textContent = `${all.length.toLocaleString()} dated entries${showNow ? ' · Now shown' : ''}`;
      const undatedHost = byId('timeline-undated-list'); undatedHost.textContent = '';
      for (const item of this.undated) {
        const button = node('button', undefined, 'timeline-undated-item'); button.type = 'button';
        button.dataset.ref = item.ref;
        button.append(node('span', item.label, 'timeline-undated-label'),
          node('span', humanType(item), 'timeline-undated-type'));
        if (this.highlightRef === item.ref) button.classList.add('highlighted');
        button.addEventListener('click', () => this.openRecord(item.ref)); undatedHost.append(button);
      }
      byId('timeline-undated').hidden = !this.undated.length;
    }

    updateTableHighlight() {
      const rows = byId('timeline-rows').querySelectorAll('tr[data-ref]');
      for (const [index, item] of this.tableItems.entries())
        rows[index]?.classList.toggle('highlighted', Boolean(this.highlightRef && intersects(item, this.highlightRef)));
      for (const button of byId('timeline-undated-list').querySelectorAll('.timeline-undated-item'))
        button.classList.toggle('highlighted', button.dataset.ref === this.highlightRef);
    }

    rangeTracks(items) {
      const byRef = new Map();
      for (const [index, item] of items.entries()) {
        if (item.boundary !== 'Start' && item.boundary !== 'End') continue;
        // Membership joins/leaves are separate transitions, not the two ends
        // of a dated Range. Do not draw a spurious connecting arrow track.
        if (item.isMembershipTransition ||
            item.occurred?.kind !== 'KnownRange') continue;
        const key = this.rangeIdentity(item);
        const pair = byRef.get(key) || { key, start: null, end: null, lane: 0,
          fuzzy: uncertainDate(item) && !unknownRange(item), unknown: unknownRange(item) };
        pair[item.boundary.toLowerCase()] = index;
        byRef.set(key, pair);
      }
      const spans = [...byRef.values()].sort((a, b) => (a.start ?? 0) - (b.start ?? 0) ||
        (b.end ?? items.length - 1) - (a.end ?? items.length - 1));
      const laneEnds = [];
      for (const span of spans) {
        const start = span.start ?? 0, end = span.end ?? items.length - 1;
        let lane = laneEnds.findIndex(last => last < start);
        if (lane < 0) lane = laneEnds.length;
        laneEnds[lane] = end;
        span.lane = lane;
      }
      return { spans, lanes: laneEnds.length };
    }

    rangeIdentity(item) {
      // One record can contribute several dated occurrences (for example a
      // character's birth and death). Pair boundaries by occurrence, not owner.
      return JSON.stringify([item.ref, item.kind, item.lane, item.title,
        item.occurred?.kind, item.occurred?.lower, item.occurred?.upper]);
    }

    rangeGutter(items, active, index, width) {
      const cell = node('td', undefined, 'timeline-range-gutter');
      if (!active.length) return cell;
      for (const span of active) {
        const x = 12 + span.lane * 18;
        const starts = span.start === index, ends = span.end === index;
        const bar = node('span', undefined,
          `timeline-range-bar ${span.unknown ? 'timeline-range-checkered' :
            span.fuzzy ? 'timeline-range-striped' : 'timeline-range-solid'}`);
        bar.style.left = `${x}px`;
        // Boundary tips share a fixed offset beside the first line of text.
        // Only the bar stretches when a row wraps onto additional lines.
        bar.style.top = starts ? 'calc(var(--range-boundary-y) + 10px)' : '0';
        bar.style.bottom = ends ? 'calc(100% - var(--range-boundary-y) + 10px)' : '0';
        cell.append(bar);
        if (starts || ends) {
          const arrowWidth = width - x - 22;
          const arrow = svgNode('svg', { viewBox: `0 0 ${arrowWidth} 10`,
            class: `timeline-range-arrow ${starts ? 'timeline-range-arrow-start' : 'timeline-range-arrow-end'}`,
            'aria-hidden': 'true', focusable: 'false' });
          arrow.style.left = `${x - 4.5}px`;
          arrow.style.width = `${arrowWidth}px`;
          // Extend the horizontal arm to a shared right edge, keeping the
          // diagonal head the same size regardless of the stem's lane.
          arrow.append(svgNode('path', { d: starts ?
            `M 0 0 H ${arrowWidth} L ${arrowWidth - 13} 10 H 0 Z` :
            `M 0 10 H ${arrowWidth} L ${arrowWidth - 13} 0 H 0 Z` }));
          cell.append(arrow);
        }
      }
      const here = items[index];
      if (!here?.isMembershipTransition &&
          (here?.boundary === 'Start' || here?.boundary === 'End'))
        cell.append(node('span', `${here.boundary} of ${here.title}`, 'sr-only'));
      return cell;
    }

    showDetail(item) {
      const detail = byId('timeline-detail'); detail.hidden = false;
      byId('timeline-detail-title').textContent = item.title;
      this.appendDate(byId('timeline-detail-date'), item);
      byId('timeline-detail-summary').textContent = [item.summary || (item.kind === 'Aggregate'
        ? 'A density cluster. The date selection narrows the table so you can inspect its entries.' : ''),
        item.recurrence ? this.repeatText(item) : '', ...(item.warnings || [])].filter(Boolean).join(' ');
      const related = byId('timeline-detail-related'); related.textContent = '';
      if (item.kind !== 'Aggregate') {
        const open = node('button', 'Open record', 'quiet-button'); open.type = 'button';
        open.addEventListener('click', () => this.openRecord(item.ref)); related.append(open);
        const copy = node('button', 'Copy Vault reference', 'quiet-button'); copy.type = 'button';
        copy.addEventListener('click', () => navigator.clipboard.writeText(
          `${item.title} · ${humanType(item)} · ${byId('continuity').value}\n${item.ref}`));
        related.append(copy);
      }
      for (const link of (item.related || []).slice(0, 20)) {
        const button = node('button', link.label, 'quiet-button'); button.type = 'button';
        button.addEventListener('click', () => this.openRecord(link.ref)); related.append(button);
      }
    }

    repeatText(item) {
      const repeat = item.recurrence;
      const unit = { Daily: 'day', Weekly: 'week', Monthly: 'month', Yearly: 'year' }[repeat.frequency];
      const schedule = repeat.interval === 1 ? `Repeats every ${unit}` : `Repeats every ${repeat.interval} ${unit}s`;
      return schedule + (repeat.until ? ` through ${repeat.until}.` : '.');
    }

    dateText(item) {
      const date = item.kind === 'Project' ? { description: item.occurred.display } : window.WritingVaultStoryDates.describe(item.occurred);
      return [date.description, item.kind === 'Aggregate' ? null : date.duration, date.context].filter(Boolean).join(' ');
    }

    appendDate(host, item, dateOverride = null) {
      const date = dateOverride ? window.WritingVaultStoryDates.describe(dateOverride) : item.kind === 'Project' ? { description: item.occurred.display } : window.WritingVaultStoryDates.describe(item.occurred);
      host.textContent = date.description;
      if (item.kind !== 'Aggregate' && date.duration)
        host.append(node('small', date.duration, 'timeline-date-duration'));
      if (date.context) host.append(node('small', date.context, 'timeline-date-context'));
    }

    dispose() { this.generation++; this.graphGeneration++; this.controller?.abort(); this.graphController?.abort(); }
  }

  window.WritingVaultTimeline = Timeline;
})();
