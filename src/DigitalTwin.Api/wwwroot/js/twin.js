(() => {
  'use strict';

  const state = {
    data: null,
    windowDays: 7,
    sourceFilter: 'ALL',
    seenEventIds: new Set(),
    particles: [],
    asOf: null,
    historyStart: null,
    replayTimer: null
  };

  const stageColumns = [
    { key: 'work', label: 'Work Item' },
    { key: 'pr', label: 'Branch / PR' },
    { key: 'build', label: 'CI Build' },
    { key: 'rc', label: 'RC / Pkg' },
    { key: 'release', label: 'Octopus' },
    { key: 'tdd', label: 'TDD' },
    { key: 'uat', label: 'UAT' },
    { key: 'prod', label: 'PROD' }
  ];

  const dom = {
    liveStatus: document.getElementById('live-status'),
    liveLabel: document.getElementById('live-label'),
    utcClock: document.getElementById('utc-clock'),
    swimLanes: document.getElementById('swim-lanes'),
    flowCanvasWrap: document.getElementById('flow-canvas-wrap'),
    flowSvg: document.getElementById('flow-svg'),
    envCards: document.getElementById('env-cards'),
    driftList: document.getElementById('drift-list'),
    kanbanStrip: document.getElementById('kanban-strip'),
    workList: document.getElementById('work-list'),
    eventTicker: document.getElementById('event-ticker'),
    drawer: document.getElementById('inspector-drawer'),
    drawerKicker: document.getElementById('drawer-kicker'),
    drawerTitle: document.getElementById('drawer-title'),
    drawerBody: document.getElementById('drawer-body'),
    drawerClose: document.getElementById('drawer-close'),
    traceInput: document.getElementById('trace-input'),
    ttSlider: document.getElementById('tt-slider'),
    ttLabel: document.getElementById('tt-label'),
    ttLive: document.getElementById('tt-live'),
    ttPlay: document.getElementById('tt-play'),
    chainList: document.getElementById('chain-list')
  };

  // "Now" for relative times: the selected instant while time travelling.
  function refNow() {
    return state.asOf ? new Date(state.asOf).getTime() : Date.now();
  }

  function updateClock() {
    const now = new Date();
    dom.utcClock.textContent = now.toISOString().substring(11, 19) + 'Z';
  }
  setInterval(updateClock, 1000);
  updateClock();

  function esc(str) {
    if (str === null || str === undefined) return '';
    return String(str)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function relTime(iso) {
    if (!iso) return '--';
    const sec = Math.max(0, Math.floor((refNow() - new Date(iso).getTime()) / 1000));
    if (sec < 60) return `${sec}s`;
    if (sec < 3600) return `${Math.floor(sec / 60)}m`;
    if (sec < 86400) return `${Math.floor(sec / 3600)}h`;
    return `${Math.floor(sec / 86400)}d`;
  }

  function renderIntake(iq) {
    if (!iq) return;
    if (iq.error) { setPill('intake', 'ERR', 'err'); return; }
    const age = iq.oldestPendingAgeSeconds != null ? `${Math.round(iq.oldestPendingAgeSeconds)}s` : '0s';
    const text = `${iq.depth} q · ${age} · DLQ ${iq.deadLetterCount}`;
    const cls = iq.deadLetterCount > 0 ? 'err' : (iq.depth > 0 && (iq.oldestPendingAgeSeconds || 0) > 60) ? 'warn' : 'ok';
    setPill('intake', text, cls);
    const pill = document.querySelector('[data-source="intake"]');
    if (pill) pill.title = `Durable intake (${iq.transport}): ${iq.capturedTotal} captured, ${iq.processedTotal} processed, depth ${iq.depth}, oldest pending ${age}, dead letters ${iq.deadLetterCount}`;
  }

  async function fetchIntake() {
    const res = await fetch('/api/intake/status', { cache: 'no-store' });
    if (res.ok) renderIntake(await res.json());
  }

  async function fetchState() {
    const url = state.asOf ? `/api/dashboard/state?asOf=${encodeURIComponent(state.asOf)}` : '/api/dashboard/state';
    const res = await fetch(url, { cache: 'no-store' });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    state.data = await res.json();
    state.historyStart = state.data.historyStartsAt || state.historyStart;
    updateTimeStrip();
    for (const ev of state.data.recentEvents || []) {
      state.seenEventIds.add(ev.idempotencyKey || ev.eventId);
    }
    renderAll();
  }

  function renderAll() {
    if (!state.data) return;
    renderHeaderAndMetrics();
    renderSwimLanes();
    renderChains();
    renderEnvironmentsAndDrift();
    renderWorkBoard();
    renderEventTicker();
    requestAnimationFrame(drawTrackConnectors);
  }

  function renderHeaderAndMetrics() {
    const s = state.data.summary;
    const dora = state.windowDays === 28 ? s.dora28Days : s.dora7Days;

    document.getElementById('m-freq').textContent = `${dora.deploymentsPerDay}/d`;
    document.getElementById('m-freq-sub').textContent = `${dora.totalDeployments} prod (${dora.windowDays}d)`;
    document.getElementById('m-lead').textContent = dora.medianLeadTimeHours != null ? `${dora.medianLeadTimeHours}h` : '--';
    document.getElementById('m-lead-sub').textContent = dora.p85LeadTimeHours != null ? `p85 ${dora.p85LeadTimeHours}h` : 'commit → prod';
    document.getElementById('m-cfr').textContent = `${dora.changeFailRatePercent}%`;
    document.getElementById('m-cfr-sub').textContent = `${dora.failedDeployments} failed / ${dora.totalDeployments}`;
    document.getElementById('m-mttr').textContent = dora.failedDeploymentRecoveryHours != null ? `${dora.failedDeploymentRecoveryHours}h` : '0h';
    document.getElementById('m-rework').textContent = dora.reworkRatePercent != null ? `${dora.reworkRatePercent}%` : '--';

    const driftCard = document.getElementById('drift-kpi-card');
    driftCard.classList.remove('ok', 'warn', 'crit');
    driftCard.classList.add(s.criticalDriftCount > 0 ? 'crit' : s.activeDriftCount > 0 ? 'warn' : 'ok');
    document.getElementById('m-drift').textContent = s.activeDriftCount;
    document.getElementById('m-drift-sub').textContent = s.criticalDriftCount > 0 ? `${s.criticalDriftCount} critical` : 'synced with GitOps';

    document.getElementById('m-cost').textContent = s.costMonthToDateUsd != null ? `$${s.costMonthToDateUsd.toFixed(2)}` : '--';
    document.getElementById('m-cost-sub').textContent = s.costYesterdayUsd != null ? `yesterday $${s.costYesterdayUsd.toFixed(2)}` : 'cost.json';

    // Ingestion pills
    const cursors = Object.fromEntries((s.ingestionCursors || []).map(c => [c.sourceKey, c]));
    const ghHooks = (s.webhooks || []).filter(w => w.source === 'github');
    const ghActive = ghHooks.filter(w => w.isActive).length;
    setPill('gh', ghHooks.length ? `${ghActive}/${ghHooks.length}` : (cursors.github?.status || '--'), ghActive === ghHooks.length && ghHooks.length > 0 ? 'ok' : 'warn');

    const q = s.gitHubQuota;
    if (q) {
      const rem = q.coreRemaining != null ? `${q.coreRemaining}/${q.coreLimit || 5000}` : `${q.twinRestCallsThisHour}/${q.budgetPerHourTarget}/h`;
      const quotaText = q.mode === 'paused' ? `PAUSED (${rem})` : `${rem} · 304:${q.twinCache304HitsTotal}`;
      const quotaCls = q.mode === 'paused' ? 'err' : q.mode === 'backoff' ? 'warn' : 'ok';
      setPill('quota', quotaText, quotaCls);
    }

    const octoHooks = (s.webhooks || []).filter(w => w.source === 'octopus');
    const octoOk = octoHooks.some(w => w.isActive);
    setPill('octo', octoOk ? 'SUB ON' : (cursors.octopus?.status || '--'), octoOk ? 'ok' : cursors.octopus?.status === 'ok' ? 'warn' : 'err');

    const azHooks = (s.webhooks || []).filter(w => w.source === 'azure');
    const azEgOk = azHooks.some(w => w.isActive);
    setPill('az', azEgOk ? 'EG+ARG' : (cursors.azure?.status === 'ok' ? 'ARG POLL' : '--'), cursors.azure?.status === 'ok' ? 'ok' : 'warn');

    setPill('probes', cursors.probes?.status === 'ok' ? `${cursors.probes.itemsProcessed} OK` : '--', cursors.probes?.status === 'ok' ? 'ok' : 'warn');

    renderIntake(s.intakeQueue);

    // Observer bar
    const twinPipe = (s.pipelines || []).find(p => p.definition.isTwinObserver);
    if (twinPipe) {
      const sha = twinPipe.repository?.latestCommitSha?.substring(0, 7) || 'head';
      const runStatus = twinPipe.latestRun?.conclusion || twinPipe.latestRun?.status || 'active';
      document.getElementById('obs-detail').textContent = `${twinPipe.definition.repository} (${sha}, CI ${runStatus}) · observing cmdemo1`;
    }
  }

  function setPill(key, text, cls) {
    const el = document.getElementById(`pill-${key}`);
    if (!el) return;
    el.textContent = text;
    el.parentElement.classList.remove('ok', 'warn', 'err');
    el.parentElement.classList.add(cls);
  }

  function renderSwimLanes() {
    const { summary, workItems, openPullRequests, recentRuns, recentDeliveries, activeDrifts } = state.data;
    const pipelines = (summary.pipelines || []).filter(p => !p.definition.isTwinObserver);

    dom.swimLanes.innerHTML = '';

    for (const p of pipelines) {
      const def = p.definition;
      const row = document.createElement('div');
      row.className = `swim-lane-row${def.isRetired ? ' retired' : ''}`;
      row.dataset.pipeline = def.id;

      const label = document.createElement('div');
      label.className = 'lane-label';
      label.innerHTML = `
        <div class="lane-title">${esc(def.name)} ${def.isRetired ? '<span class="tag-retired">retired</span>' : ''}</div>
        <div class="lane-sub">${esc(def.repository)}</div>`;
      row.appendChild(label);

      const pipeDeliveries = (recentDeliveries || []).filter(d => d.pipelineId === def.id);
      const pipeRuns = (recentRuns || []).filter(r => r.pipelineId === def.id);
      const pipePrs = (openPullRequests || []).filter(pr => pr.pipelineId === def.id);
      const pipeWork = def.id === 'app-ui' || def.id === 'factory-agentic'
        ? (workItems || []).filter(w => def.id !== 'factory-agentic' || w.workerKind === 'bot')
        : [];

      for (const col of stageColumns) {
        const node = buildStageNode(def, p, col.key, pipeWork, pipePrs, pipeRuns, pipeDeliveries, activeDrifts || []);
        row.appendChild(node);
      }

      dom.swimLanes.appendChild(row);
    }
  }

  function buildStageNode(def, pipeSummary, colKey, pipeWork, pipePrs, pipeRuns, pipeDeliveries, activeDrifts) {
    const node = document.createElement('div');
    node.className = 'stage-node';
    node.dataset.pipeline = def.id;
    node.dataset.stage = colKey;

    if (def.isRetired && (colKey === 'tdd' || colKey === 'uat' || colKey === 'prod')) {
      node.classList.add('muted');
      node.innerHTML = `<div class="node-main">—</div><div class="node-sub">retired</div>`;
      return node;
    }

    let main = '--';
    let sub = '';
    let status = 'healthy';
    let onClick = null;

    if (colKey === 'work') {
      const openCount = pipeWork.filter(w => w.state === 'open').length;
      const wipCount = pipeWork.filter(w => w.column !== 'Done').length;
      main = def.id === 'app-ui' || def.id === 'factory-agentic' ? `${openCount} open` : def.kind;
      sub = def.id === 'app-ui' ? `${wipCount} in flow (#679)` : def.id === 'factory-agentic' ? 'bot queue' : 'gitops/ops';
      onClick = () => openPipelineInspector(pipeSummary);
    } else if (colKey === 'pr') {
      main = `${pipePrs.length} PRs`;
      sub = pipePrs[0] ? `#${pipePrs[0].number} ${pipePrs[0].author}` : (pipeSummary.repository?.defaultBranch || 'main');
      onClick = () => openPipelineInspector(pipeSummary);
    } else if (colKey === 'build') {
      const latest = pipeRuns[0];
      if (latest) {
        main = latest.version || `#${latest.runNumber}`;
        sub = `${latest.workflowName}: ${latest.conclusion || latest.status}`;
        status = latest.status !== 'completed' ? 'executing' : latest.conclusion === 'failure' ? 'failed' : 'healthy';
        onClick = () => openObjectInspector('CI BUILD RUN', `${def.repository} #${latest.runNumber}`, {
          Workflow: latest.workflowName,
          Title: latest.displayTitle,
          Branch: latest.branch,
          Status: `${latest.status} / ${latest.conclusion || 'running'}`,
          Commit: latest.headSha,
          Updated: latest.updatedAt
        }, latest.url);
      } else if (def.id === 'measurement-scorecard') {
        main = 'nightly';
        sub = pipeSummary.repository?.latestCommitMessage || 'space-scorecard';
        status = 'healthy';
        onClick = () => openPipelineInspector(pipeSummary);
      } else {
        main = 'no runs';
        sub = def.repository;
      }
    } else if (colKey === 'rc') {
      const latestDel = pipeDeliveries.find(d => d.channelName === 'Default') || pipeDeliveries[0];
      main = latestDel ? latestDel.releaseVersion : (pipeSummary.stageVersions?.tdd || '--');
      sub = def.deployableName ? `${def.deployableName} package` : 'artifact';
      onClick = () => openPipelineInspector(pipeSummary);
    } else if (colKey === 'release') {
      const active = pipeDeliveries.find(d => d.state === 'executing' || d.state === 'waiting' || d.state === 'queued');
      const latest = active || pipeDeliveries[0];
      if (latest) {
        main = latest.releaseVersion;
        sub = active ? `${active.state} → ${active.stageName}` : `${latest.stageName}: ${latest.state}`;
        status = latest.state === 'executing' || latest.state === 'queued' ? 'executing'
          : latest.state === 'waiting' ? 'waiting'
          : latest.state === 'failed' || latest.hasGuidedFailure ? 'failed' : 'healthy';
        onClick = () => openDeliveryInspector(latest);
      } else {
        main = def.octopusProjectSlug || '--';
        sub = 'Octopus';
      }
    } else {
      // tdd | uat | prod
      const stageObj = (state.data.summary.stages || []).find(s => s.stage.name === colKey);
      const workload = stageObj?.workloads?.find(w => w.deployableName === def.deployableName);
      const probe = stageObj?.healthProbes?.find(pr => pr.deployableName === def.deployableName);
      const stageDel = pipeDeliveries.find(d => d.stageName === colKey);
      const stageDrift = activeDrifts.filter(dr => dr.stageName === colKey && (!dr.deployableName || dr.deployableName === def.deployableName));
      const version = workload?.runningVersion || pipeSummary.stageVersions?.[colKey] || stageDel?.releaseVersion;

      if (def.id === 'measurement-scorecard' && colKey !== 'prod') {
        node.classList.add('muted');
        node.innerHTML = `<div class="node-main">—</div><div class="node-sub">prod nightly</div>`;
        return node;
      }

      main = version || '--';
      if (stageDel && (stageDel.state === 'executing' || stageDel.state === 'waiting' || stageDel.state === 'queued')) {
        status = stageDel.state === 'waiting' ? 'waiting' : 'executing';
        sub = `${stageDel.state} (${stageDel.releaseVersion})`;
      } else if (stageDrift.some(d => d.severity === 'Critical') || (probe && !probe.isHealthy)) {
        status = 'failed';
        sub = probe && !probe.isHealthy ? `HTTP ${probe.httpStatusCode}` : 'DRIFT';
      } else if (workload) {
        sub = workload.activeRevisionName
          ? `${workload.activeRevisionName.split('--').pop()} · ${probe ? `${probe.latencyMs}ms` : workload.healthState}`
          : `${probe ? `${probe.latencyMs}ms OK` : 'Serving'}`;
      } else if (stageDel) {
        sub = `${stageDel.state} · ${relTime(stageDel.finishedAt || stageDel.queuedAt)}`;
        status = stageDel.state === 'failed' ? 'failed' : 'healthy';
      } else {
        sub = colKey;
      }

      onClick = () => openStageDeployableInspector(colKey, def, workload, probe, stageDel, stageDrift);
    }

    node.classList.add(status);
    node.innerHTML = `
      <div class="node-top">
        <span class="node-main">${esc(main)}</span>
        <span class="status-dot ${status}"></span>
      </div>
      <div class="node-sub">${esc(sub)}</div>`;
    if (onClick) node.addEventListener('click', onClick);
    return node;
  }

  function drawTrackConnectors() {
    const svg = dom.flowSvg;
    const wrapRect = dom.flowCanvasWrap.getBoundingClientRect();
    if (!wrapRect.width) return;

    svg.setAttribute('viewBox', `0 0 ${dom.flowCanvasWrap.scrollWidth} ${dom.flowCanvasWrap.scrollHeight}`);
    svg.innerHTML = '';

    const lanes = dom.swimLanes.querySelectorAll('.swim-lane-row');
    lanes.forEach(lane => {
      const nodes = [...lane.querySelectorAll('.stage-node')];
      for (let i = 0; i < nodes.length - 1; i++) {
        const a = nodes[i];
        const b = nodes[i + 1];
        if (a.classList.contains('muted') && b.classList.contains('muted')) continue;
        const ar = a.getBoundingClientRect();
        const br = b.getBoundingClientRect();
        const x1 = ar.right - wrapRect.left + dom.flowCanvasWrap.scrollLeft;
        const y1 = ar.top + ar.height / 2 - wrapRect.top;
        const x2 = br.left - wrapRect.left + dom.flowCanvasWrap.scrollLeft;
        const y2 = br.top + br.height / 2 - wrapRect.top;

        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', `M ${x1} ${y1} L ${x2} ${y2}`);
        path.setAttribute('stroke', b.classList.contains('executing') ? '#38bdf8' : b.classList.contains('failed') ? '#f43f5e' : '#283856');
        path.setAttribute('stroke-width', b.classList.contains('executing') ? '2.2' : '1.5');
        path.setAttribute('fill', 'none');
        if (b.classList.contains('muted')) {
          path.setAttribute('stroke-dasharray', '3 3');
        }
        svg.appendChild(path);
      }
    });
  }

  function spawnTokenAnimation(pipelineId, targetStageKey, outcome) {
    const lane = dom.swimLanes.querySelector(`.swim-lane-row[data-pipeline="${CSS.escape(pipelineId || 'app-ui')}"]`);
    if (!lane) return;
    const nodes = [...lane.querySelectorAll('.stage-node')];
    const targetIdx = Math.max(1, nodes.findIndex(n => n.dataset.stage === (targetStageKey || 'build')));
    const endIdx = targetIdx >= 0 ? targetIdx : 2;
    const startNode = nodes[Math.max(0, endIdx - 1)];
    const endNode = nodes[endIdx];
    if (!startNode || !endNode) return;

    endNode.classList.remove('flash');
    void endNode.offsetWidth;
    endNode.classList.add('flash');

    const wrapRect = dom.flowCanvasWrap.getBoundingClientRect();
    const sr = startNode.getBoundingClientRect();
    const er = endNode.getBoundingClientRect();
    const x1 = sr.left + sr.width / 2 - wrapRect.left + dom.flowCanvasWrap.scrollLeft;
    const y1 = sr.top + sr.height / 2 - wrapRect.top;
    const x2 = er.left + er.width / 2 - wrapRect.left + dom.flowCanvasWrap.scrollLeft;
    const y2 = er.top + er.height / 2 - wrapRect.top;

    const color = outcome === 'Failure' || outcome === 5 ? '#f43f5e'
      : outcome === 'Waiting' || outcome === 3 ? '#f59e0b'
      : outcome === 'Success' || outcome === 4 ? '#10b981' : '#38bdf8';

    const circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
    circle.setAttribute('r', '5');
    circle.setAttribute('fill', color);
    circle.setAttribute('cx', x1);
    circle.setAttribute('cy', y1);
    dom.flowSvg.appendChild(circle);

    const start = performance.now();
    const duration = 900;
    function step(ts) {
      const p = Math.min(1, (ts - start) / duration);
      circle.setAttribute('cx', x1 + (x2 - x1) * p);
      circle.setAttribute('cy', y1 + (y2 - y1) * p);
      circle.setAttribute('opacity', p > 0.8 ? String((1 - p) * 5) : '1');
      if (p < 1) {
        requestAnimationFrame(step);
      } else {
        circle.remove();
      }
    }
    requestAnimationFrame(step);
  }

  function renderEnvironmentsAndDrift() {
    const stages = state.data.summary.stages || [];
    const drifts = state.data.activeDrifts || [];
    dom.envCards.innerHTML = '';

    for (const s of stages) {
      const st = s.stage;
      const inst = s.instance;
      const critCount = (s.activeDrifts || []).filter(d => d.severity === 'Critical' || d.severity === 2).length;
      const card = document.createElement('div');
      card.className = `env-card${st.isDeclaredOnly ? ' declared' : ''}${critCount > 0 ? ' drifted' : ''}`;

      const badge = st.isDeclaredOnly
        ? `<span class="badge info">DECLARED</span>`
        : critCount > 0
          ? `<span class="badge crit">${critCount} DRIFT</span>`
          : `<span class="badge ok">SYNCED</span>`;

      const uiWorkload = (s.workloads || []).find(w => w.deployableName === 'ui');
      const uiProbe = (s.healthProbes || []).find(p => p.deployableName === 'ui');

      card.innerHTML = `
        <div class="env-card-head">
          <span class="env-name">${esc(st.name)} <span class="muted">${esc(st.tier)}</span></span>
          ${badge}
        </div>
        <div class="env-meta-row">
          <span>${esc(st.hostingBoundary)}</span>
          <span>${inst ? `${inst.resourceCount} res` : '0 res'}</span>
        </div>
        <div class="env-meta-row">
          <span>ui: ${esc(uiWorkload?.runningVersion || '--')}</span>
          <span>${uiProbe ? `${uiProbe.latencyMs}ms · db v${uiProbe.databaseVersion || '?'}` : 'inactive'}</span>
        </div>`;
      card.addEventListener('click', () => openStageFullInspector(s));
      dom.envCards.appendChild(card);
    }

    dom.driftList.innerHTML = '';
    if (drifts.length === 0) {
      dom.driftList.innerHTML = `<div class="drift-item"><span class="badge ok">NO DRIFT</span> GitOps desired state matches Azure &amp; Octopus.</div>`;
    } else {
      for (const d of drifts) {
        const sev = typeof d.severity === 'number' ? ['Info', 'Warning', 'Critical'][d.severity] : d.severity;
        const item = document.createElement('div');
        item.className = `drift-item ${sev}`;
        item.innerHTML = `
          <div class="work-row-top">
            <span class="badge ${sev === 'Critical' ? 'crit' : sev === 'Warning' ? 'warn' : 'info'}">${esc(sev)}</span>
            <span class="muted">${esc(d.stageName)}/${esc(d.deployableName || 'system')}</span>
          </div>
          <div class="work-title">${esc(d.summary)}</div>`;
        item.addEventListener('click', () => openObjectInspector('DRIFT FINDING', d.driftKey, {
          Severity: sev,
          Category: d.category,
          Stage: d.stageName,
          Expected: d.expectedValue,
          Observed: d.observedValue,
          Remediation: d.remediationHint || '--',
          Detected: d.detectedAt
        }));
        dom.driftList.appendChild(item);
      }
    }
  }

  function renderWorkBoard() {
    const ws = state.data.summary.workSummary;
    const items = state.data.workItems || [];
    document.getElementById('work-meta').textContent = `${ws.openItems} open · ${ws.botItems} bot / ${ws.teamItems} team`;

    dom.kanbanStrip.innerHTML = '';
    const cols = ['Todo', 'In Progress', 'Review', 'Merged', 'TDD', 'UAT'];
    for (const c of cols) {
      const box = document.createElement('div');
      box.className = 'kanban-col';
      box.innerHTML = `<div class="kanban-col-name">${esc(c)}</div><div class="kanban-col-count">${ws.wipByColumn?.[c] ?? 0}</div>`;
      dom.kanbanStrip.appendChild(box);
    }

    dom.workList.innerHTML = '';
    const ordered = [...items].sort((a, b) => (a.state === 'open' ? -1 : 1) - (b.state === 'open' ? -1 : 1)).slice(0, 25);
    for (const item of ordered) {
      const row = document.createElement('div');
      row.className = 'work-row';
      row.innerHTML = `
        <div class="work-row-top">
          <span class="work-title">#${item.number} ${esc(item.title)}</span>
          <span class="badge ${item.workerKind === 'bot' ? 'bot' : 'info'}">${esc(item.column)}</span>
        </div>
        <div class="work-row-sub">
          <span>${esc(item.agent || item.workerKind)}</span>
          ${item.releaseCandidateVersion ? `<span>RC ${esc(item.releaseCandidateVersion)}</span>` : ''}
          ${item.prodVersion ? `<span>prod ${esc(item.prodVersion)}</span>` : ''}
        </div>`;
      row.addEventListener('click', () => openObjectInspector('WORK ITEM', `#${item.number} ${item.title}`, {
        Column: `${item.column} (${item.columnSource})`,
        Worker: `${item.workerKind} (${item.agent || 'unassigned'})`,
        State: item.state,
        ReleaseCandidate: item.releaseCandidateVersion || '--',
        TDD: item.tddVersion || '--',
        UAT: item.uatVersion || '--',
        PROD: item.prodVersion || '--',
        Rework: `ftFailures=${item.ftFailures}, reverts=${item.reverts}`
      }, item.url));
      dom.workList.appendChild(row);
    }
  }

  function renderEventTicker(animateNewIds = new Set()) {
    const events = (state.data.recentEvents || []).filter(ev => {
      if (state.sourceFilter === 'ALL') return true;
      const srcName = typeof ev.source === 'number'
        ? ['GitHub', 'Octopus', 'Azure', 'GitOps', 'Probe', 'Twin'][ev.source]
        : ev.source;
      return srcName === state.sourceFilter;
    });

    dom.eventTicker.innerHTML = '';
    for (const ev of events.slice(0, 50)) {
      const srcName = typeof ev.source === 'number'
        ? ['GitHub', 'Octopus', 'Azure', 'GitOps', 'Probe', 'Twin'][ev.source]
        : ev.source;
      const badgeCls = ev.outcome === 5 || ev.outcome === 'Failure' ? 'crit'
        : ev.outcome === 3 || ev.outcome === 'Waiting' ? 'warn'
        : srcName === 'Octopus' ? 'bot'
        : srcName === 'Azure' ? 'info' : 'ok';

      const row = document.createElement('div');
      const key = ev.idempotencyKey || ev.eventId;
      row.className = `ticker-row${animateNewIds.has(key) ? ' is-new' : ''}`;
      row.innerHTML = `
        <span class="ticker-time">${relTime(ev.occurredAt)}</span>
        <span class="badge ${badgeCls}">${esc(srcName.substring(0, 4).toUpperCase())}</span>
        <div>
          <div class="ticker-summary">${esc(ev.summary)}</div>
          <div class="ticker-type">${esc(ev.eventType)} · ${esc(ev.pipelineId || 'system')}${ev.stageName ? `/${esc(ev.stageName)}` : ''}</div>
        </div>`;
      row.addEventListener('click', () => openObjectInspector('NORMALIZED EVENT', ev.eventType, {
        Source: `${srcName} (${ev.channel})`,
        OccurredAt: ev.occurredAt,
        Pipeline: ev.pipelineId || '--',
        Stage: ev.stageName || '--',
        Subject: `${ev.subjectType}: ${ev.subjectId}`,
        Actor: ev.actor || '--',
        Outcome: ev.outcome,
        ChainId: ev.chainId || '--',
        IdempotencyKey: ev.idempotencyKey
      }, ev.sourceUrl));
      dom.eventTicker.appendChild(row);
    }
  }

  const chainStageLabels = {
    WorkItem: 'WORK', PullRequest: 'PR', Build: 'BUILD', ReleaseCandidate: 'RC',
    OctopusRelease: 'REL', Tdd: 'TDD', Uat: 'UAT', Prod: 'PROD'
  };
  const chainStatusClass = { Succeeded: 'ok', Running: 'run', Waiting: 'wait', Failed: 'fail', Skipped: 'skip', Pending: '' };

  function fmtMinutes(m) {
    if (m == null) return '';
    if (m < 60) return `${Math.round(m)}m`;
    if (m < 1440) return `${(m / 60).toFixed(1)}h`;
    return `${(m / 1440).toFixed(1)}d`;
  }

  function renderChains() {
    const chains = state.data.changeChains || [];
    if (!chains.length) {
      dom.chainList.innerHTML = '<div class="chain-sub">No changes observed yet.</div>';
      return;
    }
    dom.chainList.innerHTML = chains.map((c, ci) => {
      const stages = c.stages || [];
      const parts = stages.map((st, i) => {
        const failedSignal = (c.signals || []).some(sig => sig.stage === st.kind && /failed|guided/.test(sig.kind));
        const cls = [chainStatusClass[st.status] || '', (st.failedAttempts > 0 || failedSignal) && st.status !== 'Failed' ? 'recovered' : ''].join(' ');
        const node = `<span class="chain-node ${cls}" style="--i:${i}" title="${esc(`${chainStageLabels[st.kind]}: ${st.status}${st.reference ? ' · ' + st.reference : ''}${st.detail ? ' · ' + st.detail : ''}`)}"><span class="lbl">${chainStageLabels[st.kind] || st.kind}${st.durationMinutes != null ? ' ' + fmtMinutes(st.durationMinutes) : ''}</span></span>`;
        if (i === stages.length - 1) return node;
        const next = stages[i + 1];
        const linkCls = next.status === 'Failed' ? 'fail'
          : next.status === 'Running' || next.status === 'Waiting' ? 'live'
          : (st.status === 'Succeeded' || st.status === 'Skipped') && next.status !== 'Pending' ? 'done' : '';
        return node + `<span class="chain-link ${linkCls}"></span>`;
      }).join('');
      const outcome = { Delivered: 'DELIVERED', Failed: 'FAILED', Waiting: 'WAITING', InProgress: 'IN FLIGHT' }[c.outcome] || c.outcome;
      const ref = [c.version, c.workItemNumber ? `#${c.workItemNumber}` : null, c.pullRequestNumber ? `PR ${c.pullRequestNumber}` : null].filter(Boolean).join(' · ');
      return `<div class="chain-row" data-chain="${ci}">
        <div><div class="chain-title" title="${esc(c.title)}">${esc(c.title)}</div><div class="chain-sub">${esc(c.deployable || c.pipelineId)} ${esc(ref)}</div></div>
        <div class="chain-path">${parts}</div>
        <div class="chain-outcome"><strong class="${esc(c.outcome)}">${outcome}</strong>${c.leadTimeHours != null ? `lead ${c.leadTimeHours}h` : relTime(c.lastActivityAt) + ' ago'}</div>
      </div>`;
    }).join('');
    dom.chainList.querySelectorAll('.chain-row').forEach(row => {
      row.addEventListener('click', () => openChainInspector(chains[Number(row.dataset.chain)]));
    });
  }

  function openChainInspector(c) {
    const kv = { Chain: c.chainId, Outcome: c.outcome, Commit: c.commitSha ? c.commitSha.substring(0, 12) : '--' };
    for (const st of c.stages || []) {
      const bits = [st.status, st.reference, st.durationMinutes != null ? fmtMinutes(st.durationMinutes) : null,
        st.failedAttempts > 0 ? `${st.failedAttempts}/${st.attempts} attempts failed` : null, st.detail].filter(Boolean);
      kv[chainStageLabels[st.kind] || st.kind] = bits.join(' · ');
    }
    (c.signals || []).forEach((sig, i) => {
      kv[`Signal ${i + 1}`] = `${chainStageLabels[sig.stage] || sig.stage} · ${sig.kind}${sig.actor ? ' · ' + sig.actor : ''} · ${sig.summary}`;
    });
    kv.LeadTime = c.leadTimeHours != null ? `${c.leadTimeHours}h commit → prod` : '--';
    kv.CycleTime = c.cycleTimeHours != null ? `${c.cycleTimeHours}h work item → prod` : '--';
    const url = (c.stages || []).map(st => st.url).filter(Boolean).pop();
    openObjectInspector('CHANGE CHAIN', c.title, kv, url);
  }

  function openObjectInspector(kicker, title, kv, linkUrl) {
    dom.drawerKicker.textContent = kicker;
    dom.drawerTitle.textContent = title;
    const rows = Object.entries(kv).map(([k, v]) => `<tr><td>${esc(k)}</td><td>${esc(v ?? '--')}</td></tr>`).join('');
    const link = linkUrl ? `<a class="drawer-link" href="${esc(linkUrl)}" target="_blank" rel="noopener">Open in source system →</a>` : '';
    dom.drawerBody.innerHTML = `<table class="kv-table">${rows}</table>${link}`;
    dom.drawer.classList.remove('hidden');
  }

  function openPipelineInspector(p) {
    const def = p.definition;
    openObjectInspector('PIPELINE', def.name, {
      Id: def.id,
      Kind: def.kind,
      Repository: def.repository,
      OctopusProject: def.octopusProjectSlug || '--',
      TDD: p.stageVersions?.tdd || '--',
      UAT: p.stageVersions?.uat || '--',
      PROD: p.stageVersions?.prod || '--',
      OpenPRs: p.openPrCount,
      LatestCommit: p.repository?.latestCommitMessage || '--',
      Webhook: p.repository?.webhookRegistered ? `Active (${p.repository.lastWebhookStatusCode || 'ok'})` : 'Pending'
    }, p.repository?.url);
  }

  function openDeliveryInspector(d) {
    openObjectInspector('OCTOPUS DEPLOYMENT', `${d.projectSlug} ${d.releaseVersion} → ${d.stageName}`, {
      State: d.state,
      Channel: d.channelName,
      StartedBy: d.startedBy || '--',
      SignedOffBy: d.signedOffBy || '--',
      SignOffReason: d.signOffReason || '--',
      GuidedFailure: d.hasGuidedFailure ? 'Yes (counts as DORA failure)' : 'No',
      WaitsFor: d.waitsForKind ? `${d.waitsForKind}: ${d.waitsForTitle}` : '--',
      Commit: d.gitCommitSha || '--',
      LeadTimeHours: d.leadTimeHours != null ? `${d.leadTimeHours}h` : '--',
      FinishedAt: d.finishedAt || 'In flight'
    }, d.taskUrl);
  }

  function openStageDeployableInspector(stage, def, workload, probe, del, drifts) {
    openObjectInspector(`${stage.toUpperCase()} RUNTIME`, `${def.name} in ${stage}`, {
      Hosting: workload?.hostingType || def.kind,
      HostingBoundary: workload?.hostingBoundary || stage,
      ResourceName: workload?.resourceName || '--',
      PinnedVersion: workload?.pinnedVersion || '--',
      RunningVersion: workload?.runningVersion || '--',
      ActiveRevision: workload?.activeRevisionName || '--',
      RevisionState: workload ? `${workload.provisioningState} / ${workload.runningState} / ${workload.healthState}` : '--',
      Replicas: workload?.replicas ?? '--',
      HealthProbe: probe ? `HTTP ${probe.httpStatusCode} (${probe.latencyMs}ms)` : '--',
      DatabaseVersion: probe?.databaseVersion || '--',
      Drifts: drifts.length > 0 ? drifts.map(d => d.summary).join('; ') : 'None'
    }, probe?.url || del?.taskUrl);
  }

  function openStageFullInspector(s) {
    const st = s.stage;
    const inst = s.instance;
    openObjectInspector('DEPLOYMENT STAGE', `${st.name.toUpperCase()} (${st.tier})`, {
      HostingBoundary: st.hostingBoundary,
      ResourceGroup: st.resourceGroup,
      Stack: inst?.stackName ? `${inst.stackName} (${inst.stackProvisioningState})` : '--',
      Database: inst?.databaseName || '--',
      KeyVault: inst?.keyVaultName || '--',
      ResourceCount: inst?.resourceCount ?? 0,
      CostMTD: inst?.costMonthToDateUsd != null ? `$${inst.costMonthToDateUsd.toFixed(2)}` : '--',
      Workloads: (s.workloads || []).map(w => `${w.deployableName}=${w.runningVersion || '?'}`).join(', ') || '--',
      ActiveDrift: (s.activeDrifts || []).length
    });
  }

  function handleIncomingEvent(ev) {
    if (!state.data || state.asOf) return;
    const key = ev.idempotencyKey || ev.eventId;
    if (state.seenEventIds.has(key)) return;
    state.seenEventIds.add(key);

    state.data.recentEvents = [ev, ...(state.data.recentEvents || [])].slice(0, 100);
    renderEventTicker(new Set([key]));

    const stageTarget = ev.stageName || (ev.eventType?.includes('pipelineRun') ? 'build' : ev.eventType?.includes('change') ? 'pr' : 'work');
    spawnTokenAnimation(ev.pipelineId || 'app-ui', stageTarget, ev.outcome);
  }

  function setupRealtime() {
    if (window.signalR) {
      const connection = new window.signalR.HubConnectionBuilder()
        .withUrl('/hubs/twin')
        .withAutomaticReconnect()
        .build();

      connection.on('twinEvent', handleIncomingEvent);
      connection.on('stateRefreshed', () => { if (!state.asOf) fetchState().catch(() => {}); });

      connection.onreconnecting(() => {
        dom.liveStatus.classList.remove('connected');
        dom.liveLabel.textContent = 'RECONNECTING';
      });
      connection.onreconnected(() => {
        dom.liveStatus.classList.add('connected');
        dom.liveLabel.textContent = 'SIGNALR LIVE';
        fetchState().catch(() => {});
      });

      connection.start()
        .then(() => {
          dom.liveStatus.classList.add('connected');
          dom.liveLabel.textContent = 'SIGNALR LIVE';
        })
        .catch(() => connectSseFallback());
    } else {
      connectSseFallback();
    }
  }

  function connectSseFallback() {
    const es = new EventSource('/api/stream');
    es.onopen = () => {
      dom.liveStatus.classList.add('connected');
      dom.liveLabel.textContent = 'SSE LIVE';
    };
    es.addEventListener('twinEvent', e => {
      try { handleIncomingEvent(JSON.parse(e.data)); } catch {}
    });
    es.addEventListener('stateRefreshed', () => { if (!state.asOf) fetchState().catch(() => {}); });
  }

  // Event listeners
  document.querySelectorAll('.toggle-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.toggle-btn').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      state.windowDays = Number(btn.dataset.window || 7);
      renderHeaderAndMetrics();
    });
  });

  document.querySelectorAll('#event-filters .filter-pill').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('#event-filters .filter-pill').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      state.sourceFilter = btn.dataset.source || 'ALL';
      renderEventTicker();
    });
  });

  dom.drawerClose.addEventListener('click', () => dom.drawer.classList.add('hidden'));

  dom.traceInput.addEventListener('keydown', e => {
    if (e.key !== 'Enter' || !state.data) return;
    const q = dom.traceInput.value.trim().replace(/^#/, '');
    if (!q) return;
    const item = (state.data.workItems || []).find(w =>
      String(w.number) === q || w.title.toLowerCase().includes(q.toLowerCase()) || w.releaseCandidateVersion === q);
    if (item) {
      openObjectInspector('LOCAL TRACE MATCH', `#${item.number} ${item.title}`, {
        Column: item.column,
        Worker: item.agent || item.workerKind,
        ReleaseCandidate: item.releaseCandidateVersion || '--',
        TDD: item.tddVersion || '--',
        UAT: item.uatVersion || '--',
        PROD: item.prodVersion || '--'
      }, item.url);
      return;
    }
    const del = (state.data.recentDeliveries || []).find(d => d.releaseVersion === q || d.taskId === q);
    if (del) {
      openDeliveryInspector(del);
      return;
    }
    const ev = (state.data.recentEvents || []).find(x => x.summary.toLowerCase().includes(q.toLowerCase()));
    if (ev) {
      openObjectInspector('EVENT MATCH', ev.eventType, { Summary: ev.summary, Pipeline: ev.pipelineId, Stage: ev.stageName, Occurred: ev.occurredAt }, ev.sourceUrl);
    }
  });

  window.addEventListener('resize', () => requestAnimationFrame(drawTrackConnectors));

  // Periodic ambient particle pulse on active/recent lanes & fallback poll refresh
  setInterval(() => {
    if (!state.data || state.asOf || !state.data.recentEvents?.length) return;
    const sample = state.data.recentEvents[Math.floor(Math.random() * Math.min(6, state.data.recentEvents.length))];
    if (sample) {
      const targetStage = sample.stageName || (sample.eventType?.includes('pipelineRun') ? 'build' : 'release');
      spawnTokenAnimation(sample.pipelineId || 'app-ui', targetStage, sample.outcome);
    }
  }, 4500);

  // Time travel: the slider spans the journal's state history (at most the last 7 days).
  function timeRange() {
    const max = Date.now();
    const start = state.historyStart ? new Date(state.historyStart).getTime() : max - 86400000;
    return { min: Math.max(start, max - 7 * 86400000), max };
  }

  function sliderToTime(value) {
    const { min, max } = timeRange();
    return min + (max - min) * (Number(value) / 1000);
  }

  function fmtUtc(ms) {
    return new Date(ms).toISOString().replace('T', ' ').substring(0, 19) + 'Z';
  }

  function updateTimeStrip() {
    const { min, max } = timeRange();
    if (!state.asOf) {
      dom.ttSlider.value = 1000;
      dom.ttLabel.textContent = `LIVE · history from ${fmtUtc(min)}`;
    } else {
      const t = new Date(state.asOf).getTime();
      dom.ttSlider.value = Math.round(((t - min) / Math.max(1, max - min)) * 1000);
      dom.ttLabel.textContent = `AS OF ${fmtUtc(t)}`;
    }
    dom.ttLive.classList.toggle('active', !state.asOf);
    document.body.classList.toggle('time-travel', !!state.asOf);
  }

  function setAsOf(ms) {
    if (ms >= Date.now() - 5000) { goLive(); return Promise.resolve(); }
    state.asOf = new Date(ms).toISOString();
    updateTimeStrip();
    return fetchState().catch(() => {});
  }

  function stopReplay() {
    if (state.replayTimer) clearInterval(state.replayTimer);
    state.replayTimer = null;
    dom.ttPlay.innerHTML = '&#9654;';
  }

  function goLive() {
    stopReplay();
    state.asOf = null;
    updateTimeStrip();
    fetchState().catch(() => {});
  }

  let sliderDebounce = null;
  dom.ttSlider.addEventListener('input', () => {
    stopReplay();
    dom.ttLabel.textContent = `AS OF ${fmtUtc(sliderToTime(dom.ttSlider.value))}`;
    clearTimeout(sliderDebounce);
    sliderDebounce = setTimeout(() => setAsOf(sliderToTime(dom.ttSlider.value)), 250);
  });
  dom.ttLive.addEventListener('click', goLive);
  dom.ttPlay.addEventListener('click', () => {
    if (state.replayTimer) { stopReplay(); return; }
    const { min } = timeRange();
    let t = state.asOf ? new Date(state.asOf).getTime() : min;
    const step = Math.max(60000, (Date.now() - t) / 40);
    dom.ttPlay.innerHTML = '&#10074;&#10074;';
    let busy = false;
    state.replayTimer = setInterval(() => {
      if (busy) return;
      busy = true;
      t += step;
      setAsOf(t).finally(() => { busy = false; });
      if (t >= Date.now()) stopReplay();
    }, 900);
  });

  setInterval(() => { if (!state.asOf) fetchState().catch(() => {}); }, 30000);
  setInterval(() => fetchIntake().catch(() => {}), 5000);

  fetchState()
    .then(setupRealtime)
    .catch(err => {
      dom.liveLabel.textContent = 'LOADING...';
      console.error(err);
      setupRealtime();
    });
})();
