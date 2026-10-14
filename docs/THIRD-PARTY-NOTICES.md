# 第三方说明

本工具显示名称为“时光留存”，独立编写的 C# / WPF 桌面程序，不是 QzoneArchive 的官方版本。

QQ 空间接口地址、请求参数及若干兼容行为参考 ShunCai/QZoneExport：

- https://github.com/ShunCai/QZoneExport
- 参考提交：7e0218fab29316ca28b99a7c63deb0627002d271
- 参考位置：core/qzone-api/clients.ts、urls.ts、core/collector/modules/messages.ts、helpers.ts、boards.ts。
- 许可证：Apache License 2.0，完整文本随附 QZoneExport-APACHE-2.0.txt。
- 本工具重新实现了 Windows 界面、本地保存、全文补全、完整性报告、分页及媒体下载逻辑；保留相关来源署名。

QzoneArchive 仅用于调研用户报告与公开停止维护公告，未纳入其历史 GPL 源码或二进制。

构建依赖：.NET 8 / Windows Desktop Runtime（Microsoft，MIT）；Microsoft.Web.WebView2 1.0.3537.50（Microsoft，包内许可）。WebView2 Runtime 为 Microsoft Edge 组件，不随本 EXE 提供。相关许可可在依赖包或官方项目中查阅。

本程序不隶属于腾讯、QQ 或上述开源项目，也没有官方接口稳定性保证。
