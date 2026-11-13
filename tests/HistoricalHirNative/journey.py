"""Verify accepted historical HIR through the single native compiler and VM."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class Fixture:
    name: str
    source: str
    expected: str


FIXTURES: tuple[Fixture, ...] = (
    Fixture(
        "public-collections",
        """let set = $std::containers::Set::new([3, 1, 3]);
$std::io::println($std::containers::Set::size(set) as String);
let heap = $std::containers::Heap::new(Number);
$std::containers::Heap::push(heap, 3);
$std::containers::Heap::push(heap, 1);
let item? = $std::containers::Heap::pop(heap);
if item is not null { $std::io::println(item as String); };
""",
        "2\n1\n",
    ),
    Fixture(
        "vector-membership",
        """let owner = $std::storage::vector_new([1, 2]);
let alias = owner;
let saved = $std::storage::vector_snapshot(owner);
$std::storage::vector_set(alias, 0, 7);
$std::io::println($std::storage::vector_get(owner, 0) as String);
$std::io::println($std::ranges::front(saved) as String);
$std::storage::vector_insert(owner, 1, 9);
$std::io::println($std::storage::vector_size(owner) as String);
$std::io::println($std::storage::vector_remove(owner, 1) as String);
$std::io::println($std::storage::vector_pop(owner) as String);
""",
        "7\n1\n3\n9\n2\n",
    ),
    Fixture(
        "scope-aliases",
        """let row = scope { number: 1 };
let owner = $std::storage::vector_new([row]);
let saved = $std::storage::vector_snapshot(owner);
row::number = 9;
let found = $std::ranges::front(saved);
$std::io::println(found::number as String);
let extracted = $std::storage::vector_take(owner);
$std::io::println($std::storage::vector_size(owner) as String);
let retained = $std::ranges::front(extracted);
retained::number = 11;
$std::io::println(row::number as String);
""",
        "9\n0\n11\n",
    ),
    Fixture(
        "first-class-binding",
        """let make = $std::storage::vector_new;
let get = $std::storage::vector_get;
let owner = make(["Klee", "原生"]);
$std::io::println(get(owner, 1) as String);
$std::io::println((owner is decltype(owner)) as String);
""",
        "原生\ntrue\n",
    ),
    Fixture(
        "slot-reuse",
        """let owner = $std::slots::new(Number);
let first = $std::slots::allocate(owner, 4);
$std::slots::release(owner, first);
let second = $std::slots::allocate(owner, 8);
$std::io::println((first == second) as String);
$std::io::println($std::slots::get(owner, second) as String);
$std::io::println($std::slots::count(owner) as String);
""",
        "true\n8\n1\n",
    ),
    Fixture(
        "logical-range-type",
        """let owner = $std::storage::vector_new([1]);
let owners = $std::ranges::materialize([owner]);
let input? = $std::io::read_line();
let expected = if input is null { decltype(owners) } else { Range<Number> };
$std::io::println((owners is expected) as String);
$std::io::println($std::meta::type_name(decltype(owner)));
""",
        "true\nStorage<Number>\n",
    ),
    Fixture(
        "arena-links",
        """let owner = $std::arena::new(Number);
let root = $std::arena::allocate(owner, 5, 0);
let child = $std::arena::allocate(owner, 2, root);
$std::arena::set_root(owner, root);
$std::arena::set_child(owner, root, true, child);
$std::io::println($std::arena::key(owner, $std::arena::root(owner)) as String);
$std::io::println($std::arena::key(owner, $std::arena::child(owner, root, true)) as String);
$std::io::println(($std::arena::parent(owner, child) == root) as String);
""",
        "5\n2\ntrue\n",
    ),
    Fixture(
        "logical-reflection",
        """let owner = $std::storage::vector_new([1]);
$std::io::println($std::meta::member_count(owner) as String);
$std::io::println($std::meta::has_member(owner, "value") as String);
let holder = scope { owner: owner };
let query = $std::meta::member_type;
let kind? = query(holder, "owner");
if kind is not null { $std::io::println($std::meta::type_name(kind)); };
let fields = $std::meta::members_of(holder);
let field = $std::ranges::front(fields);
$std::io::println($std::meta::type_name(field::storage_type));
$std::io::println($std::meta::type_name(field::type));
""",
        "0\nfalse\nStorage<Number>\nStorage<Number>\nStorage<Number>\n",
    ),
    Fixture(
        "dynamic-logical-reflection",
        """let owner = $std::storage::vector_new([1]);
let input? = $std::io::read_line();
let value = if input is null { owner } else { scope { ordinary: 0 } };
$std::io::println($std::meta::member_count(value) as String);
let holder = scope { value: value };
let kind? = $std::meta::member_type(holder, "value");
if kind is not null { $std::io::println($std::meta::type_name(kind)); };
""",
        "0\nScope | Storage\n",
    ),
    Fixture(
        "dynamic-nested-members",
        """let owner = $std::storage::vector_new([1]);
let input? = $std::io::read_line();
let value = if input is null { scope { owner: owner } } else { owner };
for field in $std::meta::members_of(value) {
    $std::io::println(field::name);
    $std::io::println($std::meta::type_name(field::type));
    $std::io::println($std::meta::type_name(field::storage_type));
};
""",
        "",
    ),
    Fixture(
        "empty-owner-range",
        """let owner = $std::storage::vector_new([1]);
let empty = $std::ranges::empty_of(decltype(owner));
$std::io::println($std::ranges::size(empty) as String);
$std::io::println($std::meta::type_name(decltype(empty)));
""",
        "0\nRange<Storage<Number>>\n",
    ),
    Fixture(
        "builtin-carriers",
        """let truth = $std::storage::vector_new([true]);
$std::io::println($std::storage::vector_get(truth, 0) as String);
let paths = $std::storage::vector_new([./payload]);
$std::io::println($std::fs::filename($std::storage::vector_get(paths, 0)) as String);
let connections = $std::storage::vector_new([$local]);
$std::io::println(($std::storage::vector_get(connections, 0) is Connection) as String);
let callbacks = $std::storage::vector_new([fn(n: Number) -> Number { n + 1 }]);
let callback = $std::storage::vector_get(callbacks, 0);
$std::io::println(callback(7) as String);
let nested = $std::storage::vector_new([[1, 2]]);
$std::io::println($std::ranges::size($std::storage::vector_get(nested, 0)) as String);
let types = $std::storage::vector_new([Number]);
$std::io::println($std::meta::is_same($std::storage::vector_get(types, 0), Number) as String);
let empty = $std::storage::vector_new([null]);
$std::io::println(($std::storage::vector_get(empty, 0) is null) as String);
""",
        "true\npayload\ntrue\n8\n2\ntrue\ntrue\n",
    ),
)


def command(binary: Path, arguments: list[str]) -> list[str]:
    prefix: list[str] = (
        ["dotnet", str(binary)] if binary.suffix == ".dll" else [str(binary)]
    )
    return [*prefix, "--no-config", "--no-telemetry", "--color", "never", *arguments]


def image_identity(binary: Path) -> dict[str, str]:
    paths: list[Path] = [binary]
    for path in binary.parent.iterdir():
        if path.is_file() and (
            path.suffix in (".dll", ".so", ".dylib")
            or path.name.endswith((".deps.json", ".runtimeconfig.json"))
        ):
            paths.append(path)
    runtimes: Path = binary.parent / "runtimes"
    if runtimes.is_dir():
        paths.extend(path for path in runtimes.rglob("*") if path.is_file())
    return {
        str(path.relative_to(binary.parent)): hashlib.sha256(
            path.read_bytes()
        ).hexdigest()
        for path in sorted(set(paths))
    }


def invoke(binary: Path, directory: Path, arguments: list[str], log: Path) -> bytes:
    completed: subprocess.CompletedProcess[bytes] = subprocess.run(
        command(binary, arguments),
        cwd=directory,
        input=b"",
        capture_output=True,
        timeout=120,
        check=False,
    )
    log.with_name(log.name + ".stdout").write_bytes(completed.stdout)
    log.with_name(log.name + ".stderr").write_bytes(completed.stderr)
    if completed.returncode != 0:
        raise AssertionError(
            f"{arguments}: exit {completed.returncode}: {completed.stderr.decode(errors='replace')}"
        )
    return completed.stdout


def check_ir(data: bytes) -> None:
    document: dict[str, Any] = json.loads(data)
    if document.get("representation") != "native-v1":
        raise AssertionError("Historical HIR did not lower to native IR")
    for kind in document.get("types", []):
        if "Storage" in str(kind.get("kind", "")):
            raise AssertionError("Historical Storage became a physical native type")
    for function in document.get("functions", []):
        for instruction in function.get("instructions", []):
            if instruction.get("op") == "CallNative" and str(
                instruction.get("symbol", "")
            ).startswith(("storage.", "arena.", "slots.")):
                raise AssertionError(
                    "Historical allocator escaped into a runtime capability"
                )


def text_end(data: bytes, offset: int) -> int:
    length: int = 0
    for shift in range(0, 35, 7):
        if offset >= len(data):
            raise ValueError("Truncated historical HIR string length")
        value: int = data[offset]
        offset += 1
        length |= (value & 127) << shift
        if value < 128:
            end: int = offset + length
            if end > len(data):
                raise ValueError("Truncated historical HIR string")
            return end
    raise ValueError("Invalid historical HIR string length")


def strip_debug_sources(data: bytes) -> bytes:
    if data[:10] != b"MSYNHIR\x00\x03\x00":
        raise ValueError("Expected historical HIR format 3")
    offset: int = text_end(data, 10) + 5
    count: int = int.from_bytes(data[offset : offset + 4], "little")
    offset += 4
    output: bytearray = bytearray(data[:offset])
    for _ in range(count):
        end: int = text_end(data, offset)
        output.extend(data[offset:end])
        offset = end
        present: int = data[offset]
        offset += 1
        if present not in (0, 1):
            raise ValueError("Invalid historical HIR source tag")
        if present:
            offset = text_end(data, offset)
        output.append(0)
    output.extend(data[offset:])
    return bytes(output)


def run_fixture(
    frozen: Path, native: Path, root: Path, fixture: Fixture
) -> dict[str, Any]:
    directory: Path = root / fixture.name
    directory.mkdir()
    source: Path = directory / "historical.msync"
    source.write_text(fixture.source, encoding="utf-8")
    expected: bytes = fixture.expected.encode()
    actual: bytes = invoke(
        frozen, directory, ["run", source.name], directory / "frozen-run"
    )
    if actual != expected:
        raise AssertionError(
            f"{fixture.name}: frozen producer disagrees with independent oracle: {actual!r}"
        )
    invoke(
        frozen,
        directory,
        ["compile", source.name, "--emit", "hir", "-o", "historical.mhir"],
        directory / "frozen-export",
    )
    source.unlink()
    artifact: Path = directory / "historical.mhir"
    original: str = hashlib.sha256(artifact.read_bytes()).hexdigest()
    stripped: Path = directory / "stripped.mhir"
    stripped.write_bytes(strip_debug_sources(artifact.read_bytes()))
    for level in (0, 2):
        portable: str = f"native-o{level}.mso"
        executable: str = f"native-o{level}.mexe"
        invoke(
            native,
            directory,
            ["compile", artifact.name, f"-O{level}", "-o", portable],
            directory / f"compile-{level}",
        )
        stripped_portable: str = f"stripped-o{level}.mso"
        invoke(
            native,
            directory,
            ["compile", stripped.name, f"-O{level}", "-o", stripped_portable],
            directory / f"compile-stripped-{level}",
        )
        if (directory / portable).read_bytes() != (
            directory / stripped_portable
        ).read_bytes():
            raise AssertionError(
                "Diagnostic source text changed native compilation output"
            )
        check_ir(
            invoke(
                native,
                directory,
                ["disassemble", portable, "--json"],
                directory / f"inspect-{level}",
            )
        )
        invoke(
            native,
            directory,
            ["bind-ir", portable, "-o", executable],
            directory / f"bind-{level}",
        )
        for route in (artifact.name, stripped.name, portable, executable):
            actual = invoke(
                native,
                directory,
                ["run", route, f"-O{level}"],
                directory / f"run-{level}-{Path(route).name}",
            )
            if actual != expected:
                raise AssertionError(
                    f"{fixture.name} O{level}/{route}: {actual!r} != {expected!r}"
                )
    if hashlib.sha256(artifact.read_bytes()).hexdigest() != original:
        raise AssertionError("Historical HIR bytes changed during native compilation")
    print(
        f"PASS {fixture.name}: frozen/stripped HIR, byte-identical native compilation, O0/O2 HIR/object/executable",
        flush=True,
    )
    return {
        "name": fixture.name,
        "hir_sha256": original,
        "levels": [0, 2],
        "routes": 4,
        "status": "complete",
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frozen-cli", type=Path, required=True)
    parser.add_argument("--native-cli", type=Path, required=True)
    parser.add_argument(
        "--case", choices=[fixture.name for fixture in FIXTURES], action="append"
    )
    options: argparse.Namespace = parser.parse_args()
    root: Path = ROOT / ".temp/historical-hir-native" / uuid.uuid4().hex
    root.mkdir(parents=True)
    print(root, flush=True)
    frozen: Path = options.frozen_cli.resolve(strict=True)
    native: Path = options.native_cli.resolve(strict=True)
    report: dict[str, Any] = {
        "status": "running",
        "frozen_cli": str(frozen),
        "native_cli": str(native),
        "frozen_sha256": hashlib.sha256(frozen.read_bytes()).hexdigest(),
        "native_sha256": hashlib.sha256(native.read_bytes()).hexdigest(),
        "frozen_payload": image_identity(frozen),
        "native_payload": image_identity(native),
        "fixtures": [],
    }
    try:
        for fixture in FIXTURES:
            if options.case is None or fixture.name in options.case:
                report["active"] = fixture.name
                report["fixtures"].append(run_fixture(frozen, native, root, fixture))
                (root / "result.json").write_text(
                    json.dumps(report, indent=2) + "\n", encoding="utf-8"
                )
        report["active"] = None
        if (
            image_identity(frozen) != report["frozen_payload"]
            or image_identity(native) != report["native_payload"]
        ):
            raise AssertionError("A producer/runtime payload changed during validation")
        report["status"] = "complete"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = str(error)
        raise
    finally:
        (root / "result.json").write_text(
            json.dumps(report, indent=2) + "\n", encoding="utf-8"
        )


if __name__ == "__main__":
    main()
