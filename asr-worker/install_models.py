from __future__ import annotations

import json
import sys

from engines.model_defaults import DEFAULT_ASR_MODEL, DEFAULT_FORCED_ALIGNER_MODEL, MODEL_CACHE_DIR
from engines.model_download import download_model


def emit(kind: str, progress: float, downloaded: int, total: int, message: str) -> None:
    print(
        json.dumps(
            {
                "kind": kind,
                "progress": progress,
                "downloaded_bytes": downloaded,
                "total_bytes": total,
                "message": message,
            },
            ensure_ascii=False,
        ),
        flush=True,
    )


def install(kind: str, reference: str) -> None:
    name = reference.rsplit("/", 1)[-1]
    emit(kind, 0.0, 0, 0, name)
    download_model(reference, lambda value, downloaded, total: emit(kind, value, downloaded, total, name))


def main() -> int:
    MODEL_CACHE_DIR.mkdir(parents=True, exist_ok=True)
    install("asr", DEFAULT_ASR_MODEL)
    install("aligner", DEFAULT_FORCED_ALIGNER_MODEL)
    emit("complete", 1.0, 0, 0, str(MODEL_CACHE_DIR))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(json.dumps({"kind": "error", "message": str(exc)}, ensure_ascii=False), flush=True)
        raise
