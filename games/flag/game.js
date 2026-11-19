// ─────────────────────────────────────────────────────────────
//  청기백기 (팔·다리, 1~4인) — 어르신용
//
//  흐름: 설정(손으로 고르기) → 준비(모두 두 손 올리면 시작) → 3·2·1
//        → [명령 → 내려!] × N (10번마다 잠깐 쉬기) → 결과(등수, 손으로 다시 하기/다른 게임)
//  난이도: 고르지 않음. 1단계(쉽게)로 시작 → 잘하면 올라가고, 힘들어하면 내려감 (commands.js Adapt)
//  점수: 성공 100 + 빠르기 최대 50 + 정확도(얼마나 높이) 최대 50 + 연속 성공 보너스 최대 50
//        "~하지 마!"를 잘 참으면 100 + 연속 보너스. 틀리거나 놓치면 0점(감점 없음), 연속 끊김.
// ─────────────────────────────────────────────────────────────
import { FemtoClient, J } from '../_shared/femto-client.js';
import { Stage, viewOptions, setViewOption } from '../_shared/video-layer.js';
import { createPlayers, trackPlayers, pickBodies, PLAYER_COLORS } from '../_shared/players.js';
import { isNeutral, limbState, LIMB_NAME } from '../_shared/pose.js';
import { HandPointer, screenToHand } from '../_shared/hand-pointer.js';
import { Sound } from '../_shared/sound.js';
import { makeCommand, TIERS, Adapt, allCommandTexts, LEVEL_NAMES, LEVEL_NAMES_ARMS } from './commands.js';

const $ = id => document.getElementById(id);
const params = new URLSearchParams(location.search);
const SIM = Math.max(0, Math.min(4, parseInt(params.get('sim') || '0', 10) || 0));
const MIRROR = true;
if (SIM) $('backLink').href = `../?sim=${SIM}`;

const client = new FemtoClient({ sim: SIM });
if (SIM && params.get('skill')) client.sim.setSkill(+params.get('skill'));   // 시험: 가상 플레이어 실력 (0~1)
const stage = new Stage($('stage'), client);
const sound = new Sound();
const pointer = new HandPointer({ client, mirror: MIRROR, onSelect: () => sound.tick() });

// 판정 시간 (초) — 어르신 기준으로 넉넉하게
const HOLD_OK = 0.15;
const HOLD_OK_CHAIN = 0.4;   // 이어가기: 한 팔을 내리면서 다른 팔을 올리는 순간 잠깐 맞는 자세가 되는 것은 성공으로 치지 않음                                          // 이만큼 유지하면 성공
const SPEECH_DELAY = 0.5;                                      // 음성이 없을 때 명령을 읽는 최소 시간
// 음성 안내가 끝날 때까지 다음 동작을 시작하지 않음 (음성이 멈춰 버린 경우를 위한 최대 대기 초)
const SPEECH_WAIT = { command: 5, back: 8, count: 5, rest: 10 };
const BACK_EXTRA_WAIT = 2.5;                                   // 모두 내릴 때까지 최대 추가 대기
const REST_EVERY = 10, REST_SECONDS = 12;                      // 10번마다 잠깐 쉬기

// ───────── 설정 ─────────
const opts = { players: 1, move: 'both', rounds: 10, voice: true };
try { Object.assign(opts, JSON.parse(localStorage.getItem('femto.flag.opts2') || '{}')); } catch {}
delete opts.level;   // v1.5까지의 빠르기 선택 (이제 자동)
if (SIM && opts.players > SIM) opts.players = SIM;

function bindChoice(id, key, parse) {
  const box = $(id);
  const sync = () => { for (const b of box.querySelectorAll('button')) b.classList.toggle('on', String(opts[key]) === b.dataset.v); };
  for (const b of box.querySelectorAll('button')) {
    b.onclick = () => {
      opts[key] = parse(b.dataset.v);
      sync();
      if (key === 'move') updateSafety();
      try { localStorage.setItem('femto.flag.opts2', JSON.stringify(opts)); } catch {}
    };
  }
  sync();
}
bindChoice('optPlayers', 'players', v => +v);
bindChoice('optMove', 'move', v => v);
bindChoice('optRounds', 'rounds', v => +v);

// 화면 보기 설정 (모든 게임 공통으로 저장): 배경 보이기/지우기, 스켈레톤 보이기/숨기기
function renderView() {
  $('optView').querySelectorAll('button').forEach(b => b.classList.toggle('on', !!viewOptions[b.dataset.k]));
  $('vBackground').textContent = viewOptions.background ? '보이기' : '지우기';
  $('vSkeleton').textContent = viewOptions.skeleton ? '보이기' : '숨기기';
}
$('optView').addEventListener('click', e => {
  const b = e.target.closest('button');
  if (!b) return;
  setViewOption(b.dataset.k, !viewOptions[b.dataset.k]);
  renderView();
});
renderView();
function updateSafety() { $('safety').classList.toggle('hidden', opts.move !== 'both'); }
updateSafety();

// ───────── 상태 ─────────
let phase = 'setup';        // setup | ready | count | command | link(이어가기 사이) | back | rest | results
let players = [];
let game = null;
let phaseStart = 0;
let lastNow = performance.now();

function show(id, on) { $(id).classList.toggle('hidden', !on); }
function setPhase(p) {
  phase = p;
  phaseStart = performance.now() / 1000;
  const playing = ['command', 'link', 'back', 'rest'].includes(p);
  show('setup', p === 'setup');
  show('ready', p === 'ready');
  show('count', p === 'count');
  show('hud', playing);
  const cardsOn = ['ready', 'count', 'command', 'link', 'back', 'rest'].includes(p);
  show('cardsL', cardsOn); show('cardsR', cardsOn);
  show('results', p === 'results');
  show('stop', p !== 'setup' && p !== 'results');
  if (p === 'setup' || p === 'results') clearTags();
  // 손으로 고르기는 설정·결과 화면에서만 (게임 중엔 팔 동작이 게임 동작이므로)
  pointer.enabled = p === 'setup' || p === 'results';
  if (!pointer.enabled) pointer.hide();
}

// ───────── 1. 설정 → 2. 준비 ─────────
$('start').onclick = startReady;
$('again').onclick = startReady;
$('toSetup').onclick = () => setPhase('setup');
$('toGames').onclick = () => { location.href = $('backLink').href; };
$('stop').onclick = () => { sound.stop(); setPhase('setup'); };

function updateSetup() {
  const n = client.bodies.size, need = opts.players, st = client.status;
  const btn = $('start'), seen = $('seen');
  if (!client.connected || st.state === 'error') {
    seen.innerHTML = `⚠ 카메라 프로그램에 연결 중이에요…`; seen.className = 'warn';
    btn.disabled = true;
  } else if (n < need) {
    seen.innerHTML = `지금 <b>${n}명</b> 보여요. <b>${need}명</b>이 카메라 앞에 서 주세요.`; seen.className = 'warn';
    btn.disabled = true;
  } else {
    seen.innerHTML = `<b>${need}명</b> 준비됐어요! <b>시작</b> 위에 손을 올려 두세요.${SIM ? ' <span style="color:var(--warn)">(가상 플레이어 시험)</span>' : ''}`; seen.className = '';
    btn.disabled = false;
  }
  btn.textContent = `▶ ${need}명 시작`;
}

function startReady() {
  if (client.bodies.size < opts.players) { setPhase('setup'); return; }
  sound.voiceOn = opts.voice;
  sound.unlock();
  players = createPlayers(client, opts.players, MIRROR);
  for (const p of players) Object.assign(p, { score: 0, streak: 0, ok: 0, total: 0, qSum: 0, qN: 0, rtSum: 0, rtN: 0, firsts: 0, readyHold: 0, result: null });
  game = { adapt: new Adapt(0), index: -1, cmd: null, level: 1, legs: opts.move === 'both' };
  sound.preload(['내려', ...allCommandTexts(game.legs)], 0.95);
  game.chain = [];   // 에이전트 음성: 명령 소리를 미리 만들어 둠
  buildTags();
  buildCards();
  setPhase('ready');
  renderReadySlots();
  sound.say('모두 두 손을 위로 올려 주세요');
  client.sim?.point(0, null);
  client.sim?.act(['RA', 'LA'], { sure: true });
}

function renderReadySlots() {
  $('readySlots').innerHTML = players.map(p =>
    `<span class="slot ${p.readyHold >= 0.8 ? 'on' : ''}" style="border-color:${p.color}">${p.readyHold >= 0.8 ? '✓ ' : ''}${esc(p.name)}${p.present ? '' : ' (자리 비움)'}</span>`).join('');
}

function updateReady(dt) {
  let all = true;
  for (const p of players) {
    const up = p.s && p.s.RA.up && p.s.LA.up;
    p.readyHold = up ? p.readyHold + dt : 0;
    if (p.readyHold < 0.8) all = false;
  }
  renderReadySlots();
  if (all && (!sound.busy || performance.now() / 1000 - phaseStart >= SPEECH_WAIT.count)) startCountdown();   // 안내 말이 끝난 뒤
}

// ───────── 3. 카운트다운 ─────────
function startCountdown() {
  setPhase('count');
  client.sim?.act([]);
  sound.say('손 내리세요. 시작합니다');
  game.countShown = -1;
  game.countFrom = null;
  $('count').textContent = '손 내리세요';
}

function updateCount(t) {
  // "손 내리세요. 시작합니다" 말이 끝난 뒤 3·2·1
  if (game.countFrom == null) {
    if (sound.busy && t - phaseStart < SPEECH_WAIT.count) return;
    game.countFrom = t;
  }
  const left = 3 - Math.floor((t - game.countFrom - 0.4) / 1.0);
  const shown = Math.min(3, left);
  if (shown !== game.countShown) {
    game.countShown = shown;
    if (shown > 0) { $('count').textContent = shown; sound.tick(); }
  }
  if (left <= 0) { sound.go(); nextCommand(); }
}

// ───────── 4. 명령 ─────────
function nextCommand() {
  game.index++;
  if (game.index >= opts.rounds) { finishGame(); return; }
  const inChain = game.chain?.length > 0;   // 이어가기 중이면 쉬는 시간 없이 다음 명령
  if (!inChain && game.index > 0 && game.index % REST_EVERY === 0 && !game.rested?.[game.index]) {
    (game.rested ??= {})[game.index] = true;
    game.index--;
    startRest();
    return;
  }
  const cfg = game.adapt.cfg;
  game.cfg = cfg;
  game.level = cfg.level;
  if (inChain) game.cmd = game.chain.shift();
  else {
    // 이어가기가 쉬는 시간·판 끝을 넘지 않게
    const maxSteps = Math.min(REST_EVERY - (game.index % REST_EVERY), opts.rounds - game.index);
    game.cmd = makeCommand(game.level, game.cmd, { legs: game.legs, maxSteps });
    game.chain = [...game.cmd.rest];
  }
  game.window = game.cmd.window * cfg.speed;   // 명령을 다 읽은 뒤부터 세는 반응 시간
  game.speechEnd = null;
  game.okStarts = [];   // 이번 명령에서 먼저 성공한 순서
  for (const p of players) {
    // 이어가기 2번째부터는 올린 채로 시작. 그 밖에는 먼저 다 내려야 판정 시작
    p.judge = { done: false, okHold: 0, wrongHold: 0, okStart: 0, start: null,
      armed: game.cmd.step > 1 || !p.s || isNeutral(p.s, { side: !game.legs }) };
    p.result = null;
  }
  setPhase('command');
  $('round').textContent = `${game.index + 1} / ${opts.rounds}번`;
  renderLevel();
  const cmd = $('cmd');
  cmd.className = '';
  cmd.innerHTML = game.cmd.parts.map(p => p.side ? `<span class="${p.side}">${p.t}</span>` : esc(p.t)).join('');
  if (game.cmd.kind === 'chain') setSub(`🔗 이어가기 ${game.cmd.step}/${game.cmd.steps} · ${game.cmd.step === 1 ? '올린 채로 다음 말을 기다려요' : '내리지 말고 이어서'}`, 'up');
  else setSub('');
  void cmd.offsetWidth; cmd.classList.add('pop');
  sound.say(game.cmd.say, 0.95);
  client.sim?.actTarget(game.cmd.target, { dont: game.cmd.dont, distract: game.cmd.distract, prev: game.cmd.prev });
}

function updateCommand(t, dt) {
  const el = t - phaseStart;
  // 명령 음성이 끝난 순간부터 반응 시간을 셈 (그 전에 움직여도 판정은 함)
  if (game.speechEnd == null && (!sound.busy || el >= SPEECH_WAIT.command)) game.speechEnd = Math.max(el, SPEECH_DELAY);
  for (const p of players) judge(p, dt, el);
  const used = game.speechEnd == null ? 0 : el - game.speechEnd;
  $('bar').firstElementChild.style.transform = `scaleX(${Math.max(0, Math.min(1, 1 - used / game.window))})`;
  // 모두 끝냈어도 명령 음성은 끝까지 들려준 뒤 "내려"
  if (game.speechEnd != null && (used >= game.window || players.every(p => p.judge.done))) endCommand();
}

// 목표 자세와 맞는지: 'down' 목표는 확실히 내렸거나 애매한 높이면 OK
const matches = (st, tgt) => (tgt === 'down' ? st === 'down' || st === 'mid' : st === tgt);

function judge(p, dt, el) {
  const j = p.judge, cmd = game.cmd;
  if (j.done) return;
  if (!p.s || !p.present) { j.okHold = j.wrongHold = 0; return; }
  const keys = Object.keys(cmd.target);                 // 판정하는 팔다리 (팔만 모드면 두 팔만)
  const st = {};
  for (const k of keys) st[k] = limbState(p.s, k, { side: !game.legs });
  // 앞 명령에서 내리지 않은 팔다리가 있으면, 먼저 다 내려야 판정 시작 (그대로 들고 있다가 맞히는 것 방지)
  if (!j.armed) { if (keys.every(k => st[k] !== 'up' && st[k] !== 'side')) j.armed = true; else return; }
  if (!j.start) {   // 명령 시작 때 자세: 이미 맞는 팔다리는 "그대로 있어야 하는 것"
    j.start = { ...st };
    j.startOk = Object.fromEntries(keys.map(k => [k, matches(st[k], cmd.target[k])]));
  }

  // 틀림: 그대로 있어야 할 팔다리를 움직였거나, 다른 자세로 들었을 때 (잠깐 흔들린 건 봐줌)
  const bad = keys.find(k => {
    const s = st[k];
    if (matches(s, cmd.target[k]) || s === 'mid') return false;
    if (j.startOk[k]) return true;
    return (s === 'up' || s === 'side') && s !== j.start[k];
  });
  if (bad) {
    j.wrongHold += dt;
    if (j.wrongHold >= game.cfg.wrong) { finish(p, 'wrong', { limb: bad }); return; }
  } else j.wrongHold = 0;
  if (cmd.dont) return;   // "~하지 마"는 끝까지 참으면 성공

  if (keys.every(k => matches(st[k], cmd.target[k]))) {
    if (j.okHold === 0) j.okStart = el;
    j.okHold += dt;
    if (j.okHold >= (cmd.kind === 'chain' ? HOLD_OK_CHAIN : HOLD_OK)) {
      const moved = keys.filter(k => cmd.target[k] !== 'down' && cmd.target[k] !== cmd.prev[k]);
      const q = moved.length ? moved.reduce((a, k) => a + p.s[k].q, 0) / moved.length : 1;
      finish(p, 'ok', { rt: Math.max(0, j.okStart - (game.speechEnd ?? j.okStart)), q, start: j.okStart });   // 말 끝난 뒤부터 걸린 시간
    }
  } else j.okHold = 0;
}

const ORDER_BONUS = [20, 10];   // 먼저 한 사람 보너스: 1등 +20, 2등 +10, 3등부터 0
const PRAISE = ['잘했어요!', '좋아요!', '훌륭해요!', '최고예요!', '멋져요!'];

/** 한 플레이어의 이번 명령 결과 확정 + 점수 */
let finish = function (p, kind, info = {}) {
  p.judge.done = true;
  p.total++;
  // 자동 난이도용 점수: 성공 1, 느린 성공 0.7, 틀림·놓침 0
  p.grade = kind !== 'ok' ? 0 : info.dont ? 1 : (info.rt <= 0.6 * game.window ? 1 : 0.7);
  let pts = 0, text, cls;
  if (kind === 'ok') {
    p.streak++;
    p.ok++;
    const streakBonus = Math.min(50, 10 * (p.streak - 1));
    if (info.dont) {
      pts = 100 + streakBonus;
      text = `잘 참았어요! +${pts}`;
    } else {
      const usable = Math.max(0.8, game.window - 0.6);
      const speed = Math.round(50 * clamp01(1 - (info.rt - 0.6) / usable));
      const acc = Math.round(50 * info.q);
      // 먼저 한 순서 보너스 (2명 이상일 때): 1등 +20, 2등 +10, 3등부터 0. 같은 순간이면 같은 등수
      let order = 0, orderText = '';
      if (players.filter(x => x.present).length >= 2) {
        const rank = game.okStarts.filter(t => t < info.start - 1e-3).length;
        game.okStarts.push(info.start);
        order = ORDER_BONUS[rank] ?? 0;
        if (order) orderText = `${rank + 1}등! `;
        p.firsts += rank === 0 ? 1 : 0;
      }
      pts = 100 + speed + acc + streakBonus + order;
      p.qSum += info.q; p.qN++;
      p.rtSum += info.rt; p.rtN++;
      text = `${orderText || PRAISE[Math.floor(Math.random() * PRAISE.length)] + ' '}+${pts}`;
    }
    cls = 'ok';
    sound.success();
  } else if (kind === 'wrong') {
    p.streak = 0;
    text = `아까워요 (${LIMB_NAME[info.limb] ?? ''})`;
    cls = 'bad';
    sound.fail();
  } else {
    p.streak = 0;
    text = '괜찮아요';
    cls = 'miss';
  }
  p.score += pts;
  p.result = { text, cls, at: performance.now() / 1000 };
  updateCards();
};

function endCommand() {
  for (const p of players) {
    if (p.judge.done) continue;
    if (game.cmd.dont && p.present) finish(p, 'ok', { dont: true });
    else finish(p, 'miss');
  }
  // 자동 난이도: 자리에 있는 사람들의 이번 결과 평균으로 판단
  const here = players.filter(p => p.present);
  const change = here.length ? game.adapt.push(here.reduce((a, p) => a + p.grade, 0) / here.length) : 0;

  // 이어가기 중이면 "내려" 없이 다음 명령으로
  if (game.chain?.length && game.index + 1 < opts.rounds) { startLink(change); return; }
  game.chain = [];

  // 5. 내려!
  setPhase('back');
  const cmd = $('cmd');
  cmd.className = 'back';
  cmd.textContent = game.cmd.dont ? '그대로~ 잘했어요' : '내려!';
  void cmd.offsetWidth; cmd.classList.add('pop');
  if (!game.cmd.dont) sound.say('내려', 0.95);
  setSub(game.cmd.dont ? '' : game.legs ? '팔과 다리를 제자리로' : '두 팔을 제자리로');
  announceChange(change);
  client.sim?.act([]);
  game.backTime = game.adapt.cfg.back;   // 이 시간이 지나도 음성 안내가 남아 있으면 끝날 때까지 기다림
}

function announceChange(change) {
  if (change > 0) {
    setSub('👍 잘하시네요! 조금 더 어렵게 해 볼게요', 'up');
    sound.levelUp();
    sound.say('잘하시네요! 조금 더 어렵게 해 볼게요', 0.95, { queue: true });
  } else if (change < 0) {
    setSub('😊 괜찮아요, 조금 쉽게 해 볼게요', 'up');
    sound.say('괜찮아요. 조금 쉽게 해 볼게요', 0.9, { queue: true });
  }
  if (change) renderLevel();
}

// ───────── 이어가기 사이: 내리지 않고 잠깐 뒤 다음 명령 ─────────
function startLink(change) {
  setPhase('link');
  setSub('🔗 그대로! 다음 말을 들어요', 'up');
  announceChange(change);
  $('bar').firstElementChild.style.transform = 'scaleX(1)';
}

function updateLink(t) {
  const el = t - phaseStart;
  if (el < 0.8) return;
  if (sound.busy && el < SPEECH_WAIT.back) return;
  nextCommand();
}

function updateBack(t) {
  const el = t - phaseStart;
  $('bar').firstElementChild.style.transform = `scaleX(${Math.max(0, 1 - el / game.backTime)})`;
  if (el < game.backTime) return;
  const notDown = players.filter(p => p.present && p.s && !isNeutral(p.s, { side: !game.legs }));
  if (notDown.length && el < game.backTime + BACK_EXTRA_WAIT) {
    setSub(`${notDown.map(p => p.name).join(', ')}님, 팔다리를 모두 내려 주세요`, 'warn');
    return;
  }
  if (sound.busy && el < game.backTime + SPEECH_WAIT.back) return;   // "내려"·난이도 안내가 끝날 때까지
  nextCommand();
}

// ───────── 잠깐 쉬기 ─────────
function startRest() {
  setPhase('rest');
  const cmd = $('cmd');
  cmd.className = 'rest';
  cmd.textContent = '🌿 잠깐 쉬어요';
  setSub('숨을 크게 들이쉬고… 천천히 내쉬세요');
  $('level').textContent = '쉬는 시간';
  void cmd.offsetWidth; cmd.classList.add('pop');
  sound.say('잘하고 있어요! 잠깐 쉬었다 할게요. 숨을 크게 들이쉬고, 천천히 내쉬세요.', 0.9);
  client.sim?.act([]);
}

function updateRest(t) {
  const el = t - phaseStart, left = Math.ceil(REST_SECONDS - el);
  $('bar').firstElementChild.style.transform = `scaleX(${Math.max(0, 1 - el / REST_SECONDS)})`;
  $('round').textContent = left > 0 ? `${left}초 뒤에 다시 시작` : '곧 다시 시작';
  if (el >= REST_SECONDS && (!sound.busy || el >= REST_SECONDS + SPEECH_WAIT.rest)) { sound.go(); nextCommand(); }
}

// ───────── 6. 결과 ─────────
function finishGame() {
  const ranked = [...players].sort((a, b) =>
    b.score - a.score || b.ok - a.ok || avg(a.rtSum, a.rtN, 9) - avg(b.rtSum, b.rtN, 9));
  let rank = 0, prev = null;
  ranked.forEach((p, i) => {
    if (!prev || p.score !== prev.score || p.ok !== prev.ok) rank = i + 1;
    p.rank = rank; prev = p;
  });
  const medal = r => ['', '🥇', '🥈', '🥉'][r] || `${r}등`;
  $('ranking').innerHTML = ranked.map(p => `
    <div class="rank ${p.rank === 1 ? 'first' : ''}" style="--c:${p.color}">
      <div class="pos">${medal(p.rank)}</div>
      <div class="who">${esc(p.name)}</div>
      <div class="pts">${p.score.toLocaleString()}점</div>
      <div class="st">
        <span>✔ ${p.ok}번 성공 / ${p.total}번</span>
        <span>높이 ${p.qN ? Math.round(100 * p.qSum / p.qN) : '-'}%</span>
        <span>반응 ${p.rtN ? (p.rtSum / p.rtN).toFixed(1) + '초' : '-'}</span>
        ${players.length >= 2 ? `<span>먼저 ${p.firsts}번</span>` : ''}
      </div>
    </div>`).join('');
  const mode = `${opts.move === 'both' ? '팔·다리' : '팔만'} · ${opts.rounds}번 · 최고 ${game.adapt.maxTier + 1}단계`;
  $('results').querySelector('h2').textContent = players.length === 1 ? `결과 (${mode})` : `${players.length}명 결과 (${mode})`;
  const okRate = players.reduce((a, p) => a + p.ok, 0) / Math.max(1, players.reduce((a, p) => a + p.total, 0));
  $('praise').textContent = okRate >= 0.8 ? '정말 잘하셨어요! 👏' : okRate >= 0.5 ? '잘하셨어요! 다음엔 더 잘할 수 있어요 👍' : '수고하셨어요! 천천히 다시 해 봐요 😊';
  saveRecords(ranked, mode);
  setPhase('results');
  sound.fanfare();
  const winners = ranked.filter(p => p.rank === 1).map(p => p.name);
  sound.say(players.length === 1 ? `${ranked[0].score}점! 수고하셨어요` : `1등은 ${winners.join(', ')}님! 모두 수고하셨어요`, 0.9);
  client.sim?.act([]);
}

function saveRecords(ranked, mode) {
  let recs = [];
  try { recs = JSON.parse(localStorage.getItem('femto.flag.records') || '[]'); } catch {}
  const now = new Date();
  const added = ranked.map(p => ({ name: p.name, score: p.score, mode: `${players.length}명 ${mode}`, date: now.toISOString(), _new: true }));
  recs = [...recs, ...added].sort((a, b) => b.score - a.score).slice(0, 10);
  $('best').innerHTML = `<b>최고 기록</b><ol>${recs.slice(0, 5).map(r =>
    `<li><b>${esc(r.name)}</b> ${r.score.toLocaleString()}점 · ${esc(r.mode)} · ${new Date(r.date).toLocaleDateString()}${r._new ? ' <span class="new">NEW</span>' : ''}</li>`).join('')}</ol>`;
  try { localStorage.setItem('femto.flag.records', JSON.stringify(recs.map(({ _new, ...r }) => r))); } catch {}
}

// ───────── 화면: 플레이어 카드 · 머리 위 이름표 ─────────
function buildCards() {
  // 화면 왼쪽 사람(P1…)은 왼쪽, 나머지는 오른쪽
  const half = Math.ceil(players.length / 2);
  const html = p => `
    <div class="card panel" id="card${p.index}" style="--c:${p.color}">
      <div class="n">${esc(p.name)}</div>
      <div class="s" id="score${p.index}">0</div>
      <div class="l"><span id="last${p.index}">준비</span><span class="streak" id="streak${p.index}"></span></div>
    </div>`;
  $('cardsL').innerHTML = players.slice(0, half).map(html).join('');
  $('cardsR').innerHTML = players.slice(half).map(html).join('');
}

function renderLevel() {
  const t = game.adapt.tier, n = TIERS.length;
  $('level').innerHTML = `${t + 1}단계 · ${(game.legs ? LEVEL_NAMES : LEVEL_NAMES_ARMS)[game.level]}<span class="dots">${'●'.repeat(t + 1)}${'○'.repeat(n - t - 1)}</span>`;
}

function setSub(text, cls = '') {
  const el = $('sub');
  if (el.textContent !== text) el.textContent = text;
  el.className = cls;
}

function updateCards() {
  for (const p of players) {
    $(`score${p.index}`).textContent = p.score.toLocaleString();
    $(`last${p.index}`).textContent = p.result ? p.result.text : (p.present ? '' : '자리 비움');
    $(`streak${p.index}`).textContent = p.streak >= 2 ? `🔥${p.streak}연속` : '';
    $(`card${p.index}`).classList.toggle('away', !p.present);
  }
}

let tagEls = [];
function buildTags() {
  clearTags();
  tagEls = players.map(p => {
    const el = document.createElement('div');
    el.className = 'tag';
    el.innerHTML = `<div class="res"></div><div class="name" style="background:${p.color}"></div>`;
    $('tags').appendChild(el);
    return el;
  });
}
function clearTags() { $('tags').replaceChildren(); tagEls = []; }

function drawTags(t) {
  players.forEach((p, i) => {
    const el = tagEls[i];
    if (!el) return;
    const pos = p.body && stage.toScreen(p.body, J.Head);
    el.style.display = pos ? '' : 'none';
    if (!pos) return;
    el.style.left = pos.x + 'px';
    el.style.top = Math.max(46, pos.y - 60 * (stage.map.unit / stage.map.dpr) - 10) + 'px';   // 무대 위쪽에서 잘리지 않게
    const name = el.querySelector('.name');
    if (name.textContent !== p.name) name.textContent = p.name;
    const res = el.querySelector('.res');
    const r = p.result, showRes = r && t - r.at < 1.8;
    const want = showRes ? r.text : '';
    if (res.textContent !== want) {
      res.textContent = want;
      res.className = 'res ' + (showRes ? r.cls + ' pop' : '');
    }
  });
}

// ───────── 소리 막힘 / 한국어 음성 안내 ─────────
function checkSound() {
  const blocked = !navigator.userActivation?.hasBeenActive && sound.blocked();
  $('soundBlocked').classList.toggle('hidden', !blocked);
}
$('soundBlocked').onclick = () => { sound.unlock(); checkSound(); };
addEventListener('pointerdown', () => { sound.unlock(); checkSound(); });
setTimeout(checkSound, 1500);
// 보호자 안내: 지금 쓰는 음성 (웹 음성 / Windows 음성 / 없음)
function showVoiceInfo() {
  const el = $('voiceInfo');
  if (sound.hasKoreanVoice) {
    el.textContent = `🔊 음성 안내: ${sound.engineLabel}` + (sound.naturalWeb ? '' : ' — 더 자연스러운 음성은 Edge + 인터넷 연결에서 나와요');
    el.className = '';
  }
  else { el.textContent = 'ℹ 한국어 음성을 찾지 못했어요. 인터넷을 연결하거나 Windows 설정 → 시간 및 언어 → 음성 → 한국어를 추가해 주세요.'; el.className = 'warn'; }
}
sound.ready.then(() => setTimeout(showVoiceInfo, 800));
sound.onEngineChange = showVoiceInfo;
try { speechSynthesis.addEventListener('voiceschanged', () => setTimeout(showVoiceInfo, 100)); } catch {}

// ───────── 키보드 (보호자용) ─────────
addEventListener('keydown', e => {
  if (e.code === 'Escape') { sound.stop(); setPhase('setup'); }
  if (e.code === 'Space' && phase === 'ready') { e.preventDefault(); startCountdown(); }
});

// ───────── 메인 루프 ─────────
function loop() {
  requestAnimationFrame(loop);
  const nowMs = performance.now(), t = nowMs / 1000;
  const dt = Math.min(0.1, (nowMs - lastNow) / 1000);
  lastNow = nowMs;

  if (phase === 'setup' || phase === 'results') {
    if (phase === 'setup') updateSetup();
    const preview = phase === 'setup'
      ? pickBodies(client, opts.players, MIRROR).map((b, i) => ({ present: true, bodyId: b.id, color: PLAYER_COLORS[i] }))
      : [];
    stage.draw({ players: preview, mirror: MIRROR, flags: false });
    pointer.update(dt);
    show('pointerHint', phase === 'results' && !pointer.visible && client.bodies.size > 0);   // 설정 화면은 안내 문구가 따로 있음
    return;
  }
  show('pointerHint', false);

  trackPlayers(players, client);
  for (const p of players) p.s = p.body ? p.reader.read(p.body) : null;

  if (phase === 'ready') updateReady(dt);
  else if (phase === 'count') updateCount(t);
  else if (phase === 'command') updateCommand(t, dt);
  else if (phase === 'link') updateLink(t);
  else if (phase === 'back') updateBack(t);
  else if (phase === 'rest') updateRest(t);

  stage.draw({ players, mirror: MIRROR, flags: true });
  drawTags(t);
  if (phase !== 'command') updateCards();
}

function clamp01(v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
function avg(s, n, d) { return n ? s / n : d; }
function esc(s) { return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }

// 가상 플레이어 시험에서만: 진행 상태 확인·손으로 가리키기 (읽기 전용 도우미)
if (SIM) {
  window.__flagLog = [];
  window.__simSkill = v => client.sim.setSkill(v);
  window.__flagState = () => ({ phase, index: game?.index, level: game?.level, tier: game?.adapt.tier, maxTier: game?.adapt.maxTier, players: players.map(p => ({ name: p.name, score: p.score, ok: p.ok, total: p.total, rank: p.rank })) });
  window.__pointAt = selector => {
    if (!selector) { client.sim.point(0, null); return; }
    const r = document.querySelector(selector).getBoundingClientRect();
    client.sim.point(0, screenToHand((r.left + r.width / 2) / innerWidth, (r.top + r.height / 2) / innerHeight));
  };
  const orig = finish;
  finish = (p, kind, info) => {
    const bot = client.sim.players[p.bodyId - 100];
    window.__flagLog.push({ i: game.index, tier: game.adapt.tier, cmd: game.cmd.text, kind: game.cmd.kind, player: p.index, judged: kind, botOk: bot?.last?.ok, botDelay: bot?.last?.delay, window: (game.speechEnd ?? 0) + game.window });
    orig(p, kind, info);
  };
}

setPhase('setup');
requestAnimationFrame(loop);
