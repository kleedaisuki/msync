using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed partial class NativePreparationState
{
    private bool FunctionPart(NativeInstruction instruction)
    {
        trace?.Metric("function_parts.queries", 1);
        if (instruction.Op == NativeOp.FunctionComponent)
        {
            if (!Known(instruction.A, out var function)) return Unknown();
            SetComputed(instruction, NativeFunctionParts.Component(function, (int)instruction.Immediate));
            trace?.Metric("function_parts.component_computed", 1);
        }
        else
        {
            if (!Arguments(instruction.Inputs, out var inputs)) return Unknown();
            SetComputed(instruction, NativeFunctionParts.Compose(inputs[0], inputs[1], inputs[2],
                image.EnvironmentLayoutResolver));
            trace?.Metric("function_parts.environment_storage_schema", inputs[1].Kind == NativeKind.Null ? 0 : 1);
            trace?.Metric("function_parts.compose_computed", 1);
        }
        return true;

        bool Unknown()
        {
            frame.Drop(instruction.Result);
            trace?.Metric("function_parts.unknown_operand", 1);
            return false;
        }
    }
}
