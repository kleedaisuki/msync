"""Workload roles shared by the application corpus and suite entry point."""

from __future__ import annotations

CORE_CASES: tuple[str, ...] = (
    "polygon-queries",
    "signal-hysteresis",
    "numeric-record-stream",
    "set-dedup",
    "bplus-inventory",
    "heap-scheduler",
    "bounded-sensor-pipeline",
    "live-policy-dispatch",
    "unicode-path-manifest",
    "local-manifest-copy",
    "local-range-copy",
)

DIAGNOSTIC_CASES: tuple[str, ...] = (
    "band-classification",
    "segment-screening",
    "literal-segment-parity",
    "ordered-calibration",
)
