namespace Msync.Core.Llvm;

public sealed partial class LlvmContext
{
    // Project diagnostics sink; not an LLVMContext semantic field.
    public Dictionary<string, ulong>? ValueHandleMetrics { get; set; }
}
