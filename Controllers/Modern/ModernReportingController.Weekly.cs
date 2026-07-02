using Compass.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Controllers.Modern;

public partial class ModernReportingController
{
    /// <summary>Weekly reporting dashboard — portfolio health, milestones, RAG/priority, and business area summary.</summary>
    [HttpGet("weekly-update")]
    public async Task<IActionResult> WeeklyUpdate(int? isoYear, int? isoWeek, int? businessAreaId, int? directorateId)
    {
        try
        {
            var model = await _monthlyReportService.BuildWeeklyDashboardAsync(isoYear, isoWeek, businessAreaId, directorateId);
            SetNav("reporting-weekly");
            return View("~/Views/Modern/Reporting/WeeklyUpdate.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading modern weekly report dashboard");
            TempData["ErrorMessage"] = "An error occurred while loading the weekly report. Please try again.";
            SetNav("reporting-weekly");
            return View("~/Views/Modern/Reporting/WeeklyUpdate.cshtml", new ModernWeeklyReportDashboardViewModel());
        }
    }

    /// <summary>Weekly submission progress — chart and league tables for weekly return completion.</summary>
    [HttpGet("weekly-submission-progress")]
    public async Task<IActionResult> WeeklySubmissionProgress(int? isoYear, int? isoWeek, int? businessAreaId, int? directorateId)
    {
        try
        {
            var model = await _monthlyReportService.BuildWeeklySubmissionProgressAsync(isoYear, isoWeek, businessAreaId, directorateId);
            SetNav("reporting-weekly-submission");
            return View("~/Views/Modern/Reporting/WeeklySubmissionProgress.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading weekly submission progress report");
            TempData["ErrorMessage"] = "An error occurred while loading weekly submission progress. Please try again.";
            SetNav("reporting-weekly-submission");
            return View("~/Views/Modern/Reporting/WeeklySubmissionProgress.cshtml", new ModernWeeklySubmissionProgressViewModel());
        }
    }
}
