# 게임 만들기 룰북

새 게임을 만들 때 지키는 규칙입니다. 사람이 만들든 AI(Claude)에게 맡기든 똑같이 적용합니다.

---

## 1. 시작 전에 항상 물어볼 것

게임 만들기를 시작하기 전에 **반드시 물어보고, 답을 받은 뒤에** 만듭니다. 짐작해서 정하지 않습니다.

| # | 질문 | 고를 수 있는 것 |
|---|---|---|
| 1 | **캐릭터 타입은 무엇으로 할까요?** | `거울뷰` / `그림자뷰` (아래 2장) |

> 질문이 늘어나면 이 표에 한 줄씩 추가합니다.

---

## 2. 캐릭터 타입

카메라 앞 사람을 화면에 어떻게 보여 줄지 정한 프리셋입니다.
`games/_shared/video-layer.js`의 `CHARACTER_TYPES`에 미리 만들어 두었으므로, 게임은 이름만 고르면 됩니다.

### 2-1. 거울뷰 (`'mirror'`)
- 카메라 **영상이 그대로** 보입니다.
- 사람의 **테두리에 글로우만** 넣습니다.
- 들어온 사람마다 **테두리 색이 다릅니다**.
  - 플레이어(`players.js`)는 플레이어 색(P1 빨강, P2 파랑, P3 초록, P4 노랑)입니다.
  - 그 밖의 사람은 지금 쓰지 않는 색을 받습니다.

### 2-2. 그림자뷰 (`'shadow'`)
- 카메라 **영상을 하나도 쓰지 않습니다**. 사람 모양(마스크)만 씁니다.
- 사람 테두리에 **옅은 글로우 20%**를 넣습니다. 사람을 구분할 정도로만 넣습니다. 색은 거울뷰처럼 사람마다 다릅니다.
- 사람 모양에 **깃털(feather)을 많이** 주고 **60% 검정**으로 칠해서 **그림자처럼** 보이게 합니다.
- **배경은 게임에서 불러와 붙입니다** (`background` 옵션, 아래 4장 참고).

### 2-3. 스켈레톤 (캐릭터 타입과 별개)
- 뼈대 선은 캐릭터 타입과 **상관없이**, 게임에서 원할 때 **언제든** 붙일 수 있습니다.
- 모두에게 붙이려면 `draw({ skeleton: true })`, 한 사람에게만 붙이려면 `stage.skeleton(body, color)`를 씁니다.

### 수치 조정
진하기와 번짐 정도는 `CHARACTER_TYPES`에서 고칩니다. 바꾸면 모든 게임에 같이 적용됩니다.

| 프리셋 | 값 | 뜻 |
|---|---|---|
| mirror | `glow: 1` | 테두리 글로우 진하기 |
| shadow | `glow: 0.2` | 옅은 글로우 20% |
| shadow | `shadow: 0.6` | 그림자 60% 검정 |
| shadow | `feather: 18` | 테두리 번짐 (마스크 640px 기준) |

---

## 3. 폴더와 파일

```
games/
├─ index.html          게임 고르기 화면 → 새 게임을 GAMES 목록에 한 줄 추가
├─ _shared/            공용 (모든 게임이 같이 씀) — 게임 하나만을 위한 코드는 넣지 않음
└─ <게임이름>/          게임마다 폴더 하나
    ├─ index.html       화면
    ├─ game.js          진행·판정·점수
    ├─ README.md        규칙·화면·판정 기준 설명
    └─ server/          (서버 기능이 꼭 필요할 때만) C# 게임 모듈
        ├─ *.cs          IGameModule 구현 → 빌드 때 자동 포함, 에이전트 시작 때 자동 연결
        └─ *.props       그 게임만 쓰는 NuGet 패키지
```

- **에이전트(`agent/`)에는 게임 전용 코드를 넣지 않습니다.**
  - 게임 서버 기능은 `games/<게임>/server/`에 둡니다. API 주소는 `/api/<게임>/...`입니다.
  - 휴대폰 같은 다른 기기에 열어 줄 주소는 모듈의 `RemoteAccess`에 적습니다.
  - 예시는 `games/smash/server/`(사진 깨기)를 보세요.
- 웹 파일만 고치면 다시 빌드할 필요가 없습니다. 에이전트가 브라우저를 자동으로 새로고침합니다.

---

## 4. 코드 사용법

```js
import { FemtoClient, J } from '../_shared/femto-client.js';
import { Stage } from '../_shared/video-layer.js';

const client = new FemtoClient({ sim: SIM });   // 그림자뷰도 마스크가 필요하므로 video 는 기본값(true)
const stage = new Stage(document.getElementById('stage'), client, { character: 'shadow' });   // 또는 'mirror'

const bg = new Image(); bg.src = 'bg.jpg';       // 그림자뷰: 게임이 붙이는 배경

function frame() {
  stage.draw({
    players,            // players.js 플레이어 목록 (사람별 색)
    mirror: true,       // 거울처럼 좌우 반전
    skeleton: false,    // 뼈대 선 (언제든 켤 수 있음)
    background: bg,     // null(투명, 캔버스 뒤 HTML 배경) / 이미지 / '#색' / (ctx, W, H) => {}
  });
  requestAnimationFrame(frame);
}
```

- 사람 영상이 아예 필요 없는 게임(예: 사진 깨기)은 `new FemtoClient({ video: false })`로 만들고 `Stage`를 쓰지 않습니다.
- 카메라 없이 시험할 때는 주소 뒤에 `?sim=인원`을 붙입니다. 이때는 영상이 없습니다.
  - 거울뷰는 뼈대로 보여 줍니다.
  - 그림자뷰는 뼈대로 만든 그림자로 대신 보여 줍니다.

---

## 5. 공통 원칙 (어르신용)

- 큰 글씨, 큰 버튼, 넉넉한 판정과 시간을 둡니다. 틀려도 말투는 부드럽게 합니다.
- 손으로 누를 버튼에는 `class="hand-target"`을 붙이고 `HandPointer`를 씁니다. 마우스가 없어도 되게 합니다.
- 음성 안내는 `_shared/sound.js`를 씁니다.
- 인원은 `_shared/players.js` 기준으로 **1~4인**입니다.
- 카메라 없이 `?sim=` 으로 시험할 수 있게 만듭니다.
