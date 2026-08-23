from __future__ import annotations

import threading
from pathlib import Path
from typing import Callable

from engines.model_defaults import MODEL_CACHE_DIR


DownloadProgress = Callable[[float, int, int], None]


def download_model(reference: str, progress: DownloadProgress | None = None) -> str:
    local_path = Path(reference).expanduser()
    if local_path.exists():
        if progress:
            progress(1.0, 0, 0)
        return str(local_path)

    try:
        from huggingface_hub import HfApi, snapshot_download
    except ImportError:
        return reference

    info = HfApi().model_info(reference, files_metadata=True)
    total_bytes = sum(int(sibling.size or 0) for sibling in info.siblings)
    repository_cache = MODEL_CACHE_DIR / f"models--{reference.replace('/', '--')}"
    stop_monitor = threading.Event()

    def report() -> None:
        if not progress:
            return
        downloaded = _cached_bytes(repository_cache)
        fraction = min(1.0, downloaded / total_bytes) if total_bytes > 0 else 0.0
        progress(fraction, min(downloaded, total_bytes), total_bytes)

    def monitor() -> None:
        while not stop_monitor.wait(0.25):
            report()

    report()
    thread = threading.Thread(target=monitor, name=f"download-progress-{reference.replace('/', '-')}", daemon=True)
    thread.start()
    try:
        snapshot = snapshot_download(reference, cache_dir=MODEL_CACHE_DIR)
    finally:
        stop_monitor.set()
        thread.join(timeout=1.0)
    if progress:
        progress(1.0, total_bytes, total_bytes)
    return snapshot


def _cached_bytes(repository_cache: Path) -> int:
    blobs = repository_cache / "blobs"
    if not blobs.exists():
        return 0
    return sum(path.stat().st_size for path in blobs.iterdir() if path.is_file())
