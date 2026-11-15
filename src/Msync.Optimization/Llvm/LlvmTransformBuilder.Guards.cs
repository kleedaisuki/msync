// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IRBuilder.h InsertPointGuard.
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTransformBuilder
{
    public readonly struct InsertPointGuard : IDisposable
    {
        private readonly LlvmTransformBuilder builder;
        private readonly InsertionPoint point;
        private readonly SourceSpan? source;

        public InsertPointGuard(LlvmTransformBuilder builder)
        {
            this.builder = builder;
            point = builder.SaveInsertionPoint();
            source = builder.CurrentSource;
        }

        public void Dispose()
        {
            builder.RestoreInsertionPoint(point);
            builder.CurrentSource = source;
        }
    }
}
