// ─────────────────────────────────────────────────────────────
//  사진 깨기
//
//  흐름: 설정(깰 사진들 고르기, 손으로도 고름) → 던지기(사진이 조금씩 깨짐) → 한 장 다 깨면 다음 사진 → 모두 깨면 결과
//  - 던지기: 손을 뒤로 뺐다가 카메라 쪽으로 휙 (_shared/throw.js). 오른쪽에서 던지면 사진 오른쪽에 맞음
//  - 던지는 것(공·토마토·달걀)은 던질 때마다 무작위
//  - 최대 인원(공통 설정, 기본 4명)까지 함께 던짐. 적어도 진행, 게임 중에 들어와도 자리가 남으면 참여
//    던진 물건·맞은 자리는 그 사람 색(P1 빨강, P2 파랑 …). 최대 인원 밖의 사람은 던져도 무시
//  - 공통 틀(game-shell.js): 헤더 = 게임 이름, 푸터 = 게임 다시 시작·스켈레톤·게임 나가기, 최대 인원 = 게임 고르기 화면
//  - 캐릭터 타입: 그림자뷰 (GAME_RULEBOOK.md) — 사진 위에 사람이 번진 그림자로 겹쳐 보임 (영상은 안 보임)
//    그리는 순서: 사진판 → 사람 그림자 → 날아가는 것·조각·"쾅!" (그림자가 효과를 가리지 않게)
//  - 사진판이 화면 전체, 위에는 대기 중인 사진 줄
//  - 휴대폰 사진: QR 코드로 up/ 페이지 (games/smash/up/) → 에이전트가 잠시 보관 → 여기서 가져감 (게임 중에도 계속 받음)
//  - 사진은 이 PC 브라우저에만 보관 (photos.js)
//
//  카메라 없이 시험: ?sim=1&autothrow=1 (가상 플레이어가 계속 던짐), 게임 중 Space 키 = 가운데로 한 번 던지기
// ─────────────────────────────────────────────────────────────
import { FemtoClient, J } from '../_shared/femto-client.js';
import { Sound } from '../_shared/sound.js';
import { HandPointer } from '../_shared/hand-pointer.js';
import { ThrowDetector } from '../_shared/throw.js';
import { Stage } from '../_shared/video-layer.js';
import { Player, pickBodies, trackPlayers } from '../_shared/players.js';
import { GameShell, settings } from '../_shared/game-shell.js';
import { PhotoBoard } from './board.js';
import { listPhotos, addImageFile, addText, removePhoto, ensureDefaults, loadImage } from './photos.js';

const $ = id => document.getElementById(id);
const params = new URLSearchParams(location.search);
const SIM = Math.max(0, Math.min(4, parseInt(params.get('sim') || '0', 10) || 0));
const AUTO_THROW = SIM && params.get('autothrow') === '1';

const OBJECTS = {
  ball: { icon: '⚾', name: '공', words: ['쾅!', '와장창!', '쨍그랑!'] },
  tomato: { icon: '🍅', name: '토마토', words: ['퍽!', '철퍽!'] },
  egg: { icon: '🥚', name: '달걀', words: ['퍽!', '철썩!'] },
};

const client = new FemtoClient({ sim: SIM });   // 그림자뷰: 영상은 안 보이지만 사람 모양(마스크)이 영상과 함께 옴
const people = new Stage($('people'), client, { character: 'shadow' });
const sound = new Sound();
const det = new ThrowDetector({ gentle: true, mirror: true });
const pointer = new HandPointer({ client, onSelect: () => sound.success() });
// 공통 틀: 헤더(게임 이름)·푸터(게임 다시 시작·스켈레톤·게임 나가기). 최대 참여 인원은 게임 고르기 화면에서 정함
const shell = new GameShell({ name: '사진 깨기', icon: '📸', sim: SIM, onHome: () => { sound.stop(); setPhase('setup'); } });

// ───────── 설정 저장 ─────────
// skip: 게임에서 뺀 사진 id (나머지는 모두 순서대로 나옴)
const opts = { power: 'gentle', skip: [] };
try { Object.assign(opts, JSON.parse(localStorage.getItem('femto.smash.opts') || '{}')); } catch {}
if (!Array.isArray(opts.skip)) opts.skip = [];
const saveOpts = () => { try { localStorage.setItem('femto.smash.opts', JSON.stringify(opts)); } catch {} };

function bindChoice(id, key) {
  const box = $(id);
  const render = () => box.querySelectorAll('button').forEach(b => b.classList.toggle('on', b.dataset.v === opts[key]));
  box.addEventListener('click', e => {
    const b = e.target.closest('button');
    if (!b) return;
    opts[key] = b.dataset.v; saveOpts(); render();
  });
  render();
}
bindChoice('optPower', 'power');

// ───────── 사진 고르기 (여러 장, 번호 순서대로 나옴) ─────────
let photos = [];
const included = () => photos.filter(p => !opts.skip.includes(p.id));
async function refreshGallery(newIds = []) {
  photos = await listPhotos();
  opts.skip = opts.skip.filter(id => photos.some(p => p.id === id));   // 지운 사진 정리
  const g = $('gallery');
  g.querySelectorAll('.ph').forEach(e => e.remove());
  let no = 0;
  for (const p of photos) {
    const on = !opts.skip.includes(p.id);
    const b = document.createElement('div');
    b.className = 'ph hand-target' + (on ? ' on' : '') + (newIds.includes(p.id) ? ' new' : '');
    b.style.backgroundImage = `url(${p.thumb})`;
    b.title = p.name + (on ? '' : ' (빠짐 · 누르면 넣기)');
    b.tabIndex = 0;
    b.innerHTML = `<span class="no">${on ? ++no : ''}</span><button class="del" title="이 사진 지우기">✕</button>`
      + (p.from ? `<span class="cap">📱 ${esc(p.from)}</span>` : '');
    b.onclick = e => {
      if (e.target.classList.contains('del')) { removePhoto(p.id).then(() => refreshGallery()); return; }
      opts.skip = on ? [...opts.skip, p.id] : opts.skip.filter(id => id !== p.id);
      saveOpts(); refreshGallery();
    };
    g.appendChild(b);
  }
  const n = included().length;
  $('start').disabled = !n;
  $('start').textContent = n > 1 ? `시작하기 ▶ (사진 ${n}장)` : '시작하기 ▶';
  saveOpts();
}

function showErr(msg) { $('err').textContent = msg || ''; }

async function addFiles(files) {
  showErr('');
  const added = [];
  for (const f of files) {
    try { added.push(await addImageFile(f)); } catch (e) { showErr(`${f.name}: ${e.message}`); }
  }
  if (added.length) await photosArrived(added);
}

/** 새 사진이 들어옴 (이 PC에서 올리거나 휴대폰에서 옴) → 목록 갱신, 게임 중이면 대기 줄 뒤에 붙임 */
async function photosArrived(added) {
  await refreshGallery(added.map(p => p.id));
  sound.success();
  if (game && phase === 'play') {
    for (const p of added) if (!game.queue.some(q => q.id === p.id)) { game.queue.push(p); game.fresh.add(p.id); }
    renderQueue();
  }
}

// ───────── 휴대폰 사진 받기 (QR) ─────────
let toastTimer = 0;
function toast(text) {
  $('toast').textContent = text;
  $('toast').classList.remove('hidden');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $('toast').classList.add('hidden'), 3500);
}

async function setupQr() {
  try {
    const info = await (await fetch('/api/smash/up/info')).json();
    if (!info.url) return;
    const src = '/api/smash/up/qr.svg?' + Date.now();
    $('qrImg').src = src; $('qrMiniImg').src = src;
    $('qrUrl').textContent = info.url.replace(/\?.*$/, '');
    qrReady = true;
    show('qrBox', true);
    show('qrMini', phase !== 'setup');
  } catch { /* 에이전트 없이 연 경우: QR 없음 */ }
}
let qrReady = false;

let inboxBusy = false;
async function pollInbox() {
  if (inboxBusy || !qrReady) return;
  inboxBusy = true;
  try {
    const list = await (await fetch('/api/smash/up/list', { cache: 'no-store' })).json();
    const added = [], errs = [];
    for (const it of list) {
      try {
        const blob = await (await fetch(`/api/smash/up/${it.seq}`, { cache: 'no-store' })).blob();
        added.push(await addImageFile(blob, it.name || '휴대폰 사진', it.from || ''));
      } catch (e) { errs.push(`${it.name}: ${e.message}`); }
      fetch(`/api/smash/up/${it.seq}`, { method: 'DELETE' }).catch(() => {});   // 성공·실패 모두 지움 (같은 사진이 계속 들어오지 않게)
    }
    if (added.length) {
      const who = [...new Set(added.map(p => p.from).filter(Boolean))];
      toast(`📱 ${who.length ? who.join(', ') + '님 ' : ''}사진 ${added.length}장이 왔어요!`);
      await photosArrived(added);
    }
    if (errs.length && phase === 'setup') showErr(errs.join(' / '));
  } catch { /* 잠깐 끊김: 다음에 다시 */ }
  inboxBusy = false;
}
setupQr();
setInterval(pollInbox, 1500);

const addPhotoDialog = $('addPhotoDialog');
$('addPhoto').onclick = () => {
  $('textRow').classList.add('hidden');
  $('textBtn').setAttribute('aria-expanded', 'false');
  $('textErr').textContent = '';
  addPhotoDialog.showModal();
  addPhotoDialog.append(pointer.el);
};
$('closeAddPhoto').onclick = () => addPhotoDialog.close();
addPhotoDialog.onclose = () => {
  if (!addPhotoDialog.open) document.body.append(pointer.el);
};
$('upload').onclick = () => $('file').click();
function addSelectedFiles(files) {
  if (!files.length) return;
  if (addPhotoDialog.open) addPhotoDialog.close();
  addFiles(files);
}
$('file').onchange = e => { addSelectedFiles([...e.target.files]); e.target.value = ''; };
$('textBtn').onclick = () => {
  $('textRow').classList.remove('hidden');
  $('textBtn').setAttribute('aria-expanded', 'true');
  $('textIn').focus();
};
$('textRow').onsubmit = async e => {
  e.preventDefault();
  const submit = $('textMake');
  if (submit.disabled) return;
  submit.disabled = true;
  $('textErr').textContent = '';
  showErr('');
  try {
    const p = await addText($('textIn').value);
    await photosArrived([p]);
    $('textIn').value = '';
    if (addPhotoDialog.open) addPhotoDialog.close();
  } catch (err) {
    if (addPhotoDialog.open) $('textErr').textContent = err.message;
    else showErr(err.message);
  } finally {
    submit.disabled = false;
  }
};
$('textIn').onkeydown = e => {
  if (e.key.startsWith('Arrow')) e.stopPropagation();
};
$('textIn').oninput = () => { $('textErr').textContent = ''; };
// 끌어다 놓기·붙여넣기
addEventListener('dragover', e => { if (phase !== 'setup') return; e.preventDefault(); $('dropHint').classList.remove('hidden'); });
addEventListener('dragleave', e => { if (!e.relatedTarget) $('dropHint').classList.add('hidden'); });
addEventListener('drop', e => {
  e.preventDefault(); $('dropHint').classList.add('hidden');
  if (phase === 'setup') addSelectedFiles([...e.dataTransfer.files].filter(f => f.type.startsWith('image/')));
});
addEventListener('paste', e => {
  if (phase !== 'setup') return;
  const files = [...e.clipboardData.files].filter(f => f.type.startsWith('image/'));
  if (files.length) { e.preventDefault(); addSelectedFiles(files); }
});
ensureDefaults().then(() => refreshGallery());

// ───────── 상태 ─────────
let phase = 'setup';    // setup | play | done | results
let board = null, boardImg = null;
let game = null;
let phaseStart = 0;
const projectiles = [], frags = [], particles = [], pops = [], rings = [];
let shake = 0;
// 참가자: 최대 인원(공통 설정)까지. 게임 중에 들어온 사람도 자리가 남으면 참여 (화면 왼쪽부터 P1 …)
let players = [];   // Player (players.js) + throws

function show(id, on) { $(id).classList.toggle('hidden', !on); }
function setPhase(p) {
  phase = p;
  phaseStart = performance.now() / 1000;
  show('setup', p === 'setup');
  show('guide', p === 'play' || p === 'done');
  show('results', p === 'results');
  show('cardsL', p !== 'setup'); show('cardsR', p !== 'setup');
  show('queueBar', p === 'play' || p === 'done');
  show('qrMini', qrReady && p !== 'setup');   // 게임 중에도 QR로 사진 계속 받기
  if (p === 'setup') refreshGallery();
  pointer.enabled = p === 'setup' || p === 'results';
  if (!pointer.enabled) pointer.hide();
}

$('start').onclick = startGame;
$('again').onclick = () => startGame();
$('toSetup').onclick = () => { sound.stop(); setPhase('setup'); };
$('toGames').onclick = () => setTimeout(() => (location.href = shell.exitUrl), 500);
addEventListener('keydown', e => {
  if (e.code === 'Escape' && phase !== 'setup') { sound.stop(); setPhase('setup'); }
  if (e.code === 'Space' && phase === 'play') { e.preventDefault(); testThrow(); }
});

/** 시작: 고른 사진을 번호 순서대로 한 장씩. 게임 중 휴대폰으로 온 사진은 줄 뒤에 붙음 */
async function startGame() {
  await refreshGallery();
  const list = included();
  if (!list.length) { showErr('사진을 먼저 골라 주세요'); return; }
  sound.unlock();
  det.setGentle(opts.power === 'gentle');
  players = [];
  joinPlayers();
  const now = performance.now() / 1000;
  game = { t0: now, throws: 0, best: 0, lastThrow: now, lastHint: now, said: new Set(), finishedAt: 0,
           queue: [...list], idx: -1, cleared: 0, nextAt: 0, fresh: new Set(), lastObj: null };
  if (!await loadPhoto(0)) return;
  setPhase('play');
  setMsg('팔을 뒤로 했다가 앞으로 휙! 💪', list.length > 1 ? '한 장 다 깨면 다음 사진이 나와요' :'사진을 향해 공을 던지듯이');
  sound.say('사진을 향해, 공을 던지듯이 팔을 앞으로 휙 던져 보세요', 0.92);
}

/** i번째 사진을 사진판에 올림 (못 여는 사진은 건너뜀) */
async function loadPhoto(i) {
  const g = game;
  for (; i < g.queue.length; i++) {
    try {
      const img = await loadImage(g.queue[i].data);
      if (game !== g) return false;   // 그사이 그만두거나 다시 시작
      boardImg = img;
      board = new PhotoBoard(boardImg);
      g.idx = i; g.nextAt = 0; g.said.clear();
      projectiles.length = frags.length = particles.length = pops.length = rings.length = 0;
      updateMeter(); renderQueue();
      return true;
    } catch (e) { showErr(e.message); }
  }
  return false;
}

/** 게임 화면 위: 대기 중인 사진 (지금 사진 다음부터) */
let queueKey = '';
function renderQueue() {
  if (!game) return;
  const rest = game.queue.slice(game.idx + 1);
  $('photoNo').textContent = `사진 ${game.idx + 1}/${game.queue.length}`;
  $('queueCount').textContent = rest.length;
  const key = rest.map(p => p.id).join(',');
  if (key === queueKey) return;
  queueKey = key;
  const MAX = 12;
  $('queueList').innerHTML = rest.length
    ? rest.slice(0, MAX).map((p, k) => `<div class="q${game.fresh.has(p.id) ? ' new' : ''}" style="background-image:url(${p.thumb})"><i>${game.idx + 2 + k}</i>${p.from ? `<span>📱 ${esc(p.from)}</span>` : ''}</div>`).join('')
      + (rest.length > MAX ? `<div class="more">+${rest.length - MAX}</div>` : '')
    : `<div class="empty">${qrReady ? '대기 중인 사진이 없어요 · 오른쪽 위 QR로 휴대폰 사진을 보내면 여기에 줄을 서요 📱' : '이 사진이 마지막이에요'}</div>`;
  game.fresh.clear();
}

function setMsg(msg, sub) {
  if ($('msg').textContent !== msg) $('msg').textContent = msg;
  if (sub != null && $('sub').textContent !== sub) $('sub').textContent = sub;
}

function updateMeter() {
  const broken = Math.round((1 - board.remain) * 100);
  const shown = board.done ? 100 : Math.min(99, Math.round(broken / (1 - 0.12)));   // 88% 깨면 다 깬 것
  $('bar').style.width = shown + '%';
  $('pct').textContent = shown + '%';
  return shown;
}

// ───────── 화면 배치 ─────────
const fx = $('fx'), fg = fx.getContext('2d');
let L = null;   // { W, H, dpr, board:{x,y,w,h,s}, fr }

function layout() {
  const dpr = Math.min(devicePixelRatio || 1, 2);
  const fr = fx.getBoundingClientRect();
  if (fx.width !== Math.round(fr.width * dpr) || fx.height !== Math.round(fr.height * dpr)) {
    fx.width = Math.round(fr.width * dpr); fx.height = Math.round(fr.height * dpr);
  }
  // 사진판: 아래 칸 전체의 가운데 (사람 모습은 보이지 않음)
  const areaX = Math.min(20, fr.width * 0.04), areaY = Math.min(16, fr.height * 0.05);
  const areaW = fr.width - 2 * areaX, areaH = fr.height - 2 * areaY;
  let b = null;
  if (board) {
    const s = Math.min(areaW * 0.96 / board.W, areaH * 0.94 / board.H);
    const w = board.W * s, h = board.H * s;
    b = { x: areaX + (areaW - w) / 2, y: areaY + (areaH - h) / 2, w, h, s };
  }
  L = { W: fr.width, H: fr.height, dpr, board: b, fr };
}

/** 사진 픽셀 → 화면(fx) 좌표 */
const toFx = (x, y) => ({ x: L.board.x + x * L.board.s, y: L.board.y + y * L.board.s });

// ───────── 던지기 ─────────
/** 빈자리가 있으면 보이는 사람을 참가자로 (가까운 사람 우선, 화면 왼쪽부터 번호) */
function joinPlayers() {
  trackPlayers(players, client);
  const free = settings.players - players.length;
  if (free <= 0) return;
  const taken = new Set(players.map(p => p.bodyId));
  const fresh = pickBodies({ bodies: new Map([...client.bodies].filter(([id]) => !taken.has(id))) }, free, true);
  for (const b of fresh) {
    const p = new Player(players.length, b, client);
    p.throws = 0;
    players.push(p);
  }
}
const playerOf = body => players.find(p => p.present && p.bodyId === body.id);

function launch({ body, handJoint, power, aim }) {
  if (!board || board.done) return;
  const pl = body ? playerOf(body) : null;
  if (body && !pl) return;   // 참가자가 아닌 사람 (최대 인원을 넘은 사람)
  const now = performance.now() / 1000;
  // 던지는 것은 매번 무작위 (바로 전과 같은 게 연달아 나오지 않게)
  const keys = Object.keys(OBJECTS).filter(k => k !== game.lastObj);
  const objKey = keys[Math.floor(Math.random() * keys.length)];
  game.lastObj = objKey;
  if (SIM) (window.__objs ??= {})[objKey] = (window.__objs[objKey] || 0) + 1;
  const target = board.aimPoint(aim.nx, aim.ny);
  // 출발: 화면 아래 가장자리, 던진 사람이 서 있는 쪽 (거울처럼: 오른쪽에 선 사람 = 화면 오른쪽)
  let fx0 = 0.5;
  if (body) {
    const h = body.conf(handJoint) > 0 ? body.pos(handJoint) : body.pos(J.SpineChest);
    fx0 = Math.max(0.08, Math.min(0.92, 0.5 - h.x / 2.4));
  }
  const from = { x: fx0 * L.W, y: L.H + 40 };
  const to = toFx(target.x, target.y);
  const color = pl ? pl.color : '#ffd23f';
  projectiles.push({ from, to, t0: now, dur: 0.55 - power * 0.12, obj: objKey, power, color, tx: target.x, ty: target.y, spin: (Math.random() - 0.5) * 14 });
  game.throws++;
  game.best = Math.max(game.best, power);
  game.lastThrow = now;
  if (pl) pl.throws++;
  sfx.whoosh(power);
}

function testThrow() {
  const body = players.find(p => p.present)?.body;
  launch({ body, handJoint: J.HandRight, power: 0.6, aim: { nx: 0.3 + Math.random() * 0.4, ny: 0.3 + Math.random() * 0.4 } });
}

function impact(p) {
  const pieces = board.hit(p.tx, p.ty, p.power, p.obj);
  const at = toFx(p.tx, p.ty), s = L.board.s;
  for (const f of pieces) {
    const c = toFx(f.cx, f.cy);
    let dx = c.x - at.x, dy = c.y - at.y;
    const d = Math.hypot(dx, dy) || 1; dx /= d; dy /= d;
    const sp = (120 + Math.random() * 260) * (0.6 + p.power);
    frags.push({ f, x: c.x, y: c.y, vx: dx * sp, vy: dy * sp - 120 - Math.random() * 160, a: 0, va: (Math.random() - 0.5) * 8, s, life: 0 });
  }
  const col = p.obj === 'tomato' ? ['#d62828', '#ff6b4a'] : p.obj === 'egg' ? ['#ffd23f', '#fffbe8'] : ['#ffffff', '#c9d2e0'];
  for (let i = 0; i < 18 + p.power * 20; i++) {
    const a = Math.random() * Math.PI * 2, sp = 100 + Math.random() * 380;
    particles.push({ x: at.x, y: at.y, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - 100, r: 2 + Math.random() * 5, c: col[i % 2], life: 0, max: 0.5 + Math.random() * 0.5 });
  }
  const words = OBJECTS[p.obj].words;
  rings.push({ x: at.x, y: at.y, color: p.color, t0: performance.now() / 1000, big: p.power });   // 누가 맞혔는지: 그 사람 색 고리
  pops.push({ x: at.x, y: at.y, color: p.color, text: pieces.length >= 8 ? words.at(-1) : words[Math.floor(Math.random() * (words.length - 1 || 1))], t0: performance.now() / 1000, big: p.power });
  shake = 0.18 + p.power * 0.2;
  sfx.impact(p.obj, p.power);
  sfx.shatter(Math.min(12, pieces.length));

  const pct = updateMeter();
  // 응원 (한 번씩)
  for (const [m, text] of [[25, '좋아요! 계속 던져요'], [50, '반이나 깼어요!'], [75, '거의 다 왔어요!']]) {
    if (pct >= m && !game.said.has(m)) { game.said.add(m); setMsg(text + ' 👏'); sound.say(text, 0.95); break; }
  }
  if (board.done) finale();
}

function finale() {
  if (phase !== 'play') return;
  game.cleared++;
  const more = game.idx + 1 < game.queue.length;
  if (!more) setPhase('done');
  const center = toFx(board.W / 2, board.H / 2);
  for (const f of board.explodeAll()) {
    const c = toFx(f.cx, f.cy);
    let dx = c.x - center.x, dy = c.y - center.y;
    const d = Math.hypot(dx, dy) || 1;
    const sp = 250 + Math.random() * 450;
    frags.push({ f, x: c.x, y: c.y, vx: dx / d * sp, vy: dy / d * sp - 250, a: 0, va: (Math.random() - 0.5) * 10, s: L.board.s, life: 0 });
  }
  updateMeter();
  shake = 0.5;
  sfx.shatter(24);
  setTimeout(() => sound.fanfare(), 250);
  if (more) {
    // 다음 사진: 조각이 날아간 뒤 잠깐 쉬고 나옴
    setMsg(`🎉 ${game.idx + 1}번째 사진 깼어요!`, '다음 사진이 나와요…');
    sound.say('와! 깼어요! 다음 사진이에요', 0.95);
    game.nextAt = performance.now() / 1000 + 2.6;
    return;
  }
  setMsg(game.queue.length > 1 ? `🎉 ${game.queue.length}장 모두 깼어요!` : '🎉 다 깼어요!', '속이 시원하시죠?');
  sound.say(game.queue.length > 1 ? '와! 사진을 모두 깼어요! 속이 시원하시죠?' : '와! 다 깼어요! 속이 시원하시죠?', 0.95);
  game.finishedAt = performance.now() / 1000;
}

async function nextPhoto() {
  game.nextAt = 0;
  if (!await loadPhoto(game.idx + 1)) {   // 남은 사진을 하나도 못 열면 끝
    if (phase === 'play') { setPhase('done'); game.finishedAt = performance.now() / 1000; setMsg('🎉 다 깼어요!', '속이 시원하시죠?'); }
    return;
  }
  setMsg(`📸 ${game.idx + 1}번째 사진!`, game.queue[game.idx].from ? `📱 ${game.queue[game.idx].from}님이 보낸 사진` : '팔을 뒤로 했다가 앞으로 휙!');
  $('msg').classList.remove('pulse'); void $('msg').offsetWidth; $('msg').classList.add('pulse');
  sound.say(`${game.idx + 1}번째 사진이에요`, 0.95);
}

function showResults() {
  setPhase('results');
  const sec = Math.round(game.finishedAt - game.t0);
  $('rPhotos').textContent = game.cleared;
  $('rThrows').textContent = game.throws;
  $('rTime').textContent = `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, '0')}`;
  $('rPower').textContent = Math.round(game.best * 100) + '%';
  $('praise').textContent = game.throws <= 20 ? '힘이 대단하세요! 💪' : '끝까지 잘하셨어요! 👏';
  $('who').innerHTML = players.filter(s => s.throws).map(s => `<span style="--c:${s.color}">${esc(s.name)} <b>${s.throws}</b>번</span>`).join('');
}

// ───────── 매 프레임 ─────────
let lastNow = performance.now();
function frame(nowMs) {
  const dt = Math.min(0.05, (nowMs - lastNow) / 1000);
  lastNow = nowMs;
  const now = nowMs / 1000;
  layout();

  if (phase === 'play' || phase === 'done') joinPlayers();
  if (phase === 'play') {
    for (const t of det.update(client.bodies, now)) launch({ body: client.bodies.get(t.bodyId), ...t });
    const here = client.bodies.size > 0;
    if (game.nextAt) { if (now >= game.nextAt) nextPhoto(); }   // 다음 사진 기다리는 중
    else if (!here) setMsg('카메라 앞에 서 주세요 🙂', '사진이 기다리고 있어요');
    else if (now - game.lastThrow > 9 && now - game.lastHint > 15) {
      game.lastHint = now;
      setMsg('팔을 뒤로 했다가 앞으로 휙! 💪', '살살 던져도 괜찮아요');
      $('msg').classList.remove('pulse'); void $('msg').offsetWidth; $('msg').classList.add('pulse');
      sound.say('팔을 뒤로 했다가, 앞으로 휙 던져 보세요', 0.92);
    }
  } else det.update(client.bodies, now);   // 기록은 계속 (시작하자마자 던져도 되게)

  if (phase === 'done' && now - game.finishedAt > 2.5 && (!sound.busy || now - game.finishedAt > 7) && frags.length < 5) showResults();

  // 날아가는 것
  for (let i = projectiles.length - 1; i >= 0; i--) {
    const p = projectiles[i];
    if (now - p.t0 >= p.dur) { projectiles.splice(i, 1); if (board && !board.done && phase === 'play') impact(p); }
  }
  step(dt);
  draw(now);
  renderCards();
  pointer.update(dt);
  show('pointerHint', phase === 'results' && !pointer.visible && client.bodies.size > 0);
  if (phase === 'setup') updateSeen();
  requestAnimationFrame(frame);
}
requestAnimationFrame(frame);


function step(dt) {
  for (let i = frags.length - 1; i >= 0; i--) {
    const f = frags[i];
    f.life += dt; f.vy += 1500 * dt; f.x += f.vx * dt; f.y += f.vy * dt; f.a += f.va * dt;
    if (f.life > 2.6 || f.y > L.H + 200) frags.splice(i, 1);
  }
  for (let i = particles.length - 1; i >= 0; i--) {
    const p = particles[i];
    p.life += dt; p.vy += 900 * dt; p.x += p.vx * dt; p.y += p.vy * dt;
    if (p.life > p.max) particles.splice(i, 1);
  }
  shake = Math.max(0, shake - dt);
}

function draw(now) {
  const c = fg;
  c.setTransform(L.dpr, 0, 0, L.dpr, 0, 0);
  c.clearRect(0, 0, L.W, L.H);
  if (!board || phase === 'setup') return;
  const b = L.board;
  const sx = shake ? (Math.random() - 0.5) * 18 * shake : 0, sy = shake ? (Math.random() - 0.5) * 18 * shake : 0;
  const effectScale = Math.min(1.25, Math.min(L.W, L.H) / 540);

  // 사진 뒤 벽 (깨진 자리로 보임)
  c.save();
  roundRect(c, b.x - 10, b.y - 10, b.w + 20, b.h + 20, 18);
  const g = c.createLinearGradient(b.x, b.y, b.x + b.w, b.y + b.h);
  g.addColorStop(0, '#20324a'); g.addColorStop(1, '#2c4a3c');
  c.fillStyle = g; c.fill();
  c.clip();
  c.globalAlpha = 0.9;
  c.fillStyle = '#ffffff';
  c.textAlign = 'center'; c.textBaseline = 'middle';
  c.font = `900 ${Math.max(1, Math.round(Math.min(b.h * 0.16, b.w * 0.14)))}px "Malgun Gothic", sans-serif`;
  c.fillText('😊 후련해요!', b.x + b.w / 2, b.y + b.h / 2, b.w * 0.9);
  c.restore();

  // 사진판
  c.drawImage(board.canvas, b.x + sx, b.y + sy, b.w, b.h);

  // 사람 그림자 (그림자뷰): 사진 위, 효과 아래. 글로우 색 = 그 사람 색 (던진 물건·고리와 같은 색)
  // 참가자만 그림자 (최대 인원 밖의 사람은 안 보임). 스켈레톤은 공통 설정
  people.draw({ players, mirror: true, others: false, skeleton: settings.skeleton, background: null });
  c.drawImage(people.canvas, 0, 0, L.W, L.H);

  // 떨어지는 조각
  for (const f of frags) {
    c.save();
    c.globalAlpha = Math.max(0, Math.min(1, (2.6 - f.life) / 0.6));
    c.translate(f.x + sx, f.y + sy); c.rotate(f.a); c.scale(f.s, f.s);
    c.drawImage(f.f.canvas, -f.f.ox, -f.f.oy);
    c.restore();
  }
  // 튀는 가루·즙
  for (const p of particles) {
    c.globalAlpha = Math.max(0, 1 - p.life / p.max);
    c.fillStyle = p.c;
    c.beginPath(); c.arc(p.x, p.y, p.r, 0, Math.PI * 2); c.fill();
  }
  c.globalAlpha = 1;
  // 맞은 자리: 던진 사람 색 고리가 퍼짐
  for (let i = rings.length - 1; i >= 0; i--) {
    const r = rings[i], k = (now - r.t0) / 0.6;
    if (k >= 1) { rings.splice(i, 1); continue; }
    c.save();
    c.globalAlpha = 1 - k;
    c.strokeStyle = r.color; c.lineWidth = 10 * (1 - k) + 2;
    c.shadowColor = r.color; c.shadowBlur = 20;
    c.beginPath(); c.arc(r.x, r.y, 30 + (90 + 80 * r.big) * k, 0, Math.PI * 2); c.stroke();
    c.restore();
  }
  // 날아가는 것: 화면 아래(던진 사람 쪽)에서 사진까지 포물선, 멀어질수록 작게. 던진 사람 색으로
  for (const p of projectiles) {
    const k = Math.min(1, (now - p.t0) / p.dur);
    const pos = q => ({
      x: p.from.x + (p.to.x - p.from.x) * q,
      y: p.from.y + (p.to.y - p.from.y) * q - Math.sin(Math.PI * q) * 160,
    });
    const { x, y } = pos(k);
    const size = 120 * effectScale * (1 - 0.5 * k);
    // 꼬리 (던진 사람 색)
    for (let t = 1; t <= 6; t++) {
      const q = k - t * 0.035;
      if (q <= 0) break;
      const tp = pos(q);
      c.globalAlpha = 0.35 * (1 - t / 7);
      c.fillStyle = p.color;
      c.beginPath(); c.arc(tp.x, tp.y, size * (0.32 - t * 0.03), 0, Math.PI * 2); c.fill();
    }
    c.globalAlpha = 1;
    c.save();
    c.translate(x, y); c.rotate(p.spin * k);
    if (p.obj === 'ball') drawBall(c, size * 0.42, p.color);
    else {
      // 토마토·달걀: 던진 사람 색 동그라미 위에
      c.shadowColor = p.color; c.shadowBlur = 24;
      c.fillStyle = p.color;
      c.beginPath(); c.arc(0, 0, size * 0.5, 0, Math.PI * 2); c.fill();
      c.shadowBlur = 0;
      c.lineWidth = Math.max(3, size * 0.05); c.strokeStyle = '#fff'; c.stroke();
      c.font = `${Math.round(size * 0.72)}px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif`;
      c.textAlign = 'center'; c.textBaseline = 'middle';
      c.fillText(OBJECTS[p.obj].icon, 0, size * 0.03);
    }
    c.restore();
  }
  // "쾅!" 글자
  for (let i = pops.length - 1; i >= 0; i--) {
    const p = pops[i], k = (now - p.t0) / 0.9;
    if (k >= 1) { pops.splice(i, 1); continue; }
    const size = Math.max(1, (60 + 40 * p.big) * effectScale * (k < 0.15 ? k / 0.15 * 1.2 : 1.2 - 0.2 * (k - 0.15)));
    c.save();
    c.globalAlpha = k > 0.7 ? (1 - k) / 0.3 : 1;
    c.font = `900 ${Math.round(size)}px "Malgun Gothic", sans-serif`;
    c.textAlign = 'center'; c.textBaseline = 'middle';
    const maxWidth = L.W * 0.9;
    const halfWidth = Math.min(maxWidth, c.measureText(p.text).width) / 2 + size * 0.1;
    const px = Math.max(halfWidth, Math.min(L.W - halfWidth, p.x));
    const py = Math.max(size * 0.7, p.y - (60 + 40 * k) * effectScale);
    c.lineWidth = size * 0.14; c.strokeStyle = '#1a1406'; c.strokeText(p.text, px, py, maxWidth);
    c.fillStyle = p.color || '#ffd23f'; c.fillText(p.text, px, py, maxWidth);
    c.restore();
  }
}

/** 공: 던진 사람 색 공 + 흰 실밥 + 반짝임 */
function drawBall(c, r, color) {
  c.shadowColor = color; c.shadowBlur = 24;
  const g = c.createRadialGradient(-r * 0.35, -r * 0.35, r * 0.1, 0, 0, r);
  g.addColorStop(0, '#ffffff'); g.addColorStop(0.25, color); g.addColorStop(1, shade(color, -0.35));
  c.fillStyle = g;
  c.beginPath(); c.arc(0, 0, r, 0, Math.PI * 2); c.fill();
  c.shadowBlur = 0;
  c.strokeStyle = 'rgba(255,255,255,.9)'; c.lineWidth = Math.max(2, r * 0.09);
  c.beginPath(); c.arc(-r * 1.35, 0, r, -0.75, 0.75); c.stroke();
  c.beginPath(); c.arc(r * 1.35, 0, r, Math.PI - 0.75, Math.PI + 0.75); c.stroke();
  c.lineWidth = Math.max(2, r * 0.07); c.strokeStyle = 'rgba(0,0,0,.35)';
  c.beginPath(); c.arc(0, 0, r, 0, Math.PI * 2); c.stroke();
}

/** 색을 밝게(+)/어둡게(-) */
function shade(color, amt) {
  const m = /^#?([0-9a-f]{6})$/i.exec(color);
  if (!m) return color;
  const n = parseInt(m[1], 16);
  const f = v => Math.max(0, Math.min(255, Math.round(amt < 0 ? v * (1 + amt) : v + (255 - v) * amt)));
  return `rgb(${f(n >> 16)}, ${f((n >> 8) & 255)}, ${f(n & 255)})`;
}

function roundRect(c, x, y, w, h, r) {
  c.beginPath();
  c.moveTo(x + r, y); c.arcTo(x + w, y, x + w, y + h, r); c.arcTo(x + w, y + h, x, y + h, r);
  c.arcTo(x, y + h, x, y, r); c.arcTo(x, y, x + w, y, r); c.closePath();
}

// ───────── 점수 카드 (던진 횟수) ─────────
let cardsKey = '';
function renderCards() {
  if (phase === 'setup') return;
  const list = players;
  const key = list.map(s => s.name + s.throws + s.color + s.present).join('|');
  if (key === cardsKey) return;
  cardsKey = key;
  const html = s => `<div class="card panel${s.present ? '' : ' away'}" style="--c:${s.color}"><div class="n">${esc(s.name)}</div><div class="s">${s.throws}<small>번</small></div></div>`;
  const half = Math.ceil(list.length / 2);
  $('cardsL').innerHTML = list.slice(0, half).map(html).join('');
  $('cardsR').innerHTML = list.slice(half).map(html).join('');
}

function updateSeen() {
  const n = client.bodies.size, max = settings.players, el = $('seen');
  const txt = client.connected === false && !SIM ? '⚠ 에이전트에 연결되지 않았어요 (run.bat 실행 확인)'
    : n ? `카메라에 <b>${n}명</b>이 보여요 · 최대 <b>${max}명</b>까지 함께 던져요` : `카메라 앞에 서 주세요 · 최대 <b>${max}명</b>까지 함께 던져요`;
  if (el.innerHTML !== txt) el.innerHTML = txt;
  el.classList.toggle('warn', !n);
}

// ───────── 효과음 ─────────
const sfx = {
  noise: null,
  buf() {
    const ac = sound.ctx();
    if (!this.noise) {
      this.noise = ac.createBuffer(1, ac.sampleRate * 1, ac.sampleRate);
      const d = this.noise.getChannelData(0);
      for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
    }
    return ac;
  },
  burst({ dur, type, f0, f1 = f0, q = 1, gain = 0.3 }) {
    try {
      const ac = this.buf(), t = ac.currentTime;
      const src = ac.createBufferSource(), flt = ac.createBiquadFilter(), g = ac.createGain();
      src.buffer = this.noise;
      flt.type = type; flt.Q.value = q;
      flt.frequency.setValueAtTime(f0, t); flt.frequency.exponentialRampToValueAtTime(f1, t + dur);
      g.gain.setValueAtTime(gain, t); g.gain.exponentialRampToValueAtTime(0.001, t + dur);
      src.connect(flt).connect(g).connect(ac.destination);
      src.start(t); src.stop(t + dur + 0.02);
    } catch { /* 무시 */ }
  },
  thump(freq = 110, dur = 0.18, gain = 0.4) {
    try {
      const ac = this.buf(), t = ac.currentTime, o = ac.createOscillator(), g = ac.createGain();
      o.frequency.setValueAtTime(freq, t); o.frequency.exponentialRampToValueAtTime(freq * 0.5, t + dur);
      g.gain.setValueAtTime(gain, t); g.gain.exponentialRampToValueAtTime(0.001, t + dur);
      o.connect(g).connect(ac.destination); o.start(t); o.stop(t + dur + 0.02);
    } catch { /* 무시 */ }
  },
  whoosh(p) { this.burst({ dur: 0.28, type: 'bandpass', f0: 400, f1: 1800, q: 1.2, gain: 0.12 + p * 0.1 }); },
  impact(obj, p) {
    if (obj === 'ball') { this.burst({ dur: 0.12, type: 'highpass', f0: 2500, gain: 0.35 }); this.thump(120, 0.2, 0.35 + p * 0.2); }
    else { this.burst({ dur: 0.22, type: 'lowpass', f0: 900, f1: 300, gain: 0.5 }); this.thump(80, 0.15, 0.3); }
  },
  shatter(n) {
    for (let i = 0; i < n; i++) setTimeout(() => sound.tone([2000 + Math.random() * 2500], 0.05, 'triangle', 0.04), 30 + Math.random() * 450);
  },
};

// ───────── 소리 허용 ─────────
function checkSound() { show('soundBlocked', sound.blocked()); }
$('soundBlocked').onclick = () => { sound.unlock(); checkSound(); };
addEventListener('pointerdown', () => { sound.unlock(); checkSound(); });
setTimeout(checkSound, 1500);

function esc(s) { return String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]); }

// ───────── 시험용 ─────────
if (SIM) {
  if (AUTO_THROW) {
    setInterval(() => {
      if (phase !== 'play') return;
      const i = Math.floor(Math.random() * SIM);
      window.__simThrows = (window.__simThrows || 0) + 1;
      client.sim.throwArm(i, { hand: Math.random() < 0.6 ? 'R' : 'L', aim: { dx: (Math.random() - 0.5) * 0.6, dy: Math.random() * 0.35 - 0.1 }, dur: 0.4 + Math.random() * 0.35 });
    }, 1400);
  }
  window.__smash = () => ({ phase, throws: game?.throws ?? 0, remain: board?.remain ?? 1, flying: projectiles.length, frags: frags.length,
    photo: game ? game.idx + 1 : 0, total: game?.queue.length ?? 0, cleared: game?.cleared ?? 0, waiting: $('queueCount').textContent, objs: window.__objs });
  window.__smashStart = () => startGame();
}
setPhase('setup');
