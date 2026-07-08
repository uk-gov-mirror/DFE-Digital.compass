using Compass.Data;
using Compass.Models;
using Compass.Models.Modern.Work;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services.Modern;

/// <summary>Loads and persists milestone status/RAG on monthly and weekly work updates.</summary>
public static class MilestoneReportHelper
{
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "not_started", "in_progress", "on_track", "at_risk", "delayed", "complete", "cancelled"
    };

    public static bool IsCompletedStatus(string? status) =>
        string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    public static async Task<List<ReportMilestoneRowViewModel>> LoadReportMilestoneRowsAsync(
        CompassDbContext context,
        int projectId,
        int? monthlyUpdateId,
        int? weeklyUpdateId,
        CancellationToken cancellationToken = default)
    {
        var milestones = await context.Milestones.AsNoTracking()
            .Include(m => m.RagStatusLookup)
            .Where(m => m.ProjectId == projectId && !m.IsDeleted)
            .OrderBy(m => m.DueDate)
            .ThenBy(m => m.Name)
            .ToListAsync(cancellationToken);

        var inProgress = milestones.Where(m => !IsCompletedStatus(m.Status)).ToList();
        if (inProgress.Count == 0)
            return [];

        Dictionary<int, WorkUpdateMilestoneEntry> savedByMilestoneId = new();
        if (monthlyUpdateId is > 0)
        {
            var saved = await context.WorkUpdateMilestoneEntries.AsNoTracking()
                .Include(e => e.RagStatusLookup)
                .Where(e => e.ProjectMonthlyUpdateId == monthlyUpdateId.Value)
                .ToListAsync(cancellationToken);
            savedByMilestoneId = saved.ToDictionary(e => e.MilestoneId);
        }
        else if (weeklyUpdateId is > 0)
        {
            var saved = await context.WorkUpdateMilestoneEntries.AsNoTracking()
                .Include(e => e.RagStatusLookup)
                .Where(e => e.ProjectWeeklyWorkUpdateId == weeklyUpdateId.Value)
                .ToListAsync(cancellationToken);
            savedByMilestoneId = saved.ToDictionary(e => e.MilestoneId);
        }

        return inProgress.Select(m =>
        {
            if (savedByMilestoneId.TryGetValue(m.Id, out var saved))
            {
                return new ReportMilestoneRowViewModel
                {
                    MilestoneId = m.Id,
                    Name = m.Name,
                    DueDate = m.DueDate,
                    Status = saved.Status,
                    RagStatusId = saved.RagStatusLookupId,
                    RagName = saved.RagStatusLookup?.Name,
                    UpdateNote = saved.UpdateNote
                };
            }

            return new ReportMilestoneRowViewModel
            {
                MilestoneId = m.Id,
                Name = m.Name,
                DueDate = m.DueDate,
                Status = m.Status,
                RagStatusId = m.RagStatusLookupId,
                RagName = m.RagStatusLookup?.Name
            };
        }).ToList();
    }

    public static List<ReportMilestoneRowViewModel> ApplyPostedMilestoneRows(
        IReadOnlyList<ReportMilestoneRowViewModel> current,
        IReadOnlyDictionary<int, string>? postedStatus,
        IReadOnlyDictionary<int, int?>? postedRagStatusId,
        IReadOnlyDictionary<int, string>? postedUpdateNote,
        IReadOnlyDictionary<int, RagStatusLookup> activeRagById)
    {
        if (current.Count == 0)
            return [];

        return current.Select(row =>
        {
            var status = postedStatus != null && postedStatus.TryGetValue(row.MilestoneId, out var ps) && !string.IsNullOrWhiteSpace(ps)
                ? ps.Trim().ToLowerInvariant()
                : row.Status;
            int? ragId = postedRagStatusId != null && postedRagStatusId.TryGetValue(row.MilestoneId, out var pr)
                ? pr
                : row.RagStatusId;
            var note = postedUpdateNote != null && postedUpdateNote.TryGetValue(row.MilestoneId, out var pn)
                ? pn
                : row.UpdateNote;

            string? ragName = null;
            if (ragId is > 0 && activeRagById.TryGetValue(ragId.Value, out var rag))
                ragName = rag.Name;

            return new ReportMilestoneRowViewModel
            {
                MilestoneId = row.MilestoneId,
                Name = row.Name,
                DueDate = row.DueDate,
                Status = status,
                RagStatusId = ragId,
                RagName = ragName,
                UpdateNote = note
            };
        }).ToList();
    }

    public static void ValidatePostedMilestones(
        Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelState,
        bool isSubmit,
        IReadOnlyList<ReportMilestoneRowViewModel> rows,
        IReadOnlyDictionary<int, RagStatusLookup> activeRagById)
    {
        if (!isSubmit || rows.Count == 0)
            return;

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Status) || !ValidStatuses.Contains(row.Status))
            {
                modelState.AddModelError(
                    $"milestoneStatus[{row.MilestoneId}]",
                    $"Select a valid status for milestone “{row.Name}”.");
            }

            if (!row.RagStatusId.HasValue)
            {
                modelState.AddModelError(
                    $"milestoneRagStatusId[{row.MilestoneId}]",
                    $"Select a RAG rating for milestone “{row.Name}”.");
            }
            else if (!activeRagById.ContainsKey(row.RagStatusId.Value))
            {
                modelState.AddModelError(
                    $"milestoneRagStatusId[{row.MilestoneId}]",
                    $"Select a valid RAG rating for milestone “{row.Name}”.");
            }
        }
    }

    public static async Task PersistMilestoneEntriesForMonthlyUpdateAsync(
        CompassDbContext context,
        int projectId,
        int monthlyUpdateId,
        IReadOnlyList<ReportMilestoneRowViewModel> rows,
        IReadOnlyDictionary<int, RagStatusLookup> activeRagById,
        string userEmail,
        string? userName,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
            return;

        var milestoneIds = rows.Select(r => r.MilestoneId).ToList();
        var milestones = await context.Milestones
            .Where(m => m.ProjectId == projectId && milestoneIds.Contains(m.Id) && !m.IsDeleted)
            .ToDictionaryAsync(m => m.Id, cancellationToken);

        var existing = await context.WorkUpdateMilestoneEntries
            .Where(e => e.ProjectMonthlyUpdateId == monthlyUpdateId)
            .ToListAsync(cancellationToken);
        context.WorkUpdateMilestoneEntries.RemoveRange(existing);

        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            if (!milestones.TryGetValue(row.MilestoneId, out var milestone))
                continue;

            var status = row.Status.Trim().ToLowerInvariant();
            if (!ValidStatuses.Contains(status))
                continue;

            int? ragId = row.RagStatusId is > 0 && activeRagById.ContainsKey(row.RagStatusId.Value)
                ? row.RagStatusId
                : null;

            context.WorkUpdateMilestoneEntries.Add(new WorkUpdateMilestoneEntry
            {
                MilestoneId = row.MilestoneId,
                Status = status,
                RagStatusLookupId = ragId,
                UpdateNote = string.IsNullOrWhiteSpace(row.UpdateNote) ? null : row.UpdateNote.Trim(),
                ProjectMonthlyUpdateId = monthlyUpdateId,
                UpdatedAt = now
            });

            var statusChanged = !string.Equals(milestone.Status, status, StringComparison.OrdinalIgnoreCase);
            var ragChanged = milestone.RagStatusLookupId != ragId;
            var noteProvided = !string.IsNullOrWhiteSpace(row.UpdateNote);

            if (statusChanged || ragChanged)
            {
                context.MilestoneUpdates.Add(new MilestoneUpdate
                {
                    MilestoneId = milestone.Id,
                    UpdateDetails = noteProvided
                        ? row.UpdateNote!.Trim()
                        : BuildAutoUpdateDetails(statusChanged, ragChanged, milestone, status, ragId, activeRagById),
                    PreviousStatus = milestone.Status,
                    NewStatus = statusChanged ? status : null,
                    PreviousRagStatusLookupId = milestone.RagStatusLookupId,
                    NewRagStatusLookupId = ragChanged ? ragId : null,
                    UpdatedByEmail = userEmail,
                    UpdatedByName = userName,
                    UpdatedAt = now
                });

                milestone.Status = status;
                milestone.RagStatusLookupId = ragId;
                milestone.UpdatedAt = now;
            }
            else if (noteProvided)
            {
                context.MilestoneUpdates.Add(new MilestoneUpdate
                {
                    MilestoneId = milestone.Id,
                    UpdateDetails = row.UpdateNote!.Trim(),
                    PreviousStatus = milestone.Status,
                    NewStatus = null,
                    PreviousRagStatusLookupId = milestone.RagStatusLookupId,
                    NewRagStatusLookupId = null,
                    UpdatedByEmail = userEmail,
                    UpdatedByName = userName,
                    UpdatedAt = now
                });
                milestone.UpdatedAt = now;
            }
        }
    }

    public static async Task PersistMilestoneEntriesForWeeklyUpdateAsync(
        CompassDbContext context,
        int projectId,
        int weeklyUpdateId,
        IReadOnlyList<ReportMilestoneRowViewModel> rows,
        IReadOnlyDictionary<int, RagStatusLookup> activeRagById,
        string userEmail,
        string? userName,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
            return;

        var milestoneIds = rows.Select(r => r.MilestoneId).ToList();
        var milestones = await context.Milestones
            .Where(m => m.ProjectId == projectId && milestoneIds.Contains(m.Id) && !m.IsDeleted)
            .ToDictionaryAsync(m => m.Id, cancellationToken);

        var existing = await context.WorkUpdateMilestoneEntries
            .Where(e => e.ProjectWeeklyWorkUpdateId == weeklyUpdateId)
            .ToListAsync(cancellationToken);
        context.WorkUpdateMilestoneEntries.RemoveRange(existing);

        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            if (!milestones.TryGetValue(row.MilestoneId, out var milestone))
                continue;

            var status = row.Status.Trim().ToLowerInvariant();
            if (!ValidStatuses.Contains(status))
                continue;

            int? ragId = row.RagStatusId is > 0 && activeRagById.ContainsKey(row.RagStatusId.Value)
                ? row.RagStatusId
                : null;

            context.WorkUpdateMilestoneEntries.Add(new WorkUpdateMilestoneEntry
            {
                MilestoneId = row.MilestoneId,
                Status = status,
                RagStatusLookupId = ragId,
                UpdateNote = string.IsNullOrWhiteSpace(row.UpdateNote) ? null : row.UpdateNote.Trim(),
                ProjectWeeklyWorkUpdateId = weeklyUpdateId,
                UpdatedAt = now
            });

            var statusChanged = !string.Equals(milestone.Status, status, StringComparison.OrdinalIgnoreCase);
            var ragChanged = milestone.RagStatusLookupId != ragId;
            var noteProvided = !string.IsNullOrWhiteSpace(row.UpdateNote);

            if (statusChanged || ragChanged)
            {
                context.MilestoneUpdates.Add(new MilestoneUpdate
                {
                    MilestoneId = milestone.Id,
                    UpdateDetails = noteProvided
                        ? row.UpdateNote!.Trim()
                        : BuildAutoUpdateDetails(statusChanged, ragChanged, milestone, status, ragId, activeRagById),
                    PreviousStatus = milestone.Status,
                    NewStatus = statusChanged ? status : null,
                    PreviousRagStatusLookupId = milestone.RagStatusLookupId,
                    NewRagStatusLookupId = ragChanged ? ragId : null,
                    UpdatedByEmail = userEmail,
                    UpdatedByName = userName,
                    UpdatedAt = now
                });

                milestone.Status = status;
                milestone.RagStatusLookupId = ragId;
                milestone.UpdatedAt = now;
            }
            else if (noteProvided)
            {
                context.MilestoneUpdates.Add(new MilestoneUpdate
                {
                    MilestoneId = milestone.Id,
                    UpdateDetails = row.UpdateNote!.Trim(),
                    PreviousStatus = milestone.Status,
                    NewStatus = null,
                    PreviousRagStatusLookupId = milestone.RagStatusLookupId,
                    NewRagStatusLookupId = null,
                    UpdatedByEmail = userEmail,
                    UpdatedByName = userName,
                    UpdatedAt = now
                });
                milestone.UpdatedAt = now;
            }
        }
    }

    public static async Task<List<ReportMilestoneRowViewModel>> LoadSubmittedMilestoneRowsAsync(
        CompassDbContext context,
        int? monthlyUpdateId,
        int? weeklyUpdateId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<WorkUpdateMilestoneEntry> query = context.WorkUpdateMilestoneEntries.AsNoTracking()
            .Include(e => e.Milestone)
            .Include(e => e.RagStatusLookup);

        if (monthlyUpdateId is > 0)
            query = query.Where(e => e.ProjectMonthlyUpdateId == monthlyUpdateId.Value);
        else if (weeklyUpdateId is > 0)
            query = query.Where(e => e.ProjectWeeklyWorkUpdateId == weeklyUpdateId.Value);
        else
            return [];

        var entries = await query
            .OrderBy(e => e.Milestone.DueDate)
            .ThenBy(e => e.Milestone.Name)
            .ToListAsync(cancellationToken);

        return entries.Select(e => new ReportMilestoneRowViewModel
        {
            MilestoneId = e.MilestoneId,
            Name = e.Milestone.Name,
            DueDate = e.Milestone.DueDate,
            Status = e.Status,
            RagStatusId = e.RagStatusLookupId,
            RagName = e.RagStatusLookup?.Name,
            UpdateNote = e.UpdateNote
        }).ToList();
    }

    public static async Task<Dictionary<int, RagStatusLookup>> LoadActiveRagLookupsAsync(
        CompassDbContext context,
        CancellationToken cancellationToken = default) =>
        await context.RagStatusLookups.AsNoTracking()
            .Where(r => r.IsActive)
            .ToDictionaryAsync(r => r.Id, cancellationToken);

    private static string BuildAutoUpdateDetails(
        bool statusChanged,
        bool ragChanged,
        Milestone milestone,
        string newStatus,
        int? newRagId,
        IReadOnlyDictionary<int, RagStatusLookup> activeRagById)
    {
        var parts = new List<string>();
        if (statusChanged)
            parts.Add($"Status updated from {FormatStatus(milestone.Status)} to {FormatStatus(newStatus)}.");
        if (ragChanged)
        {
            var prev = milestone.RagStatusLookupId is > 0 && activeRagById.TryGetValue(milestone.RagStatusLookupId.Value, out var pr)
                ? pr.Name
                : "not set";
            var next = newRagId is > 0 && activeRagById.TryGetValue(newRagId.Value, out var nr)
                ? nr.Name
                : "not set";
            parts.Add($"RAG updated from {prev} to {next}.");
        }

        return string.Join(" ", parts);
    }

    private static string FormatStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? "not set" : status.Replace('_', ' ');
}
