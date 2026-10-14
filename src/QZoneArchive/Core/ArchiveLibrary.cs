using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Globalization;
namespace QZoneArchive.Core;

public record Selection(DateTime? From = null, DateTime? Through = null, string Query = "", string Kind = "全部", bool Body = true, bool Comments = true, bool Pictures = true, bool Videos = true, bool Forwards = true, bool Likes = false, HashSet<string>? Ids = null);
public record OutputFormats(bool Txt = true, bool Html = true, bool Json = true, int TextChunkSize = 100);
public record MergeSummary(int InputPosts, int OutputPosts, int Duplicates, int ContentConflicts, int MediaFiles, string Folder);
public sealed record LoadedArchive(ArchiveState State, string Folder);
public static class ArchiveLibrary
{
    public static async Task<LoadedArchive> Load(string path)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject;
        if (root?.At("Posts") is not JsonArray || root.S("Target") == "") throw new InvalidDataException("请选择本工具导出的 archive.json，需包含 Target 和 Posts 字段。");
        var state = root.Deserialize<ArchiveState>() ?? throw new InvalidDataException("JSON 档案无法读取。");
        if (state.Posts.Any(p => p == null) || state.MediaFiles == null || state.Issues == null) throw new InvalidDataException("档案字段不完整。");
        Deduplicate(state);
        return new(state, Path.GetDirectoryName(Path.GetFullPath(path))!);
    }
    public static void Deduplicate(ArchiveState state)
    {
        state.Posts = state.Posts.GroupBy(RecordKey).Select(group =>
        {
            var rows = group.ToList(); if (rows.Count == 1) return rows[0];
            var winner = (JsonObject)rows.OrderByDescending(p => Body(p).Length).First().DeepClone();
            winner["archive_comments"] = Collector.MergeComments([], rows.SelectMany(p => p.A("archive_comments").Concat(p.A("commentlist"))));
            foreach (var field in new[] { "pic", "video", "audio", "urlinfo", "archive_likes" }) winner[field] = new JsonArray(rows.SelectMany(p => p.A(field)).DistinctBy(p => p.ToJsonString()).Select(p => p.DeepClone()).ToArray());
            var versions = rows.Select(Body).Concat(rows.SelectMany(p => p.A("archive_text_versions").Select(v => v.S()))).Where(t => t.Length > 0 && !Body(winner).StartsWith(t.TrimEnd('…', '.'), StringComparison.Ordinal)).Distinct().Select(t => (JsonNode?)JsonValue.Create(t)).ToArray();
            if (versions.Length > 0) winner["archive_text_versions"] = new JsonArray(versions);
            winner["archive_detail_done"] = rows.All(p => p.I("archive_detail_done") == 1) ? 1 : 0;
            return winner;
        }).ToList();
    }
    public static string Body(JsonNode p) => p.S("content") is { Length: > 0 } text ? text : ArchiveWriter.Text(p.At("conlist"));
    public static DateTime? PostDate(JsonNode p) => DateTime.TryParse(J.Date(p), CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;
    public static string RecordKey(JsonNode p) => p.S("tid") is { Length: > 0 } id ? id : "content-" + J.Hash(p.First("uin", "name") + "|" + J.Date(p) + "|" + Body(p).Trim() + "|" + ArchiveWriter.Text(p.At("rt_con")) + "|" + string.Join("|", Collector.MediaUrls(p).Distinct().Order()));
    public static bool Matches(JsonNode p, Selection s)
    {
        var date = PostDate(p);
        if (s.From != null && (date == null || date.Value.Date < s.From.Value.Date)) return false;
        if (s.Through != null && (date == null || date.Value.Date > s.Through.Value.Date)) return false;
        if (s.Ids != null && !s.Ids.Contains(RecordKey(p))) return false;
        if (s.Kind == "含图片" && p.A("pic").Count == 0) return false;
        if (s.Kind == "含视频" && p.A("video").Count == 0) return false;
        if (s.Kind == "有评论" && p.A("archive_comments").Count == 0 && p.A("commentlist").Count == 0) return false;
        if (s.Kind == "转发分享" && p.S("rt_tid") == "" && p.At("url") == null && p.At("urlinfo") == null) return false;
        if (s.Query.Trim() != "")
        {
            var text = string.Join("\n", new[] { Body(p), ArchiveWriter.Text(p.At("rt_con")), J.Date(p), p.First("name", "uin"), p.S("tid"), string.Join("\n", Collector.AllComments(p.At("archive_comments") ?? p.At("commentlist")).Select(c => Collector.AuthorName(c) + " " + c.S("content"))) });
            text = SpaceText.Plain(text);
            foreach (var term in s.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries)) if (!text.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    public static string? SafeMediaPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(basePath, relative));
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
        for (FileSystemInfo? current = new FileInfo(full); current != null && current.FullName.Length >= basePath.Length; current = current is FileInfo f ? f.Directory : (current as DirectoryInfo)?.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return null;
        return full;
    }
    private static async Task<string> CopyMedia(string source, string folder)
    {
        await using var stream = File.OpenRead(source);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        var extension = Path.GetExtension(source).ToLowerInvariant(); if (extension == ".jpeg") extension = ".jpg";
        if (!new[] { ".jpg", ".png", ".gif", ".webp", ".mp4", ".mp3", ".amr" }.Contains(extension)) throw new InvalidDataException("不支持的媒体文件类型。");
        var relative = "media/" + hash + extension;
        Directory.CreateDirectory(Path.Combine(folder, "media"));
        var target = Path.Combine(folder, relative); if (!File.Exists(target)) File.Copy(source, target);
        return relative;
    }
    public static async Task<MergeSummary> Import(IEnumerable<string> jsonPaths, string destination)
    {
        var sources = new List<LoadedArchive>();
        foreach (var path in jsonPaths.Distinct(StringComparer.OrdinalIgnoreCase)) sources.Add(await Load(path));
        if (sources.Count == 0) throw new InvalidDataException("没有选择 JSON 档案。");
        if (sources.Select(s => s.State.Target).Distinct().Count() != 1) throw new InvalidDataException("请选择同一个 QQ 账号的档案；不同账号分别建立档案库。");
        RequireEmpty(destination);
        var state = new ArchiveState { Target = sources[0].State.Target, Scope = "本地档案合并", Status = "已合并；原始采集未完成的内容不会自动补全", Updated = DateTimeOffset.Now.ToString("O") };
        var posts = new Dictionary<string, JsonObject>(); int total = 0, duplicates = 0, conflicts = 0;
        var events = new List<object>();
        foreach (var source in sources)
        {
            foreach (var node in source.State.Posts)
            {
                total++; var post = (JsonObject)node.DeepClone(); var key = RecordKey(post);
                if (!posts.TryGetValue(key, out var existing)) { posts[key] = post; continue; }
                duplicates++;
                var before = Body(existing); var after = Body(post);
                bool changed = before.Length > 0 && after.Length > 0 && before != after && !before.StartsWith(after.TrimEnd('…', '.')) && !after.StartsWith(before.TrimEnd('…', '.'));
                if (changed)
                {
                    conflicts++; var versions = existing.At("archive_text_versions") as JsonArray ?? new JsonArray();
                    foreach (var text in new[] { before, after }) if (!versions.Any(v => v.S() == text)) versions.Add(text);
                    if (existing.At("archive_text_versions") == null) existing["archive_text_versions"] = versions;
                }
                if (after.Length > before.Length) { existing["content"] = after; existing["conlist"] = post.At("conlist")?.DeepClone(); }
                if (ArchiveWriter.Text(post.At("rt_con")).Length > ArchiveWriter.Text(existing.At("rt_con")).Length) existing["rt_con"] = post.At("rt_con")?.DeepClone();
                foreach (var field in post) if (existing.At(field.Key) == null) existing[field.Key] = field.Value?.DeepClone();
                foreach (var field in new[] { "pic", "video", "audio", "urlinfo", "archive_likes" })
                {
                    var merged = existing.A(field).Concat(post.A(field)).GroupBy(n => n.First("url2", "hd_url", "url1", "video_id", "fuin", "uin") is { Length: > 0 } id ? id : J.Hash(n.ToJsonString())).Select(g => g.OrderByDescending(n => n.ToJsonString().Length).First().DeepClone());
                    existing[field] = new JsonArray(merged.ToArray());
                }
                existing["archive_comments"] = Collector.MergeComments(existing.A("archive_comments").Concat(existing.A("commentlist")), post.A("archive_comments").Concat(post.A("commentlist")));
                existing["archive_detail_done"] = Math.Max(existing.I("archive_detail_done"), post.I("archive_detail_done"));
                events.Add(new { Key = key, Action = changed ? "正文有差异，已保留候选版本" : "合并重复记录及评论附件" });
            }
            foreach (var pair in source.State.MediaFiles)
            {
                var path = SafeMediaPath(source.Folder, pair.Value);
                if (path == null) { state.Issues.Add(new("导入媒体", J.Hash(pair.Key)[..12], "媒体缺失或路径超出源档案目录，保留链接。")); continue; }
                try { state.MediaFiles[pair.Key] = await CopyMedia(path, destination); }
                catch (InvalidDataException ex) { state.Issues.Add(new("导入媒体", J.Hash(pair.Key)[..12], ex.Message)); }
            }
            foreach (var pair in source.State.Assets) { var path = SafeMediaPath(source.Folder, pair.Value); if (path != null) state.Assets[pair.Key] = await CopyMedia(path, destination); }
            state.Issues.AddRange(source.State.Issues);
            if (!source.State.ListFinished || source.State.Posts.Any(p => p.I("archive_detail_done") != 1)) state.Issues.Add(new("源档案", Path.GetFileName(source.Folder), "源档案包含未完成或待核对的采集内容，合并不会自动补全。"));
            state.Boards.AddRange(source.State.Boards.Select(n => n.DeepClone()));
        }
        state.Posts = posts.Values.OrderByDescending(PostDate).ToList(); state.Expected = state.Posts.Count; state.ListFinished = false;
        state.Boards = state.Boards.GroupBy(n => J.Hash(n.ToJsonString())).Select(g => g.First()).ToList(); state.Issues = state.Issues.Distinct().ToList();
        await J.Save(Path.Combine(destination, "archive.json"), state);
        var summary = new MergeSummary(total, posts.Count, duplicates, conflicts, state.MediaFiles.Values.Distinct().Count(), destination);
        await J.Save(Path.Combine(destination, "merge-report.json"), new { Summary = summary, Events = events });
        await ArchiveWriter.Write(state, destination); return summary;
    }
    private static void RequireEmpty(string folder)
    {
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) throw new IOException("结果目录非空，请选择新文件夹，避免覆盖原档案。");
        Directory.CreateDirectory(folder);
    }
    private static JsonObject CopyFields(JsonNode p, IEnumerable<string> fields)
    {
        var output = new JsonObject(); foreach (var key in fields) if (p.At(key) != null) output[key] = p.At(key)!.DeepClone(); return output;
    }
    private static JsonObject CleanComment(JsonNode c, Selection s)
    {
        var n = new JsonObject { ["id"] = Collector.CommentKey(c), ["uin"] = Collector.AuthorId(c), ["name"] = Collector.AuthorName(c), ["createTime"] = J.Date(c), ["content"] = c.S("content"), ["replyNum"] = Collector.ReplyCount(c) };
        if (s.Pictures && c.At("pic") != null) n["pic"] = c.At("pic")!.DeepClone();
        foreach (var key in new[] { "reply_name", "replyName", "reply_nick", "replyNickname", "to_name", "to_nick", "reply_user", "replyTo" }) if (c.At(key) != null) n[key] = c.At(key)!.DeepClone();
        n["archive_replies"] = new JsonArray(Collector.Replies(c).Select(r => (JsonNode)CleanComment(r, s)).ToArray()); return n;
    }
    public static ArchiveState Select(ArchiveState source, Selection selection)
    {
        if (selection.From > selection.Through) throw new ArgumentException("开始日期不能晚于结束日期。");
        var state = new ArchiveState { Target = source.Target, Scope = "用户筛选导出（不代表完整空间）", Status = "原档案状态：" + source.Status, Updated = DateTimeOffset.Now.ToString("O"), ListFinished = false };
        foreach (var p in source.Posts.Where(p => Matches(p, selection)))
        {
            var n = CopyFields(p, new[] { "tid", "uin", "name", "nickname", "created_time", "create_time", "createTime", "time", "postTime", "pubtime", "readCount", "view_num", "archive_detail_done", "archive_parser_version", "archive_comments_requested" });
            n["tid"] = RecordKey(p);
            if (selection.Body) { n["content"] = Body(p); if (p.At("archive_text_versions") != null) n["archive_text_versions"] = p.At("archive_text_versions")!.DeepClone(); }
            if (selection.Forwards) foreach (var key in new[] { "rt_tid", "rt_uin", "rt_uname", "rt_uinname", "rt_con", "rt_conlist", "url", "urlinfo" }) if (p.At(key) != null) n[key] = p.At(key)!.DeepClone();
            if (selection.Comments)
            {
                n["archive_comments"] = new JsonArray((p.At("archive_comments") ?? p.At("commentlist")).A().Select(c => (JsonNode)CleanComment(c, selection)).ToArray());
                n["archive_comment_expected"] = Math.Max(Collector.CountComments(p), p.I("archive_comment_expected")); n["archive_comments_checked"] = p.I("archive_comments_checked");
            }
            if (selection.Pictures && p.At("pic") != null) n["pic"] = p.At("pic")!.DeepClone();
            if (selection.Videos) foreach (var key in new[] { "video", "audio", "voice" }) if (p.At(key) != null) n[key] = p.At(key)!.DeepClone();
            if (selection.Likes) foreach (var key in new[] { "likenum", "archive_likes" }) if (p.At(key) != null) n[key] = p.At(key)!.DeepClone();
            n["archive_export_fields"] = new JsonObject { ["body"] = selection.Body, ["comments"] = selection.Comments, ["pictures"] = selection.Pictures, ["videos"] = selection.Videos, ["forwards"] = selection.Forwards, ["likes"] = selection.Likes };
            if (!selection.Body || !selection.Comments || !selection.Pictures || !selection.Videos || !selection.Forwards) n["archive_detail_done"] = 0;
            RemoveExcludedMedia(n, selection);
            state.Posts.Add(n);
        }
        state.Expected = state.Posts.Count; var ids = state.Posts.Select(RecordKey).ToHashSet(); var sourceIds = source.Posts.Select(RecordKey).ToHashSet(); state.Issues = source.Issues.Where(i => ids.Contains(i.Id) || !sourceIds.Contains(i.Id)).ToList(); return state;
    }
    private static void RemoveExcludedMedia(JsonNode? node, Selection s)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(k => k.Key).ToArray())
            {
                var lower = key.ToLowerInvariant();
                if ((!s.Pictures && (lower.Contains("pic") || lower.Contains("image") || lower.Contains("thumb") || lower == "cover")) || (!s.Videos && (lower.Contains("video") || lower.Contains("audio") || lower.Contains("voice")))) obj.Remove(key);
                else RemoveExcludedMedia(obj[key], s);
            }
        }
        else if (node is JsonArray a) foreach (var item in a) RemoveExcludedMedia(item, s);
    }
    public static async Task<ArchiveState> Export(LoadedArchive source, Selection selection, OutputFormats formats, string destination)
    {
        if (!formats.Txt && !formats.Html && !formats.Json) throw new ArgumentException("至少选择一种导出格式。");
        var state = Select(source.State, selection); if (state.Posts.Count == 0) throw new InvalidDataException("当前筛选没有匹配记录。");
        RequireEmpty(destination);
        foreach (var url in state.Posts.SelectMany(Collector.MediaUrls).Distinct())
        {
            if (!source.State.MediaFiles.TryGetValue(url, out var relative)) continue;
            var path = SafeMediaPath(source.Folder, relative);
            if (path == null) { state.Issues.Add(new("导出媒体", J.Hash(url)[..12], "源媒体不存在，保留原链接。")); continue; }
            try { state.MediaFiles[url] = await CopyMedia(path, destination); } catch (InvalidDataException ex) { state.Issues.Add(new("导出媒体", J.Hash(url)[..12], ex.Message)); }
        }
        foreach (var url in SpaceText.AssetsFor(state).Distinct()) { if (source.State.Assets.TryGetValue(url, out var relative)) { var path = SafeMediaPath(source.Folder, relative); if (path != null) state.Assets[url] = await CopyMedia(path, destination); } }
        if (formats.Json) await J.Save(Path.Combine(destination, "archive.json"), state);
        if (formats.Html) await ArchiveWriter.Write(state, destination, formats.Json, false);
        if (formats.Txt)
        {
            int chunk = formats.TextChunkSize <= 0 ? Math.Max(1, state.Posts.Count) : formats.TextChunkSize; int part = 1;
            foreach (var batch in state.Posts.Chunk(chunk))
            {
                var name = state.Posts.Count <= chunk ? "说说.txt" : $"说说-{part:000}.txt";
                await File.WriteAllTextAsync(Path.Combine(destination, name), PlainText(state, batch, selection, part++), new UTF8Encoding(false));
            }
        }
        await J.Save(Path.Combine(destination, "export-manifest.json"), new { SourcePosts = source.State.Posts.Count, ExportedPosts = state.Posts.Count, selection, formats, state.Issues, state.Status });
        await File.WriteAllTextAsync(Path.Combine(destination, "阅读说明.txt"), $"时光留存 · 可读档案\n\n本次导出 {state.Posts.Count} 条说说，源档案 {source.State.Posts.Count} 条。\n{state.Status}\n时间均按北京时间 UTC+8 解释；每条记录包含稳定 ID。\n正文、转发、评论是不同作者的内容，请分析时区分。缩进表示回复层级。\n未导出的字段不等于原始空间没有这些内容；未完成核对不等于内容不存在。\nTXT 可直接输入 AI；分卷按编号顺序使用。JSON 直接显示中文，保留结构与字段选择说明。\nHTML 为离线阅读与媒体画廊；请连同 media 文件夹保留。\n本次选择：正文={selection.Body}，评论={selection.Comments}，图片={selection.Pictures}，音视频={selection.Videos}，转发={selection.Forwards}，点赞={selection.Likes}。\n存在 {state.Issues.Count} 项待核对记录，详见 export-manifest.json。\n", new UTF8Encoding(false)); return state;
    }
    public static string PlainText(ArchiveState state, IEnumerable<JsonObject> posts, Selection fields, int volume)
    {
        var text = new StringBuilder($"QQ 空间说说档案 · 第 {volume} 卷\n账号：{state.Target}\n范围：{state.Scope}\n{state.Status}\n日期时区：北京时间 UTC+8\n\n");
        foreach (var p in posts)
        {
            text.AppendLine(new string('=', 64)).AppendLine("说说 ID：" + RecordKey(p)).AppendLine("日期时间：" + J.Date(p)).AppendLine("作者：" + p.First("name", "nickname", "uin"));
            text.AppendLine("核对状态：" + (p.I("archive_detail_done") == 1 ? "已完成本条采集核对" : "尚未完成，可能缺少全文或评论"));
            text.AppendLine("\n【正文】\n" + (fields.Body ? Body(p) : "本次未选择导出正文"));
            if (fields.Forwards && (p.At("rt_con") != null || p.At("rt_tid") != null)) text.AppendLine("\n【转发内容】\n原作者：" + p.First("rt_uinname", "rt_uname", "rt_uin") + "\n" + ArchiveWriter.Text(p.At("rt_con")));
            if (fields.Forwards) foreach (var link in p.A("url").Concat(p.A("urlinfo"))) text.AppendLine("分享链接：" + link.First("title", "desc") + " " + link.First("url", "url1"));
            if (fields.Forwards && Collector.IsUrl(p.S("url"))) text.AppendLine("分享链接：" + p.S("url"));
            foreach (var v in p.A("archive_text_versions")) text.AppendLine("\n【同 ID 的另一正文版本，保留供对比】\n" + v.S());
            text.AppendLine("\n【评论与回复】");
            if (!fields.Comments) text.AppendLine("本次未选择导出评论。");
            else if (p.A("archive_comments").Count == 0) text.AppendLine(p.I("archive_comments_checked") == 1 ? "本次接口返回 0 条评论。" : "没有已保存的评论；尚不能确认原说说没有评论。");
            else AddComments(text, p.A("archive_comments"), 0);
            var urls = Collector.MediaUrls(p).Distinct().ToArray();
            if (urls.Length > 0) { text.AppendLine("\n【媒体附件】"); foreach (var url in urls) text.AppendLine(state.MediaFiles.TryGetValue(url, out var local) ? "本地文件：" + local : "未下载，原链接：" + url); }
            if (fields.Likes) text.AppendLine("\n【点赞者】\n" + string.Join("、", p.A("archive_likes").Select(n => n.First("nick", "name", "fuin", "uin"))));
            foreach (var issue in state.Issues.Where(i => i.Id == p.S("tid"))) text.AppendLine("待核对：" + issue.Message); text.AppendLine();
        }
        return SpaceText.Plain(text.ToString());
    }
    private static void AddComments(StringBuilder text, IEnumerable<JsonNode> comments, int depth)
    {
        if (depth > 20) { text.AppendLine("更深层回复请参阅 JSON。"); return; }
        foreach (var c in comments)
        {
            var indent = new string(' ', depth * 2);
            text.AppendLine($"{indent}- {(depth == 0 ? "评论" : "回复")} ID：{Collector.CommentKey(c)} | {Collector.AuthorName(c)}（{Collector.AuthorId(c)}） | {J.Date(c)}");
            if (SpaceText.ReplyTo(c) is { Length: > 0 } recipient) text.AppendLine(indent + "  回复对象：" + recipient);
            text.AppendLine(indent + "  " + c.S("content").Replace("\n", "\n" + indent + "  ")); AddComments(text, Collector.Replies(c), depth + 1);
        }
    }
}
