using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

// The Native target binds its own runtime ABI, not C library symbols. This is
// LLVM's disableAllFunctions configuration; a source function named "sin" or
// "malloc" must not silently acquire a C library contract.
public sealed partial class LlvmTargetLibraryInfo(LlvmModule module)
{
    public LlvmModule Module { get; } = module;
    public bool AllFunctionsDisabled => true;
    public bool IsAvailable(string name) => false;
}
