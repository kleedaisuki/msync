# Direct source call acceptance

Run from the repository root:

```sh
dotnet run --project tests/DirectSource -c Release -p:RestoreLockedMode=true
MSYNC_JIT=off dotnet run --project tests/DirectSource -c Release --no-build
```

The host fixture uses the format-6 reader for both standalone object and
bound images. It checks exact signed-zero and NaN result bits across a
nonempty return join, raw result transport into a selected private caller
local, and preservation of lower caller operands. Two functions use the same
body with explicitly declared location and value capture ABIs. The former
mutates the original Cell; the latter mutates only its private activation. An
indirect call after both targets warm keeps its ordinary frame and updates the
same Cell once. Portable call arguments are values rather than raw locations.

On a supported host, aggregate profiling observes 9 direct activations per
capture ABI. Detailed profiling uses all 145 physical frames. Both paths
retain all 145 logical calls and 1,593 logical callee instructions. A valid
implicit-return body always uses canonical invocation, including the bound form that jumps directly
to function end. The test also checks explicit-return results with
telemetry off. Pass `--expect-native` on Linux AArch64 when the host's
executable-code publication prerequisites are available.
