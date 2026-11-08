# Global scalar redundancy

Run `dotnet run --project tests/GlobalRedundancy`.

The pass uses operand-definition identities, CFG availability and existing
result locations for full redundancy elimination. Partially available total
expressions can be completed on their missing predecessor edges. Taken critical
edges receive explicit split blocks; fallthrough insertion preserves existing
operand-stack values. The cost model reserves new result locations for repeated
uses or string operations whose work exceeds a simple scalar dispatch.

The oracle calls this pass directly through its internal interface and executes
both sides with explicit optimization level zero and executable-stage modules.
Interpreter binding therefore cannot optimize the baseline and conceal a
one-sided defect. Each changed module is validated, serialized and executed.
Independent expected values cover both directions of critical edges, reaching
stores, raw cell aliases, loop-carried versions, selected runtime diagnostics
and the two distinct IEEE-754 zero representations.

The same FRE and PRE discriminators also pass through the public
`Optimizer.Optimize` entry at levels 2 and 3. Level 1 retains two numeric
multiplications across the branch; higher levels retain one. The partial string
case executes two concatenations on its previously redundant path at level 1
and one at higher levels, while the missing edge still computes once and the
bypass path computes none. The small instruction trace checks only residual
work; actual Runtime execution remains the result/serialization oracle.

`--performance` runs a nine-sample Release component probe at 100, 500 and 2,000
distinct runtime-unknown numeric expressions, both straight-line and with a
join. Straight-line functions take only the CFG scan because local value
numbering owns their redundancy. Reaching locals are copied at CFG edges, not
at every expression; a reverse value-to-location index avoids quadratic local
searches and snapshots in the ordinary straight-line expression traversal.

Cyclic CFG components are not motion regions for this pass: their static value
identities can denote different dynamic iterations. Loop invariant motion owns
the required separate proof. No filesystem, process, Scope access, callback,
conversion or potentially failing operation is speculated by scalar PRE.
