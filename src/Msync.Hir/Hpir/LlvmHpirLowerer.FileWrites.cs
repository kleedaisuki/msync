using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal bool HasFileTextWrites { get; private set; }

        internal static bool IsFileTextWrite(LlvmFileOutInst output) =>
            output.Mode is LlvmFileOutMode.WriteText or LlvmFileOutMode.AppendText;

        internal bool CanWriteFileText(LlvmFileOutInst output) =>
            IsFileTextWrite(output) && (IsPathRecord(output.Destination.Type)
                || Union(output.Destination.Type) == (TypeKind.Path | TypeKind.String)
                || IsGuestPointer(output.Destination.Type) && ReflectedType(output, 1)?.Kind == TypeKind.String);

        internal int FileTextWriteImport(bool append, bool textPath, HpirFileTextSource source)
        {
            HasFileTextWrites = true;
            return FutureImport(HpirFileTextWriteImports.Create(append, textPath, source));
        }
    }

    private sealed partial class Body
    {
        private int fileTextWrites, deferredFileTextWrites, appendedFileTextWrites;

        private bool TryFileTextWrite(LlvmFileOutInst output)
        {
            if (!geometry.CanWriteFileText(output)) return false;
            LlvmValue path = output.Destination, content = output.Value;
            bool union = KnownPathUnion(path);
            bool textPath = !union && !Geometry.IsPathRecord(path.Type);
            bool storedText = geometry.StoredText(content) || geometry.MixedTextResult(content);

            HpirFileTextSource source = HpirFileTextSource.Ready;
            int text;
            if (content is LlvmCallInstruction format && deferredFormats is not null
                && deferredFormats.TryGetValue(format, out var receipt))
            {
                source = HpirFileTextSource.Receipt;
                text = Location(receipt);
            }
            else
            {
                if (!geometry.IsGuestPointer(content.Type) || geometry.ReflectedType(output, 0)?.Kind != TypeKind.String)
                    return false;
                if (TextCell(content))
                    source = storedText ? HpirFileTextSource.Ready : HpirFileTextSource.Completion;
                else if (FutureRoot(content)) return false;
                text = Location(content);
            }

            int destination;
            if (textPath && TextCell(path)) destination = ObserveText(output, path);
            else if (union)
                destination = NormalizePathUnion(output, path, Reserve(HpirPathImports.PathBytes, HpirPathImports.PathAlignment));
            else destination = textPath ? Location(path) : PathRecordAddress(path, output);
            if (storedText) text = ObserveText(output, content);
            bool append = output.Mode == LlvmFileOutMode.AppendText;
            int import = geometry.FileTextWriteImport(append, textPath, source);
            EmitCall(output, ~import, -1, [destination, text]);
            ++fileTextWrites;
            if (append) ++appendedFileTextWrites;
            if (source != HpirFileTextSource.Ready) ++deferredFileTextWrites;
            return true;
        }

        private void ReportFileTextWrites(CompilationTrace? trace)
        {
            trace?.Metric("paths.file_text_writes", fileTextWrites);
            trace?.Metric("paths.file_text_appends", appendedFileTextWrites);
            trace?.Metric("paths.file_text_deferred_inputs", deferredFileTextWrites);
        }
    }
}
