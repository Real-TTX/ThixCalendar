using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Core.Business;

/// <summary>
/// The permission checks of the business layer. Every service calls them itself before it changes something, whatever
/// the UI shows or hides.
///
/// An administrator can restrict ThixCalendar with machine policies (value 1 / true):
///   Windows: HKLM\SOFTWARE\Policies\ThixCalendar (DWORD)
///   macOS:   a configuration profile for the domain info.schmoldt.thixcalendar (/Library/Managed Preferences)
/// Policies:
///   LockConfiguration   the settings and accounts are read-only (the calendars stay usable)
///   DisableOnlineAccounts  no Microsoft 365 / Google / ICS accounts (only calendars on this computer)
///   DisableUpdates      no update check, no update installation
///   DisableCloud        no connection to MatCloud (nothing leaves the computer but what the accounts themselves sync)
/// </summary>
public static class AccessPolicy
{
    public const string MacPolicyDomain = "info.schmoldt.thixcalendar";

    private const string PolicyKeyPath = @"SOFTWARE\Policies\ThixCalendar";

    private static readonly string MacPolicyFile = $"/Library/Managed Preferences/{MacPolicyDomain}.plist";

    private static Func<string, bool> _readPolicy = ReadMachinePolicy;

    private static (DateTime Written, Dictionary<string, bool> Values) _macPolicies = (DateTime.MinValue, []);

    public static bool IsConfigurationLocked => _readPolicy("LockConfiguration");

    public static bool AreOnlineAccountsDisabled => _readPolicy("DisableOnlineAccounts");

    public static bool AreUpdatesDisabled => _readPolicy("DisableUpdates");

    public static bool IsCloudDisabled => _readPolicy("DisableCloud");

    public static void EnsureConfigurationEditable()
    {
        if (IsConfigurationLocked)
        {
            throw new BusinessException("Die Einstellungen wurden von einem Administrator gesperrt (Richtlinie LockConfiguration).");
        }
    }

    public static void EnsureOnlineAccountsAllowed()
    {
        if (AreOnlineAccountsDisabled)
        {
            throw new BusinessException("Online-Konten wurden von einem Administrator deaktiviert (Richtlinie DisableOnlineAccounts).");
        }
    }

    public static void EnsureCloudAllowed()
    {
        if (IsCloudDisabled)
        {
            throw new BusinessException("Die Verbindung zur MatCloud wurde von einem Administrator deaktiviert (Richtlinie DisableCloud).");
        }
    }

    public static void EnsureUpdatesAllowed()
    {
        if (AreUpdatesDisabled)
        {
            throw new BusinessException("Updates wurden von einem Administrator deaktiviert (Richtlinie DisableUpdates).");
        }
    }

    /// <summary>Tests replace the registry with their own answers.</summary>
    internal static void UsePolicyReaderForTests(Func<string, bool>? reader) => _readPolicy = reader ?? ReadMachinePolicy;

    private static bool ReadMachinePolicy(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsPolicy(name);
        }

        return OperatingSystem.IsMacOS() && ReadMacPolicies().GetValueOrDefault(name);
    }

    [SupportedOSPlatform("windows")]
    private static bool ReadWindowsPolicy(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(PolicyKeyPath);
        return key?.GetValue(name) is int value && value == 1;
    }

    /// <summary>The managed preferences are a (binary) plist; plutil turns it into JSON. Read again only after a change.</summary>
    private static Dictionary<string, bool> ReadMacPolicies()
    {
        var written = File.Exists(MacPolicyFile) ? File.GetLastWriteTimeUtc(MacPolicyFile) : DateTime.MinValue;
        if (written == _macPolicies.Written)
        {
            return _macPolicies.Values;
        }

        var values = new Dictionary<string, bool>();
        if (written != DateTime.MinValue)
        {
            try
            {
                var startInfo = new ProcessStartInfo("/usr/bin/plutil") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "-convert", "json", "-o", "-", MacPolicyFile })
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo)!;
                var json = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                using var document = JsonDocument.Parse(json);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    values[property.Name] = property.Value.ValueKind == JsonValueKind.True ||
                                            (property.Value.ValueKind == JsonValueKind.Number && property.Value.GetInt32() == 1);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Warning($"Could not read the policies in {MacPolicyFile}: {exception.Message}");
            }
        }

        _macPolicies = (written, values);
        return values;
    }
}
