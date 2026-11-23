using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallPathQualification(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirCall call,
        byte* frame, bool rightText, HpirPathAnchors anchors, ExecutionOptions? options)
    {
        var endpoint = new HpirConnectionValue(Read<nint>(frame, call.Arguments[1]));
        HpirPathValue input = PathOperand(frame, call.Arguments[2], rightText, anchors);
        RuntimeTimeline? timeline = options?.Timeline;
        using RuntimeTimelineScope? trace = timeline is null ? null
            : new HpirWorkSite(timeline, diagnostics.Source(active->Function, pc), active->Function, pc).Begin("hpir.path.qualify");
        if (trace is not null)
        {
            trace.SetAttribute("endpoint.kind", endpoint.Kind.ToString());
            trace.SetAttribute("endpoint.address", endpoint.AddressText.ToString());
        }
        try
        {
            HpirPathValue value = endpoint.Kind switch
            {
                ConnectionKind.Ssh or ConnectionKind.Wsl => QualifyRemote(input, endpoint),
                ConnectionKind.Local when endpoint.AddressText.IsEmpty => QualifyLocal(input, endpoint, anchors),
                _ => QualifyContextPath(program, ref heap, active, diagnostics, counts, pc, input, endpoint, anchors, options)
            };
            Unsafe.WriteUnaligned((void*)Read<nint>(frame, call.Arguments[0]), value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or MsyncException { Span: null })
        {
            var failure = new MsyncException(error.Message, diagnostics.Source(active->Function, pc), error);
            trace?.Fail(failure);
            throw failure;
        }
    }

    private static HpirPathValue QualifyRemote(HpirPathValue path, HpirConnectionValue endpoint) => new()
    {
        Raw = path.Raw,
        Endpoint = endpoint.Kind,
        EndpointAddress = endpoint.Address,
        Qualified = 1
    };

    private static HpirPathValue QualifyLocal(HpirPathValue path, HpirConnectionValue endpoint, HpirPathAnchors anchors)
    {
        path.Endpoint = ConnectionKind.Local;
        path.EndpointAddress = endpoint.Address;
        path.Qualified = 1;
        if (path.Directory == 0) path.Directory = anchors.Directory;
        if (path.Home == 0) path.Home = anchors.Home;
        return path;
    }

    private static HpirPathValue QualifyContextPath(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirPathValue path,
        HpirConnectionValue endpoint, HpirPathAnchors anchors, ExecutionOptions? options)
    {
        var context = new NativeRuntimeContext(options, anchors.Binding(program.Context));
        string raw = HpirTextStorage.Read(path.Raw).ToString();
        NativePathValue projected = endpoint.Kind switch
        {
            ConnectionKind.Local => NativeBuiltins.QualifyLocal(path.ToHost(contextual: true), endpoint.ToHost(), context),
            ConnectionKind.SearchPath => new(NativeBuiltins.ResolvePathName(raw, context)),
            ConnectionKind.Environment => new(Environment.GetEnvironmentVariable(raw)
                ?? throw new MsyncException($"Environment variable '{raw}' is not set")),
            _ => throw new InvalidOperationException("Unknown HPIR Connection kind.")
        };
        StorePathProjection(program, ref heap, active, pc, diagnostics, counts, ref path,
            projected.Raw, projected.LocalSpelling);
        return projected.Connection.HasValue ? QualifyLocal(path, endpoint, anchors)
            : anchors.Capture(path.Raw);
    }
}
