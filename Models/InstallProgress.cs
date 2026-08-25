namespace RetroShelf.Models;

public sealed record InstallProgress(string Stage, string Detail, int Percentage);