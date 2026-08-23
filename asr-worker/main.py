from __future__ import annotations

import asyncio
import json
import logging
import os
import sys

# SciPy/OpenBLAS can deadlock in its Windows DLL initializer when it creates a
# large native thread pool while PyTorch is being imported. ASR inference runs
# on CUDA, so one OpenBLAS thread is sufficient for the worker's CPU helpers.
os.environ["OPENBLAS_NUM_THREADS"] = "1"

from worker import AsrWorker


logging.basicConfig(stream=sys.stderr, level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")


async def main() -> int:
    worker = AsrWorker()
    loop = asyncio.get_running_loop()
    while not worker.shutdown_requested:
        line = await loop.run_in_executor(None, sys.stdin.readline)
        if not line:
            break
        try:
            request = json.loads(line)
            if not isinstance(request, dict):
                raise ValueError("request must be a JSON object")
            await worker.dispatch(request)
        except json.JSONDecodeError as exc:
            worker.emit({"id": None, "event": "error", "code": "PROTOCOL_ERROR", "message": f"Invalid JSON: {exc.msg}"})
        except Exception as exc:  # the worker must survive malformed individual requests
            logging.exception("Unhandled request dispatch failure")
            worker.emit({"id": None, "event": "error", "code": "ASR_ERROR", "message": str(exc)})
    await worker.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
