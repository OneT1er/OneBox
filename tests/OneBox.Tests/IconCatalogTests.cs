using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
using System.Reflection;
using Xunit;

namespace PowerAudioManager.Tests
{
    public sealed class IconCatalogTests
    {
        [Fact]
        public void CatalogCoversEveryManifestSemanticKey()
        {
            var expected = new[]
            {
                "Brand", "Power", "Audio", "Mute", "Lock", "Unlock", "ChevronRight", "ChevronDown", "ChevronUp", "Close",
                "Performance", "MemoryClean", "Translate", "Clipboard", "Gallery", "Launcher", "Url", "Folder", "Add", "Edit",
                "Delete", "Settings", "Modules", "Dashboard", "Temperature", "Capture", "Error", "Success", "Warning", "Cpu",
                "Gpu", "Hot", "Vram", "Dram", "Disk", "Fan", "Control", "Motherboard", "DefaultMetric"
            };
            var actual = IconCatalog.Keys.Select(k => k.ToString()).OrderBy(x => x).ToArray();
            Assert.Equal(expected.OrderBy(x => x), actual);
            Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void EveryIconHasFrozenGeometryAndAccessibleName()
        {
            foreach (var key in IconCatalog.Keys)
            {
                Assert.True(IconCatalog.GetGeometry(key).IsFrozen, key.ToString());
                Assert.False(string.IsNullOrWhiteSpace(IconCatalog.AutomationName(key)), key.ToString());
            }
        }

        [Fact]
        public void ThemeTokensKeepPurpleShadowAndAccessibleHitTarget()
        {
            Assert.Equal(0x8E, ThemeTokens.Accent.R);
            Assert.Equal(0x8C, ThemeTokens.Accent.G);
            Assert.Equal(0xD8, ThemeTokens.Accent.B);
            Assert.StartsWith("OneBox.", ThemeTokens.FlatButtonKey, StringComparison.Ordinal);
        }

        [Fact]
        public void TrayIconIsUriBackedForHNotifyIcon()
        {
            var resourceType = typeof(MainWindow).Assembly.GetType("PowerAudioManager.AppResources", throwOnError: true);
            var method = resourceType.GetMethod("LoadAppImage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var image = Assert.IsType<BitmapImage>(method.Invoke(null, new object[] { "app.ico" }));

            Assert.NotNull(image);
            Assert.NotNull(image.UriSource);
            Assert.Equal(Uri.UriSchemeFile, image.UriSource.Scheme);
            Assert.True(File.Exists(image.UriSource.LocalPath), image.UriSource.LocalPath);
        }

        [Fact]
        public void SourceContainsNoLegacyGlyphOrPackIconReferences()
        {
            var root = FindRepoRoot();
            var sourcePaths = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                               !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Concat(new[] { Path.Combine(root, "src", "OneBox.csproj"), Path.Combine(root, "Directory.Packages.props") })
                .Select(File.ReadAllText).ToArray();
            var joined = string.Join("\n", sourcePaths);
            Assert.DoesNotContain("PackIcon", joined, StringComparison.Ordinal);
            Assert.DoesNotContain("Segoe UI Emoji", joined, StringComparison.Ordinal);
            Assert.DoesNotContain("CompositeFont", joined, StringComparison.Ordinal);
            Assert.DoesNotContain("MaterialDesign", joined, StringComparison.Ordinal);
            Assert.DoesNotContain("icon-power.png", joined, StringComparison.Ordinal);
            Assert.DoesNotContain("icon-audio.png", joined, StringComparison.Ordinal);
            foreach (var rune in joined.EnumerateRunes())
            {
                var value = rune.Value;
                Assert.False((value >= 0x1F300 && value <= 0x1FAFF) || (value >= 0x2300 && value <= 0x27FF),
                    $"Legacy emoji/icon symbol U+{value:X} found in source/config");
            }
        }

        static string FindRepoRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null && !Directory.Exists(Path.Combine(current.FullName, "src"))) current = current.Parent;
            return current?.FullName ?? throw new DirectoryNotFoundException("OneBox repository root not found");
        }
    }
}
