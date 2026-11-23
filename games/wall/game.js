// ─────────────────────────────────────────────────────────────
//  벽 구멍 통과
//
//  흐름: 설정 → 공 잡기(두 손 모으기) → 3·2·1 → 벽이 계속 다가옴(점점 빨라짐) → 모두 탈락하면 결과(등수)
//  - 캐릭터 타입: 그림자뷰 (GAME_RULEBOOK.md) — 사람은 번진 그림자, 배경은 이 게임이 그리는 하늘·트랙
//  - 트랙: 최대 참여 인원(공통 설정) 수만큼 나란히, 사람이 보이는 영상 영역에 맞춰 깔림.
//          사람은 서 있는 자리(화면 왼쪽→오른쪽)에 가까운 트랙을 받음
//  - 공 잡기: 두 손을 RULE.grabGap(m) 안으로 모으면 내 트랙의 공이 손으로 올라옴.
//             두 손이 RULE.releaseGap 넘게 RULE.releaseTime 초 벌어지면 공을 놓침(바닥으로) → 다시 모으면 또 잡힘
//  - 공 위치: 화면에 보이는 두 손 사이 (가운데). 단, 내 트랙 밖·벽 위아래로는 나가지 않음. 던지는 게 아니라 손으로 옮김
//  - 벽: 저 멀리서 다가옴. 트랙마다 동그란 구멍 하나. 자리는 레일 안 가로 3 × 세로 3 중 정중앙을 뺀 8곳.
//        이전 구멍과 같은 자리는 안 나오고, 멀리 떨어진 자리가 잘 나옴
//        구멍 높이는 벽이 출발할 때 그 사람의 몸(가슴·골반 위치)에 맞춰 손이 닿는 범위 안에서, 가로는 레일 안에서 정함
//        벽이 공 자리에 오는 순간 공이 구멍 안(테두리를 RULE.edgeGrace 만큼 봐줌)이면 통과 +1, 아니면(또는 공을 놓친 상태면) 탈락
//  - 속도: 벽마다 RULE.speedUp 배씩 빨라짐 (가장 빠른 것은 RULE.minTime)
//  - 등수: 통과한 벽 수 (끝까지 버틴 사람이 가장 많음). 같으면 공동 등수
//  - 참가: 공 잡기 단계까지 들어온 사람 (게임이 시작되면 새로 참여하지 않음)
//
//  카메라 없이 시험: ?sim=2&autoplay=1 (가상 플레이어가 공을 잡고 손을 구멍 쪽으로 옮김, 빨라지면 점점 놓침)
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
  grabGap: 0.25,          // 두 손 사이가 이 안(m)이면 공 잡기
  grabTime: 0.25,         // 이만큼(초) 모으고 있어야 잡힘
  releaseGap: 0.5,        // 두 손 사이가 이보다 벌어지고
  releaseTime: 0.6,       //   이만큼(초) 지나면 공을 놓침 (잠깐 흔들린 건 봐줌)
  reachDown: 1.15,        // 손이 닿는 범위 (몸통 길이 = 가슴~골반 배수): 가슴 아래로 이만큼 = 맨 아래
  reachUp: 0.9,           //                                           가슴 위로 이만큼 = 맨 위
  holes: [0.12, 0.5, 0.88], // 구멍 가운데 높이 (손이 닿는 범위 비율): 아래 · 가운데 · 위
  // 구멍 자리 = 가로(왼쪽·가운데·오른쪽) × 세로(아래·가운데·위) 9곳 중 레일 정중앙을 뺀 8곳
  holeRadius: 0.2,        // 동그란 구멍 반지름 (손이 닿는 범위 비율)
  holeMin: 2.2,           //   단, 공 반지름의 이 배 이상 (넉넉하게)
  holeMaxLane: 0.32,      //   단, 트랙 너비의 이 비율 이하 (레일 안에서 좌우로 옮길 여유를 남김)
  edgeGrace: 0.4,         // 공이 구멍 테두리에 공 반지름의 이만큼 걸쳐도 통과로 봄
  ballSize: 0.075,        // 공 반지름 (벽 높이 비율)
  firstTime: 6.5,         // 첫 벽이 다가오는 데 걸리는 시간 (초)
  speedUp: 0.94,          // 벽마다 걸리는 시간 × 이만큼 (점점 빨라짐)
  minTime: 2.2,           // 가장 빠를 때 (초)
  spawnGap: 0.8,          // 다음 벽은 앞 벽이 이만큼 왔을 때 출발 (0~1)
  waitAll: 15,            // 공 잡기 단계: 한 명이라도 잡고 이만큼(초) 지나면 나머지를 기다리지 않고 시작
};
const PERSPECTIVE = 7;    // 멀리 있는 벽이 작아 보이는 정도

// ───────── 준비 ─────────
const client = new FemtoClient({ sim: SIM });   // 그림자뷰: 영상은 안 보이지만 사람 모양(마스크)이 영상과 함께 옴
const stage = new Stage($('stage'), client, { character: 'shadow' });
const sound = new Sound();
const pointer = new HandPointer({ client, mirror: MIRROR, onSelect: () => sound.tick() });
const shell = new GameShell({ name: '벽 구멍 통과', icon: '🧱', sim: SIM, onHome: () => { sound.stop(); setPhase('setup'); } });

// ───────── 상태 ─────────
let phase = 'setup';            // setup | grab | count | play | end | results
let phaseAt = 0;
let players = [];               // Player + { lane, ball: 'floor'|'held'|'out', x, y(공 자리 화면 px), vy, passed, outAt, near, apart }
let lanes = [];                 // 트랙: { player | null }
let game = null;
const walls = [];               // { d (0 멀리 → 1 공 자리), T, holes: [트랙별 동그란 구멍 { cx, cy, R, spot }], judged, n }
const particles = [], pops = [], rings = [];
const beams = [], holeFlashes = [];   // 통과 효과: 레일 빛기둥 · 구멍 테두리 번쩍
let shake = 0;

function show(id, on) { $(id).classList.toggle('hidden', !on); }
function setPhase(p) {
  phase = p;
  phaseAt = performance.now() / 1000;
  show('setup', p === 'setup');
  show('results', p === 'results');
  show('hud', p === 'grab' || p === 'count' || p === 'play' || p === 'end');
  show('count', p === 'count');
  pointer.enabled = p === 'setup' || p === 'results';
  if (!pointer.enabled) pointer.hide();
  if (p === 'setup') { clearFx(); players = []; lanes = []; }
  if (p === 'setup' || p === 'grab') grayLanes();
}

$('start').onclick = startGame;
$('again').onclick = startGame;
addEventListener('keydown', e => { if (e.code === 'Escape' && phase !== 'setup') { sound.stop(); setPhase('setup'); } });

function startGame() {
  sound.unlock();
  players = [];
  lanes = Array.from({ length: settings.players }, () => ({ player: null }));
  clearFx();
  game = { n: 0, nextT: RULE.firstTime, firstHeldAt: 0, countShown: 0, lastSpeedSay: 0, spots: lanes.map(() => null) };   // spots: 트랙별 지난 구멍 자리
  layout();
  joinPlayers();
  setPhase('grab');
  setMsg('🤲 두 손을 모아 공을 잡으세요');
  sound.say('두 손을 가까이 모아서, 내 앞의 공을 잡아 보세요.', 0.92);
}

function clearFx() { walls.length = particles.length = pops.length = rings.length = beams.length = holeFlashes.length = 0; }

function setMsg(t) { if ($('msg').textContent !== t) $('msg').textContent = t; }

// ───────── 참가자: 서 있는 자리에 가까운 빈 트랙 ─────────
function joinPlayers() {
  trackPlayers(players, client);
  const free = lanes.filter(l => !l.player).length;
  if (free <= 0) return;
  const taken = new Set(players.map(p => p.bodyId));
  const fresh = pickBodies({ bodies: new Map([...client.bodies].filter(([id]) => !taken.has(id))) }, free, MIRROR);
  // 빈 트랙 중 서 있는 자리에 가장 가까운 곳. 여러 명이 한꺼번에 들어오면 왼쪽 사람이 왼쪽 트랙 (순서 유지)
  const freeLanes = lanes.map((l, i) => (l.player ? -1 : i)).filter(i => i >= 0);
  let prevJ = -1;
  fresh.forEach((b, k) => {
    const scr = stage.toScreen(b, J.Pelvis);
    const want = scr ? Math.floor(((scr.x - L.X0) / L.TW) * lanes.length) : 0;
    let j = -1, best = Infinity;
    for (let q = prevJ + 1; q <= freeLanes.length - (fresh.length - k); q++)
      if (Math.abs(freeLanes[q] - want) < best) { best = Math.abs(freeLanes[q] - want); j = q; }
    if (j < 0) return;
    prevJ = j;
    const lane = freeLanes[j];
    const p = new Player(players.length, b, client);
    Object.assign(p, { lane, ball: 'floor', x: laneX(lane), y: L.FY - ballR(), vy: 0, passed: 0, outAt: 0, near: 0, apart: 0, flash: 0 });
    lanes[lane].player = p;
    players.push(p);
  });
}

// ───────── 무대 (CSS px) ─────────
// 원근: 공 자리(가까운 면)의 점 (x, y)는 깊이 d 에서 소실점 (VX, HY) 쪽으로 s(d) 만큼 줄어듦
const fxCanvas = $('fx'), fg = fxCanvas.getContext('2d');
let L = { W: 1, H: 1, dpr: 1 };
function layout() {
  const dpr = Math.min(devicePixelRatio || 1, 2);
  const r = fxCanvas.getBoundingClientRect();
  if (fxCanvas.width !== Math.round(r.width * dpr) || fxCanvas.height !== Math.round(r.height * dpr)) {
    fxCanvas.width = Math.round(r.width * dpr); fxCanvas.height = Math.round(r.height * dpr);
  }
  const W = r.width || 1, H = r.height || 1;
  const HY = H * 0.15, FY = H * 0.88;   // 하늘 끝(소실점 높이) · 공 자리 바닥 (그 아래 = 이름표 띠)
  // 트랙 = 사람이 보이는 영상 영역 (그래야 서 있는 자리 = 내 트랙, 손 위치 = 공 위치)
  const m = stage.map;
  const vx = m ? m.dx / m.dpr : 0, vw = m ? m.dw / m.dpr : W;
  const X0 = Math.max(vx, W * 0.03), X1 = Math.min(vx + vw, W * 0.97);
  L = { W, H, dpr, VX: (X0 + X1) / 2, HY, FY, WH: (FY - HY) * 0.93, TW: X1 - X0, X0 };
}
const scaleAt = d => 1 / (1 + PERSPECTIVE * (1 - d));
const proj = (x, y, d) => { const s = scaleAt(d); return [L.VX + (x - L.VX) * s, L.HY + (y - L.HY) * s]; };
const laneW = () => L.TW / Math.max(1, lanes.length);
const laneX = i => L.X0 + laneW() * (i + 0.5);                 // 트랙 가운데
const laneL = i => L.X0 + laneW() * i, laneR = i => L.X0 + laneW() * (i + 1);
const wallTop = () => L.FY - L.WH;                              // 공 자리에서 벽 꼭대기 y
const ballR = () => Math.min(L.WH * RULE.ballSize, laneW() * 0.2);
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));

/** 이 사람 손이 닿는 범위 (화면 y): 가슴·골반 위치로 몸 크기를 재서. 사람이 없으면 벽 높이 기준 */
function reachOf(p) {
  const b = p?.body, r = ballR();
  const chest = b && stage.toScreen(b, J.SpineChest), pelvis = b && stage.toScreen(b, J.Pelvis);
  let low = L.FY - L.WH * 0.1, high = L.FY - L.WH * 0.9;
  if (chest && pelvis) {
    const t = Math.max(20, pelvis.y - chest.y);   // 몸통 길이 (px)
    low = chest.y + RULE.reachDown * t; high = chest.y - RULE.reachUp * t;
  }
  return { low: clamp(low, wallTop() + r * 2, L.FY - r), high: clamp(high, wallTop() + r, L.FY - r * 2) };
}

// ───────── 두 손 읽기 ─────────
/** 두 손: 사이 거리(m) + 화면에서 두 손 가운데 (공이 갈 자리) */
function readHands(p) {
  const b = p.body;
  if (!b || b.conf(J.HandLeft) <= 0 || b.conf(J.HandRight) <= 0) return null;
  const l = b.pos(J.HandLeft), r = b.pos(J.HandRight);
  const sl = stage.toScreen(b, J.HandLeft), sr = stage.toScreen(b, J.HandRight);
  if (!sl || !sr) return null;
  return { gap: Math.hypot(l.x - r.x, l.y - r.y, l.z - r.z), x: (sl.x + sr.x) / 2, y: (sl.y + sr.y) / 2 };
}

const GRAVITY = 2400;   // 공이 떨어지는 빠르기 (px/s²)
function updateBalls(dt, now) {
  const r = ballR();
  for (const p of players) {
    if (p.ball === 'out') { if (p.y < L.H + r * 2) { p.vy += GRAVITY * dt; p.y += p.vy * dt; } continue; }
    const hands = readHands(p);
    if (p.ball === 'held') {
      if (!hands || hands.gap > RULE.releaseGap) p.apart += dt; else p.apart = 0;
      if (p.apart >= RULE.releaseTime) { drop(p); continue; }
      if (hands) {   // 공 = 두 손 사이 (내 트랙 안, 벽 높이 안에서)
        const tx = clamp(hands.x, laneL(p.lane) + r, laneR(p.lane) - r), ty = clamp(hands.y, wallTop() + r, L.FY - r);
        const k = Math.min(1, dt * 14);
        p.x += (tx - p.x) * k; p.y += (ty - p.y) * k;
      }
    } else {
      // 바닥에 놓인 공: 떨어지는 중이면 떨어지고, 두 손을 모으면 잡힘
      p.vy += GRAVITY * dt; p.y = Math.min(L.FY - r, p.y + p.vy * dt); if (p.y >= L.FY - r) p.vy = 0;
      p.x = clamp(p.x, laneL(p.lane) + r, laneR(p.lane) - r);
      if (hands && hands.gap <= RULE.grabGap) p.near += dt; else p.near = 0;
      if (p.near >= RULE.grabTime && (phase === 'grab' || phase === 'count' || phase === 'play')) grab(p, now);
    }
  }
}

function grab(p, now) {
  p.ball = 'held'; p.apart = 0; p.vy = 0;
  if (!game.firstHeldAt) game.firstHeldAt = now;
  rings.push({ x: p.x, y: p.y, color: p.color, t0: now, r: ballR() });
  sfx.grab();
  if (phase === 'play') sound.say('다시 잡았어요!', 1.05);
}

function drop(p) {
  p.ball = 'floor'; p.near = 0; p.vy = -250;
  sfx.drop();
  if (phase === 'play') sound.say(`${p.name}님, 공을 놓쳤어요. 두 손을 다시 모으세요!`, 1);
}

// ───────── 벽 ─────────
function spawnWall() {
  const T = Math.max(RULE.minTime, game.nextT);
  game.nextT = T * RULE.speedUp;
  const r = ballR(), lw = laneW();
  const holes = lanes.map((ln, i) => {
    const spot = nextSpot(game.spots[i]);
    game.spots[i] = spot;
    const reach = reachOf(ln.player);                 // 높이는 그 사람 손이 닿는 범위 안에
    const span = reach.low - reach.high;
    const R = Math.min(lw * RULE.holeMaxLane, Math.max(r * RULE.holeMin, span * RULE.holeRadius));
    const cy = clamp(reach.low - span * RULE.holes[spot.v], wallTop() + R, L.FY - R);
    const side = Math.max(0, lw / 2 - R - 6);         // 레일 안에서 좌우로 옮길 수 있는 만큼
    const cx = laneX(i) + spot.u * side;
    return { cx, cy, R, spot };
  });
  walls.push({ d: 0, T, holes, judged: false, n: ++game.n });
  sfx.whoosh();
}

/** 구멍 자리 후보: u = 가로(-1 왼쪽 · 0 가운데 · 1 오른쪽), v = 세로(0 아래 · 1 가운데 · 2 위). 레일 정중앙(0, 1)은 뺌 */
const SPOTS = [-1, 0, 1].flatMap(u => [0, 1, 2].map(v => ({ u, v }))).filter(sp => !(sp.u === 0 && sp.v === 1));
/** 다음 구멍 자리: 이전과 같은 자리는 안 나오고, 이전 구멍에서 멀수록 잘 나옴 (거리² 비율) */
function nextSpot(prev) {
  if (!prev) return SPOTS[Math.floor(Math.random() * SPOTS.length)];
  const wts = SPOTS.map(sp => (sp.u - prev.u) ** 2 + (sp.v - prev.v) ** 2);   // 같은 자리 = 0
  let x = Math.random() * wts.reduce((a, b) => a + b, 0);
  for (let k = 0; k < SPOTS.length; k++) if ((x -= wts[k]) < 0) return SPOTS[k];
  return SPOTS.find(sp => sp !== prev);
}

function updateWalls(dt, now) {
  const last = walls[walls.length - 1];
  if (phase === 'play' && (!last || last.d >= RULE.spawnGap)) spawnWall();
  for (let i = walls.length - 1; i >= 0; i--) {
    const w = walls[i];
    w.d += dt / w.T;
    if (!w.judged && w.d >= 1) judge(w, now);
    if (w.d > 1.08) walls.splice(i, 1);
  }
}

/** 벽이 공 자리에 옴: 트랙마다 통과/탈락 */
function judge(w, now) {
  w.judged = true;
  let anyOut = false;
  for (const p of players) {
    if (p.ball === 'out') continue;
    if (inHole(p, w.holes[p.lane])) {
      p.passed++;
      celebrate(p, w.holes[p.lane], now);
    } else { eliminate(p, now); anyOut = true; }
  }
  if (players.some(p => p.ball !== 'out')) {
    sfx.pass(w.n);
    if (w.n % 5 === 0 && !anyOut) { sound.say(`벽 ${w.n}개 통과! 조금 더 빨라져요`, 1); }
  }
  if (players.length && players.every(p => p.ball === 'out')) finish(now);
}

/** 통과 효과: 별·색종이 폭죽 + 겹겹 고리 + 레일 빛기둥 + 구멍 테두리 번쩍 + 큰 칭찬 글자 */
const CHEERS = ['통과!', '좋아요!', '최고!', '멋져요!', '쏙!'];
function celebrate(p, h, now) {
  const r = ballR(), cols = [p.color, '#ffffff', '#ffd43b', shade(p.color, 0.45)];
  for (let i = 0; i < 70; i++) {
    const a = Math.random() * Math.PI * 2, sp = 180 + Math.random() * 620;
    particles.push({ x: p.x, y: p.y, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - 260, r: 3 + Math.random() * 6,
      c: cols[i % cols.length], life: 0, max: 0.7 + Math.random() * 0.7,
      kind: i % 3 === 0 ? 'star' : i % 3 === 1 ? 'confetti' : 'dot', spin: (Math.random() - 0.5) * 14, a: Math.random() * 6 });
  }
  for (const [k, color] of [[0, p.color], [0.08, '#ffffff'], [0.16, '#ffd43b'], [0.26, p.color]])
    rings.push({ x: p.x, y: p.y, color, t0: now + k, r: r * 1.1, grow: 3.2, dur: 0.7 });
  beams.push({ lane: p.lane, color: p.color, t0: now });
  holeFlashes.push({ ...h, color: p.color, t0: now });
  const streak = p.passed;
  pops.push({ x: p.x, y: p.y - r * 1.8, text: `+1 ${CHEERS[Math.floor(Math.random() * CHEERS.length)]}`, color: '#ffffff', glow: p.color, t0: now, big: 1.25, dur: 1.1 });
  if (streak > 1 && streak % 5 === 0) pops.push({ x: laneX(p.lane), y: wallTop() + r * 2, text: `🔥 ${streak}개 연속!`, color: '#ffd43b', glow: '#ff922b', t0: now + 0.15, big: 1.15, dur: 1.4 });
}

/** 공이 구멍 안인지 (공을 잡고 있을 때만, 테두리에 공 반지름의 RULE.edgeGrace 만큼 걸쳐도 봐줌) */
function inHole(p, h) {
  const r = ballR(), room = r * (1 - RULE.edgeGrace);
  return p.ball === 'held' && Math.hypot(p.x - h.cx, p.y - h.cy) <= h.R - room;
}

function eliminate(p, now) {
  p.ball = 'out'; p.outAt = now; p.flash = now; p.vy = -350;
  grayLanes();
  const x = p.x, y = p.y, r = ballR();
  for (let i = 0; i < 40; i++) {
    const a = Math.random() * Math.PI * 2, sp = 120 + Math.random() * 480;
    particles.push({ x, y, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - 120, r: 2 + Math.random() * 5, c: i % 3 ? p.color : '#d9a066', life: 0, max: 0.5 + Math.random() * 0.5 });
  }
  pops.push({ x, y: y - r * 1.6, text: '쿵!', color: '#ff8787', t0: now, big: 1.3 });
  shake = 0.45;
  sfx.crash();
  const left = players.filter(q => q.ball !== 'out').length;
  sound.say(left ? `${p.name}님 탈락! 벽 ${p.passed}개 통과했어요` : `벽 ${p.passed}개 통과! 잘하셨어요`, 1);
}

function finish() {
  setPhase('end');
  setMsg('🏁 모두 끝났어요!');
  sound.fanfare();
}

// ───────── 매 프레임 ─────────
let last = performance.now();
function frame(ms) {
  requestAnimationFrame(frame);
  const dt = Math.min(0.05, (ms - last) / 1000); last = ms;
  const now = ms / 1000;

  layout();
  stage.draw({ players: phase === 'setup' ? [] : players, mirror: MIRROR, others: phase === 'setup' || phase === 'grab', skeleton: settings.skeleton, background: paintScene });

  if (phase === 'grab') joinPlayers(); else if (phase !== 'setup') trackPlayers(players, client);
  if (AUTOPLAY) simPlay(now);

  if (phase === 'grab') updateGrab(now);
  if (phase === 'count') updateCount(now);
  if (phase === 'grab' || phase === 'count' || phase === 'play' || phase === 'end') updateBalls(dt, now);
  if (phase === 'play' || phase === 'end') updateWalls(dt, now);
  if (phase === 'play') {
    const n = walls.filter(w => w.judged).length;
    $('wallNo').innerHTML = `🧱 ${n}개 통과<small>속도 ×${(RULE.firstTime / Math.max(RULE.minTime, game.nextT / RULE.speedUp)).toFixed(1)}</small>`;
    const dropped = players.filter(p => p.ball === 'floor');
    setMsg(dropped.length ? `🤲 ${dropped.map(p => p.name).join(', ')}님, 두 손을 모아 공을 잡으세요` : '손 높이로 공을 구멍에 맞추세요');
  }
  if (phase === 'end' && now - phaseAt > 2.4) showResults();

  step(dt);
  draw(now);
  pointer.update(dt);
  show('pointerHint', phase === 'results' && !pointer.visible && client.bodies.size > 0);
  if (phase === 'setup') updateSeen();
}
requestAnimationFrame(frame);

/** 공 잡기 단계: 들어온 사람이 모두 잡으면(또는 한 명이라도 잡고 오래 기다리면) 3·2·1 */
function updateGrab(now) {
  const here = players.filter(p => p.present);
  const held = here.filter(p => p.ball === 'held');
  $('wallNo').textContent = players.length ? `🤲 ${held.length} / ${here.length}명 공을 잡았어요` : '🙋 트랙 앞에 서 주세요';
  const waited = game.firstHeldAt && now - game.firstHeldAt > RULE.waitAll;
  if (held.length && (held.length === here.length || waited)) {
    setPhase('count');
    setMsg('공을 잡은 채로 기다리세요');
  }
}

function updateCount(now) {
  const k = now - phaseAt;
  if (k < 1.0) { $('count').textContent = '준비!'; return; }
  const n = 3 - Math.floor((k - 1.0) / 0.9);
  if (n > 0) {
    if (n !== game.countShown) { game.countShown = n; $('count').textContent = n; sound.tick(); }
    return;
  }
  sound.go();
  sound.say('벽이 와요! 구멍 높이에 공을 맞추세요', 1);
  setPhase('play');
}

function showResults() {
  setPhase('results');
  const list = [...players].sort((a, b) => (b.passed - a.passed) || (a.index - b.index));
  const rankOf = p => 1 + list.filter(q => q.passed > p.passed).length;   // 같으면 공동 등수
  const top = list[0];
  const tie = list.filter(p => p.passed === top?.passed).length > 1;
  $('praise').textContent = !top ? '다음엔 함께 해요!' : top.passed === 0 ? '구멍 높이를 잘 보고 공을 맞춰 보세요! 💪'
    : list.length === 1 ? `🏆 벽 ${top.passed}개 통과! 대단해요`
    : tie ? `🏆 공동 1등! 벽 ${top.passed}개` : `🏆 ${top.name}님이 벽 ${top.passed}개로 1등!`;
  $('ranking').innerHTML = list.map(p => {
    const r = rankOf(p), medal = ['🥇', '🥈', '🥉'][r - 1] ?? `${r}등`;
    return `<div class="rk${r === 1 ? ' first' : ''}" style="--c:${p.color}"><span class="pos">${medal}</span><span class="nm">${esc(p.name)}</span>`
      + `<span class="cnt">🧱 <b>${p.passed}</b>개</span><span class="etc">${praiseOf(p, r)}</span></div>`;
  }).join('');
  if (top?.passed) sound.say(list.length === 1 ? `벽 ${top.passed}개 통과! 잘하셨어요` : tie ? '공동 일등이에요!' : `${top.name}님이 1등이에요!`, 0.95);
}
const praiseOf = (p, r) => r === 1 ? '끝까지 버텼어요! 👏' : p.passed >= 10 ? '빠른 벽도 척척! 👍' : p.passed >= 3 ? '잘 맞췄어요 😊' : '다음엔 더 잘할 수 있어요 💪';

function updateSeen() {
  const n = Math.min(client.bodies.size, settings.players), el = $('seen');
  const txt = client.connected === false && !SIM ? '⚠ 에이전트에 연결되지 않았어요 (run.bat 실행 확인)'
    : n ? `<b>${n}명</b> 준비됐어요! (트랙 ${settings.players}개 · 공 잡기 전까지 들어와도 돼요)` : `카메라 앞에 서 주세요 · 트랙 <b>${settings.players}개</b> (최대 ${settings.players}명)`;
  if (el.innerHTML !== txt) el.innerHTML = txt;
  el.classList.toggle('warn', !n);
  $('start').disabled = !n;
}

// ───────── 움직임 (효과) ─────────
function step(dt) {
  for (let i = particles.length - 1; i >= 0; i--) {
    const p = particles[i];
    p.life += dt; p.vy += 900 * dt; p.vx *= 0.985; p.x += p.vx * dt; p.y += p.vy * dt; if (p.spin) p.a += p.spin * dt;
    if (p.life > p.max) particles.splice(i, 1);
  }
  shake = Math.max(0, shake - dt);
}

// ───────── 그리기 ─────────
/** 그림자뷰 배경 (게임이 붙임): 해 질 녘 하늘 + 원근 트랙 (사람 그림자는 이 위에 그려짐) */
function paintScene(c, W, H) {
  c.save();
  c.setTransform(W / L.W, 0, 0, H / L.H, 0, 0);
  const sky = c.createLinearGradient(0, 0, 0, L.HY);
  sky.addColorStop(0, '#1d2b4f'); sky.addColorStop(1, '#5a5f8f');
  c.fillStyle = sky; c.fillRect(0, 0, L.W, L.HY + 1);
  const floor = c.createLinearGradient(0, L.HY, 0, L.H);
  floor.addColorStop(0, '#3a3f5c'); floor.addColorStop(1, '#262a3d');
  c.fillStyle = floor; c.fillRect(0, L.HY, L.W, L.H - L.HY);
  // 트랙 (가까운 쪽 → 소실점)
  const n = Math.max(1, lanes.length || settings.players), w = L.TW / n;
  for (let i = 0; i < n; i++) {
    const x0 = L.X0 + w * i, x1 = x0 + w;
    const [ax, ay] = proj(x0, L.FY, 0), [bx, by] = proj(x1, L.FY, 0);
    c.fillStyle = i % 2 ? 'rgba(255,255,255,.05)' : 'rgba(255,255,255,.09)';
    c.beginPath(); c.moveTo(x0, L.FY); c.lineTo(x1, L.FY); c.lineTo(bx, by); c.lineTo(ax, ay); c.closePath(); c.fill();
    const col = lanes[i]?.player?.color;
    if (col) { c.fillStyle = col; c.globalAlpha = 0.35; c.fillRect(x0 + 4, L.FY, w - 8, L.H - L.FY); c.globalAlpha = 1; }
  }
  // 트랙 줄 + 가로 줄무늬 (다가오는 느낌)
  c.strokeStyle = 'rgba(255,255,255,.25)'; c.lineWidth = 2;
  for (let i = 0; i <= n; i++) {
    const x = L.X0 + w * i, [px, py] = proj(x, L.FY, 0);
    c.beginPath(); c.moveTo(x, L.FY); c.lineTo(px, py); c.stroke();
  }
  c.strokeStyle = 'rgba(255,255,255,.07)';
  const t = phase === 'play' ? (performance.now() / 1000 / Math.max(RULE.minTime, game?.nextT ?? 6)) % 1 : 0;
  for (let k = 0; k < 8; k++) {
    const d = ((k + t) / 8);
    const [lx, ly] = proj(L.X0, L.FY, d), [rx] = proj(L.X0 + L.TW, L.FY, d);
    c.beginPath(); c.moveTo(lx, ly); c.lineTo(rx, ly); c.stroke();
  }
  c.restore();
}

function draw(now) {
  const c = fg;
  c.setTransform(L.dpr, 0, 0, L.dpr, 0, 0);
  c.clearRect(0, 0, L.W, L.H);
  if (phase === 'setup' || !lanes.length) return;
  const sx = shake ? (Math.random() - 0.5) * 16 * shake : 0, sy = shake ? (Math.random() - 0.5) * 16 * shake : 0;
  c.save(); c.translate(sx, sy);

  // 멀리 있는 벽부터 (공 자리 앞의 벽)
  const ahead = walls.filter(w => w.d <= 1).sort((a, b) => a.d - b.d);
  for (const w of ahead) drawWall(c, w);
  drawBeams(c, now);
  // 다음 벽의 구멍 자리 안내 + 공
  const next = walls.filter(w => !w.judged).sort((a, b) => b.d - a.d)[0];
  lanes.forEach((ln, i) => {
    const p = ln.player;
    if (next && p && p.ball !== 'out') drawGuide(c, i, next, p, now);
    drawBall(c, i, p, now);
  });
  // 공 자리를 지나간 벽 (흐리게 사라짐)
  for (const w of walls.filter(w => w.d > 1)) drawWall(c, w);
  drawHands(c, now);   // 공 위에: 공을 잡고 있으면 테두리만 (공과 손이 함께 보이게)

  for (const p of particles) {
    c.globalAlpha = Math.max(0, 1 - p.life / p.max);
    c.fillStyle = p.c;
    if (p.kind === 'star') drawStar(c, p.x, p.y, p.r * 1.7, p.a);
    else if (p.kind === 'confetti') { c.save(); c.translate(p.x, p.y); c.rotate(p.a); c.fillRect(-p.r, -p.r * 0.45, p.r * 2, p.r * 0.9); c.restore(); }
    else { c.beginPath(); c.arc(p.x, p.y, p.r, 0, Math.PI * 2); c.fill(); }
  }
  c.globalAlpha = 1;
  for (let i = rings.length - 1; i >= 0; i--) {
    const r = rings[i], k = (now - r.t0) / (r.dur ?? 0.5);
    if (k >= 1) { rings.splice(i, 1); continue; }
    if (k < 0) continue;
    const e = 1 - (1 - k) ** 2;
    c.globalAlpha = 1 - k; c.strokeStyle = r.color; c.lineWidth = 9 * (1 - k) + 2;
    c.shadowColor = r.color; c.shadowBlur = 16;
    c.beginPath(); c.arc(r.x, r.y, r.r * (1 + (r.grow ?? 1.4) * e), 0, Math.PI * 2); c.stroke();
    c.shadowBlur = 0; c.globalAlpha = 1;
  }
  for (let i = pops.length - 1; i >= 0; i--) {
    const p = pops[i], k = (now - p.t0) / (p.dur ?? 0.9);
    if (k >= 1) { pops.splice(i, 1); continue; }
    if (k < 0) continue;
    // 톡 튀어나왔다가 살짝 줄며 위로 떠오름
    const pop = k < 0.12 ? 0.4 + (k / 0.12) * 0.85 : k < 0.22 ? 1.25 - (k - 0.12) / 0.1 * 0.25 : 1;
    const size = ballR() * 1.2 * p.big * pop;
    c.globalAlpha = k > 0.7 ? (1 - k) / 0.3 : 1;
    c.font = `900 ${Math.round(size)}px "Malgun Gothic", sans-serif`; c.textAlign = 'center'; c.textBaseline = 'middle';
    const y = p.y - 60 * k;
    c.lineWidth = size * 0.16; c.strokeStyle = '#0b0e13'; c.lineJoin = 'round'; c.strokeText(p.text, p.x, y);
    if (p.glow) { c.shadowColor = p.glow; c.shadowBlur = 22; }
    c.fillStyle = p.color; c.fillText(p.text, p.x, y);
    c.shadowBlur = 0; c.globalAlpha = 1;
  }
  c.restore();
  lanes.forEach((ln, i) => drawLabel(c, i, ln.player, now));
}

/** 통과 효과: 레일 바닥에서 솟는 빛기둥 + 구멍 테두리가 번쩍 커지며 사라짐 */
function drawBeams(c, now) {
  for (let i = beams.length - 1; i >= 0; i--) {
    const b = beams[i], k = (now - b.t0) / 0.8;
    if (k >= 1) { beams.splice(i, 1); continue; }
    const x0 = laneL(b.lane), w = laneW(), top = L.HY;
    const g = c.createLinearGradient(0, L.FY, 0, top);
    g.addColorStop(0, b.color); g.addColorStop(0.6, shade(b.color, 0.5)); g.addColorStop(1, 'transparent');
    c.save();
    c.globalAlpha = 0.45 * (1 - k) * Math.min(1, k * 8);
    c.globalCompositeOperation = 'lighter';
    c.fillStyle = g; c.fillRect(x0, top, w, L.FY - top);
    c.restore();
  }
  for (let i = holeFlashes.length - 1; i >= 0; i--) {
    const f = holeFlashes[i], k = (now - f.t0) / 0.6;
    if (k >= 1) { holeFlashes.splice(i, 1); continue; }
    const R = f.R * (1 + 0.35 * k);
    c.save();
    c.globalAlpha = 1 - k; c.lineWidth = 10 * (1 - k) + 2; c.strokeStyle = '#ffffff';
    c.shadowColor = f.color; c.shadowBlur = 30;
    c.beginPath(); c.arc(f.cx, f.cy, R, 0, Math.PI * 2); c.stroke();
    c.restore();
  }
}

/** 탈락한 사람의 레일을 흑백으로: 레일 폭(하늘 끝 ~ 이름표 띠)으로 자른 막에
 *  backdrop-filter 를 씌워 그 아래 배경·그림자·벽·공·이름표를 모두 흑백으로 (옆 레일은 그대로) */
const grayBox = document.createElement('div');
grayBox.id = 'grayLanes';
document.body.append(grayBox);
function grayLanes() {
  const out = new Set(phase === 'setup' || phase === 'grab' ? [] : players.filter(p => p.ball === 'out').map(p => p.lane));
  while (grayBox.children.length < lanes.length) grayBox.append(document.createElement('div'));
  [...grayBox.children].forEach((el, i) => {
    const on = out.has(i);
    if (on) Object.assign(el.style, { left: `${laneL(i)}px`, width: `${laneW()}px`, top: `${L.HY}px`, height: `${L.H - L.HY}px` });
    el.classList.toggle('on', on);
  });
}
addEventListener('resize', () => requestAnimationFrame(grayLanes));

/** 벽: 트랙마다 한 칸, 칸마다 구멍 하나 (가까워질수록 진하게, 지나가면 사라짐)
 *  두께: 뒷면을 WALL_DEPTH 만큼 더 멀리(소실점 쪽) 두고, 앞면과 뒷면 사이를 윗면·옆면·구멍 안쪽 면으로 채움.
 *  여러 면이 겹치므로 따로 마련한 캔버스에 불투명하게 그린 뒤 한 번에 투명도를 줘서 얹음 */
const WALL_DEPTH = 0.03;   // 벽 두께 (깊이 단위: 0 = 멀리, 1 = 공 자리)
const wallCv = document.createElement('canvas'), wc = wallCv.getContext('2d');
function drawWall(c, w) {
  const d = w.d, s = scaleAt(Math.min(d, 1.25)), db = d - WALL_DEPTH;
  const alpha = d <= 1 ? 0.5 + 0.42 * d : Math.max(0, 0.92 - (d - 1) * 14);   // 지나간 벽은 금방 사라짐 (화면을 가리지 않게)
  if (alpha <= 0) return;
  if (wallCv.width !== fxCanvas.width || wallCv.height !== fxCanvas.height) { wallCv.width = fxCanvas.width; wallCv.height = fxCanvas.height; }
  const g2 = wc;
  g2.setTransform(L.dpr, 0, 0, L.dpr, 0, 0);
  g2.clearRect(0, 0, L.W, L.H);
  const lw = laneW(), fog = 1 - Math.min(1, d * 1.3);
  const col = (near, far) => mix(near, far, fog);
  const quad = (pts, fill) => { g2.fillStyle = fill; g2.beginPath(); pts.forEach((pt, k) => (k ? g2.lineTo(...pt) : g2.moveTo(...pt))); g2.closePath(); g2.fill(); };
  const seg = i => ({ x0: L.X0 + lw * i + 2, x1: L.X0 + lw * (i + 1) - 2, top: L.FY - L.WH, ...w.holes[i] });
  // 1) 두께 면: 윗면 · 바깥 옆면 · 구멍 안쪽(원통) (앞면이 나중에 덮어서 보일 곳만 남음)
  lanes.forEach((ln, i) => {
    const { x0, x1, top, cx, cy, R } = seg(i);
    const F = (x, y) => proj(x, y, d), B = (x, y) => proj(x, y, db);
    quad([F(x0, top), F(x1, top), B(x1, top), B(x0, top)], col('#e7a77a', '#6a6e92'));             // 윗면 (밝게)
    quad([F(x0, L.FY), F(x0, top), B(x0, top), B(x0, L.FY)], col('#7a4630', '#33364f'));            // 왼쪽 옆면
    quad([F(x1, L.FY), F(x1, top), B(x1, top), B(x1, L.FY)], col('#7a4630', '#33364f'));            // 오른쪽 옆면
    // 구멍 안쪽: 앞 원을 안쪽 벽 색으로 칠하고(아래는 밝게, 위는 어둡게) 뒤 원(소실점 쪽으로 밀림)을 지움
    const [fx, fy] = F(cx, cy), fr = R * scaleAt(d), [bx, by] = B(cx, cy), brr = R * scaleAt(db);
    const g = g2.createLinearGradient(0, fy - fr, 0, fy + fr);
    g.addColorStop(0, col('#2e170e', '#22243a')); g.addColorStop(0.55, col('#5a3222', '#30334d')); g.addColorStop(1, col('#9a6240', '#4a4e70'));
    g2.fillStyle = g; g2.beginPath(); g2.arc(fx, fy, fr, 0, Math.PI * 2); g2.fill();
    g2.save(); g2.globalCompositeOperation = 'destination-out';
    g2.beginPath(); g2.arc(bx, by, brr, 0, Math.PI * 2); g2.fill(); g2.restore();
  });
  // 2) 앞면 (동그란 구멍 뚫림) + 벽돌 줄 + 테두리
  lanes.forEach((ln, i) => {
    const { x0, x1, top, cx, cy, R } = seg(i);
    const out = [[x0, L.FY], [x1, L.FY], [x1, top], [x0, top]].map(([x, y]) => proj(x, y, d));
    const [hx, hy] = proj(cx, cy, d), hr = R * scaleAt(d);
    const path = new Path2D();
    path.moveTo(...out[0]); path.lineTo(...out[1]); path.lineTo(...out[2]); path.lineTo(...out[3]); path.closePath();
    path.moveTo(hx + hr, hy); path.arc(hx, hy, hr, 0, Math.PI * 2);
    const g = g2.createLinearGradient(0, out[3][1], 0, out[0][1]);
    g.addColorStop(0, col('#d08a5a', '#55597a')); g.addColorStop(1, col('#9c5a3c', '#3d4060'));
    g2.fillStyle = g; g2.fill(path, 'evenodd');
    // 벽돌 줄
    g2.save(); g2.clip(path, 'evenodd');
    g2.strokeStyle = 'rgba(40,20,10,.35)'; g2.lineWidth = Math.max(1, 2 * s);
    const rows = 9, t = out[3][1], b = out[0][1], bh = (b - t) / rows;
    for (let r = 1; r < rows; r++) { g2.beginPath(); g2.moveTo(out[0][0], t + r * bh); g2.lineTo(out[1][0], t + r * bh); g2.stroke(); }
    for (let r = 0; r < rows; r++) {
      const bw = (out[1][0] - out[0][0]) / 3, off = r % 2 ? bw / 2 : 0;
      for (let x = out[0][0] + off + bw; x < out[1][0]; x += bw) { g2.beginPath(); g2.moveTo(x, t + r * bh); g2.lineTo(x, t + (r + 1) * bh); g2.stroke(); }
    }
    g2.restore();
    // 구멍 테두리 (밝게) + 벽 모서리
    g2.lineWidth = Math.max(2, 6 * s); g2.strokeStyle = '#fff3d6';
    g2.beginPath(); g2.arc(hx, hy, hr, 0, Math.PI * 2); g2.stroke();
    g2.lineWidth = Math.max(1, 3 * s); g2.strokeStyle = 'rgba(30,15,8,.6)';
    g2.beginPath(); g2.moveTo(...out[0]); g2.lineTo(...out[1]); g2.lineTo(...out[2]); g2.lineTo(...out[3]); g2.closePath(); g2.stroke();
  });
  c.save();
  c.setTransform(1, 0, 0, 1, 0, 0);
  c.globalAlpha = alpha;
  c.drawImage(wallCv, 0, 0);
  c.restore();
}

/** 다음 벽의 구멍이 올 자리를 점선으로 + 공이 벗어나 있으면 공 옆에 구멍 쪽을 가리키는 화살표 */
function drawGuide(c, i, w, p, now) {
  const { cx, cy, R } = w.holes[i];
  const ok = inHole(p, w.holes[i]);
  const k = Math.max(0, Math.min(1, (w.d - 0.15) / 0.5));   // 벽이 가까워질수록 진하게
  c.save();
  c.globalAlpha = 0.25 + 0.55 * k;
  c.setLineDash([12, 9]); c.lineWidth = 4; c.strokeStyle = ok ? '#79e6ba' : p.color;
  c.beginPath(); c.arc(cx, cy, R, 0, Math.PI * 2); c.stroke();
  c.setLineDash([]);
  if (ok) { c.fillStyle = 'rgba(121,230,186,.12)'; c.fill(); }
  if (!ok && p.ball === 'held') {
    const r = ballR(), a = Math.atan2(cy - p.y, cx - p.x), dist = r * (2.1 + 0.15 * Math.sin(now * 8));
    const ax = p.x + Math.cos(a) * dist, ay = p.y + Math.sin(a) * dist;
    c.globalAlpha = 0.6 + 0.4 * k; c.fillStyle = '#ffd43b';
    c.translate(ax, ay); c.rotate(a);
    c.beginPath(); c.moveTo(r * 0.75, 0); c.lineTo(-r * 0.35, -r * 0.7); c.lineTo(-r * 0.35, r * 0.7); c.closePath(); c.fill();
  }
  c.restore();
}

/** 공: 사람 색 공 (빈 트랙은 회색 공이 바닥에) + 바닥 그림자 */
function drawBall(c, i, p, now) {
  const r = ballR(), x = p ? p.x : laneX(i), y = p ? p.y : L.FY - r;
  if (p?.ball === 'out' && y > L.H + r) return;
  const h = clamp((L.FY - r - y) / L.WH, 0, 1);   // 바닥에서 높이 (그림자 크기용)
  const color = p ? p.color : '#868e96';
  c.save();
  c.globalAlpha = p ? (p.ball === 'out' ? 0.5 : 1) : 0.45;
  // 바닥 그림자 (높이 들수록 작고 흐리게)
  c.fillStyle = 'rgba(0,0,0,.35)';
  const k = Math.max(0, 1 - h);
  c.beginPath(); c.ellipse(x, L.FY - 2, r * (0.5 + 0.6 * k), r * 0.22 * (0.5 + 0.6 * k), 0, 0, Math.PI * 2); c.fill();
  if (p?.ball === 'held') { c.shadowColor = color; c.shadowBlur = 24; }
  const g = c.createRadialGradient(x - r * 0.35, y - r * 0.4, r * 0.1, x, y, r);
  g.addColorStop(0, '#ffffff'); g.addColorStop(0.3, color); g.addColorStop(1, shade(color, -0.45));
  c.fillStyle = g; c.beginPath(); c.arc(x, y, r, 0, Math.PI * 2); c.fill();
  c.shadowBlur = 0;
  // 잡기 안내: 바닥의 공 위에 🤲 (깜빡)
  if (p && p.ball === 'floor' && phase !== 'end') {
    c.globalAlpha = 0.6 + 0.4 * Math.sin(now * 5);
    c.font = `${Math.round(r * 1.3)}px sans-serif`; c.textAlign = 'center'; c.textBaseline = 'middle';
    c.fillText('🤲', x, y - r * 2);
    // 잡는 중이면 고리가 차오름
    if (p.near > 0) {
      c.globalAlpha = 1; c.strokeStyle = '#79e6ba'; c.lineWidth = 5;
      c.beginPath(); c.arc(x, y, r * 1.35, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * Math.min(1, p.near / RULE.grabTime)); c.stroke();
    }
  }
  c.restore();
}

/** 손 위치: 사람마다 두 손에 그 사람 색 동그라미 (✋).
 *  두 손이 잡을 만큼 가까우면 두 손을 잇는 선, 공을 잡고 있으면 손 → 공 점선 (내 손이 어느 공을 움직이는지) */
function drawHands(c, now) {
  if (phase === 'results') return;
  const r = Math.max(18, ballR() * 0.55);
  for (const p of players) {
    const b = p.body;
    if (!b || p.ball === 'out') continue;
    const pts = [J.HandLeft, J.HandRight].map(j => (b.conf(j) > 0 ? stage.toScreen(b, j) : null)).filter(Boolean);
    if (!pts.length) continue;
    const hands = readHands(p), close = hands && hands.gap <= RULE.grabGap;
    c.save();
    if (pts.length === 2) {
      const mx = (pts[0].x + pts[1].x) / 2, my = (pts[0].y + pts[1].y) / 2;
      if (close || p.ball === 'held') {   // 두 손 모음
        c.strokeStyle = p.color; c.lineWidth = 6; c.lineCap = 'round'; c.globalAlpha = 0.9;
        c.beginPath(); c.moveTo(pts[0].x, pts[0].y); c.lineTo(pts[1].x, pts[1].y); c.stroke();
      }
      if (p.ball === 'held') {            // 손 → 공
        c.setLineDash([10, 8]); c.lineWidth = 3; c.globalAlpha = 0.7;
        c.beginPath(); c.moveTo(mx, my); c.lineTo(p.x, p.y); c.stroke(); c.setLineDash([]);
      }
    }
    const held = p.ball === 'held';
    for (const pt of pts) {
      c.globalAlpha = 1;
      c.shadowColor = p.color; c.shadowBlur = 18;
      c.beginPath(); c.arc(pt.x, pt.y, r, 0, Math.PI * 2);
      if (!held) { c.fillStyle = 'rgba(10,14,20,.55)'; c.fill(); }
      c.shadowBlur = 0;
      c.strokeStyle = held ? '#ffffff' : p.color; c.lineWidth = held ? 4 : 5; c.stroke();
      if (held) { c.strokeStyle = p.color; c.lineWidth = 2; c.stroke(); }
      c.globalAlpha = held ? 0.55 : 1;
      c.font = `${Math.round(r * 1.15)}px sans-serif`; c.textAlign = 'center'; c.textBaseline = 'middle';
      c.fillText('✋', pt.x, pt.y + r * 0.05);
    }
    c.restore();
  }
}

/** 트랙 맨 앞(발밑) 이름표: 이름 · 통과한 벽 수 (실시간) */
function drawLabel(c, i, p, now) {
  const x = laneX(i), w = laneW() - 12, y = (L.FY + L.H) / 2;
  const fs = Math.round(Math.max(14, Math.min(32, w * 0.13, (L.H - L.FY) * 0.45)));
  c.save();
  c.textAlign = 'center'; c.textBaseline = 'middle';
  const flash = p && now - p.flash < 0.8 ? 1 - (now - p.flash) / 0.8 : 0;
  c.fillStyle = flash ? `rgba(255,80,80,${0.3 + 0.4 * flash})` : 'rgba(5,10,20,.6)';
  c.beginPath(); roundRectPath(c, x - w / 2, y - fs * 0.95, w, fs * 1.9, 12); c.fill();
  if (!p) {
    c.fillStyle = 'rgba(255,255,255,.4)'; c.font = `700 ${Math.round(fs * 0.75)}px "Malgun Gothic", sans-serif`;
    c.fillText('빈 자리', x, y, w - 10);
  } else {
    c.fillStyle = p.color; c.font = `800 ${Math.round(fs * 0.8)}px "Malgun Gothic", sans-serif`;
    c.fillText(p.name, x - w * 0.18, y, w * 0.55);
    c.fillStyle = p.ball === 'out' ? '#adb5bd' : '#fff'; c.font = `900 ${fs}px "Malgun Gothic", sans-serif`;
    c.fillText(p.ball === 'out' ? `탈락 ${p.passed}` : `🧱 ${p.passed}`, x + w * 0.25, y, w * 0.42);
  }
  c.restore();
}

function drawStar(c, x, y, r, a = 0) {
  c.beginPath();
  for (let i = 0; i < 10; i++) {
    const t = a - Math.PI / 2 + i * Math.PI / 5, rr = i % 2 ? r * 0.45 : r;
    i ? c.lineTo(x + Math.cos(t) * rr, y + Math.sin(t) * rr) : c.moveTo(x + Math.cos(t) * rr, y + Math.sin(t) * rr);
  }
  c.closePath(); c.fill();
}

function roundRectPath(c, x, y, w, h, r) {
  r = Math.max(0, Math.min(r, w / 2, h / 2));
  c.moveTo(x + r, y); c.arcTo(x + w, y, x + w, y + h, r); c.arcTo(x + w, y + h, x, y + h, r);
  c.arcTo(x, y + h, x, y, r); c.arcTo(x, y, x + w, y, r); c.closePath();
}

function shade(color, amt) {
  const m = /^#?([0-9a-f]{6})$/i.exec(color);
  if (!m) return color;
  const n = parseInt(m[1], 16);
  const f = v => Math.max(0, Math.min(255, Math.round(amt < 0 ? v * (1 + amt) : v + (255 - v) * amt)));
  return `rgb(${f(n >> 16)}, ${f((n >> 8) & 255)}, ${f(n & 255)})`;
}
function mix(a, b, k) {
  const p = s => [1, 3, 5].map(i => parseInt(s.slice(i, i + 2), 16));
  const A = p(a), B = p(b);
  return `rgb(${A.map((v, i) => Math.round(v + (B[i] - v) * k)).join(',')})`;
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
  whoosh() { this.burst({ dur: 0.6, type: 'bandpass', f0: 200, f1: 700, q: 1.2, gain: 0.07 }); },
  grab() { sound.tone([523, 784], 0.08, 'triangle', 0.14); },
  drop() { sound.tone([392, 262], 0.12, 'sine', 0.12); },
  // 통과: 맑은 "띵" — 벽을 지날수록 음이 조금씩 올라감
  pass(n) {
    const s = [523, 587, 659, 784, 880, 988, 1047], f = s[n % s.length];
    sound.tone([f, f * 1.25, f * 1.5, f * 2], 0.07, 'triangle', 0.14);   // 짠~ 올라가는 화음
    this.burst({ dur: 0.35, type: 'highpass', f0: 5000, f1: 8000, gain: 0.05 });   // 반짝 소리
  },
  crash() { this.burst({ dur: 0.5, type: 'lowpass', f0: 1200, f1: 80, q: 0.8, gain: 0.6 }); sound.tone([110, 70], 0.2, 'sine', 0.3); },
};

// ───────── 소리 허용 ─────────
function checkSound() { show('soundBlocked', sound.blocked()); }
$('soundBlocked').onclick = () => { sound.unlock(); checkSound(); };
addEventListener('pointerdown', () => { sound.unlock(); checkSound(); });
setTimeout(checkSound, 1500);

// ───────── 시험용 (카메라 없이) ─────────
/** ?sim=N&autoplay=1: 가상 플레이어가 공을 잡고, 화면을 보며 두 손을 다음 구멍 쪽으로 조금씩 옮김.
 *  벽이 빨라질수록(그리고 뒤 번호일수록) 겨누는 자리가 부정확해져서 하나씩 탈락 */
const simAim = new Map();
function simPlay(now) {
  for (let i = 0; i < SIM; i++) {
    const s = client.sim.players[i];
    if (phase === 'setup' || phase === 'results') { client.sim.hold(i, null); s.hx = 0; s.hy = -0.1; continue; }
    s.hx ??= 0; s.hy ??= -0.1;
    const p = players.find(q => q.bodyId === s.id), hands = p && readHands(p), b = p?.body;
    const next = walls.filter(w => !w.judged).sort((a, b) => b.d - a.d)[0];
    if (p && hands && b && next && next.d > 0.2 && phase === 'play') {
      const h = next.holes[p.lane], key = `${i}:${next.n}`;
      if (!simAim.has(key)) {
        const err = 0.3 + 1.4 * (RULE.firstTime / next.T - 1) * (1 + i * 0.6);
        const a = Math.random() * Math.PI * 2, m = Math.random() * err * h.R;
        simAim.set(key, { x: Math.cos(a) * m, y: Math.sin(a) * m });
      }
      const aim = simAim.get(key);
      // 화면 px ↔ m: 두 어깨 사이로 잼
      const a = stage.toScreen(b, J.ShoulderLeft), c = stage.toScreen(b, J.ShoulderRight);
      const pxPerM = a && c ? Math.max(50, Math.abs(a.x - c.x) / Math.max(0.1, Math.abs(b.pos(J.ShoulderLeft).x - b.pos(J.ShoulderRight).x))) : 300;
      const ex = h.cx + aim.x - hands.x, ey = h.cy + aim.y - hands.y;
      s.hx = clamp(s.hx - clamp(ex / pxPerM * 0.08, -0.02, 0.02), -0.7, 0.7);   // 거울: 화면 오른쪽 = 카메라 x 음수
      s.hy = clamp(s.hy - clamp(ey / pxPerM * 0.08, -0.02, 0.02), -0.55, 0.5);
    }
    client.sim.hold(i, { dx: s.hx, dy: s.hy });
  }
}
if (SIM) {
  window.__wall = () => ({ phase, lanes: lanes.length, walls: walls.length, n: game?.n ?? 0,
    players: players.map(p => [p.name, p.lane, p.ball, p.passed, Math.round(p.x), Math.round(p.y)]) });
  window.__wallStart = () => startGame();
  window.__wallD = () => walls.filter(w => !w.judged).sort((a, b) => b.d - a.d)[0]?.d;   // 시험용: 가장 가까운 벽이 얼마나 왔는지
}
setPhase('setup');
