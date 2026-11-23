"""Replay the pre-HPIR O2 pass-cost experiment on its frozen source."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import resource
import statistics
import subprocess
import tarfile
import time
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
BASELINE: str = "90857bf3e40f28b4d99014aa06bf2609fbfdd6d6"

PROBE: str = r'''// Development-only overlay; never included in the product checkout.
using System.Diagnostics;
using System.Globalization;
namespace Msync.Core;
public static class CompilerCostProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("MSYNC_COST_RECORD") == "1";
    public static readonly bool Visits = Environment.GetEnvironmentVariable("MSYNC_COST_VISITS") == "1";
    public static readonly bool Simplify = Environment.GetEnvironmentVariable("MSYNC_COST_COMBINE") == "simplify";
    public static readonly bool SkipDse = Environment.GetEnvironmentVariable("MSYNC_COST_DSE") == "off";
    private static readonly Dictionary<(string Phase, string Function), (long Ns, long Bytes, long Calls)> rows = [];
    public static T Measure<T>(string phase, string function, Func<T> action)
    {
        if (!Enabled) return action();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        T result = action();
        long ns = (long)Stopwatch.GetElapsedTime(start).TotalNanoseconds;
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        var key = (phase, function);
        var previous = rows.GetValueOrDefault(key);
        rows[key] = (previous.Ns + ns, previous.Bytes + bytes, previous.Calls + 1);
        return result;
    }
    public static void Flush()
    {
        if (!Enabled) return;
        foreach (var (key, value) in rows)
            Console.Error.WriteLine("MSYNC_COST\t" + key.Phase + "\t" + key.Function.Replace('\t', ' ')
                + "\t" + value.Ns.ToString(CultureInfo.InvariantCulture)
                + "\t" + value.Bytes.ToString(CultureInfo.InvariantCulture)
                + "\t" + value.Calls.ToString(CultureInfo.InvariantCulture));
        rows.Clear();
    }
}
'''


def replace_once(text: str, before: str, after: str) -> str:
    if text.count(before) != 1:
        raise RuntimeError(f"Overlay source seam changed: {before[:100]}")
    return text.replace(before, after, 1)


def prepare(work: Path) -> None:
    checkout: Path = work / "checkout"
    if checkout.exists():
        raise RuntimeError(f"Use a new work directory: {checkout}")
    checkout.mkdir(parents=True)
    archive: bytes = subprocess.check_output(
        ["git", "archive", BASELINE, "src", "vendor", "stdlib", "Directory.Build.props", "global.json"], cwd=ROOT
    )
    with tarfile.open(fileobj=io.BytesIO(archive)) as stream:
        stream.extractall(checkout, filter="data")
    (checkout / "src/Msync.Core/Diagnostics/CompilerCostProbe.cs").write_text(PROBE)
    path: Path = checkout / "src/Msync.Compiler/Compiler.Llvm.cs"
    source: str = path.read_text()
    begin: int = source.index("    private static void OptimizeLlvmFunction(")
    end: int = source.index("    private readonly record struct LlvmLoopChanges", begin)
    body: str = source[begin:end]
    phases: dict[str, str] = {
        "dom.front": "new LlvmDominatorTree(function, trace)",
        "sroa": "LlvmSroa.Run(function, tree, assumptions,\n            new() { PreserveCFG = false }, trace)",
        "early-cse": "LlvmEarlyCse.Run(function, true, trace, tree)",
        "loop-info": "new LlvmLoopInfo(function, tree, trace)",
        "loop-simplify": "LlvmLoopSimplify.Run(function, tree, loops, trace: trace)",
        "lcssa": "LlvmLcssa.Run(function, tree, loops, trace)",
        "loop-motion": "OptimizeLlvmLoops(function, tree, loops, trace)",
        "sccp": "LlvmSccp.Run(function, trace)",
        "dce": "LlvmDeadCodeElimination.Run(function, trace)",
        "dom.cleanup": "new LlvmDominatorTree(function, trace)",
        "combine": "OptimizeLlvmCombine(function, cleanupTree, trace)",
        "postdom": "new LlvmDominatorTree(function, trace, isPostDominator: true)",
        "adce": "LlvmAdce.Run(function, postDominators, cleanupTree, trace: trace)",
        "memory-cleanup": "OptimizeLlvmStores(function, cleanupTree, postDominators, trace)",
        "simplifycfg": "LlvmSimplifyCfgPass.Run(function, new(function.Parent!.DataLayout),\n            options: new() { ConvertSwitchRangeToICmp = true, ConvertSwitchToArithmetic = true,\n                HoistCommonInsts = true, SinkCommonInsts = true }, trace: trace)",
    }
    for phase, expression in phases.items():
        # The same expression builds two distinct forward trees.
        count: int = body.count(expression)
        if phase == "dom.front" and count == 2:
            body = body.replace(expression, f'CompilerCostProbe.Measure("{phase}", function.Name, () => {expression})', 1)
        elif phase == "dom.cleanup":
            before: str = "var cleanupTree = " + expression
            body = replace_once(body, before, 'var cleanupTree = CompilerCostProbe.Measure("dom.cleanup", function.Name, () => ' + expression + ')')
        else:
            body = replace_once(body, expression, f'CompilerCostProbe.Measure("{phase}", function.Name, () => {expression})')
    source = source[:begin] + body + source[end:]
    source = replace_once(source,
        "        var alias = new LlvmAAResults(function, tree: tree, metrics: metrics);\n        using var assumptions = new LlvmAssumptionCache(function, trace);",
        "        if (CompilerCostProbe.Simplify) return LlvmInstSimplifyPass.Run(function, trace);\n        var alias = new LlvmAAResults(function, tree: tree, metrics: metrics);\n        using var assumptions = new LlvmAssumptionCache(function, trace);")
    source = replace_once(source, "        var cycles = new LlvmCycleInfo(function, trace, metrics);",
        "        if (CompilerCostProbe.SkipDse) return false;\n        var cycles = new LlvmCycleInfo(function, trace, metrics);")
    path.write_text(source)
    path = checkout / "src/Msync.Compiler/Compiler.cs"
    source = path.read_text()
    source = replace_once(source, "trace is null ? LlvmHirLowerer.Lower(module)",
        'trace is null ? CompilerCostProbe.Measure("pipeline.lower", "<module>", () => LlvmHirLowerer.Lower(module))')
    source = replace_once(source, "        OptimizeLlvm(graph, optimization, trace);",
        '        CompilerCostProbe.Measure("pipeline.optimize", "<module>", () => OptimizeLlvm(graph, optimization, trace));')
    source = replace_once(source, "trace is null ? LlvmNativeLowerer.Lower(graph)",
        'trace is null ? CompilerCostProbe.Measure("pipeline.target", "<module>", () => LlvmNativeLowerer.Lower(graph))')
    source = replace_once(source, "        return program with { OptimizationLevel = optimization };",
        "        CompilerCostProbe.Flush();\n        return program with { OptimizationLevel = optimization };")
    path.write_text(source)
    path = checkout / "src/Msync.Optimization/Llvm/LlvmInstCombine.Run.cs"
    source = replace_once(path.read_text(), "            if (Trace is null) return Visit(instruction);",
        '            if (Trace is null) return global::Msync.Core.CompilerCostProbe.Visits\n                ? global::Msync.Core.CompilerCostProbe.Measure("visit." + instruction.Opcode, F.Name, () => Visit(instruction))\n                : Visit(instruction);')
    path.write_text(source)
    receipt: dict[str, Any] = {"revision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "purpose": "Diagnostic-only source overlay; real production pipeline/calls, no shipped flags or fallbacks.",
        "archive_sha256": hashlib.sha256(archive).hexdigest(),
        "overlay_files": {str(p.relative_to(checkout)): hashlib.sha256(p.read_bytes()).hexdigest() for p in (
            checkout / "src/Msync.Core/Diagnostics/CompilerCostProbe.cs",
            checkout / "src/Msync.Compiler/Compiler.Llvm.cs", checkout / "src/Msync.Compiler/Compiler.cs",
            checkout / "src/Msync.Optimization/Llvm/LlvmInstCombine.Run.cs")}}
    (work / "overlay.json").write_text(json.dumps(receipt, indent=2) + "\n")


def observe(binary: Path, source: Path, output: Path, combine: str, dse: str, record: bool, visits: bool) -> dict[str, Any]:
    env: dict[str, str] = {**os.environ, "MSYNC_COST_COMBINE": combine, "MSYNC_COST_DSE": dse,
        "MSYNC_COST_RECORD": "1" if record else "0", "MSYNC_COST_VISITS": "1" if visits else "0"}
    cpu_before: resource.struct_rusage = resource.getrusage(resource.RUSAGE_CHILDREN)
    start: int = time.perf_counter_ns()
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", "compile", "-O2", str(source), "-o", str(output)],
        cwd=ROOT, env=env, capture_output=True, text=True, check=False)
    elapsed: float = (time.perf_counter_ns() - start) / 1e6
    cpu_after: resource.struct_rusage = resource.getrusage(resource.RUSAGE_CHILDREN)
    cpu_ms: float = (cpu_after.ru_utime + cpu_after.ru_stime - cpu_before.ru_utime - cpu_before.ru_stime) * 1000
    rows: list[dict[str, Any]] = []
    for line in result.stderr.splitlines():
        if not line.startswith("MSYNC_COST\t"):
            if result.returncode == 0: raise RuntimeError(line)
            continue
        _, phase, function, ns, allocated, calls = line.split("\t")
        rows.append({"phase": phase, "function": function, "nanoseconds": int(ns), "allocated_bytes": int(allocated), "calls": int(calls)})
    return {"status": "passed" if result.returncode == 0 else "failed", "returncode": result.returncode,
        "milliseconds": elapsed, "cpu_milliseconds": cpu_ms, "phases": rows, "stderr": result.stderr if result.returncode else None,
        "artifact_sha256": hashlib.sha256(output.read_bytes()).hexdigest() if result.returncode == 0 else None}


def measure(work: Path, source: Path, pairs: int) -> None:
    binary: Path = work / "image/msync"
    modes: list[tuple[str, str]] = [("full", "on"), ("simplify", "on"), ("full", "off"), ("simplify", "off")]
    samples: dict[str, list[dict[str, Any]]] = {f"{a}/{b}": [] for a, b in modes}
    for combine, dse in modes:
        observe(binary, source, work / "output.mso", combine, dse, False, False)
    orders: list[list[str]] = []
    for index in range(pairs):
        order: list[tuple[str, str]] = [modes[(slot + index) % 4] for slot in (0, 1, 3, 2)]
        orders.append([f"{a}/{b}" for a, b in order])
        for combine, dse in order:
            samples[f"{combine}/{dse}"].append(observe(binary, source, work / "output.mso", combine, dse, False, False))
    (work / "timing-checkpoint.json").write_text(json.dumps({"samples": samples, "orders": orders}, indent=2) + "\n")
    profiles: dict[str, list[dict[str, Any]]] = {f"{a}/{b}": [] for a, b in modes}
    for repeat in range(3):
        for a, b in modes[repeat:] + modes[:repeat]:
            profiles[f"{a}/{b}"].append(observe(binary, source, work / "output.mso", a, b, True, False))
    visits: dict[str, Any] = observe(binary, source, work / "output.mso", "full", "on", True, True)
    report: dict[str, Any] = {"source": str(source), "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(), "pairs": pairs,
        "samples": samples, "orders": orders, "phase_profiles": profiles, "visitor_profile": visits,
        "milliseconds_p50": {key: statistics.median(float(s['milliseconds']) for s in values if s["status"] == "passed") for key, values in samples.items()},
        "timing_failures": {key: sum(s["status"] != "passed" for s in values) for key, values in samples.items()},
        "boundary": "Same diagnostic Native AOT image; all ordinary telemetry disabled. Ablations are local experiments, not production settings. Phase-only/visitor profiles are separate from timing samples."}
    (work / "result.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report['milliseconds_p50']))


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "measure"))
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--source", type=Path)
    parser.add_argument("--pairs", type=int, default=7)
    args: argparse.Namespace = parser.parse_args()
    work: Path = args.work.resolve()
    if not work.is_relative_to(ROOT / ".temp"):
        parser.error("--work must be inside repository .temp")
    work.mkdir(parents=True, exist_ok=True)
    if args.action == "prepare": prepare(work)
    else:
        if args.source is None: parser.error("measure requires --source")
        measure(work, args.source.resolve(), args.pairs)


if __name__ == "__main__":
    main()
