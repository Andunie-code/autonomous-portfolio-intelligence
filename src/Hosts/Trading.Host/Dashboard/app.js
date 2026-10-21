// Portfolio Intelligence — read-only M9a dashboard (ADR-0020). No build step, no external code.
// Every value from the API is rendered with textContent (never innerHTML). The bearer token lives only in this
// tab's sessionStorage and is sent only in the Authorization header of same-origin requests.
'use strict';

const TOKEN_KEY = 'pi-ops-token';
const REFRESH_MS = 60_000;
const state = { tab: 'overview', timer: null, radar: null, radarSort: { key: 'symbol', dir: 1 }, fills: [] };

// ---------- helpers ----------
const $ = (id) => document.getElementById(id);

function el(tag, attrs, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value === true ? '' : String(value));
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

const nf = (digits) => new Intl.NumberFormat('tr-TR', { minimumFractionDigits: digits, maximumFractionDigits: digits });
const money = (v) => (v === null || v === undefined ? '—' : nf(2).format(Number(v)));
const num = (v, d = 4) => (v === null || v === undefined ? '—' : nf(d).format(Number(v)));
const pct = (v, d = 2) => (v === null || v === undefined ? '—' : nf(d).format(Number(v) * 100) + ' %');
const pctRaw = (v, d = 2) => (v === null || v === undefined ? '—' : nf(d).format(Number(v)) + ' %');
const timeFormat = new Intl.DateTimeFormat('tr-TR', { timeZone: 'UTC', day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false });
const time = (v) => (v ? `${timeFormat.format(new Date(v))} UTC` : '—');
// Signed amounts carry an explicit sign, so gains and losses do not rely on colour alone.
const signed = (v) => (v === null || v === undefined ? '—' : (Number(v) > 0 ? '+' : '') + money(v));
const signedPct = (v) => (v === null || v === undefined ? '—' : (Number(v) > 0 ? '+' : '') + pct(v));
const signedPctRaw = (v) => (v === null || v === undefined ? '—' : (Number(v) > 0 ? '+' : '') + pctRaw(v));
const signClass = (v) => (Number(v) > 0 ? 'pos' : Number(v) < 0 ? 'neg' : '');
const short = (hash, n = 12) => (hash ? String(hash).slice(0, n) + '…' : '—');

function badge(text, kind) { return el('span', { class: `badge b-${kind}`, text }); }

function stateBadge(s) {
  const kinds = { PaperTrading: 'good', TestnetActive: 'good', Frozen: 'info', Disabled: 'bad', Retired: 'muted', Rejected: 'bad' };
  return badge(s, kinds[s] || 'muted');
}

function table(columns, rows, onRow) {
  const head = el('tr', {}, columns.map((c) => el('th', { class: c.num ? 'num' : '', text: c.label })));
  const body = rows.map((row) => el('tr', { class: onRow ? 'clickable' : '', tabindex: onRow ? '0' : null, onclick: onRow ? () => onRow(row) : null,
    onkeydown: onRow ? (event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onRow(row); } } : null },
    columns.map((c) => {
      const value = c.render ? c.render(row) : row[c.key];
      return el('td', { class: [c.num ? 'num' : '', c.cls ? c.cls(row) : ''].join(' ').trim() }, value instanceof Node ? value : String(value ?? '—'));
    })));
  return el('table', {}, el('thead', {}, head), el('tbody', {}, body.length ? body : [el('tr', {}, el('td', { colspan: columns.length, class: 'muted', text: 'Kayıt yok.' }))]));
}

function meter(label, value, limit, warn) {
  const share = Math.max(0, Number(value) || 0);
  const scale = Math.max(limit || 0, share, 0.0001) * 1.15;
  const fill = el('div', { class: 'fill' });
  fill.style.width = `${Math.min(100, (share / scale) * 100)}%`;
  if (limit && share >= limit) fill.classList.add('bad'); else if (warn && share >= warn) fill.classList.add('warn');
  const track = el('div', { class: 'track' }, fill);
  for (const [mark, cls] of [[limit, 'mark'], [warn, 'mark warnmark']]) {
    if (!mark) continue;
    const m = el('div', { class: cls, title: pct(mark, 0) });
    m.style.left = `${(mark / scale) * 100}%`;
    track.append(m);
  }
  const limits = [limit ? `tavan ${pct(limit, 0)}` : null, warn ? `uyarı ${pct(warn, 0)}` : null].filter(Boolean).join(' · ');
  return el('div', { class: 'meter' }, el('div', { class: 'head' }, el('span', { text: label }), el('span', { class: 'mono', text: `${pct(share)}${limits ? '  (' + limits + ')' : ''}` })), track);
}

function kpi(label, value, cls) { return el('div', { class: 'kpi' }, el('div', { class: 'label', text: label }), el('div', { class: `value ${cls || ''}`, text: value })); }

// ---------- API ----------
class Unauthorized extends Error {}

async function api(path) {
  const token = sessionStorage.getItem(TOKEN_KEY);
  if (!token) throw new Unauthorized();
  const response = await fetch(path, { headers: { Authorization: `Bearer ${token}` }, cache: 'no-store', credentials: 'omit', referrerPolicy: 'no-referrer' });
  if (response.status === 401) throw new Unauthorized();
  if (!response.ok) {
    let message = `HTTP ${response.status}`;
    try { message = (await response.json()).error || message; } catch { /* not JSON */ }
    throw new Error(message);
  }
  return response.json();
}

// ---------- views ----------
async function renderOverview(view) {
  const data = await api('/overview');
  const t = data.total; const limits = data.limits || {};
  view.replaceChildren(
    el('div', { class: 'kpis' },
      kpi('Toplam equity', money(t.equity)), kpi('Toplam PnL', signed(t.pnl), signClass(t.pnl)),
      kpi('Gerçekleşmiş PnL', signed(t.realizedPnl), signClass(t.realizedPnl)), kpi('Gerçekleşmemiş PnL', signed(t.unrealizedPnl), signClass(t.unrealizedPnl)),
      kpi('Nakit', money(t.cash)), kpi('Sermaye', money(t.capital)), kpi('Toplam maruziyet', pct(t.exposure))),
    el('div', { class: 'grid2' }, data.runs.map((run) => el('div', { class: 'card' },
      el('h2', { text: `Run ${run.runId} · kohort ${run.cohortIds.join(', ')}` }),
      el('div', { class: 'kv' },
        el('span', { class: 'k', text: 'İşlem başlangıcı' }), el('span', { text: time(run.tradingStart) }),
        el('span', { class: 'k', text: 'Son mum' }), el('span', { text: time(run.barTime) }),
        el('span', { class: 'k', text: 'Equity / sermaye' }), el('span', { text: `${money(run.equity)} / ${money(run.capital)}` }),
        el('span', { class: 'k', text: 'PnL (gerç. / gerçekleşmemiş)' }), el('span', { class: signClass(run.pnl), text: `${signed(run.pnl)} (${signed(run.realizedPnl)} / ${signed(run.unrealizedPnl)})` }),
        el('span', { class: 'k', text: 'Açık pozisyon' }), el('span', { text: run.openPositions }),
        el('span', { class: 'k', text: 'Manifesto hash' }), el('span', { class: 'mono', text: short(run.manifestHash, 16) })),
      el('h3', { text: 'Limitler' }),
      meter('Drawdown (run zirvesinden)', run.drawdown, limits.maxDrawdown),
      meter('Toplam maruziyet', run.exposure, limits.maxTotalExposure),
      meter('Deneysel pariteler (E1/E2)', run.experimentalExposure, limits.experimentalPairsMaxExposure),
      el('h3', { text: `Korelasyon kümeleri (ρ* ${run.clusterThreshold ?? limits.clusterCapThreshold ?? '—'}, D29)` }),
      run.clusters.length === 0 ? el('p', { class: 'muted', text: 'Henüz korelasyon görüntüsü yok.' }) :
        run.clusters.filter((c) => c.exposure > 0 || c.symbols.length > 1).map((c) =>
          meter(`Küme ${c.cluster}: ${c.symbols.join(', ')}`, c.exposure, limits.clusterCapMaxExposure, limits.clusterWarningShare)),
      el('p', { class: 'note', text: 'Pozisyon olmayan tekil kümeler gizlendi. Tavanı aşacak alım kırpılır ya da reddedilir; zorunlu satış yapılmaz.' })))));
}

async function renderSleeves(view) {
  const [sleeves, fills, decisions] = await Promise.all([api('/sleeves'), api('/fills?limit=100'), api('/decisions?limit=100')]);
  state.fills = fills;
  const positions = sleeves.flatMap((s) => s.positions.map((p) => ({ ...p, runId: s.runId, sleeve: s.sleeve, name: s.name, cohortId: s.cohortId })));
  const lastBuy = (p) => fills.find((f) => f.run_id === p.runId && f.sleeve === p.sleeve && f.symbol === p.symbol && f.side === 'buy');
  view.replaceChildren(
    el('div', { class: 'card' }, el('h2', { text: 'Stratejiler (sleeve)' }), table([
      { label: 'Kohort', render: (s) => s.cohortId }, { label: 'Run', render: (s) => s.runId }, { label: 'Strateji', key: 'name' },
      { label: 'Durum', render: (s) => stateBadge(s.state) }, { label: 'Sermaye', num: true, render: (s) => money(s.capital) },
      { label: 'Equity', num: true, render: (s) => money(s.equity) }, { label: 'PnL', num: true, render: (s) => signed(s.pnl), cls: (s) => signClass(s.pnl) },
      { label: 'PnL %', num: true, render: (s) => signedPct(s.pnl / s.capital), cls: (s) => signClass(s.pnl) }, { label: 'Dolum', num: true, key: 'fills' },
      { label: 'Açık', num: true, render: (s) => s.positions.length }, { label: 'Tanım', render: (s) => el('span', { class: 'mono', text: short(s.definitionHash) }) },
    ], sleeves)),
    el('div', { class: 'card' }, el('h2', { text: 'Açık pozisyonlar' }), table([
      { label: 'Strateji', key: 'name' }, { label: 'Parite', key: 'symbol' }, { label: 'Giriş', render: (p) => time(p.entryTime) },
      { label: 'Giriş fiyatı', num: true, render: (p) => num(p.entryPrice, 6) }, { label: 'Son kapanış', num: true, render: (p) => num(p.lastClose, 6) },
      { label: 'Getiri', num: true, render: (p) => signedPctRaw(p.returnPct), cls: (p) => signClass(p.returnPct) },
      { label: 'Efektif stop', num: true, render: (p) => num(p.stop, 6) }, { label: 'D25 hard stop', num: true, render: (p) => num(p.hardStop, 6) },
      { label: 'Bağlayan', render: (p) => badge(p.stopSource === 'hard-stop-d25' ? 'D25 hard stop' : 'strateji stop', p.stopSource === 'hard-stop-d25' ? 'warn' : 'info') },
      { label: 'Stopa uzaklık', num: true, render: (p) => pctRaw(p.stopDistancePct) },
    ], positions, (p) => { const f = lastBuy(p); if (f) openTrace(f.id); })),
    el('p', { class: 'note', text: 'Bir pozisyona ya da dolum satırına tıklayınca izlenebilirlik zinciri açılır.' }),
    el('div', { class: 'card' }, el('h2', { text: 'Son dolumlar' }), table([
      { label: '#', key: 'id' }, { label: 'Zaman', render: (f) => time(f.fill_time) }, { label: 'Strateji', key: 'strategy' }, { label: 'Kohort', key: 'cohort_id' },
      { label: 'Parite', key: 'symbol' }, { label: 'Yön', render: (f) => badge(f.side === 'buy' ? 'alış' : 'satış', f.side === 'buy' ? 'good' : 'bad') },
      { label: 'Fiyat', num: true, render: (f) => num(f.price, 6) }, { label: 'Miktar', num: true, render: (f) => num(f.quantity, 6) },
      { label: 'Ücret', num: true, render: (f) => num(f.fee, 4) }, { label: 'Neden', key: 'reason' },
    ], fills, (f) => openTrace(f.id))),
    el('div', { class: 'card' }, el('h2', { text: 'Son sinyaller ve risk kararları (L1/L2)' }), table([
      { label: 'Mum', render: (e) => time(e.bar_time) }, { label: 'Run', key: 'run_id' }, { label: 'Strateji', key: 'strategy' }, { label: 'Parite', key: 'symbol' },
      { label: 'Tür', render: (e) => badge(e.kind, /Rejected|Canceled/.test(e.kind) ? 'bad' : e.kind === 'Clipped' ? 'warn' : /Signal/.test(e.kind) ? 'info' : 'good') },
      { label: 'Gerekçe', key: 'detail' }, { label: 'Önce', num: true, render: (e) => num(e.before, 2) }, { label: 'Sonra', num: true, render: (e) => num(e.after, 2) },
    ], decisions)));
}

function sortRadar(rows) {
  const { key, dir } = state.radarSort;
  return [...rows].sort((a, b) => {
    const x = key === 'symbol' ? a.symbol : a.indicators[key];
    const y = key === 'symbol' ? b.symbol : b.indicators[key];
    if (x === null || x === undefined) return 1;
    if (y === null || y === undefined) return -1;
    return (typeof x === 'string' ? x.localeCompare(y) : x - y) * dir;
  });
}

const statusView = {
  'entry-signal': ['Sinyal aktif', 'good'], 'exit-signal': ['Çıkış sinyali', 'bad'], 'in-position': ['Pozisyonda', 'info'],
  partial: ['Kısmen', 'warn'], far: ['Uzak', 'muted'], unknown: ['Isınma / BTC eksik', 'muted'],
};

function nearest(row) {
  const side = row.status === 'in-position' || row.status === 'exit-signal' ? 'exit' : 'entry';
  const open = row.atoms.filter((a) => a.side === side && a.value !== 'True' && a.distancePct !== null);
  if (open.length === 0) return null;
  return open.reduce((best, a) => (Math.abs(a.distancePct) < Math.abs(best.distancePct) ? a : best));
}

function atomText(a) {
  const ops = { gt: '>', gte: '≥', lt: '<', lte: '≤', cross_above: 'yukarı keser', cross_below: 'aşağı keser' };
  return `${a.side === 'entry' ? 'giriş' : 'çıkış'}: ${a.left} ${ops[a.op] || a.op} ${a.right} → ${a.value}` +
    (a.leftValue !== null ? ` (${num(a.leftValue, 6)} vs ${num(a.rightValue, 6)}${a.distancePct !== null ? `, uzaklık ${pctRaw(a.distancePct)}` : ''})` : '');
}

function radarCell(row, strategyName) {
  const [label, kind] = statusView[row.status] || [row.status, 'muted'];
  const near = nearest(row);
  const detail = row.status === 'in-position' || row.status === 'exit-signal'
    ? `stopa ${pctRaw(row.stopDistancePct)}`
    : near ? `${row.satisfied}/${row.total} · ${near.left} ${pctRaw(near.distancePct)}` : `${row.satisfied}/${row.total}`;
  return el('td', { class: 'clickable', tabindex: '0', title: row.atoms.map(atomText).join('\n'), onclick: () => showAtoms(strategyName, row),
    onkeydown: (event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); showAtoms(strategyName, row); } } },
    el('div', { class: 'cell-status' }, badge(label, kind), el('span', { class: 'muted mono', text: detail })));
}

function showAtoms(name, row) {
  const box = $('radar-detail');
  box.replaceChildren(el('h2', { text: `${name} · ${row.symbol}` }),
    el('div', { class: 'kv' }, el('span', { class: 'k', text: 'Giriş' }), el('span', { text: row.entry }), el('span', { class: 'k', text: 'Çıkış' }), el('span', { text: row.exit }),
      row.stop !== null ? [el('span', { class: 'k', text: 'Efektif stop' }), el('span', { text: `${num(row.stop, 6)} (${pctRaw(row.stopDistancePct)})` })] : []),
    el('ul', {}, row.atoms.map((a) => el('li', { class: a.value === 'True' ? 'pos' : a.value === 'Unknown' ? 'muted' : '', text: atomText(a) }))));
  box.hidden = false;
}

async function renderRadar(view) {
  const data = state.radar = await api('/radar');
  const regime = data.regime;
  const filter = el('input', { type: 'search', placeholder: 'Parite filtrele…', oninput: () => draw() });
  const scope = el('select', { onchange: () => draw() }, el('option', { value: 'all', text: 'Tümü' }), el('option', { value: 'core', text: 'Çekirdek' }), el('option', { value: 'watch', text: 'İzleme (E1)' }));
  const indicatorBox = el('div');
  const matrixBox = el('div');
  const columns = [
    ['symbol', 'Parite'], ['close', 'Kapanış'], ['ema50', 'EMA50'], ['ema100', 'EMA100'], ['sma150', 'SMA150'], ['atrPct', 'ATR %'], ['rsi14', 'RSI14'],
    ['supertrendDirection', 'Supertrend'], ['macdHistogram', 'MACD hist'], ['adx14', 'ADX14'], ['bollingerPercentB', 'BB %B'], ['bollingerBandwidth', 'BB genişlik'],
    ['donchianUpperDistancePct', 'Donchian üst %'], ['roc42', 'ROC42'], ['volumeRatio', 'Hacim / SMA20'],
  ];
  function draw() {
    const text = filter.value.trim().toUpperCase();
    const rows = sortRadar(data.symbols.filter((s) => (!text || s.symbol.includes(text)) && (scope.value === 'all' || s.category === scope.value)));
    const head = el('tr', {}, columns.map(([key, label]) => el('th', { class: `sortable ${key === 'symbol' ? '' : 'num'}`, text: label + (state.radarSort.key === key ? (state.radarSort.dir > 0 ? ' ▲' : ' ▼') : ''),
      onclick: () => { state.radarSort = { key, dir: state.radarSort.key === key ? -state.radarSort.dir : 1 }; draw(); } })));
    const body = rows.map((s) => el('tr', {}, columns.map(([key]) => {
      const v = key === 'symbol' ? s.symbol : s.indicators[key];
      if (key === 'symbol') return el('td', {}, s.symbol, ' ', s.category === 'watch' ? badge('izleme', 'muted') : null);
      if (key === 'supertrendDirection') return el('td', { class: v > 0 ? 'pos' : v < 0 ? 'neg' : '' , text: v > 0 ? '▲ yukarı' : v < 0 ? '▼ aşağı' : '—' });
      const digits = key === 'close' || key.startsWith('ema') || key.startsWith('sma') ? 6 : 2;
      return el('td', { class: 'num', text: key.endsWith('Pct') ? pctRaw(v) : num(v, digits) });
    })));
    indicatorBox.replaceChildren(el('table', {}, el('thead', {}, head), el('tbody', {}, body)));
    const symbols = [...new Set(data.strategies.flatMap((st) => st.symbols.map((r) => r.symbol)))].sort()
      .filter((sym) => (!text || sym.includes(text)) && (scope.value === 'all' || (data.symbols.find((s) => s.symbol === sym) || {}).category === scope.value));
    const matrix = el('table', {}, el('thead', {}, el('tr', {}, el('th', { text: 'Parite' }), data.strategies.map((st) => el('th', { text: `${st.name} (K${st.cohortId})` })))),
      el('tbody', {}, symbols.map((sym) => el('tr', {}, el('td', { text: sym }), data.strategies.map((st) => {
        const row = st.symbols.find((r) => r.symbol === sym);
        return row ? radarCell(row, st.name) : el('td', { class: 'muted', text: '—' });
      })))));
    matrixBox.replaceChildren(matrix);
  }
  view.replaceChildren(
    el('div', { class: 'card' }, el('h2', { text: 'BTCUSDT piyasa rejimi' }), el('div', { class: 'regime' },
      el('span', { class: `state ${regime.state === 'Bull' ? 'pos' : regime.state === 'Bear' ? 'neg' : 'muted'}`, text: regime.state }),
      el('span', { text: `Kapanış ${num(regime.close, 2)}` }), el('span', { text: `SMA300 ${num(regime.sma300, 2)} (${pctRaw(regime.sma300DistancePct)})` }),
      el('span', { text: `EMA100 ${num(regime.ema100, 2)} (${pctRaw(regime.ema100DistancePct)})` }), el('span', { class: 'muted', text: `Son mum ${time(data.asOf)} · ${data.barsUsed} mum` }))),
    el('div', { class: 'card' }, el('h2', { text: 'Sinyal yakınlığı (aktif stratejiler)' }), el('div', { class: 'toolbar' }, filter, scope), matrixBox,
      el('p', { class: 'note', text: 'Durum ve uzaklıklar stratejinin kendi interpreter\'ından gelir. Uzaklık: koşulun sağlanması için sol operandın sağ operanda göre hareket etmesi gereken yüzde. Bir hücreye tıklayınca tüm koşullar görünür.' })),
    el('div', { id: 'radar-detail', class: 'card', hidden: true }),
    el('div', { class: 'card' }, el('h2', { text: '11 gösterge (son kapanan 4h mum)' }), indicatorBox,
      el('p', { class: 'note', text: 'EMA, ATR, RSI ve ADX son 320 mumdan başlar; daha önce başlamış bir run\'ın değerinden çok az farklı olabilir. Radar karar vermez.' })));
  draw();
}

async function openTrace(fillId) {
  state.traceFill = fillId;
  selectTab('trace');
}

async function renderTrace(view) {
  const input = el('input', { type: 'number', min: '1', placeholder: 'Dolum #', value: state.traceFill || '' });
  const go = el('button', { text: 'Zinciri göster', onclick: () => { state.traceFill = Number(input.value); refresh(); } });
  const header = el('div', { class: 'card' }, el('h2', { text: 'İzlenebilirlik zinciri (dolum → karar → sinyal → strateji → manifesto → deney → veri seti)' }),
    el('div', { class: 'toolbar' }, input, go), el('p', { class: 'note', text: 'Paper modunda ayrı OrderIntent tablosu yoktur: L2 risk kararı olayı (Approved/Clipped/Rejected), intent ve risk kararını birlikte taşır.' }));
  if (!state.traceFill) { view.replaceChildren(header); return; }
  const p = await api(`/provenance/${encodeURIComponent(state.traceFill)}`);
  const kv = (pairs) => el('div', { class: 'kv' }, pairs.flatMap(([k, v]) => [el('span', { class: 'k', text: k }), v instanceof Node ? v : el('span', { class: 'mono', text: v ?? '—' })]));
  const f = p.fill; const d = p.decision; const s = p.signal; const c = p.cohort || {};
  view.replaceChildren(header, el('div', { class: 'card' }, el('ol', { class: 'chain' },
    el('li', {}, el('div', { class: 'step', text: `Dolum #${f.id} · ${f.side === 'buy' ? 'alış' : 'satış'} ${f.symbol}` }),
      kv([['Zaman', time(f.fill_time)], ['Fiyat × miktar', `${num(f.price, 6)} × ${num(f.quantity, 6)}`], ['Ücret', num(f.fee, 4)], ['Neden', f.reason]])),
    el('li', {}, el('div', { class: 'step', text: 'Risk kararı (L2) — OrderIntent karşılığı' }),
      d ? kv([['Karar', badge(d.kind, d.kind === 'Approved' ? 'good' : d.kind === 'Clipped' ? 'warn' : 'bad')], ['Gerekçe', d.detail], ['Nominal (önce → sonra)', `${num(d.before, 2)} → ${num(d.after, 2)}`], ['Mum', time(d.bar_time)]])
        : el('p', { class: 'muted', text: f.reason === 'Stop' ? 'Koruyucu stop dolumu: risk kararı gerekmez.' : 'Bu dolumun penceresinde L2 olayı yok.' })),
    el('li', {}, el('div', { class: 'step', text: 'Sinyal (L1)' }),
      s ? kv([['Tür', s.kind], ['Mum', time(s.bar_time)], ['Değer', num(s.value, 6)]]) : el('p', { class: 'muted', text: 'Sinyal yok (stop, manuel kapatma ya da tümünü kapat).' })),
    el('li', {}, el('div', { class: 'step', text: `Sleeve · ${p.sleeve.name}` }), kv([['Run / sleeve', `${p.sleeve.runId} / ${p.sleeve.sleeve}`], ['Kohort / üye', `${p.sleeve.cohortId} / ${p.sleeve.memberIndex}`], ['Sermaye', money(p.sleeve.capital)]])),
    el('li', {}, el('div', { class: 'step', text: 'StrategyDefinition' }),
      kv([['definitionHash', p.strategy.definitionHash], ['structureHash', p.strategy.structureHash], ['Hash doğrulandı', p.strategy.verified ? 'evet' : 'HAYIR'], ['Dondurulma', time(p.strategy.frozenAt)],
        ['Yaşam döngüsü', p.strategy.lifecycle.map((l) => `${l.from_state}→${l.to_state}`).join(', ')]]),
      el('pre', { text: JSON.stringify(p.strategy.definition, null, 2) })),
    el('li', {}, el('div', { class: 'step', text: `Kohort manifestosu · ${c.cohort ?? ''}` }),
      kv([['manifestHash', c.manifestHash], ['Protokol', `${c.protocol ?? '—'} (${c.protocolCommit ?? '—'})`], ['Engine', c.engineSha], ['Image', c.imageDigest],
        ['Gücü yetersiz', String(c.underpowered ?? '—')], ['Muafiyetler', (c.exemptions || []).join(' · ')]])),
    el('li', {}, el('div', { class: 'step', text: 'ExperimentResult (araştırma penceresi)' }),
      table([{ label: '#', key: 'experiment_id' }, { label: 'Varyant', key: 'variant' }, { label: 'Protokol', key: 'protocol_version' },
        { label: 'Engine', render: (e) => short(e.engine_git_sha) }, { label: 'Sonuç hash', render: (e) => el('span', { class: 'mono', text: short(e.result_hash, 16) }) }], p.experiments)),
    el('li', {}, el('div', { class: 'step', text: 'DatasetSnapshot' }), kv(p.datasetSnapshots.map((h) => ['snapshot', h]))))));
}

async function renderSystem(view) {
  const [sys, costs, traces] = await Promise.all([api('/system'), api('/ai/costs'), api('/ai/traces?limit=50')]);
  const ks = sys.tradingKillSwitch; const ai = sys.aiKillSwitch;
  view.replaceChildren(
    el('div', { class: 'kpis' },
      kpi('Trading kill switch', ks && ks.engaged ? 'AKTİF' : 'kapalı', ks && ks.engaged ? 'neg' : 'pos'),
      kpi('LLM acil durdurma', ai && ai.tripped ? `AKTİF (${ai.trigger_kind})` : 'kapalı', ai && ai.tripped ? 'neg' : 'pos'),
      kpi('Son döngü', sys.host.outcome || '—', sys.host.outcome === 'ok' ? 'pos' : 'neg'),
      kpi('Geç döngü (7 gün, > 60 sn)', sys.lateCycles7d ?? '—', Number(sys.lateCycles7d) > 0 ? 'neg' : ''),
      kpi('Reconciliation', sys.reconciliation ? (sys.reconciliation.ok ? 'temiz' : 'UYUMSUZ') : '—', sys.reconciliation && !sys.reconciliation.ok ? 'neg' : 'pos'),
      kpi('Reconciliation hatası (7 gün)', sys.reconciliationFailures7d ?? '—')),
    el('div', { class: 'grid2' },
      el('div', { class: 'card' }, el('h2', { text: 'Holdout ve kohort durumu' }),
        el('div', { class: 'kv' }, el('span', { class: 'k', text: 'HoldoutEnvironmentVerified' }), el('span', { text: sys.holdoutEnvironmentVerified })),
        table([{ label: 'Kohort', key: 'cohort_id' }, { label: 'Durum', render: (r) => badge(r.status, r.status === 'revealed' ? 'info' : 'muted') }, { label: 'Kayıt', render: (r) => time(r.recorded_at) }], sys.cohortStatus),
        el('p', { class: 'note', text: 'Holdout veritabanı ops tarafından okunamaz (tasarım gereği). Doğrulama durumu yalnızca evaluator sonuçlarından gelir.' })),
      el('div', { class: 'card' }, el('h2', { text: 'Son audit olayları' }),
        table([{ label: '#', key: 'id' }, { label: 'Zaman', render: (a) => time(a.recorded_at) }, { label: 'Aktör', key: 'actor' }, { label: 'Olay', key: 'event' }], sys.audit))),
    el('div', { class: 'card' }, el('h2', { text: 'Döngü metrikleri (son 24)' }), table([
      { label: 'Planlanan', render: (c) => time(c.scheduled_for) }, { label: 'Süre (ms)', num: true, render: (c) => num(c.duration_ms, 0) },
      { label: 'Gecikme (sn)', num: true, render: (c) => num(c.lag_seconds, 1) }, { label: 'Mum', num: true, key: 'bars_processed' },
      { label: 'Sonuç', render: (c) => badge(c.outcome, c.outcome === 'ok' ? 'good' : c.outcome === 'degraded' ? 'warn' : 'bad') },
      { label: 'Degraded', render: (c) => (c.degraded || []).join(', ') },
    ], sys.cycles)),
    el('div', { class: 'card' }, el('h2', { text: 'AI maliyet defteri (ay × görev sınıfı)' }), table([
      { label: 'Ay', key: 'month' }, { label: 'Görev sınıfı', key: 'task_class' }, { label: 'İş türü', key: 'job_kind' },
      { label: 'Kesinleşen USD', num: true, render: (r) => num(r.settled_usd, 6) }, { label: 'Rezervasyon USD', num: true, render: (r) => num(r.reserved_usd, 6) },
      { label: 'Çağrı', num: true, key: 'calls' }, { label: 'Girdi token', num: true, key: 'input_tokens' }, { label: 'Çıktı token', num: true, key: 'output_tokens' },
    ], costs)),
    el('div', { class: 'card' }, el('h2', { text: 'DecisionTrace (son 50)' }), table([
      { label: 'Zaman', render: (t) => time(t.at) }, { label: 'Görev', key: 'task_class' }, { label: 'Mod', key: 'mode' }, { label: 'Model', key: 'actual_model' },
      { label: 'Sonuç', render: (t) => badge(t.outcome, t.outcome === 'Completed' ? 'good' : 'warn') },
      { label: 'Degraded', render: (t) => (t.degraded ? badge('degraded', 'bad') : '') },
      { label: 'Validator', render: (t) => el('span', { class: 'mono', text: JSON.stringify(t.validator_results).slice(0, 80) }) },
      { label: 'USD', num: true, render: (t) => num(t.actual_usd, 6) }, { label: 'ms', num: true, key: 'latency_ms' },
    ], traces)));
}

// ---------- shell ----------
const renderers = { overview: renderOverview, sleeves: renderSleeves, radar: renderRadar, trace: renderTrace, system: renderSystem };

function showLogin(message) {
  sessionStorage.removeItem(TOKEN_KEY);
  clearInterval(state.timer);
  for (const id of ['tabs', 'refresh', 'logout']) $(id).hidden = true;
  document.querySelectorAll('.view').forEach((v) => { v.hidden = true; v.replaceChildren(); });
  $('login').hidden = false;
  $('login-error').hidden = !message;
  $('login-error').textContent = message || '';
}

async function refresh() {
  const view = $(`view-${state.tab}`);
  try {
    await renderers[state.tab](view);
    $('updated').textContent = `Güncellendi ${new Date().toLocaleTimeString('tr-TR', { hour12: false })}`;
  } catch (e) {
    if (e instanceof Unauthorized) { showLogin('Token reddedildi.'); return; }
    view.replaceChildren(el('div', { class: 'card error', text: `Hata: ${e.message}` }));
  }
}

function selectTab(tab) {
  state.tab = tab;
  document.querySelectorAll('#tabs button').forEach((b) => { b.classList.toggle('active', b.dataset.tab === tab); if (b.dataset.tab === tab) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current'); });
  document.querySelectorAll('.view').forEach((v) => { v.hidden = v.id !== `view-${tab}`; });
  refresh();
}

function start() {
  $('login').hidden = true;
  for (const id of ['tabs', 'refresh', 'logout']) $(id).hidden = false;
  selectTab(state.tab);
  clearInterval(state.timer);
  state.timer = setInterval(() => { if (!document.hidden && state.tab !== 'trace') refresh(); }, REFRESH_MS);
}

document.addEventListener('DOMContentLoaded', () => {
  $('login-form').addEventListener('submit', (event) => {
    event.preventDefault();
    const token = $('token').value.trim();
    $('token').value = '';
    if (!token) return;
    sessionStorage.setItem(TOKEN_KEY, token);
    start();
  });
  $('logout').addEventListener('click', () => showLogin(''));
  $('refresh').addEventListener('click', () => refresh());
  document.querySelectorAll('#tabs button').forEach((b) => b.addEventListener('click', () => selectTab(b.dataset.tab)));
  if (sessionStorage.getItem(TOKEN_KEY)) start(); else showLogin('');
});
