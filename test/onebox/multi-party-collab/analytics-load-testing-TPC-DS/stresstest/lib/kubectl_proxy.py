#!/usr/bin/env python3
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.


from __future__ import annotations

import atexit
import contextlib
import os
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request

_START_TIMEOUT_SECONDS = 30
_PROBE_TIMEOUT_SECONDS = 10
_PORT_FREE_TIMEOUT_SECONDS = 5
_PORT_FREE_POLL_SECONDS = 0.2


class KubectlProxy:
    def __init__(
        self,
        kube_config: str,
        port: int,
        *,
        address: str = "localhost",
        accept_hosts: str = (
            r"^(localhost|127\.0\.0\.1|host\.docker\.internal|172\.17\.0\.1)"
            r"(:\d+)?$"
        ),
    ) -> None:
        self.kube_config = kube_config
        self.port = port
        self.address = address
        self.accept_hosts = accept_hosts

        self._process: subprocess.Popen | None = None
        self._log_file = None  # type: ignore[assignment]
        self._log_dir: str | None = None
        self._started_at: float = 0.0
        self._owned: bool = False

    def start(self, *, log_dir: str | None = None) -> str:
        self._kill_stale_listener()

        self._log_dir = log_dir
        if log_dir:
            os.makedirs(log_dir, exist_ok=True)
            log_path = os.path.join(log_dir, "kubectl-proxy.log")
            log_file = open(log_path, "w")  # noqa: SIM115
            stdout_target = log_file
            stderr_target: int = subprocess.STDOUT
        else:
            log_file = None
            stdout_target = subprocess.DEVNULL
            stderr_target = subprocess.PIPE

        cmd = [
            "kubectl",
            "proxy",
            "--port",
            str(self.port),
            "--address",
            self.address,
            "--accept-hosts",
            self.accept_hosts,
        ]
        if self.kube_config:
            cmd.extend(["--kubeconfig", self.kube_config])

        try:
            process = subprocess.Popen(
                cmd,
                stdout=stdout_target,
                stderr=stderr_target,
                text=True,
            )
        except OSError:
            if log_file is not None:
                log_file.close()
            raise

        self._process = process
        self._log_file = log_file
        self._started_at = time.monotonic()
        self._owned = True

        deadline = time.monotonic() + _START_TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            if process.poll() is not None:
                err_tail = ""
                if process.stderr is not None:
                    with contextlib.suppress(OSError, ValueError):
                        err_tail = (process.stderr.read() or "").strip()
                self._cleanup_log_file()
                raise RuntimeError(f"kubectl proxy exited unexpectedly: {err_tail}")
            if self._probe("/healthz"):
                return self.address
            time.sleep(0.5)

        self.stop()
        raise RuntimeError(
            f"kubectl proxy failed to start on port {self.port} "
            f"within {_START_TIMEOUT_SECONDS}s"
        )

    def start_or_reuse(self, *, log_dir: str | None = None) -> str:
        if self._probe("/healthz") and self._probe("/api"):
            self._owned = False
            return self.address
        return self.start(log_dir=log_dir)

    def stop(self) -> None:
        if not self._owned:
            return
        proc = self._process
        if proc is None:
            return
        if proc.poll() is None:
            with contextlib.suppress(OSError):
                proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                with contextlib.suppress(OSError):
                    proc.kill()
                    proc.wait()
        self._cleanup_log_file()
        self._process = None

    def restart(self) -> str:
        log_dir = self._log_dir
        self.stop()
        return self.start(log_dir=log_dir)

    def healthy(self) -> bool:
        if self._owned:
            proc = self._process
            if proc is None or proc.poll() is not None:
                return False
        return self._probe("/healthz") and self._probe("/api")

    def ensure_alive(self) -> None:
        if self._process is None and not self._owned:
            return
        if not self._owned:
            print(
                f"[kubectl_proxy] Reused proxy on port {self.port} "
                f"is unhealthy; cannot restart (not owned)."
            )
            return
        age = time.monotonic() - self._started_at
        print(f"[kubectl_proxy] Unhealthy (age={age:.0f}s); restarting.")
        self.restart()

    def _cleanup_log_file(self) -> None:
        log_file = self._log_file
        if log_file is not None and not log_file.closed:
            with contextlib.suppress(OSError):
                log_file.close()
        self._log_file = None

    def _port_in_use(self) -> bool:
        # Probe IPv4 explicitly: kubectl proxy binds 127.0.0.1, but
        # self.address ("localhost") can resolve to IPv6 ::1 and give a
        # false "free" reading while the IPv4 socket is still bound.
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.settimeout(1)
            return s.connect_ex(("127.0.0.1", self.port)) == 0

    def _wait_port_free(self) -> bool:
        deadline = time.monotonic() + _PORT_FREE_TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            if not self._port_in_use():
                return True
            time.sleep(_PORT_FREE_POLL_SECONDS)
        return not self._port_in_use()

    def _kill_stale_listener(self) -> None:
        # Stop any proxy left over from a prior run before binding, escalating
        # SIGTERM -> SIGKILL, then falling back to killing whatever holds the
        # port. Without this the next bind() hits "address already in use".
        pattern = rf"kubectl proxy.*--port[ =]{self.port}(\s|$)"
        for kill_cmd in (["pkill", "-f", pattern], ["pkill", "-9", "-f", pattern]):
            if not self._port_in_use():
                return
            with contextlib.suppress(FileNotFoundError):
                subprocess.run(kill_cmd, capture_output=True, text=True)
            if self._wait_port_free():
                return
        # Last resort: kill whatever process holds the TCP port (catches a
        # stale proxy launched with a different command line).
        if self._port_in_use():
            with contextlib.suppress(FileNotFoundError):
                subprocess.run(
                    ["fuser", "-k", f"{self.port}/tcp"],
                    capture_output=True,
                    text=True,
                )
            self._wait_port_free()

    def _probe(self, path: str) -> bool:
        url = f"http://127.0.0.1:{self.port}{path}"
        try:
            with urllib.request.urlopen(url, timeout=_PROBE_TIMEOUT_SECONDS) as r:  # nosec B310
                if r.status != 200:
                    return False
                if path == "/healthz":
                    return r.read(8).strip() == b"ok"
                return True
        except (urllib.error.URLError, OSError, TimeoutError, ValueError):
            return False


_active_proxy: KubectlProxy | None = None
# Serializes restart/cycle across worker threads so they don't race on bind().
_proxy_lock = threading.Lock()


def _stop_active() -> None:
    if _active_proxy is not None:
        with contextlib.suppress(Exception):
            _active_proxy.stop()


atexit.register(_stop_active)


def start_kubectl_proxy(
    kube_config: str,
    port: int,
    *,
    log_dir: str | None = None,
) -> str:
    global _active_proxy
    if _active_proxy is not None and (
        _active_proxy.kube_config != kube_config or _active_proxy.port != port
    ):
        _active_proxy.stop()
        _active_proxy = None
    if _active_proxy is None:
        _active_proxy = KubectlProxy(kube_config=kube_config, port=port)
    return _active_proxy.start_or_reuse(log_dir=log_dir)


def ensure_kubectl_proxy_alive() -> None:
    if _active_proxy is None:
        return
    with _proxy_lock:
        # Re-check under the lock: another thread may have already restarted.
        if _active_proxy.healthy():
            return
        _active_proxy.ensure_alive()


def stop_kubectl_proxy() -> None:
    if _active_proxy is not None:
        _active_proxy.stop()


def cycle_kubectl_proxy() -> None:
    if _active_proxy is None:
        return
    with _proxy_lock:
        if _active_proxy.healthy():
            return
        _active_proxy.restart()


def active_kubectl_proxy() -> KubectlProxy | None:
    return _active_proxy
