using Compass.Models.Docs;

namespace Compass.Services.Docs;

/// <summary>Functional specifications for Modern COMPASS views at <c>/docs/specifications</c>.</summary>
public static class DocsSpecificationsCatalog
{
    public static IReadOnlyList<DocsSpecificationArea> GetAreas() => Areas;

    private static readonly IReadOnlyList<DocsSpecificationArea> Areas =
    [
        WorkArea(),
        RaidArea(),
        PerformanceArea(),
        StandardsArea(),
        ServiceRegisterArea(),
        ReportingArea(),
    ];

    private static DocsSpecificationArea WorkArea() => new(
        "work",
        "Work",
        "Delivery work items (projects) and their supporting registers: strategic alignment, monthly narrative updates, milestones, embedded RAID, contacts, and links to FIPS service register products.",
        [
            Spec(
                "work-register",
                "Work register",
                "Browse and filter delivery work",
                "/modern/work",
                "Lists active, paused, completed and cancelled work items with RAG, priority, portfolio and phase. Entry point for creating work and opening detail.",
                "Project (work item), RAG and priority lookups, organisational groups, directorates, tags.",
                "Signed-in Entra user ([Authorize] on ModernWorkController). No feature gate. List is not restricted by ownership; edit actions on detail are gated separately.",
                "Register data feeds Reporting → monthly update progress, thematic views and PowerBI exports. Linked RAID and milestones roll up to portfolio reporting.",
                "GET/POST/PUT/DELETE /api/v1/WorkItems (Bearer: WorkItems read/create/update/delete). Related data via Milestones, Risks and Issues APIs where linked."),

            Spec(
                "work-detail",
                "Work item detail",
                "View a single work item",
                "/modern/work/{id}",
                "Hub for delivery metadata, strategic alignment summary, monthly updates, milestones, RAID tabs, contacts, FIPS visibility and service register links.",
                "Project (including ShowInFips), ProjectContact, ProjectMission, ProjectObjective, monthly updates, milestones, RAID assignments, FIPS product links (when fips feature on).",
                "Signed-in user. Mutations require CanUserEditWorkItemAsync (project contact, creator, SRO, service owner, PMO, business-area admin/leader, directorate leader, or Admin / Central Operations Admin / super admin). RAID tabs also require FeatureCodes.Raid.",
                "Detail is the source of truth for monthly reporting narratives, RAID context on work, and commission performance attribution.",
                "GET /api/v1/WorkItems/{id}. Milestones: GET/POST /api/v1/Milestones. Risks/issues on work: GET /api/v1/Risks and /api/v1/Issues."),

            Spec(
                "work-strategic-alignment",
                "Strategic alignment",
                "Align work to missions and outcomes",
                "/modern/work/{id}/strategic-alignment/edit",
                "Edit mission pillars, priority outcomes and related strategic fields on a work item.",
                "ProjectMission, ProjectObjective, mission and outcome lookups.",
                "Signed-in user; POST requires CanUserEditWorkItemAsync.",
                "Feeds thematic and priorities reporting; exported in work-scoped Excel and monthly packs.",
                "No public REST API for strategic alignment fields."),

            Spec(
                "work-monthly-update",
                "Monthly updates",
                "Monthly narrative reporting on work",
                "/modern/work/{id}/monthly-update/add",
                "Capture, view and edit monthly status narratives for a work item (add, view, edit flows).",
                "MonthlyUpdate, reporting period, author user, RAG narrative fields.",
                "Signed-in user; create/edit requires CanUserEditWorkItemAsync.",
                "Drives Reporting → monthly update and monthly submission progress. Used by Central Ops oversight dashboards.",
                "No dedicated MonthlyUpdates REST API; data available indirectly via reporting exports and session-authenticated UI."),

            Spec(
                "work-milestones",
                "Milestones",
                "Plan and track delivery milestones",
                "/modern/work/{id}/milestone/add",
                "Add and edit milestones on a work item with dates, status and descriptions.",
                "Milestone, Project linkage, milestone status lookups.",
                "Signed-in user; mutations require CanUserEditWorkItemAsync.",
                "Milestone dates and status appear on work reporting and RAID milestone registers.",
                "GET/POST/PATCH/DELETE /api/v1/milestones (Bearer: Milestones read/create/update/delete)."),

            Spec(
                "work-risks-issues",
                "Work-scoped RAID",
                "Manage risks and issues on work",
                "/modern/work/{id} (RAID tabs)",
                "Embedded risks and issues registers on the work detail surface (same entities as global RAID, filtered by project).",
                "Risk, Issue, ProjectId, RAID lookups, owners and business areas.",
                "Signed-in user; FeatureCodes.Raid must be on. Record edit uses RAID ownership rules (owner, SRO, creator, BA admin/leader, directorate leader, Central Ops).",
                "Rolls into RAID reporting, tier reporting and work monthly packs.",
                "GET/POST/PATCH/DELETE /api/v1/risks and /api/v1/issues when records are accessible to the token."),

            Spec(
                "work-assumptions-dependencies",
                "Assumptions and dependencies on work",
                "Track assumptions and dependencies",
                "/modern/work/{id} (Assumptions / Dependencies tabs)",
                "View and manage assumptions and dependencies assigned to the work item.",
                "Assumption, Dependency, ProjectId, status and relationship lookups.",
                "Signed-in user; FeatureCodes.Raid; edit rights follow RAID item ownership patterns.",
                "Included in RAID portfolio reporting and exports.",
                "No public REST API for Assumptions or Dependencies (UI and exports only)."),

            Spec(
                "work-contacts",
                "Contacts",
                "Work item contacts and roles",
                "/modern/work/{id}/contact/edit",
                "Manage primary contact, budget owner, SRO, service owner, PMO and additional contacts.",
                "ProjectContact, User, contact role lookups.",
                "Signed-in user; mutations require CanUserEditWorkItemAsync.",
                "Contact emails drive edit rights and notification routing.",
                "No public REST API for project contacts."),

            Spec(
                "work-service-register-links",
                "Service register links on work",
                "Link work to FIPS products",
                "/modern/work/{id} (Service register panel)",
                "Link and unlink FIPS service register products to a work item for traceability.",
                "WorkServiceRegisterLink, FipsProduct (ProductDocument), CMDB sync metadata when FeatureCodes.Fips is on.",
                "Signed-in user; FeatureCodes.Fips must be on. Link/unlink requires CanLinkFromWorkItemAsync (work edit rights or Central Ops).",
                "Links appear on service register product views and reporting → service register.",
                "Service register read: GET /api/v1/service-register/* (Bearer: ServiceRegister read). Product FIPS API: /api/products/fips/*."),
        ]);

    private static DocsSpecificationArea RaidArea() => new(
        "raid",
        "RAID",
        "Central RAID registers and processes: risks, issues, assumptions, dependencies and near misses. Requires the raid feature toggle unless noted.",
        [
            Spec(
                "raid-dashboard",
                "RAID dashboard",
                "RAID overview",
                "/modern/raid/dashboard",
                "Summary counts and entry points to RAID registers, reviews and reporting hand-offs.",
                "Aggregated Risk, Issue, Assumption, Dependency, NearMiss metrics.",
                "Signed-in user; [Authorize] + RaidFeatureGateFilter (FeatureCodes.Raid).",
                "Feeds Reporting → RAID review progress and tier reporting.",
                "Risks/Issues APIs for underlying records; no dashboard-specific API."),

            Spec(
                "raid-risks",
                "Risks register and detail",
                "Enterprise risk management",
                "/modern/raid/risks",
                "Register, create, view and edit risks with scoring, tiers, mitigations, KRIs and linked records.",
                "Risk, RiskTier, mitigations, business areas, ProjectId / ProductDocumentId, audit history.",
                "Signed-in user; FeatureCodes.Raid. Edit/delete: owner, SRO, creator, BA admin/leader, directorate leader, or Central Operations Admin / super admin.",
                "Reporting → risk, RAID exports, PowerBI via /api/v1/risks, operations console when enabled.",
                "GET/POST/PATCH/DELETE /api/v1/risks (Bearer: Risks read/create/update/delete)."),

            Spec(
                "raid-issues",
                "Issues register and detail",
                "Issue tracking and assurance",
                "/modern/raid/issues",
                "Register, create, view and edit issues with assurance tabs, actions and closure workflow.",
                "Issue, IssueAction, linked risks, business areas, ProjectId / ProductDocumentId.",
                "Signed-in user; FeatureCodes.Raid. Edit: CurrentUserMayEditIssueAsync (same pattern as risks).",
                "Reporting → RAID, assurance exports, PowerBI via /api/v1/issues.",
                "GET/POST/PATCH/DELETE /api/v1/issues (Bearer: Issues read/create/update/delete). Actions: /api/v1/actions."),

            Spec(
                "raid-assumptions",
                "Assumptions register",
                "Assumption log",
                "/modern/raid/assumptions",
                "Register and maintain assumptions with status, owners and links to work or products.",
                "Assumption, status lookups, ProjectId / ProductDocumentId.",
                "Signed-in user; FeatureCodes.Raid; per-record RAID edit rules.",
                "RAID reporting and portfolio exports.",
                "No public REST API (planned — use UI or exports)."),

            Spec(
                "raid-dependencies",
                "Dependencies register",
                "Dependency tracking",
                "/modern/raid/dependencies",
                "Register inbound/outbound dependencies with status, dates and linked delivery context.",
                "Dependency, status and type lookups, ProjectId / ProductDocumentId.",
                "Signed-in user; FeatureCodes.Raid; per-record RAID edit rules.",
                "RAID reporting and portfolio exports.",
                "No public REST API (planned — use UI or exports)."),

            Spec(
                "raid-near-misses",
                "Near misses",
                "Near miss reporting",
                "/modern/raid/near-misses",
                "Capture and review near misses with seriousness, type and status classifications.",
                "NearMiss, NearMissType, NearMissSeriousness, NearMissStatus lookups.",
                "Signed-in user; FeatureCodes.Raid; edit typically limited to creators, owners and Central Ops (same family as RAID items).",
                "Operational learning reports; JSON export via GET /modern/item-data/near-misses/{id}.",
                "No public REST API."),
        ]);

    private static DocsSpecificationArea PerformanceArea() => new(
        "performance",
        "Performance",
        "Product and commission performance reporting: metric returns, submissions and exports.",
        [
            Spec(
                "performance-commissions",
                "Performance commissions",
                "Commission list and guidance",
                "/modern/performance",
                "Lists open and historical performance commissions and links to per-commission submission flows.",
                "PerformanceCommission, reporting periods, product eligibility.",
                "Signed-in user ([Authorize] on ModernPerformanceController). Write access enforced per product in the performance service layer.",
                "Commission returns feed Reporting → performance and enterprise metric roll-ups.",
                "GET /api/v1/performance-metrics and /api/v1/enterprise-metrics (Bearer read/create as scoped on token)."),

            Spec(
                "performance-submission",
                "Commission submission",
                "Submit product performance returns",
                "/modern/performance/commission/{commissionId}/submission",
                "Enter and submit metric values for products in scope of a commission.",
                "PerformanceMetricReturn, EnterpriseMetricReturn, ProductDocument, commission configuration.",
                "Signed-in user; must be permitted to submit for the product (service owner / delegate patterns in performance services).",
                "Submitted metrics flow to Reporting → performance and PowerBI datasets.",
                "POST /api/v1/performance-metrics, POST /api/v1/enterprise-metrics (Bearer create)."),

            Spec(
                "performance-export",
                "Performance export",
                "Download commission data",
                "/modern/performance/commission/{commissionId}/export",
                "Export commission submission grids for offline analysis.",
                "Performance and enterprise metric returns for the commission.",
                "Signed-in user with access to the commission products.",
                "Same data as API metrics endpoints; used by analysts outside PowerBI.",
                "Prefer /api/v1/performance-metrics and /api/v1/enterprise-metrics for machine consumption."),
        ]);

    private static DocsSpecificationArea StandardsArea() => new(
        "standards",
        "Standards",
        "DDT Standards lifecycle and Functional Standards assessments. Requires FeatureCodes.Standards unless noted.",
        [
            Spec(
                "standards-dashboard",
                "Standards dashboard",
                "Standards overview",
                "/modern/standards/dashboard",
                "Entry point to DDT standards, functional standards and management views.",
                "DdtStandard, FunctionalStandard, assessment summaries.",
                "Signed-in user; StandardsFeatureGateFilter (FeatureCodes.Standards).",
                "Management information for standards compliance reporting.",
                "DDT: /api/v1/ddt-standards. Functional: /api/v1/functional-standards."),

            Spec(
                "standards-ddt",
                "DDT Standards",
                "DDT standard lifecycle",
                "/modern/standards/ddt",
                "Browse, draft, approve and publish DDT standards with version history.",
                "DdtStandard, stages, owners, Standards Managers workflow.",
                "Signed-in user; FeatureCodes.Standards. Draft: Standard Owners group. Approve/publish: Standard Approvers, Standard Publishers or Standards Managers; Central Ops bypass.",
                "Referenced by service register assurance views and Reporting → assessments.",
                "GET/POST/PATCH/DELETE /api/v1/ddt-standards (Bearer: DdtStandards read/create/update/delete)."),

            Spec(
                "standards-functional",
                "Functional standards",
                "Functional standard assessments",
                "/modern/standards/functional",
                "Run functional standard assessments, conduct criteria scoring and export Word/PDF summaries.",
                "FunctionalStandard, FunctionalStandardAssessment, criteria responses.",
                "Signed-in user; FeatureCodes.Standards; assessment conduct limited to assigned assessors and standards roles.",
                "Reporting → assessments; exports for governance packs.",
                "GET/POST /api/v1/functional-standards (Bearer: FunctionalStandards read/create)."),

            Spec(
                "standards-management",
                "Standards management",
                "Standards administration UI",
                "/modern/standards/management",
                "Manage standard owners, contacts and configuration for the Modern standards area.",
                "Standard ownership, user/group references.",
                "Signed-in user; CanAccessModernStandardsManagementAsync (owners, approvers, publishers, Standards Managers, Admin, Central Ops).",
                "Configuration only; does not directly feed external APIs.",
                "Admin configuration via UI; API consumers use DdtStandards and FunctionalStandards endpoints."),
        ]);

    private static DocsSpecificationArea ServiceRegisterArea() => new(
        "service-register",
        "Service register",
        "FIPS service register (CMDB-synced products) and product-scoped RAID, work, accessibility and assurance. Requires FeatureCodes.Fips for database-backed register (otherwise CMS read-only fallback).",
        [
            Spec(
                "fips-dashboard",
                "Service register dashboard",
                "FIPS overview",
                "/modern/manage/fips/dashboard",
                "Landing dashboard for the service register with counts and shortcuts.",
                "FipsProduct, business areas, CMDB sync state.",
                "Signed-in user; FeatureCodes.Fips (RequireFipsDatabaseAsync redirects when off).",
                "Reporting → service register; sync-app consumes CMDB and CMS.",
                "GET /api/v1/service-register/products and related routes (Bearer: ServiceRegister read)."),

            Spec(
                "fips-products",
                "Product register",
                "Browse FIPS products",
                "/modern/manage/fips/products",
                "Searchable register of digital products with phase, contacts and assurance summaries.",
                "FipsProduct, ProductDocument, CMDB identifiers, business area lookups.",
                "Signed-in user; FeatureCodes.Fips.",
                "Public FIPS frontend and PowerBI; AISS accessibility integration.",
                "GET /api/v1/service-register/* ; legacy GET /api/products/fips/*."),

            Spec(
                "fips-product-detail",
                "Product detail",
                "Single product hub",
                "/modern/manage/fips/{id}",
                "Product metadata, RAID tabs, linked work items, accessibility statement summary, assurance and assessments.",
                "FipsProduct, RAID entities by ProductDocumentId, WorkServiceRegisterLink, AISS summaries, service assessments.",
                "Signed-in user; FeatureCodes.Fips. RAID tabs need FeatureCodes.Raid. Product write via FipsManager / Design Ops patterns for some fields.",
                "Feeds Reporting → service register, accessibility, assessments; AISS statement service.",
                "ServiceRegister read/update; AccessibilityIssues read; Risks/Issues when linked."),

            Spec(
                "fips-product-risks",
                "Product risks",
                "Risks on a product",
                "/modern/manage/fips/{id} (Risks)",
                "View and manage risks assigned to the FIPS product.",
                "Risk with ProductDocumentId, RAID lookups.",
                "Signed-in user; FeatureCodes.Fips + FeatureCodes.Raid; RAID edit rules.",
                "Product risk roll-up in Reporting → risk and service register.",
                "GET/POST/PATCH/DELETE /api/v1/risks filtered by product."),

            Spec(
                "fips-product-issues",
                "Product issues",
                "Issues on a product",
                "/modern/manage/fips/{id} (Issues)",
                "View and manage issues assigned to the FIPS product.",
                "Issue with ProductDocumentId.",
                "Signed-in user; FeatureCodes.Fips + FeatureCodes.Raid; RAID edit rules.",
                "Reporting and assurance packs.",
                "GET/POST/PATCH/DELETE /api/v1/issues."),

            Spec(
                "fips-product-assumptions-dependencies",
                "Product assumptions and dependencies",
                "RAID on product",
                "/modern/manage/fips/{id} (Assumptions / Dependencies)",
                "Assumptions and dependencies scoped to the product.",
                "Assumption, Dependency, ProductDocumentId.",
                "Signed-in user; FeatureCodes.Fips + FeatureCodes.Raid.",
                "RAID reporting.",
                "No public REST API."),

            Spec(
                "fips-product-work-items",
                "Product work items",
                "Linked delivery work",
                "/modern/manage/fips/{id} (Work items)",
                "View and link work items to the product.",
                "WorkServiceRegisterLink, Project.",
                "Signed-in user; FeatureCodes.Fips; linking requires work link permission.",
                "Cross-links product to delivery reporting.",
                "No direct API; projects via milestones/risks on linked work."),

            Spec(
                "fips-product-accessibility",
                "Product accessibility",
                "Accessibility posture",
                "/modern/manage/fips/{id} (Accessibility)",
                "Shows accessibility statement status, issues summary and AISS integration.",
                "AccessibilityIssue, statement templates, AISS platform summary (external service).",
                "Signed-in user; FeatureCodes.Fips. Some admin actions via Accessibility administrators.",
                "Reporting → accessibility; AISS (Accessibility Issues Statement Service).",
                "GET /api/v1/accessibility-issues (Bearer: AccessibilityIssues read)."),

            Spec(
                "fips-product-assurance",
                "Product assurance",
                "Service assessments and assurance",
                "/modern/manage/fips/{id} (Assurance)",
                "Service assessment history, DDT/functional compliance indicators.",
                "ServiceAssessment, DdtStandard compliance, functional assessment links.",
                "Signed-in user; FeatureCodes.Fips.",
                "Reporting → assessments.",
                "Partial via ServiceRegister and DdtStandards/FunctionalStandards APIs."),
        ]);

    private static DocsSpecificationArea ReportingArea() => new(
        "reporting",
        "Reporting",
        "Read-only and oversight reporting across work, RAID, performance, service register and accessibility. Some views require Central Operations Admin or feature gates.",
        [
            Spec(
                "reporting-dashboard",
                "Reporting dashboard",
                "Reporting home",
                "/modern/reporting/dashboard",
                "Hub linking to thematic, priorities, resourcing, monthly update, RAID, performance and accessibility reports.",
                "Aggregates from projects, RAID, performance commissions, FIPS products.",
                "Signed-in user ([Authorize] on ModernReportingController).",
                "Central Ops and leadership consumption; exports to Excel.",
                "Underlying data via respective /api/v1/* resources."),

            Spec(
                "reporting-monthly-update",
                "Monthly update reporting",
                "Monthly narrative progress",
                "/modern/reporting/monthly-update",
                "Cross-work view of monthly update submission status for the active period.",
                "MonthlyUpdate, Project, reporting period.",
                "Signed-in user.",
                "Oversight of delivery reporting cycle; pairs with work monthly update capture.",
                "GET /api/v1/WorkItems; monthly narrative fields remain UI/export only."),

            Spec(
                "reporting-monthly-submission-progress",
                "Monthly submission progress",
                "Submission compliance",
                "/modern/reporting/monthly-submission-progress",
                "Tracks which work items have submitted monthly updates for the period.",
                "MonthlyUpdate submission flags, Project.",
                "Signed-in user.",
                "Central Ops chase workflows.",
                "No dedicated API."),

            Spec(
                "reporting-raid",
                "RAID reporting",
                "Portfolio RAID views",
                "/modern/reporting/raid",
                "Aggregated RAID dashboards and exports for leadership.",
                "Risk, Issue, Assumption, Dependency aggregates.",
                "Signed-in user; RaidFeatureGateFilter on this action.",
                "Tier reporting and Excel exports.",
                "Risks and Issues APIs for detail extracts."),

            Spec(
                "reporting-raid-review-progress",
                "RAID review progress",
                "Review cycle tracking",
                "/modern/reporting/raid-review-progress",
                "Shows progress of RAID review cycles across portfolios.",
                "RaidReviewProgress, Risk/Issue review states.",
                "Signed-in user; RaidFeatureGateFilter.",
                "Central Ops RAID governance.",
                "No dedicated API."),

            Spec(
                "reporting-risk",
                "Risk reporting",
                "Enterprise risk reporting",
                "/modern/reporting/risk",
                "Leadership risk summaries and heatmaps.",
                "Risk scores, tiers, business areas.",
                "Signed-in user; RaidFeatureGateFilter.",
                "PowerBI and Excel.",
                "GET /api/v1/risks."),

            Spec(
                "reporting-performance",
                "Performance reporting",
                "Commission performance roll-up",
                "/modern/reporting/performance",
                "Cross-commission performance compliance and metric summaries.",
                "PerformanceMetricReturn, EnterpriseMetricReturn, commissions.",
                "Signed-in user.",
                "Leadership dashboards; PowerBI.",
                "GET /api/v1/performance-metrics, GET /api/v1/enterprise-metrics."),

            Spec(
                "reporting-service-register",
                "Service register reporting",
                "FIPS register oversight",
                "/modern/reporting/service-register",
                "Portfolio view of FIPS products, phases and assurance indicators.",
                "FipsProduct, assessments, CMDB sync metadata.",
                "Signed-in user.",
                "Central Ops FIPS oversight.",
                "GET /api/v1/service-register/*."),

            Spec(
                "reporting-accessibility",
                "Accessibility reporting",
                "Accessibility compliance",
                "/modern/reporting/accessibility",
                "Cross-product accessibility statement and issue posture.",
                "AccessibilityIssue, AISS summaries, FipsProduct.",
                "Signed-in user.",
                "Accessibility governance; AISS.",
                "GET /api/v1/accessibility-issues."),

            Spec(
                "reporting-assessments",
                "Assessments reporting",
                "Service assessment reporting",
                "/modern/reporting/assessments",
                "Service assessment outcomes across the register.",
                "ServiceAssessment, DdtStandard, functional assessments.",
                "Signed-in user.",
                "Assurance reporting packs.",
                "ServiceRegister and standards APIs."),

            Spec(
                "reporting-thematic",
                "Thematic reporting",
                "Mission pillar views",
                "/modern/reporting/thematic",
                "Work and outcomes grouped by mission pillar.",
                "ProjectMission, mission lookups, Project.",
                "Signed-in user.",
                "Strategic alignment reporting.",
                "No dedicated API."),

            Spec(
                "reporting-priorities",
                "Priorities reporting",
                "Priority outcomes",
                "/modern/reporting/priorities",
                "Delivery aligned to priority outcomes.",
                "ProjectObjective, outcome lookups.",
                "Signed-in user.",
                "Priorities packs for leadership.",
                "No dedicated API."),

            Spec(
                "reporting-resourcing",
                "Resourcing reporting",
                "Resourcing and capacity",
                "/modern/reporting/resourcing",
                "Resourcing views across work and products (where configured).",
                "Project resourcing fields, FipsProduct contacts.",
                "Signed-in user.",
                "Resourcing oversight.",
                "No dedicated API."),

            Spec(
                "reporting-design-decision-records",
                "DDR reporting",
                "Design decision records",
                "/modern/reporting/design-decision-records",
                "Portfolio view of design decision records when DDR feature is enabled.",
                "DesignDecisionRecord.",
                "Signed-in user; DdrFeatureGateFilter (FeatureCodes.Ddr).",
                "Design governance reporting.",
                "No public REST API (UI reporting)."),
        ]);

    private static DocsViewSpecification Spec(
        string id,
        string title,
        string processName,
        string route,
        string whatItDoes,
        string dataUsed,
        string permissions,
        string dataUsage,
        string apiAvailability) =>
        new(id, title, processName, route, whatItDoes, dataUsed, permissions, dataUsage, apiAvailability);
}
