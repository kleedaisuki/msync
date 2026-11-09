#!/usr/bin/env python3
"""Exercise source-composed services and collections through the actual CLI."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import statistics
import subprocess
import sys
import tempfile
import time
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class Example:
    name: str
    source: str
    outputs: tuple[tuple[str, bytes], ...]
    inputs: tuple[tuple[str, bytes], ...] = ()


EXAMPLES: tuple[Example, ...] = (
    Example(
        "collection-alias-snapshot",
        r"""
let list = $std::collections::List([" alpha ", "β"]);
let alias = list;
let before = list::snapshot();
alias::add(" gamma ");
let independent = $std::collections::List(["other"]);
independent::add("object");
let normalized = $std::ranges::transform(list::snapshot(), $std::string::trim);
let result = $std::files::File(./list.txt);
result::write($std::string::join(normalized, "|"));
$std::string::join(before, "|") -> ./before.txt;
$std::string::join(independent::snapshot(), "|") -> ./independent.txt;
let removed = alias::remove_where(fn(value: String) {
    $std::string::contains(value, "alpha")
});
(removed as String) -> ./removed.txt;
$std::string::join(list::snapshot(), "|") -> ./after.txt;
""",
        (
            ("list.txt", "alpha|β|gamma".encode()),
            ("before.txt", " alpha |β".encode()),
            ("independent.txt", b"other|object"),
            ("removed.txt", b"1"),
            ("after.txt", "β| gamma ".encode()),
        ),
    ),
    Example(
        "map-set-custom-equality",
        r"""
let equal = fn(left: String, right: String) {
    $std::string::to_lowercase(left) == $std::string::to_lowercase(right)
};
let set = $std::collections::SetWith(["Alpha", "ALPHA", "Beta"], equal);
set::add("beta");
set::add("Gamma");
$std::string::join(set::snapshot(), "|") -> ./set.txt;
let map = $std::collections::MapWith([
    scope { key: "alpha", value: 1 },
    scope { key: "beta", value: 2 }
], equal);
let old_entries = map::entries();
map::put("ALPHA", 9);
map::put("gamma", 3);
(map::get_or("alpha", 0) as String) -> ./value.txt;
$std::string::join(map::keys(), "|") -> ./keys.txt;
($std::ranges::sum($std::ranges::transform(old_entries,
    fn(entry) { entry::value })) as String) -> ./old-sum.txt;
""",
        (
            ("set.txt", b"Alpha|Beta|Gamma"),
            ("value.txt", b"9"),
            ("keys.txt", b"alpha|beta|gamma"),
            ("old-sum.txt", b"3"),
        ),
    ),
    Example(
        "unicode-builder-regex",
        r"""
let builder = $std::text::Builder();
let alias = builder;
alias::append("A🙂");
builder::append("β");
let independent = $std::text::Builder();
independent::append("X");
builder::build() -> ./text.txt;
(builder::length() as String) -> ./length.txt;
independent::build() -> ./other.txt;
let pattern = $std::regex::compile("(?<letter>β)(x)?");
let matches = pattern::matches(builder::build());
let selected_match = $std::ranges::first(matches);
if selected_match is not null {
    selected_match::value -> ./match.txt;
    (selected_match::index as String) -> ./index.txt;
    (selected_match::length as String) -> ./match-length.txt;
    let selected = $std::ranges::filter(selected_match::groups, fn(group) {
        group::name == "letter"
    });
    $std::string::join($std::ranges::transform(selected,
        fn(group) { group::value }), "|") -> ./group.txt;
};
pattern::replace(builder::build(), "[${letter}]") -> ./replaced.txt;
builder::clear();
builder::append_all(["new", "value"]);
builder::build() -> ./cleared.txt;
""",
        (
            ("text.txt", "A🙂β".encode()),
            ("length.txt", b"3"),
            ("other.txt", b"X"),
            ("match.txt", "β".encode()),
            ("index.txt", b"2"),
            ("match-length.txt", b"1"),
            ("group.txt", "β".encode()),
            ("replaced.txt", "A🙂[β]".encode()),
            ("cleared.txt", b"newvalue"),
        ),
    ),
    Example(
        "environment-snapshot-and-narrowing",
        r"""
let name = "MSYNC_STDLIB_UPGRADE_TEST_VALUE";
$std::environment::unset(name);
$std::environment::get_or(name, "absent") -> ./absent.txt;
$std::environment::set(name, "old🙂");
let snapshot = $std::environment::variables();
$std::environment::set(name, "newβ");
let old = $std::ranges::filter(snapshot, fn(variable) {
    variable::name == name
});
$std::string::join($std::ranges::transform(old,
    fn(variable) { variable::value }), "|") -> ./snapshot.txt;
let value? = $std::environment::get(name);
if value is not null {
    value -> ./selected.txt;
};
$std::environment::unset(name);
$std::environment::get_or(name, "removed") -> ./removed.txt;
""",
        (
            ("absent.txt", b"absent"),
            ("snapshot.txt", "old🙂".encode()),
            ("selected.txt", "newβ".encode()),
            ("removed.txt", b"removed"),
        ),
    ),
    Example(
        "directory-file-and-command",
        r"""
let directory = $std::files::Directory(./data);
directory::create();
let file = directory::file("payload.txt");
file::write("before");
file::write("after");
file::append("🙂");
let command = $std::commands::Command(__COMMAND_EXECUTABLE__, __COMMAND_ARGS__);
let result = command::capture();
result::write_to(./stdout.txt);
result::error -> ./stderr.txt;
(result::code as String) -> ./exit-code.txt;
file::read_text() -> ./file-result.txt;
($std::ranges::size(directory::snapshot()) as String) -> ./entry-count.txt;
""",
        (
            ("data/payload.txt", "after🙂".encode()),
            ("stdout.txt", b"HELLO"),
            ("stderr.txt", b"WARN"),
            ("exit-code.txt", b"7"),
            ("file-result.txt", "after🙂".encode()),
            ("entry-count.txt", b"1"),
        ),
    ),
)


def command(binary: Path) -> list[str]:
    return ["dotnet", str(binary)] if binary.suffix == ".dll" else [str(binary)]


def invoke(
    binary: Path,
    directory: Path,
    arguments: Sequence[str],
    environment: Mapping[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    child_environment: dict[str, str] = dict(
        os.environ if environment is None else environment
    )
    child_environment["NO_COLOR"] = "1"
    return subprocess.run(
        [*command(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=directory,
        env=child_environment,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=60,
        check=False,
    )


def accept(result: subprocess.CompletedProcess[str], context: str) -> None:
    if result.returncode != 0:
        raise AssertionError(
            f"{context}: exit {result.returncode}\n"
            f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
        )


@dataclass(frozen=True)
class MonitoredCli:
    binary: Path
    environment: Mapping[str, str] | None = None
    transport_calls: Path | None = None
    expected_ssh: int | None = None

    def calls(self) -> list[str]:
        if self.transport_calls is None or not self.transport_calls.exists():
            return []
        return self.transport_calls.read_text().splitlines()

    def run(
        self, directory: Path, arguments: Sequence[str]
    ) -> subprocess.CompletedProcess[str]:
        before = self.calls()
        result = invoke(self.binary, directory, arguments, self.environment)
        observed = self.calls()[len(before) :]
        if arguments[0] in ("check", "compile", "bind-ir") and observed:
            raise AssertionError(
                f"{arguments[0]} authenticated the endpoint: {observed!r}"
            )
        if (
            arguments[0] in ("run", "run-ir")
            and self.expected_ssh is not None
            and result.returncode == 0
            and (observed.count("ssh") != self.expected_ssh or "scp" in observed)
        ):
            raise AssertionError(
                f"{arguments!r} expected {self.expected_ssh} owned SSH channels: {observed!r}"
            )
        return result


def run_examples(
    binary: Path,
    parent: Path,
    levels: Sequence[int],
    selected: Sequence[str],
    *,
    examples: Sequence[Example] = EXAMPLES,
    environment: Mapping[str, str] | None = None,
    transport_calls: Path | None = None,
    expected_ssh: int | None = None,
) -> list[dict[str, Any]]:
    examples = [
        example for example in examples if not selected or example.name in selected
    ]
    if not examples:
        raise ValueError("No library journeys selected")
    records: list[dict[str, Any]] = []
    cli = MonitoredCli(binary, environment, transport_calls, expected_ssh)
    for example in examples:
        for level in levels:
            for stage in ("source", "relocatable", "executable"):
                directory = parent / example.name / f"O{level}" / stage
                directory.mkdir(parents=True)
                for name, contents in example.inputs:
                    path = directory / name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(contents)

                source = directory / "library 体验.msync"
                program = "import sys;sys.stdout.write('HELLO');sys.stderr.write('WARN');sys.exit(7)"
                source_text = example.source.replace(
                    "__COMMAND_EXECUTABLE__", json.dumps(sys.executable)
                ).replace("__COMMAND_ARGS__", json.dumps(["-c", program]))
                source.write_text(source_text, encoding="utf-8")
                accept(
                    cli.run(directory, ["check", str(source), f"-O{level}"]),
                    example.name + " check",
                )
                for name, _ in example.outputs:
                    if (directory / name).exists():
                        raise AssertionError(
                            f"check executed the effect: {example.name}/{name}"
                        )
                artifact = source
                if stage != "source":
                    artifact = source.with_suffix(".mso")
                    accept(
                        cli.run(
                            directory,
                            ["compile", str(source), "-o", str(artifact), f"-O{level}"],
                        ),
                        example.name + " compile",
                    )
                    if stage == "executable":
                        executable = source.with_suffix(".mexe")
                        accept(
                            cli.run(
                                directory,
                                [
                                    "bind-ir",
                                    str(artifact),
                                    "-o",
                                    str(executable),
                                    "--source-file",
                                    str(source),
                                ],
                            ),
                            example.name + " bind",
                        )
                        artifact = executable
                    source.unlink()
                launch_directory = directory
                if stage == "executable":
                    launch_directory = directory / "other working directory"
                    launch_directory.mkdir()
                arguments = (
                    ["run", str(artifact), f"-O{level}"]
                    if stage == "source"
                    else ["run-ir", str(artifact)]
                )
                accept(
                    cli.run(launch_directory, arguments),
                    f"{example.name} O{level} {stage}",
                )
                for name, expected in example.outputs:
                    observed = (directory / name).read_bytes()
                    if observed != expected:
                        raise AssertionError(
                            f"{example.name} O{level} {stage} {name}: {observed!r} != {expected!r}"
                        )
                records.append(
                    {
                        "name": example.name,
                        "optimization": level,
                        "stage": stage,
                        "outputs": len(example.outputs),
                    }
                )
    return records


def ssh_journey(binary: Path, directory: Path) -> dict[str, Any]:
    sys.path.insert(0, str(ROOT / "legacy" / "benches"))
    from ssh_v065 import Fixture

    with Fixture(ROOT / ".temp" / "stdlib-upgrade" / "ssh") as fixture:
        remote = json.dumps(
            str(fixture.root / "remote data 星.txt"), ensure_ascii=False
        )
        source = f"""
let endpoint = ssh://Perf_Fixture;
let file = $std::files::File({remote} @ endpoint);
file::write("remote🙂");
file::append("!");
file::read_text() -> ./remote-read.txt;
file::copy_to(./remote-copy.txt);
let command = $std::commands::Command("/bin/sh", [
    "-c", "printf REMOTE; printf WARN >&2; exit 9"
]);
let remote_command = command::on(endpoint);
let result = remote_command::capture();
result::write_to(./remote-stdout.txt);
result::error -> ./remote-stderr.txt;
(result::code as String) -> ./remote-status.txt;
"""
        example = Example(
            "ssh-file-and-command",
            source,
            (
                ("remote-read.txt", "remote🙂!".encode()),
                ("remote-copy.txt", "remote🙂!".encode()),
                ("remote-stdout.txt", b"REMOTE"),
                ("remote-stderr.txt", b"WARN"),
                ("remote-status.txt", b"9"),
            ),
        )
        records = run_examples(
            binary,
            directory,
            (2,),
            (),
            examples=(example,),
            environment=fixture.environment(),
            transport_calls=fixture.calls,
            expected_ssh=5,
        )
        return {
            "routes": records,
            "fixture": str(fixture.root),
            "ssh_channels_per_route": 5,
            "compile_bind_authentication": False,
        }


def identify(binary: Path) -> dict[str, Any]:
    components: dict[str, str] = {}
    if (binary.parent / "Msync.Compiler.dll").exists():
        for name in ("Msync.Compiler.dll", "Msync.Core.dll", "Msync.Runtime.dll"):
            path = binary.parent / name
            components[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    return {
        "path": str(binary),
        "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "bytes": binary.stat().st_size,
        "components": components,
    }


def measure(
    images: Sequence[tuple[str, Path]], directory: Path, samples: int
) -> list[dict[str, Any]]:
    directory.mkdir(parents=True)
    records: list[dict[str, Any]] = []
    workloads = {
        "none": "let value = true; value",
        "bool": "$std::bool::both(true, false)",
    }
    for workload, source in workloads.items():
        script = directory / f"{workload}.msync"
        script.write_text(source, encoding="utf-8")
        timings: dict[str, list[float]] = {name: [] for name, _ in images}
        for index in range(samples + 2):
            ordered = images if index % 2 == 0 else tuple(reversed(images))
            for name, binary in ordered:
                started = time.perf_counter_ns()
                result = invoke(binary, directory, ["check", str(script), "-O0"])
                elapsed = (time.perf_counter_ns() - started) / 1_000_000
                accept(result, workload + " benchmark " + name)
                if index >= 2:
                    timings[name].append(elapsed)
        for name, binary in images:
            values = timings[name]
            records.append(
                {
                    "image": name,
                    "identity": identify(binary),
                    "workload": workload,
                    "samples": samples,
                    "p50_ms": statistics.median(values),
                    "minimum_ms": min(values),
                    "raw_ms": values,
                }
            )
    return records


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--perf-only", action="store_true")
    parser.add_argument("--ssh-fixture", action="store_true")
    parser.add_argument("--performance", action="store_true")
    parser.add_argument("--samples", type=int, default=15)
    parser.add_argument("--level", type=int, action="append", choices=(0, 2, 3))
    parser.add_argument("--case", action="append", default=[])
    parser.add_argument("--output", type=Path)
    options = parser.parse_args()
    binary: Path = options.binary.resolve()
    temporary = ROOT / ".temp" / "stdlib-upgrade"
    temporary.mkdir(parents=True, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="journey-", dir=temporary))
    report: dict[str, Any] = {
        "platform": platform.platform(),
        "python": platform.python_version(),
        "binary": identify(binary),
        "directory": str(directory),
    }
    local_cases: list[str] = [
        name for name in options.case if name != "ssh-file-and-command"
    ]
    if not options.perf_only and (local_cases or not options.case):
        report["journeys"] = run_examples(
            binary, directory, options.level or (0, 2, 3), local_cases
        )
    if options.ssh_fixture and (
        not options.case or "ssh-file-and-command" in options.case
    ):
        report["ssh"] = ssh_journey(binary, directory / "ssh")
    if options.performance or options.perf_only:
        images: list[tuple[str, Path]] = []
        if options.baseline:
            images.append(("baseline", options.baseline.resolve()))
        images.append(("candidate", binary))
        report["performance"] = measure(
            images, directory / "performance", options.samples
        )
    contents = json.dumps(report, indent=2, ensure_ascii=False) + "\n"
    output: Path = options.output or directory / "report.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(contents, encoding="utf-8")
    print(contents, end="")


if __name__ == "__main__":
    main()
