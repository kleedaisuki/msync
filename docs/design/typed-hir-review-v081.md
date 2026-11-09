# Typed HIR: code identity, call state and query ownership

## Scope of this review

This review concerns the initial 0.8.1 semantic implementation, not a completion
verdict. The structured ID-based HIR model and Core-only lowerer establish the
right project boundary. The remaining architectural risk is recreating the old
coupling inside semantic caches, even though no bytecode is emitted there.
The observations below were obtained from the current implementation; no broad
test matrix or production-code rewrite was performed by the reviewer.

## 1. Share code, not the first caller's abstract objects

`SemanticAnalyzer.Instantiate` initially cached `FunctionInstance.Result` and
`FunctionInstance.Context` under a key based on parameter/capture types and
callable definitions. That is a reasonable *code* key, but the cached result
also contains concrete abstract Scope regions, indexed regions and captured
location identities from the first invocation.

```msync
fn wrap(value) { fn { value } };
let first = scope { item: 1 };
let second = scope { item: 2 };
let get_first = wrap(first);
let get_second = wrap(second);
let selected = get_second();
selected::item = 7;
# first::item must still be 1; second::item must be 7.
```

Both calls should share the same specialized code. They must not share the
first call's returned closure captures or object-region facts. Adding object
identity back to the code key merely restores specialization explosion.

**Required invariant:** cached semantic bodies and summaries use formal
parameter/capture/local identities. A call instantiates their result and
effect projections in the caller's abstract state:

```text
cached code/signature + formal result/effect summary
                           |
                substitute this call's operands
                           |
            caller result facts + caller effect update
```

Useful projection forms are an input value, an input field path, a fresh
allocation site and a closure definition with projected captures. A fresh site
is instantiated once per call analysis, while borrowed inputs retain their
identity. The summary need not be a general symbolic execution language; it
only needs to express the value/alias relationships the language actually
preserves.

The initial implementation also copied all caller values, storage, objects and
indexed maps into each instance, then retained that context in the cache. This
can cost O(caller-state size × instances) despite immutable value records.
Store completed code and summaries, not an entire caller world. Body checking
needs parameters, captures and local constraint state; ordinary control-flow
forks should share persistent maps or sparse overlays rather than eagerly copy
every namespace and object table.

## 2. Recursive correctness is independent of publication

The initial `InvokeType` path selected `Publish = false`, while instance-cache
lookup and recursive reservation both required `Publish = true`. Consequently
a query of an annotated recursive function had no active result promise:

```msync
fn recurse(value: Number) -> Number { recurse(value) };
let applicable = $std::meta::is_invocable(recurse, 1);
```

The query must terminate without executing the function. It needs a query-local
instance table with a reserved result variable before body checking, just like
ordinary recursive signature solving. It must not mutate the committed cache,
and a recursion-depth quota is not a substitute for a semantic fixed point.

**Required invariant:** every constraint workspace owns its active promises and
memoized results, regardless of whether it materializes executable HIR nodes.
Publication policy cannot disable the mechanism that makes recursive analysis
well founded.

## 3. Query output must not leak into executable arenas

The initial `Add` helper suppressed expression nodes for a non-publishing
context, but the structured Match/For paths still appended payload rows and
updated global location types. For example:

```msync
let selected_type = decltype(match true {
    true => 1,
    _ => 2,
});
0
```

The query's match arms then referred to `HirNodeId.None`, while the appended
match table remained in the frozen module. Complete artifact validation
correctly rejects those invalid references, even if runtime lowering never
visits the orphan payload.

**Required invariant:** a query owns local constraints/results, not an executable
output builder. It cannot append Match/For/closure payloads or rewrite bound
declarations as a side effect of determining a type. Immutable canonical type
interning can be shared within an explicit generation lifetime; executable
payload ownership is different. Fix the producer boundary rather than weaken
artifact validation, truncate arenas after queries or add a whole-graph rollback.

## Implementation order and evidence

The compiler owner acknowledged all three observations and assigned the flow
publication issue to its owner. The coherent implementation order is:

1. Define formal-space instance summaries and a caller substitution boundary.
2. Give ordinary and query constraint workspaces independent active promises.
3. Make executable publication an owned output phase, not a Boolean convention
   that every helper must remember.
4. Add the three small programs above when the public Analyze path is coherent.
   Check independent aliases, query termination and Analyze → export → load →
   lower with no invalid table rows. Preserve strict artifact validation.

These cases are architectural discriminators, not a request to repeat the
accepted codec or bytecode semantic matrices.

## Upstream mechanisms

Rust's [query system](https://rustc-dev-guide.rust-lang.org/query.html) computes
and memoizes results by explicit query keys and records dependencies. Its
[THIR](https://rustc-dev-guide.rust-lang.org/thir.html) uses body-owned typed
arenas that can be released after their consumers finish. Roslyn's
[bound-node model](https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/BoundTree/BoundNodes.xml)
keeps semantic operations distinct from backend execution layout. The relevant
lesson for msync is explicit identity and ownership of results, scratch state
and output—not copying another compiler's complete framework.
