namespace SakuraCamp.Core;

public static class ServerLocations
{
    public static void Remember(AppSettings settings, string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
        var full = Path.GetFullPath(executable);
        settings.ServerHistory.RemoveAll(p => p.Equals(full, StringComparison.OrdinalIgnoreCase));
        settings.ServerHistory.Insert(0, full);
        if (settings.ServerHistory.Count > 32) settings.ServerHistory.RemoveRange(32, settings.ServerHistory.Count - 32);
    }

    public static string Recover(string selected, IEnumerable<string> history, IEnumerable<string> discovered)
    {
        if (File.Exists(selected)) return selected;
        return history.Concat(discovered).Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists) ?? "";
    }

    public static void ValidateNewInstallDirectory(string directory, string worldPath)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var world = Path.GetFullPath(worldPath).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Equals(world, StringComparison.OrdinalIgnoreCase) || full.StartsWith(world + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || world.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("服务端安装目录不能是存档目录、其子目录或包含存档的父目录。请选择单独的空文件夹。");
        if (!SteamRuntimePaths.IsAscii(full)) throw new InvalidOperationException("SteamCMD 安装服务端需要英文路径，请选择磁盘上的英文空文件夹。");
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            throw new InvalidOperationException("首次安装请选择空文件夹；已有服务端请直接选择其中 bin64 下的服务器程序，再执行更新。");
    }
}
