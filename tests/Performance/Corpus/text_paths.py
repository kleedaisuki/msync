"""Unicode path-manifest analysis and local metadata/copy workloads."""

from __future__ import annotations

import hashlib
import posixpath
import random
from functools import partial
from pathlib import Path

from model import Case, Fixture, OutputFile

_TEXT_PATH_SOURCE = """let text = $std::string;
let path = $std::path;
let io = $std::io;
let documents = 0;
let images = 0;
let drafts = 0;
let unicode_names = 0;
let bytes = 0;
let digest = 0;
for entry in io::stdin_lines() {
    let value = entry as Path;
    let extension = text::to_lowercase(path::extension(value) as String);
    let name = path::filename(value) as String;
    let stem = path::stem(value) as String;
    let category = if extension == ".md" or extension == ".txt" {
        documents = documents + 1;
        1
    } else {
        if extension == ".png" or extension == ".jpg" {
            images = images + 1;
            2
        } else {
            3
        }
    };
    if text::starts_with(name, "draft-") {
        drafts = drafts + 1;
    };
    if not text::is_ascii(name) {
        unicode_names = unicode_names + 1;
    };
    bytes = bytes + text::utf8_size(name);
    digest = (digest * 257 + text::length(stem) * 31 + category) % 16777213;
};
io::println(documents as String);
io::println(images as String);
io::println(drafts as String);
io::println(unicode_names as String);
io::println(bytes as String);
io::println(digest as String);
"""

_COPY_SOURCE = """let io = $std::io;
let fs = $std::fs;
let copied = 0;
let skipped = 0;
let bytes = 0;
let rows = io::read_lines(./manifest.txt);
for row in rows {
    let source = ./input / row;
    if fs::is_regular_file(source) {
        bytes = bytes + io::file_size(source);
        source -> (./out / row);
        copied = copied + 1;
    } else {
        skipped = skipped + 1;
    };
};
io::println(copied as String);
io::println(skipped as String);
io::println(bytes as String);
"""

_ASCII_STEMS: tuple[str, ...] = (
    "chapter",
    "quarterly-report",
    "release.notes",
    "inventory",
    "trail-map",
    "scan",
    "meeting notes",
    "checkpoint",
)
_UNICODE_STEMS: tuple[str, ...] = (
    "可莉",
    "résumé",
    "re\u0301sume\u0301",
    "橋",
    "🧪-run",
    "𝄞-score",
    "naïve",
    "A\u030a-land",
)
_EXTENSIONS: tuple[str, ...] = (
    ".md",
    ".md",
    ".txt",
    ".TXT",
    ".png",
    ".jpg",
    ".JPG",
    ".csv",
    ".tar.gz",
    ".log",
    "",
)


def _lines(*values: int) -> str:
    return "".join(f"{value}\n" for value in values)


def _unicode_manifest(
    _directory: Path, size: int, seed: int, *, unicode_mix: int = 1
) -> Fixture:
    rng: random.Random = random.Random(seed)
    rows: list[str] = []
    documents = images = drafts = unicode_names = byte_count = digest = 0
    for index in range(size):
        folder: str = rng.choice(("src", "archive", "samples", "notes", "release/2026"))
        batch: int = rng.randrange(48)
        if index % 37 == 36:
            entry: str = f"./{folder}/batch-{batch:02d}/"
        elif index % 31 == 30:
            entry = f"./{folder}/batch-{batch:02d}/.gitignore"
        else:
            label: str = rng.choice(
                _UNICODE_STEMS if rng.randrange(5) < unicode_mix else _ASCII_STEMS
            )
            prefix: str = "draft-" if rng.randrange(6) == 0 else ""
            suffix: str = rng.choice(_EXTENSIONS)
            entry = f"./{folder}/batch-{batch:02d}/{prefix}{label}-{index:05d}{suffix}"
        rows.append(entry)

        name: str = posixpath.basename(entry)
        stem, extension = posixpath.splitext(name)
        extension = extension.lower()
        if extension in {".md", ".txt"}:
            category: int = 1
            documents += 1
        elif extension in {".png", ".jpg"}:
            category = 2
            images += 1
        else:
            category = 3
        drafts += name.startswith("draft-")
        unicode_names += not name.isascii()
        byte_count += len(name.encode("utf-8"))
        digest = (digest * 257 + len(stem) * 31 + category) % 16_777_213

    return Fixture(
        source=_TEXT_PATH_SOURCE,
        stdin="\n".join(rows) + "\n",
        stdout=_lines(documents, images, drafts, unicode_names, byte_count, digest),
        distribution={
            "paths": size,
            "non_ascii_names": unicode_names,
            "basename_utf8_bytes": byte_count,
        },
    )


def _local_manifest_copy(directory: Path, size: int, seed: int) -> Fixture:
    rng: random.Random = random.Random(seed)
    rows: list[str] = []
    outputs: list[OutputFile] = []
    absent: list[str] = []
    skipped = byte_count = 0
    for index in range(size):
        folder: str = f"project-{index % 12:02d}/batch-{index % 5:02d}"
        if index % 29 == 5:
            relative: str = f"{folder}/draft-directory-{index:05d}"
            (directory / "input" / relative).mkdir(parents=True, exist_ok=True)
            skipped += 1
            absent.append(f"out/{relative}")
        elif index % 37 == 13:
            relative = f"{folder}/missing-{index:05d}.bin"
            skipped += 1
            absent.append(f"out/{relative}")
        else:
            stem: str = rng.choice(("data", "report", "測試", "résumé", "snapshot"))
            relative = f"{folder}/{stem}-{index:05d}.bin"
            source: Path = directory / "input" / relative
            output: Path = directory / "out" / relative
            source.parent.mkdir(parents=True, exist_ok=True)
            output.parent.mkdir(parents=True, exist_ok=True)
            if index % 17 == 0:
                length: int = rng.randrange(128 * 1024, 1536 * 1024)
            elif index % 19 == 3:
                length = 0
            else:
                length = rng.randrange(64, 4096)
            payload: bytes = rng.randbytes(length)
            source.write_bytes(payload)
            byte_count += len(payload)
            initial: bytes | None = b"stale contents\n" if index % 4 == 1 else None
            outputs.append(
                OutputFile(
                    f"out/{relative}", hashlib.sha256(payload).hexdigest(), initial
                )
            )
        rows.append(relative)

    rng.shuffle(rows)
    (directory / "manifest.txt").write_text(
        "\n".join(rows) + "\n", encoding="utf-8", newline="\n"
    )
    return Fixture(
        source=_COPY_SOURCE,
        stdout=_lines(len(outputs), skipped, byte_count),
        outputs=tuple(outputs),
        absent=tuple(absent),
        output_roots=("out",),
    )


_RANGE_COPY_SOURCE: str = """let io = $std::io;
let fs = $std::fs;
let ranges = $std::ranges;
let paths = ranges::transform(io::read_lines(./manifest.txt), fn(row: String) {
    ./input / row
});
let selected = ranges::materialize(ranges::filter(paths, fn(path: Path) {
    fs::is_regular_file(path)
}));
selected -> ./out/;
io::println(ranges::size(selected) as String);
"""


def _local_range_copy(directory: Path, size: int, seed: int) -> Fixture:
    fixture: Fixture = _local_manifest_copy(directory, size, seed)
    outputs: tuple[OutputFile, ...] = tuple(
        OutputFile(f"out/{Path(output.path).name}", output.sha256, output.initial)
        for output in fixture.outputs
    )
    assert len({output.path for output in outputs}) == len(outputs)
    return Fixture(
        source=_RANGE_COPY_SOURCE,
        stdout=f"{len(outputs)}\n",
        outputs=outputs,
        absent=tuple(f"out/{Path(path).name}" for path in fixture.absent),
        output_roots=("out",),
    )


def cases() -> tuple[Case, ...]:
    return (
        Case(
            name="unicode-path-manifest",
            family="text-path",
            description="Analyze streamed Unicode path names with lexical decomposition, text predicates and scalar/UTF-8 accounting.",
            default_size=65_000,
            generate=_unicode_manifest,
            contrast=("unicode-heavy", partial(_unicode_manifest, unicode_mix=4)),
        ),
        Case(
            name="local-manifest-copy",
            family="filesystem",
            description="Read a shuffled local manifest, query file type and size, and copy mixed-size binary files to new or existing outputs.",
            default_size=144,
            generate=_local_manifest_copy,
        ),
        Case(
            name="local-range-copy",
            family="filesystem",
            description="Select a finite mixed-size Path cohort from the same manifest and transfer it as one operation into a flat destination.",
            default_size=144,
            generate=_local_range_copy,
        ),
    )
