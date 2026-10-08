using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PvZDynamicMusic
{
    /// <summary>Audio PCM decodificato: campioni float interleaved in [-1, 1].</summary>
    internal sealed class PcmData
    {
        public string Name;
        public float[] Samples;
        public int Channels;
        public int SampleRate;
        public int Frames => Channels > 0 ? Samples.Length / Channels : 0;
        public double Seconds => SampleRate > 0 ? (double)Frames / SampleRate : 0;
    }

    /// <summary>Parser WAV (PCM 8/16/24/32 bit, float 32/64 bit, anche WAVE_FORMAT_EXTENSIBLE).</summary>
    internal static class WavLoader
    {
        public static PcmData Load(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 44 || !Tag(b, 0, "RIFF") || !Tag(b, 8, "WAVE"))
                throw new InvalidDataException("non e' un file WAV RIFF valido");

            int fmtTag = 0, channels = 0, rate = 0, blockAlign = 0, bits = 0;
            int dataOff = -1;
            long dataLen = 0;
            int pos = 12;

            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                uint size = BitConverter.ToUInt32(b, pos + 4);
                int body = pos + 8;

                if (id == "fmt ")
                {
                    fmtTag = BitConverter.ToUInt16(b, body);
                    channels = BitConverter.ToUInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    blockAlign = BitConverter.ToUInt16(b, body + 12);
                    bits = BitConverter.ToUInt16(b, body + 14);
                    if (fmtTag == 0xFFFE && size >= 26)
                        fmtTag = BitConverter.ToUInt16(b, body + 24); // sotto-formato reale
                }
                else if (id == "data")
                {
                    dataOff = body;
                    dataLen = Math.Min((long)size, (long)b.Length - body);
                    break;
                }

                long next = (long)body + size + (size & 1);
                if (next > b.Length) break;
                pos = (int)next;
            }

            if (dataOff < 0 || channels <= 0 || rate <= 0 || bits <= 0)
                throw new InvalidDataException("intestazione WAV incompleta (fmt/data mancanti)");
            if (blockAlign <= 0) blockAlign = channels * (bits / 8);

            int frames = (int)(dataLen / blockAlign);
            if (frames <= 0)
                throw new InvalidDataException("il file WAV non contiene campioni audio (0 frame)");
            int n = frames * channels;
            var s = new float[n];
            var span = new ReadOnlySpan<byte>(b, dataOff, frames * blockAlign);

            if (fmtTag == 1) // PCM intero
            {
                switch (bits)
                {
                    case 8:
                        for (int i = 0; i < n; i++) s[i] = (span[i] - 128) * (1f / 128f);
                        break;
                    case 16:
                    {
                        var src = MemoryMarshal.Cast<byte, short>(span);
                        for (int i = 0; i < n; i++) s[i] = src[i] * (1f / 32768f);
                        break;
                    }
                    case 24:
                        for (int i = 0, o = 0; i < n; i++, o += 3)
                        {
                            int v = span[o] | (span[o + 1] << 8) | (span[o + 2] << 16);
                            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                            s[i] = v * (1f / 8388608f);
                        }
                        break;
                    case 32:
                    {
                        var src = MemoryMarshal.Cast<byte, int>(span);
                        for (int i = 0; i < n; i++) s[i] = (float)(src[i] * (1.0 / 2147483648.0));
                        break;
                    }
                    default:
                        throw new InvalidDataException("PCM a " + bits + " bit non supportato");
                }
            }
            else if (fmtTag == 3) // float IEEE
            {
                if (bits == 32)
                {
                    var src = MemoryMarshal.Cast<byte, float>(span);
                    for (int i = 0; i < n; i++) s[i] = src[i];
                }
                else if (bits == 64)
                {
                    var src = MemoryMarshal.Cast<byte, double>(span);
                    for (int i = 0; i < n; i++) s[i] = (float)src[i];
                }
                else throw new InvalidDataException("float a " + bits + " bit non supportato");
            }
            else
            {
                throw new InvalidDataException("formato WAV non supportato (tag " + fmtTag + "): serve PCM o float");
            }

            return new PcmData
            {
                Name = Path.GetFileName(path),
                Samples = s,
                Channels = channels,
                SampleRate = rate
            };
        }

        private static bool Tag(byte[] b, int off, string tag)
        {
            for (int i = 0; i < 4; i++) if (b[off + i] != tag[i]) return false;
            return true;
        }
    }
}
