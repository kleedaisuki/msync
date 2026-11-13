# Native host I/O integration

The host consumes only the closed `Core.Native` builtin representations. These
checks run actual local files and child processes; they do not convert through
the retired `Value` hierarchy or use compiler/runtime mocks.

```sh
dotnet run --project tests/NativeHostIO -c Release
python3 tests/NativeHostIO/remote_journey.py \
  --binary tests/NativeHostIO/bin/Release/net10.0/Msync.NativeHostIO.Tests.dll
```

Local checks cover binary and Unicode text, append/replacement, complete tree
contents and native file metadata, finite cohort/collision preflight, captured
cwd and process input/control/ownership notification. The loopback OpenSSH
fixture owns its keys/config/server under `.temp` and checks actual native
client, authentication and session counts for upload/download/remote cohorts,
selector metadata plus bytes, controlled process input, binary I/O and specific
native failure diagnostics. The selector performs file-size queries inside its
existing transfer channel, never a separately authenticated helper.

The native binding uses the existing strongly typed file/archive/protocol
primitives. Internal compatibility overloads accept ConnectionKind/address;
they do not create old language-value wrappers. These checks establish the host
boundary; final v0.9 acceptance also requires actual source/HIR/object/executable
and interactive execution through the new VM.
