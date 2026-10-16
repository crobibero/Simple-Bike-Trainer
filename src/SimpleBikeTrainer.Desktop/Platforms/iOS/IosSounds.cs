using AVFoundation;
using Foundation;

namespace SimpleBikeTrainer.Desktop.Platforms;

/// <summary>Plays the step beeps on iPadOS, over music from another app and with the iPad on silent.</summary>
internal static class IosSounds
{
    private static readonly Dictionary<string, AVAudioPlayer?> Players = [];
    private static readonly Lock PlayersLock = new();

    public static void Play(string name, Func<byte[]> wav)
    {
        AVAudioPlayer? player;
        lock (PlayersLock)
        {
            if (Players.Count == 0)
            {
                AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionCategoryOptions.MixWithOthers);
            }

            if (!Players.TryGetValue(name, out player))
            {
                // Null when the WAV will not load: that beep then stays silent, without trying again.
                player = Players[name] = AVAudioPlayer.FromData(NSData.FromArray(wav()), out _);
                player?.PrepareToPlay();
            }
        }

        if (player is null)
        {
            return;
        }

        player.Stop();
        player.CurrentTime = 0;
        player.Play();
    }
}
