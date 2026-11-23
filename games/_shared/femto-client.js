// ─────────────────────────────────────────────────────────────
//  게임 공통: 에이전트 연결
//  ws://(이 페이지 주소)/ws 에서 스켈레톤·영상·사람 목록을 받아 정리합니다.
//
//  사용:
//    const client = new FemtoClient({ sim: 0 });   // sim: 가상 플레이어 수 (0 = 실제 카메라)
//    client.onFrame = () => { ... client.bodies ... };
//
//  bodies: Map(bodyId → Body)
//    Body.j   Float32Array(128)  관절 32개 × (x, y, z, 신뢰도)  — 미터, y 위쪽(+), 떨림 필터 적용
//    Body.p2  Float32Array(64)|null  컬러 영상 위 관절 픽셀 좌표 (u, v), 투영 실패 -1
//    Body.pid 등록된 사람 id (확인 중이면 null)
// ─────────────────────────────────────────────────────────────

export const J = {
  Pelvis: 0, SpineNavel: 1, SpineChest: 2, Neck: 3, ClavicleLeft: 4, ShoulderLeft: 5, ElbowLeft: 6, WristLeft: 7,
  HandLeft: 8, HandTipLeft: 9, ThumbLeft: 10, ClavicleRight: 11, ShoulderRight: 12, ElbowRight: 13, WristRight: 14,
  HandRight: 15, HandTipRight: 16, ThumbRight: 17, HipLeft: 18, KneeLeft: 19, AnkleLeft: 20, FootLeft: 21,
  HipRight: 22, KneeRight: 23, AnkleRight: 24, FootRight: 25, Head: 26, Nose: 27, EyeLeft: 28, EarLeft: 29,
  EyeRight: 30, EarRight: 31,
};
export const BONES = [
  [1, 0], [2, 1], [3, 2], [4, 2], [5, 4], [6, 5], [7, 6], [8, 7], [9, 8], [10, 7],
  [11, 2], [12, 11], [13, 12], [14, 13], [15, 14], [16, 15], [17, 14],
  [18, 0], [19, 18], [20, 19], [21, 20], [22, 0], [23, 22], [24, 23], [25, 24],
  [26, 3], [27, 26], [28, 27], [29, 28], [30, 27], [31, 30]];
export const FACE = new Set([J.Nose, J.EyeLeft, J.EarLeft, J.EyeRight, J.EarRight]);

// 떨림 보정 (One Euro 필터) — 뷰어와 같은 값, 게임은 반응이 중요해서 조금 가볍게
class OneEuro {
  constructor() { this.x = null; this.dx = 0; this.t = 0; }
  reset() { this.x = null; }
  filter(v, t, minCutoff, beta) {
    if (this.x === null || !(t > this.t) || t - this.t > 0.5) { this.x = v; this.dx = 0; this.t = t; return v; }
    const dt = t - this.t;
    this.t = t;
    const alpha = fc => 1 / (1 + 1 / (2 * Math.PI * fc * dt));
    this.dx += alpha(3.0) * ((v - this.x) / dt - this.dx);
    this.x += alpha(minCutoff + beta * Math.abs(this.dx)) * (v - this.x);
    return this.x;
  }
}
const CUTOFF = 2.5, BETA_3D = 20, BETA_2D = 0.08;

class Body {
  constructor(id) {
    this.id = id;
    this.pid = null;
    this.j = new Float32Array(128);
    this.p2 = null;
    this.f3 = Array.from({ length: 96 }, () => new OneEuro());
    this.f2 = Array.from({ length: 64 }, () => new OneEuro());
  }
  set(raw, p2, t) {
    for (let i = 0; i < 32; i++) {
      const c = raw[i * 4 + 3], mc = CUTOFF * (c < 2 ? 0.5 : 1);
      // 카메라 좌표(mm, y 아래) → 미터, y 위
      this.j[i * 4] = this.f3[i * 3].filter(raw[i * 4] / 1000, t, mc, BETA_3D);
      this.j[i * 4 + 1] = this.f3[i * 3 + 1].filter(-raw[i * 4 + 1] / 1000, t, mc, BETA_3D);
      this.j[i * 4 + 2] = this.f3[i * 3 + 2].filter(raw[i * 4 + 2] / 1000, t, mc, BETA_3D);
      this.j[i * 4 + 3] = c;
    }
    if (p2) {
      this.p2 ??= new Float32Array(64);
      for (let i = 0; i < 64; i++) {
        if (p2[i] < 0) { this.f2[i].reset(); this.p2[i] = -1; continue; }
        this.p2[i] = this.f2[i].filter(p2[i], t, CUTOFF, BETA_2D);
      }
    } else this.p2 = null;
  }
  /** 관절 위치 {x,y,z} (미터, y 위) */
  pos(i) { return { x: this.j[i * 4], y: this.j[i * 4 + 1], z: this.j[i * 4 + 2] }; }
  conf(i) { return this.j[i * 4 + 3]; }
}

export class FemtoClient {
  constructor({ sim = 0, video = true } = {}) {
    this.bodies = new Map();
    this.people = new Map();     // pid → { name, color }
    this.status = { state: 'starting', message: '에이전트 연결 중…' };
    this.connected = false;
    this.onFrame = null;
    this.onStatus = null;
    this.video = new VideoBuffer();
    this.wantVideo = video;
    this.sim = sim > 0 ? new SimPlayers(this, sim) : null;
    if (this.sim) {
      this.connected = true;
      this.status = { state: 'running', source: 'sim', message: `가상 플레이어 ${sim}명 (카메라 없이 시험)` };
    } else this.connect();
  }

  personName(pid) { return pid && this.people.get(pid)?.name; }

  connect() {
    const url = `ws://${location.host || 'localhost:5000'}/ws`;
    const ws = new WebSocket(url);
    ws.binaryType = 'arraybuffer';
    this.ws = ws;
    ws.onopen = () => { this.connected = true; this.send({ cmd: 'video', on: this.wantVideo }); };
    ws.onmessage = ev => {
      if (typeof ev.data !== 'string') { this.video.push(ev.data); return; }
      const m = JSON.parse(ev.data);
      if (m.type === 'frame') this.handleFrame(m);
      else if (m.type === 'people') { this.people = new Map(m.people.map(p => [p.id, p])); }
      else if (m.type === 'status') { this.status = m; this.onStatus?.(m); }
      else if (m.type === 'reload') location.reload();
    };
    ws.onclose = () => {
      this.connected = false;
      this.status = { state: 'error', message: '에이전트에 연결할 수 없습니다. run.bat 이 실행 중인지 확인하세요.' };
      this.onStatus?.(this.status);
      this.bodies.clear();
      setTimeout(() => this.connect(), 1500);
    };
  }

  send(cmd) { if (this.ws?.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(cmd)); }

  handleFrame(f) {
    const t = f.t / 1e6, seen = new Set();
    for (const b of f.bodies) {
      seen.add(b.id);
      let body = this.bodies.get(b.id);
      if (!body) { body = new Body(b.id); this.bodies.set(b.id, body); }
      body.pid = b.pid ?? null;
      body.set(b.j, b.p2, t);
    }
    for (const id of [...this.bodies.keys()]) if (!seen.has(id)) this.bodies.delete(id);
    this.onFrame?.();
  }
}

// ───────── 배경 제거 영상 (뷰어와 같은 처리) ─────────
class VideoBuffer {
  constructor() {
    this.canvas = document.createElement('canvas');      // 배경을 지운 결과 (사람만)
    this.raw = document.createElement('canvas');         // 원래 영상 (배경 포함)
    this.rawCtx = this.raw.getContext('2d');
    this.frameNo = 0;
    this.ctx = this.canvas.getContext('2d');
    this.mask = document.createElement('canvas');
    this.maskCtx = this.mask.getContext('2d');
    this.width = 0; this.height = 0; this.at = 0;
    this.busy = false; this.pending = null;
    this.cov = null; this.prev = null; this.tmp = null; this.maskImg = null;
  }
  get live() { return this.width > 0 && performance.now() - this.at < 2000; }

  async push(buf) {
    if (this.busy) { this.pending = buf; return; }
    this.busy = true;
    try {
      const v = new DataView(buf);
      if (v.getUint8(0) !== 0x56) return;
      const fmt = v.getUint8(1), iw = v.getUint16(2, true), ih = v.getUint16(4, true);
      const mw = v.getUint16(6, true), mh = v.getUint16(8, true), il = v.getUint32(10, true), ml = v.getUint32(14, true);
      const [bmp, mask] = await Promise.all([
        createImageBitmap(new Blob([new Uint8Array(buf, 18, il)], { type: fmt === 1 ? 'image/jpeg' : 'image/bmp' })),
        inflate(new Uint8Array(buf, 18 + il, ml)),
      ]);
      if (this.mask.width !== mw || this.mask.height !== mh || !this.maskImg) {
        this.mask.width = mw; this.mask.height = mh;
        this.maskImg = this.maskCtx.createImageData(mw, mh);
        this.maskImg.data.fill(255);
      }
      this.refine(mask, mw, mh);
      this.maskCtx.putImageData(this.maskImg, 0, 0);
      if (this.canvas.width !== iw || this.canvas.height !== ih) { this.canvas.width = iw; this.canvas.height = ih; }
      if (this.raw.width !== iw || this.raw.height !== ih) { this.raw.width = iw; this.raw.height = ih; }
      this.rawCtx.drawImage(bmp, 0, 0);
      const c = this.ctx;
      c.globalCompositeOperation = 'copy';
      c.drawImage(bmp, 0, 0);
      bmp.close();
      c.globalCompositeOperation = 'destination-in';
      c.imageSmoothingEnabled = true;
      c.drawImage(this.mask, 0, 0, iw, ih);
      c.globalCompositeOperation = 'source-over';
      this.width = iw; this.height = ih; this.at = performance.now();
      this.frameNo++;
    } catch (e) {
      console.warn('영상 처리 실패', e);
    } finally {
      this.busy = false;
      if (this.pending) { const b = this.pending; this.pending = null; this.push(b); }
    }
  }

  // 마스크 경계 다듬기 (프레임 평균 → 흐림 → 가운데 값 기준으로 다시 또렷하게)
  refine(mask, w, h) {
    const n = w * h, out = this.maskImg.data;
    if (!this.cov || this.cov.length !== n) { this.cov = new Float32Array(n); this.tmp = new Float32Array(n); this.prev = null; }
    const cov = this.cov;
    for (let i = 0; i < n; i++) cov[i] = mask[i] / 255;
    if (this.prev) for (let i = 0; i < n; i++) cov[i] = cov[i] * 0.7 + this.prev[i] * 0.3;
    else this.prev = new Float32Array(n);
    this.prev.set(cov);
    const r = Math.max(1, Math.round(2.5 * w / 640));
    boxBlur(cov, this.tmp, w, h, r);
    boxBlur(cov, this.tmp, w, h, Math.max(1, r >> 1));
    for (let i = 0; i < n; i++) {
      let t = (cov[i] - 0.5) / 0.14;
      t = t < 0 ? 0 : t > 1 ? 1 : t;
      out[i * 4 + 3] = (t * t * (3 - 2 * t) * 255) | 0;
    }
  }
}

async function inflate(bytes) {
  const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('deflate'));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

function boxBlur(src, tmp, w, h, r) {
  const div = 1 / (2 * r + 1);
  for (let y = 0; y < h; y++) {
    const row = y * w;
    let acc = src[row] * (r + 1);
    for (let x = 1; x <= r; x++) acc += src[row + Math.min(x, w - 1)];
    for (let x = 0; x < w; x++) {
      tmp[row + x] = acc * div;
      acc += src[row + Math.min(x + r + 1, w - 1)] - src[row + Math.max(x - r, 0)];
    }
  }
  for (let x = 0; x < w; x++) {
    let acc = tmp[x] * (r + 1);
    for (let y = 1; y <= r; y++) acc += tmp[Math.min(y, h - 1) * w + x];
    for (let y = 0; y < h; y++) {
      src[y * w + x] = acc * div;
      acc += tmp[Math.min(y + r + 1, h - 1) * w + x] - tmp[Math.max(y - r, 0) * w + x];
    }
  }
}

// ─────────────────────────────────────────────────────────────
//  가상 플레이어 (카메라 없이 게임 시험): 주소 뒤에 ?sim=2 처럼 붙이면 2명
//  게임이 client.sim.act(...) 으로 "이렇게 움직여"를 알려 주면, 사람처럼 잠깐 뒤에 (가끔 틀리게) 따라 함
// ─────────────────────────────────────────────────────────────
const SIM_TEMPLATE = [ // 미터, y 위(+), 바닥 0, 사람의 왼쪽 = 카메라 화면 오른쪽(+x)
  [0, 1.00, 0], [0, 1.15, 0], [0, 1.32, 0], [0, 1.50, 0], [0.04, 1.45, 0], [0.18, 1.44, 0], [0.20, 1.16, 0.03], [0.21, 0.91, 0.05],
  [0.21, 0.84, 0.05], [0.21, 0.77, 0.05], [0.24, 0.88, 0.08], [-0.04, 1.45, 0], [-0.18, 1.44, 0], [-0.20, 1.16, 0.03], [-0.21, 0.91, 0.05],
  [-0.21, 0.84, 0.05], [-0.21, 0.77, 0.05], [-0.24, 0.88, 0.08], [0.09, 0.97, 0], [0.10, 0.53, 0.02], [0.10, 0.10, 0], [0.10, 0.03, 0.12],
  [-0.09, 0.97, 0], [-0.10, 0.53, 0.02], [-0.10, 0.10, 0], [-0.10, 0.03, 0.12], [0, 1.62, 0.01], [0, 1.63, 0.10], [0.035, 1.67, 0.08],
  [0.075, 1.64, 0], [-0.035, 1.67, 0.08], [-0.075, 1.64, 0]];
const LIMB_KEYS = ['RA', 'LA', 'RL', 'LL'];
const SIM_SIDE = 0.51;   // 가상 플레이어 팔 각도: 0 = 내림, 0.51 = 옆으로(수평), 1 = 머리 위

class SimPlayers {
  constructor(client, n) {
    this.client = client;
    this.players = Array.from({ length: n }, (_, i) => ({
      id: 100 + i,
      x: (i - (n - 1) / 2) * 0.9,            // 0.9m 간격
      scale: [1.0, 0.9, 1.06, 0.82][i % 4],
      skill: [0.92, 0.8, 0.7, 0.86][i % 4],  // 맞힐 확률
      speed: [0.55, 0.75, 0.95, 0.65][i % 4],// 평균 반응 시간(초)
      limbs: { RA: 0, LA: 0, RL: 0, LL: 0 }, // 0 = 내림, 1 = 올림 (목표)
      cur: { RA: 0, LA: 0, RL: 0, LL: 0 },   // 지금 (부드럽게 따라감)
      timers: [],
    }));
    this.t0 = performance.now();
    setInterval(() => this.tick(), 1000 / 30);
  }

  /** 모두에게 "이 팔다리를 올려"(raise) 또는 "모두 내려"(raise = []) 를 지시. distract = 헷갈리게 하는 쪽 */
  act(raise, { distract = [], dont = false, sure = false, pool = LIMB_KEYS } = {}) {
    for (const p of this.players) {
      for (const t of p.timers) clearTimeout(t);
      p.timers = [];
      const delay = (p.speed * (0.7 + Math.random() * 0.6)) * 1000;
      let target = { RA: 0, LA: 0, RL: 0, LL: 0 };
      if (raise.length === 0) {
        p.timers.push(setTimeout(() => (p.limbs = target), 250 + Math.random() * 300));
        continue;
      }
      const ok = sure || Math.random() < p.skill;
      if (ok) {
        if (!dont) for (const k of raise) target[k] = 1;
      } else {
        // 틀리는 방식: 헷갈리는 쪽을 들거나, 반대쪽을 들거나, 하지 말라는 걸 함
        const others = pool.filter(k => !raise.includes(k));
        const wrong = dont ? raise[0] ?? distract[0] : (distract[0] ?? others[Math.floor(Math.random() * others.length)]);
        target[wrong] = 1;
      }
      p.timers.push(setTimeout(() => (p.limbs = target), delay));
      p.last = { ok, delay: delay / 1000, target };   // 시험용: 이번에 맞게 했는지
    }
  }

  /**
   * 목표 자세로 움직이라고 지시 (target: {RA:'up'|'side'|'down', ...}). prev = 직전 목표.
   * 틀릴 때: 헷갈리게 하는 쪽(distract)을 들거나, 그대로 있어야 할 팔다리를 움직이거나, 옆으로/위로를 바꿔 함
   */
  actTarget(target, { dont = false, distract = [], prev = {} } = {}) {
    const V = { down: 0, up: 1, side: SIM_SIDE };
    for (const p of this.players) {
      for (const t of p.timers) clearTimeout(t);
      p.timers = [];
      const delay = (p.speed * (0.7 + Math.random() * 0.6)) * 1000;
      const ok = Math.random() < p.skill;
      const t = { ...p.limbs };
      for (const k in target) t[k] = V[target[k]];
      if (!ok) {
        const keys = Object.keys(target);
        const legUp = target.RL === 'up' || target.LL === 'up';   // 두 발을 동시에 들 수는 없음
        const stay = keys.filter(k => target[k] === (prev[k] ?? 'down') && !(legUp && (k === 'RL' || k === 'LL')));
        if (distract.length) {   // 헷갈려서 말한 쪽을 대신 듦
          for (const k of keys) if (target[k] !== (prev[k] ?? 'down')) t[k] = V[prev[k] ?? 'down'];
          t[distract[0]] = 1;
        }
        else if (stay.length) { const k = stay[Math.floor(Math.random() * stay.length)]; t[k] = target[k] === 'down' ? 1 : 0; }
        else { const k = keys[Math.floor(Math.random() * keys.length)]; t[k] = target[k] === 'up' ? SIM_SIDE : 1; }
      }
      p.timers.push(setTimeout(() => (p.limbs = t), delay));
      p.last = { ok, delay: delay / 1000, target: t };
    }
  }

  /** (시험용) 모두의 실력(맞힐 확률 0~1)을 바꿈 → 자동 난이도 시험 */
  setSkill(v) { for (const p of this.players) p.skill = Math.max(0, Math.min(1, v)); }

  /** (시험용) i번째 가상 플레이어가 손으로 가리키기: 가슴 기준 dx, dy(미터) / null 이면 손 내림 */
  point(i, at) { this.players[i].point = at; }

  /** (시험용) i번째 가상 플레이어가 두 손을 모아 공 잡기: 가슴 기준 dx, dy(미터), gap = 두 손 사이(미터) / null 이면 손 내림 */
  hold(i, at) { this.players[i].hold = at; }

  /**
   * (시험용) i번째 가상 플레이어가 던지기: hand 'R'|'L', aim = 손을 뻗는 방향(가슴 기준 옆 dx, 위 dy, 미터),
   * dur = 걸리는 시간(초, 짧을수록 세게)
   */
  throwArm(i, { hand = 'R', aim = { dx: 0, dy: 0 }, dur = 0.55 } = {}) {
    this.players[i].throwing = { hand, aim, dur, t0: (performance.now() - this.t0) / 1000 };
  }

  tick() {
    const now = (performance.now() - this.t0) / 1000;
    const c = this.client;
    for (const p of this.players) {
      let body = c.bodies.get(p.id);
      if (!body) { body = new Body(p.id); body.pid = 'sim' + p.id; c.bodies.set(p.id, body); }
      for (const k of LIMB_KEYS) p.cur[k] += (p.limbs[k] - p.cur[k]) * 0.35;
      const raw = this.pose(p, now);
      body.set(raw.j, raw.p2, now);
    }
    c.onFrame?.();
  }

  pose(p, t) {
    const P = SIM_TEMPLATE.map(v => [v[0], v[1], v[2]]);
    const lerp = (a, b, k) => a + (b - a) * k;
    // 팔 올리기: 어깨 위로 곧게 (각도 보간)
    const arm = (sh, side, k) => {
      const ang = lerp(0.12, Math.PI * 0.94, k);  // 0 = 아래, π = 머리 위
      const s = P[sh], dir = [side * Math.sin(ang) * 0.45, -Math.cos(ang), 0.05];
      const seg = (len, f) => [s[0] + dir[0] * len * f, s[1] + dir[1] * len * f, s[2] + dir[2] * f];
      P[sh + 1] = seg(0.28, 1); P[sh + 2] = seg(0.53, 1); P[sh + 3] = seg(0.60, 1); P[sh + 4] = seg(0.67, 1); P[sh + 5] = seg(0.58, 1);
    };
    // 다리 들기: 무릎을 앞·위로, 발은 무릎 아래
    const leg = (hip, k) => {
      const h = P[hip];
      const knee = [h[0], lerp(0.53, 0.78, k), lerp(0.02, 0.32, k)];
      P[hip + 1] = knee;
      P[hip + 2] = [h[0], knee[1] - 0.43 + 0.1 * k, knee[2] - 0.05];
      P[hip + 3] = [h[0], P[hip + 2][1] - 0.06, P[hip + 2][2] + 0.12];
    };
    arm(J.ShoulderRight, -1, p.cur.RA); arm(J.ShoulderLeft, +1, p.cur.LA);
    if (p.point) {   // 오른손으로 가리키기 (가슴 기준)
      const c = P[J.SpineChest], s = P[J.ShoulderRight];
      const hand = [c[0] + p.point.dx, c[1] + p.point.dy, 0.35];
      P[J.ElbowRight] = [(s[0] + hand[0]) / 2, (s[1] + hand[1]) / 2 - 0.05, 0.18];
      P[J.WristRight] = [hand[0], hand[1] - 0.05, 0.33];
      P[J.HandRight] = hand;
      P[J.HandTipRight] = [hand[0], hand[1] + 0.05, 0.36];
      P[J.ThumbRight] = [hand[0] + 0.03, hand[1], 0.35];
    }
    if (p.hold) {   // 두 손 모으기 (공 잡기): 가슴 앞에서 두 손을 gap 만큼 벌려 같은 높이로
      const c = P[J.SpineChest], g = (p.hold.gap ?? 0.12) / 2;
      for (const [sh, side] of [[J.ShoulderRight, -1], [J.ShoulderLeft, +1]]) {
        const s = P[sh], hand = [c[0] + (p.hold.dx ?? 0) + side * g, c[1] + p.hold.dy, 0.38];
        P[sh + 1] = [(s[0] + hand[0]) / 2 + side * 0.08, (s[1] + hand[1]) / 2 - 0.06, 0.16];
        P[sh + 2] = [hand[0], hand[1] - 0.04, 0.35]; P[sh + 3] = hand;
        P[sh + 4] = [hand[0], hand[1] + 0.05, 0.4]; P[sh + 5] = [hand[0] - side * 0.03, hand[1], 0.38];
      }
    }
    if (p.throwing) {   // 던지기: 손을 뒤·위로 뺐다가(55%) 앞으로 휙(45%, 점점 빨라짐)
      const th = p.throwing, k = (t - th.t0) / th.dur;
      if (k >= 1.3) p.throwing = null;
      else {
        const sh = th.hand === 'R' ? J.ShoulderRight : J.ShoulderLeft, side = th.hand === 'R' ? -1 : 1, s = P[sh];
        const rest = P[sh + 3], back = [s[0] + side * 0.14, s[1] + 0.2, -0.32];
        const front = [s[0] + side * 0.02 + th.aim.dx, s[1] + 0.02 + th.aim.dy, 0.55];
        const L = (a, b, q) => a.map((v, n) => v + (b[n] - v) * q);
        const hand = k < 0.55 ? L(rest, back, Math.sin((k / 0.55) * Math.PI / 2))
          : k < 1 ? L(back, front, ((k - 0.55) / 0.45) ** 2) : front;
        P[sh + 1] = L(s, hand, 0.5).map((v, n) => v + (n === 1 ? -0.06 : 0));
        P[sh + 2] = L(s, hand, 0.92); P[sh + 3] = hand;
        P[sh + 4] = L(s, hand, 1.08); P[sh + 5] = L(s, hand, 1.02).map((v, n) => v + (n === 0 ? 0.03 : 0));
      }
    }
    leg(J.HipRight, p.cur.RL); leg(J.HipLeft, p.cur.LL);
    const sway = 0.02 * Math.sin(t * 1.3 + p.id);
    const j = new Float32Array(128), p2 = new Float32Array(64);
    const camH = 1.0, dist = 2.8;
    for (let i = 0; i < 32; i++) {
      const n = () => (Math.random() - 0.5) * 16;   // ±8mm 떨림
      const x = (P[i][0] * p.scale + p.x + sway) * 1000 + n();
      const y = (camH - P[i][1] * p.scale) * 1000 + n();    // 카메라 y 아래
      const z = (dist - P[i][2] * p.scale) * 1000 + n();
      j[i * 4] = x; j[i * 4 + 1] = y; j[i * 4 + 2] = z; j[i * 4 + 3] = 2;
      p2[i * 2] = 600 * x / z + 640; p2[i * 2 + 1] = 600 * y / z + 360;   // 1280×720 가상 영상
    }
    return { j, p2 };
  }
}
