using System.Security.Cryptography;
using Compass.Data;
using Compass.Models;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services;

public class ApiTokenService : IApiTokenService
{
    private readonly CompassDbContext _context;
    private readonly ILogger<ApiTokenService> _logger;

    public ApiTokenService(CompassDbContext context, ILogger<ApiTokenService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<ApiToken?> GetByTokenAsync(string token)
    {
        return await _context.ApiTokens
            .Include(t => t.Permissions)
            .FirstOrDefaultAsync(t => t.Token == token && t.IsActive);
    }

    public async Task<List<ApiToken>> GetAllTokensAsync()
    {
        return await _context.ApiTokens
            .Include(t => t.Permissions)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<ApiToken?> GetByIdAsync(int id)
    {
        return await _context.ApiTokens
            .Include(t => t.Permissions)
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<ApiToken> CreateTokenAsync(string name, string description, string createdByEmail, DateTime? expiresAt = null)
    {
        var token = new ApiToken
        {
            Name = name,
            Description = description,
            Token = GenerateSecureToken(),
            CreatedByEmail = createdByEmail,
            ExpiresAt = expiresAt,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.ApiTokens.Add(token);
        await _context.SaveChangesAsync();

        _logger.LogInformation("API token created: {TokenName} by {CreatedBy}", name, createdByEmail);

        return token;
    }

    public async Task<string> RecycleTokenAsync(int id)
    {
        var token = await _context.ApiTokens.FindAsync(id);
        if (token == null)
        {
            throw new InvalidOperationException($"Token with ID {id} not found");
        }

        var newTokenValue = GenerateSecureToken();
        token.Token = newTokenValue;
        
        await _context.SaveChangesAsync();

        _logger.LogInformation("API token recycled: {TokenId}", id);

        return newTokenValue;
    }

    public async Task<bool> DeleteTokenAsync(int id)
    {
        var token = await _context.ApiTokens.FindAsync(id);
        if (token == null)
        {
            return false;
        }

        _context.ApiTokens.Remove(token);
        await _context.SaveChangesAsync();

        _logger.LogInformation("API token deleted: {TokenId}", id);

        return true;
    }

    public async Task<bool> UpdateTokenStatusAsync(int id, bool isActive)
    {
        var token = await _context.ApiTokens.FindAsync(id);
        if (token == null)
        {
            return false;
        }

        token.IsActive = isActive;
        await _context.SaveChangesAsync();

        _logger.LogInformation("API token status updated: {TokenId} - Active: {IsActive}", id, isActive);

        return true;
    }

    public async Task<bool> UpdateLastUsedAsync(int tokenId)
    {
        var token = await _context.ApiTokens.FindAsync(tokenId);
        if (token == null)
        {
            return false;
        }

        token.LastUsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> SetPermissionsAsync(int tokenId, Dictionary<string, (bool read, bool create, bool update, bool delete)> permissions)
    {
        var token = await _context.ApiTokens
            .Include(t => t.Permissions)
            .FirstOrDefaultAsync(t => t.Id == tokenId);

        if (token == null)
        {
            return false;
        }

        // Remove existing permissions
        _context.ApiTokenPermissions.RemoveRange(token.Permissions);

        // Add new permissions
        foreach (var permission in permissions)
        {
            token.Permissions.Add(new ApiTokenPermission
            {
                ApiTokenId = tokenId,
                Resource = permission.Key,
                CanRead = permission.Value.read,
                CanCreate = permission.Value.create,
                CanUpdate = permission.Value.update,
                CanDelete = permission.Value.delete
            });
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation("API token permissions updated: {TokenId}", tokenId);

        return true;
    }

    public async Task<Dictionary<string, (bool read, bool create, bool update, bool delete)>> GetPermissionsAsync(int tokenId)
    {
        var permissions = await _context.ApiTokenPermissions
            .Where(p => p.ApiTokenId == tokenId)
            .ToListAsync();

        return permissions.ToDictionary(
            p => p.Resource,
            p => (p.CanRead, p.CanCreate, p.CanUpdate, p.CanDelete)
        );
    }

    public bool ValidatePermission(ApiToken token, string resource, string operation)
    {
        // Check if token is expired
        if (token.ExpiresAt.HasValue && token.ExpiresAt.Value < DateTime.UtcNow)
        {
            return false;
        }

        // Check if token is active
        if (!token.IsActive)
        {
            return false;
        }

        var permission = token.Permissions.FirstOrDefault(p =>
            string.Equals(p.Resource, resource, StringComparison.OrdinalIgnoreCase));

        return operation.ToLowerInvariant() switch
        {
            "read" or "get" => permission?.CanRead == true
                || (permission == null && Compass.Services.Api.ApiTokenResourceCatalog.GrantsRead(token.Permissions, resource)),
            "create" or "post" => permission?.CanCreate == true,
            "update" or "put" or "patch" => permission?.CanUpdate == true,
            "delete" => permission?.CanDelete == true,
            _ => false
        };
    }

    private static string GenerateSecureToken()
    {
        // Generate a secure random token
        const string prefix = "cps_"; // COMPASS prefix
        var randomBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randomBytes);
        }
        return prefix + Convert.ToBase64String(randomBytes).Replace("+", "").Replace("/", "").Replace("=", "");
    }
}

