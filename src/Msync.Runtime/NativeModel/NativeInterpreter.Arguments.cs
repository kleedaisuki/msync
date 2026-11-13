using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private void ValidateHostPointerArguments(NativeValue callable, IReadOnlyList<NativeValue> arguments)
    {
        NativeFunction selected = callable.AsFunction();
        if (selected.NativeName is not null) return;
        if ((uint)selected.Function >= (uint)image.Program.Functions.Count)
            throw new MsyncException($"No native function exists at index {selected.Function}.");
        NativeFunctionCode function = image.Program.Functions[selected.Function];
        if (function.CaptureCount != (selected.Environment?.Count ?? 0))
            throw new MsyncException($"Function '{function.Name}' requires {function.CaptureCount} captures.");
        for (int index = 0; index < function.CaptureCount; index++)
        {
            NativeCaptureContract capture = function.Signature.Captures[index];
            NativeCaptureLayout actual = selected.Environment!.Layout.Captures[index];
            if (actual.Location != (capture.Mode == NativeCaptureMode.Location) || !actual.Type.Equals(image.Type(capture.Type)))
                throw new MsyncException($"Function '{function.Name}' capture {index + 1} has an incompatible physical ABI.");
        }
        for (int index = 0; index < Math.Min(arguments.Count, function.ParameterCount); index++)
        {
            NativeCodeType parameter = image.Program.Types[function.Signature.Parameters[index]];
            if (parameter.Pointee >= 0)
                ValidateHostPointer(function.Name, index, parameter.Pointee, arguments[index]);
            else if (arguments[index].Kind == NativeKind.Address && parameter.Layouts is not null
                && !image.AdmitsLayout(function.Signature.Parameters[index], arguments[index].AsAddress()))
                throw new MsyncException($"Function '{function.Name}' argument {index + 1} is outside its admitted namespace layouts.");
        }
    }

    private void ValidateHostPointer(string function, int parameter, int pointee, NativeValue value)
    {
        NativeType expected = image.Type(pointee);
        string requirement = $"Function '{function}' argument {parameter + 1} requires an invariant pointer to {expected}";
        if (value.Kind != NativeKind.Address)
            throw new MsyncException($"{requirement}; received {value.Type}.");
        NativeAddress address = value.AsAddress();
        if (address.StorageType is { } actual && !actual.Equals(expected))
            throw new MsyncException($"{requirement}; received a pointer to {actual}.");
        try { NativeStorage.ValidateAddress(expected, address); }
        catch (ArgumentException error)
        { throw new MsyncException($"{requirement}; the supplied location has an incompatible physical layout.", inner: error); }
    }
}
