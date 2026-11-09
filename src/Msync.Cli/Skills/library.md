# Source-backed standard library

`$std` is a Scope. Runtime modules contain ordinary source functions and explicit
host-native declarations. The compile-time `concepts` namespace supplies named
constraints without introducing runtime values. Module topic pages show the
same embedded declarations used by the compiler.

```msync
let fs = $std::fs;
let config = ~/.codex/config.toml;
let available = fs::exists(config);
let upper = $std::string::to_uppercase("hello");
let combined = $std::bool::both(available, true);
```

- `fs`: metadata, filesystem queries, iteration and explicit mutations.
- `functional`: function and predicate composition using ordinary closures.
- `io`: UTF-8 text, line and byte Ranges, replacement and append operations.
- `math`: Number arithmetic, floating-point classification and source utilities.
- `path`: lexical decomposition, composition and endpoint qualification.
- `string`: Unicode scalar access, searching, slices, joins and text tools.
- `bool`: Bool composition as ordinary source functions.
- `ranges`: lazy adapters, structured elements, searches, folds and sorting.
- `process`: Process inspection, explicit waiting and live process control.
- `containers`: List, Map, EqualitySet, Stack, Queue, Set, BTree, BPlusTree, Heap.
- `text`: StringBuilder and text composition policies.
- `regex`: Pattern operations with typed match and group records.
- `files`: File and Directory families with explicit I/O policies.
- `commands`: Command, Completed and Checked families with runner policies.
- `data`: Option, Result, Either and Pair composition.
- `environment`: local environment and execution-context tools.
- `concepts`: reusable product, sum, capability and algebraic constraints.
- `meta`: closed-type traits and unevaluated callable/member queries.
- `shell`: typed Unix-style terminal functions and explicit filesystem effects.
- `storage`: typed backing-storage primitives used by generic containers.

```msync
fn preserve(value: $std::concepts::Option?) -> $std::concepts::Option? {
    value
};
let selected? = preserve($std::data::Option::some("Klee"));
```

## Named families and policies

State belongs to plain Scope records. Named Scopes group constructors and
associated operations; pass the instance explicitly, as with Rust associated
functions:

```msync
let labels = $std::containers::List::new(["first"]);
$std::containers::List::push(labels, "second");
let snapshot = $std::containers::List::snapshot(labels);
let report = $std::files::File::new(./report.txt);
```

Select a policy through a concept-constrained constructor, such as
`EqualitySet::with_equality(initial, equality)` or `File::with_policy(path, io)`.
The compiler specializes ordinary `fn` bodies using the supplied Scope and
callable identities. It does not use a runtime registration table or allocate
a separate collection of method closures for each instance.

Classes and named Scopes use PascalCase; functions use lowercase words joined
with underscores. Families expose constructors and explicit instance parameters;
instances contain data rather than per-object method tables.

`Set` is a red-black tree using an ordering policy; `EqualitySet` is the
separate equality-only collection. BTree, BPlusTree and Heap provide distinct
ordered data structures. Their `StrictOrder` policies do not change the
language's IEEE Number comparisons.

Concepts check fields, signatures and associated types without adding runtime
type kinds. Ordinary Scope records and generic functions supply the values and
operations. Mathematical laws remain semantic contracts rather than compiler
proofs inferred from an operation's name.

Explicit conversion is language syntax: `"42" as Number`. Read
`msync skill language conversions`; it is not a root `$std` function.

The two layers serve different purposes: a small primitive layer supplies host
and iteration capabilities, while source functions provide a broad expression
vocabulary. There is no separate Array namespace: use Range for sequence work.

Range callbacks are `fn` values. Lazy ranges evaluate only demanded elements;
constructing a range does not imply executing every effectful item.

```msync
let selected = $std::ranges::filter([true, false], fn(value) { value });
let first = $std::ranges::take(selected, 1);
```

Adapters are lazy; terminals consume their source. Buffered operations such as
sorting need finite input. Materialization preserves object references rather
than cloning Scope instances. String positions count Unicode scalar values,
not UTF-16 code units or displayed grapheme clusters.

```msync
let number = "42" as Number;
let name = "./report.txt" as Path;
let normalize = $std::functional::compose(
    $std::string::to_uppercase, $std::string::trim
);
let labels = $std::ranges::transform([" first ", " second "], normalize);
let joined = $std::string::join(labels, ", ");
```

An explicit cast changes the actual value, not only its inferred type. This
matters for transfers: String writes text, whereas Path copies file content.
Null still requires an admitted nullable type; it does not gain truthiness.

## Typed terminal tools

The REPL imports the public `$std::shell` functions as ordinary, shadowable
bindings. Enter `ls()` to list Paths, `cat(./report.txt)` to read text, or
`cd(./project)` to change that session's working directory. Ranges stream their
items to the terminal; dropping a result with `;` still discards it.

```msync
head(sort(uniq(["beta", "alpha", "beta"])), 2)
```

These are normal typed function calls, not a second shell grammar. In scripts,
select the same functions through `$std::shell::operation`. Read
`msync skill library shell` for their actual signatures and effect semantics.

The editor can navigate to these same embedded sources through versioned
`msync-stdlib` documents. For command options rather than language APIs, run
`msync --help`.
