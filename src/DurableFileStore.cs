using System;
using System.IO;
using System.Text;

namespace PowerAudioManager
{
    /// <summary>
    /// Writes replaceable application data without ever truncating the last
    /// committed file. The temporary file is flushed to disk before the
    /// same-volume rename/replace, and a previous valid generation is kept.
    /// </summary>
    internal static class DurableFileStore
    {
        public static void WriteUtf8Atomically(string path, string content, bool preserveBackup = false)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A destination path is required.", nameof(path));

            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("The destination must have a directory.", nameof(path));
            Directory.CreateDirectory(directory);

            string tempPath = path + ".tmp." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
                {
                    writer.Write(content ?? string.Empty);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (!File.Exists(path))
                {
                    File.Move(tempPath, path);
                }
                else if (preserveBackup)
                {
                    // Recovery path: replace a known-bad primary while keeping
                    // the last-known-good .bak generation untouched.
                    File.Move(tempPath, path, overwrite: true);
                }
                else
                {
                    File.Replace(tempPath, path, path + ".bak", ignoreMetadataErrors: true);
                }
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        public static string QuarantineCorruptFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string quarantine = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.Move(path, quarantine);
            return quarantine;
        }
    }
}
