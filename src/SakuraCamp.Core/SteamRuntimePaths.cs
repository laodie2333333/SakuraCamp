using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SakuraCamp.Core;

public static class SteamRuntimePaths
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint length);

    public static bool IsAscii(string path) => path.All(c => c is >= ' ' and <= '~');

    public static string Resolve(string appDataRoot)
    {
        var preferred = Path.Combine(appDataRoot, "tools");
        Directory.CreateDirectory(preferred);
        if (IsAscii(preferred)) return preferred;
        if (OperatingSystem.IsWindows())
        {
            var buffer = new StringBuilder(32768);
            var count = GetShortPathName(preferred, buffer, (uint)buffer.Capacity);
            if (count > 0 && count < buffer.Capacity && IsAscii(buffer.ToString())) return buffer.ToString();
        }
        // SteamCMD's bootstrap cannot start in a non-English directory. Use a per-user
        // namespace in ProgramData if NTFS short names are unavailable, without elevation.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))))[..16];
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SakuraCamp", "Users", identity);
        if (!IsAscii(fallback)) throw new InvalidOperationException("SteamCMD 需要英文路径，但当前系统未提供可用的英文数据目录。");
        try
        {
            Directory.CreateDirectory(fallback);
            var probe = Path.Combine(fallback, "write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
            return fallback;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("SteamCMD 不支持中文路径，且英文数据目录不可写。请先通过 Steam 安装或更新独立服务端，然后在文件目录页选择它；本次未开服。", error);
        }
    }
}
