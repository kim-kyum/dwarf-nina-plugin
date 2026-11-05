using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Dwarf.Core;

/// <summary>Minimal FITS primary-HDU reader (2-D image) returning 16-bit unsigned pixels.</summary>
public sealed class FitsImage {
    public int Width { get; }
    public int Height { get; }
    public ushort[] Pixels { get; }
    public IReadOnlyDictionary<string, string> Header { get; }
    public ushort MaxValue { get; }

    private FitsImage(int w, int h, ushort[] px, Dictionary<string, string> header) {
        Width = w; Height = h; Pixels = px; Header = header;
        ushort max = 0;
        foreach (var v in px) if (v > max) max = v;
        MaxValue = max;
    }

    public static FitsImage Parse(byte[] content) {
        const int block = 2880;
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (true) {
            if (offset + 80 > content.Length) throw new InvalidDataException("FITS header incomplete");
            var card = Encoding.ASCII.GetString(content, offset, 80);
            offset += 80;
            var key = card[..8].Trim();
            if (key == "END") break;
            if (key.Length == 0 || card.Length < 10 || card[8] != '=') continue;
            var value = card[10..];
            if (value.TrimStart().StartsWith('\'')) {
                var s = value.IndexOf('\''); var e = value.IndexOf('\'', s + 1);
                header[key] = e > s ? value[(s + 1)..e].Trim() : value.Trim();
            } else {
                var slash = value.IndexOf('/');
                header[key] = (slash >= 0 ? value[..slash] : value).Trim();
            }
        }
        var dataStart = (offset + block - 1) / block * block;
        int I(string k, int d) => header.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : d;
        double D(string k, double d) => header.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : d;

        var bitpix = I("BITPIX", 16);
        if (I("NAXIS", 0) < 2) throw new InvalidDataException("FITS is not a 2-D image");
        var width = I("NAXIS1", 0);
        var height = I("NAXIS2", 0);
        if (width <= 0 || height <= 0) throw new InvalidDataException("FITS has no image dimensions");
        var bscale = D("BSCALE", 1.0);
        var bzero = D("BZERO", 0.0);
        var bytesPer = Math.Abs(bitpix) / 8;
        var count = width * height;
        if (content.Length < dataStart + (long)count * bytesPer) throw new InvalidDataException("FITS data is truncated");

        var px = new ushort[count];
        var span = content.AsSpan(dataStart);
        for (var i = 0; i < count; i++) {
            var s = span.Slice(i * bytesPer, bytesPer);
            double raw = bitpix switch {
                8 => s[0],
                16 => BinaryPrimitives.ReadInt16BigEndian(s),
                32 => BinaryPrimitives.ReadInt32BigEndian(s),
                64 => BinaryPrimitives.ReadInt64BigEndian(s),
                -32 => BinaryPrimitives.ReadSingleBigEndian(s),
                -64 => BinaryPrimitives.ReadDoubleBigEndian(s),
                _ => throw new InvalidDataException($"Unsupported BITPIX {bitpix}"),
            };
            var v = raw * bscale + bzero;
            px[i] = v <= 0 ? (ushort)0 : v >= 65535 ? ushort.MaxValue : (ushort)Math.Round(v);
        }
        return new FitsImage(width, height, px, header);
    }
}
