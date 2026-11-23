#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""BaihuaOpenVinoHost Windows 服务包装

用 pywin32 把 openvino_host.py 注册为真正的 Windows 服务：
SCM 需要服务进程调用 StartServiceCtrlDispatcher，普通 python 脚本直接跑
会被 SCM 判定启动失败而杀掉（表现为"服务已启动但 8866 未就绪"）。

用法（管理员）:
  python openvino_host_service.py install     # 安装服务（自动开机自启）
  python openvino_host_service.py start       # 启动
  python openvino_host_service.py stop        # 停止
  python openvino_host_service.py remove      # 卸载
  python openvino_host_service.py debug       # 前台调试运行（不注册 SCM）

服务行为:
  - 启动 openvino_host.py 子进程（--port 8866 --bind 127.0.0.1）
  - 停止时终止子进程树（含 openvino_llm_server.py 实例）
"""
import os
import subprocess
import sys

import servicemanager
import win32event
import win32service
import win32serviceutil

SERVICE_NAME = "BaihuaOpenVinoHost"
SERVICE_DISPLAY = "Baihua OpenVINO Host"
SERVICE_DESC = ("Baihua OpenVINO LLM/Embedding host "
                "(openvino_llm_server.py: 8000 chat / 8001 code / 8002 embedding)")

# 服务以 LocalSystem 运行，读不到用户级环境变量 BAIHUA_HOME → 显式指定
BAIHUA_HOME = os.environ.get("BAIHUA_HOME") or r"C:\Users\lumin\.baihua"

# pywin32 服务宿主是 pythonservice.exe，此时 sys.executable 指向它自己，
# 不能用来启动子进程。必须显式定位真实 python.exe。
def _resolve_python_exe() -> str:
    """定位真实 python.exe（优先同级目录，其次 PATH）"""
    # 当前解释器所在目录下的 python.exe（常规安装布局）
    base = os.path.dirname(sys.executable)
    cand = os.path.join(base, "python.exe")
    if os.path.exists(cand):
        return cand
    # pythonservice.exe 同级（pywin32 服务布局）
    cand2 = os.path.join(base, "..", "python.exe")
    if os.path.exists(cand2):
        return os.path.normpath(cand2)
    # PATH 兜底
    return "python"


class BaihuaOpenVinoHostService(win32serviceutil.ServiceFramework):
    _svc_name_ = SERVICE_NAME
    _svc_display_name_ = SERVICE_DISPLAY
    _svc_description_ = SERVICE_DESC

    def __init__(self, args):
        win32serviceutil.ServiceFramework.__init__(self, args)
        self.hWaitStop = win32event.CreateEvent(None, 0, 0, None)
        self.proc = None

    def SvcStop(self):
        """SCM 停止请求：先报 STOP_PENDING，终止子进程树，再置停止事件"""
        self.ReportServiceStatus(win32service.SERVICE_STOP_PENDING)
        if self.proc and self.proc.poll() is None:
            try:
                # 终止整棵进程树（host + 其管理的 openvino_llm_server.py）
                subprocess.run(
                    ["taskkill", "/PID", str(self.proc.pid), "/T", "/F"],
                    capture_output=True, timeout=15,
                )
            except Exception:
                try:
                    self.proc.terminate()
                except Exception:
                    pass
        win32event.SetEvent(self.hWaitStop)

    def SvcDoRun(self):
        servicemanager.LogMsg(
            servicemanager.EVENTLOG_INFORMATION_TYPE,
            servicemanager.PYS_SERVICE_STARTED,
            (SERVICE_NAME, ""),
        )
        # 子进程继承服务环境，显式补 BAIHUA_HOME
        env = dict(os.environ)
        env["BAIHUA_HOME"] = BAIHUA_HOME

        host_script = os.path.join(
            os.path.dirname(os.path.abspath(__file__)), "openvino_host.py"
        )
        python_exe = _resolve_python_exe()
        log_dir = os.path.dirname(os.path.abspath(__file__))
        log_file = os.path.join(log_dir, "logs", "openvino_host_service.log")
        os.makedirs(os.path.dirname(log_file), exist_ok=True)
        with open(log_file, "ab") as lf:
            self.proc = subprocess.Popen(
                [python_exe, host_script, "--port", "8866", "--bind", "127.0.0.1"],
                env=env,
                stdout=lf,
                stderr=lf,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        servicemanager.LogMsg(
            servicemanager.EVENTLOG_INFORMATION_TYPE,
            0x40000000 | 4099,
            (f"openvino_host 子进程已启动 pid={self.proc.pid} python={python_exe}",),
        )
        # 等待停止事件（阻塞在服务线程）
        win32event.WaitForSingleObject(self.hWaitStop, win32event.INFINITE)
        if self.proc and self.proc.poll() is None:
            try:
                subprocess.run(
                    ["taskkill", "/PID", str(self.proc.pid), "/T", "/F"],
                    capture_output=True, timeout=15,
                )
            except Exception:
                pass
        servicemanager.LogMsg(
            servicemanager.EVENTLOG_INFORMATION_TYPE,
            servicemanager.PYS_SERVICE_STOPPED,
            (SERVICE_NAME, ""),
        )


def _ensure_service_env():
    """提示：服务以 LocalSystem 运行，若需要访问用户目录/网络需确认权限"""
    pass


if __name__ == "__main__":
    if len(sys.argv) == 1:
        servicemanager.Initialize()
        servicemanager.PrepareToHostSingle(BaihuaOpenVinoHostService)
        servicemanager.StartServiceCtrlDispatcher()
    else:
        win32serviceutil.HandleCommandLine(BaihuaOpenVinoHostService)
