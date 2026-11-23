using System.Globalization;

namespace Msync.Runtime;

public sealed record RuntimeProfileSample(string Name, Dictionary<string, string> Attributes, Dictionary<string, ulong> Metrics);

public sealed partial class RuntimeProfile(bool instructionSites = false)
{
    private int nextImage;
    private bool InstructionSites { get; } = instructionSites;

    // Drain after guest execution joins. Weak IDs survive drains without retaining old images.
    public IReadOnlyList<RuntimeProfileSample> Drain()
    {
        List<RuntimeProfileSample> samples = [];
        CollectHpir(samples);
        return samples;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record ImageIdentity(int Id);
}
