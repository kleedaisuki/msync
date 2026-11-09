namespace Msync.Cli;

internal sealed record TelemetryPolicy(int QueueCapacity, int BatchSize, int BusyTimeoutMs, long MaxSessions,
    long MaxRecords, long MaxDatabaseBytes, bool SynchronousFull)
{
    public static TelemetryPolicy From(Configuration configuration)
    {
        int queue = checked((int)configuration.TelemetryUnsigned("queue_capacity"));
        if (queue == 0) throw new ArgumentException("telemetry.queue_capacity must be positive");
        return new(queue, checked((int)configuration.TelemetryUnsigned("batch_size")),
            checked((int)configuration.TelemetryUnsigned("busy_timeout_ms")),
            checked((long)configuration.TelemetryUnsigned("max_sessions")),
            checked((long)configuration.TelemetryUnsigned("max_records_per_session")),
            checked((long)configuration.TelemetryUnsigned("max_database_bytes")),
            configuration.TelemetryBoolean("synchronous_full"));
    }
}
