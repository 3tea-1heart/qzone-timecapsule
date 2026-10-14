using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace QZoneArchive.Core;
public static class J
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static JsonNode? At(this JsonNode? n, string key) => n is JsonObject o ? o[key] : null;
    public static string S(this JsonNode? n, string key = "") => (key == "" ? n : n.At(key))?.ToString() ?? "";
    public static int I(this JsonNode? n, string key = "", int fallback = 0) => int.TryParse(n.S(key), out var v) ? v : fallback;
    public static List<JsonNode> A(this JsonNode? n, string key = "") => (key == "" ? n : n.At(key)) is JsonArray a ? a.Where(x => x != null).Select(x => x!).ToList() : [];
    public static string First(this JsonNode? n, params string[] keys) => keys.Select(k => n.S(k)).FirstOrDefault(v => v.Length > 0) ?? "";
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static JsonObject Parse(string text)
    {
        text = text.Trim().TrimStart('\uFEFF');
        if (!text.StartsWith('{'))
        {
            var start = text.IndexOf('('); var end = text.LastIndexOf(')');
            if (start < 0 || end <= start) throw new InvalidDataException("接口未返回 JSON，可能需要重新登录或完成验证。");
            text = text[(start + 1)..end];
        }
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("接口响应结构不正确。");
    }
    public static async Task Save(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, Pretty), Encoding.UTF8);
        File.Move(temp, path, true);
    }
    public static string Date(JsonNode? n)
    {
        var text = n.First("created_time", "create_time", "createTime", "postTime", "pubtime", "createTime2");
        if (long.TryParse(text, out var ts) && ts > 100000000)
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(ts > 100000000000 ? ts / 1000 : ts).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
        }
        return text.Length > 0 ? text : n.S("time");
    }
}
public record ExportOptions(string Owner, string Target, string Folder, bool Media = true, bool Likes = false, bool Boards = false, bool Shares = false, bool Refresh = false, int DelayMs = 1800, string[]? PostIds = null, bool Videos = true, bool Comments = true, bool Avatars = false, bool Emoji = false, DateTime? From = null, DateTime? Through = null, int MaxPosts = 0, bool TopUp = false, int DownloadWorkers = 4);
public record ProgressInfo(string Stage, int Posts, int Expected, int Comments, int Media, int Issues, string Message);
public record Issue(string Scope, string Id, string Message);
public sealed class ArchiveState
{
    public string Target { get; set; } = "";
    public string Scope { get; set; } = "全部可见说说";
    public int Expected { get; set; } = -1;
    public int VisibleTotal { get; set; } = -1;
    public bool ListFinished { get; set; }
    public string Status { get; set; } = "未开始";
    public string Updated { get; set; } = "";
    public List<Issue> Issues { get; set; } = [];
    public List<JsonObject> Posts { get; set; } = [];
    public List<JsonNode> Boards { get; set; } = [];
    public List<JsonNode> Shares { get; set; } = [];
    public Dictionary<string, string> MediaFiles { get; set; } = [];
    public Dictionary<string, string> Assets { get; set; } = [];
}
public interface IQzoneApi
{
    Task<JsonObject> Get(string endpoint, Dictionary<string, string> args, CancellationToken ct);
    Task<string> Download(string url, string folder, CancellationToken ct);
}
public static class Endpoints
{
    public const string List = "taotao.qq.com/cgi-bin/emotion_cgi_msglist_v6";
    public const string Detail = "taotao.qq.com/cgi-bin/emotion_cgi_msgdetail_v6";
    public const string Pictures = "taotao.qq.com/cgi-bin/emotion_cgi_get_pics_v6";
    public const string Comments = "taotao.qzone.qq.com/cgi-bin/emotion_cgi_getcmtreply_v6";
    public const string Likes = "users.qzone.qq.com/cgi-bin/likes/get_like_list_app";
    public const string Boards = "m.qzone.qq.com/cgi-bin/new/get_msgb";
    public const string Shares = "https://user.qzone.qq.com/p/h5/pc/api/sns.qzone.qq.com/cgi-bin/qzshare/cgi_qzsharegetmylistbytype";
}
