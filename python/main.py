"""
라이다 G4 터치스크린 시스템 - 진입점

사용법:
  python main.py          # GUI 모드
  python main.py --nogui  # GUI 없이 백그라운드 실행
  python main.py --headless  # GUI 없이 백그라운드 실행 (동일)
"""
import sys
import argparse


def run_gui():
    """GUI 모드 실행"""
    import tkinter as tk
    from lidar_ui import LidarTouchScreenUI
    
    try:
        root = tk.Tk()
        app = LidarTouchScreenUI(root)
        root.mainloop()
    except KeyboardInterrupt:
        pass
    except Exception as e:
        print(f"오류: {e}")
    finally:
        import os
        os._exit(0)


def run_headless():
    """GUI 없이 백그라운드 실행"""
    import time
    import signal
    from config_manager import ConfigManager
    from lidar_core import LidarController, get_monitors
    
    print("=" * 50)
    print("라이다 G4 터치스크린 - 백그라운드 모드")
    print("=" * 50)
    print("종료: Ctrl+C")
    print()
    
    # 설정 로드
    config = ConfigManager()
    controller = LidarController()
    
    # 종료 플래그
    running = True
    
    def signal_handler(signum, frame):
        nonlocal running
        print("\n종료 중...")
        running = False
    
    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)
    
    # 설정 적용
    controller.set_touch_area(
        config.get("touch_area", "x"),
        config.get("touch_area", "y"),
        config.get("touch_area", "width"),
        config.get("touch_area", "height")
    )
    controller.set_detection_params(
        config.get("touch_detection", "threshold_mm"),
        config.get("touch_detection", "min_points")
    )
    controller.set_click_cooldown(
        (config.get("touch_detection", "click_cooldown_ms") or 30) / 1000.0
    )
    
    # 저장된 모니터 정보 사용
    saved_monitors = config.get_saved_monitors()
    monitor_idx = config.get("mouse_control", "monitor_index")
    
    if saved_monitors and monitor_idx < len(saved_monitors):
        monitor = saved_monitors[monitor_idx]
        controller.set_screen_size(monitor['width'], monitor['height'])
        controller.set_screen_offset(monitor['x'], monitor['y'])
        print(f"모니터: {monitor['name']}")
    else:
        # 저장된 정보 없으면 새로 검색 후 저장
        monitors = get_monitors()
        if monitors:
            config.save_monitors(monitors)
            monitor = monitors[min(monitor_idx, len(monitors)-1)]
            controller.set_screen_size(monitor['width'], monitor['height'])
            controller.set_screen_offset(monitor['x'], monitor['y'])
            print(f"모니터: {monitor['name']} (새로 검색됨)")
        else:
            controller.set_screen_size(
                config.get("mouse_control", "screen_width"),
                config.get("mouse_control", "screen_height")
            )
            controller.set_screen_offset(
                config.get("mouse_control", "screen_offset_x"),
                config.get("mouse_control", "screen_offset_y")
            )
    
    controller.set_flip(
        config.get("lidar_position", "flip_x"),
        config.get("lidar_position", "flip_y")
    )
    controller.set_rotation(config.get("lidar_position", "rotation") or 0)
    controller.set_scan_range(
        config.get("lidar_position", "scan_start_angle") or 0,
        config.get("lidar_position", "scan_end_angle") or 180
    )
    controller.set_mouse_enabled(config.get("mouse_control", "enabled"))
    
    # 로그 콜백
    def on_log(msg):
        print(f"[LOG] {msg}")
    
    def on_touch(result):
        if result['event'] == 'click' and result['center']:
            print(f"[CLICK] ({result['center'][0]:.0f}, {result['center'][1]:.0f})")
    
    controller.on_log = on_log
    controller.on_touch_event = on_touch
    
    # 연결
    port = config.get("connection", "port")
    baudrate = config.get("connection", "baudrate")
    scan_freq = config.get("connection", "scan_frequency") or 7.0
    
    print(f"연결 시도: {port} @ {baudrate}bps")
    
    try:
        controller.connect(port, baudrate)
        
        # 주파수 설정 (스캔 전)
        actual_freq = controller.set_scan_frequency(scan_freq)
        print(f"스캔 주파수: {actual_freq:.1f}Hz ({int(actual_freq*60)}RPM)")
        
        controller.start_scan()
        print("마우스 제어 활성화:", config.get("mouse_control", "enabled"))
        print()
        
        # 메인 루프
        while running:
            time.sleep(0.1)
        
    except Exception as e:
        print(f"오류: {e}")
    finally:
        print("라이다 종료 중...")
        try:
            controller.disconnect()
        except:
            pass
        print("종료됨")
        import os
        os._exit(0)


def main():
    parser = argparse.ArgumentParser(description='라이다 G4 터치스크린 시스템')
    parser.add_argument('--nogui', '--headless', action='store_true',
                        help='GUI 없이 백그라운드 모드로 실행')
    parser.add_argument('--mouse', action='store_true',
                        help='마우스 제어 강제 활성화 (백그라운드 모드)')
    
    args = parser.parse_args()
    
    if args.nogui:
        if args.mouse:
            # 마우스 제어 강제 활성화
            from config_manager import ConfigManager
            config = ConfigManager()
            config.set(True, "mouse_control", "enabled")
        run_headless()
    else:
        run_gui()


if __name__ == "__main__":
    main()
