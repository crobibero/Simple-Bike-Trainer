using System.Diagnostics;

namespace SimpleBikeTrainer.Desktop.Services;

/// <summary>
/// Short beeps for step cues. They are written once as WAV files and played with the first player found
/// (PipeWire's pw-play, PulseAudio's paplay, or ALSA's aplay); with none, cues stay silent.
/// </summary>
public static class Sounds
{
    private static readonly Lazy<string?> Player = new(FindPlayer);
    private static readonly Lazy<string> Directory = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), $"simple-bike-trainer-{Environment.UserName}");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    });

    /// <summary>Set by tests to record what would play instead of playing it.</summary>
    internal static Action<string>? Played { get; set; }

    /// <summary>A short high beep: 3, 2, 1.</summary>
    public static void Tick() => Play("tick", 880, 0.12);

    /// <summary>A longer, higher beep as a step starts.</summary>
    public static void Go() => Play("go", 1320, 0.45);

    private static void Play(string name, double hertz, double seconds)
    {
        if (Played is { } record)
        {
            record(name);
            return;
        }

        if (Player.Value is not { } player)
        {
            return;
        }

        try
        {
            var file = Path.Combine(Directory.Value, $"{name}.wav");
            if (!File.Exists(file))
            {
                File.WriteAllBytes(file, Tone(hertz, seconds));
            }

            var start = new ProcessStartInfo(player) { UseShellExecute = false };
            start.ArgumentList.Add(file);
            using var process = Process.Start(start);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // A missed beep is not worth interrupting a ride for.
        }
    }

    private static string? FindPlayer()
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(':') ?? [];
        return new[] { "pw-play", "paplay", "aplay" }
            .SelectMany(name => path.Select(dir => Path.Combine(dir, name)))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>A 16-bit mono sine tone with 10 ms fades, as a WAV file.</summary>
    internal static byte[] Tone(double hertz, double seconds, int rate = 44100)
    {
        var count = (int)(seconds * rate);
        var fade = rate / 100;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + (count * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(count * 2);
        for (var i = 0; i < count; i++)
        {
            var envelope = Math.Min(1, Math.Min(i, count - i) / (double)fade);
            writer.Write((short)(Math.Sin(2 * Math.PI * hertz * i / rate) * envelope * short.MaxValue * 0.5));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
