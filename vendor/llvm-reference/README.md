# Inspected LLVM IR source snapshot

`SOURCE.json` records the exact upstream LLVM commit and file hashes. The
upstream license is retained at `llvm/LICENSE.TXT`.

This is source material for a managed representation of the user-selected LLVM
IR. It does not add an LLVM backend, binary dependency or pass pipeline. The
snapshot records what was inspected; it does not assert a user-selected release.

`llvm/include/llvm/IR/Instruction.def` is copied verbatim. Regenerate the exact
instruction enum with:

```sh
python3 scripts/compiler/generate_llvm_opcodes.py
```

The generated upstream vocabulary is unchanged. Project extension instructions
are declared separately: `HeapAlloc = 128`, `FileIn = 129`, and `FileOut = 130`.
There is no capture-import extension. These three operations explicitly represent
heap allocation and file input/output; the custom Native target lowers them to
its existing execution ABI. This source snapshot does not select LLVM codegen
or the LLVM backend pipeline. See [integration status](../../docs/design/llvm-ir-port-status.md)
for implemented algorithms, accepted journeys and unfinished work.

The upstream TypeID vocabulary is generated separately:

```sh
python3 scripts/compiler/generate_llvm_types.py
```

Both generators preserve upstream identifiers and numeric identities. Reserved
upstream UserOp placeholders are not authorization to add language operations.
