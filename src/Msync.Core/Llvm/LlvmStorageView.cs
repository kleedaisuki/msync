// Project target storage metadata, not an upstream LLVM optimization constraint.
namespace Msync.Core.Llvm;

public readonly record struct LlvmStorageView(LlvmType StorageType, int LogicalType, ulong ByteOffset);
