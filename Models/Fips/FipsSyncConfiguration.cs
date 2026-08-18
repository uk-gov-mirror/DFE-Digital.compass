namespace Compass.Models.Fips;

/// <summary>
/// Configuration for FIPS sync operations
/// </summary>
public class FipsSyncConfiguration
{
    public CmdbConfiguration Cmdb { get; set; } = new();
    public StrapiEnvironments Strapi { get; set; } = new();
    public SasConfiguration Sas { get; set; } = new();
    public AissConfiguration Aiss { get; set; } = new();

    /// <summary>When true, Compass runs bulk CMDB → service register sync once per UK day.</summary>
    public bool DailySyncEnabled { get; set; } = true;

    /// <summary>Hour of the UK day (0–23) to start the scheduled bulk sync. Default 6 (06:00).</summary>
    public int DailySyncHourUk { get; set; } = 6;

    /// <summary>Recipient for the daily sync summary email.</summary>
    public string DailySyncSummaryEmail { get; set; } = "fips.service@education.gov.uk";
}

public class CmdbConfiguration
{
    public string Endpoint { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class StrapiEnvironments
{
    public StrapiEnvironmentConfig Development { get; set; } = new();
    public StrapiEnvironmentConfig Test { get; set; } = new();
    public StrapiEnvironmentConfig Production { get; set; } = new();
}

public class StrapiEnvironmentConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

public class SasConfiguration
{
    public string Endpoint { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
}

public class AissConfiguration
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>AISS web app origin for UI links (no path), e.g. http://localhost:5418.</summary>
    public string WebBaseUrl { get; set; } = string.Empty;

    public string ResolveWebBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(WebBaseUrl))
            return WebBaseUrl.Trim().TrimEnd('/');

        var endpoint = Endpoint?.Trim();
        if (string.IsNullOrEmpty(endpoint))
            return "https://accessibility-statements.education.gov.uk";

        if (endpoint.EndsWith("/api/", StringComparison.OrdinalIgnoreCase))
            endpoint = endpoint[..^5];
        else if (endpoint.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            endpoint = endpoint[..^4];

        return endpoint.TrimEnd('/');
    }
}
