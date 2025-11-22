namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Service for generating map preview images for Discord notifications.
/// Creates composite images from map tiles with marker location indicators.
/// </summary>
public interface IMapPreviewService
{
    /// <summary>
    /// Generate a map preview image showing tiles around a marker location.
    /// Creates a 4x4 grid of tiles (400x400px) with the marker icon at the exact position.
    /// Returns a signed URL with 48-hour expiration.
    /// </summary>
    /// <param name="mapId">Map ID containing the marker</param>
    /// <param name="markerCoordX">Grid X coordinate of the marker</param>
    /// <param name="markerCoordY">Grid Y coordinate of the marker</param>
    /// <param name="markerX">Relative X position within grid (0-100)</param>
    /// <param name="markerY">Relative Y position within grid (0-100)</param>
    /// <param name="tenantId">Tenant ID for isolation</param>
    /// <param name="webhookUrl">Discord webhook URL (used for signature generation)</param>
    /// <param name="iconPath">Optional icon path (e.g., "gfx/terobjs/mm/geyser.png") to render on preview</param>
    /// <returns>Signed preview URL path with query parameters</returns>
    Task<string> GenerateMarkerPreviewAsync(
        int mapId,
        int markerCoordX,
        int markerCoordY,
        int markerX,
        int markerY,
        string tenantId,
        string webhookUrl,
        string? iconPath = null);

    /// <summary>
    /// Get the file path for a preview image.
    /// </summary>
    /// <param name="previewId">Preview ID returned from GenerateMarkerPreviewAsync</param>
    /// <param name="tenantId">Tenant ID for validation</param>
    /// <returns>Full file path to the preview image, or null if not found</returns>
    Task<string?> GetPreviewPathAsync(string previewId, string tenantId);

    /// <summary>
    /// Delete preview images older than the retention period (2 days).
    /// </summary>
    /// <returns>Number of files deleted</returns>
    Task<int> CleanupOldPreviewsAsync();
}
