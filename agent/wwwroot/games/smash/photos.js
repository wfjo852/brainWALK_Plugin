// ─────────────────────────────────────────────────────────────
//  사진 깨기: 사진 보관함
//
//  - 올린 사진은 이 PC의 브라우저 저장소(IndexedDB)에 보관 → 다음에 켜도 그대로 (인터넷으로 나가지 않음)
//  - 큰 사진은 긴 쪽 1280px로 줄여서 저장 (휴대폰 사진도 가볍게)
//  - "글자로 만들기": "스트레스", "걱정" 같은 글자를 큰 그림으로 만들어 저장
//  - 처음엔 글자 그림 3장을 넣어 둠
// ─────────────────────────────────────────────────────────────
const DB_NAME = 'femto-smash', STORE = 'photos', MAX_PHOTOS = 16;
let memory = null;   // 저장소를 못 쓸 때(드문 경우) 임시 보관

function openDb() {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE, { keyPath: 'id' });
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function tx(mode, fn) {
  if (memory) return fn(null);
  try {
    const db = await openDb();
    return await new Promise((resolve, reject) => {
      const t = db.transaction(STORE, mode), st = t.objectStore(STORE);
      const r = fn(st);
      t.oncomplete = () => resolve(r?.result ?? r);
      t.onerror = () => reject(t.error);
    });
  } catch {
    memory = [];
    return fn(null);
  }
}

/** 보관한 사진 목록 (최근 것 먼저) */
export async function listPhotos() {
  const all = await tx('readonly', st => (st ? st.getAll() : memory));
  return [...(all ?? [])].sort((a, b) => b.created - a.created);
}

async function put(item) {
  await tx('readwrite', st => (st ? st.put(item) : memory.push(item)));
  // 너무 많으면 오래된 것부터 정리
  const list = await listPhotos();
  for (const old of list.slice(MAX_PHOTOS)) await removePhoto(old.id);
  return item;
}

export async function removePhoto(id) {
  await tx('readwrite', st => {
    if (st) return st.delete(id);
    memory = memory.filter(p => p.id !== id);
    return null;
  });
}

const newId = () => Date.now().toString(36) + Math.random().toString(36).slice(2, 6);

/** 사진 파일 올리기 (jpg, png, heic는 브라우저가 읽을 수 있을 때만) */
export async function addImageFile(file) {
  if (!file?.type?.startsWith('image/')) throw new Error('사진 파일이 아니에요');
  let bmp;
  try { bmp = await createImageBitmap(file, { imageOrientation: 'from-image' }); }
  catch { bmp = await createImageBitmap(file); }
  const data = toJpeg(bmp, 1280, 0.88), thumb = toJpeg(bmp, 320, 0.8);
  bmp.close?.();
  return put({ id: newId(), kind: 'photo', name: file.name.replace(/\.[^.]+$/, ''), data, thumb, created: Date.now() });
}

/** 글자로 그림 만들기 */
export async function addText(text) {
  text = text.trim().slice(0, 20);
  if (!text) throw new Error('글자를 입력해 주세요');
  const c = makeTextImage(text);
  return put({ id: newId(), kind: 'text', name: text, data: c.toDataURL('image/jpeg', 0.9), thumb: toJpeg(c, 320, 0.8), created: Date.now() });
}

/** 처음 켰을 때 기본 그림 */
export async function ensureDefaults() {
  const list = await listPhotos();
  if (list.length) return list;
  for (const t of ['짜증', '걱정 근심', '스트레스']) await addText(t);   // 마지막 것이 맨 앞
  return listPhotos();
}

export function loadImage(src) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error('사진을 열 수 없어요'));
    img.src = src;
  });
}

function toJpeg(src, max, q) {
  const w = src.width, h = src.height, s = Math.min(1, max / Math.max(w, h));
  const c = document.createElement('canvas');
  c.width = Math.round(w * s); c.height = Math.round(h * s);
  const g = c.getContext('2d');
  g.fillStyle = '#fff'; g.fillRect(0, 0, c.width, c.height);   // 투명 PNG는 흰 바탕
  g.drawImage(src, 0, 0, c.width, c.height);
  return c.toDataURL('image/jpeg', q);
}

const PALETTES = [
  ['#ff8a5b', '#ff5d6c'], ['#5b8cff', '#7a5bff'], ['#3ddc97', '#2bb3c0'],
  ['#f5b84b', '#ff7b54'], ['#c86bfa', '#ff6bb5'], ['#5c6b7f', '#2f3a4a'],
];

function makeTextImage(text) {
  const W = 1200, H = 800;
  const c = document.createElement('canvas');
  c.width = W; c.height = H;
  const g = c.getContext('2d');
  const [a, b] = PALETTES[Math.floor(Math.random() * PALETTES.length)];
  const grad = g.createLinearGradient(0, 0, W, H);
  grad.addColorStop(0, a); grad.addColorStop(1, b);
  g.fillStyle = grad; g.fillRect(0, 0, W, H);
  // 바탕 무늬
  g.globalAlpha = 0.12; g.fillStyle = '#fff';
  for (let i = 0; i < 26; i++) {
    g.beginPath();
    g.arc(Math.random() * W, Math.random() * H, 20 + Math.random() * 90, 0, Math.PI * 2);
    g.fill();
  }
  g.globalAlpha = 1;
  // 큰 글자 (줄바꿈: 띄어쓰기 기준 최대 2줄)
  const words = text.split(/\s+/);
  const lines = words.length > 1 && text.length > 5 ? [words.slice(0, Math.ceil(words.length / 2)).join(' '), words.slice(Math.ceil(words.length / 2)).join(' ')] : [text];
  let size = 300;
  const font = s => `900 ${s}px "Malgun Gothic", "Segoe UI", sans-serif`;
  g.font = font(size);
  while (size > 60 && (Math.max(...lines.map(l => g.measureText(l).width)) > W * 0.86 || size * lines.length * 1.1 > H * 0.8)) { size -= 10; g.font = font(size); }
  g.textAlign = 'center'; g.textBaseline = 'middle';
  lines.forEach((l, i) => {
    const y = H / 2 + (i - (lines.length - 1) / 2) * size * 1.08;
    g.lineWidth = size * 0.08; g.strokeStyle = 'rgba(0,0,0,.25)'; g.strokeText(l, W / 2, y + size * 0.04);
    g.fillStyle = '#fff'; g.fillText(l, W / 2, y);
  });
  return c;
}
