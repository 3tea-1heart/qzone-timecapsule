using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
namespace QZoneArchive.Core;
public static partial class ArchiveWriter
{
    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
    public static string Text(JsonNode? n)
    {
        if (n == null) return "";
        if (n is JsonValue) return n.S();
        if (n is JsonArray) return string.Concat(n.A().Select(Text));
        return n.First("content", "con", "text", "title", "name");
    }
    private static string Content(JsonNode n) => n.S("content") is { Length: > 0 } s ? s : Text(n.At("conlist"));
    private static string Link(string url, string label)
    {
        if (!Collector.IsUrl(url)) return E(label);
        if (url.StartsWith("//")) url = "https:" + url;
        return $"<a href=\"{E(url)}\" target=\"_blank\" rel=\"noopener noreferrer\">{E(label)}</a>";
    }
    private static string Media(JsonNode n, ArchiveState state)
    {
        var html = new StringBuilder("<div class='media'>");
        foreach (var url in Collector.MediaUrls(n).Distinct())
        {
            if (state.MediaFiles.TryGetValue(url, out var path))
            {
                if (!path.StartsWith("media/") || path.Contains("..") || path.Contains(':')) continue;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".mp4") html.Append($"<video controls preload='metadata' src='{E(path)}'></video>");
                else if (ext is ".mp3" or ".amr") html.Append($"<audio controls preload='none' src='{E(path)}'></audio>");
                else html.Append($"<a href='{E(path)}' target='_blank' rel='noopener'><img loading='lazy' src='{E(path)}' alt='备份图片'></a>");
            }
            else html.Append($"<span class='missing'>{Link(url, "媒体未下载 · 查看原链接")}</span>");
        }
        return html.Append("</div>").ToString();
    }
    private static string Comments(JsonNode? list, ArchiveState state, int depth = 0)
    {
        if (depth > 10) return "<p>更深层回复请查看 archive.json。</p>";
        var html = new StringBuilder();
        foreach (var c in list.A())
        {
            var name = SpaceText.Name(c, state);
            var uin = Collector.AuthorId(c);
            var author = uin == state.Target ? " <span class='badge'>空间主人</span>" : "";
            var recipient = SpaceText.ReplyTo(c);
            html.Append($"<div class='comment'>{SpaceText.Avatar(uin, state)}<b>{SpaceText.Html(name == "" ? uin : name, state)}</b>{author}{(recipient.Length > 0 ? "<span class='reply-to'>回复 " + E(recipient) + "</span>" : "")}<time>{E(J.Date(c))}</time><div class='body'>{SpaceText.Html(Content(c), state)}</div>");
            var own = c.DeepClone() as JsonObject ?? new JsonObject(); foreach (var key in new[] { "list_3", "replys", "replies", "archive_replies" }) own.Remove(key);
            html.Append(Media(own, state));
            html.Append(Comments(new JsonArray(Collector.Replies(c).Select(n => n.DeepClone()).ToArray()), state, depth + 1)).Append("</div>");
        }
        return html.ToString();
    }
    public static async Task Write(ArchiveState state, string folder, bool includeJsonLink = true, bool includeMarkdown = true)
    {
        Directory.CreateDirectory(folder);
        await J.Save(Path.Combine(folder, "report.json"), new { state.Target, state.Scope, state.Status, state.Expected, state.VisibleTotal, Saved = state.Posts.Count, state.ListFinished, state.Updated, state.Issues, Note = "数量一致仅表示接口可见范围内对齐；已删除、无权限或服务端未返回的内容无法保证。" });
        var html = new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; img-src 'self' file: blob:; media-src 'self' file:; style-src 'unsafe-inline'; script-src 'unsafe-inline'\"><title>时光留存 · 离线空间</title><style>");
        html.Append(Css).Append(BlogCss).Append("</style><body><aside><div class='logo'>时光留存<span>TIME CAPSULE / LOCAL ARCHIVE</span></div><h1>把那些日子，<br>留在这里。</h1><p>一份可以离线阅读的 QQ 空间记忆。</p><nav><a href='#posts'>说说时间线</a><a href='#extras'>留言与分享</a><a href='#report'>完整性核对</a></nav><small>本页不加载远程图片或统计脚本。<br>时间显示为北京时间 UTC+8。</small></aside><main><header><span class='eyebrow'>YOUR MEMORIES, PRESERVED.</span>");
        html.Append($"<h2>{E(state.Target)} 的空间档案</h2><p>{E(state.Status)} · 保存于 {E(state.Updated)}</p><div class='stats'><strong>{state.Posts.Count}<small>已保存说说</small></strong><strong>{state.Posts.Sum(p => Collector.AllComments(p.At("archive_comments")).Count())}<small>评论与回复</small></strong><strong>{state.MediaFiles.Count}<small>本地媒体</small></strong><strong>{state.Issues.Count}<small>待核对项</small></strong></div></header>");
        html.Append("<section id='posts'><div class='toolbar'><h2>说说时间线</h2><input id='search' placeholder='搜索正文、评论或日期…' aria-label='搜索'><select id='year' aria-label='年份'><option value=''>所有年份</option></select></div><p id='result' class='muted'></p><div id='timeline'>");
        var md = new StringBuilder($"# {state.Target} 的 QQ 空间备份\n\n{state.Status}\n\n");
        foreach (var p in state.Posts)
        {
            string date = J.Date(p), content = Content(p), id = p.S("tid");
            html.Append($"<article id='post-{J.Hash(ArchiveLibrary.RecordKey(p))[..16]}' data-id='{E(ArchiveLibrary.RecordKey(p))}' data-date='{E(date.Length >= 10 ? date[..10] : "")}' data-year='{E(date.Length >= 4 ? date[..4] : "未知")}'><div class='post-head'>{SpaceText.Avatar(Collector.AuthorId(p), state)}<div><b>{SpaceText.Html(Collector.AuthorName(p) is { Length: > 0 } displayName ? displayName : Collector.AuthorId(p), state)}</b><time>{E(date)}</time></div>{Link($"https://user.qzone.qq.com/{state.Target}/mood/{id}", "查看原说说 ↗")}</div><div class='body'>{SpaceText.Html(content, state)}</div>");
            foreach (var version in p.A("archive_text_versions")) html.Append($"<details><summary>同一说说的另一正文版本</summary><div class='body'>{SpaceText.Html(version.S(), state)}</div></details>");
            var rt = Text(p.At("rt_con")); if (rt == "") rt = Text(p.At("rt_conlist"));
            if (rt.Length > 0 || p.S("rt_tid") != "") html.Append($"<blockquote><small>转发自 {SpaceText.Html(p.First("rt_uinname", "rt_uname", "rt_uin"), state)}</small><div class='body'>{SpaceText.Html(rt, state)}</div></blockquote>");
            foreach (var item in p.A("urlinfo")) html.Append($"<p>{Link(item.First("url", "url1"), item.First("title", "desc", "url"))}</p>");
            foreach (var item in p.A("url")) html.Append($"<blockquote>{Link(item.First("url", "url1"), item.First("title", "desc", "url"))}<div class='body'>{E(item.First("desc", "summary"))}</div></blockquote>");
            if (Collector.IsUrl(p.S("url"))) html.Append($"<p>{Link(p.S("url"), "分享链接")}</p>");
            var own = (JsonObject)p.DeepClone(); foreach (var key in new[] { "archive_comments", "commentlist", "archive_detail_raw" }) own.Remove(key);
            html.Append(Media(own, state));
            var views = p.First("readCount", "view_num", "viewnum", "readcount");
            html.Append($"<p class='muted'>点赞 {E(p.First("likenum", "likeTotal") is { Length: > 0 } l ? l : "未返回")} · 浏览量 {E(views == "" ? "接口未返回" : views)}</p>");
            var likes = p.A("archive_likes");
            if (likes.Count > 0) html.Append($"<details><summary>已取得的点赞者 · {likes.Count}</summary><p>{E(string.Join("、", likes.Select(n => n.First("nick", "nickname", "fuin", "uin"))))}</p></details>");
            if (p.At("archive_export_fields").S("comments") == "false") html.Append("<p class='muted'>本次未选择导出评论。</p>");
            else html.Append($"<details open><summary>评论与回复 · {Collector.AllComments(p.At("archive_comments")).Count()}</summary>{(p.At("archive_comments_requested").S() == "false" ? "<p class='muted'>本次未请求完整评论；下方为已有记录。</p>" : "")}{Comments(p.At("archive_comments"), state)}</details>");
            if (p.I("archive_detail_done") != 1) html.Append("<p class='notice'>这条说说的全文或评论尚未完成核对，可重新补采。</p>");
            foreach (var issue in state.Issues.Where(i => i.Id == id)) html.Append($"<p class='notice'>{E(issue.Scope)}：{E(issue.Message)}</p>");
            html.Append("</article>");
            md.Append($"## {date}\n\n{content}\n\n"); if (rt != "") md.Append($"转发：{rt}\n\n");
            foreach (var c in Collector.AllComments(p.At("archive_comments"))) md.Append($"- {c.First("name", "nick", "uin")} · {J.Date(c)}：{Content(c)}\n");
            md.AppendLine();
        }
        html.Append("</div><button id='more'>显示更多</button></section>");
        html.Append(Gallery(state));
        html.Append("<section id='extras'><h2>留言与独立分享</h2><p>附加模块保留接口返回内容；独立分享页链接指向的外部文章不在离线备份范围内。</p>");
        foreach (var (label, list) in new[] { ("留言", state.Boards), ("分享", state.Shares) })
            foreach (var n in list)
                html.Append($"<article><span class='eyebrow'>{label}</span><h3>{E(n.First("title", "nickname", "name"))}</h3><time>{E(J.Date(n))}</time><div class='body'>{E(n.First("content", "htmlContent", "summary", "desc", "abstract"))}</div>{Link(n.First("url", "share_url"), "原始分享链接")}{Media(n, state)}<details><summary>查看完整原始记录</summary><pre>{E(n.ToJsonString(J.Pretty))}</pre></details></article>");
        html.Append($"</section><section id='report'><h2>完整性核对</h2><p>{(state.VisibleTotal >= 0 ? "本轮范围内选中数" : "接口报告说说数")}：{state.Expected} · 累计保存：{state.Posts.Count} · 本轮列表核对：{(state.ListFinished ? "已对齐" : "未完成")}</p><p>删除、权限变化、历史接口停用可能造成无法访问。条数一致不代表能恢复全部历史。已下载的文件可离线读取，未成功的媒体保留原链接。</p><ul>");
        foreach (var issue in state.Issues) html.Append($"<li><b>{E(issue.Scope)} / {E(issue.Id)}</b>：{E(issue.Message)}</li>");
        html.Append("</ul><p><a href='report.json'>核对报告</a>");
        if (includeJsonLink) html.Append(" · <a href='archive.json'>JSON 数据</a>");
        if (includeMarkdown) html.Append(" · <a href='说说.md'>Markdown 文本</a>");
        html.Append("</p></section><footer>时光留存 · 你的记忆，保存在你自己的电脑里。</footer></main><dialog id='lightbox'><button id='close-lightbox' aria-label='关闭大图'>关闭 ×</button><img id='lightbox-image' alt='查看原图'><p id='lightbox-caption'></p></dialog><script>").Append(Script).Append("</script></body></html>");
        var output = html.ToString().Replace("</script></body>", BlogScript + "</script></body>").Replace("<body>", "<body>" + BlogHero(state)).Replace("<nav><a href='#posts'>说说时间线</a>", "<nav><a href='index.html'>说说时间线</a><a href='gallery.html'>媒体 Gallery</a>").Replace("</style>", ExtraCss + BlogCss + "</style>").Replace("<p id='result'", "<div class='datebar'><label>开始日期 <input type='date' id='from'></label><label>结束日期 <input type='date' id='through'></label><button id='reset'>清空筛选</button></div><p id='result'");
        await File.WriteAllTextAsync(Path.Combine(folder, "index.html"), output, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(folder, "gallery.html"), output.Replace("<body>", "<body class='gallery-only'>").Replace("href='#extras'", "href='index.html#extras'").Replace("href='#report'", "href='index.html#report'"), new UTF8Encoding(false));
        if (includeMarkdown) await File.WriteAllTextAsync(Path.Combine(folder, "说说.md"), md.ToString(), new UTF8Encoding(false));
    }
    private static string Gallery(ArchiveState state)
    {
        var h = new StringBuilder("<section id='gallery'><div class='toolbar'><div><span class='eyebrow'>A VISUAL DIARY</span><h2>记忆画廊</h2></div><p class='muted'>点击图片查看原图 · 相同本地文件只展示一次</p></div><div class='gallery-grid'>");
        var items = new Dictionary<string, (JsonObject Post, List<string> Ids)>();
        foreach (var p in state.Posts) foreach (var url in Collector.MediaUrls(p).Distinct())
        {
            if (!state.MediaFiles.TryGetValue(url, out var path) || !path.StartsWith("media/") || path.Contains("..") || path.Contains(':')) continue;
            if (!items.ContainsKey(path)) items[path] = (p, []);
            items[path].Ids.Add(ArchiveLibrary.RecordKey(p));
        }
        foreach (var (path, item) in items)
        {
            var p = item.Post; var caption = SpaceText.Plain(ArchiveLibrary.Body(p)); if (caption.Length > 110) caption = caption[..110] + "…";
            h.Append($"<figure class='gallery-item' data-posts='{E(System.Text.Json.JsonSerializer.Serialize(item.Ids.Distinct()))}'><div class='gallery-visual'>");
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".mp4") h.Append($"<video controls preload='metadata' src='{E(path)}'></video>");
            else if (ext is ".mp3" or ".amr") h.Append($"<audio controls preload='none' src='{E(path)}'></audio>");
            else h.Append($"<button class='image-open' data-src='{E(path)}' data-caption='{E(J.Date(p) + " · " + caption)}'><img loading='lazy' src='{E(path)}' alt='{E(caption)}'></button>");
            h.Append($"</div><figcaption><time>{E(J.Date(p))}</time><p>{E(caption)}</p><a href='index.html#post-{J.Hash(ArchiveLibrary.RecordKey(p))[..16]}'>回到这条说说 ↗</a></figcaption></figure>");
        }
        if (items.Count == 0) h.Append("<div class='empty-gallery'>还没有本地媒体。导入档案时请保留相邻的 media 文件夹，或先完成图片下载。</div>");
        return h.Append("</div><p id='gallery-result' class='muted'></p><button id='gallery-more'>显示更多媒体</button></section>").ToString();
    }
    private const string ExtraCss = """
        :root{--ink:#253c38;--muted:#7c847b;--green:#396655}body{background:#f4f1e9}aside{background:#1f3f37;color:#f0eee1}aside .logo,aside nav a{color:#f0eee1}aside p,aside small{color:#b4c4b5}aside h1{font-size:35px;letter-spacing:1px}main{max-width:1540px}header{padding:22px 0 32px}.stats{gap:0}.stats strong{padding:0 36px;border-right:1px solid #dddccf}.stats strong:first-child{padding-left:0}.stats strong:last-child{border:0}article{box-shadow:0 8px 30px #263d3605;border-radius:16px;padding:32px}h2{letter-spacing:-.5px}.toolbar input{background:#fffefa}.datebar{display:flex;gap:15px;align-items:end;flex-wrap:wrap}.datebar label{font-size:12px;color:var(--muted)}.datebar input{display:block;width:170px;margin-top:5px}.datebar button{background:transparent;color:var(--green)}#gallery{margin:45px 0}.gallery-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:18px}.gallery-item{margin:0;background:#fffdf8;border:1px solid #e3e2d7;border-radius:12px;overflow:hidden}.gallery-visual{background:#e7e7da;min-height:80px}.gallery-visual img{display:block;width:100%;height:250px;object-fit:cover;transition:transform .2s}.gallery-visual img:hover{transform:scale(1.035)}.gallery-visual video{display:block;width:100%;height:250px}.gallery-visual audio{max-width:100%;padding:20px}.image-open{padding:0;border:0;border-radius:0;display:block;width:100%;background:none;overflow:hidden}figcaption{padding:17px}figcaption p{font-size:13px;line-height:1.7;margin:8px 0}figcaption a{font-size:11px}.empty-gallery{grid-column:1/-1;border:1px dashed #bac7b4;padding:40px;border-radius:12px;color:#71806f}.gallery-only #timeline,.gallery-only #more,.gallery-only #extras,.gallery-only #report,.gallery-only #result{display:none}.gallery-only #gallery{margin-top:20px}.gallery-only #posts>.toolbar h2{font-size:0}.gallery-only #posts>.toolbar h2:after{content:'搜索画廊';font-size:25px}dialog{border:0;border-radius:16px;background:#f9f8f1;padding:20px;max-width:94vw;max-height:94vh}dialog::backdrop{background:#172c26dc}#lightbox-image{display:block;max-width:86vw;max-height:74vh;object-fit:contain;margin-top:15px}#close-lightbox{display:block;margin-left:auto}#lightbox-caption{max-width:80vw;font-size:13px}.gallery-item[hidden]{display:none}@media(max-width:850px){.stats strong{padding:0 15px}.gallery-grid{grid-template-columns:repeat(2,minmax(0,1fr))}.gallery-visual img,.gallery-visual video{height:180px}.datebar input{width:145px}}@media print{aside,.toolbar,.datebar,button{display:none}main{margin:0;padding:0}article{break-inside:avoid;box-shadow:none}details{display:block}}
        """;
    private const string Css = """
        :root{color-scheme:light;--ink:#263d36;--muted:#708179;--line:#e2e7df;--green:#246b53}*{box-sizing:border-box}body{margin:0;background:#f5f4ee;color:var(--ink);font:15px/1.75 'Segoe UI','Microsoft YaHei',sans-serif}aside{position:fixed;inset:0 auto 0 0;width:285px;padding:42px 30px;background:#e7ece0;display:flex;flex-direction:column}.logo{font-size:22px;font-weight:700;letter-spacing:3px}.logo span{display:block;font-size:9px;letter-spacing:2px;margin-top:6px}aside h1{font:32px/1.6 Georgia,'SimSun',serif;margin-top:65px}aside p,small,.muted{color:var(--muted)}nav{display:grid;gap:12px;margin:32px 0}nav a{text-decoration:none;color:var(--ink);padding:10px 0}aside small{margin-top:auto}main{margin-left:285px;padding:45px 6%;max-width:1450px}header{border-bottom:1px solid var(--line);padding-bottom:30px}.eyebrow{font-size:10px;letter-spacing:3px;color:var(--green)}h2{font-size:25px;margin:10px 0}header p{color:var(--muted)}.stats{display:flex;gap:50px;margin-top:30px}.stats strong{font-size:32px;font-weight:500}.stats small{display:block;font-size:12px;font-weight:400}.toolbar{display:flex;align-items:center;flex-wrap:wrap;gap:15px;margin:32px 0 8px}.toolbar h2{margin-right:auto}input,select,button{font:inherit;border:1px solid #d8e0d5;border-radius:8px;background:#fff;padding:10px 14px;color:var(--ink)}input{width:280px}button{cursor:pointer;background:var(--green);color:white;padding:10px 24px}article{background:#fffefa;border:1px solid var(--line);border-radius:14px;padding:28px;margin:22px 0;overflow-wrap:anywhere}.post-head{display:flex;align-items:center;gap:12px;margin-bottom:20px}.post-head>a{margin-left:auto;font-size:12px}.avatar{background:#e8eee4;border-radius:50%;width:40px;height:40px;display:grid;place-items:center}time{display:block;font-size:12px;color:var(--muted)}.body{white-space:pre-wrap;line-height:1.95}blockquote{border-left:3px solid #95ac8d;margin:22px 0;padding:14px 20px;background:#f3f5ef}.media{display:flex;flex-wrap:wrap;gap:8px;margin:16px 0}.media img{width:180px;height:180px;object-fit:cover;border-radius:8px}.media video{max-width:100%;max-height:400px}.media:empty{display:none}.missing{font-size:12px;padding:5px;background:#f9f1e6}a{color:var(--green)}summary{cursor:pointer;color:var(--green);padding:9px 0}.comment{border-left:2px solid #e3eade;padding:12px 0 12px 16px;margin:8px 0;background:#fafbf7}.comment .comment{margin-left:8px}.badge{font-size:10px;color:var(--green);background:#e5efdf;padding:3px 6px}.notice{background:#fff3dc;color:#916526;border-radius:6px;padding:10px;font-size:12px}pre{white-space:pre-wrap;font-size:12px}section{scroll-margin-top:20px}#report{background:#eaf0e4;padding:25px;margin-top:30px;border-radius:14px}footer{padding:35px 0;color:var(--muted);font-size:12px}#more{margin-bottom:25px}@media(max-width:850px){aside{position:relative;width:auto;padding:25px}aside h1{margin:20px 0;font-size:26px}aside nav,aside small{display:none}main{margin:0;padding:24px}.stats{gap:25px}.media img{width:130px;height:130px}input{width:100%}}
        """;
    private const string Script = """
        const cards=[...document.querySelectorAll('#timeline article')],search=document.getElementById('search'),year=document.getElementById('year'),from=document.getElementById('from'),through=document.getElementById('through'),gallery=[...document.querySelectorAll('.gallery-item')];let limit=30,glimit=40;for(const y of [...new Set(cards.map(c=>c.dataset.year))].sort().reverse()){const o=document.createElement('option');o.value=y;o.textContent=y;year.append(o)}function render(){const terms=search.value.toLowerCase().split(/\s+/).filter(Boolean);let matches=0;const ids=new Set();for(const c of cards){const ok=(!year.value||c.dataset.year===year.value)&&(!from.value||c.dataset.date>=from.value)&&(!through.value||c.dataset.date&&c.dataset.date<=through.value)&&terms.every(q=>c.textContent.toLowerCase().includes(q));c.hidden=!(ok&&matches<limit);if(ok){matches++;ids.add(c.dataset.id)}}document.getElementById('result').textContent=`找到 ${matches} 条 · 显示前 ${Math.min(limit,matches)} 条`;document.getElementById('more').hidden=matches<=limit;let gm=0;for(const g of gallery){const ok=JSON.parse(g.dataset.posts).some(id=>ids.has(id));g.hidden=!(ok&&gm<glimit);if(ok)gm++}document.getElementById('gallery-result').textContent=`${gm} 个媒体 · 已显示 ${Math.min(gm,glimit)} 个`;document.getElementById('gallery-more').hidden=gm<=glimit}for(const el of [search,year,from,through])el.addEventListener('input',()=>{limit=30;glimit=40;render()});document.getElementById('reset').onclick=()=>{for(const el of [search,year,from,through])el.value='';limit=30;glimit=40;render()};document.getElementById('more').onclick=()=>{limit+=30;render()};document.getElementById('gallery-more').onclick=()=>{glimit+=40;render()};const dialog=document.getElementById('lightbox');for(const b of document.querySelectorAll('.image-open'))b.onclick=()=>{document.getElementById('lightbox-image').src=b.dataset.src;document.getElementById('lightbox-caption').textContent=b.dataset.caption;dialog.showModal()};document.getElementById('close-lightbox').onclick=()=>dialog.close();dialog.onclick=e=>{if(e.target===dialog)dialog.close()};render();if(location.hash.startsWith('#post-')){const target=document.getElementById(location.hash.slice(1));if(target){target.hidden=false;target.scrollIntoView()}}
        """;
}
