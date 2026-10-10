using KHost.Abstractions.Services;

namespace KHost.Plugins.YouTubeMusic;

// Naming the settings class here is what makes the host bind it and serve IOptionsMonitor to the provider.
public sealed class YouTubeMusicPlugin : IPlugin<YouTubeMusicSettings>;
