using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using QZoneArchive.Core;

namespace QZoneArchive;
public partial class MainWindow : Window
{
    private LoginSession? session;
    private Collector? collector;
    private CancellationTokenSource? cancellation;
    private bool running;
    private bool closeAfterSave;
    private string? lastFolder;
    private readonly string profile;
    public MainWindow()
    {
        InitializeComponent(); UpdateScopeControls();
        var args = Environment.GetCommandLineArgs();
        var portable = args.Contains("--portable");
        var root = portable ? Path.Combine(AppContext.BaseDirectory, "UserData") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeCapsule");
        profile = Path.Combine(root, "Browser");
        FolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QQ空间备份");
        if (!args.Contains("--self-check")) RestorePreferences();
        var backupRoot = args.SkipWhile(a => a != "--backup-root").Skip(1).FirstOrDefault(); if (backupRoot != null) FolderBox.Text = backupRoot;
        var verifyPosts = args.SkipWhile(a => a != "--verify-posts").Skip(1).FirstOrDefault(); if (verifyPosts != null) { VerifyBox.Text = verifyPosts; VerifyExpander.IsExpanded = true; }
        Log("欢迎使用时光留存。请先扫码登录，或点击左侧预览示例档案。");
        Closing += (_, e) => { if (running) { e.Cancel = true; closeAfterSave = true; cancellation?.Cancel(); Log("正在停止并保存，完成后会自动关闭。"); } };
        Loaded += async (_, _) =>
        {
            if (rememberedArchive != null && File.Exists(rememberedArchive)) { try { library = await ArchiveLibrary.Load(rememberedArchive); RefreshLibrary(); Log("已加载上次档案；开始后将复用已有进度，并核对所选范围的变化。"); } catch (Exception ex) { Log("上次档案未加载：" + ex.Message); } }
            if (args.Contains("--self-check"))
            {
                var output = args.SkipWhile(a => a != "--self-check").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "self-check");
                try
                {
                    Directory.CreateDirectory(output);
                    var demo = await CreateDemoArchive(Path.Combine(output, "demo"));
                    ApplyProgress(new("示例预览 · 未读取真实账号", 3, 3, 2, 0, 0, "自检：已生成示例档案、长正文与本人评论。"));
                    await Task.Delay(400); UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(output, "gui.png"))) encoder.Save(stream);
                    library = new(demo, Path.Combine(output, "demo")); WorkspaceTabs.SelectedIndex = 1; RefreshLibrary(); ArchiveList.SelectedIndex = 0;
                    await Task.Delay(300); UpdateLayout();
                    var libraryBitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); libraryBitmap.Render(this);
                    var libraryEncoder = new PngBitmapEncoder(); libraryEncoder.Frames.Add(BitmapFrame.Create(libraryBitmap)); using (var stream = File.Create(Path.Combine(output, "library.png"))) libraryEncoder.Save(stream);
                    string runtime; try { runtime = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch (Exception ex) { runtime = "MISSING: " + ex.Message; }
                    await File.WriteAllTextAsync(Path.Combine(output, "self-check.txt"), "WPF window rendered\nDemo archive generated\nWebView2 runtime: " + runtime);
                }
                catch (Exception ex) { Directory.CreateDirectory(output); await File.WriteAllTextAsync(Path.Combine(output, "self-check-error.txt"), ex.ToString()); }
                Close();
            }
        };
    }
    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(message)); return; }
        if (LogBox.Text.Length > 40000) LogBox.Text = LogBox.Text[^20000..];
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n"); LogBox.ScrollToEnd();
        if (lastFolder != null) { try { Directory.CreateDirectory(lastFolder); File.AppendAllText(Path.Combine(lastFolder, "run.log"), $"[{DateTime.Now:HH:mm:ss}] {message}\n"); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private void Login_Click(object sender, RoutedEventArgs e)
    {
        var login = new LoginWindow(profile) { Owner = this };
        if (login.ShowDialog() == true) { session = login.Session; AccountLabel.Text = "已连接 QQ " + session!.Owner; Log("已连接账号 " + session.Owner + "。可以开始备份。"); }
    }
    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        try { LogoutButton.IsEnabled = false; await LoginWindow.ClearProfile(profile); session = null; AccountLabel.Text = "已退出登录"; Log("已清除工具独立浏览器的登录会话。"); }
        catch (Exception ex) { MessageBox.Show(this, "清除会话失败：" + ex.Message); }
        finally { LogoutButton.IsEnabled = true; }
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 QQ 空间备份保存位置", Multiselect = false };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (running) return;
        if (session == null) { MessageBox.Show(this, "请先点击左侧“打开扫码登录窗口”，登录并连接账号。", "先连接空间"); return; }
        var target = TargetBox.Text.Trim(); if (target == "") target = session.Owner;
        if (!Regex.IsMatch(target, "^[1-9][0-9]{4,12}$")) { MessageBox.Show(this, "请输入有效的 QQ 号，或留空导出自己。"); return; }
        if (string.IsNullOrWhiteSpace(FolderBox.Text)) { MessageBox.Show(this, "请选择保存位置。"); return; }
        try
        {
            lastFolder = Path.Combine(Path.GetFullPath(FolderBox.Text.Trim()), target);
            string[]? postIds = null;
            if (!string.IsNullOrWhiteSpace(VerifyBox.Text))
            {
                var ids = new List<string>();
                foreach (var part in Regex.Split(VerifyBox.Text.Trim(), @"\s+"))
                {
                    string id = part;
                    if (part.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        var uri = new Uri(part);
                        if (uri.Host != "user.qzone.qq.com") throw new InvalidDataException("请粘贴 user.qzone.qq.com 上的说说详情链接。");
                        var match = Regex.Match(uri.AbsolutePath, @"^/(\d+)/mood/([a-zA-Z0-9_-]+)");
                        if (!match.Success || match.Groups[1].Value != target) throw new InvalidDataException("说说链接中的 QQ 号需与目标账号一致，路径应包含 /mood/。");
                        id = match.Groups[2].Value;
                    }
                    if (!Regex.IsMatch(id, @"^[a-zA-Z0-9_-]{4,128}$")) throw new InvalidDataException("无法识别说说 ID。请使用说说详情链接，或输入完整 ID。");
                    ids.Add(id);
                }
                postIds = ids.Distinct().ToArray();
                lastFolder = Path.Combine(lastFolder, "验证-" + J.Hash(string.Join(",", postIds))[..10]);
            }
            var delay = SpeedBox.SelectedIndex switch { 0 => 1800, 2 => 5000, 3 => 800, _ => 3000 };
            int limit = 0; DateTime? from = null, through = null; bool topUp = ScopeBox.SelectedIndex == 3;
            if (topUp && library != null && library.State.Target == target) lastFolder = library.Folder;
            if (ScopeBox.SelectedIndex == 1 && (!int.TryParse(LimitBox.Text, out limit) || limit < 1)) throw new InvalidDataException("请输入大于零的备份条数。");
            if (ScopeBox.SelectedIndex == 2) { from = BackupFrom.SelectedDate; through = BackupThrough.SelectedDate; if (from == null && through == null) throw new InvalidDataException("请选择至少一个日期边界。"); if (from > through) throw new InvalidDataException("开始日期不能晚于结束日期。"); }
            if (postIds != null && ScopeBox.SelectedIndex != 0) throw new InvalidDataException("指定说说 ID 时，请将范围设为完整备份；ID 本身已限定范围。");
            if (limit > 0 || from != null || through != null) lastFolder = Path.Combine(lastFolder, "范围-" + J.Hash(limit + "|" + from?.ToString("yyyyMMdd") + "|" + through?.ToString("yyyyMMdd"))[..10]);
            var options = new ExportOptions(session.Owner, target, lastFolder, MediaCheck.IsChecked == true, LikesCheck.IsChecked == true, BoardsCheck.IsChecked == true, SharesCheck.IsChecked == true, RefreshCheck.IsChecked == true, delay, postIds, Videos: VideoCheck.IsChecked == true, Comments: CollectComments.IsChecked == true, Avatars: AvatarCheck.IsChecked == true, Emoji: EmojiCheck.IsChecked == true, From: from, Through: through, MaxPosts: limit, TopUp: topUp, DownloadWorkers: WorkersBox.SelectedIndex switch { 0 => 1, 2 => 8, _ => 4 });
            await SavePreferences(); cancellation = new(); SetRunning(true);
            using var api = new QzoneApi(session.Cookies, session.Skey, session.Token, delay, Log);
            collector = new(api, options, new Progress<ProgressInfo>(ApplyProgress));
            Log("开始采集。目录：" + lastFolder);
            await Task.Run(() => collector.Run(cancellation.Token));
            library = await ArchiveLibrary.Load(Path.Combine(lastFolder, "archive.json")); RefreshLibrary();
            Log("本轮结束。请查看核对报告，存在待核对项时可稍后再次补采。");
        }
        catch (Exception ex) { Log("无法执行：" + ex.Message); MessageBox.Show(this, ex.Message, "备份提示"); }
        finally { SetRunning(false); collector = null; cancellation?.Dispose(); cancellation = null; if (closeAfterSave) Close(); }
    }
    private void SetRunning(bool value)
    {
        running = value; OptionsPanel.IsEnabled = !value; StartButton.IsEnabled = !value; LoginButton.IsEnabled = !value; LogoutButton.IsEnabled = !value; PauseButton.IsEnabled = value; StopButton.IsEnabled = value; ProgressBar.IsIndeterminate = value; PauseButton.Content = "暂停";
    }
    private void ApplyProgress(ProgressInfo p)
    {
        PostsCount.Text = p.Posts.ToString(); CommentsCount.Text = p.Comments.ToString(); MediaCount.Text = p.Media.ToString(); IssuesCount.Text = p.Issues.ToString(); StageLabel.Text = p.Stage; Log(p.Message);
    }
    private void Pause_Click(object sender, RoutedEventArgs e) { if (collector == null) return; collector.Paused = !collector.Paused; PauseButton.Content = collector.Paused ? "继续" : "暂停"; Log(collector.Paused ? "将在当前请求完成后暂停。" : "继续采集。"); }
    private void Stop_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); StopButton.IsEnabled = false; Log("正在停止并保存已有内容…"); }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = TargetBox.Text.Trim(); if (target == "") target = session?.Owner ?? "";
            var folder = lastFolder ?? Path.Combine(FolderBox.Text, target);
            var path = Path.Combine(folder, "index.html");
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (Directory.Exists(FolderBox.Text)) Process.Start(new ProcessStartInfo(FolderBox.Text) { UseShellExecute = true });
            else MessageBox.Show(this, "还没有生成备份。你可以先预览左侧示例档案。");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }
    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
        try { var folder = Path.Combine(Path.GetDirectoryName(profile)!, "Demo"); await CreateDemoArchive(folder); Process.Start(new ProcessStartInfo(Path.Combine(folder, "index.html")) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }
    private static async Task<ArchiveState> CreateDemoArchive(string folder)
    {
        var state = Demo.Create(); Directory.CreateDirectory(Path.Combine(folder, "media"));
        string[] skies = ["#E4C29E", "#BCD1C0", "#BAC5D8"], hills = ["#8D7560", "#557766", "#596780"];
        for (int i = 0; i < 3; i++)
        {
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
            {
                var sky = (Color)ColorConverter.ConvertFromString(skies[i]); var hill = (Color)ColorConverter.ConvertFromString(hills[i]);
                dc.DrawRectangle(new SolidColorBrush(sky), null, new Rect(0, 0, 800, 600));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(248, 238, 202)), null, new Point(550 - i * 100, 170), 65, 65);
                dc.DrawEllipse(new SolidColorBrush(hill), null, new Point(150, 690), 550, 360);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(43, 75, 65)), null, new Point(710, 770), 590, 390);
            }
            var bitmap = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); var path = $"media/demo-{i}.png";
            using (var stream = File.Create(Path.Combine(folder, path))) encoder.Save(stream);
            var url = $"https://m.qpic.cn/demo-{i}.png"; state.MediaFiles[url] = path; if (i < 2) state.Assets[SpaceText.AvatarUrl((10000001 + i).ToString())] = path;
            state.Posts[i]["pic"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["url1"] = url });
        }
        await J.Save(Path.Combine(folder, "archive.json"), state); await ArchiveWriter.Write(state, folder); return state;
    }
}
