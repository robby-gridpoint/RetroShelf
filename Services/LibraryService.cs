using System.IO;
using System.IO.Compression;
using System.Text.Json;
using RetroShelf.Models;

namespace RetroShelf.Services;

public sealed class LibraryService
{
    private const long MaximumExpandedSize = 10L * 1024 * 1024 * 1024;
    private const int MaximumEntryCount = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string libraryPath;
    public readonly string gamesDirectory;
    private readonly string stagingDirectory;

    public LibraryService()
    {
        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RetroShelf");

        libraryPath = Path.Combine(dataDirectory, "library.json");
        gamesDirectory = Path.Combine(dataDirectory, "Games");
        stagingDirectory = Path.Combine(dataDirectory, "Staging");
    }

    public IReadOnlyList<GameEntry> Load()
    {
        if (!File.Exists(libraryPath))
        {
            return [];
        }

        try
        {
            string json = File.ReadAllText(libraryPath);
            return JsonSerializer.Deserialize<List<GameEntry>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            string backupPath = $"{libraryPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(libraryPath, backupPath);
            return [];
        }
    }

    public Task<GameEntry> InstallAsync(
        string archivePath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Install(archivePath, progress, cancellationToken), cancellationToken);
    }

    public void Save(IEnumerable<GameEntry> games)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
        string temporaryPath = $"{libraryPath}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(games, JsonOptions));
        File.Move(temporaryPath, libraryPath, true);
    }

    public void Uninstall(GameEntry game)
    {
        if (Directory.Exists(game.InstallDirectory))
        {
            Directory.Delete(game.InstallDirectory, true);
        }
    }

    private GameEntry Install(
        string archivePath,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new InstallProgress("Checking archive", Path.GetFileName(archivePath), 2));

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The selected ZIP could not be found.", archivePath);
        }

        if (!string.Equals(Path.GetExtension(archivePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("RetroShelf currently installs ZIP archives only.");
        }

        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(gamesDirectory);

        string gameId = Guid.NewGuid().ToString("N");
        string temporaryDirectory = Path.Combine(stagingDirectory, gameId);
        string gameName = Path.GetFileNameWithoutExtension(archivePath).Trim();
        string installDirectory = Path.Combine(gamesDirectory, $"{MakeSafeName(gameName)}-{gameId[..8]}");

        try
        {
            ExtractArchive(archivePath, temporaryDirectory, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new InstallProgress("Finding game executable", string.Empty, 94));

            string executable = FindExecutable(temporaryDirectory, gameName)
                ?? throw new InvalidDataException("The ZIP does not contain a Windows executable.");

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new InstallProgress("Finalizing installation", string.Empty, 98));
            string relativeExecutable = Path.GetRelativePath(temporaryDirectory, executable);
            Directory.Move(temporaryDirectory, installDirectory);

            progress?.Report(new InstallProgress("Installation complete", string.Empty, 100));

            return new GameEntry
            {
                Id = gameId,
                Name = gameName,
                InstallDirectory = installDirectory,
                ExecutablePath = Path.Combine(installDirectory, relativeExecutable),
                SourceArchive = Path.GetFullPath(archivePath),
                InstalledAt = DateTimeOffset.Now
            };
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }

            throw;
        }
    }

    private static void ExtractArchive(
        string archivePath,
        string destinationDirectory,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        string destinationRoot = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException("The ZIP contains too many files.");
        }

        long totalSize = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalSize = checked(totalSize + entry.Length);
            if (totalSize > MaximumExpandedSize)
            {
                throw new InvalidDataException("The ZIP expands beyond the 10 GB install limit.");
            }

            string outputPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The ZIP contains an unsafe file path.");
            }
        }

        long extractedSize = 0;
        int lastReportedPercentage = -1;
        byte[] buffer = new byte[128 * 1024];

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string outputPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using Stream input = entry.Open();
            using FileStream output = new(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.SequentialScan);

            int bytesRead;
            while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, bytesRead);
                extractedSize += bytesRead;

                int percentage = totalSize == 0
                    ? 92
                    : 5 + (int)Math.Round((double)extractedSize / totalSize * 87);
                if (percentage == lastReportedPercentage)
                {
                    continue;
                }

                lastReportedPercentage = percentage;
                progress?.Report(new InstallProgress("Extracting files", entry.FullName, percentage));
            }
        }

        progress?.Report(new InstallProgress("Verifying installation", string.Empty, 92));
    }

    private static string? FindExecutable(string rootDirectory, string gameName)
    {
        string normalizedName = NormalizeName(gameName);

        return Directory
            .EnumerateFiles(rootDirectory, "*.exe", SearchOption.AllDirectories)
            .Where(path => !IsUtilityExecutable(path))
            .OrderByDescending(path => ScoreExecutable(path, rootDirectory, normalizedName))
            .ThenBy(path => path.Length)
            .FirstOrDefault();
    }

    private static int ScoreExecutable(string path, string rootDirectory, string normalizedGameName)
    {
        string executableName = NormalizeName(Path.GetFileNameWithoutExtension(path));
        string relativePath = Path.GetRelativePath(rootDirectory, path);
        int score = executableName == normalizedGameName ? 100 : 0;
        score += relativePath.Count(character => character == Path.DirectorySeparatorChar) == 0 ? 40 : 0;
        score += executableName.Contains(normalizedGameName, StringComparison.OrdinalIgnoreCase) ? 20 : 0;
        return score;
    }

    private static bool IsUtilityExecutable(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Contains("unins", StringComparison.OrdinalIgnoreCase)
            || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            || name.Contains("crashpad", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vcredist", StringComparison.OrdinalIgnoreCase)
            || name.Contains("dxsetup", StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeSafeName(string name)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string safeName = new(name.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safeName) ? "Game" : safeName;
    }

    private static string NormalizeName(string name)
    {
        return string.Concat(name.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }
}