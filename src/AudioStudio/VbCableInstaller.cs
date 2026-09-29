using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace PowerAudioManager.AudioStudio;

internal static class VbCableInstaller
{
    // Download the unmodified package directly from VB-Audio. OneBox does not
    // redistribute or silently install the driver.
    const string OfficialPackage = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
    const string OfficialSite = "https://vb-audio.com/Cable/";

    public static async Task InstallAsync(Window owner)
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string archive = Path.Combine(downloads, "VBCABLE_Driver_Pack45.zip");
        string extracted = Path.Combine(downloads, "VBCABLE_Driver_Pack45-" +
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        string partial = archive + ".download." + Guid.NewGuid().ToString("N");
        string staging = extracted + ".extract." + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(downloads);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            using var response = await client.GetAsync(OfficialPackage, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var target = File.Create(partial))
                await source.CopyToAsync(target);
            File.Move(partial, archive, true);

            // ExtractToDirectory rejects paths that escape staging. A fresh
            // directory prevents stale files from impersonating the installer.
            ZipFile.ExtractToDirectory(archive, staging);
            string stagedInstaller = Path.Combine(staging, "VBCABLE_Setup_x64.exe");
            if (!File.Exists(stagedInstaller))
                throw new InvalidDataException("官方压缩包中未找到 VBCABLE_Setup_x64.exe。");
            Directory.Move(staging, extracted);

            Process.Start(new ProcessStartInfo(Path.Combine(extracted, "VBCABLE_Setup_x64.exe"))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = extracted
            });
            MessageBox.Show(owner,
                "已启动 VB-Audio 官方安装程序。请按安装向导完成安装，按官方提示重启，然后在 OneBox 选择 CABLE Input。",
                "VB-CABLE", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show(owner,
                "已取消管理员授权。官方安装包和解压文件仍保存在“下载”，可稍后手动运行 VBCABLE_Setup_x64.exe。",
                "VB-CABLE", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Log("VB-CABLE install", ex);
            if (MessageBox.Show(owner,
                    "VB-CABLE 安装程序未能启动：" + ex.Message + "\n\n是否打开官方页面手动下载？\n" + OfficialSite,
                    "VB-CABLE", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo(OfficialSite) { UseShellExecute = true }); }
                catch (Exception browserError) { AppLog.Log("VB-CABLE official site", browserError); }
            }
        }
        finally
        {
            try { File.Delete(partial); } catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    // Keep existing callers working until their button labels are updated.
    public static Task DownloadAsync(Window owner) => InstallAsync(owner);
}
