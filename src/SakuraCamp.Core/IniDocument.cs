using System.Text;
using System.Text.RegularExpressions;

namespace SakuraCamp.Core;

// Edits only the requested INI section. Preserves other sections, comments and CRLF.
public sealed class IniDocument
{
    private readonly List<string> lines;
    private readonly string newline;
    public IniDocument(string text)
    {
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        lines = text.Replace("\r\n", "\n").Split('\n').ToList();
    }
    public static IniDocument Load(string path) => new(File.ReadAllText(path));
    private (int Start, int End) Section(string section)
    {
        var starts = lines.Select((line, index) => (line, index)).Where(p => Regex.IsMatch(p.line, @"^\s*\[" + Regex.Escape(section) + @"\]\s*(?:[;#].*)?$", RegexOptions.IgnoreCase)).Select(p => p.index).ToArray();
        if (starts.Length > 1) throw new InvalidOperationException($"配置中存在重复的 [{section}]，请先修正。");
        if (starts.Length == 0) return (-1, -1);
        var end = lines.FindIndex(starts[0] + 1, line => Regex.IsMatch(line, @"^\s*\[.+\]"));
        return (starts[0], end < 0 ? lines.Count : end);
    }
    public string Get(string section, string key, string fallback = "")
    {
        var (start, end) = Section(section);
        if (start < 0) return fallback;
        var pattern = @"^[ \t]*" + Regex.Escape(key) + @"[ \t]*=[ \t]*(.*)$";
        for (var i = start + 1; i < end; i++)
        {
            var match = Regex.Match(lines[i], pattern, RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return fallback;
    }
    public void Set(string section, string key, string value)
    {
        if (value.Contains('\n') || value.Contains('\r')) throw new InvalidOperationException("配置值不能包含换行。");
        var (start, end) = Section(section);
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1] != "") lines.Add("");
            lines.Add($"[{section}]"); lines.Add($"{key} = {value}"); lines.Add(""); return;
        }
        var pattern = @"^[ \t]*" + Regex.Escape(key) + @"[ \t]*=";
        var matches = Enumerable.Range(start + 1, end - start - 1).Where(i => Regex.IsMatch(lines[i], pattern, RegexOptions.IgnoreCase)).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException($"配置中 [{section}] 有重复的 {key}，请先修正。");
        if (matches.Length == 1) lines[matches[0]] = $"{key} = {value}";
        else lines.Insert(start + 1, $"{key} = {value}");
    }
    public override string ToString() => string.Join(newline, lines);
    public void Save(string path) => AtomicFile.Write(path, ToString());
}

public static class AtomicFile
{
    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
