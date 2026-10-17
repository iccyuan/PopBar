using System.IO;
using System.IO.Compression;
using PopBar.Plugins;

namespace PopBar.Tests;

public sealed class PluginInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "popbar-install-tests-" + Guid.NewGuid().ToString("N"));

    public PluginInstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>This test assembly references PopBar.Sdk, which is all the installer checks for.</summary>
    private static string PluginLikeDll => typeof(PluginInstallerTests).Assembly.Location;

    [Theory]
    [InlineData("https://github.com/iccyuan/PopBar", "iccyuan", "PopBar")]
    [InlineData("https://github.com/iccyuan/PopBar/", "iccyuan", "PopBar")]
    [InlineData("github.com/a-b/c.d.git", "a-b", "c.d")]
    [InlineData("https://github.com/owner/repo/releases/tag/v1.0", "owner", "repo")]
    [InlineData("  owner/repo  ", "owner", "repo")]
    public void Parses_repository_addresses(string input, string owner, string repo) =>
        Assert.Equal((owner, repo), PluginInstaller.ParseRepo(input));

    [Theory]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("just-a-name")]
    [InlineData("")]
    public void Rejects_other_addresses(string input) => Assert.Null(PluginInstaller.ParseRepo(input));

    [Fact]
    public void Recognises_plugin_assemblies_by_their_sdk_reference()
    {
        Assert.True(PluginInstaller.ReferencesSdk(PluginLikeDll));
        Assert.False(PluginInstaller.ReferencesSdk(typeof(object).Assembly.Location)); // System.Private.CoreLib
        var junk = Path.Combine(_dir, "junk.dll");
        File.WriteAllText(junk, "not a PE file");
        Assert.False(PluginInstaller.ReferencesSdk(junk));
    }

    [Fact]
    public void Zip_with_a_top_folder_is_unpacked_flat_without_the_sdk()
    {
        var zip = Path.Combine(_dir, "My Plugin.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(PluginLikeDll, "MyPlugin-1.0/MyPlugin.dll");
            z.CreateEntryFromFile(typeof(Sdk.BarOptions).Assembly.Location, "MyPlugin-1.0/PopBar.Sdk.dll");
            z.CreateEntry("MyPlugin-1.0/readme.txt");
        }
        using var p = PluginInstaller.FromFile(zip);
        Assert.Equal("my-plugin", p.Name);
        Assert.True(File.Exists(Path.Combine(p.TempDir, "MyPlugin.dll")));
        Assert.True(File.Exists(Path.Combine(p.TempDir, "readme.txt")));
        Assert.False(File.Exists(Path.Combine(p.TempDir, "PopBar.Sdk.dll")));
        var temp = p.Root;
        p.Dispose();
        Assert.False(Directory.Exists(temp));
    }

    [Fact]
    public void Archive_without_a_plugin_is_refused()
    {
        var zip = Path.Combine(_dir, "nothing.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) z.CreateEntry("readme.txt");
        var ex = Assert.Throws<InvalidOperationException>(() => PluginInstaller.FromFile(zip));
        Assert.Contains("PopBar.Sdk", ex.Message);
    }

    [Fact]
    public void Only_plugin_folders_can_be_removed()
    {
        Assert.Throws<InvalidOperationException>(() => PluginInstaller.Remove(_dir));
    }
}
