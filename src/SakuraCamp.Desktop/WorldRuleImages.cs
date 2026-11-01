using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

// Use the atlas and image specified by each game rule. Do not ship game artwork.
internal static class WorldRuleImages
{
    public static Dictionary<string, ImageSource> Load(string executable, IReadOnlyList<WorldRuleDefinition> rules, Action<string>? warning = null)
    {
        try { return LoadImages(executable, rules, warning); }
        catch (Exception ex) when (ImageFailures.IsRecoverable(ex))
        {
            warning?.Invoke("世界规则图片资源读取失败，使用文字占位，规则仍可编辑：" + ex.Message);
            return [];
        }
    }

    private static Dictionary<string, ImageSource> LoadImages(string executable, IReadOnlyList<WorldRuleDefinition> rules, Action<string>? warning)
    {
        var result = new Dictionary<string, ImageSource>();
        var folder = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var root = new[] { Path.GetFullPath(Path.Combine(folder, "..", "data")), Path.Combine(folder, "data") }.FirstOrDefault(Directory.Exists);
        if (root is null) return result;
        var bundle = Path.Combine(root, "databundles", "images.zip");
        using var zip = File.Exists(bundle) ? ZipFile.OpenRead(bundle) : null;
        Stream? Open(string name, long limit)
        {
            if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name)) return null;
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(path)) return new FileInfo(path).Length <= limit ? File.OpenRead(path) : null;
            var entry = zip?.GetEntry(name.Replace('\\', '/'));
            return entry is { Length: > 0 } && entry.Length <= limit ? entry.Open() : null;
        }
        foreach (var group in rules.Where(r => r.IconAtlas.Length > 0).GroupBy(r => r.IconAtlas))
        {
            try
            {
                using var atlas = Open(group.Key, 2 * 1024 * 1024);
                if (atlas is null) continue;
                using var xml = XmlReader.Create(atlas, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var document = XDocument.Load(xml);
                var texture = (string?)document.Descendants("Texture").FirstOrDefault()?.Attribute("filename");
                if (texture is null) continue;
                using var stream = Open(Path.Combine(Path.GetDirectoryName(group.Key) ?? "", texture), 32 * 1024 * 1024);
                if (stream is null) continue;
                var image = CharacterAvatars.ReadTexture(stream);
                var elements = document.Descendants("Element").GroupBy(e => (string?)e.Attribute("name") ?? "").ToDictionary(g => g.Key, g => g.First());
                foreach (var rule in group)
                {
                    if (!elements.TryGetValue(rule.IconName, out var element)) continue;
                    double U(string name) => double.Parse((string?)element.Attribute(name) ?? "0", CultureInfo.InvariantCulture);
                    var rect = new Int32Rect((int)Math.Round(U("u1") * image.PixelWidth), (int)Math.Round((1 - U("v2")) * image.PixelHeight),
                        (int)Math.Round((U("u2") - U("u1")) * image.PixelWidth), (int)Math.Round((U("v2") - U("v1")) * image.PixelHeight));
                    if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 || rect.X + rect.Width > image.PixelWidth || rect.Y + rect.Height > image.PixelHeight) continue;
                    var crop = new CroppedBitmap(image, rect); crop.Freeze(); result[rule.Key] = crop;
                }
            }
            catch (Exception ex) when (ImageFailures.IsRecoverable(ex))
            { warning?.Invoke($"世界规则图标读取失败（{group.Key}），使用文字占位：{ex.Message}"); }
        }
        return result;
    }
}
