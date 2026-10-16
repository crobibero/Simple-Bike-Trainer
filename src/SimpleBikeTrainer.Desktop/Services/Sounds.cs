using System.Diagnostics;

namespace SimpleBikeTrainer.Desktop.Services;

/// <summary>Played with pw-play, paplay or aplay, whichever is found first; with none, cues stay silent.</summary>
public static class Sounds
{
    // E5 and A5: a fourth apart, high enough to carry over a trainer and a fan.
    private const double E5 = 659.26;
    private const double A5 = 880;

    private static readonly Note[] TickNotes = [new(E5, 0, 0.06)];
    private static readonly Note[] GoNotes = [new(E5, 0, 0.18), new(A5, 0.11, 0.22)];

    private static readonly Lazy<string?> Player = new(FindPlayer);
    private static readonly HashSet<string> Written = [];
    private static readonly Lazy<string> Directory = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), $"simple-bike-trainer-{Environment.UserName}");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    });

    /// <summary>Set by tests to record what would play instead of playing it.</summary>
    internal static Action<string>? Played { get; set; }

    /// <summary>A short tap: 3, 2, 1.</summary>
    public static void Tick() => Play("tick", TickNotes, 0.25);

    /// <summary>Two rising notes as a step starts.</summary>
    public static void Go() => Play("go", GoNotes, 0.8);

    private static void Play(string name, Note[] notes, double seconds)
    {
        if (Played is { } record)
        {
            record(name);
            return;
        }

#if ANDROID
        Platforms.AndroidSounds.Play(name, () => Chime(notes, seconds, Platforms.AndroidSounds.SampleRate));
#elif IOS
        Platforms.IosSounds.Play(name, () => Chime(notes, seconds));
#else
        if (Player.Value is not { } player)
        {
            return;
        }

        try
        {
            // Rewritten on each run's first play, so a file left by an older version, with other tones, is replaced.
            var file = Path.Combine(Directory.Value, $"{name}.wav");
            lock (Written)
            {
                if (Written.Add(name))
                {
                    File.WriteAllBytes(file, Chime(notes, seconds));
                }
            }

            var start = new ProcessStartInfo(player) { UseShellExecute = false };
            start.ArgumentList.Add(file);
            using var process = Process.Start(start);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // A missed beep is not worth interrupting a ride for.
        }
#endif
    }

    private static string? FindPlayer()
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(':') ?? [];
        return new[] { "pw-play", "paplay", "aplay" }
            .SelectMany(name => path.Select(dir => Path.Combine(dir, name)))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Mallet-struck notes as a 16-bit mono WAV, peaking at half of full scale, with the last 20 ms faded out.</summary>
    internal static byte[] Chime(IReadOnlyList<Note> notes, double seconds, int rate = 44100)
    {
        const double Attack = 0.012;
        const double Overtone = 0.12;
        var count = (int)(seconds * rate);
        var samples = new double[count];
        foreach (var note in notes)
        {
            for (var i = (int)(note.Start * rate); i < count; i++)
            {
                var t = (i / (double)rate) - note.Start;
                var rise = t < Attack ? 0.5 - (0.5 * Math.Cos(Math.PI * t / Attack)) : 1;
                var tone = Math.Sin(2 * Math.PI * note.Hertz * t) * Math.Exp(-t / note.Ring);
                var strike = Overtone * Math.Sin(4 * Math.PI * note.Hertz * t) * Math.Exp(-t * 3 / note.Ring);
                samples[i] += rise * (tone + strike);
            }
        }

        var peak = samples.Max(Math.Abs);
        var tail = rate / 50;
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
            var fade = Math.Min(1, (count - i) / (double)tail);
            writer.Write((short)(samples[i] / peak * fade * short.MaxValue * 0.5));
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary><see cref="Ring"/> is the time for the note to fall to 37% (1/e).</summary>
    internal readonly record struct Note(double Hertz, double Start, double Ring);
}
