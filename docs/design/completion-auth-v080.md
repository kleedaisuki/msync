# Native SSH authentication for editor completion

Remote completion selects a real endpoint, so it can require the same host-key,
agent, key-passphrase, password or security-key interaction as an explicit native
SSH operation. The retained completion shell owns stdin for its command protocol;
neither the language-server JSON-RPC stream nor the suspended REPL buffer can be
used as the native client's password input.

`SshPromptBroker` supplies the native
[OpenSSH askpass mechanism](https://man.openbsd.org/ssh.1) with the same installed
`msync` executable as its helper. A child-only environment marker selects the
helper entrypoint before help, configuration, telemetry or language-server
dispatch. Explicitly configured `SSH_ASKPASS` handlers and `SSH_ASKPASS_REQUIRE=never`
retain their native ownership. No global environment mutation is needed.

One broker belongs to one retained endpoint process. Its owned .NET named pipe
passes the exact native prompt and the reply as length-prefixed UTF-8 strings.
The helper acknowledges a consumed reply before the server disconnects: Windows
pipe disconnection must not race a reader that has not yet consumed the complete
answer. Dismissal has a distinct result from a valid empty answer. Endpoint
cancellation closes its broker and retires the helper, rather than leaving an
unrelated prompt behind. Credential answers are not written to msync history,
telemetry, artifacts or diagnostic output.

For LSP, `LanguageServer.Authentication` moves prompt admission and cancellation
through the existing protocol event loop. It is the only owner that writes
JSON-RPC output or updates pending authentication requests. Worker continuations
receive a task; they never synchronously write to the protocol stream. The client
advertises `initializationOptions.sshPrompt`, then handles:

- `msync/sshPrompt`: `{ id, endpoint, prompt, kind }` and a `{ value }` reply;
- `msync/sshPromptCancel`: the owning `id` to retire active or queued UI.

`kind` follows the native askpass hint: `confirm` is host-key confirmation,
`notice` is a nonblocking security-key notice, and ordinary questions use a masked
secret input. The VS Code client serializes questions, keeps the exact endpoint
and prompt visible, and uses cancellable native InputBox/QuickPick controls. An
unsupported client receives actionable authentication guidance instead of an
unanswered protocol request.

For the REPL, `SshTerminalPrompt` runs only while the outer PrettyPrompt callback
is suspended through its renderer-owned external-interaction scope. There is no
concurrent terminal reader. Secret keys are read without echo; they never become
source, history or a completion hint. Confirmation/text input supports grapheme
backspace, while Escape, Ctrl+C and EOF dismiss the question. The outer editor
then restores its existing buffer and renderer state.

## Focused acceptance

`tests/Msync.Authentication.Tests` links the broker implementation and invokes a
supplied real product executable as a child helper. It verifies repeated native
prompt contexts, multiline/Unicode prompt bytes, exact answer bytes, distinct
dismissal, configured-handler ownership and cancellation during an admitted
question:

```sh
dotnet run --project tests/Msync.Authentication.Tests -- /path/to/msync
```

The VS Code client's focused tests exercise masked and unmasked input, exact
host-key confirmation, queued cancellation, active cancellation, restart cleanup
and nonblocking notices. Real SSH directory-completion journeys separately own
the encrypted transport and retained-channel behavior.
