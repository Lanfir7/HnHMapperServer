using System.Text.Json.Serialization;

namespace HnHMapperServer.Services.DTOs;

/// <summary>
/// Discord webhook embed structure for rich message formatting.
/// See: https://discord.com/developers/docs/resources/channel#embed-object
/// </summary>
public class DiscordEmbedDto
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("color")]
    public int? Color { get; set; }

    [JsonPropertyName("footer")]
    public DiscordEmbedFooter? Footer { get; set; }

    [JsonPropertyName("thumbnail")]
    public DiscordEmbedThumbnail? Thumbnail { get; set; }

    [JsonPropertyName("image")]
    public DiscordEmbedImage? Image { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

/// <summary>
/// Discord embed footer.
/// </summary>
public class DiscordEmbedFooter
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Discord embed thumbnail (small image, top-right).
/// </summary>
public class DiscordEmbedThumbnail
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Discord embed image (large image, full-width).
/// </summary>
public class DiscordEmbedImage
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
