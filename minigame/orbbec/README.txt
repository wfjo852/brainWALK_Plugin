이 폴더에 Orbbec SDK K4A Wrapper (Windows 릴리스) 의 bin 폴더 안 파일을 전부 복사하세요.

  다운로드: https://github.com/orbbec/OrbbecSDK-K4A-Wrapper/releases
  (최신 Release의 Windows x64 패키지를 받아 압축을 풀고, bin 폴더의 파일을 여기에 넣습니다)

  꼭 있어야 하는 파일 예시:
    k4a.dll
    k4arecord.dll
    OrbbecSDK.dll
    OrbbecSDKConfig_v1.0.xml (또는 패키지에 들어 있는 OrbbecSDKConfig*.xml)
    depthengine_2_0.dll (패키지에 있으면)

빌드할 때 이 파일들이 실행 폴더로 자동 복사되어, Microsoft의 k4a.dll 대신 Orbbec 것이 쓰입니다.
(이 README.txt 는 복사되지 않습니다)
