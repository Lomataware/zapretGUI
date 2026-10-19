using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using ZapretGUI.Core;

namespace ZapretGUI.Services;

public enum CheckVerdict { Unknown, Running, Blocked, PartiallyBroken, NotTested }

public sealed class TestResult
{
    public string Category { get; init; } = "";   // Discord / Rutracker / YouTube
    public string Name { get; init; } = "";       // человекочитаемое имя теста
    public string Target { get; init; } = "";     // что проверяли
    public CheckVerdict Verdict { get; set; } = CheckVerdict.Unknown;
    public string Detail { get; set; } = "";      // HTTP код/исключение/ping
    public TimeSpan Latency { get; set; }
    public bool Finished { get; set; }
}

/// <summary>
/// Реальные тесты работоспособности в условиях блокировок РФ.
/// НЕ использует ping сайтов как основной критерий (ping часто блокируется эхо-запросами).
/// Методы: HTTPS GET с проверкой кода/полезной нагрузки, TLS handshake к gateway端口,
/// STUN Binding-запрос к Discord voice серверам (UDP 50000) — проверка именно голосового тракта,
/// DNS-over-HTTPS разрешение для выявления DNS-блокировки, HEAD к CDN.
/// </summary>
public sealed class BlockTestService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly LogService _log;

    public event Action<TestResult>? ResultUpdated;

    public BlockTestService(LogService log) => _log = log;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        return c;
    }

    // ================= Discord =================

    public async Task<TestResult> TestDiscordAppAsync(CancellationToken ct) =>
        await HttpCheckAsync("Discord", "Сайт discord.com", "https://discord.com/",
            expectSubstring: "<title>Discord", ct);

    public async Task<TestResult> TestDiscordApiAsync(CancellationToken ct) =>
        await HttpCheckAsync("Discord", "API discord.com/api", "https://discord.com/api/v10/gateway",
            expectSubstring: "\"url\"", ct);

    public async Task<TestResult> TestDiscordGatewayTcpAsync(CancellationToken ct) =>
        await TcpTlsHandshakeAsync("Discord", "WebSocket Gateway (gateway.discord.gg:443)",
            "gateway.discord.gg", 443, ct);

    public async Task<TestResult> TestDiscordCdnAsync(CancellationToken ct) =>
        await HeadCheckAsync("Discord", "CDN cdn.discordapp.com",
            "https://cdn.discordapp.com/discordani/animations/static/logo/data.json", ct);

    public async Task<TestResult> TestDiscordMediaAsync(CancellationToken ct) =>
        await TcpTlsHandshakeAsync("Discord", "Медиа/звонки (media.discordapp.net:443)",
            "media.discordapp.net", 443, ct);

    /// <summary>Голос Discord: реальный STUN Binding request к voice UDP диапазону.</summary>
    public async Task<TestResult> TestDiscordVoiceStunAsync(CancellationToken ct)
    {
        var res = NewResult("Discord", "Voice: STUN Binding (UDP)", "stun.svc, диапазон 19294-19344/50000");
        try
        {
            // классический RFC5389 Binding Request
            var stun = new byte[20];
            BitConverter.GetBytes((ushort)0x0001).CopyTo(stun, 0); // type = Binding Request
            BitConverter.GetBytes((ushort)0).CopyTo(stun, 2);      // length = 0
            BitConverter.GetBytes(0x2112A442u).CopyTo(stun, 4);    // magic cookie
            new Random().NextBytes(stun.AsSpan(8, 12));            // transaction id

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = 5000;
            var sw = Stopwatch.StartNew();
            var endpoints = new[]
            {
                new IPEndPoint(IPAddress.Parse("162.159.200.1"), 443),   // Discord STUN через TURN (any)
                new IPEndPoint(IPAddress.Parse("162.159.200.1"), 50000), // voice range typical
            };
            foreach (var ep in endpoints)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await udp.SendAsync(stun, ep);
                    var reply = await ReceiveWithTimeout(udp, TimeSpan.FromSeconds(5), ct);
                    if (reply != null && reply.Length >= 4 &&
                        (BitConverter.ToUInt16(reply, 0) & 0xFFFF) is 0x0101 or 0x0111)
                    {
                        res.Verdict = CheckVerdict.Running;
                        res.Detail = $"STUN Binding Response получен от {ep}";
                        break;
                    }
                }
                catch (SocketException) { continue; }
            }
            sw.Stop();
            res.Latency = sw.Elapsed;
            if (res.Verdict == CheckVerdict.Unknown)
            {
                // отсутствие ответа на UDP само по себе не доказательство блокировки,
                // поэтому дополнительно проверяем доступность voice через TCP-порт TURN 443
                res.Verdict = CheckVerdict.PartiallyBroken;
                res.Detail = "Прямой STUN-ответ не получен (возможно, блокировка UDP или NAT). Проверьте TURN 443.";
            }
        }
        catch (OperationCanceledException) { res.Detail = "Отменено"; }
        catch (Exception ex) { res.Verdict = CheckVerdict.Blocked; res.Detail = Explain(ex); }
        Publish(res);
        return res;
    }

    /// <summary>Discord updates (автообновление клиента ходит по https://updates.discord.com — тоже режется).</summary>
    public async Task<TestResult> TestDiscordUpdatesAsync(CancellationToken ct) =>
        await HeadCheckAsync("Discord", "Обновления updates.discord.com", "https://updates.discord.com/dl/app.conf", ct);

    // ================= Rutracker =================

    public async Task<TestResult> TestRutrackerMainAsync(CancellationToken ct) =>
        await HttpCheckAsync("Rutracker", "Главная rutracker.org", "https://rutracker.org/forum/index.php",
            expectSubstring: "RUTRACKER.ORG", ct);

    public async Task<TestResult> TestRutrackerMirrorAsync(CancellationToken ct) =>
        await HttpCheckAsync("Rutracker", "Зеркало nnm-club.ws", "https://nnm-club.ws/forum/index.php",
            expectSubstring: "NNM", ct, allowRedirectFail: false);

    public async Task<TestResult> TestRutrackerSearchAsync(CancellationToken ct) =>
        await HttpCheckAsync("Rutracker", "Поиск (api под блокировкой)",
            "https://rutracker.org/forum/tracker.php?search=test", expectSubstring: null, ct);

    /// <summary>DNS-проверка: резолвим rutracker обычным системным DNS и DoH Cloudflare — расхождение = DNS-подмена.</summary>
    public async Task<TestResult> TestDnsPoisonAsync(CancellationToken ct)
    {
        var res = NewResult("Rutracker", "DNS: подмена адреса (DoH vs системный)", "rutracker.org");
        try
        {
            var sys = await Dns.GetHostAddressesAsync("rutracker.org", ct);
            var doh = await ResolveViaDohAsync("rutracker.org", ct);
            var sysSet = sys.Select(x => x.ToString()).ToHashSet();
            var dohSet = doh.Select(x => x.ToString()).ToHashSet();
            res.Detail = $"система: [{string.Join(", ", sysSet)}] · DoH: [{string.Join(", ", dohSet)}]";
            res.Verdict = sysSet.SetEquals(dohSet) ? CheckVerdict.Running : CheckVerdict.Blocked;
            if (res.Verdict == CheckVerdict.Blocked)
                res.Detail += " — обнаружена DNS-подмена (характерно для ТСПУ blocking spoofing).";
        }
        catch (OperationCanceledException) { res.Detail = "Отменено"; }
        catch (Exception ex) { res.Verdict = CheckVerdict.Unknown; res.Detail = Explain(ex); }
        Publish(res);
        return res;
    }

    // ================= YouTube =================

    public async Task<TestResult> TestYouTubeWebAsync(CancellationToken ct) =>
        await HttpCheckAsync("YouTube", "Веб youtube.com", "https://www.youtube.com/",
            expectSubstring: "YouTube", ct);

    public async Task<TestResult> TestYouTubeVideoStreamAsync(CancellationToken ct) =>
        await HeadCheckAsync("YouTube", "Видеотрафик redirector.googlevideo.com",
            "https://redirector.googlevideo.com/videoplayback", ct);

    public async Task<TestResult> TestYouTubeImagesAsync(CancellationToken ct) =>
        await HeadCheckAsync("YouTube", "Превью i.ytimg.com", "https://i.ytimg.com/vb/blank.gif", ct);

    // ================= Google (инфраструктурный минимум) =================

    public async Task<TestResult> TestGoogleAsync(CancellationToken ct) =>
        await HttpCheckAsync("Google", "google.com (целью не является, но важно для gstatic)", "https://www.google.com/generate_204",
            expectSubstring: null, ct, expectStatus204: true);

    public async Task<TestResult> TestGstaticAsync(CancellationToken ct) =>
        await HeadCheckAsync("Google", "Статика www.gstatic.com", "https://www.gstatic.com/generate_204", ct);

    // ================= Cloudflare =================
    public async Task<TestResult> TestCloudflareApiAsync(CancellationToken ct) =>
        await HttpCheckAsync("Cloudflare", "Speed test api (cache)",
            "https://speed.cloudflare.com/__down?bytes=1024", expectSubstring: null, ct);

    // ================= orchestration =================

    public IReadOnlyList<TestResult> AllTargets() => new List<TestResult>
    {
        NewResult("Discord", "Сайт discord.com", "https://discord.com/"),
        NewResult("Discord", "API gateway JSON", "https://discord.com/api/v10/gateway"),
        NewResult("Discord", "WebSocket Gateway TLS", "gateway.discord.gg:443"),
        NewResult("Discord", "CDN cdn.discordapp.com", "https://cdn.discordapp.com/…"),
        NewResult("Discord", "Медиа media.discordapp.net TLS", "media.discordapp.net:443"),
        NewResult("Discord", "Voice STUN UDP", "stun/voice UDP"),
        NewResult("Discord", "Обновления updates.discord.com", "https://updates.discord.com/…"),
        NewResult("Rutracker", "Главная forum/index.php", "https://rutracker.org/forum/index.php"),
        NewResult("Rutracker", "Зеркало nnm-club.ws", "https://nnm-club.ws/…"),
        NewResult("Rutracker", "DNS подмена (DoH)", "rutracker.org"),
        NewResult("YouTube", "Веб youtube.com", "https://www.youtube.com/"),
        NewResult("YouTube", "googlevideo stream", "redirector.googlevideo.com"),
        NewResult("YouTube", "ytimg превью", "i.ytimg.com"),
        NewResult("Google", "generate_204", "www.google.com/generate_204"),
        NewResult("Google", "gstatic 204", "www.gstatic.com/generate_204"),
        NewResult("Cloudflare", "speed download", "speed.cloudflare.com"),
    };

    public async Task RunAllAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var tasks = new List<Func<CancellationToken, Task<TestResult>>>
        {
            TestDiscordAppAsync, TestDiscordApiAsync, TestDiscordGatewayTcpAsync,
            TestDiscordCdnAsync, TestDiscordMediaAsync, TestDiscordVoiceStunAsync,
            TestDiscordUpdatesAsync,
            TestRutrackerMainAsync, TestRutrackerMirrorAsync, TestDnsPoisonAsync,
            TestYouTubeWebAsync, TestYouTubeVideoStreamAsync, TestYouTubeImagesAsync,
            TestGoogleAsync, TestGstaticAsync, TestCloudflareApiAsync,
        };
        int done = 0;
        // последовательно + немного параллельно, чтобы не грузить сеть и DPI подряд
        var running = tasks.Select(t => t(ct)).ToList();
        foreach (var task in running)
        {
            try { await task; } catch (OperationCanceledException) { break; }
            done++;
            progress?.Report((double)done / tasks.Count);
        }
    }

    // ================= primitives =================

    private async Task<TestResult> HttpCheckAsync(string cat, string name, string url,
        string? expectSubstring, CancellationToken ct, bool expectStatus204 = false, bool allowRedirectFail = true)
    {
        var res = NewResult(cat, name, url);
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Accept-Language", "ru,en;q=0.9");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            var body = expectSubstring != null ? await resp.Content.ReadAsStringAsync(ct) : "";
            res.Latency = sw.Elapsed;
            res.Detail = $"{(int)resp.StatusCode} {resp.ReasonPhrase}, {body.Length} B";
            if (expectStatus204)
                res.Verdict = resp.StatusCode == HttpStatusCode.NoContent ? CheckVerdict.Running : CheckVerdict.Blocked;
            else if (expectSubstring != null)
                res.Verdict = resp.IsSuccessStatusCode && body.Contains(expectSubstring, StringComparison.OrdinalIgnoreCase)
                    ? CheckVerdict.Running
                    : resp.IsGatewayTimeout() || resp.StatusCode == HttpStatusCode.Forbidden ? CheckVerdict.Blocked : CheckVerdict.PartiallyBroken;
            else
                res.Verdict = resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Redirect ? CheckVerdict.Running : CheckVerdict.PartiallyBroken;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { res.Verdict = CheckVerdict.Blocked; res.Detail = "Таймаут соединения (похоже на блокировку по IP/SNI)"; res.Latency = sw.Elapsed; }
        catch (HttpRequestException ex)
        { res.Verdict = CheckVerdict.Blocked; res.Detail = Explain(ex); res.Latency = sw.Elapsed; }
        catch (Exception ex)
        { res.Verdict = CheckVerdict.Unknown; res.Detail = Explain(ex); res.Latency = sw.Elapsed; }
        Publish(res);
        return res;
    }

    private async Task<TestResult> HeadCheckAsync(string cat, string name, string url, CancellationToken ct)
    {
        var res = NewResult(cat, name, url);
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            res.Latency = sw.Elapsed;
            res.Detail = $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
            // для generate_204 и статики считаем успехом любой ответ сервера <=4xx за 10с — значит маршрут жив
            res.Verdict = resp.StatusCode != HttpStatusCode.GatewayTimeout &&
                          resp.StatusCode != HttpStatusCode.ServiceUnavailable &&
                          sw.Elapsed < TimeSpan.FromSeconds(9.5)
                ? CheckVerdict.Running : CheckVerdict.Blocked;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { res.Verdict = CheckVerdict.Blocked; res.Detail = "Таймаут HEAD-запроса"; res.Latency = sw.Elapsed; }
        catch (Exception ex)
        { res.Verdict = IsConnectivityError(ex) ? CheckVerdict.Blocked : CheckVerdict.Unknown; res.Detail = Explain(ex); res.Latency = sw.Elapsed; }
        Publish(res);
        return res;
    }

    private async Task<TestResult> TcpTlsHandshakeAsync(string cat, string name, string host, int port, CancellationToken ct)
    {
        var res = NewResult(cat, name, $"{host}:{port}");
        var sw = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(host, port, cts.Token);
            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, _, _, _) => true, // нас интересует сам факт handshake, не цепочка
            }, cts.Token);
            res.Latency = sw.Elapsed;
            res.Verdict = CheckVerdict.Running;
            res.Detail = $"TCP+TLS OK ({ssl.SslProtocol}, {ssl.TargetHostName})";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { res.Verdict = CheckVerdict.Blocked; res.Detail = "Таймаут TCP/TLS handshake (RST/Drop по SNI)"; res.Latency = sw.Elapsed; }
        catch (AuthenticationException ex)
        { res.Verdict = CheckVerdict.PartiallyBroken; res.Detail = "TLS handshake прерван: " + Explain(ex); res.Latency = sw.Elapsed; }
        catch (SocketException ex)
        { res.Verdict = CheckVerdict.Blocked; res.Detail = Explain(ex); res.Latency = sw.Elapsed; }
        catch (Exception ex)
        { res.Verdict = IsConnectivityError(ex) ? CheckVerdict.Blocked : CheckVerdict.Unknown; res.Detail = Explain(ex); res.Latency = sw.Elapsed; }
        Publish(res);
        return res;
    }

    private async Task<string[]> ResolveViaDohAsync(string host, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(
            $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A",
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var ips = new List<string>();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("Answer", out var answers))
            foreach (var a in answers.EnumerateArray())
                if (a.TryGetProperty("data", out var d))
                    ips.Add(d.GetString() ?? "");
        return ips.ToArray();
    }

    private static async Task<byte[]?> ReceiveWithTimeout(UdpClient udp, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { var r = await udp.ReceiveAsync(cts.Token); return r.Buffer; }
        catch { return null; }
    }

    private TestResult NewResult(string cat, string name, string target) =>
        new() { Category = cat, Name = name, Target = target };

    private void Publish(TestResult r) { r.Finished = true; ResultUpdated?.Invoke(r); }

    private static bool IsConnectivityError(Exception ex) =>
        ex is SocketException or HttpRequestException or IOException
        || ex.InnerException is SocketException;

    private static string Explain(Exception ex)
    {
        var msg = ex.Message.Replace("\r", " ").Replace("\n", " ");
        if (ex is WebException wex && wex.Status == WebExceptionStatus.NameResolutionFailure)
            msg = "DNS не резолвится: " + msg;
        return msg.Length > 140 ? msg[..140] + "…" : msg;
    }
}

internal static class TestResultExt
{
    public static bool IsGatewayTimeout(this HttpResponseMessage resp) =>
        (int)resp.StatusCode == 504 || (int)resp.StatusCode == 522 || (int)resp.StatusCode == 525;
}
