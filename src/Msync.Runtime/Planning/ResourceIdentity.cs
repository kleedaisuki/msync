using Msync.Core;

namespace Msync.Runtime;

internal sealed class ResourceIdentity : IEqualityComparer<PlanResource>
{
    internal static readonly ResourceIdentity Instance = new();
    public bool Equals(PlanResource? left, PlanResource? right) => ReferenceEquals(left, right)
        || left is not null && right is not null && left.Endpoint == right.Endpoint
            && Comparer(left.Endpoint).Equals(left.Path, right.Path);
    public int GetHashCode(PlanResource resource) => HashCode.Combine(resource.Endpoint,
        Comparer(resource.Endpoint).GetHashCode(resource.Path));
    private static StringComparer Comparer(string endpoint) => endpoint == "local" && OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
