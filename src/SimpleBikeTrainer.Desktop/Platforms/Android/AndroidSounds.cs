using Android.Media;

namespace SimpleBikeTrainer.Desktop.Platforms;

/// <summary>Plays the step beeps on Android: each is loaded once into a track of its own and replayed from there.</summary>
internal static class AndroidSounds
{
    public const int SampleRate = 44100;

    // A WAV file's header before the samples, as Sounds.Chime writes it.
    private const int WavHeader = 44;

    private static readonly Dictionary<string, AudioTrack> Tracks = [];
    private static readonly Lock TracksLock = new();

    public static void Play(string name, Func<byte[]> wav)
    {
        try
        {
            AudioTrack track;
            lock (TracksLock)
            {
                if (!Tracks.TryGetValue(name, out var loaded))
                {
                    loaded = Tracks[name] = Load(wav());
                }

                track = loaded;
            }

            track.Stop();
            track.ReloadStaticData();
            track.Play();
        }
        catch (Exception e) when (e is Java.Lang.IllegalStateException or Java.Lang.UnsupportedOperationException)
        {
            // A missed beep is not worth interrupting a ride for.
            AndroidApp.Log(e, $"Could not play {name}");
        }
    }

    private static AudioTrack Load(byte[] wav)
    {
        var samples = wav.AsSpan(WavHeader).ToArray();
        var track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Sonification)!
                .Build()!)
            .SetAudioFormat(new AudioFormat.Builder()
                .SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(SampleRate)!
                .SetChannelMask(ChannelOut.Mono)!
                .Build()!)
            .SetTransferMode(AudioTrackMode.Static)
            .SetBufferSizeInBytes(samples.Length)
            .Build();
        track.Write(samples, 0, samples.Length);
        return track;
    }
}
