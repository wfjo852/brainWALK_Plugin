package main

import (
	"archive/zip"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestScoreAsset(t *testing.T) {
	names := []string{"OrbbecSDK_K4A_Wrapper_v2.0.12_linux_x64.tar.gz", "OrbbecSDK_K4A_Wrapper_v2.0.12_win_x86.zip",
		"OrbbecSDK_K4A_Wrapper_v2.0.12_windows_x64_release.zip", "Source code (zip)", "OrbbecSDK_K4A_Wrapper_v1.10.3_windows_202406.zip"}
	best, bs := "", 0
	for _, n := range names {
		if s := scoreAsset(n); s > bs {
			best, bs = n, s
		}
	}
	if !strings.Contains(best, "windows_x64") {
		t.Fatalf("picked %q", best)
	}
}

func TestExtractPicksX64Bin(t *testing.T) {
	dir := t.TempDir()
	zp := filepath.Join(dir, "w.zip")
	f, _ := os.Create(zp)
	zw := zip.NewWriter(f)
	for _, n := range []string{"pkg/x86/bin/k4a.dll", "pkg/x64/bin/k4a.dll", "pkg/x64/bin/OrbbecSDK.dll", "pkg/x64/bin/OrbbecSDKConfig.xml", "pkg/x64/bin/extensions/depthengine/depthengine.dll", "pkg/x64/lib/k4a.lib", "../evil.txt"} {
		w, _ := zw.Create(n)
		w.Write([]byte(n))
	}
	zw.Close()
	f.Close()
	bin, files, err := extractWrapperBin(zp, filepath.Join(dir, "out"))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasSuffix(filepath.ToSlash(bin), "pkg/x64/bin") || len(files) != 4 || !strings.Contains(strings.Join(files, "|"), filepath.Join("extensions", "depthengine", "depthengine.dll")) {
		t.Fatalf("bin=%s files=%v", bin, files)
	}
	if _, err := os.Stat(filepath.Join(dir, "evil.txt")); err == nil {
		t.Fatal("path traversal")
	}
}

func TestDownloadResume(t *testing.T) {
	payload := strings.Repeat("0123456789", 50000)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.ServeContent(w, r, "x.bin", timeZero, strings.NewReader(payload))
	}))
	defer srv.Close()
	dir := t.TempDir()
	dest := filepath.Join(dir, "x.bin")
	os.WriteFile(dest+".part", []byte(payload[:123456]), 0o644) // 중간에 끊긴 상태
	if err := download(srv.URL, dest); err != nil {
		t.Fatal(err)
	}
	b, _ := os.ReadFile(dest)
	if string(b) != payload {
		t.Fatalf("size %d", len(b))
	}
}

var timeZero = time.Time{}

func TestNonASCII(t *testing.T) {
	if nonASCII(`D:\Femto Web Test\FemtoSkeleton`) || !nonASCII(`C:\Users\홍길동\바탕 화면\FemtoSkeleton`) {
		t.Fatal("nonASCII")
	}
}

func TestVerifyRuntimeCopiesMissing(t *testing.T) {
	root := t.TempDir()
	rootDir = root
	pf := t.TempDir()
	t.Setenv("ProgramFiles", pf)
	sdk := btSDKBin()
	os.MkdirAll(sdk, 0o755)
	for _, f := range append(btRuntimeFiles, "dnn_model_2_0_lite_op11.onnx") {
		os.WriteFile(filepath.Join(sdk, f), []byte("sdk-"+f), 0o644)
	}
	os.MkdirAll(orbbecDir(), 0o755)
	os.WriteFile(filepath.Join(orbbecDir(), "k4a.dll"), []byte("orbbec-k4a-longer"), 0o644)
	os.WriteFile(filepath.Join(orbbecDir(), "OrbbecSDK.dll"), []byte("ob"), 0o644)
	out := filepath.Join(root, "agent", "bin", "x64", "Release", "net8.0", "win-x64")
	os.MkdirAll(out, 0o755)
	os.WriteFile(filepath.Join(out, "FemtoSkeletonAgent.dll"), []byte("x"), 0o644)
	os.WriteFile(filepath.Join(out, "k4a.dll"), []byte("microsoft"), 0o644)     // 잘못된 k4a.dll
	os.WriteFile(filepath.Join(out, "onnxruntime.dll"), []byte("nuget"), 0o644) // 이미 있음 → 유지
	if !verifyRuntime() {
		t.Fatal("verify failed")
	}
	b, _ := os.ReadFile(filepath.Join(out, "k4a.dll"))
	if string(b) != "orbbec-k4a-longer" {
		t.Fatal("k4a not replaced")
	}
	b, _ = os.ReadFile(filepath.Join(out, "onnxruntime.dll"))
	if string(b) != "nuget" {
		t.Fatal("existing overwritten")
	}
	for _, f := range []string{"directml.dll", "dnn_model_2_0_op11.onnx", "k4abt.dll", "dnn_model_2_0_lite_op11.onnx"} {
		if !fileExists(filepath.Join(out, f)) {
			t.Fatal("missing " + f)
		}
	}
}

func TestOrbbecReadyNeedsExtensions(t *testing.T) {
	rootDir = t.TempDir()
	os.MkdirAll(orbbecDir(), 0o755)
	for _, f := range []string{"k4a.dll", "OrbbecSDK.dll"} {
		os.WriteFile(filepath.Join(orbbecDir(), f), []byte("x"), 0o644)
	}
	os.WriteFile(filepath.Join(orbbecDir(), "VERSION.txt"), []byte("v2.0.12  (x.zip)"), 0o644)
	if ok, _ := orbbecReady(); ok {
		t.Fatal("v2 without extensions must not be ready")
	}
	os.MkdirAll(filepath.Join(orbbecDir(), "extensions", "depthengine"), 0o755)
	if ok, _ := orbbecReady(); !ok {
		t.Fatal("should be ready")
	}
}
