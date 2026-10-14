using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
namespace QZoneArchive.Core;
public static class SpaceText
{
    private static readonly Regex Tokens = new(@"@\{[^}]*\}|\[em\]e\d+\[/em\]", RegexOptions.IgnoreCase);
    private static readonly Regex Faces = new(@"\[em\]e(\d+)\[/em\]", RegexOptions.IgnoreCase);
    private static string Field(string token, string field) => Regex.Match(token, @"(?:\{|,)\s*" + field + @":([^}]*?)(?=,\s*\w+:|\})").Groups[1].Value;
    public static string MentionName(string token) => WebUtility.HtmlDecode(Field(token, "nick") is { Length: > 0 } name ? name : Field(token, "uin"));
    public static string Plain(string? value) => Tokens.Replace(WebUtility.HtmlDecode(value ?? ""), m => m.Value.StartsWith('@') ? "@" + MentionName(m.Value) : "[QQ表情 " + Faces.Match(m.Value).Groups[1].Value + "]");
    public static string FaceUrl(string id) => "https://qzonestyle.gtimg.cn/qzone/em/e" + id + ".gif";
    public static string AvatarUrl(string id) => Regex.IsMatch(id, @"^\d{5,13}$") ? $"https://qlogo2.store.qq.com/qzone/{id}/{id}/100" : "";
    public static string Name(JsonNode n, ArchiveState state)
    {
        var name = Collector.AuthorName(n); if (name.Length > 0) return name;
        var id = Collector.AuthorId(n);
        var found = state.Posts.Cast<JsonNode>().Concat(state.Posts.SelectMany(p => Collector.AllComments(p.At("archive_comments")))).FirstOrDefault(p => Collector.AuthorId(p) == id && Collector.AuthorName(p).Length > 0);
        return found == null ? (id == "" ? "昵称未返回" : "QQ " + id) : Collector.AuthorName(found);
    }
    private static string E(string value) => WebUtility.HtmlEncode(value);
    public static string Html(string value, ArchiveState state)
    {
        var input = WebUtility.HtmlDecode(value); var result = new System.Text.StringBuilder(); int offset = 0;
        foreach (Match m in Tokens.Matches(input))
        {
            result.Append(E(input[offset..m.Index]));
            if (m.Value.StartsWith('@')) result.Append("<span class='mention'>@" + Html(MentionName(m.Value), state) + "</span>");
            else
            {
                var id = Faces.Match(m.Value).Groups[1].Value;
                if (state.Assets.TryGetValue(FaceUrl(id), out var path) && SafePath(path)) result.Append($"<img class='emoji' src='{E(path)}' alt='[QQ表情 {id}]' title='QQ表情 {id}'>");
                else result.Append(E("[QQ表情 " + id + "]"));
            }
            offset = m.Index + m.Length;
        }
        return result.Append(E(input[offset..])).ToString();
    }
    public static bool SafePath(string path) => path.StartsWith("media/") && !path.Contains("..") && !path.Contains(':') && !path.Contains('\\');
    public static string Avatar(string id, ArchiveState state)
    {
        if (state.Assets.TryGetValue(AvatarUrl(id), out var path) && SafePath(path)) return $"<img class='avatar' src='{E(path)}' alt='用户头像' loading='lazy'>";
        return "<span class='avatar avatar-fallback' aria-label='未保存头像'>Q</span>";
    }
    public static string ReplyTo(JsonNode n) => Plain(n.First("reply_name", "replyName", "reply_nick", "replyNickname", "to_name", "to_nick") is { Length: > 0 } name ? name : n.At("reply_user").First("name", "nickname", "nick") is { Length: > 0 } user ? user : n.At("replyTo").First("name", "nickname", "nick"));
    public static IEnumerable<string> AssetsFor(ArchiveState state, bool avatars = true, bool emoji = true)
    {
        if (avatars && AvatarUrl(state.Target) is { Length: > 0 } owner) yield return owner;
        foreach (var p in state.Posts.Cast<JsonNode>().Concat(state.Boards).Concat(state.Shares))
            foreach (var url in Walk(p)) yield return url;
        IEnumerable<string> Walk(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if (avatars && AvatarUrl(Collector.AuthorId(n)) is { Length: > 0 } url) yield return url;
                foreach (var pair in o.Where(k => k.Key != "archive_detail_raw")) foreach (var url2 in Walk(pair.Value)) yield return url2;
            }
            else if (n is JsonArray a) { foreach (var item in a) foreach (var url in Walk(item)) yield return url; }
            else if (n is JsonValue)
            {
                var text = n.ToString(); if (emoji) foreach (Match face in Faces.Matches(text)) yield return FaceUrl(face.Groups[1].Value);
                if (avatars) foreach (Match mention in Tokens.Matches(text)) if (AvatarUrl(Field(mention.Value, "uin")) is { Length: > 0 } url) yield return url;
            }
        }
    }
}
