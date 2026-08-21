using System;
using System.Threading;
using System.Threading.Tasks;
using OneBox.Contracts;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class UpdateWorkflowTests
{
    private static UpdateCandidate Candidate() => new("2.0.0", "notes", new object());

    [Fact]
    public async Task Check_NoUpdate_ReturnsSuccessfulFinalResult()
    {
        var client = new FakeUpdateClient { CheckResult = null };
        UpdateOperationResult result = await new UpdateWorkflow(client, new FakeCoordinator())
            .CheckAsync(CancellationToken.None);
        Assert.True(result.Success);
        Assert.False(result.UpdateAvailable);
        Assert.Equal(1, client.CheckCalls);
    }

    [Fact]
    public async Task Check_UpdateAvailable_PreservesCandidate()
    {
        UpdateCandidate candidate = Candidate();
        var client = new FakeUpdateClient { CheckResult = candidate };
        UpdateOperationResult result = await new UpdateWorkflow(client, new FakeCoordinator())
            .CheckAsync(CancellationToken.None);
        Assert.True(result.UpdateAvailable);
        Assert.Same(candidate, result.Candidate);
    }

    [Fact]
    public async Task Check_DevelopmentOrPortableEnvironment_IsRejectedWithoutNetwork()
    {
        var client = new FakeUpdateClient { IsInstalled = false };
        UpdateOperationResult result = await new UpdateWorkflow(client, new FakeCoordinator())
            .CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateErrorCode.NotInstalled, result.ErrorCode);
        Assert.Contains("开发/便携环境", result.Message);
        Assert.Equal(0, client.CheckCalls);
    }

    [Fact]
    public async Task Check_Cancellation_IsStructured()
    {
        var client = new FakeUpdateClient
        {
            CheckException = new OperationCanceledException(),
        };
        UpdateOperationResult result = await new UpdateWorkflow(client, new FakeCoordinator())
            .CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateErrorCode.Cancelled, result.ErrorCode);
    }

    [Theory]
    [InlineData(UpdateErrorCode.DownloadFailed)]
    [InlineData(UpdateErrorCode.VerificationFailed)]
    [InlineData(UpdateErrorCode.LockConflict)]
    public async Task Download_KnownFailure_IsPreserved(UpdateErrorCode code)
    {
        var client = new FakeUpdateClient
        {
            DownloadException = new UpdateOperationException(code, "mapped"),
        };
        var coordinator = new FakeCoordinator();
        UpdateOperationResult result = await new UpdateWorkflow(client, coordinator)
            .DownloadAndApplyAsync(Candidate(), null, CancellationToken.None);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(0, coordinator.PrepareCalls);
        Assert.Equal(0, client.ApplyCalls);
    }

    [Fact]
    public async Task Apply_CoordinationFailure_PreventsApply()
    {
        var client = new FakeUpdateClient();
        var coordinator = new FakeCoordinator
        {
            PrepareResult = UpdateCoordinationResult.Fail("service busy"),
        };
        UpdateOperationResult result = await new UpdateWorkflow(client, coordinator)
            .DownloadAndApplyAsync(Candidate(), null, CancellationToken.None);
        Assert.Equal(UpdateErrorCode.CoordinationFailed, result.ErrorCode);
        Assert.Equal(0, client.ApplyCalls);
    }

    [Fact]
    public async Task Apply_Failure_RecoversStoppedService()
    {
        var client = new FakeUpdateClient
        {
            ApplyException = new UpdateOperationException(UpdateErrorCode.ApplyFailed, "apply failed"),
        };
        var coordinator = new FakeCoordinator();
        UpdateOperationResult result = await new UpdateWorkflow(client, coordinator)
            .DownloadAndApplyAsync(Candidate(), null, CancellationToken.None);
        Assert.Equal(UpdateErrorCode.ApplyFailed, result.ErrorCode);
        Assert.Equal(1, coordinator.PrepareCalls);
        Assert.Equal(1, coordinator.RecoverCalls);
    }

    [Fact]
    public async Task Apply_Success_CompletesEveryStageInOrder()
    {
        var client = new FakeUpdateClient();
        var coordinator = new FakeCoordinator();
        UpdateOperationResult result = await new UpdateWorkflow(client, coordinator)
            .DownloadAndApplyAsync(Candidate(), null, CancellationToken.None);
        Assert.True(result.Applied);
        Assert.Equal(1, client.DownloadCalls);
        Assert.Equal(1, coordinator.PrepareCalls);
        Assert.Equal(1, client.ApplyCalls);
        Assert.Equal(0, coordinator.RecoverCalls);
    }

    [Fact]
    public async Task CommandBridge_AwaitsFinalUpdateResult()
    {
        var completion = new TaskCompletionSource<UpdateOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<UpdateOperationResult> command = UpdateCommandBridge.ExecuteAsync(
            _ => completion.Task, CancellationToken.None);
        Assert.False(command.IsCompleted);
        completion.SetResult(UpdateOperationResult.NoUpdate());
        Assert.True((await command).Success);
    }

    [Theory]
    [InlineData(false, false, ServiceImagePathKind.Missing, false, UpdateServiceRepairAction.None)]
    [InlineData(true, true, ServiceImagePathKind.Current, true, UpdateServiceRepairAction.None)]
    [InlineData(true, true, ServiceImagePathKind.Current, false, UpdateServiceRepairAction.Start)]
    [InlineData(true, true, ServiceImagePathKind.LegacyGui, false, UpdateServiceRepairAction.MigrateAndStart)]
    [InlineData(true, true, ServiceImagePathKind.Other, false, UpdateServiceRepairAction.MigrateAndStart)]
    public void ServiceRepairPolicy_RequiresStartOrMigrationWhenNeeded(bool installed, bool pending,
        ServiceImagePathKind kind, bool running, UpdateServiceRepairAction expected)
    {
        Assert.Equal(expected, UpdateServiceRepairPolicy.Decide(installed, pending, kind, running));
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException), UpdateErrorCode.Cancelled)]
    [InlineData(typeof(System.Net.Http.HttpRequestException), UpdateErrorCode.Offline)]
    public void ExceptionClassifier_MapsStableCategories(Type exceptionType, UpdateErrorCode expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType);
        Assert.Equal(expected, UpdateExceptionClassifier.Classify(exception, UpdateErrorCode.Unknown));
    }

    private sealed class FakeUpdateClient : IUpdateClient
    {
        public bool IsInstalled { get; set; } = true;
        public string CurrentVersion => "1.7.2";
        public UpdateCandidate CheckResult { get; set; }
        public Exception CheckException { get; set; }
        public Exception DownloadException { get; set; }
        public Exception ApplyException { get; set; }
        public int CheckCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public int ApplyCalls { get; private set; }

        public Task<UpdateCandidate> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCalls++;
            if (CheckException != null) throw CheckException;
            return Task.FromResult(CheckResult);
        }

        public Task DownloadAsync(UpdateCandidate candidate, IProgress<int> progress,
            CancellationToken cancellationToken)
        {
            DownloadCalls++;
            if (DownloadException != null) throw DownloadException;
            progress?.Report(100);
            return Task.CompletedTask;
        }

        public void ApplyAndRestart(UpdateCandidate candidate)
        {
            ApplyCalls++;
            if (ApplyException != null) throw ApplyException;
        }
    }

    private sealed class FakeCoordinator : IUpdateApplicationCoordinator
    {
        public UpdateCoordinationResult PrepareResult { get; set; } = UpdateCoordinationResult.Ok();
        public UpdateCoordinationResult RecoverResult { get; set; } = UpdateCoordinationResult.Ok();
        public int PrepareCalls { get; private set; }
        public int RecoverCalls { get; private set; }

        public Task<UpdateCoordinationResult> PrepareAsync(CancellationToken cancellationToken)
        {
            PrepareCalls++;
            return Task.FromResult(PrepareResult);
        }

        public Task<UpdateCoordinationResult> RecoverAsync(CancellationToken cancellationToken)
        {
            RecoverCalls++;
            return Task.FromResult(RecoverResult);
        }
    }
}
