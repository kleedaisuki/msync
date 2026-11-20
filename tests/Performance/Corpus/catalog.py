"""Workload roles shared by the application corpus and suite entry point."""

from __future__ import annotations

CORE_CASES: tuple[str, ...] = (
    "set-dedup",
    "signal-hysteresis",
    "numeric-record-stream",
    "bounded-sensor-pipeline",
    "live-policy-dispatch",
    "unicode-path-manifest",
)

DIAGNOSTIC_CASES: tuple[str, ...] = (
    "bplus-inventory",
    "heap-scheduler",
    "polygon-queries",
    "band-classification",
    "segment-screening",
    "literal-segment-parity",
    "ordered-calibration",
    "local-manifest-copy",
    "local-range-copy",
)
