namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    public static void Validate(NativeProgram program) => ValidateCore(program, null);

    public static void Validate(NativeProgram program, CompilationTrace? trace)
    {
        if (trace?.Detail is not { } detail) { ValidateCore(program, null); return; }
        detail.Run("compiler.verify", (program, detail), static state => ValidateCore(state.program, state.detail));
    }

    private static void ValidateCore(NativeProgram program, CompilationTrace? trace)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (program.Semantics != ModuleSemantics.Current || !Enum.IsDefined(program.Stage))
            throw new MsyncException("Unsupported native IR execution semantics or stage");
        if ((program.Stage == ArtifactStage.Executable) != (program.Context is not null))
            throw new MsyncException("Native IR stage and binding context disagree");
        if ((uint)program.OptimizationLevel > 3 || (uint)program.EntryPoint >= (uint)program.Functions.Count)
            throw new MsyncException("Invalid native IR optimization level or entrypoint");
        if (trace is null) ValidateTypes(program);
        else trace.Run("compiler.verify.types", program, ValidateTypes);
        foreach (NativeConstant constant in program.Constants) ValidateConstant(constant);
        for (int index = 0; index < program.Functions.Count; index++)
        {
            if (trace is null) { ValidateFunction(program, index); continue; }
            trace.Run("compiler.verify.function", (program, index, trace),
                static state => ValidateFunction(state.program, state.index, state.trace),
                attributes: new Dictionary<string, string> { ["function.name"] = program.Functions[index].Name,
                    ["function.id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        foreach (NativeAllocationPlan plan in program.Allocations)
        {
            if (!Enum.IsDefined(plan.Placement) || (uint)plan.Function >= (uint)program.Functions.Count
                || (uint)plan.Layout >= (uint)program.Layouts.Count
                || (uint)plan.Instruction >= (uint)program.Functions[plan.Function].Instructions.Count)
                throw new MsyncException("Invalid native allocation plan");
            NativeInstruction instruction = program.Functions[plan.Function].Instructions[plan.Instruction];
            if (instruction.Op != NativeOp.Allocate && !(instruction.Op == NativeOp.Nop && plan.Placement == NativeAllocationPlacement.Eliminated)
                || instruction.Op == NativeOp.Allocate && instruction.A != plan.Layout)
                throw new MsyncException("Native allocation plan disagrees with its instruction", instruction.Source);
        }
        foreach (int slot in program.SessionRoots ?? [])
            if ((uint)slot >= (uint)program.Functions[program.EntryPoint].Slots.Count)
                throw new MsyncException("Native session root refers to a missing location");
        if (program.SessionDispatches is { } dispatches)
            for (int index = 0; index < dispatches.Count; index++)
            {
                if (dispatches[index].Slot != index) throw new MsyncException("Native session dispatch identities must be dense");
                foreach (var (source, target) in dispatches[index].Targets)
                    ValidateTarget(program, source, target, null);
            }
        ValidatePlans(program);
    }

    private static void ValidateTypes(NativeProgram program)
    {
        foreach (NativeCodeType type in program.Types)
        {
            if (type.IsMachine)
            {
                if (type.Kind != TypeKind.Never || type.Element != -1 || type.Pointee != -1 || type.Layouts is not null)
                    throw new MsyncException("Native machine scalars cannot carry source type, element or referent metadata");
                continue;
            }
            if ((type.Kind & ~TypeKind.Any) != 0 || type.Element < -1 || type.Pointee < -1)
                throw new MsyncException("Native IR contains an unknown builtin type or invalid type reference");
            if (type.Element >= 0)
            {
                TypeIndex(program, type.Element);
                if ((type.Kind & TypeKind.Range) == 0) throw new MsyncException("Only Range types declare an element contract");
            }
            if (type.Pointee >= 0)
            {
                TypeIndex(program, type.Pointee);
                if (type.Kind != TypeKind.Never || type.Element >= 0 || type.Layouts is { Count: > 0 })
                    throw new MsyncException("IR pointers must have only their pointee contract");
            }
            int previous = -1;
            foreach (int layout in type.Layouts ?? [])
            {
                LayoutIndex(program, layout);
                if ((type.Kind & TypeKind.Scope) == 0 || layout <= previous)
                    throw new MsyncException("Namespace layout alternatives must be sorted, unique and belong to a namespace type");
                previous = layout;
            }
        }
        foreach (NativeCodeLayout layout in program.Layouts)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (NativeCodeField field in layout.Fields)
            {
                if (field.Name is null || !names.Add(field.Name)) throw new MsyncException("Native layout fields must have unique names");
                TypeIndex(program, field.Type);
            }
        }
        foreach (NativeProjection projection in program.Projections)
        {
            if (projection.Cases.Count == 0) throw new MsyncException("A native projection requires finite layout alternatives");
            HashSet<int> layouts = [];
            string? name = null;
            foreach (NativeProjectionCase alternative in projection.Cases)
            {
                LayoutIndex(program, alternative.Layout);
                NativeCodeLayout layout = program.Layouts[alternative.Layout];
                if ((uint)alternative.Field >= (uint)layout.Fields.Count || !layouts.Add(alternative.Layout))
                    throw new MsyncException("Invalid or repeated native projection layout alternative");
                string field = layout.Fields[alternative.Field].Name;
                name ??= field;
                if (field != name) throw new MsyncException("Native projection alternatives select different fields");
            }
        }
        HashSet<int> declarations = [];
        foreach (NativeStatic item in program.Statics)
        {
            LayoutIndex(program, item.Layout);
            if (item.Declaration < 0 || !declarations.Add(item.Declaration))
                throw new MsyncException("Native static declarations must have distinct nonnegative identities");
        }
    }

    private static void ValidateFunction(NativeProgram program, int functionIndex, CompilationTrace? trace = null)
    {
        NativeFunctionCode function = program.Functions[functionIndex];
        if (function.Name is null || function.ParameterCount < 0 || function.CaptureCount < 0 || function.IteratorCount < 0
            || (long)function.ParameterCount + function.CaptureCount > function.Slots.Count
            || function.ParameterCount != function.Signature.Parameters.Count
            || function.CaptureCount != function.Signature.Captures.Count)
            throw new MsyncException($"Invalid native frame/signature in function '{function.Name}'");
        TypeIndex(program, function.Signature.Result);
        if (function.ReflectionSignature is { } reflection)
        {
            if (reflection.Parameters is null || reflection.Result is null
                || reflection.Parameters.Count != function.ParameterCount)
                throw new MsyncException($"Invalid native logical reflection signature in '{function.Name}'");
            foreach (TypeRef parameter in reflection.Parameters)
            {
                if (parameter is null) throw new MsyncException($"Missing native reflection parameter type in '{function.Name}'");
                ValidateTypeValue(parameter, legacyMetadata: true);
            }
            ValidateTypeValue(reflection.Result, legacyMetadata: true);
        }
        foreach (NativeSlot slot in function.Slots)
        {
            TypeIndex(program, slot.Type);
            if (!Enum.IsDefined(slot.Role)) throw new MsyncException($"Invalid native slot role in '{function.Name}'");
        }
        for (int index = 0; index < function.CaptureCount; index++)
        {
            NativeCaptureContract capture = function.Signature.Captures[index];
            TypeIndex(program, capture.Type);
            if (!Enum.IsDefined(capture.Mode) || !SameType(program, capture.Type, function.Slots[index].Type))
                throw new MsyncException($"Invalid native capture contract in '{function.Name}'");
        }
        for (int index = 0; index < function.ParameterCount; index++)
        {
            int parameter = function.Signature.Parameters[index];
            TypeIndex(program, parameter);
            if (!Accepts(program, function.Slots[function.CaptureCount + index].Type, parameter))
                throw new MsyncException($"Native parameter contract disagrees with its slot in '{function.Name}'");
        }
        foreach (NativeInstruction instruction in function.Instructions)
            ValidateInstruction(program, functionIndex, function, instruction);
        if (trace is null) { ValidatePointerUses(program, function); ValidateFlow(function); return; }
        trace.Run("compiler.verify.pointers", (program, function), static state => ValidatePointerUses(state.program, state.function));
        trace.Run("compiler.verify.flow", function, ValidateFlow);
    }

    private static void ValidateInstruction(NativeProgram program, int functionIndex,
        NativeFunctionCode function, NativeInstruction instruction)
    {
        if (!Enum.IsDefined(instruction.Op)) throw Error(instruction, $"Unknown native opcode {(byte)instruction.Op}");
        ValidateSpan(instruction.Source);
        if (instruction.Type < -1) throw Error(instruction, "Invalid native instruction type");
        if (instruction.Type >= 0) TypeIndex(program, instruction.Type, instruction.Source);
        if (instruction.Result >= 0)
        {
            Register(function, instruction, instruction.Result);
            if (!NativeInstructionFacts.Schema(instruction.Op).AcceptsResult)
                throw Error(instruction, "This native instruction does not produce a register value");
        }
        else if (NativeInstructionFacts.Schema(instruction.Op).RequiresResult) throw Error(instruction, "A value-producing native instruction needs a result register");
        switch (instruction.Op)
        {
            case NativeOp.Nop: case NativeOp.Barrier: case NativeOp.Unreachable: break;
            case NativeOp.Constant:
                if ((uint)instruction.A >= (uint)program.Constants.Count) throw Error(instruction, "Invalid native constant index");
                if (program.Constants[instruction.A].Kind is NativeKind.MachineInteger or NativeKind.MachineFloat)
                    RequireMachineConstant(program, function, instruction, program.Constants[instruction.A]);
                else RequireResult(program, function, instruction, NativeValue.TypeOfKind(program.Constants[instruction.A].Kind).Kind);
                break;
            case NativeOp.Copy: case NativeOp.Move:
                RequireAssignable(program, function, instruction, instruction.Result, instruction.A);
                break;
            case NativeOp.Drop: Register(function, instruction, instruction.A); break;
            case NativeOp.FrameAddress:
                Register(function, instruction, instruction.A);
                RequirePointer(program, function, instruction, instruction.Result, function.Slots[instruction.A].Type);
                break;
            case NativeOp.BindLocation:
                Register(function, instruction, instruction.A);
                RequirePointer(program, function, instruction, instruction.B, function.Slots[instruction.A].Type);
                break;
            case NativeOp.Allocate:
                LayoutIndex(program, instruction.A, instruction.Source);
                RequireNamespace(program, function, instruction, instruction.Result, instruction.A);
                if (instruction.C >= 0) RequireKind(program, function, instruction, instruction.C, TypeKind.Number);
                else if (instruction.C != -1) throw Error(instruction, "Invalid native allocation count register");
                break;
            case NativeOp.IndexAddress:
                RequireAddress(program, function, instruction, instruction.A);
                RequireKind(program, function, instruction, instruction.B, TypeKind.Number);
                LayoutIndex(program, instruction.C, instruction.Source);
                RequireNamespace(program, function, instruction, instruction.Result, instruction.C);
                break;
            case NativeOp.CopyMemory: case NativeOp.ClearMemory:
                RequireAddress(program, function, instruction, instruction.A);
                RequireKind(program, function, instruction, instruction.C, TypeKind.Number);
                if (instruction.Immediate < 0 || instruction.Immediate >= program.Layouts.Count)
                    throw Error(instruction, "Invalid native memory stride layout");
                if (instruction.Op == NativeOp.CopyMemory) RequireAddress(program, function, instruction, instruction.B);
                break;
            case NativeOp.EnvironmentAddress:
                RequireKind(program, function, instruction, instruction.A, TypeKind.Function);
                LayoutIndex(program, instruction.B, instruction.Source);
                RequireNamespace(program, function, instruction, instruction.Result, instruction.B);
                break;
            case NativeOp.RangeFromMemory:
                RequireAddress(program, function, instruction, instruction.A);
                RequireKind(program, function, instruction, instruction.B, TypeKind.Number);
                LayoutIndex(program, instruction.C, instruction.Source);
                RequireResult(program, function, instruction, TypeKind.Range);
                NativeCodeLayout elementLayout = program.Layouts[instruction.C];
                int elementType = program.Types[function.Slots[instruction.Result].Type].Element;
                if (instruction.Immediate < 0 || instruction.Immediate >= elementLayout.Fields.Count || elementType < 0
                    || !Accepts(program, elementType, elementLayout.Fields[(int)instruction.Immediate].Type))
                    throw Error(instruction, "Native memory Range requires a selected field admitted by its element contract");
                if (instruction.Symbol is not (null or "move"))
                    throw Error(instruction, "Native memory Range has an unknown ownership mode");
                if (instruction.Inputs is { Count: > 1 }) throw Error(instruction, "Native memory Range admits only one logical element descriptor");
                foreach (int descriptor in instruction.Inputs ?? []) RequireKind(program, function, instruction, descriptor, TypeKind.Type);
                break;
            case NativeOp.AddressOffset:
                RequireAddress(program, function, instruction, instruction.A);
                LayoutIndex(program, instruction.B, instruction.Source);
                NativeCodeLayout layout = program.Layouts[instruction.B];
                if ((uint)instruction.C >= (uint)layout.Fields.Count) throw Error(instruction, "Invalid native field ordinal");
                RequirePointer(program, function, instruction, instruction.Result, layout.Fields[instruction.C].Type);
                break;
            case NativeOp.ProjectAddress:
                RequireAddress(program, function, instruction, instruction.A);
                if ((uint)instruction.B >= (uint)program.Projections.Count) throw Error(instruction, "Invalid native projection index");
                int projected = program.Types[function.Slots[instruction.Result].Type].Pointee;
                if (projected < 0) throw Error(instruction, "Native projection result is not a typed pointer");
                foreach (NativeProjectionCase alternative in program.Projections[instruction.B].Cases)
                    if (!Accepts(program, projected, program.Layouts[alternative.Layout].Fields[alternative.Field].Type))
                        throw Error(instruction, "Native projection's logical contract does not admit its selected field");
                break;
            case NativeOp.Load:
                TypeIndex(program, instruction.Type, instruction.Source);
                RequirePointer(program, function, instruction, instruction.A, instruction.Type);
                if (!NativeTypeRelations.SameStorageRepresentation(program.Types, function.Slots[instruction.Result].Type, instruction.Type))
                    throw Error(instruction, "A native load's destination must use the declared storage representation");
                break;
            case NativeOp.Store:
                TypeIndex(program, instruction.Type, instruction.Source);
                RequirePointer(program, function, instruction, instruction.A, instruction.Type);
                Register(function, instruction, instruction.B);
                if (!PhysicalAssignable(program, instruction.Type, function.Slots[instruction.B].Type))
                    throw Error(instruction, "Native stored value violates the location's fixed type");
                if ((uint)instruction.C > 1) throw Error(instruction, "Invalid native store initialization mode");
                break;
            case NativeOp.Jump: Target(function, instruction, instruction.A); break;
            case NativeOp.Branch:
                RequireKind(program, function, instruction, instruction.A, TypeKind.Bool);
                Target(function, instruction, instruction.B); Target(function, instruction, instruction.C);
                break;
            case NativeOp.Unary: ValidateUnary(program, function, instruction); break;
            case NativeOp.Binary: ValidateBinary(program, function, instruction); break;
            case NativeOp.MachineBinary: case NativeOp.MachineCompare: case NativeOp.MachineCast:
            case NativeOp.MachineIntrinsic: case NativeOp.MachineUnary: ValidateMachine(program, function, instruction); break;
            case NativeOp.Select:
                RequireKind(program, function, instruction, instruction.A, TypeKind.Bool);
                RequireAssignable(program, function, instruction, instruction.Result, instruction.B);
                RequireAssignable(program, function, instruction, instruction.Result, instruction.C);
                break;
            case NativeOp.Convert: case NativeOp.Narrow:
                Register(function, instruction, instruction.A);
                TypeIndex(program, instruction.Type, instruction.Source);
                if (!SameType(program, instruction.Type, function.Slots[instruction.Result].Type))
                    throw Error(instruction, "Native conversion/refinement result type disagrees with its register");
                if (instruction.Op == NativeOp.Narrow
                    && !Accepts(program, function.Slots[instruction.A].Type, instruction.Type))
                    throw Error(instruction, "Native narrowing target is not an alternative of its operand");
                break;
            case NativeOp.MakeType:
                if (instruction.Immediate < 0 || instruction.Immediate > ushort.MaxValue
                    || ((TypeKind)instruction.Immediate & ~(TypeKind.Any | TypeKind.Storage)) != 0)
                    throw Error(instruction, "Native Type construction contains an unknown descriptor kind");
                if (instruction.A >= 0)
                {
                    RequireKind(program, function, instruction, instruction.A, TypeKind.Type);
                    if (((TypeKind)instruction.Immediate & (TypeKind.Range | TypeKind.Storage)) == 0)
                        throw Error(instruction, "Native Type construction element requires a container descriptor");
                }
                else if (instruction.A != -1) throw Error(instruction, "Invalid native Type construction element register");
                RequireResult(program, function, instruction, TypeKind.Type);
                break;
            case NativeOp.FunctionComponent: case NativeOp.ComposeFunction:
                ValidateFunctionPart(program, function, instruction); break;
            case NativeOp.MakeFunction: ValidateClosure(program, function, instruction); break;
            case NativeOp.AdaptFunction: ValidateCall(program, function, instruction, invoke: false); break;
            case NativeOp.Call: ValidateCall(program, function, instruction, invoke: true); break;
            case NativeOp.CallNative: ValidateNativeCall(program, function, instruction); break;
            case NativeOp.MakeRange:
                RequireRangeDescriptor(program, function, instruction);
                RequireResult(program, function, instruction, TypeKind.Range);
                foreach (int input in instruction.Inputs ?? []) Register(function, instruction, input);
                break;
            case NativeOp.OpenIterator:
                RequireKind(program, function, instruction, instruction.A, TypeKind.Range);
                Iterator(function, instruction, instruction.B);
                break;
            case NativeOp.NextIterator:
                Iterator(function, instruction, instruction.A); Target(function, instruction, instruction.B);
                break;
            case NativeOp.CloseIterator: Iterator(function, instruction, instruction.A); break;
            case NativeOp.Transfer:
                Register(function, instruction, instruction.A); Register(function, instruction, instruction.B);
                RequireResult(program, function, instruction, TypeKind.Bool);
                break;
            case NativeOp.ProcessCall:
                RequireKind(program, function, instruction, instruction.A, TypeKind.Path | TypeKind.String);
                foreach (int input in instruction.Inputs ?? []) Register(function, instruction, input);
                RequireResult(program, function, instruction, TypeKind.Process);
                break;
            case NativeOp.SelectPath:
                Register(function, instruction, instruction.A);
                if (instruction.Symbol is null) throw Error(instruction, "Path selection is missing its pattern");
                RequireResult(program, function, instruction, TypeKind.Range);
                break;
            case NativeOp.Acquire:
                if (instruction.A >= 0) Register(function, instruction, instruction.A);
                else if (instruction.A != -1) throw Error(instruction, "Invalid acquire operand");
                if (instruction.Result >= 0) RequireAssignable(program, function, instruction, instruction.Result, instruction.A);
                break;
            case NativeOp.ExecutePlan: case NativeOp.PlanResult:
                if (program.ExecutionPlans is not { } plans || (uint)instruction.A >= (uint)plans.Count
                    || plans[instruction.A].Function != functionIndex)
                    throw Error(instruction, "Invalid native execution plan reference");
                if (instruction.Op == NativeOp.PlanResult && plans[instruction.A].ResultTransfers is null)
                    throw Error(instruction, "Observed native plan result requires result transfers");
                break;
            case NativeOp.TestStatic:
                Static(program, instruction, instruction.A);
                RequireResult(program, function, instruction, TypeKind.Bool);
                break;
            case NativeOp.StaticAddress:
                Static(program, instruction, instruction.A);
                RequireNamespace(program, function, instruction, instruction.Result, program.Statics[instruction.A].Layout);
                break;
            case NativeOp.PublishStatic: Static(program, instruction, instruction.A); break;
            case NativeOp.Return:
                Register(function, instruction, instruction.A);
                if (!Accepts(program, function.Signature.Result, function.Slots[instruction.A].Type))
                    throw Error(instruction, "Native return violates its function signature");
                break;
            default: throw Error(instruction, "Native opcode has no validation contract");
        }
    }

    private static void RequireRangeDescriptor(NativeProgram program, NativeFunctionCode function, NativeInstruction instruction)
    {
        if (instruction.C >= 0) RequireKind(program, function, instruction, instruction.C, TypeKind.Type);
        else if (instruction.C != -1) throw Error(instruction, "Invalid native logical Range element descriptor register");
    }

    private static void ValidateUnary(NativeProgram program, NativeFunctionCode function, NativeInstruction instruction)
    {
        if ((ulong)instruction.Immediate > byte.MaxValue || !Enum.IsDefined((NativeUnary)instruction.Immediate))
            throw Error(instruction, "Unknown native unary operation");
        NativeUnary operation = (NativeUnary)instruction.Immediate;
        Register(function, instruction, instruction.A);
        if (operation == NativeUnary.IsType) RequireKind(program, function, instruction, instruction.B, TypeKind.Type);
        if (operation == NativeUnary.HasLayout) LayoutIndex(program, instruction.B, instruction.Source);
        if (operation is NativeUnary.Not or NativeUnary.Negate)
            RequireKind(program, function, instruction, instruction.A, operation == NativeUnary.Not ? TypeKind.Bool : TypeKind.Number);
        RequireResult(program, function, instruction, operation switch
        {
            NativeUnary.Negate => TypeKind.Number,
            NativeUnary.ValueType => TypeKind.Type,
            _ => TypeKind.Bool
        });
    }

    private static void ValidateBinary(NativeProgram program, NativeFunctionCode function, NativeInstruction instruction)
    {
        if ((ulong)instruction.Immediate > byte.MaxValue || !Enum.IsDefined((NativeBinary)instruction.Immediate))
            throw Error(instruction, "Unknown native binary operation");
        NativeBinary operation = (NativeBinary)instruction.Immediate;
        Register(function, instruction, instruction.A); Register(function, instruction, instruction.B);
        if (operation is NativeBinary.BoolAnd or NativeBinary.BoolOr)
        {
            RequireKind(program, function, instruction, instruction.A, TypeKind.Bool);
            RequireKind(program, function, instruction, instruction.B, TypeKind.Bool);
            RequireResult(program, function, instruction, TypeKind.Bool);
        }
        else if (operation is NativeBinary.Subtract or NativeBinary.Multiply or NativeBinary.Divide or NativeBinary.Remainder or NativeBinary.Power)
        {
            RequireKind(program, function, instruction, instruction.A, TypeKind.Number);
            RequireKind(program, function, instruction, instruction.B, TypeKind.Number);
            RequireResult(program, function, instruction, TypeKind.Number);
        }
        else if (operation == NativeBinary.Add)
        {
            RequireKind(program, function, instruction, instruction.A, TypeKind.Number | TypeKind.String);
            RequireKind(program, function, instruction, instruction.B, TypeKind.Number | TypeKind.String);
            TypeKind possible = program.Types[function.Slots[instruction.A].Type].Kind
                & program.Types[function.Slots[instruction.B].Type].Kind;
            if (possible == TypeKind.Never) throw Error(instruction, "Native addition operands have no compatible alternative");
            RequireResult(program, function, instruction, possible);
        }
        else if (operation == NativeBinary.Qualify)
        {
            RequireKind(program, function, instruction, instruction.A, TypeKind.Connection);
            RequireKind(program, function, instruction, instruction.B, TypeKind.Path | TypeKind.String);
            RequireResult(program, function, instruction, TypeKind.Path);
        }
        else if (operation == NativeBinary.PathJoin)
        {
            RequireKind(program, function, instruction, instruction.A, TypeKind.Path | TypeKind.String);
            RequireKind(program, function, instruction, instruction.B, TypeKind.Path | TypeKind.String);
            RequireResult(program, function, instruction, TypeKind.Path);
        }
        else RequireResult(program, function, instruction, TypeKind.Bool);
    }

    private static void ValidateClosure(NativeProgram program, NativeFunctionCode function, NativeInstruction instruction)
    {
        RequireResult(program, function, instruction, TypeKind.Function);
        if (instruction.C is not (-1 or 0)) throw Error(instruction, "Invalid native function environment mode");
        if (instruction.C == 0 && (instruction.B != -1 || instruction.Inputs is { Count: > 0 }))
            throw Error(instruction, "Plain function references cannot carry an environment");
        if (instruction.A == -1)
        {
            Msync.Core.NativeSignature signature = NativeSignatureOf(instruction);
            if (instruction.Inputs is { Count: > 0 }) throw Error(instruction, $"Native symbol '{signature.Name}' cannot contain source captures");
            if (instruction.B >= 0)
            {
                LayoutIndex(program, instruction.B, instruction.Source);
                if (program.Layouts[instruction.B].Fields.Count != 0)
                    throw Error(instruction, "Native symbol requires an empty environment layout");
            }
            else if (instruction.B != -1) throw Error(instruction, "Invalid native environment layout");
            return;
        }
        FunctionIndex(program, instruction.A, instruction.Source);
        NativeFunctionCode target = program.Functions[instruction.A];
        IReadOnlyList<int> captures = instruction.Inputs ?? [];
        if (captures.Count != target.CaptureCount) throw Error(instruction, "Native closure capture count disagrees with target");
        if (instruction.C == 0 && target.CaptureCount != 0)
            throw Error(instruction, "Plain function references require a zero-capture target");
        NativeCodeLayout? environment = null;
        if (instruction.B >= 0)
        {
            LayoutIndex(program, instruction.B, instruction.Source);
            environment = program.Layouts[instruction.B];
            if (environment.Fields.Count != captures.Count)
                throw Error(instruction, "Native explicit environment layout disagrees with capture count");
        }
        else if (instruction.B != -1) throw Error(instruction, "Invalid native environment layout");
        for (int index = 0; index < captures.Count; index++)
        {
            NativeCaptureContract signature = target.Signature.Captures[index];
            int type = environment?.Fields[index].Type ?? signature.Type;
            NativeCodeType field = program.Types[type];
            bool location = environment is null ? signature.Mode == NativeCaptureMode.Location : field.Pointee >= 0;
            int valueType = environment is not null && location ? field.Pointee : type;
            if (target.IsTemplate && (program.Types[signature.Type].Kind & program.Types[valueType].Kind) != program.Types[valueType].Kind)
                throw Error(instruction, "Native template environment violates its abstract builtin capture constraint");
            if (location != (signature.Mode == NativeCaptureMode.Location)
                || !target.IsTemplate && !SameType(program, valueType, signature.Type))
                throw Error(instruction, "Native closed function environment disagrees with its invariant capture signature");
            if (location) RequirePointer(program, function, instruction, captures[index], valueType);
            else
            {
                Register(function, instruction, captures[index]);
                if (!Accepts(program, valueType, function.Slots[captures[index]].Type))
                    throw Error(instruction, "Native captured value violates its fixed type");
            }
        }
    }

    private static void ValidateCall(NativeProgram program, NativeFunctionCode function, NativeInstruction instruction, bool invoke)
    {
        RequireKind(program, function, instruction, instruction.A, TypeKind.Function);
        if (!invoke) RequireResult(program, function, instruction, TypeKind.Function);
        foreach (int argument in instruction.Inputs ?? []) Register(function, instruction, argument);
        if (instruction.B >= 0)
        {
            FunctionIndex(program, instruction.B, instruction.Source);
            if (invoke) ValidateCallTarget(program, function, instruction, instruction.B);
        }
        else if (instruction.B != -1) throw Error(instruction, "Invalid native call target");
        if (instruction.C >= 0)
        {
            if (program.SessionDispatches is not { } dispatches || (uint)instruction.C >= (uint)dispatches.Count)
                throw Error(instruction, "Native call requires its active session dispatch table");
            if (invoke)
                foreach (int target in dispatches[instruction.C].Targets.Values)
                    ValidateCallTarget(program, function, instruction, target);
        }
        else if (instruction.C != -1) throw Error(instruction, "Invalid native session call signature");
        if (instruction.Symbol is { } encoded)
            foreach (var (identity, target) in FunctionDispatch.Targets(encoded))
            {
                ValidateTarget(program, identity, target, instruction.Source);
                if (invoke) ValidateCallTarget(program, function, instruction, target);
            }
    }

    private static void ValidateCallTarget(NativeProgram program, NativeFunctionCode caller, NativeInstruction instruction, int index)
    {
        NativeFunctionCode target = program.Functions[index];
        IReadOnlyList<int> arguments = instruction.Inputs ?? [];
        if (target.IsTemplate || arguments.Count != target.ParameterCount)
            throw Error(instruction, "Native call target is not an executable specialization of this arity");
        for (int argument = 0; argument < arguments.Count; argument++)
            if (!Accepts(program, target.Signature.Parameters[argument], caller.Slots[arguments[argument]].Type))
                throw Error(instruction, "Native call argument violates its parameter contract");
        if (!Accepts(program, caller.Slots[instruction.Result].Type, target.Signature.Result))
            throw Error(instruction, "Native call result violates its destination contract");
    }

    private static void ValidateNativeCall(NativeProgram program, NativeFunctionCode function, NativeInstruction instruction)
    {
        RequireRangeDescriptor(program, function, instruction);
        if (instruction.C >= 0)
        {
            NativeCodeType result = program.Types[function.Slots[instruction.Result].Type];
            if (result.Pointee >= 0 || result.Kind != TypeKind.Range || result.Element < 0)
                throw Error(instruction, "Native logical element descriptor requires an exact Range result with a declared physical element ABI");
        }
        Msync.Core.NativeSignature signature = NativeSignatureOf(instruction);
        IReadOnlyList<int> arguments = instruction.Inputs ?? [];
        if (!signature.Variadic && arguments.Count != signature.Parameters.Count)
            throw Error(instruction, $"Native '{signature.Name}' argument count disagrees with its signature");
        for (int index = 0; index < arguments.Count; index++)
        {
            Register(function, instruction, arguments[index]);
            if (index < signature.Parameters.Count
                && !signature.Parameters[index].Accepts(RuntimeType(program, function.Slots[arguments[index]].Type)))
                throw Error(instruction, $"Native '{signature.Name}' argument type disagrees with its signature");
        }
        if (signature.ResultRule == NativeResultRule.Fixed
            && !RuntimeType(program, function.Slots[instruction.Result].Type).Accepts(signature.Result))
            throw Error(instruction, $"Native '{signature.Name}' result type disagrees with its destination");
    }

    private static Msync.Core.NativeSignature NativeSignatureOf(NativeInstruction instruction)
    {
        if (instruction.Symbol is not { } name || name.StartsWith("arena.", StringComparison.Ordinal)
            || name.StartsWith("storage.", StringComparison.Ordinal) || name.StartsWith("slots.", StringComparison.Ordinal)
            || !NativeSignatures.TryGet(name, out Msync.Core.NativeSignature? signature))
            throw Error(instruction, $"Unknown or retired native operation '{instruction.Symbol}'");
        return signature;
    }

    private static void ValidateTarget(NativeProgram program, int source, int target, SourceSpan? span)
    {
        FunctionIndex(program, source, span); FunctionIndex(program, target, span);
        IReadOnlyList<NativeCaptureContract> left = program.Functions[source].Signature.Captures;
        IReadOnlyList<NativeCaptureContract> right = program.Functions[target].Signature.Captures;
        if (left.Count != right.Count) throw new MsyncException("Native dispatch changes capture arity", span);
        for (int index = 0; index < left.Count; index++)
            if (left[index].Mode != right[index].Mode
                || !program.Functions[source].IsTemplate && !SameType(program, left[index].Type, right[index].Type))
                throw new MsyncException("Native dispatch changes capture layout", span);
    }

    private static void ValidateConstant(NativeConstant value)
    {
        if (value.OriginId < -1 || value.OriginOffset < 0
            || value.Kind != NativeKind.Path && (value.OriginId != -1 || value.OriginOffset != 0))
            throw new MsyncException("Native literal has an invalid definition origin");
        if (value.Kind is not (NativeKind.Null or NativeKind.Bool or NativeKind.Number or NativeKind.String
            or NativeKind.Path or NativeKind.Connection or NativeKind.Type or NativeKind.MachineInteger or NativeKind.MachineFloat))
            throw new MsyncException($"Runtime-owned {value.Kind} cannot appear in a native constant table");
        if (value.Kind is NativeKind.MachineInteger or NativeKind.MachineFloat) _ = value.ToMachineValue();
        if (value.Kind is NativeKind.String or NativeKind.Connection && value.Text is null
            || value.Kind == NativeKind.Path && value.Text is null && value.Path is null)
            throw new MsyncException("Native text/path/connection literal is missing its text");
        if (value.Path is { } path)
        {
            if (value.Kind != NativeKind.Path || value.Text is { } spelling && spelling != path.Raw)
                throw new MsyncException("Native literal Path descriptor disagrees with its kind or spelling");
            ValidatePath(path);
        }
        if (value.Kind == NativeKind.Connection && !Enum.IsDefined(value.ConnectionKind))
            throw new MsyncException("Unknown native Connection kind");
        if (value.Kind == NativeKind.Type && value.Type is null)
            throw new MsyncException("Native Type literal is missing its descriptor");
        if (value.Type is { } type) ValidateTypeValue(type, value.Kind == NativeKind.Type);
    }

    private static void ValidateTypeValue(TypeRef type, bool legacyMetadata = false)
    {
        // Format-6 Type literals can describe retired storage without creating a storage value or IR type.
        TypeKind allowed = legacyMetadata ? TypeKind.Any | TypeKind.Storage : TypeKind.Any;
        TypeKind containers = legacyMetadata ? TypeKind.Range | TypeKind.Storage : TypeKind.Range;
        for (TypeRef? current = type; current is not null; current = current.Element)
            if ((current.Kind & ~allowed) != 0 || current.Element is not null && (current.Kind & containers) == 0)
                throw new MsyncException("Native Type descriptor contains an unknown builtin or invalid element type");
    }

    private static void ValidateSpan(SourceSpan? source)
    {
        if (source is { } span && (span.Source is null || span.Start < 0 || span.Length < 0 || (long)span.Start + span.Length > int.MaxValue))
            throw new MsyncException("Invalid native source origin");
    }

    private static void TypeIndex(NativeProgram program, int index, SourceSpan? span = null)
    { if ((uint)index >= (uint)program.Types.Count) throw new MsyncException($"Invalid native type index {index}", span); }
    private static void LayoutIndex(NativeProgram program, int index, SourceSpan? span = null)
    { if ((uint)index >= (uint)program.Layouts.Count) throw new MsyncException($"Invalid native layout index {index}", span); }
    private static void FunctionIndex(NativeProgram program, int index, SourceSpan? span = null)
    { if ((uint)index >= (uint)program.Functions.Count) throw new MsyncException($"Invalid native function index {index}", span); }
    private static void Register(NativeFunctionCode function, NativeInstruction instruction, int index)
    { if ((uint)index >= (uint)function.Slots.Count) throw Error(instruction, $"Invalid register {index} in native function '{function.Name}'"); }
    private static void Target(NativeFunctionCode function, NativeInstruction instruction, int target)
    { if ((uint)target >= (uint)function.Instructions.Count) throw Error(instruction, $"Invalid native branch target {target}"); }
    private static void Iterator(NativeFunctionCode function, NativeInstruction instruction, int iterator)
    { if ((uint)iterator >= (uint)function.IteratorCount) throw Error(instruction, $"Invalid native iterator index {iterator}"); }
    private static void Static(NativeProgram program, NativeInstruction instruction, int index)
    { if ((uint)index >= (uint)program.Statics.Count) throw Error(instruction, $"Invalid native static index {index}"); }
    private static MsyncException Error(NativeInstruction instruction, string message) => new(message, instruction.Source);

    private static void RequireAssignable(NativeProgram program, NativeFunctionCode function,
        NativeInstruction instruction, int destination, int source)
    {
        Register(function, instruction, destination); Register(function, instruction, source);
        if (!Accepts(program, function.Slots[destination].Type, function.Slots[source].Type))
            throw Error(instruction, "Native value does not fit its destination register");
    }

    private static void RequirePointer(NativeProgram program, NativeFunctionCode function,
        NativeInstruction instruction, int register, int pointee)
    {
        Register(function, instruction, register);
        int actual = program.Types[function.Slots[register].Type].Pointee;
        if (actual < 0 || !SameType(program, actual, pointee))
            throw Error(instruction, "Native pointer's storage contract disagrees with the selected location");
    }

    private static void RequireAddress(NativeProgram program, NativeFunctionCode function,
        NativeInstruction instruction, int register)
    {
        Register(function, instruction, register);
        NativeCodeType type = program.Types[function.Slots[register].Type];
        if (type.Pointee < 0 && type.Kind != TypeKind.Scope)
            throw Error(instruction, "Native address formation requires a typed pointer or namespace reference");
    }

    private static void RequireNamespace(NativeProgram program, NativeFunctionCode function,
        NativeInstruction instruction, int register, int layout)
    {
        RequireResult(program, function, instruction with { Result = register }, TypeKind.Scope);
        IReadOnlyList<int>? domain = program.Types[function.Slots[register].Type].Layouts;
        if (domain is not null && !domain.Contains(layout))
            throw Error(instruction, "Native allocation layout does not fit the namespace reference's alternatives");
    }

    private static void RequireKind(NativeProgram program, NativeFunctionCode function,
        NativeInstruction instruction, int register, TypeKind allowed)
    {
        Register(function, instruction, register);
        NativeCodeType type = program.Types[function.Slots[register].Type];
        if (type.Pointee >= 0 || type.Kind == TypeKind.Never || (type.Kind & ~allowed) != 0)
            throw Error(instruction, $"Native operand requires {allowed}, received {type.Kind}");
    }

    private static void RequireResult(NativeProgram program, NativeFunctionCode function,
        NativeInstruction instruction, TypeKind produced)
    {
        Register(function, instruction, instruction.Result);
        NativeCodeType destination = program.Types[function.Slots[instruction.Result].Type];
        if (destination.Pointee >= 0 || (destination.Kind & produced) != produced)
            throw Error(instruction, $"Native {produced} result does not fit destination {destination.Kind}");
    }

    private static TypeKind Kind(NativeKind kind) => kind <= NativeKind.Address
        ? NativeValue.TypeOfKind(kind).Kind : throw new MsyncException("Unknown native kind");

    private static TypeRef RuntimeType(NativeProgram program, int index)
    {
        List<TypeKind> kinds = [];
        HashSet<int> seen = [];
        for (int next = index; next >= 0; next = program.Types[next].Element)
        {
            if (!seen.Add(next)) throw new MsyncException("Recursive Range element type cannot be projected into a finite builtin descriptor");
            NativeCodeType type = program.Types[next];
            if (type.IsMachine) throw new MsyncException("A machine scalar is not a source builtin type");
            if (type.Pointee >= 0) throw new MsyncException("An IR pointer is not a source builtin type");
            kinds.Add(type.Kind);
        }
        TypeRef? result = null;
        for (int item = kinds.Count - 1; item >= 0; item--) result = new(kinds[item], result);
        return result!;
    }

    private static bool SameType(NativeProgram program, int left, int right) =>
        Accepts(program, left, right) && Accepts(program, right, left);

    private static bool Accepts(NativeProgram program, int destination, int source) =>
        NativeTypeRelations.Accepts(program.Types, destination, source);

}
