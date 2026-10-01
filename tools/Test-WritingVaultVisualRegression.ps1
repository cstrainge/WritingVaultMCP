[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Database,
    [Parameter(Mandatory)][string] $BackupRoot,
    [string] $Server,
    [string] $Viewer,
    [string] $Continuity = 'Lostville Preview',
    [string] $Address = 'http://127.0.0.1:5285',
    [switch] $UseExistingViewer,
    [switch] $CookieOnly,
    [switch] $DateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $Server) { $Server = Join-Path $PSScriptRoot '..\bin\Debug\net10.0-windows\WritingVaultMcp.dll' }
if (-not $Viewer) { $Viewer = Join-Path $PSScriptRoot '..\WritingVault.Web\bin\Debug\net10.0-windows\WritingVault.Web.dll' }
$address = $Address.TrimEnd('/')
$port = ([Uri]$address).Port
if ($address -ne "http://127.0.0.1:$port" -or $port -notin @(5284, 5285)) {
    throw 'The visual check requires a fixed loopback viewer address on port 5284 or 5285.'
}
$capture = Join-Path $PSScriptRoot 'Capture-WritingVaultPreview.ps1'
$outputRoot = Join-Path $PSScriptRoot '..\artifacts\visual-regression'
$databasePath = [IO.Path]::GetFullPath($Database)
$backupPath = [IO.Path]::GetFullPath($BackupRoot)
$serverPath = [IO.Path]::GetFullPath($Server)
$viewerPath = [IO.Path]::GetFullPath($Viewer)
foreach ($path in @($databasePath, $serverPath, $viewerPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required test input is missing: $path" }
}
if (-not (Test-Path -LiteralPath $backupPath -PathType Container)) {
    throw 'The disposable fixture backup directory is missing.'
}
if (-not $UseExistingViewer -and (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)) {
    throw "Port $port is occupied; the visual check will not attach to an unknown viewer."
}

$route = "$address/c/$([Uri]::EscapeDataString($Continuity))"
$process = $null
try {
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    if (-not $UseExistingViewer) {
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = 'C:\Program Files\dotnet\dotnet.exe'
        # Windows PowerShell 5.1 runs on .NET Framework, where ProcessStartInfo has no ArgumentList.
        $start.Arguments = (@($viewerPath, '--server', $serverPath, '--database', $databasePath,
                '--backup-root', $backupPath) | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($start)
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $ready = $false
    while (-not $ready -and [DateTime]::UtcNow -lt $deadline) {
        if ($null -ne $process -and $process.HasExited) { throw "The Debug viewer exited with code $($process.ExitCode)." }
        try { $ready = (Invoke-WebRequest -UseBasicParsing -Uri $address -TimeoutSec 2).StatusCode -eq 200 }
        catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) { throw 'The Debug viewer did not become ready on the fixed port.' }
    Start-Sleep -Milliseconds 300
    if ($null -ne $process -and $process.HasExited) { throw "A different viewer already owns port $port; the disposable Debug viewer exited." }
    $expectedPage = [IO.File]::ReadAllBytes((Join-Path (Split-Path -Parent $viewerPath) 'wwwroot\index.html'))
    $servedPage = (Invoke-WebRequest -UseBasicParsing -Uri "$address/?probe=visual-regression" -TimeoutSec 5).RawContentStream.ToArray()
    if ([Convert]::ToBase64String($servedPage) -cne [Convert]::ToBase64String($expectedPage)) {
        throw "Port $port is serving a different viewer build; the visual check will not use it."
    }
    foreach ($asset in @('app.js', 'record-pages.js', 'story-dates.js', 'timeline.js', 'app.css')) {
        $expectedAsset = [IO.File]::ReadAllBytes((Join-Path (Split-Path -Parent $viewerPath) "wwwroot\$asset"))
        $servedAsset = (Invoke-WebRequest -UseBasicParsing -Uri "$address/$asset" -TimeoutSec 5).RawContentStream.ToArray()
        if ([Convert]::ToBase64String($servedAsset) -cne [Convert]::ToBase64String($expectedAsset)) {
            throw "Port $port is serving a different $asset build; the visual check will not use it."
        }
    }

    if (-not $CookieOnly) {
    if (-not $DateOnly) {
    $overview = @'
(async () => {
  const box = selector => document.querySelector(selector).getBoundingClientRect();
  const visible = selector => !document.querySelector(selector).hidden;
  for (let attempt = 0; !document.querySelector('#continuity-note-list .note-card .record-prose')?.textContent.includes('Time in Lostville') && attempt < 40; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  const sidebar = box('.sidebar'), workspace = box('.workspace'), hero = box('#overview .hero');
  if (!visible('#overview') || !document.querySelector('#overview-title').textContent.includes('Lostville')) throw Error('Overview did not load.');
  if (sidebar.width < 190 || workspace.left < sidebar.right - 2 || hero.width < 600) throw Error('Desktop shell geometry changed.');
  if (document.documentElement.scrollWidth > innerWidth + 2) throw Error('Desktop shell overflows horizontally.');
  if (getComputedStyle(document.body).fontFamily.toLowerCase().includes('times new roman')) throw Error('Viewer typography fell back to a browser default.');
  if (!document.activeElement.classList.contains('skip-link')) throw Error('First keyboard Tab did not reach Skip to content.');
  if (document.querySelector('.pill') || /read[- ]only/i.test(document.querySelector('.brand').textContent) ||
      /read[- ]only/i.test(document.querySelector('#overview').textContent))
    throw Error('Redundant read-only badge or label remains visible.');
  const note = document.querySelector('#continuity-note-list .note-card');
  if (!note || note.querySelectorAll('.note-card-title').length !== 1 ||
      note.textContent.split('Timeline rule').length !== 2 ||
      document.querySelectorAll('#continuity-note-list > .result').length)
    throw Error('Continuity note did not render as one card with one title.');
  return { view: 'overview', sidebar: Math.round(sidebar.width), hero: Math.round(hero.width) };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'overview-desktop.png') -Url $route `
        -WaitSeconds 15 -KeySequence @('Tab') -ProbeExpression $overview | Out-Host

    $sourcesNavigation = @'
(async () => {
  const sourceLink = document.querySelector('.primary-nav [data-view="Source"]');
  if (!sourceLink?.querySelector('.nav-icon svg path') ||
      sourceLink.querySelector('.nav-label')?.textContent !== 'Sources')
    throw Error('Sources navigation is missing its document icon or label.');
  sourceLink.click();
  for (let attempt = 0; document.querySelector('#results').hidden && attempt < 80; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  if (document.querySelector('#results').hidden ||
      document.querySelector('#results-title').textContent !== 'Sources')
    throw Error('The Sources heading included its icon text or did not load.');
  return { view: 'sources navigation', heading: 'Sources', icon: 'document' };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'sources-navigation.png') -Url $route `
        -WaitSeconds 10 -ProbeExpression $sourcesNavigation | Out-Host

    $organizationsNavigation = @'
(async () => {
  const link = document.querySelector('.primary-nav [data-view="Organization"]');
  if (link?.querySelectorAll('.nav-icon svg circle').length !== 3 ||
      link.querySelector('.nav-label')?.textContent !== 'Organizations')
    throw Error('Organizations navigation is missing its group icon or label.');
  link.click();
  for (let attempt = 0; document.querySelector('#results').hidden && attempt < 80; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  if (document.querySelector('#results').hidden ||
      document.querySelector('#results-title').textContent !== 'Organizations')
    throw Error('The Organizations heading included its icon text or did not load.');
  return { view: 'organizations navigation', heading: 'Organizations', icon: 'group' };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'organizations-navigation.png') -Url $route `
        -WaitSeconds 10 -ProbeExpression $organizationsNavigation | Out-Host

    $timeline = @'
(() => {
  const graph = document.querySelector('#timeline-graph').getBoundingClientRect();
  const scroller = document.querySelector('#timeline-graph-wrap');
  const table = document.querySelector('#timeline-table').getBoundingClientRect();
  const rows = document.querySelectorAll('#timeline-rows tr').length;
  const marks = document.querySelectorAll('#timeline-graph .timeline-mark').length;
  if (document.querySelector('#timeline').hidden || rows < 1 || marks < 1) throw Error('Timeline fixture did not render.');
  if (document.querySelector('#timeline-more, #timeline-previous, #timeline-undated-more, #timeline-undated-previous') ||
      document.querySelector('#timeline-table-count').textContent.includes('Page '))
    throw Error('Timeline table still exposes pagination.');
  const total = Number(document.querySelector('#timeline-table-count').textContent.match(/[\d,]+/)?.[0].replace(/,/g, ''));
  if (!Number.isInteger(total) || total !== rows) throw Error('Timeline table omits selected dated entries.');
  if (!document.querySelector('#timeline-graph .timeline-card') ||
      !document.querySelector('#timeline-graph .timeline-stem') ||
      !document.querySelector('#timeline-graph .timeline-axis-band') ||
      !document.querySelector('#timeline-graph .timeline-axis-label'))
    throw Error('The card-and-ruler timeline did not render.');
  for (const mark of document.querySelectorAll('#timeline-graph .timeline-mark:not(.compact)')) {
    const card = mark.querySelector('.timeline-card').getBBox();
    for (const label of mark.querySelectorAll('.timeline-card-title, .timeline-card-category')) {
      const text = label.getBBox();
      if (text.x < card.x + 7 || text.x + text.width > card.x + card.width - 7 ||
          text.y < card.y + 6 || text.y + text.height > card.y + card.height - 6)
        throw Error(`Timeline label escapes its card: ${label.textContent}`);
    }
  }
  if (graph.width < 600 || table.top <= graph.bottom || table.width < 600) throw Error('Timeline graph/table geometry changed.');
  if (document.documentElement.scrollWidth > innerWidth + 2) throw Error('Desktop timeline overflows horizontally.');
  if (scroller.scrollWidth <= scroller.clientWidth * 1.5) throw Error('Timeline dates were squeezed into one screen.');
  scroller.focus();
  scroller.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
  if (scroller.scrollLeft < 100) throw Error('Focused timeline cannot scroll with the right arrow key.');
  scroller.scrollLeft = (scroller.scrollWidth - scroller.clientWidth) / 2;
  if (scroller.scrollLeft < 100 || document.querySelectorAll('#timeline-rows tr').length !== rows)
    throw Error('Horizontal scrolling changed the integrated table.');
  return { view: 'timeline', rows, marks, graph: Math.round(graph.width), scrolled: Math.round(scroller.scrollLeft) };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'timeline-desktop.png') -Url "$route#timeline" -WaitSeconds 15 -ProbeExpression $timeline | Out-Host

    $nowMarker = @'
(async () => {
  const storyDates = window.WritingVaultStoryDates;
  const clock = { status: 'Set', source: 'session', currentTime: '2021-09-08T15:45:00-07:00', referenceTimeZoneId: 'America/Vancouver' };
  const continuityClock = { status: 'Set', source: 'continuity', currentTime: '2021-09-08T22:45:00Z', referenceTimeZoneId: 'America/Vancouver' };
  if (storyDates.clockLocalDateTime(clock) !== '2021-09-08T15:45:00' ||
      storyDates.clockLocalDateTime(continuityClock) !== '2021-09-08T15:45:00')
    throw Error('The Now marker did not preserve the selected local story time.');
  const event = { ref: 'event:now-probe', kind: 'WorldEvent', lane: 'WorldEvents', title: 'Afternoon event', related: [],
    occurred: { kind: 'ExactInstant', lower: '2021-09-08T12:00:00', upper: '2021-09-08T12:00:00' } };
  const timeline = new window.WritingVaultTimeline(async () => ({ items: [event], observedRevision: 'now-probe', hasMore: false }), () => {});
  timeline.graphItems = [event]; timeline.revision = 'now-probe'; timeline.clock = clock;
  timeline.viewport = [Date.UTC(2021, 8, 8), Date.UTC(2021, 8, 9)]; timeline.renderGraph();
  const graph = document.querySelector('#timeline-graph');
  let marker = graph.querySelector('.timeline-now-marker');
  if (!marker || !document.querySelector('#timeline-now-outside').hidden || marker.getAttribute('role') !== 'button')
    throw Error('The highlighted Now card is missing or inaccessible.');
  const card = marker.querySelector('.timeline-now-card').getBBox();
  const eventCard = graph.querySelector('.timeline-card').getBBox();
  if (card.y + card.height > eventCard.y) throw Error('Now overlaps an event card.');
  for (const label of marker.querySelectorAll('.timeline-now-heading, .timeline-now-date, .timeline-now-zone')) {
    const box = label.getBBox();
    if (box.x < card.x + 6 || box.x + box.width > card.x + card.width - 6 ||
        box.y < card.y + 3 || box.y + box.height > card.y + card.height - 3)
      throw Error(`Text escapes the Now card: ${label.textContent}`);
  }
  const expectedX = 56 + (graph.viewBox.baseVal.width - 104) * 15.75 / 24;
  if (Math.abs(Number(marker.querySelector('.timeline-now').getAttribute('x1')) - expectedX) > 1)
    throw Error('Now was placed at midnight instead of the selected hour.');
  timeline.viewport = [Date.UTC(2021, 7, 1), Date.UTC(2021, 7, 10)]; timeline.renderGraph();
  const outside = document.querySelector('#timeline-now-outside');
  if (outside.hidden || !outside.textContent.includes('later')) throw Error('The out-of-window Now control is missing.');
  outside.click(); await new Promise(resolve => setTimeout(resolve, 100));
  if (!graph.querySelector('.timeline-now-marker') || !outside.hidden) throw Error('The Now control did not jump to story time.');
  timeline.clock = null; timeline.renderGraph();
  if (graph.querySelector('.timeline-now-marker') || !outside.hidden) throw Error('Unset time still has a positioned Now card.');
  timeline.clock = clock; timeline.viewport = [Date.UTC(2021, 8, 8), Date.UTC(2021, 8, 9)]; timeline.renderGraph();
  return { view: 'Now marker', localTime: storyDates.clockLocalDateTime(clock), jump: true };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'timeline-now.png') -Url "$route#timeline" -WaitSeconds 15 -ProbeExpression $nowMarker | Out-Host

    $groupedTimeline = @'
(() => {
  const layout = document.querySelector('#timeline-layout');
  const graph = document.querySelector('#timeline-graph');
  const rows = document.querySelectorAll('#timeline-rows tr').length;
  const groups = [...graph.querySelectorAll('.timeline-group-label')];
  const baselines = [...graph.querySelectorAll('.timeline-group-line')];
  if (layout.value !== 'Grouped' || groups.length < 2 || baselines.length !== groups.length)
    throw Error('Grouped timeline did not restore and separate record types.');
  const positions = baselines.map(line => Number(line.getAttribute('y1')));
  if (positions.some((value, index) => index && value <= positions[index - 1]))
    throw Error('Grouped timeline baselines overlap or run out of order.');
  if (!location.search.includes('timelineLayout=Grouped') || rows < 1)
    throw Error('Grouped layout lost URL state or chronology rows.');
  return { view: 'grouped timeline', groups: groups.map(group => group.textContent), rows };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'timeline-grouped.png') `
        -Url "${route}?timelineLayout=Grouped#timeline" -WaitSeconds 15 -ProbeExpression $groupedTimeline | Out-Host

    $longTimeline = @'
(() => {
  const scroller = document.querySelector('#timeline-graph-wrap');
  const graph = document.querySelector('#timeline-graph');
  if (document.querySelector('#timeline').hidden || !graph.querySelector('.timeline-mark') ||
      graph.viewBox.baseVal.width !== 16000 || scroller.scrollWidth !== 16000)
    throw Error('A long date span did not render as a bounded scrollable graph.');
  scroller.scrollLeft = (scroller.scrollWidth - scroller.clientWidth) * .8;
  if (scroller.scrollLeft < 1000 || document.documentElement.scrollWidth > innerWidth + 2)
    throw Error('The long graph cannot scroll without overflowing the page.');
  return { view: 'long timeline', canvas: scroller.scrollWidth, scrolled: Math.round(scroller.scrollLeft) };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'timeline-long-span.png') `
        -Url "${route}?timelineViewFrom=1900-01-01&timelineViewTo=2050-01-01#timeline" `
        -WaitSeconds 15 -ProbeExpression $longTimeline | Out-Host
    }

    $dates = @'
(() => {
  const format = window.WritingVaultStoryDates.describe;
  const sameDay = format({ kind: 'KnownRange', lower: '2026-09-05T08:00:00',
    upper: '2026-09-05T22:30:00', lowerInclusive: true, upperInclusive: true });
  if (sameDay.description !== 'From 8:00am to 10:30pm on Saturday September 5th, 2026.' ||
      sameDay.duration !== 'Over 14 hours and 30 minutes.') throw Error('Single-day timed range changed.');
  const multiDay = format({ kind: 'KnownRange', lower: '2026-09-05T08:00:00',
    upper: '2026-09-08T22:30:00', lowerInclusive: true, upperInclusive: true });
  if (multiDay.description !== 'From 8:00am on Saturday the 5th to 10:30pm on Tuesday the 8th in September 2026.' ||
      multiDay.duration !== 'Over 3 days, 14 hours and 30 minutes.') throw Error('Multi-day timed range changed.');
  const untimed = format({ kind: 'KnownRange', lower: '2026-09-05', upper: '2026-09-08',
    lowerInclusive: true, upperInclusive: true });
  if (untimed.description !== 'From Saturday the 5th to Tuesday the 8th in September 2026.' ||
      untimed.duration !== 'Over 3 days.') throw Error('Date-only range gained a clock time or wrong duration.');
  const exclusive = format({ kind: 'KnownRange', lower: '2026-09-05', upper: '2026-09-08',
    lowerInclusive: true, upperInclusive: false });
  if (exclusive.description !== 'From Saturday the 5th to before Tuesday the 8th in September 2026.' ||
      exclusive.duration !== 'Over 3 days.') throw Error('An exclusive date-only boundary was presented as inclusive.');
  const exact = format({ kind: 'ExactDate', lower: '2026-09-05', upper: '2026-09-06' });
  if (exact.description !== 'Saturday September 5th, 2026' || exact.duration)
    throw Error('An exact calendar date was presented as a timed event.');
  const raw = window.WritingVaultStoryDates.fromFields({ eventKind: 'KnownRange',
    eventLowerBound: '2026-09-05T00:00:00', eventUpperBound: '2026-09-08T00:00:00',
    eventLowerInclusive: true, eventUpperInclusive: true }, 'event');
  if (raw.description !== untimed.description) throw Error('Access midnight bounds leaked as clock times.');
  const fog = Array.from(document.querySelectorAll('#timeline-rows tr'))
    .filter(row => row.textContent.includes('The fog week'));
  if (document.querySelectorAll('#timeline-table thead th').length !== 7 ||
      fog.length !== 1 || fog[0].classList.contains('timeline-boundary-start') ||
      fog[0].classList.contains('timeline-boundary-end') ||
      !fog[0].textContent.includes('Range meaning needs review') ||
      fog[0].querySelector('.timeline-range-arrow'))
    throw Error('An ambiguous legacy range was presented as a certain start or end.');
  if (!Array.from(document.querySelectorAll('.timeline-mark.unknown .timeline-period'))
        .some(bar => getComputedStyle(bar).fill.includes('#timeline-unknown-')))
    throw Error('An ambiguous legacy range lost its checkerboard graph pattern.');
  const proto = window.WritingVaultTimeline.prototype;
  const overlapRange = { kind: 'KnownRange', lower: '2026-09-01', upper: '2026-09-08' };
  const tracks = proto.rangeTracks([
    { ref: 'a', boundary: 'Start', occurred: overlapRange },
    { ref: 'b', boundary: 'Start', occurred: overlapRange },
    { ref: 'a', boundary: 'End', occurred: overlapRange },
    { ref: 'b', boundary: 'End', occurred: overlapRange }
  ]);
  if (tracks.lanes !== 2 || tracks.spans[0].lane === tracks.spans[1].lane)
    throw Error('Overlapping range arrows share a visual track.');
  const known = { ref: 'known', kind: 'WorldEvent', title: 'Known period',
    occurred: { kind: 'KnownRange', lower: '2026-09-01', upper: '2026-09-08' } };
  const fuzzy = { ref: 'fuzzy', kind: 'WorldEvent', title: 'Uncertain period',
    occurred: { kind: 'UncertainRange', lower: '2026-09-01', upper: '2026-09-08',
      originalText: 'sometime that week' } };
  const knownRows = [{ ...known, boundary: 'Start' }, { ...known, boundary: 'End' }];
  const fuzzyRows = [{ ...fuzzy, boundary: 'Start' }, { ...fuzzy, boundary: 'End' }];
  const knownSpan = proto.rangeTracks(knownRows).spans[0];
  const fuzzySpans = proto.rangeTracks(fuzzyRows).spans;
  const start = proto.rangeGutter(knownRows, [knownSpan], 0, 30);
  const end = proto.rangeGutter(knownRows, [knownSpan], 1, 30);
  const probeRow = document.createElement('tr');
  probeRow.append(start, end);
  document.querySelector('#timeline-rows').append(probeRow);
  const solidBar = start.querySelector('.timeline-range-solid');
  const solidFill = solidBar && getComputedStyle(solidBar).backgroundImage === 'none' &&
    getComputedStyle(solidBar).backgroundColor !== 'rgba(0, 0, 0, 0)';
  const halfHeads = start.querySelector('.timeline-range-arrow')?.getAttribute('d') ===
    'M 6 55 H 18 l -5 5' && end.querySelector('.timeline-range-arrow')?.getAttribute('d') ===
    'M 6 45 H 18 l -5 -5';
  const spacedBars = start.querySelector('.timeline-range-bar')?.style.top ===
    'calc(60% + 8px)' && end.querySelector('.timeline-range-bar')?.style.bottom ===
    'calc(60% + 8px)';
  probeRow.remove();
  if (!knownSpan || knownSpan.fuzzy || fuzzySpans.length || !solidFill ||
      !halfHeads || !spacedBars)
    throw Error('Only known intervals may have paired range arrows.');
  const sameOwner = proto.rangeTracks([
    { ref: 'character', title: 'birth', boundary: 'Start', occurred: { kind: 'KnownRange', lower: '2026-09-01', upper: '2026-09-10' } },
    { ref: 'character', title: 'death', boundary: 'Start', occurred: { kind: 'KnownRange', lower: '2026-09-02', upper: '2026-09-11' } },
    { ref: 'character', title: 'birth', boundary: 'End', occurred: { kind: 'KnownRange', lower: '2026-09-01', upper: '2026-09-10' } },
    { ref: 'character', title: 'death', boundary: 'End', occurred: { kind: 'KnownRange', lower: '2026-09-02', upper: '2026-09-11' } }
  ]);
  if (sameOwner.spans.length !== 2 || sameOwner.lanes !== 2)
    throw Error('Distinct ranges on one record were connected together.');
  const rowDate = window.WritingVaultStoryDates.chronologyDate;
  let previous = null;
  const grouped = [
    { kind: 'Year', lower: '1849-01-01' },
    { kind: 'ExactDate', lower: '1849-09-05' },
    { kind: 'ExactDate', lower: '1849-10-03' },
    { kind: 'ExactDate', lower: '1849-10-09' },
    { kind: 'ExactDate', lower: '1849-10-09' },
    { kind: 'Before', upper: '1922-08-22' },
    { kind: 'ExactDate', lower: '2021-09-08' },
    { kind: 'ExactInstant', lower: '2021-09-08T08:00:00' }
  ].map(input => { const result = rowDate(input, previous); previous = result.next; return result; });
  if (grouped[0].year !== '1849' || grouped[0].month || grouped[0].day ||
      grouped[1].year || grouped[1].month !== 'September' || grouped[1].day !== '5th' ||
      grouped[2].year || grouped[2].month !== 'October' || grouped[2].day !== '3rd' ||
      grouped[3].year || grouped[3].month || grouped[3].day !== '9th' ||
      grouped[4].year || grouped[4].month || grouped[4].day ||
      grouped[5].kind !== 'period' || grouped[5].full.description !== 'Before Tuesday August 22nd, 1922' ||
      grouped[6].year !== '2021' || grouped[7].year || grouped[7].month || grouped[7].day ||
      grouped[7].time !== '8:00am')
    throw Error('Chronology calendar grouping repeated or suppressed the wrong date component.');
  if (format({ kind: 'Year', lower: '2026-01-01' }).description !== '2026' ||
      format({ kind: 'Before', upper: '2026-09-05' }).duration ||
      format({ kind: 'Unknown' }).description !== 'Date unknown')
    throw Error('Fuzzy or unknown date precision was overstated.');
  if (format({ kind: 'Before', upper: '2026-09-08T22:30:00' }).description !==
      'Before 10:30pm on Tuesday September 8th, 2026')
    throw Error('A known time on an open-ended date was discarded.');
  if (window.WritingVaultStoryDates.clockTime({ currentTime: '2026-09-29T19:00:00Z',
      referenceTimeZoneId: 'America/Vancouver' }) !==
      '12:00pm on Tuesday September 29th, 2026 (America/Vancouver)')
    throw Error('The artificial story clock lost its local-time context.');
  return { view: 'date language', sameDay, multiDay, untimed, exact };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'timeline-date-language.png') -Url "$route#timeline" `
        -WaitSeconds 15 -ProbeExpression $dates | Out-Host
    if ($DateOnly) { return }

    $nowSetup = @'
(async () => {
  const waitFor = async (predicate) => {
    for (let attempt = 0; attempt < 120; attempt++) {
      if (predicate()) return;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw Error('The chronology did not finish updating.');
  };
  const count = document.querySelectorAll('#timeline-rows tr[data-ref]').length;
  document.querySelector('#timeline-clock').click();
  document.querySelector('#story-date').value = '2021-09-05';
  document.querySelector('#story-has-time').click();
  document.querySelector('#story-time').value = '12:30';
  document.querySelector('#story-zone').value = 'America/Vancouver';
  document.querySelector('#time-form').requestSubmit();
  await waitFor(() => document.querySelector('.timeline-now-row'));
  const rows = Array.from(document.querySelectorAll('#timeline-rows tr'));
  const now = rows.findIndex(row => row.classList.contains('timeline-now-row'));
  if (document.querySelectorAll('#timeline-rows tr[data-ref]').length !== count ||
      now < 0 || rows.filter(row => row.textContent.includes('The fog week')).length !== 1)
    throw Error('The full chronology lost entries or split an ambiguous range around Now.');
  document.querySelector('#timeline-range-from').value = '2021-09-08';
  document.querySelector('#timeline-range-to').value = '2021-09-09';
  document.querySelector('#timeline-apply-range').click();
  await waitFor(() => document.querySelector('#timeline-table-count').textContent !== 'Loading…' &&
    !document.querySelector('.timeline-now-row'));
  document.querySelector('#timeline-clear-range').click();
  await waitFor(() => document.querySelector('.timeline-now-row') &&
    document.querySelectorAll('#timeline-rows tr[data-ref]').length === count);
  document.querySelector('.timeline-now-row').scrollIntoView({ block: 'center' });
  return true;
})()
'@
    $nowCheck = @'
(() => {
  const row = document.querySelector('.timeline-now-row');
  if (!row || !row.textContent.includes('America/Vancouver') ||
      !document.querySelector('#timeline-table-count').textContent.includes('Now shown'))
    throw Error('Now is missing from the complete chronology outside the graph viewport.');
  return { view: 'complete chronology with Now', row: row.textContent.trim() };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'timeline-now-row.png') `
        -Url "${route}?timelineViewFrom=1900-01-01&timelineViewTo=1900-01-10#timeline" `
        -WaitSeconds 15 -ProbeSetupExpression $nowSetup -ProbeExpression $nowCheck | Out-Host

    $eventSetup = @'
(async () => {
  for (let attempt = 0; !document.querySelector('#first-result') && attempt < 60; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  const row = Array.from(document.querySelectorAll('#result-list .result'))
    .find(item => item.textContent.includes('The fog week'));
  if (!row) throw Error('The fog week is missing from the disposable event fixture.');
  row.click();
  for (let attempt = 0; (document.querySelector('#record-title').textContent !== 'The fog week' ||
      !document.querySelector('#record-fields').textContent.includes('Range meaning needs review')) && attempt < 60; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  return true;
})()
'@
    $eventDate = @'
(() => {
  const details = document.querySelector('#record-fields');
  if (document.querySelector('#record-title').textContent !== 'The fog week' ||
      !details.textContent.includes('From Friday the 3rd to Tuesday the 7th in September 2021.') ||
      !details.textContent.includes('Range meaning needs review') ||
      details.textContent.includes('Over 4 days.') ||
      /event (lower|upper) bound/i.test(details.textContent))
    throw Error('The world-event page overstated an ambiguous legacy range.');
  return { view: 'event date', details: details.textContent };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'event-date-range.png') -Url "$route#events" `
        -WaitSeconds 15 -ProbeSetupExpression $eventSetup -ProbeExpression $eventDate | Out-Host

    $undatedSetup = @'
(async () => {
  for (let attempt = 0; !document.querySelector('#first-result') && attempt < 60; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  const row = Array.from(document.querySelectorAll('#result-list .result'))
    .find(item => item.textContent.includes('An undated account'));
  if (!row) throw Error('The undated event is missing from the disposable fixture.');
  row.click();
  for (let attempt = 0; (document.querySelector('#record-title').textContent !== 'An undated account' ||
      !document.querySelector('#record-fields').textContent.includes('An undated account')) && attempt < 60; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  return true;
})()
'@
    $undated = @'
(() => {
  const details = document.querySelector('#record-fields');
  if (document.querySelector('#record-title').textContent !== 'An undated account' ||
      /\b(Event|Unknown|Lower Bound|Upper Bound)\b/i.test(details.textContent) ||
      !details.textContent.includes('An undated account'))
    throw Error('An undated event still displays an empty or unknown date field.');
  return { view: 'undated event', details: details.textContent };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'event-date-unknown.png') -Url "$route#events" `
        -WaitSeconds 15 -ProbeSetupExpression $undatedSetup -ProbeExpression $undated | Out-Host

    $keyboard = @'
(() => {
  if (document.querySelector('#timeline-detail').hidden) throw Error('Enter did not open keyboard marker details.');
  if (!document.querySelector('#timeline-detail-title').textContent.trim()) throw Error('Keyboard marker details have no title.');
  return { view: 'timeline', keyboardMarker: true };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'timeline-keyboard-detail.png') -Url "$route#timeline" `
        -WaitSeconds 15 -ProbeSetupExpression "(() => { document.querySelector('#timeline-graph .timeline-mark').focus(); return true; })()" `
        -KeySequence @('Enter') -AfterClickWaitSeconds 1 -ProbeExpression $keyboard | Out-Host

    $record = @'
(async () => {
  document.documentElement.dataset.theme = 'dark';
  const visible = selector => !document.querySelector(selector).hidden;
  for (let attempt = 0; (document.querySelector('#record').hidden ||
      document.querySelector('#record-gallery').hidden ||
      !document.querySelector('#record-sections .note-document .record-prose')?.textContent.includes('The key') ||
      !document.querySelector('#record-history .history-entry')) && attempt < 40; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  const main = document.querySelector('#record').getBoundingClientRect();
  const menu = document.querySelector('#menu').getBoundingClientRect();
  const gallery = document.querySelector('#record-gallery');
  if (!visible('#record') || !document.querySelector('#record-title').textContent.trim()) throw Error('Character page did not load.');
  if (document.querySelector('#record-title').textContent !== 'Chlo\u00eb Bell' ||
      document.querySelector('#record-preferred').hidden ||
      document.querySelector('#record-preferred-name').textContent !== 'Chlo\u00eb' ||
      document.querySelector('#record-aka').hidden ||
      !document.querySelector('#record-aka-list').textContent.includes('The Clockkeeper') ||
      [...document.querySelectorAll('#record-sections h2')].some(heading => heading.textContent === 'Aliases') ||
      document.querySelector('#record-fields').textContent.includes('Preferred Name'))
    throw Error('Character full name, preferred name, or AKA header did not render correctly.');
  if (menu.width < 20 || main.width < 300 || main.right > innerWidth + 2) throw Error('Narrow record layout changed.');
  if (gallery.hidden || !gallery.querySelector('img')) throw Error('Character image did not render.');
  const actions = [...document.querySelectorAll('.record-actions .record-action')];
  if (actions.length !== 3 || actions.some(button => !button.querySelector('svg[aria-hidden="true"]') ||
      !button.getAttribute('aria-label') || button.dataset.tooltip !== button.getAttribute('aria-label')) ||
      actions.some(button => button.hidden)) throw Error('Record icon actions lost their labels or icons.');
  actions[1].focus();
  await new Promise(resolve => setTimeout(resolve, 250));
  if (getComputedStyle(actions[1], '::after').opacity !== '1' ||
      !getComputedStyle(actions[1], '::after').content.includes('Copy page link'))
    throw Error('The record action tooltip did not appear on keyboard focus.');
  const note = document.querySelector('#record-sections .note-document');
  if (!note || !note.textContent.includes('The key') ||
      note.textContent.split('What she remembers').length !== 2 ||
      note.closest('.record-section').querySelectorAll('.note-document').length !== 1 ||
      note.closest('.record-section').querySelectorAll('.result-list > .result').length)
    throw Error('Character notes did not render as one document with one title.');
  const age = document.querySelector('#record-main .age-section');
  if (!age || !age.textContent.includes('Story time is unset')) throw Error('Unset character age was not explained.');
  if (document.querySelector('#record-fields').textContent.includes('Observed Revision') ||
      document.querySelector('#record-fields').textContent.includes('Temporal Profile') ||
      document.querySelector('#record-fields').textContent.includes('BIRTH LOWER INCLUSIVE') ||
      /\b(Birth|Death)\b/.test(document.querySelector('#record-fields').textContent))
    throw Error('Internal age metadata or raw date storage leaked into details.');
  const details = document.querySelector('.record-detail-panel');
  const history = document.querySelector('#record-history');
  if (!history.querySelector('.history-entry') ||
      history.getBoundingClientRect().top - details.getBoundingClientRect().bottom < 16 ||
      getComputedStyle(details).borderTopWidth === '0px' || getComputedStyle(history).borderTopWidth === '0px')
    throw Error('History is not visually separated from Details on the narrow record page.');
  if (document.documentElement.scrollWidth > innerWidth + 2) throw Error('Narrow record page overflows horizontally.');
  return { view: 'character', width: Math.round(main.width), image: true, theme: 'dark' };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'character-mobile-dark.png') -Url "$route#characters" `
        -Width 390 -Height 844 -WaitSeconds 15 -ClickSelector '#first-result' `
        -AfterClickWaitSeconds 1 -ProbeExpression $record | Out-Host

    $maraSetup = @'
(async () => {
  const row = Array.from(document.querySelectorAll('#result-list .result')).find(item => item.textContent.includes('Mara Vale'));
  if (!row) throw Error('Mara Vale is missing from the disposable fixture.');
  row.click();
  for (let attempt = 0; document.querySelector('#record').hidden && attempt < 60; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  document.querySelector('#clock').click();
  document.querySelector('#story-date').value = '2026-09-29';
  document.querySelector('#story-has-time').click();
  document.querySelector('#story-time').value = '12:00';
  document.querySelector('#story-zone').value = 'America/Vancouver';
  document.querySelector('#time-form').requestSubmit();
  for (let attempt = 0; !document.querySelector('.age-unavailable') && attempt < 80; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  return true;
})()
'@
    $mara = @'
(() => {
  const age = document.querySelector('.age-section');
  const labels = Array.from(age?.querySelectorAll('dt') || []).map(item => item.textContent);
  if (document.querySelector('#record-title').textContent !== 'Mara Vale' ||
      labels.length || age.querySelector('.age-measures') ||
      age.querySelector('.age-unavailable')?.textContent !== 'Birth date unknown. Age cannot be calculated.')
    throw Error('Unknown age was repeated across four measures.');
  if (age.textContent.split('Birth date unknown').length !== 2 || age.textContent.includes('Temporal aging is disabled'))
    throw Error('Unknown age repeated its explanation or an irrelevant warning.');
  const fields = document.querySelector('#record-fields');
  if (fields.querySelectorAll('dt').length !== 1 || !fields.textContent.includes('Mara Vale') ||
      /\b(Birth|Death|Unknown)\b/i.test(fields.textContent))
    throw Error('Unknown character details remain visible.');
  const unset = window.WritingVaultRecordPages({}).isUnsetDetail;
  if (![null, undefined, '', '   ', 'Unknown', 'not set', [], {}].every(unset) ||
      [false, 0, 'Unknown origin', ['one']].some(unset))
    throw Error('Unset detail filtering dropped a meaningful value or kept a placeholder.');
  const renderer = window.WritingVaultRecordPages({ label: value => String(value) });
  const host = document.createElement('div');
  const same = { status: 'Alive', exactYears: 21, minimumYears: 21, maximumYears: 21 };
  const paused = { status: 'Exact', exactYears: 11, minimumYears: 11, maximumYears: 11 };
  const example = { status: 'Alive', calendar: same, legal: same, biological: same,
    experienced: same, warnings: ['Temporal aging is disabled; biological and experienced age equal calendar age.'] };
  renderer.renderAge(host, example);
  if (host.querySelector('.age-summary')?.textContent !== '21 years' || host.querySelector('.age-measures') ||
      host.querySelector('.age-warnings')) throw Error('Matching ages did not collapse to one age.');
  host.replaceChildren();
  renderer.renderAge(host, { ...example, biological: paused, experienced: paused,
    warnings: ['The birth date is unknown; age cannot be calculated.',
      'Temporal aging is disabled; biological and experienced age equal calendar age.',
      'Review this unusual chronology.'] });
  const groups = [...host.querySelectorAll('.age-measures > div')].map(item => item.textContent);
  if (groups.length !== 2 || !groups[0].includes('Calendar, Legal') || !groups[0].includes('21 years') ||
      !groups[1].includes('Biological, Experienced') || !groups[1].includes('11 years'))
    throw Error('Distinct ages were not grouped by their displayed value.');
  if (host.querySelector('.age-warnings')?.textContent !== 'Review this unusual chronology.')
    throw Error('Routine age warnings remained visible or a meaningful warning was hidden.');
  const futureClock = { source: 'session', currentTime: '2030-01-12T21:15:00-08:00',
    referenceTimeZoneId: 'America/Vancouver' };
  const futureRenderer = window.WritingVaultRecordPages({ state: { session: { clock: futureClock } },
    label: value => String(value) });
  host.replaceChildren();
  futureRenderer.renderAge(host, { ...example, asOf: '2030-01-13T05:15:00Z',
    referenceTimeZoneId: 'America/Vancouver', warnings: [] });
  if (!host.querySelector('.age-context')?.textContent.includes('9:15pm'))
    throw Error('Character age displayed a different local time from the accepted session clock.');
  const record = document.querySelector('#record').textContent;
  if (record.includes('Observed Revision') || record.includes('Related Character Reference') ||
      record.includes('Id: 1')) throw Error('Internal current-state metadata leaked into the page.');
  if (!document.querySelector('.current-state-list')?.textContent.includes('friend of'))
    throw Error('Active relationship summary was lost.');
  return { view: 'Mara age', unknownCards: labels.length, groupedMeasures: groups.length, currentState: true };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'mara-age-set.png') -Url "$route#characters" `
        -WaitSeconds 15 -ProbeSetupExpression $maraSetup -ProbeExpression $mara | Out-Host

    $noteSetup = @'
(async () => {
  document.querySelector('#first-result').click();
  for (let attempt = 0; !document.querySelector('#record-sections .note-card-title') && attempt < 60; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  const title = document.querySelector('#record-sections .note-card-title');
  if (!title) throw Error('Character note title did not load.');
  title.click();
  for (let attempt = 0; document.querySelector('#record-title').textContent !== 'What she remembers' && attempt < 60; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  return true;
})()
'@
    $note = @'
(async () => {
  for (let attempt = 0; !document.querySelector('#record-history .history-entry') && attempt < 40; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  const record = document.querySelector('#record');
  const body = document.querySelector('#record-main .note-page-body');
  if (record.hidden || document.querySelector('#record-title').textContent !== 'What she remembers' ||
      !body?.textContent.includes('The key') || !body.querySelector('a[href="https://example.org/lostville"]'))
    throw Error('Standalone note page lost its Markdown body or safe link.');
  if (record.textContent.split('What she remembers').length !== 2 ||
      document.querySelector('#record-main').textContent.includes('Body'))
    throw Error('Standalone note repeats its title or adds a redundant body heading.');
  const detailPanel = document.querySelector('.record-details');
  const details = detailPanel.getBoundingClientRect();
  const history = document.querySelector('#record-history').getBoundingClientRect();
  if ((!detailPanel.hidden && (history.top - details.bottom < 16 || details.left !== history.left)) ||
      (detailPanel.hidden && document.querySelector('#record-fields').childElementCount) ||
      !document.querySelector('#record-history .history-entry'))
    throw Error('History is not a distinct panel beside the note body.');
  return { view: 'note', titleCount: 1, markdown: true };
})()
'@
    & $capture -Output (Join-Path $outputRoot 'note-page.png') -Url "$route#characters" `
        -WaitSeconds 15 -ProbeSetupExpression $noteSetup -ProbeExpression $note | Out-Host

    }
    $cookieProfile = Join-Path $outputRoot "story-time-profile-$([Guid]::NewGuid().ToString('N'))"
    try {
        $cookieSet = @'
(async () => {
  for (let attempt = 0; document.querySelector('#overview').hidden && attempt < 80; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  document.querySelector('#clock').click();
  document.querySelector('#story-date').value = '2030-01-12';
  document.querySelector('#story-has-time').click();
  document.querySelector('#story-time').value = '21:15';
  document.querySelector('#story-zone').value = 'America/Vancouver';
  document.querySelector('#time-form').requestSubmit();
  for (let attempt = 0; document.querySelector('#time-dialog').open && attempt < 80; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  const saved = await window.WritingVaultStoryTimeCookies.read('Lostville Preview');
  if (!saved || saved.local !== '2030-01-12T21:15' ||
      !document.querySelector('#clock').textContent.includes('9:15pm') ||
      window.WritingVaultStoryDates.clockLocalDate({ source: 'session', currentTime: saved.instant }) !== '2030-01-12')
    throw Error('Story time was not saved with the accepted local date and offset.');
  return { view: 'story time cookie saved', local: saved.local };
})()
'@
        & $capture -Output (Join-Path $outputRoot 'story-time-cookie-set.png') -Url $route `
            -WaitSeconds 10 -ProfileDirectory $cookieProfile -ProbeExpression $cookieSet | Out-Host

        $cookieRestore = @'
(async () => {
  for (let attempt = 0; (document.querySelector('#overview').hidden ||
      !document.querySelector('#clock').textContent.includes('9:15pm')) && attempt < 100; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  const saved = await window.WritingVaultStoryTimeCookies.read('Lostville Preview');
  const headers = { 'X-WritingVault-Session': sessionStorage.getItem('writing-vault-session'),
    'X-WritingVault-Request': '1' };
  const bootstrap = await fetch('/api/bootstrap', { headers }).then(response => response.json());
  if (!saved || !document.querySelector('#clock').textContent.includes('9:15pm') ||
      bootstrap.session.clock.source !== 'session')
    throw Error('The saved story time was not restored after reopening the browser: ' +
      JSON.stringify({ savedLocal: saved?.local || null,
        clockLabel: document.querySelector('#clock').textContent,
        source: bootstrap.session.clock.source,
        warning: document.querySelector('#time-storage-warning')?.textContent || null }));
  document.querySelector('#clock').click();
  if (document.querySelector('#story-date').value !== saved.local.slice(0, 10) ||
      !document.querySelector('#story-has-time').checked ||
      document.querySelector('#story-time').value !== saved.local.slice(11, 16) ||
      document.querySelector('#story-zone').value !== saved.zone)
    throw Error('The time dialog did not prefill the saved local time and zone.');
  document.querySelector('#clear-time').click();
  for (let attempt = 0; document.querySelector('#time-dialog').open && attempt < 80; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  if (await window.WritingVaultStoryTimeCookies.read('Lostville Preview') ||
      document.querySelector('#clock').textContent !== 'Set story time')
    throw Error('Use continuity clock did not clear the saved cookie.');
  return { view: 'story time cookie restored and cleared', restored: true, cleared: true };
})()
'@
        & $capture -Output (Join-Path $outputRoot 'story-time-cookie-restored.png') -Url $route `
            -WaitSeconds 10 -ProfileDirectory $cookieProfile -ProbeExpression $cookieRestore | Out-Host

        $cookieFallback = @'
(async () => {
  for (let attempt = 0; ![...document.querySelector('#continuity').options].some(option =>
      option.value === 'Lostville Preview') && attempt < 80; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  const stored = await window.WritingVaultStoryTimeCookies.write('Lostville Preview', {
    instant: '2030-01-12T21:15:00-08:00', local: '2030-01-12T21:15',
    zone: 'No/Such_Zone', ambiguous: 'Earlier' });
  if (!stored) throw Error('Could not seed the invalid-zone cookie.');
  const selector = document.querySelector('#continuity');
  selector.value = 'Lostville Preview';
  selector.dispatchEvent(new Event('change', { bubbles: true }));
  for (let attempt = 0; document.querySelector('#overview').hidden && attempt < 100; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  const warning = document.querySelector('#time-storage-warning');
  if (document.querySelector('#overview').hidden || warning.hidden ||
      !warning.textContent.includes('cookie was kept') ||
      !(await window.WritingVaultStoryTimeCookies.read('Lostville Preview')) ||
      document.querySelector('#clock').textContent !== 'Set story time')
    throw Error('An invalid saved timezone did not fall back to the continuity clock while retaining the cookie.');
  document.querySelector('#clock').click();
  document.querySelector('#clear-time').click();
  for (let attempt = 0; document.querySelector('#time-dialog').open && attempt < 80; attempt++)
    await new Promise(resolve => setTimeout(resolve, 250));
  if (await window.WritingVaultStoryTimeCookies.read('Lostville Preview') || !warning.hidden)
    throw Error('Explicit Clear did not remove the invalid saved cookie and its warning.');
  return { view: 'invalid story time fallback', retainedUntilClear: true };
})()
'@
        & $capture -Output (Join-Path $outputRoot 'story-time-cookie-fallback.png') -Url "$address/" `
            -WaitSeconds 10 -ProfileDirectory $cookieProfile -ProbeExpression $cookieFallback | Out-Host
    }
    finally {
        if (Test-Path -LiteralPath $cookieProfile -PathType Container) {
            $resolvedProfile = (Resolve-Path -LiteralPath $cookieProfile).Path
            $resolvedArtifacts = [IO.Path]::GetFullPath($outputRoot).TrimEnd('\') + '\'
            if (-not $resolvedProfile.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath (Join-Path $resolvedProfile '.writing-vault-capture-profile') -PathType Leaf)) {
                throw 'Refusing to remove an unverified story-time browser profile.'
            }
            Remove-Item -LiteralPath $resolvedProfile -Recurse -Force
        }
    }
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { $process.Kill() }
        $process.WaitForExit()
        $process.Dispose()
    }
}
