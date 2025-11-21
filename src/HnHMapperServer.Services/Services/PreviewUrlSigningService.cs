using HnHMapperServer.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Implementation of preview URL signing service.
/// Uses HMAC-SHA256 signatures derived from Discord webhook URLs to secure preview access.
/// </summary>
public class PreviewUrlSigningService : IPreviewUrlSigningService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<PreviewUrlSigningService> _logger;
    private const string CACHE_KEY_PREFIX = "PreviewSigningKey_";
    private static readonly TimeSpan CACHE_DURATION = TimeSpan.FromHours(24);

    public PreviewUrlSigningService(
        IMemoryCache cache,
        ILogger<PreviewUrlSigningService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Generate a signed preview URL with HMAC signature and expiration.
    /// </summary>
    public string GenerateSignedUrl(string previewId, string webhookUrl, TimeSpan validityDuration)
    {
        if (string.IsNullOrWhiteSpace(previewId))
            throw new ArgumentException("Preview ID cannot be empty", nameof(previewId));

        if (string.IsNullOrWhiteSpace(webhookUrl))
            throw new ArgumentException("Webhook URL cannot be empty", nameof(webhookUrl));

        // Calculate expiration timestamp
        var expiresAt = DateTimeOffset.UtcNow.Add(validityDuration);
        var expiresTimestamp = expiresAt.ToUnixTimeSeconds().ToString();

        // Generate HMAC signature
        var signature = ComputeHmac(previewId, expiresTimestamp, webhookUrl);

        // Return signed URL path with query parameters
        return $"/map/preview/{previewId}?expires={expiresTimestamp}&sig={signature}";
    }

    /// <summary>
    /// Validate a signed preview URL by checking HMAC signature and expiration.
    /// </summary>
    public bool ValidateSignedUrl(string previewId, string expiresTimestamp, string signature, string webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(previewId) ||
            string.IsNullOrWhiteSpace(expiresTimestamp) ||
            string.IsNullOrWhiteSpace(signature) ||
            string.IsNullOrWhiteSpace(webhookUrl))
        {
            _logger.LogWarning("Signature validation failed: missing required parameters");
            return false;
        }

        // Check expiration
        if (!long.TryParse(expiresTimestamp, out var expiresUnix))
        {
            _logger.LogWarning("Signature validation failed: invalid expiration timestamp");
            return false;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresUnix);
        if (expiresAt < DateTimeOffset.UtcNow)
        {
            _logger.LogDebug("Signature validation failed: URL expired at {ExpiresAt}", expiresAt);
            return false;
        }

        // Compute expected signature
        var expectedSignature = ComputeHmac(previewId, expiresTimestamp, webhookUrl);

        // Constant-time comparison to prevent timing attacks
        if (!ConstantTimeEquals(signature, expectedSignature))
        {
            _logger.LogWarning("Signature validation failed: HMAC mismatch for preview {PreviewId}", previewId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Compute HMAC-SHA256 signature for preview URL.
    /// </summary>
    private string ComputeHmac(string previewId, string expiresTimestamp, string webhookUrl)
    {
        var signingKey = DeriveSigningKey(webhookUrl);
        var payload = $"{previewId}:{expiresTimestamp}";

        using var hmac = new HMACSHA256(signingKey);
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Derive signing key from webhook URL using SHA-256.
    /// Uses caching to avoid repeated hashing of the same webhook URL.
    /// </summary>
    private byte[] DeriveSigningKey(string webhookUrl)
    {
        // Create cache key from webhook URL hash (don't store URLs in cache keys)
        var urlHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(webhookUrl)));
        var cacheKey = $"{CACHE_KEY_PREFIX}{urlHash}";

        // Try to get from cache
        if (_cache.TryGetValue<byte[]>(cacheKey, out var cachedKey) && cachedKey != null)
        {
            return cachedKey;
        }

        // Derive key from webhook URL using SHA-256
        // This makes each tenant's key unique based on their webhook URL
        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(webhookUrl));

        // Cache for 24 hours
        _cache.Set(cacheKey, keyBytes, CACHE_DURATION);

        _logger.LogDebug("Derived new signing key from webhook URL (cached for 24h)");

        return keyBytes;
    }

    /// <summary>
    /// Constant-time string comparison to prevent timing attacks.
    /// </summary>
    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length)
            return false;

        var result = 0;
        for (var i = 0; i < a.Length; i++)
        {
            result |= a[i] ^ b[i];
        }

        return result == 0;
    }
}
