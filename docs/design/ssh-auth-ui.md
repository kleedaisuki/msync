# SSH authentication UI and application home

## User intent

A user must recognize an authentication request as belonging to their current
msync task, understand which connection/key is being unlocked, and know what
cancellation does. A mysterious generic password box is not sufficient. On
Windows the user explicitly chose a native dialog so REPL background transfers
can continue without taking password input through the line editor.

## Native dialog information hierarchy

```text
msync — SSH authentication

Connection: ssh://Production_Box
Task: copy.msync / current msync transfer
Settings: OpenSSH configuration (or explicitly supplied user/port)

Request from OpenSSH:
  alice@example.com's password:

Password: [ masked input                         ]

Only the current OpenSSH client receives this input.
msync does not save it in settings, history, traces or IR.
Cancel stops this authentication; files already committed are not rolled back.

                         [ Cancel ] [ OK ]
```

This is an information-layout specification, not a promise to replace the OS
credential dialog's localized buttons or system styling. Keep native keyboard,
focus, masking and accessibility behavior. Brand the title with msync; put the
connection and reason above the input, not solely in a terminal the user may no
longer be watching. Do not claim a resolved hostname/username/port if only a Host
alias is known. The raw native prompt retains actual diagnostic context. When a
trusted source-file/task label is unavailable, use `Current msync transfer`
rather than guessing a filename.

Show only nonsecret connection/task metadata from invocation-owned state. Never
accept password material from a DSL URL, environment-based password cache,
command-line argument, configuration, telemetry or IR. Pass the answer only on
the askpass stdout channel expected by OpenSSH. Do not decorate that channel.
Bound/escape native prompt display to prevent control text from altering app-owned
labels while retaining the useful host/key/fingerprint detail.

Credential-dialog text has a 1024-UTF-16-unit native budget, excluding its NUL.
Reserve the credential/cancellation notice and prioritize the actual request;
middle-elide optional task/connection metadata with an explicit ellipsis while
preserving path ends/basenames. If the request itself exceeds the remaining
budget, mark its middle elision too. Never split a Unicode scalar/surrogate pair,
truncate a real connection/path argument, or fail authentication solely because
a supported source-script pathname is long. Host-key confirmation and notices
use a separate MessageBox surface and retain the full sanitized native request.

## Distinguish request types

| Request | Treatment | Cancellation |
| --- | --- | --- |
| Server password or keyboard-interactive secret | Native masked credential input; retain OpenSSH's exact question | Return failure without a secret; pending authentication must end |
| Private-key passphrase | Same protected input, but explicitly describe local private-key unlock; show key path from native prompt | Do not imply server rejected a password |
| Host identity/fingerprint confirmation | Separate confirmation dialog with raw fingerprint/question; never a password field | Refuse/return failure rather than silently trust the host |
| Informational/no-response askpass | Native informational surface if requested by OpenSSH; no invented credential input | No secret produced |

The username displayed for a running request is not a second login editor: that
SSH process has already selected its user. Keep it read-only when the native
credential API supports doing so. Do not offer credential persistence or a
remember-password checkbox. A wrong password can cause a new OpenSSH request;
it must retain the same connection identity, not silently mutate the username.

## Ownership and cancellation

The parent submission owns cancellation from authentication through metadata
preparation and transfer. Pending dialogs must not survive cancellation or app
exit. OpenSSH can create an askpass child, so killing only the immediate SSH PID
is insufficient on Windows. The planned process boundary is a private JobObject:
create the client suspended, assign it to the job, then resume it; the job owns
helper descendants before any client code can create a prompt. Close/terminate
that job on cancellation and cleanup before waiting for captured output drains.
Do not use job breakaway flags or place unrelated/user-owned SSH masters in it.

The built-in Windows helper is an early internal execution mode of the current
msync executable. It runs before ordinary CLI parsing/telemetry. GUI DLLs load
only on this path, not eagerly for `--version`, local copies or every worker.
Activate askpass only in an invocation-owned SSH context where REPL editing may
race with a background prompt; do not change the user's process-global SSH
settings. Preserve an explicitly configured external askpass helper.

Unix uses foreground authentication plus invocation-owned multiplexed connections
for interactive background work. Native OpenSSH owns password/noecho behavior.
Existing configured user masters are reused, never stopped by msync cleanup.
Direct CLI foreground requests remain native and cancellable without requiring
a writable scratch directory just to plan remote metadata.
When the Unix REPL cannot create a suitably short owned control socket, finite
operations retain their native foreground behavior. An affected SSH stream plan
must stay in the foreground with an explicit notice rather than racing the
editor for credentials. This fallback must not change the client's working
directory or reinterpret relative IdentityFile/ProxyCommand settings.

## Application home versus SSH configuration

Current msync has no standalone persistent user-settings loader or automatically
created `~/.msync/config.toml`. This must not be represented as an existing
feature. Native SSH settings remain in the OpenSSH configuration (normally
`~/.ssh/config`, or `MSYNC_SSH_CONFIG` for an explicit alternate file).

The user approved `~/.msync/bin/` for separately distributed helper executables.
The application-home layout to use when such artifacts/settings are introduced:

```text
~/.msync/
  config.toml   # msync-owned preferences, once a settings loader is introduced
  bin/          # optional installed helper executables
```

Do not create an empty settings file as a claim of support, duplicate SSH
connection definitions, or store passwords there. The current self-executable
helper does not need a second binary or a new home-directory installation step.
Managed build dependencies remain in the normal project/package-manager layout;
repository tests keep disposable fixtures under project `.temp`.

## Acceptance

- Local native OpenSSH account-password and key-passphrase PTY tests must prove
  successful transfer, wrong-secret retry, no echo, Ctrl-C recovery and a fresh
  successful next submission without a stale cancellation token.
- Real configured-master and FIFO tests must prove external-master survival,
  foreground authentication before editor resume, loss-of-lease failure without
  a hidden background prompt, and owned-process cleanup.
- Windows tests must exercise prompt text/context classification, secret-free
  cancellation/error paths, early-helper dispatch and JobObject descendant
  ownership. Cross-compilation proves signatures/types, not actual desktop UI.
- A real Windows desktop acceptance pass is needed to verify the visible native
  dialog, focus/accessibility, correct native client askpass invocation and
  cancellation with an open window. Do not label Unix or headless evidence as
  completing that visual/native-platform check.

Opt-in automated native-window acceptance lives in `tests/windows_askpass.rs`.
The Windows Actions job explicitly runs its ignored tests, while default local
runs do not open unsolicited dialogs. The tests enumerate only the owned helper
PID's windows, check the branded caption, connection/task/request labels and a
masked password control, then drive the actual dialog's OK/Cancel handlers using
bounded window messages. Acceptance must produce the exact Unicode fixture
response on stdout; cancellation must signal the parent event without response
bytes. Missing desktop access or an unexpected external UI broker is a diagnostic
failure, not a silent skip. No global focus, keyboard injection, clipboard, or
unrelated window manipulation is used. Human focus, screen-reader and visual
usability review remains distinct from these automated native-control checks.
