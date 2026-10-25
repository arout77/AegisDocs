using System.Text;

namespace ReceiptCapture.Core.HiddenContent;

/// <summary>What a picture's EXIF block gives away. Only the few fields that matter for privacy are read.</summary>
internal sealed class ExifInfo
{
    public bool HasGps { get; set; }
    public bool HasThumbnail { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public string? Software { get; set; }
    public string? DateTaken { get; set; }
    public string? Artist { get; set; }
    public string? Copyright { get; set; }
    public string? Description { get; set; }
    public string? OwnerName { get; set; }
    public string? UserComment { get; set; }
    public bool HasSerialNumber { get; set; }
}

/// <summary>A small, careful reader for the TIFF structure inside EXIF. Anything out of range just stops the read.</summary>
internal static class ExifReader
{
    /// <param name="tiff">The bytes starting at the byte-order mark ("II" or "MM").</param>
    public static ExifInfo? Parse(ReadOnlySpan<byte> block) => ParseBytes(block.ToArray());

    private static ExifInfo? ParseBytes(byte[] tiff)
    {
        if (tiff.Length < 8) return null;
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') little = false;
        else return null;

        ushort U16(int o) => o < 0 || o + 2 > tiff.Length ? (ushort)0 : little ? (ushort)(tiff[o] | tiff[o + 1] << 8) : (ushort)(tiff[o] << 8 | tiff[o + 1]);
        uint U32(int o) => o < 0 || o + 4 > tiff.Length ? 0
            : little ? (uint)(tiff[o] | tiff[o + 1] << 8 | tiff[o + 2] << 16 | tiff[o + 3] << 24)
                     : (uint)(tiff[o] << 24 | tiff[o + 1] << 16 | tiff[o + 2] << 8 | tiff[o + 3]);

        if (U16(2) != 42) return null;

        var info = new ExifInfo();

        // Reads one image directory: calls back for every entry and returns the offset of the next directory.
        uint ReadIfd(uint offset, Action<ushort, ushort, uint, int> entry)
        {
            if (offset == 0 || offset + 2 > tiff.Length) return 0;
            int count = Math.Min((int)U16((int)offset), 500);
            for (int i = 0; i < count; i++)
            {
                int e = (int)offset + 2 + i * 12;
                if (e + 12 > tiff.Length) return 0;
                entry(U16(e), U16(e + 2), U32(e + 4), e + 8);
            }
            int next = (int)offset + 2 + count * 12;
            return U32(next);
        }

        string? Ascii(ushort type, uint count, int valueAt)
        {
            if (type != 2 || count == 0 || count > 2000) return null;
            int at = count <= 4 ? valueAt : (int)U32(valueAt);
            if (at < 0 || at + (int)count > tiff.Length) return null;
            var s = Encoding.ASCII.GetString(tiff.AsSpan(at, (int)count)).Trim('\0', ' ');
            return s.Length == 0 ? null : s;
        }

        uint exifIfd = 0, gpsIfd = 0;
        uint ifd1 = ReadIfd(U32(4), (tag, type, count, at) =>
        {
            switch (tag)
            {
                case 0x010E: info.Description = Ascii(type, count, at); break;
                case 0x010F: info.Make = Ascii(type, count, at); break;
                case 0x0110: info.Model = Ascii(type, count, at); break;
                case 0x0131: info.Software = Ascii(type, count, at); break;
                case 0x0132: info.DateTaken ??= Ascii(type, count, at); break;
                case 0x013B: info.Artist = Ascii(type, count, at); break;
                case 0x8298: info.Copyright = Ascii(type, count, at); break;
                case 0x8769: exifIfd = U32(at); break;
                case 0x8825: gpsIfd = U32(at); break;
            }
        });

        if (exifIfd != 0)
            ReadIfd(exifIfd, (tag, type, count, at) =>
            {
                switch (tag)
                {
                    case 0x9003: info.DateTaken = Ascii(type, count, at) ?? info.DateTaken; break;
                    case 0xA430: info.OwnerName = Ascii(type, count, at); break;
                    case 0xA431: case 0xA435: if (Ascii(type, count, at) != null) info.HasSerialNumber = true; break;
                    case 0x9286:
                        {
                            // 8 bytes naming the character set, then the text.
                            int from = count <= 4 ? at : (int)U32(at);
                            if (count > 8 && count < 4000 && from >= 0 && from + (int)count <= tiff.Length)
                            {
                                var text = Encoding.UTF8.GetString(tiff.AsSpan(from + 8, (int)count - 8)).Trim('\0', ' ');
                                if (text.Length > 0) info.UserComment = text;
                            }
                            break;
                        }
                }
            });

        if (gpsIfd != 0)
            ReadIfd(gpsIfd, (tag, _, _, _) =>
            {
                if (tag is 0x0002 or 0x0004) info.HasGps = true;      // latitude or longitude is present
            });

        if (ifd1 != 0)
            ReadIfd(ifd1, (tag, _, _, _) =>
            {
                if (tag == 0x0201) info.HasThumbnail = true;           // an embedded JPEG preview
            });

        return info;
    }
}
