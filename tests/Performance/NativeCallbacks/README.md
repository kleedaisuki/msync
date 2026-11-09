# Native callback allocation probe

`NativeRegistry` receives the interpreter's invocation callback even for native
operations that never call user functions. This probe measures an actual native
Buffer with 1,024 indexed reads, initialization and a size query. The bytecode
uses ordinary storage primitives and is serialized/bound before timing. It also
measures a literal with no native calls, distinguishing the added interpreter
field from lazily allocated delegates.

The source check passes real `ranges.transform` callbacks through native code,
mutates captured storage, replaces the callable, and verifies fresh interpreter
instances at O0/O2/O3 through source, `.mso` and `.mexe`.

```sh
DOTNET_TieredCompilation=0 dotnet run -c Release \
  --project tests/Performance/NativeCallbacks -- \
  --calls 1024 --samples 9 --iterations 32 \
  --output .temp/native-callback/current.json
```

For matched prior/current assemblies use `-p:FrozenAssemblies=/absolute/path`
and a separate `-p:NuGetLockFilePath=/absolute/.temp/probe.lock.json`; this avoids
altering the real project-reference lock graph. Publish outputs and build
artifacts should likewise use isolated directories. The report records runtime
and executable-artifact SHA256, actual native-call count and raw observations.
These managed component observations isolate delegate allocation; they do not
stand in for Native AOT tree or terminal latency.
