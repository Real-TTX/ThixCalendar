using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ThixCalendar.Core.Infrastructure;

namespace ThixCalendar.Core.Platform;

/// <summary>
/// The reminder sound. Windows: ThixCalendar's own soft two-note chime (generated once, played from memory through
/// winmm – it sounds even when the system sounds are off, but follows the volume). macOS: the system sound "Glass".
/// Plays without blocking; a sound that cannot be played is logged and skipped.
/// </summary>
public static partial class Chime
{
    private const uint SoundAsync = 0x0001;
    private const uint SoundMemory = 0x0004;
    private const uint SoundNoDefault = 0x0002;

    private static readonly object Gate = new();
    private static IntPtr _wave;

    /// <summary>Tests count instead of playing.</summary>
    internal static int PlayedForTests { get; private set; }

    internal static bool IsMutedForTests { get; set; }

    public static void Play()
    {
        if (IsMutedForTests)
        {
            PlayedForTests++;
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                PlayWindows();
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("/usr/bin/afplay") { ArgumentList = { "/System/Library/Sounds/Glass.aiff" }, UseShellExecute = false })?.Dispose();
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warning($"Reminder sound could not be played: {exception.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void PlayWindows()
    {
        lock (Gate)
        {
            if (_wave == IntPtr.Zero)
            {
                // Unmanaged and kept for the life of the process: winmm reads it while the sound plays (SND_ASYNC).
                var bytes = Wave();
                _wave = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, _wave, bytes.Length);
            }

            PlaySound(_wave, IntPtr.Zero, SoundMemory | SoundAsync | SoundNoDefault);
        }
    }

    /// <summary>A 16-bit mono WAV: two bell-like notes (E6, then A6) with a soft attack and a long fade.</summary>
    internal static byte[] Wave()
    {
        const int sampleRate = 44100;
        var notes = new[] { (Frequency: 1318.5, Start: 0.0, Length: 0.55), (Frequency: 1760.0, Start: 0.16, Length: 0.75) };
        var total = (int)(sampleRate * 0.95);
        var samples = new double[total];
        foreach (var (frequency, start, length) in notes)
        {
            var first = (int)(start * sampleRate);
            var count = (int)(length * sampleRate);
            for (var index = 0; index < count && first + index < total; index++)
            {
                var time = (double)index / sampleRate;
                var attack = Math.Min(1.0, time / 0.006);
                var decay = Math.Exp(-time * 5.5);
                var tone = Math.Sin(2 * Math.PI * frequency * time) + 0.35 * Math.Sin(2 * Math.PI * frequency * 2 * time) + 0.12 * Math.Sin(2 * Math.PI * frequency * 3 * time);
                samples[first + index] += 0.32 * attack * decay * tone;
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + total * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(total * 2);
        foreach (var sample in samples)
        {
            writer.Write((short)Math.Round(Math.Clamp(sample, -1, 1) * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
