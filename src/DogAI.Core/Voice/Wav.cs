using System.Buffers.Binary;

namespace DogAI.Core.Voice;

/// <summary>Reads and writes WAV sound files, and changes sample rates. Mono only; stereo is mixed down on reading.</summary>
public static class Wav
{
    /// <summary>16-bit PCM WAV bytes for a clip.</summary>
    public static byte[] Write(AudioClip clip)
    {
        var data = clip.Samples.Length * 2;
        var bytes = new byte[44 + data];
        var span = bytes.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], clip.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], clip.SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data);
        for (var i = 0; i < clip.Samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], (short)Math.Round(Math.Clamp(clip.Samples[i], -1f, 1f) * short.MaxValue));
        return bytes;
    }

    /// <summary>Reads a PCM (8/16/24/32-bit) or 32-bit float WAV file. Throws <see cref="InvalidDataException"/> for anything else.</summary>
    public static AudioClip Read(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.Length < 12 || !span[..4].SequenceEqual("RIFF"u8) || !span[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a WAV file.");
        int format = 0, channels = 0, rate = 0, bits = 0;
        var pos = 12;
        while (pos + 8 <= span.Length)
        {
            var id = span.Slice(pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(span[(pos + 4)..]);
            var body = pos + 8;
            if (size < 0 || body + size > span.Length) size = span.Length - body;
            if (id.SequenceEqual("fmt "u8))
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(span[body..]);
                channels = BinaryPrimitives.ReadInt16LittleEndian(span[(body + 2)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(span[(body + 4)..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(span[(body + 14)..]);
                if (format == 0xFFFE && size >= 26) format = BinaryPrimitives.ReadInt16LittleEndian(span[(body + 24)..]); // WAVE_FORMAT_EXTENSIBLE
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels <= 0 || rate <= 0) break;
                return new AudioClip(Decode(span.Slice(body, size), format, channels, bits), rate);
            }
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException("The WAV file has no readable sound.");
    }

    private static float[] Decode(ReadOnlySpan<byte> data, int format, int channels, int bits)
    {
        var bytesPer = bits / 8;
        if (bytesPer == 0 || !(format == 1 || (format == 3 && bits == 32)))
            throw new InvalidDataException("Only PCM and 32-bit float WAV files can be read.");
        var frames = data.Length / (bytesPer * channels);
        var samples = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var s = data.Slice((f * channels + c) * bytesPer, bytesPer);
                sum += format == 3 ? BinaryPrimitives.ReadSingleLittleEndian(s) : bits switch
                {
                    8 => (s[0] - 128) / 128f,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                    24 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                };
            }
            samples[f] = sum / channels;
        }
        return samples;
    }

    /// <summary>The clip at another sample rate (linear interpolation, with a simple averaging filter when lowering).</summary>
    public static float[] Resample(float[] samples, int from, int to)
    {
        if (from == to || samples.Length == 0) return samples;
        var source = samples;
        if (to < from)
        {
            // Average neighbours first so high sounds do not fold back as noise.
            var width = Math.Max(1, (int)Math.Round(from / (double)to));
            if (width > 1)
            {
                source = new float[samples.Length];
                float run = 0;
                for (var i = 0; i < samples.Length; i++)
                {
                    run += samples[i];
                    if (i >= width) run -= samples[i - width];
                    source[i] = run / Math.Min(i + 1, width);
                }
            }
        }
        var count = (int)((long)source.Length * to / from);
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            var x = i * (double)from / to;
            var a = (int)x;
            var t = (float)(x - a);
            var s0 = source[Math.Min(a, source.Length - 1)];
            var s1 = source[Math.Min(a + 1, source.Length - 1)];
            result[i] = s0 + (s1 - s0) * t;
        }
        return result;
    }
}
