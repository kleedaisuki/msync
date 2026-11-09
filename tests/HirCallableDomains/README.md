# Callable write domains and evaluation ownership

The public frontend exports HIR, the fixture serializes/readbacks that HIR, and
both relocatable and context-bound bytecode execute in the actual interpreter.
The tests distinguish current callable flow from a deferred location's possible
future definitions. They also keep Scope object aliases separate from Function
value copies and from unrelated instances of the same factory.

`BoundCallableWrites` indexes existing bound semantic nodes. Its finite analysis
starts from executable roots, follows source calls and native callback admission,
and excludes `decltype` and dependent-query operands. Literal-unreachable writes
and writes in uncalled generic bodies cannot introduce new callable candidates.
Scope points-to information identifies field declarations; source write sites,
receiver/capture cell ownership distinguish unrelated actual objects. Canonical
binding definitions and live storage owners are different identities. Neither
runtime cell nor object identities become specialization-cache keys.

Current eagerness and future type safety are separate components of a read-only
return scheme. Snapshot policy is preserved at call construction; it is not a
mutable-value observation to discard with a literal. Future callable alternatives
cannot retroactively change that policy. Policy memoization has its own finite
Boolean/eager facts key; executable specialization still does not key Boolean
constants or caller object IDs. A single call/defer node is published afterward.

Before specialization, reachable input fields and captured cells carry an
optional future callable domain. It belongs to a live storage owner, separately
from the current callable value. Scope aliases retain it; Function values passed
by value do not borrow the original cell's future assignments. Formalization
maps the owner and captures, while code schemas encode only definitions, types,
and alias topology. This keeps lazy members returned inside another Scope correct
without restoring the whole caller environment to a callee.

Each possible source target is analyzed in an isolated facts overlay. The call
joins those alternatives once; it does not sequentially apply every candidate's
writes. Native callable identity is its registered name, independent of the
empty-array representation of its nonexistent captures. Source captures retain
their normal identity.

Buffer-returning helpers remain eager, including resource fields reached through
Range item selection. Their policy schema retains nested eagerness but not live
resource IDs. Fresh semantic graph descriptions escaping a lazy body are
published to its caller without applying deferred writes to borrowed state.

Unknown future REPL definitions are handled separately by session signature
obligations; this closed-source index does not pretend to predict unseen source.

Run `dotnet run --project tests/HirCallableDomains -c Release`. Supply case names
as application arguments to select an incremental journey. `FrozenAssemblies`
can point to a coherent frozen product assembly set.
