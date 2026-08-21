using System;
using System.Threading;
using System.Threading.Tasks;

namespace PowerAudioManager
{
    public enum UpdateErrorCode
    {
        None,
        NotInstalled,
        Offline,
        DownloadFailed,
        VerificationFailed,
        LockConflict,
        Cancelled,
        CoordinationFailed,
        ApplyFailed,
        Unknown,
    }

    public sealed class UpdateCandidate
    {
        public UpdateCandidate(string version, string releaseNotes, object nativeValue)
        {
            Version = version ?? string.Empty;
            ReleaseNotes = releaseNotes ?? string.Empty;
            NativeValue = nativeValue ?? throw new ArgumentNullException(nameof(nativeValue));
        }

        public string Version { get; }
        public string ReleaseNotes { get; }
        public object NativeValue { get; }
    }

    public sealed class UpdateOperationResult
    {
        private UpdateOperationResult(bool success, bool updateAvailable, bool applied,
            UpdateErrorCode errorCode, string message, UpdateCandidate candidate)
        {
            Success = success;
            UpdateAvailable = updateAvailable;
            Applied = applied;
            ErrorCode = errorCode;
            Message = message ?? string.Empty;
            Candidate = candidate;
        }

        public bool Success { get; }
        public bool UpdateAvailable { get; }
        public bool Applied { get; }
        public UpdateErrorCode ErrorCode { get; }
        public string Message { get; }
        public UpdateCandidate Candidate { get; }

        public static UpdateOperationResult NoUpdate() =>
            new(true, false, false, UpdateErrorCode.None, "当前已是最新版本。", null);

        public static UpdateOperationResult Available(UpdateCandidate candidate) =>
            new(true, true, false, UpdateErrorCode.None, $"发现新版本 {candidate.Version}。", candidate);

        public static UpdateOperationResult AppliedSuccessfully(UpdateCandidate candidate) =>
            new(true, true, true, UpdateErrorCode.None, "更新已准备完成，OneBox 将重启。", candidate);

        public static UpdateOperationResult Failed(UpdateErrorCode code, string message) =>
            new(false, false, false, code, message, null);
    }

    public sealed class UpdateOperationException : Exception
    {
        public UpdateOperationException(UpdateErrorCode errorCode, string message, Exception innerException = null)
            : base(message, innerException) => ErrorCode = errorCode;

        public UpdateErrorCode ErrorCode { get; }
    }

    public readonly record struct UpdateCoordinationResult(bool Success, string Error)
    {
        public static UpdateCoordinationResult Ok() => new(true, string.Empty);
        public static UpdateCoordinationResult Fail(string error) => new(false, error ?? "更新协调失败。");
    }

    public interface IUpdateClient
    {
        bool IsInstalled { get; }
        string CurrentVersion { get; }
        Task<UpdateCandidate> CheckAsync(CancellationToken cancellationToken);
        Task DownloadAsync(UpdateCandidate candidate, IProgress<int> progress, CancellationToken cancellationToken);
        void ApplyAndRestart(UpdateCandidate candidate);
    }

    public interface IUpdateApplicationCoordinator
    {
        Task<UpdateCoordinationResult> PrepareAsync(CancellationToken cancellationToken);
        Task<UpdateCoordinationResult> RecoverAsync(CancellationToken cancellationToken);
    }

    public sealed class UpdateWorkflow
    {
        private readonly IUpdateClient _client;
        private readonly IUpdateApplicationCoordinator _coordinator;

        public UpdateWorkflow(IUpdateClient client, IUpdateApplicationCoordinator coordinator)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        public string CurrentVersion => _client.CurrentVersion;

        public async Task<UpdateOperationResult> CheckAsync(CancellationToken cancellationToken)
        {
            if (!_client.IsInstalled)
                return UpdateOperationResult.Failed(UpdateErrorCode.NotInstalled,
                    "当前为开发/便携环境，不能自动更新；请安装 Velopack 正式版本后重试。");
            try
            {
                UpdateCandidate candidate = await _client.CheckAsync(cancellationToken).ConfigureAwait(false);
                return candidate == null ? UpdateOperationResult.NoUpdate() : UpdateOperationResult.Available(candidate);
            }
            catch (Exception ex)
            {
                return FromException(ex, UpdateErrorCode.Offline, "无法连接更新源，请检查网络后重试。");
            }
        }

        public async Task<UpdateOperationResult> DownloadAndApplyAsync(UpdateCandidate candidate,
            IProgress<int> progress, CancellationToken cancellationToken)
        {
            if (candidate == null)
                return UpdateOperationResult.Failed(UpdateErrorCode.Unknown, "更新信息无效，请重新检查更新。");
            try
            {
                await _client.DownloadAsync(candidate, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return FromException(ex, UpdateErrorCode.DownloadFailed, "更新包下载失败，请稍后重试。");
            }

            UpdateCoordinationResult prepared;
            try
            {
                prepared = await _coordinator.PrepareAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                prepared = UpdateCoordinationResult.Fail(ex.Message);
            }
            if (!prepared.Success)
                return UpdateOperationResult.Failed(UpdateErrorCode.CoordinationFailed,
                    "无法安全停止 OneBox 服务与硬件进程：" + prepared.Error);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _client.ApplyAndRestart(candidate);
                return UpdateOperationResult.AppliedSuccessfully(candidate);
            }
            catch (Exception ex)
            {
                UpdateOperationResult failure = FromException(ex, UpdateErrorCode.ApplyFailed,
                    "无法应用更新，已保留下载内容供下次重试。");
                try
                {
                    UpdateCoordinationResult recovered = await _coordinator.RecoverAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!recovered.Success)
                        return UpdateOperationResult.Failed(failure.ErrorCode,
                            failure.Message + " 服务恢复也失败：" + recovered.Error);
                }
                catch (Exception recoveryError)
                {
                    return UpdateOperationResult.Failed(failure.ErrorCode,
                        failure.Message + " 服务恢复也失败：" + recoveryError.Message);
                }
                return failure;
            }
        }

        private static UpdateOperationResult FromException(Exception exception,
            UpdateErrorCode fallbackCode, string fallbackMessage)
        {
            if (exception is OperationCanceledException)
                return UpdateOperationResult.Failed(UpdateErrorCode.Cancelled, "更新操作已取消。");
            if (exception is UpdateOperationException updateError)
                return UpdateOperationResult.Failed(updateError.ErrorCode, updateError.Message);
            return UpdateOperationResult.Failed(fallbackCode,
                string.IsNullOrWhiteSpace(exception?.Message) ? fallbackMessage : fallbackMessage + " " + exception.Message);
        }
    }
}
