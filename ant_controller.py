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
        self.hr_period = 8070
        self.rf_freq = 57

        self.on_bpm: Optional[Callable] = None
        self.on_status: Optional[Callable] = None
        self.on_error: Optional[Callable] = None

        self._running = False
        self._thread: Optional[threading.Thread] = None
        self._node = None          # stop()에서 접근하기 위해 클래스 멤버로
        self._node_lock = threading.Lock()
        self._last_send_time = 0
        self.last_bpm = 0
        self.is_running = False

    def start(self):
        if self._running:
            return
        self._running = True
        self.is_running = True
        self._thread = threading.Thread(target=self._run_loop, daemon=True)
        self._thread.start()

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
            channel.set_id(self.device_id, self.device_type, 0)
            channel.set_period(self.hr_period)
            channel.set_rf_freq(self.rf_freq)

            def on_data(data):
                if not data or len(data) < 8:
                    return
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
            with self._node_lock:
                self._node = None
            sock.close()
            self._cleanup()

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
