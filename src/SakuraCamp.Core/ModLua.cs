using System.Diagnostics;
using System.Globalization;
using System.Text;
using MoonSharp.Interpreter;

namespace SakuraCamp.Core;

// Metadata runs in a Lua-only environment with no filesystem, network, CLR, or module loader.
// Keep the VM private: callers receive plain values, never executable Lua functions.
internal static class ModLua
{
    public static (Script Script, DynValue Result) Read(string source, string folder = "", bool metadata = false, Action<Script>? configure = null)
    {
        if (source.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Mod 配置文件过大，无法读取。");
        var script = new Script(CoreModules.Basic | CoreModules.GlobalConsts | CoreModules.TableIterators |
            CoreModules.String | CoreModules.Table | CoreModules.Math | CoreModules.Bit32);
        script.Options.DebugPrint = _ => { };
        script.Globals["print"] = DynValue.NewCallback((_, _) => DynValue.Nil);
        script.Globals["collectgarbage"] = DynValue.Nil;
        script.Globals["locale"] = "zh";
        script.Globals["folder_name"] = folder;
        script.Globals["ChooseTranslationTable"] = DynValue.NewCallback((_, args) =>
        {
            var t = args[0].Table;
            if (t is null) return DynValue.Nil;
            var value = t.Get("zh"); if (value.IsNil()) value = t.Get("zhr");
            if (value.IsNil()) value = t.Get(1); if (value.IsNil()) value = t.Get("en");
            return value;
        });
        // Bound large C# string allocations as well as Lua instruction loops.
        script.Globals.Get("string").Table.Set("rep", DynValue.NewCallback((_, args) =>
        {
            var text = args[0].String ?? ""; var n = args[1].Number;
            if (!double.IsFinite(n) || n < 0 || n > 65536 || text.Length * n > 65536) throw new ScriptRuntimeException("string.rep limit");
            return DynValue.NewString(string.Concat(Enumerable.Repeat(text, (int)n)));
        }));
        configure?.Invoke(script);
        try
        {
            var function = script.LoadString(source, codeFriendlyName: metadata ? "modinfo" : "modoverrides");
            var coroutine = script.CreateCoroutine(function).Coroutine;
            coroutine.AutoYieldCounter = 10000;
            var watch = Stopwatch.StartNew(); var instructions = 0;
            DynValue result;
            do
            {
                result = coroutine.Resume(); instructions += 10000;
                if (instructions > 2_000_000 || watch.Elapsed > TimeSpan.FromMilliseconds(700))
                    throw new InvalidOperationException("Mod 配置读取超过执行限额，已停止读取。");
            } while (coroutine.State != CoroutineState.Dead);
            return (script, result);
        }
        catch (InterpreterException ex) { throw new InvalidOperationException("无法读取 Mod 的 Lua 配置：" + ex.DecoratedMessage, ex); }
    }

    public static LuaValue Value(DynValue value, int depth = 0, HashSet<Table>? seen = null)
    {
        if (depth > 24) throw new InvalidOperationException("Mod 配置表嵌套过深。");
        switch (value.Type)
        {
            case DataType.Nil: case DataType.Void: return LuaValue.Nil;
            case DataType.Boolean: return new(value.Boolean ? "true" : "false");
            case DataType.String: return new(Quote(value.String));
            case DataType.Number when double.IsFinite(value.Number): return new(value.Number.ToString("R", CultureInfo.InvariantCulture));
            case DataType.Table:
                seen ??= [];
                if (!seen.Add(value.Table)) throw new InvalidOperationException("Mod 配置包含循环表，无法保存。");
                var pairs = value.Table.Pairs.Take(10001).ToArray();
                if (pairs.Length > 10000) throw new InvalidOperationException("Mod 配置表过大。");
                var fields = pairs.Select(p =>
                {
                    if (p.Key.Type is not (DataType.String or DataType.Number or DataType.Boolean)) throw new InvalidOperationException("Mod 配置含无法保存的键。");
                    return (Key: Value(p.Key, depth + 1, seen).Code, Val: Value(p.Value, depth + 1, seen).Code);
                }).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"[{p.Key}]={p.Val}").ToArray();
                seen.Remove(value.Table);
                var code = "{" + string.Join(",", fields) + "}";
                if (code.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Mod 配置表过大。");
                return new(code);
            default: throw new InvalidOperationException("Mod 配置包含函数或不支持的值，已保留原文件。");
        }
    }

    public static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var c in value)
            result.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", < ' ' => "\\" + ((int)c).ToString("D3", CultureInfo.InvariantCulture), _ => c.ToString() });
        return result.Append('"').ToString();
    }

    public static DynValue CopyTo(Script owner, DynValue value, int depth = 0)
    {
        if (depth > 24) throw new InvalidOperationException("Mod 配置表嵌套过深。");
        if (value.Type != DataType.Table) return value;
        var table = new Table(owner);
        foreach (var pair in value.Table.Pairs) table.Set(CopyTo(owner, pair.Key, depth + 1), CopyTo(owner, pair.Value, depth + 1));
        return DynValue.NewTable(table);
    }
}

public sealed record LuaValue(string Code)
{
    public static LuaValue Nil { get; } = new("nil");
    public override string ToString() => Code;
}
