using System.Text;
using System.Threading.Channels;

namespace SakuraCamp.Core;

// Independent of UI ring buffers: no dropped entries, no line truncation, no rotation mid-run.
public sealed class DiagnosticJournal : IAsyncDisposable
{
    private sealed record Entry(string? Line, TaskCompletionSource<long>? Barrier = null);
    private readonly Channel<Entry> entries = Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task writerTask;
    private Exception? failure;
    public string Path { get; }
    public DiagnosticJournal(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        writerTask = Task.Run(WriteAsync);
    }
    public void Append(string redactedLine)
    {
        ThrowIfFailed();
        if (!entries.Writer.TryWrite(new(redactedLine))) throw new IOException("完整诊断日志已关闭。");
    }
    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref failure) is { } error) throw new IOException("完整诊断日志写入失败，请检查磁盘空间和目录权限。", error);
    }
    private async Task WriteAsync()
    {
        TaskCompletionSource<long>? activeBarrier = null;
        try
        {
            await using var file = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
            await using var writer = new StreamWriter(file, new UTF8Encoding(false)) { AutoFlush = true };
            await foreach (var entry in entries.Reader.ReadAllAsync())
            {
                if (entry.Line is { } line) await writer.WriteLineAsync(line);
                if (entry.Barrier is { } barrier)
                {
                    activeBarrier = barrier;
                    await writer.FlushAsync();
                    barrier.TrySetResult(file.Position);
                    activeBarrier = null;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref failure, error); entries.Writer.TryComplete(error);
            activeBarrier?.TrySetException(error);
            while (entries.Reader.TryRead(out var entry)) entry.Barrier?.TrySetException(error);
        }
    }
    public async Task ExportAsync(string destination, params string[] secrets)
    {
        if (System.IO.Path.GetFullPath(destination).Equals(System.IO.Path.GetFullPath(Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择其他导出位置，不能覆盖正在写入的完整日志。");
        ThrowIfFailed();
        var barrier = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!entries.Writer.TryWrite(new(null, barrier))) { ThrowIfFailed(); throw new IOException("完整诊断日志已关闭。"); }
        var length = await barrier.Task;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Bound the snapshot at a flushed line boundary while live logging continues.
            await using (var input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[65536];
                while (length > 0)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)));
                    if (count == 0) throw new IOException("完整日志读取意外中断，未导出不完整文件。");
                    await output.WriteAsync(buffer.AsMemory(0, count)); length -= count;
                }
            }
            // Reapply current credentials at export, including values entered after earlier entries.
            using (var reader = new StreamReader(temporary, Encoding.UTF8))
            await using (var output = new StreamWriter(destination, false, new UTF8Encoding(false)))
                while (await reader.ReadLineAsync() is { } line) await output.WriteLineAsync(LogReader.Redact(line, secrets));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        ThrowIfFailed();
    }
    public async ValueTask DisposeAsync() { entries.Writer.TryComplete(); await writerTask; ThrowIfFailed(); }
}
