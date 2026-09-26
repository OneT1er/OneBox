using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace PowerAudioManager.AudioStudio;

internal static class VbCableInstaller
{
    // This URL is linked from the VB-Audio CABLE download page. Download the
    // unmodified vendor archive; do not redistribute the driver with OneBox.
    const string OfficialPackage = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";

    public static async Task DownloadAsync(Window owner)
    {
        string destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "VBCABLE_Driver_Pack45.zip");
        string partial = destination + ".download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            using var response = await client.GetAsync(OfficialPackage, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var target = File.Create(partial))
                await source.CopyToAsync(target);
            File.Move(partial, destination, true);
            MessageBox.Show(owner,
                "官方安装包已保存到“下载”。请解压，右键以管理员身份运行 VBCABLE_Setup_x64.exe，按官方提示重启，然后在 OneBox 选择 CABLE Input。",
                "VB-CABLE", MessageBoxButton.OK, MessageBoxImage.Information);
            Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Log("VB-CABLE download", ex);
            MessageBox.Show(owner, "下载失败：" + ex.Message + "\n请从 https://vb-audio.com/Cable/ 获取官方安装包。",
                "VB-CABLE", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { try { File.Delete(partial); } catch { } }
    }
}
