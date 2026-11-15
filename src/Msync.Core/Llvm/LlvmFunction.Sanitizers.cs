// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Attributes.td.
namespace Msync.Core.Llvm;

public sealed partial class LlvmFunction
{
    public bool SanitizeThread { get; set; }
    public bool SanitizeConcurrency { get; set; }
    public bool SanitizeAddress { get; set; }
    public bool SanitizeHWAddress { get; set; }
}
