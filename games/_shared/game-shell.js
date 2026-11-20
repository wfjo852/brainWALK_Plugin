// ─────────────────────────────────────────────────────────────
//  공통 틀 (모든 화면이 같은 모양, GAME_RULEBOOK.md 3장)
//
//   게임 고르기 화면: 안내 문구 밑에 최대 참여 인원, 푸터 = [스켈레톤] [게임 종료]
//     new GameShell({ sim, buttons: ['skeleton', 'quit'] }); shell.playersPicker(el);
//
//   게임 화면:
//   ┌ 헤더: 게임 이름 ───────────────────────────────┐
//   │ 본문: 게임마다 다름 (시작 전 화면 = 제목 · 소제목)     │
//   └ 푸터: [게임 다시 시작]  [스켈레톤]  [게임 나가기] ────────┘   (왼쪽 · 가운데 · 오른쪽)
//
//  공통 설정 (모든 게임이 같이 씀, 이 PC에 저장)
//    settings.players : 최대 인원 1~4 (기본 4). 사람이 이보다 적어도 1명 이상이면 게임 진행
//    settings.skeleton: 뼈대 선 보이기 (기본 숨기기)
//
//  const shell = new GameShell({ name: '청기백기', icon: '🚩', sim: SIM, onHome: () => setPhase('setup') });
//  shell.onChange = () => { ... };               // 인원·스켈레톤이 바뀌었을 때
//  stage.draw({ skeleton: settings.skeleton });
//  createPlayers(client, settings.players, true);
//
//  - 게임 다시 시작: onHome 호출 (이 게임의 첫 화면으로. 게임 중이면 멈추고)
//  - 스켈레톤  : 보이기/숨기기 (settings.skeleton)
//  - 게임 나가기: 게임 고르기 화면으로
//  - 배경음악: bgm 옵션 (기본 'random' = 게임 화면, 'lobby' = 게임 고르기 화면, false = 없음). bgm.js 참고
//  - 게임은 화면을 헤더·푸터 사이에 배치: CSS 변수 var(--shell-top), var(--shell-bottom)
// ─────────────────────────────────────────────────────────────
import { Bgm } from './bgm.js';

const KEY = 'femto.common';
export const MAX_PLAYERS = 4;

/**
 * 공통 설정
 *  players : 최대 인원 (기본 4). 이보다 적게 있어도 1명 이상이면 게임 진행
 *  skeleton: 뼈대 선 보이기
 */
export const settings = { players: MAX_PLAYERS, skeleton: false };
(function load() {
  let saved = null;
  try { saved = JSON.parse(localStorage.getItem(KEY) || 'null'); } catch {}
  if (saved) { Object.assign(settings, saved); return; }
  // 처음: 예전 보기 설정의 스켈레톤 값만 가져옴
  try { const v = JSON.parse(localStorage.getItem('femto.view') || '{}'); if (typeof v.skeleton === 'boolean') settings.skeleton = v.skeleton; } catch {}
})();
settings.players = Math.max(1, Math.min(MAX_PLAYERS, settings.players | 0 || MAX_PLAYERS));

export function saveSettings() {
  try { localStorage.setItem(KEY, JSON.stringify(settings)); } catch {}
}

export class GameShell {
  /**
   * name   : 헤더에 보일 게임 이름
   * icon   : 이름 앞 그림 (선택)
   * sim    : 가상 플레이어 수 (?sim=) — 인원 선택을 이 수까지로 제한, 나가기 주소에 붙임
   * onHome : [게임 다시 시작] 눌렀을 때
   */
  /**
   * buttons: 푸터 버튼 (순서대로)
   *   게임 화면 기본      ['home', 'skeleton', 'exit']  게임 다시 시작 · 스켈레톤 · 게임 나가기
   *   게임 고르기 화면    ['skeleton', 'quit']          스켈레톤 · 게임 종료(프로그램 끄기)
   * name 이 없으면 헤더 없음 (게임 고르기 화면)
   * bgm    : 배경음악 'random'(게임 화면 기본) · 'lobby'(게임 고르기 화면) · 곡 id · false(없음)
   */
  constructor({ name = null, icon = '', sim = 0, onHome = null, buttons = ['home', 'skeleton', 'exit'], bgm = 'random' } = {}) {
    this.sim = sim;
    this.onHome = onHome;
    this.onChange = null;
    this.pickers = [];
    if (sim && settings.players > sim) settings.players = sim;

    const root = document.documentElement;
    root.classList.add('has-shell');
    if (name) {
      this.head = document.createElement('header');
      this.head.className = 'shell-head';
      this.head.innerHTML = `<span class="shell-name">${icon ? icon + ' ' : ''}${esc(name)}</span>`;
      document.body.append(this.head);
    } else root.classList.add('no-shell-head');

    // 자리: 왼쪽 = 게임 다시 시작, 가운데 = 스켈레톤, 오른쪽 = 게임 나가기 / 게임 종료
    const BTN = {
      home: ['l', '<button class="hand-target" data-act="home" title="이 게임의 첫 화면으로 (Esc)">🔄 게임 다시 시작</button>'],
      skeleton: ['c', '<button class="hand-target" data-act="skeleton" title="뼈대 선 보이기/숨기기">🦴 스켈레톤 <small></small></button>'],
      exit: ['r', '<button class="hand-target" data-act="exit" title="게임 고르기 화면으로">🚪 게임 나가기</button>'],
      quit: ['r', '<button class="hand-target" data-act="quit" title="프로그램 끄기">⏻ 게임 종료</button>'],
    };
    this.foot = document.createElement('footer');
    this.foot.className = 'shell-foot';
    const slot = s => buttons.filter(b => BTN[b]?.[0] === s).map(b => BTN[b][1]).join('');
    this.foot.innerHTML = `<div class="l">${slot('l')}</div><div class="c">${slot('c')}</div><div class="r">${slot('r')}</div>`;
    document.body.append(this.foot);

    const on = (act, fn) => { const b = this.foot.querySelector(`[data-act=${act}]`); if (b) b.onclick = fn; };
    on('home', () => this.onHome?.());
    on('skeleton', () => {
      settings.skeleton = !settings.skeleton;
      saveSettings(); this.render(); this.onChange?.();
    });
    on('exit', () => setTimeout(() => (location.href = this.exitUrl), 300));
    on('quit', () => this.confirmQuit());
    this.render();
    this.bgm = bgm ? new Bgm(bgm) : null;
  }

  /** 게임 종료: 한 번 더 물어본 뒤 에이전트에 종료 요청 (게임 창을 닫고 에이전트도 꺼짐) */
  confirmQuit() {
    if (document.querySelector('.shell-quit')) return;
    const box = document.createElement('div');
    box.className = 'shell-quit';
    box.innerHTML = `<div class="panel">
        <h2>게임을 끝낼까요?</h2>
        <p>프로그램이 꺼져요.</p>
        <div class="row"><button class="hand-target yes">⏻ 예, 끝내기</button><button class="hand-target no">↩ 아니요</button></div>
      </div>`;
    document.body.append(box);
    box.querySelector('.no').onclick = () => box.remove();
    box.querySelector('.yes').onclick = async () => {
      box.querySelector('.panel').innerHTML = '<h2>종료하는 중…</h2><p>잠시 뒤 화면이 닫혀요.</p>';
      try {
        const r = await fetch('/api/quit', { method: 'POST', headers: { 'X-Femto': 'quit' } });
        if (!r.ok) throw new Error();
      }
      catch { box.querySelector('.panel').innerHTML = '<h2>종료하지 못했어요</h2><p>에이전트에 연결되어 있지 않아요. Alt+F4 로 창을 닫아 주세요.</p>'; setTimeout(() => box.remove(), 4000); }
    };
  }

  /** 게임 고르기 화면 주소 */
  get exitUrl() { return '../' + (this.sim ? `?sim=${this.sim}` : ''); }

  /** 인원 선택 (시작 전 화면의 제목 밑에 넣음) */
  playersPicker(el, { label = '최대 참여 인원' } = {}) {
    const max = this.sim ? Math.min(MAX_PLAYERS, this.sim) : MAX_PLAYERS;
    el.classList.add('shell-players');
    el.innerHTML = `<label>${esc(label)}</label><div class="opts">`
      + Array.from({ length: max }, (_, i) => `<button class="hand-target" data-v="${i + 1}">${i + 1}명</button>`).join('') + '</div>';
    el.addEventListener('click', e => {
      const b = e.target.closest('button[data-v]');
      if (!b) return;
      settings.players = +b.dataset.v;
      saveSettings(); this.render(); this.onChange?.();
    });
    this.pickers.push(el);
    this.render();
  }

  render() {
    const sk = this.foot.querySelector('[data-act=skeleton]');
    sk.classList.toggle('on', settings.skeleton);
    sk.querySelector('small').textContent = settings.skeleton ? '보이기' : '숨기기';
    for (const el of this.pickers)
      for (const b of el.querySelectorAll('button[data-v]')) b.classList.toggle('on', +b.dataset.v === settings.players);
  }
}

function esc(s) { return String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]); }
