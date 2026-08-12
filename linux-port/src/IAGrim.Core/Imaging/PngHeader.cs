using System.Buffers.Binary;

namespace IAGrim.Core.Imaging;

/// <summary>
/// Reads the dimensions out of a PNG without decoding it.
///
/// Some ARC entries are already PNG. Upstream ran them through GDI+ purely to learn their
/// size before deciding whether they were an icon or a full texture; the size is in the
/// IHDR chunk, which is always first.
/// </summary>
public static class PngHeader {
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool TryReadSize(byte[] data, out int width, out int height) {
        width = height = 0;

        // signature (8) + length (4) + "IHDR" (4) + width (4) + height (4)
        if (data is null || data.Length < 24) {
            return false;
        }
        if (!data.AsSpan(0, 8).SequenceEqual(Signature)) {
            return false;
        }
        if (data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R') {
            return false;
        }

        width  = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));

        return width > 0 && height > 0;
    }
}
