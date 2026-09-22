"""
ANT+ 심박계 컨트롤러
- 백그라운드 스레드로 실행
- stop() 시 node.stop()으로 블로킹 루프 즉시 중단
- UDP로 데이터 전송
"""
import os
import sys
import gc
import socket
import json
import time
import threading
from typing import Callable, Optional


def _find_libusb_dll() -> Optional[str]:
    """libusb-1.0.dll 경로 탐색 (프로젝트 폴더 우선)"""
    search_dirs = []

    if getattr(sys, 'frozen', False):
        if hasattr(sys, '_MEIPASS'):
            search_dirs.append(sys._MEIPASS)
        search_dirs.append(os.path.dirname(sys.executable))
    else:
        search_dirs.append(os.path.dirname(os.path.abspath(__file__)))

    for d in search_dirs:
        path = os.path.join(d, 'libusb-1.0.dll')
        if os.path.exists(path):
            return path
    return None


def _patch_usb_backend():
    """libusb-1.0.dll을 ctypes로 미리 로드해서 pyusb가 우선 선택하도록 함"""
    dll = _find_libusb_dll()
    if not dll:
        return
    dll_dir = os.path.dirname(dll)
    if hasattr(os, 'add_dll_directory'):
        try:
            os.add_dll_directory(dll_dir)
        except Exception:
            pass
    os.environ['PATH'] = dll_dir + os.pathsep + os.environ.get('PATH', '')
    if 'libusb-1.0' in os.path.basename(dll):
        try:
            import ctypes
            ctypes.CDLL(dll)
        except Exception:
            pass


def _purge_modules():
    """openant/usb 모듈 캐시 제거 → 재import 시 완전 초기화"""
    to_remove = [k for k in sys.modules if k.startswith(('openant', 'usb'))]
    for k in to_remove:
        del sys.modules[k]
    gc.collect()


def _release_usb():
    """ANT+ 동글 USB 리소스 강제 해제"""
    try:
        import usb.core
        import usb.util
        devices = list(usb.core.find(find_all=True, idVendor=0x0FCF))
        for dev in devices:
            try:
                usb.util.dispose_resources(dev)
            except Exception:
                pass
    except Exception:
        pass


class AntController:
    def __init__(self):
        self.udp_ip = "127.0.0.1"
        self.udp_port = 5005

        self.device_type = 120
        self.device_id = 0
        self.transmission_type = 0
        self.hr_period = 8070
        self.rf_freq = 57

        self.on_bpm: Optional[Callable] = None
        self.on_status: Optional[Callable] = None
        self.on_error: Optional[Callable] = None
        self.on_device_found: Optional[Callable] = None   # (device_id, device_type, transmission_type)
        self.on_scan_done: Optional[Callable] = None
        self.on_connected: Optional[Callable] = None       # (device_id)
        self.on_disconnected: Optional[Callable] = None

        self._running = False
        self._thread: Optional[threading.Thread] = None
        self._node = None          # stop()에서 접근하기 위해 클래스 멤버로
        self._node_lock = threading.Lock()
        self._last_send_time = 0
        self.last_bpm = 0
        self.is_running = False

        self._scanning = False
        self._scan_node = None
        self._scan_thread: Optional[threading.Thread] = None

        self._connected = False
        self._last_data_time = 0.0
        self._watchdog_stop: Optional[threading.Event] = None
        self._watchdog_thread: Optional[threading.Thread] = None
        self.disconnect_timeout = 6.0  # 이 시간(초) 동안 데이터가 없으면 연결 해제로 판단

    def start(self):
        if self._running:
            return
        self._running = True
        self.is_running = True
        self._thread = threading.Thread(target=self._run_loop, daemon=True)
        self._thread.start()

    def start_scan(self, duration: float = 10.0):
        """주변 ANT+ 장비 검색 (self.on_device_found로 발견된 장비 콜백)"""
        if self._scanning or self._running:
            return
        self._scanning = True
        self._scan_thread = threading.Thread(target=self._scan_loop, args=(duration,), daemon=True)
        self._scan_thread.start()

    @property
    def is_scanning(self) -> bool:
        return self._scanning

    def stop_scan(self):
        self._scanning = False
        node = self._scan_node
        if node is not None:
            try:
                node.stop()
            except Exception:
                pass

    def _scan_loop(self, duration: float):
        _patch_usb_backend()

        from openant.easy.node import Node
        from openant.devices import ANTPLUS_NETWORK_KEY
        from openant.devices.scanner import Scanner

        found_keys = set()
        node = Node()
        self._scan_node = node
        timer = None
        try:
            node.set_network_key(0x00, ANTPLUS_NETWORK_KEY)
            scanner = Scanner(node, device_id=0, device_type=0)

            def on_found(device_tuple):
                dev_id, dev_type, dev_trans = device_tuple
                key = (dev_id, dev_type, dev_trans)
                if key in found_keys:
                    return
                found_keys.add(key)
                if self.on_device_found:
                    self.on_device_found(dev_id, dev_type, dev_trans)

            scanner.on_found = on_found

            timer = threading.Timer(duration, node.stop)
            timer.daemon = True
            timer.start()

            self._notify_status(f"장비 검색 중... ({int(duration)}초)")
            node.start()  # 블로킹 — stop_scan() 또는 timeout으로 탈출
        except Exception as e:
            self._notify_error(f"스캔 오류: {e}")
        finally:
            if timer is not None:
                timer.cancel()
            self._scan_node = None
            self._scanning = False
            _release_usb()
            _purge_modules()
            if self.on_scan_done:
                self.on_scan_done()

    def stop(self):
        self._running = False
        # node.start() 블로킹 루프를 외부에서 즉시 중단
        with self._node_lock:
            node = self._node
        if node is not None:
            try:
                node.stop()
            except Exception:
                pass
        # 스레드 종료 대기 (최대 5초)
        if self._thread is not None:
            self._thread.join(timeout=5)
        self.is_running = False

    def _run_loop(self):
        while self._running:
            try:
                self._run_once()
            except Exception as e:
                self._notify_error(str(e))
                if self._running:
                    self._notify_status("USB 정리 중...")
                    self._cleanup()
                    self._notify_status("3초 후 재시도...")
                    time.sleep(3)
        self.is_running = False

    def _run_once(self):
        _patch_usb_backend()

        from openant.easy.node import Node
        from openant.devices import ANTPLUS_NETWORK_KEY

        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

        self._notify_status("ANT+ 동글 연결 중...")

        node = Node()
        with self._node_lock:
            self._node = node

        try:
            node.set_network_key(0, ANTPLUS_NETWORK_KEY)

            channel = node.new_channel(0x00)
            channel.set_id(self.device_id, self.device_type, self.transmission_type)
            channel.set_period(self.hr_period)
            channel.set_rf_freq(self.rf_freq)

            self._connected = False
            self._last_data_time = time.time()
            self._watchdog_stop = threading.Event()
            self._watchdog_thread = threading.Thread(
                target=self._watchdog_loop, args=(self._watchdog_stop,), daemon=True)
            self._watchdog_thread.start()

            def on_data(data):
                if not data or len(data) < 8:
                    return
                self._last_data_time = time.time()
                if not self._connected:
                    self._connected = True
                    self._notify_connected(self.device_id)
                now = time.time()
                if now - self._last_send_time < 1.0:
                    return
                self._last_send_time = now
                bpm = data[7]
                self.last_bpm = bpm
                msg = json.dumps({"bpm": bpm})
                try:
                    sock.sendto(msg.encode('utf-8'), (self.udp_ip, self.udp_port))
                except Exception as e:
                    self._notify_error(f"UDP 전송 오류: {e}")
                if self.on_bpm:
                    self.on_bpm(bpm)

            channel.on_broadcast_data = on_data
            channel.on_burst_data = on_data

            self._notify_status(f"ANT+ 수신 중 → UDP {self.udp_ip}:{self.udp_port}")
            channel.open()
            node.start()  # 블로킹 — stop()에서 node.stop()으로 탈출

        finally:
            if self._watchdog_stop is not None:
                self._watchdog_stop.set()
            if self._watchdog_thread is not None:
                self._watchdog_thread.join(timeout=2)
            if self._connected:
                self._connected = False
                self._notify_disconnected()
            with self._node_lock:
                self._node = None
            sock.close()
            self._cleanup()

    def _watchdog_loop(self, stop_event: threading.Event):
        """일정 시간 이상 데이터가 없으면 연결 해제로 판단"""
        while not stop_event.wait(1.0):
            if self._connected and (time.time() - self._last_data_time) > self.disconnect_timeout:
                self._connected = False
                self._notify_disconnected()

    def _cleanup(self):
        """USB 완전 해제 + 모듈 캐시 초기화"""
        _release_usb()
        _purge_modules()
        time.sleep(1.0)

    def _notify_status(self, msg: str):
        if self.on_status:
            self.on_status(msg)

    def _notify_error(self, msg: str):
        if self.on_error:
            self.on_error(msg)

    def _notify_connected(self, device_id: int):
        if self.on_connected:
            self.on_connected(device_id)

    def _notify_disconnected(self):
        if self.on_disconnected:
            self.on_disconnected()
