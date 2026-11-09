using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using Msync.Runtime;

if (!OperatingSystem.IsWindows()) await TransferEngine.VerifyReceiptAsync();

namespace Msync.Runtime
{
    public static partial class TransferEngine
    {
        internal static async Task VerifyReceiptAsync()
        {
            string root = Path.Combine(Environment.CurrentDirectory, ".temp", "archive-receipt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            byte[] contents = new byte[1280123]; new Random(415).NextBytes(contents);
            await File.WriteAllBytesAsync(Path.Combine(root, "data"), contents);
            try
            {
                foreach (string program in new[] { "exec tar --format=pax -cpf - --files-from /dev/null", "exec tar --format=pax -cpf - ./data" })
                {
                    var info = new ProcessStartInfo("sh") { WorkingDirectory = root, RedirectStandardInput = true, RedirectStandardOutput = true };
                    info.ArgumentList.Add("-c"); info.ArgumentList.Add(WithArchiveReceipt(program));
                    using var producer = Process.Start(info)!;
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var kill = cancellation.Token.Register(() => { if (!producer.HasExited) producer.Kill(entireProcessTree: true); });
                    using var forwarded = new MemoryStream();
                    await PumpArchiveAsync(producer, forwarded, receipt: true, 65536, cancellation.Token);
                    await producer.WaitForExitAsync(cancellation.Token);
                    if (producer.ExitCode != 0) throw new IOException("Archive producer did not accept the receipt");
                    forwarded.Position = 0;
                    using var reader = new TarReader(forwarded);
                    var entry = await reader.GetNextEntryAsync();
                    if (program.Contains("./data", StringComparison.Ordinal))
                    {
                        using var received = new MemoryStream();
                        await entry!.DataStream!.CopyToAsync(received);
                        if (!SHA256.HashData(received.ToArray()).AsSpan().SequenceEqual(SHA256.HashData(contents))) throw new IOException("Receipt relay changed file bytes");
                    }
                    else if (entry is not null) throw new IOException("Empty selected work invented an archive member");
                }
                Console.WriteLine("Archive receipt kept producer alive through logical end, forwarding exact large and empty archives without a second channel.");
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
}
