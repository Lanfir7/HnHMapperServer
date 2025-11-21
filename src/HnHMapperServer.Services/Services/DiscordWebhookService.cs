using System.Net.Http.Json;
using System.Text.Json;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.DTOs;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Implementation of Discord webhook service.
/// Sends notifications to Discord via webhooks with rich embed formatting.
/// </summary>
public class DiscordWebhookService : IDiscordWebhookService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DiscordWebhookService> _logger;
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IMapPreviewService _mapPreviewService;

    public DiscordWebhookService(
        IHttpClientFactory httpClientFactory,
        ILogger<DiscordWebhookService> logger,
        ApplicationDbContext db,
        IConfiguration configuration,
        IMapPreviewService mapPreviewService)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _db = db;
        _configuration = configuration;
        _mapPreviewService = mapPreviewService;
    }

    /// <summary>
    /// Send a notification to Discord via webhook.
    /// </summary>
    public async Task<bool> SendNotificationAsync(NotificationDto notification, string webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            _logger.LogWarning("Discord webhook URL is empty, skipping notification");
            return false;
        }

        try
        {
            var embedData = await BuildEmbedAsync(notification, webhookUrl);

            // Use the EXACT same pattern as TestWebhookAsync (which works)
            // Build embed as anonymous object, not Dictionary
            var embed = new
            {
                title = embedData.Title,
                description = embedData.Description,
                color = embedData.Color,
                footer = new { text = embedData.Footer!.Text },
                timestamp = embedData.Timestamp
            };

            var payload = new
            {
                embeds = new[] { embed }
            };

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            // Debug logging - log the exact object before serialization
            _logger.LogDebug("Embed object: Title={Title}, Description={Description}, Color={Color}, HasFooter={HasFooter}, Timestamp={Timestamp}",
                embed.title, embed.description, embed.color, embed.footer != null, embed.timestamp);

            // Use PostAsJsonAsync like the test webhook (which works)
            var response = await httpClient.PostAsJsonAsync(webhookUrl, payload);

            // Log response for debugging
            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("Discord response: Status={StatusCode}, Body={Body}", response.StatusCode, responseBody);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Successfully sent Discord notification {NotificationId} to webhook",
                    notification.Id);
                return true;
            }
            else
            {
                _logger.LogWarning(
                    "Failed to send Discord notification {NotificationId}. Status: {StatusCode}, Response: {Response}",
                    notification.Id, response.StatusCode, responseBody);
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "HTTP error sending Discord notification {NotificationId}: {Message}",
                notification.Id, ex.Message);
            return false;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex,
                "Timeout sending Discord notification {NotificationId}",
                notification.Id);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error sending Discord notification {NotificationId}: {Message}",
                notification.Id, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Test a Discord webhook URL by sending a test message.
    /// </summary>
    public async Task<bool> TestWebhookAsync(string webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            _logger.LogWarning("Cannot test empty Discord webhook URL");
            return false;
        }

        try
        {
            var testEmbed = new
            {
                title = "✅ Test Notification",
                description = "Your Discord webhook is configured correctly! You will receive notifications here when timers expire.",
                color = 3066993, // Green color
                footer = new
                {
                    text = "HavenMap Discord Integration"
                },
                timestamp = DateTime.UtcNow.ToString("O")
            };

            var payload = new
            {
                embeds = new[] { testEmbed }
            };

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var response = await httpClient.PostAsJsonAsync(webhookUrl, payload);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Discord webhook test successful");
                return true;
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "Discord webhook test failed. Status: {StatusCode}, Response: {Response}",
                    response.StatusCode, responseBody);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing Discord webhook: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Build a Discord embed object from a notification.
    /// </summary>
    private async Task<DiscordEmbedDto> BuildEmbedAsync(NotificationDto notification, string webhookUrl)
    {
        // Determine emoji based on notification type and priority
        var emoji = notification.Type switch
        {
            "MarkerTimerExpired" => "🔔",
            "StandaloneTimerExpired" => "🔔",
            "TimerPreExpiryWarning" => notification.Priority switch
            {
                "Urgent" => "⚠️",      // 10 minutes - warning sign
                "High" => "⏱️",        // 1 hour - stopwatch
                "Normal" when notification.Title?.Contains("4 hours") == true => "⏰",  // 4 hours - alarm clock
                "Normal" when notification.Title?.Contains("1 day") == true => "📅",    // 1 day - calendar
                _ => "⏰"
            },
            _ => "📢"
        };

        // Determine color based on priority
        var color = notification.Priority switch
        {
            "Urgent" => 15548997,  // Red (#ED4245)
            "High" => 16753920,    // Orange (#FFA500)
            _ => 3447003           // Blue (#3498DB)
        };

        // Format notification type for display
        var typeDisplay = notification.Type switch
        {
            "MarkerTimerExpired" => "Timer Expired",
            "StandaloneTimerExpired" => "Timer Expired",
            "TimerPreExpiryWarning" => "Timer Warning",
            _ => notification.Type
        };

        // Get base URL for map links
        var baseUrl = _configuration["Discord:BaseUrl"] ?? _configuration["Kestrel:Endpoints:Http:Url"] ?? "http://localhost";
        baseUrl = baseUrl.TrimEnd('/');

        // Detect localhost for development warning
        var isLocalhost = baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                          baseUrl.Contains("127.0.0.1");

        // Try to parse marker data for enhanced formatting
        string? iconUrl = null;
        string? mapUrl = null;
        string? markerName = null;

        if (notification.ActionType == "NavigateToMarker" && !string.IsNullOrWhiteSpace(notification.ActionData))
        {
            try
            {
                var actionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(notification.ActionData);
                if (actionData != null)
                {
                    int? markerId = actionData.TryGetValue("markerId", out var markerIdElement) && markerIdElement.ValueKind != JsonValueKind.Null
                        ? markerIdElement.GetInt32() : null;
                    int? customMarkerId = actionData.TryGetValue("customMarkerId", out var customMarkerIdElement) && customMarkerIdElement.ValueKind != JsonValueKind.Null
                        ? customMarkerIdElement.GetInt32() : null;

                    // Fetch marker details from database
                    if (customMarkerId.HasValue)
                    {
                        var customMarker = await _db.CustomMarkers
                            .Where(m => m.Id == customMarkerId.Value && m.TenantId == notification.TenantId)
                            .FirstOrDefaultAsync();

                        if (customMarker != null)
                        {
                            markerName = customMarker.Title;
                            iconUrl = $"{baseUrl}/icons/{customMarker.Icon}";
                            mapUrl = $"{baseUrl}/map?mapid={customMarker.MapId}&x={customMarker.CoordX}&y={customMarker.CoordY}";
                        }
                    }
                    else if (markerId.HasValue)
                    {
                        var marker = await _db.Markers
                            .Where(m => m.Id == markerId.Value && m.TenantId == notification.TenantId)
                            .FirstOrDefaultAsync();

                        if (marker != null)
                        {
                            // Look up grid to get MapId and coordinates
                            var grid = await _db.Grids
                                .Where(g => g.Id == marker.GridId && g.TenantId == notification.TenantId)
                                .FirstOrDefaultAsync();

                            if (grid != null)
                            {
                                markerName = marker.Name;
                                iconUrl = $"{baseUrl}/icons/{marker.Image}";
                                // Grid ID format is "x_y" so split to get coordinates
                                var gridCoords = marker.GridId.Split('_');
                                if (gridCoords.Length == 2)
                                {
                                    mapUrl = $"{baseUrl}/map?mapid={grid.Map}&x={gridCoords[0]}&y={gridCoords[1]}";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse marker data for Discord notification {NotificationId}", notification.Id);
            }
        }

        // Prepare preview URL before building embed
        string? previewUrl = null;

        // Generate map preview for marker-based notifications
        if (notification.ActionType == "NavigateToMarker" && !string.IsNullOrWhiteSpace(notification.ActionData))
        {
            try
            {
                var actionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(notification.ActionData);
                if (actionData != null)
                {
                    int? customMarkerId = actionData.TryGetValue("customMarkerId", out var customMarkerIdElement) && customMarkerIdElement.ValueKind != JsonValueKind.Null
                        ? customMarkerIdElement.GetInt32() : null;
                    int? markerId = actionData.TryGetValue("markerId", out var markerIdElement) && markerIdElement.ValueKind != JsonValueKind.Null
                        ? markerIdElement.GetInt32() : null;

                    // Generate preview for custom markers
                    if (customMarkerId.HasValue)
                    {
                        var customMarker = await _db.CustomMarkers
                            .Where(m => m.Id == customMarkerId.Value && m.TenantId == notification.TenantId)
                            .FirstOrDefaultAsync();

                        if (customMarker != null)
                        {
                            var signedUrl = await _mapPreviewService.GenerateMarkerPreviewAsync(
                                customMarker.MapId,
                                customMarker.CoordX,
                                customMarker.CoordY,
                                customMarker.X,
                                customMarker.Y,
                                notification.TenantId,
                                webhookUrl);

                            previewUrl = $"{baseUrl}{signedUrl}";

                            _logger.LogDebug("Added signed map preview to Discord notification {NotificationId}: {PreviewUrl}",
                                notification.Id, previewUrl);
                        }
                    }
                    // Generate preview for regular markers
                    else if (markerId.HasValue)
                    {
                        var marker = await _db.Markers
                            .Where(m => m.Id == markerId.Value && m.TenantId == notification.TenantId)
                            .FirstOrDefaultAsync();

                        if (marker != null)
                        {
                            var grid = await _db.Grids
                                .Where(g => g.Id == marker.GridId && g.TenantId == notification.TenantId)
                                .FirstOrDefaultAsync();

                            if (grid != null)
                            {
                                // Parse grid coordinates from GridId (format: "x_y")
                                var gridCoords = marker.GridId.Split('_');
                                if (gridCoords.Length == 2 &&
                                    int.TryParse(gridCoords[0], out var gridX) &&
                                    int.TryParse(gridCoords[1], out var gridY))
                                {
                                    var signedUrl = await _mapPreviewService.GenerateMarkerPreviewAsync(
                                        grid.Map,
                                        gridX,
                                        gridY,
                                        marker.PositionX,
                                        marker.PositionY,
                                        notification.TenantId,
                                        webhookUrl);

                                    previewUrl = $"{baseUrl}{signedUrl}";

                                    _logger.LogDebug("Added signed map preview to Discord notification {NotificationId}: {PreviewUrl}",
                                        notification.Id, previewUrl);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail notification if preview generation fails
                _logger.LogWarning(ex, "Failed to generate map preview for Discord notification {NotificationId}", notification.Id);
            }
        }

        // Build the embed using Discord DTO for clean JSON serialization
        // Add localhost warning to description if running locally
        var description = notification.Message;
        if (isLocalhost && (!string.IsNullOrEmpty(iconUrl) || !string.IsNullOrEmpty(previewUrl)))
        {
            description += "\n\n⚠️ *Running on localhost - images cannot be displayed. Deploy to production to see map previews and icons.*";
        }

        // Create embed DTO - null fields are automatically excluded by JsonIgnoreCondition
        // IMPORTANT: Exclude URL, thumbnail and image on localhost because Discord will reject the embed if it can't fetch the URLs
        var embed = new DiscordEmbedDto
        {
            Title = !string.IsNullOrEmpty(mapUrl)
                ? $"{emoji} {markerName ?? notification.Title}"
                : $"{emoji} {notification.Title}",
            Description = description,
            Color = color,
            Url = !isLocalhost && !string.IsNullOrEmpty(mapUrl) ? mapUrl : null,
            Thumbnail = !isLocalhost && !string.IsNullOrEmpty(iconUrl) ? new DiscordEmbedThumbnail { Url = iconUrl } : null,
            Image = !isLocalhost && !string.IsNullOrEmpty(previewUrl) ? new DiscordEmbedImage { Url = previewUrl } : null,
            Footer = new DiscordEmbedFooter { Text = "HavenMap Notification" },
            Timestamp = notification.CreatedAt.ToString("O")  // ISO 8601 format
        };

        return embed;
    }
}
