// ─────────────────────────────────────────────────────────────
//  게임 공통: 화면에 사람 그리기
//
//  ▶ 새 게임은 캐릭터 타입을 정해서 씀 (GAME_RULEBOOK.md)
//    const stage = new Stage(canvas, client, { character: 'mirror' });   // 또는 'shadow'
//    stage.draw({ players, mirror: true, skeleton: false, background: img });
//
//    'mirror' 거울뷰  : 카메라 영상 그대로 + 사람 테두리에 글로우만 (사람마다 다른 색)
//    'shadow' 그림자뷰: 영상은 안 씀. 사람 모양을 깃털처럼 번진 60% 검정으로 (그림자처럼)
//                      + 사람마다 다른 색의 옅은 글로우 20% (구분만 되게). 배경은 게임이 넣음
//
//  draw() 옵션
//    players    : players.js 의 플레이어 목록 (그 사람 색으로 글로우). 목록에 없는 사람도 각자 다른 색
//    others     : 플레이어가 아닌 사람도 그릴지 (기본 true)
//    mirror     : 거울처럼 좌우 반전 (기본 true)
//    fit        : 'contain'(기본, 영상 전체) / 'cover'(칸을 꽉 채우고 사람 쪽을 가운데로)
//    skeleton   : 뼈대 선 덧그리기 — 캐릭터 타입과 상관없이 언제든 켤 수 있음 (기본 false)
//    flags      : 청기·백기 그리기 (기본 false)
//    background : 무대 배경 (그림자뷰에서 게임이 붙이는 배경, 거울뷰에서는 영상 밖 여백)
//                 null = 투명 (캔버스 뒤의 HTML/CSS 배경이 보임) / 이미지·캔버스 = 꽉 채워 그림 /
//                 함수 (ctx, W, H) => {} = 직접 그림 / 문자열 = 색
//
//  stage.toScreen(body, J.Head) → {x, y} 화면 좌표 (이름표·점수 띄울 때)
//  stage.skeleton(body, color) → 뼈대만 따로 그리기 (draw 다음에 불러서 원하는 사람만)
//
//  영상이 없을 때(가상 플레이어 ?sim=): 거울뷰는 뼈대, 그림자뷰는 뼈대로 만든 그림자로 대신 보여 줌
//
//  ▷ 예전 방식 (character 를 안 주면): 청기백기·게임 고르기 화면이 쓰는 보기 설정
//    viewOptions.background / glow / skeleton (모든 게임 공통, 이 PC에 저장)
// ─────────────────────────────────────────────────────────────
import { J, BONES, FACE } from './femto-client.js';

const VIRTUAL_W = 1280, VIRTUAL_H = 720;   // 영상이 없을 때(가상 플레이어) 기준 크기

// ───────── 캐릭터 타입 프리셋 ─────────
export const CHARACTER_TYPES = {
  mirror: {
    name: '거울뷰',
    video: true,          // 카메라 영상을 그대로
    glow: 1,              // 테두리 글로우 진하기
    shadow: 0,            // 그림자 진하기 (안 씀)
  },
  shadow: {
    name: '그림자뷰',
    video: false,         // 영상은 하나도 안 씀 (사람 모양 마스크만)
    glow: 0.2,            // 옅은 글로우 20% (사람 구분만 되게)
    shadow: 0.6,          // 60% 검정
    feather: 18,          // 테두리 번짐 (마스크 640px 기준 px) — 클수록 부드러운 그림자
  },
};

// 사람마다 다른 테두리 색: 플레이어는 플레이어 색(players.js), 그 밖의 사람은 이 목록에서 겹치지 않게
const BODY_COLORS = ['#ff6b6b', '#4dabf7', '#51cf66', '#fcc419', '#cc5de8', '#ff922b', '#22b8cf', '#f06595'];

// ───────── 보기 설정 (모든 게임 공통) ─────────
const VIEW_KEY = 'femto.view';
export const viewOptions = { background: true, glow: true, skeleton: false };
try { Object.assign(viewOptions, JSON.parse(localStorage.getItem(VIEW_KEY) || '{}')); } catch {}
export function setViewOption(key, value) {
  viewOptions[key] = value;
  try { localStorage.setItem(VIEW_KEY, JSON.stringify(viewOptions)); } catch {}
}

// 테두리 빛 색을 정할 때 쓰는 관절 (가까운 사람 색으로)
const GLOW_JOINTS = [J.Pelvis, J.SpineNavel, J.SpineChest, J.Neck, J.Head, J.ShoulderLeft, J.ElbowLeft, J.WristLeft, J.HandLeft,
  J.ShoulderRight, J.ElbowRight, J.WristRight, J.HandRight, J.HipLeft, J.KneeLeft, J.AnkleLeft, J.HipRight, J.KneeRight, J.AnkleRight];
const rgbCache = new Map();
function rgbOf(color) {
  if (rgbCache.has(color)) return rgbCache.get(color);
  const c = document.createElement('canvas').getContext('2d');
  c.fillStyle = color; c.fillRect(0, 0, 1, 1);
  const d = c.getImageData(0, 0, 1, 1).data, v = [d[0], d[1], d[2]];
  rgbCache.set(color, v);
  return v;
}

export class Stage {
  /** character: 'mirror' | 'shadow' (새 게임). 안 주면 예전 방식(viewOptions) */
  constructor(canvas, client, { character = null } = {}) {
    if (character && !CHARACTER_TYPES[character]) throw new Error(`캐릭터 타입이 없어요: ${character} (mirror | shadow)`);
    this.character = character;
    this.preset = character ? CHARACTER_TYPES[character] : null;
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.client = client;
    this.map = null;
    this.bodyColors = new Map();   // bodyId → 색 (플레이어가 아닌 사람)
    this.shadowOut = document.createElement('canvas');   // 번진 그림자 (마스크 해상도, 영상이 새로 올 때만 만듦)
    this.shadowOutCtx = this.shadowOut.getContext('2d');
    this.shadowKey = -1;
    this.glow = document.createElement('canvas');   // 사람마다 색을 칠한 윤곽 (마스크 해상도)
    this.glowCtx = this.glow.getContext('2d');
    this.glowOut = document.createElement('canvas');   // 번지게 한 빛 (영상이 새로 올 때만 만듦 → 그리기는 가볍게)
    this.glowOutCtx = this.glowOut.getContext('2d');
    this.glowKey = '';
  }

  /** 영상 픽셀 → 화면 좌표 변환 준비 */
  layout(mirror, fit = 'contain') {
    const dpr = Math.min(devicePixelRatio || 1, 2);
    const W = Math.round(this.canvas.clientWidth * dpr), H = Math.round(this.canvas.clientHeight * dpr);
    if (this.canvas.width !== W || this.canvas.height !== H) { this.canvas.width = W; this.canvas.height = H; }
    const v = this.client.video;
    const vw = v.live ? v.width : VIRTUAL_W, vh = v.live ? v.height : VIRTUAL_H;
    const scale = fit === 'cover' ? Math.max(W / vw, H / vh) : Math.min(W / vw, H / vh);   // 비율 유지
    const dw = vw * scale, dh = vh * scale;
    let dx = (W - dw) / 2, dy = (H - dh) / 2;
    if (fit === 'cover' && dw > W) {
      // 보이는 사람들 가운데로 (천천히 따라감)
      let sum = 0, n = 0;
      for (const b of this.client.bodies.values()) { const u = b.p2?.[0]; if (u >= 0) { sum += u; n++; } }
      const target = n ? sum / n : vw / 2;
      this.focusU = this.focusU == null ? target : this.focusU + (target - this.focusU) * 0.05;
      const fu = mirror ? vw - this.focusU : this.focusU;
      dx = Math.max(W - dw, Math.min(0, W / 2 - fu * scale));
    }
    this.map = { W, H, dpr, vw, vh, scale, dx, dy, dw, dh, mirror, unit: scale * vw / 1280 };
    return this.map;
  }

  sx(u) { const m = this.map; return m.mirror ? m.dx + m.dw - u * m.scale : m.dx + u * m.scale; }
  sy(v) { const m = this.map; return m.dy + v * m.scale; }

  /** 관절의 화면 좌표 (CSS 픽셀). 못 구하면 null */
  toScreen(body, joint) {
    if (!body?.p2 || !this.map || body.p2[joint * 2] < 0) return null;
    return { x: this.sx(body.p2[joint * 2]) / this.map.dpr, y: this.sy(body.p2[joint * 2 + 1]) / this.map.dpr };
  }

  draw(opts = {}) {
    if (this.preset) this.drawCharacter(opts);
    else this.drawLegacy(opts);
  }

  /** 사람마다 다른 색: 플레이어면 플레이어 색, 아니면 지금 안 쓰는 색 (한 번 정하면 계속 같은 색) */
  colorOf(body, byBody) {
    const pl = byBody.get(body.id);
    if (pl) return pl.color;
    let col = this.bodyColors.get(body.id);
    if (!col) {
      const used = new Set([...byBody.values()].map(p => p.color));
      for (const [id, c] of this.bodyColors) if (this.client.bodies.has(id)) used.add(c);
      col = BODY_COLORS.find(c => !used.has(c)) ?? BODY_COLORS[this.bodyColors.size % BODY_COLORS.length];
      this.bodyColors.set(body.id, col);
    }
    return col;
  }

  /** 캐릭터 타입(거울뷰·그림자뷰)으로 그리기 */
  drawCharacter({ players = [], mirror = true, flags = false, others = true, fit = 'contain', skeleton = false, background = null } = {}) {
    const m = this.layout(mirror, fit), c = this.ctx, v = this.client.video, P = this.preset;
    c.setTransform(1, 0, 0, 1, 0, 0);
    this.paintBackground(background, m);

    // 사라진 사람 색은 정리 (다음 사람이 그 색을 쓸 수 있게)
    for (const id of [...this.bodyColors.keys()]) if (!this.client.bodies.has(id)) this.bodyColors.delete(id);
    const byBody = new Map(players.filter(p => p.present).map(p => [p.bodyId, p]));
    const colorOf = body => (byBody.has(body.id) || others ? this.colorOf(body, byBody) : null);

    if (v.live) {
      const x = mirror ? m.W - m.dx - m.dw : m.dx;
      c.save();
      if (mirror) { c.translate(m.W, 0); c.scale(-1, 1); }
      if (P.video) c.drawImage(v.raw, x, m.dy, m.dw, m.dh);                         // 거울뷰: 영상 그대로
      if (P.glow > 0 && this.buildGlow(v, colorOf)) {
        c.globalAlpha = P.glow;
        c.drawImage(this.glowOut, x, m.dy, m.dw, m.dh);
        c.globalAlpha = 1;
      }
      if (P.video) c.drawImage(v.canvas, x, m.dy, m.dw, m.dh);                      // 사람을 다시 위에 → 글로우는 윤곽 바깥에만
      if (P.shadow > 0 && this.buildShadow(v, P.feather)) {                        // 그림자뷰: 번진 검은 그림자
        c.globalAlpha = P.shadow;
        c.drawImage(this.shadowOut, x, m.dy, m.dw, m.dh);
        c.globalAlpha = 1;
      }
      c.restore();
    } else if (P.shadow > 0) {
      // 영상이 없을 때(가상 플레이어): 뼈대로 그림자 모양을 만들어 대신 보여 줌
      for (const body of this.client.bodies.values()) {
        const col = colorOf(body);
        if (col) this.skeletonShadow(body, col, P);
      }
    }

    for (const body of this.client.bodies.values()) {
      const col = colorOf(body);
      if (!col) continue;
      if (skeleton || (!v.live && P.video)) this.skeleton(body, col, byBody.has(body.id) ? 1 : 0.7, v.live);
      if (flags && byBody.has(body.id)) this.flags(body);
    }
  }

  paintBackground(bg, m) {
    const c = this.ctx;
    if (bg == null) { c.clearRect(0, 0, m.W, m.H); return; }
    if (typeof bg === 'function') { c.clearRect(0, 0, m.W, m.H); bg(c, m.W, m.H); return; }
    if (typeof bg === 'string') { c.fillStyle = bg; c.fillRect(0, 0, m.W, m.H); return; }
    // 이미지·캔버스: 비율 유지하며 꽉 채움
    const iw = bg.naturalWidth || bg.videoWidth || bg.width, ih = bg.naturalHeight || bg.videoHeight || bg.height;
    if (!iw || !ih) { c.clearRect(0, 0, m.W, m.H); return; }
    const s = Math.max(m.W / iw, m.H / ih);
    c.drawImage(bg, (m.W - iw * s) / 2, (m.H - ih * s) / 2, iw * s, ih * s);
  }

  /** 그림자: 사람 마스크를 검게 칠하고 많이 번지게 (영상이 새로 올 때만 만듦) */
  buildShadow(v, feather) {
    if (!v.mask.width) return false;
    if (this.shadowKey === v.frameNo) return true;
    this.shadowKey = v.frameNo;
    const w = v.mask.width, h = v.mask.height;
    const o = this.shadowOut, oc = this.shadowOutCtx;
    if (o.width !== w || o.height !== h) { o.width = w; o.height = h; }
    if (!this.shadowTmp) { this.shadowTmp = document.createElement('canvas'); this.shadowTmpCtx = this.shadowTmp.getContext('2d'); }
    const t = this.shadowTmp, tc = this.shadowTmpCtx;
    if (t.width !== w || t.height !== h) { t.width = w; t.height = h; }
    // 사람 모양 → 검정
    tc.globalCompositeOperation = 'copy';
    tc.drawImage(v.mask, 0, 0);
    tc.globalCompositeOperation = 'source-in';
    tc.fillStyle = '#000'; tc.fillRect(0, 0, w, h);
    tc.globalCompositeOperation = 'source-over';
    // 깃털처럼 번지게
    oc.clearRect(0, 0, w, h);
    oc.filter = `blur(${Math.max(1, feather * w / 640)}px)`;
    oc.drawImage(t, 0, 0);
    oc.filter = 'none';
    return true;
  }

  /** 영상이 없을 때 그림자 대신: 굵은 뼈대를 검게 번지게 + 옅은 색 글로우 */
  skeletonShadow(body, color, P) {
    const p = body.p2, c = this.ctx, m = this.map;
    if (!p) return;
    const ok = i => p[i * 2] >= 0 && body.conf(i) > 0 && !FACE.has(i);
    const lw = 46 * m.unit;
    const shape = (stroke, alpha, blur) => {
      c.save();
      c.globalAlpha = alpha;
      c.filter = `blur(${blur}px)`;
      c.strokeStyle = c.fillStyle = stroke;
      c.lineCap = 'round'; c.lineJoin = 'round'; c.lineWidth = lw;
      for (const [a, b] of BONES) {
        if (!ok(a) || !ok(b)) continue;
        c.beginPath(); c.moveTo(this.sx(p[a * 2]), this.sy(p[a * 2 + 1])); c.lineTo(this.sx(p[b * 2]), this.sy(p[b * 2 + 1])); c.stroke();
      }
      if (ok(J.Head)) { c.beginPath(); c.arc(this.sx(p[J.Head * 2]), this.sy(p[J.Head * 2 + 1]), 40 * m.unit, 0, Math.PI * 2); c.fill(); }
      c.restore();
    };
    shape(color, P.glow, 14 * m.unit);              // 옅은 색 글로우 (바깥)
    shape('#000', P.shadow, P.feather * m.unit);    // 번진 검은 그림자
  }

  /** 예전 방식 (viewOptions): 청기백기·게임 고르기 화면 */
  drawLegacy({ players = [], mirror = true, flags = true, others = true, fit = 'contain' } = {}) {
    const m = this.layout(mirror, fit), c = this.ctx, v = this.client.video;
    const opt = viewOptions;
    c.setTransform(1, 0, 0, 1, 0, 0);
    // 무대 배경
    const g = c.createLinearGradient(0, 0, 0, m.H);
    g.addColorStop(0, '#141a24'); g.addColorStop(0.62, '#1b2230'); g.addColorStop(1, '#0e1218');
    c.fillStyle = g;
    c.fillRect(0, 0, m.W, m.H);

    const byBody = new Map(players.filter(p => p.present).map(p => [p.bodyId, p]));
    if (v.live) {
      const x = mirror ? m.W - m.dx - m.dw : m.dx;
      c.save();
      if (mirror) { c.translate(m.W, 0); c.scale(-1, 1); }
      if (opt.background) {
        c.drawImage(v.raw, x, m.dy, m.dw, m.dh);
        if (opt.glow) { c.fillStyle = 'rgba(8,10,16,.28)'; c.fillRect(x, m.dy, m.dw, m.dh); }   // 배경을 살짝 어둡게 → 사람·빛이 또렷
      }
      // 사람마다 색 빛 → 그 위에 사람을 다시 그려서 빛이 윤곽 바깥에만 보이게
      const legacyColor = body => (byBody.get(body.id)?.color ?? (others ? '#8a94a6' : null));
      if (opt.glow && this.buildGlow(v, legacyColor)) c.drawImage(this.glowOut, x, m.dy, m.dw, m.dh);
      c.drawImage(v.canvas, x, m.dy, m.dw, m.dh);
      c.restore();
    }

    for (const body of this.client.bodies.values()) {
      const pl = byBody.get(body.id);
      if (!pl && !others) continue;
      if (opt.skeleton || !v.live) this.skeleton(body, pl ? pl.color : 'rgba(150,160,175,.55)', pl ? 1 : 0.6, v.live);
      if (pl && flags) this.flags(body);
    }
  }

  /**
   * 사람 윤곽을 사람마다 색으로 칠한 그림 만들기 (영상이 새로 올 때만).
   * 마스크는 "사람인지 아닌지"만 알려 주므로, 각 점을 가장 가까운 사람(관절)의 색으로 칠함.
   */
  buildGlow(v, colorOf) {
    const mi = v.maskImg;
    if (!mi) return false;
    const w = mi.width, h = mi.height, sx = w / v.width, sy = h / v.height;
    const pts = [], cols = [];
    let key = v.frameNo + ':';
    for (const body of this.client.bodies.values()) {
      const col = colorOf(body);   // null = 그리지 않음
      if (!col) continue;
      key += body.id + col;
      if (!body.p2) continue;
      const ci = cols.length;
      cols.push(rgbOf(col));
      for (const j of GLOW_JOINTS) {
        const u = body.p2[j * 2], vv = body.p2[j * 2 + 1];
        if (u >= 0) pts.push(u * sx, vv * sy, ci);
      }
    }
    if (!pts.length) return false;
    if (key === this.glowKey) return true;
    this.glowKey = key;
    if (this.glow.width !== w || this.glow.height !== h) {
      this.glow.width = w; this.glow.height = h;
      this.glowImg = this.glowCtx.createImageData(w, h);
    }
    const src = mi.data, out = this.glowImg.data, n = pts.length;
    for (let y = 0, i = 0; y < h; y++) {
      for (let x = 0; x < w; x++, i += 4) {
        const a = src[i + 3];
        if (a < 10) { out[i + 3] = 0; continue; }
        let best = 0, bd = Infinity;
        for (let k = 0; k < n; k += 3) {
          const dx = pts[k] - x, dy = pts[k + 1] - y, d = dx * dx + dy * dy;
          if (d < bd) { bd = d; best = pts[k + 2]; }
        }
        const [r, gg, b] = cols[best];
        out[i] = r; out[i + 1] = gg; out[i + 2] = b; out[i + 3] = a;
      }
    }
    this.glowCtx.putImageData(this.glowImg, 0, 0);
    // 작은 그림에서 번지게: 넓게 퍼지는 빛 2번 + 또렷한 테두리 1번
    const o = this.glowOut, oc = this.glowOutCtx;
    if (o.width !== w || o.height !== h) { o.width = w; o.height = h; }
    const k = w / 640;
    oc.clearRect(0, 0, w, h);
    oc.filter = `blur(${Math.max(2, 7 * k)}px)`;
    oc.drawImage(this.glow, 0, 0); oc.drawImage(this.glow, 0, 0);
    oc.filter = `blur(${Math.max(1, 1.6 * k)}px)`;
    oc.drawImage(this.glow, 0, 0);
    oc.filter = 'none';
    return true;
  }

  /** 뼈대 선. 캐릭터 타입과 상관없이 언제든 (draw 다음에 부르면 그 위에 그려짐) */
  skeleton(body, color = '#ffffff', weight = 1, overVideo = this.client.video.live) {
    const p = body.p2, c = this.ctx, m = this.map;
    if (!p || !m) return;
    const lw = Math.max(2.5 * m.dpr, (overVideo ? 5 : 9) * m.unit) * weight;
    const ok = i => p[i * 2] >= 0 && body.conf(i) > 0 && !FACE.has(i);
    c.lineCap = 'round';
    c.strokeStyle = color;
    for (const [a, b] of BONES) {
      if (!ok(a) || !ok(b)) continue;
      c.lineWidth = lw;
      c.beginPath();
      c.moveTo(this.sx(p[b * 2]), this.sy(p[b * 2 + 1]));
      c.lineTo(this.sx(p[a * 2]), this.sy(p[a * 2 + 1]));
      c.stroke();
    }
    if (!overVideo && ok(J.Head)) {   // 영상이 없을 땐 머리를 동그랗게
      c.fillStyle = color;
      c.beginPath();
      c.arc(this.sx(p[J.Head * 2]), this.sy(p[J.Head * 2 + 1]), 26 * m.unit, 0, Math.PI * 2);
      c.fill();
    }
    for (let i = 0; i < 32; i++) {
      if (!ok(i) || i === J.Head) continue;
      c.fillStyle = '#fff';
      c.beginPath();
      c.arc(this.sx(p[i * 2]), this.sy(p[i * 2 + 1]), lw * 0.75, 0, Math.PI * 2);
      c.fill();
    }
  }

  // 청기(오른손, 파랑) · 백기(왼손, 흰색)
  flags(body) {
    const p = body.p2;
    if (!p) return;
    this.flag(p, J.HandRight, J.WristRight, '#2f6bff');
    this.flag(p, J.HandLeft, J.WristLeft, '#f4f6fa');
  }

  flag(p, hand, wrist, color) {
    if (p[hand * 2] < 0 || p[wrist * 2] < 0) return;
    const c = this.ctx, m = this.map;
    const hx = this.sx(p[hand * 2]), hy = this.sy(p[hand * 2 + 1]);
    const wx = this.sx(p[wrist * 2]), wy = this.sy(p[wrist * 2 + 1]);
    let dx = hx - wx, dy = hy - wy;
    const len = Math.hypot(dx, dy) || 1;
    dx /= len; dy /= len;
    const pole = 80 * m.unit, fw = 46 * m.unit, fh = 34 * m.unit;
    const tx = hx + dx * pole, ty = hy + dy * pole;     // 깃대 끝
    c.strokeStyle = '#c9a66b';
    c.lineWidth = 6 * m.unit;
    c.beginPath(); c.moveTo(hx, hy); c.lineTo(tx, ty); c.stroke();
    // 깃발: 깃대 끝에서 바깥쪽으로
    // 깃발은 몸 바깥쪽으로 펼침
    const nx = -dy, ny = dx, side = (nx * (hx - this.sx(p[J.SpineChest * 2])) >= 0) ? 1 : -1;
    const ox = nx * fw * side, oy = ny * fw * side;
    c.fillStyle = color;
    c.strokeStyle = 'rgba(0,0,0,.35)';
    c.lineWidth = 2 * m.dpr;
    c.beginPath();
    c.moveTo(tx, ty);
    c.lineTo(tx + ox, ty + oy);
    c.lineTo(tx + ox - dx * fh, ty + oy - dy * fh);
    c.lineTo(tx - dx * fh, ty - dy * fh);
    c.closePath();
    c.fill(); c.stroke();
  }
}
