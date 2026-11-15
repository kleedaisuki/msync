# LLVM-to-Native performance review bundle

The [integrated review](../../design/llvm-performance-regression-review.md)
records the control/candidate identities, measured regressions, cross-review
findings, exclusions and priorities. Eight independent reports are retained in
this directory. This review did not change product code or claim a speedup.

`results/` preserves the final standard application pairs, nine-pair O2 compile
observations, focused AOT integration receipts and the structural liveness
replay. Absolute paths identify the original local observation; binary hashes
identify its actual payload. Native AOT binaries and full JSONL traces remain
local artifacts, not Git source files.

The structural replay is development infrastructure, not a product dependency:

```sh
python3 scripts/compiler/analyze_native_liveness.py \
  CONTROL_DISASSEMBLY.json CANDIDATE_DISASSEMBLY.json \
  --output .temp/native-liveness/result.json
```

Supply the CLI's `disassemble --json` output. The probe reproduces the existing
NativeRegisterPlan worklist's set operations for the entry function; it neither
executes an optimizer nor estimates wall time. Retained JSON includes input
hashes, all peak members and backedge inventories. Do not interpret raw opcode
counts or these structural work counts as exclusive elapsed-time attribution.
