using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.IO;
namespace QZoneArchive.Core;
public sealed class QzoneApi : IQzoneApi, IDisposable
{
    private readonly HttpClient client;
    private readonly HttpClient mediaClient;
    private readonly int gtk;
    private readonly string token;
    private readonly int delay;
    private readonly Action<string> log;
    public QzoneApi(CookieContainer cookies, string skey, string token, int delay, Action<string> log)
    {
        client = new(new HttpClientHandler { CookieContainer = cookies, AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        mediaClient = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(3) };
        foreach (var c in new[] { client, mediaClient })
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36");
            c.DefaultRequestHeaders.Referrer = new Uri("https://user.qzone.qq.com/");
        }
        this.token = token; this.delay = delay; this.log = log;
        int hash = 5381; foreach (var ch in skey) hash = unchecked(hash + (hash << 5) + ch); gtk = hash & 0x7fffffff;
    }
    public async Task<JsonObject> Get(string endpoint, Dictionary<string, string> args, CancellationToken ct)
    {
        var p = new Dictionary<string, string>(args) { ["g_tk"] = gtk.ToString(), ["qzonetoken"] = token, ["format"] = "jsonp", ["inCharset"] = "utf-8", ["outCharset"] = "utf-8", ["_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() };
        var baseUrl = endpoint.StartsWith("https://") ? endpoint : "https://user.qzone.qq.com/proxy/domain/" + endpoint;
        if (new Uri(baseUrl).Host != "user.qzone.qq.com") throw new InvalidDataException("不支持的接口主机。");
        var url = baseUrl + "?" + string.Join("&", p.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(delay + Random.Shared.Next(100, 500), ct);
            try
            {
                using var response = await client.GetAsync(url, ct);
                if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("登录已失效或需要验证，请重新扫码后继续。");
                response.EnsureSuccessStatusCode();
                var data = J.Parse(await response.Content.ReadAsStringAsync(ct));
                var code = data.At("code") ?? data.At("ret");
                if (code != null && code.I() != 0) throw new InvalidDataException($"QQ 接口返回 {code}：{data.First("message", "msg")}。请在登录窗口检查登录状态或稍后重试。");
                return data;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (attempt >= 3) throw new InvalidDataException($"请求连续 4 次失败：{SafeError(ex)}");
                log($"接口暂未成功，{attempt + 1}/3 次重试，等待 {3 * (1 << attempt)} 秒。");
                await Task.Delay(3000 * (1 << attempt), ct);
            }
        }
    }
    private static string SafeError(Exception ex) => ex is HttpRequestException h ? $"HTTP / 网络错误（{h.StatusCode?.ToString() ?? "连接失败"}）" : ex is TaskCanceledException ? "请求超时" : ex.Message;
    public static bool AllowedMedia(Uri u) => (u.Scheme is "https" or "http") && u.IsDefaultPort && !u.IsLoopback && new[] { "qpic.cn", "qq.com", "gtimg.cn", "gtimg.com", "qlogo.cn" }.Any(d => u.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    public async Task<string> Download(string url, string folder, CancellationToken ct)
    {
        if (url.StartsWith("//")) url = "https:" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !AllowedMedia(u)) throw new InvalidDataException("媒体链接不是受支持的腾讯媒体域名，已保留原始链接。");
        if (u.Scheme == "http") u = new UriBuilder(u) { Scheme = "https", Port = -1 }.Uri;
        for (int redirects = 0; redirects < 6; redirects++)
        {
            using var response = await mediaClient.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                u = new Uri(u, response.Headers.Location ?? throw new InvalidDataException("媒体重定向缺少地址。"));
                if (!AllowedMedia(u)) throw new InvalidDataException("媒体重定向到了不受支持的地址。");
                continue;
            }
            response.EnsureSuccessStatusCode();
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            var ext = type switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp", "video/mp4" => ".mp4", "audio/mpeg" => ".mp3", "audio/amr" => ".amr", _ => "" };
            if (ext == "") throw new InvalidDataException("媒体未返回支持的图片/音视频格式，保留链接。");
            Directory.CreateDirectory(folder);
            var name = J.Hash(url) + ext; var path = Path.Combine(folder, name);
            if (File.Exists(path) && new FileInfo(path).Length > 0) return "media/" + name;
            var tmp = path + ".part";
            try
            {
                await using (var output = File.Create(tmp))
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                {
                    var buffer = new byte[65536]; long total = 0; int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        total += read;
                        if (total > 512L * 1024 * 1024) throw new InvalidDataException("单个媒体超过 512 MB，保留链接。");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    if (total == 0) throw new InvalidDataException("媒体返回空文件。");
                }
                await using (var content = File.OpenRead(tmp)) name = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(content, ct)).ToLowerInvariant() + ext;
                path = Path.Combine(folder, name);
                try { File.Move(tmp, path, false); }
                catch (IOException) when (File.Exists(path)) { File.Delete(tmp); }
                return "media/" + name;
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        throw new InvalidDataException("媒体重定向次数过多。");
    }
    public void Dispose() { client.Dispose(); mediaClient.Dispose(); }
}
