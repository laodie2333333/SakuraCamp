namespace SakuraCamp.Core;

public static class WorldDeletion
{
    public static string Validate(WorldInfo world, string saveRoot)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(world.Path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(saveRoot));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (Directory.GetParent(target) is null ||
            (!target.Equals(root, StringComparison.OrdinalIgnoreCase) && !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("删除目标不在当前存档目录中，请刷新世界列表后重试。");
        if (!File.Exists(Path.Combine(target, "cluster.ini")) || !Directory.Exists(Path.Combine(target, "Master")))
            throw new InvalidOperationException("目标不再是有效的世界目录，请刷新世界列表后重试。");
        var directories = new Stack<string>(); directories.Push(target);
        while (directories.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("世界目录包含链接或目录联接，无法直接删除。请先核对真实存档位置。");
            if (directory != target && File.Exists(Path.Combine(directory, "cluster.ini")) && Directory.Exists(Path.Combine(directory, "Master")))
                throw new InvalidOperationException("目标目录中还包含其他世界，已阻止删除。请重新选择单个世界。");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("世界目录包含链接或目录联接，无法直接删除。请先核对真实存档位置。");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
            }
        }
        return target;
    }

    // Caller must obtain explicit confirmation for this particular world before invoking.
    public static void Delete(WorldInfo world, string saveRoot)
    {
        WorldFiles.RequireStopped();
        var target = Validate(world, saveRoot);
        WorldFiles.RequireStopped();
        Directory.Delete(target, recursive: true);
    }
}
