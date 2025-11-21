namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Service for signing and validating map preview URLs using HMAC signatures.
/// Uses Discord webhook URLs as tenant-specific secrets to derive signing keys.
/// </summary>
public interface IPreviewUrlSigningService
{
    /// <summary>
    /// Generate a signed preview URL with expiration.
    /// </summary>
    /// <param name="previewId">Preview file ID</param>
    /// <param name="webhookUrl">Discord webhook URL (used as tenant-specific secret)</param>
    /// <param name="validityDuration">How long the URL should be valid</param>
    /// <returns>Signed URL with expires and sig query parameters</returns>
    string GenerateSignedUrl(string previewId, string webhookUrl, TimeSpan validityDuration);

    /// <summary>
    /// Validate a signed preview URL.
    /// </summary>
    /// <param name="previewId">Preview file ID from URL path</param>
    /// <param name="expiresTimestamp">Expiration timestamp from query string</param>
    /// <param name="signature">HMAC signature from query string</param>
    /// <param name="webhookUrl">Discord webhook URL to derive signing key</param>
    /// <returns>True if signature is valid and not expired</returns>
    bool ValidateSignedUrl(string previewId, string expiresTimestamp, string signature, string webhookUrl);
}
