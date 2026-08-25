using System.Text.Json.Serialization;

namespace RetroShelf.Models;

public sealed class CompatibilityInfo
{
    [JsonPropertyName("runnable")]
    public bool? Runnable { get; init; }

    [JsonPropertyName("compilable")]
    public bool? Compilable { get; init; }

    [JsonPropertyName("compatibility_score")]
    public int? CompatibilityScore { get; init; }

    [JsonPropertyName("compatibility_notes")]
    public string? CompatibilityNotes { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("executable_name")]
    public string ExecutableName { get; init; } = string.Empty;
}
