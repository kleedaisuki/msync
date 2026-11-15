using Msync.Core.Llvm;
using Msync.Core.Native;

internal static class FunctionEnvironmentProperties
{
    internal static int Run()
    {
        NativeType number = NativeType.Of(NativeKind.Number);
        NativeType address = NativeType.Of(NativeKind.Address);
        var cell = new NativeAllocation(new([new NativeField("cell", number)])).Field(0);
        cell.Store(NativeValue.FromNumber(7));
        var contract = new NativeEnvironmentLayout([new(number, Location: true)]);
        NativeLayout combined = NativeLayout.Compose([new([new NativeField("prefix", number)]), contract.Storage], out var offsets);
        var row = new NativeAllocation(combined);
        NativeAddress baseAddress = row.AddressAt(offsets[1]);
        baseAddress.OffsetBy(contract.Storage[0].Offset, address).Store(NativeValue.FromAddress(cell));
        var code = NativeValue.FromMachineInteger(new LlvmAPInt(64, 1));
        NativeValue function = NativeFunctionParts.Compose(code, NativeValue.FromAddress(baseAddress), NativeValue.Null, [contract]);
        NativeEnvironment environment = function.AsFunction().Environment!;
        Program.Require(environment.Address == baseAddress && ReferenceEquals(environment.Address.Allocation, row),
            "Packing a Function must retain the existing capture row, including its interior offset.");
        Program.Require(environment[0].IsLocation && environment[0].Location == cell && environment[0].Read().AsNumber() == 7,
            "A view must interpret the row as captured locations, never by-value copies.");
        cell.Store(NativeValue.FromNumber(19));
        Program.Require(environment[0].Read().AsNumber() == 19, "Mutations through the original location remain observable.");
        Program.Require(NativeFunctionParts.Component(function, 0).AsMachineInteger() == new LlvmAPInt(64, 1) &&
            NativeFunctionParts.Component(function, 1).AsAddress() == baseAddress && NativeFunctionParts.Component(function, 2).Kind == NativeKind.Null,
            "Code, environment and nullable name projections preserve their exact carriers.");
        NativeValue named = NativeFunctionParts.Compose(NativeValue.FromMachineInteger(LlvmAPInt.GetZero(64)),
            NativeValue.Null, NativeValue.FromString("io.write_text"), []);
        Program.Require(named.AsFunction() is { Function: -1, Environment: null, NativeName: "io.write_text" },
            "A zero code token preserves the existing named-native Function ABI.");
        var publicEnvironment = new NativeEnvironment(contract, [new NativeCapture(cell)]);
        NativeValue publicNamed = NativeValue.FromFunction(new(-1, publicEnvironment, "io.write_text"));
        NativeValue repacked = NativeFunctionParts.Compose(NativeFunctionParts.Component(publicNamed, 0),
            NativeFunctionParts.Component(publicNamed, 1), NativeFunctionParts.Component(publicNamed, 2), []);
        Program.Require(repacked.AsFunction().NativeName == "io.write_text" &&
            repacked.AsFunction().Environment!.Address == publicEnvironment.Address &&
            repacked.AsFunction().Environment![0].Location == cell,
            "Named-native public Function environments preserve their existing schema and locations.");
        NativeValue namedWithCode = NativeValue.FromFunction(new(int.MaxValue, publicEnvironment, "io.write_text"));
        NativeValue codeRepacked = NativeFunctionParts.Compose(NativeFunctionParts.Component(namedWithCode, 0),
            NativeFunctionParts.Component(namedWithCode, 1), NativeFunctionParts.Component(namedWithCode, 2), []);
        Program.Require(codeRepacked.AsFunction().Function == int.MaxValue && codeRepacked.AsFunction().NativeName == "io.write_text" &&
            codeRepacked.AsFunction().Environment!.Address == publicEnvironment.Address,
            "The existing named-native dispatch ABI does not reinterpret its unused code field as a source function contract.");
        return 7;
    }
}
