using Msync.Core.Llvm;

namespace Msync.Core.Native;

public static class NativeFunctionParts
{
    private static readonly NativeMachineType CodeType = NativeMachineType.Integer(64, 8);

    public static NativeValue Component(NativeValue value, int index)
    {
        NativeFunction function = value.AsFunction();
        return index switch
        {
            0 => NativeValue.FromMachineInteger(new LlvmAPInt(64, unchecked((ulong)((long)function.Function + 1))), CodeType),
            1 => function.Environment is { } environment ? NativeValue.FromAddress(environment.Address) : NativeValue.Null,
            2 => function.NativeName is { } name ? NativeValue.FromString(name) : NativeValue.Null,
            _ => throw new InvalidOperationException("Unknown compiled Function component")
        };
    }

    public static NativeValue Compose(NativeValue code, NativeValue environment, NativeValue name,
        NativeEnvironmentLayout[] environments) => Compose(code, environment, name, environments, null);

    public static NativeValue Compose(NativeValue code, NativeValue environment, NativeValue name,
        Func<NativeAddress, NativeEnvironmentLayout> environmentLayout) =>
        Compose(code, environment, name, [], environmentLayout);

    public static NativeValue Compose(NativeValue code, NativeValue environment, NativeValue name,
        NativeEnvironmentLayout[] environments, Func<NativeAddress, NativeEnvironmentLayout>? environmentLayout)
    {
        int target = unchecked((int)(code.AsMachineInteger().GetZExtValue() - 1));
        NativeEnvironment? captures = null;
        if (environment.Kind != NativeKind.Null)
        {
            NativeAddress address = environment.AsAddress();
            NativeEnvironmentLayout layout = environmentLayout is not null ? environmentLayout(address)
                : name.Kind == NativeKind.Null ? environments[target] : address.Allocation.Layout.EnvironmentSchema!;
            captures = NativeEnvironment.View(layout, address);
        }
        return NativeValue.FromFunction(new(target, captures, name.Kind == NativeKind.Null ? null : name.AsString()));
    }
}
