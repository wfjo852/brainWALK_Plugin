# -*- mode: python ; coding: utf-8 -*-
from PyInstaller.utils.hooks import collect_all

datas = [('lidar_config.json', '.'), ('icon\\setting_icon.png', 'icon')]
binaries = [('libusb-1.0.dll', '.')]
hiddenimports = ['openant', 'openant.easy', 'openant.easy.node', 'openant.easy.channel', 'openant.base', 'openant.base.ant', 'openant.base.driver', 'openant.base.message', 'openant.devices', 'usb', 'usb.core', 'usb.util', 'usb.backend', 'usb.backend.libusb0', 'usb.backend.libusb1', 'usb.backend.openusb', 'serial', 'serial.tools', 'serial.tools.list_ports', 'matplotlib', 'matplotlib.backends.backend_tkagg', 'matplotlib.figure', 'numpy', 'screeninfo', 'pyautogui', 'PIL']
tmp_ret = collect_all('openant')
datas += tmp_ret[0]; binaries += tmp_ret[1]; hiddenimports += tmp_ret[2]
tmp_ret = collect_all('usb')
datas += tmp_ret[0]; binaries += tmp_ret[1]; hiddenimports += tmp_ret[2]


a = Analysis(
    ['main.py'],
    pathex=[],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='LidarANT',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=['icon\\setting_icon.png'],
)
