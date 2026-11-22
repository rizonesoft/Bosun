"""Shared scratch-folder helpers for Python tests (D00 T07 §3).

Resolves BOSUN_SCRATCH when set, non-whitespace and an existing directory; otherwise falls back to the system
temporary folder. Callers that need a separate root (such as BOSUN_E2E_SCRATCH) pass `root=...` (and
`create_root=True` when a missing override root should be created). Once a root is selected, a failure creating
the child directory raises OSError naming that root and never falls back to system temp. Stdlib only.
"""
from __future__ import annotations

import os
from pathlib import Path
import re
import tempfile

_SAFE_NAME = re.compile(r"\A[A-Za-z0-9._-]+\Z")


def _validate_name(name: str) -> str:
    if not name or name in (".", "..") or not _SAFE_NAME.fullmatch(name):
        raise ValueError(f"invalid scratch folder name {name!r} (allowed: A-Z, a-z, 0-9, ., _, -)")
    return name


def scratch_root(*, root: str | os.PathLike[str] | None = None, create_root: bool = False) -> Path:
    """Return the scratch parent directory to allocate under."""
    if root is not None:
        raw = os.fspath(root).strip()
        if not raw:
            raise ValueError("scratch root override must not be empty")
        candidate = Path(raw)
        if candidate.is_dir():
            return candidate.resolve()
        if create_root:
            try:
                candidate.mkdir(parents=True, exist_ok=True)
            except OSError as exc:
                raise OSError(f"could not create scratch root {candidate}: {exc}") from exc
            if candidate.is_dir():
                return candidate.resolve()
        raise OSError(f"scratch root {candidate} is not an existing directory")

    env_raw = os.environ.get("BOSUN_SCRATCH", "").strip()
    if env_raw and os.path.isdir(env_raw):
        return Path(env_raw).resolve()
    return Path(tempfile.gettempdir())


def scratch_dir(name: str = "test", *, root: str | os.PathLike[str] | None = None, create_root: bool = False) -> Path:
    """Create and return a new unique directory under the scratch root; the caller removes it."""
    safe = _validate_name(name)
    base = scratch_root(root=root, create_root=create_root)
    try:
        return Path(tempfile.mkdtemp(prefix=f"bosun-{safe}-", dir=str(base)))
    except OSError as exc:
        raise OSError(f"could not create scratch folder under {base}: {exc}") from exc


def scratch_tempdir(
    name: str = "test",
    *,
    root: str | os.PathLike[str] | None = None,
    create_root: bool = False,
) -> tempfile.TemporaryDirectory[str]:
    """Create and return a TemporaryDirectory under the scratch root."""
    safe = _validate_name(name)
    base = scratch_root(root=root, create_root=create_root)
    try:
        return tempfile.TemporaryDirectory(prefix=f"bosun-{safe}-", dir=str(base))
    except OSError as exc:
        raise OSError(f"could not create scratch folder under {base}: {exc}") from exc
