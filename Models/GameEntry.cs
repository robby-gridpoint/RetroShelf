using System.Text.Json.Serialization;

namespace RetroShelf.Models;

public sealed class GameEntry
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string InstallDirectory { get; init; }
    public required string ExecutablePath { get; set; }
    public required string SourceArchive { get; init; }
    public DateTimeOffset InstalledAt { get; init; }
    public DateTimeOffset? LastPlayedAt { get; set; }
    public int PlayCount { get; set; }
    public long TotalPlayTimeSeconds { get; set; }
    public bool IsFavorite { get; set; }
    public string LaunchArguments { get; set; } = string.Empty;

    [JsonIgnore]
    public CompatibilityInfo? Compatibility { get; set; }

    [JsonIgnore]
    public bool HasCompatibility => Compatibility is not null;

    [JsonIgnore]
    public string CompatibilityBadge => Compatibility?.CompatibilityScore is int score ? $"{score}%" : "Unknown";

    [JsonIgnore]
    public string LastPlayedDisplay => LastPlayedAt?.LocalDateTime.ToString("g") ?? "Never";

    [JsonIgnore]
    public string PlayTimeDisplay
    {
        get
        {
            TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, TotalPlayTimeSeconds));
            if (duration.TotalSeconds > 0 && duration.TotalMinutes < 1)
            {
                return "< 1 min";
            }

            if (duration.TotalHours < 1)
            {
                return $"{(int)duration.TotalMinutes} min";
            }

            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }
    }
}