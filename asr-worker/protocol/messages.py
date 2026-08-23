from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any


@dataclass(slots=True)
class WordTimestamp:
    start_us: int
    end_us: int
    text: str


@dataclass(slots=True)
class SubtitleSegment:
    start_us: int
    end_us: int
    text: str
    confidence: float | None = None
    words: list[WordTimestamp] | None = None

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def require_string(request: dict[str, Any], name: str, *, allow_empty: bool = False) -> str:
    value = request.get(name)
    if not isinstance(value, str) or (not allow_empty and not value.strip()):
        raise ValueError(f"'{name}' must be a non-empty string")
    return value
