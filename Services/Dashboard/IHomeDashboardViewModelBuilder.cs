using Compass.Models;
using Compass.ViewModels.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Services.Dashboard;

public interface IHomeDashboardViewModelBuilder
{
    Task<UserPreference> GetOrCreateDashboardPreferenceAsync(User user);

    Task<HomeDashboardViewModel> BuildDashboardViewModelAsync(
        User currentUser,
        string userEmail,
        UserPreference preference,
        IUrlHelper url,
        HttpContext httpContext,
        bool showRaidIssues = true);
}
