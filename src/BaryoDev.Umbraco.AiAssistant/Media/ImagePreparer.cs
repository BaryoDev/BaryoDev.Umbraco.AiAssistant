using System.Buffers.Binary;
using BaryoDev.Umbraco.AiAssistant.Claude;

namespace BaryoDev.Umbraco.AiAssistant.Media;

/// <summary>
/// Checks that an image is one the Messages API accepts: jpeg, png, gif or webp, under the size and pixel limits.
/// Images are sent as they are. The API scales large images down itself, so no image library is needed here.
/// </summary>
public static class ImagePreparer
{
    /// <summary>The API limit is 5 MB per image.</summary>
    public const int MaxBytes = 4_500_000;

    /// <summary>The API rejects an edge longer than 8000 pixels.</summary>
    public const int MaxEdge = 8000;

    /// <summary>Throws ArgumentException with a plain reason when the file cannot be used.</summary>
    public static ClaudeImage Prepare(byte[] data, string? fileName)
    {
        if (string.Equals(Path.GetExtension(fileName), ".svg", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SVG files are skipped. Claude cannot read them as images.");
        if (data.Length == 0) throw new ArgumentException("The file is empty.");

        (string mime, int width, int height) = Sniff(data) ?? throw new ArgumentException("The file is not a JPEG, PNG, GIF or WebP image that can be read.");
        if (data.Length > MaxBytes) throw new ArgumentException("The image is larger than 4.5 MB. Resize it and run again.");
        if (width <= 0 || height <= 0 || Math.Max(width, height) > MaxEdge)
            throw new ArgumentException($"The image is longer than {MaxEdge} pixels on one side. Resize it and run again.");
        return new ClaudeImage(mime, Convert.ToBase64String(data));
    }

    internal static (string Mime, int Width, int Height)? Sniff(byte[] d)
    {
        try
        {
            if (d.Length >= 24 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G' && d[12] == 'I' && d[13] == 'H' && d[14] == 'D' && d[15] == 'R')
                return ("image/png", (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(20)));

            if (d.Length >= 10 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8')
                return ("image/gif", BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8)));

            if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8) return Jpeg(d);
            if (d.Length >= 30 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return Webp(d);
        }
        catch (ArgumentOutOfRangeException) { }
        return null;
    }

    private static (string, int, int)? Jpeg(byte[] d)
    {
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            int length = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2));
            bool frame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (frame) return ("image/jpeg", BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 7)), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 5)));
            if (length < 2) return null;
            i += 2 + length;
        }
        return null;
    }

    private static (string, int, int)? Webp(byte[] d)
    {
        string chunk = System.Text.Encoding.ASCII.GetString(d, 12, 4);
        switch (chunk)
        {
            case "VP8X":
                return ("image/webp", 1 + (d[24] | d[25] << 8 | d[26] << 16), 1 + (d[27] | d[28] << 8 | d[29] << 16));
            case "VP8L":
                {
                    uint bits = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(21));
                    return ("image/webp", (int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
                }
            case "VP8 ":
                return ("image/webp", BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(26)) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(28)) & 0x3FFF);
            default:
                return null;
        }
    }
}
