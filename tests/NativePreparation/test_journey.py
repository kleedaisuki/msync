"""Native contextual preparation through detached CLI artifacts and real files."""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]


def invoke(binary: list[str], cwd: Path, *arguments: str) -> str:
    completed: subprocess.CompletedProcess[str] = subprocess.run(
        [*binary, "--no-config", *arguments, "--color", "never"], cwd=cwd,
        text=True, capture_output=True, timeout=40, check=False,
    )
    assert completed.returncode == 0, (arguments, completed.stdout, completed.stderr)
    return completed.stdout


def bind(binary: list[str], root: Path, source: str, optimization: int) -> tuple[Path, dict[str, Any]]:
    file: Path = root / "program.msync"
    object_file: Path = root / "program.mso"
    executable: Path = root / "program.mexe"
    file.write_text(source, encoding="utf-8")
    invoke(binary, root, "compile", str(file), f"-O{optimization}", "-o", str(object_file))
    invoke(binary, root, "bind-ir", str(object_file), "-o", str(executable))
    data: dict[str, Any] = json.loads(invoke(binary, root, "disassemble", str(executable), "--json"))
    file.unlink()
    object_file.unlink()
    return executable, data


def execute(binary: list[str], root: Path, executable: Path) -> str:
    launch: Path = root / "launch"
    launch.mkdir(exist_ok=True)
    return invoke(binary, launch, "run-ir", str(executable))


def check(binary: list[str], root: Path, optimization: int) -> int:
    missing: str = "./missing-source -> ./out; \"last\" -> ./out;"
    artifact: Path
    data: dict[str, Any]
    artifact, data = bind(binary, root, missing, optimization)
    assert sum(plan["eliminatedTransfers"] for plan in data["executionPlans"]) == 1, data
    execute(binary, root, artifact)
    assert (root / "out").read_text() == "last"

    marker: Path = root / "marker"
    marker.unlink(missing_ok=True)
    artifact, data = bind(binary, root, '''
        if $std::fs::exists(./marker) { "yes" -> ./out; }
        else { "no" -> ./out; };
    ''', optimization)
    assert len(data["executionPlans"]) == 1, data
    marker.write_text("changed after binding", encoding="utf-8")
    execute(binary, root, artifact)
    assert (root / "out").read_text() == "no", "metadata must be frozen, not re-observed"
    marker.unlink()

    source: Path = root / "inputs"
    source.mkdir(exist_ok=True)
    (source / "first.txt").write_text("first", encoding="utf-8")
    (source / "ignore.bin").write_text("ignored", encoding="utf-8")
    artifact, data = bind(binary, root, r'./inputs / <.*\.txt> -> ./selected/;', optimization)
    assert len(data["executionPlans"]) == 1, data
    (source / "later.txt").write_text("later", encoding="utf-8")
    execute(binary, root, artifact)
    assert (root / "selected" / "first.txt").read_text() == "first"
    assert not (root / "selected" / "later.txt").exists(), "selection membership must be frozen"
    (source / "later.txt").unlink()

    artifact, data = bind(binary, root, '''
        $std::fs::touch(./marker);
        if $std::fs::exists(./marker) { "runtime" -> ./out; }
        else { "wrong" -> ./out; };
    ''', optimization)
    assert not marker.exists(), "binding must not perform the opaque write"
    assert not data["executionPlans"], data
    execute(binary, root, artifact)
    assert (root / "out").read_text() == "runtime"
    marker.unlink()

    artifact, data = bind(binary, root, '''
        "published" -> ./marker;
        if $std::fs::exists(./marker) { "ordered" -> ./out; }
        else { "wrong" -> ./out; };
    ''', optimization)
    assert not marker.exists()
    assert len(data["executionPlans"]) == 1, data
    execute(binary, root, artifact)
    assert (root / "out").read_text() == "ordered", "residual metadata must await actual publication"
    marker.unlink()

    artifact, data = bind(binary, root, '''
        fn probe(path: Path) { $std::fs::exists(path) };
        if probe(./marker) { "wrong" -> ./out; } else { "wrapped" -> ./out; };
    ''', optimization)
    assert len(data["executionPlans"]) == 1, data
    marker.write_text("changed", encoding="utf-8")
    execute(binary, root, artifact)
    assert (root / "out").read_text() == "wrapped"
    marker.unlink()

    artifact, data = bind(binary, root, '''
        let counter = 0;
        fn mutate() { counter = counter + 1; true };
        if mutate() { $std::io::println($std::text::format("{}", counter)); };
    ''', optimization)
    output: str = execute(binary, root, artifact)
    assert output.strip() == "1", "preparation must not erase caller-visible mutations"

    artifact, data = bind(binary, root, r'''
        let first = "first" -> ./out;
        "last" -> ./out;
        $std::io::println($std::text::format("{}", first));
    ''', optimization)
    assert sum(plan["eliminatedTransfers"] for plan in data["executionPlans"]) == 0, data
    assert sum(len(plan["transfers"]) for plan in data["executionPlans"]) == 2, data
    assert execute(binary, root, artifact).strip() == "true"
    assert (root / "out").read_text() == "last"

    for name, shared, extra in (("first", "a", "keep"), ("second", "b", "other")):
        tree: Path = root / name / "tree"
        tree.mkdir(parents=True, exist_ok=True)
        (tree / "shared").write_text(shared, encoding="utf-8")
        (tree / extra).write_text(extra, encoding="utf-8")
    artifact, data = bind(binary, root, r'./first/tree -> ./merged/; ./second/tree -> ./merged/;', optimization)
    assert sum(plan["eliminatedTransfers"] for plan in data["executionPlans"]) == 0, data
    execute(binary, root, artifact)
    assert (root / "merged/tree/shared").read_text() == "b"
    assert (root / "merged/tree/keep").read_text() == "keep"
    assert (root / "merged/tree/other").read_text() == "other"

    artifact, data = bind(binary, root, r'''
        for item in (./inputs / <.*\.txt>) { item -> ./iterated/; };
    ''', optimization)
    (source / "later.txt").write_text("later", encoding="utf-8")
    execute(binary, root, artifact)
    assert (root / "iterated/first.txt").read_text() == "first"
    assert not (root / "iterated/later.txt").exists(), "loop iteration freezes only selected metadata, not body effects"
    (source / "later.txt").unlink()

    artifact, data = bind(binary, root, r'''
        fn after_publication(path: Path) { $std::fs::exists(path) };
        "published" -> ./marker;
        if after_publication(./marker) { "wrapped-ordered" -> ./out; }
        else { "wrong" -> ./out; };
    ''', optimization)
    assert not marker.exists()
    execute(binary, root, artifact)
    assert (root / "out").read_text() == "wrapped-ordered", "callee metadata inherits pending publications"
    marker.unlink()

    count: int = 11
    actual: Path = root / "actual"
    actual.mkdir(exist_ok=True)
    alias: Path = root / "alias"
    try:
        alias.symlink_to(actual, target_is_directory=True)
    except OSError:
        if not sys.platform.startswith("win"):
            raise
    else:
        artifact, data = bind(binary, root, r'"first" -> ./alias/out; "last" -> ./actual/out;', optimization)
        assert sum(plan["eliminatedTransfers"] for plan in data["executionPlans"]) == 1, data
        execute(binary, root, artifact)
        assert (actual / "out").read_text() == "last"
        alias.unlink()
        count += 1
        physical: Path = root / "physical-tree"
        physical.mkdir()
        (physical / "survivor").write_text("surviving", encoding="utf-8")
        linked: Path = root / "linked-destination"
        linked.mkdir()
        (linked / "tree").symlink_to(physical, target_is_directory=True)
        artifact, data = bind(binary, root, r'''
            ./first/tree -> ./linked-destination/;
            "final" -> ./physical-tree/shared;
        ''', optimization)
        execute(binary, root, artifact)
        assert (physical / "shared").read_text() == "final", "directory merge footprint follows the destination link"
        assert (physical / "keep").read_text() == "keep"
        assert (physical / "survivor").read_text() == "surviving"
        count += 1
    return count


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: list[str] = [str(arguments.binary.resolve())] if arguments.binary else [
        "dotnet", str(ROOT / "src/Msync.Cli/bin/Release/net10.0/msync.dll")
    ]
    temporary: Path = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="native-preparation-", dir=temporary) as directory:
        root: Path = Path(directory)
        count: int = 0
        for optimization in (0, 2):
            work: Path = root / f"O{optimization}"
            work.mkdir()
            count += check(binary, work, optimization)
    print(f"PASS: {count} native preparation CLI journeys (O0/O2, source-free, changed cwd/state)")


if __name__ == "__main__":
    main()
