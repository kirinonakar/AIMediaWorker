from __future__ import annotations

import json
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

    repository_cache = MODEL_CACHE_DIR / f"models--{reference.replace('/', '--')}"
    cached_snapshot = _find_complete_snapshot(repository_cache)
    if cached_snapshot is not None:
        cached_bytes = _cached_bytes(repository_cache)
        if progress:
            progress(1.0, cached_bytes, cached_bytes)
        return str(cached_snapshot)

    try:
        from huggingface_hub import HfApi, snapshot_download
    except ImportError:
        return reference

    info = HfApi().model_info(reference, files_metadata=True)
    total_bytes = sum(int(sibling.size or 0) for sibling in info.siblings)
    stop_monitor = threading.Event()

    def report() -> None:
        if not progress:
            return
        downloaded = _cached_bytes(repository_cache)
        fraction = min(0.99, downloaded / total_bytes) if total_bytes > 0 else 0.0
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


def _find_complete_snapshot(repository_cache: Path) -> Path | None:
    blobs = repository_cache / "blobs"
    if not blobs.is_dir() or any(blobs.glob("*.incomplete")):
        return None
    candidates: list[Path] = []
    main_ref = repository_cache / "refs" / "main"
    if main_ref.is_file():
        revision = main_ref.read_text(encoding="utf-8").strip()
        if revision:
            candidates.append(repository_cache / "snapshots" / revision)
    snapshots = repository_cache / "snapshots"
    if snapshots.is_dir():
        candidates.extend(sorted((path for path in snapshots.iterdir() if path.is_dir()), key=lambda path: path.stat().st_mtime, reverse=True))
    for snapshot in dict.fromkeys(candidates):
        if _snapshot_has_complete_weights(snapshot):
            return snapshot
    return None


def _snapshot_has_complete_weights(snapshot: Path) -> bool:
    if not (snapshot / "config.json").is_file():
        return False
    indexes = list(snapshot.glob("*.safetensors.index.json"))
    if indexes:
        try:
            for index in indexes:
                weight_map = json.loads(index.read_text(encoding="utf-8")).get("weight_map", {})
                shards = {str(name) for name in weight_map.values()}
                if not shards or not all((snapshot / name).is_file() for name in shards):
                    return False
            return True
        except (OSError, UnicodeError, json.JSONDecodeError, AttributeError):
            return False
    return any(path.is_file() for pattern in ("*.safetensors", "pytorch_model*.bin") for path in snapshot.glob(pattern))


def _cached_bytes(repository_cache: Path) -> int:
    blobs = repository_cache / "blobs"
    if not blobs.exists():
        return 0
    return sum(path.stat().st_size for path in blobs.iterdir() if path.is_file())
