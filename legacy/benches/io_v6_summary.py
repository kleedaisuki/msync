"""Summarize paired io_v6 JSONL without treating libtest output as samples."""
from __future__ import annotations

import argparse
import csv
import json
import statistics
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import TextIO, cast


@dataclass(frozen=True)
class Sample:
    size: int
    pair: int
    mechanism: str
    elapsed_us: float
    cpu_us: float


def load(path: Path) -> list[Sample]:
    samples: list[Sample] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        start: int = line.find('{"size":')
        if start < 0:
            continue
        record: dict[str, object] = cast(dict[str, object], json.loads(line[start:]))
        samples.append(Sample(
            size=int(cast(int, record["size"])),
            pair=int(cast(int, record["pair"])),
            mechanism=str(record["mechanism"]),
            elapsed_us=float(cast(float, record["elapsed_us"])),
            cpu_us=float(cast(float, record["cpu_us"])),
        ))
    if not samples:
        raise ValueError(f"no io_v6 samples in {path}")
    return samples


def percentile(values: list[float], fraction: float) -> float:
    ordered: list[float] = sorted(values)
    position: float = (len(ordered) - 1) * fraction
    lower: int = int(position)
    upper: int = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def summarize(samples: list[Sample], output: TextIO) -> None:
    by_size: defaultdict[int, list[Sample]] = defaultdict(list)
    for sample in samples:
        by_size[sample.size].append(sample)
    fields: list[str] = [
        "size_bytes", "pairs", "kernel_p50_us", "buffered_p50_us",
        "kernel_p90_us", "buffered_p90_us", "kernel_cpu_p50_us",
        "buffered_cpu_p50_us", "paired_buffered_over_kernel_p50", "buffered_wins",
    ]
    writer: csv.DictWriter[str] = csv.DictWriter(output, fieldnames=fields)
    writer.writeheader()
    for size, group in sorted(by_size.items()):
        by_pair: defaultdict[int, dict[str, Sample]] = defaultdict(dict)
        for sample in group:
            if sample.mechanism in by_pair[sample.pair]:
                raise ValueError(f"duplicate arm in size={size}, pair={sample.pair}")
            by_pair[sample.pair][sample.mechanism] = sample
        if any(set(pair) != {"kernel", "buffered"} for pair in by_pair.values()):
            raise ValueError(f"unpaired records at size={size}")
        kernel: list[Sample] = [pair["kernel"] for pair in by_pair.values()]
        buffered: list[Sample] = [pair["buffered"] for pair in by_pair.values()]
        ratios: list[float] = [
            pair["buffered"].elapsed_us / pair["kernel"].elapsed_us
            for pair in by_pair.values()
        ]
        row: dict[str, str | int] = {
            "size_bytes": size,
            "pairs": len(by_pair),
            "kernel_p50_us": f"{statistics.median(s.elapsed_us for s in kernel):.3f}",
            "buffered_p50_us": f"{statistics.median(s.elapsed_us for s in buffered):.3f}",
            "kernel_p90_us": f"{percentile([s.elapsed_us for s in kernel], 0.9):.3f}",
            "buffered_p90_us": f"{percentile([s.elapsed_us for s in buffered], 0.9):.3f}",
            "kernel_cpu_p50_us": f"{statistics.median(s.cpu_us for s in kernel):.3f}",
            "buffered_cpu_p50_us": f"{statistics.median(s.cpu_us for s in buffered):.3f}",
            "paired_buffered_over_kernel_p50": f"{statistics.median(ratios):.4f}",
            "buffered_wins": sum(ratio < 1 for ratio in ratios),
        }
        writer.writerow(row)


def summarize_dispatch(before: list[Sample], after: list[Sample], output: TextIO) -> None:
    arms: list[dict[int, list[Sample]]] = []
    for samples in [before, after]:
        groups: defaultdict[int, list[Sample]] = defaultdict(list)
        for sample in samples:
            if sample.mechanism != "dispatch":
                raise ValueError("before/after comparison requires production-dispatch samples")
            groups[sample.size].append(sample)
        arms.append(dict(groups))
    if set(arms[0]) != set(arms[1]):
        raise ValueError("before/after payload sizes do not match")
    fields: list[str] = [
        "size_bytes", "before_samples", "after_samples", "before_p50_us", "after_p50_us",
        "before_p90_us", "after_p90_us", "before_cpu_p50_us", "after_cpu_p50_us",
        "ratio_after_over_before_p50",
    ]
    writer: csv.DictWriter[str] = csv.DictWriter(output, fieldnames=fields)
    writer.writeheader()
    for size in sorted(arms[0]):
        left: list[Sample] = arms[0][size]
        right: list[Sample] = arms[1][size]
        before_p50: float = statistics.median(sample.elapsed_us for sample in left)
        after_p50: float = statistics.median(sample.elapsed_us for sample in right)
        row: dict[str, str | int] = {
            "size_bytes": size,
            "before_samples": len(left),
            "after_samples": len(right),
            "before_p50_us": f"{before_p50:.3f}",
            "after_p50_us": f"{after_p50:.3f}",
            "before_p90_us": f"{percentile([sample.elapsed_us for sample in left], 0.9):.3f}",
            "after_p90_us": f"{percentile([sample.elapsed_us for sample in right], 0.9):.3f}",
            "before_cpu_p50_us": f"{statistics.median(sample.cpu_us for sample in left):.3f}",
            "after_cpu_p50_us": f"{statistics.median(sample.cpu_us for sample in right):.3f}",
            "ratio_after_over_before_p50": f"{after_p50 / before_p50:.4f}",
        }
        writer.writerow(row)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    parser.add_argument("--after", type=Path, help="compare production-dispatch samples against this after log")
    args: argparse.Namespace = parser.parse_args()
    if args.after is not None:
        summarize_dispatch(load(args.log), load(args.after), sys.stdout)
    else:
        summarize(load(args.log), sys.stdout)


if __name__ == "__main__":
    main()
