using System;
using System.Text.RegularExpressions;

namespace OneBox.Contracts;

public static partial class PipeNames
{
    public static string ForCommand(string userSid) => $"OneBox.V{IpcProtocol.Version}.{NormalizeSid(userSid)}.Command";
    public static string ForHardware(string userSid) => $"OneBox.V{IpcProtocol.Version}.{NormalizeSid(userSid)}.Hardware";

    public static string NormalizeSid(string userSid)
    {
        if (string.IsNullOrWhiteSpace(userSid) || userSid.Length > 184 || !SidPattern().IsMatch(userSid))
            throw new ArgumentException("A valid Windows user SID is required.", nameof(userSid));
        return userSid.Replace('-', '_');
    }

    [GeneratedRegex("^S-1-[0-9]+(?:-[0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();
}
