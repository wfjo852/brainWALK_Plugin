// ─────────────────────────────────────────────────────────────
//  게임 공통: 소리 (음성 안내 + 효과음)
//
//  음성(TTS)은 쓸 수 있는 것 중 가장 좋은 것을 자동으로 고릅니다.
//   1. 웹 음성 — Edge의 자연스러운 온라인 한국어 음성 (Microsoft SunHi/InJoon Online, 인터넷 필요)
//   2. 에이전트 음성 — 에이전트가 Windows 한국어 음성(Heami)으로 만든 소리 (/tts, 인터넷 불필요)
//   3. 브라우저 기본 한국어 음성
//  ★ 한 번 정한 목소리는 중간에 바뀌지 않습니다 (자연 음성과 기계음이 섞이면 듣기 불편).
//   - 화면을 열면 1번을 소리 없이 한 번 미리 불러 봄 (연결 준비 + 되는지 확인)
//   - 1번이 한 번이라도 나왔으면: 조금 늦어도 기다림 (다른 음성으로 바꾸지 않음)
//   - 1번이 아예 안 되면(인터넷 없음 등): 처음부터 2번으로 정하고 그 화면에서는 계속 2번
//   - 1번이 나오다가 오류가 3번 연속 나면 그때만 2번으로 바꿈 (그 뒤로는 계속 2번)
//
//  sound.say('오른팔 올려!')                  지금 하던 말을 끊고 바로 말함
//  sound.say('조금 쉽게 해 볼게요', 0.9, { queue: true })   하던 말이 끝난 뒤에 말함
//  sound.preload([...문장])                  에이전트 음성을 미리 만들어 둠 (명령이 늦지 않게)
// ─────────────────────────────────────────────────────────────
export class Sound {
  constructor() {
    this.voiceOn = true;
    this.fxOn = true;
    this.voice = null;          // 브라우저 한국어 음성
    this.ac = null;
    this.server = false;        // 에이전트 음성 사용 가능
    this.serverVoice = null;
    this.webOk = false;         // 웹 음성이 한 번이라도 제대로 나왔는지
    this.webErrors = 0;         // 웹 음성 연속 오류 수
    this.webOff = false;        // 웹 음성을 쓰지 않기로 정함 (이 화면에서는 계속)
    this.lastCancel = 0;
    this.webWaitSince = 0;      // 웹 음성이 아직 한 번도 안 나온 채로 기다리기 시작한 시각
    this.cache = new Map();
    this.queue = [];
    this.token = 0;
    this.src = null;
    this.playing = false;
    const pick = () => {
      const ko = (speechSynthesis?.getVoices?.() ?? []).filter(v => v.lang?.toLowerCase().startsWith('ko'));
      this.voice = ko.find(v => /Online|Natural/i.test(v.name) && /SunHi/i.test(v.name))
        || ko.find(v => /Online|Natural/i.test(v.name))
        || ko.find(v => /Heami|Google/i.test(v.name)) || ko[0] || null;
      this.warm();
    };
    this.pick = pick;
    try { pick(); speechSynthesis.onvoiceschanged = pick; } catch { /* 음성 합성 없음 */ }
    this.ready = fetch('/tts/status').then(r => (r.ok ? r.json() : null))
      .then(j => { this.server = !!j?.available; this.serverVoice = j?.voice ?? null; })
      .catch(() => {});
  }

  get naturalWeb() { return !!this.voice && /Online|Natural/i.test(this.voice.name) && !this.webOff; }

  /** 웹 음성이 한 번도 안 나온 채 6초가 지났으면 → 안 되는 것으로 보고 에이전트 음성으로 정함 */
  async checkWebSilent() {
    await this.ready;
    if (this.webOk || this.webOff || !this.webWaitSince || !this.server) return false;
    if (performance.now() - this.webWaitSince < 6000) return false;
    try { speechSynthesis.cancel(); } catch {}
    this.giveUpWeb('웹 음성이 나오지 않음 (인터넷 연결 확인)');
    if (this.playing && this.current) { const [t, r, tok] = this.current; if (tok === this.token) this.speakServer(t, r, tok); }
    return true;
  }

  /** 웹 음성을 그만 쓰기로 정함 → 이 화면에서는 계속 에이전트 음성 (섞이지 않게) */
  giveUpWeb(why) {
    if (this.webOff || !this.server) return;
    this.webOff = true;
    console.info(`[음성] ${why} → 이 화면에서는 Windows 음성을 사용합니다.`);
    this.onEngineChange?.();
  }

  /** 웹 음성 연결을 미리 준비 (소리 없이 한 번 말해 봄) */
  warm() {
    if (this.warmed || !this.naturalWeb) return;
    this.warmed = true;
    try {
      const u = new SpeechSynthesisUtterance('안녕하세요');
      u.lang = 'ko-KR'; u.voice = this.voice; u.volume = 0;
      let interrupted = false;
      u.onstart = () => { this.webOk = true; this.webErrors = 0; };
      if (this.playing) return;
      this.webWaitSince ||= performance.now();
      speechSynthesis.speak(u);
      setTimeout(() => this.checkWebSilent(), 6100);
    } catch { /* 무시 */ }
  }
  /** 지금 말하는 중이거나 말할 차례가 남아 있는지 (게임이 다음 동작을 기다릴 때) */
  get busy() { return this.voiceOn && (this.playing || this.queue.length > 0); }
  get hasKoreanVoice() { return this.server || !!this.voice; }
  /** 지금 쓰는 음성 이름 (보호자 안내용) */
  get engineLabel() {
    if (this.naturalWeb) return `웹 음성 · ${this.voice.name.replace(/ - .*$/, '')}`;
    if (this.server) return `Windows 음성 · ${this.serverVoice ?? ''}`;
    if (this.voice) return `브라우저 음성 · ${this.voice.name.replace(/ - .*$/, '')}`;
    return '';
  }

  ctx() { this.ac ??= new AudioContext(); return this.ac; }

  /** 브라우저가 소리를 막고 있는지 (사람이 화면을 한 번도 누르지 않았을 때) */
  blocked() {
    try { return this.ctx().state === 'suspended'; } catch { return false; }
  }
  /** 화면을 누른 순간 호출 → 소리 허용 */
  unlock() {
    try { this.ctx().resume(); } catch {}
    try { speechSynthesis.speak(new SpeechSynthesisUtterance('')); } catch {}
  }

  // 어르신이 알아듣기 쉽게 기본 속도를 조금 느리게
  say(text, rate = 0.95, { queue = false } = {}) {
    if (!this.voiceOn || !text) return;
    if (queue && this.playing) { this.queue.push([text, rate]); return; }
    this.stop();
    this.speakNow(text, rate);
  }

  stop() {
    this.token++;
    this.queue = [];
    this.playing = false;
    try { if (speechSynthesis.speaking || speechSynthesis.pending) { speechSynthesis.cancel(); this.lastCancel = performance.now(); } } catch {}
    try { this.src?.stop(); } catch {}
    this.src = null;
  }

  speakNow(text, rate) {
    if (!this.voice) this.pick?.();      // 음성 목록이 늦게 오는 경우
    const token = ++this.token;
    this.playing = true;
    this.current = [text, rate, token];
    if (this.naturalWeb) this.speakWeb(text, rate, token, true);
    else if (this.server) this.speakServer(text, rate, token);
    else this.speakWeb(text, rate, token, false);
  }

  done(token) {
    if (token !== this.token) return;
    this.playing = false;
    const next = this.queue.shift();
    if (next) this.speakNow(...next);
  }

  speakWeb(text, rate, token, isNatural) {
    if (typeof speechSynthesis === 'undefined') { if (this.server) this.speakServer(text, rate, token); else this.done(token); return; }
    // 방금 끊은 직후에 바로 말하면 Edge가 늦거나 빠뜨리는 경우가 있어 아주 잠깐 쉬고 말함
    const gap = 60 - (performance.now() - this.lastCancel);
    if (gap > 0) { setTimeout(() => { if (token === this.token) this.speakWeb(text, rate, token, isNatural); }, gap); return; }
    let started = false, switched = false;
    const toServer = () => {
      if (switched || token !== this.token) return;
      switched = true;
      try { speechSynthesis.cancel(); } catch {}
      this.speakServer(text, rate, token);
    };
    try {
      const u = new SpeechSynthesisUtterance(text);
      u.lang = 'ko-KR';
      if (this.voice) u.voice = this.voice;
      u.rate = rate;
      u.onstart = () => { started = true; if (isNatural) { this.webOk = true; this.webErrors = 0; } };
      u.onend = () => { if (!switched) this.done(token); };
      u.onerror = e => {
        if (e.error === 'interrupted' || e.error === 'canceled') { if (!switched) this.done(token); return; }
        if (!isNatural) { this.done(token); return; }
        // 진짜 오류(네트워크 등): 3번 연속일 때만 에이전트 음성으로 바꿈. 그 전엔 이 문장을 웹 음성으로 한 번 더
        if (++this.webErrors >= 3) { this.giveUpWeb('웹 음성 오류가 계속됨'); toServer(); }
        else if (!switched && token === this.token) { switched = true; setTimeout(() => { if (token === this.token) this.speakWeb(text, rate, token, true); }, 200); }
      };
      speechSynthesis.speak(u);
      // 웹 음성이 아직 한 번도 안 나온 채 6초 넘게 조용하면 → 안 되는 것으로 보고 에이전트 음성 (명령이 바뀌어도 시간은 이어서 셈)
      // (한 번이라도 나왔으면 늦어도 기다림 — 목소리가 섞이지 않게)
      if (isNatural && !this.webOk) {
        this.webWaitSince ||= performance.now();
        setTimeout(() => this.checkWebSilent(), Math.max(200, 6100 - (performance.now() - this.webWaitSince)));
      }
    } catch { if (isNatural && this.server) { this.giveUpWeb('웹 음성 사용 불가'); toServer(); } else this.done(token); }
  }

  async speakServer(text, rate, token) {
    const buf = await this.load(text, rate);
    if (token !== this.token) return;
    if (!buf) {   // 에이전트 음성 실패 → 브라우저 음성으로
      if (this.voice) this.speakWeb(text, rate, token, false); else this.done(token);
      return;
    }
    try {
      const ac = this.ctx();
      if (ac.state === 'suspended') ac.resume();
      const s = ac.createBufferSource();
      s.buffer = buf;
      s.connect(ac.destination);
      s.onended = () => this.done(token);
      s.start();
      this.src = s;
    } catch { this.done(token); }
  }

  /** 에이전트 음성 받아 두기 (같은 문장은 한 번만) */
  load(text, rate) {
    const sapiRate = Math.max(-10, Math.min(10, Math.round((rate - 1) * 20)));   // 0.95 → -1, 0.9 → -2
    const key = sapiRate + '|' + text;
    if (!this.cache.has(key)) {
      this.cache.set(key, fetch(`/tts?rate=${sapiRate}&text=${encodeURIComponent(text)}`)
        .then(r => (r.ok ? r.arrayBuffer() : Promise.reject(new Error(String(r.status)))))
        .then(b => this.ctx().decodeAudioData(b))
        .catch(() => { this.cache.delete(key); return null; }));
    }
    return this.cache.get(key);
  }

  /** 자주 쓰는 문장을 미리 만들어 둠 (에이전트 음성을 쓸 때만, 하나씩 차례로) */
  async preload(texts, rate = 0.95) {
    await this.ready;
    if (!this.server) return;
    for (const t of texts) await this.load(t, rate);
  }

  // 효과음 (Web Audio)
  tone(freqs, dur = 0.12, type = 'sine', gain = 0.18) {
    if (!this.fxOn) return;
    try {
      const ac = this.ctx();
      const t0 = ac.currentTime;
      freqs.forEach((f, i) => {
        const o = ac.createOscillator(), g = ac.createGain();
        o.type = type; o.frequency.value = f;
        const s = t0 + i * dur;
        g.gain.setValueAtTime(0, s);
        g.gain.linearRampToValueAtTime(gain, s + 0.01);
        g.gain.exponentialRampToValueAtTime(0.001, s + dur);
        o.connect(g).connect(ac.destination);
        o.start(s); o.stop(s + dur + 0.02);
      });
    } catch { /* 무시 */ }
  }
  success() { this.tone([880, 1320], 0.09); }
  fail() { this.tone([392, 330], 0.16, 'sine', 0.1); }   // 놀라지 않게 부드러운 소리
  tick() { this.tone([660], 0.06, 'sine', 0.12); }
  go() { this.tone([523, 659, 784], 0.1); }
  fanfare() { this.tone([523, 659, 784, 1047], 0.16); }
  levelUp() { this.tone([659, 784, 988], 0.11, 'triangle', 0.14); }
}
