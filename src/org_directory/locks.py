"""One sync at a time per settings file (win-background).

Two syncs at once (the daily task and one started by hand) would write the same charts and state.
On Windows this is a named mutex, which Windows releases if the process dies, so a crash can't leave
it stuck.
"""

from __future__ import annotations

import contextlib

from win_background import exclusive, key

from .config import LOCAL, Config


@contextlib.contextmanager
def run(config: Config, timeout: float = 600):
    """One sync at a time. Raises TimeoutError if another doesn't finish within timeout seconds."""
    with exclusive(f"org-directory-run-{key(config.path)}", timeout, LOCAL, what=f"sync for {config.path}"):
        yield
