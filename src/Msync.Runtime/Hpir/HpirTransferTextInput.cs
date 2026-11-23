using System.Globalization;
using System.Text;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Hpir;

internal readonly record struct HpirTransferTextInput(string? Ready, Task<string?>? Text = null,
    Task<NativeProcess>? Process = null, bool Stderr = false, Task<byte>? Bool = null, HpirNumberEffectInput? Number = null,
    HpirTransferTextInput[]? Parts = null)
{
    internal Task? Dependency => (Task?)Text ?? (Task?)Process ?? (Task?)Bool ?? Number?.Value.Task ?? FirstPartDependency();

    private Task? FirstPartDependency()
    {
        if (Parts is not { } parts) return null;
        foreach (var part in parts)
            if (part.Dependency is { } dependency) return dependency;
        return null;
    }

    internal static string BoolText(byte value) => value == 0 ? "false" : "true";

    internal async ValueTask<string> Read(HpirWorkSite site, CancellationToken token,
        string nullMessage = "A transfer source must contain Text, not null.")
    {
        if (Parts is { } parts)
        {
            if (parts.Length == 2)
                return string.Concat(await parts[0].Read(site, token, nullMessage).ConfigureAwait(false),
                    await parts[1].Read(site, token, nullMessage).ConfigureAwait(false));
            StringBuilder joined = new();
            foreach (var part in parts) joined.Append(await part.Read(site, token, nullMessage).ConfigureAwait(false));
            return joined.ToString();
        }
        if (Number is { } number)
        {
            number.Requested.TrySetResult();
            return NumberText(await number.Value.Task.WaitAsync(token).ConfigureAwait(false));
        }
        if (Bool is { } boolean)
            return BoolText(await boolean.WaitAsync(token).ConfigureAwait(false));
        if (Process is { } process)
        {
            NativeProcess completed = await process.WaitAsync(token).ConfigureAwait(false);
            return Stderr ? completed.StandardError : completed.StandardOutput;
        }
        string? content = Text is { } text ? await text.WaitAsync(token).ConfigureAwait(false) : Ready;
        return content ?? throw new MsyncException(nullMessage, site.Source);
    }

    internal static string NumberText(double value) => value.ToString(CultureInfo.InvariantCulture);
}

// The transfer worker gets this boundary mailbox, never the guest cell or stack.
internal sealed class HpirNumberEffectInput
{
    internal readonly TaskCompletionSource Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<double> Value = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
