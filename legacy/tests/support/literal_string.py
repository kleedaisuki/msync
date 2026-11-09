"""Canonical quoted msync source, distinct from JSON and shell encoding."""


def quoted(value: str) -> str:
    return '"' + value.replace('"', '""') + '"'
