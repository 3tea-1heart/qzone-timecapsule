using System.Text.Json.Nodes;
namespace QZoneArchive.Core;
public static class Demo
{
    public static ArchiveState Create() => new()
    {
        Target = "10000001", Expected = 3, ListFinished = true, Status = "示例档案 · 虚构内容，仅供预览", Updated = "2026-10-13T09:00:00+08:00",
        Posts = [
            new JsonObject { ["tid"] = "demo-3", ["uin"] = "10000001", ["name"] = "示例用户", ["created_time"] = 1726315200, ["content"] = "今天翻到了很久以前的照片。📷✨\n和 @{uin:10000002,nick:老朋友,who:1} 一起重看那年的夏天。\n\n那时候以为日子很长，后来才发现，那些一起等过的车、走过的街、聊到半夜的话，才是最想留下来的东西。\n\n这是虚构的示例说说。你导出的长正文会在这里完整显示，不会人为裁成前几百字。", ["likenum"] = 12, ["readCount"] = 108, ["archive_detail_done"] = 1,
                ["archive_comments"] = new JsonArray(new JsonObject { ["id"] = "c1", ["uin"] = "10000001", ["name"] = "示例用户", ["content"] = "给自己补一句：记得把原图也存下来。", ["create_time"] = 1726315260,
                    ["list_3"] = new JsonArray(new JsonObject { ["id"] = "r1", ["uin"] = "10000002", ["name"] = "老朋友", ["content"] = "我们下次再去一次。🥰", ["create_time"] = 1726315360 }) }) },
            new JsonObject { ["tid"] = "demo-2", ["name"] = "示例用户", ["created_time"] = 1536926400, ["content"] = "分享一段当时喜欢的话。", ["rt_uname"] = "文字摘录（虚构）", ["rt_tid"] = "forward-demo", ["rt_con"] = "有些回忆不用每天想起，但希望想起的时候，它们还在那里。", ["archive_detail_done"] = 1, ["archive_comments"] = new JsonArray() },
            new JsonObject { ["tid"] = "demo-1", ["name"] = "示例用户", ["created_time"] = 1379160000, ["content"] = "放学后的操场。风很轻，天还没有黑。\n——这是一条虚构的旧日记忆。", ["archive_detail_done"] = 1, ["archive_comments"] = new JsonArray() }
        ]
    };
}
