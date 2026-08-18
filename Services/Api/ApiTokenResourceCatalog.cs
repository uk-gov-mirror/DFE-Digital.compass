using Compass.Models;

namespace Compass.Services.Api;

public static class ApiTokenResourceCatalog
{
    public static readonly string[] Resources =
    {
        "WorkItems", "Risks", "Issues", "Milestones", "PerformanceMetrics",
        "EnterpriseMetrics", "FunctionalStandards", "DdtStandards",
        "ServiceRegister",
        "CmsAccessRequests",
        "AdminLookups"
    };

    /// <summary>
    /// Tokens issued as read-only-all-data before a catalog resource existed
    /// inherit <c>read</c> on that resource when they already have read-only
    /// access to every other catalog resource.
    /// </summary>
    public static bool GrantsRead(IEnumerable<ApiTokenPermission> permissions, string resource)
    {
        var stored = permissions.ToList();
        var match = stored.FirstOrDefault(p =>
            string.Equals(p.Resource, resource, StringComparison.OrdinalIgnoreCase));
        if (match != null)
            return match.CanRead;

        if (stored.Count == 0)
            return false;
        if (stored.Any(p => !p.CanRead || p.CanCreate || p.CanUpdate || p.CanDelete))
            return false;

        var names = stored.Select(p => p.Resource).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Resources
            .Where(r => !r.Equals(resource, StringComparison.OrdinalIgnoreCase))
            .All(names.Contains);
    }

    public static Dictionary<string, (bool read, bool create, bool update, bool delete)> ReadOnlyAllData()
    {
        return Resources.ToDictionary(r => r, _ => (read: true, create: false, update: false, delete: false));
    }

    public static bool IsReadOnlyAllData(Dictionary<string, (bool read, bool create, bool update, bool delete)> permissions)
    {
        foreach (var resource in Resources)
        {
            if (!permissions.TryGetValue(resource, out var p))
                return false;
            if (!p.read || p.create || p.update || p.delete)
                return false;
        }
        return true;
    }

    public static bool HasDelete(Dictionary<string, (bool read, bool create, bool update, bool delete)> permissions) =>
        permissions.Values.Any(p => p.delete);

    public static bool HasWrite(Dictionary<string, (bool read, bool create, bool update, bool delete)> permissions) =>
        permissions.Values.Any(p => p.create || p.update);
}
