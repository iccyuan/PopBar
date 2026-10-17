using System.IO;
using PopBar.Core;

namespace PopBar.Tests;

public class JavaAccessBridgeSetupTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"popbar-jab-{Guid.NewGuid():N}.properties");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void Enable_creates_the_file_and_disable_removes_it()
    {
        JavaAccessBridgeSetup.Apply(true, _path);
        Assert.Contains("assistive_technologies=com.sun.java.accessibility.AccessBridge", File.ReadAllLines(_path));
        JavaAccessBridgeSetup.Apply(true, _path); // idempotent
        Assert.Single(File.ReadAllLines(_path), l => l.StartsWith("assistive_technologies"));
        JavaAccessBridgeSetup.Apply(false, _path);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Disable_keeps_the_users_own_lines()
    {
        File.WriteAllLines(_path, ["# mine", "screen_reader_present=true"]);
        JavaAccessBridgeSetup.Apply(true, _path);
        JavaAccessBridgeSetup.Apply(false, _path);
        Assert.Equal(["# mine", "screen_reader_present=true"], File.ReadAllLines(_path));
    }

    [Fact]
    public void An_existing_assistive_technologies_setting_is_left_alone()
    {
        string[] own = ["assistive_technologies=com.example.Reader"];
        File.WriteAllLines(_path, own);
        JavaAccessBridgeSetup.Apply(true, _path);
        Assert.Equal(own, File.ReadAllLines(_path));
        JavaAccessBridgeSetup.Apply(false, _path);
        Assert.Equal(own, File.ReadAllLines(_path));
    }
}
