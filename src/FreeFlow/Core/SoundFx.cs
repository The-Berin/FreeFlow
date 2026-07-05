using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FreeFlow.Core;

/// <summary>Short soft tones for record start/stop/error, generated on the fly.</summary>
public static class SoundFx
{
    public static void RecordStart(AppConfig cfg) => Play(cfg, 880, 70);
    public static void RecordStop(AppConfig cfg) => Play(cfg, 587, 70);
    public static void ErrorTone(AppConfig cfg) => Play(cfg, 220, 160);

    private static void Play(AppConfig cfg, double freq, int ms)
    {
        if (!cfg.PlaySounds) return;
        try
        {
            var gen = new SignalGenerator(44100, 1)
            {
                Type = SignalGeneratorType.Sin,
                Frequency = freq,
                Gain = 0.12,
            }.Take(TimeSpan.FromMilliseconds(ms));

            var outDev = new WaveOutEvent();
            outDev.Init(gen);
            outDev.PlaybackStopped += (_, _) => outDev.Dispose();
            outDev.Play();
        }
        catch
        {
            // no output device — dictation still works, stay silent
        }
    }
}
