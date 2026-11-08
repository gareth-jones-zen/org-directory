"""One sync at a time per settings file, from teams-channel-notes.

Two syncs at once (the daily task and one started by hand) would write the same charts and state.
On Windows this is a named mutex, which Windows releases if the process dies, so a crash can't leave
it stuck; elsewhere, a file lock.
"""

from __future__ import annotations

import contextlib
import hashlib
import sys
import time

from .config import LOCAL, Config

WAIT_OBJECT_0, WAIT_ABANDONED = 0, 0x80


def _key(config: Config) -> str:
    return hashlib.sha256(str(config.path).lower().encode()).hexdigest()[:16]


class _Lock:
    def __init__(self, name: str):
        self.name, self.handle, self.file = name, None, None

    def acquire(self, timeout: float) -> bool:
        if sys.platform == "win32":
            import ctypes
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.CreateMutexW.restype = ctypes.c_void_p
            kernel32.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
            self.handle = kernel32.CreateMutexW(None, False, f"Local\\{self.name}")
            result = kernel32.WaitForSingleObject(ctypes.c_void_p(self.handle), int(timeout * 1000))
            if result in (WAIT_OBJECT_0, WAIT_ABANDONED):
                return True
            kernel32.CloseHandle(ctypes.c_void_p(self.handle))
            self.handle = None
            return False
        import fcntl
        LOCAL.mkdir(parents=True, exist_ok=True)
        self.file = open(LOCAL / f"{self.name}.lock", "w")
        deadline = time.time() + timeout
        while True:
            try:
                fcntl.flock(self.file, fcntl.LOCK_EX | fcntl.LOCK_NB)
                return True
            except OSError:
                if time.time() >= deadline:
                    self.file.close()
                    self.file = None
                    return False
                time.sleep(1)

    def release(self):
        if self.handle is not None:
            import ctypes
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.ReleaseMutex(ctypes.c_void_p(self.handle))
            kernel32.CloseHandle(ctypes.c_void_p(self.handle))
            self.handle = None
        if self.file is not None:
            self.file.close()
            self.file = None


@contextlib.contextmanager
def run(config: Config, timeout: float = 600):
    """One sync at a time. Raises TimeoutError if another doesn't finish within timeout seconds."""
    lock = _Lock(f"org-directory-run-{_key(config)}")
    if not lock.acquire(timeout):
        raise TimeoutError(f"Another sync for {config.path} didn't finish within {timeout / 60:.0f} minutes.")
    try:
        yield
    finally:
        lock.release()
