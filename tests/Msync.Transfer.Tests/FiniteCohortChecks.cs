using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class FiniteCohortChecks
{
    internal static async Task RunAsync(string mode)
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("MSYNC_TRANSFER_WORK")!, "cohort-" + mode);
        string source = Path.Combine(root, "input");
        string destination = Path.Combine(root, "output");
        Directory.CreateDirectory(source);
        string[] names = [.. Enumerable.Range(0, 32).Select(index => $"member-{index:000}"), "line\nslash\\ 可莉'%0A%25\n", "two words"];
        for (int index = 0; index < names.Length; index++)
            await File.WriteAllBytesAsync(Path.Combine(source, names[index]), [(byte)index, 0, 255, 128]);
        Directory.CreateDirectory(Path.Combine(source, "tree", "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "tree", "nested", "file"), "finite directory bytes");
        const string link = "missing%0A\n可莉";
        File.CreateSymbolicLink(Path.Combine(source, "link"), link);
        NativeConnection first = new(ConnectionKind.Ssh, Environment.GetEnvironmentVariable("MSYNC_TRANSFER_REMOTE")!);
        NativeConnection second = new(ConnectionKind.Ssh, Environment.GetEnvironmentVariable("MSYNC_TRANSFER_SECOND_REMOTE")!);
        NativeConnection? producer = mode == "upload" ? null : first;
        NativeConnection? consumer = mode switch
        {
            "upload" or "same" => first,
            "download" => null,
            "relay" => second,
            _ => throw new ArgumentException($"Unknown finite-cohort mode '{mode}'.")
        };
        NativeValue[] inputs = names.Append("tree").Append("link").Select(name =>
            NativeValue.FromPath(new(Path.Combine(source, name), Connection: producer))).ToArray();
        NativeValue cohort = NativeValue.FromRange(NativeRange.Snapshot(inputs, TypeRef.Path));
        NativeRuntimeContext context = new(new ExecutionOptions(WorkingDirectory: root));
        if (!await NativeHostIO.TransferAsync(cohort, new(destination + "/", Connection: consumer), context))
            throw new InvalidOperationException("The actual finite-cohort transfer failed.");
        for (int index = 0; index < names.Length; index++)
            if (!(await File.ReadAllBytesAsync(Path.Combine(destination, names[index]))).SequenceEqual(new byte[] { (byte)index, 0, 255, 128 }))
                throw new InvalidOperationException($"Finite {mode} member bytes/spelling changed: {names[index]}");
        if (await File.ReadAllTextAsync(Path.Combine(destination, "tree", "nested", "file")) != "finite directory bytes"
            || new FileInfo(Path.Combine(destination, "link")).LinkTarget != link)
            throw new InvalidOperationException("Finite cohort directory/link semantics changed.");
        Console.WriteLine($"PASS finite {mode} cohort: {inputs.Length} members, exact bytes and native spellings");
    }
}
