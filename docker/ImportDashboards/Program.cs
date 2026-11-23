using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

var ooPass = Environment.GetEnvironmentVariable("OPENOBSERVE_ROOT_PASSWORD") ?? "";
var ooEmail = Environment.GetEnvironmentVariable("OPENOBSERVE_ROOT_EMAIL") ?? "";
Console.WriteLine($"Email: {ooEmail}, Pass: {(ooPass.Length > 4 ? ooPass[..4] + "..." : "(empty)")}");
if (string.IsNullOrEmpty(ooPass)) { Console.WriteLine("ERROR: OPENOBSERVE_ROOT_PASSWORD not set"); return; }
var remoteCred = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ooEmail}:{ooPass}"));

var localEmail = Environment.GetEnvironmentVariable("LOCAL_OPENOBSERVE_EMAIL") ?? "root@localhost.com";
// 本地 OpenObserve 密码不再提供硬编码回退：优先 LOCAL_OPENOBSERVE_PASSWORD，否则用 compose 的 OPENOBSERVE_PASSWORD（同一实例），仍缺失则报错退出
var localPass = Environment.GetEnvironmentVariable("LOCAL_OPENOBSERVE_PASSWORD")
                ?? Environment.GetEnvironmentVariable("OPENOBSERVE_PASSWORD") ?? "";
Console.WriteLine($"Local Email: {localEmail}, Pass: {(localPass.Length > 4 ? localPass[..4] + "..." : "(empty)")}");
if (string.IsNullOrEmpty(localPass))
{
    Console.WriteLine("ERROR: LOCAL_OPENOBSERVE_PASSWORD not set（本地 OpenObserve 密码，即 compose 的 OPENOBSERVE_PASSWORD）");
    return;
}
var localCred = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{localEmail}:{localPass}"));

var ids = new[] { "7459266129253367808", "7459266168595939328", "7459266196630667264", "7459666781049716736" };

using var http = new HttpClient();

foreach (var id in ids)
{
    try
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.shzhengji.com/oo/api/default/dashboards/{id}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", remoteCred);
        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed {id}: HTTP {(int)resp.StatusCode}");
            continue;
        }
        var json = await resp.Content.ReadAsStringAsync();
        if (json.StartsWith('\uFEFF')) json = json[1..];

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("v8", out var v8))
        {
            Console.WriteLine($"Failed {id}: no v8 property");
            continue;
        }

        var title = v8.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var tabCount = 0;
        var panelCount = 0;
        if (v8.TryGetProperty("tabs", out var tabs))
        {
            tabCount = tabs.GetArrayLength();
            foreach (var tab in tabs.EnumerateArray())
                if (tab.TryGetProperty("panels", out var panels))
                    panelCount += panels.GetArrayLength();
        }
        Console.WriteLine($"  {title}: {tabCount} tabs, {panelCount} panels");

        var v8Raw = v8.GetRawText();

        var postReq = new HttpRequestMessage(HttpMethod.Post, "http://localhost:5082/api/default/dashboards");
        postReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", localCred);
        postReq.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(v8Raw));
        postReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        var postResp = await http.SendAsync(postReq);
        var postRespBody = await postResp.Content.ReadAsStringAsync();
        var postDoc = JsonDocument.Parse(postRespBody);

        var newId = "";
        var newHash = "";
        if (postDoc.RootElement.TryGetProperty("v8", out var rv8) && rv8.TryGetProperty("dashboardId", out var didEl))
            newId = didEl.GetString() ?? "";
        if (postDoc.RootElement.TryGetProperty("hash", out var hashEl))
            newHash = hashEl.GetString() ?? "";

        var respTabs = 0;
        var respPanels = 0;
        if (postDoc.RootElement.TryGetProperty("v8", out var rv8b) && rv8b.TryGetProperty("tabs", out var rtabs))
        {
            respTabs = rtabs.GetArrayLength();
            foreach (var rt in rtabs.EnumerateArray())
                if (rt.TryGetProperty("panels", out var rp))
                    respPanels += rp.GetArrayLength();
        }

        Console.WriteLine($"  POST: id={newId}, hash={newHash}, tabs={respTabs}, panels={respPanels}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed {id}: {ex.Message}");
    }
}
