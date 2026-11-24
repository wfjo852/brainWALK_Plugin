// FemtoSetup.exe — Femto Bolt 웹 스켈레톤 실행에 필요한 것을 한 번에 설치합니다.
//
//  1. Visual C++ 재배포 패키지 (x64)
//  2. .NET 8 SDK
//  3. Azure Kinect Body Tracking SDK 1.1.2
//  4. Orbbec SDK K4A Wrapper (최신 릴리스) → orbbec 폴더
//  5. Orbbec UVC 메타데이터 레지스트리 등록 (obsensor_metadata_win10.ps1 -op install_all)
//  6. 에이전트 빌드 (NuGet 복원)
//  7. 바탕화면 바로가기
//
// 이미 설치된 항목은 건너뜁니다. 여러 번 실행해도 안전합니다.
// 옵션: --skip-bt (Body Tracking SDK 1.6GB 설치 건너뛰기), --force-orbbec (Wrapper 다시 받기), --yes (질문 없이 진행)
package main

import (
	"archive/zip"
	"bufio"
	_ "embed"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strings"
	"time"
)

//go:embed obsensor_metadata_win10.ps1
var metadataScript []byte

const (
	vcRedistURL     = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
	dotnetMetaURL   = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json"
	dotnetMetaURL2  = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/8.0/releases.json"
	dotnetPageURL   = "https://dotnet.microsoft.com/download/dotnet/8.0"
	bodyTrackingURL = "https://download.microsoft.com/download/b/4/6/b469e83e-7884-4bd9-a284-1959cd2c0b76/Azure%20Kinect%20Body%20Tracking%20SDK%201.1.2.msi"
	bodyTrackingPg  = "https://www.microsoft.com/download/details.aspx?id=104221"
	orbbecAPI       = "https://api.github.com/repos/orbbec/OrbbecSDK-K4A-Wrapper/releases?per_page=15"
	orbbecPage      = "https://github.com/orbbec/OrbbecSDK-K4A-Wrapper/releases"
	userAgent       = "FemtoSkeleton-Setup/1.0"
)

var (
	rootDir     string
	cacheDir    string
	logFile     *os.File
	optSkipBT   bool
	optForceOB  bool
	optYes      bool
	optOrbbecV1 bool
	stdin       = bufio.NewReader(os.Stdin)
)

type stepResult struct {
	name   string
	status string // ok | skip | warn | fail
	note   string
}

var results []stepResult

// ───────────────────────── 출력 ─────────────────────────

const (
	cReset = "\x1b[0m"
	cBold  = "\x1b[1m"
	cDim   = "\x1b[90m"
	cGreen = "\x1b[32m"
	cYel   = "\x1b[33m"
	cRed   = "\x1b[31m"
	cCyan  = "\x1b[36m"
)

func logf(format string, a ...any) {
	if logFile != nil {
		fmt.Fprintf(logFile, "%s  %s\n", time.Now().Format("15:04:05"), fmt.Sprintf(format, a...))
	}
}
func say(color, format string, a ...any) {
	msg := fmt.Sprintf(format, a...)
	fmt.Println(color + msg + cReset)
	logf("%s", msg)
}
func info(format string, a ...any) { say("", "  "+format, a...) }
func good(format string, a ...any) { say(cGreen, "  ✔ "+format, a...) }
func warn(format string, a ...any) { say(cYel, "  ! "+format, a...) }
func bad(format string, a ...any)  { say(cRed, "  ✖ "+format, a...) }
func header(n, total int, title string) {
	fmt.Println()
	say(cBold+cCyan, "[%d/%d] %s", n, total, title)
}

func record(name, status, note string) { results = append(results, stepResult{name, status, note}) }

func ask(prompt string, def bool) bool {
	if optYes {
		return def
	}
	hint := "[Y/n]"
	if !def {
		hint = "[y/N]"
	}
	fmt.Printf("  %s %s ", prompt, hint)
	line, _ := stdin.ReadString('\n')
	line = strings.TrimSpace(strings.ToLower(line))
	if line == "" {
		return def
	}
	return line == "y" || line == "yes" || line == "ㅛ"
}

func waitEnter(prompt string) string {
	fmt.Print("  " + prompt)
	line, _ := stdin.ReadString('\n')
	return strings.TrimSpace(strings.ToLower(line))
}

// ───────────────────────── 실행 도우미 ─────────────────────────

// 콘솔에 출력을 그대로 보여주며 실행
func run(dir, name string, args ...string) (int, error) {
	logf("run: %s %s", name, strings.Join(args, " "))
	cmd := exec.Command(name, args...)
	cmd.Dir = dir
	cmd.Stdout, cmd.Stderr, cmd.Stdin = os.Stdout, os.Stderr, os.Stdin
	err := cmd.Run()
	return exitCode(err), err
}

// 출력을 받아서 반환
func output(name string, args ...string) (string, error) {
	cmd := exec.Command(name, args...)
	hideWindow(cmd)
	out, err := cmd.CombinedOutput()
	return string(out), err
}

func exitCode(err error) int {
	if err == nil {
		return 0
	}
	var ee *exec.ExitError
	if errors.As(err, &ee) {
		return ee.ExitCode()
	}
	return -1
}

func powershell(script string) (string, error) {
	return output("powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
		"[Console]::OutputEncoding=[Text.Encoding]::UTF8; "+script)
}

// 설치 프로그램 종료 코드: 0 성공, 1638 이미 최신 버전 설치됨, 3010/1641 재부팅 필요
func installerOK(code int) (bool, bool) {
	switch code {
	case 0, 1638:
		return true, false
	case 3010, 1641:
		return true, true
	}
	return false, false
}

var needReboot bool

// ───────────────────────── 다운로드 ─────────────────────────

func httpClient() *http.Client {
	return &http.Client{Timeout: 0, Transport: &http.Transport{Proxy: http.ProxyFromEnvironment, ResponseHeaderTimeout: 60 * time.Second}}
}

func getJSON(url string, v any) error {
	req, _ := http.NewRequest("GET", url, nil)
	req.Header.Set("User-Agent", userAgent)
	req.Header.Set("Accept", "application/vnd.github+json, application/json")
	c := httpClient()
	c.Timeout = 60 * time.Second
	resp, err := c.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return fmt.Errorf("HTTP %d (%s)", resp.StatusCode, url)
	}
	return json.NewDecoder(resp.Body).Decode(v)
}

// 이어받기 지원 다운로드. 이미 다 받은 파일은 다시 받지 않습니다.
func download(url, dest string) error {
	for attempt := 1; attempt <= 4; attempt++ {
		err := downloadOnce(url, dest)
		if err == nil {
			return nil
		}
		fmt.Println()
		warn("다운로드 오류 (%d/4): %v", attempt, err)
		time.Sleep(time.Duration(attempt*3) * time.Second)
	}
	return fmt.Errorf("다운로드 실패: %s", url)
}

func downloadOnce(url, dest string) error {
	part := dest + ".part"
	if fi, err := os.Stat(dest); err == nil && fi.Size() > 0 {
		info("이미 받은 파일 사용: %s", filepath.Base(dest))
		return nil
	}
	var have int64
	if fi, err := os.Stat(part); err == nil {
		have = fi.Size()
	}

	req, _ := http.NewRequest("GET", url, nil)
	req.Header.Set("User-Agent", userAgent)
	if have > 0 {
		req.Header.Set("Range", fmt.Sprintf("bytes=%d-", have))
	}
	resp, err := httpClient().Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	flags := os.O_CREATE | os.O_WRONLY
	switch {
	case resp.StatusCode == http.StatusPartialContent && have > 0:
		flags |= os.O_APPEND
	case resp.StatusCode == http.StatusOK:
		flags |= os.O_TRUNC
		have = 0
	case resp.StatusCode == http.StatusRequestedRangeNotSatisfiable && have > 0:
		return os.Rename(part, dest) // 이미 끝까지 받음
	default:
		return fmt.Errorf("HTTP %d", resp.StatusCode)
	}

	total := int64(-1)
	if resp.ContentLength > 0 {
		total = have + resp.ContentLength
	}
	f, err := os.OpenFile(part, flags, 0o644)
	if err != nil {
		return err
	}

	buf := make([]byte, 256*1024)
	done := have
	start, last := time.Now(), time.Time{}
	var since int64
	for {
		n, rerr := resp.Body.Read(buf)
		if n > 0 {
			if _, werr := f.Write(buf[:n]); werr != nil {
				f.Close()
				return werr
			}
			done += int64(n)
			since += int64(n)
		}
		if time.Since(last) > 300*time.Millisecond || rerr != nil {
			last = time.Now()
			speed := float64(since) / time.Since(start).Seconds() / 1048576
			if total > 0 {
				fmt.Printf("\r  다운로드 %7.1f / %.1f MB  (%3.0f%%)  %5.1f MB/s   ", mb(done), mb(total), float64(done)*100/float64(total), speed)
			} else {
				fmt.Printf("\r  다운로드 %7.1f MB  %5.1f MB/s   ", mb(done), speed)
			}
		}
		if rerr == io.EOF {
			break
		}
		if rerr != nil {
			f.Close()
			return rerr
		}
	}
	fmt.Println()
	f.Close()
	if total > 0 && done != total {
		return fmt.Errorf("크기 불일치 (%d / %d)", done, total)
	}
	return os.Rename(part, dest)
}

func mb(n int64) float64 { return float64(n) / 1048576 }

func openBrowser(url string) { exec.Command("rundll32", "url.dll,FileProtocolHandler", url).Start() }

// ───────────────────────── 1. VC++ 재배포 ─────────────────────────

func vcInstalled() bool {
	for _, key := range []string{
		`HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64`,
		`HKLM\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64`,
	} {
		out, err := output("reg", "query", key, "/v", "Installed")
		if err == nil && strings.Contains(out, "0x1") {
			return true
		}
	}
	return false
}

func stepVCRedist() {
	name := "Visual C++ 재배포 패키지"
	if vcInstalled() {
		good("이미 설치되어 있습니다.")
		record(name, "skip", "이미 설치됨")
		return
	}
	dest := filepath.Join(cacheDir, "vc_redist.x64.exe")
	if err := download(vcRedistURL, dest); err != nil {
		bad("%v", err)
		record(name, "fail", "다운로드 실패")
		return
	}
	info("설치 중...")
	code, _ := run("", dest, "/install", "/quiet", "/norestart")
	if ok, reboot := installerOK(code); ok {
		needReboot = needReboot || reboot
		good("설치 완료")
		record(name, "ok", "")
	} else {
		bad("설치 실패 (종료 코드 %d)", code)
		record(name, "fail", fmt.Sprintf("종료 코드 %d", code))
	}
}

// ───────────────────────── 2. .NET 8 SDK ─────────────────────────

func dotnetPath() string {
	p := filepath.Join(os.Getenv("ProgramFiles"), "dotnet", "dotnet.exe")
	if _, err := os.Stat(p); err == nil {
		return p
	}
	if p, err := exec.LookPath("dotnet"); err == nil {
		return p
	}
	return ""
}

func dotnet8Version() string {
	d := dotnetPath()
	if d == "" {
		return ""
	}
	out, err := output(d, "--list-sdks")
	if err != nil {
		return ""
	}
	for _, line := range strings.Split(out, "\n") {
		if strings.HasPrefix(strings.TrimSpace(line), "8.") {
			return strings.Fields(line)[0]
		}
	}
	return ""
}

func stepDotnet() {
	name := ".NET 8 SDK"
	if v := dotnet8Version(); v != "" {
		good("이미 설치되어 있습니다 (%s)", v)
		record(name, "skip", v)
		return
	}

	// 1순위: winget
	if _, err := exec.LookPath("winget"); err == nil {
		info("winget으로 설치 중...")
		code, _ := run("", "winget", "install", "--id", "Microsoft.DotNet.SDK.8", "-e", "--silent",
			"--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity")
		if v := dotnet8Version(); v != "" {
			good("설치 완료 (%s)", v)
			record(name, "ok", v)
			return
		}
		warn("winget 설치가 끝나지 않았습니다 (코드 %d). 직접 내려받아 설치합니다.", code)
	}

	// 2순위: Microsoft 공식 릴리스 정보에서 설치 파일 주소 찾기
	type file struct{ Name, Rid, URL string }
	var meta struct {
		Releases []struct {
			Sdk struct {
				Version string
				Files   []file
			}
		}
	}
	err := getJSON(dotnetMetaURL, &meta)
	if err != nil {
		err = getJSON(dotnetMetaURL2, &meta)
	}
	url, ver := "", ""
	if err == nil && len(meta.Releases) > 0 {
		ver = meta.Releases[0].Sdk.Version
		for _, f := range meta.Releases[0].Sdk.Files {
			if f.Name == "dotnet-sdk-win-x64.exe" {
				url = f.URL
			}
		}
	}
	if url == "" {
		bad("설치 파일 주소를 찾지 못했습니다. 브라우저에서 직접 설치해 주세요: %s", dotnetPageURL)
		openBrowser(dotnetPageURL)
		record(name, "fail", "수동 설치 필요")
		return
	}
	dest := filepath.Join(cacheDir, "dotnet-sdk-"+ver+"-win-x64.exe")
	if err := download(url, dest); err != nil {
		bad("%v", err)
		record(name, "fail", "다운로드 실패")
		return
	}
	info(".NET SDK %s 설치 중... (1~3분)", ver)
	code, _ := run("", dest, "/install", "/quiet", "/norestart")
	ok, reboot := installerOK(code)
	needReboot = needReboot || reboot
	if v := dotnet8Version(); ok && v != "" {
		good("설치 완료 (%s)", v)
		record(name, "ok", v)
	} else {
		bad("설치 실패 (종료 코드 %d)", code)
		record(name, "fail", fmt.Sprintf("종료 코드 %d", code))
	}
}

// ───────────────────────── 3. Body Tracking SDK ─────────────────────────

func installedProgram(pattern string) string {
	out, _ := powershell(`$p = Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '` + pattern + `' } | Select-Object -First 1; if ($p) { "$($p.DisplayName) $($p.DisplayVersion)" }`)
	return strings.TrimSpace(out)
}

func stepBodyTracking() {
	name := "Body Tracking SDK 1.1.2"
	if found := installedProgram("*Body Tracking SDK*"); found != "" {
		good("이미 설치되어 있습니다 (%s)", found)
		record(name, "skip", found)
		return
	}
	if optSkipBT {
		warn("--skip-bt 옵션으로 건너뜁니다.")
		record(name, "skip", "건너뜀(옵션)")
		return
	}
	info("약 1.6GB입니다. 인터넷 속도에 따라 수 분~수십 분 걸립니다. (중간에 끊겨도 다시 실행하면 이어받습니다)")
	dest := filepath.Join(cacheDir, "Azure Kinect Body Tracking SDK 1.1.2.msi")
	if err := download(bodyTrackingURL, dest); err != nil {
		bad("%v  → 수동 다운로드: %s", err, bodyTrackingPg)
		record(name, "fail", "다운로드 실패")
		return
	}
	info("설치 중... (수 분 걸릴 수 있습니다)")
	logPath := filepath.Join(cacheDir, "bodytracking_install.log")
	code, _ := run("", "msiexec.exe", "/i", dest, "/qn", "/norestart", "/L*v", logPath)
	if ok, reboot := installerOK(code); ok {
		needReboot = needReboot || reboot
		good("설치 완료")
		record(name, "ok", "")
	} else {
		bad("설치 실패 (종료 코드 %d). 로그: %s", code, logPath)
		record(name, "fail", fmt.Sprintf("종료 코드 %d", code))
	}
}

// ───────────────────────── 4. Orbbec K4A Wrapper ─────────────────────────

func orbbecDir() string { return filepath.Join(rootDir, "orbbec") }

func orbbecReady() (bool, string) {
	for _, f := range []string{"k4a.dll", "OrbbecSDK.dll"} {
		if _, err := os.Stat(filepath.Join(orbbecDir(), f)); err != nil {
			return false, ""
		}
	}
	v, _ := os.ReadFile(filepath.Join(orbbecDir(), "VERSION.txt"))
	ver := strings.TrimSpace(string(v))
	// v2 계열은 extensions 플러그인 폴더가 반드시 있어야 함 (이전 설치 프로그램은 이 폴더를 빠뜨렸음)
	if !strings.HasPrefix(strings.ToLower(ver), "v1.") && !fileExists(filepath.Join(orbbecDir(), "extensions")) {
		return false, ver + " (extensions 폴더 없음)"
	}
	return true, ver
}

type ghAsset struct {
	Name string `json:"name"`
	URL  string `json:"browser_download_url"`
	Size int64  `json:"size"`
}
type ghRelease struct {
	Tag        string    `json:"tag_name"`
	Draft      bool      `json:"draft"`
	Prerelease bool      `json:"prerelease"`
	Assets     []ghAsset `json:"assets"`
}

// Windows x64 zip 자산 고르기 (점수가 높을수록 적합)
func scoreAsset(name string) int {
	n := strings.ToLower(name)
	if !strings.HasSuffix(n, ".zip") || !strings.Contains(n, "win") {
		return -1
	}
	s := 1
	if strings.Contains(n, "x64") || strings.Contains(n, "amd64") || strings.Contains(n, "win64") {
		s += 2
	}
	if strings.Contains(n, "x86") || strings.Contains(n, "win32") || strings.Contains(n, "arm") {
		s -= 3
	}
	if strings.Contains(n, "debug") || strings.Contains(n, "source") || strings.Contains(n, "src") {
		s -= 2
	}
	return s
}

func stepOrbbec() {
	name := "Orbbec K4A Wrapper"
	ok, v := orbbecReady()
	// 지금 들어 있는 버전 계열이 요청과 다르면 교체
	wantV1 := optOrbbecV1
	haveV1 := strings.HasPrefix(strings.ToLower(v), "v1.")
	if ok && !optForceOB && wantV1 == haveV1 {
		good("orbbec 폴더에 이미 있습니다 %s", v)
		record(name, "skip", v)
		return
	}
	if ok {
		info("기존 Wrapper(%s)를 교체합니다.", v)
		clearOrbbecDir()
	}

	var releases []ghRelease
	if err := getJSON(orbbecAPI, &releases); err != nil {
		bad("GitHub 릴리스 정보를 가져오지 못했습니다: %v", err)
		info("브라우저에서 Windows 패키지를 받아 bin 폴더 파일을 %s 에 넣어 주세요.", orbbecDir())
		openBrowser(orbbecPage)
		record(name, "fail", "수동 설치 필요")
		return
	}

	var rel *ghRelease
	var asset *ghAsset
	for i := range releases {
		r := &releases[i]
		if r.Draft || r.Prerelease {
			continue
		}
		// --orbbec-v1: 구형 펌웨어용 1.x 계열만 선택
		if optOrbbecV1 && !strings.HasPrefix(strings.ToLower(r.Tag), "v1.") {
			continue
		}
		best, bestScore := -1, 0
		for j, a := range r.Assets {
			if s := scoreAsset(a.Name); s > bestScore {
				best, bestScore = j, s
			}
		}
		if best >= 0 {
			rel, asset = r, &r.Assets[best]
			break
		}
	}
	if asset == nil {
		bad("Windows용 릴리스 파일을 찾지 못했습니다.")
		openBrowser(orbbecPage)
		record(name, "fail", "수동 설치 필요")
		return
	}

	info("릴리스 %s : %s (%.0f MB)", rel.Tag, asset.Name, mb(asset.Size))
	zipPath := filepath.Join(cacheDir, asset.Name)
	if err := download(asset.URL, zipPath); err != nil {
		bad("%v", err)
		record(name, "fail", "다운로드 실패")
		return
	}

	binDir, files, err := extractWrapperBin(zipPath, filepath.Join(cacheDir, "orbbec_extract"))
	if err != nil {
		bad("압축 해제 실패: %v", err)
		record(name, "fail", err.Error())
		return
	}
	if err := os.MkdirAll(orbbecDir(), 0o755); err != nil {
		bad("%v", err)
		record(name, "fail", err.Error())
		return
	}
	for _, f := range files {
		dst := filepath.Join(orbbecDir(), f)
		os.MkdirAll(filepath.Dir(dst), 0o755)
		if err := copyFile(filepath.Join(binDir, f), dst); err != nil {
			bad("복사 실패 %s: %v", f, err)
			record(name, "fail", "복사 실패")
			return
		}
	}
	os.WriteFile(filepath.Join(orbbecDir(), "VERSION.txt"), []byte(rel.Tag+"  ("+asset.Name+")\r\n"), 0o644)
	good("%d개 파일을 orbbec 폴더에 넣었습니다 (%s)", len(files), rel.Tag)
	record(name, "ok", rel.Tag)
}

// orbbec 폴더의 이전 Wrapper 파일 삭제 (README.txt 는 유지)
func clearOrbbecDir() {
	entries, _ := os.ReadDir(orbbecDir())
	for _, e := range entries {
		if !strings.EqualFold(e.Name(), "README.txt") {
			os.RemoveAll(filepath.Join(orbbecDir(), e.Name()))
		}
	}
	// 빌드 출력에 남은 이전 버전 DLL도 정리 (다음 빌드에서 새로 복사됨)
	os.RemoveAll(filepath.Join(rootDir, "agent", "bin"))
	os.RemoveAll(filepath.Join(rootDir, "agent", "obj"))
}

// zip을 풀고 k4a.dll 이 들어 있는 x64 bin 폴더를 찾습니다.
func extractWrapperBin(zipPath, outDir string) (string, []string, error) {
	os.RemoveAll(outDir)
	zr, err := zip.OpenReader(zipPath)
	if err != nil {
		return "", nil, err
	}
	defer zr.Close()

	var candidates []string
	for _, zf := range zr.File {
		target := filepath.Join(outDir, filepath.FromSlash(zf.Name))
		if !strings.HasPrefix(target, filepath.Clean(outDir)+string(os.PathSeparator)) {
			continue // 경로 조작 방지
		}
		if zf.FileInfo().IsDir() {
			os.MkdirAll(target, 0o755)
			continue
		}
		os.MkdirAll(filepath.Dir(target), 0o755)
		rc, err := zf.Open()
		if err != nil {
			return "", nil, err
		}
		w, err := os.Create(target)
		if err != nil {
			rc.Close()
			return "", nil, err
		}
		_, err = io.Copy(w, rc)
		w.Close()
		rc.Close()
		if err != nil {
			return "", nil, err
		}
		if strings.EqualFold(filepath.Base(target), "k4a.dll") {
			candidates = append(candidates, filepath.Dir(target))
		}
	}
	if len(candidates) == 0 {
		return "", nil, errors.New("압축 파일 안에 k4a.dll 이 없습니다")
	}
	// x64 / bin 경로를 우선
	sort.SliceStable(candidates, func(i, j int) bool { return binScore(candidates[i]) > binScore(candidates[j]) })
	bin := candidates[0]

	// bin 아래 전체 (extensions\depthengine, extensions\frameprocessor 등 하위 폴더 포함)
	files, err := listTree(bin)
	if err != nil {
		return "", nil, err
	}
	return bin, files, nil
}

// dir 아래 모든 파일의 상대 경로
func listTree(dir string) ([]string, error) {
	var files []string
	err := filepath.WalkDir(dir, func(p string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if !d.IsDir() {
			rel, _ := filepath.Rel(dir, p)
			files = append(files, rel)
		}
		return nil
	})
	return files, err
}

func copyTree(src, dst string) (int, error) {
	files, err := listTree(src)
	if err != nil {
		return 0, err
	}
	for _, f := range files {
		d := filepath.Join(dst, f)
		os.MkdirAll(filepath.Dir(d), 0o755)
		if err := copyFile(filepath.Join(src, f), d); err != nil {
			return 0, err
		}
	}
	return len(files), nil
}

func binScore(p string) int {
	l := strings.ToLower(p)
	s := 0
	if strings.Contains(l, "x64") || strings.Contains(l, "amd64") {
		s += 3
	}
	if strings.Contains(l, "x86") || strings.Contains(l, "win32") {
		s -= 5
	}
	if strings.HasSuffix(l, "bin") {
		s++
	}
	if strings.Contains(l, "release") {
		s++
	}
	return s
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

// ───────────────────────── 5. 메타데이터 등록 ─────────────────────────

var reVID = regexp.MustCompile(`(?i)VID_2BC5`)

func femtoConnected() string {
	out, _ := powershell(`Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like '*VID_2BC5*' } | Select-Object -First 1 -ExpandProperty FriendlyName`)
	out = strings.TrimSpace(out)
	if out == "" {
		// FriendlyName이 비어도 장치는 있을 수 있음
		raw, _ := powershell(`Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like '*VID_2BC5*' } | Select-Object -First 1 -ExpandProperty InstanceId`)
		if reVID.MatchString(raw) {
			return "Orbbec 장치"
		}
	}
	return out
}

func stepMetadata() {
	name := "UVC 메타데이터 등록"
	dev := femtoConnected()
	for dev == "" {
		warn("Femto Bolt가 연결되어 있지 않습니다.")
		ans := waitEnter("USB 3 포트에 연결한 뒤 Enter (이 단계 건너뛰기: S + Enter) > ")
		if ans == "s" || ans == "ㄴ" || optYes {
			warn("건너뜁니다. 나중에 카메라를 연결한 상태로 이 설치 프로그램을 다시 실행하세요.")
			record(name, "warn", "카메라 미연결로 건너뜀")
			return
		}
		time.Sleep(2 * time.Second)
		dev = femtoConnected()
	}
	good("카메라 감지: %s", dev)

	script := filepath.Join(cacheDir, "obsensor_metadata_win10.ps1")
	if err := os.WriteFile(script, metadataScript, 0o644); err != nil {
		bad("%v", err)
		record(name, "fail", err.Error())
		return
	}
	code, _ := run("", "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-op", "install_all")
	if code == 0 {
		good("등록 완료 (카메라를 한 번 뽑았다 다시 꽂으면 확실히 적용됩니다)")
		record(name, "ok", "")
	} else {
		bad("스크립트 실패 (종료 코드 %d)", code)
		record(name, "fail", fmt.Sprintf("종료 코드 %d", code))
	}
}

// ───────────────────────── 6. Windows 카메라 권한 ─────────────────────────
// Femto Bolt 의 깊이/컬러는 UVC 카메라로 잡히므로, Windows 개인정보 설정에서
// "데스크톱 앱의 카메라 액세스"가 꺼져 있으면 Orbbec SDK가 센서를 열지 못합니다.

const consentBase = `SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam`

type consentKey struct {
	hive, key, label string
}

var consentKeys = []consentKey{
	{"HKLM", consentBase, "이 장치의 카메라 액세스"},
	{"HKCU", consentBase, "앱의 카메라 액세스"},
	{"HKCU", consentBase + `\NonPackaged`, "데스크톱 앱의 카메라 액세스"},
}

// 값이 없으면 Windows 기본값(허용)
func consentValue(k consentKey) string {
	out, err := output("reg", "query", k.hive+`\`+k.key, "/v", "Value")
	if err != nil {
		return ""
	}
	if strings.Contains(out, "Deny") {
		return "Deny"
	}
	if strings.Contains(out, "Allow") {
		return "Allow"
	}
	return ""
}

func stepCameraPrivacy() {
	name := "Windows 카메라 권한"
	var denied []consentKey
	for _, k := range consentKeys {
		if consentValue(k) == "Deny" {
			denied = append(denied, k)
			warn("꺼져 있음: %s", k.label)
		}
	}
	if len(denied) == 0 {
		good("카메라 액세스가 허용되어 있습니다.")
		record(name, "skip", "허용됨")
		return
	}
	info("이 설정이 꺼져 있으면 Femto Bolt 센서를 열 수 없습니다 (k4aviewer도 동작하지 않음).")
	if !ask("위 항목을 '허용'으로 바꿀까요? (설정 → 개인정보 및 보안 → 카메라 와 같은 설정)", true) {
		warn("변경하지 않았습니다.")
		record(name, "warn", "꺼져 있음")
		return
	}
	for _, k := range denied {
		if _, err := output("reg", "add", k.hive+`\`+k.key, "/v", "Value", "/t", "REG_SZ", "/d", "Allow", "/f"); err != nil {
			bad("변경 실패: %s", k.label)
			record(name, "fail", "변경 실패")
			return
		}
	}
	good("허용으로 변경했습니다. 카메라를 뽑았다 다시 꽂아 주세요.")
	record(name, "ok", "허용으로 변경")
}

// ───────────────────────── 7. 빌드 ─────────────────────────

// Body Tracking 실행에 필요한 런타임 파일 (실행 폴더에 있어야 함)
var btRuntimeFiles = []string{"k4abt.dll", "onnxruntime.dll", "directml.dll", "dnn_model_2_0_op11.onnx"}

func btSDKBin() string {
	return filepath.Join(os.Getenv("ProgramFiles"), "Azure Kinect Body Tracking SDK", "sdk", "windows-desktop", "amd64", "release", "bin")
}

// agent\bin 아래에서 가장 최근에 빌드된 FemtoSkeletonAgent.dll 폴더
func agentOutDir() string {
	best, bestT := "", time.Time{}
	filepath.WalkDir(filepath.Join(rootDir, "agent", "bin"), func(p string, d os.DirEntry, err error) error {
		if err == nil && !d.IsDir() && strings.EqualFold(d.Name(), "FemtoSkeletonAgent.dll") {
			if fi, e := d.Info(); e == nil && fi.ModTime().After(bestT) {
				best, bestT = filepath.Dir(p), fi.ModTime()
			}
		}
		return nil
	})
	return best
}

func sameSize(a, b string) bool {
	fa, e1 := os.Stat(a)
	fb, e2 := os.Stat(b)
	return e1 == nil && e2 == nil && fa.Size() == fb.Size()
}

// 빌드 결과 폴더에 필요한 DLL/모델이 다 있는지 확인하고, 없으면 SDK 설치 폴더에서 복사
func verifyRuntime() bool {
	out := agentOutDir()
	if out == "" {
		bad("빌드 결과 폴더를 찾지 못했습니다.")
		return false
	}
	info("실행 폴더 확인: %s", out)
	allOK := true

	for _, f := range btRuntimeFiles {
		dst := filepath.Join(out, f)
		if _, err := os.Stat(dst); err == nil {
			continue
		}
		src := filepath.Join(btSDKBin(), f)
		if _, err := os.Stat(src); err != nil {
			bad("없음: %s (Body Tracking SDK 설치 폴더에도 없음)", f)
			allOK = false
			continue
		}
		if err := copyFile(src, dst); err != nil {
			bad("복사 실패: %s", f)
			allOK = false
			continue
		}
		warn("없어서 SDK 설치 폴더에서 복사함: %s", f)
	}
	// lite 모델도 있으면 함께
	if lite := filepath.Join(btSDKBin(), "dnn_model_2_0_lite_op11.onnx"); fileExists(lite) && !fileExists(filepath.Join(out, filepath.Base(lite))) {
		copyFile(lite, filepath.Join(out, filepath.Base(lite)))
	}

	// Orbbec SDK v2 플러그인 폴더 (depthengine, frameprocessor 등) — 없으면 Start 단계에서 실패함
	if ext := filepath.Join(orbbecDir(), "extensions"); fileExists(ext) {
		if n, err := copyTree(ext, filepath.Join(out, "extensions")); err != nil {
			bad("extensions 폴더 복사 실패: %v", err)
			allOK = false
		} else {
			info("Orbbec extensions 플러그인 %d개 파일 확인", n)
		}
	}

	// k4a.dll 이 Microsoft 것이 아니라 Orbbec Wrapper 것인지
	for _, f := range []string{"k4a.dll", "OrbbecSDK.dll"} {
		src, dst := filepath.Join(orbbecDir(), f), filepath.Join(out, f)
		if !fileExists(src) {
			bad("orbbec 폴더에 %s 가 없습니다.", f)
			allOK = false
			continue
		}
		if !sameSize(src, dst) {
			if err := copyFile(src, dst); err != nil {
				bad("%s 교체 실패: %v", f, err)
				allOK = false
				continue
			}
			warn("%s 를 Orbbec 것으로 교체했습니다.", f)
		}
	}
	if allOK {
		good("Body Tracking 런타임(k4abt, onnxruntime, directml, 모델)과 Orbbec DLL 확인 완료")
	}
	return allOK
}

func fileExists(p string) bool { _, err := os.Stat(p); return err == nil }

// 경로에 한글 등 ASCII가 아닌 문자가 있으면 Body Tracking이 실패함
// ("String conversion error: Illegal byte sequence encounted in the input.")
func nonASCII(s string) bool {
	for _, r := range s {
		if r > 127 {
			return true
		}
	}
	return false
}

func stepBuild() {
	name := "에이전트 빌드"
	d := dotnetPath()
	if d == "" || dotnet8Version() == "" {
		bad(".NET 8 SDK가 없어 빌드할 수 없습니다.")
		record(name, "fail", ".NET 8 SDK 없음")
		return
	}
	info("NuGet 패키지 복원 + 빌드 중... (처음엔 수백 MB를 받아 몇 분 걸립니다)")
	agent := filepath.Join(rootDir, "agent")
	code, _ := run(agent, d, "build", "-c", "Release", "--nologo", "-v", "minimal")
	if code == 0 {
		good("빌드 성공")
		if verifyRuntime() {
			record(name, "ok", "")
		} else {
			record(name, "fail", "런타임 파일 누락")
		}
	} else {
		bad("빌드 실패 (위 오류 메시지를 확인하세요)")
		record(name, "fail", "빌드 오류")
	}
}

// ───────────────────────── 8. 바로가기 ─────────────────────────

func stepShortcuts() {
	name := "바탕화면 바로가기"
	ps := func(link, target, desc string) string {
		return fmt.Sprintf(`$s=$w.CreateShortcut((Join-Path $d '%s.lnk')); $s.TargetPath='%s'; $s.WorkingDirectory='%s'; $s.Description='%s'; $s.IconLocation='%%SystemRoot%%\System32\shell32.dll,137'; $s.Save();`,
			link, psq(target), psq(rootDir), desc)
	}
	script := `$w=New-Object -ComObject WScript.Shell; $d=[Environment]::GetFolderPath('Desktop'); ` +
		ps("Femto 스켈레톤", filepath.Join(rootDir, "run.bat"), "Femto Bolt 웹 스켈레톤 실행") +
		ps("Femto 스켈레톤 (데모)", filepath.Join(rootDir, "demo.bat"), "카메라 없이 데모 실행")
	if out, err := powershell(script); err != nil {
		warn("바로가기 생성 실패: %s", strings.TrimSpace(out))
		record(name, "warn", "생성 실패")
		return
	}
	good("'Femto 스켈레톤', 'Femto 스켈레톤 (데모)' 바로가기를 만들었습니다.")
	record(name, "ok", "")
}

func psq(s string) string { return strings.ReplaceAll(s, "'", "''") }

// ───────────────────────── 메인 ─────────────────────────

func main() {
	if runtime.GOOS == "windows" {
		setupConsole()
	}
	for _, a := range os.Args[1:] {
		switch strings.ToLower(a) {
		case "--skip-bt":
			optSkipBT = true
		case "--force-orbbec":
			optForceOB = true
		case "--yes", "-y":
			optYes = true
		case "--orbbec-v1":
			optOrbbecV1 = true
		}
	}

	exe, _ := os.Executable()
	rootDir = filepath.Dir(exe)
	fmt.Println()
	say(cBold, "  ┌──────────────────────────────────────────────┐")
	say(cBold, "  │   Femto Bolt 웹 스켈레톤 — 필수 프로그램 설치  │")
	say(cBold, "  └──────────────────────────────────────────────┘")

	if _, err := os.Stat(filepath.Join(rootDir, "agent", "FemtoSkeletonAgent.csproj")); err != nil {
		bad("이 파일은 FemtoSkeleton 폴더 안(run.bat 옆)에 두고 실행해야 합니다.")
		info("현재 위치: %s", rootDir)
		waitEnter("Enter를 누르면 닫힙니다.")
		os.Exit(1)
	}

	// 한글 경로 확인 (Body Tracking SDK가 ASCII가 아닌 경로에서 실패함)
	if nonASCII(rootDir) {
		bad("폴더 경로에 한글 등 영문이 아닌 문자가 있습니다:")
		info("%s", rootDir)
		info("Body Tracking SDK는 이런 경로에서 'String conversion error: Illegal byte sequence' 오류로 실패합니다.")
		info("영문/숫자로만 된 경로로 폴더를 옮긴 뒤 다시 실행하세요. 예) D:\\FemtoSkeleton")
		waitEnter("Enter를 누르면 닫힙니다.")
		os.Exit(1)
	}
	if home, _ := os.UserHomeDir(); nonASCII(home) {
		warn("사용자 폴더 경로에 영문이 아닌 문자가 있습니다 (%s). 문제가 생기면 알려 주세요.", home)
	}

	cacheDir = filepath.Join(os.TempDir(), "FemtoSetup")
	os.MkdirAll(cacheDir, 0o755)
	logFile, _ = os.Create(filepath.Join(rootDir, "setup.log"))
	defer logFile.Close()
	logf("root=%s cache=%s args=%v", rootDir, cacheDir, os.Args[1:])

	// 현재 상태 미리 보여주기
	fmt.Println()
	info("설치 위치 : %s", rootDir)
	info("받은 파일 : %s", cacheDir)
	fmt.Println()
	status := func(ok bool, s string) string {
		if ok {
			return cGreen + "설치됨 " + s + cReset
		}
		return cYel + "설치 필요" + cReset
	}
	dn := dotnet8Version()
	bt := installedProgram("*Body Tracking SDK*")
	obOK, obV := orbbecReady()
	fmt.Printf("    1. Visual C++ 재배포 패키지     %s\n", status(vcInstalled(), ""))
	fmt.Printf("    2. .NET 8 SDK                   %s\n", status(dn != "", dn))
	fmt.Printf("    3. Body Tracking SDK 1.1.2      %s\n", status(bt != "", ""))
	fmt.Printf("    4. Orbbec K4A Wrapper           %s\n", status(obOK, obV))
	fmt.Printf("    5. UVC 메타데이터 등록           카메라 연결 후 등록 (매번 확인)\n")
	fmt.Printf("    6. Windows 카메라 권한           데스크톱 앱 카메라 액세스 확인\n")
	fmt.Printf("    7. 에이전트 빌드                 빌드 + 런타임 파일(DirectML, ONNX 모델, Orbbec DLL) 확인\n")
	fmt.Printf("    8. 바탕화면 바로가기\n")
	if optOrbbecV1 {
		fmt.Printf("\n    %s--orbbec-v1: 구형 펌웨어용 Orbbec Wrapper 1.x 를 설치합니다.%s\n", cYel, cReset)
	}
	fmt.Println()
	if !ask("설치를 시작할까요?", true) {
		return
	}

	const total = 8
	header(1, total, "Visual C++ 재배포 패키지 (x64)")
	stepVCRedist()
	header(2, total, ".NET 8 SDK")
	stepDotnet()
	header(3, total, "Azure Kinect Body Tracking SDK 1.1.2")
	stepBodyTracking()
	header(4, total, "Orbbec SDK K4A Wrapper → orbbec 폴더")
	stepOrbbec()
	header(5, total, "Femto Bolt UVC 메타데이터 등록")
	stepMetadata()
	header(6, total, "Windows 카메라 권한")
	stepCameraPrivacy()
	header(7, total, "에이전트 빌드 + 런타임 파일 확인")
	stepBuild()
	header(8, total, "바탕화면 바로가기")
	stepShortcuts()

	// 요약
	fmt.Println()
	say(cBold, "  ─────────────────── 결과 ───────────────────")
	failed := 0
	for _, r := range results {
		icon, color := "✔", cGreen
		switch r.status {
		case "skip":
			icon, color = "–", cDim
		case "warn":
			icon, color = "!", cYel
		case "fail":
			icon, color = "✖", cRed
			failed++
		}
		note := ""
		if r.note != "" {
			note = "  (" + r.note + ")"
		}
		say(color, "   %s %s%s", icon, r.name, note)
	}
	fmt.Println()
	_, obVer := orbbecReady()
	if strings.HasPrefix(strings.ToLower(obVer), "v2.") {
		info("참고: Orbbec Wrapper %s 는 Femto Bolt 펌웨어 1.1.2 이상이 필요합니다.", strings.Fields(obVer)[0])
		info("      orbbec\\k4aviewer.exe 에서도 카메라가 안 열리면 → Orbbec Viewer로 펌웨어 업데이트,")
		info("      또는 구형 펌웨어용 Wrapper로 교체:  FemtoSetup.exe --orbbec-v1")
	}
	info("로그: %s", filepath.Join(rootDir, "setup.log"))

	if needReboot {
		warn("일부 구성 요소 적용을 위해 재부팅이 필요합니다. 재부팅 후 바로가기로 실행하세요.")
	} else if failed == 0 {
		fmt.Println()
		if viewer := filepath.Join(orbbecDir(), "k4aviewer.exe"); fileExists(viewer) && ask("먼저 k4aviewer로 카메라를 테스트할까요? (Open Device → Start, 깊이 영상이 보이면 정상)", false) {
			exec.Command(viewer).Start()
			waitEnter("뷰어를 닫은 뒤 Enter > ")
		}
		if ask("지금 실행할까요?", true) {
			exec.Command("cmd", "/c", "start", "", filepath.Join(rootDir, "run.bat")).Start()
		}
	} else {
		warn("실패한 항목이 있습니다. 문제를 해결한 뒤 이 프로그램을 다시 실행하면 남은 것만 설치합니다.")
	}
	fmt.Println()
	waitEnter("Enter를 누르면 닫힙니다.")
}
