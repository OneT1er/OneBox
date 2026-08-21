using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OneBox.Contracts;
using Xunit;

namespace OneBox.Tests;

public sealed class IpcProtocolTests
{
    [Fact]
    public void Validator_AcceptsAllowedVersionedRequest()
    {
        IpcRequest request = IpcRequest.Create(IpcCommand.CleanMemory, new CleanMemoryPayload { Flags = 3 });
        Assert.True(IpcValidator.Validate(request, IpcCommand.CleanMemory).IsValid);
    }

    [Fact]
    public void Validator_RejectsUnsupportedVersion()
    {
        IpcRequest request = IpcRequest.Create(IpcCommand.Ping, new { });
        request.Version++;
        Assert.Equal(IpcErrorCode.UnsupportedVersion, IpcValidator.Validate(request, IpcCommand.Ping).ErrorCode);
    }

    [Fact]
    public void Validator_RejectsEmptyRequestId()
    {
        IpcRequest request = IpcRequest.Create(IpcCommand.Ping, new { }, Guid.Empty);
        Assert.Equal(IpcErrorCode.InvalidRequestId, IpcValidator.Validate(request, IpcCommand.Ping).ErrorCode);
    }

    [Fact]
    public void Validator_RejectsUndefinedCommand()
    {
        IpcRequest request = IpcRequest.Create(IpcCommand.Ping, new { });
        request.Command = (IpcCommand)999;
        Assert.Equal(IpcErrorCode.UnsupportedCommand, IpcValidator.Validate(request).ErrorCode);
    }

    [Fact]
    public void Validator_RejectsCommandOnWrongEndpoint()
    {
        IpcRequest request = IpcRequest.Create(IpcCommand.SubscribeHardware, new HardwareSubscribePayload());
        Assert.Equal(IpcErrorCode.UnsupportedCommand, IpcValidator.Validate(request, IpcCommand.CleanMemory).ErrorCode);
    }

    [Fact]
    public async Task Framing_RoundTripsTypedRequest()
    {
        IpcRequest expected = IpcRequest.Create(IpcCommand.CleanMemory, new CleanMemoryPayload { Flags = 0x35 });
        using var stream = new MemoryStream();
        await IpcFraming.WriteAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        IpcRequest actual = await IpcFraming.ReadAsync<IpcRequest>(stream, CancellationToken.None);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(0x35, actual.Payload.Deserialize<CleanMemoryPayload>(IpcJson.Options).Flags);
    }

    [Fact]
    public async Task Framing_RejectsOversizedDeclaredLengthBeforeAllocation()
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, IpcProtocol.MaxMessageBytes + 1);
        using var stream = new MemoryStream(header);
        IpcProtocolException exception = await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadAsync<IpcRequest>(stream, CancellationToken.None));
        Assert.Equal(IpcErrorCode.PayloadTooLarge, exception.ErrorCode);
    }

    [Fact]
    public async Task Framing_RejectsNonPositiveLength()
    {
        using var stream = new MemoryStream(new byte[4]);
        IpcProtocolException exception = await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadAsync<IpcRequest>(stream, CancellationToken.None));
        Assert.Equal(IpcErrorCode.InvalidMessage, exception.ErrorCode);
    }

    [Fact]
    public async Task Framing_RejectsTruncatedBody()
    {
        byte[] bytes = new byte[7];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 10);
        using var stream = new MemoryStream(bytes);
        IpcProtocolException exception = await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadAsync<IpcRequest>(stream, CancellationToken.None));
        Assert.Equal(IpcErrorCode.InvalidMessage, exception.ErrorCode);
    }

    [Fact]
    public void Response_PreservesCorrelationAndTypedResult()
    {
        IpcRequest request = IpcRequest.Create(IpcCommand.CleanMemory, new CleanMemoryPayload { Flags = 1 });
        IpcResponse response = IpcResponse.Ok(request, new CleanMemoryResult { FreedBytes = 1234 });
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal((ulong)1234, response.ReadResult<CleanMemoryResult>().FreedBytes);
    }

    [Fact]
    public void PipeNames_AreStableAndIsolatedBySid()
    {
        const string first = "S-1-5-21-100-200-300-1001";
        const string second = "S-1-5-21-100-200-300-1002";
        Assert.Equal(PipeNames.ForCommand(first), PipeNames.ForCommand(first));
        Assert.NotEqual(PipeNames.ForCommand(first), PipeNames.ForCommand(second));
        Assert.Contains("1001", PipeNames.ForCommand(first));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sid")]
    [InlineData("S-2-5-21-1")]
    public void PipeNames_RejectInvalidSid(string value)
    {
        Assert.Throws<ArgumentException>(() => PipeNames.ForHardware(value));
    }

    [Fact]
    public void ReconnectBackoff_ExponentiatesClampsAndResets()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(350));
        Assert.Equal(100, backoff.NextDelay().TotalMilliseconds);
        Assert.Equal(200, backoff.NextDelay().TotalMilliseconds);
        Assert.Equal(350, backoff.NextDelay().TotalMilliseconds);
        Assert.Equal(350, backoff.NextDelay().TotalMilliseconds);
        backoff.Reset();
        Assert.Equal(100, backoff.NextDelay().TotalMilliseconds);
    }

    [Fact]
    public void RateLimiter_RejectsBurstAndRecoversNextWindow()
    {
        var limiter = new FixedWindowRateLimiter(2, TimeSpan.FromSeconds(1));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(limiter.TryAcquire(now));
        Assert.True(limiter.TryAcquire(now.AddMilliseconds(10)));
        Assert.False(limiter.TryAcquire(now.AddMilliseconds(20)));
        Assert.True(limiter.TryAcquire(now.AddSeconds(1)));
    }

    [Fact]
    public void ServiceImagePath_DetectsLegacyGuiRegistration()
    {
        string expected = @"C:\Program Files\OneBox\OneBox.Service.exe";
        Assert.Equal(ServiceImagePathKind.LegacyGui,
            ServiceImagePath.Classify("\"C:\\Program Files\\OneBox\\OneBox.exe\" --service", expected));
    }

    [Fact]
    public void ServiceImagePath_DetectsCurrentAndForeignRegistration()
    {
        string expected = @"C:\Program Files\OneBox\OneBox.Service.exe";
        Assert.Equal(ServiceImagePathKind.Current, ServiceImagePath.Classify($"\"{expected}\"", expected));
        Assert.Equal(ServiceImagePathKind.Other, ServiceImagePath.Classify("\"C:\\Tools\\Other.exe\"", expected));
    }

    [Fact]
    public void ServiceCoordinator_CreatesMissingServiceAndStartsIt()
    {
        var operations = new FakeServiceOperations { IsInstalled = false };
        ServiceRegistrationResult result = new ServiceRegistrationCoordinator(operations).Ensure(@"C:\OneBox\OneBox.Service.exe");
        Assert.True(result.Success);
        Assert.Equal(ServiceRegistrationAction.Created, result.Action);
        Assert.Equal(1, operations.CreateCalls);
        Assert.Equal(1, operations.StartCalls);
        Assert.Equal(0, operations.ConfigureCalls);
    }

    [Fact]
    public void ServiceCoordinator_MigratesLegacyRegistrationBeforeStart()
    {
        var operations = new FakeServiceOperations
        {
            IsInstalled = true,
            ImagePath = "\"C:\\OneBox\\OneBox.exe\" --service",
        };
        ServiceRegistrationResult result = new ServiceRegistrationCoordinator(operations).Ensure(@"C:\OneBox\OneBox.Service.exe");
        Assert.True(result.Success);
        Assert.Equal(ServiceRegistrationAction.MigratedLegacy, result.Action);
        Assert.Equal(1, operations.StopCalls);
        Assert.Equal(1, operations.ConfigureCalls);
        Assert.Equal(1, operations.StartCalls);
    }

    [Fact]
    public void ServiceCoordinator_PropagatesConfigurationFailureWithoutStarting()
    {
        var operations = new FakeServiceOperations
        {
            IsInstalled = true,
            ImagePath = "\"C:\\Other\\Service.exe\"",
            ConfigureExitCode = 37,
        };
        ServiceRegistrationResult result = new ServiceRegistrationCoordinator(operations).Ensure(@"C:\OneBox\OneBox.Service.exe");
        Assert.False(result.Success);
        Assert.Equal(37, result.ExitCode);
        Assert.Equal(0, operations.StartCalls);
    }

    [Fact]
    public void ServiceCoordinator_PreservesStructuredOperationDiagnostic()
    {
        var operations = new FakeServiceOperations
        {
            IsInstalled = true,
            ImagePath = "\"C:\\Other\\Service.exe\"",
            ConfigureExitCode = -1,
            LastError = "sc.exe launch failed: Win32Exception: access denied",
        };
        ServiceRegistrationResult result = new ServiceRegistrationCoordinator(operations).Ensure(@"C:\OneBox\OneBox.Service.exe");
        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("sc.exe launch failed: Win32Exception: access denied", result.Diagnostic);
    }

    [Fact]
    public void ElevatedHelperTimeout_ReportsWhetherProcessWasTerminated()
    {
        Assert.Contains("已终止", ElevatedHelperPolicy.TimeoutMessage(true), StringComparison.Ordinal);
        Assert.Contains("仍在运行", ElevatedHelperPolicy.TimeoutMessage(false), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("S-1-5-18", true)]
    [InlineData("s-1-5-18", true)]
    [InlineData("S-1-5-32-544", false)]
    [InlineData("S-1-5-21-100-200-300-1001", false)]
    [InlineData(null, false)]
    public void PipeServerIdentity_TrustsOnlyLocalSystem(string sid, bool expected)
    {
        Assert.Equal(expected, PipeServerIdentity.IsTrusted(sid));
    }

    [Fact]
    public void SessionLaunchTracker_BacksOffAndCompletesAfterSuccess()
    {
        var tracker = new SessionLaunchTracker(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(tracker.ShouldAttempt(7, now));
        Assert.Equal(TimeSpan.FromSeconds(2), tracker.RecordFailure(7, now));
        Assert.False(tracker.ShouldAttempt(7, now.AddSeconds(1)));
        Assert.True(tracker.ShouldAttempt(7, now.AddSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(4), tracker.RecordFailure(7, now.AddSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(5), tracker.RecordFailure(7, now.AddSeconds(6)));
        tracker.RecordCompleted(7);
        Assert.False(tracker.ShouldAttempt(7, DateTimeOffset.MaxValue));
    }

    [Fact]
    public void SessionLaunchTracker_RemovesInactiveSessionForIdReuse()
    {
        var tracker = new SessionLaunchTracker();
        tracker.RecordCompleted(42);
        tracker.Synchronize(Array.Empty<int>());
        Assert.False(tracker.IsTracked(42));
        Assert.True(tracker.ShouldAttempt(42, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(false, 3, false, AutoStartSettingsDecision.None)]
    [InlineData(true, 1, true, AutoStartSettingsDecision.Registry)]
    [InlineData(true, 2, true, AutoStartSettingsDecision.ScheduledTask)]
    [InlineData(true, 3, true, AutoStartSettingsDecision.Service)]
    [InlineData(true, 0, true, AutoStartSettingsDecision.Service)]
    [InlineData(true, 99, true, AutoStartSettingsDecision.Service)]
    public void AutoStartSettingsDecision_PreservesValidMethodAndDefaultsToService(
        bool requested, int configured, bool expectedEnabled, int expectedMethod)
    {
        AutoStartSettingsDecision decision = AutoStartSettingsDecision.Create(requested, configured);
        Assert.Equal(expectedEnabled, decision.Enable);
        Assert.Equal(expectedMethod, decision.Method);
    }

    [Fact]
    public void ServiceCoordinator_PropagatesStartFailure()
    {
        var operations = new FakeServiceOperations
        {
            IsInstalled = true,
            ImagePath = "\"C:\\OneBox\\OneBox.Service.exe\"",
            StartExitCode = 1460,
        };
        ServiceRegistrationResult result = new ServiceRegistrationCoordinator(operations).Ensure(@"C:\OneBox\OneBox.Service.exe");
        Assert.False(result.Success);
        Assert.Equal(1460, result.ExitCode);
        Assert.Equal(1, operations.StartCalls);
    }

    private sealed class FakeServiceOperations : IServiceRegistrationOperations
    {
        public bool IsInstalled { get; set; }
        public string ImagePath { get; set; }
        public string LastError { get; set; }
        public int ConfigureExitCode { get; set; }
        public int StartExitCode { get; set; }
        public int StopCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int ConfigureCalls { get; private set; }
        public int StartCalls { get; private set; }

        public int StopIfRunning() { StopCalls++; return 0; }
        public int Create(string executablePath) { CreateCalls++; return 0; }
        public int Configure(string executablePath) { ConfigureCalls++; return ConfigureExitCode; }
        public int StartIfStopped() { StartCalls++; return StartExitCode; }
    }
}
