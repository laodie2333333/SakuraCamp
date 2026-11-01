using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

internal static class ModImages
{
    public static ImageSource? Load(InstalledMod mod, Action<string>? warning = null)
    {
        if (!mod.Downloaded) return null;
        try
        {
            // Preview images are local Mod assets. Never fetch or redistribute workshop art.
            var atlasPath = SafePath(mod.Folder, mod.IconAtlas);
            var texturePath = SafePath(mod.Folder, mod.Icon);
            XElement? element = null;
            if (atlasPath is not null && File.Exists(atlasPath))
            {
                if (new FileInfo(atlasPath).Length > 1024 * 1024) return null;
                var xml = XDocument.Load(atlasPath);
                var texture = (string?)xml.Descendants("Texture").FirstOrDefault()?.Attribute("filename");
                if (texture is not null) texturePath = SafePath(Path.GetDirectoryName(atlasPath)!, texture);
                element = xml.Descendants("Element").FirstOrDefault(e => (string?)e.Attribute("name") == mod.Icon) ?? xml.Descendants("Element").FirstOrDefault();
            }
            if (texturePath is not null && File.Exists(texturePath))
            {
                using var stream = File.OpenRead(texturePath);
                var image = CharacterAvatars.ReadTexture(stream);
                if (element is null) return image;
                double U(string name) => double.Parse((string?)element.Attribute(name) ?? "0", CultureInfo.InvariantCulture);
                var rect = new Int32Rect((int)Math.Round(U("u1") * image.PixelWidth), (int)Math.Round((1 - U("v2")) * image.PixelHeight),
                    (int)Math.Round((U("u2") - U("u1")) * image.PixelWidth), (int)Math.Round((U("v2") - U("v1")) * image.PixelHeight));
                var crop = new CroppedBitmap(image, rect); crop.Freeze(); return crop;
            }
        }
        catch (Exception ex) when (ImageFailures.IsRecoverable(ex))
        { warning?.Invoke($"Mod 图片读取失败（{mod.Key}），尝试预览图或文字占位：{ex.Message}"); }
        foreach (var name in new[] { "preview.png", "preview.jpg", "modicon.png", "modicon.jpg", "icon.png" })
        {
            try
            {
                var path = SafePath(mod.Folder, name);
                if (path is null || !File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) continue;
                using var stream = File.OpenRead(path);
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 160; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
            }
            catch (Exception ex) when (ImageFailures.IsRecoverable(ex))
            { warning?.Invoke($"Mod 预览图读取失败（{mod.Key}/{name}），继续使用其他图片或文字占位：{ex.Message}"); }
        }
        return null;
    }

    private static string? SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        return path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : null;
    }
}
