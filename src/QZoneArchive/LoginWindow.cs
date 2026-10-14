using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace QZoneArchive;
public record LoginSession(string Owner, CookieContainer Cookies, string Skey, string Token);
public sealed class LoginWindow : Window
{
    private readonly WebView2 browser = new();
    private readonly TextBlock info = new() { Text = "请在 QQ 官方页面扫码登录，进入空间后点击右侧按钮。", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button confirm = new() { Content = "已登录，连接此账号", Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(12, 0, 0, 0), IsEnabled = false };
    public LoginSession? Session { get; private set; }
    private readonly string profile;
    public LoginWindow(string profile)
    {
        this.profile = profile;
        Title = "登录 QQ 官方空间 · 时光留存"; Width = 1080; Height = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel(); var bar = new DockPanel { Margin = new Thickness(16) }; DockPanel.SetDock(bar, Dock.Bottom); DockPanel.SetDock(confirm, Dock.Right); bar.Children.Add(confirm); bar.Children.Add(info); dock.Children.Add(bar); dock.Children.Add(browser); Content = dock;
        confirm.Click += Confirm; Loaded += Initialize; Closed += (_, _) => browser.Dispose();
    }
    private async void Initialize(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(profile);
            var env = await CoreWebView2Environment.CreateAsync(null, profile);
            await browser.EnsureCoreWebView2Async(env);
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            browser.CoreWebView2.NewWindowRequested += (_, args) => { args.Handled = true; if (SafeNavigation(args.Uri)) browser.CoreWebView2.Navigate(args.Uri); };
            browser.CoreWebView2.NavigationStarting += (_, args) => { if (!SafeNavigation(args.Uri)) { args.Cancel = true; info.Text = "仅在 QQ 官方域名内登录；外部跳转已停止。"; } };
            browser.CoreWebView2.DownloadStarting += (_, args) => args.Cancel = true;
            browser.CoreWebView2.Navigate("https://qzone.qq.com/"); confirm.IsEnabled = true;
        }
        catch (Exception ex) { info.Text = "浏览器初始化失败。请安装 Microsoft Edge WebView2 Runtime 后重试。"; MessageBox.Show(this, info.Text + "\n" + ex.Message, "登录组件"); }
    }
    private static bool SafeNavigation(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && (u.Host == "qq.com" || u.Host.EndsWith(".qq.com") || u.Host.EndsWith(".gtimg.cn"));
    private async void Confirm(object sender, RoutedEventArgs e)
    {
        confirm.IsEnabled = false;
        try
        {
            var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync("https://user.qzone.qq.com/");
            string Get(string name) => cookies.FirstOrDefault(c => c.Name == name)?.Value ?? "";
            var owner = Get("p_uin"); if (owner == "") owner = Get("uin"); owner = owner.TrimStart('o', '0');
            var skey = Get("p_skey"); if (skey == "") skey = Get("skey");
            if (!Regex.IsMatch(owner, "^[1-9][0-9]{4,12}$") || skey == "") { info.Text = "还没有取得有效登录。请扫码并进入自己的 QQ 空间主页。"; return; }
            // qzonetoken belongs to the visited space page; no cookie or token is logged to disk.
            var raw = await browser.CoreWebView2.ExecuteScriptAsync("(()=>{try{return typeof window.g_qzonetoken==='function'?window.g_qzonetoken():String(window.g_qzonetoken||'')}catch{return ''}})()");
            var token = JsonSerializer.Deserialize<string>(raw) ?? "";
            if (!browser.Source.Host.EndsWith("qzone.qq.com")) { browser.CoreWebView2.Navigate("https://user.qzone.qq.com/" + owner); info.Text = "正在打开空间主页，请加载完后再次点击连接。"; return; }
            var container = new CookieContainer();
            foreach (var c in cookies)
            {
                try { container.Add(new Cookie(c.Name, c.Value, c.Path, c.Domain) { Secure = c.IsSecure, HttpOnly = c.IsHttpOnly }); } catch (CookieException) { }
            }
            Session = new(owner, container, skey, token); DialogResult = true;
        }
        catch (Exception ex) { info.Text = "连接失败：" + ex.Message; }
        finally { confirm.IsEnabled = true; }
    }
    public static async Task ClearProfile(string profile)
    {
        var view = new WebView2();
        try { await view.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, profile)); await view.CoreWebView2.Profile.ClearBrowsingDataAsync(); }
        finally { view.Dispose(); }
    }
}
