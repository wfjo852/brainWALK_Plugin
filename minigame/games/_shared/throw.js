// ─────────────────────────────────────────────────────────────
//  게임 공통: "던지기" 동작 알아채기
//
//  손이 몸(가슴) 기준으로 카메라 쪽(앞)으로 빠르게 나가면 던진 것으로 봅니다.
//   - 앞으로 나가는 빠르기: 최근 0.12초 동안 평균 (m/s)
//   - 앞으로 나간 거리: 최근 0.5초 동안 가장 뒤에 있던 곳부터 지금까지 (m)
//   - 손이 배꼽보다 위 (밑으로 던지는 것도 인정)
//   - 한 손으로 연달아 던지려면 0.6초 쉼
//  몸 기준이라 걸어서 다가오는 건 던지기로 치지 않습니다.
//
//  const det = new ThrowDetector({ gentle: true });
//  매 프레임: for (const t of det.update(client.bodies, now)) { t.bodyId, t.hand('R'|'L'), t.power(0~1), t.aim{nx,ny} }
// ─────────────────────────────────────────────────────────────
import { J } from './femto-client.js';

/** 어르신 기준: 살살 던져도 인정 / 세게 던져야 인정 */
export const THROW_LEVELS = {
  gentle: { speed: 0.75, travel: 0.14 },
  strong: { speed: 1.2, travel: 0.22 },
};

export class ThrowDetector {
  constructor({ gentle = true, mirror = true } = {}) {
    this.cfg = gentle ? THROW_LEVELS.gentle : THROW_LEVELS.strong;
    this.mirror = mirror;
    this.hist = new Map();      // "bodyId:R" → [{t, x, y, z}] (가슴 기준)
    this.cool = new Map();      // "bodyId:R" → 다시 던질 수 있는 시각
  }

  setGentle(g) { this.cfg = g ? THROW_LEVELS.gentle : THROW_LEVELS.strong; }

  /** 이번 프레임에 던진 것들 */
  update(bodies, now) {
    const out = [];
    for (const body of bodies.values()) {
      if (body.conf(J.SpineChest) <= 0) continue;
      const chest = body.pos(J.SpineChest), navel = body.pos(J.SpineNavel);
      const sR = body.pos(J.ShoulderRight), sL = body.pos(J.ShoulderLeft);
      const shoulderY = (sR.y + sL.y) / 2;
      for (const [hand, wrist, side] of [[J.HandRight, J.WristRight, 'R'], [J.HandLeft, J.WristLeft, 'L']]) {
        if (body.conf(wrist) <= 0) continue;
        const key = body.id + ':' + side;
        const w = body.pos(wrist), h = body.conf(hand) > 0 ? body.pos(hand) : w;
        const pt = { t: now, x: h.x - chest.x, y: h.y - chest.y, z: h.z - chest.z, ay: h.y };
        const hs = this.hist.get(key) ?? [];
        hs.push(pt);
        while (hs.length && now - hs[0].t > 0.6) hs.shift();
        this.hist.set(key, hs);
        if ((this.cool.get(key) ?? 0) > now) continue;
        if (hs.length < 4) continue;

        // 0.12초 전 위치와 비교한 앞으로(카메라 쪽 = z 감소) 빠르기
        let old = hs[0];
        for (const q of hs) { if (now - q.t >= 0.12) old = q; else break; }
        const dt = now - old.t;
        if (dt < 0.06) continue;
        const fwd = -(pt.z - old.z) / dt;
        // 최근 0.5초 동안 가장 뒤(z 큼)에서 지금까지 앞으로 나간 거리
        let back = pt.z;
        for (const q of hs) if (now - q.t <= 0.5) back = Math.max(back, q.z);
        const travel = back - pt.z;
        if (fwd < this.cfg.speed || travel < this.cfg.travel) continue;
        if (h.y < navel.y) continue;                                   // 손이 너무 아래 (팔을 내리는 중)

        // 겨냥: 던진 손이 몸 기준 어디에 있었는지 + 옆으로 움직인 방향 → 사진 위 위치 (0~1)
        const vx = (pt.x - old.x) / dt, vy = (pt.y - old.y) / dt;
        let sx = pt.x + vx * 0.12;
        if (this.mirror) sx = -sx;                                     // 거울: 사람 오른쪽 = 화면 오른쪽
        const nx = clamp01(0.5 + sx / 0.9);
        const ny = clamp01(0.5 - (h.y - shoulderY - 0.08 + vy * 0.08) / 0.7);   // 어깨보다 조금 위에서 던지면 가운데
        const power = clamp01(0.35 + 0.65 * Math.min(1, (fwd - this.cfg.speed) / 1.6 + (travel - this.cfg.travel) / 0.6));
        out.push({ bodyId: body.id, hand: side, handJoint: hand, power, speed: fwd, aim: { nx, ny } });
        this.cool.set(key, now + 0.6);
        this.hist.set(key, [pt]);                                      // 다시 뒤로 뺐다 던져야 다음 던지기
      }
    }
    // 사라진 사람 기록 정리
    for (const key of this.hist.keys()) if (!bodies.has(+key.split(':')[0])) { this.hist.delete(key); this.cool.delete(key); }
    return out;
  }
}

function clamp01(v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
