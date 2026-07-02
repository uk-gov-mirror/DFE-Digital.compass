using Compass.Models;
using Compass.Services;
using Compass.ViewModels.Dashboard;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Services.Dashboard;

public interface IYourTasksViewModelBuilder
{
    YourTasksViewModel Build(YourTasksBuildInput input);

    Task<YourTasksViewModel> BuildAsync(
        User currentUser,
        string userEmail,
        IUrlHelper url,
        bool showRaidIssues,
        YourTasksLinkOptions links,
        string idPrefix = "dashboard-task",
        CancellationToken cancellationToken = default);

    Task<List<(ProductDto Product, Commission Commission, CommissionSubmissionStatus Status, DateTime DueDate)>>
        LoadProductsNeedingCommissionReportingAsync(
            IReadOnlyList<ProductDto> myProducts,
            CancellationToken cancellationToken = default);
}
