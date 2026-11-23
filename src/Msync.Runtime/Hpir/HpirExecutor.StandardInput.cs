using Msync.Runtime.Native;

namespace Msync.Runtime.Hpir;

internal static partial class HpirExecutor
{
    private static async ValueTask<string?> ReadRemainingInput(TextReader reader, HpirWorkSite site, CancellationToken token)
    {
        using var trace = site.Begin("input.read_stdin");
        try
        {
            string text = await StandardTextInput.ReadToEnd(reader, token).ConfigureAwait(false);
            trace?.SetMetric("input.characters", text.Length);
            return text;
        }
        catch (Exception error) { trace?.Fail(error); throw; }
    }
}
