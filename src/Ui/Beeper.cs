using System.IO;
using System.Media;

namespace ClipWatch;

public enum ToastSound
{
    None,

    Start,

    Save,

    Error
}

public static class Beeper
{
    private const int SampleRate = 44100;

    private static readonly Dictionary<ToastSound, byte[]> Cache = new();
    private static readonly object Gate = new();

    public static void Play(ToastSound sound, Config config)
    {
        if (sound == ToastSound.None || !config.PlaySounds) return;

        var volume = Math.Clamp(config.SoundVolume, 0.0, 1.0);
        if (volume <= 0) return;

        byte[] wav;
        lock (Gate)
        {
            if (!Cache.TryGetValue(sound, out var cached))
            {
                cached = Build(sound, volume);
                Cache[sound] = cached;
            }
            wav = cached;
        }

        try
        {
            var player = new SoundPlayer(new MemoryStream(wav));
            player.Play();
        }
        catch
        {
        }
    }

    public static void Reset()
    {
        lock (Gate) Cache.Clear();
    }

    private static byte[] Build(ToastSound sound, double volume) => sound switch
    {
        ToastSound.Start => Render(volume, (784, 0.230, 2.6)),
        ToastSound.Save => Render(volume, (880, 0.170, 3.0), (0, 0.010, 0), (1175, 0.520, 1.9)),
        ToastSound.Error => Render(volume, (300, 0.320, 2.4)),
        _ => Array.Empty<byte>()
    };

    private static byte[] Render(double volume, params (double Freq, double Seconds, double Decay)[] segments)
    {
        var samples = new List<short>();

        foreach (var (freq, seconds, decay_k) in segments)
        {
            var count = (int)(SampleRate * seconds);

            for (var i = 0; i < count; i++)
            {
                if (freq <= 0)
                {
                    samples.Add(0);
                    continue;
                }

                var t = (double)i / SampleRate;

                var attack = Math.Min(1.0, t / 0.006);
                var decay = Math.Exp(-decay_k * (t / seconds));
                var envelope = attack * decay;

                var w = 2 * Math.PI * freq * t;
                var tone = Math.Sin(w)
                         + 0.30 * Math.Sin(2 * w) * decay
                         + 0.11 * Math.Sin(3 * w) * decay * decay;

                var value = (tone / 1.41) * envelope * volume * 0.60;
                samples.Add((short)(Math.Clamp(value, -1.0, 1.0) * short.MaxValue));
            }
        }

        return WriteWav(samples);
    }

    private static byte[] WriteWav(List<short> samples)
    {
        var dataLength = samples.Count * 2;

        using var ms = new MemoryStream(44 + dataLength);
        using var w = new BinaryWriter(ms);

        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataLength);
        w.Write(new[] { 'W', 'A', 'V', 'E' });

        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);

        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataLength);

        foreach (var s in samples) w.Write(s);

        w.Flush();
        return ms.ToArray();
    }
}
