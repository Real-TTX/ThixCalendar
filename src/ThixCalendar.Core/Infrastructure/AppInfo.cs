using System.Reflection;

namespace ThixCalendar.Core.Infrastructure;

/// <summary>Version, channel, build number and app registrations baked in at build time (Directory.Build.props).</summary>
public static class AppInfo
{
    private static readonly Assembly ThisAssembly = typeof(AppInfo).Assembly;

    public const string ProductName = "ThixCalendar";

    /// <summary>e.g. 0.1.42-20261115, nightly-43-20261115 or local-20261115.</summary>
    public static string Version { get; } =
        ThisAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "local";

    /// <summary>release, nightly or local.</summary>
    public static string Channel { get; } = ReadMetadata("ThixCalendarChannel") ?? "local";

    public static int BuildNumber { get; } = int.TryParse(ReadMetadata("ThixCalendarBuildNumber"), out var number) ? number : 0;

    /// <summary>owner/name of the GitHub repository that publishes the builds.</summary>
    public static string Repository { get; } = ReadMetadata("ThixCalendarRepository") ?? "Real-TTX/ThixCalendar";

    /// <summary>The Microsoft Entra app registration (public client) built into this version; may be empty.</summary>
    public static string BuiltInMicrosoftClientId { get; } = ReadMetadata("ThixCalendarMicrosoftClientId") ?? "";

    /// <summary>The Google OAuth client ("Desktop app") built into this version; may be empty.</summary>
    public static string BuiltInGoogleClientId { get; } = ReadMetadata("ThixCalendarGoogleClientId") ?? "";

    public static string BuiltInGoogleClientSecret { get; } = ReadMetadata("ThixCalendarGoogleClientSecret") ?? "";

    public static bool IsLocalBuild => Channel == "local";

    /// <summary>How the cloud names the system of a device.</summary>
    public static string PlatformName => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    /// <summary>The name of this computer (the device name offered for the cloud).</summary>
    public static string MachineName => Environment.MachineName;

    private static string? ReadMetadata(string key) =>
        ThisAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value is { Length: > 0 } value
            ? value
            : null;
}
