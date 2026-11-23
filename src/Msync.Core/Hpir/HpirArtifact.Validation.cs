namespace Msync.Core.Hpir;

public static partial class HpirArtifact
{
    private static void Validate(HpirProgram program, CompilationTrace? trace)
    {
        ValidateStage(program);
        if ((uint)program.EntryPoint >= (uint)program.Functions.Count || program.Functions[program.EntryPoint].Parameters.Count != 0)
            throw new MsyncException("Invalid HPIR entry point");
        foreach (var layout in program.Layouts) ValidateLayout(layout);
        foreach (var global in program.Globals)
            if (global.Declaration < 0 || (uint)global.Layout >= (uint)program.Layouts.Count)
                throw new MsyncException("Invalid HPIR global layout");
        for (int index = 0; index < program.TypeLiterals.Count; ++index)
        {
            var type = program.TypeLiterals[index];
            if ((type.Kinds & ~TypeKind.Runtime) != 0 || type.Element < -1 || type.Element >= index)
                throw new MsyncException("Invalid HPIR type literal");
        }
        foreach (int type in program.TypeCells)
            if ((uint)type >= (uint)program.TypeLiterals.Count) throw new MsyncException("Invalid HPIR addressable type literal");
        if (program.TextLayout != -1 && ((uint)program.TextLayout >= (uint)program.Layouts.Count
                || !HpirScalarImports.IsTextLayout(program.Layouts[program.TextLayout])))
            throw new MsyncException("Invalid HPIR Text layout");
        foreach (var import in program.Imports)
            if (!ValidImport(program, import))
                throw new MsyncException("Invalid HPIR runtime import ABI");
        int instructions = 0, calls = 0, maps = 0;
        foreach (var function in program.Functions)
        {
            ValidateFunction(program, function);
            instructions += function.Instructions.Length;
            calls += function.Calls.Count;
            maps += function.Safepoints.Count;
        }
        trace?.Metric("validation.instructions", instructions);
        trace?.Metric("validation.calls", calls);
        trace?.Metric("validation.layouts", program.Layouts.Count);
        trace?.Metric("validation.imports", program.Imports.Count);
        trace?.Metric("validation.safepoints", maps);
    }

    private static bool ValidImport(HpirProgram program, HpirImport import) => import.Service switch
    {
        HpirService.GuestNumber => HpirGuestImports.Valid(program, import),
        HpirService.LivePathBatch => HpirPathBatchImports.Valid(program, import),
        HpirService.AppendTextChunk => HpirTextChunkImports.Valid(program, import),
        HpirService.Work => HpirWorkImports.Valid(import),
        HpirService.RangeSequence or HpirService.RangeDeferredSequence or HpirService.RangeDrop or HpirService.RangeTake
            or HpirService.RangeOpen => HpirRangeImports.Valid(import, program.Layouts),
        HpirService.RangeTransform or HpirService.RangeTransformTake or HpirService.RangeTransformOpen
            or HpirService.RangeBufferGrow or HpirService.RangePackWords or HpirService.RangeBound
            => HpirTransformImports.Valid(program, import),
        HpirService.LiveTransfer or HpirService.LiveTransferFutureText or HpirService.LiveTransferBoolText
            or HpirService.LiveTransferNumberText or HpirService.LiveTransferSelection =>
            HpirLiveTransferImports.Valid(program, import),
        HpirService.TypeName => import.Binding >= 0 && import.Binding <= program.TextLiterals.Count - program.TypeLiterals.Count
            && program.TextLayout >= 0 && import.Result == HpirWidth.U64 && import.Arguments is [HpirWidth.U64],
        HpirService.TypeAccepts => import.Binding == 0 && import.Result == HpirWidth.U8
            && import.Arguments is [HpirWidth.U64, HpirWidth.U64],
        HpirService.ContextCheck => (uint)import.Binding < (uint)program.ContextChecks.Count
            && import.Result == HpirWidth.U8 && import.Arguments.Length == 0,
        HpirService.FailDiagnostic => (uint)import.Binding < (uint)program.TextLiterals.Count
            && import.Result == HpirWidth.Void && import.Arguments.Length == 0,
        HpirService.FileMetadata => HpirFileMetadataImports.Valid(program, import),
        HpirService.RenameFile => HpirFileRenameImports.Valid(program, import),
        HpirService.Path => HpirPathImports.Valid(program, import),
        HpirService.HasLayout => (uint)import.Binding < (uint)program.Layouts.Count
            && import.Result == HpirWidth.U8 && import.Arguments is [HpirWidth.U64],
        HpirService.AllocateObject => (uint)import.Binding < (uint)program.Layouts.Count
            && import.Result == HpirWidth.U64 && import.Arguments.Length == 0,
        HpirService.Word32 => HpirWord32Imports.Valid(import),
        HpirService.String => HpirStringImports.Valid(program, import),
        HpirService.Regex => HpirRegexImports.Valid(program, import),
        HpirService.RegexFind => HpirRegexResultImports.Valid(program, import),
        HpirService.PathSelection => HpirPathSelectionImports.Valid(program, import),
        HpirService.DirectorySource or HpirService.DirectoryOpen or HpirService.DirectoryNext or HpirService.DirectoryClose
            => HpirDirectoryImports.Valid(program, import),
        HpirService.TextRangeSource or HpirService.TextRangeOpen or HpirService.TextRangeNext or HpirService.TextRangeClose
            or HpirService.TextRangeJoin
            => HpirTextRangeImports.Valid(program, import),
        HpirService.MathF64 => HpirScalarImports.ValidMath(import),
        HpirService.FutureMath => HpirFutureMathImports.Valid(program, import),
        HpirService.FormatF64 => (uint)import.Binding < (uint)program.Layouts.Count
            && HpirScalarImports.IsTextLayout(program.Layouts[import.Binding])
            && import.Result == HpirWidth.U64 && import.Arguments is [HpirWidth.U64],
        HpirService.ParseF64 or HpirService.ParsePathF64 => import.Binding == 0 && import.Result == HpirWidth.U64
            && import.Arguments is [HpirWidth.U64],
        HpirService.ParseBool or HpirService.ParsePathBool => import.Binding == 0 && import.Result == HpirWidth.U8
            && import.Arguments is [HpirWidth.U64],
        HpirService.TextFormat => HpirFormatImports.Valid(program, import),
        HpirService.WriteFileText => HpirFileTextWriteImports.Valid(program, import),
        HpirService.FileBytesOpen or HpirService.FileBytesChunk => HpirFileByteImports.Valid(import),
        HpirService.ReadLineText or HpirService.ReadStdinText or HpirService.ReadFileText or HpirService.DemandText or HpirService.TextIsNull or HpirService.TextParseNumber
            or HpirService.ConcatTextPending
            or HpirService.ProcessStdout or HpirService.ProcessStderr =>
            HpirTextInputImports.Valid(program, import),
        HpirService.WriteText => (uint)import.Binding <= (uint)HpirTextOutput.FlushError && import.Result == HpirWidth.Void
            && (HpirScalarImports.FlushOnly((HpirTextOutput)import.Binding)
                ? import.Arguments.Length == 0 : import.Arguments is [HpirWidth.U64]),
        HpirService.Transfer or HpirService.SharedTransfer => ValidPreparedTransfer(program, import),
        HpirService.Fail => import.Result == HpirWidth.Void
            && (import.Binding == (int)HpirFailure.RangeIndex ? import.Arguments is [HpirWidth.U64]
                : (uint)import.Binding <= (uint)HpirFailure.RangeIndexStalled && import.Arguments.Length == 0),
        HpirService.FutureLift or HpirService.FutureDemand or HpirService.ProcessStart
            or HpirService.ProcessExit or HpirService.ProcessSuccess => HpirFutureImports.Valid(program, import),
        HpirService.ProcessSpawn or HpirService.ProcessControl => HpirProcessLibraryImports.Valid(program, import),
        HpirService.ProcessTextReady => (uint)import.Binding <= 1 && program.TextLayout >= 0
            && import.Result == HpirWidth.U64 && import.Arguments is [HpirWidth.U64],
        _ => false
    };

    private static bool ValidPreparedTransfer(HpirProgram program, HpirImport import)
    {
        if (import.Result != HpirWidth.Void || import.Arguments.Length != 0) return false;
        int index = import.Binding;
        bool shared = import.Service == HpirService.SharedTransfer;
        return program.Stage == HpirStage.Relocatable
            ? (uint)index < (uint)program.TransferInputs.Count && (!shared || program.TransferInputs[index].Kind != HpirTransferSource.Selection)
            : (uint)index < (uint)program.PreparedTransfers.Count && (!shared || program.PreparedTransfers[index].Kind != HpirTransferSource.Selection);
    }

    private static void ValidateLayout(HpirLayout layout)
    {
        if (layout.Size <= 0 || layout.Alignment is not (1 or 2 or 4 or 8 or 16))
            throw new MsyncException("Invalid HPIR layout extent or alignment");
        ValidatePointers(layout.PointerOffsets, layout.Size, "layout base pointer");
        ValidatePointers(layout.BorrowOffsets, layout.Size, "layout borrow");
    }

    private static void ValidatePointers(IReadOnlyList<int> offsets, int extent, string kind)
    {
        int next = 0;
        foreach (int offset in offsets)
        {
            if (offset < next || offset > extent - 8)
                throw new MsyncException($"Invalid HPIR {kind} offset {offset} (extent {extent})");
            next = offset + 8;
        }
    }

    private static void ValidateFunction(HpirProgram program, HpirFunction function)
    {
        int extent = function.FrameBytes, limit = function.Instructions.Length;
        if (extent < 0 || extent > int.MaxValue - HpirAbi.FrameHeaderBytes || extent % HpirAbi.FrameAlignment != 0
            || limit == 0 || !ValidWidth(function.Result))
            throw new MsyncException($"Invalid HPIR frame contract in '{function.Name}'");
        int next = 0;
        foreach (var parameter in function.Parameters)
        {
            if (!ValidWidth(parameter.Width) || parameter.Width == HpirWidth.Void || parameter.Offset < next
                || parameter.Offset > extent - (int)parameter.Width)
                throw new MsyncException($"Invalid HPIR parameter layout in '{function.Name}'");
            next = parameter.Offset + (int)parameter.Width;
        }
        int previous = -1;
        foreach (var source in function.Sources)
        {
            if (source.Instruction <= previous || source.Instruction >= limit || source.Source.Start < 0 || source.Source.Length < 0)
                throw new MsyncException($"Invalid HPIR source map in '{function.Name}'");
            previous = source.Instruction;
        }
        previous = -1;
        foreach (var map in function.Safepoints)
        {
            if (map.Instruction <= previous || map.Instruction >= limit || function.Instructions[map.Instruction].Op != HpirOp.Call)
                throw new MsyncException($"Invalid HPIR safepoint in '{function.Name}'");
            ValidatePointers(map.RootOffsets, extent, $"base root in '{function.Name}'");
            ValidatePointers(map.BorrowOffsets, extent, $"borrowed root in '{function.Name}'");
            previous = map.Instruction;
        }
        for (int pc = 0; pc < limit; ++pc)
            ValidateInstruction(program, function, pc);
    }

    private static void ValidateInstruction(HpirProgram program, HpirFunction function, int pc)
    {
        HpirInstruction instruction = function.Instructions[pc];
        var (d, a, b) = HpirInstructionShape.Of(instruction.Op);
        int extent = function.FrameBytes, limit = function.Instructions.Length;
        bool valid = Operand(instruction.D, d, extent) && Operand(instruction.A, a, extent) && Operand(instruction.B, b, extent);
        valid &= instruction.Op switch
        {
            HpirOp.AddressOf => (uint)instruction.A < (uint)extent,
            HpirOp.AddressOfStatic or HpirOp.AddressOfStaticCell => (ulong)instruction.Immediate < (ulong)program.TextLiterals.Count,
            HpirOp.AddressOfConnection => HpirConnectionAbi.ValidLiteral(instruction.Immediate, program.TextLiterals.Count),
            HpirOp.AddressOfTypeCell => (ulong)instruction.Immediate < (ulong)program.TypeCells.Count,
            HpirOp.AddressOfGlobal => (ulong)instruction.Immediate <= int.MaxValue / sizeof(ulong)
                && (instruction.Immediate >> 1) < program.Globals.Count,
            HpirOp.Jump => (uint)instruction.A < (uint)limit,
            HpirOp.Branch => (uint)instruction.B < (uint)limit && (uint)instruction.D < (uint)limit,
            HpirOp.Return => ValidReturn(program, function, instruction, extent),
            HpirOp.Call => (uint)instruction.A < (uint)function.Calls.Count
                && ValidCall(program, function.Calls[instruction.A], instruction.D, extent),
            _ => d != HpirWidth.Void
        };
        valid = valid && (pc < limit - 1 || instruction.Op is HpirOp.Jump or HpirOp.Branch or HpirOp.Return
            || instruction.Op == HpirOp.Call && IsFailure(program, function.Calls[instruction.A]));
        if (!valid) throw new MsyncException($"Invalid HPIR instruction in '{function.Name}' at {pc}: {instruction.Op}");
    }

    private static bool ValidReturn(HpirProgram program, HpirFunction function, HpirInstruction instruction, int extent)
    {
        if (instruction.Immediate >= (long)HpirReturnRoot.AggregateLayout)
            return function.Result == HpirWidth.Void && instruction.A >= 0 && Operand(instruction.A, HpirWidth.U64, extent)
                && instruction.Immediate - (long)HpirReturnRoot.AggregateLayout < program.Layouts.Count;
        return Operand(instruction.A, function.Result, extent)
            && (instruction.Immediate == 0 || function.Result == HpirWidth.U64
                && instruction.Immediate is (long)HpirReturnRoot.Exact or (long)HpirReturnRoot.Borrowed);
    }

    private static bool IsFailure(HpirProgram program, HpirCall call) =>
        call.Target < 0 && program.Imports[~call.Target].Service is HpirService.Fail or HpirService.FailDiagnostic;

    private static bool ValidCall(HpirProgram program, HpirCall call, int destination, int frame)
    {
        if (call.Target < 0)
        {
            if ((uint)~call.Target >= (uint)program.Imports.Count) return false;
            var imported = program.Imports[~call.Target];
            if (imported.Arguments.Length != call.Arguments.Count || !Operand(destination, imported.Result, frame)) return false;
            for (int index = 0; index < imported.Arguments.Length; ++index)
                if (!Operand(call.Arguments[index], imported.Arguments[index], frame)) return false;
            return true;
        }
        if (call.Target >= program.Functions.Count) return false;
        var target = program.Functions[call.Target];
        if (call.Arguments.Count != target.Parameters.Count || !Operand(destination, target.Result, frame)) return false;
        for (int index = 0; index < call.Arguments.Count; ++index)
            if (!Operand(call.Arguments[index], target.Parameters[index].Width, frame)) return false;
        return true;
    }

    private static bool Operand(int operand, HpirWidth width, int frame) => width == HpirWidth.Void
        || (operand >= 0 ? operand <= frame - (int)width : ~operand <= frame - 8);

    private static bool ValidWidth(HpirWidth width) =>
        width is HpirWidth.Void or HpirWidth.U8 or HpirWidth.U16 or HpirWidth.U32 or HpirWidth.U64;
}
