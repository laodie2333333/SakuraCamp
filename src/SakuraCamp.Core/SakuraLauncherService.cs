using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SakuraCamp.Core;

public sealed record LauncherTunnel(int Id, int Node, string Name, string Type, int Status, bool Enabled, int State, string LocalIp, int LocalPort, string Remote);

// The installed official Windows launcher uses HTTP/2 gRPC over this named pipe.
// Implement only its public tunnel stream and UpdateTunnel RPC; never read its saved credentials.
public interface ILauncherTunnelService
{
    Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    Task<IReadOnlyList<LauncherTunnel>> ReadTunnelsAsync(IReadOnlyList<int> selectedIds, CancellationToken ct = default);
    Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default);
}

public sealed class SakuraLauncherService : ILauncherTunnelService, IDisposable
{
    public const string PipeName = "SakuraFrpService3";
    private readonly HttpClient http;
    public SakuraLauncherService()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(ct); return pipe; }
                catch { pipe.Dispose(); throw; }
            }
        };
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static bool IsAvailable => OperatingSystem.IsWindows() && Directory.GetFiles(@"\\.\pipe\").Any(p => p.EndsWith("\\" + PipeName, StringComparison.OrdinalIgnoreCase));

    private static byte[] Frame(byte[] message)
    {
        var frame = new byte[message.Length + 5];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), message.Length);
        message.CopyTo(frame, 5); return frame;
    }
    private static HttpRequestMessage Request(string method, byte[] message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/NatfrpService/" + method)
        {
            Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(Frame(message))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation("TE", "trailers");
        return request;
    }
    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[5]; await stream.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (header[0] != 0 || size < 0 || size > 1024 * 1024) throw new InvalidOperationException("樱花本地服务返回了不支持的消息。");
        var data = new byte[size]; await stream.ReadExactlyAsync(data, ct); return data;
    }

    public async Task<IReadOnlyList<LauncherTunnel>> ReadTunnelsAsync(IReadOnlyList<int> selectedIds, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = Request("StreamTunnels", []);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var rows = new Dictionary<int, LauncherTunnel>();
        while (true)
        {
            var message = ProtoFields.Read(await ReadFrameAsync(stream, deadline.Token));
            var tunnelBytes = message.Bytes(2);
            if (tunnelBytes.Length == 0) continue;
            var tunnel = ParseTunnel(tunnelBytes);
            rows[tunnel.Id] = tunnel;
            if (selectedIds.All(rows.ContainsKey)) return selectedIds.Select(id => rows[id]).ToArray();
        }
    }

    public async Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        var tunnel = new List<byte>(); ProtoFields.AddNumber(tunnel, 1, id); ProtoFields.AddNumber(tunnel, 8, enabled ? 1 : 0);
        var update = new List<byte>(); ProtoFields.AddNumber(update, 1, 2); ProtoFields.AddBytes(update, 2, tunnel.ToArray());
        await UnaryAsync("UpdateTunnel", update.ToArray(), ct);
        // This service acknowledges with an empty TunnelUpdate. Confirm the switch through its live model.
        var current = await ReadTunnelsAsync(new[] { id }, ct);
        if (current.Single().Enabled != enabled) throw new InvalidOperationException("樱花本地服务未确认通道开关变更。");
    }

    public Task RefreshAsync(CancellationToken ct = default) => UnaryAsync("ReloadTunnels", [], ct);

    private async Task UnaryAsync(string method, byte[] payload, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = Request(method, payload);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        // Drain this unary reply so HTTP/2 trailers (including gRPC failures) are available.
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        await ReadFrameAsync(stream, deadline.Token);
        if (await stream.ReadAsync(new byte[1], deadline.Token) != 0) throw new InvalidOperationException("樱花服务返回了异常的控制响应。");
        var statuses = response.TrailingHeaders.TryGetValues("grpc-status", out var trailers) ? trailers
            : response.Headers.TryGetValues("grpc-status", out var headers) ? headers : [];
        if (statuses.Any(value => value != "0")) throw new InvalidOperationException("樱花本地服务拒绝了操作，请检查官方客户端登录状态和日志。");
    }

    public static LauncherTunnel ParseTunnel(byte[] data)
    {
        var p = ProtoFields.Read(data);
        return new((int)p.Number(1), (int)p.Number(2), p.Text(3), p.Text(4), (int)p.Number(7), p.Number(8) != 0,
            (int)p.Number(9), p.Text(10), (int)p.Number(11), p.Text(12));
    }

    public void Dispose() => http.Dispose();

    private sealed class ProtoFields
    {
        private readonly Dictionary<int, ulong> numbers = [];
        private readonly Dictionary<int, byte[]> bytes = [];
        public ulong Number(int field) => numbers.GetValueOrDefault(field);
        public byte[] Bytes(int field) => bytes.GetValueOrDefault(field) ?? [];
        public string Text(int field) => Encoding.UTF8.GetString(Bytes(field));
        private static ulong Varint(byte[] data, ref int offset)
        {
            ulong value = 0;
            for (var shift = 0; shift < 64 && offset < data.Length; shift += 7)
            {
                var b = data[offset++]; value |= (ulong)(b & 127) << shift;
                if ((b & 128) == 0) return value;
            }
            throw new InvalidOperationException("樱花服务消息编码异常。");
        }
        public static ProtoFields Read(byte[] data)
        {
            var result = new ProtoFields(); var offset = 0;
            while (offset < data.Length)
            {
                var tag = Varint(data, ref offset); var field = (int)(tag >> 3);
                switch (tag & 7)
                {
                    case 0: result.numbers[field] = Varint(data, ref offset); break;
                    case 2:
                        var size = checked((int)Varint(data, ref offset));
                        if (size < 0 || size > data.Length - offset) throw new InvalidOperationException("樱花服务消息长度异常。");
                        result.bytes[field] = data.AsSpan(offset, size).ToArray(); offset += size; break;
                    case 1: offset += 8; break;
                    case 5: offset += 4; break;
                    default: throw new InvalidOperationException("樱花服务消息格式不受支持。");
                }
                if (offset > data.Length) throw new InvalidOperationException("樱花服务消息被截断。");
            }
            return result;
        }
        private static void WriteVarint(List<byte> data, ulong value)
        {
            while (value >= 128) { data.Add((byte)((value & 127) | 128)); value >>= 7; }
            data.Add((byte)value);
        }
        public static void AddNumber(List<byte> data, int field, int value) { WriteVarint(data, (ulong)(field << 3)); WriteVarint(data, (ulong)value); }
        public static void AddBytes(List<byte> data, int field, byte[] value) { WriteVarint(data, (ulong)((field << 3) | 2)); WriteVarint(data, (ulong)value.Length); data.AddRange(value); }
    }
}
