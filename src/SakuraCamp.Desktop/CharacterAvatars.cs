using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace SakuraCamp.Desktop;

// Read portraits from the user's installed game; no game art is shipped with the application.
internal static class CharacterAvatars
{
    public static Dictionary<string, ImageSource> Load(string executable)
    {
        var result = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        var root = Directory.GetParent(Path.GetDirectoryName(executable) ?? "");
        if (root is null) return result;
        var path = Path.Combine(root.FullName, "data", "databundles", "images.zip");
        if (!File.Exists(path)) return result;
        using var zip = ZipFile.OpenRead(path);
        var texture = zip.GetEntry("images/avatars.tex");
        var atlas = zip.GetEntry("images/avatars.xml");
        if (texture is null || atlas is null) return result;
        using var input = texture.Open();
        var bitmap = ReadTexture(input);
        var width = bitmap.PixelWidth; var height = bitmap.PixelHeight;
        using var xml = atlas.Open();
        foreach (var element in XDocument.Load(xml).Descendants("Element"))
        {
            var name = (string?)element.Attribute("name") ?? "";
            if (!name.StartsWith("avatar_", StringComparison.Ordinal) || !name.EndsWith(".tex", StringComparison.Ordinal)) continue;
            double U(string attr) => double.Parse((string?)element.Attribute(attr) ?? "0", CultureInfo.InvariantCulture);
            var x = (int)Math.Round(U("u1") * width);
            var y = (int)Math.Round((1 - U("v2")) * height);
            var w = (int)Math.Round((U("u2") - U("u1")) * width);
            var h = (int)Math.Round((U("v2") - U("v1")) * height);
            if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > width || y + h > height) continue;
            var crop = new CroppedBitmap(bitmap, new Int32Rect(x, y, w, h)); crop.Freeze();
            result[name[7..^4]] = crop;
        }
        return result;
    }

    internal static BitmapSource ReadTexture(Stream input)
    {
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x5845544b) throw new InvalidDataException("Unsupported game texture");
        var header = reader.ReadUInt32();
        if (((header >> 4) & 31) != 2) throw new InvalidDataException("Unsupported portrait compression");
        var levels = (int)((header >> 13) & 31);
        if (levels < 1 || levels > 16) throw new InvalidDataException("Invalid portrait mip count");
        var width = reader.ReadUInt16(); var height = reader.ReadUInt16();
        reader.ReadUInt16(); var size = reader.ReadUInt32();
        if (width == 0 || height == 0 || width > 4096 || height > 4096 || size != ((width + 3) / 4) * ((height + 3) / 4) * 16)
            throw new InvalidDataException("Invalid portrait dimensions");
        reader.ReadBytes((levels - 1) * 10);
        var data = reader.ReadBytes((int)size);
        if (data.Length != size) throw new InvalidDataException("Truncated portrait texture");
        var pixels = DecodeDxt5(data, width, height);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] DecodeDxt5(byte[] data, int width, int height)
    {
        var pixels = new byte[width * height * 4]; var offset = 0;
        var alpha = new byte[8]; var colors = new byte[4, 3];
        for (var by = 0; by < height; by += 4)
        for (var bx = 0; bx < width; bx += 4)
        {
            alpha[0] = data[offset]; alpha[1] = data[offset + 1];
            if (alpha[0] > alpha[1])
                for (var i = 1; i <= 6; i++) alpha[i + 1] = (byte)(((7 - i) * alpha[0] + i * alpha[1]) / 7);
            else
            {
                for (var i = 1; i <= 4; i++) alpha[i + 1] = (byte)(((5 - i) * alpha[0] + i * alpha[1]) / 5);
                alpha[6] = 0; alpha[7] = 255;
            }
            ulong indices = 0;
            for (var i = 0; i < 6; i++) indices |= (ulong)data[offset + 2 + i] << (8 * i);
            for (var i = 0; i < 2; i++)
            {
                var c = data[offset + 8 + i * 2] | data[offset + 9 + i * 2] << 8;
                colors[i, 0] = (byte)((c & 31) * 255 / 31);
                colors[i, 1] = (byte)(((c >> 5) & 63) * 255 / 63);
                colors[i, 2] = (byte)(((c >> 11) & 31) * 255 / 31);
            }
            for (var i = 0; i < 3; i++)
            {
                colors[2, i] = (byte)((2 * colors[0, i] + colors[1, i]) / 3);
                colors[3, i] = (byte)((colors[0, i] + 2 * colors[1, i]) / 3);
            }
            var ci = BitConverter.ToUInt32(data, offset + 12);
            for (var i = 0; i < 16; i++)
            {
                var x = bx + i % 4; var y = by + i / 4;
                if (x >= width || y >= height) continue;
                // KTEX mip rows are stored bottom to top.
                var dest = ((height - 1 - y) * width + x) * 4;
                var color = (int)((ci >> (2 * i)) & 3);
                for (var c = 0; c < 3; c++) pixels[dest + c] = colors[color, c];
                pixels[dest + 3] = alpha[(indices >> (3 * i)) & 7];
            }
            offset += 16;
        }
        return pixels;
    }
}
