using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace VideoDownloader;

sealed class BackgroundPack
{
    static readonly byte[] Magic = "NYAB"u8.ToArray();
    const byte FormatVersion = 1;
    public const int Quality = 85;

    readonly byte[] _data;
    readonly (int Offset, int Length)[] _frames;

    BackgroundPack(byte[] data, (int, int)[] frames, int[] delays, string source)
    {
        _data = data;
        _frames = frames;
        Delays = delays;
        Source = source;
    }

    public int Count => _frames.Length;
    public int[] Delays { get; }
    public string Source { get; }

    public static string SourceOf(byte[] gif) => Convert.ToHexString(SHA256.HashData(gif), 0, 8);

    public Bitmap Frame(int index)
    {
        var (offset, length) = _frames[index];
        using var ms = new MemoryStream(_data, offset, length, writable: false);
        using var image = Image.FromStream(ms);
        return new Bitmap(image);
    }

    public static BackgroundPack Read(byte[] data)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data, writable: false));
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadByte() != FormatVersion)
                throw new InvalidDataException("not a background pack");
            var source = Convert.ToHexString(reader.ReadBytes(8));
            int count = reader.ReadUInt16();
            if (count == 0) throw new InvalidDataException("the pack has no frames");
            var frames = new (int, int)[count];
            var delays = new int[count];
            for (int i = 0; i < count; i++)
            {
                delays[i] = reader.ReadInt32();
                int length = reader.ReadInt32();
                int offset = (int)reader.BaseStream.Position;
                if (length <= 0 || offset + (long)length > data.Length) throw new InvalidDataException($"frame {i} is cut off");
                frames[i] = (offset, length);
                reader.BaseStream.Position = offset + length;
            }
            return new BackgroundPack(data, frames, delays, source);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("the pack is cut off", e);
        }
    }

    public static byte[] Pack(byte[] gifBytes, int quality = Quality)
    {
        using var gifStream = new MemoryStream(gifBytes, writable: false);
        using var gif = Image.FromStream(gifStream);
        var time = FrameDimension.Time;
        int count = gif.GetFrameCount(time);
        if (count > ushort.MaxValue) throw new InvalidDataException("too many frames");
        var delays = GifDelays(gif, count);
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(Convert.FromHexString(SourceOf(gifBytes)));
        writer.Write((ushort)count);
        for (int i = 0; i < count; i++)
        {
            gif.SelectActiveFrame(time, i);
            using var frame = Backdrop.Frame(gif, 0);
            using var flat = new Bitmap(frame.Width, frame.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(flat))
            {
                g.Clear(Color.White);
                g.DrawImageUnscaled(frame, 0, 0);
            }
            using var encoded = new MemoryStream();
            flat.Save(encoded, jpeg, parameters);
            writer.Write(delays[i]);
            writer.Write((int)encoded.Length);
            writer.Write(encoded.GetBuffer(), 0, (int)encoded.Length);
        }
        writer.Flush();
        return output.ToArray();
    }

    internal static int[] GifDelays(Image gif, int count)
    {
        var delays = Enumerable.Repeat(100, count).ToArray();
        try
        {
            var raw = gif.GetPropertyItem(0x5100)?.Value;
            if (raw != null)
                for (int i = 0; i < count && i * 4 + 3 < raw.Length; i++)
                {
                    int d = BitConverter.ToInt32(raw, i * 4) * 10;
                    delays[i] = d < 20 ? 100 : d;
                }
        }
        catch (Exception e) when (e is ArgumentException or System.Runtime.InteropServices.ExternalException) { }
        return delays;
    }
}
