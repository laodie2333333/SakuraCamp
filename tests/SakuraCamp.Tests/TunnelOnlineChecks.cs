using System.Net;
using SakuraCamp.Core;

static class TunnelOnlineChecks
{
    private sealed class Api(Func<int, string> respond) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/v4/tunnels") throw new Exception("Online checks must not query nodes");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(++Requests)) });
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        using var handler = new Api(n => n == 1 ? "[{\"id\":1,\"online\":true},{\"id\":2,\"online\":false}]" : "[{\"id\":1,\"online\":true},{\"id\":2,\"online\":true}]");
        using var http = new HttpClient(handler);
        var confirmed = false;
        await new SakuraClient(http).WaitForOnlineAsync("fixture-key", new[] { 1, 2 }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), _ => confirmed = true);
        check(handler.Requests == 2 && confirmed, "official online verification waits until both selected tunnels are online");
        async Task Reject(string json, string title)
        {
            using var api = new Api(_ => json); using var client = new HttpClient(api);
            try { await new SakuraClient(client).WaitForOnlineAsync("fixture-key", new[] { 1, 2 }, TimeSpan.Zero); }
            catch (InvalidOperationException) { check(true, title); return; }
            throw new Exception("FAIL: " + title);
        }
        await Reject("[{\"id\":1,\"online\":true},{\"id\":2,\"online\":false}]", "one offline tunnel cannot pass official online verification");
        await Reject("[{\"id\":1,\"online\":true}]", "missing cave tunnel stops online verification");
        await Reject("[{\"id\":1,\"online\":true},{\"id\":2,\"online\":true,\"status\":1}]", "disabled but online-looking tunnel cannot pass verification");
        await Reject("{}", "malformed official state does not report tunnels as online");
    }
}
