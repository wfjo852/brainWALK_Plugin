// ─────────────────────────────────────────────────────────────
//  게임 공통: 배경음악 (BGM)
//
//  곡 목록: games/_bgm/tracks.json (곡 추가·라이선스는 그 파일만 고치면 됨)
//    - 게임 고르기 화면: tracks.json 의 lobby 곡을 계속 반복
//    - 게임 화면      : lobby 곡을 뺀 나머지에서 무작위, 한 곡이 끝나면 다른 곡으로
//    - 라이선스가 있는 곡은 화면 아래(푸터 바로 위)에 credit 문구를 작게 표시
//    - 음성 안내가 나오는 동안은 음악을 작게 줄임 (말이 잘 들리게)
//    - 브라우저가 소리를 막고 있으면 화면을 처음 누를 때 시작
//
//  GameShell 이 알아서 켭니다:  new GameShell({ bgm: 'lobby' })   게임 고르기 화면
//                              new GameShell({ ... })               게임 화면 (기본: 무작위)
//                              new GameShell({ bgm: false })        음악 없음
// ─────────────────────────────────────────────────────────────
import { Sound } from './sound.js';

const BASE = new URL('../_bgm/', import.meta.url);
const DUCK = 0.3;   // 음성 안내 중 음량 비율

export class Bgm {
  /** mode: 'lobby' | 'random' | 곡 id */
  constructor(mode = 'random') {
    this.mode = mode;
    this.audio = new Audio();
    this.audio.preload = 'auto';
    this.volume = 0.3;
    this.level = 0;            // 지금 음량 (부드럽게 바뀜)
    this.track = null;
    this.list = [];
    this.credit = document.createElement('div');
    this.credit.className = 'bgm-credit hidden';
    document.body.append(this.credit);
    this.audio.addEventListener('ended', () => this.next());
    this.ready = this.load();
  }

  async load() {
    let cfg;
    try { cfg = await (await fetch(new URL('tracks.json', BASE))).json(); }
    catch (e) { console.warn('[BGM] 곡 목록을 못 읽음', e); return; }
    this.volume = cfg.volume ?? this.volume;
    const all = cfg.tracks ?? [];
    if (this.mode === 'lobby') this.list = all.filter(t => t.id === cfg.lobby);
    else if (this.mode === 'random') this.list = all.filter(t => t.id !== cfg.lobby);
    else this.list = all.filter(t => t.id === this.mode);
    if (!this.list.length) this.list = all;
    this.audio.loop = this.list.length === 1;
    this.next();
    // 음량: 음성 안내 중이면 줄이고, 아니면 원래대로 (부드럽게)
    setInterval(() => {
      const target = this.volume * (Sound.speaking() ? DUCK : 1);
      this.level += (target - this.level) * 0.35;
      this.audio.volume = Math.max(0, Math.min(1, this.level));
    }, 100);
  }

  /** 다음 곡 (무작위, 방금 곡은 피함) */
  next() {
    if (!this.list.length) return;
    const pool = this.list.length > 1 ? this.list.filter(t => t !== this.track) : this.list;
    this.track = pool[Math.floor(Math.random() * pool.length)];
    this.audio.src = new URL(this.track.file, BASE).href;
    this.credit.textContent = this.track.credit ? `♪ ${this.track.credit}` : '';
    this.credit.classList.toggle('hidden', !this.track.credit);
    this.play();
  }

  play() {
    this.audio.play().catch(() => {
      // 소리가 막혀 있음 → 화면을 처음 누르거나 키를 누를 때 시작
      const go = () => { removeEventListener('pointerdown', go); removeEventListener('keydown', go); this.audio.play().catch(() => {}); };
      addEventListener('pointerdown', go); addEventListener('keydown', go);
    });
  }

  stop() { this.audio.pause(); }
}
