# brainWALK C#

Python 버전의 핵심 기능을 Windows Forms와 .NET 9로 구현한 Windows 앱입니다. LiDAR G4의 시리얼 스캔, 터치 좌표 계산, 마우스 클릭, ANT+ 장비 검색 및 BPM 수신, UDP 전송, JSON 설정 저장을 지원합니다.

## 빌드 준비

다음 항목이 필요합니다.

- Windows
- .NET 9 SDK
- `CSharp/ant-libs/ANT_Libraries.zip`

## BAT 파일로 빌드

탐색기에서 `CSharp/build.bat`을 더블클릭하거나 터미널에서 실행하세요.

```bat
cd CSharp
build.bat
```

BAT 파일은 다음 작업을 자동으로 수행합니다.

1. `icon/setting_icon.png`를 Windows 실행 파일 아이콘으로 변환
2. `ANT_Libraries.zip`에서 PC용 ANT DLL 추출
3. .NET 9 x86 단일 실행 파일 게시

빌드 결과:

```text
CSharp\bin\Release\net9.0-windows\win-x86\publish-pc\brainWALK-setting.exe
```

게시 폴더의 `brainWALK-setting.exe`, `RUN.bat`, ANT DLL, `lidar_config.json`을 함께 사용해야 합니다.

## PowerShell로 빌드

BAT 파일 대신 저장소 루트에서 직접 실행할 수도 있습니다.

```powershell
powershell -ExecutionPolicy Bypass -File CSharp/build.ps1
```

코드 컴파일만 확인하려면 다음 명령을 사용합니다.

```powershell
dotnet build CSharp/BrainWalk.csproj -c Release
```

첫 실행 후 설정을 저장하면 게시 폴더의 `lidar_config.json`이 수정됩니다. Python 버전의 설정 파일을 덮어쓰지 않습니다.

## 실행

설정 화면을 열려면:

```powershell
.\brainWALK-setting.exe
```

저장된 설정으로 LiDAR 터치와 ANT를 바로 시작하려면:

```powershell
.\brainWALK-setting.exe -console
```

빌드 결과 폴더의 `RUN.bat`을 더블클릭해도 같은 콘솔 모드로 실행됩니다.

콘솔 모드는 `Ctrl+C`로 종료합니다.

## 프리뷰 설정

설정 화면의 `표시 방식`에서 다음 모드를 선택할 수 있습니다. 선택값은 `lidar_config.json`에 저장됩니다.

- `터치 위치만 보기`: 터치 영역과 터치·드래그 위치를 표시하고 LiDAR 원시 포인트는 그리지 않습니다.
- `전체 프리뷰`: LiDAR 포인트, 기준 영역, 각도와 터치 위치를 모두 표시합니다.
- `프리뷰 없음`: 스캔 화면 갱신을 중단하여 렌더링 부하를 줄입니다.

`-console`과 `RUN.bat` 실행에서는 설정값과 관계없이 프리뷰 창을 만들지 않습니다.

## 장치

- LiDAR: Python 설정과 같은 COM 포트 및 baud rate를 선택하세요.
- ANT+: 이 버전은 PC SDK의 `ANT_NET.dll` (`ANT_Managed_Library` API)과 `ANT_WrappedLib.dll`을 사용합니다. 제공된 PC SDK 바이너리는 x86입니다. 이 빌드는 x64 Windows에서 x86 호환 모드로 실행됩니다. Windows on ARM에서도 x86 앱 에뮬레이션으로 실행할 수 있지만, ANT USB 드라이버는 ARM64 호환 버전이 필요합니다. `ARM/`과 `x64/`의 `ANT_NET_UWP.dll`은 UWP 전용이므로 이 WinForms 앱에 사용하지 않습니다.
- 심박수는 Python 버전과 같이 `127.0.0.1:5005`에 `{"bpm":숫자}` 형식으로 전송합니다.

GUI와 통신 코드는 C#으로 구현했습니다. 실제 장치 연결과 신호 수신은 해당 장비를 연결한 Windows PC에서 확인해야 합니다.
