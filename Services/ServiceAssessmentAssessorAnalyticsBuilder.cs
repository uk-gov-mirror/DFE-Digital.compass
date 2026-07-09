using System.Globalization;
using Compass.ViewModels.Modern;

namespace Compass.Services;

/// <summary>Assessor panel analytics from SAS <c>/assessors/summary</c>.</summary>
public static class ServiceAssessmentAssessorAnalyticsBuilder
{
    private const int FinancialQuarterCount = 6;
    private const string ServiceAssessmentType = "Service assessment";
    private const string PeerReviewType = "Peer review";

    public static SasAssessorAnalysisVm Build(SasAssessorsSummaryResponse? response)
    {
        var vm = new SasAssessorAnalysisVm();
        if (response?.Assessors is not { Count: > 0 } assessors)
        {
            vm.LoadFailed = response is null;
            return vm;
        }

        var assignments = assessors
            .SelectMany(a => (a.Assessments ?? []).Select(x => (Assessor: a, Assignment: x)))
            .ToList();

        vm.TotalAssessors = assessors.Count;
        vm.TotalPanelAssignments = assignments.Count;
        vm.PublishedAssignments = assignments.Count(x =>
            string.Equals(x.Assignment.Status, "Published", StringComparison.OrdinalIgnoreCase));

        vm.ByPrimaryRole = assessors
            .GroupBy(a => string.IsNullOrWhiteSpace(a.PrimaryRole) ? "Unknown" : a.PrimaryRole!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .ToList();

        vm.ByOutcome = assignments
            .GroupBy(x => NormalizeOutcome(x.Assignment.Outcome), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .ToList();

        vm.ByType = assignments
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Assignment.Type) ? "Unknown" : x.Assignment.Type!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .ToList();

        vm.ByStatus = assignments
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Assignment.Status) ? "Unknown" : x.Assignment.Status!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .ToList();

        var periods = GetLastFinancialQuarters(DateTime.UtcNow, FinancialQuarterCount);
        vm.PeriodLabels = periods.Select(p => p.Label).ToList();
        vm.PeriodTotals = periods
            .Select(p => assignments.Count(x =>
                x.Assignment.AssessmentDateTime is { } d &&
                string.Equals(GetUkFinancialQuarterKey(d), p.Key, StringComparison.Ordinal)))
            .ToList();

        var leagueYears = assignments
            .Select(x => x.Assignment.AssessmentDateTime?.Year)
            .Where(y => y.HasValue)
            .Select(y => y!.Value)
            .Distinct()
            .OrderByDescending(y => y)
            .ToList();

        var leagueTabs = new List<SasAssessorLeagueYearTabVm>
        {
            new()
            {
                Key = "all",
                Label = "All time",
                IsDefault = true,
                Rows = BuildLeagueTable(assessors, calendarYear: null)
            }
        };

        foreach (var year in leagueYears)
        {
            leagueTabs.Add(new SasAssessorLeagueYearTabVm
            {
                Key = year.ToString(CultureInfo.InvariantCulture),
                Label = year.ToString(CultureInfo.InvariantCulture),
                Rows = BuildLeagueTable(assessors, calendarYear: year)
            });
        }

        vm.LeagueYearTabs = leagueTabs;

        vm.TopRoles = vm.ByPrimaryRole.Take(8).ToList();
        return vm;
    }

    private static List<SasAssessorLeagueRowVm> BuildLeagueTable(
        IReadOnlyList<SasAssessorSummaryRow> assessors,
        int? calendarYear)
    {
        var rows = assessors
            .Select(a =>
            {
                var panel = FilterPanelByYear(a.Assessments ?? [], calendarYear);
                if (calendarYear.HasValue && panel.Count == 0)
                {
                    return null;
                }

                var published = panel.Count(x =>
                    string.Equals(x.Status, "Published", StringComparison.OrdinalIgnoreCase));
                var (red, amber, green) = CountOutcomesFromPanel(panel);

                return new SasAssessorLeagueRowVm
                {
                    AssessorId = a.AssessorID ?? a.UserID ?? 0,
                    DisplayName = FormatName(a.FirstName, a.LastName, a.EmailAddress),
                    Email = a.EmailAddress?.Trim() ?? "",
                    PrimaryRole = string.IsNullOrWhiteSpace(a.PrimaryRole) ? "—" : a.PrimaryRole!.Trim(),
                    ServiceAssessmentCount = CountByType(panel, ServiceAssessmentType),
                    PeerReviewCount = CountByType(panel, PeerReviewType),
                    PanelCount = calendarYear.HasValue
                        ? panel.Count
                        : (a.AssessmentCount > 0 ? a.AssessmentCount : panel.Count),
                    PublishedCount = published,
                    Red = red,
                    Amber = amber,
                    Green = green,
                    Assignments = panel.Select(MapLeagueAssignment).ToList()
                };
            })
            .Where(r => r is not null)
            .Select(r => r!)
            .OrderByDescending(r => r.PanelCount)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Rank = i + 1;
        }

        return rows;
    }

    private static List<SasAssessorAssessmentRow> FilterPanelByYear(
        IReadOnlyList<SasAssessorAssessmentRow> panel,
        int? calendarYear)
    {
        if (!calendarYear.HasValue)
        {
            return panel.ToList();
        }

        return panel
            .Where(x => x.AssessmentDateTime is { } d && d.Year == calendarYear.Value)
            .ToList();
    }

    private static (int Red, int Amber, int Green) CountOutcomesFromPanel(IReadOnlyList<SasAssessorAssessmentRow> panel)
    {
        var red = 0;
        var amber = 0;
        var green = 0;
        foreach (var assignment in panel)
        {
            switch (NormalizeOutcome(assignment.Outcome))
            {
                case "Red":
                    red++;
                    break;
                case "Amber":
                    amber++;
                    break;
                case "Green":
                    green++;
                    break;
            }
        }

        return (red, amber, green);
    }

    private static int CountByType(IReadOnlyList<SasAssessorAssessmentRow> panel, string typeLabel) =>
        panel.Count(x => string.Equals(x.Type?.Trim(), typeLabel, StringComparison.OrdinalIgnoreCase));

    private static SasAssessorLeagueAssignmentVm MapLeagueAssignment(SasAssessorAssessmentRow assignment) =>
        new()
        {
            AssessmentId = assignment.AssessmentID,
            Name = assignment.Name?.Trim() ?? "",
            PanelRole = string.IsNullOrWhiteSpace(assignment.PanelRole) ? "—" : assignment.PanelRole!.Trim(),
            Type = string.IsNullOrWhiteSpace(assignment.Type) ? "—" : assignment.Type!.Trim(),
            Phase = string.IsNullOrWhiteSpace(assignment.Phase) ? "—" : assignment.Phase!.Trim(),
            Status = string.IsNullOrWhiteSpace(assignment.Status) ? "—" : assignment.Status!.Trim(),
            Outcome = NormalizeOutcome(assignment.Outcome),
            AssessmentDateTime = assignment.AssessmentDateTime
        };

    private static string FormatName(string? first, string? last, string? email)
    {
        var name = $"{first?.Trim()} {last?.Trim()}".Trim();
        return string.IsNullOrWhiteSpace(name) ? (email?.Trim() ?? "Unknown") : name;
    }

    private static string NormalizeOutcome(string? outcome)
    {
        var t = (outcome ?? "").Trim();
        if (string.IsNullOrEmpty(t))
        {
            return "Not rated";
        }

        if (t.Equals("Green", StringComparison.OrdinalIgnoreCase))
        {
            return "Green";
        }

        if (t.Contains("Amber", StringComparison.OrdinalIgnoreCase))
        {
            return "Amber";
        }

        if (t.Equals("Red", StringComparison.OrdinalIgnoreCase))
        {
            return "Red";
        }

        if (t.Contains("Not rated", StringComparison.OrdinalIgnoreCase))
        {
            return "Not rated";
        }

        return t;
    }

    private static IReadOnlyList<(string Key, string Label)> GetLastFinancialQuarters(DateTime anchorUtc, int count)
    {
        var quarters = new List<(string Key, string Label)>();
        var cursor = anchorUtc;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (quarters.Count < count)
        {
            var (key, label) = GetUkFinancialQuarter(cursor);
            if (seen.Add(key))
            {
                quarters.Add((key, label));
            }

            cursor = cursor.AddMonths(-3);
            if (quarters.Count >= count * 2)
            {
                break;
            }
        }

        quarters.Reverse();
        return quarters;
    }

    private static (string Key, string Label) GetUkFinancialQuarter(DateTime date)
    {
        var fyStartYear = date.Month >= 4 ? date.Year : date.Year - 1;
        var quarter = date.Month switch
        {
            >= 4 and <= 6 => 1,
            >= 7 and <= 9 => 2,
            >= 10 and <= 12 => 3,
            _ => 4
        };
        var fyEndShort = (fyStartYear + 1) % 100;
        var key = $"{fyStartYear}-{fyEndShort:D2}-Q{quarter}";
        var label = $"{fyStartYear}–{fyEndShort:D2} Q{quarter}";
        return (key, label);
    }

    private static string GetUkFinancialQuarterKey(DateTime date) => GetUkFinancialQuarter(date).Key;
}
