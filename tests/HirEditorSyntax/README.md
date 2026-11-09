# Syntax-only editor facts

This executable links the product syntax parser and editor syntax index, with
only Core as a dependency. It does not instantiate the semantic compiler,
produce a bytecode module, or execute user expressions.

The index supplies declaration roles, written signatures, outline symbols,
annotation positions and associated-type links while a document is incomplete.
Typed HIR observations may refine these facts; they do not need to manufacture
a partially lowered program just to keep syntax presentation available.

The focused run passed 84 contracts for named/anonymous Scopes, function and
native signatures, inline associated types, qualified concept segments and an
unfinished generic body. All source and selection spans belong to the original
document. A frozen Core assembly can be supplied to build the test alone:

```sh
dotnet run --project tests/HirEditorSyntax -c Release \
  --artifacts-path .temp/hir-editor-syntax \
  -p:FrozenAssemblies=/absolute/path/to/frozen/assemblies
```
