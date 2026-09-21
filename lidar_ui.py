"""
라이다 터치스크린 + ANT+ 통합 UI 모듈
- tkinter 기반 GUI
- matplotlib 시각화
- ANT+ 심박계 UDP 전송
"""
import platform
import time
import signal
import sys
import os
import webbrowser
import tkinter as tk
from tkinter import ttk, messagebox, filedialog
import matplotlib.pyplot as plt
from matplotlib.backends.backend_tkagg import FigureCanvasTkAgg
from matplotlib.patches import Rectangle, Circle
from matplotlib import font_manager, rc

from config_manager import ConfigManager
from lidar_core import LidarController, get_available_ports, get_monitors
from ant_controller import AntController


def _resource_path(relative_path: str) -> str:
    """리소스 경로 반환 - PyInstaller 번들 환경과 소스 실행 환경 모두 지원"""
    base_path = getattr(sys, '_MEIPASS', os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(base_path, relative_path)


class LidarTouchScreenUI:
    """라이다 터치스크린 UI 클래스"""
    
    def __init__(self, root: tk.Tk, config_path: str = "lidar_config.json"):
        self.root = root
        self.root.title("라이다 G4 터치스크린 시스템")

        # 화면 해상도에 맞춰 가변 크기 적용 (1920x1080 이하 환경 대응)
        screen_w = self.root.winfo_screenwidth()
        screen_h = self.root.winfo_screenheight()
        win_w = min(1400, screen_w - 40)
        win_h = min(900, screen_h - 80)
        self.root.geometry(f"{win_w}x{win_h}")
        self.root.minsize(1000, 600)

        # 창/작업표시줄 아이콘 설정
        try:
            icon_path = _resource_path(os.path.join('icon', 'setting_icon.png'))
            self._icon_img = tk.PhotoImage(file=icon_path)
            self.root.iconphoto(True, self._icon_img)
        except Exception:
            pass

        self.config_manager = ConfigManager(config_path)
        self.controller = LidarController()
        self.ant = AntController()
        self._setup_controller_callbacks()
        self._setup_ant_callbacks()
        self._setup_fonts()
        self._load_settings_from_config()
        
        # 저장된 모니터 정보 로드, 없으면 새로 검색
        saved_monitors = self.config_manager.get_saved_monitors()
        if saved_monitors:
            self.monitors = saved_monitors
        else:
            self.monitors = get_monitors()
            if self.monitors:
                self.config_manager.save_monitors(self.monitors)
        
        self.enable_mouse_control = tk.BooleanVar(value=self.config_manager.get("mouse_control", "enabled"))
        self.lidar_position_var = tk.StringVar(value=self.config_manager.get("lidar_position", "position"))
        self.flip_x_var = tk.BooleanVar(value=self.config_manager.get("lidar_position", "flip_x"))
        self.flip_y_var = tk.BooleanVar(value=self.config_manager.get("lidar_position", "flip_y"))
        self.rotation_var = tk.StringVar(value=str(self.config_manager.get("lidar_position", "rotation") or 0))
        self.monitor_var = tk.StringVar()
        
        self._setup_ui()
        
        self.root.protocol("WM_DELETE_WINDOW", self._on_closing)
        signal.signal(signal.SIGINT, self._signal_handler)
        signal.signal(signal.SIGTERM, self._signal_handler)
    
    def _signal_handler(self, signum, frame):
        try:
            if self.controller.is_scanning:
                self.controller.is_scanning = False
            if self.controller.is_connected:
                self.controller.disconnect()
        except:
            pass
        import os
        os._exit(0)
    
    def _setup_fonts(self):
        system_name = platform.system()
        if system_name == 'Windows':
            try:
                path = "c:/Windows/Fonts/malgun.ttf"
                font_name = font_manager.FontProperties(fname=path).get_name()
                rc('font', family=font_name)
            except:
                pass
        elif system_name == 'Darwin':
            rc('font', family='AppleGothic')
        plt.rcParams['axes.unicode_minus'] = False
    
    def _setup_controller_callbacks(self):
        self.controller.on_data_update = self._on_data_update
        self.controller.on_touch_event = self._on_touch_event
        self.controller.on_error = self._on_error
        self.controller.on_log = self._log_status

    def _setup_ant_callbacks(self):
        self.ant.on_bpm = self._on_ant_bpm
        self.ant.on_status = self._on_ant_status
        self.ant.on_error = self._on_ant_error
    
    def _load_settings_from_config(self):
        cfg = self.config_manager
        self.controller.set_touch_area(
            cfg.get("touch_area", "x"), cfg.get("touch_area", "y"),
            cfg.get("touch_area", "width"), cfg.get("touch_area", "height"))
        self.controller.set_touch_offset(
            cfg.get("touch_offset", "x_mm") or 0,
            cfg.get("touch_offset", "y_mm") or 0)
        self.controller.set_detection_params(
            cfg.get("touch_detection", "threshold_mm"),
            cfg.get("touch_detection", "min_points"))
        self.controller.set_click_cooldown(
            (cfg.get("touch_detection", "click_cooldown_ms") or 30) / 1000.0)
        self.controller.set_screen_size(
            cfg.get("mouse_control", "screen_width"),
            cfg.get("mouse_control", "screen_height"))
        self.controller.set_screen_offset(
            cfg.get("mouse_control", "screen_offset_x"),
            cfg.get("mouse_control", "screen_offset_y"))
        self.controller.set_mouse_enabled(cfg.get("mouse_control", "enabled"))
        self.controller.set_flip(
            cfg.get("lidar_position", "flip_x"),
            cfg.get("lidar_position", "flip_y"))
        self.controller.set_rotation(cfg.get("lidar_position", "rotation") or 0)
        self.controller.set_scan_range(
            cfg.get("lidar_position", "scan_start_angle") or 0,
            cfg.get("lidar_position", "scan_end_angle") or 180
        )
        self.plot_xlim = cfg.get("visualization", "plot_xlim")
        self.plot_ylim = cfg.get("visualization", "plot_ylim")
        self.point_size = cfg.get("visualization", "point_size")
        self.touch_point_size = cfg.get("visualization", "touch_point_size")
    
    def _setup_ui(self):
        main_container = tk.Frame(self.root)
        main_container.pack(fill=tk.BOTH, expand=True)
        self._setup_visualization_panel(main_container)
        self._setup_settings_panel(main_container)
    
    def _setup_visualization_panel(self, parent):
        left_frame = tk.Frame(parent, bg='white')
        left_frame.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=10, pady=10)
        self._setup_control_bar(left_frame)
        self.fig, self.ax = plt.subplots(figsize=(10, 8))
        self.canvas = FigureCanvasTkAgg(self.fig, left_frame)
        self.canvas.get_tk_widget().pack(fill=tk.BOTH, expand=True)
        
        # 마우스 좌표 표시 라벨
        self.coord_label = tk.Label(left_frame, text="좌표: (-, -)", 
                                    font=('Arial', 10), bg='white', anchor='w')
        self.coord_label.pack(fill=tk.X, pady=(5, 0))
        
        # 마우스 이동 이벤트 연결
        self.canvas.mpl_connect('motion_notify_event', self._on_mouse_move)
        
        self._draw_initial_plot()
    
    def _setup_control_bar(self, parent):
        control_frame = tk.Frame(parent, bg='white')
        control_frame.pack(fill=tk.X, pady=(0, 10))
        tk.Label(control_frame, text="라이다 G4 터치스크린",
                font=('Arial', 16, 'bold'), bg='white').pack(side=tk.LEFT)
        self.status_label = tk.Label(control_frame, text="● 연결 안 됨",
                                     font=('Arial', 12), fg='red', bg='white')
        self.status_label.pack(side=tk.LEFT, padx=20)
        self.connect_btn = tk.Button(control_frame, text="연결 및 시작",
                                     command=self._toggle_connection,
                                     bg='#22c55e', fg='white',
                                     font=('Arial', 10, 'bold'), padx=20, pady=5)
        self.connect_btn.pack(side=tk.RIGHT, padx=5)
    
    def _draw_initial_plot(self):
        self.ax.set_xlim(self.plot_xlim)
        self.ax.set_ylim(self.plot_ylim)
        self.ax.set_xlabel('X (mm)')
        self.ax.set_ylabel('Y (mm)')
        self.ax.set_title('라이다 G4 터치스크린')
        self.ax.grid(True, alpha=0.3)
        self.ax.set_aspect('equal')
        self.canvas.draw()
    
    def _setup_settings_panel(self, parent):
        right_frame = tk.Frame(parent, bg='#f3f4f6', width=380)
        right_frame.pack(side=tk.RIGHT, fill=tk.BOTH, padx=10, pady=10)
        right_frame.pack_propagate(False)

        # ── 상단 탭 메뉴 ──────────────────────────────────────
        tab_bar = tk.Frame(right_frame, bg='#1e293b')
        tab_bar.pack(fill=tk.X)

        self._tab_btns = {}
        for key, label in [("lidar", "라이다 설정"), ("ant", "ANT+ 설정")]:
            btn = tk.Button(tab_bar, text=label,
                            font=('Arial', 10, 'bold'),
                            bd=0, padx=16, pady=8, cursor='hand2',
                            command=lambda k=key: self._switch_tab(k))
            btn.pack(side=tk.LEFT)
            self._tab_btns[key] = btn

        # ── 공유 로그 (항상 하단 고정) ─────────────────────────
        log_outer = tk.Frame(right_frame, bg='#f3f4f6')
        log_outer.pack(side=tk.BOTTOM, fill=tk.X, padx=10, pady=(0, 5))
        self._create_status_section(log_outer)

        # ── 탭 콘텐츠 영역 ────────────────────────────────────
        content_area = tk.Frame(right_frame, bg='#f3f4f6')
        content_area.pack(fill=tk.BOTH, expand=True)

        self._tab_frames = {}
        for key in ("lidar", "ant"):
            canvas = tk.Canvas(content_area, bg='#f3f4f6', highlightthickness=0)
            sb = ttk.Scrollbar(content_area, orient="vertical", command=canvas.yview)
            inner = tk.Frame(canvas, bg='#f3f4f6')
            inner.bind("<Configure>",
                       lambda e, c=canvas: c.configure(scrollregion=c.bbox("all")))
            canvas.create_window((0, 0), window=inner, anchor="nw", width=356)
            canvas.configure(yscrollcommand=sb.set)
            self._tab_frames[key] = (canvas, sb, inner)

        # 현재 활성 탭의 캔버스로만 휠 스크롤을 전달 (탭 전환 시 _switch_tab에서 갱신)
        self._active_canvas = None

        def _on_mousewheel(event):
            if self._active_canvas is not None:
                self._active_canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")

        self.root.bind_all("<MouseWheel>", _on_mousewheel)

        # 라이다 탭 섹션
        lidar_inner = self._tab_frames["lidar"][2]
        self._create_config_management_section(lidar_inner)
        self._create_connection_section(lidar_inner)
        self._create_mouse_control_section(lidar_inner)
        self._create_detection_section(lidar_inner)
        self._create_lidar_position_section(lidar_inner)
        self._create_view_area_section(lidar_inner)
        self._create_touch_area_section(lidar_inner)

        # ANT+ 탭 섹션
        ant_inner = self._tab_frames["ant"][2]
        self._create_ant_section(ant_inner)

        # 기본 탭 활성화
        self._switch_tab("lidar")

    def _switch_tab(self, key: str):
        # 기존 탭 언팩
        for k, (canvas, sb, _) in self._tab_frames.items():
            canvas.pack_forget()
            sb.pack_forget()

        # 선택 탭 표시
        canvas, sb, _ = self._tab_frames[key]
        sb.pack(side=tk.RIGHT, fill=tk.Y)
        canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self._active_canvas = canvas

        # 버튼 색상 업데이트
        colors = {
            "lidar": {"active": "#2563eb", "inactive": "#334155"},
            "ant":   {"active": "#be185d", "inactive": "#334155"},
        }
        for k, btn in self._tab_btns.items():
            is_active = (k == key)
            active_color = colors[k]["active"] if is_active else colors[k]["inactive"]
            btn.config(bg=active_color, fg='white')
    
    def _create_config_management_section(self, parent):
        frame = tk.LabelFrame(parent, text="설정 파일 관리", bg='#e0f2fe',
                             font=('Arial', 10, 'bold'), padx=10, pady=10)
        frame.pack(fill=tk.X, padx=10, pady=5)
        btn_frame = tk.Frame(frame, bg='#e0f2fe')
        btn_frame.pack(fill=tk.X)
        tk.Button(btn_frame, text="저장", command=self._save_config,
                 bg='#0ea5e9', fg='white', font=('Arial', 9, 'bold'), width=8).pack(side=tk.LEFT, padx=2, pady=5)
        tk.Button(btn_frame, text="불러오기", command=self._load_config,
                 bg='#0ea5e9', fg='white', font=('Arial', 9, 'bold'), width=8).pack(side=tk.LEFT, padx=2, pady=5)
        tk.Button(btn_frame, text="기본값", command=self._reset_to_default,
                 bg='#ef4444', fg='white', font=('Arial', 9, 'bold'), width=8).pack(side=tk.LEFT, padx=2, pady=5)
        
        # 링크 버튼 프레임
        link_frame = tk.Frame(frame, bg='#e0f2fe')
        link_frame.pack(fill=tk.X, pady=(5, 0))
        tk.Button(link_frame, text="🔗 다운로드 페이지", command=self._open_download_page,
                 bg='#8b5cf6', fg='white', font=('Arial', 9, 'bold')).pack(side=tk.LEFT, padx=2)
        tk.Button(link_frame, text="📖 사용 설명서", command=self._open_manual_page,
                 bg='#10b981', fg='white', font=('Arial', 9, 'bold')).pack(side=tk.LEFT, padx=2)
    
    def _create_connection_section(self, parent):
        frame = tk.LabelFrame(parent, text="연결 설정", bg='white',
                             font=('Arial', 10, 'bold'), padx=10, pady=10)
        frame.pack(fill=tk.X, padx=10, pady=5)
        cfg = self.config_manager
        
        tk.Label(frame, text="COM 포트", bg='white').grid(row=0, column=0, sticky='w', pady=5)
        port_frame = tk.Frame(frame, bg='white')
        port_frame.grid(row=0, column=1, columnspan=2, pady=5, sticky='ew')
        
        self.port_var = tk.StringVar(value=cfg.get("connection", "port"))
        self.port_combo = ttk.Combobox(port_frame, textvariable=self.port_var, width=12)
        self.port_combo.pack(side=tk.LEFT)
        tk.Button(port_frame, text="검색", command=self._refresh_ports,
                 bg='#3b82f6', fg='white', font=('Arial', 9, 'bold')).pack(side=tk.LEFT, padx=5)
        
        self.port_desc_label = tk.Label(frame, text="", bg='white', fg='gray', font=('Arial', 8))
        self.port_desc_label.grid(row=1, column=0, columnspan=3, sticky='w', pady=2)
        self.port_combo.bind('<<ComboboxSelected>>', self._on_port_selected)
        self.port_info = {}
        self._refresh_ports()
        
        tk.Label(frame, text="Baudrate", bg='white').grid(row=2, column=0, sticky='w', pady=5)
        self.baudrate_entry = tk.Entry(frame, width=15)
        self.baudrate_entry.insert(0, str(cfg.get("connection", "baudrate")))
        self.baudrate_entry.grid(row=2, column=1, pady=5, sticky='w')
        
        # RPM 조절 버튼
        tk.Label(frame, text="스캔 속도", bg='white').grid(row=3, column=0, sticky='w', pady=5)
        rpm_frame = tk.Frame(frame, bg='white')
        rpm_frame.grid(row=3, column=1, columnspan=2, pady=5, sticky='ew')
        
        self.current_freq = cfg.get("connection", "scan_frequency") or 7.0
        
        tk.Button(rpm_frame, text="-1", command=lambda: self._change_rpm(-1),
                 bg='#ef4444', fg='white', font=('Arial', 9, 'bold'), width=3).pack(side=tk.LEFT, padx=2)
        tk.Button(rpm_frame, text="-.1", command=lambda: self._change_rpm(-0.1),
                 bg='#f97316', fg='white', font=('Arial', 8), width=3).pack(side=tk.LEFT, padx=2)
        
        self.rpm_label = tk.Label(rpm_frame, text=f"{self.current_freq:.1f}Hz", 
                                  bg='#e5e7eb', width=8, font=('Arial', 10, 'bold'))
        self.rpm_label.pack(side=tk.LEFT, padx=5)
        
        tk.Button(rpm_frame, text="+.1", command=lambda: self._change_rpm(0.1),
                 bg='#22c55e', fg='white', font=('Arial', 8), width=3).pack(side=tk.LEFT, padx=2)
        tk.Button(rpm_frame, text="+1", command=lambda: self._change_rpm(1),
                 bg='#16a34a', fg='white', font=('Arial', 9, 'bold'), width=3).pack(side=tk.LEFT, padx=2)
    
    def _refresh_ports(self):
        ports = get_available_ports()
        self.port_info = {port: desc for port, desc in ports}
        port_names = [port for port, desc in ports]
        self.port_combo['values'] = port_names
        if port_names:
            if self.port_var.get() not in port_names:
                self.port_var.set(port_names[0])
            self._update_port_description()
            if hasattr(self, 'status_text'):
                self._log_status(f"포트 {len(port_names)}개 발견")
        else:
            self.port_desc_label.config(text="⚠ 사용 가능한 포트 없음")
    
    def _on_port_selected(self, event=None):
        self._update_port_description()
    
    def _update_port_description(self):
        port = self.port_var.get()
        if port in self.port_info:
            self.port_desc_label.config(text=f"📌 {self.port_info[port]}")
    
    def _change_rpm(self, delta: float):
        """RPM 변경 (+/-1 또는 +/-0.1)"""
        # 목표 주파수 계산
        new_freq = self.current_freq + delta
        new_freq = max(4.0, min(12.0, new_freq))
        
        if not self.controller.is_connected:
            # 연결 안 됨 - UI만 업데이트
            self.current_freq = new_freq
            self.rpm_label.config(text=f"{self.current_freq:.1f}Hz")
            self._log_status(f"주파수 설정: {self.current_freq:.1f}Hz (연결 후 적용)")
        else:
            # 연결됨 - 라이다에 명령 전송
            self._log_status(f"주파수 변경 중... ({delta:+.1f}Hz)")
            
            # 별도 스레드에서 실행 (UI 블로킹 방지)
            def apply_freq():
                actual = self.controller.set_scan_frequency(new_freq)
                self.current_freq = actual
                self.root.after(0, lambda: self.rpm_label.config(text=f"{actual:.1f}Hz"))
            
            import threading
            threading.Thread(target=apply_freq, daemon=True).start()
    
    def _create_lidar_position_section(self, parent):
        frame = tk.LabelFrame(parent, text="라이다 위치 및 반전", bg='#fef3c7',
                             font=('Arial', 10, 'bold'), padx=10, pady=10)
        frame.pack(fill=tk.X, padx=10, pady=5)
        
        pos_frame = tk.Frame(frame, bg='#fef3c7')
        pos_frame.pack(fill=tk.X, pady=5)
        tk.Label(pos_frame, text="라이다 위치:", bg='#fef3c7').pack(side=tk.LEFT)
        tk.Radiobutton(pos_frame, text="상단", variable=self.lidar_position_var,
                      value="top", bg='#fef3c7', command=self._on_lidar_position_changed).pack(side=tk.LEFT, padx=10)
        tk.Radiobutton(pos_frame, text="하단", variable=self.lidar_position_var,
                      value="bottom", bg='#fef3c7', command=self._on_lidar_position_changed).pack(side=tk.LEFT, padx=10)
        
        # 회전 설정
        rotation_frame = tk.Frame(frame, bg='#fef3c7')
        rotation_frame.pack(fill=tk.X, pady=5)
        tk.Label(rotation_frame, text="라이다 회전:", bg='#fef3c7').pack(side=tk.LEFT)
        rotation_combo = ttk.Combobox(rotation_frame, textvariable=self.rotation_var,
                                      values=['0', '90', '180', '270'], width=5, state='readonly')
        rotation_combo.pack(side=tk.LEFT, padx=10)
        tk.Label(rotation_frame, text="도", bg='#fef3c7').pack(side=tk.LEFT)
        rotation_combo.bind('<<ComboboxSelected>>', self._on_rotation_changed)
        
        # 스캔 범위 설정
        scan_frame = tk.Frame(frame, bg='#fef3c7')
        scan_frame.pack(fill=tk.X, pady=5)
        tk.Label(scan_frame, text="스캔 범위:", bg='#fef3c7').pack(side=tk.LEFT)
        self.scan_start_entry = tk.Entry(scan_frame, width=5)
        self.scan_start_entry.insert(0, str(self.config_manager.get("lidar_position", "scan_start_angle") or 0))
        self.scan_start_entry.pack(side=tk.LEFT, padx=2)
        tk.Label(scan_frame, text="~", bg='#fef3c7').pack(side=tk.LEFT)
        self.scan_end_entry = tk.Entry(scan_frame, width=5)
        self.scan_end_entry.insert(0, str(self.config_manager.get("lidar_position", "scan_end_angle") or 180))
        self.scan_end_entry.pack(side=tk.LEFT, padx=2)
        tk.Label(scan_frame, text="도", bg='#fef3c7').pack(side=tk.LEFT)
        tk.Button(scan_frame, text="적용", command=self._on_scan_range_changed,
                 bg='#f59e0b', fg='white', font=('Arial', 8, 'bold')).pack(side=tk.LEFT, padx=5)
        
        flip_frame = tk.Frame(frame, bg='#fef3c7')
        flip_frame.pack(fill=tk.X, pady=5)
        tk.Checkbutton(flip_frame, text="좌우 반전 (X)", variable=self.flip_x_var,
                      bg='#fef3c7', command=self._on_flip_changed).pack(side=tk.LEFT, padx=10)
        tk.Checkbutton(flip_frame, text="상하 반전 (Y)", variable=self.flip_y_var,
                      bg='#fef3c7', command=self._on_flip_changed).pack(side=tk.LEFT, padx=10)
    
    def _on_lidar_position_changed(self):
        position = self.lidar_position_var.get()
        rotation = int(self.rotation_var.get())
        
        if position == "top":
            self.plot_ylim = [-3000, 0]
            # 상단: 회전값+180 ~ 회전값 (또는 회전값+360)
            start = (rotation + 180) % 360
            end = (rotation + 360) % 360
            if end == 0:
                end = 360
        else:
            self.plot_ylim = [0, 3000]
            # 하단: 회전값 ~ 회전값+180
            start = rotation % 360
            end = (rotation + 180) % 360
        
        # Y축 범위 업데이트
        self.view_y_min.delete(0, tk.END)
        self.view_y_min.insert(0, str(self.plot_ylim[0]))
        self.view_y_max.delete(0, tk.END)
        self.view_y_max.insert(0, str(self.plot_ylim[1]))
        
        # 스캔 범위 업데이트
        self.scan_start_entry.delete(0, tk.END)
        self.scan_start_entry.insert(0, str(start))
        self.scan_end_entry.delete(0, tk.END)
        self.scan_end_entry.insert(0, str(end))
        
        # 컨트롤러에 적용
        self.controller.set_scan_range(start, end)
        
        self._log_status(f"라이다 위치: {position}, 스캔: {start}°~{end}°")
    
    def _on_flip_changed(self):
        self.controller.set_flip(self.flip_x_var.get(), self.flip_y_var.get())
        self._log_status(f"반전: X={self.flip_x_var.get()}, Y={self.flip_y_var.get()}")
    
    def _on_rotation_changed(self, event=None):
        rotation = int(self.rotation_var.get())
        self.controller.set_rotation(rotation)
        
        # 스캔 범위도 자동 업데이트
        position = self.lidar_position_var.get()
        if position == "top":
            start = (rotation + 180) % 360
            end = (rotation + 360) % 360
            if end == 0:
                end = 360
        else:
            start = rotation % 360
            end = (rotation + 180) % 360
        
        self.scan_start_entry.delete(0, tk.END)
        self.scan_start_entry.insert(0, str(start))
        self.scan_end_entry.delete(0, tk.END)
        self.scan_end_entry.insert(0, str(end))
        
        self.controller.set_scan_range(start, end)
        self._log_status(f"회전: {rotation}°, 스캔: {start}°~{end}°")
    
    def _on_scan_range_changed(self):
        try:
            start = int(self.scan_start_entry.get())
            end = int(self.scan_end_entry.get())
            self.controller.set_scan_range(start, end)
            self._log_status(f"스캔 범위: {start}° ~ {end}°")
        except ValueError:
            pass
    
    def _create_view_area_section(self, parent):
        frame = tk.LabelFrame(parent, text="화면 표시 영역 (mm)", bg='#e0e7ff',
                             font=('Arial', 10, 'bold'), padx=10, pady=10)
        frame.pack(fill=tk.X, padx=10, pady=5)
        
        tk.Label(frame, text="X 범위", bg='#e0e7ff').grid(row=0, column=0, sticky='w', pady=3)
        x_frame = tk.Frame(frame, bg='#e0e7ff')
        x_frame.grid(row=0, column=1, pady=3, sticky='w')
        self.view_x_min = tk.Entry(x_frame, width=6)
        self.view_x_min.insert(0, str(self.plot_xlim[0]))
        self.view_x_min.pack(side=tk.LEFT)
        tk.Label(x_frame, text=" ~ ", bg='#e0e7ff').pack(side=tk.LEFT)
        self.view_x_max = tk.Entry(x_frame, width=6)
        self.view_x_max.insert(0, str(self.plot_xlim[1]))
        self.view_x_max.pack(side=tk.LEFT)
        
        tk.Label(frame, text="Y 범위", bg='#e0e7ff').grid(row=1, column=0, sticky='w', pady=3)
        y_frame = tk.Frame(frame, bg='#e0e7ff')
        y_frame.grid(row=1, column=1, pady=3, sticky='w')
        self.view_y_min = tk.Entry(y_frame, width=6)
        self.view_y_min.insert(0, str(self.plot_ylim[0]))
        self.view_y_min.pack(side=tk.LEFT)
        tk.Label(y_frame, text=" ~ ", bg='#e0e7ff').pack(side=tk.LEFT)
        self.view_y_max = tk.Entry(y_frame, width=6)
        self.view_y_max.insert(0, str(self.plot_ylim[1]))
        self.view_y_max.pack(side=tk.LEFT)
        
        tk.Button(frame, text="적용", command=self._apply_view_area,
                 bg='#6366f1', fg='white', font=('Arial', 9, 'bold')).grid(row=2, column=0, columnspan=2, pady=10)
    
    def _apply_view_area(self):
        try:
            x_min, x_max = int(self.view_x_min.get()), int(self.view_x_max.get())
            y_min, y_max = int(self.view_y_min.get()), int(self.view_y_max.get())
            if x_min >= x_max or y_min >= y_max:
                messagebox.showerror("오류", "최소값은 최대값보다 작아야 합니다.")
                return
            self.plot_xlim = [x_min, x_max]
            self.plot_ylim = [y_min, y_max]
            self._log_status(f"표시 영역: X({x_min}~{x_max}), Y({y_min}~{y_max})")
        except ValueError:
            messagebox.showerror("오류", "올바른 숫자를 입력하세요.")
    
    def _create_touch_area_section(self, parent):
        frame = tk.LabelFrame(parent, text="터치 영역 (mm) - 좌상단 기준", bg='#fff7ed',
                             font=('Arial', 10, 'bold'), padx=10, pady=10)
        frame.pack(fill=tk.X, padx=10, pady=5)
        touch_area = self.controller.touch_area
        
        tk.Label(frame, text="X", bg='#fff7ed').grid(row=0, column=0, sticky='w', pady=3)
        self.area_x = tk.Entry(frame, width=8)
        self.area_x.insert(0, str(touch_area['x']))
        self.area_x.grid(row=0, column=1, pady=3, padx=2)
        
        tk.Label(frame, text="Y", bg='#fff7ed').grid(row=0, column=2, sticky='w', pady=3, padx=(10,0))
        self.area_y = tk.Entry(frame, width=8)
        self.area_y.insert(0, str(touch_area['y']))
        self.area_y.grid(row=0, column=3, pady=3, padx=2)
        
        tk.Label(frame, text="너비", bg='#fff7ed').grid(row=1, column=0, sticky='w', pady=3)
        self.area_width = tk.Entry(frame, width=8)
        self.area_width.insert(0, str(touch_area['width']))
        self.area_width.grid(row=1, column=1, pady=3, padx=2)
        
        tk.Label(frame, text="높이", bg='#fff7ed').grid(row=1, column=2, sticky='w', pady=3, padx=(10,0))
        self.area_height = tk.Entry(frame, width=8)
        self.area_height.insert(0, str(touch_area['height']))
        self.area_height.grid(row=1, column=3, pady=3, padx=2)

        ttk.Separator(frame, orient='horizontal').grid(row=2, column=0, columnspan=4, sticky='ew', pady=6)

        tk.Label(frame, text="터치 오프셋 (mm)", bg='#fff7ed', font=('Arial', 9, 'bold')).grid(
            row=3, column=0, columnspan=4, sticky='w', pady=(0, 3))

        cfg = self.config_manager
        tk.Label(frame, text="X\n(+오른쪽)", bg='#fff7ed', font=('Arial', 8), justify='left').grid(
            row=4, column=0, sticky='w', pady=3)
        self.offset_x_entry = tk.Entry(frame, width=8)
        self.offset_x_entry.insert(0, str(cfg.get("touch_offset", "x_mm") or 0))
        self.offset_x_entry.grid(row=4, column=1, pady=3, padx=2)

        tk.Label(frame, text="Y\n(+위쪽)", bg='#fff7ed', font=('Arial', 8), justify='left').grid(
            row=4, column=2, sticky='w', pady=3, padx=(10, 0))
        self.offset_y_entry = tk.Entry(frame, width=8)
        self.offset_y_entry.insert(0, str(cfg.get("touch_offset", "y_mm") or 0))
        self.offset_y_entry.grid(row=4, column=3, pady=3, padx=2)

        tk.Button(frame, text="적용", command=self._apply_touch_area,
                 bg='#f97316', fg='white', font=('Arial', 9, 'bold')).grid(row=5, column=0, columnspan=4, pady=10)
    
    def _create_detection_section(self, parent):
        frame = tk.LabelFrame(parent, text="터치 감지", bg='#f0fdf4',
                             font=('Arial', 10, 'bold'), padx=10, pady=10)
        frame.pack(fill=tk.X, padx=10, pady=5)
        cfg = self.config_manager
        
        tk.Label(frame, text="터치 인식범위(mm)", bg='#f0fdf4').grid(row=0, column=0, sticky='w', pady=3)
        self.threshold_entry = tk.Entry(frame, width=8)
        self.threshold_entry.insert(0, str(cfg.get("touch_detection", "threshold_mm")))
        self.threshold_entry.grid(row=0, column=1, pady=3)
        
        tk.Label(frame, text="최소포인트", bg='#f0fdf4').grid(row=1, column=0, sticky='w', pady=3)
        self.min_points_entry = tk.Entry(frame, width=8)
        self.min_points_entry.insert(0, str(cfg.get("touch_detection", "min_points")))
        self.min_points_entry.grid(row=1, column=1, pady=3)
        
        tk.Label(frame, text="클릭쿨다운(ms)", bg='#f0fdf4').grid(row=2, column=0, sticky='w', pady=3)
        self.click_cooldown_entry = tk.Entry(frame, width=8)
        self.click_cooldown_entry.insert(0, str(int(cfg.get("touch_detection", "click_cooldown_ms") or 30)))
        self.click_cooldown_entry.grid(row=2, column=1, pady=3)
        
        tk.Button(frame, text="적용", command=self._apply_detection_settings,
                 bg='#16a34a', fg='white', font=('Arial', 9, 'bold')).grid(row=3, column=0, columnspan=2, pady=10)
    
    def _create_mouse_control_section(self, parent):
        frame = tk.LabelFrame(parent, text="마우스 제어", bg='white',
                             font=('Arial', 10, 'bold'), padx=10, pady=10)
        frame.pack(fill=tk.X, padx=10, pady=5)
        cfg = self.config_manager
        
        tk.Checkbutton(frame, text="마우스 제어 활성화", variable=self.enable_mouse_control,
                      command=self._toggle_mouse_control, bg='white', font=('Arial', 10)).pack(anchor='w', pady=5)
        
        monitor_frame = tk.Frame(frame, bg='white')
        monitor_frame.pack(fill=tk.X, pady=5)
        tk.Label(monitor_frame, text="모니터", bg='white').pack(side=tk.LEFT)
        
        monitor_names = [m['name'] for m in self.monitors]
        self.monitor_combo = ttk.Combobox(monitor_frame, textvariable=self.monitor_var,
                                          values=monitor_names, width=20, state='readonly')
        self.monitor_combo.pack(side=tk.LEFT, padx=5)
        if monitor_names:
            idx = cfg.get("mouse_control", "monitor_index")
            self.monitor_combo.current(min(idx, len(monitor_names)-1))
        self.monitor_combo.bind('<<ComboboxSelected>>', self._on_monitor_selected)
        tk.Button(monitor_frame, text="🔄", command=self._refresh_monitors, font=('Arial', 9)).pack(side=tk.LEFT, padx=2)
        
        size_frame = tk.Frame(frame, bg='white')
        size_frame.pack(fill=tk.X, pady=5)
        tk.Label(size_frame, text="화면 크기", bg='white').grid(row=0, column=0, sticky='w')
        self.screen_w = tk.Entry(size_frame, width=6)
        self.screen_w.insert(0, str(cfg.get("mouse_control", "screen_width")))
        self.screen_w.grid(row=0, column=1, padx=2)
        tk.Label(size_frame, text="x", bg='white').grid(row=0, column=2)
        self.screen_h = tk.Entry(size_frame, width=6)
        self.screen_h.insert(0, str(cfg.get("mouse_control", "screen_height")))
        self.screen_h.grid(row=0, column=3, padx=2)
    
    def _refresh_monitors(self):
        self.monitors = get_monitors()
        self.monitor_combo['values'] = [m['name'] for m in self.monitors]
        if self.monitors:
            self.monitor_combo.current(0)
            self._on_monitor_selected()
            # 모니터 정보 저장
            self.config_manager.save_monitors(self.monitors)
        self._log_status(f"모니터 {len(self.monitors)}개 발견 (저장됨)")
    
    def _on_monitor_selected(self, event=None):
        idx = self.monitor_combo.current()
        if 0 <= idx < len(self.monitors):
            m = self.monitors[idx]
            self.screen_w.delete(0, tk.END)
            self.screen_w.insert(0, str(m['width']))
            self.screen_h.delete(0, tk.END)
            self.screen_h.insert(0, str(m['height']))
            self.controller.set_screen_size(m['width'], m['height'])
            self.controller.set_screen_offset(m['x'], m['y'])
            
            # 선택된 모니터 정보 저장
            self.config_manager.set(idx, "mouse_control", "monitor_index")
            self.config_manager.set(m['width'], "mouse_control", "screen_width")
            self.config_manager.set(m['height'], "mouse_control", "screen_height")
            self.config_manager.set(m['x'], "mouse_control", "screen_offset_x")
            self.config_manager.set(m['y'], "mouse_control", "screen_offset_y")
            
            self._log_status(f"모니터 선택: {m['name']}")
    
    def _create_ant_section(self, parent):
        frame = tk.LabelFrame(parent, text="ANT+ 심박계", bg='#fce7f3',
                              font=('Arial', 10, 'bold'), padx=10, pady=10)
        frame.pack(fill=tk.X, padx=10, pady=5)

        # 상태 + BPM
        top_frame = tk.Frame(frame, bg='#fce7f3')
        top_frame.pack(fill=tk.X, pady=(0, 5))
        self.ant_status_label = tk.Label(top_frame, text="● 중지됨",
                                         fg='gray', bg='#fce7f3', font=('Arial', 10))
        self.ant_status_label.pack(side=tk.LEFT)
        self.ant_bpm_label = tk.Label(top_frame, text="-- BPM",
                                      fg='#be185d', bg='#fce7f3', font=('Arial', 14, 'bold'))
        self.ant_bpm_label.pack(side=tk.RIGHT)

        # 시작/중지 버튼
        self.ant_btn = tk.Button(frame, text="ANT+ 시작",
                                 command=self._toggle_ant,
                                 bg='#ec4899', fg='white',
                                 font=('Arial', 10, 'bold'), pady=4)
        self.ant_btn.pack(fill=tk.X, pady=(0, 8))

        # UDP 설정
        udp_frame = tk.LabelFrame(frame, text="UDP 설정", bg='#fce7f3',
                                   font=('Arial', 9), padx=8, pady=6)
        udp_frame.pack(fill=tk.X, pady=3)

        tk.Label(udp_frame, text="IP", bg='#fce7f3').grid(row=0, column=0, sticky='w', pady=2)
        self.ant_udp_ip = tk.Entry(udp_frame, width=15)
        self.ant_udp_ip.insert(0, self.ant.udp_ip)
        self.ant_udp_ip.grid(row=0, column=1, pady=2, padx=4)

        tk.Label(udp_frame, text="Port", bg='#fce7f3').grid(row=1, column=0, sticky='w', pady=2)
        self.ant_udp_port = tk.Entry(udp_frame, width=15)
        self.ant_udp_port.insert(0, str(self.ant.udp_port))
        self.ant_udp_port.grid(row=1, column=1, pady=2, padx=4)

        # ANT+ 설정
        ant_cfg_frame = tk.LabelFrame(frame, text="ANT+ 설정", bg='#fce7f3',
                                       font=('Arial', 9), padx=8, pady=6)
        ant_cfg_frame.pack(fill=tk.X, pady=3)

        fields = [
            ("Device Type", "ant_device_type", self.ant.device_type),
            ("Device ID",   "ant_device_id",   self.ant.device_id),
            ("HR Period",   "ant_hr_period",    self.ant.hr_period),
            ("RF Freq",     "ant_rf_freq",      self.ant.rf_freq),
        ]
        for i, (label, attr, val) in enumerate(fields):
            tk.Label(ant_cfg_frame, text=label, bg='#fce7f3').grid(row=i, column=0, sticky='w', pady=2)
            entry = tk.Entry(ant_cfg_frame, width=10)
            entry.insert(0, str(val))
            entry.grid(row=i, column=1, pady=2, padx=4)
            setattr(self, attr, entry)

        tk.Button(frame, text="설정 적용", command=self._apply_ant_settings,
                  bg='#9d174d', fg='white', font=('Arial', 9, 'bold')).pack(fill=tk.X, pady=(5, 0))

    def _toggle_ant(self):
        if self.ant.is_running:
            self.ant.stop()
            self.ant_btn.config(text="ANT+ 시작", bg='#ec4899')
            self.ant_status_label.config(text="● 중지됨", fg='gray')
            self.ant_bpm_label.config(text="-- BPM")
        else:
            self._apply_ant_settings()
            self.ant.start()
            self.ant_btn.config(text="ANT+ 중지", bg='#6b21a8')
            self.ant_status_label.config(text="● 연결 중...", fg='orange')

    def _apply_ant_settings(self):
        try:
            self.ant.udp_ip = self.ant_udp_ip.get().strip()
            self.ant.udp_port = int(self.ant_udp_port.get())
            self.ant.device_type = int(self.ant_device_type.get())
            self.ant.device_id = int(self.ant_device_id.get())
            self.ant.hr_period = int(self.ant_hr_period.get())
            self.ant.rf_freq = int(self.ant_rf_freq.get())
        except ValueError:
            messagebox.showerror("오류", "ANT+ 설정값을 확인하세요.")

    def _on_ant_bpm(self, bpm: int):
        self.root.after(0, lambda: self.ant_bpm_label.config(text=f"{bpm} BPM"))

    def _on_ant_status(self, msg: str):
        self.root.after(0, lambda: (
            self.ant_status_label.config(text=f"● {msg}", fg='green'),
            self._log_status(f"[ANT+] {msg}")
        ))

    def _on_ant_error(self, msg: str):
        self.root.after(0, lambda: (
            self.ant_status_label.config(text="● 오류 (재시도 중)", fg='red'),
            self._log_status(f"[ANT+ 오류] {msg}")
        ))

    def _create_status_section(self, parent):
        frame = tk.LabelFrame(parent, text="로그", bg='white',
                             font=('Arial', 10, 'bold'), padx=10, pady=10)
        frame.pack(fill=tk.X, padx=10, pady=5)
        self.status_text = tk.Text(frame, height=8, width=30, font=('Courier', 9))
        self.status_text.pack(fill=tk.BOTH, expand=True)
    
    def _toggle_connection(self):
        if self.controller.is_connected:
            self._disconnect_and_stop()
        else:
            self._connect_and_start()
    
    def _connect_and_start(self):
        try:
            port = self.port_var.get()
            baudrate = int(self.baudrate_entry.get())
            self.controller.connect(port, baudrate)
            
            # RPM 설정 적용 (연결 직후, 스캔 전)
            target_freq = self.current_freq
            actual_freq = self.controller.set_scan_frequency(target_freq)
            self.current_freq = actual_freq
            self.rpm_label.config(text=f"{actual_freq:.1f}Hz")
            
            self.controller.start_scan()
            self.status_label.config(text="● 연결됨 (스캔 중)", fg='green')
            self.connect_btn.config(text="중지 및 연결 해제", bg='#ef4444')
        except Exception as e:
            error_msg = (
                f"라이다 연결 실패:\n{e}\n\n"
                "─────────────────────────────\n"
                "Lidar를 연결하지 못한다면 아래 드라이버를 설치하세요:\n\n"
                "UART 보드 드라이버 - CP210x_VCP_Windows\n\n"
                "다운로드:\n"
                "https://ko.ydlidar.com/download/category/tool-sdk-ros-lidarclient"
            )
            messagebox.showerror("연결 오류", error_msg)
    
    def _disconnect_and_stop(self):
        self.controller.disconnect()
        self.status_label.config(text="● 연결 안 됨", fg='red')
        self.connect_btn.config(text="연결 및 시작", bg='#22c55e')
    
    def _toggle_mouse_control(self):
        self.controller.set_mouse_enabled(self.enable_mouse_control.get())
    
    def _apply_touch_area(self):
        try:
            x, y = int(self.area_x.get()), int(self.area_y.get())
            w, h = int(self.area_width.get()), int(self.area_height.get())
            offset_x = float(self.offset_x_entry.get())
            offset_y = float(self.offset_y_entry.get())
            self.controller.set_touch_area(x, y, w, h)
            self.controller.set_touch_offset(offset_x, offset_y)
            self._log_status(f"터치 영역: ({x}, {y}) {w}x{h}, 오프셋: ({offset_x}, {offset_y})")
        except ValueError:
            messagebox.showerror("오류", "올바른 숫자를 입력하세요.")
    
    def _apply_detection_settings(self):
        try:
            threshold = int(self.threshold_entry.get())
            min_points = int(self.min_points_entry.get())
            click_cooldown_ms = int(self.click_cooldown_entry.get())
            
            self.controller.set_detection_params(threshold, min_points)
            self.controller.set_click_cooldown(click_cooldown_ms / 1000.0)  # ms를 초로 변환
            self.controller.set_screen_size(int(self.screen_w.get()), int(self.screen_h.get()))
            self._log_status(f"감지 설정 적용됨 (쿨다운: {click_cooldown_ms}ms)")
        except ValueError:
            messagebox.showerror("오류", "올바른 숫자를 입력하세요.")
    
    def _on_data_update(self, distdict, touch_result):
        self.root.after(0, lambda: self._update_plot(touch_result))
    
    def _on_mouse_move(self, event):
        """마우스 이동 시 좌표 표시"""
        if event.xdata is not None and event.ydata is not None:
            self.coord_label.config(text=f"좌표: ({event.xdata:.0f}, {event.ydata:.0f}) mm")
        else:
            self.coord_label.config(text="좌표: (-, -)")
    
    def _on_touch_event(self, touch_result):
        event, center = touch_result['event'], touch_result['center']
        if event == 'start':
            self._log_status("터치 시작")
        elif event == 'click' and center:
            self._log_status(f"클릭! ({center[0]:.0f}, {center[1]:.0f})")
    
    def _on_error(self, error):
        self._log_status(f"오류: {error}")
    
    def _update_plot(self, touch_result=None):
        self.ax.clear()
        
        x, y = self.controller.get_cartesian_points()
        self.ax.scatter(x, y, c='blue', s=self.point_size, alpha=0.6)
        
        # 터치 인식범위 - 라이다 위치(0,0) 중심 원
        threshold = self.controller.touch_detector.threshold
        self.ax.add_patch(Circle((0, 0), threshold, linewidth=1.5, edgecolor='#f97316',
                                  facecolor='none', linestyle='--', zorder=5))

        # 라이다 위치 (0,0) - 검정 원
        self.ax.add_patch(Circle((0, 0), 50, color='black', zorder=10))

        # 터치 영역
        ta = self.controller.touch_area
        self.ax.add_patch(Rectangle((ta['x'], ta['y']), ta['width'], ta['height'],
                                     linewidth=2, edgecolor='green', facecolor='green', alpha=0.2))
        
        # 기준점 (좌상단) - 파란 원
        origin_x, origin_y = ta['x'], ta['y']
        self.ax.add_patch(Circle((origin_x, origin_y), 30, color='blue', zorder=10))
        self.ax.annotate('기준점', (origin_x, origin_y), textcoords="offset points",
                        xytext=(10, 10), fontsize=8, color='blue')
        
        # 터치 포인트
        touch_points = self.controller.touch_points
        if touch_points:
            tx = [p['x'] for p in touch_points]
            ty = [p['y'] for p in touch_points]
            self.ax.scatter(tx, ty, c='red', s=self.touch_point_size, marker='x', linewidths=3, zorder=15)
            avg_x, avg_y = sum(tx)/len(tx), sum(ty)/len(ty)
            self.ax.scatter([avg_x], [avg_y], c='red', s=200, marker='o', edgecolors='white', linewidths=2, zorder=20)
            self.ax.annotate(f'({avg_x:.0f}, {avg_y:.0f})', (avg_x, avg_y),
                           textcoords="offset points", xytext=(15, 15), fontsize=10, color='red',
                           bbox=dict(boxstyle='round', facecolor='white', alpha=0.8))
        
        self.ax.set_xlim(self.plot_xlim)
        self.ax.set_ylim(self.plot_ylim)
        self.ax.set_xlabel('X (mm)')
        self.ax.set_ylabel('Y (mm)')
        self.ax.set_title('라이다 G4 터치스크린')
        self.ax.grid(True, alpha=0.3)
        self.ax.set_aspect('equal')
        self.canvas.draw()
    
    def _log_status(self, message: str):
        timestamp = time.strftime("%H:%M:%S")
        self.status_text.insert(tk.END, f"[{timestamp}] {message}\n")
        self.status_text.see(tk.END)
    
    def _save_all_settings_to_config(self):
        cfg = self.config_manager
        ta = self.controller.touch_area
        
        cfg.set(self.port_var.get(), "connection", "port")
        cfg.set(int(self.baudrate_entry.get()), "connection", "baudrate")
        cfg.set(self.current_freq, "connection", "scan_frequency")
        cfg.set(self.lidar_position_var.get(), "lidar_position", "position")
        cfg.set(self.flip_x_var.get(), "lidar_position", "flip_x")
        cfg.set(self.flip_y_var.get(), "lidar_position", "flip_y")
        cfg.set(int(self.rotation_var.get()), "lidar_position", "rotation")
        cfg.set(int(self.scan_start_entry.get()), "lidar_position", "scan_start_angle")
        cfg.set(int(self.scan_end_entry.get()), "lidar_position", "scan_end_angle")
        cfg.set(ta['x'], "touch_area", "x")
        cfg.set(ta['y'], "touch_area", "y")
        cfg.set(ta['width'], "touch_area", "width")
        cfg.set(ta['height'], "touch_area", "height")
        cfg.set(float(self.offset_x_entry.get()), "touch_offset", "x_mm")
        cfg.set(float(self.offset_y_entry.get()), "touch_offset", "y_mm")
        cfg.set(int(self.threshold_entry.get()), "touch_detection", "threshold_mm")
        cfg.set(int(self.min_points_entry.get()), "touch_detection", "min_points")
        cfg.set(int(self.click_cooldown_entry.get()), "touch_detection", "click_cooldown_ms")
        cfg.set(self.enable_mouse_control.get(), "mouse_control", "enabled")
        cfg.set(self.monitor_combo.current(), "mouse_control", "monitor_index")
        cfg.set(int(self.screen_w.get()), "mouse_control", "screen_width")
        cfg.set(int(self.screen_h.get()), "mouse_control", "screen_height")
        cfg.set(self.plot_xlim, "visualization", "plot_xlim")
        cfg.set(self.plot_ylim, "visualization", "plot_ylim")
        return cfg.save_config()
    
    def _save_config(self):
        if self._save_all_settings_to_config():
            self._log_status("설정 저장됨")
            messagebox.showinfo("성공", "설정이 저장되었습니다.")
    
    def _load_config(self):
        self.config_manager.config = self.config_manager.load_config()
        self._load_settings_from_config()
        self._refresh_ui_from_settings()
        self._log_status("설정 로드됨")
    
    def _reset_to_default(self):
        if messagebox.askyesno("확인", "기본값으로 복원하시겠습니까?"):
            self.config_manager.reset_to_default()
            self._load_settings_from_config()
            self._refresh_ui_from_settings()
            self._log_status("기본값 복원됨")
    
    def _open_download_page(self):
        """다운로드 페이지 열기"""
        url = self.config_manager.get("links", "download_url") or "https://github.com"
        webbrowser.open(url)
        self._log_status(f"웹페이지 열기: {url}")
    
    def _open_manual_page(self):
        """사용 설명서 페이지 열기"""
        url = self.config_manager.get("links", "manual_url") or "https://github.com"
        webbrowser.open(url)
        self._log_status(f"웹페이지 열기: {url}")
    
    def _refresh_ui_from_settings(self):
        cfg = self.config_manager
        ta = self.controller.touch_area
        
        self.port_var.set(cfg.get("connection", "port"))
        self.baudrate_entry.delete(0, tk.END)
        self.baudrate_entry.insert(0, str(cfg.get("connection", "baudrate")))
        
        # RPM 라벨 업데이트
        self.current_freq = cfg.get("connection", "scan_frequency") or 7.0
        self.rpm_label.config(text=f"{self.current_freq:.1f}Hz")
        
        self.lidar_position_var.set(cfg.get("lidar_position", "position"))
        self.flip_x_var.set(cfg.get("lidar_position", "flip_x"))
        self.flip_y_var.set(cfg.get("lidar_position", "flip_y"))
        self.rotation_var.set(str(cfg.get("lidar_position", "rotation") or 0))
        
        for entry, val in [(self.view_x_min, self.plot_xlim[0]), (self.view_x_max, self.plot_xlim[1]),
                           (self.view_y_min, self.plot_ylim[0]), (self.view_y_max, self.plot_ylim[1]),
                           (self.area_x, ta['x']), (self.area_y, ta['y']),
                           (self.area_width, ta['width']), (self.area_height, ta['height']),
                           (self.offset_x_entry, cfg.get("touch_offset", "x_mm") or 0),
                           (self.offset_y_entry, cfg.get("touch_offset", "y_mm") or 0),
                           (self.threshold_entry, cfg.get("touch_detection", "threshold_mm")),
                           (self.min_points_entry, cfg.get("touch_detection", "min_points")),
                           (self.click_cooldown_entry, cfg.get("touch_detection", "click_cooldown_ms") or 30),
                           (self.screen_w, cfg.get("mouse_control", "screen_width")),
                           (self.screen_h, cfg.get("mouse_control", "screen_height"))]:
            entry.delete(0, tk.END)
            entry.insert(0, str(val))
        
        self.enable_mouse_control.set(cfg.get("mouse_control", "enabled"))
    
    def _on_closing(self):
        if messagebox.askyesno("종료", "설정을 저장하시겠습니까?"):
            self._save_all_settings_to_config()
        
        # ANT+ 종료
        try:
            self.ant.stop()
        except:
            pass

        # 라이다 종료
        try:
            if self.controller.is_scanning:
                self.controller.is_scanning = False
            if self.controller.is_connected:
                self.controller.disconnect()
        except:
            pass
        
        # UI 종료
        try:
            self.root.quit()
            self.root.destroy()
        except:
            pass
        
        # 프로세스 강제 종료
        import os
        os._exit(0)
