// ─────────────────────────────────────────────────────────────
//  청기백기: 명령 만들기
//
//  [팔·다리 모드]
//  단계 1  한 팔          "오른팔 올려!"
//  단계 2  팔·다리 하나    "왼발 들어!"
//  단계 3  ~말고          "오른발 말고 왼팔 올려!"   (오른발을 들면 틀림)
//  단계 4  두 개 동시      "오른팔 올리고 왼발 들어!"
//  단계 5  ~하지 마       "왼팔 올리지 마!"          (아무것도 안 들어야 성공)
//
//  [팔만 모드] — 팔만 쓰더라도 지루하지 않게 동작·규칙을 다양하게
//  단계 1  한 팔 올리기     "오른팔 올려!"
//  단계 2  만세·옆으로      "두 팔 다 올려!", "왼팔 옆으로 벌려!", "청기 올려!"(청기 = 오른팔, 백기 = 왼팔)
//  단계 3  섞어서·~말고     "오른팔 올리고 왼팔 옆으로!", "백기 말고 청기 올려!"
//  단계 4  반대로·이어가기   "반대로! 청기 올려!"(→ 왼팔), 내리지 않고 이어서 "청기 올려 → 백기 올려 → 청기 내려"
//  단계 5  ~하지 마·긴 이어가기
//
//  단계가 높으면 아래 단계 명령도 섞여 나옵니다.
//  명령은 "목표 자세"(target)로 판정합니다: 팔다리마다 up(올림) / side(옆으로) / down(내림)
// ─────────────────────────────────────────────────────────────

const SIDE = { RA: 'R', LA: 'L', RL: 'R', LL: 'L' };
const WORD = { RA: '오른팔', LA: '왼팔', RL: '오른발', LL: '왼발' };
const isArm = k => k === 'RA' || k === 'LA';
const verb = k => (isArm(k) ? '올려' : '들어');
const verbAnd = k => (isArm(k) ? '올리고' : '들고');
const verbDont = k => (isArm(k) ? '올리지 마' : '들지 마');

export const LEVEL_NAMES = ['', '한 팔', '팔·다리', '~말고', '두 개 동시', '~하지 마'];
export const LEVEL_NAMES_ARMS = ['', '한 팔', '만세·옆으로', '섞어서', '반대로·이어가기', '~하지 마'];

/**
 * 자동 난이도 단계 (처음엔 1단계로 쉽게 → 잘하면 올라가고, 힘들어하면 내려감)
 *  level: 명령 종류(위 단계 1~5), speed: 반응 시간 배율(클수록 느긋), back: "내려" 시간(초),
 *  wrong: 다른 팔다리를 이만큼(초) 들고 있어야 틀림 (잠깐 움찔한 건 봐줌)
 */
export const TIERS = [
  { level: 1, speed: 1.6,  back: 3.0, wrong: 0.6 },
  { level: 2, speed: 1.5,  back: 2.8, wrong: 0.6 },
  { level: 2, speed: 1.35, back: 2.6, wrong: 0.55 },
  { level: 3, speed: 1.35, back: 2.6, wrong: 0.5 },
  { level: 3, speed: 1.2,  back: 2.3, wrong: 0.45 },
  { level: 4, speed: 1.2,  back: 2.2, wrong: 0.45 },
  { level: 4, speed: 1.05, back: 2.0, wrong: 0.4 },
  { level: 5, speed: 1.05, back: 2.0, wrong: 0.4 },
  { level: 5, speed: 0.95, back: 1.8, wrong: 0.35 },
];

/**
 * 자동 난이도 조절
 *  명령마다 모두의 결과를 0~1 점수로 넣으면 (성공 1, 느린 성공 0.7, 틀림·놓침 0)
 *  - 올리기: 바꾼 뒤 3번(내려간 직후엔 4번) 이상 지났고, 최근 3번 평균이 0.8 이상
 *  - 내리기: 최근 2번 연속 0.35 이하 (어려워하는 게 보이면 바로), 또는 최근 3번 평균 0.5 미만
 *  반환값: +1 (올림) / -1 (내림) / 0
 */
export class Adapt {
  constructor(start = 0) { this.tier = start; this.hist = []; this.since = 0; this.lastMove = 0; this.maxTier = start; }
  get cfg() { return TIERS[this.tier]; }
  push(g) {
    this.hist.push(g);
    this.since++;
    const last = n => this.hist.slice(-n);
    const mean = a => a.reduce((x, y) => x + y, 0) / a.length;
    const l2 = last(2), l3 = last(3);
    const struggling = (this.since >= 2 && l2.length === 2 && l2.every(v => v <= 0.35))
      || (this.since >= 3 && mean(l3) < 0.5);
    if (struggling && this.tier > 0) return this.move(-1);
    const need = this.lastMove < 0 ? 4 : 3;
    if (this.since >= need && l3.length === 3 && mean(l3) >= 0.8 && this.tier < TIERS.length - 1) return this.move(+1);
    return 0;
  }
  move(d) {
    this.tier += d; this.since = 0; this.lastMove = d;
    this.maxTier = Math.max(this.maxTier, this.tier);
    return d;
  }
}

const pick = a => a[Math.floor(Math.random() * a.length)];

/**
 * 명령 하나 만들기
 *  반환: { target:{RA,LA[,RL,LL]: 'up'|'side'|'down'}, prev(이전 목표), dont, parts, text, window, level, kind,
 *          raise(올려야 할 것), distract(헷갈리게 하는 것), rest:[이어가기 다음 명령들], step, steps }
 *  maxSteps: 남은 명령 수 (이어가기가 판 끝·쉬는 시간을 넘지 않게)
 */
export function makeCommand(level, prev, { legs = true, maxSteps = 99 } = {}) {
  for (let tries = 0; tries < 30; tries++) {
    const c = legs ? build(level) : buildArms(level, maxSteps);
    if (!prev || c.text !== prev.text) return c;
  }
  return legs ? build(level) : buildArms(level, maxSteps);
}

const DOWN4 = () => ({ RA: 'down', LA: 'down', RL: 'down', LL: 'down' });

function build(level) {
  // 높은 단계에서도 쉬운 명령이 섞이도록
  const kinds = [];
  if (level <= 1) kinds.push('arm');
  else kinds.push('one', 'one');
  if (level >= 3) kinds.push('but', 'but');
  if (level >= 4) kinds.push('two', 'two');
  if (level >= 5) kinds.push('dont');
  const kind = pick(kinds);

  const all = ['RA', 'LA', 'RL', 'LL'];
  let raise = [], distract = [], dont = false, parts;
  if (kind === 'arm' || kind === 'one') {
    const k = kind === 'arm' ? pick(['RA', 'LA']) : pick(all);
    raise = [k];
    parts = [w(k), { t: ` ${verb(k)}!` }];
  } else if (kind === 'but') {
    const k = pick(all), d = pick(all.filter(x => x !== k));
    raise = [k]; distract = [d];
    parts = [w(d), { t: ' 말고 ' }, w(k), { t: ` ${verb(k)}!` }];
  } else if (kind === 'two') {
    // 두 발 동시는 불가 → 팔+팔 또는 팔+다리
    const a = pick(['RA', 'LA']), b = pick(all.filter(x => x !== a));
    const [x, y] = Math.random() < 0.5 ? [a, b] : [b, a];
    raise = [x, y];
    parts = [w(x), { t: ` ${verbAnd(x)} ` }, w(y), { t: ` ${verb(y)}!` }];
  } else {
    const k = pick(all);
    raise = []; distract = [k]; dont = true;
    parts = [w(k), { t: ` ${verbDont(k)}!` }];
  }
  const target = DOWN4();
  for (const k of raise) target[k] = 'up';
  const base = { arm: 2.6, one: 2.6, but: 3.0, two: 3.2, dont: 2.4 }[kind];
  return cmd({ target, prev: DOWN4(), raise, distract, dont, parts, window: base, level, kind });
}

// ───────── 팔만 모드 ─────────
const ARM_NAME = { RA: ['오른팔', '청기'], LA: ['왼팔', '백기'] };
const OTHER = { RA: 'LA', LA: 'RA' };
const DOWN2 = () => ({ RA: 'down', LA: 'down' });

function buildArms(level, maxSteps) {
  // 새로 열린 명령이 더 자주 나오고, 아래 단계 명령도 섞임
  const kinds = ['one'];
  if (level >= 2) kinds.push('both', 'side', 'side', 'sideBoth', 'one');
  if (level >= 3) kinds.push('mix', 'mix', 'but', 'but');
  if (level >= 4) kinds.push('reverse', 'reverse', 'reverse');
  if (level >= 4 && maxSteps >= 3) kinds.push('chain', 'chain', 'chain');
  if (level >= 5) kinds.push('dont', 'dont', 'reverse');
  const kind = pick(kinds);
  const flags = level >= 2;                       // 2단계부터 "청기/백기" 이름도 섞어 부름
  const nm = k => an(k, flags && Math.random() < 0.45);
  const target = DOWN2();
  let parts, raise = [], distract = [], dont = false;

  if (kind === 'one') {
    const k = pick(['RA', 'LA']);
    target[k] = 'up'; raise = [k];
    parts = [nm(k), { t: ' 올려!' }];
  } else if (kind === 'both') {
    target.RA = target.LA = 'up'; raise = ['RA', 'LA'];
    parts = Math.random() < 0.5 ? [{ t: '두 팔 다 올려!' }] : [{ t: '만세!' }];
  } else if (kind === 'side') {
    const k = pick(['RA', 'LA']);
    target[k] = 'side'; raise = [k];
    parts = [an(k, false), { t: ' 옆으로 벌려!' }];
  } else if (kind === 'sideBoth') {
    target.RA = target.LA = 'side'; raise = ['RA', 'LA'];
    parts = [{ t: '두 팔 옆으로 벌려!' }];
  } else if (kind === 'mix') {
    const k = pick(['RA', 'LA']), o = OTHER[k];
    target[k] = 'up'; target[o] = 'side'; raise = [k, o];
    parts = [an(k, false), { t: ' 올리고 ' }, an(o, false), { t: ' 옆으로!' }];
  } else if (kind === 'but') {
    const k = pick(['RA', 'LA']), d = OTHER[k];
    target[k] = 'up'; raise = [k]; distract = [d];
    parts = [nm(d), { t: ' 말고 ' }, nm(k), { t: ' 올려!' }];
  } else if (kind === 'reverse') {
    // "반대로!" → 말한 팔의 반대쪽 팔을 올림 (말한 팔을 올리면 틀림)
    const said = pick(['RA', 'LA']), k = OTHER[said];
    const how = level >= 5 && Math.random() < 0.35 ? 'side' : 'up';
    target[k] = how; raise = [k]; distract = [said];
    parts = [{ t: '반대로! ' }, nm(said), { t: how === 'up' ? ' 올려!' : ' 옆으로!' }];
  } else if (kind === 'dont') {
    const k = pick(['RA', 'LA']);
    dont = true; distract = [k];
    parts = [nm(k), { t: ' 올리지 마!' }];
  } else {
    return chain(level, Math.min(maxSteps, level >= 5 ? pick([3, 4]) : 3));
  }
  const base = { one: 2.6, both: 2.6, side: 2.8, sideBoth: 2.8, mix: 3.2, but: 3.0, reverse: 3.2, dont: 2.4 }[kind];
  return cmd({ target, prev: DOWN2(), raise, distract, dont, parts, window: base, level, kind });
}

/**
 * 이어가기 (원래 청기백기 방식): "내려" 없이 명령이 이어지고, 지금 어느 팔이 올라가 있는지 기억해야 함
 *  예) 청기 올려 → 백기 올려(둘 다 올라감) → 청기 내려(백기만)
 */
function chain(level, n) {
  let state = DOWN2();
  const steps = [];
  let last = null;
  for (let i = 0; i < n; i++) {
    // 바꿀 팔: 같은 팔을 연달아 바꾸지 않도록
    const k = last ? OTHER[last] : pick(['RA', 'LA']);
    const now = state[k];
    let to;
    if (now === 'down') to = level >= 5 && Math.random() < 0.3 ? 'side' : 'up';
    else to = 'down';
    // 마지막 명령엔 "내려"가 꼭 한 번은 들어가도록 (어느 팔이 올라가 있는지 기억해야 하는 순간) — 올라간 팔 중 아무거나
    if (i === n - 1 && !steps.some(s => s.to === 'down')) {
      const k2 = pick(['RA', 'LA'].filter(x => state[x] !== 'down'));
      steps.push(step(state, k2, 'down', level, i, n));
      break;
    }
    steps.push(step(state, k, to, level, i, n));
    state = steps.at(-1).target;
    last = k;
  }
  const first = steps[0];
  first.rest = steps.slice(1);
  return first;
}

function step(prevState, k, to, level, i, n) {
  const target = { ...prevState, [k]: to };
  const word = to === 'up' ? ' 올려!' : to === 'side' ? ' 옆으로!' : ' 내려!';
  const parts = [an(k, Math.random() < 0.6), { t: word }];
  return cmd({ target, prev: { ...prevState }, raise: to === 'down' ? [] : [k], distract: [], dont: false, parts,
    window: 2.8, level, kind: 'chain', step: i + 1, steps: n, to });
}

function an(k, flag) { return { t: ARM_NAME[k][flag ? 1 : 0], side: SIDE[k] }; }

function cmd(c) {
  c.text = c.parts.map(p => p.t).join('');
  c.rest ??= []; c.step ??= 1; c.steps ??= 1;
  c.say = c.kind === 'chain' && c.step === 1 ? `이어가기! ${c.text}` : c.text;   // 읽어 줄 말
  return c;
}

function w(k) { return { t: WORD[k], side: SIDE[k] }; }

/** 나올 수 있는 명령 문장 (에이전트 음성을 미리 만들어 둘 때) — 실제로 여러 번 만들어 모음 */
export function allCommandTexts(legs = true) {
  const out = new Set();
  for (let lv = 1; lv <= 5; lv++) for (let i = 0; i < 400; i++) {
    const c = makeCommand(lv, null, { legs });
    out.add(c.say);
    for (const r of c.rest) out.add(r.say);
  }
  return [...out];
}
