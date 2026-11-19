# 버전 관리와 되돌리기

이 폴더는 **Git**으로 버전 관리됩니다. Claude가 수정할 때마다
1) 무엇을 바꿨는지 커밋으로 남기고, 2) 버전 태그(`v1.1`, `v1.2` …)를 붙이고, 3) `CHANGELOG.md`에 적습니다.

## 수정이 반영되는 방식

Claude는 이 폴더의 파일을 **직접** 고칩니다 (압축 파일을 따로 보내지 않음).

- **웹 화면** (`agent\wwwroot\...`): 실행 중이면 브라우저가 자동으로 새로고침되어 바로 보입니다.
- **C# 코드** (`agent\*.cs`, `.csproj`): 실행 중인 창을 닫고 `run.bat`(또는 바로가기)을 다시 실행하세요. 자동으로 빌드됩니다.
- **설치 프로그램** (`tools\setup-src`): 설치 과정을 바꿀 때만 `FemtoSetup.exe`를 새로 만들어 폴더에 넣습니다.

## 가장 쉬운 방법: Claude에게 말하기

- "방금 바꾼 거 되돌려줘"
- "v1.0 상태로 돌려줘"
- "버전 목록 보여줘" / "v1.1이랑 v1.2 뭐가 달라?"

되돌릴 때도 기록을 지우지 않고 **"되돌림"을 새 버전으로** 남기므로, 되돌린 걸 다시 되살리는 것도 가능합니다.

## 직접 하고 싶을 때 (Git for Windows 또는 GitHub Desktop 필요)

이 폴더에서 명령 프롬프트를 열고:

```bat
git log --oneline --decorate     :: 버전 목록
git diff v1.0 v1.1 --stat        :: 두 버전 사이 바뀐 파일
git revert --no-edit HEAD        :: 마지막 변경 취소 (새 버전으로 기록)
git checkout v1.0 -- .           :: 파일들을 v1.0 상태로 (그다음 git commit -m "v1.0으로 되돌림")
```

GitHub Desktop을 쓰면 `File → Add local repository`로 이 폴더를 열어 History 탭에서 버전을 보고 되돌릴 수 있습니다.

## 버전 관리에서 빠지는 것

- `agent/bin`, `agent/obj` (빌드 결과 — `run.bat`이 다시 만듦)
- `orbbec/` 안의 DLL (`FemtoSetup.exe`가 다시 받음)
- `setup.log`
- 등록된 사람 목록 `people.json` (이 폴더가 아니라 `%LocalAppData%\FemtoSkeleton`에 있음)

되돌린 뒤에는 `run.bat`(또는 바탕화면 바로가기)으로 실행하면 자동으로 다시 빌드됩니다.
