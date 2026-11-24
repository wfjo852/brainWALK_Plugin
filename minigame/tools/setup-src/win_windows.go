package main

import (
	"os/exec"
	"syscall"
	"unsafe"
)

func hideWindow(cmd *exec.Cmd) { cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true} }

// 콘솔을 UTF-8 + 색상 지원으로
func setupConsole() {
	k32 := syscall.NewLazyDLL("kernel32.dll")
	k32.NewProc("SetConsoleOutputCP").Call(65001)
	k32.NewProc("SetConsoleCP").Call(65001)
	h, _, _ := k32.NewProc("GetStdHandle").Call(uintptr(0xFFFFFFF5)) // STD_OUTPUT_HANDLE (-11)
	var mode uint32
	if r, _, _ := k32.NewProc("GetConsoleMode").Call(h, uintptr(unsafe.Pointer(&mode))); r != 0 {
		k32.NewProc("SetConsoleMode").Call(h, uintptr(mode|0x0004)) // ENABLE_VIRTUAL_TERMINAL_PROCESSING
	}
	k32.NewProc("SetConsoleTitleW").Call(uintptr(unsafe.Pointer(syscall.StringToUTF16Ptr("Femto Bolt Skeleton 설치"))))
}
