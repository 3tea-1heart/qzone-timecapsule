# 时光留存 · Qzone TimeCapsule

把 QQ 空间保存成可以翻阅、检索，也便于交给 AI 分析的个人档案。Windows 桌面程序，C# / WPF / WebView2，手动扫码登录。

[下载 Windows 版本](https://github.com/3tea-1heart/qzone-timecapsule/releases) · [使用说明](docs/使用说明.md) · [1.2.1 更新说明](docs/更新说明-1.2.1.md)

## 功能

- 分别获取说说详情和分页评论，针对长正文截断、本人评论和回复缺失进行处理；保留时间及转发内容。
- 选择日期范围、最近 N 条、指定说说，或接着上次的档案补采。
- 智能续传核对列表变化，复用已完成的内容；可选深度复核正文和评论。
- 图片、音视频、评论、头像、QQ 表情分别选择，附件支持并发下载。
- 说说、评论按标识合并；附件按文件内容摘要去重。保留不同标识的同文说说和有差异的历史候选版本。
- 导入本工具 JSON 档案并合并，按日期、字段导出 TXT / HTML / JSON。
- 离线博客式阅读页、搜索、Gallery、大图查看、主题和排版选项；支持 emoji、昵称和 @ 提及。
- TXT 按篇组织时间、正文、评论及附件说明，适合 AI 分析；图片分析需要另行提供图片。

## 开始使用

1. 从 Releases 下载 Windows x64 ZIP，解压后启动 `TimeCapsule.exe`。
2. 在官方 QQ 空间登录页手动扫码，选择保存位置和备份范围。
3. 点击开始。再次备份时选择原目录，使用“智能续传 / 核对变化”。
4. 使用档案页导入 `archive.json`，或打开输出的 `index.html` 阅读。移动备份时一起保留 `media` 文件夹。

需要 Windows x64 和 Microsoft Edge WebView2 Runtime。发行包包含 .NET 运行时。程序不会要求你提供 QQ 密码；登录浏览器可能保留会话，归档和浏览器数据应妥善保管。

## 范围与完整性

仅能导出当前登录账号有权限访问、且接口仍可返回的内容。可以指定有权访问的其他空间。不能绕过权限或恢复已删除内容，也不保证整个历史账号绝对完整；未完成项会记录供重试。

列表摘要无法反映所有旧评论编辑，严格复查时请启用深度复核。头像是下载时的当前头像。独立日志、独立相册、独立分享页及 QQ 聊天记录导出尚未实现。

公开测试使用合成数据。长正文和本人评论的真实样例曾在本机验证，但不代表完整账号采集。私人档案、会话和真实测试材料不包含在仓库中。

## 构建与测试

Windows 安装 .NET 8 SDK 后：

```powershell
dotnet run --project tests/TimeCapsule.Tests -c Release -- artifacts/tests/fresh-run
powershell -ExecutionPolicy Bypass -File build.ps1
```

每次测试使用新目录，因为测试包含续传场景。产物位于 `release/`，版本取自项目文件。UI 自检：`TimeCapsule.exe --self-check 输出目录 --portable`。

GitHub Actions 对推送和 PR 运行测试与构建；推送与项目版本一致的版本标签后发布 Windows ZIP 和 SHA-256。见 [发布流程](docs/发布流程.md)。

## 参考与许可

独立实现，非腾讯官方产品。参考项目和依赖见 [第三方说明](docs/THIRD-PARTY-NOTICES.md)。本仓库尚未声明项目整体的开源授权许可证；第三方组件遵循各自许可。公开源码不等于授予任意再分发许可。
