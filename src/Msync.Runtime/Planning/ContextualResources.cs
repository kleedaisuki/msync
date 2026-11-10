using Msync.Core;

namespace Msync.Runtime;

public sealed class ContextualResources(CancellationToken ownerCancellation = default) : IAsyncDisposable
{
    private sealed class QueryScope : IDisposable
    {
        internal static readonly QueryScope Instance = new();
        public void Dispose() { }
    }
    private readonly Dictionary<ConnectionValue, TransferEngine.RemoteTransferSession> sessions = [];
    private readonly Lock ownership = new();
    internal bool HasSessions => sessions.Count != 0;

    public TransferEngine.RemoteTransferSession GetSession(ConnectionValue connection,
        CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled) cancellationToken = ownerCancellation;
        lock (ownership)
        {
            if (sessions.TryGetValue(connection, out var session) && session.IsActive) return session;
            if (session is not null)
            {
                sessions.Remove(connection);
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            sessions.Add(connection, session = TransferEngine.OpenSession(connection, cancellationToken));
            return session;
        }
    }

    public IDisposable Enter(ConnectionValue connection, CancellationToken cancellationToken = default) =>
        TransferEngine.HasSelectedQuery(connection) ? QueryScope.Instance
            : TransferEngine.UseSession(GetSession(connection, cancellationToken));

    public async ValueTask DisposeAsync()
    {
        foreach (var session in sessions.Values) await session.DisposeAsync();
        sessions.Clear();
    }
}
