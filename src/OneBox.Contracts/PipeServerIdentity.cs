using System;

namespace OneBox.Contracts;

public static class PipeServerIdentity
{
    public const string LocalSystemSid = "S-1-5-18";

    public static bool IsTrusted(string sid) =>
        string.Equals(sid, LocalSystemSid, StringComparison.OrdinalIgnoreCase);
}
