#!/usr/bin/env python3
"""Verify an AppImage maps its main X11 window in an isolated user environment."""

import ctypes
import os
from pathlib import Path
import select
import shutil
import signal
import subprocess
import sys
import tempfile
import time


class XAnyEvent(ctypes.Structure):
    _fields_ = [("type", ctypes.c_int), ("serial", ctypes.c_ulong),
                ("send_event", ctypes.c_int), ("display", ctypes.c_void_p),
                ("window", ctypes.c_ulong)]


class XMapEvent(ctypes.Structure):
    _fields_ = XAnyEvent._fields_[:-1] + [("event", ctypes.c_ulong),
                                        ("window", ctypes.c_ulong),
                                        ("override_redirect", ctypes.c_int)]


class XEvent(ctypes.Union):
    _fields_ = [("type", ctypes.c_int), ("any", XAnyEvent),
                ("map", XMapEvent), ("padding", ctypes.c_long * 24)]


def stop_group(process):
    if process is None:
        return
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=10)


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: Test_LinuxWindow.py path/to/application.AppImage")
    image = Path(sys.argv[1]).resolve(strict=True)
    xlib = ctypes.CDLL("libX11.so.6")
    xlib.XOpenDisplay.argtypes = [ctypes.c_char_p]
    xlib.XOpenDisplay.restype = ctypes.c_void_p
    xlib.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
    xlib.XDefaultRootWindow.restype = ctypes.c_ulong
    xlib.XConnectionNumber.argtypes = [ctypes.c_void_p]
    xlib.XSelectInput.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_long]
    xlib.XPending.argtypes = [ctypes.c_void_p]
    xlib.XNextEvent.argtypes = [ctypes.c_void_p, ctypes.POINTER(XEvent)]
    xlib.XFetchName.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_void_p)]
    xlib.XFree.argtypes = [ctypes.c_void_p]
    xlib.XFlush.argtypes = [ctypes.c_void_p]
    xlib.XCloseDisplay.argtypes = [ctypes.c_void_p]
    display = None
    server = None
    application = None
    process_descriptor = None
    with tempfile.TemporaryDirectory(prefix="ratiomaster-x11-") as temporary:
        scratch = Path(temporary)
        launch_image = scratch / image.name
        shutil.copy2(image, launch_image)
        launch_image.chmod(0o755)
        environment = os.environ.copy()
        for variable in ("HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_RUNTIME_DIR", "TMPDIR"):
            directory = scratch / variable.lower()
            directory.mkdir(mode=0o700)
            environment[variable] = str(directory)
        environment["APPIMAGE_EXTRACT_AND_RUN"] = "1"
        read_ready, write_ready = os.pipe()
        try:
            with (scratch / "xvfb.log").open("wb") as server_log:
                server = subprocess.Popen(["Xvfb", "-displayfd", str(write_ready), "-screen", "0", "1280x900x24", "-nolisten", "tcp"],
                                          pass_fds=(write_ready,), stdout=server_log, stderr=subprocess.STDOUT,
                                          env=environment, start_new_session=True)
            os.close(write_ready)
            write_ready = None
            if not select.select([read_ready], [], [], 20)[0]:
                raise RuntimeError("Xvfb did not report a ready display.")
            display_number = os.read(read_ready, 100).decode().strip()
            if not display_number.isdigit():
                raise RuntimeError("Xvfb failed before creating a display.")
            environment["DISPLAY"] = ":" + display_number
            display = xlib.XOpenDisplay(environment["DISPLAY"].encode())
            if not display:
                raise RuntimeError("Cannot connect to the isolated X11 display.")
            root = xlib.XDefaultRootWindow(display)
            xlib.XSelectInput(display, root, 1 << 19)  # SubstructureNotifyMask
            xlib.XFlush(display)
            with (scratch / "application.log").open("wb") as application_log:
                application = subprocess.Popen([str(launch_image)], stdout=application_log, stderr=subprocess.STDOUT,
                                               env=environment, cwd=scratch, start_new_session=True)
            process_descriptor = os.pidfd_open(application.pid)
            connection = xlib.XConnectionNumber(display)
            deadline = time.monotonic() + 45
            while True:
                while xlib.XPending(display):
                    event = XEvent()
                    xlib.XNextEvent(display, ctypes.byref(event))
                    window = None
                    if event.type == 19:  # MapNotify
                        window = event.map.window
                        xlib.XSelectInput(display, window, 1 << 22)  # PropertyChangeMask
                    elif event.type == 28:  # PropertyNotify
                        window = event.any.window
                    if window is None:
                        continue
                    name = ctypes.c_void_p()
                    if xlib.XFetchName(display, window, ctypes.byref(name)) and name.value:
                        try:
                            title = ctypes.string_at(name).decode("utf-8", errors="replace")
                        finally:
                            xlib.XFree(name)
                        if title.lower().startswith("ratiomaster.net "):
                            print(f"PASS: AppImage mapped its main X11 window: {title}", flush=True)
                            return
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise RuntimeError("The AppImage did not map its main window within 45 seconds.")
                ready = select.select([connection, process_descriptor], [], [], remaining)[0]
                if process_descriptor in ready:
                    raise RuntimeError(f"The AppImage exited before showing its main window: {application.wait()}")
        except Exception:
            for log in (scratch / "xvfb.log", scratch / "application.log"):
                if log.exists():
                    print(log.read_text(errors="replace"), file=sys.stderr)
            raise
        finally:
            if process_descriptor is not None:
                os.close(process_descriptor)
            stop_group(application)
            if display:
                xlib.XCloseDisplay(display)
            stop_group(server)
            os.close(read_ready)
            if write_ready is not None:
                os.close(write_ready)


if __name__ == "__main__":
    main()
