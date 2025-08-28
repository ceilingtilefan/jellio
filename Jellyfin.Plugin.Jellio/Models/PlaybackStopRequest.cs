using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellio.Models;

public class PlaybackStopRequest
{
    [JsonPropertyName("itemId")]
    public required System.Guid ItemId { get; set; }

    [JsonPropertyName("positionTicks")]
    public required long PositionTicks { get; set; }
} 