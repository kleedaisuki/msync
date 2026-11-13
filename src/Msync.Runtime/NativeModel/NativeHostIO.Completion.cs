using System.Diagnostics;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeHostIO
{
    public static ProcessStartInfo CreateRemoteShellProcessStartInfo(NativeConnection connection) =>
        NativeTransferProtocol.CreateShellProcessStartInfo(connection.Kind, connection.Address,
            "exec sh -s", [], redirectInput: true);
}
