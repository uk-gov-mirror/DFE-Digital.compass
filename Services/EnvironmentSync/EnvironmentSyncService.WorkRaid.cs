using Compass.Data;
using Compass.Models;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services.EnvironmentSync;

public sealed partial class EnvironmentSyncService
{
  private async Task<IReadOnlyList<EnvironmentSyncCountLine>> PreviewWorkRaidAsync(
    CompassDbContext source,
    CompassDbContext target,
    CancellationToken cancellationToken)
  {
    var sourceProjects = await source.Projects.AsNoTracking().CountAsync(p => !p.IsDeleted, cancellationToken);
    var targetProjects = await target.Projects.AsNoTracking().CountAsync(p => !p.IsDeleted, cancellationToken);
    var sourceProjectCodes = await source.Projects.AsNoTracking()
      .Where(p => !p.IsDeleted && !string.IsNullOrWhiteSpace(p.ProjectCode))
      .Select(p => p.ProjectCode.Trim())
      .ToListAsync(cancellationToken);
    var targetCodes = await target.Projects.AsNoTracking()
      .Where(p => !p.IsDeleted && !string.IsNullOrWhiteSpace(p.ProjectCode))
      .Select(p => p.ProjectCode.Trim().ToLower())
      .ToListAsync(cancellationToken);
    var targetCodeSet = targetCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var wouldCreateProjects = sourceProjectCodes.Count(c => !targetCodeSet.Contains(c));
    var wouldUpdateProjects = sourceProjectCodes.Count - wouldCreateProjects;

    var sourceRisks = await source.Risks.AsNoTracking().CountAsync(r => !r.IsDeleted, cancellationToken);
    var targetRisks = await target.Risks.AsNoTracking().CountAsync(r => !r.IsDeleted, cancellationToken);
    var sourceIssues = await source.Issues.AsNoTracking().CountAsync(i => !i.IsDeleted, cancellationToken);
    var targetIssues = await target.Issues.AsNoTracking().CountAsync(i => !i.IsDeleted, cancellationToken);

    return
    [
      Line("Work items (projects)", sourceProjects, targetProjects, wouldCreateProjects, wouldUpdateProjects),
      Line("Risks", sourceRisks, targetRisks),
      Line("Issues", sourceIssues, targetIssues)
    ];
  }

  private async Task<EnvironmentSyncResult> SyncWorkRaidAsync(
    CompassDbContext source,
    CompassDbContext target,
    string sourceCatalog,
    string targetCatalog,
    bool dryRun,
    CancellationToken cancellationToken)
  {
    var messages = new List<string>();
    var errors = new List<string>();
    var created = 0;
    var updated = 0;
    var skipped = 0;

    var phaseMap = await SyncNamedLookupsAsync(
      source.PhaseLookups.AsNoTracking(),
      target,
      target.PhaseLookups,
      x => x.Name,
      (src, existing) =>
      {
        existing.Description = src.Description;
        existing.SortOrder = src.SortOrder;
        existing.IsActive = src.IsActive;
      },
      src => new PhaseLookup
      {
        Name = src.Name,
        Description = src.Description,
        SortOrder = src.SortOrder,
        IsActive = src.IsActive
      },
      dryRun,
      cancellationToken);

    var businessAreaMap = await SyncNamedLookupsAsync(
      source.BusinessAreaLookups.AsNoTracking(),
      target,
      target.BusinessAreaLookups,
      x => x.Name,
      (src, existing) =>
      {
        existing.Description = src.Description;
        existing.SortOrder = src.SortOrder;
        existing.IsActive = src.IsActive;
      },
      src => new BusinessAreaLookup
      {
        Name = src.Name,
        Description = src.Description,
        SortOrder = src.SortOrder,
        IsActive = src.IsActive
      },
      dryRun,
      cancellationToken);

    var ragMap = await SyncNamedLookupsAsync(
      source.RagStatusLookups.AsNoTracking(),
      target,
      target.RagStatusLookups,
      x => x.Name,
      (src, existing) =>
      {
        existing.Description = src.Description;
        existing.CssClass = src.CssClass;
        existing.SortOrder = src.SortOrder;
        existing.IsActive = src.IsActive;
      },
      src => new RagStatusLookup
      {
        Name = src.Name,
        Description = src.Description,
        CssClass = src.CssClass,
        SortOrder = src.SortOrder,
        IsActive = src.IsActive
      },
      dryRun,
      cancellationToken);

    var priorityMap = await SyncNamedLookupsAsync(
      source.DeliveryPriorities.AsNoTracking(),
      target,
      target.DeliveryPriorities,
      x => x.Name,
      (src, existing) =>
      {
        existing.Summary = src.Summary;
        existing.Description = src.Description;
        existing.CssClass = src.CssClass;
        existing.SortOrder = src.SortOrder;
        existing.IsActive = src.IsActive;
      },
      src => new DeliveryPriority
      {
        Name = src.Name,
        Summary = src.Summary,
        Description = src.Description,
        CssClass = src.CssClass,
        SortOrder = src.SortOrder,
        IsActive = src.IsActive
      },
      dryRun,
      cancellationToken);

    await SyncRaidLookupsAsync(source, target, dryRun, cancellationToken);
    var userMap = await BuildUserEmailMapAsync(source, target, dryRun, cancellationToken);
    var projectResult = await SyncProjectsAsync(source, target, phaseMap, businessAreaMap, ragMap, priorityMap, userMap, dryRun, cancellationToken);
    var riskResult = await SyncRisksAsync(source, target, projectResult.IdMap, userMap, dryRun, cancellationToken);
    var issueResult = await SyncIssuesAsync(source, target, projectResult.IdMap, riskResult.IdMap, userMap, dryRun, cancellationToken);

    created += projectResult.Created + riskResult.Created + issueResult.Created;
    updated += projectResult.Updated + riskResult.Updated + issueResult.Updated;
    skipped += projectResult.Skipped + riskResult.Skipped + issueResult.Skipped;
    messages.AddRange(projectResult.Messages);
    messages.AddRange(riskResult.Messages);
    messages.AddRange(issueResult.Messages);

    messages.Add($"Work and RAID sync from {sourceCatalog} to {targetCatalog} completed.");
    if (dryRun)
      messages.Insert(0, "Dry run — no changes were saved.");

    return new EnvironmentSyncResult
    {
      Success = errors.Count == 0,
      DryRun = dryRun,
      Direction = EnvironmentSyncDirection.ProdToDevWorkRaid,
      SourceCatalog = sourceCatalog,
      TargetCatalog = targetCatalog,
      Messages = messages,
      Errors = errors,
      Created = created,
      Updated = updated,
      Skipped = skipped
    };
  }

  private async Task SyncRaidLookupsAsync(
    CompassDbContext source,
    CompassDbContext target,
    bool dryRun,
    CancellationToken cancellationToken)
  {
    await SyncNamedLookupsAsync(source.RiskTiers.AsNoTracking(), target, target.RiskTiers, x => x.Name,
      (s, t) =>
      {
        t.Code = s.Code;
        t.Description = s.Description;
        t.Summary = s.Summary;
        t.SortOrder = s.SortOrder;
        t.GovernanceLevel = s.GovernanceLevel;
        t.IsProposedTier = s.IsProposedTier;
        t.IsActive = s.IsActive;
      },
      s => new RiskTier
      {
        Code = s.Code,
        Name = s.Name,
        Description = s.Description,
        Summary = s.Summary,
        SortOrder = s.SortOrder,
        GovernanceLevel = s.GovernanceLevel,
        IsProposedTier = s.IsProposedTier,
        IsActive = s.IsActive
      },
      dryRun, cancellationToken);

    await SyncRaidLookupAsync<RiskStatus>(source, target, dryRun, cancellationToken);
    await SyncRaidLookupAsync<RiskPriority>(source, target, dryRun, cancellationToken);
    await SyncRaidLookupAsync<RiskLikelihood>(source, target, dryRun, cancellationToken);
    await SyncRaidLookupAsync<RiskImpactLevel>(source, target, dryRun, cancellationToken);
    await SyncRaidLookupAsync<RiskProximity>(source, target, dryRun, cancellationToken);
    await SyncRaidLookupAsync<RiskCategory>(source, target, dryRun, cancellationToken);
    await SyncRaidLookupAsync<IssueStatus>(source, target, dryRun, cancellationToken);
    await SyncRaidLookupAsync<IssuePriority>(source, target, dryRun, cancellationToken);
    await SyncRaidLookupAsync<IssueSeverity>(source, target, dryRun, cancellationToken);
    await SyncRaidLookupAsync<IssueCategory>(source, target, dryRun, cancellationToken);
  }

  private async Task<Dictionary<int, int>> BuildUserEmailMapAsync(
    CompassDbContext source,
    CompassDbContext target,
    bool dryRun,
    CancellationToken cancellationToken)
  {
    var map = new Dictionary<int, int>();
    var sourceUsers = await source.Users.AsNoTracking()
      .Where(u => !string.IsNullOrWhiteSpace(u.Email))
      .ToListAsync(cancellationToken);
    var targetByEmail = await target.Users
      .Where(u => !string.IsNullOrWhiteSpace(u.Email))
      .ToDictionaryAsync(u => u.Email!.Trim().ToLowerInvariant(), u => u.Id, cancellationToken);

    foreach (var src in sourceUsers)
    {
      var email = src.Email!.Trim().ToLowerInvariant();
      if (targetByEmail.TryGetValue(email, out var targetId))
      {
        map[src.Id] = targetId;
        continue;
      }

      if (dryRun)
        continue;

      var user = new User
      {
        Email = src.Email,
        Name = src.Name,
        Role = UserRole.Visitor,
        CreatedAt = src.CreatedAt,
        UpdatedAt = DateTime.UtcNow
      };
      target.Users.Add(user);
      await target.SaveChangesAsync(cancellationToken);
      map[src.Id] = user.Id;
      targetByEmail[email] = user.Id;
    }

    return map;
  }

  private async Task<(Dictionary<int, int> IdMap, int Created, int Updated, int Skipped, List<string> Messages)> SyncProjectsAsync(
    CompassDbContext source,
    CompassDbContext target,
    Dictionary<int, int> phaseMap,
    Dictionary<int, int> businessAreaMap,
    Dictionary<int, int> ragMap,
    Dictionary<int, int> priorityMap,
    Dictionary<int, int> userMap,
    bool dryRun,
    CancellationToken cancellationToken)
  {
    var idMap = new Dictionary<int, int>();
    var created = 0;
    var updated = 0;
    var skipped = 0;
    var messages = new List<string>();

    var sourceProjects = await source.Projects.AsNoTracking()
      .Where(p => !p.IsDeleted)
      .ToListAsync(cancellationToken);
    var targetProjects = await target.Projects
      .Where(p => !p.IsDeleted)
      .ToListAsync(cancellationToken);

    var targetByCode = targetProjects
      .Where(p => !string.IsNullOrWhiteSpace(p.ProjectCode))
      .GroupBy(p => p.ProjectCode.Trim(), StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    foreach (var src in sourceProjects)
    {
      var key = ResolveProjectKey(src);
      if (string.IsNullOrWhiteSpace(key))
        continue;

      if (!targetByCode.TryGetValue(key, out var existing))
      {
        if (dryRun) { created++; continue; }

        var project = MapProject(src, phaseMap, businessAreaMap, ragMap, priorityMap, userMap);
        target.Projects.Add(project);
        await target.SaveChangesAsync(cancellationToken);
        idMap[src.Id] = project.Id;
        targetByCode[key] = project;
        created++;
      }
      else
      {
        idMap[src.Id] = existing.Id;
        if (dryRun) { updated++; continue; }

        CopyProjectFields(existing, src, phaseMap, businessAreaMap, ragMap, priorityMap, userMap);
        updated++;
      }
    }

    if (!dryRun)
      await target.SaveChangesAsync(cancellationToken);

    messages.Add($"Projects: {created} created, {updated} updated, {skipped} skipped.");
    return (idMap, created, updated, skipped, messages);
  }

  private async Task<(Dictionary<int, int> IdMap, int Created, int Updated, int Skipped, List<string> Messages)> SyncRisksAsync(
    CompassDbContext source,
    CompassDbContext target,
    Dictionary<int, int> projectMap,
    Dictionary<int, int> userMap,
    bool dryRun,
    CancellationToken cancellationToken)
  {
    var idMap = new Dictionary<int, int>();
    var created = 0;
    var updated = 0;
    var skipped = 0;
    var messages = new List<string>();

    var lookupMaps = await BuildRiskLookupMapsAsync(source, target, cancellationToken);
    var sourceRows = await source.Risks.AsNoTracking()
      .Where(r => !r.IsDeleted)
      .ToListAsync(cancellationToken);
    var targetRows = await target.Risks
      .Where(r => !r.IsDeleted)
      .ToListAsync(cancellationToken);

    var targetByKey = targetRows.ToDictionary(
      r => $"{r.ProjectId}|{r.Title.Trim()}",
      StringComparer.OrdinalIgnoreCase);

    foreach (var src in sourceRows)
    {
      var projectId = RemapSettingsProjectId(src.ProjectId, projectMap);
      var key = $"{projectId}|{src.Title.Trim()}";
      if (string.IsNullOrWhiteSpace(key))
        continue;

      if (!targetByKey.TryGetValue(key, out var existing))
      {
        if (dryRun) { created++; continue; }

        var risk = MapRisk(src, projectId, userMap, lookupMaps);
        target.Risks.Add(risk);
        await target.SaveChangesAsync(cancellationToken);
        idMap[src.Id] = risk.Id;
        targetByKey[key] = risk;
        created++;
      }
      else
      {
        idMap[src.Id] = existing.Id;
        if (dryRun) { updated++; continue; }
        CopyRiskFields(existing, src, projectId, userMap, lookupMaps);
        updated++;
      }
    }

    if (!dryRun)
      await target.SaveChangesAsync(cancellationToken);

    messages.Add($"Risks: {created} created, {updated} updated, {skipped} skipped.");
    return (idMap, created, updated, skipped, messages);
  }

  private async Task<(int Created, int Updated, int Skipped, List<string> Messages)> SyncIssuesAsync(
    CompassDbContext source,
    CompassDbContext target,
    Dictionary<int, int> projectMap,
    Dictionary<int, int> riskMap,
    Dictionary<int, int> userMap,
    bool dryRun,
    CancellationToken cancellationToken)
  {
    var created = 0;
    var updated = 0;
    var skipped = 0;
    var messages = new List<string>();

    var lookupMaps = await BuildIssueLookupMapsAsync(source, target, cancellationToken);
    var sourceRows = await source.Issues.AsNoTracking()
      .Where(i => !i.IsDeleted)
      .ToListAsync(cancellationToken);
    var targetRows = await target.Issues
      .Where(i => !i.IsDeleted)
      .ToListAsync(cancellationToken);

    var sourceProjectCodes = await source.Projects.AsNoTracking()
      .Where(p => !p.IsDeleted)
      .ToDictionaryAsync(p => p.Id, ResolveProjectKey, cancellationToken);

    var targetByKey = targetRows.ToDictionary(
      i => BuildIssueKey(i.ProjectId, i.Title, i.DetectedDate),
      StringComparer.OrdinalIgnoreCase);

    foreach (var src in sourceRows)
    {
      var projectId = RemapSettingsProjectId(src.ProjectId, projectMap);
      var key = BuildIssueKey(projectId, src.Title, src.DetectedDate);
      if (string.IsNullOrWhiteSpace(key))
        continue;

      if (!targetByKey.TryGetValue(key, out var existing))
      {
        if (dryRun) { created++; continue; }

        var issue = MapIssue(src, projectId, riskMap, userMap, lookupMaps);
        target.Issues.Add(issue);
        await target.SaveChangesAsync(cancellationToken);
        targetByKey[key] = issue;
        created++;
      }
      else
      {
        if (dryRun) { updated++; continue; }
        CopyIssueFields(existing, src, projectId, riskMap, userMap, lookupMaps);
        updated++;
      }
    }

    if (!dryRun)
      await target.SaveChangesAsync(cancellationToken);

    messages.Add($"Issues: {created} created, {updated} updated, {skipped} skipped.");
    return (created, updated, skipped, messages);
  }

  private static string ResolveProjectKey(Project project)
  {
    if (!string.IsNullOrWhiteSpace(project.ProjectCode))
      return project.ProjectCode.Trim();
    if (!string.IsNullOrWhiteSpace(project.HistoricBuRTId))
      return $"historic:{project.HistoricBuRTId.Trim()}";
    return $"id:{project.Id}";
  }

  private static int? RemapSettingsProjectId(int? sourceProjectId, Dictionary<int, int> projectMap) =>
    sourceProjectId.HasValue && projectMap.TryGetValue(sourceProjectId.Value, out var mapped) ? mapped : null;

  private static string BuildIssueKey(int? projectId, string title, DateTime detectedDate) =>
    $"{projectId}|{title.Trim()}|{detectedDate:yyyy-MM-dd}";

  private async Task SyncRaidLookupAsync<TLookup>(
    CompassDbContext source,
    CompassDbContext target,
    bool dryRun,
    CancellationToken cancellationToken) where TLookup : RaidLookupBase, new()
  {
    var sourceSet = source.Set<TLookup>();
    var targetSet = target.Set<TLookup>();
    await SyncNamedLookupsAsync(
      sourceSet.AsNoTracking(),
      target,
      targetSet,
      x => x.Label,
      (src, existing) =>
      {
        existing.Code = src.Code;
        existing.Description = src.Description;
        existing.SortOrder = src.SortOrder;
        existing.IsActive = src.IsActive;
        if (src is RiskLikelihood srcLikelihood && existing is RiskLikelihood targetLikelihood)
          targetLikelihood.MatrixScore = srcLikelihood.MatrixScore;
        if (src is RiskImpactLevel srcImpact && existing is RiskImpactLevel targetImpact)
          targetImpact.MatrixScore = srcImpact.MatrixScore;
      },
      src => new TLookup
      {
        Code = src.Code,
        Label = src.Label,
        Description = src.Description,
        SortOrder = src.SortOrder,
        IsActive = src.IsActive
      },
      dryRun,
      cancellationToken);
  }

  private static Project MapProject(
    Project src,
    Dictionary<int, int> phaseMap,
    Dictionary<int, int> businessAreaMap,
    Dictionary<int, int> ragMap,
    Dictionary<int, int> priorityMap,
    Dictionary<int, int> userMap)
  {
    var project = new Project
    {
      ProjectCode = src.ProjectCode,
      Title = src.Title,
      HistoricBuRTId = src.HistoricBuRTId,
      CreatedAt = src.CreatedAt,
      UpdatedAt = DateTime.UtcNow
    };
    CopyProjectFields(project, src, phaseMap, businessAreaMap, ragMap, priorityMap, userMap);
    return project;
  }

  private static void CopyProjectFields(
    Project target,
    Project src,
    Dictionary<int, int> phaseMap,
    Dictionary<int, int> businessAreaMap,
    Dictionary<int, int> ragMap,
    Dictionary<int, int> priorityMap,
    Dictionary<int, int> userMap)
  {
    target.ProjectCode = src.ProjectCode;
    target.Title = src.Title;
    target.Aim = src.Aim;
    target.StrategicObjectives = src.StrategicObjectives;
    target.MissionPillars = src.MissionPillars;
    target.StartDate = src.StartDate;
    target.TargetDeliveryDate = src.TargetDeliveryDate;
    target.ActualDeliveryDate = src.ActualDeliveryDate;
    target.IsFlagship = src.IsFlagship;
    target.IsAiInitiative = src.IsAiInitiative;
    target.ShowInFips = src.ShowInFips;
    target.RagStatusLookupId = RemapNullable(src.RagStatusLookupId, ragMap);
    target.RagStatus = src.RagStatus;
    target.RagJustification = src.RagJustification;
    target.PathToGreen = src.PathToGreen;
    target.PhaseId = RemapNullable(src.PhaseId, phaseMap);
    target.BusinessAreaId = RemapNullable(src.BusinessAreaId, businessAreaMap);
    target.HistoricBuRTId = src.HistoricBuRTId;
    target.PrimaryContactUserId = RemapNullable(src.PrimaryContactUserId, userMap);
    target.DeliveryPriorityId = RemapNullable(src.DeliveryPriorityId, priorityMap);
    target.DeliveryPriorityChangeReason = src.DeliveryPriorityChangeReason;
    target.IsMultiDepartmentProject = src.IsMultiDepartmentProject;
    target.OtherDepartments = src.OtherDepartments;
    target.BusinessCaseApproval = src.BusinessCaseApproval;
    target.TotalPermFte = src.TotalPermFte;
    target.TotalMspFte = src.TotalMspFte;
    target.Status = src.Status;
    target.StatusChangeReason = src.StatusChangeReason;
    target.IsDeleted = false;
    target.CreationMethod = src.CreationMethod;
    target.UpdatedAt = DateTime.UtcNow;
  }

  private async Task<RiskLookupMaps> BuildRiskLookupMapsAsync(
    CompassDbContext source,
    CompassDbContext target,
    CancellationToken cancellationToken)
  {
    return new RiskLookupMaps
    {
      Tier = await BuildLookupMapAsync(source.RiskTiers, target.RiskTiers, x => x.Name, cancellationToken),
      Status = await BuildLookupMapAsync(source.RiskStatuses, target.RiskStatuses, x => x.Label, cancellationToken),
      Priority = await BuildLookupMapAsync(source.RiskPriorities, target.RiskPriorities, x => x.Label, cancellationToken),
      Likelihood = await BuildLookupMapAsync(source.RiskLikelihoods, target.RiskLikelihoods, x => x.Label, cancellationToken),
      Impact = await BuildLookupMapAsync(source.RiskImpactLevels, target.RiskImpactLevels, x => x.Label, cancellationToken),
      Proximity = await BuildLookupMapAsync(source.RiskProximities, target.RiskProximities, x => x.Label, cancellationToken),
      Category = await BuildLookupMapAsync(source.RiskCategories, target.RiskCategories, x => x.Label, cancellationToken)
    };
  }

  private async Task<IssueLookupMaps> BuildIssueLookupMapsAsync(
    CompassDbContext source,
    CompassDbContext target,
    CancellationToken cancellationToken) =>
    new()
    {
      Status = await BuildLookupMapAsync(source.IssueStatuses, target.IssueStatuses, x => x.Label, cancellationToken),
      Priority = await BuildLookupMapAsync(source.IssuePriorities, target.IssuePriorities, x => x.Label, cancellationToken),
      Severity = await BuildLookupMapAsync(source.IssueSeverities, target.IssueSeverities, x => x.Label, cancellationToken),
      Category = await BuildLookupMapAsync(source.IssueCategories, target.IssueCategories, x => x.Label, cancellationToken)
    };

  private static async Task<Dictionary<int, int>> BuildLookupMapAsync<T>(
    IQueryable<T> sourceQuery,
    IQueryable<T> targetQuery,
    Func<T, string> getKey,
    CancellationToken cancellationToken) where T : class
  {
    var sourceRows = await sourceQuery.AsNoTracking().ToListAsync(cancellationToken);
    var targetRows = await targetQuery.AsNoTracking().ToListAsync(cancellationToken);
    var targetByName = targetRows.ToDictionary(
      x => getKey(x).Trim(),
      x => GetEntityId(x),
      StringComparer.OrdinalIgnoreCase);

    var map = new Dictionary<int, int>();
    foreach (var src in sourceRows)
    {
      var srcId = GetEntityId(src);
      var key = getKey(src).Trim();
      if (targetByName.TryGetValue(key, out var targetId))
        map[srcId] = targetId;
    }

    return map;
  }

  private static Risk MapRisk(
    Risk src,
    int? projectId,
    Dictionary<int, int> userMap,
    RiskLookupMaps lookups)
  {
    var risk = new Risk
    {
      Title = src.Title,
      CreatedAt = src.CreatedAt,
      UpdatedAt = DateTime.UtcNow
    };
    CopyRiskFields(risk, src, projectId, userMap, lookups);
    return risk;
  }

  private static void CopyRiskFields(
    Risk target,
    Risk src,
    int? projectId,
    Dictionary<int, int> userMap,
    RiskLookupMaps lookups)
  {
    target.ProjectId = projectId;
    target.FipsId = src.FipsId;
    target.ProductDocumentId = src.ProductDocumentId;
    target.Title = src.Title;
    target.Description = src.Description;
    target.Category = src.Category;
    target.BusinessArea = src.BusinessArea;
    target.RiskTierId = RemapNullable(src.RiskTierId, lookups.Tier);
    target.OwnerEmail = src.OwnerEmail;
    target.ImpactRating = src.ImpactRating;
    target.LikelihoodRating = src.LikelihoodRating;
    target.RiskScore = src.RiskScore;
    target.ProximityDate = src.ProximityDate;
    target.Response = src.Response;
    target.ResidualImpact = src.ResidualImpact;
    target.ResidualLikelihood = src.ResidualLikelihood;
    target.TargetDate = src.TargetDate;
    target.Status = src.Status;
    target.ClosedDate = src.ClosedDate;
    target.Notes = src.Notes;
    target.Source = src.Source;
    target.SourceId = src.SourceId;
    target.OwnerUserId = RemapNullable(src.OwnerUserId, userMap);
    target.RiskStatusId = RemapNullable(src.RiskStatusId, lookups.Status);
    target.RiskPriorityId = RemapNullable(src.RiskPriorityId, lookups.Priority);
    target.RiskLikelihoodId = RemapNullable(src.RiskLikelihoodId, lookups.Likelihood);
    target.RiskImpactLevelId = RemapNullable(src.RiskImpactLevelId, lookups.Impact);
    target.RiskProximityId = RemapNullable(src.RiskProximityId, lookups.Proximity);
    target.RiskCategoryId = RemapNullable(src.RiskCategoryId, lookups.Category);
    target.InherentScore = src.InherentScore;
    target.ResidualScore = src.ResidualScore;
    target.IdentifiedDate = src.IdentifiedDate;
    target.NextReviewDate = src.NextReviewDate;
    target.LastReviewDate = src.LastReviewDate;
    target.RaidAssociationKind = src.RaidAssociationKind;
    target.SroUserId = RemapNullable(src.SroUserId, userMap);
    target.ResponseStrategy = src.ResponseStrategy;
    target.HowIdentified = src.HowIdentified;
    target.Cause = src.Cause;
    target.ImpactIfRealised = src.ImpactIfRealised;
    target.Contingency = src.Contingency;
    target.Assurance = src.Assurance;
    target.FinancialImpact = src.FinancialImpact;
    target.CreatedByUserId = RemapNullable(src.CreatedByUserId, userMap);
    target.UpdatedByUserId = RemapNullable(src.UpdatedByUserId, userMap);
    target.ClosedByUserId = RemapNullable(src.ClosedByUserId, userMap);
    target.IsDeleted = false;
    target.UpdatedAt = DateTime.UtcNow;
  }

  private static Issue MapIssue(
    Issue src,
    int? projectId,
    Dictionary<int, int> riskMap,
    Dictionary<int, int> userMap,
    IssueLookupMaps lookups)
  {
    var issue = new Issue
    {
      Title = src.Title,
      DetectedDate = src.DetectedDate,
      CreatedAt = src.CreatedAt,
      UpdatedAt = DateTime.UtcNow
    };
    CopyIssueFields(issue, src, projectId, riskMap, userMap, lookups);
    return issue;
  }

  private static void CopyIssueFields(
    Issue target,
    Issue src,
    int? projectId,
    Dictionary<int, int> riskMap,
    Dictionary<int, int> userMap,
    IssueLookupMaps lookups)
  {
    target.ProjectId = projectId;
    target.FipsId = src.FipsId;
    target.ProductDocumentId = src.ProductDocumentId;
    target.Title = src.Title;
    target.Description = src.Description;
    target.Category = src.Category;
    target.BusinessArea = src.BusinessArea;
    target.OwnerUserId = RemapNullable(src.OwnerUserId, userMap);
    target.Severity = src.Severity;
    target.Priority = src.Priority;
    target.DetectedDate = src.DetectedDate;
    target.TargetResolutionDate = src.TargetResolutionDate;
    target.Status = src.Status;
    target.ResolutionSummary = src.ResolutionSummary;
    target.Workaround = src.Workaround;
    target.SourceType = src.SourceType;
    target.SourceReference = src.SourceReference;
    target.SourceRecordUrl = src.SourceRecordUrl;
    target.SourceRiskId = RemapNullable(src.SourceRiskId, riskMap);
    target.BlockedFlag = src.BlockedFlag;
    target.ClosedDate = src.ClosedDate;
    target.Source = src.Source;
    target.SourceId = src.SourceId;
    target.RiskId = RemapNullable(src.RiskId, riskMap);
    target.RaidAssociationKind = src.RaidAssociationKind;
    target.SroUserId = RemapNullable(src.SroUserId, userMap);
    target.UserImpactSummary = src.UserImpactSummary;
    target.ServiceImpactSummary = src.ServiceImpactSummary;
    target.StatusId = RemapNullable(src.StatusId, lookups.Status);
    target.PriorityId = RemapNullable(src.PriorityId, lookups.Priority);
    target.SeverityId = RemapNullable(src.SeverityId, lookups.Severity);
    target.IssueCategoryId = RemapNullable(src.IssueCategoryId, lookups.Category);
    target.ResolvedDate = src.ResolvedDate;
    target.CreatedByUserId = RemapNullable(src.CreatedByUserId, userMap);
    target.UpdatedByUserId = RemapNullable(src.UpdatedByUserId, userMap);
    target.ClosedByUserId = RemapNullable(src.ClosedByUserId, userMap);
    target.DetailedCause = src.DetailedCause;
    target.AssuranceArrangements = src.AssuranceArrangements;
    target.IsDeleted = false;
    target.UpdatedAt = DateTime.UtcNow;
  }

  private sealed class RiskLookupMaps
  {
    public Dictionary<int, int> Tier { get; init; } = [];
    public Dictionary<int, int> Status { get; init; } = [];
    public Dictionary<int, int> Priority { get; init; } = [];
    public Dictionary<int, int> Likelihood { get; init; } = [];
    public Dictionary<int, int> Impact { get; init; } = [];
    public Dictionary<int, int> Proximity { get; init; } = [];
    public Dictionary<int, int> Category { get; init; } = [];
  }

  private sealed class IssueLookupMaps
  {
    public Dictionary<int, int> Status { get; init; } = [];
    public Dictionary<int, int> Priority { get; init; } = [];
    public Dictionary<int, int> Severity { get; init; } = [];
    public Dictionary<int, int> Category { get; init; } = [];
  }
}
