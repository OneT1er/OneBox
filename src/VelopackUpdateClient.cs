using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace PowerAudioManager
{
    public static class UpdateExceptionClassifier
    {
        public static UpdateErrorCode Classify(Exception exception, UpdateErrorCode fallback)
        {
            if (exception is OperationCanceledException) return UpdateErrorCode.Cancelled;
            if (exception is NotInstalledException) return UpdateErrorCode.NotInstalled;
            if (exception is AcquireLockFailedException) return UpdateErrorCode.LockConflict;
            if (exception is ChecksumFailedException) return UpdateErrorCode.VerificationFailed;
            if (exception is HttpRequestException) return UpdateErrorCode.Offline;
            if (exception is IOException io && IsSharingViolation(io.HResult)) return UpdateErrorCode.LockConflict;
            return fallback;
        }

        private static bool IsSharingViolation(int hresult)
        {
            int code = hresult & 0xFFFF;
            return code == 32 || code == 33;
        }
    }

    internal sealed class VelopackUpdateClient : IUpdateClient
    {
        public const string RepositoryUrl = "https://github.com/OneT1er/OneBox";

        private readonly UpdateManager _manager;

        public VelopackUpdateClient()
        {
            var source = new GithubSource(RepositoryUrl, null, false, null);
            _manager = new UpdateManager(source, new UpdateOptions(), null);
        }

        public bool IsInstalled => _manager.IsInstalled && !_manager.IsPortable;

        public string CurrentVersion => _manager.IsInstalled
            ? _manager.CurrentVersion?.ToString() ?? ApplicationVersion.Value
            : ApplicationVersion.Value;

        public async Task<UpdateCandidate> CheckAsync(CancellationToken cancellationToken)
        {
            try
            {
                UpdateInfo update = await _manager.CheckForUpdatesAsync()
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                if (update == null) return null;
                VelopackAsset target = update.TargetFullRelease;
                return new UpdateCandidate(target.Version?.ToString(), target.NotesMarkdown, update);
            }
            catch (Exception ex)
            {
                throw Map(ex, UpdateErrorCode.Offline, "检查更新失败");
            }
        }

        public async Task DownloadAsync(UpdateCandidate candidate, IProgress<int> progress,
            CancellationToken cancellationToken)
        {
            if (candidate?.NativeValue is not UpdateInfo update)
                throw new UpdateOperationException(UpdateErrorCode.DownloadFailed, "更新信息与 Velopack 会话不匹配。");
            try
            {
                await _manager.DownloadUpdatesAsync(update, value => progress?.Report(value), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw Map(ex, UpdateErrorCode.DownloadFailed, "下载更新包失败");
            }
        }

        public void ApplyAndRestart(UpdateCandidate candidate)
        {
            if (candidate?.NativeValue is not UpdateInfo update)
                throw new UpdateOperationException(UpdateErrorCode.ApplyFailed, "更新信息与 Velopack 会话不匹配。");
            try
            {
                _manager.ApplyUpdatesAndRestart(update.TargetFullRelease, Array.Empty<string>());
            }
            catch (Exception ex)
            {
                throw Map(ex, UpdateErrorCode.ApplyFailed, "应用更新失败");
            }
        }

        private static UpdateOperationException Map(Exception exception, UpdateErrorCode fallback, string context)
        {
            UpdateErrorCode code = UpdateExceptionClassifier.Classify(exception, fallback);
            string message = code switch
            {
                UpdateErrorCode.NotInstalled => "当前为开发/便携环境，不能自动更新；请安装 Velopack 正式版本后重试。",
                UpdateErrorCode.Offline => "无法连接 GitHub 更新源，请检查网络后重试。",
                UpdateErrorCode.DownloadFailed => "更新包下载失败，请稍后重试。",
                UpdateErrorCode.VerificationFailed => "更新包完整性校验失败，已拒绝安装；请重新下载。",
                UpdateErrorCode.LockConflict => "另一个更新进程正在运行或文件被占用，请稍后重试。",
                UpdateErrorCode.Cancelled => "更新操作已取消。",
                UpdateErrorCode.ApplyFailed => "更新包无法应用，当前版本保持不变。",
                _ => context + "：" + exception.Message,
            };
            return new UpdateOperationException(code, message, exception);
        }
    }

    public static class ApplicationVersion
    {
        public static string Value
        {
            get
            {
                Version version = typeof(ApplicationVersion).Assembly.GetName().Version;
                return version == null ? "0.0.0" : version.ToString(3);
            }
        }
    }
}
