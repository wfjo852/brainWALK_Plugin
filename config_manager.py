"""
설정 관리 모듈
- JSON 기반 설정 저장/로드
"""
import json
import os
from typing import Any, Dict, Optional, List


class ConfigManager:
    """설정 관리 클래스"""
    
    DEFAULT_CONFIG = {
        "connection": {
            "port": "COM4",
            "baudrate": 230400,
            "scan_frequency": 7
        },
        "lidar_position": {
            "position": "top",
            "flip_x": True,
            "flip_y": False,
            "rotation": 0,
            "scan_start_angle": 180,
            "scan_end_angle": 360
        },
        "touch_area": {
            "x": -500,
            "y": -200,
            "width": 700,
            "height": -600
        },
        "touch_offset": {
            "x_mm": 0,
            "y_mm": 0
        },
        "touch_detection": {
            "threshold_mm": 1000,
            "min_points": 1,
            "click_cooldown_ms": 30
        },
        "mouse_control": {
            "enabled": True,
            "monitor_index": 0,
            "screen_width": 2560,
            "screen_height": 1440,
            "screen_offset_x": 0,
            "screen_offset_y": 0
        },
        "visualization": {
            "plot_xlim": [-1000, 1000],
            "plot_ylim": [-1000, 0],
            "point_size": 2,
            "touch_point_size": 50
        },
        "links": {
            "download_url": "https://ydlidar.com/download/category/tool-sdk-ros/",
            "manual_url": "https://ydlidar.com/static/upload/file/20260615/1781511390297792.pdf"
        },
        "monitors": []
    }
    
    def __init__(self, config_path: str = "lidar_config.json"):
        self.config_path = config_path
        self.config = self.load_config()
    
    def load_config(self) -> Dict:
        """설정 파일 로드"""
        if os.path.exists(self.config_path):
            try:
                with open(self.config_path, 'r', encoding='utf-8') as f:
                    loaded = json.load(f)
                    return self._merge_with_defaults(loaded)
            except Exception as e:
                print(f"설정 로드 오류: {e}")
        return self._deep_copy(self.DEFAULT_CONFIG)
    
    def _deep_copy(self, d: Dict) -> Dict:
        """딕셔너리 깊은 복사"""
        return json.loads(json.dumps(d))
    
    def _merge_with_defaults(self, loaded: Dict) -> Dict:
        """로드된 설정을 기본값과 병합"""
        result = self._deep_copy(self.DEFAULT_CONFIG)
        for key, value in loaded.items():
            if key in result:
                if isinstance(value, dict) and isinstance(result[key], dict):
                    result[key] = {**result[key], **value}
                else:
                    result[key] = value
            else:
                result[key] = value
        return result
    
    def save_config(self) -> bool:
        """설정 파일 저장"""
        try:
            with open(self.config_path, 'w', encoding='utf-8') as f:
                json.dump(self.config, f, indent=4, ensure_ascii=False)
            return True
        except Exception as e:
            print(f"설정 저장 오류: {e}")
            return False
    
    def get(self, *keys) -> Any:
        """설정 값 가져오기"""
        value = self.config
        for key in keys:
            if isinstance(value, dict) and key in value:
                value = value[key]
            else:
                return None
        return value
    
    def set(self, value: Any, *keys) -> bool:
        """설정 값 설정하기"""
        if not keys:
            return False
        
        target = self.config
        for key in keys[:-1]:
            if key not in target:
                target[key] = {}
            target = target[key]
        
        target[keys[-1]] = value
        return True
    
    def save_monitors(self, monitors: List[Dict]):
        """모니터 정보 저장"""
        self.config["monitors"] = monitors
        self.save_config()
    
    def get_saved_monitors(self) -> List[Dict]:
        """저장된 모니터 정보 가져오기"""
        return self.config.get("monitors", [])
    
    def export_config(self, filepath: str) -> bool:
        """설정 내보내기"""
        try:
            with open(filepath, 'w', encoding='utf-8') as f:
                json.dump(self.config, f, indent=4, ensure_ascii=False)
            return True
        except Exception:
            return False
    
    def import_config(self, filepath: str) -> bool:
        """설정 가져오기"""
        try:
            with open(filepath, 'r', encoding='utf-8') as f:
                self.config = self._merge_with_defaults(json.load(f))
            return True
        except Exception:
            return False
    
    def reset_to_default(self):
        """기본값으로 리셋"""
        self.config = self._deep_copy(self.DEFAULT_CONFIG)
        self.save_config()
