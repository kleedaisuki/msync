# Lowered execution and native compilation

This executable tests the production backend, not the CLR JIT. All policies,
including `Off`, consume the same lowered `NativeExecutionImage`; `Force`
allocates/seals and enters native code. Every builtin retains complete typed
helpers. No source replay or second VM is involved.

```sh
dotnet run --project tests/NativeTiering -c Release
dotnet publish tests/NativeTiering -c Release -r linux-x64 \
  -p:NativeTierAot=true -o artifacts/native-tiering/linux-x64
artifacts/native-tiering/linux-x64/Msync.NativeTiering.Tests
```

The user journeys cover exact IEEE bits, closure state and retargeting, complete
String/Path/Connection-containing namespaces, Range callbacks, changing indirect
call targets/layouts without redundant guards, actual kind guard failures,
recompilation, cancellation, GC during real byte publication,
selected I/O suspension without replay, Process state, and concurrent code-page
invalidation. A gated read spans `UpdateProgram`; old work remains on its image.

Lowering properties assert that private dead address/access intermediates become
typed transfers for every builtin, without crossing independently reachable PCs.
Uninitialized accesses retain their original spans after strict artifact round
trips. Mixed variants, indirect pointer mutation, rebinding, captured aliases and
helper writes retain their values/identities; a complete union copy does not
invent a numeric guard. Ordered other-invocation writes/pending/clear changes
before a gated resume must be observed. VM Future -> host Store -> resumed
arithmetic must retain CWT pending semantics, not treat physical init as Ready.
Checked scalar refinements preserve signaling/quiet NaNs and negative zero and
never demand a discarded pending value. Logical source/opcode counters remain
equal across modes; a helper-only infinite loop publishes bounded live progress.

Generated code owns no managed references. Primitive byte/init buffers are pinned
only during synchronous execution, never across the saved asynchronous operation.
Automatic promotion remains disabled until representative frozen-image Native
AOT measurements establish a gain. Native entries alone are not a performance
claim; ARM64/Windows encoding and execution require their own platform evidence.

Register-region journeys exercise definition-specific values on reused slots,
12-argument register pressure and real stack spills, eliminated intermediate
frame descriptors/stores, checked union refinements, native branch conditions
with pending payloads, native jump cancellation, exact taken/untaken profile
edges and invocation/pin-lease reuse across safepoints. A contextual transfer
followed by a mutable loop checks that preparation does not freeze first-iteration
values into a source-free executable. See the
[backend receipt](../../docs/design/native-execution-lowering-v090.md#register-allocated-native-regions).

The focused `--unary-math` journey round-trips Native2 routines through the
artifact reader and compares compiled scalar math with the interpreter for
finite values, signed zero, subnormals and non-finite inputs. It also checks an
unresolved Process result, native-call census preservation, and unary math
inside a register region whose entry PC precedes the builtin.
