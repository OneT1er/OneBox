using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OneBox.Windows;

internal static class SecurePipe
{
    public static PipeSecurity CreateSecurity(string userSid)
    {
        var target = new SecurityIdentifier(userSid);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(system);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(administrators, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(target, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    public static bool IsExpectedClient(NamedPipeServerStream pipe, string expectedUserSid)
    {
        string actualSid = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                actualSid = identity.User?.Value;
            });
        }
        catch
        {
            return false;
        }
        return string.Equals(actualSid, expectedUserSid, StringComparison.OrdinalIgnoreCase);
    }
}
