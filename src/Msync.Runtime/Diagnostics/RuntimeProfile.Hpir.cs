using System.Runtime.CompilerServices;
using Msync.Core.Hpir;
using Msync.Runtime.Hpir;

namespace Msync.Runtime;

public sealed partial class RuntimeProfile
{
    private readonly Lock hpirGate = new();
    private readonly Dictionary<HpirProgram, HpirCensus> hpirImages = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<HpirProgram, ImageIdentity> hpirIdentities = new();

    internal HpirCensus BeginHpir(HpirProgram program) => new(
        hpirIdentities.GetValue(program, _ => new(Interlocked.Increment(ref nextImage))).Id,
        program.Functions.Count, InstructionSites);

    internal void PublishHpir(HpirProgram program, HpirCensus census)
    {
        lock (hpirGate)
        {
            if (hpirImages.TryGetValue(program, out HpirCensus? previous)) previous.Merge(census);
            else hpirImages.Add(program, census);
        }
    }

    private void CollectHpir(List<RuntimeProfileSample> samples)
    {
        lock (hpirGate)
        {
            foreach ((HpirProgram program, HpirCensus census) in hpirImages.OrderBy(static pair => pair.Value.Identity))
            {
                var diagnostics = new HpirDiagnostics(program, null, null);
                for (int index = 0; index < census.Functions.Length; index++)
                {
                    if (census.Functions[index] is not { } function) continue;
                    function.Collect(samples, program.Functions[index], diagnostics, census.Identity, index);
                }
            }
            hpirImages.Clear();
        }
    }

    internal sealed class HpirCensus(int identity, int functions, bool sites)
    {
        internal readonly int Identity = identity;
        internal readonly HpirFunctionCensus?[] Functions = new HpirFunctionCensus?[functions];

        internal HpirFunctionCensus For(int function) => Functions[function] ??= new(sites);

        internal void Merge(HpirCensus source)
        {
            for (int i = 0; i < Functions.Length; i++)
            {
                if (source.Functions[i] is not { } added) continue;
                if (Functions[i] is { } previous) previous.Merge(added);
                else Functions[i] = added;
            }
        }
    }

    internal sealed class HpirFunctionCensus(bool sites)
    {
        private static readonly HpirOp[] operations = Enum.GetValues<HpirOp>();
        private readonly ulong[] opcodes = new ulong[operations.Max(static op => (int)op) + 1];
        private readonly Dictionary<int, ulong>? instructions = sites ? [] : null;
        private readonly Dictionary<(int Site, int Target), ulong>? edges = sites ? [] : null;
        private readonly Dictionary<(int Site, int Target), ulong> calls = [];
        private readonly Dictionary<(int Site, HpirService Service, int Binding), (ulong Calls, ulong Inline)> imports = [];
        private long segmentAllocations, segmentReentries, reserved, released;
        private ulong entered, returned, machineInstructions, machineEntries, machineCalls, machineReturns;
        private long heapObjects, heapPayload, heapReserved, heapReused, collections, stackScans, roots, borrows, rootObjects;
        private long reclaimedObjects, reclaimedBytes, releasedBytes, tracedEdges, workspacePeak, liveBytesPeak;
        private long borrowCandidates, borrowMatches, indexedObjects;
        private long textReused, textAsciiCase, textUnicodeCase;
        private long textJoinItems, textJoinCodeUnits, formattedFields, formattedUnits;
        private long textRangeYields, textRangeExhaustions, textRangeCodeUnits;
        private ulong numberParseShortInteger, numberParseGeneral, numberParseInvalid;
        private ulong rangeBufferGrowths, rangeBufferCopied, rangeBufferCapacity, rangePacks, rangePackedWords, rangePackedBytes;

        internal void EnterFrame(ulong hits = 1) => entered += hits;
        internal void Return() => returned++;

        internal void EnterSegment(bool reused, long reserved, long released)
        {
            if (reused) segmentReentries++;
            else segmentAllocations++;
            this.reserved += reserved;
            this.released += released;
        }

        internal void Step(HpirOp operation, int pc)
        {
            opcodes[(int)operation]++;
            if (instructions is not null) Count(instructions, pc, 1);
        }

        internal void MachineEnter() => machineEntries++;

        internal void MachineCall(int site, int target, ulong hits)
        {
            machineCalls += hits;
            Count(calls, (site, target), hits);
        }

        internal void MachineReturn(ulong hits)
        {
            machineReturns += hits;
            returned += hits;
        }

        internal void MachineStep(HpirOp operation, int pc, ulong hits)
        {
            machineInstructions += hits;
            opcodes[(int)operation] += hits;
            if (instructions is not null) Count(instructions, pc, hits);
        }

        internal void Edge(int from, int to, ulong hits = 1)
        {
            if (edges is not null && hits != 0) Count(edges, (from, to), hits);
        }

        internal void Call(int from, int target) => Count(calls, (from, target), 1);

        internal void Import(int site, HpirImport import, ulong hits = 1, bool inline = false) =>
            AddImport((site, import.Service, import.Binding), hits, inline ? hits : 0);

        private void AddImport((int Site, HpirService Service, int Binding) site, ulong hits, ulong inline)
        {
            ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(imports, site, out _);
            count.Calls += hits;
            count.Inline += inline;
        }

        internal void StringReuse() => textReused++;

        internal void NumberTextParse(NumberTextPath path)
        {
            if (path == NumberTextPath.ShortInteger) numberParseShortInteger++;
            else if (path == NumberTextPath.General) numberParseGeneral++;
            else numberParseInvalid++;
        }

        internal void Formatted(int fields, int units)
        {
            formattedFields += fields;
            formattedUnits += units;
        }

        internal void StringJoin(long items, int chars)
        {
            textJoinItems += items;
            textJoinCodeUnits += chars;
        }

        internal void StringCase(bool ascii)
        {
            if (ascii) textAsciiCase++;
            else textUnicodeCase++;
        }

        internal void TextRangePull(string? line)
        {
            if (line is null) textRangeExhaustions++;
            else
            {
                textRangeYields++;
                textRangeCodeUnits += line.Length;
            }
        }

        internal void RangeBufferGrow(ulong length, ulong capacity)
        {
            ++rangeBufferGrowths;
            rangeBufferCopied += length;
            rangeBufferCapacity = Math.Max(rangeBufferCapacity, capacity);
        }

        internal void RangePacked(ulong items, int stride)
        {
            ++rangePacks;
            if (stride == sizeof(ulong)) rangePackedWords += items;
            rangePackedBytes += items * (ulong)stride;
        }

        internal void HeapAllocation(long payload, HpirHeap.Allocation allocation)
        {
            heapObjects++;
            heapPayload += payload;
            heapReserved += allocation.ReservedBytes;
            if (allocation.Reused) heapReused++;
        }

        internal void Collection(int frames, int locations, int borrowLocations, int marked, HpirHeap.Sweep sweep)
        {
            collections++;
            stackScans += frames;
            roots += locations;
            borrows += borrowLocations;
            rootObjects += marked;
            reclaimedObjects += sweep.ReclaimedObjects;
            reclaimedBytes += sweep.ReclaimedBytes;
            releasedBytes += sweep.ReleasedBytes;
            tracedEdges += sweep.TracedEdges;
            borrowCandidates += sweep.BorrowCandidates;
            borrowMatches += sweep.BorrowMatches;
            indexedObjects += sweep.IndexedObjects;
            workspacePeak = Math.Max(workspacePeak, sweep.TraceWorkspaceBytes);
            liveBytesPeak = Math.Max(liveBytesPeak, sweep.LiveBytes);
        }

        private static void Count<TKey>(Dictionary<TKey, ulong> counter, TKey key, ulong count) where TKey : notnull =>
            System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(counter, key, out _) += count;

        internal void Merge(HpirFunctionCensus added)
        {
            entered += added.entered;
            returned += added.returned;
            machineInstructions += added.machineInstructions;
            machineEntries += added.machineEntries;
            machineCalls += added.machineCalls;
            machineReturns += added.machineReturns;
            segmentAllocations += added.segmentAllocations;
            segmentReentries += added.segmentReentries;
            reserved += added.reserved;
            released += added.released;
            heapObjects += added.heapObjects;
            heapPayload += added.heapPayload;
            heapReserved += added.heapReserved;
            heapReused += added.heapReused;
            collections += added.collections;
            stackScans += added.stackScans;
            roots += added.roots;
            borrows += added.borrows;
            rootObjects += added.rootObjects;
            reclaimedObjects += added.reclaimedObjects;
            reclaimedBytes += added.reclaimedBytes;
            releasedBytes += added.releasedBytes;
            tracedEdges += added.tracedEdges;
            borrowCandidates += added.borrowCandidates;
            borrowMatches += added.borrowMatches;
            indexedObjects += added.indexedObjects;
            textReused += added.textReused;
            textJoinItems += added.textJoinItems;
            formattedFields += added.formattedFields;
            formattedUnits += added.formattedUnits;
            textJoinCodeUnits += added.textJoinCodeUnits;
            textAsciiCase += added.textAsciiCase;
            textUnicodeCase += added.textUnicodeCase;
            textRangeYields += added.textRangeYields;
            textRangeExhaustions += added.textRangeExhaustions;
            textRangeCodeUnits += added.textRangeCodeUnits;
            numberParseShortInteger += added.numberParseShortInteger;
            numberParseGeneral += added.numberParseGeneral;
            numberParseInvalid += added.numberParseInvalid;
            rangePacks += added.rangePacks;
            rangePackedWords += added.rangePackedWords;
            rangePackedBytes += added.rangePackedBytes;
            rangeBufferGrowths += added.rangeBufferGrowths;
            rangeBufferCopied += added.rangeBufferCopied;
            rangeBufferCapacity = Math.Max(rangeBufferCapacity, added.rangeBufferCapacity);
            workspacePeak = Math.Max(workspacePeak, added.workspacePeak);
            liveBytesPeak = Math.Max(liveBytesPeak, added.liveBytesPeak);
            for (int i = 0; i < opcodes.Length; i++) opcodes[i] += added.opcodes[i];
            foreach (var pair in added.calls) Count(calls, pair.Key, pair.Value);
            foreach (var (site, counts) in added.imports) AddImport(site, counts.Calls, counts.Inline);
            if (instructions is not null)
                foreach (var pair in added.instructions!) Count(instructions, pair.Key, pair.Value);
            if (edges is not null)
                foreach (var pair in added.edges!) Count(edges, pair.Key, pair.Value);
        }

        internal void Collect(List<RuntimeProfileSample> samples, HpirFunction function, HpirDiagnostics diagnostics,
            int image, int index)
        {
            var attributes = new Dictionary<string, string>
            {
                ["image.id"] = Number(image), ["function.id"] = Number(index), ["function.name"] = function.Name,
                ["ir.kind"] = "hpir"
            };
            ulong total = 0;
            foreach (HpirOp operation in operations)
            {
                ulong hits = opcodes[(int)operation];
                if (hits == 0) continue;
                total += hits;
                samples.Add(new("vm.opcode", new(attributes) { ["opcode"] = operation.ToString() }, new() { ["hits"] = hits }));
            }
            ulong arguments = (ulong)function.Parameters.Count * (ulong)entered;
            ulong argumentBytes = (ulong)function.Parameters.Sum(static value => (long)value.Width) * (ulong)entered;
            samples.Add(new("vm.function", WithSource(new(attributes), -1), new()
            {
                ["calls"] = (ulong)entered, ["returns"] = (ulong)returned, ["instructions"] = total,
                ["machine_instructions"] = machineInstructions, ["machine_entries"] = machineEntries,
                ["machine_calls"] = machineCalls, ["machine_returns"] = machineReturns,
                ["stack_frames"] = (ulong)entered, ["stack_frame_bytes"] = (ulong)function.FrameBytes * (ulong)entered,
                ["stack_segment_allocations"] = (ulong)segmentAllocations, ["stack_segment_reentries"] = (ulong)segmentReentries,
                ["stack_segment_native_bytes"] = (ulong)reserved, ["stack_segment_replaced_bytes"] = (ulong)released,
                ["call_arguments_copied"] = arguments, ["call_physical_argument_bytes"] = argumentBytes,
                ["native_heap_objects"] = (ulong)heapObjects, ["native_heap_payload_bytes"] = (ulong)heapPayload,
                ["native_heap_reserved_bytes"] = (ulong)heapReserved, ["native_heap_reused_objects"] = (ulong)heapReused,
                ["text_inputs_reused"] = (ulong)textReused, ["text_ascii_case"] = (ulong)textAsciiCase,
                ["text_join_items"] = (ulong)textJoinItems, ["text_join_code_units"] = (ulong)textJoinCodeUnits,
                ["text_format_fields"] = (ulong)formattedFields, ["text_format_code_units"] = (ulong)formattedUnits,
                ["text_unicode_case"] = (ulong)textUnicodeCase,
                ["text_range_yields"] = (ulong)textRangeYields, ["text_range_exhaustions"] = (ulong)textRangeExhaustions,
                ["text_range_code_units"] = (ulong)textRangeCodeUnits,
                ["text_number_parse_short_integer"] = numberParseShortInteger,
                ["text_number_parse_general"] = numberParseGeneral,
                ["text_number_parse_invalid"] = numberParseInvalid,
                ["range_packs"] = rangePacks, ["range_packed_words"] = rangePackedWords, ["range_packed_bytes"] = rangePackedBytes,
                ["range_buffer_growths"] = rangeBufferGrowths, ["range_buffer_copied_items"] = rangeBufferCopied,
                ["range_buffer_peak_capacity"] = rangeBufferCapacity,
                ["gc_collections"] = (ulong)collections, ["gc_stack_frames"] = (ulong)stackScans,
                ["gc_root_locations"] = (ulong)roots, ["gc_borrow_locations"] = (ulong)borrows,
                ["gc_root_objects"] = (ulong)rootObjects,
                ["gc_reclaimed_objects"] = (ulong)reclaimedObjects, ["gc_reclaimed_bytes"] = (ulong)reclaimedBytes,
                ["gc_released_bytes"] = (ulong)releasedBytes, ["gc_traced_edges"] = (ulong)tracedEdges,
                ["gc_borrow_candidates"] = (ulong)borrowCandidates, ["gc_borrow_matches"] = (ulong)borrowMatches,
                ["gc_indexed_objects"] = (ulong)indexedObjects,
                ["gc_workspace_peak_bytes"] = (ulong)workspacePeak, ["gc_live_peak_bytes"] = (ulong)liveBytesPeak
            }));
            if (instructions is not null)
                foreach ((int site, ulong hits) in instructions.OrderBy(static pair => pair.Key))
                    samples.Add(new("vm.instruction", Site(site, function.Instructions[site].Op.ToString()), new() { ["hits"] = hits }));
            if (edges is not null)
                foreach (var (edge, hits) in edges.OrderBy(static pair => pair.Key))
                    samples.Add(new("vm.edge", SiteOperand(edge.Site, "target.instruction", edge.Target), new() { ["hits"] = hits }));
            foreach (var (call, hits) in calls.OrderBy(static pair => pair.Key))
                samples.Add(new("vm.call", SiteOperand(call.Site, "target.function.id", call.Target), new() { ["hits"] = hits }));
            foreach (var (import, counts) in imports.OrderBy(static pair => pair.Key))
            {
                var site = SiteOperand(import.Site, "native.binding", import.Binding);
                site["native.name"] = import.Service switch
                {
                    HpirService.MathF64 => "math." + HpirScalarImports.MathName((HpirMathOp)import.Binding),
                    HpirService.String => HpirStringImports.Name((HpirStringOp)import.Binding),
                    HpirService.Regex => HpirRegexImports.Name(HpirRegexImports.Decode(import.Binding).Operation),
                    HpirService.RegexFind => "regex.find",
                    HpirService.PathSelection => "path.selection.capture",
                    HpirService.LiveTransferSelection => "transfer.live_selection",
                    HpirService.LivePathBatch => "transfer.live_path_batch",
                    HpirService.TextRangeSource => HpirTextRangeImports.DecodeSource(import.Binding).Source == HpirTextRangeSource.Stdin
                        ? "io.stdin_lines" : "io.read_lines",
                    HpirService.RangeTransform => "ranges.transform.capture",
                    HpirService.RangeTransformTake => "ranges.transform.take",
                    HpirService.RangeTransformOpen => "ranges.transform.open",
                    HpirService.RangeBound => $"ranges.{HpirTransformImports.BoundName(HpirTransformImports.DecodeBound(import.Binding).Operation)}.bound",
                    HpirService.RangeBufferGrow => "ranges.materialize.grow",
                    HpirService.RangePackWords => "ranges.materialize.pack_words",
                    HpirService.TextRangeOpen => "text_range.open",
                    HpirService.TextRangeNext => "text_range.next",
                    HpirService.TextRangeClose => "text_range.close",
                    HpirService.TextRangeJoin => "string.join.live",
                    HpirService.AppendTextChunk => "text.append_chunk",
                    HpirService.FormatF64 => "format.number",
                    HpirService.ParseF64 => "parse.number",
                    HpirService.ParseBool => "parse.bool",
                    HpirService.ParsePathF64 => "path.parse_number",
                    HpirService.ParsePathBool => "path.parse_bool",
                    HpirService.Path => "path." + HpirPathImports.Decode(import.Binding).Op,
                    HpirService.CreateDirectories => HpirDirectoryCreationImports.Name,
                    HpirService.RenameFile => "fs.rename",
                    HpirService.DirectorySource => HpirDirectoryImports.DecodeSource(import.Binding).Recursive
                        ? "fs.recursive_directory_iterator" : "fs.directory_iterator",
                    HpirService.DirectoryOpen => import.Binding == 0 ? "fs.directory.open" : "fs.path_selection.open",
                    HpirService.DirectoryNext => "fs.directory.next",
                    HpirService.DirectoryClose => "fs.directory.close",
                    HpirService.FileMetadata => HpirFileMetadataImports.NativeName(HpirFileMetadataImports.Decode(import.Binding).Operation),
                    HpirService.LiveTransfer => "transfer.live",
                    HpirService.SharedTransfer => "transfer.prepared_shared",
                    HpirService.LiveTransferFutureText => "transfer.live_text_completion",
                    HpirService.LiveTransferBoolText => "transfer.live_bool_text",
                    HpirService.LiveTransferNumberText => "transfer.live_number_text",
                    HpirService.ReadFileText => "io.read_text",
                    HpirService.WriteFileText => HpirFileTextWriteImports.NativeName(HpirFileTextWriteImports.Decode(import.Binding).Append),
                    HpirService.FileBytesOpen => HpirFileByteImports.NativeName((import.Binding & 1) != 0),
                    HpirService.FileBytesChunk => import.Binding == 0 ? "io.bytes.chunk" : "io.bytes.finish",
                    HpirService.ReadLineText => "io.read_line",
                    HpirService.ReadStdinText => "io.read_stdin",
                    HpirService.TextFormat => "text.format",
                    HpirService.ConcatTextPending => "string.concat.completion",
                    HpirService.DemandText => "future.text.demand",
                    HpirService.TextIsNull => "future.text.is_null",
                    HpirService.TextParseNumber => "future.text.parse_number",
                    HpirService.ProcessStart => "process.start",
                    HpirService.ProcessSpawn => "process.spawn",
                    HpirService.ProcessControl => HpirProcessLibraryImports.Name((HpirProcessControl)import.Binding),
                    HpirService.ProcessExit => "process.exit_code",
                    HpirService.ProcessSuccess => "process.success",
                    HpirService.ProcessStdout => "process.stdout",
                    HpirService.ProcessStderr => "process.stderr",
                    HpirService.TypeName => "meta.type_name",
                    HpirService.ProcessTextReady => import.Binding == 0 ? "process.stdout.ready" : "process.stderr.ready",
                    HpirService.Fail when import.Binding == (int)HpirFailure.RangeIndex => "ranges.at.failure",
                    HpirService.WriteText => HpirScalarImports.TextOutputName((HpirTextOutput)import.Binding),
                    _ => import.Service.ToString()
                };
                if (import.Service is HpirService.AllocateObject or HpirService.AllocateSessionScope or HpirService.FormatF64)
                    site["allocation.layout"] = Number(import.Binding);
                samples.Add(new("vm.native", site, new() { ["calls"] = counts.Calls, ["machine_inline_calls"] = counts.Inline }));
            }

            Dictionary<string, string> Site(int site, string opcode) => WithSource(new(attributes)
                { ["instruction"] = Number(site), ["opcode"] = opcode }, site);
            Dictionary<string, string> SiteOperand(int site, string key, int value) => WithSource(new(attributes)
                { ["instruction"] = Number(site), [key] = Number(value) }, site);
            Dictionary<string, string> WithSource(Dictionary<string, string> row, int site)
            {
                if (diagnostics.Source(index, site) is not { } source) return row;
                row["source.file"] = source.Source;
                row["source.offset"] = Number(source.Start);
                row["source.length"] = Number(source.Length);
                return row;
            }
        }
    }
}
