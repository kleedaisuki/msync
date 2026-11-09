"""Generate same-code calls independently of an unrelated lexical world."""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class ScalingCase:
    source: str
    world_bindings: int
    calls: int


def generate_source(world_bindings: int, calls: int) -> str:
    lines = ["{"]
    lines.extend(
        f"    let unrelated_{index} = {index};" for index in range(world_bindings)
    )
    lines.append("    fn wrap(value: Scope) { scope { value: value } };")
    lines.extend(
        f"    let result_{index} = wrap(scope {{ item: {index} }});"
        for index in range(calls)
    )
    lines.extend(
        [
            "    scope {",
            "        first: result_0::value::item,",
            f"        middle: result_{calls // 2}::value::item,",
            f"        last: result_{calls - 1}::value::item,",
            "    }",
            "}",
        ]
    )
    return "\n".join(lines) + "\n"


def scaling_cases() -> dict[str, ScalingCase]:
    return {
        f"world_{world}_calls_{calls}": ScalingCase(
            generate_source(world, calls), world, calls
        )
        for world in (32, 1024)
        for calls in (8, 64)
    }
