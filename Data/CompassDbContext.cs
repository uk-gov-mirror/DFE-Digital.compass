using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Data;
using System.Text.Json;
using Compass.Models;
using Compass.Models.DemandPipeline;
using Compass.Models.DemandTriage;
using Compass.Models.Fips;
using Compass.Services;

namespace Compass.Data;

public partial class CompassDbContext : DbContext
{
    private readonly IAuditContextProvider _auditContextProvider;
    private bool _suppressAuditLogging;
    private bool _auditSchemaChecked;
    private bool _auditColumnsAvailable = true;

    public CompassDbContext(DbContextOptions<CompassDbContext> options) : this(options, new NullAuditContextProvider())
    {
    }

    public CompassDbContext(
        DbContextOptions<CompassDbContext> options,
        IAuditContextProvider auditContextProvider) : base(options)
    {
        _auditContextProvider = auditContextProvider;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Suppress pending model changes warning for now
        optionsBuilder.ConfigureWarnings(warnings =>
            warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Configure default string length for SQL Server (to avoid nvarchar(max) for indexed columns)
        configurationBuilder.Properties<string>()
            .HaveMaxLength(450); // SQL Server index key size limit
    }

    // User management
    public DbSet<User> Users { get; set; }
    public DbSet<UserBusinessAreaRoleAssignment> UserBusinessAreaRoleAssignments { get; set; }
    public DbSet<BusinessAreaAdminMember> BusinessAreaAdminMembers { get; set; }
    public DbSet<BusinessAreaLeadershipMember> BusinessAreaLeadershipMembers { get; set; }
    public DbSet<CompassNotificationSetting> CompassNotificationSettings { get; set; }
    public DbSet<CompassNotificationEmailLog> CompassNotificationEmailLogs { get; set; }
    public DbSet<HttpErrorEmailSettings> HttpErrorEmailSettings { get; set; }
    public DbSet<UserPreference> UserPreferences { get; set; }

    // Role-based access control
    public DbSet<Group> Groups { get; set; }
    public DbSet<Feature> Features { get; set; }
    public DbSet<FeatureUserAllow> FeatureUserAllows { get; set; }
    public DbSet<FeatureGroupAllow> FeatureGroupAllows { get; set; }
    public DbSet<UserGroup> UserGroups { get; set; }
    public DbSet<GroupFeaturePermission> GroupFeaturePermissions { get; set; }

    // API Management
    public DbSet<ApiToken> ApiTokens { get; set; }
    public DbSet<ApiTokenPermission> ApiTokenPermissions { get; set; }
    public DbSet<ApiTokenRequest> ApiTokenRequests { get; set; }
    public DbSet<ApiTokenMember> ApiTokenMembers { get; set; }
    public DbSet<ApiRequestLog> ApiRequestLogs { get; set; }

    // Operational reports
    public DbSet<PerformanceMetric> PerformanceMetrics { get; set; }

    // Functional standards
    public DbSet<FunctionalStandard> FunctionalStandards { get; set; }
    public DbSet<FunctionalStandardTheme> FunctionalStandardThemes { get; set; }
    public DbSet<PracticeArea> PracticeAreas { get; set; }
    public DbSet<Criterion> Criteria { get; set; }

    // Delivery reporting
    public DbSet<ProductReturn> ProductReturns { get; set; }
    public DbSet<ProductMetricValue> ProductMetricValues { get; set; }

    // Commission reporting
    public DbSet<Commission> Commissions { get; set; }
    public DbSet<CommissionSubmission> CommissionSubmissions { get; set; }
    public DbSet<CommissionMetricValue> CommissionMetricValues { get; set; }

    // Custom report library
    public DbSet<CustomReport> CustomReports { get; set; }
    public DbSet<CustomReportShare> CustomReportShares { get; set; }

    // Performance reporting management
    public DbSet<PerformanceReportingDueDateOverride> PerformanceReportingDueDateOverrides { get; set; }
    public DbSet<PerformanceReportingBusinessAreaConfig> PerformanceReportingBusinessAreaConfigs { get; set; }
    public DbSet<PerformanceReportingProductExclusion> PerformanceReportingProductExclusions { get; set; }
    public DbSet<PerformanceReportingPeriodExclusion> PerformanceReportingPeriodExclusions { get; set; }

    // KPI management
    public DbSet<Kpi> Kpis { get; set; }
    public DbSet<KpiDataPoint> KpiDataPoints { get; set; }

    // Enterprise reporting - Functional Standard Assessments
    public DbSet<FunctionalStandardAssessment> FunctionalStandardAssessments { get; set; }
    public DbSet<AssessmentCriteriaResponse> AssessmentCriteriaResponses { get; set; }

    // Organizational structure
    public DbSet<OrganizationalGroup> OrganizationalGroups { get; set; }
    public DbSet<OrganizationalRole> OrganizationalRoles { get; set; }
    public DbSet<GovernmentDepartment> GovernmentDepartments { get; set; }

    // Accessibility Management (Apps)
    public DbSet<ProductAccessibility> ProductAccessibilities { get; set; }
    public DbSet<ContactMethod> ContactMethods { get; set; }
    public DbSet<AuditHistory> AuditHistories { get; set; }
    public DbSet<AccessibilityIssue> AccessibilityIssues { get; set; }
    public DbSet<IssueComment> IssueComments { get; set; }
    public DbSet<IssueHistory> IssueHistories { get; set; }
    public DbSet<WcagCriterion> WcagCriteria { get; set; }
    public DbSet<IssueWcagCriterion> IssueWcagCriteria { get; set; }
    public DbSet<AccessibilityRetestRequest> AccessibilityRetestRequests { get; set; }
    public DbSet<StatementVerificationRequest> StatementVerificationRequests { get; set; }
    public DbSet<AccessibilityEmailConfiguration> AccessibilityEmailConfigurations { get; set; }
    public DbSet<StatementTemplate> StatementTemplates { get; set; }

    // Notifications
    public DbSet<NotificationTemplate> NotificationTemplates { get; set; }
    public DbSet<NotificationRule> NotificationRules { get; set; }
    public DbSet<NotificationLog> NotificationLogs { get; set; }

    // Enterprise reporting - Enterprise Metrics
    public DbSet<EnterpriseMetric> EnterpriseMetrics { get; set; }
    public DbSet<EnterpriseReturn> EnterpriseReturns { get; set; }
    public DbSet<EnterpriseMetricValue> EnterpriseMetricValues { get; set; }

    // Staff Role Return
    public DbSet<StaffRoleReturn> StaffRoleReturns { get; set; }
    public DbSet<StaffRoleReturnSkill> StaffRoleReturnSkills { get; set; }
    public DbSet<GddRole> GddRoles { get; set; }
    public DbSet<Skill> Skills { get; set; }

    // Learning & Development (L&D)
    public DbSet<TrainingCourse> TrainingCourses { get; set; }
    public DbSet<TrainingRecord> TrainingRecords { get; set; }
    public DbSet<TrainingRequest> TrainingRequests { get; set; }
    public DbSet<UserProfessionalProfile> UserProfessionalProfiles { get; set; }
    public DbSet<CapabilityGap> CapabilityGaps { get; set; }
    public DbSet<HOPS> HOPS { get; set; }
    public DbSet<TrainingNudge> TrainingNudges { get; set; }
    public DbSet<LearningBudget> LearningBudgets { get; set; }

    // DDAT Framework
    public DbSet<DdatFrameworkVersion> DdatFrameworkVersions { get; set; }
    public DbSet<DdatFrameworkSkill> DdatFrameworkSkills { get; set; }
    public DbSet<DdatFrameworkSkillGradeMapping> DdatFrameworkSkillGradeMappings { get; set; }
    public DbSet<DdatFrameworkRole> DdatFrameworkRoles { get; set; }
    public DbSet<DdatFrameworkRoleSkill> DdatFrameworkRoleSkills { get; set; }
    public DbSet<DdatFrameworkChangeNote> DdatFrameworkChangeNotes { get; set; }
    public DbSet<UserDdatFrameworkSkill> UserDdatFrameworkSkills { get; set; }
    public DbSet<Grade> Grades { get; set; }

    // Product Governance
    public DbSet<Objective> Objectives { get; set; }
    public DbSet<Risk> Risks { get; set; }
    public DbSet<Issue> Issues { get; set; }
    public DbSet<IssueAssuranceEvent> IssueAssuranceEvents { get; set; }
    public DbSet<Milestone> Milestones { get; set; }
    public DbSet<Models.Action> Actions { get; set; }
    public DbSet<Decision> Decisions { get; set; }
    public DbSet<Comment> Comments { get; set; }

    // Surveys (Apps)
    public DbSet<FipsService> Services { get; set; }
    public DbSet<SurveyTemplate> SurveyTemplates { get; set; }
    public DbSet<SurveyQuestion> SurveyQuestions { get; set; }
    public DbSet<SurveyOption> SurveyOptions { get; set; }
    public DbSet<ResponseScale> ResponseScales { get; set; }
    public DbSet<ResponseScaleOption> ResponseScaleOptions { get; set; }
    public DbSet<JourneyStep> JourneySteps { get; set; }
    public DbSet<SurveyInstance> SurveyInstances { get; set; }
    public DbSet<SurveyResponse> SurveyResponses { get; set; }
    public DbSet<ResponseAnswer> ResponseAnswers { get; set; }
    public DbSet<ScoreSnapshot> ScoreSnapshots { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }

    // Project Management
    public DbSet<Project> Projects { get; set; }
    public DbSet<Mission> Missions { get; set; }
    public DbSet<FundingSource> FundingSources { get; set; }
    public DbSet<ProjectRagHistory> ProjectRagHistories { get; set; }
    public DbSet<ProjectSuccess> ProjectSuccesses { get; set; }
    public DbSet<ProjectOutcome> ProjectOutcomes { get; set; }
    public DbSet<ProjectNeed> ProjectNeeds { get; set; }
    public DbSet<ProjectProblemStatement> ProjectProblemStatements { get; set; }
    public DbSet<ProjectProblemStatementHistory> ProjectProblemStatementHistories { get; set; }
    public DbSet<ProjectMission> ProjectMissions { get; set; }
    public DbSet<ProjectFundingAllocation> ProjectFundingAllocations { get; set; }
    public DbSet<ProjectResourceFunding> ProjectResourceFundings { get; set; }
    public DbSet<ProjectResourceFundingHistory> ProjectResourceFundingHistories { get; set; }
    public DbSet<ProjectContact> ProjectContacts { get; set; }
    public DbSet<ProjectObjective> ProjectObjectives { get; set; }
    public DbSet<Dependency> Dependencies { get; set; }
    public DbSet<Assumption> Assumptions { get; set; }
    public DbSet<NearMiss> NearMisses { get; set; }
    public DbSet<NearMissOwner> NearMissOwners { get; set; }
    public DbSet<NearMissAction> NearMissActions { get; set; }
    public DbSet<NearMissMitigation> NearMissMitigations { get; set; }
    public DbSet<ProjectProduct> ProjectProducts { get; set; }
    public DbSet<ProjectDraft> ProjectDrafts { get; set; }

    // RAID Lookups
    public DbSet<RiskType> RiskTypes { get; set; }
    public DbSet<RiskTier> RiskTiers { get; set; }
    public DbSet<ActionSource> ActionSources { get; set; }

    // Help Chatbot
    public DbSet<ChatConversation> ChatConversations { get; set; }

    // Project Lookups
    public DbSet<BusinessAreaLookup> BusinessAreaLookups { get; set; }
    public DbSet<PhaseLookup> PhaseLookups { get; set; }

    // Division and Business Area Management
    public DbSet<Division> Divisions { get; set; }
    public DbSet<DivisionBusinessArea> DivisionBusinessAreas { get; set; }
    public DbSet<BusinessAreaUser> BusinessAreaUsers { get; set; }
    public DbSet<DivisionUser> DivisionUsers { get; set; }
    public DbSet<DeliveryPriority> DeliveryPriorities { get; set; }
    public DbSet<KpiCategory> KpiCategories { get; set; }
    public DbSet<ActivityTypeLookup> ActivityTypeLookups { get; set; }
    public DbSet<WorkItemTagLookup> WorkItemTagLookups { get; set; }
    public DbSet<ProjectWorkItemTag> ProjectWorkItemTags { get; set; }
    public DbSet<DirectorateLookup> DirectorateLookups { get; set; }
    public DbSet<RiskAppetiteLookup> RiskAppetiteLookups { get; set; }
    public DbSet<RagStatusLookup> RagStatusLookups { get; set; }
    public DbSet<ResourceBandLookup> ResourceBandLookups { get; set; }

    // Project relationships
    public DbSet<ProjectStatusUpdate> ProjectStatusUpdates { get; set; }
    public DbSet<ProjectMonthlyUpdate> ProjectMonthlyUpdates { get; set; }
    public DbSet<MonthlyUpdateNarrative> MonthlyUpdateNarratives { get; set; }
    public DbSet<ProjectWeeklySuccessUpdate> ProjectWeeklySuccessUpdates { get; set; }
    public DbSet<MonthlyStatusReport> MonthlyStatusReports { get; set; }
    public DbSet<MonthlyStatusReportTimescaleConfig> MonthlyStatusReportTimescaleConfigs { get; set; }
    public DbSet<MonthlyUpdateDeadlineConfig> MonthlyUpdateDeadlineConfigs { get; set; }
    public DbSet<WorkReportingCycle> WorkReportingCycles { get; set; }
    public DbSet<WorkReportingCyclePeriod> WorkReportingCyclePeriods { get; set; }
    public DbSet<WeeklyWorkReportingConfig> WeeklyWorkReportingConfigs { get; set; }
    public DbSet<WeeklyWorkReportingScopeProject> WeeklyWorkReportingScopeProjects { get; set; }
    public DbSet<ProjectWeeklyWorkUpdate> ProjectWeeklyWorkUpdates { get; set; }
    public DbSet<ProjectSeniorResponsibleOfficer> ProjectSeniorResponsibleOfficers { get; set; }
    public DbSet<ProjectServiceOwner> ProjectServiceOwners { get; set; }
    public DbSet<ProjectDirectorate> ProjectDirectorates { get; set; }
    public DbSet<ProjectArtefact> ProjectArtefacts { get; set; }
    public DbSet<ProjectBudgetOwner> ProjectBudgetOwners { get; set; }
    public DbSet<ProjectPmoContact> ProjectPmoContacts { get; set; }
    public DbSet<ProjectWatchlist> ProjectWatchlists { get; set; }

    // Product DQ Reviews
    public DbSet<ProductDqReview> ProductDqReviews { get; set; }

    // RAID Junction Tables
    public DbSet<RiskAction> RiskActions { get; set; }
    public DbSet<RiskRiskType> RiskRiskTypes { get; set; }
    public DbSet<IssueAction> IssueActions { get; set; }
    public DbSet<RiskDecision> RiskDecisions { get; set; }
    public DbSet<IssueDecision> IssueDecisions { get; set; }
    public DbSet<IssueRisk> IssueRisks { get; set; }
    public DbSet<RiskKeyRiskIndicator> RiskKeyRiskIndicators { get; set; }
    public DbSet<RiskRiskCategory> RiskRiskCategories { get; set; }
    public DbSet<IssueIssueCategory> IssueIssueCategories { get; set; }
    public DbSet<RiskDivision> RiskDivisions { get; set; }
    public DbSet<RiskBusinessArea> RiskBusinessAreas { get; set; }
    public DbSet<IssueDivision> IssueDivisions { get; set; }
    public DbSet<IssueBusinessArea> IssueBusinessAreas { get; set; }
    public DbSet<AssumptionDivision> AssumptionDivisions { get; set; }
    public DbSet<AssumptionBusinessArea> AssumptionBusinessAreas { get; set; }
    public DbSet<RaidEscalationTierChangeRequest> RaidEscalationTierChangeRequests { get; set; }
    public DbSet<Compass.Models.Raid.RaidMonthlyReview> RaidMonthlyReviews { get; set; }

    // RAID Registers
    public DbSet<RaidRegister> RaidRegisters { get; set; }
    public DbSet<RaidRegisterUser> RaidRegisterUsers { get; set; }
    public DbSet<RaidRegisterWorkItem> RaidRegisterWorkItems { get; set; }
    public DbSet<RaidRegisterService> RaidRegisterServices { get; set; }
    public DbSet<RaidRegisterDirectorate> RaidRegisterDirectorates { get; set; }
    public DbSet<RaidRegisterBusinessArea> RaidRegisterBusinessAreas { get; set; }
    public DbSet<RaidRegisterRisk> RaidRegisterRisks { get; set; }
    public DbSet<RaidRegisterIssue> RaidRegisterIssues { get; set; }
    public DbSet<RaidRegisterAssumption> RaidRegisterAssumptions { get; set; }
    public DbSet<RaidRegisterDependency> RaidRegisterDependencies { get; set; }
    public DbSet<RaidRegisterNearMiss> RaidRegisterNearMisses { get; set; }
    public DbSet<RaidRegisterSpreadsheetLayout> RaidRegisterSpreadsheetLayouts { get; set; }

    /// <summary>CMS access requests (Design histories, DDT manual, etc.) for Operations.</summary>
    public DbSet<CmsAccessRequest> CmsAccessRequests { get; set; }

    /// <summary>Allowed CMS product names and sign-in URLs for the CMS access request API (Admin-managed).</summary>
    public DbSet<CmsAccessRequestProduct> CmsAccessRequestProducts { get; set; }

    public DbSet<ActionDecision> ActionDecisions { get; set; }
    public DbSet<MilestoneAction> MilestoneActions { get; set; }
    public DbSet<MilestoneRisk> MilestoneRisks { get; set; }
    public DbSet<MilestoneIssue> MilestoneIssues { get; set; }
    public DbSet<MilestoneUpdate> MilestoneUpdates { get; set; }

    // Demand Triage (spec-aligned v3)
    public DbSet<DemandTriageRequest> DemandTriageRequests { get; set; }
    public DbSet<DemandExploratoryReview> DemandExploratoryReviews { get; set; }
    public DbSet<DemandScorecard> DemandScorecards { get; set; }
    public DbSet<DemandAnswer> DemandAnswers { get; set; }
    public DbSet<DemandTriageOutcome> DemandTriageOutcomes { get; set; }
    public DbSet<DemandTriageAuditEvent> DemandTriageAuditEvents { get; set; }

    // Business Cases
    public DbSet<BusinessCase> BusinessCases { get; set; }
    public DbSet<BusinessCaseDdtFeedback> BusinessCaseDdtFeedbacks { get; set; }
    public DbSet<BusinessCaseReviewer> BusinessCaseReviewers { get; set; }
    public DbSet<BusinessCaseProject> BusinessCaseProjects { get; set; }
    public DbSet<BusinessCaseProduct> BusinessCaseProducts { get; set; }
    public DbSet<BusinessCaseStatusLookup> BusinessCaseStatusLookups { get; set; }

    // Demand pipeline (Compass2 lifecycle — separate from legacy BusinessCases / demand triage)
    public DbSet<DemandPipelineBusinessCase> DemandPipelineBusinessCases { get; set; }
    public DbSet<DemandPipelineRequest> DemandPipelineRequests { get; set; }
    public DbSet<UniversalBarrierLookup> UniversalBarrierLookups { get; set; }
    public DbSet<DemandPipelineRequestUniversalBarrier> DemandPipelineRequestUniversalBarriers { get; set; }
    public DbSet<DemandPipelineRiskIssue> DemandPipelineRiskIssues { get; set; }
    public DbSet<DemandPipelineStage> DemandPipelineStages { get; set; }
    public DbSet<DemandPipelineTriageMeeting> DemandPipelineTriageMeetings { get; set; }
    public DbSet<DemandScoringFrameworkSection> DemandScoringFrameworkSections { get; set; }
    public DbSet<DemandScoringFrameworkQuestion> DemandScoringFrameworkQuestions { get; set; }
    public DbSet<DemandScoringFrameworkOption> DemandScoringFrameworkOptions { get; set; }
    public DbSet<DemandScoringBandDefinition> DemandScoringBandDefinitions { get; set; }

    // DDT Standards Management
    public DbSet<DdtStandard> DdtStandards { get; set; }
    public DbSet<DdtStandardOwner> DdtStandardOwners { get; set; }
    public DbSet<DdtStandardContact> DdtStandardContacts { get; set; }
    public DbSet<DdtStandardPhase> DdtStandardPhases { get; set; }
    public DbSet<DdtStandardValidationRule> DdtStandardValidationRules { get; set; }
    public DbSet<DdtStandardVersion> DdtStandardVersions { get; set; }
    public DbSet<DdtStandardCategory> DdtStandardCategories { get; set; }
    public DbSet<DdtStandardSubCategory> DdtStandardSubCategories { get; set; }
    public DbSet<DdtStandardComment> DdtStandardComments { get; set; }
    public DbSet<DdtStandardProduct> DdtStandardProducts { get; set; }
    public DbSet<DdtStandardException> DdtStandardExceptions { get; set; }
    public DbSet<DdtStandardUnpublishAudit> DdtStandardUnpublishAudits { get; set; }

    // Standards Configuration
    public DbSet<StandardCategory> StandardCategories { get; set; }
    public DbSet<StandardSubCategory> StandardSubCategories { get; set; }
    public DbSet<StandardProduct> StandardProducts { get; set; }

    // Service Standards (GOV.UK Service Standards)
    public DbSet<ServiceStandard> ServiceStandards { get; set; }
    public DbSet<ServiceStandardPhaseGuidance> ServiceStandardPhaseGuidance { get; set; }
    public DbSet<DdatProfession> DdatProfessions { get; set; }
    public DbSet<ServiceStandardProfession> ServiceStandardProfessions { get; set; }

    // Technology Code of Practice
    public DbSet<TechnologyCodeOfPractice> TechnologyCodeOfPractice { get; set; }
    public DbSet<TechnologyCodeOfPracticeProfession> TechnologyCodeOfPracticeProfessions { get; set; }
    public DbSet<TechnologyCodeOfPracticePhaseGuidance> TechnologyCodeOfPracticePhaseGuidance { get; set; }

    // Profession and Skills Management
    public DbSet<ProfessionSkill> ProfessionSkills { get; set; }
    public DbSet<UserProfessionalProfileSkill> UserProfessionalProfileSkills { get; set; }

    // FIPS Sync Management
    public DbSet<FipsSyncHistory> FipsSyncHistories { get; set; }

    // FIPS CMDB products
    public DbSet<CMDBProduct> CMDBProducts { get; set; }
    public DbSet<CMDBProductBusinessArea> CMDBProductBusinessAreas { get; set; }
    public DbSet<CMDBProductDirectorate> CMDBProductDirectorates { get; set; }
    public DbSet<CMDBProductChannel> CMDBProductChannels { get; set; }
    public DbSet<CMDBProductUserGroup> CMDBProductUserGroups { get; set; }
    public DbSet<CMDBProductType> CMDBProductTypes { get; set; }
    public DbSet<CMDBProductFipsCategorisationItem> CMDBProductFipsCategorisationItems { get; set; }
    public DbSet<CMDBProductContact> CMDBProductContacts { get; set; }
    public DbSet<CMDBProductObjective> CMDBProductObjectives { get; set; }
    public DbSet<CMDBProductMission> CMDBProductMissions { get; set; }
    public DbSet<CMDBProductWorkItemTag> CMDBProductWorkItemTags { get; set; }
    public DbSet<FipsCmdbSyncRule> FipsCmdbSyncRules { get; set; }

    // FIPS configuration
    public DbSet<FipsBusinessArea> FipsBusinessAreas { get; set; }
    public DbSet<FipsDirectorate> FipsDirectorates { get; set; }
    public DbSet<FipsChannel> FipsChannels { get; set; }
    public DbSet<FipsType> FipsTypes { get; set; }
    public DbSet<FipsUserGroup> FipsUserGroups { get; set; }
    public DbSet<FipsUserGroupSynonym> FipsUserGroupSynonyms { get; set; }
    public DbSet<FipsContactRole> FipsContactRoles { get; set; }
    public DbSet<FipsCategorisationGroup> FipsCategorisationGroups { get; set; }
    public DbSet<FipsCategorisationItem> FipsCategorisationItems { get; set; }

    // Service lines (FIPS / service register)
    public DbSet<ServiceLine> ServiceLines { get; set; }
    public DbSet<ServiceLineDivision> ServiceLineDivisions { get; set; }
    public DbSet<ServiceLineBusinessArea> ServiceLineBusinessAreas { get; set; }
    public DbSet<ServiceLineProduct> ServiceLineProducts { get; set; }
    public DbSet<ServiceLineProject> ServiceLineProjects { get; set; }

    // Design Decision Records (DDR). Tables share the `ddr_` prefix per ddr.md §2.
    public DbSet<Compass.Models.Ddr.DesignDecisionRecord> DesignDecisionRecords { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrAlternative> DdrAlternatives { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrEvidence> DdrEvidences { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrProductLink> DdrProductLinks { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrWorkItemLink> DdrWorkItemLinks { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrStandardLink> DdrStandardLinks { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrComponentPatternLink> DdrComponentPatternLinks { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrRelatedRecord> DdrRelatedRecords { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrComment> DdrComments { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrInsightClassification> DdrInsightClassifications { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrRecommendedFollowUp> DdrRecommendedFollowUps { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrGitHubIssueLink> DdrGitHubIssueLinks { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrAuditEvent> DdrAuditEvents { get; set; } = default!;
    public DbSet<Compass.Models.Ddr.DdrFeatureSetting> DdrFeatureSettings { get; set; } = default!;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (_suppressAuditLogging)
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        var auditEntries = PrepareAuditEntries();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        RefreshAuditEntityIds(auditEntries);
        AppendAuditEntries(auditEntries, acceptAllChangesOnSuccess);
        return result;
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        if (_suppressAuditLogging)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        var auditEntries = PrepareAuditEntries();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        RefreshAuditEntityIds(auditEntries);
        await AppendAuditEntriesAsync(auditEntries, acceptAllChangesOnSuccess, cancellationToken);
        return result;
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => SaveChangesAsync(true, cancellationToken);

    private sealed class PendingAuditEntry
    {
        public required AuditLog Audit { get; init; }
        public required EntityEntry Entry { get; init; }
    }

    private List<PendingAuditEntry> PrepareAuditEntries()
    {
        // Do not call ChangeTracker.DetectChanges() here — SaveChanges will detect changes once.
        // A full-graph DetectChanges is expensive when many entities are tracked (e.g. FIPS product Includes).
        var auditEntries = new List<PendingAuditEntry>();
        var timestamp = DateTime.UtcNow;
        var currentUserId = _auditContextProvider.UserId;
        var currentUserName = _auditContextProvider.UserName;
        var currentUserEmail = _auditContextProvider.UserEmail;
        var ipAddress = _auditContextProvider.IpAddress;
        var userAgent = _auditContextProvider.UserAgent;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is AuditLog)
            {
                continue;
            }

            if (entry.State is EntityState.Detached or EntityState.Unchanged)
            {
                continue;
            }

            var entityName = entry.Metadata.ClrType.Name;
            var primaryKey = GetPrimaryKey(entry);
            var audit = new AuditLog
            {
                Entity = entityName,
                EntityId = primaryKey,
                EntityReference = entry.Entity.ToString(),
                Action = entry.State switch
                {
                    EntityState.Added => "Create",
                    EntityState.Modified => "Update",
                    EntityState.Deleted => "Delete",
                    _ => entry.State.ToString()
                },
                ChangedUtc = timestamp,
                ChangedBy = currentUserName,
                ChangedByUserId = currentUserId,
                ChangedByEmail = currentUserEmail,
                IpAddress = ipAddress,
                UserAgent = userAgent
            };

            switch (entry.State)
            {
                case EntityState.Added:
                    audit.AfterJson = SerializePropertyValues(entry.Properties, includeOriginalValues: false);
                    break;
                case EntityState.Deleted:
                    audit.BeforeJson = SerializePropertyValues(entry.Properties, includeOriginalValues: true);
                    break;
                case EntityState.Modified:
                    audit.BeforeJson = SerializeChangedValues(entry.Properties, useOriginalValues: true);
                    audit.AfterJson = SerializeChangedValues(entry.Properties, useOriginalValues: false);
                    break;
            }

            auditEntries.Add(new PendingAuditEntry { Audit = audit, Entry = entry });
        }

        return auditEntries;
    }

    private void RefreshAuditEntityIds(IEnumerable<PendingAuditEntry> auditEntries)
    {
        foreach (var pending in auditEntries)
        {
            if (pending.Entry.State == EntityState.Detached)
            {
                continue;
            }

            var entityId = GetPrimaryKey(pending.Entry);
            if (!string.IsNullOrWhiteSpace(entityId))
            {
                pending.Audit.EntityId = entityId;
            }
        }
    }

    private string SerializePropertyValues(IEnumerable<PropertyEntry> properties, bool includeOriginalValues)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var property in properties)
        {
            if (property.Metadata.IsPrimaryKey())
            {
                continue;
            }

            dictionary[property.Metadata.Name] = includeOriginalValues
                ? property.OriginalValue
                : property.CurrentValue;
        }

        return JsonSerializer.Serialize(dictionary);
    }

    private string SerializeChangedValues(IEnumerable<PropertyEntry> properties, bool useOriginalValues)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var property in properties)
        {
            if (!property.IsModified)
            {
                continue;
            }

            if (property.Metadata.IsPrimaryKey())
            {
                continue;
            }

            dictionary[property.Metadata.Name] = useOriginalValues
                ? property.OriginalValue
                : property.CurrentValue;
        }

        return JsonSerializer.Serialize(dictionary);
    }

    private string GetPrimaryKey(EntityEntry entry)
    {
        var key = entry.Properties
            .Where(p => p.Metadata.IsPrimaryKey())
            .Select(p => p.CurrentValue?.ToString() ?? p.OriginalValue?.ToString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return key ?? string.Empty;
    }

    private bool EnsureAuditColumnsAvailable()
    {
        if (_auditSchemaChecked)
        {
            return _auditColumnsAvailable;
        }

        var connection = Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;

        try
        {
            if (!wasOpen)
            {
                connection.Open();
            }

            using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM INFORMATION_SCHEMA.COLUMNS 
                WHERE TABLE_NAME = 'AuditLogs' 
                  AND COLUMN_NAME IN ('AfterJson','BeforeJson','ChangedByEmail','ChangedByUserId','EntityReference','IpAddress','UserAgent')";

            var count = Convert.ToInt32(command.ExecuteScalar() ?? 0);
            _auditColumnsAvailable = count >= 7;
        }
        catch
        {
            _auditColumnsAvailable = false;
        }
        finally
        {
            _auditSchemaChecked = true;
            if (!wasOpen && connection.State == ConnectionState.Open)
            {
                connection.Close();
            }
        }

        return _auditColumnsAvailable;
    }

    private static bool IsMissingAuditColumnException(Exception? exception)
    {
        if (exception == null)
        {
            return false;
        }

        if (exception is SqlException sqlException && sqlException.Number == 207)
        {
            return true;
        }

        return IsMissingAuditColumnException(exception.InnerException);
    }

    private void AppendAuditEntries(IEnumerable<PendingAuditEntry> auditEntries, bool acceptAllChangesOnSuccess)
    {
        var entries = auditEntries.Select(p => p.Audit).ToList();
        if (!entries.Any())
        {
            return;
        }

        if (!EnsureAuditColumnsAvailable())
        {
            return;
        }

        try
        {
            _suppressAuditLogging = true;
            AuditLogs.AddRange(entries);
            base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (Exception ex) when (IsMissingAuditColumnException(ex))
        {
            _auditColumnsAvailable = false;
        }
        finally
        {
            _suppressAuditLogging = false;
        }
    }

    private async Task AppendAuditEntriesAsync(IEnumerable<PendingAuditEntry> auditEntries, bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        var entries = auditEntries.Select(p => p.Audit).ToList();
        if (!entries.Any())
        {
            return;
        }

        if (!EnsureAuditColumnsAvailable())
        {
            return;
        }

        try
        {
            _suppressAuditLogging = true;
            AuditLogs.AddRange(entries);
            await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (Exception ex) when (IsMissingAuditColumnException(ex))
        {
            _auditColumnsAvailable = false;
        }
        finally
        {
            _suppressAuditLogging = false;
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();
        modelBuilder.Entity<UserBusinessAreaRoleAssignment>()
            .HasIndex(a => new { a.UserId, a.BusinessAreaKey, a.Role })
            .IsUnique();

        modelBuilder.Entity<UserBusinessAreaRoleAssignment>()
            .HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BusinessAreaAdminMember>()
            .HasIndex(m => new { m.UserId, m.BusinessAreaLookupId })
            .IsUnique();
        modelBuilder.Entity<BusinessAreaAdminMember>()
            .HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BusinessAreaAdminMember>()
            .HasOne(m => m.BusinessAreaLookup)
            .WithMany()
            .HasForeignKey(m => m.BusinessAreaLookupId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BusinessAreaLeadershipMember>()
            .HasIndex(m => new { m.UserId, m.BusinessAreaLookupId })
            .IsUnique();
        modelBuilder.Entity<BusinessAreaLeadershipMember>()
            .HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BusinessAreaLeadershipMember>()
            .HasOne(m => m.BusinessAreaLookup)
            .WithMany()
            .HasForeignKey(m => m.BusinessAreaLookupId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Compass.Models.Raid.RaidMonthlyReview>()
            .HasIndex(x => new { x.RecordType, x.RecordId, x.ReviewYear, x.ReviewMonth })
            .IsUnique();
        modelBuilder.Entity<Compass.Models.Raid.RaidMonthlyReview>()
            .HasOne(x => x.ReviewedByUser)
            .WithMany()
            .HasForeignKey(x => x.ReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DivisionUser>()
            .HasIndex(m => new { m.UserId, m.DivisionId })
            .IsUnique();

        modelBuilder.Entity<DivisionBusinessArea>()
            .HasIndex(m => new { m.DivisionId, m.BusinessAreaLookupId })
            .IsUnique();

        modelBuilder.Entity<CompassNotificationSetting>()
            .HasIndex(x => x.EventKey)
            .IsUnique();

        modelBuilder.Entity<CompassNotificationEmailLog>()
            .HasIndex(x => x.SentAtUtc);

        modelBuilder.Entity<CompassNotificationEmailLog>()
            .HasIndex(x => x.EventKey);

        modelBuilder.Entity<User>()
            .HasIndex(u => u.AzureObjectId)
            .IsUnique()
            .HasFilter("[AzureObjectId] IS NOT NULL");

        // Surveys configuration
        modelBuilder.Entity<FipsService>()
            .HasIndex(s => s.FipsId)
            .IsUnique();

        modelBuilder.Entity<FipsService>()
            .HasMany(s => s.SurveyInstances)
            .WithOne(si => si.Service)
            .HasForeignKey(si => si.ServiceId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SurveyTemplate>()
            .HasIndex(t => new { t.Name, t.Version })
            .IsUnique();

        modelBuilder.Entity<SurveyQuestion>()
            .HasOne(q => q.Template)
            .WithMany(t => t.Questions)
            .HasForeignKey(q => q.SurveyTemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SurveyQuestion>()
            .HasIndex(q => new { q.SurveyTemplateId, q.Code })
            .IsUnique();

        modelBuilder.Entity<SurveyOption>()
            .HasOne(o => o.Question)
            .WithMany(q => q.Options)
            .HasForeignKey(o => o.SurveyQuestionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ResponseScaleOption>()
            .HasOne(o => o.Scale)
            .WithMany(s => s.Options)
            .HasForeignKey(o => o.ResponseScaleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<JourneyStep>()
            .HasOne(js => js.Template)
            .WithMany(t => t.JourneySteps)
            .HasForeignKey(js => js.SurveyTemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<JourneyStep>()
            .HasIndex(js => new { js.SurveyTemplateId, js.Ordinal })
            .IsUnique();

        modelBuilder.Entity<SurveyInstance>()
            .HasOne(si => si.Template)
            .WithMany()
            .HasForeignKey(si => si.SurveyTemplateId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SurveyInstance>()
            .HasIndex(si => new { si.ServiceId, si.IsActive })
            .HasFilter("[IsActive] = 1");

        modelBuilder.Entity<SurveyResponse>()
            .HasOne(r => r.SurveyInstance)
            .WithMany(si => si.Responses)
            .HasForeignKey(r => r.SurveyInstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ResponseAnswer>()
            .HasOne(ra => ra.SurveyResponse)
            .WithMany(r => r.Answers)
            .HasForeignKey(ra => ra.SurveyResponseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ResponseAnswer>()
            .HasOne(ra => ra.SurveyQuestion)
            .WithMany()
            .HasForeignKey(ra => ra.SurveyQuestionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Configure User entity
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<User>()
            .HasIndex(u => u.AzureObjectId)
            .IsUnique()
            .HasFilter("[AzureObjectId] IS NOT NULL");

        modelBuilder.Entity<UserPreference>()
            .Property(p => p.DashboardLayout)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<CmsAccessRequest>()
            .Property(x => x.SignInPageUrl)
            .HasColumnType("nvarchar(max)");
        modelBuilder.Entity<CmsAccessRequest>()
            .Property(x => x.Comments)
            .HasColumnType("nvarchar(max)");
        modelBuilder.Entity<CmsAccessRequest>()
            .Property(x => x.RegistrationToken)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<CmsAccessRequestProduct>()
            .HasIndex(x => x.Name)
            .IsUnique();

        modelBuilder.Entity<CmsAccessRequestProduct>()
            .Property(x => x.SignInPageUrl)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<AuditLog>()
            .Property(a => a.AfterJson)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<AuditLog>()
            .Property(a => a.BeforeJson)
            .HasColumnType("nvarchar(max)");

        // Configure PerformanceMetric entity
        modelBuilder.Entity<PerformanceMetric>()
            .HasIndex(pm => pm.Identifier)
            .IsUnique();

        // Configure FunctionalStandard relationships
        modelBuilder.Entity<FunctionalStandard>()
            .Property(fs => fs.Id)
            .ValueGeneratedNever(); // User-defined ID

        modelBuilder.Entity<FunctionalStandardTheme>()
            .HasOne(t => t.FunctionalStandard)
            .WithMany(fs => fs.Themes)
            .HasForeignKey(t => t.FunctionalStandardId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PracticeArea>()
            .HasOne(pa => pa.Theme)
            .WithMany(t => t.PracticeAreas)
            .HasForeignKey(pa => new { pa.FunctionalStandardId, pa.ThemeId })
            .HasPrincipalKey(t => new { t.FunctionalStandardId, t.ThemeId })
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<FunctionalStandardTheme>()
            .HasIndex(t => new { t.FunctionalStandardId, t.ThemeId })
            .IsUnique();

        modelBuilder.Entity<Criterion>()
            .HasOne(c => c.PracticeArea)
            .WithMany(pa => pa.Criteria)
            .HasForeignKey(c => new { c.FunctionalStandardId, c.ThemeId, c.PracticeAreaId })
            .HasPrincipalKey(pa => new { pa.FunctionalStandardId, pa.ThemeId, pa.PracticeAreaId })
            .OnDelete(DeleteBehavior.Cascade);

        // Configure ProductReturn
        // Note: Index is non-unique initially to allow NULLs during migration
        // Will be made unique after data migration populates DocumentIds
        modelBuilder.Entity<ProductReturn>()
            .HasIndex(pr => new { pr.ProductDocumentId, pr.Year, pr.Month });

        modelBuilder.Entity<ProductReturn>()
            .HasIndex(pr => pr.FipsId);

        modelBuilder.Entity<ProductReturn>()
            .HasMany(pr => pr.MetricValues)
            .WithOne(mv => mv.ProductReturn)
            .HasForeignKey(mv => mv.ProductReturnId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure ProductMetricValue
        modelBuilder.Entity<ProductMetricValue>()
            .HasOne(mv => mv.PerformanceMetric)
            .WithMany()
            .HasForeignKey(mv => mv.PerformanceMetricId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ProductMetricValue>()
            .HasIndex(mv => new { mv.ProductReturnId, mv.PerformanceMetricId })
            .IsUnique();

        // Configure Commission
        modelBuilder.Entity<Commission>()
            .HasIndex(c => c.IsActive);

        modelBuilder.Entity<Commission>()
            .HasMany(c => c.Submissions)
            .WithOne(cs => cs.Commission)
            .HasForeignKey(cs => cs.CommissionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure CommissionSubmission
        modelBuilder.Entity<CommissionSubmission>()
            .HasIndex(cs => new { cs.CommissionId, cs.ProductDocumentId });

        modelBuilder.Entity<CommissionSubmission>()
            .HasIndex(cs => cs.FipsId);

        modelBuilder.Entity<CommissionSubmission>()
            .HasMany(cs => cs.MetricValues)
            .WithOne(cmv => cmv.CommissionSubmission)
            .HasForeignKey(cmv => cmv.CommissionSubmissionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure CommissionMetricValue
        modelBuilder.Entity<CommissionMetricValue>()
            .HasOne(cmv => cmv.PerformanceMetric)
            .WithMany()
            .HasForeignKey(cmv => cmv.PerformanceMetricId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CommissionMetricValue>()
            .HasIndex(cmv => new { cmv.CommissionSubmissionId, cmv.PerformanceMetricId })
            .IsUnique();

        modelBuilder.Entity<CustomReport>()
            .Property(r => r.Name)
            .HasMaxLength(200);
        modelBuilder.Entity<CustomReport>()
            .Property(r => r.Description)
            .HasMaxLength(2000);
        modelBuilder.Entity<CustomReport>()
            .Property(r => r.DefaultFilterJson)
            .HasMaxLength(8000);
        modelBuilder.Entity<CustomReport>()
            .Property(r => r.DefinitionJson)
            .HasMaxLength(32000);
        modelBuilder.Entity<CustomReport>()
            .Property(r => r.DataSource)
            .HasConversion<int>();
        modelBuilder.Entity<CustomReport>()
            .Property(r => r.Visibility)
            .HasConversion<int>();
        modelBuilder.Entity<CustomReport>()
            .HasOne(r => r.Owner)
            .WithMany()
            .HasForeignKey(r => r.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CustomReport>()
            .HasIndex(r => r.OwnerUserId);
        modelBuilder.Entity<CustomReport>()
            .HasIndex(r => r.Visibility);
        modelBuilder.Entity<CustomReport>()
            .HasMany(r => r.Shares)
            .WithOne(s => s.CustomReport)
            .HasForeignKey(s => s.CustomReportId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CustomReportShare>()
            .HasIndex(s => new { s.CustomReportId, s.UserId })
            .IsUnique();
        modelBuilder.Entity<CustomReportShare>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure PerformanceReportingDueDateOverride
        modelBuilder.Entity<PerformanceReportingDueDateOverride>()
            .HasIndex(prdo => new { prdo.ReportingYear, prdo.ReportingMonth })
            .IsUnique();

        modelBuilder.Entity<PerformanceReportingDueDateOverride>()
            .HasIndex(prdo => prdo.IsActive);

        // Configure PerformanceReportingBusinessAreaConfig
        modelBuilder.Entity<PerformanceReportingBusinessAreaConfig>()
            .HasIndex(prbac => prbac.BusinessAreaName);

        modelBuilder.Entity<PerformanceReportingBusinessAreaConfig>()
            .HasIndex(prbac => prbac.IsActive);

        // Configure PerformanceReportingProductExclusion
        // Note: Index is non-unique initially to allow NULLs during migration
        // Will be made unique after data migration populates DocumentIds
        modelBuilder.Entity<PerformanceReportingProductExclusion>()
            .HasIndex(prpe => prpe.ProductDocumentId);

        modelBuilder.Entity<PerformanceReportingProductExclusion>()
            .HasIndex(prpe => prpe.FipsId);

        modelBuilder.Entity<PerformanceReportingProductExclusion>()
            .HasIndex(prpe => prpe.IsActive);

        modelBuilder.Entity<PerformanceReportingProductExclusion>()
            .HasIndex(prpe => new { prpe.ProductDocumentId, prpe.IsActive });

        // Configure PerformanceReportingPeriodExclusion
        modelBuilder.Entity<PerformanceReportingPeriodExclusion>()
            .HasIndex(prpe => new { prpe.Year, prpe.Month })
            .IsUnique();

        modelBuilder.Entity<PerformanceReportingPeriodExclusion>()
            .HasIndex(prpe => prpe.IsActive);

        // Configure FunctionalStandardAssessment
        modelBuilder.Entity<FunctionalStandardAssessment>()
            .HasOne(fsa => fsa.FunctionalStandard)
            .WithMany()
            .HasForeignKey(fsa => fsa.FunctionalStandardId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<FunctionalStandardAssessment>()
            .HasMany(fsa => fsa.CriteriaResponses)
            .WithOne(acr => acr.Assessment)
            .HasForeignKey(acr => acr.AssessmentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure AssessmentCriteriaResponse
        modelBuilder.Entity<AssessmentCriteriaResponse>()
            .HasOne(acr => acr.Criterion)
            .WithMany()
            .HasForeignKey(acr => new { acr.FunctionalStandardId, acr.ThemeId, acr.PracticeAreaId, acr.CriteriaCode })
            .HasPrincipalKey(c => new { c.FunctionalStandardId, c.ThemeId, c.PracticeAreaId, c.CriteriaCode })
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssessmentCriteriaResponse>()
            .HasIndex(acr => new { acr.AssessmentId, acr.FunctionalStandardId, acr.ThemeId, acr.PracticeAreaId, acr.CriteriaCode })
            .IsUnique();

        // Configure EnterpriseMetric
        modelBuilder.Entity<EnterpriseMetric>()
            .HasIndex(em => em.Identifier)
            .IsUnique();

        // Configure EnterpriseReturn
        modelBuilder.Entity<EnterpriseReturn>()
            .HasIndex(er => new { er.Year, er.Month })
            .IsUnique();

        modelBuilder.Entity<EnterpriseReturn>()
            .HasMany(er => er.MetricValues)
            .WithOne(emv => emv.EnterpriseReturn)
            .HasForeignKey(emv => emv.EnterpriseReturnId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure EnterpriseMetricValue
        modelBuilder.Entity<EnterpriseMetricValue>()
            .HasOne(emv => emv.EnterpriseMetric)
            .WithMany()
            .HasForeignKey(emv => emv.EnterpriseMetricId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EnterpriseMetricValue>()
            .HasIndex(emv => new { emv.EnterpriseReturnId, emv.EnterpriseMetricId })
            .IsUnique();

        // Configure RAID entities

        // Objective indexes
        modelBuilder.Entity<Objective>()
            .HasIndex(o => o.Status);

        modelBuilder.Entity<Objective>()
            .HasIndex(o => o.RagStatus);

        modelBuilder.Entity<Objective>()
            .HasIndex(o => o.OwnerUserId);

        // Risk configuration
        modelBuilder.Entity<Risk>()
            .HasOne(r => r.Objective)
            .WithMany(o => o.Risks)
            .HasForeignKey(r => r.ObjectiveId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Risk>()
            .HasOne(r => r.RiskTier)
            .WithMany()
            .HasForeignKey(r => r.RiskTierId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Risk>()
            .HasIndex(r => r.ObjectiveId);

        modelBuilder.Entity<Risk>()
            .HasIndex(r => r.RiskTierId);

        modelBuilder.Entity<Risk>()
            .HasIndex(r => r.FipsId);

        modelBuilder.Entity<Risk>()
            .HasIndex(r => r.ProductDocumentId);

        modelBuilder.Entity<Risk>()
            .HasIndex(r => r.Status);

        modelBuilder.Entity<Risk>()
            .HasIndex(r => r.RiskScore)
            .IsDescending();

        modelBuilder.Entity<Risk>()
            .HasIndex(r => r.ProximityDate);

        // Risk: multiple FK columns pointing at the same lookup tables need explicit config
        modelBuilder.Entity<Risk>()
            .HasOne(r => r.CurrentLikelihood)
            .WithMany()
            .HasForeignKey(r => r.CurrentLikelihoodId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Risk>()
            .HasOne(r => r.CurrentImpactLevel)
            .WithMany()
            .HasForeignKey(r => r.CurrentImpactLevelId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Risk>()
            .HasOne(r => r.ResidualLikelihoodLevel)
            .WithMany()
            .HasForeignKey(r => r.ResidualLikelihoodId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Risk>()
            .HasOne(r => r.ResidualImpactLevel)
            .WithMany()
            .HasForeignKey(r => r.ResidualImpactLevelId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Risk>()
            .HasOne(r => r.ToleranceLikelihood)
            .WithMany()
            .HasForeignKey(r => r.ToleranceLikelihoodId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Risk>()
            .HasOne(r => r.ToleranceImpactLevel)
            .WithMany()
            .HasForeignKey(r => r.ToleranceImpactLevelId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<RiskRatingHistory>(e =>
        {
            e.HasOne(h => h.Risk)
                .WithMany(r => r.RatingHistory)
                .HasForeignKey(h => h.RiskId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(h => h.Likelihood)
                .WithMany()
                .HasForeignKey(h => h.LikelihoodId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(h => h.ImpactLevel)
                .WithMany()
                .HasForeignKey(h => h.ImpactLevelId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(h => h.ChangedByUser)
                .WithMany()
                .HasForeignKey(h => h.ChangedByUserId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(h => h.RiskId);
            e.HasIndex(h => new { h.RiskId, h.RatingType });
            e.HasIndex(h => h.ChangedAt);
        });

        modelBuilder.Entity<RiskKeyRiskIndicator>(e =>
        {
            e.HasOne(x => x.Risk)
                .WithMany(r => r.KeyRiskIndicators)
                .HasForeignKey(x => x.RiskId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.RiskId);
            e.HasIndex(x => new { x.RiskId, x.SortOrder });
            ConfigureRaidNarrativeColumns(e.Property(x => x.Description));
            ConfigureRaidNarrativeColumns(e.Property(x => x.Metric));
            ConfigureRaidNarrativeColumns(e.Property(x => x.Threshold));
        });

        ConfigureRaidRiskNarrativeColumns(modelBuilder.Entity<Risk>());

        // Issue configuration
        modelBuilder.Entity<Issue>()
            .HasOne(i => i.Objective)
            .WithMany(o => o.Issues)
            .HasForeignKey(i => i.ObjectiveId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Issue>()
            .HasIndex(i => i.ObjectiveId);

        modelBuilder.Entity<Issue>()
            .HasIndex(i => i.FipsId);

        modelBuilder.Entity<RiskRiskCategory>(e =>
        {
            e.HasKey(x => new { x.RiskId, x.RiskCategoryId });
            e.HasOne(x => x.Risk)
                .WithMany(r => r.RiskRiskCategories)
                .HasForeignKey(x => x.RiskId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.RiskCategory)
                .WithMany()
                .HasForeignKey(x => x.RiskCategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IssueIssueCategory>(e =>
        {
            e.HasKey(x => new { x.IssueId, x.IssueCategoryId });
            e.HasOne(x => x.Issue)
                .WithMany(i => i.IssueIssueCategories)
                .HasForeignKey(x => x.IssueId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.IssueCategory)
                .WithMany()
                .HasForeignKey(x => x.IssueCategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RiskDivision>(e =>
        {
            e.HasKey(x => new { x.RiskId, x.DivisionId });
            e.HasOne(x => x.Risk)
                .WithMany(r => r.RiskDivisions)
                .HasForeignKey(x => x.RiskId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Division)
                .WithMany()
                .HasForeignKey(x => x.DivisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RiskBusinessArea>(e =>
        {
            e.HasKey(x => new { x.RiskId, x.BusinessAreaLookupId });
            e.HasOne(x => x.Risk)
                .WithMany(r => r.RiskBusinessAreas)
                .HasForeignKey(x => x.RiskId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.BusinessAreaLookup)
                .WithMany()
                .HasForeignKey(x => x.BusinessAreaLookupId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IssueDivision>(e =>
        {
            e.HasKey(x => new { x.IssueId, x.DivisionId });
            e.HasOne(x => x.Issue)
                .WithMany(i => i.IssueDivisions)
                .HasForeignKey(x => x.IssueId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Division)
                .WithMany()
                .HasForeignKey(x => x.DivisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IssueBusinessArea>(e =>
        {
            e.HasKey(x => new { x.IssueId, x.BusinessAreaLookupId });
            e.HasOne(x => x.Issue)
                .WithMany(i => i.IssueBusinessAreas)
                .HasForeignKey(x => x.IssueId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.BusinessAreaLookup)
                .WithMany()
                .HasForeignKey(x => x.BusinessAreaLookupId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AssumptionDivision>(e =>
        {
            e.HasKey(x => new { x.AssumptionId, x.DivisionId });
            e.HasOne(x => x.Assumption)
                .WithMany(a => a.AssumptionDivisions)
                .HasForeignKey(x => x.AssumptionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Division)
                .WithMany()
                .HasForeignKey(x => x.DivisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidEscalationTierChangeRequest>(e =>
        {
            e.Property(x => x.RecordType).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.Rationale).HasMaxLength(2000);
            e.Property(x => x.DecisionNote).HasMaxLength(500);
            e.HasOne(x => x.Risk)
                .WithMany()
                .HasForeignKey(x => x.RiskId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Issue)
                .WithMany()
                .HasForeignKey(x => x.IssueId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FromRiskTier)
                .WithMany()
                .HasForeignKey(x => x.FromRiskTierId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ToRiskTier)
                .WithMany()
                .HasForeignKey(x => x.ToRiskTierId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SubmittedByUser)
                .WithMany()
                .HasForeignKey(x => x.SubmittedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DecidedByUser)
                .WithMany()
                .HasForeignKey(x => x.DecidedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AssumptionBusinessArea>(e =>
        {
            e.HasKey(x => new { x.AssumptionId, x.BusinessAreaLookupId });
            e.HasOne(x => x.Assumption)
                .WithMany(a => a.AssumptionBusinessAreas)
                .HasForeignKey(x => x.AssumptionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.BusinessAreaLookup)
                .WithMany()
                .HasForeignKey(x => x.BusinessAreaLookupId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NearMiss>(e =>
        {
            e.HasIndex(x => x.Reference).IsUnique();
            e.Property(x => x.Reference).HasMaxLength(50).IsRequired();
            e.Property(x => x.Impact).HasMaxLength(4000);
        });

        modelBuilder.Entity<NearMissOwner>(e =>
        {
            e.HasKey(x => new { x.NearMissId, x.UserId });
            e.HasOne(x => x.NearMiss)
                .WithMany(n => n.NearMissOwners)
                .HasForeignKey(x => x.NearMissId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NearMissAction>(e =>
        {
            e.Property(x => x.ActionText).HasMaxLength(4000).IsRequired();
            e.HasOne(x => x.NearMiss)
                .WithMany(n => n.NearMissActions)
                .HasForeignKey(x => x.NearMissId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NearMissMitigation>(e =>
        {
            e.Property(x => x.AssuranceTakenPlace).HasMaxLength(4000).IsRequired();
            e.HasOne(x => x.NearMiss)
                .WithMany(n => n.NearMissMitigations)
                .HasForeignKey(x => x.NearMissId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── RAID Registers ──────────────────────────────────────────

        modelBuilder.Entity<RaidRegister>(e =>
        {
            e.HasIndex(x => x.CreatedByUserId);
            e.HasIndex(x => x.DirectorateLookupId);
            e.HasIndex(x => x.BusinessAreaLookupId);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DirectorateLookup).WithMany().HasForeignKey(x => x.DirectorateLookupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.BusinessAreaLookup).WithMany().HasForeignKey(x => x.BusinessAreaLookupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidRegisterUser>(e =>
        {
            e.HasKey(x => new { x.RaidRegisterId, x.UserId });
            e.HasOne(x => x.RaidRegister).WithMany(r => r.Users).HasForeignKey(x => x.RaidRegisterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidRegisterWorkItem>(e =>
        {
            e.HasKey(x => new { x.RaidRegisterId, x.ProjectId });
            e.HasOne(x => x.RaidRegister).WithMany(r => r.WorkItems).HasForeignKey(x => x.RaidRegisterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidRegisterService>(e =>
        {
            e.HasKey(x => new { x.RaidRegisterId, x.FipsServiceId });
            e.HasOne(x => x.RaidRegister).WithMany(r => r.Services).HasForeignKey(x => x.RaidRegisterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FipsService).WithMany().HasForeignKey(x => x.FipsServiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidRegisterDirectorate>(e =>
        {
            e.HasKey(x => new { x.RaidRegisterId, x.DirectorateLookupId });
            e.HasOne(x => x.RaidRegister).WithMany(r => r.Directorates).HasForeignKey(x => x.RaidRegisterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.DirectorateLookup).WithMany().HasForeignKey(x => x.DirectorateLookupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidRegisterBusinessArea>(e =>
        {
            e.HasKey(x => new { x.RaidRegisterId, x.BusinessAreaLookupId });
            e.HasOne(x => x.RaidRegister).WithMany(r => r.BusinessAreas).HasForeignKey(x => x.RaidRegisterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.BusinessAreaLookup).WithMany().HasForeignKey(x => x.BusinessAreaLookupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidRegisterRisk>(e =>
        {
            e.HasKey(x => new { x.RaidRegisterId, x.RiskId });
            e.HasOne(x => x.RaidRegister).WithMany(r => r.Risks).HasForeignKey(x => x.RaidRegisterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Risk).WithMany().HasForeignKey(x => x.RiskId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AddedByUser).WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidRegisterIssue>(e =>
        {
            e.HasKey(x => new { x.RaidRegisterId, x.IssueId });
            e.HasOne(x => x.RaidRegister).WithMany(r => r.Issues).HasForeignKey(x => x.RaidRegisterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Issue).WithMany().HasForeignKey(x => x.IssueId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AddedByUser).WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidRegisterAssumption>(e =>
        {
            e.HasKey(x => new { x.RaidRegisterId, x.AssumptionId });
            e.HasOne(x => x.RaidRegister).WithMany(r => r.Assumptions).HasForeignKey(x => x.RaidRegisterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Assumption).WithMany().HasForeignKey(x => x.AssumptionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AddedByUser).WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidRegisterDependency>(e =>
        {
            e.HasKey(x => new { x.RaidRegisterId, x.DependencyId });
            e.HasOne(x => x.RaidRegister).WithMany(r => r.Dependencies).HasForeignKey(x => x.RaidRegisterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Dependency).WithMany().HasForeignKey(x => x.DependencyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AddedByUser).WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidRegisterNearMiss>(e =>
        {
            e.HasKey(x => new { x.RaidRegisterId, x.NearMissId });
            e.HasOne(x => x.RaidRegister).WithMany(r => r.NearMisses).HasForeignKey(x => x.RaidRegisterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.NearMiss).WithMany().HasForeignKey(x => x.NearMissId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AddedByUser).WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidRegisterSpreadsheetLayout>(e =>
        {
            e.HasIndex(x => x.EntityType).IsUnique();
            e.Property(x => x.EntityType).HasMaxLength(32);
            e.Property(x => x.ColumnOrderJson).HasColumnType("nvarchar(max)");
            e.HasOne(x => x.UpdatedByUser).WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Issue>()
            .HasIndex(i => i.ProductDocumentId);

        modelBuilder.Entity<Issue>()
            .HasIndex(i => i.Status);

        modelBuilder.Entity<Issue>()
            .HasIndex(i => new { i.Severity, i.Priority });

        modelBuilder.Entity<Issue>()
            .HasIndex(i => i.TargetResolutionDate);

        modelBuilder.Entity<IssueAssuranceEvent>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.EventKind).HasMaxLength(50);
            ConfigureRaidNarrativeColumns(e.Property(x => x.DecisionSummary));
            e.HasOne(x => x.Issue)
                .WithMany(i => i.AssuranceEvents)
                .HasForeignKey(x => x.IssueId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.IssueId);
            e.HasIndex(x => new { x.IssueId, x.SortOrder });
        });

        ConfigureRaidIssueNarrativeColumns(modelBuilder.Entity<Issue>());

        // Milestone configuration
        modelBuilder.Entity<Milestone>()
            .HasOne(m => m.Objective)
            .WithMany(o => o.Milestones)
            .HasForeignKey(m => m.ObjectiveId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Milestone>()
            .HasIndex(m => m.ObjectiveId);

        modelBuilder.Entity<Milestone>()
            .HasIndex(m => m.FipsId);

        modelBuilder.Entity<Milestone>()
            .HasIndex(m => m.ProductDocumentId);

        modelBuilder.Entity<Milestone>()
            .HasIndex(m => m.Status);

        modelBuilder.Entity<Milestone>()
            .HasIndex(m => m.DueDate);

        // KPI configuration
        modelBuilder.Entity<Kpi>()
            .Property(k => k.Name)
            .HasMaxLength(200);

        modelBuilder.Entity<Kpi>()
            .Property(k => k.Code)
            .HasMaxLength(50);

        modelBuilder.Entity<Kpi>()
            .Property(k => k.Category)
            .HasMaxLength(100);

        modelBuilder.Entity<Kpi>()
            .Property(k => k.UnitOfMeasure)
            .HasMaxLength(50);

        modelBuilder.Entity<Kpi>()
            .Property(k => k.Frequency)
            .HasMaxLength(50);

        modelBuilder.Entity<Kpi>()
            .Property(k => k.ReportingStage)
            .HasMaxLength(200);

        modelBuilder.Entity<Kpi>()
            .Property(k => k.Status)
            .HasMaxLength(50);

        modelBuilder.Entity<Kpi>()
            .Property(k => k.AssignedToEntityId)
            .HasMaxLength(100);

        modelBuilder.Entity<Kpi>()
            .Property(k => k.EntityType)
            .HasMaxLength(20);

        modelBuilder.Entity<Kpi>()
            .Property(k => k.ProductDocumentId)
            .HasMaxLength(100);

        modelBuilder.Entity<Kpi>()
            .Property(k => k.ProductFipsId)
            .HasMaxLength(50);

        modelBuilder.Entity<Kpi>()
            .Property(k => k.Description)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<Kpi>()
            .Property(k => k.CalculationMethod)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<Kpi>()
            .Property(k => k.Thresholds)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<Kpi>()
            .Property(k => k.DataSource)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<Kpi>()
            .Property(k => k.ValidationRule)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<Kpi>()
            .HasIndex(k => k.Code)
            .IsUnique();

        modelBuilder.Entity<Kpi>()
            .HasIndex(k => new { k.AssignedToEntityId, k.EntityType });

        modelBuilder.Entity<Kpi>()
            .HasIndex(k => k.ProjectId);

        modelBuilder.Entity<Kpi>()
            .HasIndex(k => k.ObjectiveId);

        modelBuilder.Entity<Kpi>()
            .HasIndex(k => k.MilestoneId);

        modelBuilder.Entity<Kpi>()
            .HasIndex(k => k.OwnerUserId);

        modelBuilder.Entity<Kpi>()
            .HasIndex(k => k.Active);

        modelBuilder.Entity<Kpi>()
            .HasIndex(k => k.ProductDocumentId);

        modelBuilder.Entity<Kpi>()
            .Property(k => k.TargetValue)
            .HasPrecision(18, 4);

        modelBuilder.Entity<Kpi>()
            .HasOne(k => k.Project)
            .WithMany(p => p.Kpis)
            .HasForeignKey(k => k.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Kpi>()
            .HasOne(k => k.Objective)
            .WithMany(o => o.Kpis)
            .HasForeignKey(k => k.ObjectiveId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Kpi>()
            .HasOne(k => k.Milestone)
            .WithMany(m => m.Kpis)
            .HasForeignKey(k => k.MilestoneId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Kpi>()
            .HasOne(k => k.OwnerUser)
            .WithMany()
            .HasForeignKey(k => k.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // KPI performance data configuration
        modelBuilder.Entity<KpiDataPoint>()
            .HasOne(dp => dp.Kpi)
            .WithMany(k => k.PerformanceData)
            .HasForeignKey(dp => dp.KpiId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<KpiDataPoint>()
            .HasOne(dp => dp.SubmittedByUser)
            .WithMany()
            .HasForeignKey(dp => dp.SubmittedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<KpiDataPoint>()
            .HasIndex(dp => dp.KpiId);

        modelBuilder.Entity<KpiDataPoint>()
            .HasIndex(dp => dp.ReportingPeriodStart);

        modelBuilder.Entity<KpiDataPoint>()
            .Property(dp => dp.SubmissionStatus)
            .HasMaxLength(30);

        modelBuilder.Entity<KpiDataPoint>()
            .Property(dp => dp.ValueNarrative)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<KpiDataPoint>()
            .Property(dp => dp.Notes)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<KpiDataPoint>()
            .Property(dp => dp.Value)
            .HasPrecision(20, 4);

        // Action configuration
        modelBuilder.Entity<Models.Action>()
            .HasOne(a => a.Objective)
            .WithMany(o => o.Actions)
            .HasForeignKey(a => a.ObjectiveId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Models.Action>()
            .HasOne(a => a.ParentAction)
            .WithMany(a => a.SubActions)
            .HasForeignKey(a => a.ParentActionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Models.Action>()
            .HasOne(a => a.ActionSource)
            .WithMany()
            .HasForeignKey(a => a.ActionSourceId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Models.Action>()
            .HasIndex(a => a.ObjectiveId);

        modelBuilder.Entity<Models.Action>()
            .HasIndex(a => a.ActionSourceId);

        modelBuilder.Entity<Models.Action>()
            .HasIndex(a => a.FipsId);

        modelBuilder.Entity<Models.Action>()
            .HasIndex(a => a.ProductDocumentId);

        modelBuilder.Entity<Models.Action>()
            .HasIndex(a => a.AssignedToEmail);

        modelBuilder.Entity<Models.Action>()
            .HasIndex(a => new { a.Status, a.Priority });

        modelBuilder.Entity<Models.Action>()
            .Property(a => a.Title)
            .HasMaxLength(RaidNarrativeMaxLength)
            .HasColumnType("nvarchar(4000)");

        modelBuilder.Entity<Models.Action>()
            .Property(a => a.Notes)
            .HasMaxLength(RaidNarrativeMaxLength)
            .HasColumnType("nvarchar(4000)");

        modelBuilder.Entity<Models.Action>()
            .HasIndex(a => a.DueDate);

        modelBuilder.Entity<Models.Action>()
            .HasOne(a => a.Decision)
            .WithMany(d => d.Actions)
            .HasForeignKey(a => a.DecisionId)
            .OnDelete(DeleteBehavior.SetNull);

        // Decision configuration
        modelBuilder.Entity<Decision>()
            .HasOne(d => d.Objective)
            .WithMany(o => o.Decisions)
            .HasForeignKey(d => d.ObjectiveId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Decision>()
            .HasOne(d => d.Project)
            .WithMany(p => p.Decisions)
            .HasForeignKey(d => d.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Decision>()
            .HasOne(d => d.OwnerUser)
            .WithMany()
            .HasForeignKey(d => d.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Decision>()
            .HasIndex(d => d.ObjectiveId);

        modelBuilder.Entity<Decision>()
            .HasIndex(d => d.ProjectId);

        modelBuilder.Entity<Decision>()
            .HasIndex(d => d.OwnerUserId);

        modelBuilder.Entity<Decision>()
            .HasIndex(d => d.FipsId);

        modelBuilder.Entity<Decision>()
            .HasIndex(d => d.ProductDocumentId);

        modelBuilder.Entity<Decision>()
            .HasIndex(d => d.Status);

        modelBuilder.Entity<Decision>()
            .HasIndex(d => d.DecisionDate);

        // Junction table configurations

        // RiskAction
        modelBuilder.Entity<RiskAction>()
            .HasKey(ra => new { ra.RiskId, ra.ActionId });

        modelBuilder.Entity<RiskAction>()
            .HasOne(ra => ra.Risk)
            .WithMany(r => r.RiskActions)
            .HasForeignKey(ra => ra.RiskId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RiskAction>()
            .HasOne(ra => ra.Action)
            .WithMany(a => a.RiskActions)
            .HasForeignKey(ra => ra.ActionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RiskAction>()
            .HasIndex(ra => ra.ActionId);

        // IssueAction
        modelBuilder.Entity<IssueAction>()
            .HasKey(ia => new { ia.IssueId, ia.ActionId });

        modelBuilder.Entity<IssueAction>()
            .HasOne(ia => ia.Issue)
            .WithMany(i => i.IssueActions)
            .HasForeignKey(ia => ia.IssueId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<IssueAction>()
            .HasOne(ia => ia.Action)
            .WithMany(a => a.IssueActions)
            .HasForeignKey(ia => ia.ActionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<IssueAction>()
            .HasIndex(ia => ia.ActionId);

        // RiskDecision
        modelBuilder.Entity<RiskDecision>()
            .HasKey(rd => new { rd.RiskId, rd.DecisionId });

        modelBuilder.Entity<RiskDecision>()
            .HasOne(rd => rd.Risk)
            .WithMany(r => r.RiskDecisions)
            .HasForeignKey(rd => rd.RiskId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RiskDecision>()
            .HasOne(rd => rd.Decision)
            .WithMany(d => d.RiskDecisions)
            .HasForeignKey(rd => rd.DecisionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RiskDecision>()
            .HasIndex(rd => rd.DecisionId);

        // IssueDecision
        modelBuilder.Entity<IssueDecision>()
            .HasKey(id => new { id.IssueId, id.DecisionId });

        modelBuilder.Entity<IssueDecision>()
            .HasOne(id => id.Issue)
            .WithMany(i => i.IssueDecisions)
            .HasForeignKey(id => id.IssueId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<IssueDecision>()
            .HasOne(id => id.Decision)
            .WithMany(d => d.IssueDecisions)
            .HasForeignKey(id => id.DecisionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<IssueDecision>()
            .HasIndex(id => id.DecisionId);

        // ActionDecision
        modelBuilder.Entity<ActionDecision>()
            .HasKey(ad => new { ad.ActionId, ad.DecisionId });

        modelBuilder.Entity<ActionDecision>()
            .HasOne(ad => ad.Action)
            .WithMany(a => a.ActionDecisions)
            .HasForeignKey(ad => ad.ActionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ActionDecision>()
            .HasOne(ad => ad.Decision)
            .WithMany(d => d.ActionDecisions)
            .HasForeignKey(ad => ad.DecisionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ActionDecision>()
            .HasIndex(ad => ad.DecisionId);

        // IssueRisk
        modelBuilder.Entity<IssueRisk>()
            .HasKey(ir => new { ir.IssueId, ir.RiskId });

        modelBuilder.Entity<IssueRisk>()
            .HasOne(ir => ir.Issue)
            .WithMany(i => i.IssueRisks)
            .HasForeignKey(ir => ir.IssueId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<IssueRisk>()
            .HasOne(ir => ir.Risk)
            .WithMany(r => r.IssueRisks)
            .HasForeignKey(ir => ir.RiskId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<IssueRisk>()
            .HasIndex(ir => ir.RiskId);

        // MilestoneAction
        modelBuilder.Entity<MilestoneAction>()
            .HasKey(ma => new { ma.MilestoneId, ma.ActionId });

        modelBuilder.Entity<MilestoneAction>()
            .HasOne(ma => ma.Milestone)
            .WithMany(m => m.MilestoneActions)
            .HasForeignKey(ma => ma.MilestoneId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MilestoneAction>()
            .HasOne(ma => ma.Action)
            .WithMany(a => a.MilestoneActions)
            .HasForeignKey(ma => ma.ActionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MilestoneAction>()
            .HasIndex(ma => ma.ActionId);

        // MilestoneRisk
        modelBuilder.Entity<MilestoneRisk>()
            .HasKey(mr => new { mr.MilestoneId, mr.RiskId });

        modelBuilder.Entity<MilestoneRisk>()
            .HasOne(mr => mr.Milestone)
            .WithMany(m => m.MilestoneRisks)
            .HasForeignKey(mr => mr.MilestoneId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MilestoneRisk>()
            .HasOne(mr => mr.Risk)
            .WithMany(r => r.MilestoneRisks)
            .HasForeignKey(mr => mr.RiskId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MilestoneRisk>()
            .HasIndex(mr => mr.RiskId);

        // MilestoneIssue
        modelBuilder.Entity<MilestoneIssue>()
            .HasKey(mi => new { mi.MilestoneId, mi.IssueId });

        modelBuilder.Entity<MilestoneIssue>()
            .HasOne(mi => mi.Milestone)
            .WithMany(m => m.MilestoneIssues)
            .HasForeignKey(mi => mi.MilestoneId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MilestoneIssue>()
            .HasOne(mi => mi.Issue)
            .WithMany(i => i.MilestoneIssues)
            .HasForeignKey(mi => mi.IssueId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MilestoneIssue>()
            .HasIndex(mi => mi.IssueId);

        // Risk-RiskType junction table
        modelBuilder.Entity<RiskRiskType>()
            .HasKey(rrt => new { rrt.RiskId, rrt.RiskTypeId });

        modelBuilder.Entity<RiskRiskType>()
            .HasOne(rrt => rrt.Risk)
            .WithMany(r => r.RiskRiskTypes)
            .HasForeignKey(rrt => rrt.RiskId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RiskRiskType>()
            .HasOne(rrt => rrt.RiskType)
            .WithMany()
            .HasForeignKey(rrt => rrt.RiskTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RiskRiskType>()
            .HasIndex(rrt => rrt.RiskTypeId);

        // RAID Lookups
        modelBuilder.Entity<RiskType>()
            .HasIndex(rt => rt.Code)
            .IsUnique();

        modelBuilder.Entity<RiskType>()
            .HasIndex(rt => rt.IsActive);

        modelBuilder.Entity<RiskTier>()
            .HasIndex(rt => rt.Code)
            .IsUnique();

        modelBuilder.Entity<RiskTier>()
            .HasIndex(rt => rt.IsActive);

        modelBuilder.Entity<RiskTier>()
            .HasIndex(rt => rt.SortOrder);

        modelBuilder.Entity<ActionSource>()
            .HasIndex(a_s => a_s.Code)
            .IsUnique();

        modelBuilder.Entity<ActionSource>()
            .HasIndex(a_s => a_s.IsActive);

        modelBuilder.Entity<ActionSource>()
            .HasIndex(a_s => a_s.SortOrder);

        // Project Lookups
        modelBuilder.Entity<BusinessAreaLookup>()
            .HasIndex(ba => ba.Name)
            .IsUnique();

        modelBuilder.Entity<BusinessAreaLookup>()
            .HasIndex(ba => ba.IsActive);

        modelBuilder.Entity<BusinessAreaLookup>()
            .HasIndex(ba => ba.SortOrder);

        modelBuilder.Entity<PhaseLookup>()
            .HasIndex(p => p.Name)
            .IsUnique();

        modelBuilder.Entity<PhaseLookup>()
            .HasIndex(p => p.IsActive);

        modelBuilder.Entity<PhaseLookup>()
            .HasIndex(p => p.SortOrder);

        modelBuilder.Entity<KpiCategory>()
            .HasIndex(k => k.Code)
            .IsUnique();

        modelBuilder.Entity<KpiCategory>()
            .HasIndex(k => k.IsActive);

        modelBuilder.Entity<KpiCategory>()
            .HasIndex(k => k.SortOrder);

        modelBuilder.Entity<DeliveryPriority>()
            .HasIndex(dp => dp.Name)
            .IsUnique();

        modelBuilder.Entity<DeliveryPriority>()
            .HasIndex(dp => dp.IsActive);

        modelBuilder.Entity<DeliveryPriority>()
            .HasIndex(dp => dp.SortOrder);

        modelBuilder.Entity<ResourceBandLookup>()
            .HasIndex(rb => rb.Name)
            .IsUnique();

        modelBuilder.Entity<ResourceBandLookup>()
            .HasIndex(rb => rb.IsActive);

        modelBuilder.Entity<ResourceBandLookup>()
            .HasIndex(rb => rb.SortOrder);

        // User preferences
        modelBuilder.Entity<UserPreference>()
            .HasOne(up => up.User)
            .WithOne()
            .HasForeignKey<UserPreference>(up => up.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserPreference>()
            .HasIndex(up => up.RaidRegisterBusinessAreaLookupId);

        modelBuilder.Entity<UserPreference>()
            .HasOne<BusinessAreaLookup>()
            .WithMany()
            .HasForeignKey(up => up.RaidRegisterBusinessAreaLookupId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // API Token configuration
        modelBuilder.Entity<ApiToken>()
            .HasIndex(at => at.Token)
            .IsUnique();

        modelBuilder.Entity<ApiToken>()
            .HasIndex(at => at.IsActive);

        modelBuilder.Entity<ApiToken>()
            .HasIndex(at => at.CreatedAt);

        modelBuilder.Entity<ApiToken>()
            .HasIndex(at => at.ExpiresAt);

        modelBuilder.Entity<ApiToken>()
            .HasIndex(at => at.Name)
            .IsUnique();

        modelBuilder.Entity<ApiToken>()
            .HasIndex(at => at.OwnerEmail);

        modelBuilder.Entity<ApiTokenRequest>()
            .HasOne(r => r.IssuedApiToken)
            .WithMany()
            .HasForeignKey(r => r.IssuedApiTokenId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ApiTokenRequest>()
            .HasIndex(r => r.Status);

        modelBuilder.Entity<ApiTokenRequest>()
            .HasIndex(r => r.RequestorEmail);

        modelBuilder.Entity<ApiTokenMember>()
            .HasOne(m => m.ApiToken)
            .WithMany(t => t.Members)
            .HasForeignKey(m => m.ApiTokenId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ApiTokenMember>()
            .HasIndex(m => new { m.ApiTokenId, m.UserEmail })
            .IsUnique();

        modelBuilder.Entity<ApiTokenPermission>()
            .HasOne(atp => atp.ApiToken)
            .WithMany(at => at.Permissions)
            .HasForeignKey(atp => atp.ApiTokenId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ApiTokenPermission>()
            .HasIndex(atp => new { atp.ApiTokenId, atp.Resource })
            .IsUnique();

        modelBuilder.Entity<ApiRequestLog>()
            .HasOne(arl => arl.ApiToken)
            .WithMany(at => at.RequestLogs)
            .HasForeignKey(arl => arl.ApiTokenId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ApiRequestLog>()
            .HasIndex(arl => arl.RequestTimestamp);

        modelBuilder.Entity<ApiRequestLog>()
            .HasIndex(arl => arl.ApiTokenId);

        modelBuilder.Entity<ApiRequestLog>()
            .HasIndex(arl => arl.IsSuccess);

        // ProductAccessibility configuration
        // Note: Index is non-unique initially to allow NULLs during migration
        // Will be made unique after data migration populates DocumentIds
        modelBuilder.Entity<ProductAccessibility>()
            .HasIndex(pa => pa.ProductDocumentId);

        modelBuilder.Entity<ProductAccessibility>()
            .HasIndex(pa => pa.FipsId);

        // AccessibilityIssue configuration
        modelBuilder.Entity<AccessibilityIssue>()
            .Property(ai => ai.IssueDescription)
            .HasColumnType("nvarchar(max)");

        // IssueHistory configuration - allow unlimited length for OldValue and NewValue
        modelBuilder.Entity<IssueHistory>()
            .Property(ih => ih.OldValue)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<IssueHistory>()
            .Property(ih => ih.NewValue)
            .HasColumnType("nvarchar(max)");

        // Accessibility retest request configuration
        modelBuilder.Entity<AccessibilityRetestRequest>()
            .HasOne(rr => rr.AccessibilityIssue)
            .WithMany(ai => ai.RetestRequests)
            .HasForeignKey(rr => rr.AccessibilityIssueId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AccessibilityRetestRequest>()
            .HasIndex(rr => rr.AccessibilityIssueId);

        modelBuilder.Entity<AccessibilityRetestRequest>()
            .HasIndex(rr => rr.IsCompleted);

        modelBuilder.Entity<AccessibilityRetestRequest>()
            .HasIndex(rr => rr.RequestedAt);

        // Accessibility email configuration
        modelBuilder.Entity<AccessibilityEmailConfiguration>()
            .HasIndex(ec => ec.Purpose);

        modelBuilder.Entity<AccessibilityEmailConfiguration>()
            .HasIndex(ec => ec.IsActive);

        modelBuilder.Entity<AccessibilityEmailConfiguration>()
            .HasIndex(ec => new { ec.Purpose, ec.EmailAddress })
            .IsUnique();

        modelBuilder.Entity<HttpErrorEmailSettings>()
            .HasKey(s => s.Id);
        modelBuilder.Entity<HttpErrorEmailSettings>()
            .Property(s => s.Id)
            .ValueGeneratedNever();
        modelBuilder.Entity<HttpErrorEmailSettings>()
            .Property(s => s.ContactEmail)
            .HasMaxLength(256);
        modelBuilder.Entity<HttpErrorEmailSettings>()
            .Property(s => s.UpdatedByEmail)
            .HasMaxLength(256);

        // StatementTemplate configuration
        modelBuilder.Entity<StatementTemplate>()
            .HasIndex(st => new { st.Name, st.Version })
            .IsUnique();

        modelBuilder.Entity<StatementTemplate>()
            .HasIndex(st => st.Name);

        modelBuilder.Entity<StatementTemplate>()
            .HasIndex(st => st.IsActive);

        modelBuilder.Entity<StatementTemplate>()
            .HasIndex(st => st.CreatedAt);

        modelBuilder.Entity<StatementTemplate>()
            .Property(st => st.Content)
            .HasMaxLength(int.MaxValue)
            .HasColumnType("nvarchar(max)");

        // ========================================
        // PROJECT MANAGEMENT CONFIGURATION
        // ========================================

        // Mission configuration
        modelBuilder.Entity<Mission>()
            .HasIndex(m => m.Status);

        modelBuilder.Entity<Mission>()
            .HasIndex(m => m.OwnerUserId);

        // Project configuration
        modelBuilder.Entity<Project>()
            .HasIndex(p => p.RagStatus);

        modelBuilder.Entity<Project>()
            .HasIndex(p => p.Status);

        modelBuilder.Entity<Project>()
            .HasIndex(p => p.IsFlagship);

        modelBuilder.Entity<Project>()
            .HasIndex(p => p.StartDate);

        modelBuilder.Entity<Project>()
            .HasIndex(p => p.TargetDeliveryDate);

        // Configure decimal precision for FTE fields
        modelBuilder.Entity<Project>()
            .Property(p => p.TotalPermFte)
            .HasPrecision(10, 2);

        modelBuilder.Entity<Project>()
            .Property(p => p.TotalMspFte)
            .HasPrecision(10, 2);

        modelBuilder.Entity<Project>()
            .Property(p => p.ServiceUsers)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<Project>()
            .Property(p => p.Aim)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<Project>()
            .HasIndex(p => p.PipelineDemandRequestId);

        modelBuilder.Entity<Project>()
            .HasOne(p => p.PipelineDemandRequest)
            .WithMany()
            .HasForeignKey(p => p.PipelineDemandRequestId)
            .OnDelete(DeleteBehavior.SetNull);

        // ProjectRagHistory configuration
        modelBuilder.Entity<ProjectRagHistory>()
            .HasOne(prh => prh.Project)
            .WithMany(p => p.RagHistory)
            .HasForeignKey(prh => prh.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectRagHistory>()
            .HasIndex(prh => prh.ProjectId);

        modelBuilder.Entity<ProjectRagHistory>()
            .HasIndex(prh => prh.ChangedAt);

        ConfigureMonthlyReportTextColumns(modelBuilder.Entity<ProjectMonthlyUpdate>());
        ConfigureMonthlyReportTextColumns(modelBuilder.Entity<MonthlyUpdateNarrative>());
        ConfigureMonthlyReportTextColumns(modelBuilder.Entity<Project>());
        ConfigureMonthlyReportTextColumns(modelBuilder.Entity<ProjectRagHistory>());

        // ProjectSuccess configuration
        modelBuilder.Entity<ProjectSuccess>()
            .HasOne(ps => ps.Project)
            .WithMany(p => p.Successes)
            .HasForeignKey(ps => ps.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectSuccess>()
            .HasIndex(ps => ps.ProjectId);

        modelBuilder.Entity<ProjectSuccess>()
            .HasIndex(ps => ps.RecordedAt);

        // ProjectOutcome configuration
        modelBuilder.Entity<ProjectOutcome>()
            .HasOne(po => po.Project)
            .WithMany(p => p.Outcomes)
            .HasForeignKey(po => po.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectOutcome>()
            .HasIndex(po => po.ProjectId);

        modelBuilder.Entity<ProjectOutcome>()
            .HasIndex(po => po.SortOrder);

        modelBuilder.Entity<ProjectOutcome>()
            .HasIndex(po => po.ConfidenceLevel);

        // ProjectMission configuration
        modelBuilder.Entity<ProjectMission>()
            .HasOne(pm => pm.Project)
            .WithMany(p => p.ProjectMissions)
            .HasForeignKey(pm => pm.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectMission>()
            .HasOne(pm => pm.Mission)
            .WithMany()
            .HasForeignKey(pm => pm.MissionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectMission>()
            .HasIndex(pm => pm.ProjectId);

        modelBuilder.Entity<ProjectMission>()
            .HasIndex(pm => pm.MissionId);

        // ProjectFundingAllocation configuration
        modelBuilder.Entity<ProjectFundingAllocation>()
            .HasOne(pfa => pfa.Project)
            .WithMany(p => p.FundingAllocations)
            .HasForeignKey(pfa => pfa.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectFundingAllocation>()
            .HasOne(pfa => pfa.FundingSource)
            .WithMany()
            .HasForeignKey(pfa => pfa.FundingSourceId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectFundingAllocation>()
            .HasIndex(pfa => pfa.ProjectId);

        modelBuilder.Entity<ProjectFundingAllocation>()
            .HasIndex(pfa => pfa.FundingSourceId);

        // ProjectContact configuration
        modelBuilder.Entity<ProjectContact>()
            .HasOne(pc => pc.Project)
            .WithMany(p => p.ProjectContacts)
            .HasForeignKey(pc => pc.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectContact>()
            .HasOne(pc => pc.User)
            .WithMany()
            .HasForeignKey(pc => pc.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ProjectContact>()
            .HasIndex(pc => pc.ProjectId);

        modelBuilder.Entity<ProjectContact>()
            .HasIndex(pc => pc.SortOrder);

        modelBuilder.Entity<ProjectContact>()
            .HasIndex(pc => pc.Role);

        modelBuilder.Entity<ProjectContact>()
            .HasIndex(pc => pc.TeamStatus);

        // ProjectStatusUpdate configuration
        modelBuilder.Entity<ProjectStatusUpdate>()
            .HasOne(psu => psu.Project)
            .WithMany(p => p.StatusUpdates)
            .HasForeignKey(psu => psu.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectStatusUpdate>()
            .HasOne(psu => psu.CreatedByUser)
            .WithMany()
            .HasForeignKey(psu => psu.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        modelBuilder.Entity<ProjectStatusUpdate>()
            .HasOne(psu => psu.UpdatedByUser)
            .WithMany()
            .HasForeignKey(psu => psu.UpdatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ProjectStatusUpdate>()
            .HasIndex(psu => psu.ProjectId);

        modelBuilder.Entity<ProjectStatusUpdate>()
            .HasIndex(psu => psu.CreatedAt);

        modelBuilder.Entity<ProjectStatusUpdate>()
            .Property(psu => psu.Narrative)
            .HasColumnType("nvarchar(max)");

        // MonthlyStatusReport configuration
        modelBuilder.Entity<MonthlyStatusReport>()
            .HasOne(msr => msr.Project)
            .WithMany()
            .HasForeignKey(msr => msr.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MonthlyStatusReport>()
            .HasOne(msr => msr.CreatedByUser)
            .WithMany()
            .HasForeignKey(msr => msr.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        modelBuilder.Entity<MonthlyStatusReport>()
            .HasIndex(msr => new { msr.ProjectId, msr.ReportingYear, msr.ReportingMonth })
            .IsUnique();

        modelBuilder.Entity<MonthlyStatusReport>()
            .HasIndex(msr => new { msr.ReportingYear, msr.ReportingMonth });

        modelBuilder.Entity<MonthlyStatusReport>()
            .Property(msr => msr.Narrative)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<MonthlyStatusReport>()
            .Property(msr => msr.MilestoneProgress)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<MonthlyStatusReport>()
            .Property(msr => msr.DeliverableProgress)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<MonthlyStatusReport>()
            .Property(msr => msr.KeyAchievements)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<MonthlyStatusReport>()
            .Property(msr => msr.Challenges)
            .HasColumnType("nvarchar(max)");

        modelBuilder.Entity<MonthlyStatusReport>()
            .Property(msr => msr.NextMonthOutlook)
            .HasColumnType("nvarchar(max)");

        // MonthlyStatusReportTimescaleConfig configuration
        modelBuilder.Entity<MonthlyStatusReportTimescaleConfig>()
            .HasIndex(tsc => tsc.IsDefault)
            .HasFilter("[IsDefault] = 1");

        modelBuilder.Entity<MonthlyStatusReportTimescaleConfig>()
            .HasIndex(tsc => tsc.IsActive);

        // ProjectArtefact configuration
        modelBuilder.Entity<ProjectArtefact>()
            .HasOne(pa => pa.Project)
            .WithMany(p => p.Artefacts)
            .HasForeignKey(pa => pa.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectArtefact>()
            .HasOne(pa => pa.CreatedByUser)
            .WithMany()
            .HasForeignKey(pa => pa.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        modelBuilder.Entity<ProjectArtefact>()
            .HasOne(pa => pa.UpdatedByUser)
            .WithMany()
            .HasForeignKey(pa => pa.UpdatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ProjectArtefact>()
            .HasIndex(pa => pa.ProjectId);

        modelBuilder.Entity<ProjectArtefact>()
            .HasIndex(pa => pa.CreatedAt);

        modelBuilder.Entity<ProjectArtefact>()
            .HasIndex(pa => pa.IsDeleted);

        modelBuilder.Entity<ProjectArtefact>()
            .Property(pa => pa.Description)
            .HasColumnType("nvarchar(max)");

        // ProjectSeniorResponsibleOfficer configuration
        modelBuilder.Entity<ProjectSeniorResponsibleOfficer>()
            .HasOne(psro => psro.Project)
            .WithMany(p => p.SeniorResponsibleOfficers)
            .HasForeignKey(psro => psro.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectSeniorResponsibleOfficer>()
            .HasOne(psro => psro.User)
            .WithMany()
            .HasForeignKey(psro => psro.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ProjectSeniorResponsibleOfficer>()
            .HasIndex(psro => psro.ProjectId);

        modelBuilder.Entity<ProjectSeniorResponsibleOfficer>()
            .HasIndex(psro => new { psro.ProjectId, psro.UserId })
            .IsUnique();

        // ProjectServiceOwner configuration
        modelBuilder.Entity<ProjectServiceOwner>()
            .HasOne(pso => pso.Project)
            .WithMany(p => p.ServiceOwners)
            .HasForeignKey(pso => pso.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectServiceOwner>()
            .HasOne(pso => pso.User)
            .WithMany()
            .HasForeignKey(pso => pso.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ProjectServiceOwner>()
            .HasIndex(pso => pso.ProjectId);

        modelBuilder.Entity<ProjectServiceOwner>()
            .HasIndex(pso => new { pso.ProjectId, pso.UserId })
            .IsUnique();

        // ProjectDirectorate configuration
        modelBuilder.Entity<ProjectDirectorate>()
            .HasOne(pd => pd.Project)
            .WithMany(p => p.Directorates)
            .HasForeignKey(pd => pd.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectDirectorate>()
            .HasOne(pd => pd.Division)
            .WithMany()
            .HasForeignKey(pd => pd.DivisionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ProjectDirectorate>()
            .HasIndex(pd => pd.ProjectId);

        modelBuilder.Entity<ProjectDirectorate>()
            .HasIndex(pd => new { pd.ProjectId, pd.DivisionId })
            .IsUnique();

        // ProjectBudgetOwner configuration
        modelBuilder.Entity<ProjectBudgetOwner>()
            .HasOne(pbo => pbo.Project)
            .WithMany(p => p.BudgetOwners)
            .HasForeignKey(pbo => pbo.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectBudgetOwner>()
            .HasOne(pbo => pbo.BusinessAreaLookup)
            .WithMany()
            .HasForeignKey(pbo => pbo.BusinessAreaLookupId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ProjectBudgetOwner>()
            .HasIndex(pbo => pbo.ProjectId);

        modelBuilder.Entity<ProjectBudgetOwner>()
            .HasIndex(pbo => new { pbo.ProjectId, pbo.BusinessAreaLookupId })
            .IsUnique();

        // ProjectPmoContact configuration
        modelBuilder.Entity<ProjectPmoContact>()
            .HasOne(ppc => ppc.Project)
            .WithMany(p => p.PmoContacts)
            .HasForeignKey(ppc => ppc.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectPmoContact>()
            .HasOne(ppc => ppc.User)
            .WithMany()
            .HasForeignKey(ppc => ppc.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ProjectPmoContact>()
            .HasIndex(ppc => ppc.ProjectId);

        modelBuilder.Entity<ProjectPmoContact>()
            .HasIndex(ppc => new { ppc.ProjectId, ppc.UserId })
            .IsUnique();

        // ActivityTypeLookup configuration
        modelBuilder.Entity<ActivityTypeLookup>()
            .HasIndex(at => at.Name)
            .IsUnique();

        modelBuilder.Entity<ActivityTypeLookup>()
            .HasIndex(at => at.IsActive);

        modelBuilder.Entity<WorkItemTagLookup>()
            .HasIndex(t => t.Name)
            .IsUnique();

        modelBuilder.Entity<WorkItemTagLookup>()
            .HasIndex(t => t.IsActive);

        modelBuilder.Entity<ProjectWorkItemTag>()
            .HasKey(x => new { x.ProjectId, x.WorkItemTagLookupId });

        modelBuilder.Entity<ProjectWorkItemTag>()
            .HasOne(x => x.Project)
            .WithMany(p => p.ProjectWorkItemTags)
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectWorkItemTag>()
            .HasOne(x => x.WorkItemTagLookup)
            .WithMany(t => t.ProjectLinks)
            .HasForeignKey(x => x.WorkItemTagLookupId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ProjectWorkItemTag>()
            .HasIndex(x => x.ProjectId);

        modelBuilder.Entity<ProjectWorkItemTag>()
            .HasIndex(x => x.WorkItemTagLookupId);

        // DirectorateLookup configuration
        modelBuilder.Entity<DirectorateLookup>()
            .HasIndex(dl => dl.Name)
            .IsUnique();

        modelBuilder.Entity<DirectorateLookup>()
            .HasIndex(dl => dl.IsActive);

        // RiskAppetiteLookup configuration
        modelBuilder.Entity<RiskAppetiteLookup>()
            .HasIndex(ral => ral.Name)
            .IsUnique();

        modelBuilder.Entity<RiskAppetiteLookup>()
            .HasIndex(ral => ral.IsActive);

        // ProjectObjective configuration
        modelBuilder.Entity<ProjectObjective>()
            .HasOne(po => po.Project)
            .WithMany(p => p.ProjectObjectives)
            .HasForeignKey(po => po.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectObjective>()
            .HasOne(po => po.Objective)
            .WithMany()
            .HasForeignKey(po => po.ObjectiveId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectObjective>()
            .HasIndex(po => po.ProjectId);

        modelBuilder.Entity<ProjectObjective>()
            .HasIndex(po => po.ObjectiveId);

        // Dependency configuration
        modelBuilder.Entity<Dependency>()
            .HasIndex(d => new { d.SourceEntityType, d.SourceEntityId });

        modelBuilder.Entity<Dependency>()
            .HasIndex(d => new { d.TargetEntityType, d.TargetEntityId });

        modelBuilder.Entity<Dependency>()
            .HasIndex(d => d.Status);

        modelBuilder.Entity<Dependency>()
            .HasIndex(d => d.DependencyType);

        modelBuilder.Entity<Dependency>()
            .HasIndex(d => d.DependencyCriticalityId);

        modelBuilder.Entity<Dependency>()
            .HasIndex(d => d.DependencyLinkTypeId);

        modelBuilder.Entity<Assumption>()
            .HasOne(a => a.Project)
            .WithMany(p => p.Assumptions)
            .HasForeignKey(a => a.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Assumption>()
            .HasIndex(a => new { a.ProjectId, a.IsDeleted });

        // ProjectProduct configuration
        modelBuilder.Entity<ProjectProduct>()
            .HasOne(pp => pp.Project)
            .WithMany(p => p.ProjectProducts)
            .HasForeignKey(pp => pp.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectProduct>()
            .HasIndex(pp => pp.ProjectId);

        modelBuilder.Entity<ProjectProduct>()
            .HasIndex(pp => pp.ProductDocumentId);

        modelBuilder.Entity<ProjectProduct>()
            .HasIndex(pp => pp.ProductFipsId);

        // ProjectDraft configuration
        modelBuilder.Entity<ProjectDraft>()
            .HasIndex(pd => pd.SessionId);

        modelBuilder.Entity<ProjectDraft>()
            .HasIndex(pd => pd.UserEmail);

        modelBuilder.Entity<ProjectDraft>()
            .HasIndex(pd => pd.IsConfirmed);

        modelBuilder.Entity<ProjectDraft>()
            .HasIndex(pd => pd.CreatedAt);

        // Configure decimal precision for FTE fields
        modelBuilder.Entity<ProjectDraft>()
            .Property(pd => pd.TotalPermFte)
            .HasPrecision(10, 2);

        modelBuilder.Entity<ProjectDraft>()
            .Property(pd => pd.TotalMspFte)
            .HasPrecision(10, 2);

        // FundingSource configuration
        modelBuilder.Entity<FundingSource>()
            .HasIndex(fs => fs.Code)
            .IsUnique();

        modelBuilder.Entity<FundingSource>()
            .HasIndex(fs => fs.IsActive);

        modelBuilder.Entity<FundingSource>()
            .HasIndex(fs => fs.SortOrder);

        // Update existing models to include Project relationships
        modelBuilder.Entity<Milestone>()
            .HasOne(m => m.Project)
            .WithMany(p => p.Milestones)
            .HasForeignKey(m => m.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Milestone>()
            .HasIndex(m => m.ProjectId);

        modelBuilder.Entity<Risk>()
            .HasOne(r => r.Project)
            .WithMany(p => p.Risks)
            .HasForeignKey(r => r.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Risk>()
            .HasIndex(r => r.ProjectId);

        modelBuilder.Entity<Issue>()
            .HasOne(i => i.Project)
            .WithMany(p => p.Issues)
            .HasForeignKey(i => i.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Issue>()
            .HasIndex(i => i.ProjectId);

        modelBuilder.Entity<Models.Action>()
            .HasOne(a => a.Project)
            .WithMany(p => p.Actions)
            .HasForeignKey(a => a.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Models.Action>()
            .HasIndex(a => a.ProjectId);

        // Update Objective to include Mission relationship
        modelBuilder.Entity<Objective>()
            .HasOne(o => o.Mission)
            .WithMany(m => m.Objectives)
            .HasForeignKey(o => o.MissionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Objective>()
            .HasIndex(o => o.MissionId);

        // Configure StaffRoleReturn
        modelBuilder.Entity<StaffRoleReturn>()
            .HasOne(srr => srr.User)
            .WithMany()
            .HasForeignKey(srr => srr.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<StaffRoleReturn>()
            .HasOne(srr => srr.GddRole)
            .WithMany(role => role.StaffRoleReturns)
            .HasForeignKey(srr => srr.GddRoleId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<StaffRoleReturn>()
            .HasIndex(srr => srr.UserId);

        modelBuilder.Entity<StaffRoleReturn>()
            .HasIndex(srr => new { srr.UserId, srr.Year })
            .IsUnique();

        modelBuilder.Entity<StaffRoleReturn>()
            .HasMany(srr => srr.SecondarySkills)
            .WithOne(srr => srr.StaffRoleReturn)
            .HasForeignKey(srr => srr.StaffRoleReturnId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure StaffRoleReturnSkill
        modelBuilder.Entity<StaffRoleReturnSkill>()
            .HasOne(srs => srs.Skill)
            .WithMany(s => s.StaffRoleReturns)
            .HasForeignKey(srs => srs.SkillId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<StaffRoleReturnSkill>()
            .HasIndex(srs => new { srs.StaffRoleReturnId, srs.SkillId })
            .IsUnique();

        // Configure GddRole
        modelBuilder.Entity<GddRole>()
            .HasIndex(role => new { role.RoleFamily, role.RoleName, role.RoleLevel })
            .IsUnique();

        // GddRole.Description needs to be unlimited (nvarchar(max)) for long descriptions from CSV
        modelBuilder.Entity<GddRole>()
            .Property(r => r.Description)
            .HasMaxLength(int.MaxValue) // Override default MaxLength(450)
            .HasColumnType("nvarchar(max)");

        // Configure Skill
        modelBuilder.Entity<Skill>()
            .HasIndex(s => s.SkillName)
            .IsUnique();

        // Skill.Description needs to be unlimited (nvarchar(max)) for long descriptions from CSV
        modelBuilder.Entity<Skill>()
            .Property(s => s.Description)
            .HasMaxLength(int.MaxValue) // Override default MaxLength(450)
            .HasColumnType("nvarchar(max)");

        // ========================================
        // ROLE-BASED ACCESS CONTROL CONFIGURATION
        // ========================================

        // Group configuration
        modelBuilder.Entity<Group>()
            .HasIndex(g => g.Name)
            .IsUnique();

        modelBuilder.Entity<Group>()
            .HasIndex(g => g.IsActive);

        modelBuilder.Entity<Group>()
            .HasIndex(g => g.IsSystemGroup);

        // Feature configuration
        modelBuilder.Entity<Feature>()
            .HasIndex(f => f.Code)
            .IsUnique();

        modelBuilder.Entity<Feature>()
            .HasIndex(f => f.Name);

        modelBuilder.Entity<Feature>()
            .HasIndex(f => f.IsActive);

        modelBuilder.Entity<Feature>()
            .Property(f => f.AccessMode)
            .HasConversion<int>();

        modelBuilder.Entity<Feature>()
            .HasMany(f => f.UserAllows)
            .WithOne(a => a.Feature)
            .HasForeignKey(a => a.FeatureId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<FeatureUserAllow>()
            .HasIndex(a => new { a.FeatureId, a.UserId })
            .IsUnique();

        modelBuilder.Entity<Feature>()
            .HasMany(f => f.GroupAllows)
            .WithOne(a => a.Feature)
            .HasForeignKey(a => a.FeatureId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<FeatureGroupAllow>()
            .HasIndex(a => new { a.FeatureId, a.GroupId })
            .IsUnique();

        modelBuilder.Entity<FeatureGroupAllow>()
            .HasOne(a => a.Group)
            .WithMany()
            .HasForeignKey(a => a.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // UserGroup configuration (many-to-many: User ↔ Group)
        modelBuilder.Entity<UserGroup>()
            .HasIndex(ug => new { ug.UserId, ug.GroupId })
            .IsUnique();

        modelBuilder.Entity<UserGroup>()
            .HasOne(ug => ug.User)
            .WithMany(u => u.UserGroups)
            .HasForeignKey(ug => ug.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // ProductDqReview configuration
        modelBuilder.Entity<ProductDqReview>()
            .Property(r => r.ChangesMade)
            .HasMaxLength(4000)
            .HasColumnType("nvarchar(4000)");
        modelBuilder.Entity<ProductDqReview>()
            .Property(r => r.ContactChangesJson)
            .HasColumnType("nvarchar(MAX)");

        modelBuilder.Entity<UserGroup>()
            .HasOne(ug => ug.Group)
            .WithMany(g => g.UserGroups)
            .HasForeignKey(ug => ug.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserGroup>()
            .HasIndex(ug => ug.UserId);

        modelBuilder.Entity<UserGroup>()
            .HasIndex(ug => ug.GroupId);

        // GroupFeaturePermission configuration (many-to-many: Group ↔ Feature with Permission)
        modelBuilder.Entity<GroupFeaturePermission>()
            .HasIndex(gfp => new { gfp.GroupId, gfp.FeatureId, gfp.Permission })
            .IsUnique();

        modelBuilder.Entity<GroupFeaturePermission>()
            .HasOne(gfp => gfp.Group)
            .WithMany(g => g.GroupFeaturePermissions)
            .HasForeignKey(gfp => gfp.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GroupFeaturePermission>()
            .HasOne(gfp => gfp.Feature)
            .WithMany(f => f.GroupFeaturePermissions)
            .HasForeignKey(gfp => gfp.FeatureId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GroupFeaturePermission>()
            .HasIndex(gfp => gfp.GroupId);

        modelBuilder.Entity<GroupFeaturePermission>()
            .HasIndex(gfp => gfp.FeatureId);

        modelBuilder.Entity<GroupFeaturePermission>()
            .HasIndex(gfp => gfp.Permission);

        // BUSINESS CASE CONFIGURATION
        // ========================================

        // BusinessCase configuration
        modelBuilder.Entity<BusinessCase>()
            .HasIndex(bc => bc.BusinessCaseId)
            .IsUnique();

        modelBuilder.Entity<BusinessCase>()
            .HasIndex(bc => bc.RequestorEmail);

        modelBuilder.Entity<BusinessCase>()
            .HasIndex(bc => bc.BusinessArea);

        // BusinessCaseDdtFeedback configuration
        modelBuilder.Entity<BusinessCaseDdtFeedback>()
            .HasOne(f => f.BusinessCase)
            .WithMany(bc => bc.DdtFeedbacks)
            .HasForeignKey(f => f.BusinessCaseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BusinessCaseDdtFeedback>()
            .HasIndex(f => f.BusinessCaseId);

        // BusinessCaseReviewer configuration
        modelBuilder.Entity<BusinessCaseReviewer>()
            .HasOne(r => r.BusinessCase)
            .WithMany(bc => bc.Reviewers)
            .HasForeignKey(r => r.BusinessCaseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BusinessCaseReviewer>()
            .HasIndex(r => r.BusinessCaseId);

        // BusinessCaseProject configuration
        modelBuilder.Entity<BusinessCaseProject>()
            .HasOne(bcp => bcp.BusinessCase)
            .WithMany(bc => bc.BusinessCaseProjects)
            .HasForeignKey(bcp => bcp.BusinessCaseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BusinessCaseProject>()
            .HasOne(bcp => bcp.Project)
            .WithMany()
            .HasForeignKey(bcp => bcp.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<BusinessCaseProject>()
            .HasIndex(bcp => new { bcp.BusinessCaseId, bcp.ProjectId })
            .IsUnique();

        // BusinessCaseProduct configuration
        modelBuilder.Entity<BusinessCaseProduct>()
            .HasOne(bcp => bcp.BusinessCase)
            .WithMany(bc => bc.BusinessCaseProducts)
            .HasForeignKey(bcp => bcp.BusinessCaseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BusinessCaseProduct>()
            .HasIndex(bcp => new { bcp.BusinessCaseId, bcp.ProductFipsId })
            .IsUnique();

        // DdatProfession configuration - ignore RoleGroup property until migration is created
        modelBuilder.Entity<DdatProfession>()
            .Ignore(d => d.RoleGroup);

        // ========================================
        // LEARNING & DEVELOPMENT (L&D) CONFIGURATION
        // ========================================

        // TrainingCourse configuration
        modelBuilder.Entity<TrainingCourse>()
            .HasIndex(tc => tc.Active);

        modelBuilder.Entity<TrainingCourse>()
            .HasIndex(tc => tc.Title);

        modelBuilder.Entity<TrainingCourse>()
            .HasIndex(tc => tc.Provider);

        // TrainingRecord configuration
        modelBuilder.Entity<TrainingRecord>()
            .HasOne(tr => tr.User)
            .WithMany()
            .HasForeignKey(tr => tr.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TrainingRecord>()
            .HasOne(tr => tr.Course)
            .WithMany(tc => tc.TrainingRecords)
            .HasForeignKey(tr => tr.CourseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TrainingRecord>()
            .HasIndex(tr => tr.UserId);

        modelBuilder.Entity<TrainingRecord>()
            .HasIndex(tr => tr.CourseId);

        modelBuilder.Entity<TrainingRecord>()
            .HasIndex(tr => tr.Status);

        modelBuilder.Entity<TrainingRecord>()
            .HasIndex(tr => tr.DateAttended);

        // TrainingRequest configuration
        modelBuilder.Entity<TrainingRequest>()
            .HasOne(tr => tr.User)
            .WithMany()
            .HasForeignKey(tr => tr.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TrainingRequest>()
            .HasOne(tr => tr.Course)
            .WithMany(tc => tc.TrainingRequests)
            .HasForeignKey(tr => tr.CourseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TrainingRequest>()
            .HasOne(tr => tr.Decision)
            .WithMany()
            .HasForeignKey(tr => tr.DecisionId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<TrainingRequest>()
            .HasIndex(tr => tr.UserId);

        modelBuilder.Entity<TrainingRequest>()
            .HasIndex(tr => tr.CourseId);

        modelBuilder.Entity<TrainingRequest>()
            .HasIndex(tr => tr.Status);

        modelBuilder.Entity<TrainingRequest>()
            .HasIndex(tr => tr.CreatedAt);

        // UserProfessionalProfile configuration
        modelBuilder.Entity<UserProfessionalProfile>()
            .HasOne(upp => upp.User)
            .WithMany()
            .HasForeignKey(upp => upp.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserProfessionalProfile>()
            .HasOne(upp => upp.DdatProfession)
            .WithMany()
            .HasForeignKey(upp => upp.DdatProfessionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<UserProfessionalProfile>()
            .HasOne(upp => upp.DdatFrameworkRole)
            .WithMany()
            .HasForeignKey(upp => upp.DdatFrameworkRoleId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<UserProfessionalProfile>()
            .HasIndex(upp => upp.UserId)
            .IsUnique();

        modelBuilder.Entity<UserProfessionalProfile>()
            .HasIndex(upp => upp.Profession);

        modelBuilder.Entity<UserProfessionalProfile>()
            .HasIndex(upp => upp.DdatProfessionId);

        modelBuilder.Entity<UserProfessionalProfile>()
            .HasMany(upp => upp.UserSkills)
            .WithOne(ups => ups.UserProfessionalProfile)
            .HasForeignKey(ups => ups.UserProfessionalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        // UserProfessionalProfileSkill configuration
        modelBuilder.Entity<UserProfessionalProfileSkill>()
            .HasOne(ups => ups.Skill)
            .WithMany()
            .HasForeignKey(ups => ups.SkillId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<UserProfessionalProfileSkill>()
            .HasIndex(ups => new { ups.UserProfessionalProfileId, ups.SkillId })
            .IsUnique();

        // HOPS configuration
        modelBuilder.Entity<HOPS>()
            .HasOne(h => h.User)
            .WithMany()
            .HasForeignKey(h => h.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<HOPS>()
            .HasOne(h => h.DdatProfession)
            .WithMany()
            .HasForeignKey(h => h.DdatProfessionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<HOPS>()
            .HasIndex(h => h.UserId);

        modelBuilder.Entity<HOPS>()
            .HasIndex(h => h.DdatProfessionId);

        modelBuilder.Entity<HOPS>()
            .HasIndex(h => new { h.UserId, h.DdatProfessionId })
            .IsUnique();

        // ProfessionSkill configuration (many-to-many: DdatProfession ↔ Skill)
        modelBuilder.Entity<ProfessionSkill>()
            .HasIndex(ps => new { ps.DdatProfessionId, ps.SkillId })
            .IsUnique();

        modelBuilder.Entity<ProfessionSkill>()
            .HasOne(ps => ps.DdatProfession)
            .WithMany(dp => dp.ProfessionSkills)
            .HasForeignKey(ps => ps.DdatProfessionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProfessionSkill>()
            .HasOne(ps => ps.Skill)
            .WithMany(s => s.ProfessionSkills)
            .HasForeignKey(ps => ps.SkillId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ProfessionSkill>()
            .HasIndex(ps => ps.DdatProfessionId);

        modelBuilder.Entity<ProfessionSkill>()
            .HasIndex(ps => ps.SkillId);

        // CapabilityGap configuration
        modelBuilder.Entity<CapabilityGap>()
            .HasOne(cg => cg.UserProfessionalProfile)
            .WithMany(upp => upp.CapabilityGaps)
            .HasForeignKey(cg => cg.UserProfessionalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CapabilityGap>()
            .HasOne(cg => cg.Action)
            .WithMany()
            .HasForeignKey(cg => cg.ActionId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<CapabilityGap>()
            .HasIndex(cg => cg.UserProfessionalProfileId);

        modelBuilder.Entity<HOPS>()
            .HasIndex(h => new { h.UserId, h.DdatProfessionId })
            .IsUnique();

        // TrainingNudge configuration
        modelBuilder.Entity<TrainingNudge>()
            .HasOne(tn => tn.User)
            .WithMany()
            .HasForeignKey(tn => tn.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TrainingNudge>()
            .HasOne(tn => tn.Course)
            .WithMany()
            .HasForeignKey(tn => tn.CourseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TrainingNudge>()
            .HasIndex(tn => tn.UserId);

        modelBuilder.Entity<TrainingNudge>()
            .HasIndex(tn => new { tn.UserId, tn.IsActive });

        // LearningBudget configuration
        modelBuilder.Entity<LearningBudget>()
            .HasIndex(lb => new { lb.FinancialYear, lb.IsActive })
            .IsUnique()
            .HasFilter("[IsActive] = 1"); // Only one active budget per FY

        modelBuilder.Entity<LearningBudget>()
            .HasIndex(lb => lb.FinancialYear);

        // DDAT Framework Version configuration
        modelBuilder.Entity<DdatFrameworkVersion>()
            .HasIndex(fv => fv.VersionIdentifier)
            .IsUnique();

        modelBuilder.Entity<DdatFrameworkVersion>()
            .HasIndex(fv => fv.IsActive);

        // DDAT Framework Skill configuration
        modelBuilder.Entity<DdatFrameworkSkill>()
            .HasOne(s => s.FrameworkVersion)
            .WithMany(v => v.Skills)
            .HasForeignKey(s => s.FrameworkVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DdatFrameworkSkill>()
            .HasIndex(s => new { s.SkillName, s.FrameworkVersionId });

        modelBuilder.Entity<DdatFrameworkSkill>()
            .HasIndex(s => s.IsArchived);

        modelBuilder.Entity<DdatFrameworkSkillGradeMapping>()
            .HasOne(m => m.DdatFrameworkSkill)
            .WithMany(s => s.GradeMappings)
            .HasForeignKey(m => m.DdatFrameworkSkillId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DdatFrameworkSkillGradeMapping>()
            .HasIndex(m => new { m.DdatFrameworkSkillId, m.CapabilityLevel, m.Grade })
            .IsUnique();

        // DDAT Framework Role configuration
        modelBuilder.Entity<DdatFrameworkRole>()
            .HasOne(r => r.FrameworkVersion)
            .WithMany(v => v.Roles)
            .HasForeignKey(r => r.FrameworkVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DdatFrameworkRole>()
            .HasIndex(r => new { r.Role, r.RoleLevel, r.FrameworkVersionId });

        modelBuilder.Entity<DdatFrameworkRole>()
            .HasIndex(r => r.IsArchived);

        modelBuilder.Entity<DdatFrameworkRoleSkill>()
            .HasOne(rs => rs.DdatFrameworkRole)
            .WithMany(r => r.RoleSkills)
            .HasForeignKey(rs => rs.DdatFrameworkRoleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DdatFrameworkRoleSkill>()
            .HasIndex(rs => new { rs.DdatFrameworkRoleId, rs.SkillName, rs.SkillLevel });

        // DDAT Framework Change Note configuration
        modelBuilder.Entity<DdatFrameworkChangeNote>()
            .HasOne(cn => cn.FrameworkVersion)
            .WithMany(v => v.ChangeNotes)
            .HasForeignKey(cn => cn.FrameworkVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DdatFrameworkChangeNote>()
            .HasIndex(cn => cn.Timestamp);

        // UserDdatFrameworkSkill configuration
        modelBuilder.Entity<UserDdatFrameworkSkill>()
            .HasOne(udfs => udfs.UserProfessionalProfile)
            .WithMany(upp => upp.AdditionalDdatFrameworkSkills)
            .HasForeignKey(udfs => udfs.UserProfessionalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserDdatFrameworkSkill>()
            .HasOne(udfs => udfs.DdatFrameworkSkill)
            .WithMany()
            .HasForeignKey(udfs => udfs.DdatFrameworkSkillId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<UserDdatFrameworkSkill>()
            .HasIndex(udfs => new { udfs.UserProfessionalProfileId, udfs.DdatFrameworkSkillId })
            .IsUnique();

        // Grade configuration
        modelBuilder.Entity<Grade>()
            .HasIndex(g => g.Code)
            .IsUnique();

        modelBuilder.Entity<Grade>()
            .HasIndex(g => g.IsActive);

        modelBuilder.Entity<Grade>()
            .HasIndex(g => g.DisplayOrder);

        // FIPS Sync History configuration
        modelBuilder.Entity<FipsSyncHistory>()
            .HasIndex(fsh => fsh.StartedAt);

        modelBuilder.Entity<FipsSyncHistory>()
            .HasIndex(fsh => fsh.Status);

        modelBuilder.Entity<FipsSyncHistory>()
            .HasIndex(fsh => fsh.TargetEnvironment);

        modelBuilder.Entity<FipsSyncHistory>()
            .HasIndex(fsh => fsh.SyncType);

        // ========================================
        // DEMAND TRIAGE (spec-aligned v3)
        // ========================================

        modelBuilder.Entity<DemandTriageRequest>()
            .HasIndex(r => r.RequestReference).IsUnique();
        modelBuilder.Entity<DemandTriageRequest>()
            .HasIndex(r => r.Status);
        modelBuilder.Entity<DemandTriageRequest>()
            .HasIndex(r => r.OwnerUserEmail);
        modelBuilder.Entity<DemandTriageRequest>()
            .HasIndex(r => r.SubmittedAt);
        modelBuilder.Entity<DemandTriageRequest>()
            .HasIndex(r => r.DeletedAt);

        modelBuilder.Entity<DemandTriageRequest>()
            .HasOne(r => r.BusinessCase)
            .WithMany()
            .HasForeignKey(r => r.BusinessCaseId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<DemandTriageRequest>()
            .HasOne(r => r.ConvertedProject)
            .WithMany()
            .HasForeignKey(r => r.ConvertedProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DemandExploratoryReview>()
            .HasOne(e => e.DemandTriageRequest)
            .WithOne(r => r.ExploratoryReview)
            .HasForeignKey<DemandExploratoryReview>(e => e.DemandTriageRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DemandScorecard>()
            .HasOne(s => s.DemandTriageRequest)
            .WithOne(r => r.Scorecard)
            .HasForeignKey<DemandScorecard>(s => s.DemandTriageRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DemandAnswer>()
            .HasOne(a => a.Scorecard)
            .WithMany(s => s.Answers)
            .HasForeignKey(a => a.DemandScorecardId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DemandAnswer>()
            .HasIndex(a => new { a.DemandScorecardId, a.QuestionCode });

        modelBuilder.Entity<DemandTriageOutcome>()
            .HasOne(o => o.DemandTriageRequest)
            .WithOne(r => r.TriageOutcome)
            .HasForeignKey<DemandTriageOutcome>(o => o.DemandTriageRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DemandTriageAuditEvent>()
            .HasOne(e => e.DemandTriageRequest)
            .WithMany(r => r.AuditEvents)
            .HasForeignKey(e => e.DemandTriageRequestId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DemandTriageAuditEvent>()
            .HasIndex(e => e.DemandTriageRequestId);
        modelBuilder.Entity<DemandTriageAuditEvent>()
            .HasIndex(e => e.OccurredAt);

        // Demand pipeline (Compass2)
        modelBuilder.Entity<DemandPipelineRequest>(e =>
        {
            e.HasIndex(x => x.Reference).IsUnique();
            e.HasIndex(x => x.BusinessCaseId);
            e.HasIndex(x => x.Status);
            e.Property(x => x.Description).HasMaxLength(-1);
            e.Property(x => x.PointsOfContact).HasMaxLength(-1);
            e.Property(x => x.PreviousResearch).HasMaxLength(-1);
            e.Property(x => x.ManifestoCommitment).HasMaxLength(-1);
            e.Property(x => x.ExpectedBenefits).HasMaxLength(-1);
            e.Property(x => x.RiskIfNotDelivered).HasMaxLength(-1);
            e.Property(x => x.PriorityOutcomeIds).HasMaxLength(500);
            e.Property(x => x.MissionPillarIds).HasMaxLength(500);
            e.Property(x => x.FundingProvidedDetails).HasMaxLength(-1);
            e.Property(x => x.HeadcountProvidedDetails).HasMaxLength(-1);
            e.Property(x => x.DigitalServiceChangeDetails).HasMaxLength(-1);
            e.Property(x => x.ExploreNotes).HasMaxLength(-1);
            e.Property(x => x.ExploreRelatedLinksJson).HasMaxLength(-1);
            e.Property(x => x.ExploreLinksToExistingWork).HasMaxLength(-1);
            e.Property(x => x.ExploreResearchAndInsights).HasMaxLength(-1);
            e.Property(x => x.ExploreAimClarification).HasMaxLength(-1);
            e.Property(x => x.ExplorePolicies).HasMaxLength(-1);
            e.Property(x => x.ExploreUserGroups).HasMaxLength(-1);
            e.Property(x => x.ScoringAssessmentNotes).HasMaxLength(-1);
            e.Property(x => x.ScoringConcernsNotes).HasMaxLength(-1);
            e.Property(x => x.ScoringAnswersJson).HasMaxLength(-1);
            e.Property(x => x.TriageOutcomeNarrative).HasMaxLength(-1);
            e.HasIndex(x => x.TriageMeetingId);
            e.HasIndex(x => x.TriageCreatedProjectId);
            e.HasOne<DemandPipelineTriageMeeting>()
                .WithMany()
                .HasForeignKey(x => x.TriageMeetingId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Project>()
                .WithMany()
                .HasForeignKey(x => x.TriageCreatedProjectId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.TriageStageLookupId);
            e.HasIndex(x => x.TriageAssignedBusinessAreaId);
            e.HasIndex(x => x.TriagePrimaryContactUserId);
            e.HasOne<DemandTriageOutcomeStage>()
                .WithMany()
                .HasForeignKey(x => x.TriageStageLookupId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne<BusinessAreaLookup>()
                .WithMany()
                .HasForeignKey(x => x.TriageAssignedBusinessAreaId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.TriagePrimaryContactUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UniversalBarrierLookup>(e =>
        {
            e.HasIndex(x => x.SortOrder);
            e.Property(x => x.Description).HasMaxLength(-1);
        });

        modelBuilder.Entity<DemandPipelineRequestUniversalBarrier>(e =>
        {
            e.HasKey(x => new { x.DemandPipelineRequestId, x.UniversalBarrierLookupId });
            e.HasOne(x => x.DemandPipelineRequest)
                .WithMany()
                .HasForeignKey(x => x.DemandPipelineRequestId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.UniversalBarrierLookup)
                .WithMany()
                .HasForeignKey(x => x.UniversalBarrierLookupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DemandPipelineRiskIssue>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DemandPipelineRequestId);
            e.Property(x => x.Details).HasMaxLength(-1);
            e.Property(x => x.Description).HasMaxLength(-1);
            e.Property(x => x.ImpactOnDelivery).HasMaxLength(-1);
            e.Property(x => x.MitigationOrAction).HasMaxLength(-1);
            e.HasOne(x => x.DemandPipelineRequest)
                .WithMany()
                .HasForeignKey(x => x.DemandPipelineRequestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DemandPipelineBusinessCase>(e =>
        {
            e.HasIndex(x => x.Reference).IsUnique();
            e.Property(x => x.ProblemStatement).HasMaxLength(-1);
            e.Property(x => x.ProposedSolution).HasMaxLength(-1);
            e.Property(x => x.Evidence).HasMaxLength(-1);
            e.Property(x => x.Benefits).HasMaxLength(-1);
            e.Property(x => x.StatutoryDriverComments).HasMaxLength(-1);
            e.Property(x => x.StatutoryReference).HasMaxLength(-1);
            e.Property(x => x.FundingComments).HasMaxLength(-1);
            e.Property(x => x.LinkedWorkAndDemands).HasMaxLength(-1);
        });

        modelBuilder.Entity<DemandPipelineStage>(e =>
        {
            e.HasIndex(x => x.DisplayOrder);
        });

        modelBuilder.Entity<DemandPipelineTriageMeeting>(e =>
        {
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.MeetingDate);
            e.Property(x => x.Notes).HasMaxLength(-1);
            e.Property(x => x.CopilotSummaryNotes).HasMaxLength(-1);
            e.Property(x => x.AgendaJson).HasMaxLength(-1);
            e.Property(x => x.Attendees).HasMaxLength(-1);
        });

        modelBuilder.Entity<DemandScoringFrameworkSection>(e =>
        {
            e.HasIndex(x => x.Key);
            e.HasMany(x => x.Questions)
                .WithOne(q => q.Section)
                .HasForeignKey(q => q.SectionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Description).HasMaxLength(-1);
        });

        modelBuilder.Entity<DemandScoringFrameworkQuestion>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasMany(x => x.Options)
                .WithOne(o => o.Question)
                .HasForeignKey(o => o.QuestionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Hint).HasMaxLength(-1);
        });

        modelBuilder.Entity<DemandScoringFrameworkOption>(e =>
        {
            e.HasIndex(x => new { x.QuestionId, x.SortOrder });
        });

        modelBuilder.Entity<DemandScoringBandDefinition>(e =>
        {
            e.HasIndex(x => x.SortOrder);
        });

        modelBuilder.Entity<WorkReportingCycle>(e =>
        {
            e.ToTable("ReportingCycles");
            e.HasIndex(x => x.Code).IsUnique();
        });
        modelBuilder.Entity<WorkReportingCyclePeriod>(e =>
        {
            e.ToTable("ReportingCyclePeriods");
            e.HasOne(p => p.ReportingCycle)
                .WithMany(c => c.Periods)
                .HasForeignKey(p => p.ReportingCycleId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.ReportingCycleId, p.PeriodKey }).IsUnique();
        });

        modelBuilder.Entity<WeeklyWorkReportingConfig>(e =>
        {
            e.HasData(new WeeklyWorkReportingConfig
            {
                Id = 1,
                PeriodStartDayOfWeek = DayOfWeek.Monday,
                PeriodEndDayOfWeek = DayOfWeek.Friday,
                DueDayOfWeek = DayOfWeek.Friday,
                DueWeekOffset = WeeklyWorkReportingDueWeekOffset.SameWeek,
                FirstReportingPeriodStart = new DateTime(2026, 6, 29, 0, 0, 0, DateTimeKind.Utc),
                IsActive = true,
                UpdatedAt = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc)
            });
        });

        modelBuilder.Entity<WeeklyWorkReportingScopeProject>(e =>
        {
            e.HasIndex(x => x.ProjectId).IsUnique();
            e.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectWeeklyWorkUpdate>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.IsoYear, x.IsoWeek }).IsUnique();
            e.HasOne(x => x.Project)
                .WithMany(p => p.WeeklyWorkUpdates)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            ConfigureRaidNarrativeColumns(e.Property(x => x.Narrative));
            ConfigureRaidNarrativeColumns(e.Property(x => x.PeopleNarrative));
            ConfigureRaidNarrativeColumns(e.Property(x => x.DraftRagJustification));
            ConfigureRaidNarrativeColumns(e.Property(x => x.DraftPathToGreen));
        });

        // ----- FIPS CMDB products -----
        modelBuilder.Entity<CMDBProduct>(e =>
        {
            e.HasIndex(x => x.UniqueID).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.UniqueID).ValueGeneratedOnAdd();
            e.Property(x => x.CMDBDescription).HasColumnType("nvarchar(max)");
            e.Property(x => x.UserDescription).HasColumnType("nvarchar(max)");
            e.Property(x => x.LastCmdbSnapshotJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.OtherDepartments).HasColumnType("nvarchar(max)");
            e.HasOne(x => x.Phase).WithMany().HasForeignKey(x => x.PhaseId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.RiskAppetiteLookup).WithMany().HasForeignKey(x => x.RiskAppetiteLookupId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<FipsCmdbSyncRule>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.FieldScope).HasMaxLength(40);
            e.Property(x => x.MatchKind).HasMaxLength(20);
            e.Property(x => x.Pattern).HasMaxLength(2000);
            e.HasIndex(x => new { x.IsActive, x.SortOrder });
        });

        modelBuilder.Entity<FipsBusinessArea>(e =>
        {
            e.HasIndex(x => x.BusinessAreaLookupId)
                .IsUnique()
                .HasFilter("[BusinessAreaLookupId] IS NOT NULL");
            e.HasOne(x => x.BusinessAreaLookup)
                .WithMany()
                .HasForeignKey(x => x.BusinessAreaLookupId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CMDBProductBusinessArea>(e =>
        {
            e.HasOne(x => x.CMDBProduct).WithMany(p => p.BusinessAreas).HasForeignKey(x => x.CMDBProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FipsBusinessArea).WithMany().HasForeignKey(x => x.FipsBusinessAreaId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FipsDirectorate>(e =>
        {
            e.HasIndex(x => x.DirectorateLookupId)
                .IsUnique()
                .HasFilter("[DirectorateLookupId] IS NOT NULL");
            e.HasOne(x => x.DirectorateLookup)
                .WithMany()
                .HasForeignKey(x => x.DirectorateLookupId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CMDBProductDirectorate>(e =>
        {
            e.HasOne(x => x.CMDBProduct).WithMany(p => p.Directorates).HasForeignKey(x => x.CMDBProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FipsDirectorate).WithMany().HasForeignKey(x => x.FipsDirectorateId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CMDBProductChannel>(e =>
        {
            e.HasOne(x => x.CMDBProduct).WithMany(p => p.Channels).HasForeignKey(x => x.CMDBProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FipsChannel).WithMany().HasForeignKey(x => x.FipsChannelId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CMDBProductUserGroup>(e =>
        {
            e.HasOne(x => x.CMDBProduct).WithMany(p => p.UserGroups).HasForeignKey(x => x.CMDBProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FipsUserGroup).WithMany().HasForeignKey(x => x.FipsUserGroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CMDBProductType>(e =>
        {
            e.HasOne(x => x.CMDBProduct).WithMany(p => p.Types).HasForeignKey(x => x.CMDBProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FipsType).WithMany().HasForeignKey(x => x.FipsTypeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CMDBProductFipsCategorisationItem>(e =>
        {
            e.HasOne(x => x.CMDBProduct).WithMany(p => p.CategorisationItems).HasForeignKey(x => x.CMDBProductId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FipsCategorisationItem).WithMany().HasForeignKey(x => x.FipsCategorisationItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FipsCategorisationItem>(e =>
        {
            e.HasOne(x => x.Group).WithMany(g => g.Items).HasForeignKey(x => x.FipsCategorisationGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CMDBProductContact>(e =>
        {
            e.HasOne(x => x.CMDBProduct).WithMany(p => p.Contacts).HasForeignKey(x => x.CMDBProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FipsContactRole).WithMany().HasForeignKey(x => x.FipsContactRoleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CMDBProductObjective>(e =>
        {
            e.HasOne(x => x.CMDBProduct).WithMany(p => p.Objectives).HasForeignKey(x => x.CMDBProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Objective).WithMany().HasForeignKey(x => x.ObjectiveId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.CMDBProductId, x.ObjectiveId }).IsUnique();
        });

        modelBuilder.Entity<CMDBProductMission>(e =>
        {
            e.HasOne(x => x.CMDBProduct).WithMany(p => p.Missions).HasForeignKey(x => x.CMDBProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Mission).WithMany().HasForeignKey(x => x.MissionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.CMDBProductId, x.MissionId }).IsUnique();
        });

        modelBuilder.Entity<CMDBProductWorkItemTag>(e =>
        {
            e.HasKey(x => new { x.CMDBProductId, x.WorkItemTagLookupId });
            e.HasOne(x => x.CMDBProduct)
                .WithMany(p => p.WorkItemTags)
                .HasForeignKey(x => x.CMDBProductId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.WorkItemTagLookup)
                .WithMany()
                .HasForeignKey(x => x.WorkItemTagLookupId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FipsUserGroup>(e =>
        {
            e.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FipsUserGroupSynonym>(e =>
        {
            e.HasOne(x => x.FipsUserGroup).WithMany(g => g.Synonyms).HasForeignKey(x => x.FipsUserGroupId).OnDelete(DeleteBehavior.Cascade);
        });

        // ----- Service lines (portfolio groupings) -----
        modelBuilder.Entity<ServiceLine>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(200);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Description).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<ServiceLineDivision>(e =>
        {
            e.HasKey(x => new { x.ServiceLineId, x.DivisionId });
            e.HasOne(x => x.ServiceLine)
                .WithMany(s => s.ServiceLineDivisions)
                .HasForeignKey(x => x.ServiceLineId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Division)
                .WithMany()
                .HasForeignKey(x => x.DivisionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServiceLineBusinessArea>(e =>
        {
            e.HasKey(x => new { x.ServiceLineId, x.BusinessAreaLookupId });
            e.HasOne(x => x.ServiceLine)
                .WithMany(s => s.ServiceLineBusinessAreas)
                .HasForeignKey(x => x.ServiceLineId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.BusinessAreaLookup)
                .WithMany()
                .HasForeignKey(x => x.BusinessAreaLookupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServiceLineProduct>(e =>
        {
            e.HasKey(x => new { x.ServiceLineId, x.CMDBProductId });
            e.HasOne(x => x.ServiceLine)
                .WithMany(s => s.ServiceLineProducts)
                .HasForeignKey(x => x.ServiceLineId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CMDBProduct)
                .WithMany()
                .HasForeignKey(x => x.CMDBProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServiceLineProject>(e =>
        {
            e.HasKey(x => new { x.ServiceLineId, x.ProjectId });
            e.HasOne(x => x.ServiceLine)
                .WithMany(s => s.ServiceLineProjects)
                .HasForeignKey(x => x.ServiceLineId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ----- Design Decision Records (DDR) -----
        // ddr.md §2 mandates the `ddr_` prefix on every DDR table.
        modelBuilder.Entity<Compass.Models.Ddr.DesignDecisionRecord>(e =>
        {
            e.ToTable("ddr_record");
            e.HasIndex(x => x.Reference).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Category);
            e.HasIndex(x => x.DeviationFlag);
            e.HasIndex(x => x.ReviewDate);
            e.HasIndex(x => x.CreatedAt);
            // Long-form text columns must allow more than the project's default string length (450).
            e.Property(x => x.ContextProblemStatement).HasColumnType("nvarchar(max)");
            e.Property(x => x.Decision).HasColumnType("nvarchar(max)");
            e.Property(x => x.Rationale).HasColumnType("nvarchar(max)");
            e.Property(x => x.ConsequencesTradeoffs).HasColumnType("nvarchar(max)");
            e.Property(x => x.DeviationDetails).HasColumnType("nvarchar(max)");
            e.Property(x => x.RetrospectiveContext).HasColumnType("nvarchar(max)");
            e.Property(x => x.CurrentValidityRationale).HasColumnType("nvarchar(max)");
            e.Property(x => x.MessageToDesignOps).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrAlternative>(e =>
        {
            e.ToTable("ddr_alternative");
            e.HasIndex(x => x.DesignDecisionRecordId);
            e.Property(x => x.AlternativeText).HasColumnType("nvarchar(max)");
            e.Property(x => x.Outcome).HasColumnType("nvarchar(max)");
            e.HasOne(x => x.DesignDecisionRecord)
                .WithMany(r => r.Alternatives)
                .HasForeignKey(x => x.DesignDecisionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrEvidence>(e =>
        {
            e.ToTable("ddr_evidence");
            e.HasIndex(x => x.DesignDecisionRecordId);
            e.Property(x => x.EvidenceSummary).HasColumnType("nvarchar(max)");
            e.HasOne(x => x.DesignDecisionRecord)
                .WithMany(r => r.Evidence)
                .HasForeignKey(x => x.DesignDecisionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrProductLink>(e =>
        {
            e.ToTable("ddr_record_product_link");
            e.HasIndex(x => new { x.DesignDecisionRecordId, x.FipsProductId }).IsUnique();
            e.HasIndex(x => x.FipsProductId);
            e.HasOne(x => x.DesignDecisionRecord)
                .WithMany(r => r.ProductLinks)
                .HasForeignKey(x => x.DesignDecisionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrWorkItemLink>(e =>
        {
            e.ToTable("ddr_record_work_item_link");
            e.HasIndex(x => new { x.DesignDecisionRecordId, x.WorkItemId }).IsUnique();
            e.HasIndex(x => x.WorkItemId);
            e.HasOne(x => x.DesignDecisionRecord)
                .WithMany(r => r.WorkItemLinks)
                .HasForeignKey(x => x.DesignDecisionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrStandardLink>(e =>
        {
            e.ToTable("ddr_standard_link");
            e.HasIndex(x => x.DesignDecisionRecordId);
            e.HasOne(x => x.DesignDecisionRecord)
                .WithMany(r => r.StandardLinks)
                .HasForeignKey(x => x.DesignDecisionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrComponentPatternLink>(e =>
        {
            e.ToTable("ddr_component_pattern_link");
            e.HasIndex(x => x.DesignDecisionRecordId);
            e.HasOne(x => x.DesignDecisionRecord)
                .WithMany(r => r.ComponentPatternLinks)
                .HasForeignKey(x => x.DesignDecisionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrRelatedRecord>(e =>
        {
            e.ToTable("ddr_related_record");
            e.HasIndex(x => x.DesignDecisionRecordId);
            e.HasIndex(x => x.RelatedDesignDecisionRecordId);
            e.HasOne(x => x.DesignDecisionRecord)
                .WithMany(r => r.RelatedRecords)
                .HasForeignKey(x => x.DesignDecisionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrComment>(e =>
        {
            e.ToTable("ddr_comment");
            e.HasIndex(x => x.DesignDecisionRecordId);
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.CommentText).HasColumnType("nvarchar(max)");
            e.HasOne(x => x.DesignDecisionRecord)
                .WithMany(r => r.Comments)
                .HasForeignKey(x => x.DesignDecisionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrInsightClassification>(e =>
        {
            e.ToTable("ddr_insight_classification");
            e.HasIndex(x => x.DesignDecisionRecordId);
            e.HasIndex(x => x.Classification);
            e.HasOne(x => x.DesignDecisionRecord)
                .WithMany(r => r.InsightClassifications)
                .HasForeignKey(x => x.DesignDecisionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrRecommendedFollowUp>(e =>
        {
            e.ToTable("ddr_recommended_follow_up");
            e.HasIndex(x => x.DesignDecisionRecordId);
            e.HasIndex(x => x.Status);
            e.Property(x => x.Notes).HasColumnType("nvarchar(max)");
            e.HasOne(x => x.DesignDecisionRecord)
                .WithMany(r => r.RecommendedFollowUps)
                .HasForeignKey(x => x.DesignDecisionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrGitHubIssueLink>(e =>
        {
            e.ToTable("ddr_github_issue_link");
            e.HasIndex(x => x.DesignDecisionRecordId);
            e.HasOne(x => x.DesignDecisionRecord)
                .WithMany(r => r.GitHubIssueLinks)
                .HasForeignKey(x => x.DesignDecisionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrAuditEvent>(e =>
        {
            e.ToTable("ddr_audit_event");
            e.HasIndex(x => x.DesignDecisionRecordId);
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.PreviousValue).HasColumnType("nvarchar(max)");
            e.Property(x => x.NewValue).HasColumnType("nvarchar(max)");
            e.HasOne(x => x.DesignDecisionRecord)
                .WithMany(r => r.AuditEvents)
                .HasForeignKey(x => x.DesignDecisionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Compass.Models.Ddr.DdrFeatureSetting>(e =>
        {
            e.ToTable("ddr_feature_setting");
            e.HasIndex(x => x.SettingKey);
            e.HasIndex(x => x.UpdatedAt);
            e.Property(x => x.Reason).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<Comment>(e =>
        {
            ConfigureRaidNarrativeColumns(e.Property(c => c.CommentText));
        });

    }

    private const int RaidNarrativeMaxLength = 4000;

    private static void ConfigureRaidNarrativeColumns<T>(PropertyBuilder<T> property) =>
        property.HasMaxLength(RaidNarrativeMaxLength).HasColumnType("nvarchar(4000)");

    private static void ConfigureRaidRiskNarrativeColumns(EntityTypeBuilder<Risk> e)
    {
        e.Property(r => r.Title).HasMaxLength(200).HasColumnType("nvarchar(200)");
        ConfigureRaidNarrativeColumns(e.Property(r => r.Description));
        ConfigureRaidNarrativeColumns(e.Property(r => r.Cause));
        ConfigureRaidNarrativeColumns(e.Property(r => r.ImpactIfRealised));
        ConfigureRaidNarrativeColumns(e.Property(r => r.Contingency));
        ConfigureRaidNarrativeColumns(e.Property(r => r.Assurance));
        ConfigureRaidNarrativeColumns(e.Property(r => r.FinancialImpact));
        ConfigureRaidNarrativeColumns(e.Property(r => r.Notes));
        ConfigureRaidNarrativeColumns(e.Property(r => r.ResponseStrategy));
        ConfigureRaidNarrativeColumns(e.Property(r => r.HowIdentified));
        e.Property(r => r.Status).HasMaxLength(20).HasColumnType("nvarchar(20)");
        e.Property(r => r.Response).HasMaxLength(20).HasColumnType("nvarchar(20)");
    }

    private static void ConfigureRaidIssueNarrativeColumns(EntityTypeBuilder<Issue> e)
    {
        e.Property(i => i.Title).HasMaxLength(200).HasColumnType("nvarchar(200)");
        ConfigureRaidNarrativeColumns(e.Property(i => i.Description));
        ConfigureRaidNarrativeColumns(e.Property(i => i.Workaround));
        ConfigureRaidNarrativeColumns(e.Property(i => i.ResolutionSummary));
        ConfigureRaidNarrativeColumns(e.Property(i => i.UserImpactSummary));
        ConfigureRaidNarrativeColumns(e.Property(i => i.ServiceImpactSummary));
        ConfigureRaidNarrativeColumns(e.Property(i => i.DetailedCause));
        ConfigureRaidNarrativeColumns(e.Property(i => i.AssuranceArrangements));
        e.Property(i => i.Severity).HasMaxLength(10).HasColumnType("nvarchar(10)");
        e.Property(i => i.Priority).HasMaxLength(10).HasColumnType("nvarchar(10)");
        e.Property(i => i.Status).HasMaxLength(20).HasColumnType("nvarchar(20)");
    }

    private static void ConfigureMonthlyReportTextColumns(EntityTypeBuilder<ProjectMonthlyUpdate> e)
    {
        ConfigureRaidNarrativeColumns(e.Property(x => x.Narrative));
        ConfigureRaidNarrativeColumns(e.Property(x => x.PeopleNarrative));
        ConfigureRaidNarrativeColumns(e.Property(x => x.DraftRagJustification));
        ConfigureRaidNarrativeColumns(e.Property(x => x.DraftPathToGreen));
    }

    private static void ConfigureMonthlyReportTextColumns(EntityTypeBuilder<MonthlyUpdateNarrative> e) =>
        ConfigureRaidNarrativeColumns(e.Property(x => x.Narrative));

    private static void ConfigureMonthlyReportTextColumns(EntityTypeBuilder<Project> e)
    {
        ConfigureRaidNarrativeColumns(e.Property(x => x.RagJustification));
        ConfigureRaidNarrativeColumns(e.Property(x => x.PathToGreen));
    }

    private static void ConfigureMonthlyReportTextColumns(EntityTypeBuilder<ProjectRagHistory> e)
    {
        ConfigureRaidNarrativeColumns(e.Property(x => x.Justification));
        ConfigureRaidNarrativeColumns(e.Property(x => x.PathToGreen));
    }
}

file sealed class NullAuditContextProvider : IAuditContextProvider
{
    public string? UserId => null;
    public string? UserEmail => null;
    public string? UserName => null;
    public string? IpAddress => null;
    public string? UserAgent => null;
}

