namespace Compass.Models.Docs;

/// <summary>
/// Single source of truth for the Compass API endpoint catalogue rendered
/// on the <c>/docs/api</c> reference page and driven into the interactive
/// <c>/docs/api-explorer</c> page. Every endpoint listed here is implemented
/// in the codebase and verified to return data; outdated resources
/// (Accessibility — managed in AISS; Surveys; Statement Templates; Actions) are intentionally
/// omitted. Assumptions, dependencies, and near misses are planned REST resources.
/// </summary>
public static class ApiCatalogue
{
    /// <summary>All endpoint groups, in display order.</summary>
    public static IReadOnlyList<ApiEndpointSection> Sections { get; } = BuildSections();

    private static ApiEndpointSection[] BuildSections() => new ApiEndpointSection[]
    {
        new(
            Id: "work-items",
            Title: "Work items",
            Description: "Delivery work items (the Project table behind /modern/work). Use this as the dimension table for RAID, milestones and monthly reporting. Status values are Active, Paused, Completed and Cancelled.",
            Endpoints: new ApiEndpointDoc[]
            {
                new(
                    Id: "work-items-list",
                    Method: "GET",
                    Path: "/api/v1/WorkItems",
                    Scope: "WorkItems:read",
                    Description: "Paged list of work items with RAG, phase, business area, priority, portfolio, primary contact, tags and FIPS visibility.",
                    QueryParams: new[]
                    {
                        ("status", "string", "Filter by status: Active, Paused, Completed, Cancelled"),
                        ("businessAreaId", "integer", "Filter by business area lookup id"),
                        ("phaseId", "integer", "Filter by phase lookup id"),
                        ("ragStatusId", "integer", "Filter by RAG status lookup id"),
                        ("priorityId", "integer", "Filter by delivery priority lookup id"),
                        ("portfolioId", "integer", "Filter by organisational group (portfolio) id"),
                        ("flagship", "boolean", "true / false — flagship work only"),
                        ("showInFips", "string", "true / yes / 1 or false / no / 0 — work marked to appear in FIPS"),
                        ("q", "string", "Free-text search across title and project code"),
                        ("page", "integer", "Page number (default 1)"),
                        ("pageSize", "integer", "Results per page (default 50, max 100)")
                    },
                    ResponseShape: "Paged envelope: { data: [], pagination: { currentPage, pageSize, totalPages, totalRecords } }",
                    ResponseExample: """
{
  "data": [
    {
      "id": 412,
      "projectCode": "DDTDEL-0412",
      "title": "Teacher reference numbers",
      "aim": "Give teachers a single identifier across services",
      "status": "Active",
      "startDate": "2025-04-01T00:00:00Z",
      "targetEndDate": "2027-03-31T00:00:00Z",
      "isFlagship": true,
      "showInFips": true,
      "ragStatus": { "id": 2, "name": "Amber" },
      "phase": { "id": 3, "name": "Private beta" },
      "businessArea": { "id": 5, "name": "Teacher Services" },
      "priority": { "id": 1, "name": "P1" },
      "portfolio": { "id": 8, "name": "Teacher Services portfolio" },
      "primaryContact": { "id": 91, "name": "Alex Morgan", "email": "alex.morgan@education.gov.uk" },
      "tags": [ { "id": 4, "name": "Workforce" } ],
      "createdAt": "2025-03-12T09:00:00Z",
      "updatedAt": "2026-08-01T14:22:00Z"
    }
  ],
  "pagination": { "currentPage": 1, "pageSize": 50, "totalPages": 9, "totalRecords": 412 }
}
"""),
                new(
                    Id: "work-items-get",
                    Method: "GET",
                    Path: "/api/v1/WorkItems/{id}",
                    Scope: "WorkItems:read",
                    Description: "Single work item including contacts, directorates, tags, linked products, FIPS visibility, RAID/milestone counts and the latest monthly update summary.",
                    RouteParams: new[] { ("id", "integer", "Work item id (Project.Id)") },
                    ResponseExample: """
{
  "id": 412,
  "projectCode": "DDTDEL-0412",
  "title": "Teacher reference numbers",
  "aim": "Give teachers a single identifier across services",
  "problemStatement": "Teachers currently hold multiple identifiers across DfE systems.",
  "status": "Active",
  "isFlagship": true,
  "showInFips": true,
  "ragStatus": { "id": 2, "name": "Amber" },
  "phase": { "id": 3, "name": "Private beta" },
  "contacts": [
    { "id": 18, "role": "Primary contact", "name": "Alex Morgan", "email": "alex.morgan@education.gov.uk" }
  ],
  "linkedProducts": [
    { "documentId": "0c64e10b-…", "fipsId": "FIPS-2491", "title": "Apply for a teacher reference" }
  ],
  "counts": { "openRisks": 3, "openIssues": 1, "milestones": 6 },
  "latestMonthlyUpdate": { "id": 8801, "year": 2026, "month": 7, "submittedAt": "2026-08-04T10:12:00Z" }
}
"""),
                new(
                    Id: "work-items-create",
                    Method: "POST",
                    Path: "/api/v1/WorkItems",
                    Scope: "WorkItems:create",
                    Description: "Create a work item. A DDTDEL-nnnn project code is assigned server-side. Status defaults to Active.",
                    BodyExample: """
{
  "title": "Teacher reference numbers",
  "aim": "Give teachers a single identifier across services",
  "problemStatement": "Teachers currently hold multiple identifiers across DfE systems.",
  "status": "Active",
  "startDate": "2025-04-01T00:00:00Z",
  "targetEndDate": "2027-03-31T00:00:00Z",
  "businessAreaId": 5,
  "portfolioId": 8,
  "phaseId": 3,
  "priorityId": 1,
  "ragStatusId": 2,
  "primaryContactUserId": 91,
  "isFlagship": true,
  "showInFips": true,
  "tagIds": [4]
}
""",
                    ResponseExample: """
{
  "id": 413,
  "projectCode": "DDTDEL-0413",
  "title": "Teacher reference numbers",
  "status": "Active",
  "createdAt": "2026-08-18T09:00:00Z"
}
"""),
                new(
                    Id: "work-items-update",
                    Method: "PUT",
                    Path: "/api/v1/WorkItems/{id}",
                    Scope: "WorkItems:update",
                    Description: "Partial update — send only the fields you want to change.",
                    RouteParams: new[] { ("id", "integer", "Work item id") },
                    BodyExample: """
{
  "status": "Paused",
  "ragStatusId": 3,
  "isFlagship": false,
  "showInFips": true
}
""",
                    ResponseExample: "{ \"id\": 412, \"status\": \"Paused\", \"updatedAt\": \"2026-08-18T09:15:00Z\" }"),
                new(
                    Id: "work-items-delete",
                    Method: "DELETE",
                    Path: "/api/v1/WorkItems/{id}",
                    Scope: "WorkItems:delete",
                    Description: "Soft-deletes a work item (IsDeleted flag). Delete is not available through self-service token requests.",
                    RouteParams: new[] { ("id", "integer", "Work item id") },
                    ResponseExample: "{ \"message\": \"Work item deleted\" }")
            }),

        new(
            Id: "risks",
            Title: "Risks",
            Description: "Operational risks captured against work items and FIPS products. Risk score is auto-computed as impact × likelihood.",
            Endpoints: new ApiEndpointDoc[]
            {
                new(
                    Id: "risks-list",
                    Method: "GET",
                    Path: "/api/v1/Risks",
                    Scope: "Risks:read",
                    Description: "Paged list of risks with risk tier and risk types included.",
                    QueryParams: new[]
                    {
                        ("fipsId", "string", "Filter by FIPS product id (optional)"),
                        ("status", "string", "Filter by status: new, open, treating, monitoring, closed"),
                        ("minScore", "integer", "Filter by minimum risk score 1–25"),
                        ("page", "integer", "Page number (default 1)"),
                        ("pageSize", "integer", "Results per page (default 50, max 100)")
                    },
                    ResponseShape: "Paged envelope: { data: [], pagination: { currentPage, pageSize, totalPages, totalRecords } }",
                    ResponseExample: """
{
  "data": [
    {
      "id": 1,
      "title": "Data quality risk",
      "description": "Missing source-system feeds for April reporting",
      "fipsId": "PROG-0148",
      "objectiveId": 12,
      "riskTierId": 3,
      "impactRating": 4,
      "likelihoodRating": 3,
      "riskScore": 12,
      "status": "open",
      "category": "data",
      "ownerEmail": "programme.manager@education.gov.uk",
      "createdAt": "2026-04-01T09:00:00Z",
      "updatedAt": "2026-04-15T14:30:00Z",
      "riskTier": { "id": 3, "name": "Programme" },
      "riskTypes": [ { "id": 2, "name": "Operational" } ]
    }
  ],
  "pagination": { "currentPage": 1, "pageSize": 50, "totalPages": 5, "totalRecords": 223 }
}
"""),
                new(
                    Id: "risks-get",
                    Method: "GET",
                    Path: "/api/v1/Risks/{id}",
                    Scope: "Risks:read",
                    Description: "Single risk, including linked actions.",
                    RouteParams: new[] { ("id", "integer", "Risk id") },
                    ResponseExample: """
{
  "id": 1,
  "title": "Data quality risk",
  "riskScore": 12,
  "status": "open",
  "actions": [
    { "id": 4, "title": "Investigate feed gap", "status": "in_progress" }
  ]
}
"""),
                new(
                    Id: "risks-create",
                    Method: "POST",
                    Path: "/api/v1/Risks",
                    Scope: "Risks:create",
                    Description: "Create a risk. Risk score is computed server-side from impact × likelihood.",
                    BodyExample: """
{
  "title": "Data quality risk",
  "description": "Missing source-system feeds for April reporting",
  "fipsId": "PROG-0148",
  "objectiveId": 12,
  "riskTierId": 3,
  "impactRating": 4,
  "likelihoodRating": 3,
  "ownerEmail": "programme.manager@education.gov.uk",
  "status": "open",
  "category": "data",
  "businessArea": "Digital Delivery",
  "proximityDate": "2026-12-01T00:00:00Z",
  "targetDate": "2027-01-15T00:00:00Z",
  "response": "mitigate",
  "riskTypeIds": [2, 7]
}
""",
                    ResponseExample: """
{
  "id": 224,
  "title": "Data quality risk",
  "riskScore": 12,
  "status": "open",
  "createdAt": "2026-05-19T10:30:00Z"
}
"""),
                new(
                    Id: "risks-update",
                    Method: "PUT",
                    Path: "/api/v1/Risks/{id}",
                    Scope: "Risks:update",
                    Description: "Partial update — send only the fields you want to change.",
                    RouteParams: new[] { ("id", "integer", "Risk id") },
                    BodyExample: """
{
  "status": "treating",
  "impactRating": 5,
  "likelihoodRating": 2
}
""",
                    ResponseExample: "{ \"id\": 1, \"status\": \"treating\", \"riskScore\": 10 }"),
                new(
                    Id: "risks-delete",
                    Method: "DELETE",
                    Path: "/api/v1/Risks/{id}",
                    Scope: "Risks:delete",
                    Description: "Soft-deletes a risk (IsDeleted flag).",
                    RouteParams: new[] { ("id", "integer", "Risk id") },
                    ResponseExample: "{ \"message\": \"Risk deleted\" }")
            }),

        new(
            Id: "issues",
            Title: "Issues",
            Description: "Issues raised against work items and FIPS products.",
            Endpoints: new ApiEndpointDoc[]
            {
                new(
                    Id: "issues-list",
                    Method: "GET",
                    Path: "/api/v1/Issues",
                    Scope: "Issues:read",
                    Description: "Paged list of issues.",
                    QueryParams: new[]
                    {
                        ("fipsId", "string", "Filter by FIPS product id"),
                        ("status", "string", "Filter by status: new, open, in_progress, resolved, closed"),
                        ("severity", "string", "Filter by severity: low, medium, high, critical"),
                        ("page", "integer", "Page number (default 1)"),
                        ("pageSize", "integer", "Results per page (default 50)")
                    },
                    ResponseShape: "Paged envelope: { data: [], pagination: { … } }",
                    ResponseExample: """
{
  "data": [
    {
      "id": 88,
      "title": "Sync failure for FIPS-2491",
      "fipsId": "FIPS-2491",
      "severity": "high",
      "status": "in_progress",
      "createdAt": "2026-04-12T08:11:00Z"
    }
  ],
  "pagination": { "currentPage": 1, "pageSize": 50, "totalPages": 2, "totalRecords": 73 }
}
"""),
                new(
                    Id: "issues-get",
                    Method: "GET",
                    Path: "/api/v1/Issues/{id}",
                    Scope: "Issues:read",
                    Description: "Single issue with its linked actions.",
                    RouteParams: new[] { ("id", "integer", "Issue id") },
                    ResponseExample: """
{
  "id": 88,
  "title": "Sync failure for FIPS-2491",
  "status": "in_progress",
  "actions": [
    { "id": 12, "title": "Re-run sync once CMDB feed recovers" }
  ]
}
"""),
                new(
                    Id: "issues-create",
                    Method: "POST",
                    Path: "/api/v1/Issues",
                    Scope: "Issues:create",
                    Description: "Create a new issue.",
                    BodyExample: """
{
  "title": "Sync failure for FIPS-2491",
  "description": "CMDB feed returned 500 for three retries",
  "fipsId": "FIPS-2491",
  "severity": "high",
  "category": "integration",
  "status": "open",
  "ownerEmail": "ops.lead@education.gov.uk"
}
""",
                    ResponseExample: "{ \"id\": 89, \"status\": \"open\" }"),
                new(
                    Id: "issues-update",
                    Method: "PUT",
                    Path: "/api/v1/Issues/{id}",
                    Scope: "Issues:update",
                    Description: "Partial update.",
                    RouteParams: new[] { ("id", "integer", "Issue id") },
                    BodyExample: "{ \"status\": \"resolved\" }",
                    ResponseExample: "{ \"id\": 88, \"status\": \"resolved\" }"),
                new(
                    Id: "issues-delete",
                    Method: "DELETE",
                    Path: "/api/v1/Issues/{id}",
                    Scope: "Issues:delete",
                    Description: "Soft-delete an issue.",
                    RouteParams: new[] { ("id", "integer", "Issue id") },
                    ResponseExample: "{ \"message\": \"Issue deleted\" }")
            }),

        new(
            Id: "milestones",
            Title: "Milestones",
            Description: "Delivery milestones linked to work items and products.",
            Endpoints: new ApiEndpointDoc[]
            {
                new(
                    Id: "milestones-list",
                    Method: "GET",
                    Path: "/api/v1/Milestones",
                    Scope: "Milestones:read",
                    Description: "Paged list of milestones.",
                    QueryParams: new[]
                    {
                        ("fipsId", "string", "Filter by FIPS product id"),
                        ("status", "string", "Filter by status: not_started, in_progress, at_risk, completed, missed"),
                        ("page", "integer", "Page number"),
                        ("pageSize", "integer", "Results per page (default 50)")
                    },
                    ResponseExample: """
{
  "data": [
    { "id": 14, "title": "Private beta", "targetDate": "2026-09-30T00:00:00Z", "status": "in_progress" }
  ],
  "pagination": { "currentPage": 1, "pageSize": 50, "totalPages": 1, "totalRecords": 14 }
}
"""),
                new(
                    Id: "milestones-get",
                    Method: "GET",
                    Path: "/api/v1/Milestones/{id}",
                    Scope: "Milestones:read",
                    Description: "Single milestone including linked actions, risks and issues.",
                    RouteParams: new[] { ("id", "integer", "Milestone id") },
                    ResponseExample: """
{
  "id": 14,
  "title": "Private beta",
  "targetDate": "2026-09-30T00:00:00Z",
  "status": "in_progress",
  "actions": [],
  "risks": [],
  "issues": []
}
"""),
                new(
                    Id: "milestones-create",
                    Method: "POST",
                    Path: "/api/v1/Milestones",
                    Scope: "Milestones:create",
                    Description: "Create a milestone.",
                    BodyExample: """
{
  "title": "Private beta",
  "description": "First external delivery to selected users",
  "fipsId": "PROG-0148",
  "targetDate": "2026-09-30T00:00:00Z",
  "status": "not_started",
  "ownerEmail": "delivery.manager@education.gov.uk"
}
""",
                    ResponseExample: "{ \"id\": 15, \"status\": \"not_started\" }"),
                new(
                    Id: "milestones-update",
                    Method: "PUT",
                    Path: "/api/v1/Milestones/{id}",
                    Scope: "Milestones:update",
                    Description: "Partial update.",
                    RouteParams: new[] { ("id", "integer", "Milestone id") },
                    BodyExample: "{ \"status\": \"completed\", \"completedDate\": \"2026-09-29T00:00:00Z\" }",
                    ResponseExample: "{ \"id\": 14, \"status\": \"completed\" }"),
                new(
                    Id: "milestones-delete",
                    Method: "DELETE",
                    Path: "/api/v1/Milestones/{id}",
                    Scope: "Milestones:delete",
                    Description: "Soft-delete a milestone.",
                    RouteParams: new[] { ("id", "integer", "Milestone id") },
                    ResponseExample: "{ \"message\": \"Milestone deleted\" }")
            }),

        new(
            Id: "performance-metrics",
            Title: "Performance metrics",
            Description: "Per-product performance returns submitted monthly against the metric catalogue.",
            Endpoints: new ApiEndpointDoc[]
            {
                new(
                    Id: "perf-list",
                    Method: "GET",
                    Path: "/api/v1/PerformanceMetrics",
                    Scope: "PerformanceMetrics:read",
                    Description: "Catalogue of all performance metric definitions, ordered by identifier.",
                    ResponseExample: """
[
  { "id": 1, "identifier": "PM01", "name": "Monthly active users", "unit": "count" },
  { "id": 2, "identifier": "PM02", "name": "User satisfaction", "unit": "score (1-5)" }
]
"""),
                new(
                    Id: "perf-get",
                    Method: "GET",
                    Path: "/api/v1/PerformanceMetrics/{id}",
                    Scope: "PerformanceMetrics:read",
                    Description: "Single performance metric definition.",
                    RouteParams: new[] { ("id", "integer", "Metric id") },
                    ResponseExample: "{ \"id\": 1, \"identifier\": \"PM01\", \"name\": \"Monthly active users\" }"),
                new(
                    Id: "perf-submit",
                    Method: "POST",
                    Path: "/api/v1/PerformanceMetrics/submit",
                    Scope: "PerformanceMetrics:create",
                    Description: "Upsert a metric value for a product/return (fipsId, year, month, metricId).",
                    BodyExample: """
{
  "fipsId": "FIPS-2491",
  "year": 2026,
  "month": 4,
  "metricId": 1,
  "value": 12450,
  "comment": "Includes batch import on the 30th"
}
""",
                    ResponseExample: "{ \"id\": 7821, \"created\": true }"),
                new(
                    Id: "perf-values",
                    Method: "GET",
                    Path: "/api/v1/PerformanceMetrics/values",
                    Scope: "PerformanceMetrics:read",
                    Description: "Submitted metric values, optionally filtered.",
                    QueryParams: new[]
                    {
                        ("fipsId", "string", "Filter by FIPS product id"),
                        ("year", "integer", "Filter by year"),
                        ("month", "integer", "Filter by month 1–12")
                    },
                    ResponseExample: "[ { \"fipsId\": \"FIPS-2491\", \"year\": 2026, \"month\": 4, \"metricId\": 1, \"value\": 12450 } ]")
            }),

        new(
            Id: "enterprise-metrics",
            Title: "Enterprise metrics",
            Description: "Cross-portfolio metrics owned centrally rather than per product.",
            Endpoints: new ApiEndpointDoc[]
            {
                new(
                    Id: "ent-list",
                    Method: "GET",
                    Path: "/api/v1/EnterpriseMetrics",
                    Scope: "EnterpriseMetrics:read",
                    Description: "Catalogue of all enterprise metric definitions.",
                    ResponseExample: "[ { \"id\": 1, \"identifier\": \"EM01\", \"name\": \"Total spend\", \"unit\": \"£\" } ]"),
                new(
                    Id: "ent-get",
                    Method: "GET",
                    Path: "/api/v1/EnterpriseMetrics/{id}",
                    Scope: "EnterpriseMetrics:read",
                    Description: "Single enterprise metric definition.",
                    RouteParams: new[] { ("id", "integer", "Metric id") },
                    ResponseExample: "{ \"id\": 1, \"identifier\": \"EM01\", \"name\": \"Total spend\" }"),
                new(
                    Id: "ent-submit",
                    Method: "POST",
                    Path: "/api/v1/EnterpriseMetrics/submit",
                    Scope: "EnterpriseMetrics:create",
                    Description: "Upsert an enterprise metric value for (year, month, metricId).",
                    BodyExample: """
{
  "year": 2026,
  "month": 4,
  "metricId": 1,
  "value": 985000,
  "comment": "Confirmed by Finance on 12 May"
}
""",
                    ResponseExample: "{ \"id\": 312, \"created\": true }"),
                new(
                    Id: "ent-values",
                    Method: "GET",
                    Path: "/api/v1/EnterpriseMetrics/values",
                    Scope: "EnterpriseMetrics:read",
                    Description: "Submitted enterprise metric values.",
                    QueryParams: new[]
                    {
                        ("year", "integer", "Filter by year"),
                        ("month", "integer", "Filter by month 1–12")
                    },
                    ResponseExample: "[ { \"year\": 2026, \"month\": 4, \"metricId\": 1, \"value\": 985000 } ]")
            }),

        new(
            Id: "functional-standards",
            Title: "Functional standards",
            Description: "Government Functional Standards reference data plus product self-assessments. Note the URL literal is functionalstandards (no hyphen) — /api/v1/functional-standards will 404.",
            Endpoints: new ApiEndpointDoc[]
            {
                new(
                    Id: "fs-list",
                    Method: "GET",
                    Path: "/api/v1/functionalstandards",
                    Scope: "FunctionalStandards:read",
                    Description: "All functional standards with their themes (and practice-area counts).",
                    ResponseExample: """
[
  {
    "id": 1,
    "code": "GovS 002",
    "name": "Project delivery",
    "themes": [
      { "id": 4, "name": "Governance", "practiceAreaCount": 3 }
    ]
  }
]
"""),
                new(
                    Id: "fs-get",
                    Method: "GET",
                    Path: "/api/v1/functionalstandards/{id}",
                    Scope: "FunctionalStandards:read",
                    Description: "Single standard with the full Themes → PracticeAreas → Criteria tree.",
                    RouteParams: new[] { ("id", "integer", "Functional standard id") },
                    ResponseExample: """
{
  "id": 1,
  "code": "GovS 002",
  "name": "Project delivery",
  "themes": [
    {
      "id": 4,
      "name": "Governance",
      "practiceAreas": [
        { "id": 11, "name": "Decision making", "criteria": [ { "id": 51, "text": "Decisions are recorded" } ] }
      ]
    }
  ]
}
"""),
                new(
                    Id: "fs-assess-list",
                    Method: "GET",
                    Path: "/api/v1/functionalstandards/assessments",
                    Scope: "FunctionalStandards:read",
                    Description: "Assessment list, optionally filtered by standard id.",
                    QueryParams: new[] { ("functionalStandardId", "integer", "Filter by standard id") },
                    ResponseExample: "[ { \"id\": 21, \"functionalStandardId\": 1, \"fipsId\": \"FIPS-2491\", \"status\": \"in_progress\" } ]"),
                new(
                    Id: "fs-assess-get",
                    Method: "GET",
                    Path: "/api/v1/functionalstandards/assessments/{id}",
                    Scope: "FunctionalStandards:read",
                    Description: "Single assessment with criteria responses.",
                    RouteParams: new[] { ("id", "integer", "Assessment id") },
                    ResponseExample: "{ \"id\": 21, \"status\": \"in_progress\", \"criteriaResponses\": [ { \"criterionId\": 51, \"rating\": \"green\" } ] }"),
                new(
                    Id: "fs-assess-create",
                    Method: "POST",
                    Path: "/api/v1/functionalstandards/assessments",
                    Scope: "FunctionalStandards:create",
                    Description: "Create a new assessment against a standard for a product.",
                    BodyExample: """
{
  "functionalStandardId": 1,
  "fipsId": "FIPS-2491",
  "assessorEmail": "delivery.lead@education.gov.uk",
  "status": "in_progress"
}
""",
                    ResponseExample: "{ \"id\": 22, \"status\": \"in_progress\" }")
            }),

        new(
            Id: "ddt-standards",
            Title: "DDT standards",
            Description: "Digital, data and technology standards catalogue. Read endpoints are anonymous; write endpoints have been deprecated in favour of the modern Standards admin UI.",
            Endpoints: new ApiEndpointDoc[]
            {
                new(
                    Id: "ddt-list",
                    Method: "GET",
                    Path: "/api/v1/DdtStandards",
                    Scope: "Anonymous",
                    Description: "Paged DDT standards. Defaults to published-only when neither stage nor published is supplied.",
                    QueryParams: new[]
                    {
                        ("search", "string", "Free-text search"),
                        ("stage", "string", "draft / in-review / for-approval / published / unpublished"),
                        ("category", "string", "Standards category filter"),
                        ("published", "boolean", "true / false"),
                        ("phase", "string", "Service phase filter"),
                        ("page", "integer", "Page number (default 1)"),
                        ("pageSize", "integer", "Results per page (default 50, max 100)")
                    },
                    ResponseExample: """
{
  "data": [
    { "id": 7, "uuid": "5f3a…", "slug": "service-standards", "name": "Make user-centred services", "stage": "published" }
  ],
  "pagination": { "currentPage": 1, "pageSize": 50, "totalPages": 4, "totalRecords": 178 }
}
"""),
                new(
                    Id: "ddt-by-stage",
                    Method: "GET",
                    Path: "/api/v1/DdtStandards/by-stage",
                    Scope: "Anonymous",
                    Description: "Stage-filtered list. Accepts kebab aliases: drafts, in-review, for-approval, published, unpublished.",
                    QueryParams: new[]
                    {
                        ("stage", "string (required)", "drafts / in-review / for-approval / published / unpublished"),
                        ("search", "string", "Free-text search"),
                        ("category", "string", "Standards category filter"),
                        ("creatorId", "integer", "Filter by creator user id"),
                        ("ownerId", "integer", "Filter by owner user id"),
                        ("contactId", "integer", "Filter by primary contact user id"),
                        ("legalStandard", "boolean", "Legal-standard flag"),
                        ("page", "integer", "Page number (default 1)"),
                        ("pageSize", "integer", "Results per page (default 50)")
                    },
                    ResponseExample: "{ \"data\": [], \"pagination\": { \"currentPage\": 1, \"pageSize\": 50, \"totalPages\": 0, \"totalRecords\": 0 } }"),
                new(
                    Id: "ddt-get",
                    Method: "GET",
                    Path: "/api/v1/DdtStandards/{id}",
                    Scope: "Anonymous",
                    Description: "Full standard including version history and comments.",
                    RouteParams: new[] { ("id", "integer", "Standard id") },
                    ResponseExample: """
{
  "id": 7,
  "name": "Make user-centred services",
  "stage": "published",
  "versions": [ { "version": "1.2.0", "publishedAt": "2026-02-01T09:00:00Z" } ],
  "comments": []
}
"""),
                new(
                    Id: "ddt-by-uuid",
                    Method: "GET",
                    Path: "/api/v1/DdtStandards/uuid/{uuid}",
                    Scope: "Anonymous",
                    Description: "Resolves a UUID to the standard, returning the same shape as the by-id endpoint.",
                    RouteParams: new[] { ("uuid", "string", "Standard UUID") },
                    ResponseExample: "{ \"id\": 7, \"uuid\": \"5f3a…\", \"name\": \"Make user-centred services\" }"),
                new(
                    Id: "ddt-by-id-public",
                    Method: "GET",
                    Path: "/api/v1/DdtStandards/by-id/{id}",
                    Scope: "Anonymous",
                    Description: "Same as /api/v1/DdtStandards/{id} but restricted to published standards.",
                    RouteParams: new[] { ("id", "integer", "Standard id") },
                    ResponseExample: "{ \"id\": 7, \"name\": \"Make user-centred services\", \"stage\": \"published\" }"),
                new(
                    Id: "ddt-by-legacy",
                    Method: "GET",
                    Path: "/api/v1/DdtStandards/by-legacy-id/{legacyId}",
                    Scope: "Anonymous",
                    Description: "Latest published standard with the given legacy id.",
                    RouteParams: new[] { ("legacyId", "string", "Legacy DDT identifier") },
                    ResponseExample: "{ \"id\": 7, \"legacyId\": \"DDT-101\", \"name\": \"Make user-centred services\" }"),
                new(
                    Id: "ddt-by-slug",
                    Method: "GET",
                    Path: "/api/v1/DdtStandards/by-slug/{slug}",
                    Scope: "Anonymous",
                    Description: "Latest published standard with the given slug.",
                    RouteParams: new[] { ("slug", "string", "URL slug") },
                    ResponseExample: "{ \"id\": 7, \"slug\": \"make-user-centred-services\" }"),
                new(
                    Id: "ddt-by-status",
                    Method: "GET",
                    Path: "/api/v1/DdtStandards/by-status/{status}",
                    Scope: "Anonymous (draft requires sign-in)",
                    Description: "Convenience wrapper over /by-stage. Accepts draft / published / unpublished.",
                    RouteParams: new[] { ("status", "string", "draft / published / unpublished") },
                    QueryParams: new[]
                    {
                        ("search", "string", "Free-text search"),
                        ("category", "string", "Standards category filter"),
                        ("page", "integer", "Page number (default 1)"),
                        ("pageSize", "integer", "Results per page (default 50)"),
                        ("fields", "string", "Comma-separated field list to project")
                    },
                    ResponseExample: "{ \"data\": [], \"pagination\": { \"currentPage\": 1, \"pageSize\": 50, \"totalPages\": 0, \"totalRecords\": 0 } }"),
                new(
                    Id: "ddt-validation",
                    Method: "GET",
                    Path: "/api/v1/DdtStandards/{id}/validation-rules",
                    Scope: "Anonymous",
                    Description: "Active validation rules attached to a standard.",
                    RouteParams: new[] { ("id", "integer", "Standard id") },
                    ResponseExample: "[ { \"id\": 4, \"ruleType\": \"regex\", \"expression\": \"^[A-Z]+$\" } ]"),
                new(
                    Id: "ddt-exemptions",
                    Method: "GET",
                    Path: "/api/DdtStandards/Exceptions",
                    Scope: "Anonymous",
                    Description: "Paged list of exemptions (HTML accept headers redirect to /DdtStandards/Exceptions).",
                    QueryParams: new[]
                    {
                        ("standardId", "integer", "Filter by standard id"),
                        ("status", "string", "Exemption status filter"),
                        ("search", "string", "Free-text search"),
                        ("page", "integer", "Page number (default 1)"),
                        ("pageSize", "integer", "Results per page (default 50)")
                    },
                    ResponseExample: "{ \"data\": [], \"pagination\": { \"currentPage\": 1, \"pageSize\": 50, \"totalPages\": 0, \"totalRecords\": 0 } }"),
                new(
                    Id: "ddt-approved-products",
                    Method: "GET",
                    Path: "/api/DdtStandards/ApprovedProducts",
                    Scope: "Anonymous",
                    Description: "Paged approved/tolerated products with linked published standards.",
                    QueryParams: new[]
                    {
                        ("search", "string", "Free-text search"),
                        ("approvalStatus", "string", "approved / tolerated"),
                        ("page", "integer", "Page number"),
                        ("pageSize", "integer", "Results per page")
                    },
                    ResponseExample: "{ \"data\": [], \"pagination\": { \"currentPage\": 1, \"pageSize\": 50, \"totalPages\": 0, \"totalRecords\": 0 } }")
            }),

        new(
            Id: "service-register",
            Title: "Service register (FIPS / CMDB)",
            Description: "Read access to the Service register (FIPS plus CMDB sync data) and lookup catalogues used by the modern Service register UI.",
            Endpoints: new ApiEndpointDoc[]
            {
                new(
                    Id: "sr-products",
                    Method: "GET",
                    Path: "/api/v1/ServiceRegister/products",
                    Scope: "ServiceRegister:read",
                    Description: "Paged FIPS/CMDB product list with rich filters. numericId bypasses status filters (used by onboarding search).",
                    QueryParams: new[]
                    {
                        ("page", "integer", "Page number (default 1)"),
                        ("pageSize", "integer", "Results per page (default 100, max 1000)"),
                        ("status", "string[]", "New / Active / Inactive / Rejected"),
                        ("enterpriseOnly", "boolean", "Enterprise-flagged only"),
                        ("excludeEnterprise", "boolean", "Exclude enterprise-flagged"),
                        ("numericId", "integer", "Filter by numeric CMDB id (ignores status)"),
                        ("categoryIds", "integer[]", "Filter by categorisation item ids"),
                        ("channelIds", "integer[]", "Filter by FIPS channel ids"),
                        ("typeIds", "integer[]", "Filter by FIPS type ids"),
                        ("businessAreaIds", "integer[]", "Filter by business area ids"),
                        ("userGroupIds", "integer[]", "Filter by user group ids"),
                        ("contactRoleIds", "integer[]", "Filter by contact role ids"),
                        ("q", "string", "Free-text search across name / id / description")
                    },
                    ResponseExample: """
{
  "data": [
    {
      "id": "0c64e10b-…",
      "fipsId": "FIPS-2491",
      "name": "Apply for a teacher reference",
      "status": "Active",
      "businessArea": { "id": 5, "name": "Teacher Services" }
    }
  ],
  "pagination": { "currentPage": 1, "pageSize": 100, "totalPages": 9, "totalRecords": 824 }
}
"""),
                new(
                    Id: "sr-products-enterprise",
                    Method: "GET",
                    Path: "/api/v1/ServiceRegister/products/enterprise-active",
                    Scope: "ServiceRegister:read",
                    Description: "Enterprise-flagged products excluding Inactive/Rejected (includes New and Active).",
                    QueryParams: new[]
                    {
                        ("page", "integer", "Page number (default 1)"),
                        ("pageSize", "integer", "Results per page (default 100)"),
                        ("categoryIds", "integer[]", "Filter by categorisation item ids"),
                        ("channelIds", "integer[]", "Filter by FIPS channel ids"),
                        ("typeIds", "integer[]", "Filter by FIPS type ids"),
                        ("businessAreaIds", "integer[]", "Filter by business area ids"),
                        ("userGroupIds", "integer[]", "Filter by user group ids"),
                        ("contactRoleIds", "integer[]", "Filter by contact role ids"),
                        ("q", "string", "Free-text search")
                    },
                    ResponseExample: "{ \"data\": [], \"pagination\": { \"currentPage\": 1, \"pageSize\": 100, \"totalPages\": 0, \"totalRecords\": 0 } }"),
                new(
                    Id: "sr-product",
                    Method: "GET",
                    Path: "/api/v1/ServiceRegister/products/{id}",
                    Scope: "ServiceRegister:read",
                    Description: "Single FIPS/CMDB product with categorisations and contacts.",
                    RouteParams: new[] { ("id", "guid", "Product UUID") },
                    ResponseExample: """
{
  "id": "0c64e10b-…",
  "fipsId": "FIPS-2491",
  "name": "Apply for a teacher reference",
  "status": "Active",
  "categorisations": [ { "groupName": "Audience", "itemName": "Teachers" } ],
  "contacts": [ { "roleName": "Service owner", "userEmail": "owner@education.gov.uk" } ]
}
"""),
                new(
                    Id: "sr-product-patch",
                    Method: "PATCH",
                    Path: "/api/v1/ServiceRegister/products/{id}",
                    Scope: "ServiceRegister:update",
                    Description: "Update productUrl only (max 2000 characters).",
                    RouteParams: new[] { ("id", "guid", "Product UUID") },
                    BodyExample: "{ \"productUrl\": \"https://www.gov.uk/apply-for-a-teacher-reference\" }",
                    ResponseExample: "{ \"id\": \"0c64e10b-…\", \"productUrl\": \"https://www.gov.uk/apply-for-a-teacher-reference\" }"),
                new(
                    Id: "sr-cat-items",
                    Method: "GET",
                    Path: "/api/v1/ServiceRegister/categorisation-items",
                    Scope: "ServiceRegister:read",
                    Description: "All active FIPS categorisation items with their group names.",
                    ResponseExample: "[ { \"id\": 42, \"groupName\": \"Audience\", \"name\": \"Teachers\" } ]"),
                new(
                    Id: "sr-fips",
                    Method: "GET",
                    Path: "/api/v1/ServiceRegister/fips",
                    Scope: "ServiceRegister:read",
                    Description: "Bundle of every FIPS lookup (channels, types, business areas, user groups, contact roles, categorisation groups).",
                    ResponseExample: "{ \"channels\": [], \"types\": [], \"businessAreas\": [], \"userGroups\": [], \"contactRoles\": [], \"categorisationGroups\": [] }"),
                new(
                    Id: "sr-fips-config",
                    Method: "GET",
                    Path: "/api/v1/ServiceRegister/fips/configuration",
                    Scope: "ServiceRegister:read",
                    Description: "Alias of /api/v1/ServiceRegister/fips.",
                    ResponseExample: "{ \"channels\": [], \"types\": [], \"businessAreas\": [], \"userGroups\": [], \"contactRoles\": [], \"categorisationGroups\": [] }"),
                new(
                    Id: "sr-fips-channels",
                    Method: "GET",
                    Path: "/api/v1/ServiceRegister/fips/channels",
                    Scope: "ServiceRegister:read",
                    Description: "FIPS channels lookup.",
                    ResponseExample: "[ { \"id\": 1, \"name\": \"Web\" }, { \"id\": 2, \"name\": \"API\" } ]"),
                new(
                    Id: "sr-fips-types",
                    Method: "GET",
                    Path: "/api/v1/ServiceRegister/fips/types",
                    Scope: "ServiceRegister:read",
                    Description: "FIPS service types lookup.",
                    ResponseExample: "[ { \"id\": 1, \"name\": \"Transactional\" } ]"),
                new(
                    Id: "sr-fips-areas",
                    Method: "GET",
                    Path: "/api/v1/ServiceRegister/fips/business-areas",
                    Scope: "ServiceRegister:read",
                    Description: "FIPS business areas lookup.",
                    ResponseExample: "[ { \"id\": 5, \"name\": \"Teacher Services\" } ]"),
                new(
                    Id: "sr-fips-groups",
                    Method: "GET",
                    Path: "/api/v1/ServiceRegister/fips/user-groups",
                    Scope: "ServiceRegister:read",
                    Description: "FIPS user groups lookup (root groups with children and synonyms).",
                    ResponseExample: "[ { \"id\": 1, \"name\": \"Teachers\", \"children\": [], \"synonyms\": [\"Classroom teachers\"] } ]"),
                new(
                    Id: "sr-fips-roles",
                    Method: "GET",
                    Path: "/api/v1/ServiceRegister/fips/contact-roles",
                    Scope: "ServiceRegister:read",
                    Description: "FIPS contact roles lookup.",
                    ResponseExample: "[ { \"id\": 1, \"name\": \"Service owner\" } ]"),
                new(
                    Id: "sr-fips-cats",
                    Method: "GET",
                    Path: "/api/v1/ServiceRegister/fips/categorisation",
                    Scope: "ServiceRegister:read",
                    Description: "FIPS categorisation groups with nested items.",
                    ResponseExample: "[ { \"id\": 1, \"name\": \"Audience\", \"items\": [ { \"id\": 42, \"name\": \"Teachers\" } ] } ]")
            }),

        new(
            Id: "cms-access",
            Title: "CMS access requests",
            Description: "Submit a request for Operations to provision a CMS account.",
            Endpoints: new ApiEndpointDoc[]
            {
                new(
                    Id: "cms-create",
                    Method: "POST",
                    Path: "/api/v1/cms-access-requests",
                    Scope: "CmsAccessRequests:create",
                    Description: "Submit a CMS access request. Email must be @education.gov.uk. Returns 201 with a Location header.",
                    BodyExample: """
{
  "cmsName": "Service register",
  "requesterEmail": "new.user@education.gov.uk",
  "requesterFullName": "New User",
  "businessArea": "Teacher Services",
  "justification": "Joining the Service register operations team"
}
""",
                    ResponseExample: "{ \"id\": 1234, \"signInUrl\": \"https://service-register.example.gov.uk/sign-in\", \"status\": \"submitted\" }")
            }),

        new(
            Id: "admin-lookups",
            Title: "Admin lookups",
            Description: "Catalogue of every supported lookup (status lists, categories, tiers). Use the catalogue endpoint to discover keys, then fetch items by key.",
            Endpoints: new ApiEndpointDoc[]
            {
                new(
                    Id: "lookups-list",
                    Method: "GET",
                    Path: "/api/v1/admin/lookups",
                    Scope: "AdminLookups:read",
                    Description: "Catalogue of every supported lookup key, with item count and a deep-link URL.",
                    ResponseExample: """
[
  { "key": "risk-statuses", "count": 5, "url": "/api/v1/admin/lookups/risk-statuses" },
  { "key": "issue-severities", "count": 4, "url": "/api/v1/admin/lookups/issue-severities" }
]
"""),
                new(
                    Id: "lookups-items",
                    Method: "GET",
                    Path: "/api/v1/admin/lookups/{key}",
                    Scope: "AdminLookups:read",
                    Description: "Items for one lookup, normalised to AdminLookupItemDto. See the list of valid keys below the explorer.",
                    RouteParams: new[] { ("key", "string", "Lookup key, e.g. risk-statuses") },
                    QueryParams: new[] { ("includeInactive", "boolean", "Include inactive items (default false)") },
                    ResponseExample: """
[
  { "id": 1, "code": "open", "name": "Open", "sortOrder": 10, "isActive": true },
  { "id": 2, "code": "treating", "name": "Treating", "sortOrder": 20, "isActive": true }
]
""")
            }),

        new(
            Id: "health",
            Title: "Health",
            Description: "Lightweight liveness probe.",
            Endpoints: new ApiEndpointDoc[]
            {
                new(
                    Id: "health",
                    Method: "GET",
                    Path: "/api/health",
                    Scope: "Anonymous",
                    Description: "Verify API availability and timestamp.",
                    ResponseExample: "{ \"status\": \"healthy\", \"timestamp\": \"2026-05-19T20:30:00Z\" }")
            })
    };
}
