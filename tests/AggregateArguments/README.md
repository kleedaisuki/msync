# Read-only aggregate arguments

`dotnet run --project tests/AggregateArguments` exercises the independent
`AggregateArgumentSpecialization` pass through real execution and both binary
stages. Controls explicitly use O0, including automatic context binding.

The motivating function is an effectful I/O helper such as
`fn copy(doc, destination) { doc::file -> destination; }`. Ordinary inlining
cannot erase its invocation-owned task join. A private clone can instead take
the `file` payload directly, removing the aggregate formal and its field lookup
without moving the I/O or removing the function-return boundary.

Eligibility is semantic, not a size quota:

* The actual callable identity and capture capacity are proved, rather than
  inferred from the `Call.B` specialization hint alone.
* A split formal is a Scope used only by direct local-load/member-read pairs.
  Rebinding, raw captures, stores and jumps into the middle of a pair retain the
  original function.
* Caller values prove every requested field exists. Thus extracting a payload
  cannot introduce a missing-member failure before an earlier callee effect.
  Unknown branch-join shapes retain ordinary calls.
* The VM-memory summary has no writes or unknown writes. Whole-object escape
  and return are excluded. An enumerated argument must resolve to a Path,
  String or a known callback-free range; deferred callback mutation is not
  treated as a read-only operation.

All actual arguments run once before field extraction. This matters when a
later argument modifies or rebinds the earlier aggregate's binding: its already
evaluated object reference is retained, but the fields are read after the later
argument's effects. Captures and original escaping callback signatures remain
unchanged. Existing equivalent clone bodies can be reused; cloning does not
depend on individual argument values. Recursive call components are not split.

The suite checks actual file bytes, both selected branch paths, argument writes
and rebinding, original callback use, eager versus deferred ranges, aliased
mutation, missing-member error order, native callable identity, binary round
trips and persistent-session/O1 exclusion. It also checks that repeated pass
application does not produce another equivalent clone.

This is a bounded IPA-SRA mechanism, not arbitrary aggregate ABI rewriting:
cyclic or unknown caller shapes and non-read-only callees remain fully usable
through the existing bytecode path. No opcode or artifact-format change is
needed. Parent optimizer wiring and product-pipeline acceptance are separate
from the isolated transformation contracts.
