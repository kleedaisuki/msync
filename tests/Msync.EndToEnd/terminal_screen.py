"""Observe the visible cells painted by the real terminal journeys."""

from __future__ import annotations

import unicodedata
from codecs import IncrementalDecoder, getincrementaldecoder
from collections.abc import Callable


class Screen:
    def __init__(
        self,
        output: Callable[[], str | bytes | bytearray],
        rows: int = 30,
        columns: int = 120,
    ) -> None:
        self.output = output
        self.rows = rows
        self.columns = columns
        self.cells = [[" "] * columns for _ in range(rows)]
        self.row = 0
        self.column = 0
        self.offset = 0
        self.escape = ""
        self.wrap = False
        self.decoder: IncrementalDecoder = getincrementaldecoder("utf-8")("replace")

    def lines(self) -> tuple[str, ...]:
        current = self.output()
        chunk = current[self.offset :]
        text = chunk if isinstance(chunk, str) else self.decoder.decode(chunk)
        for character in text:
            self.consume(character)
        self.offset = len(current)
        return tuple("".join(line).rstrip() for line in self.cells)

    def contains(self, value: str) -> bool:
        return any(value in line for line in self.lines())

    def down(self) -> None:
        self.row += 1
        if self.row == self.rows:
            self.cells.pop(0)
            self.cells.append([" "] * self.columns)
            self.row -= 1

    def consume(self, character: str) -> None:
        if self.escape:
            self.escape += character
            if self.escape.startswith("\x1b["):
                if len(self.escape) > 2 and "@" <= character <= "~":
                    self.csi(self.escape[2:-1], character)
                    self.escape = ""
            elif self.escape.startswith("\x1b]"):
                if character == "\x07" or self.escape.endswith("\x1b\\"):
                    self.escape = ""
            elif len(self.escape) == 2:
                self.escape = ""
            return
        if character == "\x1b":
            self.escape = character
        elif character == "\r":
            self.column = 0
            self.wrap = False
        elif character == "\n":
            self.down()
            self.wrap = False
        elif character == "\b":
            self.column = max(0, self.column - 1)
            self.wrap = False
        elif character >= " " and not unicodedata.combining(character):
            if self.wrap:
                self.column = 0
                self.down()
            width = 2 if unicodedata.east_asian_width(character) in {"W", "F"} else 1
            self.cells[self.row][self.column] = character
            if width == 2 and self.column + 1 < self.columns:
                self.cells[self.row][self.column + 1] = ""
            self.wrap = self.column + width >= self.columns
            self.column = min(self.columns - 1, self.column + width)

    def csi(self, parameters: str, final: str) -> None:
        if parameters.startswith(("?", ">")) or final not in "ABCDHfGJK":
            return
        values = [int(part or "0") for part in parameters.split(";")]
        count = values[0] or 1
        self.wrap = False
        if final == "A":
            self.row = max(0, self.row - count)
        elif final == "B":
            self.row = min(self.rows - 1, self.row + count)
        elif final == "C":
            self.column = min(self.columns - 1, self.column + count)
        elif final == "D":
            self.column = max(0, self.column - count)
        elif final in {"H", "f"}:
            self.row = min(self.rows - 1, count - 1)
            self.column = (
                min(self.columns - 1, (values[1] or 1) - 1) if len(values) > 1 else 0
            )
        elif final == "G":
            self.column = min(self.columns - 1, count - 1)
        elif final == "K":
            start = 0 if values[0] in {1, 2} else self.column
            end = self.column + 1 if values[0] == 1 else self.columns
            self.cells[self.row][start:end] = [" "] * (end - start)
        elif final == "J":
            for row in range(self.rows):
                if (
                    values[0] == 2
                    or values[0] == 0
                    and row > self.row
                    or values[0] == 1
                    and row < self.row
                ):
                    self.cells[row] = [" "] * self.columns
            if values[0] != 2:
                self.csi(str(values[0]), "K")
