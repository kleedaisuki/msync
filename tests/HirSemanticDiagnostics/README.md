# Typed HIR semantic diagnostic contracts

This focused executable checks representative mistakes through the public
`Compiler.Analyze` entrypoint: nullable transfer, fixed binding and field
storage, missing members/types, function arity, exhaustive matching, and a
structural concept failure. It checks useful source labels/help, actual
member/parameter names, declaration-owned related locations, and an applicable
`?` insertion fix. An arithmetic union mismatch checks that the diagnostic
points at the operand rather than the whole binary expression.

Use `-p:FrozenAssemblies=/absolute/path` to consume one coordinated Compiler,
Core, Hir and Optimization assembly set without rebuilding production. The
project restricts assembly resolution to explicit HintPaths; verify the copied
Compiler SHA when comparing semantic generations.

The published Native 0.8.0 executable at SHA256
`10d27794ccf2dd4c5638fd6d77d3510de5b6f1dd61ac0e0557382e3cf36c32ad`
provided the actual CLI baseline. Eight misuse files were checked against that
executable and the first true-cutover managed CLI snapshot (Compiler SHA256
`3296ff6984adfbb10360c836568b90c9ac7c24bd48b503cdf5b87385d3592b42`).
The baseline's type-directed Null/Bool/Range/conversion advice, named argument
help and related declaration locations informed the new factory; no old
mutable compiler or bytecode-emission path is retained. Snapshot 4 passed the
initial 32 contracts. The operand-span and fixed-field checks were added after
that observation and require the next coordinated source snapshot.
