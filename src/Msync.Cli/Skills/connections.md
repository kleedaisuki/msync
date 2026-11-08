# Connections: native SSH and WSL

Connection is a physical endpoint, distinct from lexical Scope. `$local`, `$wsl`
and `ssh://...` produce Connection values. Qualify a Path with `::`.

```msync
let remote = ssh://forge;
let config = ~/.codex;
config -> remote::config;
$wsl::~/.codex -> ./from-wsl;
```

A bare `ssh://forge` retains native SSH configuration: aliases, agents and
interactive authentication belong to the actual selected operation. Merely
checking or binding a program does not authenticate. An unselected branch
does not open an unrelated connection.

One finite SSH transfer owns one native client/channel for metadata and bytes.
WSL uses the native Windows transport and an installed distribution. Run
`msync doctor` to see whether optional `ssh` and `wsl` tools are on PATH.

```msync
let remote = ssh://user@host:2222;
let source = ~/.codex;
if exists source { source -> remote::~/.codex };
```

`connection::path` uses the selected endpoint's path grammar. Keep remote POSIX
paths separate from Windows drive spelling. Use a Scope member for a lexical
setting and a Connection for endpoint qualification; changing a name's spelling
alone does not change its type.
