"""
라이다 기능 핵심 모듈 - 최적화 버전
- 시리얼 통신 (타임아웃 최적화)
- 데이터 파싱 및 처리
- 터치 감지 로직
- 마우스 제어 (고속)
- 병렬 처리 지원
"""
import serial
import serial.tools.list_ports
import math
import time
import threading
import atexit
import queue
import numpy as np
from math import atan, pi, floor
from typing import Dict, List, Callable, Optional, Tuple

try:
    import pyautogui
    PYAUTOGUI_AVAILABLE = True
    # === 핵심 최적화: pyautogui 딜레이 제거 ===
    pyautogui.PAUSE = 0  # 명령 간 딜레이 완전 제거
    pyautogui.FAILSAFE = False  # 모서리 안전장치 비활성화
except ImportError:
    PYAUTOGUI_AVAILABLE = False
    print("경고: pyautogui가 설치되지 않아 마우스 제어 기능을 사용할 수 없습니다.")


def get_available_ports() -> List[Tuple[str, str]]:
    """
    사용 가능한 시리얼 포트 목록 반환
    Returns: [(포트명, 설명), ...]
    """
    ports = serial.tools.list_ports.comports()
    result = []
    for port in ports:
        desc = port.description if port.description else port.device
        result.append((port.device, desc))
    return result


def get_monitors() -> List[Dict]:
    """
    사용 가능한 모니터 목록 반환
    Returns: [{'name': str, 'x': int, 'y': int, 'width': int, 'height': int}, ...]
    """
    monitors = []
    
    # 방법 1: screeninfo 라이브러리 사용
    try:
        from screeninfo import get_monitors as get_screeninfo_monitors
        for i, m in enumerate(get_screeninfo_monitors()):
            monitors.append({
                'name': f"모니터 {i+1}: {m.width}x{m.height} ({m.x},{m.y})",
                'x': m.x,
                'y': m.y,
                'width': m.width,
                'height': m.height
            })
        if monitors:
            return monitors
    except:
        pass
    
    # 방법 2: Windows API 직접 사용
    try:
        import ctypes
        from ctypes import wintypes
        
        user32 = ctypes.windll.user32
        
        MONITORENUMPROC = ctypes.WINFUNCTYPE(
            ctypes.c_bool,
            ctypes.c_ulong,
            ctypes.c_ulong,
            ctypes.POINTER(wintypes.RECT),
            ctypes.c_double
        )
        
        def callback(hMonitor, hdcMonitor, lprcMonitor, dwData):
            rect = lprcMonitor.contents
            width = rect.right - rect.left
            height = rect.bottom - rect.top
            monitors.append({
                'name': f"모니터 {len(monitors)+1}: {width}x{height} ({rect.left},{rect.top})",
                'x': rect.left,
                'y': rect.top,
                'width': width,
                'height': height
            })
            return True
        
        user32.EnumDisplayMonitors(None, None, MONITORENUMPROC(callback), 0)
        
        if monitors:
            return monitors
    except:
        pass
    
    # 방법 3: tkinter로 기본 화면 크기만
    try:
        import tkinter as tk
        temp_root = tk.Tk()
        temp_root.withdraw()
        width = temp_root.winfo_screenwidth()
        height = temp_root.winfo_screenheight()
        temp_root.destroy()
        monitors.append({
            'name': f"기본 모니터: {width}x{height}",
            'x': 0,
            'y': 0,
            'width': width,
            'height': height
        })
        return monitors
    except:
        pass
    
    # 방법 4: pyautogui 사용
    if PYAUTOGUI_AVAILABLE:
        try:
            size = pyautogui.size()
            monitors.append({
                'name': f"기본 모니터: {size[0]}x{size[1]}",
                'x': 0,
                'y': 0,
                'width': size[0],
                'height': size[1]
            })
            return monitors
        except:
            pass
    
    # 기본값
    monitors.append({
        'name': "기본 모니터: 1920x1080",
        'x': 0,
        'y': 0,
        'width': 1920,
        'height': 1080
    })
    return monitors


class LidarDataParser:
    """라이다 데이터 파싱 클래스"""
    
    @staticmethod
    def hex_arr_to_dec(data: tuple) -> int:
        """16진수 배열을 10진수로 변환"""
        val = 0
        for i in range(len(data)):
            val = val + (data[i] * (256 ** i))
        return val
    
    @staticmethod
    def angle_correction(dist: float) -> float:
        """각도 보정"""
        if dist == 0:
            return 0
        return (atan(21.8 * ((155.3 - dist) / (155.3 * dist))) * (180 / pi))
    
    @classmethod
    def check_sum(cls, data: bytes) -> bool:
        """체크섬 검증"""
        try:
            ocs = cls.hex_arr_to_dec((data[6], data[7]))
            LSN = data[1]
            cs = 0x55AA ^ cls.hex_arr_to_dec((data[0], data[1])) ^ \
                 cls.hex_arr_to_dec((data[2], data[3])) ^ \
                 cls.hex_arr_to_dec((data[4], data[5]))
            for i in range(0, 2*LSN, 2):
                cs = cs ^ cls.hex_arr_to_dec((data[8+i], data[8+i+1]))
            return cs == ocs
        except:
            return False
    
    @classmethod
    def calculate(cls, d: bytes) -> List[Tuple[float, float]]:
        """거리 및 각도 계산"""
        ddict = []
        LSN = d[1]
        Angle_fsa = ((cls.hex_arr_to_dec((d[2], d[3])) >> 1) / 64.0)
        Angle_lsa = ((cls.hex_arr_to_dec((d[4], d[5])) >> 1) / 64.0)

        if Angle_fsa < Angle_lsa:
            Angle_diff = Angle_lsa - Angle_fsa
        else:
            Angle_diff = 360 + Angle_lsa - Angle_fsa

        for i in range(0, 2*LSN, 2):
            dist_i = cls.hex_arr_to_dec((d[8+i], d[8+i+1])) / 4
            Angle_i_tmp = ((Angle_diff / float(LSN)) * (i / 2)) + Angle_fsa

            if Angle_i_tmp > 360:
                Angle_i = Angle_i_tmp - 360
            elif Angle_i_tmp < 0:
                Angle_i = Angle_i_tmp + 360
            else:
                Angle_i = Angle_i_tmp

            Angle_i = Angle_i + cls.angle_correction(dist_i)
            ddict.append((dist_i, Angle_i))

        return ddict
    
    @staticmethod
    def mean(data: List[float]) -> float:
        """0이 아닌 값들의 평균 계산"""
        non_zero = [i for i in data if i != 0]
        if len(non_zero) > 0:
            return float(sum(non_zero) / len(non_zero))
        return 0


class LidarConnection:
    """라이다 시리얼 연결 관리 클래스 - 최적화 버전"""
    
    CMD_START_SCAN = bytearray([0xa5, 0x60])
    CMD_STOP_SCAN = bytearray([0xa5, 0x65])
    CMD_GET_FREQ = bytearray([0xa5, 0x0d])      # 현재 주파수 조회
    CMD_FREQ_UP_01 = bytearray([0xa5, 0x09])    # +0.1Hz
    CMD_FREQ_DOWN_01 = bytearray([0xa5, 0x0a])  # -0.1Hz
    CMD_FREQ_UP_1 = bytearray([0xa5, 0x0b])     # +1Hz
    CMD_FREQ_DOWN_1 = bytearray([0xa5, 0x0c])   # -1Hz
    DATA_DELIMITER = b"\xaa\x55"
    
    def __init__(self):
        self.ser: Optional[serial.Serial] = None
        self.is_connected = False
        self.current_freq = 7.0  # 기본 주파수 (Hz)
    
    def connect(self, port: str, baudrate: int) -> bool:
        """라이다 연결 - 최적화된 타임아웃 설정"""
        try:
            self.ser = serial.Serial(
                port=port,
                baudrate=baudrate,
                timeout=0.5,           # 응답 대기용 타임아웃 (충분히 길게)
                write_timeout=0.5
            )
            self.is_connected = self.ser.isOpen()
            return self.is_connected
        except Exception as e:
            self.is_connected = False
            raise ConnectionError(f"라이다 연결 실패: {e}")
    
    def disconnect(self):
        """라이다 연결 해제"""
        if self.ser:
            try:
                self.ser.write(self.CMD_STOP_SCAN)
                time.sleep(0.1)
                self.ser.close()
            except:
                pass
        self.is_connected = False
    
    def start_scan(self) -> bool:
        """스캔 시작 명령 전송"""
        if self.ser and self.is_connected:
            self.ser.write(self.CMD_START_SCAN)
            return True
        return False
    
    def stop_scan(self) -> bool:
        """스캔 중지 명령 전송"""
        if self.ser and self.is_connected:
            self.ser.write(self.CMD_STOP_SCAN)
            time.sleep(0.1)  # 중지 후 안정화 대기
            self.ser.reset_input_buffer()  # 버퍼 정리
            return True
        return False
    
    def _send_and_receive(self, cmd: bytearray) -> bytes:
        """명령 전송 후 응답 수신"""
        if not self.ser or not self.is_connected:
            return b""
        
        try:
            # 버퍼 정리
            self.ser.reset_input_buffer()
            time.sleep(0.05)
            
            # 명령 전송
            self.ser.write(cmd)
            
            # 응답 대기 (최대 500ms)
            time.sleep(0.1)
            
            # 응답 읽기 (헤더 7바이트 + 데이터 4바이트 = 11바이트)
            response = self.ser.read(11)
            return response
        except Exception as e:
            print(f"[DEBUG] 통신 오류: {e}")
            return b""
    
    def _parse_freq_response(self, response: bytes) -> float:
        """주파수 응답 파싱 (A5 5A 04 00 00 00 04 [4bytes])"""
        try:
            if len(response) >= 11:
                # 시작 사인 확인
                if response[0] == 0xA5 and response[1] == 0x5A:
                    # 리틀 엔디안 4바이트 → 주파수 (Hz = value / 100)
                    freq_raw = (response[7] | 
                               (response[8] << 8) | 
                               (response[9] << 16) | 
                               (response[10] << 24))
                    freq = freq_raw / 100.0
                    print(f"[DEBUG] 응답 파싱: raw={freq_raw}, freq={freq:.2f}Hz")
                    return freq
                else:
                    print(f"[DEBUG] 잘못된 시작 사인: {response[:2].hex()}")
            else:
                print(f"[DEBUG] 응답 길이 부족: {len(response)} bytes")
        except Exception as e:
            print(f"[DEBUG] 파싱 오류: {e}")
        return -1  # 실패 시 -1 반환
    
    def get_scan_frequency(self) -> float:
        """현재 스캔 주파수 조회 (스캔 중지 상태에서만)"""
        if not self.ser or not self.is_connected:
            return self.current_freq
        
        response = self._send_and_receive(self.CMD_GET_FREQ)
        freq = self._parse_freq_response(response)
        
        if freq > 0:
            self.current_freq = freq
            return freq
        return self.current_freq
    
    def increase_frequency(self, step: float = 1.0) -> float:
        """
        주파수 증가 (스캔 중지 상태에서만)
        step: 0.1 또는 1.0
        """
        if not self.ser or not self.is_connected:
            return self.current_freq
        
        cmd = self.CMD_FREQ_UP_1 if step >= 1.0 else self.CMD_FREQ_UP_01
        print(f"[DEBUG] 주파수 증가 명령: {cmd.hex()}")
        
        response = self._send_and_receive(cmd)
        freq = self._parse_freq_response(response)
        
        if freq > 0:
            self.current_freq = freq
            return freq
        return self.current_freq
    
    def decrease_frequency(self, step: float = 1.0) -> float:
        """
        주파수 감소 (스캔 중지 상태에서만)
        step: 0.1 또는 1.0
        """
        if not self.ser or not self.is_connected:
            return self.current_freq
        
        cmd = self.CMD_FREQ_DOWN_1 if step >= 1.0 else self.CMD_FREQ_DOWN_01
        print(f"[DEBUG] 주파수 감소 명령: {cmd.hex()}")
        
        response = self._send_and_receive(cmd)
        freq = self._parse_freq_response(response)
        
        if freq > 0:
            self.current_freq = freq
            return freq
        return self.current_freq
    
    def set_scan_frequency(self, target_freq: float) -> float:
        """
        목표 주파수로 설정 (스캔 중지 상태에서만)
        4~12 Hz 범위
        
        주의: 스캔 중에는 호출하면 안 됨!
        """
        if not self.ser or not self.is_connected:
            return self.current_freq
        
        target_freq = max(4.0, min(12.0, target_freq))
        print(f"[DEBUG] 목표 주파수: {target_freq:.1f}Hz")
        
        try:
            # 현재 주파수 조회
            current = self.get_scan_frequency()
            print(f"[DEBUG] 현재 주파수: {current:.1f}Hz")
            
            # 목표까지 조정 (최대 20번 시도)
            for i in range(20):
                diff = target_freq - current
                
                if abs(diff) < 0.15:  # 0.15Hz 이내면 완료
                    print(f"[DEBUG] 목표 도달: {current:.1f}Hz")
                    break
                
                if diff > 0:
                    # 주파수 증가
                    step = 1.0 if diff >= 1.0 else 0.1
                    current = self.increase_frequency(step)
                else:
                    # 주파수 감소
                    step = 1.0 if diff <= -1.0 else 0.1
                    current = self.decrease_frequency(step)
                
                print(f"[DEBUG] 조정 {i+1}: {current:.1f}Hz (목표: {target_freq:.1f}Hz)")
                time.sleep(0.05)
            
            self.current_freq = current
            return current
            
        except Exception as e:
            print(f"[DEBUG] 주파수 설정 오류: {e}")
            return self.current_freq
    
    def read_data(self, size: int = 6000) -> bytes:
        """데이터 읽기 - 최적화: 버퍼에 있는 데이터만 즉시 읽기"""
        if self.ser and self.is_connected:
            # === 최적화: 버퍼에 있는 만큼만 즉시 읽기 ===
            available = self.ser.in_waiting
            if available > 0:
                return self.ser.read(min(available, size))
        return b""
    
    def read_data_blocking(self, size: int = 4000) -> bytes:
        """데이터 읽기 - 블로킹 방식 (기존 호환성)"""
        if self.ser and self.is_connected:
            return self.ser.read(size)
        return b""


class TouchDetector:
    """터치 감지 클래스 - 최적화 버전"""
    
    def __init__(self):
        self.touch_area = {
            'x': -500,
            'y': 500,
            'width': 1000,
            'height': 800
        }
        
        self.threshold = 10000
        self.min_points = 1
        
        # 반전 및 회전 설정
        self.flip_x = False
        self.flip_y = False
        self.rotation = 0
        
        # 스캔 범위 설정 (기본 180도)
        self.scan_start_angle = 0
        self.scan_end_angle = 180
        
        # 각도 테이블 미리 계산 (0~359도)
        angles_rad = np.radians(np.arange(360))
        self.cos_table = np.cos(angles_rad).astype(np.float32)
        self.sin_table = np.sin(angles_rad).astype(np.float32)
        
        # 스캔 마스크 (유효한 각도만)
        self._update_scan_mask()
        
        self.touch_points: List[Dict] = []
        self.last_center: Optional[Tuple[float, float]] = None
    
    def _update_scan_mask(self):
        """스캔 범위 마스크 업데이트"""
        self.scan_mask = np.zeros(360, dtype=bool)
        start = self.scan_start_angle % 360
        end = self.scan_end_angle % 360
        
        if start <= end:
            self.scan_mask[start:end+1] = True
        else:
            self.scan_mask[start:] = True
            self.scan_mask[:end+1] = True
    
    def set_scan_range(self, start_angle: int, end_angle: int):
        """스캔 범위 설정"""
        self.scan_start_angle = start_angle
        self.scan_end_angle = end_angle
        self._update_scan_mask()
    
    def set_touch_area(self, x: int, y: int, width: int, height: int):
        self.touch_area = {'x': x, 'y': y, 'width': width, 'height': height}
    
    def set_flip(self, flip_x: bool, flip_y: bool):
        self.flip_x = flip_x
        self.flip_y = flip_y
    
    def set_rotation(self, rotation: int):
        self.rotation = rotation % 360
    
    def set_detection_params(self, threshold: int, min_points: int, click_time: float = 0):
        self.threshold = threshold
        self.min_points = min_points
    
    def detect_fast(self, distances: np.ndarray) -> Dict:
        """
        고속 터치 감지 - NumPy 벡터 연산
        distances: 360개 거리값 배열
        """
        # 스캔 범위 + 유효 거리 필터링
        valid_mask = self.scan_mask & (distances > 0) & (distances < self.threshold)
        
        if not np.any(valid_mask):
            return {'is_touching': False, 'center': None, 'event': None, 'touch_points': []}
        
        # 유효한 인덱스와 거리
        valid_indices = np.where(valid_mask)[0]
        valid_distances = distances[valid_mask]
        
        # 좌표 계산 (벡터 연산)
        x_coords = valid_distances * self.cos_table[valid_indices]
        y_coords = valid_distances * self.sin_table[valid_indices]
        
        # 회전 적용
        if self.rotation == 90:
            x_coords, y_coords = y_coords.copy(), -x_coords
        elif self.rotation == 180:
            x_coords, y_coords = -x_coords, -y_coords
        elif self.rotation == 270:
            x_coords, y_coords = -y_coords, x_coords.copy()
        
        # 반전 적용
        if self.flip_x:
            x_coords = -x_coords
        if self.flip_y:
            y_coords = -y_coords
        
        # 터치 영역 필터링
        ta = self.touch_area
        x_min, x_max = ta['x'], ta['x'] + ta['width']
        y_min, y_max = ta['y'], ta['y'] + ta['height']
        
        if y_min > y_max:
            y_min, y_max = y_max, y_min
        
        area_mask = (x_coords >= x_min) & (x_coords <= x_max) & \
                    (y_coords >= y_min) & (y_coords <= y_max)
        
        touch_x = x_coords[area_mask]
        touch_y = y_coords[area_mask]
        
        if len(touch_x) >= self.min_points:
            center_x = float(np.mean(touch_x))
            center_y = float(np.mean(touch_y))
            center = (center_x, center_y)
            
            self.touch_points = [{'x': float(touch_x[i]), 'y': float(touch_y[i])} 
                                 for i in range(min(len(touch_x), 10))]
            
            self.last_center = center
            return {
                'is_touching': True,
                'center': center,
                'event': 'click',
                'touch_points': self.touch_points
            }
        
        self.touch_points = []
        return {'is_touching': False, 'center': None, 'event': None, 'touch_points': []}
    
    def detect(self, distdict: Dict[int, float]) -> Dict:
        """하위 호환성을 위한 래퍼 - 내부적으로 detect_fast 사용"""
        distances = np.array([distdict.get(i, 0) for i in range(360)], dtype=np.float32)
        return self.detect_fast(distances)


class MouseController:
    """마우스 제어 클래스 - 최적화 버전"""
    
    def __init__(self):
        self.enabled = False
        self.screen_width = 1920
        self.screen_height = 1080
        self.screen_offset_x = 0
        self.screen_offset_y = 0
        self.touch_area = {
            'x': -500,
            'y': 500,
            'width': 1000,
            'height': 800
        }

        # 터치 포인트 오프셋 (mm 단위, x: +오른쪽/-왼쪽, y: +위쪽/-아래쪽)
        self.touch_offset_x = 0
        self.touch_offset_y = 0

        # === 최적화: 클릭 쿨다운 설정 ===
        self.click_cooldown = 0.03  # 30ms 쿨다운
        self.last_click_time = 0
        self.min_move_distance = 2  # 최소 이동 거리 (픽셀)

    def set_screen_size(self, width: int, height: int):
        """화면 크기 설정"""
        self.screen_width = width
        self.screen_height = height

    def set_screen_offset(self, x: int, y: int):
        """모니터 오프셋 설정 (멀티 모니터용)"""
        self.screen_offset_x = x
        self.screen_offset_y = y

    def set_touch_area(self, touch_area: Dict):
        """터치 영역 설정"""
        self.touch_area = touch_area

    def set_touch_offset(self, x_mm: float, y_mm: float):
        """터치 포인트 오프셋 설정 (mm, x: +오른쪽/-왼쪽, y: +위쪽/-아래쪽)"""
        self.touch_offset_x = x_mm
        self.touch_offset_y = y_mm

    def set_click_cooldown(self, cooldown: float):
        """클릭 쿨다운 설정 (초)"""
        self.click_cooldown = cooldown

    def lidar_to_screen(self, lidar_x: float, lidar_y: float) -> Tuple[int, int]:
        """라이다 좌표를 화면 좌표로 변환"""
        lidar_x = lidar_x + self.touch_offset_x
        lidar_y = lidar_y + self.touch_offset_y

        ta = self.touch_area

        norm_x = (lidar_x - ta['x']) / ta['width']
        norm_y = (lidar_y - ta['y']) / ta['height']
        
        screen_x = int(norm_x * self.screen_width) + self.screen_offset_x
        screen_y = int(norm_y * self.screen_height) + self.screen_offset_y
        
        screen_x = max(self.screen_offset_x, min(screen_x, self.screen_offset_x + self.screen_width - 1))
        screen_y = max(self.screen_offset_y, min(screen_y, self.screen_height - 1))
        
        return screen_x, screen_y
    
    def move_to(self, lidar_x: float, lidar_y: float):
        """마우스 이동 - 최적화: duration 제거"""
        if not self.enabled or not PYAUTOGUI_AVAILABLE:
            return False
        
        try:
            screen_x, screen_y = self.lidar_to_screen(lidar_x, lidar_y)
            # === 최적화: duration=0, _pause=False ===
            pyautogui.moveTo(screen_x, screen_y, duration=0, _pause=False)
            return True
        except Exception:
            return False
    
    def click(self, lidar_x: float, lidar_y: float) -> bool:
        """마우스 클릭 - 최적화: 쿨다운 기반"""
        if not self.enabled or not PYAUTOGUI_AVAILABLE:
            return False
        
        try:
            # === 최적화: 시간 기반 쿨다운 체크 ===
            now = time.perf_counter()
            if now - self.last_click_time < self.click_cooldown:
                return False
            
            screen_x, screen_y = self.lidar_to_screen(lidar_x, lidar_y)
            # === 최적화: _pause=False로 추가 딜레이 방지 ===
            pyautogui.click(screen_x, screen_y, _pause=False)
            self.last_click_time = now
            return True
        except Exception:
            return False
    
    def click_fast(self, screen_x: int, screen_y: int) -> bool:
        """화면 좌표로 직접 클릭 - 가장 빠름"""
        if not self.enabled or not PYAUTOGUI_AVAILABLE:
            return False
        
        try:
            now = time.perf_counter()
            if now - self.last_click_time < self.click_cooldown:
                return False
            
            pyautogui.click(screen_x, screen_y, _pause=False)
            self.last_click_time = now
            return True
        except Exception:
            return False


class LidarController:
    """라이다 통합 컨트롤러 - 최적화 버전 (병렬 처리)"""
    
    def __init__(self):
        self.connection = LidarConnection()
        self.parser = LidarDataParser()
        self.touch_detector = TouchDetector()
        self.mouse_controller = MouseController()
        
        self.distdict: Dict[int, float] = {i: 0 for i in range(360)}
        
        self.is_scanning = False
        self.read_thread: Optional[threading.Thread] = None
        self.process_thread: Optional[threading.Thread] = None
        
        # === 최적화: 병렬 처리를 위한 큐 ===
        self.data_queue: queue.Queue = queue.Queue(maxsize=3)
        self.use_parallel = True  # 병렬 처리 사용 여부
        
        # 반전 및 회전 설정
        self.flip_x = False
        self.flip_y = False
        self.rotation = 0
        
        # 스캔 범위
        self.scan_start_angle = 0
        self.scan_end_angle = 180
        
        # 콜백 함수들
        self.on_data_update: Optional[Callable] = None
        self.on_touch_event: Optional[Callable] = None
        self.on_error: Optional[Callable] = None
        self.on_log: Optional[Callable] = None
        
        # === 최적화: 클릭 쿨다운 ===
        self.click_cooldown = 0.03  # 30ms
        self.last_click_time = 0
        
        # 비정상 종료 시 라이다 정리
        atexit.register(self._cleanup)
    
    def _cleanup(self):
        """프로그램 종료 시 정리"""
        try:
            if self.is_scanning:
                self.is_scanning = False
            self.connection.disconnect()
        except:
            pass
    
    def log(self, message: str):
        """로그 메시지 전송"""
        if self.on_log:
            self.on_log(message)
    
    def connect(self, port: str, baudrate: int) -> bool:
        """라이다 연결"""
        try:
            result = self.connection.connect(port, baudrate)
            if result:
                self.log(f"연결 성공: {port} @ {baudrate}bps")
            return result
        except ConnectionError as e:
            self.log(str(e))
            raise
    
    def disconnect(self):
        """라이다 연결 해제"""
        self.stop_scan()
        self.connection.disconnect()
        self.log("연결 해제됨")
    
    def start_scan(self):
        """스캔 시작 - 병렬 또는 단일 스레드"""
        if not self.connection.is_connected:
            raise RuntimeError("라이다가 연결되지 않았습니다.")
        
        self.connection.start_scan()
        self.is_scanning = True
        
        # 큐 비우기
        while not self.data_queue.empty():
            try:
                self.data_queue.get_nowait()
            except:
                break
        
        if self.use_parallel:
            # === 최적화: 읽기/처리 분리 ===
            self.read_thread = threading.Thread(target=self._read_loop, daemon=True)
            self.process_thread = threading.Thread(target=self._process_loop, daemon=True)
            self.read_thread.start()
            self.process_thread.start()
            self.log("스캔 시작 (병렬 모드)")
        else:
            # 단일 스레드 모드
            self.read_thread = threading.Thread(target=self._scan_loop_single, daemon=True)
            self.read_thread.start()
            self.log("스캔 시작 (단일 스레드 모드)")
    
    def stop_scan(self):
        """스캔 중지"""
        self.is_scanning = False
        self.connection.stop_scan()
        self.log("스캔 중지")
    
    def set_scan_frequency(self, target_freq: float) -> float:
        """
        스캔 주파수(RPM) 설정
        
        주의: 스캔 중지 상태에서만 호출해야 함!
        스캔 중이면 자동으로 중지 → 설정 → 재시작
        
        Args:
            target_freq: 4~12 Hz (4Hz=240RPM, 12Hz=720RPM)
        
        Returns:
            설정된 실제 주파수
        """
        was_scanning = self.is_scanning
        
        # 스캔 중이면 중지
        if was_scanning:
            self.log("주파수 변경을 위해 스캔 중지...")
            self.is_scanning = False
            self.connection.stop_scan()
            time.sleep(0.3)  # 스캔 완전 중지 대기
        
        # 주파수 설정
        self.log(f"목표 주파수: {target_freq:.1f}Hz...")
        actual_freq = self.connection.set_scan_frequency(target_freq)
        rpm = int(actual_freq * 60)
        self.log(f"스캔 주파수 설정됨: {actual_freq:.1f}Hz ({rpm}RPM)")
        
        # 스캔 중이었으면 재시작
        if was_scanning:
            time.sleep(0.1)
            self.log("스캔 재시작...")
            self.start_scan()
        
        return actual_freq
    
    def get_scan_frequency(self) -> float:
        """현재 스캔 주파수 반환"""
        return self.connection.current_freq
    
    def set_flip(self, flip_x: bool, flip_y: bool):
        """좌우/상하 반전 설정"""
        self.flip_x = flip_x
        self.flip_y = flip_y
        self.touch_detector.set_flip(flip_x, flip_y)
    
    def set_rotation(self, rotation: int):
        """회전 설정 (0, 90, 180, 270도)"""
        self.rotation = rotation % 360
        self.touch_detector.set_rotation(self.rotation)
    
    def set_scan_range(self, start_angle: int, end_angle: int):
        """스캔 범위 설정"""
        self.scan_start_angle = start_angle
        self.scan_end_angle = end_angle
        self.touch_detector.set_scan_range(start_angle, end_angle)
    
    def set_click_cooldown(self, cooldown: float):
        """클릭 쿨다운 설정 (초)"""
        self.click_cooldown = cooldown
        self.mouse_controller.set_click_cooldown(cooldown)
    
    def _read_loop(self):
        """데이터 읽기 전용 스레드 - 최대 속도"""
        buffer = b""
        
        while self.is_scanning:
            try:
                # 버퍼에 있는 데이터 읽기
                chunk = self.connection.read_data(8000)
                if chunk:
                    buffer += chunk
                    
                    # 충분한 데이터가 모이면 큐에 전달
                    if len(buffer) >= 2000:
                        try:
                            self.data_queue.put_nowait(buffer)
                        except queue.Full:
                            # 큐가 가득 차면 오래된 데이터 버리고 새 데이터 추가
                            try:
                                self.data_queue.get_nowait()
                            except:
                                pass
                            self.data_queue.put_nowait(buffer)
                        buffer = b""
                else:
                    # 데이터가 없으면 짧게 대기
                    time.sleep(0.001)
                    
            except Exception as e:
                if self.is_scanning:
                    self.log(f"읽기 오류: {e}")
                break
    
    def _process_loop(self):
        """데이터 처리 전용 스레드"""
        distances = np.zeros(360, dtype=np.float32)
        scan_mask = self.touch_detector.scan_mask
        
        while self.is_scanning:
            try:
                # 큐에서 데이터 가져오기
                try:
                    data = self.data_queue.get(timeout=0.05)
                except queue.Empty:
                    continue
                
                # 데이터 파싱
                data_parts = data.split(LidarConnection.DATA_DELIMITER)[1:-1]
                
                distances.fill(0)
                dist_counts = np.zeros(360, dtype=np.int32)
                dist_sums = np.zeros(360, dtype=np.float32)
                
                for e in data_parts:
                    try:
                        if len(e) > 0 and e[0] == 0:
                            if self.parser.check_sum(e):
                                d = self.parser.calculate(e)
                                for dist_i, angle_i in d:
                                    angle = int(angle_i) % 360
                                    if scan_mask[angle] and dist_i > 0:
                                        dist_sums[angle] += dist_i
                                        dist_counts[angle] += 1
                    except:
                        pass
                
                # 평균 계산
                valid = dist_counts > 0
                distances[valid] = dist_sums[valid] / dist_counts[valid]
                
                self.distdict = {i: float(distances[i]) for i in range(360) if distances[i] > 0}
                
                # 터치 감지
                touch_result = self.touch_detector.detect_fast(distances)
                
                # === 최적화: 터치 시 즉시 클릭 (시간 기반) ===
                if touch_result['is_touching'] and touch_result['center']:
                    center = touch_result['center']
                    now = time.perf_counter()
                    if now - self.last_click_time >= self.click_cooldown:
                        if self.mouse_controller.click(*center):
                            self.last_click_time = now
                
                # 콜백
                if self.on_data_update:
                    self.on_data_update(self.distdict, touch_result)
                
                if self.on_touch_event and touch_result['event']:
                    self.on_touch_event(touch_result)
                
            except Exception as e:
                if self.is_scanning:
                    self.log(f"처리 오류: {e}")
                    if self.on_error:
                        self.on_error(e)
    
    def _scan_loop_single(self):
        """단일 스레드 스캔 루프 (기존 호환성)"""
        distances = np.zeros(360, dtype=np.float32)
        scan_mask = self.touch_detector.scan_mask
        
        while self.is_scanning:
            try:
                # 데이터 읽기
                data1 = self.connection.read_data(4000)
                if not data1:
                    time.sleep(0.001)
                    continue
                    
                data2 = data1.split(LidarConnection.DATA_DELIMITER)[1:-1]
                
                distances.fill(0)
                dist_counts = np.zeros(360, dtype=np.int32)
                dist_sums = np.zeros(360, dtype=np.float32)
                
                for e in data2:
                    try:
                        if len(e) > 0 and e[0] == 0:
                            if self.parser.check_sum(e):
                                d = self.parser.calculate(e)
                                for dist_i, angle_i in d:
                                    angle = int(angle_i) % 360
                                    if scan_mask[angle] and dist_i > 0:
                                        dist_sums[angle] += dist_i
                                        dist_counts[angle] += 1
                    except:
                        pass
                
                valid = dist_counts > 0
                distances[valid] = dist_sums[valid] / dist_counts[valid]
                
                self.distdict = {i: float(distances[i]) for i in range(360) if distances[i] > 0}
                
                # 터치 감지
                touch_result = self.touch_detector.detect_fast(distances)
                
                # 터치 시 클릭
                if touch_result['is_touching'] and touch_result['center']:
                    center = touch_result['center']
                    now = time.perf_counter()
                    if now - self.last_click_time >= self.click_cooldown:
                        if self.mouse_controller.click(*center):
                            self.last_click_time = now
                
                if self.on_data_update:
                    self.on_data_update(self.distdict, touch_result)
                
                if self.on_touch_event and touch_result['event']:
                    self.on_touch_event(touch_result)
                
            except Exception as e:
                if self.is_scanning:
                    self.log(f"스캔 오류: {e}")
                    if self.on_error:
                        self.on_error(e)
                break
    
    def set_touch_area(self, x: int, y: int, width: int, height: int):
        """터치 영역 설정"""
        self.touch_detector.set_touch_area(x, y, width, height)
        self.mouse_controller.set_touch_area({
            'x': x, 'y': y, 'width': width, 'height': height
        })

    def set_touch_offset(self, x_mm: float, y_mm: float):
        """터치 포인트 오프셋 설정 (mm, x: +오른쪽/-왼쪽, y: +위쪽/-아래쪽)"""
        self.mouse_controller.set_touch_offset(x_mm, y_mm)
    
    def set_detection_params(self, threshold: int, min_points: int, click_time: float = 0):
        """감지 파라미터 설정"""
        self.touch_detector.set_detection_params(threshold, min_points, click_time)
    
    def set_mouse_enabled(self, enabled: bool):
        """마우스 제어 활성화/비활성화"""
        self.mouse_controller.enabled = enabled
    
    def set_screen_size(self, width: int, height: int):
        """화면 크기 설정"""
        self.mouse_controller.set_screen_size(width, height)
    
    def set_screen_offset(self, x: int, y: int):
        """모니터 오프셋 설정"""
        self.mouse_controller.set_screen_offset(x, y)
    
    def set_parallel_mode(self, enabled: bool):
        """병렬 처리 모드 설정"""
        self.use_parallel = enabled
    
    def get_cartesian_points(self) -> Tuple[List[float], List[float]]:
        """직교좌표로 변환된 포인트 반환 - 최적화 버전"""
        distances = np.array([self.distdict.get(i, 0) for i in range(360)], dtype=np.float32)
        
        valid_mask = self.touch_detector.scan_mask & (distances > 0)
        
        if not np.any(valid_mask):
            return [], []
        
        valid_indices = np.where(valid_mask)[0]
        valid_distances = distances[valid_mask]
        
        angles_rad = np.radians(valid_indices)
        x_coords = valid_distances * np.cos(angles_rad)
        y_coords = valid_distances * np.sin(angles_rad)
        
        if self.rotation == 90:
            x_coords, y_coords = y_coords.copy(), -x_coords
        elif self.rotation == 180:
            x_coords, y_coords = -x_coords, -y_coords
        elif self.rotation == 270:
            x_coords, y_coords = -y_coords, x_coords.copy()
        
        if self.flip_x:
            x_coords = -x_coords
        if self.flip_y:
            y_coords = -y_coords
        
        return x_coords.tolist(), y_coords.tolist()
    
    @property
    def is_connected(self) -> bool:
        return self.connection.is_connected
    
    @property
    def touch_points(self) -> List[Dict]:
        return self.touch_detector.touch_points
    
    @property
    def touch_area(self) -> Dict:
        return self.touch_detector.touch_area


# === 추가: 성능 측정 유틸리티 ===
class PerformanceMonitor:
    """성능 모니터링 클래스"""
    
    def __init__(self):
        self.frame_times: List[float] = []
        self.click_latencies: List[float] = []
        self.max_samples = 100
    
    def record_frame(self, duration: float):
        """프레임 처리 시간 기록"""
        self.frame_times.append(duration)
        if len(self.frame_times) > self.max_samples:
            self.frame_times.pop(0)
    
    def record_click_latency(self, latency: float):
        """클릭 지연 시간 기록"""
        self.click_latencies.append(latency)
        if len(self.click_latencies) > self.max_samples:
            self.click_latencies.pop(0)
    
    def get_fps(self) -> float:
        """현재 FPS 반환"""
        if not self.frame_times:
            return 0
        avg_time = sum(self.frame_times) / len(self.frame_times)
        return 1.0 / avg_time if avg_time > 0 else 0
    
    def get_avg_latency(self) -> float:
        """평균 지연 시간 반환 (ms)"""
        if not self.click_latencies:
            return 0
        return sum(self.click_latencies) / len(self.click_latencies) * 1000
    
    def get_stats(self) -> Dict:
        """성능 통계 반환"""
        return {
            'fps': self.get_fps(),
            'avg_latency_ms': self.get_avg_latency(),
            'frame_count': len(self.frame_times)
        }
