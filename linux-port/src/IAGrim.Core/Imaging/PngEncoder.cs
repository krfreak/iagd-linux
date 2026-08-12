using System.Buffers.Binary;
using System.IO.Compression;

namespace IAGrim.Core.Imaging;

/// <summary>
/// Minimal PNG writer, used to replace <c>Bitmap.Save(path, ImageFormat.Png)</c>.
///
/// Upstream extracts item icons through System.Drawing, which throws
/// <c>TypeInitializationException</c> (GDI+ unavailable) on Linux since .NET 7. The DDS
/// decoding upstream does is already pure byte manipulation producing a BGRA
/// <see cref="int"/>[]; only the final encode needed replacing.
///
/// Written by hand rather than pulling in ImageSharp or SkiaSharp: the required subset is
/// small, it avoids a native dependency in the shipped app, and it sidesteps ImageSharp's
/// split-licence question entirely. Output is 8-bit RGBA, non-interlaced, which every
/// consumer of these icons (a browser) handles natively.
/// </summary>
public static class PngEncoder {
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <param name="pixels">
    /// One packed ARGB value per pixel, row-major, matching what upstream's DDS readers
    /// produce for a 32bppArgb bitmap: 0xAARRGGBB.
    /// </param>
    public static void WriteArgb(string path, int width, int height, int[] pixels) {
        using var stream = File.Create(path);
        WriteArgb(stream, width, height, pixels);
    }

    public static void WriteArgb(Stream output, int width, int height, int[] pixels) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (pixels.Length < (long)width * height) {
            throw new ArgumentException(
                $"Pixel buffer holds {pixels.Length} entries, need {(long)width * height} for {width}x{height}.",
                nameof(pixels));
        }

        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
        ihdr[8]  = 8;   // bit depth
        ihdr[9]  = 6;   // colour type: truecolour with alpha
        ihdr[10] = 0;   // compression: deflate
        ihdr[11] = 0;   // filter method
        ihdr[12] = 0;   // no interlace
        WriteChunk(output, "IHDR", ihdr);

        WriteChunk(output, "IDAT", BuildImageData(width, height, pixels));
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Scanlines, each prefixed with filter type 0 (None), zlib-compressed.
    /// Filtering would shrink the output, but these are tiny icons and the decode cost of
    /// getting a filter wrong is far worse than the bytes saved.
    /// </summary>
    private static byte[] BuildImageData(int width, int height, int[] pixels) {
        var raw = new byte[(long)height * (1 + (long)width * 4) is var size && size <= int.MaxValue
            ? (int)size
            : throw new ArgumentException("Image is too large to encode.")];

        var offset = 0;
        for (var y = 0; y < height; y++) {
            raw[offset++] = 0;   // filter: None

            var row = y * width;
            for (var x = 0; x < width; x++) {
                var argb = pixels[row + x];
                raw[offset++] = (byte)(argb >> 16);   // R
                raw[offset++] = (byte)(argb >> 8);    // G
                raw[offset++] = (byte)argb;           // B
                raw[offset++] = (byte)(argb >> 24);   // A
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) {
            zlib.Write(raw, 0, raw.Length);
        }
        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data) {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++) {
            typeBytes[i] = (byte)type[i];
        }
        output.Write(typeBytes);
        output.Write(data);

        // The CRC covers the type and the data, but not the length.
        var crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static class Crc32 {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable() {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++) {
                var c = n;
                for (var k = 0; k < 8; k++) {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) {
            var c = 0xFFFFFFFFu;
            foreach (var b in first)  c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            foreach (var b in second) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
