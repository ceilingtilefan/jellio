using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellio.Models;

public class PlaybackProgressRequest
{
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; set; }

    [JsonPropertyName("positionTicks")]
    public required long PositionTicks { get; set; }

    [JsonPropertyName("isPaused")]
    public bool IsPaused { get; set; }
} 