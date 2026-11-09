
namespace Msync.Cli;

internal enum TerminalInputKind { Text, Cancelled, EndOfInput }

internal readonly record struct TerminalInput(TerminalInputKind Kind, string Text, CancellationToken CancellationToken);
