using System.Runtime.CompilerServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeStep ExecuteFunctionComponent(NativeInterpreter owner, Activation activation,
        in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        owner.ProjectFunctionComponent(activation, instruction, pc);
        return new(pc + instruction.Span);
    }

    private void ProjectFunctionComponent(Activation activation, in NativeExecutionInstruction instruction, int pc)
    {
        NativeDatum function = activation.Frame.GetDatum(instruction.A);
        bool exactAddress = function.IsReady && instruction.Immediate == 1 &&
            activation.Frame.Location(instruction.Result).Type.Kinds == 1UL << (int)NativeKind.Address;
        activation.Work.Counters.FunctionComponent(function.IsReady, exactAddress);
        if (exactAddress)
        {
            activation.Frame.SetPointer(instruction.Result, function.Value.AsFunction().Environment!.Address);
            return;
        }
        if (function.IsReady)
        {
            activation.Frame.Set(instruction.Result, NativeFunctionParts.Component(function.Value, (int)instruction.Immediate));
            return;
        }
        NativeDatum result = function.MapMachine((int)instruction.Immediate,
            static (value, index) => NativeFunctionParts.Component(value, index),
            image.Type(activation.Frame.Type(instruction.Result)), activation.Execution.Metadata[pc].Source);
        activation.Frame.SetDatum(instruction.Result, Track(result));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeStep ExecuteComposeFunction(NativeInterpreter owner, Activation activation,
        in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeInstruction metadata = activation.Execution.Metadata[pc];
        IReadOnlyList<int> inputs = metadata.Inputs!;
        NativeDatum code = activation.Frame.GetDatum(inputs[0]);
        NativeDatum environment = activation.Frame.GetDatum(inputs[1]);
        NativeDatum name = activation.Frame.GetDatum(inputs[2]);
        bool ready = code.IsReady && environment.IsReady && name.IsReady;
        activation.Work.Counters.FunctionCompose(ready);
        if (ready)
        {
            NativeValue function = NativeFunctionParts.ComposeGuest(code.Value, environment.Value, name.Value,
                owner.image.EnvironmentLayoutResolver, out bool? reused);
            if (reused is { } recycled) activation.Work.Counters.NativeGuestEnvironment(recycled);
            if (function.AsFunction().Environment is { } captures) owner.PublishMachineCaptures(captures);
            activation.Frame.Set(instruction.Result, function);
        }
        else activation.Frame.SetDatum(instruction.Result, owner.Track(NativeDatum.ComposeFunction(code, environment, name,
            metadata.Source, owner.image.EnvironmentLayoutResolver)));
        return new(pc + instruction.Span);
    }
}
