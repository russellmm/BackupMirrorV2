/* Backup Mirror dashboard — no dependencies, talks to the service's /api. */
'use strict';

// ------------------------------------------------------------------ helpers

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const icon = (name, cls = '') => `<svg class="${cls}"><use href="#i-${name}"/></svg>`;
const nf = new Intl.NumberFormat();
const DAY_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

async function api(method, url, body) {
  const headers = { 'X-BackupMirror': '1' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(url, { method, headers, body: body !== undefined ? JSON.stringify(body) : undefined, credentials: 'same-origin' });
  if (res.status === 401) { location.href = '/login.html'; throw new Error('Sign in required'); }
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* not json */ }
  if (!res.ok) throw new Error((data && (data.error || data.title)) || `${res.status} ${res.statusText}`);
  return data;
}

function fmtBytes(n) {
  if (n == null || isNaN(n)) return '—';
  const u = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  let i = 0; let v = n;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return i === 0 ? `${n} B` : `${v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v.toFixed(2)} ${u[i]}`;
}

function fmtRel(iso, { future = false } = {}) {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  let s = Math.round((t - Date.now()) / 1000);
  const ahead = s > 0; s = Math.abs(s);
  if (ahead && s > 20 * 3600 && s < 7 * 86400) {
    // Far-off future runs read better as a day and time ("Mon 02:00").
    const d = new Date(iso);
    return d.toLocaleDateString([], { weekday: 'short' }) + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }
  if (s < 45) return future || ahead ? 'in a moment' : 'just now';
  const m = Math.round(s / 60), h = Math.floor(s / 3600), d = Math.floor(s / 86400);
  let txt;
  if (s < 3600) txt = `${m} min`;
  else if (s < 86400) { const mm = Math.round((s % 3600) / 60); txt = mm && h < 6 ? `${h} h ${mm} min` : `${h} h`; }
  else if (d < 30) txt = `${d} day${d > 1 ? 's' : ''}`;
  else return new Date(iso).toLocaleDateString();
  return ahead ? `in ${txt}` : `${txt} ago`;
}

function fmtDuration(ms) {
  if (ms == null) return '';
  const s = Math.round(ms / 1000);
  if (s < 1) return '<1 s';
  if (s < 60) return `${s} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} min ${s % 60} s`;
  return `${Math.floor(m / 60)} h ${m % 60} min`;
}

const fmtClock = (iso) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

function fmtDay(iso) {
  const d = new Date(iso); const today = new Date();
  const y = new Date(); y.setDate(today.getDate() - 1);
  if (d.toDateString() === today.toDateString()) return 'Today';
  if (d.toDateString() === y.toDateString()) return 'Yesterday';
  return d.toLocaleDateString([], { weekday: 'long', month: 'short', day: 'numeric' });
}

function sepFor(p) { return /\\/.test(p) || /^[A-Za-z]:/.test(p) ? '\\' : '/'; }
function baseName(p) { const parts = String(p || '').split(/[\\/]+/).filter(Boolean); return parts.length ? parts[parts.length - 1].replace(/:$/, '') : ''; }
function joinPath(a, b) {
  if (!a) return b; if (!b) return a;
  const s = sepFor(a);
  return a.replace(/[\\/]+$/, '') + s + b;
}
const isUnc = (p) => /^(\\\\|\/\/)[^\\/]+[\\/]+[^\\/]+/.test(p || '');

function debounce(fn, ms) { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; }

// ------------------------------------------------------------------ state

const state = {
  status: null,
  jobs: new Map(),
  armed: true,
  connected: false,
  view: 'overview',
  history: [],
  historyLoaded: false,
  historyJob: '',
  logs: [],
  logFilter: '',
  logAuto: true,
  layout: (() => { try { return localStorage.getItem('bm-layout'); } catch { return null; } })(),
  jobFilter: 'all',
  jobQuery: '',
  loaded: false,
};

const STATUS = {
  idle: { label: 'Idle', icon: 'clock' },
  running: { label: 'Syncing', icon: 'refresh' },
  queued: { label: 'Queued', icon: 'clock' },
  waitingConfirm: { label: 'Needs review', icon: 'alert' },
  degraded: { label: 'Unreachable', icon: 'plug' },
  failed: { label: 'Failed', icon: 'x' },
  success: { label: 'Up to date', icon: 'check' },
  stopped: { label: 'All paused', icon: 'pause' },
  cancelled: { label: 'Cancelled', icon: 'stop' },
  warning: { label: 'Completed with issues', icon: 'alert' },
};
const MODES = {
  realtime: { label: 'Realtime', icon: 'zap', desc: 'Mirrors changes within seconds of them happening.' },
  scheduled: { label: 'Scheduled', icon: 'clock', desc: 'Runs at set times on the days you choose.' },
  manualOnly: { label: 'Manual', icon: 'hand', desc: 'Only runs when you press Sync now.' },
};
const TRIGGERS = { manual: 'Manual', schedule: 'Scheduled', realtime: 'Realtime', 'catch-up': 'Catch-up', startup: 'Startup', resume: 'Resumed', confirm: 'Confirmed deletes' };

const sortedJobs = () => Array.from(state.jobs.values()).sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
const jobStatusKey = (j) => (!j.enabled ? 'disabled' : j.status);
const needsAttention = (j) => j.enabled && ['failed', 'degraded', 'waitingConfirm'].includes(j.status);
const serverIsWindows = () => /windows/i.test(state.status?.os || '');

function scheduleText(j) {
  const s = j.schedule || { times: [], days: [] };
  if (!s.times.length) return 'No times set';
  const days = s.days.length === 0 || s.days.length === 7 ? 'Daily'
    : s.days.length === 5 && [1, 2, 3, 4, 5].every((d) => s.days.includes(d)) ? 'Weekdays'
      : s.days.length === 2 && s.days.includes(0) && s.days.includes(6) ? 'Weekends'
        : s.days.slice().sort().map((d) => DAY_NAMES[d]).join(', ');
  return `${days} at ${s.times.join(', ')}`;
}

// ------------------------------------------------------------------ toasts & overlays

function toast(kind, title, msg, ms = 4200) {
  const ic = { ok: 'check', bad: 'x', warn: 'alert', info: 'info' }[kind] || 'info';
  const el = document.createElement('div');
  el.className = `toast ${kind}`;
  el.innerHTML = `<div class="t-ico">${icon(ic)}</div><div><b>${esc(title)}</b>${msg ? `<span>${esc(msg)}</span>` : ''}</div>`;
  $('#toasts').appendChild(el);
  setTimeout(() => { el.classList.add('out'); setTimeout(() => el.remove(), 260); }, ms);
}

const overlays = [];
function pushOverlay(html, { onClose, kind = 'modal' } = {}) {
  const scrim = document.createElement('div');
  scrim.className = 'scrim';
  const box = document.createElement('div');
  box.innerHTML = html;
  const el = box.firstElementChild;
  // Each new overlay stacks above the previous one (e.g. folder picker over the editor).
  scrim.style.zIndex = String(50 + overlays.length * 2);
  el.style.zIndex = String(51 + overlays.length * 2);
  $('#layer').append(scrim, el);
  const entry = { el, scrim, onClose, kind };
  overlays.push(entry);
  scrim.addEventListener('click', () => closeOverlay(entry));
  setTimeout(() => { const f = el.querySelector('[autofocus]'); if (f) f.focus(); }, 30);
  return entry;
}
function closeOverlay(entry = overlays[overlays.length - 1]) {
  if (!entry) return;
  const i = overlays.indexOf(entry);
  if (i >= 0) overlays.splice(i, 1);
  entry.el.remove(); entry.scrim.remove();
  if (entry.onClose) entry.onClose();
}

function confirmDialog({ title, body, ok = 'Confirm', danger = false, iconName = 'alert' }) {
  return new Promise((resolve) => {
    let done = false;
    const entry = pushOverlay(`
      <div class="modal" style="--w:460px" role="dialog" aria-modal="true">
        <div class="modal-head"><div class="hico ${danger ? 'bad' : 'warn'}">${icon(iconName)}</div>
          <div><h2>${esc(title)}</h2><p>${body}</p></div></div>
        <div class="modal-foot">
          <button class="btn ghost" data-r="0">Cancel</button>
          <button class="btn ${danger ? 'danger' : 'primary'}" data-r="1" autofocus>${esc(ok)}</button>
        </div>
      </div>`, { onClose: () => { if (!done) resolve(false); } });
    entry.el.addEventListener('click', (e) => {
      const b = e.target.closest('[data-r]');
      if (!b) return;
      done = true; resolve(b.dataset.r === '1'); closeOverlay(entry);
    });
  });
}

// ------------------------------------------------------------------ data

async function loadStatus() {
  const s = await api('GET', '/api/status');
  state.status = s;
  state.armed = s.armed;
  state.jobs = new Map(s.jobs.map((j) => [j.id, j]));
  state.loaded = true;
  renderChrome();
  render();
}

async function loadHistory() {
  const q = state.historyJob ? `&jobId=${encodeURIComponent(state.historyJob)}` : '';
  state.history = await api('GET', `/api/history?limit=300${q}`);
  state.historyLoaded = true;
}

let es;
function connectEvents() {
  if (es) es.close();
  es = new EventSource('/api/events');
  es.onopen = () => {
    const wasDown = !state.connected;
    state.connected = true;
    renderChrome();
    if (wasDown && state.loaded) loadStatus().catch(() => {});
  };
  es.onerror = () => { state.connected = false; renderChrome(); };
  es.addEventListener('job', (e) => {
    const j = JSON.parse(e.data);
    const prev = state.jobs.get(j.id);
    state.jobs.set(j.id, j);
    if (!prev) { render(); return; }
    updateCard(j);
    scheduleHeroUpdate();
  });
  es.addEventListener('jobRemoved', (e) => {
    const { id } = JSON.parse(e.data);
    state.jobs.delete(id);
    render();
  });
  es.addEventListener('state', (e) => {
    state.armed = JSON.parse(e.data).armed;
    renderChrome();
    scheduleHeroUpdate();
  });
  es.addEventListener('compareProgress', (e) => {
    const d = JSON.parse(e.data);
    const el = $(`#cmp-scan[data-job="${d.jobId}"]`);
    if (el) el.textContent = `Scanning… ${nf.format(d.scanned)} entries`;
  });
  es.addEventListener('progress', (e) => {
    const { jobId, progress } = JSON.parse(e.data);
    const j = state.jobs.get(jobId);
    if (!j) return;
    j.progress = progress;
    updateProgress(j);
  });
  es.addEventListener('run', (e) => {
    const r = JSON.parse(e.data);
    if (state.historyLoaded && (!state.historyJob || state.historyJob === r.jobId)) state.history.unshift(r);
    if (state.view === 'activity') renderView();
    if (r.status === 'failed' || r.status === 'degraded') toast('bad', `${r.jobName} failed`, r.message, 7000);
    else if (r.status === 'waitingConfirm') toast('warn', `${r.jobName} needs review`, r.message, 7000);
    else if (r.trigger === 'manual' || r.trigger === 'confirm') toast(r.status === 'success' ? 'ok' : 'warn', `${r.jobName} finished`, r.message);
    scheduleHeroUpdate();
  });
}

// ------------------------------------------------------------------ chrome (sidebar, banner, nav)

function applyThemeButtons() {
  let choice = 'system';
  try { choice = localStorage.getItem('bm-theme') || 'system'; } catch { /* ignore */ }
  $$('[data-theme-choice]').forEach((b) => b.classList.toggle('on', b.dataset.themeChoice === choice));
}
function setTheme(choice) {
  try { localStorage.setItem('bm-theme', choice); } catch { /* ignore */ }
  if (choice === 'system') document.documentElement.removeAttribute('data-theme');
  else document.documentElement.setAttribute('data-theme', choice);
  applyThemeButtons();
}

function renderChrome() {
  const s = state.status;
  $('#brand-host').textContent = s ? `${s.machine} · v${s.version}` : 'connecting…';
  const pill = $('#service-pill');
  pill.className = 'service-pill ' + (state.connected ? 'ok' : 'bad');
  pill.querySelector('.label').textContent = state.connected
    ? (state.armed ? 'Service running' : 'Service running · paused')
    : 'Service unreachable';
  $('#conn-banner').hidden = state.connected || !state.loaded;
  const attention = sortedJobs().filter(needsAttention).length;
  const overviewLink = $('#nav a[data-view="overview"]');
  let c = overviewLink.querySelector('.count');
  if (attention) {
    if (!c) { c = document.createElement('span'); c.className = 'count'; overviewLink.appendChild(c); }
    c.textContent = attention;
  } else if (c) c.remove();
  document.title = attention ? `(${attention}) Backup Mirror` : 'Backup Mirror';
}

const VIEWS = {
  overview: { title: 'Overview', sub: () => greeting() },
  activity: { title: 'Activity', sub: () => 'Every sync that changed something, newest first.' },
  logs: { title: 'Service log', sub: () => 'Live diagnostics from the Backup Mirror service.' },
  settings: { title: 'Settings', sub: () => 'Service details, access and appearance.' },
};

function greeting() {
  const h = new Date().getHours();
  const part = h < 5 ? 'Working late' : h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
  return `${part}. Here's how your mirrors are doing.`;
}

function route() {
  const v = (location.hash.replace(/^#\/?/, '') || 'overview').split('?')[0];
  state.view = VIEWS[v] ? v : 'overview';
  $$('#nav a').forEach((a) => a.classList.toggle('active', a.dataset.view === state.view));
  $('#sidebar').classList.remove('open');
  render();
}

// ------------------------------------------------------------------ rendering

function render() {
  const v = VIEWS[state.view];
  $('#view-title').textContent = v.title;
  $('#view-sub').textContent = v.sub();
  renderTopActions();
  renderView();
  renderChrome();
}

function renderTopActions() {
  const el = $('#topbar-actions');
  if (state.view === 'overview' && state.loaded) {
    el.innerHTML = `
      <button class="btn" data-action="sync-all" ${!state.armed || !state.jobs.size ? 'disabled' : ''}>${icon('refresh')}<span>Sync all</span></button>
      <button class="btn primary" data-action="new-job">${icon('plus')}<span>New mirror</span></button>`;
  } else el.innerHTML = '';
}

function renderView() {
  const view = $('#view');
  if (!state.loaded) {
    view.innerHTML = `<div class="skeleton" style="height:170px;margin-bottom:18px"></div><div class="jobs"><div class="skeleton"></div><div class="skeleton"></div></div>`;
    return;
  }
  switch (state.view) {
    case 'overview': view.innerHTML = overviewHtml(); break;
    case 'activity': renderActivity(view); break;
    case 'logs': renderLogs(view); break;
    case 'settings': view.innerHTML = settingsHtml(); break;
  }
}

// ---- overview

function heroModel() {
  const jobs = sortedJobs();
  const enabled = jobs.filter((j) => j.enabled);
  const running = enabled.filter((j) => j.status === 'running' || j.status === 'queued');
  const attention = enabled.filter(needsAttention);
  const failed = attention.filter((j) => j.status !== 'waitingConfirm');
  const lastOk = jobs.map((j) => j.lastSuccessUtc).filter(Boolean).sort().pop();
  const healthy = enabled.filter((j) => ['success', 'idle', 'running', 'queued'].includes(j.status)).length;
  const frac = enabled.length ? healthy / enabled.length : 1;

  if (!jobs.length) return { cls: 'state-ok', ic: 'shield', frac: 0, title: "Let's protect your first folder", text: 'Create a mirror to keep an exact, always-current copy of a folder on another drive or a network share.' };
  if (!state.armed) return { cls: 'state-paused', ic: 'pause', frac: 0, title: 'Paused — nothing is syncing', text: 'Your mirrors are on hold. Anything that changed meanwhile is caught up as soon as you resume.' };
  if (failed.length) return { cls: 'state-bad', ic: 'alert', frac, title: `${failed.length} mirror${failed.length > 1 ? 's' : ''} ${failed.length > 1 ? 'need' : 'needs'} attention`, text: failed.map((j) => `${j.name}: ${j.message || STATUS[j.status].label}`).join(' · ') };
  if (attention.length) return { cls: 'state-warn', ic: 'alert', frac, title: `${attention.length} mirror${attention.length > 1 ? 's are' : ' is'} waiting for you`, text: 'A large number of files would be deleted from a destination. Review and confirm to continue.' };
  if (running.length) return { cls: 'state-run', ic: 'refresh', frac, title: `Syncing ${running.length} mirror${running.length > 1 ? 's' : ''}…`, text: running.map((j) => j.name).join(', ') };
  return {
    cls: 'state-ok', ic: 'shield', frac: 1,
    title: enabled.length === jobs.length ? (jobs.length === 1 ? 'Your mirror is protected' : `All ${jobs.length} mirrors are protected`) : `${enabled.length} of ${jobs.length} mirrors active`,
    text: lastOk ? `Last successful sync ${fmtRel(lastOk)}. Changes are mirrored automatically.` : 'Everything is configured. Run a first sync whenever you are ready.',
  };
}

function heroHtml() {
  const m = heroModel();
  const C = 2 * Math.PI * 46;
  return `
    <div class="hero ${m.cls}" id="hero">
      <div class="ring">
        <svg viewBox="0 0 108 108">
          <circle class="orbit" cx="54" cy="54" r="52"/>
          <circle class="track" cx="54" cy="54" r="46"/>
          <circle class="arc" cx="54" cy="54" r="46" stroke-dasharray="${C}" stroke-dashoffset="${C * (1 - Math.max(0.02, m.frac))}" transform="rotate(-90 54 54)"/>
        </svg>
        <div class="core">${icon(m.ic)}</div>
      </div>
      <div><h2>${esc(m.title)}</h2><p>${esc(m.text)}</p></div>
      <div class="hero-actions">
        ${state.jobs.size ? (state.armed
          ? `<button class="btn lg" data-action="arm" data-v="0">${icon('pause')}Pause all</button>`
          : `<button class="btn primary lg" data-action="arm" data-v="1">${icon('play')}Resume all</button>`)
          : `<button class="btn primary lg" data-action="new-job">${icon('plus')}New mirror</button>`}
      </div>
    </div>`;
}

function statsHtml() {
  const jobs = sortedJobs();
  const dayAgo = Date.now() - 864e5;
  const recent = state.history.filter((r) => new Date(r.startedUtc).getTime() > dayAgo);
  const bytes = recent.reduce((a, r) => a + (r.bytesCopied || 0), 0);
  const files = recent.reduce((a, r) => a + (r.filesCopied || 0), 0);
  const lastOk = jobs.map((j) => j.lastSuccessUtc).filter(Boolean).sort().pop();
  const next = jobs.map((j) => j.nextRunUtc).filter(Boolean).sort()[0];
  const anyRealtime = jobs.some((j) => j.enabled && j.mode === 'realtime');
  const stat = (c, ic, v, l, title = '') => `<div class="stat" title="${esc(title)}"><div class="ico ${c}">${icon(ic)}</div><div><div class="v">${v}</div><div class="l">${l}</div></div></div>`;
  return `<div class="stats" id="stats">
    ${stat('c1', 'folder', `${jobs.filter((j) => j.enabled).length}<span class="faint" style="font-size:15px"> / ${jobs.length}</span>`, 'Active mirrors')}
    ${stat('c2', 'activity', state.historyLoaded ? fmtBytes(bytes) : '—', 'Copied in 24 h', `${nf.format(files)} files`)}
    ${stat('c3', 'check', lastOk ? esc(fmtRel(lastOk)) : 'Never', 'Last good sync', lastOk ? new Date(lastOk).toLocaleString() : '')}
    ${stat('c4', next ? 'calendar' : 'zap', next ? esc(fmtRel(next, { future: true })) : anyRealtime ? 'Live' : '—', next ? 'Next scheduled' : 'Realtime', next ? new Date(next).toLocaleString() : '')}
  </div>`;
}

let heroRaf = 0;
function scheduleHeroUpdate() {
  if (heroRaf) return;
  heroRaf = requestAnimationFrame(() => {
    heroRaf = 0;
    renderChrome();
    renderTopActions();
    if (state.view !== 'overview') return;
    const hero = $('#hero'), stats = $('#stats');
    if (hero) hero.outerHTML = heroHtml();
    if (stats) stats.outerHTML = statsHtml();
  });
}

// ---- job collection: layout, filters, search

const currentLayout = () => state.layout || (state.jobs.size > 4 ? 'list' : 'cards');
const isRunning = (j) => j.status === 'running' || j.status === 'queued';
const FILTERS = {
  all: { label: 'All', test: () => true },
  attention: { label: 'Needs attention', test: (j) => needsAttention(j) || (j.enabled && j.status === 'warning') },
  running: { label: 'Syncing', test: isRunning },
  disabled: { label: 'Paused', test: (j) => !j.enabled },
};

function matchesView(j) {
  if (!(FILTERS[state.jobFilter] || FILTERS.all).test(j)) return false;
  const q = state.jobQuery.trim().toLowerCase();
  return !q || [j.name, j.sourcePath, j.destinationPath].some((v) => (v || '').toLowerCase().includes(q));
}

// Problems first, then running, then the rest; disabled last. Alphabetical within each group.
function rank(j) { return !j.enabled ? 3 : needsAttention(j) ? 0 : isRunning(j) ? 1 : 2; }
const viewJobs = () => sortedJobs().filter(matchesView).sort((a, b) => rank(a) - rank(b));

function jobsToolbarHtml() {
  const all = sortedJobs();
  const chips = Object.entries(FILTERS).map(([k, f]) => {
    const n = all.filter(f.test).length;
    if (k !== 'all' && n === 0 && state.jobFilter !== k) return '';
    return `<button class="fchip ${state.jobFilter === k ? 'on' : ''} f-${k}" data-action="filter" data-v="${k}">${f.label}<span>${n}</span></button>`;
  }).join('');
  const lay = currentLayout();
  return `<div class="jobs-toolbar">
    <h3>Mirrors</h3>
    <div class="fchips">${chips}</div>
    <span class="spacer"></span>
    <div class="search sm">${icon('search')}<input class="input sm" id="job-search" placeholder="Search mirrors…" value="${esc(state.jobQuery)}" autocomplete="off"></div>
    <div class="seg" role="group" aria-label="Layout">
      <button class="${lay === 'list' ? 'on' : ''}" data-action="layout" data-v="list" title="Compact list">${icon('list')}</button>
      <button class="${lay === 'cards' ? 'on' : ''}" data-action="layout" data-v="cards" title="Cards">${icon('grid')}</button>
    </div>
  </div>`;
}

function jobsHtml() {
  const jobs = viewJobs();
  if (!jobs.length) {
    return `<div class="no-match">${icon('search')}<span>No mirrors match${state.jobQuery ? ` “${esc(state.jobQuery)}”` : ' this filter'}.</span>
      <button class="btn sm ghost" data-action="clear-filter">Show all</button></div>`;
  }
  return currentLayout() === 'list'
    ? `<div class="jlist">${jobs.map(jobRowHtml).join('')}</div>`
    : `<div class="jobs">${jobs.map(jobCardHtml).join('')}</div>`;
}

const jobHtml = (j) => (currentLayout() === 'list' ? jobRowHtml(j) : jobCardHtml(j));

function renderJobs() {
  const wrap = $('#jobs-wrap');
  if (!wrap) return;
  wrap.innerHTML = jobsHtml();
  const tb = $('#jobs-toolbar-wrap');
  if (tb) {
    const focused = document.activeElement?.id === 'job-search';
    const caret = focused ? document.activeElement.selectionStart : 0;
    tb.innerHTML = jobsToolbarHtml();
    if (focused) { const i = $('#job-search'); i.focus(); i.setSelectionRange(caret, caret); }
  }
}

function overviewHtml() {
  if (!state.jobs.size) return `${heroHtml()}${statsHtml()}${emptyHtml()}`;
  return `${heroHtml()}${statsHtml()}
    <div id="jobs-toolbar-wrap">${jobsToolbarHtml()}</div>
    <div id="jobs-wrap">${jobsHtml()}</div>`;
}

function statusLabel(j) {
  if (!j.enabled) return 'Paused';
  if (j.status === 'idle' && !j.lastRunUtc) return 'Not run yet';
  return (STATUS[j.status] || STATUS.idle).label;
}

function progressPct(p) {
  if (!p || !p.filesTotal || p.phase === 'scanning') return null;
  return p.bytesTotal > 0 ? Math.min(100, (p.bytesDone / p.bytesTotal) * 100) : (p.filesDone / p.filesTotal) * 100;
}

function rowWhen(j) {
  if (j.status === 'queued') return ['Queued', 'Waiting for a free slot'];
  if (j.status === 'running') {
    const p = j.progress, pct = progressPct(p);
    if (pct == null) return ['Comparing…', 'Scanning folders'];
    return [`${pct.toFixed(0)}% · ${fmtBytes(p.bytesDone)}`, `${nf.format(p.filesDone)}/${nf.format(p.filesTotal)} files${p.bytesPerSecond > 1024 ? ` · ${fmtBytes(p.bytesPerSecond)}/s` : ''}`];
  }
  const last = j.lastRunUtc ? fmtRel(j.lastRunUtc) : 'Never synced';
  const sub = j.mode === 'scheduled'
    ? (j.nextRunUtc ? `Next ${fmtRel(j.nextRunUtc, { future: true })}` : 'No upcoming run')
    : j.mode === 'realtime' ? (j.enabled && state.armed ? 'Watching' : 'Not watching') : 'Manual';
  return [last, sub];
}

function jobRowHtml(j) {
  const sk = jobStatusKey(j);
  const mode = MODES[j.mode] || MODES.manualOnly;
  const running = isRunning(j);
  const canSync = j.enabled && state.armed;
  const [w1, w2] = rowWhen(j);
  const problem = j.enabled && ['failed', 'degraded', 'warning'].includes(j.status) && j.message;
  const pct = progressPct(j.progress);
  const detail = problem
    ? `<div class="jr-sub bad" title="${esc(j.sourcePath)} → ${esc(j.destinationPath)}">${esc(j.message)}</div>`
    : `<div class="jr-sub jr-path" title="${esc(j.sourcePath)}  →  ${esc(j.destinationPath)}"><bdi>${esc(j.sourcePath)}</bdi><span class="arr">→</span><bdi>${esc(j.destinationPath)}</bdi></div>`;
  return `
  <div class="jrow st-${j.status} ${j.enabled ? '' : 'disabled'}" data-job="${j.id}">
    <div class="mode-tile sm mode-${j.mode}" title="${esc(mode.label)}${j.mode === 'scheduled' ? ' · ' + esc(scheduleText(j)) : ''}">${icon(mode.icon)}</div>
    <div class="jr-main">
      <div class="jr-name"><span title="${esc(j.name)}">${esc(j.name)}</span>${j.excludes.length ? `<span class="jr-tag" title="Excludes: ${esc(j.excludes.join(', '))}">${icon('ban')}${j.excludes.length}</span>` : ''}</div>
      ${detail}
    </div>
    <div class="jr-when"><div>${esc(w1)}</div><div class="sub">${esc(w2)}</div></div>
    <div class="jr-status"><span class="badge ${sk}" title="${esc(j.message || '')}"><span class="dot"></span>${esc(statusLabel(j))}</span></div>
    <div class="jr-actions">
      ${j.enabled && j.status === 'waitingConfirm' ? `<button class="btn warn sm" data-action="review" data-id="${j.id}">${icon('alert')}Review ${nf.format(j.pendingDeleteCount)}</button>` : ''}
      ${running
        ? `<button class="icon-btn" data-action="cancel" data-id="${j.id}" title="Stop">${icon('stop')}</button>`
        : `<button class="icon-btn" data-action="compare" data-id="${j.id}" title="Compare: see every change before it happens">${icon('diff')}</button>${canSync
          ? `<button class="icon-btn accent" data-action="sync" data-id="${j.id}" title="Sync now">${icon('refresh')}</button>`
          : `<button class="icon-btn resume" data-action="resume-job" data-id="${j.id}" title="Resume this mirror">${icon('play')}</button>`}`}
      <div class="menu-wrap"><button class="icon-btn" data-action="menu" data-id="${j.id}" aria-label="More actions">${icon('more')}</button></div>
    </div>
    <div class="jr-bar ${running && pct == null ? 'indeterminate' : ''}" ${running ? '' : 'hidden'}><i style="width:${(pct || 0).toFixed(1)}%"></i></div>
  </div>`;
}

function emptyHtml() {
  return `<div class="empty">
    <div class="empty-art"><svg viewBox="0 0 180 120">
      <defs><linearGradient id="eg" x1="0" x2="1" y1="0" y2="1"><stop offset="0" stop-color="#22d3ee"/><stop offset="1" stop-color="#8b5cf6"/></linearGradient></defs>
      <rect x="14" y="30" width="64" height="50" rx="10" fill="url(#eg)" opacity=".9"/>
      <path d="M14 40a10 10 0 0 1 10-10h14l6 7h0" fill="url(#eg)"/>
      <rect x="102" y="30" width="64" height="50" rx="10" fill="none" stroke="url(#eg)" stroke-width="2.5" stroke-dasharray="6 5"/>
      <path d="M84 55h12m-5-5 5 5-5 5" stroke="url(#eg)" stroke-width="3" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
      <circle cx="46" cy="98" r="3" fill="#22d3ee" opacity=".5"/><circle cx="134" cy="98" r="3" fill="#8b5cf6" opacity=".5"/>
    </svg></div>
    <h3>No mirrors yet</h3>
    <p>A mirror keeps an exact copy of a folder somewhere else: another disk, a NAS or a network share. New and changed files are copied; deleted files are removed.</p>
    <button class="btn primary lg" data-action="new-job">${icon('plus')}Create your first mirror</button>
  </div>`;
}

function whenLines(j) {
  const lines = [];
  if (j.status === 'running' && j.progress) lines.push(`Started ${fmtRel(j.progress.startedUtc)}`);
  else if (j.lastRunUtc) lines.push(`Last sync ${fmtRel(j.lastRunUtc)}`);
  else lines.push('Never synced');
  if (j.mode === 'scheduled') lines.push(j.nextRunUtc ? `Next ${fmtRel(j.nextRunUtc, { future: true }).replace(/^in /, 'in ')} · ${scheduleText(j)}` : scheduleText(j));
  else if (j.mode === 'realtime') lines.push(j.enabled && state.armed ? 'Watching for changes' : 'Not watching');
  else lines.push('Runs when you press Sync now');
  return lines;
}

function jobCardHtml(j) {
  const sk = jobStatusKey(j);
  const st = sk === 'disabled' ? { label: 'Paused' }
    : j.status === 'idle' && !j.lastRunUtc ? { label: 'Not run yet' } : STATUS[j.status] || STATUS.idle;
  const mode = MODES[j.mode] || MODES.manualOnly;
  const running = j.status === 'running' || j.status === 'queued';
  const canSync = j.enabled && state.armed;
  const [l1, l2] = whenLines(j);
  let callout = '';
  if (j.enabled && j.status === 'waitingConfirm') {
    callout = `<div class="callout warn">${icon('alert')}<div class="grow"><b>${nf.format(j.pendingDeleteCount)} files</b> would be deleted from the destination. Nothing has been removed yet.</div>
      <button class="btn warn sm" data-action="review" data-id="${j.id}">Review</button></div>`;
  } else if (j.enabled && ['failed', 'degraded'].includes(j.status) && j.message) {
    callout = `<div class="callout bad">${icon(j.status === 'degraded' ? 'plug' : 'x')}<div class="grow">${esc(j.message)}</div></div>`;
  } else if (j.enabled && j.status === 'warning' && j.message) {
    callout = `<div class="callout warn">${icon('alert')}<div class="grow">${esc(j.message)}</div></div>`;
  }
  const isUncDest = isUnc(j.destinationPath);

  return `
  <article class="job st-${j.status} ${j.enabled ? '' : 'disabled'}" data-job="${j.id}">
    <div class="job-head">
      <div class="mode-tile mode-${j.mode}" title="${esc(mode.label)}">${icon(mode.icon)}</div>
      <div class="job-title">
        <h3 title="${esc(j.name)}">${esc(j.name)}</h3>
        <div class="job-meta"><span>${esc(mode.label)}</span>${j.direction === 'destinationToSource' ? '<span class="sep"></span><span class="rev-tag">Sync to Destination</span>' : ''}${j.excludes.length ? `<span class="sep"></span><span title="${esc(j.excludes.join(', '))}">${j.excludes.length} exclusion${j.excludes.length > 1 ? 's' : ''}</span>` : ''}${j.lastRun && j.lastRun.filesScanned ? `<span class="sep"></span><span>${nf.format(j.lastRun.filesScanned)} files</span>` : ''}</div>
      </div>
      <span class="badge ${sk}"><span class="dot"></span>${esc(st.label)}</span>
      <div class="menu-wrap">
        <button class="icon-btn" data-action="menu" data-id="${j.id}" aria-label="More actions">${icon('more')}</button>
      </div>
    </div>
    <div class="flow">
      <div class="endpoint"><div class="lbl">${icon('folder')}Source</div><div class="p" title="${esc(j.sourcePath)}"><bdi>${esc(j.sourcePath)}</bdi></div></div>
      <div class="flow-arrow"></div>
      <div class="endpoint"><div class="lbl">${icon(isUncDest ? 'server' : 'drive')}Mirror</div><div class="p" title="${esc(j.destinationPath)}"><bdi>${esc(j.destinationPath)}</bdi></div></div>
    </div>
    <div class="progress" ${running ? '' : 'hidden'}>${progressInner(j)}</div>
    ${callout}
    <div class="job-foot">
      <label class="switch" title="${j.enabled ? 'Pause this mirror' : 'Resume this mirror'}">
        <input type="checkbox" data-action="toggle" data-id="${j.id}" ${j.enabled ? 'checked' : ''}><span class="track"></span>
      </label>
      <div class="when"><div>${esc(l1)}</div><div class="sub">${esc(l2)}</div></div>
      ${running
        ? `<button class="btn sm" data-action="cancel" data-id="${j.id}">${icon('stop')}Stop</button>`
        : `<button class="btn sm ghost" data-action="compare" data-id="${j.id}" title="See every change before it happens">${icon('diff')}Compare</button>${canSync
          ? `<button class="btn sm primary" data-action="sync" data-id="${j.id}">${icon('refresh')}Sync now</button>`
          : `<button class="btn sm resume" data-action="resume-job" data-id="${j.id}">${icon('play')}Resume</button>`}`}
    </div>
  </article>`;
}

function progressInner(j) {
  const p = j.progress;
  if (j.status === 'queued') return `<div class="bar indeterminate"><i></i></div><div class="progress-meta"><span>Waiting for another mirror to finish…</span></div>`;
  if (!p || p.phase === 'scanning' || !p.filesTotal) {
    return `<div class="bar indeterminate"><i></i></div><div class="progress-meta"><span>${p && p.phase !== 'scanning' ? 'Finishing up…' : 'Comparing folders…'}</span></div>`;
  }
  const pct = p.bytesTotal > 0 ? Math.min(100, (p.bytesDone / p.bytesTotal) * 100) : (p.filesDone / p.filesTotal) * 100;
  const phase = p.phase === 'deleting' ? 'Removing deleted files' : `<b>${nf.format(p.filesDone)}</b> of <b>${nf.format(p.filesTotal)}</b> files`;
  return `<div class="bar"><i style="width:${pct.toFixed(1)}%"></i></div>
    <div class="progress-meta"><span>${phase}</span><span><b>${fmtBytes(p.bytesDone)}</b> / ${fmtBytes(p.bytesTotal)}${p.bytesPerSecond > 1024 ? ` · ${fmtBytes(p.bytesPerSecond)}/s` : ''}</span></div>
    ${p.currentFile ? `<div class="progress-file" title="${esc(p.currentFile)}">${esc(p.currentFile)}</div>` : ''}`;
}

function updateCard(j) {
  if (state.view !== 'overview') return;
  if (!$('#jobs-wrap')) { renderView(); return; }
  const el = $(`[data-job="${j.id}"]`);
  // Joined or left the current filter: redraw the collection (also refreshes filter counts).
  if (!el || !matchesView(j)) { renderJobs(); return; }
  if ($(`.menu[data-for="${j.id}"]`)) return; // don't yank an open menu away
  const tb = $('#jobs-toolbar-wrap');
  if (tb && !tb.contains(document.activeElement)) tb.innerHTML = jobsToolbarHtml();
  const tmp = document.createElement('div');
  tmp.innerHTML = jobHtml(j);
  const fresh = tmp.firstElementChild;
  fresh.style.animation = 'none';
  el.replaceWith(fresh);
}

function updateProgress(j) {
  if (state.view !== 'overview') return;
  const row = $(`.jrow[data-job="${j.id}"]`);
  if (row) {
    const bar = row.querySelector('.jr-bar');
    if (bar.hidden) { updateCard(j); return; }
    const pct = progressPct(j.progress);
    bar.classList.toggle('indeterminate', pct == null);
    bar.querySelector('i').style.width = (pct || 0).toFixed(1) + '%';
    const [w1, w2] = rowWhen(j);
    row.querySelector('.jr-when').innerHTML = `<div>${esc(w1)}</div><div class="sub">${esc(w2)}</div>`;
    return;
  }
  const el = $(`.job[data-job="${j.id}"] .progress`);
  if (!el) return;
  el.hidden = false;
  const bar = el.querySelector('.bar:not(.indeterminate) > i');
  const p = j.progress;
  if (bar && p && p.filesTotal && p.phase !== 'scanning') {
    // Update in place so the bar animates smoothly instead of re-rendering.
    const pct = p.bytesTotal > 0 ? Math.min(100, (p.bytesDone / p.bytesTotal) * 100) : (p.filesDone / p.filesTotal) * 100;
    bar.style.width = pct.toFixed(1) + '%';
    const tmp = document.createElement('div');
    tmp.innerHTML = progressInner(j);
    const meta = tmp.querySelector('.progress-meta'), file = tmp.querySelector('.progress-file');
    el.querySelector('.progress-meta').replaceWith(meta);
    const oldFile = el.querySelector('.progress-file');
    if (oldFile) oldFile.remove();
    if (file) el.appendChild(file);
  } else el.innerHTML = progressInner(j);
}

function openJobMenu(btn, id) {
  closeMenus();
  const j = state.jobs.get(id);
  if (!j) return;
  const menu = document.createElement('div');
  menu.className = 'menu';
  menu.innerHTML = `
    <button data-action="compare" data-id="${id}">${icon('diff')}Compare…</button>
    <button data-action="edit" data-id="${id}">${icon('edit')}Edit mirror</button>
    <button data-action="history" data-id="${id}">${icon('activity')}View activity</button>
    ${j.enabled && state.armed
      ? `<button data-action="toggle-menu" data-id="${id}">${icon('pause')}Pause this mirror</button>`
      : `<button data-action="resume-job" data-id="${id}">${icon('play')}Resume this mirror</button>`}
    ${j.pendingDeleteCount ? `<button data-action="review" data-id="${id}">${icon('alert')}Review deletes</button>` : ''}
    <hr><button class="danger" data-action="delete" data-id="${id}">${icon('trash')}Delete mirror</button>`;
  // Floating layer on <body>: never clipped by the list's rounded, overflow-hidden container.
  menu.classList.add('floating');
  menu.dataset.for = id;
  document.body.appendChild(menu);
  const r = btn.getBoundingClientRect(), m = menu.getBoundingClientRect();
  const left = Math.max(8, Math.min(r.right - m.width, innerWidth - m.width - 8));
  const below = r.bottom + 6, above = r.top - m.height - 6;
  menu.style.left = `${left}px`;
  // Prefer below, then above; on very short windows keep it fully on screen (it may cover the button).
  const top = below + m.height <= innerHeight - 8 ? below : above >= 8 ? above : Math.max(8, innerHeight - m.height - 8);
  menu.style.top = `${top}px`;
  btn.classList.add('menu-open');
}
function closeMenus() {
  $$('.menu').forEach((m) => m.remove());
  $$('.menu-open').forEach((b) => b.classList.remove('menu-open'));
}
// A floating menu would drift away from its button; close it instead.
window.addEventListener('scroll', () => { if ($('.menu.floating')) closeMenus(); }, true);
window.addEventListener('resize', closeMenus);

// ---- activity

async function renderActivity(view) {
  const jobs = sortedJobs();
  view.innerHTML = `<div class="panel">
    <div class="toolbar">
      <select class="input sm" id="hist-job" style="width:auto;min-width:200px">
        <option value="">All mirrors</option>
        ${jobs.map((j) => `<option value="${j.id}" ${state.historyJob === j.id ? 'selected' : ''}>${esc(j.name)}</option>`).join('')}
      </select>
      <span class="spacer" style="flex:1"></span>
      <button class="btn sm ghost" data-action="refresh-history">${icon('refresh')}Refresh</button>
    </div>
    <div class="timeline" id="timeline">${state.historyLoaded ? timelineHtml() : '<div class="picker-empty"><span class="spinner"></span></div>'}</div>
  </div>`;
  if (!state.historyLoaded) {
    try { await loadHistory(); } catch (e) { toast('bad', 'Could not load activity', e.message); }
    const tl = $('#timeline');
    if (tl) tl.innerHTML = timelineHtml();
  }
}

function timelineHtml() {
  if (!state.history.length) return `<div class="picker-empty">${icon('activity')}<p>No activity yet. Runs that copy or delete something show up here.</p></div>`;
  let day = '';
  return state.history.map((r) => {
    const d = fmtDay(r.startedUtc);
    const head = d !== day ? `<div class="tl-day">${esc(d)}</div>` : '';
    day = d;
    const st = STATUS[r.status] || STATUS.idle;
    const dur = r.finishedUtc ? fmtDuration(new Date(r.finishedUtc) - new Date(r.startedUtc)) : '';
    const nums = [];
    if (r.filesCopied) nums.push(`<span><b>${nf.format(r.filesCopied)}</b> copied</span><span><b>${fmtBytes(r.bytesCopied)}</b></span>`);
    if (r.filesDeleted) nums.push(`<span><b>${nf.format(r.filesDeleted)}</b> deleted</span>`);
    if (r.deletesHeld) nums.push(`<span><b>${nf.format(r.deletesHeld)}</b> deletes held</span>`);
    if (r.filesScanned) nums.push(`<span><b>${nf.format(r.filesScanned)}</b> scanned</span>`);
    if (r.errors) nums.push(`<span style="color:var(--bad)"><b style="color:inherit">${nf.format(r.errors)}</b> problems</span>`);
    return `${head}<div class="tl-item ${r.status}">
      <div class="tl-ico">${icon(st.icon)}</div>
      <div><div class="tl-title">${esc(r.jobName)}<span class="tag">${esc(TRIGGERS[r.trigger] || r.trigger)}</span></div>
        <div class="tl-msg">${esc(r.message || st.label)}</div>
        ${nums.length ? `<div class="tl-nums">${nums.join('')}</div>` : ''}
        ${r.errorSamples && r.errorSamples.length ? `<div class="tl-errors">${r.errorSamples.slice(0, 5).map((e) => `<div>${esc(e)}</div>`).join('')}</div>` : ''}
      </div>
      <div class="tl-time">${fmtClock(r.startedUtc)}<div>${esc(dur)}</div></div>
    </div>`;
  }).join('');
}

// ---- logs

let logTimer = 0;
async function renderLogs(view) {
  view.innerHTML = `<div class="panel">
    <div class="toolbar">
      <div class="search">${icon('search')}<input class="input sm" id="log-filter" placeholder="Filter log lines…" value="${esc(state.logFilter)}"></div>
      <label class="switch"><input type="checkbox" id="log-auto" ${state.logAuto ? 'checked' : ''}><span class="track"></span>Live</label>
      <span style="flex:1"></span>
      <button class="btn sm ghost" data-action="refresh-logs">${icon('refresh')}Refresh</button>
    </div>
    <div class="term" id="term"><div class="faint">Loading…</div></div>
  </div>`;
  await refreshLogs(true);
}

async function refreshLogs(scroll) {
  try { state.logs = await api('GET', '/api/logs?lines=600'); } catch (e) { state.logs = [`ERROR ${e.message}`]; }
  const term = $('#term');
  if (!term) return;
  const atBottom = term.scrollTop + term.clientHeight >= term.scrollHeight - 30;
  const f = state.logFilter.toLowerCase();
  const lines = state.logs.filter((l) => !f || l.toLowerCase().includes(f));
  term.innerHTML = lines.length ? lines.map((l) => {
    const m = /^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) (\w+) ([^:]+): (.*)$/s.exec(l);
    if (!m) return `<div>${esc(l)}</div>`;
    return `<div><span class="ts">${esc(m[1])}</span> <span class="lv ${esc(m[2])}">${esc(m[2])}</span><span class="cat">${esc(m[3])}</span> ${esc(m[4])}</div>`;
  }).join('') : '<div class="faint">No log lines match.</div>';
  if (scroll || atBottom) term.scrollTop = term.scrollHeight;
}

function logTick() {
  clearTimeout(logTimer);
  logTimer = setTimeout(async () => {
    if (state.view === 'logs' && state.logAuto && !document.hidden) await refreshLogs(false);
    logTick();
  }, 3000);
}

// ---- settings

function settingsHtml() {
  const s = state.status;
  const uptime = fmtDuration(Date.now() - new Date(s.startedUtc).getTime());
  const isWin = serverIsWindows();
  const keyCmd = isWin
    ? `type "${s.dataRoot}\\access.key"`
    : `sudo cat ${s.dataRoot}/access.key`;
  const settingsPath = isWin ? `${s.dataRoot}\\settings.json` : `${s.dataRoot}/settings.json`;
  return `<div class="cards">
    <div class="card"><h3>${icon('server')}Service</h3>
      <dl class="kv">
        <dt>Version</dt><dd>${esc(s.version)}</dd>
        <dt>Machine</dt><dd>${esc(s.machine)}</dd>
        <dt>Operating system</dt><dd>${esc(s.os)}</dd>
        <dt>Running for</dt><dd>${esc(uptime)}</dd>
        <dt>Data folder</dt><dd class="mono">${esc(s.dataRoot)}</dd>
        <dt>Jobs</dt><dd>${state.jobs.size} configured · ${state.armed ? 'running' : 'paused'}</dd>
      </dl>
    </div>
    <div class="card"><h3>${icon('key')}Dashboard access</h3>
      <p class="muted" style="margin:0">${s.remoteAccessEnabled
        ? 'Remote access is <b>on</b>. Other machines can open this dashboard after signing in with the access key.'
        : 'The dashboard only listens on this machine (<span class="mono">127.0.0.1</span>). That is the safest default.'}</p>
      <div class="hint">To reach it from another computer, set <span class="mono">"BackupMirror": { "Urls": "http://0.0.0.0:8737" }</span> in:</div>
      <div class="code">${esc(settingsPath)}</div>
      <div class="hint">then restart the service and sign in with the key shown by:</div>
      <div class="code">${esc(keyCmd)}</div>
      ${s.remoteClient ? `<button class="btn sm" data-action="logout" style="align-self:flex-start">${icon('logout')}Sign out</button>` : ''}
    </div>
    <div class="card"><h3>${icon('sun')}Appearance</h3>
      <div class="theme-switch" style="max-width:300px">
        <button data-theme-choice="light">${icon('sun')}</button><button data-theme-choice="system">${icon('monitor')}</button><button data-theme-choice="dark">${icon('moon')}</button>
      </div>
      <div class="hint">Light, match your system, or dark. Remembered in this browser.</div>
    </div>
    <div class="card"><h3>${icon('info')}How mirroring works</h3>
      <ul class="muted" style="margin:0;padding-left:18px;display:flex;flex-direction:column;gap:6px">
        <li>One-way: the source always wins. New and changed files are copied; files removed from the source are removed from the mirror.</li>
        <li>Files are copied to a temporary name and swapped in when complete, so an interrupted copy never leaves a broken file.</li>
        <li>If a run would delete more files than the job's safety limit, it waits for you to review first.</li>
        <li>Folders that can't be read are skipped and reported; their mirrored copies are never deleted.</li>
      </ul>
    </div>
    <div class="card"><h3>${icon('grid')}Keyboard</h3>
      <dl class="kv"><dt><kbd>N</kbd></dt><dd>New mirror</dd><dt><kbd>/</kbd></dt><dd>Search mirrors</dd><dt><kbd>Esc</kbd></dt><dd>Close dialog</dd><dt><kbd>Ctrl</kbd> + <kbd>Enter</kbd></dt><dd>Save the mirror being edited</dd></dl>
    </div>
  </div>`;
}

// ------------------------------------------------------------------ editor

function openEditor(job) {
  const isNew = !job;
  const f = {
    name: job?.name || '',
    enabled: job ? job.enabled : true,
    sourcePath: job?.sourcePath || '',
    destBase: job?.destinationPath || '',
    appendName: isNew,
    userName: job?.destinationUserName || '',
    password: null,
    hasPassword: !!job?.hasPassword,
    mode: job?.mode || 'realtime',
    times: job ? [...job.schedule.times] : ['02:00'],
    days: job ? [...job.schedule.days] : [],
    excludes: job ? [...job.excludes] : [],
    threshold: job ? job.deleteConfirmThreshold : 10,
    direction: job?.direction || 'sourceToDestination',
    includeSubfolders: job ? job.includeSubfolders : true,
    includeHidden: job ? job.includeHidden : true,
    copyMissing: job ? job.copyMissing : true,
    updateChanged: job ? job.updateChanged : true,
    deleteExtra: job ? job.deleteExtra : true,
    keepDays: job ? job.keepRemovedDays : 30,
    nameTouched: !!job,
  };
  if (!f.times.length) f.times = ['02:00'];
  const destination = () => (f.appendName && f.sourcePath && f.destBase ? joinPath(f.destBase, baseName(f.sourcePath)) : f.destBase);
  const SUGGEST = ['*.tmp', '~$*', 'Thumbs.db', 'desktop.ini', '.DS_Store', 'node_modules', '.cache', '*.part', '$RECYCLE.BIN', 'System Volume Information'];

  const entry = pushOverlay(`
    <div class="drawer" role="dialog" aria-modal="true" aria-label="${isNew ? 'New mirror' : 'Edit mirror'}">
      <div class="drawer-head">
        <div class="mode-tile mode-${f.mode}" id="ed-tile">${icon(MODES[f.mode].icon)}</div>
        <div><h2>${isNew ? 'New mirror' : 'Edit mirror'}</h2><div class="hint">${isNew ? 'Keep an exact copy of a folder somewhere else.' : esc(job.name)}</div></div>
        <button class="icon-btn" data-ed="close" aria-label="Close">${icon('x')}</button>
      </div>
      <div class="drawer-body">
        <div class="fsec">
          <div class="fsec-title">Folders</div>
          <div class="field"><label for="ed-src">Source folder</label>
            <div class="row"><input class="input mono grow" id="ed-src" placeholder="${serverIsWindows() ? 'C:\\Users\\you\\Documents' : '/home/you/documents'}" value="${esc(f.sourcePath)}" autocomplete="off" spellcheck="false" ${isNew ? 'autofocus' : ''}>
              <button class="btn" data-ed="browse-src">${icon('folder')}Browse</button></div>
            <div class="check-line" id="chk-src"></div>
          </div>
          <div class="field"><label for="ed-dst">Mirror to</label>
            <div class="row"><input class="input mono grow" id="ed-dst" placeholder="${serverIsWindows() ? '\\\\nas\\backup or D:\\Backup' : '/mnt/backup'}" value="${esc(f.destBase)}" autocomplete="off" spellcheck="false">
              <button class="btn" data-ed="browse-dst">${icon('folder')}Browse</button></div>
            <label class="switch" style="margin-top:4px"><input type="checkbox" id="ed-append" ${f.appendName ? 'checked' : ''}><span class="track"></span>Put the source folder's name inside this folder</label>
            <div class="preview-path" id="ed-preview"></div>
            <div class="check-line" id="chk-dst"></div>
          </div>
          <div id="ed-creds"></div>
        </div>

        <div class="fsec">
          <div class="fsec-title">Name</div>
          <div class="field"><input class="input" id="ed-name" placeholder="e.g. Documents to NAS" value="${esc(f.name)}" maxlength="80"></div>
        </div>

        <div class="fsec">
          <div class="fsec-title">When to sync</div>
          <div class="mode-cards" id="ed-modes"></div>
          <div id="ed-schedule"></div>
        </div>

        <div class="fsec">
          <div class="fsec-title">What to sync</div>
          <div class="dir-cards" id="ed-dirs"></div>
          <div class="opt-grid">
            <label class="check"><input type="checkbox" id="ed-sub" ${f.includeSubfolders ? 'checked' : ''}><span></span>Include subfolders</label>
            <label class="check"><input type="checkbox" id="ed-hid" ${f.includeHidden ? 'checked' : ''}><span></span>Include hidden/system files</label>
          </div>
          <div class="field"><label>Changes</label>
            <div class="opt-grid">
              <label class="check c-copy"><input type="checkbox" id="ed-copy" ${f.copyMissing ? 'checked' : ''}><span></span>Copy missing files</label>
              <label class="check c-upd"><input type="checkbox" id="ed-upd" ${f.updateChanged ? 'checked' : ''}><span></span>Update changed files</label>
              <label class="check c-del"><input type="checkbox" id="ed-del" ${f.deleteExtra ? 'checked' : ''}><span></span>Delete extra files</label>
            </div>
            <div class="hint" id="ed-changes-hint"></div>
          </div>
          <div class="callout ok">${icon('trash')}<div class="grow">
            <label class="switch"><input type="checkbox" id="ed-keep" ${f.keepDays > 0 ? 'checked' : ''}><span class="track"></span>Keep files a sync deletes or overwrites</label>
            <div class="row" id="ed-keep-row" style="flex-wrap:wrap;margin-top:10px;${f.keepDays > 0 ? '' : 'display:none'}">
              <span class="muted">for</span><input type="number" class="input sm" id="ed-keepdays" min="1" max="3650" value="${f.keepDays > 0 ? f.keepDays : 30}"><span class="muted">days, in a <span class="mono">.backupmirror-trash</span> folder on the side being changed</span>
            </div>
            <div class="hint" style="margin-top:6px">Works on network shares and Linux too, and also protects overwritten files.</div>
          </div></div>
        </div>

        <div class="fsec">
          <div class="fsec-title">Exclusions</div>
          <div class="hint">Skipped on both sides. A name like <span class="mono">node_modules</span> or <span class="mono">*.tmp</span> matches anywhere; use a slash to anchor a path, e.g. <span class="mono">cache/**</span>.</div>
          <div class="chips" id="ed-excl"></div>
          <div class="row"><input class="input sm mono grow" id="ed-excl-in" placeholder="Add a pattern and press Enter" spellcheck="false"><button class="btn sm" data-ed="excl-add">${icon('plus')}Add</button></div>
          <div class="chips" id="ed-excl-sugg"></div>
        </div>

        <div class="fsec">
          <div class="fsec-title">Safety</div>
          <div class="callout info">${icon('shield')}<div class="grow">
            <label class="switch"><input type="checkbox" id="ed-guard" ${f.threshold >= 0 ? 'checked' : ''}><span class="track"></span>Ask me before a sync deletes many files</label>
            <div class="row" id="ed-thr-row" style="flex-wrap:wrap;margin-top:10px;${f.threshold >= 0 ? '' : 'display:none'}">
              <span class="muted">Ask when more than</span><input type="number" class="input sm" id="ed-thr" min="0" max="1000000" value="${f.threshold >= 0 ? f.threshold : 10}"><span class="muted">files would be deleted</span>
            </div></div></div>
          <label class="switch"><input type="checkbox" id="ed-enabled" ${f.enabled ? 'checked' : ''}><span class="track"></span>Mirror is enabled</label>
        </div>
      </div>
      <div class="drawer-foot">
        ${isNew ? '' : `<button class="btn ghost subtle-danger" data-ed="delete">${icon('trash')}Delete</button>`}
        <span class="spacer"></span>
        <button class="btn ghost" data-ed="close">Cancel</button>
        <button class="btn primary" data-ed="save">${icon('check')}${isNew ? 'Create mirror' : 'Save changes'}</button>
      </div>
    </div>`, { kind: 'drawer' });
  const root = entry.el;

  const renderModes = () => {
    $('#ed-modes', root).innerHTML = Object.entries(MODES).map(([k, m]) => `
      <button type="button" class="mode-card mode-${k} ${f.mode === k ? 'on' : ''} ${f.direction === 'destinationToSource' && k !== 'manualOnly' ? 'off' : ''}" data-mode="${k}" ${f.direction === 'destinationToSource' && k !== 'manualOnly' ? 'title="Sync to Destination changes your source folder, so it only runs when you start it."' : ''}>
        <span class="tick">${icon('check')}</span>
        <div class="mode-tile mode-${k}">${icon(m.icon)}</div><b>${m.label}</b><span>${m.desc}</span>
      </button>`).join('');
    const tile = $('#ed-tile', root);
    tile.className = `mode-tile mode-${f.mode}`;
    tile.innerHTML = icon(MODES[f.mode].icon);
    renderSchedule();
  };

  const renderDirs = () => {
    const src = baseName(f.sourcePath) || 'source', dst = baseName(destination()) || 'mirror';
    $('#ed-dirs', root).innerHTML = [
      ['sourceToDestination', 'Sync to Source', `Make the mirror match the source. <b>${esc(src)}</b> is only read.`, 'arrow-right'],
      ['destinationToSource', 'Sync to Destination', `Make the source match the mirror, e.g. to restore. <b>${esc(dst)}</b> is only read. Manual runs only.`, 'arrow-left'],
    ].map(([k, label, desc, ic]) => `
      <button type="button" class="dir-card ${f.direction === k ? 'on' : ''} ${k === 'destinationToSource' ? 'rev' : ''}" data-dir="${k}">
        <span class="tick">${icon('check')}</span>${icon(ic)}<b>${label}</b><span>${desc}</span>
      </button>`).join('');
    const hint = $('#ed-changes-hint', root);
    const target = f.direction === 'destinationToSource' ? 'source' : 'mirror';
    const off = [!$('#ed-copy', root).checked && 'missing files are not copied', !$('#ed-upd', root).checked && 'changed files are not updated',
      !$('#ed-del', root).checked && `extra files stay in the ${target}`].filter(Boolean);
    hint.textContent = off.length ? `Not an exact mirror: ${off.join(', ')}.` : `Exact mirror: the ${target} ends up identical.`;
  };

  const renderSchedule = () => {
    const el = $('#ed-schedule', root);
    if (f.mode !== 'scheduled') {
      el.innerHTML = f.mode === 'realtime'
        ? `<div class="hint">${icon('info')} Changes are picked up within a few seconds. A full check also runs every few minutes to catch anything missed.</div>`
        : '';
      return;
    }
    const sel = (d) => f.days.length === 0 || f.days.includes(d);
    el.innerHTML = `
      <div class="field"><label>Times</label>
        <div class="chips">${f.times.map((t, i) => `<span class="chip time">${icon('clock')}${esc(t)}<button type="button" data-time-del="${i}" aria-label="Remove ${esc(t)}">${icon('x')}</button></span>`).join('')}
          <input type="time" class="input sm" id="ed-time" value="12:00" style="width:120px"><button class="btn sm" data-ed="time-add">${icon('plus')}Add time</button></div>
      </div>
      <div class="field"><div class="row"><label style="flex:1">Days</label>
        <div class="presets"><button type="button" class="preset" data-days="all">Every day</button><button type="button" class="preset" data-days="wk">Weekdays</button><button type="button" class="preset" data-days="we">Weekends</button></div></div>
        <div class="days">${[1, 2, 3, 4, 5, 6, 0].map((d) => `<button type="button" class="day ${sel(d) ? 'on' : ''}" data-day="${d}">${DAY_NAMES[d]}</button>`).join('')}</div>
        <div class="hint" id="ed-sched-sum">${esc(scheduleText({ schedule: { times: f.times, days: f.days } }))}. If the computer was off at a scheduled time, the run happens when it starts again.</div>
      </div>`;
  };

  const renderExcludes = () => {
    $('#ed-excl', root).innerHTML = f.excludes.length
      ? f.excludes.map((x, i) => `<span class="chip mono">${esc(x)}<button type="button" data-excl-del="${i}" aria-label="Remove">${icon('x')}</button></span>`).join('')
      : '<span class="hint">Nothing excluded. Everything in the source is mirrored.</span>';
    $('#ed-excl-sugg', root).innerHTML = SUGGEST.filter((s) => !f.excludes.includes(s))
      .map((s) => `<button type="button" class="chip suggest mono" data-excl-sugg="${esc(s)}">+ ${esc(s)}</button>`).join('');
  };

  const renderPreview = () => {
    const d = destination();
    const prev = $('#ed-preview', root);
    if (!d) { prev.hidden = true; return; }
    prev.hidden = false;
    if (f.appendName && f.sourcePath && f.destBase) {
      const n = baseName(f.sourcePath);
      prev.innerHTML = `${icon('chevron-right')} ${esc(d.slice(0, d.length - n.length))}<b>${esc(n)}</b>`;
    } else prev.innerHTML = `${icon('chevron-right')} ${esc(d)}`;
    renderCreds();
  };

  const renderCreds = () => {
    const el = $('#ed-creds', root);
    const d = destination();
    if (!isUnc(d)) { el.innerHTML = ''; return; }
    if (!serverIsWindows()) {
      el.innerHTML = `<div class="callout info">${icon('info')}<div class="grow">On Linux, mount the share first (cifs/nfs in <span class="mono">/etc/fstab</span>) and pick the mount folder, e.g. <span class="mono">/mnt/nas</span>.</div></div>`;
      return;
    }
    if ($('#ed-user', root)) {
      if (f.pendingPassword) { $('#ed-pass', root).value = f.pendingPassword; f.pendingPassword = null; }
      if (f.userName && !$('#ed-user', root).value) $('#ed-user', root).value = f.userName;
      return; // keep typed values
    }
    el.innerHTML = `<details class="adv" ${f.userName ? 'open' : ''}><summary>${icon('lock')}Network share sign-in <span class="hint" style="font-weight:400">optional</span>${icon('chevron-down', 'chev')}</summary>
      <div class="inner">
        <div class="hint">The service runs as a system account, so it may need its own sign-in for this share. Stored encrypted on this machine.</div>
        <div class="grid2">
          <div class="field"><label for="ed-user">User name</label><input class="input" id="ed-user" placeholder="NAS\\backup" value="${esc(f.userName)}" autocomplete="off"></div>
          <div class="field"><label for="ed-pass">Password</label><input class="input" id="ed-pass" type="password" placeholder="${f.hasPassword ? '•••••••• (saved)' : ''}" autocomplete="new-password"></div>
        </div>
        ${f.hasPassword ? '<div class="hint">Leave the password blank to keep the saved one.</div>' : ''}
      </div></details>`;
    if (f.pendingPassword) { $('#ed-pass', root).value = f.pendingPassword; f.pendingPassword = null; }
  };

  // Share sign-in for browsing (typed values, else the job's saved ones on the server side).
  const pickerCreds = () => ({
    userName: $('#ed-user', root)?.value.trim() || f.userName || null,
    password: $('#ed-pass', root)?.value || f.pendingPassword || null,
    jobId: job?.id || null,
  });
  // The picker asked for a share password: remember it in the mirror's sign-in fields.
  const takeCreds = ({ userName, password }) => {
    f.userName = userName;
    f.pendingPassword = password;
    renderCreds();
    const d = $('#ed-creds details', root);
    if (d) d.open = true;
  };

  const check = async (kind) => {
    const el = $(kind === 'source' ? '#chk-src' : '#chk-dst', root);
    const path = kind === 'source' ? f.sourcePath : destination();
    if (!path) { el.className = 'check-line'; el.innerHTML = ''; return; }
    el.className = 'check-line'; el.innerHTML = '<span class="spinner" style="width:12px;height:12px"></span> Checking…';
    try {
      const pw = $('#ed-pass', root)?.value;
      const r = await api('POST', '/api/fs/check', {
        path, kind, userName: $('#ed-user', root)?.value || null, password: pw ? pw : null, jobId: job?.id || null,
      });
      if (path !== (kind === 'source' ? f.sourcePath : destination())) return; // stale
      el.className = 'check-line ' + (r.ok ? (r.exists ? 'ok' : 'info') : 'bad');
      el.innerHTML = `${icon(r.ok ? (r.exists ? 'check' : 'info') : 'x')}<span>${esc(r.message)}</span>${r.freeBytes != null ? `<span class="space">${fmtBytes(r.freeBytes)} free</span>` : ''}`;
      if (r.suggestion) {
        // Mapped drive letter: offer the UNC path the service can actually reach.
        let target = r.suggestion;
        if (kind === 'destination' && f.appendName && f.sourcePath && f.destBase) target = target.slice(0, target.length - baseName(f.sourcePath).length - 1);
        const b = document.createElement('button');
        b.type = 'button'; b.className = 'btn sm'; b.style.marginLeft = 'auto';
        b.innerHTML = `${icon('server')}Use ${esc(target)}`;
        b.onclick = () => {
          if (kind === 'source') { f.sourcePath = target; $('#ed-src', root).value = target; }
          else { f.destBase = target; $('#ed-dst', root).value = target; }
          renderPreview(); check(kind);
        };
        el.appendChild(b);
      }
    } catch (e) { el.className = 'check-line bad'; el.innerHTML = `${icon('x')}${esc(e.message)}`; }
  };
  const checkSrc = debounce(() => check('source'), 450);
  const checkDst = debounce(() => check('destination'), 450);

  const autoName = () => {
    if (f.nameTouched) return;
    const n = baseName(f.sourcePath);
    const dst = baseName(f.destBase);
    f.name = n ? (dst && dst !== n ? `${n} → ${dst}` : n) : '';
    $('#ed-name', root).value = f.name;
  };

  renderModes(); renderExcludes(); renderPreview(); renderDirs();
  if (f.sourcePath) check('source');
  if (f.destBase) check('destination');

  root.addEventListener('input', (e) => {
    const id = e.target.id;
    if (id === 'ed-src') { f.sourcePath = e.target.value.trim(); autoName(); renderPreview(); checkSrc(); if (f.appendName) checkDst(); }
    else if (id === 'ed-dst') { f.destBase = e.target.value.trim(); autoName(); renderPreview(); checkDst(); }
    else if (id === 'ed-name') { f.name = e.target.value; f.nameTouched = true; }
    else if (id === 'ed-user') { f.userName = e.target.value; }
  });
  root.addEventListener('change', (e) => {
    const id = e.target.id;
    if (id === 'ed-append') { f.appendName = e.target.checked; renderPreview(); checkDst(); }
    else if (id === 'ed-guard') { $('#ed-thr-row', root).style.display = e.target.checked ? '' : 'none'; }
    else if (id === 'ed-keep') { $('#ed-keep-row', root).style.display = e.target.checked ? '' : 'none'; }
    else if (['ed-copy', 'ed-upd', 'ed-del'].includes(id)) renderDirs();
    else if (id === 'ed-pass' || id === 'ed-user') checkDst();
  });
  $('#ed-excl-in', root).addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); addExcl(); } });
  const addExcl = () => {
    const inp = $('#ed-excl-in', root);
    inp.value.split(/[,;\n]/).map((s) => s.trim()).filter(Boolean).forEach((s) => { if (!f.excludes.includes(s)) f.excludes.push(s); });
    inp.value = ''; renderExcludes();
  };

  root.addEventListener('click', async (e) => {
    const t = e.target.closest('button');
    if (!t) return;
    if (t.dataset.mode) {
      if (t.classList.contains('off')) { toast('info', 'Manual only', 'Sync to Destination changes your source folder, so it only runs when you start it.'); return; }
      f.mode = t.dataset.mode; renderModes(); return;
    }
    if (t.dataset.dir) {
      f.direction = t.dataset.dir;
      if (f.direction === 'destinationToSource') f.mode = 'manualOnly';
      renderDirs(); renderModes(); return;
    }
    if (t.dataset.timeDel != null) { f.times.splice(+t.dataset.timeDel, 1); renderSchedule(); return; }
    if (t.dataset.day != null) {
      const d = +t.dataset.day;
      let days = f.days.length ? [...f.days] : [0, 1, 2, 3, 4, 5, 6];
      days = days.includes(d) ? days.filter((x) => x !== d) : [...days, d];
      if (!days.length) { toast('info', 'Pick at least one day'); return; }
      f.days = days.length === 7 ? [] : days.sort();
      renderSchedule(); return;
    }
    if (t.dataset.days) { f.days = t.dataset.days === 'all' ? [] : t.dataset.days === 'wk' ? [1, 2, 3, 4, 5] : [0, 6]; renderSchedule(); return; }
    if (t.dataset.exclDel != null) { f.excludes.splice(+t.dataset.exclDel, 1); renderExcludes(); return; }
    if (t.dataset.exclSugg) { f.excludes.push(t.dataset.exclSugg); renderExcludes(); return; }
    switch (t.dataset.ed) {
      case 'close': closeOverlay(entry); break;
      case 'time-add': {
        const v = $('#ed-time', root).value;
        if (v && !f.times.includes(v)) { f.times.push(v); f.times.sort(); }
        renderSchedule(); break;
      }
      case 'excl-add': addExcl(); break;
      case 'browse-src':
        openPicker({ title: 'Choose the source folder', initial: f.sourcePath, creds: pickerCreds, onCreds: takeCreds, onPick: (p) => { f.sourcePath = p; $('#ed-src', root).value = p; autoName(); renderPreview(); check('source'); if (f.appendName) check('destination'); } });
        break;
      case 'browse-dst':
        openPicker({ title: 'Choose where to mirror to', initial: f.destBase, destination: true, creds: pickerCreds, onCreds: takeCreds, onPick: (p) => { f.destBase = p; $('#ed-dst', root).value = p; autoName(); renderPreview(); check('destination'); } });
        break;
      case 'delete': if (await deleteJob(job)) closeOverlay(entry); break;
      case 'save': await save(); break;
    }
  });
  root.addEventListener('keydown', (e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); save(); } });

  const save = async () => {
    const dst = destination();
    const name = f.name.trim() || baseName(f.sourcePath);
    if (!f.sourcePath) { toast('warn', 'Choose a source folder'); $('#ed-src', root).focus(); return; }
    if (!dst) { toast('warn', 'Choose where to mirror to'); $('#ed-dst', root).focus(); return; }
    if (f.mode === 'scheduled' && !f.times.length) { toast('warn', 'Add at least one time'); return; }
    if (!$('#ed-copy', root).checked && !$('#ed-upd', root).checked && !$('#ed-del', root).checked) { toast('warn', 'Choose at least one kind of change'); return; }
    const guard = $('#ed-guard', root).checked;
    const pw = $('#ed-pass', root)?.value;
    const body = {
      name, enabled: $('#ed-enabled', root).checked, sourcePath: f.sourcePath, destinationPath: dst,
      destinationUserName: $('#ed-user', root) ? ($('#ed-user', root).value.trim() || null) : (isUnc(dst) ? f.userName || null : null),
      password: pw ? pw : null,
      mode: f.mode, times: f.times, days: f.days, excludes: f.excludes,
      deleteConfirmThreshold: guard ? Math.max(0, parseInt($('#ed-thr', root).value, 10) || 0) : -1,
      direction: f.direction,
      includeSubfolders: $('#ed-sub', root).checked,
      includeHidden: $('#ed-hid', root).checked,
      copyMissing: $('#ed-copy', root).checked,
      updateChanged: $('#ed-upd', root).checked,
      deleteExtra: $('#ed-del', root).checked,
      keepRemovedDays: $('#ed-keep', root).checked ? Math.min(3650, Math.max(1, parseInt($('#ed-keepdays', root).value, 10) || 30)) : 0,
    };
    const btn = $('[data-ed="save"]', root);
    btn.disabled = true;
    try {
      const saved = isNew ? await api('POST', '/api/jobs', body) : await api('PUT', `/api/jobs/${job.id}`, body);
      state.jobs.set(saved.id, saved);
      closeOverlay(entry);
      render();
      toast('ok', isNew ? 'Mirror created' : 'Changes saved',
        isNew ? (saved.mode === 'realtime' && saved.enabled && state.armed ? 'The first sync starts right away.' : saved.mode === 'manualOnly' ? 'Press Sync now to run it.' : scheduleText(saved)) : saved.name);
    } catch (err) {
      toast('bad', 'Could not save', err.message, 6000);
    } finally { btn.disabled = false; }
  };
}

// ------------------------------------------------------------------ folder picker

function openPicker({ title, initial, onPick, destination = false, creds = null, onCreds = null }) {
  const p = { roots: [], path: '', listing: null, selected: null, loading: false };
  const entry = pushOverlay(`
    <div class="modal" style="--w:820px" role="dialog" aria-modal="true">
      <div class="modal-head"><div class="hico brand">${icon('folder')}</div><div><h2>${esc(title)}</h2><p>Folders on <b>${esc(state.status?.machine || 'the server')}</b>, as the Backup Mirror service sees them.</p></div>
        <button class="icon-btn" data-pk="close" style="margin-left:auto">${icon('x')}</button></div>
      <div class="crumbs" id="pk-crumbs"></div>
      <div class="picker">
        <div class="picker-roots" id="pk-roots"><div class="picker-empty"><span class="spinner"></span></div></div>
        <div class="picker-main">
          <div class="picker-bar">
            <button class="icon-btn" data-pk="up" title="Up one level">${icon('arrow-up')}</button>
            <input class="input sm mono" id="pk-path" placeholder="Type a path${serverIsWindows() ? ' or \\\\server\\share' : ''} and press Enter" spellcheck="false" style="flex:1">
            <button class="btn sm" data-pk="mkdir" title="New folder">${icon('folder-plus')}New folder</button>
          </div>
          <div class="picker-list" id="pk-list"></div>
        </div>
      </div>
      <div class="modal-foot"><div class="hint mono" id="pk-sel" style="margin-right:auto;align-self:center;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:60%"></div>
        <button class="btn ghost" data-pk="close">Cancel</button>
        <button class="btn primary" data-pk="choose">${icon('check')}Choose folder</button></div>
    </div>`);
  const root = entry.el;

  const renderRoots = () => {
    let sharesHeader = false;
    $('#pk-roots', root).innerHTML = p.roots.map((r) => {
      const ic = r.kind === 'network' || r.kind === 'share' ? 'server' : r.kind === 'home' ? 'home' : r.kind === 'folder' ? 'folder' : 'drive';
      const active = p.path && p.path.toLowerCase().startsWith(r.path.toLowerCase());
      const head = r.kind === 'share' && !sharesHeader ? (sharesHeader = true, `<div class="picker-group">Network shares</div>`) : '';
      const sub = r.freeBytes != null ? `${fmtBytes(r.freeBytes)} free` : r.detail || '';
      return `${head}<button data-root="${esc(r.path)}" class="${active ? 'on' : ''}" title="${esc(r.path)}">${icon(ic)}<span class="rname">${esc(r.name)}${sub ? `<span class="rfree">${esc(sub)}</span>` : ''}</span></button>`;
    }).join('') || '<div class="picker-empty">No drives found</div>';
  };

  const renderList = () => {
    const l = p.listing;
    $('#pk-path', root).value = p.path;
    $('#pk-sel', root).textContent = p.selected || p.path || '';
    // breadcrumbs
    const parts = []; let acc = '';
    const s = sepFor(p.path);
    if (/^(\\\\|\/\/)/.test(p.path)) {
      const segs = p.path.replace(/^[\\/]+/, '').split(/[\\/]+/).filter(Boolean);
      acc = '\\\\' + segs.slice(0, 2).join('\\');
      parts.push({ name: acc, path: acc });
      segs.slice(2).forEach((g) => { acc += '\\' + g; parts.push({ name: g, path: acc }); });
    } else if (s === '\\') {
      const segs = p.path.split('\\').filter(Boolean);
      segs.forEach((g, i) => { acc = i === 0 ? g + '\\' : joinPath(acc, g); parts.push({ name: g, path: acc }); });
    } else {
      parts.push({ name: '/', path: '/' });
      p.path.split('/').filter(Boolean).forEach((g) => { acc += '/' + g; parts.push({ name: g, path: acc }); });
    }
    $('#pk-crumbs', root).innerHTML = parts.map((c, i) => `${i ? icon('chevron-right') : ''}<button data-nav="${esc(c.path)}">${esc(c.name)}</button>`).join('');
    renderRoots();
    const el = $('#pk-list', root);
    if (p.loading) { el.innerHTML = '<div class="picker-empty"><span class="spinner"></span></div>'; return; }
    if (!l) { el.innerHTML = '<div class="picker-empty">Pick a drive on the left.</div>'; return; }
    if (l.error) {
      const needsLogin = /^\\\\/.test(p.path) && /denied|password|logon|credential|1326|86\)/i.test(l.error);
      el.innerHTML = `<div class="picker-empty">${icon(needsLogin ? 'lock' : 'alert')}<p>${esc(l.error)}</p>
        ${needsLogin ? `<form class="share-login" id="pk-login">
          <input class="input sm" id="pk-user" placeholder="User name (e.g. NAS\\backup)" autocomplete="off" value="${esc(p.creds?.userName || '')}">
          <input class="input sm" id="pk-pass" type="password" placeholder="Password" autocomplete="new-password">
          <button class="btn sm primary" type="submit">${icon('key')}Connect</button></form>
          <div class="hint">Saved with this mirror (encrypted) when you save it.</div>` : ''}</div>`;
      $('#pk-login', root)?.addEventListener('submit', (ev) => {
        ev.preventDefault();
        p.creds = { userName: $('#pk-user', root).value.trim(), password: $('#pk-pass', root).value, jobId: null };
        p.credsTyped = true;
        go(p.path);
      });
      return;
    }
    if (!l.dirs.length) { el.innerHTML = `<div class="picker-empty">${icon('folder')}<p>No sub-folders here.${destination ? ' You can choose this folder, or create a new one.' : ''}</p></div>`; return; }
    el.innerHTML = l.dirs.map((d) => `<button data-dir="${esc(d.path)}" class="${p.selected === d.path ? 'sel' : ''}" title="Double-click to open">${icon('folder')}<span class="name">${esc(d.name)}</span><span class="go">${icon('chevron-right')}</span></button>`).join('');
  };

  const go = async (path) => {
    p.path = path; p.selected = null; p.loading = true; renderList();
    try {
      const c = p.creds || (creds ? creds() : {});
      p.listing = await api('POST', '/api/fs/list', { path, userName: c.userName || null, password: c.password || null, jobId: c.jobId || null });
      p.path = p.listing.path || path;
      if (!p.listing.error && p.credsTyped && onCreds) { onCreds(p.creds); p.credsTyped = false; }
    } catch (e) { p.listing = { error: e.message, dirs: [] }; }
    p.loading = false; renderList();
  };

  (async () => {
    try { p.roots = await api('GET', '/api/fs/roots'); } catch (e) { p.roots = []; }
    renderRoots();
    const start = initial && initial.trim() ? initial.trim() : (p.roots.find((r) => r.kind === 'home') || p.roots[0] || {}).path;
    if (start) await go(start); else renderList();
  })();

  root.addEventListener('click', async (e) => {
    const b = e.target.closest('button');
    if (!b) return;
    if (b.dataset.root) return go(b.dataset.root);
    if (b.dataset.nav) return go(b.dataset.nav);
    if (b.dataset.dir) {
      if (e.detail >= 2) return go(b.dataset.dir);
      p.selected = b.dataset.dir;
      $$('#pk-list button', root).forEach((x) => x.classList.toggle('sel', x === b));
      $('#pk-sel', root).textContent = p.selected;
      return;
    }
    switch (b.dataset.pk) {
      case 'close': closeOverlay(entry); break;
      case 'up': if (p.listing?.parent) go(p.listing.parent); break;
      case 'choose': {
        const chosen = p.selected || p.path;
        if (!chosen) return;
        closeOverlay(entry); onPick(chosen); break;
      }
      case 'mkdir': {
        if (!p.path) return;
        const bar = $('.picker-bar', root);
        if ($('#pk-new', bar)) return;
        const wrap = document.createElement('div');
        wrap.className = 'row';
        wrap.innerHTML = `<input class="input sm" id="pk-new" placeholder="Folder name" style="width:160px"><button class="btn sm primary" data-pk="mkdir-ok">${icon('check')}</button>`;
        b.replaceWith(wrap);
        $('#pk-new', root).focus();
        $('#pk-new', root).addEventListener('keydown', (ev) => { if (ev.key === 'Enter') $('[data-pk="mkdir-ok"]', root).click(); });
        break;
      }
      case 'mkdir-ok': {
        const name = $('#pk-new', root).value.trim();
        if (!name) return;
        try {
          const made = await api('POST', '/api/fs/mkdir', { path: joinPath(p.path, name) });
          const btn = document.createElement('button');
          btn.className = 'btn sm'; btn.dataset.pk = 'mkdir'; btn.innerHTML = `${icon('folder-plus')}New folder`;
          b.parentElement.replaceWith(btn);
          await go(p.path);
          p.selected = made.path; renderList();
        } catch (err) { toast('bad', 'Could not create folder', err.message); }
        break;
      }
    }
  });
  $('#pk-path', root).addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); go(e.target.value.trim()); } });
}

// ------------------------------------------------------------------ compare (OpenDiff-style preview)

const CMP_KIND = [
  { label: 'Create folder', cls: 'k-add', ic: 'folder-plus' },
  { label: 'Copy', cls: 'k-add', ic: 'plus' },
  { label: 'Update', cls: 'k-upd', ic: 'refresh' },
  { label: 'Delete', cls: 'k-del', ic: 'trash' },
  { label: 'Remove folder', cls: 'k-del', ic: 'folder' },
];
const ROW_H = 32;

function openCompare(job) {
  const o = {
    direction: job.direction, includeSubfolders: job.includeSubfolders, includeHidden: job.includeHidden,
    copyMissing: job.copyMissing, updateChanged: job.updateChanged, deleteExtra: job.deleteExtra,
  };
  const c = { result: null, checked: null, view: [], kinds: new Set([0, 1, 2, 3, 4]), query: '', abort: null, busy: false };

  const entry = pushOverlay(`
    <div class="modal cmp" role="dialog" aria-modal="true" aria-label="Compare">
      <div class="modal-head"><div class="hico brand">${icon('diff')}</div>
        <div style="min-width:0"><h2>Compare “${esc(job.name)}”</h2><p id="cmp-paths"></p></div>
        <button class="icon-btn" data-c="close" style="margin-left:auto">${icon('x')}</button></div>
      <div class="cmp-opts">
        <div class="seg txt" role="radiogroup" aria-label="Mode">
          <button data-c="dir" data-v="sourceToDestination">Sync to Source</button>
          <button data-c="dir" data-v="destinationToSource">Sync to Destination</button>
        </div>
        <label class="check"><input type="checkbox" data-o="includeSubfolders"><span></span>Include subfolders</label>
        <label class="check"><input type="checkbox" data-o="includeHidden"><span></span>Include hidden/system files</label>
        <span class="vsep"></span>
        <label class="check c-copy"><input type="checkbox" data-o="copyMissing"><span></span>Copy missing</label>
        <label class="check c-upd"><input type="checkbox" data-o="updateChanged"><span></span>Update changed</label>
        <label class="check c-del"><input type="checkbox" data-o="deleteExtra"><span></span>Delete extra</label>
        <span class="spacer"></span>
        <button class="btn primary" data-c="run">${icon('diff')}Compare</button>
      </div>
      <div class="cmp-summary" id="cmp-summary"></div>
      <div class="cmp-table">
        <div class="cmp-head"><span><input type="checkbox" id="cmp-all" title="Check or uncheck everything shown"></span><span>Action</span><span>File or folder</span><span class="num">Size</span><span>Modified</span></div>
        <div class="cmp-list" id="cmp-list"><div class="cmp-spacer" id="cmp-spacer"></div></div>
        <div class="cmp-state" id="cmp-state"></div>
      </div>
      <div class="modal-foot cmp-foot">
        <span class="hint" id="cmp-count"></span>
        <span class="spacer"></span>
        <button class="btn ghost" data-c="close">Close</button>
        <button class="btn primary" data-c="apply" disabled>${icon('check')}<span id="cmp-apply-label">Apply checked changes</span></button>
      </div>
    </div>`, { onClose: () => c.abort?.abort() });
  const root = entry.el;
  const list = $('#cmp-list', root), spacer = $('#cmp-spacer', root);

  const targetWord = () => (o.direction === 'destinationToSource' ? 'Source' : 'Destination');
  const syncOpts = () => {
    $$('[data-c="dir"]', root).forEach((b) => b.classList.toggle('on', b.dataset.v === o.direction));
    $$('[data-o]', root).forEach((i) => { i.checked = !!o[i.dataset.o]; });
    const from = o.direction === 'destinationToSource' ? job.destinationPath : job.sourcePath;
    const to = o.direction === 'destinationToSource' ? job.sourcePath : job.destinationPath;
    $('#cmp-paths', root).innerHTML = `<span class="mono">${esc(from)}</span> ${icon('arrow-right')} <span class="mono">${esc(to)}</span>
      <span class="faint"> · only the ${targetWord().toLowerCase()} is changed</span>`;
  };

  const setState = (html) => { const st = $('#cmp-state', root); st.innerHTML = html; st.hidden = !html; };
  const stale = () => {
    // Options changed after a compare: like OpenDiff, the old list can't be applied any more.
    if (!c.result) return;
    c.result = null; c.view = []; spacer.style.height = '0px'; spacer.innerHTML = '';
    $('#cmp-summary', root).innerHTML = '';
    setState(`<div class="picker-empty">${icon('diff')}<p>Options changed. Press <b>Compare</b> to see what will happen.</p></div>`);
    updateFoot();
  };

  const applyFilter = () => {
    const q = c.query.trim().toLowerCase();
    c.view = [];
    if (c.result) c.result.items.forEach((it, i) => { if (c.kinds.has(it.k) && (!q || it.p.toLowerCase().includes(q))) c.view.push(i); });
    spacer.style.height = `${c.view.length * ROW_H}px`;
    list.scrollTop = 0;
    paint();
    updateFoot();
  };

  const paint = () => {
    if (!c.result) return;
    const first = Math.max(0, Math.floor(list.scrollTop / ROW_H) - 10);
    const last = Math.min(c.view.length, first + Math.ceil(list.clientHeight / ROW_H) + 20);
    let html = '';
    for (let n = first; n < last; n++) {
      const i = c.view[n], it = c.result.items[i], k = CMP_KIND[it.k];
      const folder = it.k === 0 || it.k === 4;
      html += `<div class="cmp-row ${k.cls} ${c.checked[i] ? '' : 'unchecked'}" style="top:${n * ROW_H}px" data-i="${i}">
        <span><input type="checkbox" data-i="${i}" ${c.checked[i] ? 'checked' : ''}></span>
        <span class="act">${icon(k.ic)}${k.label}</span>
        <span class="path" title="${esc(it.p)}">${esc(it.p)}</span>
        <span class="num">${folder ? '' : fmtBytes(it.s)}</span>
        <span class="faint">${it.t ? new Date(it.t).toLocaleString([], { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : ''}</span>
      </div>`;
    }
    spacer.innerHTML = html;
    const allShown = c.view.length > 0 && c.view.every((i) => c.checked[i]);
    const someShown = c.view.some((i) => c.checked[i]);
    const all = $('#cmp-all', root);
    all.checked = allShown; all.indeterminate = !allShown && someShown;
  };
  let raf = 0;
  list.addEventListener('scroll', () => { if (!raf) raf = requestAnimationFrame(() => { raf = 0; paint(); }); });

  const checkedCount = () => (c.checked ? c.checked.reduce((a, v) => a + v, 0) : 0);
  const updateFoot = () => {
    const btn = $('[data-c="apply"]', root);
    const n = checkedCount();
    $('#cmp-apply-label', root).textContent = `Apply ${n ? nf.format(n) + ' ' : ''}checked change${n === 1 ? '' : 's'} to ${targetWord()}…`;
    btn.disabled = !c.result || n === 0 || c.busy;
    btn.classList.toggle('danger', o.direction === 'destinationToSource');
    btn.classList.toggle('primary', o.direction !== 'destinationToSource');
    $('#cmp-count', root).textContent = c.result
      ? `${nf.format(n)} of ${nf.format(c.result.items.length)} checked${c.view.length !== c.result.items.length ? ` · ${nf.format(c.view.length)} shown` : ''}`
      : '';
  };

  const renderSummary = () => {
    const r = c.result, k = r.counts;
    const chip = (kinds, cls, ic, n, label, bytes) => n
      ? `<button class="cchip ${cls} ${kinds.every((x) => c.kinds.has(x)) ? 'on' : ''}" data-kinds="${kinds.join(',')}">${icon(ic)}<b>${nf.format(n)}</b> ${label}${bytes ? `<span>${fmtBytes(bytes)}</span>` : ''}</button>` : '';
    const total = r.items.length;
    $('#cmp-summary', root).innerHTML = total === 0
      ? ''
      : `${chip([1], 'k-add', 'plus', k.copy, 'to copy', k.copyBytes)}${chip([0], 'k-add', 'folder-plus', k.createFolders, 'folders to create')}
         ${chip([2], 'k-upd', 'refresh', k.update, 'to update', k.updateBytes)}${chip([3], 'k-del', 'trash', k.delete, 'to delete', k.deleteBytes)}
         ${chip([4], 'k-del', 'folder', k.removeFolders, 'folders to remove')}
         <span class="spacer"></span>
         <div class="search sm">${icon('search')}<input class="input sm" id="cmp-q" placeholder="Filter paths…" value="${esc(c.query)}"></div>
         <span class="faint cmp-meta">${nf.format(r.fromFiles)} files in one folder, ${nf.format(r.toFiles)} in the other · ${r.seconds.toFixed(1)} s</span>`;
    if (r.warnings.length) {
      $('#cmp-summary', root).insertAdjacentHTML('beforeend',
        `<div class="callout warn" style="flex-basis:100%">${icon('alert')}<div class="grow"><b>${nf.format(r.warnings.length)} folder(s) or file(s) could not be read</b> and are left out (nothing under them is deleted).
          <div class="mono" style="margin-top:4px;font-size:11.5px">${r.warnings.slice(0, 3).map(esc).join('<br>')}</div></div></div>`);
    }
  };

  const run = async () => {
    c.abort?.abort();
    c.abort = new AbortController();
    c.busy = true; c.result = null; spacer.innerHTML = ''; spacer.style.height = '0px';
    $('#cmp-summary', root).innerHTML = '';
    setState(`<div class="picker-empty"><span class="spinner"></span><p id="cmp-scan" data-job="${job.id}">Scanning both folders… nothing is changed.</p>
      <button class="btn sm ghost" data-c="cancel">Cancel</button></div>`);
    updateFoot();
    try {
      const res = await fetch(`/api/jobs/${job.id}/compare`, {
        method: 'POST', signal: c.abort.signal, credentials: 'same-origin',
        headers: { 'X-BackupMirror': '1', 'Content-Type': 'application/json' }, body: JSON.stringify(o),
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) throw new Error((data && data.error) || `${res.status} ${res.statusText}`);
      c.result = data;
      c.checked = new Uint8Array(data.items.length).fill(1);
      c.kinds = new Set([0, 1, 2, 3, 4]); c.query = '';
      renderSummary();
      setState(data.items.length ? '' : `<div class="picker-empty match">${icon('check')}<p><b>The folders already match.</b><br>Nothing would change with these options.</p></div>`);
      if (data.truncated) toast('info', 'Long list', `Showing the first ${nf.format(data.items.length)} changes.`);
      applyFilter();
    } catch (e) {
      if (e.name === 'AbortError') { setState(`<div class="picker-empty">${icon('x')}<p>Cancelled.</p></div>`); }
      else setState(`<div class="picker-empty">${icon('alert')}<p>${esc(e.message)}</p></div>`);
    } finally { c.busy = false; updateFoot(); }
  };

  const apply = async () => {
    const r = c.result;
    const skip = [], kept = [0, 0, 0, 0, 0], bytes = [0, 0, 0, 0, 0];
    r.items.forEach((it, i) => { if (c.checked[i]) { kept[it.k]++; bytes[it.k] += it.s || 0; } else skip.push(i); });
    const lines = [
      kept[0] && `CREATE ${nf.format(kept[0])} folder(s)`,
      kept[1] && `COPY ${nf.format(kept[1])} file(s) (${fmtBytes(bytes[1])})`,
      kept[2] && `OVERWRITE ${nf.format(kept[2])} file(s) (${fmtBytes(bytes[2])})`,
      kept[3] && `DELETE ${nf.format(kept[3])} file(s) (${fmtBytes(bytes[3])})`,
      kept[4] && `REMOVE ${nf.format(kept[4])} empty folder(s)`,
    ].filter(Boolean);
    const keep = job.keepRemovedDays > 0
      ? `Deleted and overwritten files are kept for ${job.keepRemovedDays} days in <span class="mono">.backupmirror-trash</span>.`
      : '<b>Deleted and overwritten files cannot be recovered</b> (turn on “Keep files a sync deletes or overwrites” in the mirror settings to keep them).';
    const ok = await confirmDialog({
      title: `Change the ${targetWord().toLowerCase()} folder?`,
      body: `<span class="mono">${esc(r.to)}</span> will be changed:<ul class="apply-list">${lines.map((l) => `<li>${l}</li>`).join('')}</ul>
        <span class="mono">${esc(r.from)}</span> is only read. ${keep}`,
      ok: `Apply ${nf.format(kept.reduce((a, v) => a + v, 0))} changes`, danger: kept[3] > 0 || kept[2] > 0 || o.direction === 'destinationToSource',
      iconName: kept[3] ? 'trash' : 'check',
    });
    if (!ok) return;
    try {
      await api('POST', `/api/compare/${r.id}/apply`, { skip });
      closeOverlay(entry);
      toast('ok', 'Applying changes', `${job.name}: progress shows on the overview.`);
    } catch (e) { toast('bad', 'Could not apply', e.message, 7000); }
  };

  root.addEventListener('click', (e) => {
    const b = e.target.closest('button');
    const row = e.target.closest('.cmp-row');
    if (e.target.matches('input[type="checkbox"][data-i]')) {
      c.checked[+e.target.dataset.i] = e.target.checked ? 1 : 0;
      e.target.closest('.cmp-row').classList.toggle('unchecked', !e.target.checked);
      updateFoot(); paint(); return;
    }
    if (row && !b && e.target.tagName !== 'INPUT') {
      const i = +row.dataset.i; c.checked[i] = c.checked[i] ? 0 : 1; paint(); updateFoot(); return;
    }
    if (!b) return;
    if (b.dataset.kinds) {
      const ks = b.dataset.kinds.split(',').map(Number);
      const allOn = c.kinds.size === 5;
      // First click isolates a kind; clicking the only active kind shows everything again.
      if (allOn) c.kinds = new Set(ks);
      else if (ks.every((k) => c.kinds.has(k)) && c.kinds.size === ks.length) c.kinds = new Set([0, 1, 2, 3, 4]);
      else ks.forEach((k) => (c.kinds.has(k) ? c.kinds.delete(k) : c.kinds.add(k)));
      if (!c.kinds.size) c.kinds = new Set([0, 1, 2, 3, 4]);
      renderSummary(); applyFilter(); return;
    }
    switch (b.dataset.c) {
      case 'close': closeOverlay(entry); break;
      case 'cancel': c.abort?.abort(); break;
      case 'run': run(); break;
      case 'apply': apply(); break;
      case 'dir': o.direction = b.dataset.v; syncOpts(); stale(); updateFoot(); break;
    }
  });
  root.addEventListener('change', (e) => {
    if (e.target.dataset.o) { o[e.target.dataset.o] = e.target.checked; stale(); }
    else if (e.target.id === 'cmp-all') {
      const v = e.target.checked ? 1 : 0;
      c.view.forEach((i) => { c.checked[i] = v; });
      paint(); updateFoot();
    }
  });
  root.addEventListener('input', debounce((e) => {
    if (e.target.id === 'cmp-q') { c.query = e.target.value; applyFilter(); }
  }, 150));

  syncOpts();
  run();
}

// ------------------------------------------------------------------ actions

async function deleteJob(job) {
  const ok = await confirmDialog({
    title: `Delete “${job.name}”?`,
    body: 'The mirror job is removed. <b>No files are deleted</b> — the source and the existing mirror copy stay exactly as they are.',
    ok: 'Delete mirror', danger: true, iconName: 'trash',
  });
  if (!ok) return false;
  try {
    await api('DELETE', `/api/jobs/${job.id}`);
    state.jobs.delete(job.id);
    render();
    toast('ok', 'Mirror deleted', job.name);
    return true;
  } catch (e) { toast('bad', 'Could not delete', e.message); return false; }
}

async function reviewDeletes(id) {
  const j = state.jobs.get(id);
  let data;
  try { data = await api('GET', `/api/jobs/${id}/pending-deletes`); } catch (e) { toast('bad', 'Could not load the list', e.message); return; }
  const more = data.count - data.files.length;
  const entry = pushOverlay(`
    <div class="modal" style="--w:640px" role="dialog" aria-modal="true">
      <div class="modal-head"><div class="hico warn">${icon('alert')}</div>
        <div><h2>Review deletes for “${esc(j.name)}”</h2>
        <p>These <b>${nf.format(data.count)}</b> files are in the mirror but no longer in the source. Confirming removes them from <span class="mono">${esc(j.destinationPath)}</span>. New and changed files have already been copied.</p></div></div>
      <div class="modal-body"><div class="filelist">
        ${data.files.slice().sort(new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' }).compare).map((f) => `<div>${icon('trash')}${esc(f)}</div>`).join('')}
        ${more > 0 ? `<div class="more">…and ${nf.format(more)} more</div>` : ''}
      </div></div>
      <div class="modal-foot">
        <button class="btn ghost" data-r="close">Decide later</button>
        <button class="btn" data-r="skip">Keep files for now</button>
        <button class="btn danger" data-r="confirm">${icon('trash')}Delete ${nf.format(data.count)} files</button>
      </div>
    </div>`);
  entry.el.addEventListener('click', async (e) => {
    const b = e.target.closest('[data-r]');
    if (!b) return;
    if (b.dataset.r === 'close') return closeOverlay(entry);
    try {
      if (b.dataset.r === 'skip') { await api('POST', `/api/jobs/${id}/skip-deletes`); toast('info', 'Files kept', 'The next sync will ask again if they are still missing from the source.'); }
      else { await api('POST', `/api/jobs/${id}/confirm-deletes`); toast('ok', 'Deleting confirmed', 'The mirror is being brought in line with the source.'); }
      closeOverlay(entry);
    } catch (err) { toast('bad', 'Something went wrong', err.message); }
  });
}

async function syncNow(id) {
  try { await api('POST', `/api/jobs/${id}/sync`); }
  catch (e) { toast('warn', 'Not started', e.message); }
}

document.addEventListener('click', async (e) => {
  const themeBtn = e.target.closest('[data-theme-choice]');
  if (themeBtn) { setTheme(themeBtn.dataset.themeChoice); return; }

  const t = e.target.closest('[data-action]');
  if (!t || t.tagName === 'INPUT') { if (!e.target.closest('.menu')) closeMenus(); return; }
  const id = t.dataset.id;
  const job = id ? state.jobs.get(id) : null;
  const action = t.dataset.action;
  if (action !== 'menu') closeMenus();
  switch (action) {
    case 'menu': {
      const open = $(`.menu[data-for="${id}"]`);
      if (open) closeMenus(); else openJobMenu(t, id);
      break;
    }
    case 'new-job': openEditor(null); break;
    case 'edit': if (job) openEditor(job); break;
    case 'compare': if (job) openCompare(job); break;
    case 'delete': if (job) deleteJob(job); break;
    case 'sync': syncNow(id); break;
    case 'cancel': try { await api('POST', `/api/jobs/${id}/cancel`); } catch (err) { toast('warn', 'Could not stop', err.message); } break;
    case 'review': reviewDeletes(id); break;
    case 'history': state.historyJob = id; state.historyLoaded = false; location.hash = '#/activity'; break;
    case 'toggle-menu': if (job) setEnabled(job, !job.enabled); break;
    case 'resume-job': if (job) resumeJob(job); break;
    case 'sync-all': try { const r = await api('POST', '/api/sync-all'); toast('info', `Syncing ${r.started} mirror${r.started === 1 ? '' : 's'}`); } catch (err) { toast('bad', 'Could not start', err.message); } break;
    case 'arm': {
      const armed = t.dataset.v === '1';
      if (!armed && !(await confirmDialog({ title: 'Pause all mirrors?', body: 'Running syncs stop and nothing is mirrored until you resume. This stays in effect after a restart.', ok: 'Pause all', iconName: 'pause' }))) return;
      try { await api('POST', '/api/arm', { armed }); state.armed = armed; toast(armed ? 'ok' : 'info', armed ? 'Mirrors resumed' : 'All mirrors paused', armed ? 'Catching up on anything that changed.' : ''); }
      catch (err) { toast('bad', 'Could not change state', err.message); }
      scheduleHeroUpdate();
      renderJobs();
      break;
    }
    case 'refresh-history': state.historyLoaded = false; renderView(); break;
    case 'refresh-logs': refreshLogs(true); break;
    case 'filter': state.jobFilter = t.dataset.v; renderJobs(); break;
    case 'clear-filter': state.jobFilter = 'all'; state.jobQuery = ''; renderJobs(); break;
    case 'layout':
      state.layout = t.dataset.v;
      try { localStorage.setItem('bm-layout', state.layout); } catch { /* ignore */ }
      renderJobs(); break;
    case 'toggle-nav': $('#sidebar').classList.toggle('open'); break;
    case 'logout': await api('POST', '/api/logout'); location.href = '/login.html'; break;
  }
});

document.addEventListener('change', async (e) => {
  const t = e.target;
  if (t.dataset.action === 'toggle') {
    const job = state.jobs.get(t.dataset.id);
    if (job) setEnabled(job, t.checked);
  } else if (t.id === 'hist-job') {
    state.historyJob = t.value; state.historyLoaded = false; renderView();
  } else if (t.id === 'log-auto') {
    state.logAuto = t.checked;
  }
});
document.addEventListener('input', debounce((e) => {
  if (e.target.id === 'log-filter') { state.logFilter = e.target.value; refreshLogs(false); }
  if (e.target.id === 'job-search') { state.jobQuery = e.target.value; renderJobs(); }
}, 200));

/**
 * Resume one mirror. If everything is paused, ask whether to resume all mirrors or only this one
 * (the others are then paused individually, so they stay put).
 */
async function resumeJob(job) {
  if (state.armed) { if (!job.enabled) await setEnabled(job, true); return; }
  const others = sortedJobs().filter((j) => j.id !== job.id && j.enabled);
  const choice = await new Promise((resolve) => {
    let done = false;
    const entry = pushOverlay(`
      <div class="modal" style="--w:500px" role="dialog" aria-modal="true">
        <div class="modal-head"><div class="hico brand">${icon('play')}</div>
          <div><h2>All mirrors are paused</h2><p>Resume “${esc(job.name)}” on its own, or everything?</p></div></div>
        <div class="modal-foot">
          <button class="btn ghost" data-r="cancel">Cancel</button>
          ${others.length ? `<button class="btn" data-r="all">${icon('play')}Resume all ${state.jobs.size}</button>` : ''}
          <button class="btn primary" data-r="one" autofocus>${icon('play')}${others.length ? 'Only this mirror' : 'Resume'}</button>
        </div>
        ${others.length ? `<div class="hint" style="padding:0 24px 18px">“Only this mirror” pauses the other ${others.length} individually; resume them any time from their rows.</div>` : ''}
      </div>`, { onClose: () => { if (!done) resolve('cancel'); } });
    entry.el.addEventListener('click', (e) => {
      const b = e.target.closest('[data-r]');
      if (!b) return;
      done = true; resolve(b.dataset.r); closeOverlay(entry);
    });
  });
  if (choice === 'cancel') return;
  try {
    if (choice === 'one') {
      for (const o of others) await api('POST', `/api/jobs/${o.id}/enabled`, { enabled: false });
      if (!job.enabled) await api('POST', `/api/jobs/${job.id}/enabled`, { enabled: true });
    }
    await api('POST', '/api/arm', { armed: true });
    state.armed = true;
    toast('ok', choice === 'one' ? `${job.name} resumed` : 'All mirrors resumed', 'Catching up on anything that changed.');
    await loadStatus();
  } catch (e) { toast('bad', 'Could not resume', e.message); }
}

async function setEnabled(job, enabled) {
  try {
    const v = await api('POST', `/api/jobs/${job.id}/enabled`, { enabled });
    state.jobs.set(v.id, v); updateCard(v); scheduleHeroUpdate();
  } catch (e) { toast('bad', 'Could not update', e.message); updateCard(job); }
}

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    if ($('.menu')) { closeMenus(); return; }
    if (overlays.length) { closeOverlay(); return; }
  }
  const typing = /INPUT|TEXTAREA|SELECT/.test(document.activeElement?.tagName || '');
  if (!typing && !overlays.length && (e.key === 'n' || e.key === 'N') && !e.ctrlKey && !e.metaKey && !e.altKey && state.loaded) {
    e.preventDefault(); openEditor(null);
  } else if (!typing && !overlays.length && e.key === '/' && $('#job-search')) {
    e.preventDefault(); $('#job-search').focus();
  }
});

// ------------------------------------------------------------------ boot

window.addEventListener('hashchange', route);
applyThemeButtons();
route();
(async function boot() {
  for (let attempt = 0; ; attempt++) {
    try {
      await loadStatus();
      break;
    } catch (e) {
      state.connected = false; renderChrome();
      if (attempt === 0) $('#view').innerHTML = `<div class="empty">${icon('plug')}<h3>Can't reach the Backup Mirror service</h3><p>${esc(e.message)}. Retrying…</p></div>`;
      await new Promise((r) => setTimeout(r, 3000));
    }
  }
  connectEvents();
  loadHistory().then(scheduleHeroUpdate).catch(() => {});
  logTick();
  // Keep relative times ("5 min ago") fresh.
  setInterval(() => {
    if (state.view !== 'overview' || document.hidden || overlays.length || $('.menu')) return;
    scheduleHeroUpdate();
    if (!$('.menu') && !$('#jobs-toolbar-wrap')?.contains(document.activeElement)) renderJobs();
  }, 30000);
})();
