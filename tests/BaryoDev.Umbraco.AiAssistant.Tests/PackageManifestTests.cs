using System.Text.Json;

namespace BaryoDev.Umbraco.AiAssistant.Tests;

public class PackageManifestTests
{
    static string Manifest()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src"))) dir = Path.GetDirectoryName(dir);
        return Directory.GetFiles(Path.Combine(dir!, "src"), "umbraco-package.json", SearchOption.AllDirectories)
            .First(f => !f.Contains("/obj/") && !f.Contains("/bin/"));
    }

    [Fact]
    public void Every_property_action_has_a_kind()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Manifest()));
        var actions = doc.RootElement.GetProperty("extensions").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "propertyAction").ToList();
        Assert.Equal(3, actions.Count);
        Assert.All(actions, a => Assert.Equal("default", a.GetProperty("kind").GetString()));
    }
}
