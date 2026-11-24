// ─────────────────────────────────────────────────────────────
//  구슬 꿰기
//
//  흐름: 설정(시간 고르기, 손으로도 고름) → 3·2·1 → 구슬이 사방에서 날아옴(정한 시간 동안) → 결과(등수)
//  - 캐릭터 타입: 그림자뷰 (GAME_RULEBOOK.md) — 사람은 번진 그림자, 배경은 이 게임이 그리는 밤하늘
//  - 꿰기: 손을 빠르게(RULE.swipeSpeed m/s 이상) 휘두르면 지난 위치 → 지금 위치를 잇는 "칼날 선"이 생김.
//          그 선이 구슬 가운데 구멍(반지름의 RULE.centerHit 안)을 지나면 그 사람의 실에 꿰어짐
//  - 폭탄: 손이 닿기만 해도(빠르기 상관없이) 꿴 구슬 RULE.bombLoss 개를 잃음
//  - 구슬: 한 번에 1~5개, 아래·왼쪽·오른쪽에서 포물선으로. 가끔 폭탄이 섞여 나옴
//  - 실: 사람마다 한 줄, 화면 양옆 (P1 왼쪽, P2 오른쪽, P3 왼쪽 안, P4 오른쪽 안). 꿴 구슬이 날아가 위부터 쌓임
//  - 등수: 꿴 구슬 수. 같으면 공동 등수
//  - 참가: 최대 인원(공통 설정)까지, 게임 중에 들어와도 자리가 남으면 참여
//
//  카메라 없이 시험: ?sim=2&autoplay=1 (가상 플레이어가 오른손을 계속 휘두름)
// ─────────────────────────────────────────────────────────────
import { FemtoClient, J } from '../_shared/femto-client.js';
import { Stage } from '../_shared/video-layer.js';
import { Player, pickBodies, trackPlayers } from '../_shared/players.js';
import { HandPointer } from '../_shared/hand-pointer.js';
import { Sound } from '../_shared/sound.js';
import { GameShell, settings } from '../_shared/game-shell.js';

const $ = id => document.getElementById(id);
const params = new URLSearchParams(location.search);
const SIM = Math.max(0, Math.min(4, parseInt(params.get('sim') || '0', 10) || 0));
const AUTOPLAY = SIM && params.get('autoplay') === '1';
const MIRROR = true;

// ───────── 규칙 숫자 (어르신 기준으로 넉넉하게) ─────────
const RULE = {
  swipeSpeed: 0.8,        // 손이 이 빠르기(m/s) 이상일 때만 꿰기 (가만히 댄 손은 안 꿰짐)
  centerHit: 0.65,        // 손이 지나간 선이 구슬 중심에서 반지름의 이만큼 안을 지나면 꿰어짐
  bombTouch: 0.85,        // 폭탄: 손(또는 지나간 선)이 반지름의 이만큼 안에 닿으면
  bombLoss: 3,            // 폭탄에 닿으면 잃는 구슬 수
  perWave: [1, 5],        // 한 번에 날아오는 구슬 수
  waveGap: [1.6, 2.6],    // 다음 묶음까지 (초)
  bombChance: 0.25,       // 묶음마다 폭탄이 섞일 확률
  bombAfter: 6,           // 시작하고 이 초가 지나야 폭탄이 나옴
  flight: [2.8, 3.6],     // 구슬이 화면에 머무는 시간 (초) — 느긋하게
  beadSize: 0.065,        // 구슬 반지름 = 무대 높이 × 이만큼
};
const BEAD_COLORS = ['#ff6b6b', '#ffa94d', '#ffd43b', '#69db7c', '#38d9a9', '#4dabf7', '#9775fa', '#f783ac'];
const HANDS = [J.HandRight, J.HandLeft];

// ───────── 준비 ─────────
const client = new FemtoClient({ sim: SIM });   // 그림자뷰: 영상은 안 보이지만 사람 모양(마스크)이 영상과 함께 옴
const stage = new Stage($('stage'), client, { character: 'shadow' });
const sound = new Sound();
const pointer = new HandPointer({ client, mirror: MIRROR, onSelect: () => sound.tick() });
const shell = new GameShell({ name: '구슬 꿰기', icon: '📿', sim: SIM, onHome: () => { sound.stop(); setPhase('setup'); } });

const opts = { time: 60 };
try { Object.assign(opts, JSON.parse(localStorage.getItem('femto.bead.opts') || '{}')); } catch {}
(function bindTime() {
  const box = $('optTime');
  const sync = () => box.querySelectorAll('button').forEach(b => b.classList.toggle('on', +b.dataset.v === opts.time));
  box.addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    opts.time = +b.dataset.v; sync();
    try { localStorage.setItem('femto.bead.opts', JSON.stringify(opts)); } catch {}
  });
  sync();
})();

// ───────── 상태 ─────────
let phase = 'setup';            // setup | count | play | end | results
let phaseAt = 0;
let players = [];               // Player + { beads: [색…], lost, bombs, hands: {prev…}, combo }
let game = null;
const items = [];               // 날아다니는 구슬·폭탄 { kind, x, y, vx, vy, r, color, spin, a }
const flyers = [];              // 꿰어져 실로 날아가는 구슬
const drops = [];               // 폭탄 때문에 실에서 떨어지는 구슬
const particles = [], rings = [], pops = [];
const trails = new Map();       // `${index}:${hand}` → [{x, y, t}] 손이 지나간 자국
let shake = 0;

function show(id, on) { $(id).classList.toggle('hidden', !on); }
function setPhase(p) {
  phase = p;
  phaseAt = performance.now() / 1000;
  show('setup', p === 'setup');
  show('results', p === 'results');
  show('hud', p === 'count' || p === 'play' || p === 'end');
  show('count', p === 'count');
  pointer.enabled = p === 'setup' || p === 'results';
  if (!pointer.enabled) pointer.hide();
  if (p === 'setup') { items.length = flyers.length = drops.length = particles.length = rings.length = pops.length = 0; trails.clear(); }
}

$('start').onclick = startGame;
$('again').onclick = startGame;
addEventListener('keydown', e => { if (e.code === 'Escape' && phase !== 'setup') { sound.stop(); setPhase('setup'); } });

function startGame() {
  sound.unlock();
  players = [];
  joinPlayers();
  items.length = flyers.length = drops.length = particles.length = rings.length = pops.length = 0;
  trails.clear();
  game = { t0: 0, end: 0, nextWave: 0, waves: 0, said10: false, countShown: 0 };
  setPhase('count');
  setMsg('구슬 가운데를 손으로 휙! 💣 폭탄은 피하세요');
  sound.say('날아오는 구슬의 가운데를, 손으로 휙 그어 보세요. 폭탄은 피하세요!', 0.92);
}

function setMsg(t) { if ($('msg').textContent !== t) $('msg').textContent = t; }

// ───────── 참가자 (최대 인원까지, 중간 참여) ─────────
function joinPlayers() {
  trackPlayers(players, client);
  const free = settings.players - players.length;
  if (free <= 0) return;
  const taken = new Set(players.map(p => p.bodyId));
  const fresh = pickBodies({ bodies: new Map([...client.bodies].filter(([id]) => !taken.has(id))) }, free, MIRROR);
  for (const b of fresh) {
    const p = new Player(players.length, b, client);
    Object.assign(p, { beads: [], bombs: 0, lost: 0, hands: {}, combo: { n: 0, t: 0 }, flash: 0, lastAt: Infinity });
    players.push(p);
  }
}

// ───────── 무대 크기 ─────────
const fxCanvas = $('fx'), fg = fxCanvas.getContext('2d');
let V = null;   // 영상 자리 (CSS px): 그림자와 손이 있는 곳 = 구슬이 날아다니는 곳
let L = null;   // { W, H, dpr }
function layout() {
  const dpr = Math.min(devicePixelRatio || 1, 2);
  const r = fxCanvas.getBoundingClientRect();
  if (fxCanvas.width !== Math.round(r.width * dpr) || fxCanvas.height !== Math.round(r.height * dpr)) {
    fxCanvas.width = Math.round(r.width * dpr); fxCanvas.height = Math.round(r.height * dpr);
  }
  L = { W: r.width, H: r.height, dpr };
  const m = stage.map;
  V = m ? { x: m.dx / m.dpr, y: m.dy / m.dpr, w: m.dw / m.dpr, h: m.dh / m.dpr } : { x: 0, y: 0, w: r.width, h: r.height };
}
const beadR = () => Math.max(18, V.h * RULE.beadSize);

// ───────── 실 (사람마다 한 줄, 화면 양옆) ─────────
// P1 왼쪽 바깥, P2 오른쪽 바깥, P3 왼쪽 안, P4 오른쪽 안 → 2명이면 양쪽에 하나씩
function lane(p) {
  const side = p.index % 2 === 0 ? -1 : 1, depth = p.index >> 1;
  const sideW = Math.max(V.x, 0);
  const w = Math.max(56, Math.min(100, sideW / 2 || 70));
  const x = side < 0 ? 8 + w * (depth + 0.5) : L.W - 8 - w * (depth + 0.5);
  const top = 64, bottom = L.H - 16;
  return { x, w, top, bottom, side };
}
/** k번째 구슬이 실에 꿰일 자리 */
function strandSpot(p, k) {
  const ln = lane(p), rs = Math.min(ln.w * 0.3, 15);
  const len = ln.bottom - (ln.top + 34), n = Math.max(k + 1, p.beads.length + 1);
  const gap = Math.min(rs * 2.05, len / n);
  return { x: ln.x, y: ln.top + 34 + rs + k * gap, r: rs };
}

// ───────── 구슬 던지기 (사방에서 포물선) ─────────
function spawnWave(now) {
  const [a, b] = RULE.perWave;
  const n = a + Math.floor(Math.random() * (b - a + 1));
  const bomb = now - game.t0 > RULE.bombAfter && Math.random() < RULE.bombChance;
  const from = ['bottom', 'bottom', 'bottom', 'left', 'right'][Math.floor(Math.random() * 5)];   // 한 묶음은 같은 쪽에서
  const bombAt = bomb ? Math.floor(Math.random() * (n + 1)) : -1;   // 폭탄은 묶음 안 무작위 순서에 하나
  for (let i = 0; i < n + (bomb ? 1 : 0); i++) {
    const kind = i === bombAt ? 'bomb' : 'bead';
    setTimeout(() => { if (phase === 'play') launch(kind, from); }, i * 160);   // 조금씩 시간차를 두고
  }
  game.waves++;
}

function launch(kind, from) {
  const r = beadR();
  const T = RULE.flight[0] + Math.random() * (RULE.flight[1] - RULE.flight[0]);
  const apexY = V.y + V.h * (0.12 + Math.random() * 0.35);   // 가장 높이 올라가는 곳
  let x0, y0, x1;
  if (from === 'bottom') {
    x0 = V.x + V.w * (0.1 + Math.random() * 0.8);
    y0 = V.y + V.h + r;
    x1 = V.x + V.w * (0.1 + Math.random() * 0.8);
  } else {
    const left = from === 'left';
    x0 = left ? V.x - r : V.x + V.w + r;
    y0 = V.y + V.h * (0.55 + Math.random() * 0.35);
    x1 = left ? V.x + V.w * (0.55 + Math.random() * 0.4) : V.x + V.w * (0.05 + Math.random() * 0.4);
  }
  // 포물선: T 초 동안 y0 → apex → (y0 아래로) 떨어짐. 중간(T/2)에 가장 높이
  const rise = Math.max(r * 2, y0 - apexY);
  const g = 8 * rise / (T * T);
  const vy = -g * T / 2;
  const vx = (x1 - x0) / T;
  items.push({ kind, x: x0, y: y0, vx, vy, g, r: kind === 'bomb' ? r * 1.05 : r, color: BEAD_COLORS[Math.floor(Math.random() * BEAD_COLORS.length)],
               spin: (Math.random() - 0.5) * 3, a: Math.random() * 6, born: performance.now() / 1000 });
  if (kind === 'bomb') sfx.fuse(); else sfx.launch();
}

// ───────── 손 = 칼날: 빠르게 지나간 선으로 꿰기 ─────────
function handleHands(now) {
  for (const p of players) {
    const body = p.body;
    for (const hand of HANDS) {
      const key = hand;
      const prev = p.hands[key];
      const scr = body && body.conf(hand) > 0 ? stage.toScreen(body, hand) : null;
      if (!scr) { p.hands[key] = null; continue; }
      const p3 = body.pos(hand);
      const cur = { x: scr.x, y: scr.y, p3, t: now };
      p.hands[key] = cur;
      if (!prev || now - prev.t > 0.2) continue;
      const dt = Math.max(1e-3, now - prev.t);
      const speed = Math.hypot(p3.x - prev.p3.x, p3.y - prev.p3.y, p3.z - prev.p3.z) / dt;   // m/s
      const fast = speed >= RULE.swipeSpeed;
      if (fast) addTrail(p, hand, cur, now);

      for (let i = items.length - 1; i >= 0; i--) {
        const it = items[i];
        const d = distToSeg(it.x, it.y, prev.x, prev.y, cur.x, cur.y);
        if (it.kind === 'bomb') {
          if (d <= it.r * RULE.bombTouch) { items.splice(i, 1); bombHit(p, it, now); }
        } else if (fast && d <= it.r * RULE.centerHit) {
          items.splice(i, 1); thread(p, it, now);
        }
      }
    }
  }
}

function addTrail(p, hand, pt, now) {
  const k = `${p.index}:${hand}`;
  let tr = trails.get(k);
  if (!tr) trails.set(k, tr = []);
  tr.push({ x: pt.x, y: pt.y, t: now });
  if (tr.length > 14) tr.shift();
}

/** 꿰기: 효과 + 구슬이 그 사람 실로 날아감 */
function thread(p, it, now) {
  // 한 번에 여러 개 (0.7초 안에 연달아)
  p.combo = now - p.combo.t < 0.7 ? { n: p.combo.n + 1, t: now } : { n: 1, t: now };
  const k = p.beads.length + flyers.filter(f => f.p === p).length;
  flyers.push({ p, color: it.color, x0: it.x, y0: it.y, r0: it.r, t0: now, dur: 0.55, k });
  // 이펙트: 구슬 색 반짝이 + 사람 색 고리 + "+1"
  for (let i = 0; i < 26; i++) {
    const a = Math.random() * Math.PI * 2, sp = 120 + Math.random() * 420;
    particles.push({ x: it.x, y: it.y, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - 60, r: 2 + Math.random() * 4, c: i % 3 ? it.color : '#ffffff', life: 0, max: 0.45 + Math.random() * 0.4, star: i % 4 === 0 });
  }
  rings.push({ x: it.x, y: it.y, color: p.color, t0: now, r: it.r });
  rings.push({ x: it.x, y: it.y, color: '#ffffff', t0: now + 0.06, r: it.r * 0.6 });
  const combo = p.combo.n >= 2 ? `  ${p.combo.n}개 한 번에!` : '';
  pops.push({ x: it.x, y: it.y - it.r, text: `+1${combo}`, color: p.color, t0: now, big: p.combo.n >= 2 ? 1.3 : 1 });
  sfx.thread(k);
  if (p.combo.n === 3) sound.say('세 개 한 번에!', 1.05);
}

/** 폭탄: 펑! 꿴 구슬 RULE.bombLoss 개가 실에서 떨어짐 */
function bombHit(p, it, now) {
  p.bombs++;
  const lose = Math.min(RULE.bombLoss, p.beads.length);
  p.lost += lose;
  for (let i = 0; i < lose; i++) {
    const k = p.beads.length - 1;
    const s = strandSpot(p, k);
    drops.push({ x: s.x, y: s.y, r: s.r, color: p.beads.pop(), vx: (Math.random() - 0.5) * 260 + (lane(p).side < 0 ? 120 : -120), vy: -200 - Math.random() * 200, life: 0 });
  }
  p.flash = now;
  const fire = ['#fff3b0', '#ffd23f', '#ff922b', '#ff5d5d', '#555'];
  for (let i = 0; i < 70; i++) {
    const a = Math.random() * Math.PI * 2, sp = 150 + Math.random() * 600;
    particles.push({ x: it.x, y: it.y, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - 100, r: 3 + Math.random() * 7, c: fire[i % fire.length], life: 0, max: 0.5 + Math.random() * 0.6 });
  }
  rings.push({ x: it.x, y: it.y, color: '#ff5d5d', t0: now, r: it.r * 1.5 });
  pops.push({ x: it.x, y: it.y - it.r, text: lose ? `💣 -${lose}` : '💣 펑!', color: '#ff6b6b', t0: now, big: 1.4 });
  shake = 0.5;
  sfx.boom();
  sound.say(lose ? `앗, 폭탄! 구슬 ${lose}개를 잃었어요` : '앗, 폭탄!', 1);
}

function distToSeg(px, py, ax, ay, bx, by) {
  const dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
  const t = l2 ? Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / l2)) : 0;
  return Math.hypot(px - (ax + t * dx), py - (ay + t * dy));
}

// ───────── 매 프레임 ─────────
let last = performance.now();
function frame(ms) {
  requestAnimationFrame(frame);
  const dt = Math.min(0.05, (ms - last) / 1000); last = ms;
  const now = ms / 1000;

  // 배경 + 그림자 (참가자만, 스켈레톤은 공통 설정)
  const showPlayers = phase === 'setup' ? [] : players;
  stage.draw({ players: showPlayers, mirror: MIRROR, others: phase === 'setup', skeleton: settings.skeleton, background: paintSky });
  layout();

  if (phase !== 'setup' && phase !== 'results') joinPlayers();
  if (AUTOPLAY) simSwing(now);

  if (phase === 'count') updateCount(now);
  if (phase === 'play') {
    const left = game.end - now;
    $('timer').textContent = fmt(Math.max(0, left));
    $('timer').classList.toggle('low', left <= 10);
    if (left <= 10 && !game.said10) { game.said10 = true; sound.say('10초 남았어요!', 1); }
    if (now >= game.nextWave) {
      spawnWave(now);
      game.nextWave = now + RULE.waveGap[0] + Math.random() * (RULE.waveGap[1] - RULE.waveGap[0]);
    }
    handleHands(now);
    if (left <= 0) finish(now);
  } else if (phase === 'end') {
    handleHands(now);   // 날아가는 중인 구슬은 마저 꿸 수 있게
    if (now - phaseAt > 2.2 && !flyers.length) showResults();
  } else for (const p of players) for (const h of HANDS) p.hands[h] = null;

  step(dt, now);
  draw(now);
  pointer.update(dt);
  show('pointerHint', phase === 'results' && !pointer.visible && client.bodies.size > 0);
  if (phase === 'setup') updateSeen();
}
requestAnimationFrame(frame);

function updateCount(now) {
  const k = now - phaseAt;
  if (k < 1.2) { $('count').textContent = '준비!'; return; }
  const n = 3 - Math.floor((k - 1.2) / 0.9);
  if (n > 0) {
    if (n !== game.countShown) { game.countShown = n; $('count').textContent = n; sound.tick(); }
    return;
  }
  sound.go();
  game.t0 = now; game.end = now + opts.time; game.nextWave = now + 0.4;
  $('timer').textContent = fmt(opts.time);
  setPhase('play');
}

function finish(now) {
  setPhase('end');
  setMsg('⏰ 시간 끝!');
  sound.fanfare();
  sound.say('시간 끝! 잘하셨어요', 0.95);
  // 남은 폭탄은 조용히 사라짐
  for (let i = items.length - 1; i >= 0; i--) if (items[i].kind === 'bomb') items.splice(i, 1);
}

const fmt = s => { const t = Math.ceil(s); return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, '0')}`; };

function showResults() {
  setPhase('results');
  const list = [...players].sort((a, b) => (b.beads.length - a.beads.length) || (a.index - b.index));
  const rankOf = p => 1 + list.filter(q => q.beads.length > p.beads.length).length;   // 같으면 공동 등수
  const top = list[0];
  const tie = list.filter(p => p.beads.length === top?.beads.length).length > 1;
  $('praise').textContent = !top ? '다음엔 함께 해요!' : top.beads.length === 0 ? '구슬 가운데를 노려 보세요! 💪'
    : tie ? `🏆 공동 1등! 구슬 ${top.beads.length}개` : `🏆 ${top.name}님이 구슬 ${top.beads.length}개로 1등!`;
  $('ranking').innerHTML = list.map(p => {
    const r = rankOf(p), medal = ['🥇', '🥈', '🥉'][r - 1] ?? `${r}등`;
    return `<div class="rk${r === 1 ? ' first' : ''}" style="--c:${p.color}"><span class="pos">${medal}</span><span class="nm">${esc(p.name)}</span>`
      + `<span class="cnt">📿 <b>${p.beads.length}</b>개</span><span class="etc">💣 폭탄 ${p.bombs}번${p.lost ? ` (잃은 구슬 ${p.lost}개)` : ''}</span></div>`;
  }).join('');
  if (top?.beads.length) sound.say(tie ? '공동 일등이에요!' : `${top.name}님이 1등이에요!`, 0.95);
}

function updateSeen() {
  const n = Math.min(client.bodies.size, settings.players), el = $('seen');
  const txt = client.connected === false && !SIM ? '⚠ 에이전트에 연결되지 않았어요 (run.bat 실행 확인)'
    : n ? `<b>${n}명</b> 준비됐어요! (최대 ${settings.players}명까지 · 게임 중에 들어와도 돼요)` : `카메라 앞에 서 주세요 · 최대 <b>${settings.players}명</b>까지 함께 해요`;
  if (el.innerHTML !== txt) el.innerHTML = txt;
  el.classList.toggle('warn', !n);
  $('start').disabled = !n;
}

// ───────── 움직임 ─────────
function step(dt, now) {
  for (let i = items.length - 1; i >= 0; i--) {
    const it = items[i];
    it.vy += it.g * dt; it.x += it.vx * dt; it.y += it.vy * dt; it.a += it.spin * dt;
    if (it.vy > 0 && it.y > V.y + V.h + it.r * 2) items.splice(i, 1);   // 놓친 구슬: 그냥 사라짐 (벌점 없음)
  }
  for (let i = flyers.length - 1; i >= 0; i--) {
    const f = flyers[i];
    if (now - f.t0 >= f.dur) { flyers.splice(i, 1); f.p.beads.push(f.color); sfx.click(f.p.beads.length); f.p.lastAt = now; }
  }
  for (let i = drops.length - 1; i >= 0; i--) {
    const d = drops[i];
    d.life += dt; d.vy += 1400 * dt; d.x += d.vx * dt; d.y += d.vy * dt;
    if (d.y > L.H + 40) drops.splice(i, 1);
  }
  for (let i = particles.length - 1; i >= 0; i--) {
    const p = particles[i];
    p.life += dt; p.vy += 700 * dt; p.x += p.vx * dt; p.y += p.vy * dt;
    if (p.life > p.max) particles.splice(i, 1);
  }
  for (const [k, tr] of trails) { while (tr.length && now - tr[0].t > 0.22) tr.shift(); if (!tr.length) trails.delete(k); }
  shake = Math.max(0, shake - dt);
}

// ───────── 그리기 ─────────
let skyCache = null;
/** 그림자뷰 배경 (게임이 붙임): 밤하늘 + 흐린 별빛 */
function paintSky(c, W, H) {
  if (!skyCache || skyCache.width !== W || skyCache.height !== H) {
    skyCache = document.createElement('canvas'); skyCache.width = W; skyCache.height = H;
    const g = skyCache.getContext('2d');
    const gr = g.createLinearGradient(0, 0, 0, H);
    gr.addColorStop(0, '#1b2a55'); gr.addColorStop(0.55, '#2c3f73'); gr.addColorStop(1, '#4a4f86');
    g.fillStyle = gr; g.fillRect(0, 0, W, H);
    for (let i = 0; i < 70; i++) {
      const x = Math.random() * W, y = Math.random() * H * 0.8, r = (Math.random() * 1.6 + 0.4) * (W / 1600);
      g.globalAlpha = 0.3 + Math.random() * 0.6; g.fillStyle = '#fff';
      g.beginPath(); g.arc(x, y, r, 0, Math.PI * 2); g.fill();
    }
    g.globalAlpha = 0.18;
    for (let i = 0; i < 8; i++) {
      const x = Math.random() * W, y = Math.random() * H, r = (60 + Math.random() * 160) * (W / 1600);
      const rg = g.createRadialGradient(x, y, 0, x, y, r);
      rg.addColorStop(0, BEAD_COLORS[i % BEAD_COLORS.length]); rg.addColorStop(1, 'transparent');
      g.fillStyle = rg; g.beginPath(); g.arc(x, y, r, 0, Math.PI * 2); g.fill();
    }
  }
  c.drawImage(skyCache, 0, 0);
}

function draw(now) {
  const c = fg;
  c.setTransform(L.dpr, 0, 0, L.dpr, 0, 0);
  c.clearRect(0, 0, L.W, L.H);
  const sx = shake ? (Math.random() - 0.5) * 16 * shake : 0, sy = shake ? (Math.random() - 0.5) * 16 * shake : 0;
  c.save(); c.translate(sx, sy);

  if (phase !== 'setup') {
    // 손 자국 (휘두를 때만, 그 사람 색)
    for (const [k, tr] of trails) {
      const p = players[+k.split(':')[0]];
      if (!p || tr.length < 2) continue;
      for (let i = 1; i < tr.length; i++) {
        const a = tr[i - 1], b = tr[i], age = (now - b.t) / 0.22;
        c.strokeStyle = p.color; c.globalAlpha = Math.max(0, 0.9 - age);
        c.lineWidth = Math.max(4, beadR() * 0.45 * (i / tr.length)); c.lineCap = 'round';
        c.shadowColor = p.color; c.shadowBlur = 16;
        c.beginPath(); c.moveTo(a.x, a.y); c.lineTo(b.x, b.y); c.stroke();
      }
      c.shadowBlur = 0; c.globalAlpha = 1;
    }
    for (const it of items) it.kind === 'bomb' ? drawBomb(c, it, now) : drawBead(c, it.x, it.y, it.r, it.color, it.a, true);
    // 꿰어져 날아가는 구슬 + 실 (그 사람 색 실이 구슬을 끌어당김)
    for (const f of flyers) {
      const k = Math.min(1, (now - f.t0) / f.dur), e = 1 - (1 - k) ** 3;
      const s = strandSpot(f.p, f.k);
      const mx = (f.x0 + s.x) / 2, my = Math.min(f.y0, s.y) - 120;
      const x = bez(f.x0, mx, s.x, e), y = bez(f.y0, my, s.y, e), r = f.r0 + (s.r - f.r0) * e;
      c.strokeStyle = f.p.color; c.lineWidth = 3; c.globalAlpha = 0.85;
      c.beginPath(); c.moveTo(s.x, s.y); c.quadraticCurveTo(mx, my, x, y); c.stroke(); c.globalAlpha = 1;
      drawBead(c, x, y, r, f.color, k * 6, false);
    }
    for (const d of drops) drawBead(c, d.x, d.y, d.r, d.color, d.life * 8, false);
    for (const p of particles) {
      c.globalAlpha = Math.max(0, 1 - p.life / p.max);
      c.fillStyle = p.c;
      if (p.star) drawStar(c, p.x, p.y, p.r * 1.8);
      else { c.beginPath(); c.arc(p.x, p.y, p.r, 0, Math.PI * 2); c.fill(); }
    }
    c.globalAlpha = 1;
    for (let i = rings.length - 1; i >= 0; i--) {
      const r = rings[i], k = (now - r.t0) / 0.5;
      if (k >= 1) { rings.splice(i, 1); continue; }
      if (k < 0) continue;
      c.globalAlpha = 1 - k; c.strokeStyle = r.color; c.lineWidth = 8 * (1 - k) + 2;
      c.shadowColor = r.color; c.shadowBlur = 18;
      c.beginPath(); c.arc(r.x, r.y, r.r * (1 + 1.6 * k), 0, Math.PI * 2); c.stroke();
      c.shadowBlur = 0; c.globalAlpha = 1;
    }
    for (let i = pops.length - 1; i >= 0; i--) {
      const p = pops[i], k = (now - p.t0) / 0.9;
      if (k >= 1) { pops.splice(i, 1); continue; }
      const size = beadR() * 1.1 * p.big * (k < 0.15 ? k / 0.15 * 1.15 : 1.15 - 0.15 * (k - 0.15));
      c.globalAlpha = k > 0.7 ? (1 - k) / 0.3 : 1;
      c.font = `900 ${Math.round(size)}px "Malgun Gothic", sans-serif`; c.textAlign = 'center'; c.textBaseline = 'middle';
      const y = Math.max(size, p.y - 50 * k);
      c.lineWidth = size * 0.14; c.strokeStyle = '#0b0e13'; c.strokeText(p.text, p.x, y);
      c.fillStyle = p.color; c.fillText(p.text, p.x, y);
      c.globalAlpha = 1;
    }
  }
  c.restore();
  if (phase !== 'setup') for (const p of players) drawStrand(c, p, now);
}

const bez = (a, m, b, t) => (1 - t) * (1 - t) * a + 2 * (1 - t) * t * m + t * t * b;

/** 구슬: 반짝이는 공 + 가운데 구멍 (가운데를 노리라는 표시) */
function drawBead(c, x, y, r, color, a, hole) {
  const g = c.createRadialGradient(x - r * 0.35, y - r * 0.4, r * 0.1, x, y, r);
  g.addColorStop(0, '#ffffff'); g.addColorStop(0.25, color); g.addColorStop(1, shade(color, -0.45));
  c.fillStyle = g;
  c.beginPath(); c.arc(x, y, r, 0, Math.PI * 2); c.fill();
  c.lineWidth = Math.max(1.5, r * 0.06); c.strokeStyle = 'rgba(0,0,0,.35)'; c.stroke();
  // 구멍 (살짝 돌아감)
  const hr = r * (hole ? 0.26 : 0.22);
  c.fillStyle = 'rgba(10,12,20,.75)';
  c.beginPath(); c.ellipse(x + Math.cos(a) * r * 0.05, y, hr, hr * (0.75 + 0.25 * Math.abs(Math.cos(a))), 0, 0, Math.PI * 2); c.fill();
  if (hole) {   // 가운데 노리는 고리
    c.strokeStyle = 'rgba(255,255,255,.75)'; c.lineWidth = Math.max(1.5, r * 0.05);
    c.beginPath(); c.arc(x, y, r * RULE.centerHit, 0, Math.PI * 2); c.setLineDash([r * 0.18, r * 0.14]); c.stroke(); c.setLineDash([]);
  }
}

/** 폭탄: 검은 공 + 빨갛게 깜빡이는 테두리 + 불꽃 심지 (구슬과 확실히 다르게) */
function drawBomb(c, it, now) {
  const { x, y, r } = it, blink = 0.5 + 0.5 * Math.sin(now * 12);
  c.save();
  c.shadowColor = '#ff2d2d'; c.shadowBlur = 20 + 20 * blink;
  c.strokeStyle = `rgba(255,60,60,${0.6 + 0.4 * blink})`; c.lineWidth = Math.max(4, r * 0.14);
  c.beginPath(); c.arc(x, y, r * 1.12, 0, Math.PI * 2); c.stroke();
  c.restore();
  const g = c.createRadialGradient(x - r * 0.35, y - r * 0.35, r * 0.1, x, y, r);
  g.addColorStop(0, '#6b6f7a'); g.addColorStop(1, '#0d0f14');
  c.fillStyle = g; c.beginPath(); c.arc(x, y, r, 0, Math.PI * 2); c.fill();
  // 심지 + 불꽃
  const fx = x + r * 0.55, fy = y - r * 0.85;
  c.strokeStyle = '#c9a66b'; c.lineWidth = Math.max(2, r * 0.1);
  c.beginPath(); c.moveTo(x + r * 0.35, y - r * 0.7); c.quadraticCurveTo(x + r * 0.6, y - r * 1.05, fx, fy); c.stroke();
  c.fillStyle = blink > 0.5 ? '#ffd23f' : '#ff7b2d';
  drawStar(c, fx, fy, r * (0.28 + 0.1 * blink));
  // 엑스 표시 (건드리지 마세요)
  c.strokeStyle = 'rgba(255,90,90,.95)'; c.lineWidth = Math.max(3, r * 0.12); c.lineCap = 'round';
  const k = r * 0.32;
  c.beginPath(); c.moveTo(x - k, y - k); c.lineTo(x + k, y + k); c.moveTo(x + k, y - k); c.lineTo(x - k, y + k); c.stroke();
}

/** 실: 위에서 늘어진 그 사람 색 실 + 꿴 구슬 + 이름·개수 */
function drawStrand(c, p, now) {
  const ln = lane(p);
  const flash = now - p.flash < 0.6 ? 1 - (now - p.flash) / 0.6 : 0;
  c.save();
  c.globalAlpha = p.present ? 1 : 0.5;
  c.fillStyle = flash ? `rgba(255,80,80,${0.25 + 0.35 * flash})` : 'rgba(5,10,20,.55)';
  roundRect(c, ln.x - ln.w / 2 + 3, ln.top - 56, ln.w - 6, ln.bottom - ln.top + 56, 12); c.fill();
  // 이름 · 개수
  c.textAlign = 'center'; c.textBaseline = 'middle';
  c.fillStyle = p.color; c.font = `800 ${Math.round(Math.min(18, ln.w * 0.22))}px "Malgun Gothic", sans-serif`;
  c.fillText(p.name, ln.x, ln.top - 38, ln.w - 10);
  c.fillStyle = '#fff'; c.font = `900 ${Math.round(Math.min(34, ln.w * 0.42))}px "Malgun Gothic", sans-serif`;
  c.fillText(String(p.beads.length), ln.x, ln.top - 6, ln.w - 10);
  // 실
  const top = ln.top + 26, end = p.beads.length ? strandSpot(p, p.beads.length - 1).y + 18 : top + 40;
  c.strokeStyle = p.color; c.lineWidth = 3;
  c.beginPath(); c.moveTo(ln.x, top); c.lineTo(ln.x, Math.min(ln.bottom, end)); c.stroke();
  // 구슬 (아래로 쌓임)
  p.beads.forEach((col, k) => { const s = strandSpot(p, k); if (s.y < ln.bottom) drawBead(c, s.x, s.y, s.r, col, k, false); });
  c.restore();
}

function drawStar(c, x, y, r) {
  c.beginPath();
  for (let i = 0; i < 10; i++) {
    const a = -Math.PI / 2 + i * Math.PI / 5, rr = i % 2 ? r * 0.45 : r;
    i ? c.lineTo(x + Math.cos(a) * rr, y + Math.sin(a) * rr) : c.moveTo(x + Math.cos(a) * rr, y + Math.sin(a) * rr);
  }
  c.closePath(); c.fill();
}

function roundRect(c, x, y, w, h, r) {
  c.beginPath(); c.moveTo(x + r, y); c.arcTo(x + w, y, x + w, y + h, r); c.arcTo(x + w, y + h, x, y + h, r);
  c.arcTo(x, y + h, x, y, r); c.arcTo(x, y, x + w, y, r); c.closePath();
}

function shade(color, amt) {
  const m = /^#?([0-9a-f]{6})$/i.exec(color);
  if (!m) return color;
  const n = parseInt(m[1], 16);
  const f = v => Math.max(0, Math.min(255, Math.round(amt < 0 ? v * (1 + amt) : v + (255 - v) * amt)));
  return `rgb(${f(n >> 16)}, ${f((n >> 8) & 255)}, ${f(n & 255)})`;
}

function esc(s) { return String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]); }

// ───────── 효과음 ─────────
const sfx = {
  noise: null,
  ac() {
    const ac = sound.ctx();
    if (!this.noise) {
      this.noise = ac.createBuffer(1, ac.sampleRate, ac.sampleRate);
      const d = this.noise.getChannelData(0);
      for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
    }
    return ac;
  },
  burst({ dur, type, f0, f1 = f0, q = 1, gain = 0.3 }) {
    try {
      const ac = this.ac(), t = ac.currentTime;
      const src = ac.createBufferSource(), flt = ac.createBiquadFilter(), g = ac.createGain();
      src.buffer = this.noise; flt.type = type; flt.Q.value = q;
      flt.frequency.setValueAtTime(f0, t); flt.frequency.exponentialRampToValueAtTime(f1, t + dur);
      g.gain.setValueAtTime(gain, t); g.gain.exponentialRampToValueAtTime(0.001, t + dur);
      src.connect(flt).connect(g).connect(ac.destination); src.start(t); src.stop(t + dur + 0.02);
    } catch { /* 무시 */ }
  },
  launch() { this.burst({ dur: 0.22, type: 'bandpass', f0: 300, f1: 900, q: 1.5, gain: 0.06 }); },
  fuse() { this.burst({ dur: 0.5, type: 'highpass', f0: 4000, f1: 2500, gain: 0.06 }); },
  // 꿰기: 맑은 "띵" — 꿸수록 음이 조금씩 올라감
  thread(k) { const s = [523, 587, 659, 784, 880, 988, 1047]; sound.tone([s[k % s.length], s[k % s.length] * 1.5], 0.08, 'triangle', 0.14); this.burst({ dur: 0.12, type: 'highpass', f0: 3000, gain: 0.08 }); },
  // 실에 닿음: 작은 "톡"
  click(n) { sound.tone([1200 + (n % 8) * 40], 0.035, 'square', 0.04); },
  boom() { this.burst({ dur: 0.7, type: 'lowpass', f0: 1400, f1: 60, q: 0.7, gain: 0.8 }); sound.tone([90, 60], 0.25, 'sine', 0.35); },
};

// ───────── 소리 허용 ─────────
function checkSound() { show('soundBlocked', sound.blocked()); }
$('soundBlocked').onclick = () => { sound.unlock(); checkSound(); };
addEventListener('pointerdown', () => { sound.unlock(); checkSound(); });
setTimeout(checkSound, 1500);

// ───────── 시험용 (카메라 없이) ─────────
/** ?sim=N&autoplay=1: 가상 플레이어가 오른손을 크게 휘두름 (가끔 구슬 쪽으로) */
function simSwing(now) {
  if (phase !== 'play' && phase !== 'end') { for (let i = 0; i < SIM; i++) client.sim.point(i, null); return; }
  for (let i = 0; i < SIM; i++) {
    const t = now * 3.2 + i * 1.7;
    client.sim.point(i, { dx: 0.5 * Math.sin(t), dy: 0.05 + 0.32 * Math.sin(now * 1.9 + i) });
  }
}
if (SIM) {
  window.__bead = () => ({ phase, items: items.length, bombs: items.filter(x => x.kind === 'bomb').length,
    players: players.map(p => [p.name, p.beads.length, p.bombs]), waves: game?.waves ?? 0 });
  window.__beadStart = () => startGame();
}
setPhase('setup');
