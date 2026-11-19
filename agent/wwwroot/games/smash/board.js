// ─────────────────────────────────────────────────────────────
//  사진 깨기: 깨지는 사진판
//
//  - 사진을 약 180개의 삼각형 조각으로 나눔 (조금씩 흔든 격자 → 자연스러운 깨짐 모양)
//  - 맞으면: 맞은 곳에 금(공) 또는 얼룩(토마토·달걀)이 생기고, 가까운 조각일수록 많이 상함
//  - 다 상한 조각은 떨어져 나감 (조각 그림을 따로 떼어 game.js에서 떨어뜨림)
//  - 맞은 곳에 이미 조각이 없으면 가장 가까운 남은 조각 쪽으로 맞혀 줌 (매번 뭔가 깨지게)
//  - 남은 양이 12% 아래면 남은 조각을 한꺼번에 와장창
// ─────────────────────────────────────────────────────────────
export const FINISH_AT = 0.12;

export class PhotoBoard {
  constructor(img, { pieces = 180 } = {}) {
    const max = 1200, s = Math.min(1, max / Math.max(img.width, img.height));
    this.W = Math.round(img.width * s); this.H = Math.round(img.height * s);
    this.canvas = document.createElement('canvas');
    this.canvas.width = this.W; this.canvas.height = this.H;
    this.g = this.canvas.getContext('2d');
    this.g.drawImage(img, 0, 0, this.W, this.H);
    this.diag = Math.hypot(this.W, this.H);
    this.build(pieces);
  }

  build(n) {
    const aspect = this.W / this.H;
    const cols = Math.max(6, Math.round(Math.sqrt(n / 2 * aspect))), rows = Math.max(4, Math.round(n / 2 / cols));
    const cw = this.W / cols, ch = this.H / rows;
    const V = [];
    for (let r = 0; r <= rows; r++) for (let c = 0; c <= cols; c++) {
      const jx = c > 0 && c < cols ? (Math.random() - 0.5) * 0.76 * cw : 0;
      const jy = r > 0 && r < rows ? (Math.random() - 0.5) * 0.76 * ch : 0;
      V.push([c * cw + jx, r * ch + jy]);
    }
    const at = (c, r) => V[r * (cols + 1) + c];
    this.pieces = [];
    for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) {
      const a = at(c, r), b = at(c + 1, r), d = at(c, r + 1), e = at(c + 1, r + 1);
      const tris = Math.random() < 0.5 ? [[a, b, e], [a, e, d]] : [[a, b, d], [b, e, d]];
      for (const poly of tris) {
        const cx = (poly[0][0] + poly[1][0] + poly[2][0]) / 3, cy = (poly[0][1] + poly[1][1] + poly[2][1]) / 3;
        const area = Math.abs((poly[1][0] - poly[0][0]) * (poly[2][1] - poly[0][1]) - (poly[2][0] - poly[0][0]) * (poly[1][1] - poly[0][1])) / 2;
        this.pieces.push({ poly, cx, cy, area, hp: 1, alive: true });
      }
    }
    this.total = this.pieces.reduce((s, p) => s + p.area, 0);
    this.left = this.total;
  }

  /** 남은 비율 (1 = 그대로, 0 = 다 깨짐) */
  get remain() { return this.left / this.total; }
  get done() { return this.remain <= FINISH_AT; }

  /** 이 위치(0~1)에 맞으면 실제로 맞을 곳 (빈 곳이면 가장 가까운 남은 조각) */
  aimPoint(u, v) {
    let x = u * this.W, y = v * this.H;
    // 남은 조각들 가운데 쪽으로 살짝 끌어당김 (구석만 남아 오래 걸리지 않게)
    let mx = 0, my = 0, ma = 0;
    for (const p of this.pieces) if (p.alive) { mx += p.cx * p.area; my += p.cy * p.area; ma += p.area; }
    if (ma) { x += (mx / ma - x) * 0.2; y += (my / ma - y) * 0.2; }
    const R = this.radius(0.5);
    let near = null, nd = Infinity;
    for (const p of this.pieces) {
      if (!p.alive) continue;
      const d = Math.hypot(p.cx - x, p.cy - y);
      if (d < nd) { nd = d; near = p; }
    }
    if (near && nd > R * 0.45) { x = near.cx; y = near.cy; }
    return { x, y, u: x / this.W, v: y / this.H };
  }

  radius(power) { return (0.11 + 0.09 * power) * this.diag; }

  /**
   * 맞히기. x, y = aimPoint 결과(사진 픽셀), power 0~1, kind = 'ball' | 'tomato' | 'egg'
   * 반환: 떨어져 나간 조각들 [{canvas, cx, cy, ox, oy}] (cx,cy = 조각 중심, ox,oy = 그림 왼쪽 위 기준 중심 위치)
   */
  hit(x, y, power, kind) {
    const R = this.radius(power), g = this.g;
    g.save();
    g.globalCompositeOperation = 'source-atop';      // 남아 있는 사진 위에만 그림
    if (kind === 'ball') this.cracks(x, y, R, power);
    else this.splat(x, y, R, kind);
    g.restore();

    // 조각 상하게 하기 (가까울수록 많이). 가장 가까운 조각은 반드시 떨어짐
    let nearest = null, nd = Infinity;
    const out = [];
    for (const p of this.pieces) {
      if (!p.alive) continue;
      const d = Math.hypot(p.cx - x, p.cy - y);
      if (d < nd) { nd = d; nearest = p; }
      if (d >= R) continue;
      const k = 1 - Math.pow(d / R, 1.5);
      p.hp -= power * 1.4 * k + 0.25 * k;
    }
    if (nearest) nearest.hp = Math.min(nearest.hp, 0);
    for (const p of this.pieces) if (p.alive && p.hp <= 0) out.push(this.detach(p));
    return out;
  }

  /** 남은 조각 모두 떼어내기 (마지막 와장창) */
  explodeAll() {
    return this.pieces.filter(p => p.alive).map(p => this.detach(p));
  }

  detach(p) {
    p.alive = false;
    this.left -= p.area;
    // 조각 그림 떼어내기
    const xs = p.poly.map(v => v[0]), ys = p.poly.map(v => v[1]);
    const x0 = Math.floor(Math.min(...xs)) - 2, y0 = Math.floor(Math.min(...ys)) - 2;
    const w = Math.ceil(Math.max(...xs)) + 2 - x0, h = Math.ceil(Math.max(...ys)) + 2 - y0;
    const c = document.createElement('canvas');
    c.width = Math.max(1, w); c.height = Math.max(1, h);
    const fg = c.getContext('2d');
    fg.beginPath();
    p.poly.forEach(([vx, vy], i) => (i ? fg.lineTo(vx - x0, vy - y0) : fg.moveTo(vx - x0, vy - y0)));
    fg.closePath();
    fg.save(); fg.clip();
    fg.drawImage(this.canvas, x0, y0, w, h, 0, 0, w, h);
    fg.restore();
    fg.strokeStyle = 'rgba(255,255,255,.55)'; fg.lineWidth = 1.5; fg.stroke();   // 깨진 가장자리
    // 사진판에서 지우기 (이음새가 남지 않게 살짝 넓게)
    const g = this.g;
    g.save();
    g.globalCompositeOperation = 'destination-out';
    g.beginPath();
    p.poly.forEach(([vx, vy], i) => (i ? g.lineTo(vx, vy) : g.moveTo(vx, vy)));
    g.closePath();
    g.fill(); g.lineWidth = 1.6; g.stroke();
    g.restore();
    return { canvas: c, cx: p.cx, cy: p.cy, ox: p.cx - x0, oy: p.cy - y0 };
  }

  // 공: 유리처럼 퍼지는 금
  cracks(x, y, R, power) {
    const g = this.g, n = 7 + Math.round(power * 6);
    const rays = [];
    for (let i = 0; i < n; i++) {
      let a = (i / n) * Math.PI * 2 + Math.random() * 0.5, px = x, py = y;
      const len = R * (0.6 + Math.random() * 0.8), pts = [[px, py]];
      for (let s = 0; s < 6; s++) {
        a += (Math.random() - 0.5) * 0.6;
        px += Math.cos(a) * len / 6; py += Math.sin(a) * len / 6;
        pts.push([px, py]);
      }
      rays.push(pts);
    }
    const line = (w, col, off) => {
      g.lineWidth = w; g.strokeStyle = col; g.lineJoin = 'round';
      for (const pts of rays) {
        g.beginPath();
        pts.forEach(([px, py], i) => (i ? g.lineTo(px + off, py + off) : g.moveTo(px + off, py + off)));
        g.stroke();
      }
      // 동그란 금
      g.beginPath();
      g.arc(x + off, y + off, R * 0.22, 0, Math.PI * 2);
      g.stroke();
    };
    line(5, 'rgba(0,0,0,.45)', 1.5);
    line(2, 'rgba(255,255,255,.85)', 0);
    // 맞은 자리 움푹
    const rg = g.createRadialGradient(x, y, 0, x, y, R * 0.25);
    rg.addColorStop(0, 'rgba(0,0,0,.35)'); rg.addColorStop(1, 'rgba(0,0,0,0)');
    g.fillStyle = rg;
    g.beginPath(); g.arc(x, y, R * 0.25, 0, Math.PI * 2); g.fill();
  }

  // 토마토: 빨간 얼룩, 달걀: 흰자 + 노른자
  splat(x, y, R, kind) {
    const g = this.g, r0 = R * 0.3;
    const blob = (bx, by, r, col) => {
      g.fillStyle = col;
      g.beginPath();
      const n = 14;
      for (let i = 0; i <= n; i++) {
        const a = (i / n) * Math.PI * 2, rr = r * (0.75 + Math.random() * 0.45);
        const px = bx + Math.cos(a) * rr, py = by + Math.sin(a) * rr;
        i ? g.lineTo(px, py) : g.moveTo(px, py);
      }
      g.closePath(); g.fill();
    };
    const main = kind === 'tomato' ? 'rgba(214,40,40,.92)' : 'rgba(250,250,240,.92)';
    blob(x, y, r0, main);
    for (let i = 0; i < 12; i++) {   // 튄 방울
      const a = Math.random() * Math.PI * 2, d = r0 * (1 + Math.random() * 1.8);
      blob(x + Math.cos(a) * d, y + Math.sin(a) * d, r0 * (0.08 + Math.random() * 0.18), main);
    }
    // 흘러내림
    for (let i = 0; i < 4; i++) {
      const sx = x + (Math.random() - 0.5) * r0 * 1.2;
      g.fillStyle = main;
      g.fillRect(sx, y, r0 * 0.12, r0 * (0.8 + Math.random() * 1.4));
    }
    if (kind === 'tomato') {
      blob(x - r0 * 0.15, y - r0 * 0.15, r0 * 0.35, 'rgba(255,120,90,.8)');
      g.fillStyle = 'rgba(60,140,40,.95)';   // 꼭지
      g.beginPath(); g.ellipse(x + r0 * 0.3, y - r0 * 0.25, r0 * 0.18, r0 * 0.07, 0.6, 0, Math.PI * 2); g.fill();
    } else {
      blob(x, y, r0 * 0.38, 'rgba(255,190,30,.97)');
      g.fillStyle = 'rgba(255,255,255,.7)';
      g.beginPath(); g.arc(x - r0 * 0.1, y - r0 * 0.1, r0 * 0.08, 0, Math.PI * 2); g.fill();
    }
  }
}
