using System;
using System.IO;

namespace OneBox.Contracts;

public enum ServiceImagePathKind
{
    Missing,
    Current,
    LegacyGui,
    Other,
}

public static class ServiceImagePath
{
    public static ServiceImagePathKind Classify(string configuredImagePath, string expectedServiceExecutable)
    {
        if (string.IsNullOrWhiteSpace(configuredImagePath)) return ServiceImagePathKind.Missing;
        string executable = ExtractExecutable(configuredImagePath);
        if (PathsEqual(executable, expectedServiceExecutable)) return ServiceImagePathKind.Current;
        string fileName = Path.GetFileName(executable);
        if (string.Equals(fileName, "OneBox.exe", StringComparison.OrdinalIgnoreCase) &&
            configuredImagePath.Contains("--service", StringComparison.OrdinalIgnoreCase))
            return ServiceImagePathKind.LegacyGui;
        return ServiceImagePathKind.Other;
    }

    public static string ExtractExecutable(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return string.Empty;
        string value = imagePath.Trim();
        if (value[0] == '"')
        {
            int closing = value.IndexOf('"', 1);
            return closing > 1 ? value.Substring(1, closing - 1) : value.Trim('"');
        }
        int separator = value.IndexOf(' ');
        return separator < 0 ? value : value.Substring(0, separator);
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }
}
