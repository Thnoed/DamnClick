using System.IO;
using System.Media;

namespace Clicker.Core;

/// <summary>
/// Toggle blips played from WAVs embedded in the exe (no sidecar files).
/// System.Media.SoundPlayer is used instead of WPF MediaPlayer: it needs no
/// codecs, no STA thread, no async open — Play() just works from any thread
/// and always shows up in the volume mixer.
/// Volume is applied by scaling PCM samples in memory on change.
/// </summary>
public sealed class SoundBank : IDisposable
{
    private byte[]? _onRaw;
    private byte[]? _offRaw;
    private SoundPlayer? _onPlayer;
    private SoundPlayer? _offPlayer;
    private int _volume = 50;
    private bool _disposed;

    public SoundBank()
    {
        _onRaw = ReadWavResource("snd_on.wav");
        _offRaw = ReadWavResource("snd_off.wav");
        RebuildPlayers();
    }

    public void SetVolume(int volume)
    {
        volume = Num.Clamp(volume, 0, 100);
        if (volume == _volume && _onPlayer != null) return;
        _volume = volume;
        RebuildPlayers();
    }

    public void Play(bool on)
    {
        if (_disposed || _volume <= 0) return;
        try { (on ? _onPlayer : _offPlayer)?.Play(); } catch { }
    }

    private void RebuildPlayers()
    {
        try { _onPlayer?.Stop(); } catch { }
        try { _offPlayer?.Stop(); } catch { }
        _onPlayer = scaled(_onRaw);
        _offPlayer = scaled(_offRaw);
    }

    private SoundPlayer? scaled(byte[]? raw)
    {
        if (raw == null) return null;
        try { return new SoundPlayer(new MemoryStream(ScaleWav(raw, _volume))); }
        catch { return null; }
    }

    private static byte[]? ReadWavResource(string name)
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase));
            if (resName == null) return null;
            using var rs = asm.GetManifestResourceStream(resName);
            if (rs == null) return null;
            using var reader = new System.Resources.ResourceReader(rs);
            foreach (System.Collections.DictionaryEntry e in reader)
            {
                if (string.Equals((string)e.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (e.Value is Stream s)
                    {
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                    if (e.Value is byte[] b) return b;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Scale 16-bit PCM samples by volume. Unknown layouts pass through.</summary>
    private static byte[] ScaleWav(byte[] wav, int volume)
    {
        if (volume >= 100) return wav;
        try
        {
            if (wav.Length < 44 || wav[0] != 'R') return wav;
            int fmtPos = FindChunk(wav, "fmt ");
            int dataPos = FindChunk(wav, "data");
            if (fmtPos < 0 || dataPos < 0) return wav;
            ushort format = BitConverter.ToUInt16(wav, fmtPos + 8);
            ushort bits = BitConverter.ToUInt16(wav, fmtPos + 22);
            if (format != 1 || bits != 16) return wav; // PCM16 only
            int dataLen = BitConverter.ToInt32(wav, dataPos + 4);
            int start = dataPos + 8;
            if (start + dataLen > wav.Length) return wav;
            var out_ = (byte[])wav.Clone();
            double k = volume / 100.0;
            for (int i = start; i < start + dataLen; i += 2)
            {
                short s = (short)(wav[i] | (wav[i + 1] << 8));
                int v = (int)(s * k);
                if (v > short.MaxValue) v = short.MaxValue;
                if (v < short.MinValue) v = short.MinValue;
                out_[i] = (byte)(v & 0xFF);
                out_[i + 1] = (byte)((v >> 8) & 0xFF);
            }
            return out_;
        }
        catch { return wav; }
    }

    private static int FindChunk(byte[] wav, string id)
    {
        for (int i = 12; i + 8 <= wav.Length; i++)
        {
            if (wav[i] == id[0] && wav[i + 1] == id[1] && wav[i + 2] == id[2] && wav[i + 3] == id[3])
                return i;
        }
        return -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _onPlayer?.Stop(); } catch { }
        try { _offPlayer?.Stop(); } catch { }
        try { _onPlayer?.Dispose(); } catch { }
        try { _offPlayer?.Dispose(); } catch { }
    }
}
