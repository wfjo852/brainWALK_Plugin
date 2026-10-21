"""
ANT+ 장비 스캔 테스트 (콘솔 확인용)
- 주변 ANT+ 장비를 스캔해서 device_id / device_type / transmission_type 출력
- Ctrl+C 로 종료
"""
from ant_controller import _patch_usb_backend, _release_usb, _purge_modules

_patch_usb_backend()

from openant.easy.node import Node
from openant.devices import ANTPLUS_NETWORK_KEY
from openant.devices.common import DeviceType
from openant.devices.scanner import Scanner


def on_found(device_tuple):
    device_id, device_type, device_trans = device_tuple
    try:
        type_name = DeviceType(device_type).name
    except ValueError:
        type_name = "Unknown"
    print(f"[FOUND] id={device_id} type={device_type}({type_name}) trans={device_trans}")


def on_update(device_tuple, common):
    device_id = device_tuple[0]
    print(f"[UPDATE] id={device_id} common={common}")


def main():
    node = Node()
    node.set_network_key(0x00, ANTPLUS_NETWORK_KEY)

    scanner = Scanner(node, device_id=0, device_type=0)
    scanner.on_found = on_found
    scanner.on_update = on_update

    print("ANT+ 장비 스캔 시작... (Ctrl+C 로 종료)")
    try:
        node.start()
    except KeyboardInterrupt:
        print("스캔 종료 중...")
    finally:
        scanner.close_channel()
        node.stop()
        _release_usb()
        _purge_modules()


if __name__ == "__main__":
    main()
