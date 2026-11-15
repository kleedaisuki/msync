using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Body
    {
        private LlvmValue Scope(HirNode node)
        {
            var construction = Hir.Scopes[node.Data];
            owner.session?.ScopeShapes.Add(construction.Layout);
            if (construction.Singleton >= 0) return Singleton(node, construction);
            var layout = owner.Layout(construction.Layout);
            var pointer = Add(new LlvmHeapAllocInst(owner.Int(Math.Max(1UL, owner.module.DataLayout.GetTypeAllocSize(layout).GetFixedValue())),
                owner.module.DataLayout.GetABITypeAlign(layout), owner.DataPointer.AddressSpace)
            { LogicalType = owner.metadata.Type(node.Type), LogicalLayout = owner.metadata.Layout(construction.Layout), LogicalAllocationSite = construction.Site?.Value ?? -1 });
            BindScope(node, construction, layout, pointer);
            foreach (var child in Hir.Children(node))
            {
                if (block.HasTerminator) break;
                Emit(child);
            }
            return pointer;
        }

        private void BindScope(HirNode node, HirScopeConstruction construction, LlvmStructType layout, LlvmValue pointer)
        {
            for (int index = 0; index < construction.Fields.Length; index++)
                locations[construction.Fields[index]] = FieldAddress(layout, pointer, index);
            if (construction.Self is { } self)
                Add(new LlvmStoreInstruction(Represent(pointer, Hir.Locations[self.Value].StorageType,
                    Hir.Locations[self.Value].AdmitsNull), Address(self)));
        }

        private LlvmValue FieldAddress(LlvmStructType layout, LlvmValue pointer, int ordinal)
        {
            int logicalLayout = owner.structLayouts[layout];
            int pointee = owner.metadata.Layouts[logicalLayout].Fields[ordinal].Type;
            return Add(new LlvmGetElementPtrInstruction(layout, pointer,
                [owner.Int(0), LlvmConstantInt.Get(LlvmIntegerType.Get(owner.context, 32), (ulong)ordinal)])
                { LogicalType = owner.metadata.Pointer(pointee) });
        }

        private LlvmValue NativeFunction(HirNode node)
        {
            var layout = owner.FunctionType();
            var name = new LlvmGlobalVariable(owner.module, owner.DataPointer, true,
                LlvmLinkage.ExternalLinkage, name: "msync.literal." + owner.constants.Count)
                { LogicalType = owner.metadata.Pointer(owner.metadata.RuntimeType(TypeRef.String)) };
            owner.constants.Add(name, new(NativeKind.String, Text: node.Text));
            var nameValue = Add(new LlvmLoadInstruction(name.ValueType, name)
                { LogicalType = owner.metadata.RuntimeType(TypeRef.String) });
            var seed = LlvmPoisonValue.Get(layout);
            var identity = Add(new LlvmInsertValueInstruction(seed,
                LlvmConstantPointerNull.Get(owner.CodePointer), [0])
                { LogicalType = owner.metadata.RuntimeType(TypeRef.Function) });
            var environment = Add(new LlvmInsertValueInstruction(identity,
                LlvmConstantPointerNull.Get(owner.DataPointer), [1])
                { LogicalType = owner.metadata.RuntimeType(TypeRef.Function) });
            return Add(new LlvmInsertValueInstruction(environment, nameValue, [2]));
        }

        private LlvmValue PathJoin(HirNode node)
        {
            var left = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            var right = Child(node, 1);
            if (block.HasTerminator) return owner.Null;
            current = node;
            var signature = LlvmFunctionType.Get(owner.Type(node.Type), [left.Type, right.Type]);
            const string name = "msync.path.join";
            var target = owner.module.GetFunction(System.Text.Encoding.UTF8.GetBytes(name)) ??
                LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage, name, owner.module);
            owner.runtimeCalls[target] = NativeBinary.PathJoin;
            return Add(new LlvmCallInstruction(signature, target, [left, right]));
        }

        private LlvmValue Call(HirNode node)
        {
            var call = Hir.Calls[node.Data];
            if (call.Kind != HirCallKind.Native || call.SessionSlot >= 0) return SourceCall(node);
            string name = call.NativeName ?? "";
            var callee = Hir.Node(Hir.Children(node)[0]);
            if (callee.Kind != HirKind.NativeFunction)
            {
                Child(node, 0);
                if (block.HasTerminator) return owner.Null;
            }
            var arguments = new List<LlvmValue>();
            for (int index = 1; index < node.Children.Count; index++)
            {
                arguments.Add(Child(node, index));
                if (block.HasTerminator) return owner.Null;
            }
            current = node;
            var resultType = owner.Type(node.Type);
            if (name is not ("io.read_text" or "io.read_lines" or "io.read_bytes" or "io.write_text" or
                "io.append_text" or "io.write_bytes" or "io.append_bytes"))
                return NativeBuiltin(node, name, arguments);
            return name switch
            {
                "io.read_text" => Add(new LlvmFileInInst(resultType, arguments[0], LlvmFileInFormat.Text)),
                "io.read_lines" => Add(new LlvmFileInInst(resultType, arguments[0], LlvmFileInFormat.Lines)),
                "io.read_bytes" => Add(new LlvmFileInInst(resultType, arguments[0], LlvmFileInFormat.Bytes)),
                "io.write_text" => Write(LlvmFileOutMode.WriteText),
                "io.append_text" => Write(LlvmFileOutMode.AppendText),
                "io.write_bytes" => Write(LlvmFileOutMode.WriteBytes),
                _ => Write(LlvmFileOutMode.AppendBytes)
            };
            LlvmValue Write(LlvmFileOutMode mode) => Add(new LlvmFileOutInst(resultType, arguments[1], arguments[0], mode));
        }

        private LlvmValue Transfer(HirNode node)
        {
            var value = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            var destination = Child(node, 1);
            if (block.HasTerminator) return owner.Null;
            current = node;
            return Add(new LlvmFileOutInst(owner.Type(node.Type), value, destination, LlvmFileOutMode.Transfer));
        }
    }
}
