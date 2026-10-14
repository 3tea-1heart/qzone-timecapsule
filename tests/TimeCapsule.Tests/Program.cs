using System.Text.Json.Nodes;
using QZoneArchive.Core;

var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/tests");
Directory.CreateDirectory(root);
int assertions = 0;
void Check(bool b, string message) { if (!b) throw new Exception("FAIL: " + message); assertions++; Console.WriteLine("PASS: " + message); }
Check(J.Parse("_Callback({\"content\":\"a(b)\\n中文\"});").S("content") == "a(b)\n中文", "JSONP preserves punctuation and Unicode");
Check(!QzoneApi.AllowedMedia(new Uri("https://qq.com.evil.test/a")) && !QzoneApi.AllowedMedia(new Uri("http://127.0.0.1/a")), "Media host allowlist rejects spoofing and loopback");
Check(QzoneApi.AllowedMedia(new Uri("https://m.qpic.cn/a")), "QQ CDN accepted");
Check(J.Date(new JsonObject { ["created_time"] = 0 }) == "0", "Zero date is not invented");

async Task<(Collector, Fake)> Run(string name, string mode = "normal", bool refresh = false, bool media = true)
{
    var api = new Fake(mode);
    var collector = new Collector(api, new("10001", "10001", Path.Combine(root, name), media, true, false, false, refresh, 0), new SyncProgress());
    await collector.Run(CancellationToken.None); return (collector, api);
}
var (normal, normalApi) = await Run("normal");
Check(normal.State.Posts.Count == 3 && normal.State.ListFinished, "Short pages continue using actual offsets; all posts collected");
Check(normalApi.Offsets.SequenceEqual(new[] { 0, 2 }), "No skipped list items after a short page");
var p = normal.State.Posts.First(p => p.S("tid") == "p1");
Check(p.S("content").Length > 3000 && p.S("content").EndsWith("正文结束"), "Long text saved beyond preview and beyond several hundred characters");
Check(p.S("rt_con").EndsWith("转发结束"), "Forwarded full text retained");
Check(p.A("archive_comments").Count == 101, "Comments paginated beyond first 100");
Check(p.A("archive_comments").Any(c => c.S("uin") == "10001"), "Owner comments are never filtered out");
Check(Collector.AllComments(p.At("archive_comments")).Any(c => c.S("content") == "自己回复自己的评论"), "Self replies preserved");
Check(p.A("pic").Count == 2, "Missing list images obtained through picture detail endpoint");
Check(p.A("archive_likes").Any(l => l.S("fuin") == "10001"), "Current user like retained via is_dolike");
Check(normal.State.MediaFiles.Count == 2, "Downloaded media indexed");
Check(normal.State.Issues.Count == 0, "Healthy fixture has no completeness warnings");
var html = await File.ReadAllTextAsync(Path.Combine(root, "normal", "index.html"));
Check(!html.Contains("<script>alert('x')</script>") && html.Contains("&lt;script&gt;"), "Untrusted post markup escaped in offline HTML");
Check(html.Contains("media/") && !html.Contains("src='https://"), "Offline media uses local paths only");
var (resume, resumeApi) = await Run("normal");
Check(resume.State.Posts.Count == 3 && resumeApi.DetailCalls == 0, "Resume deduplicates and skips completed details");
var (_, refreshApi) = await Run("normal", refresh: true);
Check(refreshApi.DetailCalls == 3, "Refresh mode explicitly rechecks all full text");
var (broken, _) = await Run("empty-middle", "empty-middle");
Check(!broken.State.ListFinished && broken.State.Issues.Any(i => i.Scope == "列表"), "Unexpected empty list page cannot produce complete status");
var (badComments, _) = await Run("bad-comments", "bad-comments");
Check(badComments.State.Issues.Any(i => i.Scope == "评论") && badComments.State.Posts.First().I("archive_detail_done") == 0, "Comment errors remain retriable and are not treated as no comments");
var (partialReplies, _) = await Run("partial-replies", "partial-replies");
Check(partialReplies.State.Issues.Any(i => i.Scope == "评论回复"), "Truncated reply count is reported explicitly");
var (repeated, repeatedApi) = await Run("repeated", "repeat-comments");
Check(repeated.State.Issues.Any(i => i.Message.Contains("重复")) && repeatedApi.CommentCalls < 20, "Repeated comment pages stop with a warning");
var cancelApi = new Fake("normal"); using var cts = new CancellationTokenSource();
cancelApi.OnDetail = () => cts.Cancel();
var cancelled = new Collector(cancelApi, new("10001", "10001", Path.Combine(root, "cancelled"), false), new SyncProgress());
await cancelled.Run(cts.Token);
Check(cancelled.State.Status.Contains("已停止") && File.Exists(Path.Combine(root, "cancelled", "index.html")), "Cancellation saves state and partial offline archive");
Check(cancelled.State.Posts.Count == 3, "Collected posts survive cancellation during detail stage");
var verifyApi = new Fake("normal");
var verify = new Collector(verifyApi, new("10001", "10001", Path.Combine(root, "verify"), false, PostIds: ["p1"]), new SyncProgress());
await verify.Run(CancellationToken.None);
Check(verify.State.Posts.Count == 1 && verifyApi.Offsets.Count == 0, "Targeted verification reads selected post without scanning the whole account");
Check(verify.State.Scope.Contains("不是全量") && verify.State.Status.Contains("不是全量"), "Targeted result cannot be mistaken for full backup");
var (liveShape, liveApi) = await Run("live-shape", "live-shape");
var lp = liveShape.State.Posts.First(p => p.S("tid") == "p1");
Check(liveShape.State.Issues.Count == 0 && liveApi.CommentCalls == 4, "Declared total ends pagination before fieldless terminal response");
Check(lp.A("archive_comments").First().S("uin") == "10001" && lp.A("archive_comments").First().S("name") == "自己", "Live poster.id and poster.name normalize correctly");
Check(Collector.AllComments(lp.At("archive_comments")).Count() == 103, "Reply aliases merge without duplicates and preserve extra replies");
var lh = await File.ReadAllTextAsync(Path.Combine(root, "live-shape", "index.html"));
Check(lh.Contains("空间主人") && lh.Contains("新的补充回复"), "Live author badge and both reply sources render in HTML");
var sourcePath = Path.Combine(root, "normal", "archive.json");
var loaded = await ArchiveLibrary.Load(sourcePath);
var sourceBytes = await File.ReadAllTextAsync(sourcePath);
var merged = await ArchiveLibrary.Import(new[] { sourcePath, Path.Combine(root, "live-shape", "archive.json") }, Path.Combine(root, "merged"));
Check(merged.InputPosts == 6 && merged.OutputPosts == 3 && merged.Duplicates == 3, "Import merges same IDs without losing distinct posts");
Check(await File.ReadAllTextAsync(sourcePath) == sourceBytes, "Import does not modify source archive");
var mergedState = await ArchiveLibrary.Load(Path.Combine(root, "merged", "archive.json"));
Check(Collector.AllComments(mergedState.State.Posts.First(p => p.S("tid") == "p1").At("archive_comments")).Any(c => c.S("content") == "新的补充回复"), "Merge keeps additional replies");
Check(ArchiveLibrary.SafeMediaPath(loaded.Folder, "../normal/archive.json") != null, "Normalized in-root path is accepted");
Check(ArchiveLibrary.SafeMediaPath(loaded.Folder, "../live-shape/archive.json") == null, "Media paths cannot escape source directory");
var reduced = await ArchiveLibrary.Export(loaded, new(Comments: false, Pictures: false, Videos: false, Forwards: false), new(TextChunkSize: 1), Path.Combine(root, "readable"));
Check(reduced.Posts.All(p => p.At("archive_comments") == null && p.At("archive_detail_raw") == null && p.At("commentlist") == null), "Excluded comments cannot leak through raw snapshots");
Check(reduced.MediaFiles.Count == 0 && reduced.Posts.All(p => p.At("pic") == null && p.At("rt_con") == null), "Excluded media and forwards are absent");
Check(Directory.GetFiles(Path.Combine(root, "readable"), "说说-*.txt").Length == 3, "TXT splits on record boundaries");
var readableJson = await File.ReadAllTextAsync(Path.Combine(root, "readable", "archive.json"));
Check(readableJson.Contains("正文结束"), "JSON contains directly readable Chinese");
var readableTxt = await File.ReadAllTextAsync(Path.Combine(root, "readable", "说说-001.txt"));
Check(readableTxt.Contains("正文结束") && readableTxt.Contains("本次未选择导出评论"), "TXT preserves full text and distinguishes excluded comments");
var reducedHtml = await File.ReadAllTextAsync(Path.Combine(root, "readable", "index.html"));
Check(reducedHtml.Contains("本次未选择导出评论"), "HTML identifies excluded comments");
var roundtrip = await ArchiveLibrary.Load(Path.Combine(root, "readable", "archive.json"));
Check(roundtrip.State.Posts.Count == 3, "Exported JSON imports again");
await ArchiveLibrary.Export(loaded, new(), new(false, true, false), Path.Combine(root, "html-only"));
var htmlOnly = await File.ReadAllTextAsync(Path.Combine(root, "html-only", "index.html"));
Check(!htmlOnly.Contains("href='archive.json'") && !htmlOnly.Contains("href='说说.md'"), "HTML-only export contains no unavailable download links");
Check(File.Exists(Path.Combine(root, "html-only", "gallery.html")), "Offline Gallery is generated");
Check(ArchiveLibrary.Matches(lp, new(Query: "新的补充回复")), "Search finds deeply nested own replies");
var dateFixture = new JsonObject { ["tid"] = "date", ["create_time"] = "2026-10-13 23:59:59", ["content"] = "日期边界" };
Check(ArchiveLibrary.Matches(dateFixture, new(new(2026,10,13), new(2026,10,13))), "Date range includes entire final day");
Check(!ArchiveLibrary.Matches(dateFixture, new(Through: new(2026,10,12))), "Date range excludes next day");
var nest = new ArchiveState { Target = "10001", Posts = [new JsonObject { ["tid"] = "nested", ["rt_con"] = new JsonObject { ["content"] = "保留", ["pic"] = new JsonArray("private-media") } }] };
Check(!ArchiveLibrary.Select(nest, new(Pictures: false)).Posts[0].ToJsonString().Contains("private-media"), "Image exclusion applies to nested forward content");
try { await ArchiveLibrary.Export(loaded, new(), new(), Path.Combine(root, "readable")); Check(false, "Existing result must be protected"); } catch (IOException) { Check(true, "Existing result directory is protected"); }
var conflictA = new ArchiveState { Target = "10001", Posts = [new JsonObject { ["tid"] = "same", ["content"] = "旧正文", ["create_time"] = "2026-10-13 10:00:00" }, new JsonObject { ["tid"] = "different", ["content"] = "旧正文" }] };
var conflictB = new ArchiveState { Target = "10001", Posts = [new JsonObject { ["tid"] = "same", ["content"] = "修改后的另一段正文" }] };
await J.Save(Path.Combine(root,"conflict-a","archive.json"),conflictA); await J.Save(Path.Combine(root,"conflict-b","archive.json"),conflictB);
var conflictResult = await ArchiveLibrary.Import(new[] {Path.Combine(root,"conflict-a","archive.json"),Path.Combine(root,"conflict-b","archive.json")},Path.Combine(root,"conflict-result"));
var conflictLoaded = await ArchiveLibrary.Load(Path.Combine(root,"conflict-result","archive.json"));
Check(conflictResult.OutputPosts == 2 && conflictResult.ContentConflicts == 1 && conflictLoaded.State.Posts.First(p => p.S("tid")=="same").A("archive_text_versions").Count == 2,"Conflicting same-ID versions preserved; different IDs not falsely deleted");
conflictB.Target = "20002"; await J.Save(Path.Combine(root,"other-account","archive.json"),conflictB);
try { await ArchiveLibrary.Import(new[] {Path.Combine(root,"conflict-a","archive.json"),Path.Combine(root,"other-account","archive.json")},Path.Combine(root,"invalid-merge")); Check(false,"Cross-account merge must fail"); } catch (InvalidDataException) { Check(true,"Different accounts cannot be silently merged"); }
Check(SpaceText.Plain("hi 😀👨‍👩‍👧‍👦 @{uin:12345,nick:阿&amp;明,who:1}") == "hi 😀👨‍👩‍👧‍👦 @阿&明", "Unicode emoji and mention nicknames render without protocol tokens");
Check(SpaceText.Plain("@{uin:12345,who:1}") == "@12345", "Missing mention nickname falls back to user ID");
var decorated = Demo.Create(); decorated.Assets[SpaceText.FaceUrl("100")] = "media/face.gif"; decorated.Assets[SpaceText.AvatarUrl(decorated.Target)] = "media/head.png";
Check(SpaceText.Html("[em]e100[/em] <script>bad</script>", decorated).Contains("class='emoji'") && !SpaceText.Html("[em]e100[/em] <script>bad</script>", decorated).Contains("<script>"), "QQ emoji uses local asset while untrusted markup stays escaped");
Check(SpaceText.ReplyTo(new JsonObject { ["replyTo"] = new JsonObject { ["nickname"] = "对方😊" } }) == "对方😊", "Reply recipient nickname resolved separately from author");
Check(Collector.AuthorName(new JsonObject { ["poster"] = new JsonObject { ["nick"] = "朋友" } }) == "朋友", "Alternate poster nick field supported");
var recentApi = new Fake("normal"); var recent = new Collector(recentApi,new("10001","10001",Path.Combine(root,"recent"),false,Videos:false,Comments:false,MaxPosts:1),new SyncProgress()); await recent.Run(CancellationToken.None);
Check(recent.State.Posts.Count == 1 && recentApi.DetailCalls == 1 && recentApi.Offsets.Count == 1, "Recent N stops list and detail requests early");
Check(recentApi.CommentCalls == 0 && recent.State.Posts[0].I("archive_detail_done") == 0 && recent.State.Scope.Contains("不是全量"), "Skipping comments saves requests without claiming full detail completion");
var topupApi = new Fake("normal");var topup = new Collector(topupApi,new("10001","10001",Path.Combine(root,"recent"),false,Videos:false,TopUp:true),new SyncProgress());await topup.Run(CancellationToken.None);
Check(topupApi.Offsets.Count == 0 && topupApi.CommentCalls > 0 && topup.State.Posts[0].I("archive_detail_done") == 1, "Top-up skips list and completes previously omitted comments");
var excludedApi = new Fake("normal"); var excluded = new Collector(excludedApi,new("10001","10001",Path.Combine(root,"date-empty"),false,Videos:false,From:new DateTime(2026,1,1)),new SyncProgress());await excluded.Run(CancellationToken.None);
Check(excluded.State.Posts.Count == 0 && excludedApi.DetailCalls == 0, "Date filtering prevents out-of-range detail requests");
var splitMedia = Collector.DownloadUrls(new JsonObject { ["video"] = new JsonArray(new JsonObject { ["video_id"] = "v", ["url1"] = "https://m.qpic.cn/cover.jpg", ["video_url"] = "https://video.qq.com/movie.mp4" }) }).ToList();
Check(splitMedia.Count(j=>j.Video)==1 && splitMedia.Count(j=>!j.Video)==1,"Video file and thumbnail are classified separately");
var assetsApi = new Fake("normal");var assetsCollector = new Collector(assetsApi,new("10001","10001",Path.Combine(root,"avatars"),false,Videos:false,Comments:false,Avatars:true,MaxPosts:1),new SyncProgress());await assetsCollector.Run(CancellationToken.None);
Check(assetsCollector.State.Assets.Count == 1 && assetsCollector.State.MediaFiles.Count == 0,"Avatar storage is independent of photo and video selection");
var assetsExport = await ArchiveLibrary.Export(new(assetsCollector.State,Path.Combine(root,"avatars")),new(),new(),Path.Combine(root,"avatars-export"));
Check(assetsExport.Assets.Count == 1 && File.Exists(Path.Combine(root,"avatars-export",assetsExport.Assets.Values.First())),"Avatar survives export and local file copy");
var probe = new ParallelProbe(); var concurrent = new Collector(probe,new("10001","10001",Path.Combine(root,"concurrent"),Avatars:true,DownloadWorkers:4),new SyncProgress());await concurrent.Run(CancellationToken.None);
Check(probe.Maximum > 1 && probe.Maximum <= 4,"Downloads actually overlap within configured concurrency bound");
Check(reduced.Posts.All(p=>p.I("archive_detail_done")==0),"Field-limited JSON is not marked as a complete collection");
var restoreApi = new Fake("normal");var restoreCollector = new Collector(restoreApi,new("10001","10001",Path.Combine(root,"readable"),false,Videos:false,TopUp:true),new SyncProgress());await restoreCollector.Run(CancellationToken.None);
Check(restoreApi.DetailCalls==3 && restoreCollector.State.Posts.All(p=>p.At("archive_export_fields")==null),"Recollecting a field-limited export restores data and clears old exclusion markers");
var smartFolder = Path.Combine(root,"smart-resume");
var firstSmartApi = new Fake("normal");await new Collector(firstSmartApi,new("10001","10001",smartFolder,false,Videos:false),new SyncProgress()).Run(CancellationToken.None);
var unchangedApi = new Fake("normal");await new Collector(unchangedApi,new("10001","10001",smartFolder,false,Videos:false),new SyncProgress()).Run(CancellationToken.None);
Check(unchangedApi.Offsets.Count > 0 && unchangedApi.DetailCalls == 0 && unchangedApi.CommentCalls == 0,"Smart resume checks list but reuses unchanged complete details and comments");
var changedApi = new Fake("edited-list");var changedCollector = new Collector(changedApi,new("10001","10001",smartFolder,false,Videos:false),new SyncProgress());await changedCollector.Run(CancellationToken.None);
Check(changedApi.DetailCalls == 1 && changedCollector.State.Posts.Count == 3,"Same-count edited post triggers only its own detail refresh without duplicate rows");
var unorderedA = new JsonObject { ["tid"]="a", ["content"]="正文", ["pictotal"]=2};var unorderedB=new JsonObject { ["pictotal"]=2,["content"]="正文",["tid"]="a"};
Check(Collector.ListSignature(unorderedA)==Collector.ListSignature(unorderedB),"Change fingerprint ignores object property ordering");
var duplicatesState = new ArchiveState { Target="10001", Posts=[new JsonObject { ["tid"]="dupe",["content"]="正文",["archive_comments"]=new JsonArray(new JsonObject { ["id"]="c1",["content"]="评论" }) },new JsonObject { ["tid"]="dupe",["content"]="正文加长",["archive_comments"]=new JsonArray(new JsonObject { ["id"]="c1",["content"]="评论" },new JsonObject { ["id"]="c2",["content"]="补充" }) }]};
ArchiveLibrary.Deduplicate(duplicatesState);Check(duplicatesState.Posts.Count==1 && duplicatesState.Posts[0].S("content")=="正文加长" && duplicatesState.Posts[0].A("archive_comments").Count==2,"Resuming duplicate legacy records preserves longer text and unions distinct comments");
await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), $"{assertions} assertions passed. Synthetic fixtures only; this is not a live QQ collection.\n");
Console.WriteLine($"SUCCESS: {assertions} assertions");

sealed class SyncProgress : IProgress<ProgressInfo> { public void Report(ProgressInfo p) { } }
sealed class Fake(string mode) : IQzoneApi
{
    public List<int> Offsets = [];
    public int DetailCalls, CommentCalls;
    public Action? OnDetail;
    static JsonObject Post(int i) => new() { ["tid"] = "p" + i, ["uin"] = "10001", ["name"] = "示例本人", ["created_time"] = 1700000000 - i, ["content"] = "短预览…", ["has_more_con"] = 1, ["cmtnum"] = i == 1 ? 101 : 0, ["pictotal"] = i == 1 ? 2 : 0, ["pic"] = new JsonArray() };
    public Task<JsonObject> Get(string endpoint, Dictionary<string,string> p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        JsonObject result;
        if (endpoint == Endpoints.List)
        {
            int offset = int.Parse(p["pos"]); Offsets.Add(offset);
            result = new() { ["code"] = 0, ["total"] = 3, ["msglist"] = offset == 0 ? new JsonArray(Post(1), Post(2)) : mode == "empty-middle" ? new JsonArray() : new JsonArray(Post(3)) };
            if (mode == "edited-list" && offset == 0) result.A("msglist")[1]["content"] = "修改后的列表预览";
        }
        else if (endpoint == Endpoints.Detail)
        {
            DetailCalls++; OnDetail?.Invoke(); ct.ThrowIfCancellationRequested();
            result = new() { ["code"] = 0, ["content"] = "<script>alert('x')</script>" + new string('长', 3200) + "正文结束", ["rt_con"] = new string('转', 1200) + "转发结束", ["has_more_con"] = 0 };
        }
        else if (endpoint == Endpoints.Pictures) result = new() { ["imageUrls"] = new JsonArray("https://m.qpic.cn/one.jpg", "https://m.qpic.cn/two.jpg") };
        else if (endpoint == Endpoints.Comments)
        {
            CommentCalls++;
            if (mode == "bad-comments") throw new IOException("模拟评论接口失败");
            int start = int.Parse(p["start"]); if (mode == "repeat-comments") start = 0;
            var rows = new JsonArray();
            if (p["topicId"].EndsWith("_p1")) for (int i = start; i < Math.Min(start + 100, 101); i++)
            {
                var c = new JsonObject { ["tid"] = "c" + i, ["uin"] = i == 0 ? "10001" : "10002", ["name"] = i == 0 ? "自己" : "朋友", ["content"] = "评论" + i, ["create_time"] = 1700000000 };
                if (i == 0) { c["reply_num"] = mode == "partial-replies" ? 5 : 1; c["list_3"] = new JsonArray(new JsonObject { ["tid"] = "r1", ["uin"] = "10001", ["content"] = "自己回复自己的评论" }); }
                rows.Add(c);
            }
            if (mode == "live-shape")
            {
                foreach (var row in rows)
                {
                    var c = (JsonObject)row!;
                    c["poster"] = new JsonObject { ["id"] = c["uin"]!.DeepClone(), ["name"] = c["name"]!.DeepClone() };
                    c["id"] = c["tid"]!.DeepClone(); c.Remove("tid"); c.Remove("uin"); c.Remove("name");
                    c["postTime"] = c["create_time"]!.DeepClone(); c.Remove("create_time");
                    if (c.S("id") == "c0")
                    {
                        c.Remove("reply_num"); c["extendData"] = new JsonObject { ["replyNum"] = 2 };
                        c["replies"] = new JsonArray(new JsonObject { ["id"] = "r1", ["poster"] = new JsonObject { ["id"] = "10001", ["name"] = "自己" }, ["content"] = "自己回复自己的评论" }, new JsonObject { ["id"] = "r2", ["poster"] = new JsonObject { ["id"] = "10002", ["name"] = "朋友" }, ["content"] = "新的补充回复" });
                    }
                }
                result = start > 100 ? new JsonObject { ["data"] = new JsonObject() } : new JsonObject { ["data"] = new JsonObject { ["comments"] = rows } };
            }
            else result = new() { ["commentlist"] = rows };
        }
        else if (endpoint == Endpoints.Likes) result = new() { ["data"] = new JsonObject { ["is_dolike"] = 1, ["like_uin_info"] = p["begin_uin"] == "0" ? new JsonArray(new JsonObject { ["fuin"] = "10002", ["nick"] = "好友" }) : new JsonArray() } };
        else throw new Exception("Unexpected endpoint");
        return Task.FromResult(result);
    }
    public async Task<string> Download(string url, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder); var name = J.Hash(url) + ".png";
        await File.WriteAllBytesAsync(Path.Combine(folder, name), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZQmcAAAAASUVORK5CYII="), ct);
        return "media/" + name;
    }
}




sealed class ParallelProbe : IQzoneApi
{
    private readonly Fake inner = new("normal"); private int active; public int Maximum;
    public Task<JsonObject> Get(string e, Dictionary<string,string> args, CancellationToken ct) => inner.Get(e,args,ct);
    public async Task<string> Download(string url,string folder,CancellationToken ct) { var count=Interlocked.Increment(ref active); lock(this) Maximum=Math.Max(Maximum,count);try {await Task.Delay(70,ct);return await inner.Download(url,folder,ct);} finally {Interlocked.Decrement(ref active);} }
}
