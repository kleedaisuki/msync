using System.Text;
using Msync.Core;

namespace Msync.Runtime;

internal static class TransferTimeline
{
    internal static void Source(RuntimeTimelineScope span, Value source)
    {
        if (source is PathValue path) Path(span, "source", path);
        else if (source is StringValue text)
        {
            span.SetAttribute("source.kind", "literal");
            span.SetMetric("source.utf8_bytes", Encoding.UTF8.GetByteCount(text.Data));
        }
        else span.SetAttribute("source.kind", source.Type.ToString());
    }

    internal static void Path(RuntimeTimelineScope span, string role, PathValue path)
    {
        span.SetAttribute(role + ".path", path.Raw);
        if (path.LocalSpelling is { } spelling) span.SetAttribute(role + ".native_spelling", spelling);
        if (path.WorkingDirectory is { } directory) span.SetAttribute(role + ".anchor", directory);
        Endpoint(span, role, path.Connection);
    }

    internal static void Endpoint(RuntimeTimelineScope span, string role, ConnectionValue? connection)
    {
        span.SetAttribute(role + ".endpoint_kind", (connection?.Kind ?? ConnectionKind.Local).ToString());
        if (connection is not { Address.Length: > 0 }) return;
        string address = connection.Address;
        if (connection.Kind == ConnectionKind.Ssh)
        {
            // Keep aliases, user and host/port, but not URI user-info passwords.
            int at = address.LastIndexOf('@');
            int start = address.StartsWith("ssh://", StringComparison.Ordinal) ? 6 : 0;
            int colon = address.IndexOf(':', start);
            if (colon >= start && colon < at) address = address[..colon] + address[at..];
        }
        span.SetAttribute(role + ".endpoint", address);
    }

}
