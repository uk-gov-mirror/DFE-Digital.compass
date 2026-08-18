namespace Compass.ViewModels.Modern;

public sealed class WorkServiceRegisterLinkRow
{
    public int ProjectProductId { get; init; }
    public Guid ServiceRegisterProductId { get; init; }
    public string Title { get; init; } = "";
    public int? RegisterUniqueId { get; init; }
    public string? ServiceOwner { get; init; }
    public string? PhaseName { get; init; }
    public DateTime LinkedAt { get; init; }
    public string DetailUrl { get; init; } = "";
}

public sealed class ServiceRegisterWorkLinkRow
{
    public int ProjectProductId { get; init; }
    public int WorkItemId { get; init; }
    public string Title { get; init; } = "";
    public string WorkCode { get; init; } = "";
    public string? Status { get; init; }
    public DateTime LinkedAt { get; init; }
    public string DetailUrl { get; init; } = "";
}

public sealed class WorkServiceRegisterLinksPanelViewModel
{
    public int WorkItemId { get; init; }
    public bool CanLink { get; init; }
    public bool CanEdit { get; init; }
    public bool CanCreateServiceOffering { get; init; }
    public bool ShowInFips { get; init; }
    public IReadOnlyList<WorkServiceRegisterLinkRow> Links { get; init; } = Array.Empty<WorkServiceRegisterLinkRow>();
    public string PickProductsUrl { get; init; } = "";
    public string LinkUrl { get; init; } = "";
    public string SaveShowInFipsUrl { get; init; } = "";
}

public sealed class FipsProductWorkItemsPanelViewModel
{
    public Guid ProductId { get; init; }
    public bool CanLink { get; init; }
    public IReadOnlyList<ServiceRegisterWorkLinkRow> Links { get; init; } = Array.Empty<ServiceRegisterWorkLinkRow>();
    /// <summary>Side nav badge count when <see cref="Links"/> is not loaded.</summary>
    public int? LinkCount { get; init; }
    public string PickWorkUrl { get; init; } = "";
    public string LinkUrl { get; init; } = "";
}
