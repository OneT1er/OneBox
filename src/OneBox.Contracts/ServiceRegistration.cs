namespace OneBox.Contracts;

public interface IServiceRegistrationOperations
{
    bool IsInstalled { get; }
    string ImagePath { get; }
    string LastError { get; }
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
    int ExitCode,
    string Diagnostic = null);

public sealed class ServiceRegistrationCoordinator(IServiceRegistrationOperations operations)
{
    ServiceRegistrationResult Failure(ServiceRegistrationAction action, ServiceImagePathKind kind, int exitCode)
        => new(false, action, kind, exitCode, operations.LastError);

    public ServiceRegistrationResult Ensure(string expectedExecutablePath)
    {
        if (!operations.IsInstalled)
        {
            int createExit = operations.Create(expectedExecutablePath);
            if (createExit != 0) return Failure(ServiceRegistrationAction.Created, ServiceImagePathKind.Missing, createExit);
            int startExit = operations.StartIfStopped();
            return startExit == 0
                ? new(true, ServiceRegistrationAction.Created, ServiceImagePathKind.Missing, 0)
                : Failure(ServiceRegistrationAction.Created, ServiceImagePathKind.Missing, startExit);
        }

        ServiceImagePathKind kind = ServiceImagePath.Classify(operations.ImagePath, expectedExecutablePath);
        ServiceRegistrationAction action = ServiceRegistrationAction.None;
        if (kind != ServiceImagePathKind.Current)
        {
            int stopExit = operations.StopIfRunning();
            if (stopExit != 0) return Failure(ServiceRegistrationAction.Reconfigured, kind, stopExit);
            int configureExit = operations.Configure(expectedExecutablePath);
            action = kind == ServiceImagePathKind.LegacyGui ? ServiceRegistrationAction.MigratedLegacy : ServiceRegistrationAction.Reconfigured;
            if (configureExit != 0) return Failure(action, kind, configureExit);
        }
        int start = operations.StartIfStopped();
        return start == 0
            ? new(true, action, kind, 0)
            : Failure(action, kind, start);
    }
}
