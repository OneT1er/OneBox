using System;
using System.IO;

namespace OneBox.Hardware;

internal static class HardwareLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "OneBox.Hardware.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
