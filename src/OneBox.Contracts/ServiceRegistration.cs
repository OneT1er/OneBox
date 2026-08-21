namespace OneBox.Contracts;

public interface IServiceRegistrationOperations
{
    bool IsInstalled { get; }
    string ImagePath { get; }
    int StopIfRunning();
    int Create(string executablePath);
    int Configure(string executablePath);
    int StartIfStopped();
}

public enum ServiceRegistrationAction
{
    None,
    Created,
    MigratedLegacy,
    Reconfigured,
}

public readonly record struct ServiceRegistrationResult(
    bool Success,
    ServiceRegistrationAction Action,
    ServiceImagePathKind PreviousPathKind,
    int ExitCode);

public sealed class ServiceRegistrationCoordinator(IServiceRegistrationOperations operations)
{
    public ServiceRegistrationResult Ensure(string expectedExecutablePath)
    {
        if (!operations.IsInstalled)
        {
            int createExit = operations.Create(expectedExecutablePath);
            if (createExit != 0) return new(false, ServiceRegistrationAction.Created, ServiceImagePathKind.Missing, createExit);
            int startExit = operations.StartIfStopped();
            return new(startExit == 0, ServiceRegistrationAction.Created, ServiceImagePathKind.Missing, startExit);
        }

        ServiceImagePathKind kind = ServiceImagePath.Classify(operations.ImagePath, expectedExecutablePath);
        ServiceRegistrationAction action = ServiceRegistrationAction.None;
        if (kind != ServiceImagePathKind.Current)
        {
            int stopExit = operations.StopIfRunning();
            if (stopExit != 0) return new(false, ServiceRegistrationAction.Reconfigured, kind, stopExit);
            int configureExit = operations.Configure(expectedExecutablePath);
            action = kind == ServiceImagePathKind.LegacyGui ? ServiceRegistrationAction.MigratedLegacy : ServiceRegistrationAction.Reconfigured;
            if (configureExit != 0) return new(false, action, kind, configureExit);
        }
        int start = operations.StartIfStopped();
        return new(start == 0, action, kind, start);
    }
}
