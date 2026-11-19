// ─────────────────────────────────────────────────────────────
//  게임 공통: 팔·다리 동작 판정
//
//  사람 몸 기준으로 판단합니다 (카메라 기울기·키와 상관없이 동작):
//   - "위쪽" = 골반 → 목 방향, 길이 기준 = 몸통 길이
//   어르신 기준 (어깨·고관절 가동 범위가 작아도 인정되도록):
//   - 팔 올림:  손목(또는 손)이 어깨보다 몸통 길이의 10% 이상 위 (약 5cm)
//   - 팔 내림:  손목이 어깨보다 몸통 길이의 30% 이상 아래
//   - 다리 들기: 한쪽 발목(또는 무릎)이 반대쪽보다 몸통 길이의 14% 이상 위 (약 7cm)
//   - 다리 내림: 두 발목 높이 차이가 몸통 길이의 10% 이하
//   - 팔 옆으로: 올림은 아니고, 손목이 어깨 높이 근처(내림보다 위)에서 몸 바깥쪽으로 팔 길이의 55% 이상 나감
//  그 밖의 중간 자세는 "애매함" — 아무것도 아님.
//  quality(0~1): 얼마나 확실하게 했는지 (팔은 머리 높이까지 들면 1, 옆으로는 쭉 펴면 1, 다리는 발을 약 20cm 들면 1)
// ─────────────────────────────────────────────────────────────
import { J } from './femto-client.js';

export const LIMBS = ['RA', 'LA', 'RL', 'LL'];
export const LIMB_NAME = { RA: '오른팔', LA: '왼팔', RL: '오른발', LL: '왼발' };

const TH = {
  armUp: 0.10, armUpOff: 0.04,       // 올림 판정 / 올림 해제 (흔들림 방지용 간격)
  armDown: -0.30,
  legUp: 0.14, legUpOff: 0.08,
  legDown: 0.10,
  side: 0.55, sideOff: 0.45,          // 팔 옆으로: 바깥쪽으로 나간 정도(팔 길이 비율)
};

export class PoseReader {
  constructor() {
    this.up = { RA: false, LA: false, RL: false, LL: false };
    this.side = { RA: false, LA: false };
    this.state = {};
  }

  /** body(femto-client Body) → { RA:{up, down, q}, LA, RL, LL, torso } */
  read(body) {
    const p = i => body.pos(i);
    const pel = p(J.Pelvis), neck = p(J.Neck);
    let ux = neck.x - pel.x, uy = neck.y - pel.y, uz = neck.z - pel.z;
    const torso = Math.hypot(ux, uy, uz) || 0.5;
    ux /= torso; uy /= torso; uz /= torso;
    const h = i => { const q = p(i); return q.x * ux + q.y * uy + q.z * uz; };   // 몸 위쪽 방향 높이
    const head = h(J.Head);

    // 몸의 오른쪽 방향 (왼쪽 어깨 → 오른쪽 어깨)
    const sR = p(J.ShoulderRight), sL = p(J.ShoulderLeft);
    let rx = sR.x - sL.x, ry = sR.y - sL.y, rz = sR.z - sL.z;
    const rn = Math.hypot(rx, ry, rz) || 1; rx /= rn; ry /= rn; rz /= rn;
    const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);

    const arm = (key, shoulder, elbow, wrist, hand, out) => {
      const rel = (Math.max(h(wrist), h(hand)) - h(shoulder)) / torso;
      const was = this.up[key];
      const up = was ? rel > TH.armUpOff : rel > TH.armUp;
      this.up[key] = up;
      const reach = (head - h(shoulder)) / torso;                         // 머리 높이 = 다 들었음
      // 옆으로: 손목이 몸 바깥쪽으로 얼마나 나갔는지 (팔 길이 비율)
      const S = p(shoulder), E = p(elbow), W = p(wrist);
      const len = dist(S, E) + dist(E, W) || 0.5;
      const outward = out * ((W.x - S.x) * rx + (W.y - S.y) * ry + (W.z - S.z) * rz) / len;
      const side = !up && rel > TH.armDown && (this.side[key] ? outward > TH.sideOff : outward > TH.side);
      this.side[key] = side;
      return { up, side, down: rel < TH.armDown, q: up ? clamp01(rel / reach) : side ? clamp01(outward / 0.85) : 0, rel, outward };
    };
    const leg = (key, ankle, knee, otherAnkle, otherKnee) => {
      const lift = Math.max(h(ankle) - h(otherAnkle), (h(knee) - h(otherKnee)) * 1.2) / torso;
      const was = this.up[key];
      const up = was ? lift > TH.legUpOff : lift > TH.legUp;
      this.up[key] = up;
      return { up, down: Math.abs(h(ankle) - h(otherAnkle)) / torso < TH.legDown && lift < TH.legDown, q: clamp01(lift / 0.4), rel: lift };
    };

    const s = {
      RA: arm('RA', J.ShoulderRight, J.ElbowRight, J.WristRight, J.HandRight, +1),
      LA: arm('LA', J.ShoulderLeft, J.ElbowLeft, J.WristLeft, J.HandLeft, -1),
      RL: leg('RL', J.AnkleRight, J.KneeRight, J.AnkleLeft, J.KneeLeft),
      LL: leg('LL', J.AnkleLeft, J.KneeLeft, J.AnkleRight, J.KneeRight),
      torso,
    };
    this.state = s;
    return s;
  }
}

/** 지금 올라가 있는 팔다리 목록 */
export function raisedLimbs(s) { return LIMBS.filter(k => s[k]?.up); }

/** 모두 내렸는지 (원위치). side: 팔 옆으로 벌린 것도 "안 내림"으로 볼지 (팔만 모드) */
export function isNeutral(s, { side = false } = {}) { return LIMBS.every(k => !s[k]?.up && !(side && s[k]?.side)); }

/** 팔다리 하나의 지금 자세: 'up' | 'side' | 'down' | 'mid'(애매함). side: 옆으로 자세를 따로 볼지 */
export function limbState(s, k, { side = false } = {}) {
  const L = s[k];
  if (!L) return 'mid';
  if (L.up) return 'up';
  if (side && L.side) return 'side';
  if (L.down || k === 'RL' || k === 'LL') return 'down';   // 다리는 안 들면 내림
  return 'mid';
}

function clamp01(v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
