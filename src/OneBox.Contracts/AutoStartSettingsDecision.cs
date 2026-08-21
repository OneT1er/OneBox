namespace OneBox.Contracts;

public readonly record struct AutoStartSettingsDecision(bool Enable, int Method)
{
    public const int None = 0;
    public const int Registry = 1;
    public const int ScheduledTask = 2;
    public const int Service = 3;

    public static AutoStartSettingsDecision Create(bool requestedEnabled, int configuredMethod)
    {
        if (!requestedEnabled) return new(false, None);
        int method = configuredMethod is Registry or ScheduledTask or Service
            ? configuredMethod
            : Service;
        return new(true, method);
    }
}
