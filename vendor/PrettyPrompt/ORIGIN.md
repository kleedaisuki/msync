# PrettyPrompt 6.0.5 with opt-in inline hints

Upstream: https://github.com/waf/PrettyPrompt

Pinned revision: `b7bbd3da5fbc1af53d1e0e6977ce43760a138ca1`, the repository
revision recorded by the PrettyPrompt 6.0.5 NuGet package previously used here.
Files under this directory originate from its `src/PrettyPrompt` subtree, with
the upstream MPL-2.0 license and individual file notices retained. Git applies
this repository's ordinary C# LF policy; the license retains its original bytes.

## Why this source dependency exists

The pinned public callbacks support completion and overload panes but not inline
presentation-only suffixes. msync needs the Rust baseline's distinction between
an insertable keyword/history suggestion and noninsertable contextual help.
Drawing these outside PrettyPrompt would create a second cursor, resize, erase
and input-ownership protocol. Instead this small extension uses the same native
screen areas, pooled cells, grapheme widths and incremental renderer.

## Local modifications

- `PromptCallbacks.cs`: optional `PromptHint` and `GetHintAsync`; the interface
  supplies a default implementation and existing callbacks remain valid.
- `PromptConfiguration.cs`: opt-in init-only `EnableHints`; the original
  constructor and default behavior remain unchanged.
- `History/HistoryLog.cs`: prefix suggestions reuse the existing loaded history.
- `Prompt.cs`: compute suggestions only at the document end; an explicit right
  arrow or End accepts only an insertable suffix. Enter, clipboard, history and
  submitted text never include unaccepted hints. A foreground interaction API
  lends the terminal while an awaited callback is paused, then reanchors and
  repaints the original editor document without putting credentials in it.
- `Rendering/Renderer.cs`: hints are extra native screen cells, clipped by the
  existing grapheme-width helper. Submission removes them before the final
  repaint. Foreground authentication uses the same renderer's suspend/restart.
- `Panes/CodePane.cs`: reanchor the existing document after foreground output;
  no second input reader or terminal renderer is introduced.
- `PrettyPrompt.csproj`: inherit neither msync's copyright nor its analyzer
  policy; keep the original runtime/source dependencies TextCopy 6.2.1 and
  Wcwidth.Sources 4.0.1. The NuGet-publication-only SourceLink reference is not
  needed for this source dependency (its pinned build tool also has NU1902).
  All generic/Linux/Windows resolution graphs are committed as lock files.

Unique static completion suggestions precede history; history precedes
noninsertable context. Hinting is disabled by default and when `NO_COLOR` is
present. Filesystem/network completion is an explicit application request, not
per-keystroke hint work. Modified files carry nearby modification notices.

The native distribution includes this exact corresponding source subtree under
its third-party attribution directory, including this note, project and license.
msync remains version 0.8.0; the dependency retains its upstream assembly version.
