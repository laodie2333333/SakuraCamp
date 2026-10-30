using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SakuraCamp.Desktop;

internal static class SecretStore
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static byte[] Transform(byte[] data, bool encrypt)
    {
        var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, input.Data, data.Length);
        Blob output = default;
        try
        {
            var ok = encrypt ? CryptProtectData(ref input, "SakuraCamp", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException("Windows 无法加密或读取访问密钥，请重新输入。");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); Array.Clear(data); }
    }
    public static void Save(string path, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SakuraCamp.Core.AtomicFile.Write(path, Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(token), true)));
    }
    public static string Load(string path) => File.Exists(path) ? Encoding.UTF8.GetString(Transform(Convert.FromBase64String(File.ReadAllText(path)), false)) : "";
}
