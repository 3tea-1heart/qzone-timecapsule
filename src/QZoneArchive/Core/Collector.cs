using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QZoneArchive.Core;
public sealed class Collector(IQzoneApi api, ExportOptions options, IProgress<ProgressInfo> progress)
{
    public ArchiveState State { get; private set; } = new();
    public bool Paused { get; set; }
    private string StatePath => Path.Combine(options.Folder, "archive.json");
    private Dictionary<string, string> Args(params (string, object)[] pairs)
    {
        var p = new Dictionary<string, string> { ["uin"] = options.Target, ["hostUin"] = options.Target, ["hostuin"] = options.Owner, ["callback"] = "_Callback", ["code_version"] = "1" };
        foreach (var (key, value) in pairs) p[key] = value.ToString()!;
        return p;
    }
    private void Report(string stage, string msg) => progress.Report(new(stage, State.Posts.Count, State.Expected, State.Posts.Sum(p => AllComments(p.At("archive_comments")).Count()), State.MediaFiles.Count, State.Issues.Count, msg));
    private void Warn(string scope, string id, string msg) { if (!State.Issues.Any(i => i.Scope == scope && i.Id == id && i.Message == msg)) State.Issues.Add(new(scope, id, msg)); Report(scope, msg); }
    private async Task Tick(CancellationToken ct) { while (Paused) await Task.Delay(250, ct); ct.ThrowIfCancellationRequested(); }
    private async Task Save() { State.Updated = DateTimeOffset.Now.ToString("O"); await J.Save(StatePath, State); }
    public static IEnumerable<JsonNode> AllComments(JsonNode? list)
    {
        return Walk(list.A());
        static IEnumerable<JsonNode> Walk(IEnumerable<JsonNode> comments)
        {
            foreach (var c in comments)
            {
                yield return c;
                foreach (var r in Walk(Replies(c))) yield return r;
            }
        }
    }
    public static string AuthorId(JsonNode n) => n.First("uin", "fuin") is { Length: > 0 } id ? id : n.At("poster").First("id", "uin") is { Length: > 0 } poster ? poster : n.At("user").First("uin", "id");
    public static string AuthorName(JsonNode n) => n.First("name", "nickname", "nick") is { Length: > 0 } name ? name : n.At("poster").First("name", "nickname", "nick") is { Length: > 0 } poster ? poster : n.At("user").First("nickname", "name", "nick");
    public static List<JsonNode> Replies(JsonNode n) => n.At("archive_replies") is JsonArray ? n.A("archive_replies") : Merge([], n.A("list_3").Concat(n.A("replys")).Concat(n.A("replies"))).A();
    public static int ReplyCount(JsonNode n) => new[] { n.I("reply_num"), n.I("replynum"), n.I("replyNum"), n.I("replytotal"), n.At("extendData").I("replyNum") }.Max();
    public static string CommentKey(JsonNode n) => n.First("tid", "commentid", "id") is { Length: > 0 } id ? id : J.Hash(AuthorId(n) + "|" + J.Date(n) + "|" + n.S("content"));
    public static JsonArray MergeComments(IEnumerable<JsonNode> old, IEnumerable<JsonNode> fresh) => Merge(old, fresh);
    public static string ListSignature(JsonNode post)
    {
        var summary = new JsonObject();
        foreach (var key in new[] { "content", "conlist", "editTime", "modifytime", "last_modify", "pictotal", "pic", "video", "rt_tid", "rt_con", "rt_conlist", "has_more_con", "rt_has_more_con" }) if (post.At(key) != null) summary[key] = post.At(key)!.DeepClone();
        summary["comment_count"] = CountComments(post);
        summary["comment_sample"] = new JsonArray(AllComments(post.At("commentlist")).Select(c => (JsonNode)new JsonObject { ["id"] = CommentKey(c), ["content"] = c.S("content"), ["replies"] = ReplyCount(c) }).OrderBy(c => c.S("id")).ToArray());
        return J.Hash(Canonical(summary));
        static string Canonical(JsonNode? node) => node switch { JsonObject obj => "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}", JsonArray arr => "[" + string.Join(",", arr.Select(Canonical)) + "]", _ => node?.ToJsonString() ?? "null" };
    }
    private static bool LegacyChanged(JsonNode old, JsonNode fresh)
    {
        if (CountComments(old) != CountComments(fresh)) return true;
        var current = ArchiveLibrary.Body(fresh).TrimEnd('…', '.'); var saved = ArchiveLibrary.Body(old);
        if (current.Length > 0 && !(saved == current || (fresh.I("has_more_con") == 1 && saved.StartsWith(current, StringComparison.Ordinal)))) return true;
        var known = AllComments(old.At("archive_comments")).GroupBy(CommentKey).ToDictionary(g => g.Key, g => g.First());
        return AllComments(fresh.At("commentlist")).Any(c => !known.TryGetValue(CommentKey(c), out var prior) || c.S("content") != prior.S("content") || ReplyCount(c) != ReplyCount(prior));
    }
    private static JsonArray Merge(IEnumerable<JsonNode> old, IEnumerable<JsonNode> fresh)
    {
        var map = new Dictionary<string, JsonNode>();
        foreach (var n in old.Concat(fresh))
        {
            var key = CommentKey(n);
            map.TryGetValue(key, out var previous);
            var copy = previous?.DeepClone() as JsonObject ?? new JsonObject();
            if (n is JsonObject obj) foreach (var kv in obj) copy[kv.Key] = kv.Value?.DeepClone();
            var author = AuthorId(n); if (author != "") copy["uin"] = author;
            var name = AuthorName(n); if (name != "") copy["name"] = name;
            copy["archive_replies"] = Merge(previous == null ? [] : Replies(previous), Replies(n));
            map[key] = copy;
        }
        return new JsonArray(map.Values.ToArray());
    }
    public async Task Run(CancellationToken ct)
    {
        Directory.CreateDirectory(options.Folder);
        // Exclusive backup lock prevents two windows corrupting the same archive.
        using var guard = new FileStream(Path.Combine(options.Folder, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(StatePath))
        {
            State = JsonSerializer.Deserialize<ArchiveState>(await File.ReadAllTextAsync(StatePath, ct)) ?? throw new InvalidDataException("备份文件无法读取。");
            if (State.Target != options.Target) throw new InvalidDataException("该目录属于其他 QQ 账号，请选择另一个目录。");
            File.Copy(StatePath, StatePath + ".bak", true);
            ArchiveLibrary.Deduplicate(State);
        }
        State.Target = options.Target; State.Issues = []; State.ListFinished = false; State.Status = "采集中";
        try
        {
            if (options.TopUp) { if (State.Posts.Count == 0) throw new InvalidDataException("当前目录没有已有档案，不能使用只补采模式。"); State.Scope = "只补已有档案（不检查新增说说）"; State.ListFinished = true; }
            else if (options.PostIds is { Length: > 0 })
            {
                State.Scope = "指定说说验证（不是全量备份）";
                foreach (var id in options.PostIds.Distinct())
                    if (!State.Posts.Any(p => p.S("tid") == id)) State.Posts.Add(new JsonObject { ["tid"] = id, ["uin"] = options.Target });
                State.Expected = options.PostIds.Distinct().Count(); State.ListFinished = true;
            }
            else { State.Scope = options.From != null || options.Through != null || options.MaxPosts > 0 ? "指定范围备份（不是全量空间）" : "全部可见说说"; await CollectList(ct); }
            int reused = 0, refreshed = 0;
            foreach (var post in ActivePosts().ToList())
            {
                await Tick(ct);
                var id = post.S("tid"); var before = State.Issues.Count;
                var done = post.I("archive_detail_done") == 1 && post.I("archive_parser_version") == 2 && !options.Refresh && new[] { "body", "comments", "pictures", "videos", "forwards" }.All(k => post.At("archive_export_fields").S(k) != "false");
                bool dirty = !done;
                if (!done)
                {
                    refreshed++;
                    Report("补全说说", $"读取全文、图片与完整评论 · {id}");
                    await Attempt("全文", id, () => FullContent(post, ct));
                    await Attempt("图片列表", id, () => Pictures(post, ct));
                    if (options.Comments) await Attempt("评论", id, () => Comments(post, ct));
                    post["archive_comments_requested"] = options.Comments;
                    post["archive_detail_done"] = before == State.Issues.Count && options.Comments ? 1 : 0;
                    post["archive_parser_version"] = 2;
                }
                else reused++;
                if (options.Likes && (options.Refresh || post.I("archive_likes_done") != 1))
                { dirty = true; await Attempt("点赞", id, () => Likes(post, ct)); }
                if (dirty) await Save();
            }
            Report("增量核对完成", $"复用未变化记录 {reused} 条；补采或重新核对 {refreshed} 条。");
            if (options.Boards) await Attempt("留言板", "list", () => Extras(false, ct));
            if (options.Shares) Warn("独立分享", "unsupported", "独立分享页尚未适配；说说中的转发内容仍会保存。");
            if (options.Media || options.Videos || options.Avatars || options.Emoji) await Media(ct);
            State.Status = State.Issues.Count == 0 && State.ListFinished ? (options.PostIds is { Length: > 0 } ? "指定说说验证已完成（不是全量备份）" : State.Scope == "全部可见说说" ? "已完成本轮采集（以接口可见范围为准）" : "已完成所选范围 · " + State.Scope) : "已保存，存在待核对项";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { State.Status = "已停止，已保存进度"; }
        catch (Exception ex) { State.Status = "任务中断，已保存进度"; Warn("任务", "run", ex.Message); }
        finally
        {
            await Save();
            await ArchiveWriter.Write(State, options.Folder);
            Report(State.Status, "已生成离线时间线、JSON 与核对报告。再次开始可重新核对列表并补采未完成条目。");
        }
    }
    private async Task Attempt(string scope, string id, Func<Task> run)
    {
        try { await run(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Warn(scope, id, ex.Message); }
    }
    private async Task CollectList(CancellationToken ct)
    {
        var map = State.Posts.ToDictionary(p => p.S("tid"));
        var selected = new HashSet<string>(); var seen = new HashSet<string>(); var failed = new List<int>();
        int offset = 0, repeat = 0; bool end = false;
        State.Expected = -1;
        async Task<int> Page(int pos)
        {
            var data = await api.Get(Endpoints.List, Args(("pos", pos), ("num", 20), ("ftype", 0), ("sort", 0), ("replynum", options.Comments ? 100 : 0), ("need_private_comment", 1), ("callback", "_preloadCallback")), ct);
            if (data.At("total") != null) State.Expected = Math.Max(State.Expected, data.I("total"));
            if (data.At("msglist") is not JsonArray && !(data.At("msglist") == null && data.I("total", -1) == 0)) throw new InvalidDataException("说说列表缺少 msglist 字段，不能当作导出结束。");
            var rows = data.A("msglist");
            if (rows.Count == 0 && State.Expected > pos) throw new InvalidDataException($"第 {pos} 偏移页意外为空，接口总数为 {State.Expected}。");
            int fresh = 0;
            foreach (var node in rows)
            {
                if (node is not JsonObject p || p.S("tid") == "") throw new InvalidDataException("说说条目没有 tid，需检查接口兼容性。");
                var id = p.S("tid"); if (seen.Add(id)) fresh++;
                if (!ArchiveLibrary.Matches(p, new(options.From, options.Through)) || (options.MaxPosts > 0 && selected.Count >= options.MaxPosts && !selected.Contains(id))) continue;
                selected.Add(id);
                if (map.TryGetValue(id, out var previous))
                {
                    // Keep full content and previous comments until fresh detail is successfully obtained.
                    var signature = ListSignature(p);
                    bool changed = previous.S("archive_list_signature") is { Length: > 0 } oldSignature ? oldSignature != signature : LegacyChanged(previous, p);
                    if (changed) { previous["archive_detail_done"] = 0; Report("检测到变化", $"说说 {id} 的摘要或互动记录变化，将重新核对详情。"); }
                    if (p.At("likenum") != null && p.I("likenum") != previous.I("likenum")) previous["archive_likes_done"] = 0;
                    previous["archive_list_signature"] = signature;
                    previous["archive_list_checked_at"] = DateTimeOffset.Now.ToString("O");
                    foreach (var k in new[] { "commenttotal", "cmtnum", "reply_num", "replies", "likenum", "readCount", "view_num" })
                        if (p.At(k) != null) previous[k] = p[k]!.DeepClone();
                }
                else { map[id] = (JsonObject)p.DeepClone(); map[id]["archive_list_signature"] = ListSignature(p); State.Posts.Add(map[id]); }
            }
            if (rows.Count > 0 && fresh == 0) repeat++; else repeat = 0;
            Report("枚举说说", $"列表偏移 {pos} · 本轮发现 {seen.Count} 条 · 接口总数 {State.Expected}");
            await Save(); return rows.Count;
        }
        for (int page = 0; page < 10000; page++)
        {
            await Tick(ct);
            int count;
            try { count = await Page(offset); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed.Add(offset); Report("列表补采", $"偏移 {offset} 暂未成功，将在枚举后再补采。{ex.Message}");
                if (State.Expected < 0 || failed.Count >= 5) break;
                offset += 20; if (offset >= State.Expected) { end = true; break; } continue;
            }
            if (options.MaxPosts > 0 && selected.Count >= options.MaxPosts) { end = true; break; }
            if (repeat >= 2) { Warn("列表", offset.ToString(), "连续返回重复页，已停止，避免把重复数据当作完整备份。"); break; }
            if (count == 0) { end = true; break; }
            offset += count; // Use actual page length, never number of new / deduplicated posts.
            if (State.Expected >= 0 && offset >= State.Expected) { end = true; break; }
        }
        foreach (var pos in failed)
        {
            await Tick(ct); await Attempt("列表", pos.ToString(), async () => { await Page(pos); });
        }
        if (!end) Warn("列表", "end", "尚未确认列表终点，请稍后继续补采。");
        if (options.MaxPosts == 0 && State.Expected >= 0 && seen.Count != State.Expected) Warn("列表", "count", $"本轮独立条目 {seen.Count}，接口报告 {State.Expected}；可能有删除、权限变化或漏页，不能保证全部齐全。");
        State.ListFinished = end && !State.Issues.Any(i => i.Scope == "列表");
        if (options.From != null || options.Through != null || options.MaxPosts > 0) { State.VisibleTotal = State.Expected; State.Expected = selected.Count; }
        State.Posts = State.Posts.OrderByDescending(p => p.I("created_time")).ToList();
    }
    public static int CountComments(JsonNode p) => new[] { p.I("commenttotal"), p.I("cmtnum"), p.I("reply_num"), p.I("replies") }.Max();
    private async Task FullContent(JsonObject post, CancellationToken ct)
    {
        var detail = await api.Get(Endpoints.Detail, Args(("tid", post.S("tid")), ("t1_source", 1), ("not_trunc_con", 1), ("need_private_comment", 1), ("qzreferrer", "https://user.qzone.qq.com")), ct);
        var d = detail.At("data") as JsonObject ?? detail;
        if (d.At("content") == null && d.At("conlist") == null) throw new InvalidDataException("详情没有返回正文；保留列表内容，标记为尚未补全。");
        post.Remove("archive_export_fields");
        post["archive_detail_raw"] = d.DeepClone();
        post["archive_comments"] = Merge(post.A("archive_comments"), d.A("commentlist"));
        foreach (var k in new[] { "name", "nickname", "created_time", "createTime", "likenum", "readCount", "view_num", "content", "conlist", "rt_con", "rt_conlist", "rt_uin", "rt_uname", "rt_uinname", "rt_tid", "pic", "video", "audio", "url", "urlinfo", "url_info", "share_info", "pictotal", "commenttotal", "cmtnum", "reply_num", "replies", "has_more_con", "rt_has_more_con" })
        {
            if (d.At(k) == null) continue;
            if (k == "content" && d.S(k).Length < post.S(k).Length) continue;
            if (d.At(k) is JsonArray && d.A(k).Count < post.A(k).Count) continue;
            post[k] = d[k]!.DeepClone();
        }
        post["archive_fulltext"] = 1;
        if (d.I("has_more_con") == 1 || d.I("rt_has_more_con") == 1) throw new InvalidDataException("详情接口仍标记正文或转发正文被截断，请核对这条说说。");
    }
    private async Task Pictures(JsonObject post, CancellationToken ct)
    {
        if (post.I("pictotal") <= post.A("pic").Count) return;
        var data = await api.Get(Endpoints.Pictures, Args(("tid", post.S("tid")), ("t1_source", 1)), ct);
        var urls = data.A("imageUrls");
        if (urls.Count == 0) throw new InvalidDataException("更多图片接口返回空，图片列表可能不全。");
        var pics = post.A("pic").Select(n => n.DeepClone()).ToList();
        for (int i = 0; i < urls.Count; i++)
        {
            if (i >= pics.Count) pics.Add(new JsonObject());
            pics[i]["url2"] = urls[i].S();
        }
        post["pic"] = new JsonArray(pics.ToArray());
        if (post.I("pictotal") > pics.Count) throw new InvalidDataException($"图片列表 {pics.Count}/{post.I("pictotal")}，待核对。");
    }
    private async Task Comments(JsonObject post, CancellationToken ct)
    {
        var merged = Merge(post.A("archive_comments"), post.A("commentlist"));
        post["archive_comments"] = merged;
        var fresh = new HashSet<string>(); var expected = CountComments(post); int start = 0; bool finished = false;
        for (int page = 0; page < 10000; page++)
        {
            await Tick(ct);
            var data = await api.Get(Endpoints.Comments, Args(("uin", options.Owner), ("start", start), ("num", 100), ("order", 0), ("topicId", options.Target + "_" + post.S("tid")), ("ref", "qzone"), ("need_private_comment", 1)), ct);
            var body = data.At("data") ?? data;
            var list = data.At("commentlist") ?? body.At("comments") ?? body.At("commentlist");
            // Missing schema is never silently treated as zero comments.
            if (list is not JsonArray && !(body.At("total") != null && body.I("total") == 0)) throw new InvalidDataException($"评论响应无法识别（offset={start}，已取={fresh.Count}，预期={expected}，根字段={string.Join(',', data.Select(x => x.Key))}，data字段={string.Join(',', (body as JsonObject ?? new JsonObject()).Select(x => x.Key))}）。已保留取得的评论。");
            var rows = list.A();
            expected = Math.Max(expected, Math.Max(body.I("total"), body.I("commenttotal")));
            int added = 0; foreach (var row in rows) if (fresh.Add(CommentKey(row))) added++;
            post["archive_comments"] = Merge(post.A("archive_comments"), rows);
            post["archive_comment_expected"] = expected;
            if (rows.Count == 0) { finished = true; break; }
            if (added == 0) throw new InvalidDataException("评论分页重复，已保留目前拿到的内容。");
            start += rows.Count;
            // The live API omits the comments field on the terminal page. Stop only
            // after the independently fetched unique comments meet the declared total.
            if (expected > 0 && fresh.Count >= expected) { finished = true; break; }
        }
        if (!finished) throw new InvalidDataException("评论分页达到上限，尚未确认结束。");
        if (fresh.Count < expected) Warn("评论", post.S("tid"), $"本轮获得 {fresh.Count} 条一级评论，接口计数 {expected}。计数可能含回复或不可见内容，需要核对。");
        foreach (var c in post.A("archive_comments"))
        {
            int expectedReplies = ReplyCount(c);
            int replies = Replies(c).Count;
            if (expectedReplies > replies) Warn("评论回复", post.S("tid"), $"评论 {CommentKey(c)} 的回复只返回 {replies}/{expectedReplies}，原始数据已保留。");
        }
        post["archive_comments_checked"] = 1;
    }
    private async Task Likes(JsonObject post, CancellationToken ct)
    {
        var likes = new Dictionary<string, JsonNode>(); var begin = "0";
        for (int page = 0; page < 10000; page++)
        {
            await Tick(ct);
            var result = await api.Get(Endpoints.Likes, Args(("uin", options.Owner), ("unikey", $"http://user.qzone.qq.com/{options.Target}/mood/{post.S("tid")}"), ("begin_uin", begin), ("query_count", 60), ("if_first_page", begin == "0" ? 1 : 0)), ct);
            var data = result.At("data") ?? throw new InvalidDataException("点赞接口缺少 data。");
            if (data.I("is_dolike") == 1) likes[options.Owner] = new JsonObject { ["fuin"] = options.Owner, ["nick"] = "当前登录账号" };
            var rows = data.A("like_uin_info");
            foreach (var n in rows) likes[n.First("fuin", "uin")] = n.DeepClone();
            post["archive_likes"] = new JsonArray(likes.Values.Select(n => n.DeepClone()).ToArray());
            if (rows.Count == 0) { post["archive_likes_done"] = 1; return; }
            var next = rows.Last().First("fuin", "uin");
            if (next == begin || next == "") throw new InvalidDataException("点赞分页没有推进。");
            begin = next;
        }
        throw new InvalidDataException("点赞分页达到上限。");
    }
    private async Task Extras(bool shares, CancellationToken ct)
    {
        var output = new Dictionary<string, JsonNode>(); int pos = 0;
        foreach (var row in shares ? State.Shares : State.Boards)
        {
            var key = row.First("id", "tid", "shareid"); if (key == "") key = J.Hash(row.ToJsonString());
            output[key] = row.DeepClone();
        }
        var seen = new HashSet<string>();
        for (int page = 0; page < 10000; page++)
        {
            await Tick(ct);
            var data = await api.Get(shares ? Endpoints.Shares : Endpoints.Boards, shares ? Args(("page", page), ("num", 50), ("spaceuin", options.Target), ("isfriend", 0), ("ttype", 0)) : Args(("uin", options.Owner), ("start", pos), ("num", 50)), ct);
            var body = data.At("data") ?? data;
            var list = shares ? body.At("items") ?? body.At("list") ?? body.At("share_list") : body.At("commentList") ?? body.At("commentlist");
            if (list is not JsonArray) throw new InvalidDataException("附加模块返回的结构不兼容；已保存其他模块。");
            var rows = list.A(); if (rows.Count == 0) return;
            int added = 0;
            foreach (var row in rows)
            {
                var key = row.First("id", "tid", "shareid"); if (key == "") key = J.Hash(row.ToJsonString());
                if (seen.Add(key)) added++;
                output[key] = row.DeepClone();
            }
            if (shares) State.Shares = output.Values.ToList(); else State.Boards = output.Values.ToList();
            if (added == 0) throw new InvalidDataException("附加模块分页重复。");
            pos += rows.Count; Report(shares ? "独立分享" : "留言板", $"已取得 {pos} 条"); await Save();
        }
        throw new InvalidDataException("附加模块分页达到上限。");
    }
    public static IEnumerable<string> MediaUrls(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            // A video's url2 may be a watch page; download its preview and direct media separately.
            bool video = obj.At("video_id") != null || obj.At("videoid") != null || obj.At("video_url") != null;
            var img = video ? obj.First("preview_img", "pic_url", "url1") : obj.First("hd_url", "url2", "url1", "b_url", "pic_url", "preview_img");
            if (IsUrl(img)) yield return img;
            foreach (var kv in obj)
            {
                if (kv.Key == "archive_detail_raw") continue;
                if (kv.Key is "video_url" or "play_url" or "download_url" or "url3") { if (IsUrl(kv.Value.S())) yield return kv.Value.S(); }
                if (kv.Value is JsonObject or JsonArray) foreach (var u in MediaUrls(kv.Value)) yield return u;
            }
        }
        else if (node is JsonArray arr) foreach (var n in arr) foreach (var u in MediaUrls(n)) yield return u;
    }
    public static bool IsUrl(string s) => s.StartsWith("https://") || s.StartsWith("http://") || s.StartsWith("//");
    private IEnumerable<JsonObject> ActivePosts() => State.Posts.Where(p => (options.PostIds == null || options.PostIds.Contains(p.S("tid"))) && ArchiveLibrary.Matches(p, new(options.From, options.Through))).OrderByDescending(ArchiveLibrary.PostDate).Take(options.MaxPosts > 0 ? options.MaxPosts : int.MaxValue);
    public static IEnumerable<(string Url, bool Video)> DownloadUrls(JsonNode? node, bool videoContext = false)
    {
        if (node is JsonObject obj)
        {
            bool video = videoContext || obj.At("video_id") != null || obj.At("videoid") != null || obj.At("video_url") != null;
            var image = video ? obj.First("preview_img", "pic_url", "url1") : obj.First("hd_url", "url2", "url1", "b_url", "pic_url", "preview_img");
            if (IsUrl(image)) yield return (image, System.Text.RegularExpressions.Regex.IsMatch(image, @"\.(mp4|mp3|m4a|amr|webm)(?:[?#]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            foreach (var kv in obj)
            {
                if (kv.Key == "archive_detail_raw") continue;
                if (kv.Key is "video_url" or "play_url" or "download_url" or "url3" && IsUrl(kv.Value.S())) yield return (kv.Value.S(), true);
                if (video && kv.Key == "url" && IsUrl(kv.Value.S()) && System.Text.RegularExpressions.Regex.IsMatch(kv.Value.S(), @"\.(mp4|mp3|m4a|amr|webm)(?:[?#]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) yield return (kv.Value.S(), true);
                if (kv.Value is JsonObject or JsonArray) foreach (var u in DownloadUrls(kv.Value, kv.Key is "video" or "audio" or "voice" || video)) yield return u;
            }
        }
        else if (node is JsonArray arr) foreach (var n in arr) foreach (var u in DownloadUrls(n, videoContext)) yield return u;
    }
    private async Task Media(CancellationToken ct)
    {
        var active = new ArchiveState { Target = State.Target, Posts = ActivePosts().ToList(), Boards = State.Boards, Shares = State.Shares };
        var jobs = active.Posts.Cast<JsonNode>().Concat(State.Boards).Concat(State.Shares).SelectMany(n => DownloadUrls(n)).Where(j => j.Video ? options.Videos : options.Media).Select(j => (j.Url, Asset: false))
            .Concat(SpaceText.AssetsFor(active, options.Avatars, options.Emoji).Select(u => (Url: u, Asset: true))).DistinctBy(j => j.Url).ToList();
        var pending = jobs.Where(j => !(j.Asset ? State.Assets : State.MediaFiles).TryGetValue(j.Url, out var local) || ArchiveLibrary.SafeMediaPath(options.Folder, local) == null).ToList();
        int done = jobs.Count - pending.Count; Report("下载附件", $"已复用 {done} 个附件，剩余 {pending.Count} 个；并发 {options.DownloadWorkers}。");
        foreach (var batch in pending.Chunk(Math.Clamp(options.DownloadWorkers, 1, 8)))
        {
            await Tick(ct);
            var results = await Task.WhenAll(batch.Select(async job =>
            {
                try
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        try { return (job, Path: await api.Download(job.Url, Path.Combine(options.Folder, "media"), ct), Error: (Exception?)null); }
                        catch (OperationCanceledException) { throw; }
                        catch when (attempt < 2) { await Task.Delay(1500 * (attempt + 1), ct); }
                    }
                }
                catch (Exception ex) { return (job, Path: "", Error: (Exception?)ex); }
            }));
            foreach (var result in results)
            {
                done++;
                if (result.Error == null) (result.job.Asset ? State.Assets : State.MediaFiles)[result.job.Url] = result.Path;
                else if (result.Error is not OperationCanceledException) Warn("媒体", J.Hash(result.job.Url)[..16], "附件下载失败，保留来源；可稍后补采。");
            }
            await Save(); ct.ThrowIfCancellationRequested(); Report("下载附件", $"附件 {done}/{jobs.Count} · 已保存媒体 {State.MediaFiles.Count}，头像和表情 {State.Assets.Count}");
        }
    }
}
