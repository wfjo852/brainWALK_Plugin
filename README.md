# Femto Bolt 웹 스켈레톤

Femto Bolt 앞에서 움직이면 웹 브라우저의 3D 스켈레톤이 똑같이 움직입니다.

```
[Femto Bolt] ─USB─ [C# 에이전트: Body Tracking]  ── ws://localhost:5000/ws ──▶  [웹 브라우저: three.js 3D]
```

## ⚡ 빠른 시작

1. Femto Bolt를 USB 3 포트에 연결
2. **`FemtoSetup.exe`** 더블클릭 → 관리자 권한 허용 → Enter
   (필요한 프로그램 설치, Orbbec Wrapper 다운로드, 메타데이터 등록, 빌드, 바탕화면 바로가기까지 자동)
3. 바탕화면의 **Femto 스켈레톤** 실행

> Windows가 "PC 보호" 창을 띄우면 **추가 정보 → 실행**을 누르세요 (서명되지 않은 exe라서 뜨는 경고입니다).
> 이미 설치된 항목은 건너뛰므로 여러 번 실행해도 안전합니다. 다운로드가 끊겨도 다시 실행하면 이어받습니다.
> 옵션: `FemtoSetup.exe --skip-bt` (Body Tracking SDK 1.6GB 설치 생략), `--force-orbbec` (Wrapper 다시 받기),
> `--orbbec-v1` (펌웨어가 1.1.2 미만일 때 구형 펌웨어용 Wrapper 1.x 설치)
> 폴더 경로는 영문/숫자만 사용하세요 (한글 경로에서는 Body Tracking이 실패합니다).
> 설치 프로그램 소스는 `tools/setup-src/` 에 있습니다 (Go).

아래 1~2장은 수동으로 설치할 때의 설명입니다.

---

- **agent/** : C# (.NET 8) 에이전트. 카메라 → 관절 32개 추출 → WebSocket으로 30fps 전송, 웹 페이지도 같이 서빙
- **agent/wwwroot/** : 웹 화면 (three.js 포함, 인터넷 없이 동작)
- **orbbec/** : Orbbec K4A Wrapper DLL을 넣는 곳
- **demo.bat** : 카메라 없이 가짜 스켈레톤으로 웹 테스트
- **run.bat** : Femto Bolt로 실제 실행

---

## 1. 먼저 데모로 확인 (카메라 없이, 5분)

1. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 설치 (이미 있으면 생략. `dotnet --list-sdks` 로 확인)
2. `demo.bat` 더블클릭
3. 브라우저가 자동으로 `http://localhost:5000` 을 열고, 손 흔드는 스켈레톤이 보이면 성공

> 처음 실행 시 NuGet 패키지(Body Tracking 런타임 포함, 수백 MB)를 받느라 몇 분 걸립니다.

## 2. Femto Bolt 연결 준비 (최초 1회)

| # | 할 일 | 비고 |
|---|---|---|
| 1 | **Azure Kinect Body Tracking SDK 1.1.2** 설치 (`Azure Kinect Body Tracking SDK 1.1.2.msi`) | Orbbec 공식 문서가 지정한 버전 |
| 2 | **Orbbec SDK K4A Wrapper** Windows 릴리스 다운로드 → `bin` 폴더 안 파일을 전부 이 프로젝트의 `orbbec/` 폴더에 복사 | https://github.com/orbbec/OrbbecSDK-K4A-Wrapper/releases |
| 3 | **메타데이터 등록 스크립트** 실행: 관리자 PowerShell에서 `obsensor_metadata_win10.ps1` 실행 (Wrapper/Orbbec SDK 패키지의 scripts 폴더) | 타임스탬프 등 프레임 메타데이터에 필요. 카메라를 꽂은 상태에서 실행 |
| 4 | Femto Bolt를 **USB 3.x** 포트에 연결, 전원 확인 | |
| 5 | (권장) Orbbec Viewer 또는 Wrapper의 `k4aviewer.exe` 로 뎁스 영상이 나오는지 확인 | 문제를 카메라/코드로 분리하기 좋음 |

### 실행

`run.bat` 더블클릭 → 콘솔에 `[running] 트래킹 중` 이 뜨고 **게임 고르기 화면이 전체 화면으로** 열립니다 (Edge/Chrome 앱 창, 소리 자동 허용).
전체 화면 해제 F11, 닫기 Alt+F4.
처음엔 Body Tracking 모델 로딩에 수십 초 걸릴 수 있습니다.

## 3. 실행 옵션

```
run.bat --port 5050       포트 변경
run.bat --mode cpu        GPU가 없을 때 (느림, 수 fps)
run.bat --no-browser      브라우저 자동 실행 안 함
run.bat --viewer          게임 대신 스켈레톤 뷰어로 시작
run.bat --window          전체 화면 대신 창으로
```

기본 처리 모드는 `gpu` 이며, Windows에서는 SDK가 DirectML을 자동 선택합니다(대부분의 DirectX 12 GPU에서 동작, CUDA 설치 불필요).

## 4. 웹 화면

- **거울 모드**: 켜면 거울처럼 보입니다(내가 오른손 들면 화면 오른쪽 손이 올라감)
- **카메라가 사람 따라가기**: 시점이 사람을 따라 이동
- **떨림 보정**: 정지 시 떨림을 줄이는 강도 (0 = 끔)
- 마우스 드래그로 회전, 휠로 확대/축소, 우클릭 드래그로 이동
- 회색 관절 = 신뢰도 낮음(가려졌거나 추정된 관절)

## 4-000. 게임 (🎮)

- 프로그램 시작 화면 = 게임 고르기 (`http://localhost:5000/games/`). 뷰어에서는 **🎮 게임 하러 가기**
- 어르신용: 마우스 없이 **손을 가슴 높이로 들어 버튼 위에 1.5초** 올려 두면 선택, 큰 글씨·느린 속도·넉넉한 판정
- 게임마다 `agent/wwwroot/games/<게임>/` 폴더. 구조와 새 게임 추가 방법은 `agent/wwwroot/games/README.md`
- 첫 게임 **청기백기**(팔·다리 또는 팔만, 1~4인, 점수·등수, 쉬는 시간): 규칙은 `agent/wwwroot/games/flag/README.md`
- 두 번째 게임 **사진 깨기**(사진에 던지는 시늉 → 사진이 조금씩 깨짐): `agent/wwwroot/games/smash/README.md`
- 카메라 없이 시험: 게임 목록의 "가상 1~4명" 링크 (`?sim=N`)

## 4-00. 분할 보기 (기본 화면): 왼쪽 아바타 + 오른쪽 내 모습

- **왼쪽:** 가벼운 3D 마네킹이 내 동작을 그대로 따라 합니다 (팔·다리·손·발·머리 포함). **뒤에서 보는 시점**이라 내가 오른손을 들면 아바타도 화면 오른쪽 손을 듭니다.
  - 외부 모델 파일 없이 관절 위치로 만드는 마네킹이라 가볍고, 어깨 너비로 체형 크기를 맞춥니다 (아이·어른).
  - 사람마다 등록된 색이 살짝 섞이고 머리 위에 이름표가 붙습니다. 여러 명이면 카메라가 모두 들어오도록 거리를 조절합니다.
- **오른쪽:** 배경을 지운 내 모습 + 스켈레톤 (아래 4-0).
- 보기 패널에서 `분할 보기` / `내 모습` / `3D` 를 바꿀 수 있고, `숨기기`로 설정 패널을 접을 수 있습니다 (오른쪽 위 `설정 열기`로 다시 펼침).

## 4-0. 내 모습 + 스켈레톤 

- 컬러 카메라 영상에서 **배경을 지우고 사람만** 보여주며, 그 위에 스켈레톤과 이름표를 겹쳐 그립니다.
- 사람 영역은 Body Tracking이 만드는 body index map(깊이 기준)을 컬러 카메라 좌표로 옮겨서 구합니다.
- 오른쪽 **보기**에서 `내 모습 + 스켈레톤` / `3D` 전환, `배경 지우기`, `스켈레톤 겹쳐 보기`를 켜고 끌 수 있습니다.
- **경계 다듬기** 슬라이더: 깊이 기준 마스크의 계단·떨림·작은 구멍을 매끄럽게 정리합니다. 높일수록 경계가 부드럽고 안정적이지만, 빠르게 움직일 때 테두리가 살짝 늦게 따라옵니다 (0 = 다듬지 않음).
- 카메라는 컬러 720p(MJPG) + 깊이를 함께 켭니다. **USB 3 연결이 필수**입니다. 컬러를 켤 수 없으면 자동으로 스켈레톤만 보냅니다.
- 3D 보기로 바꾸면 영상 전송을 멈춰 부하를 줄입니다.
- 한계: 깊이 센서 기준이라 머리카락 끝·손가락처럼 얇은 부분이나 경계가 약간 거칠 수 있고, 사람 바로 뒤의 물건이 함께 남을 수 있습니다.

## 4-2. 기울기·바닥 보정, 떨림 보정

- **기울기·바닥 보정** (기본 켬): 카메라 내장 기울기 센서로 중력 방향을 재서, 카메라를 위아래·옆으로 기울여 달아도 아바타가 똑바로 서고 바닥에 붙습니다. 상태 패널에 현재 기울기가 표시됩니다.
- **떨림 보정** 슬라이더 (기본 0.6): 가만히 있을 때의 관절 떨림을 줄입니다. 빠른 동작은 거의 지연 없이 따라갑니다. 0이면 끔, 높일수록 정지 시 더 안정적입니다.

## 4-1. 여러 사람 구분 (이름 등록)

- 처음 보는 사람은 잠깐(약 0.7초) 체형(뼈 길이)을 재서 **"사람 1", "사람 2"…로 자동 등록**됩니다. 다리가 가려져도 보이는 부위(어깨·팔·몸통)로 잽니다.
- 화면 밖으로 나갔다 돌아오면 Body Tracking의 번호는 바뀌지만, 체형을 비교해서 **같은 사람으로 다시 알아봅니다.**
- 오른쪽 **사람** 목록에서 이름을 눌러 바꾸고 Enter. 머리 위 이름표에 바로 반영됩니다.
- 잘못 알아봤으면 그 사람 아래 **"다른 사람이에요…"** 에서 맞는 사람을 고르거나 **새 사람으로 등록**하세요.
  고를수록 그 사람의 체형 정보가 보정됩니다. × 로 목록에서 지울 수 있습니다.
- 방금 나간 사람은 체형을 충분히 못 재도 같은 사람으로 이어 붙입니다. 판단 근거는 `%LocalAppData%\FemtoSkeleton\reid.log`에 남습니다.
- **자동 등록 정리** 버튼: 화면에 없는 "사람 N"을 한 번에 지웁니다 (직접 이름 붙인 사람은 유지).
- 목록은 `%LocalAppData%\FemtoSkeleton\people.json` 에 저장되어 프로그램을 다시 켜도 유지됩니다 (데모는 `people-demo.json`).
- 한계: 체형이 아주 비슷한 사람(키·팔다리 길이 차이 수% 이내)끼리는 헷갈릴 수 있습니다. 얼굴이나 옷으로 구분하지는 않습니다.

## 5. 데이터 형식 (웹에서 직접 쓰고 싶을 때)

WebSocket `ws://localhost:5000/ws` 로 JSON 텍스트 메시지가 옵니다.

```jsonc
// 상태
{"type":"status","state":"running","source":"camera","message":"트래킹 중 ..."}   // state: starting | running | error

// 프레임 (30fps)
{"type":"frame","t":123456,"bodies":[
  {"id":1,"pid":"p1a2b3c4","j":[x,y,z,conf, x,y,z,conf, ... ]}   // pid: 등록된 사람 (확인 중이면 null)
]}

// 사람 목록 (바뀔 때마다)
{"type":"people","people":[{"id":"p1a2b3c4","name":"철수","color":0,"auto":false,"track":1,"lastSeen":"..."}]}
// track: 지금 화면에 있으면 body id, 없으면 null

// 영상 (WebSocket 바이너리 메시지) — 형식은 agent/VideoPacket.cs 참고
//   헤더 18바이트 + JPEG(실제 카메라) 또는 BMP(데모) + zlib 압축 사람 마스크
// 프레임의 각 body에는 컬러 영상 위 관절 좌표 "p2":[u,v, ...] (32×2) 가 추가됩니다

// 브라우저 → 에이전트 명령
{"cmd":"video","on":false}                    // 이 브라우저는 영상 받지 않음 (3D 보기)
{"cmd":"rename","pid":"p1a2b3c4","name":"영희"}
{"cmd":"assign","track":3,"pid":"p1a2b3c4"}   // body 3은 이 사람
{"cmd":"new","track":3}                       // body 3을 새 사람으로
{"cmd":"delete","pid":"p1a2b3c4"}
```

- 좌표: **mm, 깊이 카메라 기준** (x 오른쪽, y 아래, z 카메라 앞쪽)
- conf: 0 없음, 1 낮음, 2 중간, 3 높음
- 관절 순서: Pelvis, SpineNavel, SpineChest, Neck, ClavicleLeft, ShoulderLeft, ElbowLeft, WristLeft, HandLeft, HandTipLeft, ThumbLeft, ClavicleRight, ShoulderRight, ElbowRight, WristRight, HandRight, HandTipRight, ThumbRight, HipLeft, KneeLeft, AnkleLeft, FootLeft, HipRight, KneeRight, AnkleRight, FootRight, Head, Nose, EyeLeft, EarLeft, EyeRight, EarRight

다른 웹 앱에서 붙고 싶으면 `index.html?ws=ws://localhost:5000/ws` 처럼 주소를 넘기거나 위 형식을 그대로 받으면 됩니다.

## 6. 문제 해결

| 증상 | 확인할 것 |
|---|---|
| 웹에 "에이전트에 연결할 수 없음" | 콘솔 창(run.bat)이 떠 있는지 |
| "Femto Bolt를 찾을 수 없습니다" | USB 3 연결, 다른 프로그램(Orbbec Viewer 등)이 카메라를 잡고 있지 않은지 |
| "DLL을 찾지 못했습니다" | `orbbec/` 폴더에 Wrapper 파일을 넣었는지, 넣은 뒤 다시 빌드했는지 (`agent/bin` 폴더 삭제 후 재실행) |
| 카메라는 열리는데 Tracker 생성에서 오류 | Body Tracking SDK 1.1.2 설치, GPU 드라이버 업데이트, 안 되면 `--mode cpu` 로 시험 |
| 프레임이 끊기거나 타임스탬프 오류 | 메타데이터 등록 스크립트 실행, 관리자 권한으로 run.bat 실행 시험 |
| 사람이 안 잡힘 | 카메라에서 1~3m, 전신이 화면에 들어오게. 너무 가까우면 안 잡힘 |
