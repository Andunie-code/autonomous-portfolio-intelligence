// Portfolio Intelligence — read-only M9a dashboard (ADR-0020). No build step, no external code.
// Every value from the API is rendered with textContent (never innerHTML); icons are built with createElementNS.
// The bearer token lives only in this tab's sessionStorage and is sent only in the Authorization header of
// same-origin requests.
'use strict';

const TOKEN_KEY = 'pi-ops-token';
const THEME_KEY = 'pi-theme';
const REFRESH_MS = 60_000;
const state = { tab: 'overview', timer: null, radarSort: { key: 'symbol', dir: 1 }, fills: [], traceFill: null };

// ---------- names ----------
// Readable names of the frozen strategies; unknown names are shown as they are.
const STRATEGY_TITLES = {
  'ema-trend': 'EMA 50/100 Trend',
  supertrend: 'Supertrend 10/3',
  'h08-smabtc-n150-b300': 'H08 · BTC Filtreli SMA 150',
  'h04-macd-f12-s26-g12': 'H04 · MACD Sıfır Üstü Kesişim',
  'h07-volbo-n50-v2p5': 'H07 · Hacim Teyitli Donchian 50',
};
// Holdout cohort ids: Kohort 0 was registered as 1, Kohort 1 as 2.
const cohortLabel = (id) => (id === null || id === undefined ? '—' : `K${Number(id) - 1}`);
const strategyTitle = (name, cohortId) => `${STRATEGY_TITLES[name] || name}${cohortId ? ` (${cohortLabel(cohortId)})` : ''}`;

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
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

// Replaces a view's content. Unlike el(), the DOM's replaceChildren would print null as the text "null", so
// optional parts (a warning shown only sometimes) are dropped here.
function show(target, ...children) {
  target.replaceChildren(...children.flat(Infinity).filter((c) => c !== null && c !== undefined && c !== false));
}

// Inline SVG icons (24×24 strokes, Lucide-like shapes), decorative unless labelled by their parent.
const ICONS = {
  info: ['M12 16v-4', 'M12 8h.01', 'C12 12 10'],
  alert: ['M12 9v4', 'M12 17h.01', 'M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z'],
  up: ['M3 17l6-6 4 4 8-8', 'M14 7h7v7'],
  down: ['M3 7l6 6 4-4 8 8', 'M14 17h7v-7'],
  check: ['M20 6 9 17l-5-5'],
  x: ['M18 6 6 18', 'M6 6l12 12'],
  dot: ['C12 12 4'],
  zap: ['M13 2 3 14h9l-1 8 10-12h-9l1-8z'],
  clock: ['C12 12 10', 'M12 6v6l4 2'],
  target: ['C12 12 10', 'C12 12 6', 'C12 12 2'],
  pause: ['M10 4H6v16h4z', 'M18 4h-4v16h4z'],
  sun: ['C12 12 4', 'M12 2v2', 'M12 20v2', 'M4.9 4.9l1.4 1.4', 'M17.7 17.7l1.4 1.4', 'M2 12h2', 'M20 12h2', 'M4.9 19.1l1.4-1.4', 'M17.7 6.3l1.4-1.4'],
  moon: ['M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z'],
  refresh: ['M21 12a9 9 0 1 1-2.6-6.4L21 8', 'M21 3v5h-5'],
  shield: ['M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z'],
  link: ['M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7', 'M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7'],
};
const SVG_NS = 'http://www.w3.org/2000/svg';
function icon(name) {
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('class', 'icon');
  svg.setAttribute('aria-hidden', 'true');
  for (const d of ICONS[name] || []) {
    if (d.startsWith('C')) {
      const [cx, cy, r] = d.slice(1).trim().split(/\s+/);
      const c = document.createElementNS(SVG_NS, 'circle');
      c.setAttribute('cx', cx); c.setAttribute('cy', cy); c.setAttribute('r', r);
      svg.append(c);
    } else {
      const p = document.createElementNS(SVG_NS, 'path');
      p.setAttribute('d', d);
      svg.append(p);
    }
  }
  return svg;
}

const nf = (digits) => new Intl.NumberFormat('tr-TR', { minimumFractionDigits: digits, maximumFractionDigits: digits });
const isNil = (v) => v === null || v === undefined || Number.isNaN(Number(v));
const money = (v) => (isNil(v) ? '—' : nf(2).format(Number(v)));
const num = (v, d = 4) => (isNil(v) ? '—' : nf(d).format(Number(v)));
// Prices: digits by magnitude, so 0,00001234 and 64.000,00 both read well.
const priceDigits = (v) => { const a = Math.abs(Number(v)); return a >= 1000 ? 2 : a >= 1 ? 4 : a >= 0.01 ? 6 : 8; };
const price = (v) => (isNil(v) ? '—' : nf(priceDigits(v)).format(Number(v)));
const pct = (v, d = 2) => (isNil(v) ? '—' : `%${nf(d).format(Number(v) * 100)}`);
const pctRaw = (v, d = 2) => (isNil(v) ? '—' : `%${nf(d).format(Number(v))}`);
const timeFormat = new Intl.DateTimeFormat('tr-TR', { timeZone: 'UTC', day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false });
const time = (v) => (v ? `${timeFormat.format(new Date(v))} UTC` : '—');
// Signed amounts carry an explicit sign, so gains and losses do not rely on colour alone.
const sign = (v) => (Number(v) > 0 ? '+' : Number(v) < 0 ? '−' : '');
const signed = (v) => (isNil(v) ? '—' : sign(v) + money(Math.abs(Number(v))));
const signedPct = (v) => (isNil(v) ? '—' : sign(v) + pct(Math.abs(Number(v))));
const signedPctRaw = (v) => (isNil(v) ? '—' : sign(v) + pctRaw(Math.abs(Number(v))));
const signClass = (v) => (Number(v) > 0 ? 'pos' : Number(v) < 0 ? 'neg' : '');
const short = (hash, n = 12) => (hash ? `${String(hash).slice(0, n)}…` : '—');

function badge(text, kind, iconName) { return el('span', { class: `badge b-${kind}` }, iconName ? icon(iconName) : null, text); }

const STATE_VIEW = {
  PaperTrading: ['Paper trading', 'good'], TestnetActive: ['Testnet', 'good'], Frozen: ['Donduruldu', 'info'],
  Disabled: ['Devre dışı', 'bad'], Retired: ['Emekli', 'muted'], Rejected: ['Reddedildi', 'bad'],
};
function stateBadge(s) { const [label, kind] = STATE_VIEW[s] || [s, 'muted']; return badge(label, kind); }

const EVENT_VIEW = {
  EntrySignal: ['Giriş sinyali', 'info', 'zap'], ExitSignal: ['Çıkış sinyali', 'info', 'zap'],
  Approved: ['Onaylandı', 'good', 'check'], Clipped: ['Kırpıldı', 'warn', 'alert'], Rejected: ['Reddedildi', 'bad', 'x'], Canceled: ['İptal', 'bad', 'x'],
};
function eventBadge(kind) { const [label, k, i] = EVENT_VIEW[kind] || [kind, 'muted']; return badge(label, k, i); }

function strategyCell(name, cohortId) {
  return el('div', { class: 'strategy' }, el('span', { class: 'title', text: strategyTitle(name, cohortId) }), el('span', { class: 'code', text: name }));
}

function callout(text, kind = 'info') {
  return el('div', { class: `callout ${kind === 'info' ? '' : kind}`.trim(), role: kind === 'info' ? null : 'note' },
    icon(kind === 'info' ? 'info' : 'alert'), el('div', {}, text));
}

function card(title, ...children) { return el('div', { class: 'card' }, el('div', { class: 'card-head' }, el('h2', { text: title })), children); }
function scrollCard(title, ...children) { const c = card(title, ...children); c.classList.add('scroll'); return c; }

function table(columns, rows, onRow) {
  const head = el('tr', {}, columns.map((c) => el('th', { class: c.num ? 'num' : '', scope: 'col', text: c.label })));
  const body = rows.map((row) => el('tr', {
    class: onRow ? 'clickable' : '', tabindex: onRow ? '0' : null, onclick: onRow ? () => onRow(row) : null,
    onkeydown: onRow ? (event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onRow(row); } } : null,
  }, columns.map((c) => {
    const value = c.render ? c.render(row) : row[c.key];
    return el('td', { class: [c.num ? 'num' : '', c.cls ? c.cls(row) : ''].join(' ').trim() }, value instanceof Node ? value : String(value ?? '—'));
  })));
  return el('table', {}, el('thead', {}, head),
    el('tbody', {}, body.length ? body : [el('tr', {}, el('td', { colspan: columns.length, class: 'empty', text: 'Kayıt yok.' }))]));
}

function meter(label, value, limit, warn) {
  const share = Math.max(0, Number(value) || 0);
  const scale = Math.max(limit || 0, share, 0.0001) * 1.15;
  const fill = el('div', { class: 'fill' });
  fill.style.width = `${Math.min(100, (share / scale) * 100)}%`;
  if (limit && share >= limit) fill.classList.add('bad'); else if (warn && share >= warn) fill.classList.add('warn');
  const track = el('div', { class: 'track', role: 'meter', 'aria-valuemin': '0', 'aria-valuemax': String(limit || 1), 'aria-valuenow': String(share), 'aria-label': label }, fill);
  for (const [mark, cls] of [[limit, 'mark'], [warn, 'mark warnmark']]) {
    if (!mark) continue;
    const m = el('div', { class: cls, title: pct(mark, 0) });
    m.style.left = `${(mark / scale) * 100}%`;
    track.append(m);
  }
  const limits = [limit ? `tavan ${pct(limit, 0)}` : null, warn ? `uyarı ${pct(warn, 0)}` : null].filter(Boolean).join(' · ');
  return el('div', { class: 'meter' }, el('div', { class: 'head' }, el('span', { text: label }),
    el('span', { class: 'mono', text: `${pct(share)}${limits ? `  (${limits})` : ''}` })), track);
}

function micro(on, total, partial) {
  return el('span', { class: `micro ${partial ? 'partial' : ''}`.trim(), 'aria-label': `${on}/${total} koşul`, role: 'img' },
    Array.from({ length: total }, (_, i) => el('span', { class: i < on ? 'on' : '' })));
}

function kpi(label, value, cls, sub) {
  return el('div', { class: 'kpi' }, el('div', { class: 'label', text: label }), el('div', { class: `value ${cls || ''}`.trim(), text: value }), sub ? el('div', { class: 'sub', text: sub }) : null);
}

const kv = (pairs) => el('div', { class: 'kv' }, pairs.flatMap(([k, v]) => [el('span', { class: 'k', text: k }), v instanceof Node ? v : el('span', { class: 'v mono', text: v ?? '—' })]));

// ---------- clocks ----------
// Trading.Host's cycle runs at HH:00:15 UTC; 4h candles close at 00, 04, …, 20 UTC and are processed by the next cycle.
function nextCheck(now) { const t = new Date(now); t.setUTCMinutes(0, 15, 0); if (t <= now) t.setUTCHours(t.getUTCHours() + 1); return t; }
function nextClose(now) { const t = new Date(now); t.setUTCMinutes(0, 0, 0); t.setUTCHours(Math.floor(t.getUTCHours() / 4) * 4 + 4); return t; }
const two = (n) => String(n).padStart(2, '0');
const hhmm = (t) => `${two(t.getUTCHours())}:${two(t.getUTCMinutes())}:${two(t.getUTCSeconds())} UTC`;
function span(ms) {
  const s = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(s / 3600); const m = Math.floor((s % 3600) / 60); const sec = s % 60;
  return h > 0 ? `${h} sa ${two(m)} dk` : `${two(m)} dk ${two(sec)} sn`;
}
function tickClocks() {
  const now = new Date();
  const check = nextCheck(now); const close = nextClose(now);
  $('next-check-at').textContent = `(${hhmm(check)})`;
  $('next-check').textContent = span(check - now);
  $('next-close-at').textContent = `(${hhmm(close).slice(0, 5)} UTC)`;
  $('next-close').textContent = span(close - now);
}

// ---------- theme ----------
function storedTheme() { try { return localStorage.getItem(THEME_KEY); } catch { return null; } }
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  const button = $('theme');
  button.replaceChildren(icon(theme === 'dark' ? 'sun' : 'moon'));
  button.setAttribute('aria-label', theme === 'dark' ? 'Açık temaya geç' : 'Koyu temaya geç');
  button.title = button.getAttribute('aria-label');
}
function toggleTheme() {
  const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  try { localStorage.setItem(THEME_KEY, next); } catch { /* per-viewer convenience only */ }
  applyTheme(next);
}

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

// ---------- D30 progress (m6-protocol §6.1) ----------
function progress(label, value, target, unit) {
  const share = Math.max(0, Math.min(1, Number(value) / target));
  const fill = el('div', { class: `fill ${share >= 1 ? '' : 'warn'}`.trim() });
  fill.style.width = `${share * 100}%`;
  return el('div', { class: 'meter' }, el('div', { class: 'head' }, el('span', { text: label }), el('span', { class: 'mono', text: `${num(value, unit === 'gün' ? 1 : 0)}/${target} ${unit}` })),
    el('div', { class: 'track', role: 'meter', 'aria-valuemin': '0', 'aria-valuemax': String(target), 'aria-valuenow': String(value), 'aria-label': label }, fill));
}

const MISSING_TEXT = { days: '14 gün dolmadı', bars: '60 mum dolmadı', 'critical-errors': 'son 7 günde kritik hata var' };

function promotionCard(promotion) {
  const rows = promotion.strategies.filter((s) => ['PaperTrading', 'TestnetActive'].includes(s.state));
  return card('Testnet terfisi (D30: ≥ 14 gün, ≥ 60 işlenmiş 4h mum, son 7 günde 0 kritik hata)',
    rows.length === 0 ? el('p', { class: 'note', text: 'Paper\'da strateji yok.' }) : rows.map((s) => el('div', { class: 'promotion' },
      el('div', { class: 'card-head' }, strategyCell(s.name, s.cohortId),
        s.state === 'TestnetActive' ? badge('Testnet', 'good', 'check') : s.eligible ? badge('Uygun · kullanıcı onayı bekliyor', 'good', 'check') : badge('Bekliyor', 'muted', 'clock'),
        s.experimental ? badge('deneysel', 'warn', 'alert') : null),
      progress(`${cohortLabel(s.cohortId).replace('K', 'Kohort ')}: gün`, s.days, promotion.minimumDays, 'gün'),
      progress('İşlenmiş 4h mum', s.bars, promotion.minimumBars, 'mum'),
      el('p', { class: 'note', text: `Kritik hata (7 gün): ${s.criticalErrors7d} · kesinti oranı ${pct(s.outageRatio)} · paper başlangıcı ${time(s.paperSince)}`
        + (s.missing.length ? ` · eksik: ${s.missing.map((m) => MISSING_TEXT[m] || m).join(', ')}` : '') }))),
    el('p', { class: 'note', text: 'Geçiş yalnızca kullanıcı oturumuyla yapılır (scripts/m6-testnet-activate.ps1, confirm: true). Doğrulama gerekmez; Validated olmayan stratejiler "deneysel" etiketi taşır.' }));
}

// ---------- overview ----------
async function renderOverview(view) {
  const [data, promotion] = await Promise.all([api('/overview'), api('/promotion').catch(() => null)]);
  const t = data.total; const limits = data.limits || {};
  const waiting = data.runs.filter((r) => r.awaitingFirstBar).length;
  show(view, 
    callout('Bu ekran paper run\'larının toplamını (Testnet run\'ları ayrı kartlarda, toplama girmez) ve her run\'ın risk limitlerine göre durumunu gösterir. Sistem her 4h mum kapanışında stratejileri çalıştırır; '
      + 'kararlar risk kurallarından (toplam maruziyet, drawdown, D29 korelasyon kümesi tavanı) geçer ve emir bir sonraki mumun açılışında dolar.'),
    el('div', { class: 'kpis' },
      kpi('Toplam equity', money(t.equity), '', `sermaye ${money(t.capital)}`),
      kpi('Toplam PnL', signed(t.pnl), signClass(t.pnl), t.capital ? signedPct(t.pnl / t.capital) : ''),
      kpi('Gerçekleşmiş PnL', signed(t.realizedPnl), signClass(t.realizedPnl)),
      kpi('Gerçekleşmemiş PnL', signed(t.unrealizedPnl), signClass(t.unrealizedPnl)),
      kpi('Nakit', money(t.cash), '', waiting ? `${waiting} run ilk mumu bekliyor; başlangıç nakdi dahil` : ''),
      kpi('Toplam maruziyet', pct(t.exposure), '', limits.maxTotalExposure ? `tavan ${pct(limits.maxTotalExposure, 0)}` : '')),
    el('div', { class: 'grid2' }, data.runs.map((run) => el('div', { class: 'card' },
      el('div', { class: 'card-head' },
        el('div', { class: 'run-head' }, el('h2', { text: `Run ${run.runId}` }), run.venue === 'testnet' ? badge('Testnet', 'warn') : null,
          run.cohortIds.map((c) => badge(`Kohort ${cohortLabel(c).slice(1)} · #${c}`, 'accent'))),
        run.awaitingFirstBar ? badge('İlk 4h mum kapanışı bekleniyor', 'warn', 'clock') : badge(`Son mum ${time(run.barTime)}`, 'muted', 'clock')),
      run.awaitingFirstBar ? callout('Bu run henüz ilk mumunu işlemedi: sermayenin tamamı nakitte, pozisyon yok. İlk adım, işlem başlangıcından sonraki ilk 4h mum kapanışında çalışır.', 'warn') : null,
      kv([
        ['İşlem başlangıcı', time(run.tradingStart)],
        ['Equity / sermaye', `${money(run.equity)} / ${money(run.capital)}`],
        ['Nakit', money(run.cash)],
        ['PnL', el('span', { class: `v mono ${signClass(run.pnl)}`, text: `${signed(run.pnl)}  (gerçekleşmiş ${signed(run.realizedPnl)} · açık ${signed(run.unrealizedPnl)})` })],
        ['Açık pozisyon', String(run.openPositions)],
        ['Manifesto hash', short(run.manifestHash, 16)],
      ]),
      el('h3', { text: 'Risk limitleri' }),
      meter('Drawdown (run zirvesinden)', run.drawdown, limits.maxDrawdown),
      meter('Toplam maruziyet', run.exposure, limits.maxTotalExposure),
      meter('Deneysel pariteler (E1/E2)', run.experimentalExposure, limits.experimentalPairsMaxExposure),
      el('h3', { text: `Korelasyon kümeleri · ρ* ${run.clusterThreshold ?? limits.clusterCapThreshold ?? '—'} (D29)` }),
      run.clusters.length === 0 ? el('p', { class: 'note', text: 'Henüz korelasyon görüntüsü yok (ilk adımlardan sonra oluşur).' })
        : run.clusters.filter((c) => c.exposure > 0 || c.symbols.length > 1).map((c) =>
          meter(`Küme ${c.cluster}: ${c.symbols.join(', ')}`, c.exposure, limits.clusterCapMaxExposure, limits.clusterWarningShare)),
      el('p', { class: 'note', text: 'Pozisyonu olmayan tekil kümeler gizlendi. Tavanı aşacak alım kırpılır ya da reddedilir; zorunlu satış yapılmaz.' })))),
    promotion ? promotionCard(promotion) : null);
}

// ---------- testnet (m6-protocol §7) ----------
const ORDER_STATUS_VIEW = {
  Submitting: ['Gönderiliyor', 'info'], Accepted: ['Borsada', 'good'], PartiallyFilled: ['Kısmi dolum', 'warn'], Filled: ['Doldu', 'good'],
  Canceled: ['İptal', 'muted'], Expired: ['Süresi doldu', 'muted'], ExchangeRejected: ['Borsa reddi', 'bad'], Unknown: ['Bilinmiyor', 'bad'],
  NeverAccepted: ['Borsaya ulaşmadı', 'muted'], LocalRejected: ['Filtre reddi', 'warn'], CancelRequested: ['İptal isteniyor', 'info'], none: ['Stop yok', 'bad'],
};
function orderBadge(status) { const [label, kind] = ORDER_STATUS_VIEW[status] || [status, 'muted']; return badge(label, kind); }
const PURPOSE_TEXT = { Entry: 'Giriş', Stop: 'Koruyucu stop', Exit: 'Çıkış', Close: 'Kapatma' };

async function renderTestnet(view) {
  const data = await api('/testnet');
  const dev = data.deviations || {};
  show(view,
    callout('Testnet emirleri outbox üzerinden gider: emir önce veritabanına "gönderiliyor" olarak yazılır, sonra borsaya gönderilir. Zaman aşımı ya da 5xx durumunda emir "bilinmiyor" olur ve '
      + 'clientOrderId ile sorgulanarak çözülür; asla körü körüne yeniden gönderilmez. Her pozisyonun borsada bir koruyucu stop emri olmalıdır; kill switch giriş emirlerini iptal eder, stop\'lara dokunmaz.'),
    data.enabled ? null : callout('Testnet bu Trading.Host\'ta kapalı (Trading:Testnet ve Testnet anahtarı). Paper sürüyor.', 'warn'),
    el('div', { class: 'kpis' },
      kpi('Testnet run', String(data.runs.filter((r) => !r.closed_at).length), '', data.runs.length ? `toplam ${data.runs.length}` : 'henüz yok'),
      kpi('Bilinmeyen emir', String(data.unknown ?? 0), Number(data.unknown) > 0 ? 'neg' : 'pos'),
      kpi('Korumasız pozisyon', String(data.unprotected ?? 0), Number(data.unprotected) > 0 ? 'neg' : 'pos'),
      kpi('Reconciliation', data.reconciliation ? (data.reconciliation.ok ? 'Temiz' : 'UYUMSUZ') : '—', data.reconciliation ? (data.reconciliation.ok ? 'pos' : 'neg') : '',
        data.reconciliation ? `${data.reconciliation.reason || ''} ${time(data.reconciliation.checked_at)}`.trim() : ''),
      kpi('Bakiye tabanı', data.baseline ? time(data.baseline.recorded_at) : '—', '', data.baseline ? data.baseline.source : ''),
      kpi('Kural snapshot', data.rules ? short(data.rules.rules_hash) : '—', '', data.rules ? time(data.rules.recorded_at) : '')),
    scrollCard('Pozisyonlar ve borsadaki koruyucu stop', table([
      { label: 'Strateji', render: (p) => strategyCell(p.strategy, p.cohortId) }, { label: 'Run', render: (p) => p.runId },
      { label: 'Parite', render: (p) => el('strong', { text: p.symbol }) }, { label: 'Miktar', num: true, render: (p) => num(p.quantity, 6) },
      { label: 'Motor stop', num: true, render: (p) => price(p.engineStop) }, { label: 'Borsa stop', num: true, render: (p) => price(p.exchangeStop) },
      { label: 'Tip', key: 'stopType' }, { label: 'Durum', render: (p) => orderBadge(p.stopStatus) },
    ], data.positions)),
    el('div', { class: 'grid2' },
      card('Outbox (amaç × durum)', table([
        { label: 'Amaç', render: (r) => PURPOSE_TEXT[r.purpose] || r.purpose }, { label: 'Durum', render: (r) => orderBadge(r.status) }, { label: 'Adet', num: true, key: 'n' },
      ], data.outbox)),
      card('L3 Testnet sapmaları', kv([
        ['Borsa reddi', String(dev.exchange_rejected ?? 0)], ['Filtre reddi (gönderilmedi)', String(dev.filter_rejected ?? 0)],
        ['Dolmayan IOC giriş', String(dev.ioc_unfilled ?? 0)], ['Kısmi dolum', String(dev.partial_fills ?? 0)],
        ['STOP_LOSS_LIMIT yedeği', String(dev.stop_limit_fallback ?? 0)], ['Borsaya ulaşmayan', String(dev.never_accepted ?? 0)],
        ['Stop seviyesinin altında dolan stop', String(dev.stop_fills_below_level ?? 0)], ['Stop kayması (bps, ort.)', dev.stop_slippage_bps === null || dev.stop_slippage_bps === undefined ? '—' : num(dev.stop_slippage_bps, 2)],
      ]), el('p', { class: 'note', text: 'Bilgilendiricidir; doğrulamaya girmez (ADR-0015).' }))),
    scrollCard('Son emirler', table([
      { label: '#', key: 'id', num: true }, { label: 'Zaman', render: (o) => time(o.created_at) }, { label: 'Parite', render: (o) => el('strong', { text: o.symbol }) },
      { label: 'Amaç', render: (o) => PURPOSE_TEXT[o.purpose] || o.purpose }, { label: 'Tip', key: 'order_type' }, { label: 'Yön', key: 'side' },
      { label: 'Miktar', num: true, render: (o) => num(o.quantity, 6) }, { label: 'Limit', num: true, render: (o) => price(o.price) }, { label: 'Stop', num: true, render: (o) => price(o.stop_price) },
      { label: 'Durum', render: (o) => orderBadge(o.status) }, { label: 'Gerekçe', render: (o) => o.reason || '' },
      { label: 'clientOrderId', render: (o) => el('span', { class: 'mono muted', text: o.client_order_id }) },
    ], data.recentOrders)),
    scrollCard('Testnet run\'ları', table([
      { label: 'Run', key: 'run_id', num: true }, { label: 'Kohort', render: (r) => (r.cohort_ids || []).map((c) => cohortLabel(c)).join(', ') },
      { label: 'İşlem başlangıcı', render: (r) => time(r.trading_start) },
      { label: 'Durum', render: (r) => (r.closed_at ? badge(`Kapandı (${r.closed_reason})`, 'muted') : badge('Açık', 'good')) },
    ], data.runs)));
}

// ---------- sleeves ----------
async function renderSleeves(view) {
  const [sleeves, fills, decisions] = await Promise.all([api('/sleeves'), api('/fills?limit=100'), api('/decisions?limit=100')]);
  state.fills = fills;
  const cohortOf = Object.fromEntries(sleeves.map((s) => [`${s.runId}/${s.sleeve}`, s.cohortId]));
  const positions = sleeves.flatMap((s) => s.positions.map((p) => ({ ...p, runId: s.runId, sleeve: s.sleeve, name: s.name, cohortId: s.cohortId })));
  const lastBuy = (p) => fills.find((f) => f.run_id === p.runId && f.sleeve === p.sleeve && f.symbol === p.symbol && f.side === 'buy');
  show(view, 
    callout('Her strateji kendi sleeve\'inde (ayrı nakit ve pozisyonlar) çalışır. Bir sinyal önce L1 sinyal olayı olarak yazılır, ardından L2 risk kararıyla onaylanır, kırpılır ya da reddedilir; '
      + 'onaylanan emir bir sonraki 4h mumun açılış fiyatından dolar. Bir pozisyona ya da dolum satırına tıklayınca izlenebilirlik zinciri açılır.'),
    scrollCard('Stratejiler (sleeve)', table([
      { label: 'Strateji', render: (s) => strategyCell(s.name, s.cohortId) }, { label: 'Run', render: (s) => s.runId },
      { label: 'Durum', render: (s) => stateBadge(s.state) }, { label: 'Sermaye', num: true, render: (s) => money(s.capital) },
      { label: 'Equity', num: true, render: (s) => money(s.equity) }, { label: 'PnL', num: true, render: (s) => signed(s.pnl), cls: (s) => signClass(s.pnl) },
      { label: 'PnL %', num: true, render: (s) => signedPct(s.pnl / s.capital), cls: (s) => signClass(s.pnl) }, { label: 'Dolum', num: true, key: 'fills' },
      { label: 'Açık', num: true, render: (s) => s.positions.length }, { label: 'Tanım hash', render: (s) => el('span', { class: 'mono muted', text: short(s.definitionHash) }) },
    ], sleeves)),
    scrollCard('Açık pozisyonlar', table([
      { label: 'Strateji', render: (p) => strategyCell(p.name, p.cohortId) }, { label: 'Parite', render: (p) => el('strong', { text: p.symbol }) },
      { label: 'Giriş', render: (p) => time(p.entryTime) },
      { label: 'Giriş fiyatı', num: true, render: (p) => price(p.entryPrice) }, { label: 'Son kapanış', num: true, render: (p) => price(p.lastClose) },
      { label: 'Getiri', num: true, render: (p) => signedPctRaw(p.returnPct), cls: (p) => signClass(p.returnPct) },
      { label: 'Efektif stop', num: true, render: (p) => price(p.stop) }, { label: 'D25 hard stop', num: true, render: (p) => price(p.hardStop) },
      { label: 'Bağlayan stop', render: (p) => badge(p.stopSource === 'hard-stop-d25' ? 'D25 hard stop (%10)' : 'Strateji stop\'u', p.stopSource === 'hard-stop-d25' ? 'warn' : 'info', 'shield') },
      { label: 'Stopa uzaklık', num: true, render: (p) => pctRaw(p.stopDistancePct) },
    ], positions, (p) => { const f = lastBuy(p); if (f) openTrace(f.id); })),
    scrollCard('Son dolumlar', table([
      { label: '#', key: 'id', num: true }, { label: 'Zaman', render: (f) => time(f.fill_time) }, { label: 'Strateji', render: (f) => strategyCell(f.strategy, f.cohort_id) },
      { label: 'Parite', render: (f) => el('strong', { text: f.symbol }) },
      { label: 'Yön', render: (f) => badge(f.side === 'buy' ? 'Alış' : 'Satış', f.side === 'buy' ? 'good' : 'bad', f.side === 'buy' ? 'up' : 'down') },
      { label: 'Fiyat', num: true, render: (f) => price(f.price) }, { label: 'Miktar', num: true, render: (f) => num(f.quantity, 6) },
      { label: 'Ücret', num: true, render: (f) => num(f.fee, 4) }, { label: 'Neden', key: 'reason' },
    ], fills, (f) => openTrace(f.id))),
    scrollCard('Son sinyaller (L1) ve risk kararları (L2)', table([
      { label: 'Mum', render: (e) => time(e.bar_time) }, { label: 'Strateji', render: (e) => strategyCell(e.strategy, cohortOf[`${e.run_id}/${e.sleeve}`]) },
      { label: 'Parite', render: (e) => el('strong', { text: e.symbol }) }, { label: 'Olay', render: (e) => eventBadge(e.kind) },
      { label: 'Gerekçe', key: 'detail' }, { label: 'Nominal önce', num: true, render: (e) => num(e.before, 2) }, { label: 'Nominal sonra', num: true, render: (e) => num(e.after, 2) },
    ], decisions)));
}

// ---------- radar ----------
const ARROW = { gt: '>', gte: '≥', lt: '<', lte: '≤' };
const distanceText = (a) => (a.distancePct !== null && a.distancePct !== undefined ? pctRaw(Math.abs(a.distancePct)) : `fark ${num(Math.abs(a.gap), 4)}`);
const isZero = (name) => /^-?0(\.0+)?$/.test(name);

/** One condition in words: what holds now, and how far the other side is. */
function describeAtom(a, panel) {
  const L = a.leftName; const R = a.rightName;
  if (a.value === 'Unknown' || isNil(a.leftValue) || isNil(a.rightValue)) return `${L} / ${R}: veri yok (ısınma ya da eksik BTC mumu)`;
  const ok = a.value === 'True';
  const l = Number(a.leftValue); const r = Number(a.rightValue);
  const tick = ok ? ' ✓' : '';
  if (/ yönü$/.test(L)) { // Supertrend direction (+1 / −1) against 0
    const bull = l > 0;
    const level = panel && !isNil(panel.supertrendLevel) && panel.close ? (Number(panel.supertrendLevel) - Number(panel.close)) / Number(panel.close) * 100 : null;
    if (ok) return `${bull ? 'Boğa' : 'Ayı'} trendine dönüş bu mumda ✓`;
    return bull ? `Boğa trendi sürüyor${level !== null ? ` (Supertrend alt bandı %${nf(2).format(Math.abs(level))} aşağıda)` : ''}`
      : `Ayı trendi${level !== null ? ` (kırılıma %${nf(2).format(Math.abs(level))})` : ''}`;
  }
  if (a.rightMult) { // e.g. volume > 2.5 × average
    const ratio = r === 0 ? null : l / (r / Number(a.rightMult));
    return `${L} ${ratio === null ? '—' : `${nf(1).format(ratio)}x`} / ${nf(1).format(Number(a.rightMult))}x ${R}${tick}`;
  }
  if (a.leftName === 'BTC fiyatı' && /^BTC SMA/.test(R)) return l > r ? `BTC boğa (fiyat ${R} üstünde)${tick}` : `BTC ayı (fiyat ${R}'ün %${nf(2).format(Math.abs((l - r) / r * 100))} altında)`;
  if (a.op === 'cross_above' || a.op === 'cross_below') {
    const up = a.op === 'cross_above';
    if (ok) return `${L}, ${R}'yi ${up ? 'yukarı' : 'aşağı'} kesti${tick}`;
    const past = up ? l > r : l < r;
    if (past) return `${L} ${up ? '>' : '<'} ${R} (kesişim daha önce oldu; yeni sinyal için önce geri dönmeli)`;
    return `${L} ${up ? '<' : '>'} ${R} · kesişime ${distanceText(a)}`;
  }
  const relation = l > r ? '>' : l < r ? '<' : '=';
  if (isZero(a.right)) return `${L} ${relation} 0 (${num(l, Math.abs(l) >= 1 ? 2 : 6)})${tick}`;
  return ok ? `${L} ${ARROW[a.op] || relation} ${R}${tick}` : `${L} ${relation} ${R} · mesafe ${distanceText(a)}`;
}

/** Status label of a proximity row, in words that say what happens next. */
function proximityView(row) {
  const entry = row.atoms.filter((a) => a.side === 'entry');
  const cross = entry.find((a) => a.op.startsWith('cross_'));
  switch (row.status) {
    case 'entry-signal':
      return row.entryKind === 'cross'
        ? ['Yeni giriş sinyali', 'good', 'zap', 'Bu mumda kesişim oldu → alım bir sonraki 4h mumun açılışında (risk kuralları izin verirse).']
        : ['Giriş koşulları sağlandı', 'good', 'check', 'Koşullar bu mumda sağlanıyor → pozisyon yoksa alım bir sonraki 4h mumun açılışında.'];
    case 'exit-signal': return ['Çıkış sinyali', 'bad', 'down', 'Satış bir sonraki 4h mumun açılışında.'];
    case 'in-position': return ['Pozisyonda', 'info', 'shield', `Efektif stopa ${pctRaw(row.stopDistancePct)}.`];
    case 'unknown': return ['Isınma / BTC verisi eksik', 'muted', 'pause', 'Göstergeler ya da BTC mumu eksik: giriş yapılmaz.'];
    default: break;
  }
  if (cross && !isNil(cross.leftValue) && !isNil(cross.rightValue)) {
    const up = cross.op === 'cross_above';
    const past = up ? Number(cross.leftValue) > Number(cross.rightValue) : Number(cross.leftValue) < Number(cross.rightValue);
    if (past) return ['Trend sürüyor', 'muted', 'up', 'Kesişim daha önce oldu; yeni giriş için önce ters yöne dönmesi gerekir.'];
    return ['Kesişim bekleniyor', row.status === 'partial' ? 'warn' : 'muted', 'target', 'Giriş, kesişimin gerçekleştiği mumda tetiklenir.'];
  }
  return row.status === 'partial' ? ['Kısmen sağlandı', 'warn', 'target', 'Koşulların bir kısmı sağlanıyor.'] : ['Uzak', 'muted', 'dot', 'Giriş koşulları sağlanmıyor.'];
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

function showAtoms(strategy, row, panel, decisions) {
  const box = $('radar-detail');
  const [label, kind, iconName, hint] = proximityView(row);
  const side = row.status === 'in-position' || row.status === 'exit-signal' ? 'exit' : 'entry';
  const last = decisions.find((d) => d.strategy === strategy.name && d.symbol === row.symbol);
  const atomItem = (a) => el('li', { class: a.value === 'True' ? 'ok' : a.value === 'Unknown' ? 'unknown' : 'no' },
    icon(a.value === 'True' ? 'check' : a.value === 'Unknown' ? 'pause' : 'x'),
    el('div', {}, describeAtom(a, panel), el('span', { class: 'raw', text: `${a.left} ${a.op} ${a.right} → ${a.value}` })));
  box.replaceChildren(
    el('div', { class: 'card-head' }, el('h2', { text: `${strategyTitle(strategy.name, strategy.cohortId)} · ${row.symbol}` }), badge(label, kind, iconName)),
    el('p', { class: 'note', text: hint }),
    kv([['Son kapanış', panel ? price(panel.close) : '—'],
      ['Son L2 kararı', last ? el('span', {}, eventBadge(last.kind), ` ${time(last.bar_time)}${last.detail ? ` · ${last.detail}` : ''}`) : 'Son 200 olayda yok'],
      row.stop !== null ? ['Efektif stop', `${price(row.stop)} (${pctRaw(row.stopDistancePct)} aşağıda)`] : null].filter(Boolean)),
    el('h3', { text: side === 'entry' ? 'Giriş koşulları' : 'Çıkış koşulları' }),
    el('ul', { class: 'conditions' }, row.atoms.filter((a) => a.side === side).map(atomItem)),
    el('h3', { text: side === 'entry' ? 'Çıkış koşulları (pozisyon açılınca)' : 'Giriş koşulları' }),
    el('ul', { class: 'conditions' }, row.atoms.filter((a) => a.side !== side).map(atomItem)));
  box.hidden = false;
  box.scrollIntoView({ behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth', block: 'nearest' });
}

const INDICATOR_GROUPS = [
  ['Fiyat', [['close', 'Kapanış', 'price']]],
  ['Trend', [['ema50', 'EMA50', 'price'], ['ema100', 'EMA100', 'price'], ['sma150', 'SMA150', 'price'], ['supertrendDirection', 'Supertrend', 'dir']]],
  ['Momentum', [['rsi14', 'RSI14', 'rsi'], ['macdHistogram', 'MACD hist.', 'signed'], ['roc42', 'ROC42 %', 'signed'], ['adx14', 'ADX14', 'n2']]],
  ['Volatilite', [['atrPct', 'ATR %', 'pctRaw'], ['bollingerPercentB', 'BB %B', 'n2'], ['bollingerBandwidth', 'BB genişlik', 'n2']]],
  ['Kırılım ve hacim', [['donchianUpperDistancePct', 'Donchian50 tavana %', 'pctRaw'], ['volumeRatio', 'Hacim / ort.20', 'ratio']]],
];

function indicatorCell(kindOf, v) {
  switch (kindOf) {
    case 'price': return el('td', { class: 'num', text: price(v) });
    case 'dir': return el('td', {}, isNil(v) ? '—' : badge(v > 0 ? 'Boğa' : 'Ayı', v > 0 ? 'good' : 'bad', v > 0 ? 'up' : 'down'));
    case 'rsi': return el('td', { class: `num ${v >= 70 ? 'warn-text' : v <= 30 ? 'neg' : ''}`.trim(), text: num(v, 1) });
    case 'signed': return el('td', { class: `num ${signClass(v)}`.trim(), text: isNil(v) ? '—' : sign(v) + num(Math.abs(v), Math.abs(v) >= 1 ? 2 : 6) });
    case 'pctRaw': return el('td', { class: 'num', text: pctRaw(v) });
    case 'ratio': return el('td', { class: `num ${v >= 2.5 ? 'pos' : ''}`.trim(), text: isNil(v) ? '—' : `${nf(2).format(v)}x` });
    default: return el('td', { class: 'num', text: num(v, 2) });
  }
}

async function renderRadar(view) {
  const [data, decisions] = await Promise.all([api('/radar'), api('/decisions?limit=200')]);
  const regime = data.regime;
  const panels = Object.fromEntries(data.symbols.map((s) => [s.symbol, s]));
  const filter = el('input', { type: 'search', placeholder: 'Parite filtrele…', 'aria-label': 'Parite filtrele', oninput: () => draw() });
  const scope = el('select', { 'aria-label': 'Parite kümesi', onchange: () => draw() },
    el('option', { value: 'all', text: 'Tüm pariteler' }), el('option', { value: 'core', text: 'İşlem gören (çekirdek)' }), el('option', { value: 'watch', text: 'Yalnızca izleme (E1)' }));
  const indicatorBox = el('div');
  const matrixBox = el('div');
  const bull = regime.state === 'Bull';

  function draw() {
    const text = filter.value.trim().toUpperCase();
    const visible = (sym) => (!text || sym.includes(text)) && (scope.value === 'all' || (panels[sym] || {}).category === scope.value);

    // Proximity matrix
    const symbols = [...new Set(data.strategies.flatMap((st) => st.symbols.map((r) => r.symbol)))].sort().filter(visible);
    const head = el('tr', {}, el('th', { scope: 'col', text: 'Parite' }), el('th', { scope: 'col', class: 'num', text: 'Son kapanış' }),
      data.strategies.map((st) => el('th', { scope: 'col' }, strategyCell(st.name, st.cohortId))));
    const rows = symbols.map((sym) => el('tr', {},
      el('td', {}, el('strong', { text: sym }), (panels[sym] || {}).category === 'watch' ? [' ', badge('izleme', 'muted')] : null),
      el('td', { class: 'num', text: panels[sym] ? price(panels[sym].indicators.close) : '—' }),
      data.strategies.map((st) => {
        const row = st.symbols.find((r) => r.symbol === sym);
        if (!row) return el('td', { class: 'muted', text: '—' });
        const [label, kind, iconName] = proximityView(row);
        const panel = panels[sym] ? panels[sym].indicators : null;
        const side = row.status === 'in-position' || row.status === 'exit-signal' ? 'exit' : 'entry';
        const lines = row.status === 'in-position' ? [`Stopa ${pctRaw(row.stopDistancePct)}`]
          : row.atoms.filter((a) => a.side === side).map((a) => describeAtom(a, panel));
        const open = () => showAtoms(st, row, panel, decisions);
        return el('td', { class: 'clickable', tabindex: '0', role: 'button', 'aria-label': `${strategyTitle(st.name, st.cohortId)} ${sym}: ${label}`,
          onclick: open, onkeydown: (event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); open(); } } },
        el('div', { class: 'cell' },
          el('div', {}, badge(label, kind, iconName), ' ', micro(row.satisfied, row.total, row.status === 'partial')),
          lines.map((line) => el('span', { class: 'line', text: line }))));
      })));
    matrixBox.replaceChildren(el('table', {}, el('thead', {}, head), el('tbody', {}, rows.length ? rows : [el('tr', {}, el('td', { colspan: 2 + data.strategies.length, class: 'empty', text: 'Eşleşen parite yok.' }))])));

    // Indicator table with grouped headers
    const columns = INDICATOR_GROUPS.flatMap(([, cols]) => cols);
    const groupRow = el('tr', {}, el('th', { class: 'group', text: '' }), INDICATOR_GROUPS.map(([name, cols]) => el('th', { class: 'group', colspan: cols.length, scope: 'colgroup', text: name })));
    const sortHead = (key, label, numeric, first) => el('th', {
      class: `sortable ${numeric ? 'num' : ''} ${first ? 'sep' : ''}`.trim(), scope: 'col', tabindex: '0',
      'aria-sort': state.radarSort.key === key ? (state.radarSort.dir > 0 ? 'ascending' : 'descending') : null,
      onclick: () => { state.radarSort = { key, dir: state.radarSort.key === key ? -state.radarSort.dir : 1 }; draw(); },
      onkeydown: (event) => { if (event.key === 'Enter') { state.radarSort = { key, dir: state.radarSort.key === key ? -state.radarSort.dir : 1 }; draw(); } },
    }, label + (state.radarSort.key === key ? (state.radarSort.dir > 0 ? ' ▲' : ' ▼') : ''));
    const firsts = new Set(INDICATOR_GROUPS.map(([, cols]) => cols[0][0]));
    const labelRow = el('tr', {}, sortHead('symbol', 'Parite', false, false), columns.map(([key, label]) => sortHead(key, label, key !== 'supertrendDirection', firsts.has(key))));
    const body = sortRadar(data.symbols.filter((s) => visible(s.symbol))).map((s) => el('tr', {},
      el('td', {}, el('strong', { text: s.symbol }), s.category === 'watch' ? [' ', badge('izleme', 'muted')] : null),
      columns.map(([key, , kindOf]) => { const td = indicatorCell(kindOf, s.indicators[key]); if (firsts.has(key)) td.classList.add('sep'); return td; })));
    indicatorBox.replaceChildren(el('table', {}, el('thead', {}, groupRow, labelRow), el('tbody', {}, body)));
  }

  show(view, 
    callout('Bu ekran, aktif her stratejinin giriş ve çıkış koşullarını son kapanan 4h mumla, stratejinin kendi interpreter\'ıyla değerlendirir. Radar karar vermez: '
      + 'gerçek kararlar her saat HH:00:15 UTC\'deki döngüde verilir ve alım/satım bir sonraki 4h mumun açılış fiyatından gerçekleşir. Bu yüzden "Yeni giriş sinyali" görünen bir paritede pozisyon, sonraki mum işlenene kadar görünmez.'),
    el('div', { class: 'card' }, el('div', { class: 'card-head' }, el('h2', { text: 'BTCUSDT piyasa rejimi' }), el('span', { class: 'muted small', text: `Son mum ${time(data.asOf)} · ${data.barsUsed} mum` })),
      el('div', { class: 'regime' },
        el('span', { class: `state ${bull ? 'pos' : regime.state === 'Bear' ? 'neg' : 'muted'}` }, icon(bull ? 'up' : regime.state === 'Bear' ? 'down' : 'pause'),
          bull ? 'Boğa' : regime.state === 'Bear' ? 'Ayı' : 'Bilinmiyor'),
        el('div', { class: 'stat' }, el('span', { class: 'k', text: 'Kapanış' }), el('span', { class: 'v', text: price(regime.close) })),
        el('div', { class: 'stat' }, el('span', { class: 'k', text: 'SMA300' }), el('span', { class: 'v', text: `${price(regime.sma300)} (${signedPctRaw(regime.sma300DistancePct)})` })),
        el('div', { class: 'stat' }, el('span', { class: 'k', text: 'EMA100' }), el('span', { class: 'v', text: `${price(regime.ema100)} (${signedPctRaw(regime.ema100DistancePct)})` }))),
      el('p', { class: 'note', text: 'Rejim: BTC kapanışı SMA300\'ün üstündeyse Boğa. H08 yalnızca Boğa rejiminde giriş yapar.' })),
    el('div', { class: 'card scroll' }, el('div', { class: 'card-head' }, el('h2', { text: 'Sinyal yakınlığı (aktif stratejiler)' }), el('div', { class: 'toolbar' }, filter, scope)), matrixBox,
      el('div', { class: 'legend' },
        el('span', {}, badge('Yeni giriş sinyali', 'good', 'zap'), 'bu mumda kesişim; alım sonraki açılışta'),
        el('span', {}, badge('Giriş koşulları sağlandı', 'good', 'check'), 'seviye koşulları sağlanıyor'),
        el('span', {}, badge('Kesişim bekleniyor', 'warn', 'target'), 'kesişime yaklaşıyor'),
        el('span', {}, badge('Trend sürüyor', 'muted', 'up'), 'kesişim önceden oldu'),
        el('span', {}, badge('Pozisyonda', 'info', 'shield'), 'çıkış koşulu ve stop izleniyor')),
      el('p', { class: 'note', text: 'Bir hücreye tıklayınca tüm koşullar, ham DSL ifadeleri ve o parite için son risk kararı (L2) görünür.' })),
    el('div', { id: 'radar-detail', class: 'card', hidden: true, 'aria-live': 'polite' }),
    el('div', { class: 'card scroll' }, el('div', { class: 'card-head' }, el('h2', { text: '11 gösterge · son kapanan 4h mum' })), indicatorBox,
      el('p', { class: 'note', text: 'EMA, ATR, RSI ve ADX son 320 mumdan hesaplanır; daha önce başlamış bir run\'ın değerinden çok az farklı olabilir. RSI ≥ 70 sarı, ≤ 30 kırmızı; hacim oranı ≥ 2,5x yeşil.' })));
  draw();
}

// ---------- trace ----------
function openTrace(fillId) {
  state.traceFill = fillId;
  selectTab('trace');
}

async function renderTrace(view) {
  const input = el('input', { type: 'number', min: '1', placeholder: 'Dolum #', 'aria-label': 'Dolum numarası', value: state.traceFill || '' });
  const go = el('button', { class: 'btn primary', type: 'button', text: 'Zinciri göster', onclick: () => { state.traceFill = Number(input.value); refresh(); } });
  const header = el('div', {},
    callout('Bu ekran bir dolumun nereden geldiğini adım adım gösterir: dolum → risk kararı (L2) → sinyal (L1) → strateji tanımı ve hash\'i → kohort manifestosu → araştırma deneyleri → veri seti. '
      + 'Paper modunda ayrı OrderIntent tablosu yoktur: L2 risk kararı olayı intent ile kararı birlikte taşır.'),
    el('div', { class: 'card' }, el('div', { class: 'toolbar' }, input, go)));
  if (!state.traceFill) { show(view, header); return; }
  const p = await api(`/provenance/${encodeURIComponent(state.traceFill)}`);
  const f = p.fill; const d = p.decision; const s = p.signal; const c = p.cohort || {};
  show(view, header, el('div', { class: 'card' }, el('ol', { class: 'chain' },
    el('li', {}, el('div', { class: 'step' }, `Dolum #${f.id} · `, badge(f.side === 'buy' ? 'Alış' : 'Satış', f.side === 'buy' ? 'good' : 'bad'), ` ${f.symbol}`),
      kv([['Strateji', strategyTitle(f.strategy, f.cohort_id)], ['Zaman', time(f.fill_time)], ['Fiyat × miktar', `${price(f.price)} × ${num(f.quantity, 6)}`], ['Ücret', num(f.fee, 4)], ['Neden', f.reason]])),
    el('li', {}, el('div', { class: 'step', text: 'Risk kararı (L2) · OrderIntent karşılığı' }),
      d ? kv([['Karar', eventBadge(d.kind)], ['Gerekçe', d.detail], ['Nominal (önce → sonra)', `${num(d.before, 2)} → ${num(d.after, 2)}`], ['Mum', time(d.bar_time)]])
        : el('p', { class: 'note', text: f.reason === 'Stop' ? 'Koruyucu stop dolumu: risk kararı gerekmez.' : 'Bu dolumun penceresinde L2 olayı yok.' })),
    el('li', {}, el('div', { class: 'step', text: 'Sinyal (L1)' }),
      s ? kv([['Olay', eventBadge(s.kind)], ['Mum', time(s.bar_time)], ['Değer', num(s.value, 6)]]) : el('p', { class: 'note', text: 'Sinyal yok (stop, manuel kapatma ya da tümünü kapat).' })),
    el('li', {}, el('div', { class: 'step', text: `Sleeve · ${strategyTitle(p.sleeve.name, p.sleeve.cohortId)}` }),
      kv([['Run / sleeve', `${p.sleeve.runId} / ${p.sleeve.sleeve}`], ['Kohort / üye', `${cohortLabel(p.sleeve.cohortId)} (#${p.sleeve.cohortId}) / ${p.sleeve.memberIndex}`], ['Sermaye', money(p.sleeve.capital)]])),
    el('li', {}, el('div', { class: 'step', text: 'Strateji tanımı (StrategyDefinition)' }),
      kv([['definitionHash', p.strategy.definitionHash], ['structureHash', p.strategy.structureHash],
        ['Hash doğrulandı', p.strategy.verified ? badge('evet', 'good', 'check') : badge('HAYIR', 'bad', 'x')], ['Dondurulma', time(p.strategy.frozenAt)],
        ['Yaşam döngüsü', p.strategy.lifecycle.map((l) => `${l.from_state}→${l.to_state}`).join(', ')]]),
      el('pre', { text: JSON.stringify(p.strategy.definition, null, 2) })),
    el('li', {}, el('div', { class: 'step', text: `Kohort manifestosu · ${c.cohort ?? ''}` }),
      kv([['manifestHash', c.manifestHash], ['Protokol', `${c.protocol ?? '—'} (${c.protocolCommit ?? '—'})`], ['Engine', c.engineSha], ['Image', c.imageDigest],
        ['Gücü yetersiz (underpowered)', String(c.underpowered ?? '—')], ['Muafiyetler', (c.exemptions || []).join(' · ') || '—']])),
    el('li', {}, el('div', { class: 'step', text: 'Deney sonuçları (araştırma penceresi)' }),
      table([{ label: '#', key: 'experiment_id', num: true }, { label: 'Varyant', key: 'variant' }, { label: 'Protokol', key: 'protocol_version' },
        { label: 'Engine', render: (e) => el('span', { class: 'mono', text: short(e.engine_git_sha) }) }, { label: 'Sonuç hash', render: (e) => el('span', { class: 'mono', text: short(e.result_hash, 16) }) }], p.experiments)),
    el('li', {}, el('div', { class: 'step', text: 'Veri seti (DatasetSnapshot)' }), kv(p.datasetSnapshots.map((h) => ['snapshot', h]))))));
}

// ---------- system ----------
async function renderSystem(view) {
  const sys = await api('/system');
  const [costs, traces] = await Promise.all([api('/ai/costs').catch((e) => e), api('/ai/traces?limit=50').catch((e) => e)]);
  const ks = sys.tradingKillSwitch; const ai = sys.aiKillSwitch;
  const unavailable = sys.unavailable || [];
  const aiMissing = unavailable.includes('aiKillSwitch');
  const failed = (x) => x instanceof Error;
  show(view, 
    callout('Bu ekran sistemin sağlığını gösterir: kill switch\'ler, saatlik döngülerin süresi ve gecikmesi, reconciliation (defter ↔ bakiye tutarlılığı), audit olayları ve AI maliyeti. '
      + 'Trading kill switch açıkken yeni giriş yapılmaz; çıkışlar ve stop\'lar çalışmaya devam eder.'),
    unavailable.length ? callout(`Okunamayan bölümler: ${unavailable.join(', ')}. ${sys.unavailableReason || ''}`, 'warn') : null,
    el('div', { class: 'kpis' },
      kpi('Trading kill switch', ks && ks.engaged ? 'AKTİF' : 'Kapalı', ks && ks.engaged ? 'neg' : 'pos', ks ? `${ks.actor} · ${time(ks.recorded_at)}` : ''),
      kpi('LLM acil durdurma', aiMissing ? '—' : ai && ai.tripped ? `AKTİF (${ai.trigger_kind})` : 'Kapalı', aiMissing ? '' : ai && ai.tripped ? 'neg' : 'pos', aiMissing ? 'migration 0008 gerekli' : ''),
      kpi('Son döngü', sys.host.outcome || '—', sys.host.outcome === 'ok' ? 'pos' : sys.host.outcome ? 'neg' : '', sys.host.lastCycle ? time(sys.host.lastCycle) : ''),
      kpi('Geç döngü (7 gün, > 60 sn)', String(sys.lateCycles7d ?? '—'), Number(sys.lateCycles7d) > 0 ? 'neg' : 'pos'),
      kpi('Reconciliation', sys.reconciliation ? (sys.reconciliation.ok ? 'Temiz' : 'UYUMSUZ') : '—', sys.reconciliation ? (sys.reconciliation.ok ? 'pos' : 'neg') : '',
        sys.reconciliation ? time(sys.reconciliation.checked_at) : ''),
      kpi('Reconciliation hatası (7 gün)', String(sys.reconciliationFailures7d ?? '—'), Number(sys.reconciliationFailures7d) > 0 ? 'neg' : '')),
    el('div', { class: 'grid2' },
      card('Holdout ve kohort durumu',
        kv([['HoldoutEnvironmentVerified', 'ops tarafından okunamaz (holdout veritabanı; H-ENV raporuna bakın)']]),
        table([{ label: 'Kohort', render: (r) => `${cohortLabel(r.cohort_id)} (#${r.cohort_id})` },
          { label: 'Durum', render: (r) => badge(r.status === 'revealed' ? 'Açıklandı' : r.status === 'sealed' ? 'Mühürlü' : r.status, r.status === 'revealed' ? 'info' : 'muted') },
          { label: 'Kayıt', render: (r) => time(r.recorded_at) }], sys.cohortStatus),
        el('p', { class: 'note', text: 'Doğrulama durumu yalnızca evaluator sonuçlarından gelir; dashboard holdout verisi göstermez.' })),
      scrollCard('Son audit olayları', unavailable.includes('audit') ? el('p', { class: 'note', text: 'Audit okunamıyor: migration 0008 uygulanmamış.' })
        : table([{ label: '#', key: 'id', num: true }, { label: 'Zaman', render: (a) => time(a.recorded_at) }, { label: 'Aktör', key: 'actor' }, { label: 'Olay', render: (a) => el('span', { class: 'mono', text: a.event }) }], sys.audit))),
    scrollCard('Saatlik döngüler (son 24)', table([
      { label: 'Planlanan', render: (c) => time(c.scheduled_for) }, { label: 'Süre (ms)', num: true, render: (c) => num(c.duration_ms, 0) },
      { label: 'Gecikme (sn)', num: true, render: (c) => num(c.lag_seconds, 1) }, { label: 'İşlenen mum', num: true, key: 'bars_processed' },
      { label: 'Sonuç', render: (c) => badge(c.outcome, c.outcome === 'ok' ? 'good' : c.outcome === 'degraded' ? 'warn' : 'bad') },
      { label: 'Degraded nedeni', render: (c) => (c.degraded || []).join(', ') },
    ], sys.cycles)),
    scrollCard('AI maliyet defteri (ay × görev sınıfı)', failed(costs) ? el('p', { class: 'note', text: `Okunamadı: ${costs.message}` }) : table([
      { label: 'Ay', key: 'month' }, { label: 'Görev sınıfı', key: 'task_class' }, { label: 'İş türü', key: 'job_kind' },
      { label: 'Kesinleşen USD', num: true, render: (r) => num(r.settled_usd, 4) }, { label: 'Rezervasyon USD', num: true, render: (r) => num(r.reserved_usd, 4) },
      { label: 'Çağrı', num: true, key: 'calls' }, { label: 'Girdi token', num: true, key: 'input_tokens' }, { label: 'Çıktı token', num: true, key: 'output_tokens' },
    ], costs)),
    scrollCard('DecisionTrace (son 50)', failed(traces) ? el('p', { class: 'note', text: `Okunamadı: ${traces.message}` }) : table([
      { label: 'Zaman', render: (t) => time(t.at) }, { label: 'Görev', key: 'task_class' }, { label: 'Mod', key: 'mode' }, { label: 'Model', key: 'actual_model' },
      { label: 'Sonuç', render: (t) => badge(t.outcome, t.outcome === 'Completed' ? 'good' : 'warn') },
      { label: 'Degraded', render: (t) => (t.degraded ? badge('degraded', 'bad', 'alert') : '') },
      { label: 'Validator', render: (t) => el('span', { class: 'mono', text: JSON.stringify(t.validator_results).slice(0, 80) }) },
      { label: 'USD', num: true, render: (t) => num(t.actual_usd, 6) }, { label: 'ms', num: true, key: 'latency_ms' },
    ], traces)));
}

// ---------- shell ----------
const renderers = { overview: renderOverview, sleeves: renderSleeves, radar: renderRadar, testnet: renderTestnet, trace: renderTrace, system: renderSystem };

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
    show(view, el('div', { class: 'callout bad', role: 'alert' }, icon('alert'), el('div', {}, el('strong', { text: 'Veri alınamadı. ' }), e.message)));
  }
}

function selectTab(tab) {
  state.tab = tab;
  document.querySelectorAll('#tabs button').forEach((b) => {
    b.classList.toggle('active', b.dataset.tab === tab);
    if (b.dataset.tab === tab) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current');
  });
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
  applyTheme(storedTheme() === 'light' ? 'light' : 'dark');
  $('theme').addEventListener('click', toggleTheme);
  $('refresh').replaceChildren(icon('refresh'));
  tickClocks();
  setInterval(tickClocks, 1000);
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
