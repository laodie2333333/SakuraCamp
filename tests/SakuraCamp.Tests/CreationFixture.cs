using System.Net;
using System.Text;
using System.Text.Json;

sealed class CreationFixture : HttpMessageHandler
{
    public List<Dictionary<string, object>> Tunnels { get; } = [];
    public int Creates { get; private set; }
    public int Edits { get; private set; }
    public int Flags { get; set; } = 4 | 32;
    public bool FailCavesOnce { get; set; }
    public bool LoseMasterResponseOnce { get; set; }
    public bool HideTunnels { get; set; }
    public bool FailBeforeCreateOnce { get; set; }
    public HttpStatusCode? NextCreateStatus { get; set; }
    public int NextCreateCode { get; set; } = 403;
    public List<Dictionary<string, string>> CreatedForms { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Headers.Authorization?.ToString() != "Bearer fixture-key") throw new Exception("Authorization missing");
        object data;
        if (request.Method == HttpMethod.Get)
        {
            if (path == "/v4/nodes") data = new Dictionary<string, object> { ["216"] = new { name = "Fixture node", host = "frp-example.com", vip = 0, flag = Flags } };
            else if (path == "/v4/tunnels") data = HideTunnels ? new List<Dictionary<string, object>>() : Tunnels;
            else throw new Exception("Unexpected GET " + path);
        }
        else
        {
            var encoded = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = encoded.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
            if (path == "/v4/tunnels")
            {
                if (FailBeforeCreateOnce) { FailBeforeCreateOnce = false; throw new HttpRequestException("fixture network failed before create"); }
                if (NextCreateStatus is { } errorStatus)
                { NextCreateStatus = null; return Reply(new { code = NextCreateCode, msg = "fixture denied fixture-key" }, errorStatus); }
                if (FailCavesOnce && form["name"].EndsWith('c'))
                { FailCavesOnce = false; return Reply(new { message = "fixture tunnel quota" }, HttpStatusCode.Forbidden); }
                Creates++; CreatedForms.Add(form);
                var id = Creates + 100;
                var port = 40000 + id;
                Tunnels.Add(new() { ["id"] = id, ["name"] = form["name"], ["note"] = form["note"], ["node"] = int.Parse(form["node"]),
                    ["type"] = form["type"], ["local_ip"] = form["local_ip"], ["local_port"] = int.Parse(form["local_port"]), ["remote"] = port.ToString(), ["online"] = false, ["status"] = 0 });
                if (LoseMasterResponseOnce) { LoseMasterResponseOnce = false; throw new HttpRequestException("fixture response lost"); }
                data = new { id, name = form["name"], remote = port.ToString() };
            }
            else if (path == "/v4/tunnel/edit")
            { Edits++; Tunnels.Single(t => (int)t["id"] == int.Parse(form["id"]))["local_port"] = int.Parse(form["local_port"]); data = new { extra = "" }; }
            else throw new Exception("Unexpected mutation " + path);
        }
        return Reply(data);
    }
    private static HttpResponseMessage Reply(object data, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
}
