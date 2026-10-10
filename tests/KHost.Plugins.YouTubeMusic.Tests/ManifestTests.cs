using KHost.Abstractions.Models.Plugins;
using KHost.Abstractions.Services;
using System.Text.Json;

namespace KHost.Plugins.YouTubeMusic.Tests;

/// <summary>The host reads the manifest, not this assembly: a setting it cannot parse fails at
/// load time, not build time.</summary>
public class ManifestTests
{
    private static readonly string ManifestPath = Path.Combine(AppContext.BaseDirectory, "manifest.json");

    [Fact]
    public void Manifest_ParsesTheWayTheHostParsesIt()
    {
        var manifest = Read();

        Assert.NotEqual(Guid.Empty, manifest.Id);
        Assert.Equal(PluginApi.CurrentVersion, manifest.ApiVersion);
        Assert.Equal(6, manifest.ApiVersion);
        Assert.Equal("KHost.Plugins.YouTubeMusic.dll", manifest.EntryAssembly);
        Assert.NotEmpty(manifest.Settings);
    }

    // Without it the host binds no settings class and serves the provider no IOptionsMonitor.
    [Fact]
    public void EntryPoint_NamesTheSettingsClass()
        => Assert.True(typeof(IPlugin<YouTubeMusicSettings>).IsAssignableFrom(typeof(YouTubeMusicPlugin)));

    [Fact]
    public void Manifest_EverySettingKeyBindsToASettingsProperty()
    {
        var properties = typeof(YouTubeMusicSettings).GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var setting in Read().Settings)
            Assert.True(properties.Contains(setting.Key), $"Manifest setting '{setting.Key}' binds to nothing.");
    }

    // A manifest button the provider does not answer is drawn and does nothing when pressed.
    [Fact]
    public void Manifest_EveryButtonIsOneTheProviderHandles()
    {
        string[] handled = [YouTubeMusicBreakMusicProvider.SetupButton, YouTubeMusicBreakMusicProvider.OpenButton];

        Assert.Equal(handled.Order(), Read().Buttons.Select(button => button.Key).Order());
    }

    private static PluginManifest Read()
        => JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(ManifestPath), JsonSerializerOptions.Web)!;
}
