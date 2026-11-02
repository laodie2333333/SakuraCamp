using SakuraCamp.Core;

internal static class WorldDeletionChecks
{
    public static void Run(string workspace, Action<bool, string> check)
    {
        var root = Path.Combine(workspace, "delete-worlds");
        WorldInfo Create(string path)
        {
            Directory.CreateDirectory(Path.Combine(path, "Master", "save"));
            Directory.CreateDirectory(Path.Combine(path, "Caves", "save"));
            File.WriteAllText(Path.Combine(path, "cluster.ini"), "[NETWORK]\ncluster_name=删除测试\n");
            File.WriteAllText(Path.Combine(path, "Master", "save", "world.dat"), "surface");
            File.WriteAllText(Path.Combine(path, "Caves", "save", "world.dat"), "caves");
            return Discovery.ReadWorld(path);
        }
        void Reject(Action action, string label)
        {
            try { action(); } catch (InvalidOperationException) { check(true, label); return; }
            check(false, label);
        }
        var first = Create(Path.Combine(root, "account", "Cluster_1"));
        var second = Create(Path.Combine(root, "account", "Cluster_2"));
        var external = Create(Path.Combine(workspace, "delete-worlds-other", "Cluster_3"));
        check(WorldDeletion.Validate(first, root) == first.Path && Directory.Exists(first.Path), "deletion preflight validates one world without changing files");
        Reject(() => WorldDeletion.Delete(external, root), "deletion rejects a world outside configured root including sibling path prefix");
        Reject(() => WorldDeletion.Delete(first with { Path = root }, root), "deletion rejects account or save root without world structure");
        var nested = Create(Path.Combine(first.Path, "nested-world"));
        Reject(() => WorldDeletion.Delete(first, root), "deletion refuses a directory containing an additional world");
        check(Directory.Exists(first.Path) && Directory.Exists(nested.Path), "rejected nested-world deletion preserves both worlds");
        WorldDeletion.Delete(nested, root);
        WorldDeletion.Delete(first, root);
        check(!Directory.Exists(first.Path) && Directory.Exists(second.Path) && Directory.Exists(external.Path), "confirmed deletion removes only the target world with both shards");
        check(File.ReadAllText(Path.Combine(second.Path, "Master", "save", "world.dat")) == "surface", "deleting another world preserves sibling save bytes");
        var direct = Create(Path.Combine(workspace, "direct-delete-world"));
        WorldDeletion.Delete(direct, direct.Path);
        check(!Directory.Exists(direct.Path), "a single world selected directly as the save root can be deleted");
        // A directory junction is a user-data boundary, even if it points inside the test tree.
        var link = Path.Combine(second.Path, "external-link");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = "/c mklink /J \"" + link + "\" \"" + external.Path + "\"",
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Cannot create isolated junction fixture: " + process.StandardError.ReadToEnd());
        Reject(() => WorldDeletion.Delete(second, root), "deletion refuses links instead of following them into another world");
        check(Directory.Exists(external.Path) && Directory.Exists(second.Path), "linked external world and target remain after rejection");
    }
}
