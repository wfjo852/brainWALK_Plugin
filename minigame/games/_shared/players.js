// ─────────────────────────────────────────────────────────────
//  게임 공통: 1~4인 자리 배정과 추적
//
//  - 시작할 때 화면에 보이는 사람 중 카메라에 가까운 N명을 골라, 화면 왼쪽부터 P1, P2 … 로 배정
//  - 게임 중 트래킹이 끊겼다 다시 잡히면(번호가 바뀜) 같은 사람(등록 id) 또는 원래 자리 근처의 사람으로 다시 연결
//  - 이름은 사람 목록(뷰어에서 등록·수정한 이름)을 쓰고, 없으면 "사람 N"
// ─────────────────────────────────────────────────────────────
import { J } from './femto-client.js';
import { PoseReader } from './pose.js';

export const PLAYER_COLORS = ['#ff6b6b', '#4dabf7', '#51cf66', '#fcc419'];

export function normalizePersonName(name) {
  return name?.replace(/^(?:사람|플레이어)\s*(\d+)$/u, '사람 $1');
}

export class Player {
  constructor(index, body, client) {
    this.index = index;
    this.color = PLAYER_COLORS[index];
    this.bodyId = body.id;
    this.pid = body.pid;
    this.fallbackName = `사람 ${index + 1}`;
    this.lastX = body.pos(J.Pelvis).x;
    this.present = true;
    this.reader = new PoseReader();
    this.client = client;
  }
  get name() { return normalizePersonName(this.client.personName(this.pid)) || this.fallbackName; }
  get body() { return this.present ? this.client.bodies.get(this.bodyId) : null; }
}

/** 화면에 보이는 사람 중 시작할 N명 고르기 (가까운 사람 우선) → 화면 왼쪽부터 정렬 */
export function pickBodies(client, n, mirror) {
  const list = [...client.bodies.values()]
    .filter(b => b.conf(J.Pelvis) > 0)
    .sort((a, b) => a.pos(J.Pelvis).z - b.pos(J.Pelvis).z)
    .slice(0, n);
  // 거울 모드면 카메라 x가 작을수록 화면 오른쪽 → 화면 왼쪽부터 P1
  return list.sort((a, b) => (mirror ? -1 : 1) * (a.pos(J.Pelvis).x - b.pos(J.Pelvis).x));
}

export function createPlayers(client, n, mirror) {
  return pickBodies(client, n, mirror).map((b, i) => new Player(i, b, client));
}

/** 매 프레임: 자리를 비운 플레이어를 다시 연결 */
export function trackPlayers(players, client) {
  const taken = new Set(players.filter(p => p.present && client.bodies.has(p.bodyId)).map(p => p.bodyId));
  for (const p of players) {
    const b = client.bodies.get(p.bodyId);
    if (b) {
      p.present = true;
      p.lastX = b.pos(J.Pelvis).x;
      if (b.pid) p.pid = b.pid;
      continue;
    }
    p.present = false;
    // 1) 같은 등록 사람  2) 원래 자리(0.7m 안)에 가장 가까운 사람
    let best = null, bestD = 0.7;
    for (const c of client.bodies.values()) {
      if (taken.has(c.id)) continue;
      if (p.pid && c.pid === p.pid) { best = c; break; }
      const d = Math.abs(c.pos(J.Pelvis).x - p.lastX);
      if (d < bestD) { best = c; bestD = d; }
    }
    if (best) {
      p.bodyId = best.id;
      p.present = true;
      p.reader = new PoseReader();
      taken.add(best.id);
    }
  }
}
