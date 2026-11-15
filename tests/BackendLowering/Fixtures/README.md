# Historical work-hint fixture

`constructor-work-v3.mexe` was produced by the frozen
`artifacts/native/evaluator-small-roots/msync` compiler/binder, before the
construction-versus-consumption work-scope correction. It is Native format 2,
compact revision 3, not a fabricated current-format payload.

The source is equivalent to:

```text
let bias = 1;
let result = $std::ranges::first($std::ranges::transform([2], fn(value) {value + bias}));
bias = 9;
$std::io::println("submitted");
$std::io::println(result as String);
```

The old executable must now be rejected with a source-recompilation diagnostic.
`BackendLowering --artifact-revision` checks that boundary, then compiles the
source above into the current format and executes it under Off and Force. Fresh
compilation must print `submitted` and `3` without any loader-side analysis.
