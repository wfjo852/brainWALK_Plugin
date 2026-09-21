"""
라이다 G4 + ANT+ 통합 빌드 스크립트

사용법:
    python build.py            # GUI 버전 빌드 (기본)
    python build.py --console  # 콘솔 버전 빌드 (디버그용)
    python build.py --all      # 둘 다 빌드
    python build.py --clean    # 빌드 폴더 정리만
"""
import os
import sys
import subprocess
import shutil
import argparse

APP_NAME = "LidarANT"
LIBUSB_DLL = "libusb-1.0.dll"   # 프로젝트 폴더 내 DLL
ICON_FILE = os.path.join("icon", "setting_icon.png")   # 앱 아이콘

COLLECT_ALL = [
    "openant",
    "usb",
]

HIDDEN_IMPORTS = [
    "openant",
    "openant.easy",
    "openant.easy.node",
    "openant.easy.channel",
    "openant.base",
    "openant.base.ant",
    "openant.base.driver",
    "openant.base.message",
    "openant.devices",
    "usb",
    "usb.core",
    "usb.util",
    "usb.backend",
    "usb.backend.libusb0",
    "usb.backend.libusb1",
    "usb.backend.openusb",
    "serial",
    "serial.tools",
    "serial.tools.list_ports",
    "matplotlib",
    "matplotlib.backends.backend_tkagg",
    "matplotlib.figure",
    "numpy",
    "screeninfo",
    "pyautogui",
    "PIL",
]


def check_pyinstaller():
    try:
        import PyInstaller
        print(f"✓ PyInstaller {PyInstaller.__version__} 설치됨")
        return True
    except ImportError:
        print("✗ PyInstaller가 설치되지 않았습니다.")
        print("  설치: pip install pyinstaller")
        return False


def check_libusb():
    if os.path.exists(LIBUSB_DLL):
        print(f"✓ {LIBUSB_DLL} 확인됨")
        return True
    else:
        print(f"✗ {LIBUSB_DLL} 없음 — 프로젝트 폴더에 DLL을 복사하세요")
        return False


def check_icon():
    if os.path.exists(ICON_FILE):
        print(f"✓ {ICON_FILE} 확인됨")
        return True
    else:
        print(f"✗ {ICON_FILE} 없음 — icon 폴더에 setting_icon.png를 넣으세요")
        return False


def clean_build():
    for d in ['build', 'dist', '__pycache__']:
        if os.path.exists(d):
            shutil.rmtree(d)
            print(f"  삭제: {d}/")
    import glob
    for f in glob.glob("*.spec"):
        os.remove(f)
        print(f"  삭제: {f}")


def _base_cmd(name: str, windowed: bool) -> list:
    sep = ';' if sys.platform == 'win32' else ':'

    cmd = [
        sys.executable, '-m', 'PyInstaller',
        f'--name={name}',
        '--onefile',
        '--windowed' if windowed else '--console',
        '--noconfirm',
        '--clean',
        f'--add-data=lidar_config.json{sep}.',
        f'--add-binary={LIBUSB_DLL}{sep}.',
        f'--add-data={ICON_FILE}{sep}icon',
        f'--icon={ICON_FILE}',
    ]

    for pkg in COLLECT_ALL:
        cmd.append(f'--collect-all={pkg}')

    for h in HIDDEN_IMPORTS:
        cmd.append(f'--hidden-import={h}')

    cmd.append('main.py')
    return cmd


def build_gui():
    print("\n" + "=" * 50)
    print(f"GUI 버전 빌드: {APP_NAME}.exe")
    print("=" * 50)
    result = subprocess.run(_base_cmd(APP_NAME, windowed=True))
    if result.returncode == 0:
        print(f"\n✓ 완료 → dist/{APP_NAME}.exe")
        return True
    print("\n✗ 빌드 실패")
    return False


def build_console():
    name = f"{APP_NAME}_Console"
    print("\n" + "=" * 50)
    print(f"콘솔 버전 빌드: {name}.exe")
    print("=" * 50)
    result = subprocess.run(_base_cmd(name, windowed=False))
    if result.returncode == 0:
        print(f"\n✓ 완료 → dist/{name}.exe")
        return True
    print("\n✗ 빌드 실패")
    return False


def copy_assets():
    if os.path.exists('dist'):
        if os.path.exists('lidar_config.json'):
            shutil.copy('lidar_config.json', 'dist/')
            print("✓ lidar_config.json 복사됨")


def main():
    parser = argparse.ArgumentParser(description='라이다 G4 + ANT+ 통합 빌드')
    parser.add_argument('--console', action='store_true', help='콘솔 버전만 빌드')
    parser.add_argument('--all', action='store_true', help='GUI + 콘솔 모두 빌드')
    parser.add_argument('--clean', action='store_true', help='빌드 폴더 정리만')
    args = parser.parse_args()

    print("=" * 50)
    print("라이다 G4 + ANT+ 통합 빌드")
    print("=" * 50)

    if not args.clean:
        if not check_pyinstaller():
            sys.exit(1)
        if not check_libusb():
            sys.exit(1)
        if not check_icon():
            sys.exit(1)

    print("\n빌드 폴더 정리 중...")
    clean_build()

    if args.clean:
        print("\n정리 완료!")
        return

    if args.all:
        success = build_gui() and build_console()
    elif args.console:
        success = build_console()
    else:
        success = build_gui()

    if success:
        copy_assets()
        print("\n" + "=" * 50)
        print("빌드 완료!  출력: dist/")
        print("=" * 50)
    else:
        print("\n빌드 중 오류가 발생했습니다.")
        sys.exit(1)


if __name__ == '__main__':
    main()
