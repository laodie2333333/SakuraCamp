namespace SakuraCamp.Core;

// A bounded handoff from server output readers to the UI. Readiness is parsed separately.
public sealed class PendingLogs(int capacity = 4096)
{
    private readonly object gate = new();
    private readonly Queue<string> lines = new();
    private int skipped;
    public void Add(string line)
    {
        lock (gate)
        {
            if (lines.Count >= Math.Max(1, capacity)) { lines.Dequeue(); skipped++; }
            lines.Enqueue(line.Length > 8192 ? line[..8192] + "…（显示已截断）" : line);
        }
    }
    public string[] Take(int maximum, out int omitted)
    {
        lock (gate)
        {
            omitted = skipped; skipped = 0;
            var batch = new string[Math.Min(Math.Max(0, maximum), lines.Count)];
            for (var index = 0; index < batch.Length; index++) batch[index] = lines.Dequeue();
            return batch;
        }
    }
    public void Clear() { lock (gate) { lines.Clear(); skipped = 0; } }
}
