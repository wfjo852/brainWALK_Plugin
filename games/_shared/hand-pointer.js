// ─────────────────────────────────────────────────────────────
//  게임 공통: 손으로 고르기 (마우스 없이)
//
//  - 손을 가슴 높이 이상으로 들면 화면에 손 커서가 나타납니다.
//  - 화면 전체를 "몸 앞에서 손이 닿는 범위"에 맞춰 놓아서, 제자리에 서서 팔만 움직여도 화면 끝까지 갑니다.
//  - 마우스·터치·키보드(←→↑↓, Enter)로도 똑같이 고를 수 있습니다 (보호자·관리자용).
//
//  손 커서 = 손 모양(✋) + 배경(원) + 테두리 + 테두리 인디케이터(테두리를 따라 차오르는 초록 줄)
//
//  상태 (class="hand-target" 인 버튼·카드 기준)
//    기본   : 버튼 밖                      → 어두운 배경, 흰 테두리
//    올라감 : 버튼 위 (손이 움직이는 중)    → 배경이 연한 초록색            (커서에 .hover)
//    고정   : 버튼 위에서 손이 멈춤         → 테두리 인디케이터가 차오름    (커서에 .locked)
//             다 차면(dwell 초) 클릭 이벤트. 그사이 손이 크게 움직이면 인디케이터는 처음부터
//
//  const pointer = new HandPointer({ client, mirror: true, dwell: 1.5, onSelect: el => sound.tick() });
//  매 프레임: pointer.update(dt)
// ─────────────────────────────────────────────────────────────
import { J } from './femto-client.js';

// 손이 닿는 범위 (가슴 기준, 미터). 어르신 어깨 가동 범위를 고려해 작게 잡음
export const ZONE = { halfWidth: 0.42, up: 0.42, down: 0.30, sideShift: 0.12 };

/** 몸 기준 손 위치 → 화면 비율(0~1). 손을 안 들었으면 null */
export function handToScreen(body, mirror = true) {
  const chest = body.pos(J.SpineChest);
  let best = null;
  for (const [hand, side] of [[J.HandRight, -1], [J.HandLeft, +1]]) {   // 사람 오른쪽 = 카메라 x 음수
    if (body.conf(hand) <= 0) continue;
    const h = body.pos(hand);
    const dy = h.y - chest.y;
    if (dy < -ZONE.down * 0.7) continue;                                  // 손을 내리고 있음
    if (!best || dy > best.dy) best = { dy, dx: h.x - (chest.x + side * ZONE.sideShift), hand };
  }
  if (!best) return null;
  const sx = mirror ? -best.dx : best.dx;
  return {
    nx: clamp01(0.5 + sx / (2 * ZONE.halfWidth)),
    ny: clamp01(1 - (best.dy + ZONE.down) / (ZONE.up + ZONE.down)),
    hand: best.hand,
  };
}

/** (시험용) 화면 비율 → 손을 놓아야 할 위치 (가슴 기준 카메라 좌표, 미터) */
export function screenToHand(nx, ny, side = -1, mirror = true) {
  const sx = (nx - 0.5) * 2 * ZONE.halfWidth;
  return { dx: (mirror ? -sx : sx) + side * ZONE.sideShift, dy: (1 - ny) * (ZONE.up + ZONE.down) - ZONE.down };
}

export class HandPointer {
  /**
   * dwell       : 고정된 뒤 클릭까지 걸리는 초 (테두리 인디케이터가 다 차는 시간)
   * settle      : 손이 이만큼(초) 멈춰 있어야 "고정"으로 봄
   * steadyRadius: 이 안에서 움직이면 멈춘 것으로 봄 (화면 짧은 변의 비율, 손 떨림 허용)
   */
  constructor({ client, mirror = true, dwell = 1.5, settle = 0.25, steadyRadius = 0.04, getBody = null, onSelect = null } = {}) {
    this.client = client;
    this.mirror = mirror;
    this.dwell = dwell;
    this.settle = settle;
    this.steadyRadius = steadyRadius;
    this.getBody = getBody;     // 누가 조작할지 (없으면 카메라에 가장 가까운 사람 중 손 든 사람)
    this.onSelect = onSelect;
    this.enabled = true;
    this.x = -1; this.y = -1; this.visible = false;
    this.target = null; this.progress = 0; this.cooldown = 0;
    this.anchor = null; this.still = 0;   // 고정 판정: 기준점과 멈춘 시간

    this.el = document.createElement('div');
    this.el.className = 'hand-cursor hidden';
    this.el.innerHTML = `<svg viewBox="0 0 100 100">`
      + `<circle class="bg" cx="50" cy="50" r="44"/>`          // 배경
      + `<circle class="border" cx="50" cy="50" r="44"/>`      // 테두리
      + `<circle class="indicator" cx="50" cy="50" r="44"/>`   // 테두리 인디케이터
      + `</svg><span>✋</span>`;                                // 손 모양
    document.body.appendChild(this.el);
    this.indicator = this.el.querySelector('.indicator');

    // 키보드: 화살표로 이동, Enter로 선택
    addEventListener('keydown', e => {
      if (!this.enabled) return;
      const list = this.targets();
      if (!list.length) return;
      let i = list.indexOf(document.activeElement);
      if (['ArrowRight', 'ArrowDown'].includes(e.key)) { list[(i + 1) % list.length].focus(); e.preventDefault(); }
      if (['ArrowLeft', 'ArrowUp'].includes(e.key)) { list[(i - 1 + list.length) % list.length].focus(); e.preventDefault(); }
    });
  }

  targets() {
    return [...document.querySelectorAll('.hand-target')].filter(el => el.offsetParent !== null && !el.disabled);
  }

  pickBody() {
    if (this.getBody) return this.getBody();
    let best = null, bestZ = Infinity;
    for (const b of this.client.bodies.values()) {
      if (!handToScreen(b, this.mirror)) continue;
      const z = b.pos(J.Pelvis).z;
      if (z < bestZ) { best = b; bestZ = z; }
    }
    return best;
  }

  update(dt) {
    if (!this.enabled) { this.hide(); return; }
    this.cooldown = Math.max(0, this.cooldown - dt);
    const body = this.pickBody();
    const p = body && handToScreen(body, this.mirror);
    if (!p) { this.hide(); return; }

    const tx = p.nx * innerWidth, ty = p.ny * innerHeight;
    if (!this.visible) { this.x = tx; this.y = ty; }
    this.x += (tx - this.x) * Math.min(1, dt * 12);   // 손 떨림에도 커서가 부드럽게
    this.y += (ty - this.y) * Math.min(1, dt * 12);
    this.visible = true;
    this.el.classList.remove('hidden');
    this.el.style.transform = `translate(${this.x}px, ${this.y}px)`;

    // 커서 아래 고를 수 있는 것
    let hit = null;
    for (const el of document.elementsFromPoint(this.x, this.y)) {
      const t = el.closest?.('.hand-target');
      if (t && !t.disabled) { hit = t; break; }
    }
    if (hit !== this.target) {
      this.target?.classList.remove('hand-hover');
      this.target = hit;
      this.resetLock();
      hit?.classList.add('hand-hover');
    }

    // 고정 판정: 기준점에서 steadyRadius 안에 settle 초 이상 있으면 "고정"
    let locked = false;
    if (hit) {
      const r = this.steadyRadius * Math.min(innerWidth, innerHeight);
      if (!this.anchor || Math.hypot(this.x - this.anchor.x, this.y - this.anchor.y) > r) this.resetLock();   // 움직임 → 처음부터
      else this.still += dt;
      locked = this.still >= this.settle && this.cooldown === 0;
    }
    if (locked) {
      this.progress += dt / this.dwell;
      if (this.progress >= 1) {
        // 인디케이터가 다 참 → 클릭
        this.progress = 0;
        this.cooldown = 1.5;
        this.still = 0;
        hit.classList.add('hand-picked');
        setTimeout(() => hit.classList.remove('hand-picked'), 400);
        this.el.classList.add('fired');
        setTimeout(() => this.el.classList.remove('fired'), 350);
        this.onSelect?.(hit);
        hit.click();
      }
    } else this.progress = 0;

    this.indicator.style.strokeDashoffset = String(276.5 * (1 - this.progress));
    this.el.classList.toggle('hover', !!hit);          // 올라감: 배경 연한 초록
    this.el.classList.toggle('locked', locked);        // 고정: 테두리 인디케이터 켜짐
  }

  resetLock() {
    this.anchor = { x: this.x, y: this.y };
    this.still = 0;
    this.progress = 0;
  }

  hide() {
    if (!this.visible) return;
    this.visible = false;
    this.el.classList.add('hidden');
    this.el.classList.remove('hover', 'locked');
    this.target?.classList.remove('hand-hover');
    this.target = null;
    this.progress = 0;
    this.anchor = null; this.still = 0;
  }
}

function clamp01(v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
