using System.IO;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QZoneArchive.Core;
namespace QZoneArchive;
public partial class MainWindow
{
    private LoadedArchive? library;
    private void UpdateScopeControls()
    {
        if (LimitBox == null || BackupFrom == null || BackupThrough == null) return;
        LimitBox.IsEnabled = ScopeBox.SelectedIndex == 1; BackupFrom.IsEnabled = BackupThrough.IsEnabled = ScopeBox.SelectedIndex == 2;
    }
    private void Scope_Changed(object sender, SelectionChangedEventArgs e) => UpdateScopeControls();
    private void TopUpLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (library == null) { MessageBox.Show(this, "请先打开需要补采的 JSON 档案。"); return; }
        TargetBox.Text = library.State.Target; ScopeBox.SelectedIndex = 3; WorkspaceTabs.SelectedIndex = 0;
        Log("已选择补采当前档案：" + library.Folder + "。按下开始即可补齐所选内容。");
    }
    private sealed record ArchiveRow(JsonObject Post, string Label);
    private Selection LibrarySelection(bool fields = false) => new(FromDate.SelectedDate, ThroughDate.SelectedDate, SearchBox.Text,
        (KindBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部",
        !fields || BodyOut.IsChecked == true, !fields || CommentsOut.IsChecked == true,
        !fields || PicturesOut.IsChecked == true, !fields || VideosOut.IsChecked == true,
        !fields || ForwardsOut.IsChecked == true, fields && LikesOut.IsChecked == true,
        fields && OnlySelected.IsChecked == true ? ArchiveList.SelectedItems.Cast<ArchiveRow>().Select(r => ArchiveLibrary.RecordKey(r.Post)).ToHashSet() : null);
    private void RefreshLibrary()
    {
        if (library == null || ArchiveList == null) return;
        var rows = library.State.Posts.Where(p => ArchiveLibrary.Matches(p, LibrarySelection())).OrderByDescending(ArchiveLibrary.PostDate)
            .Select(p => new ArchiveRow(p, J.Date(p) + "\n" + new string(ArchiveLibrary.Body(p).Replace('\n', ' ').Take(38).ToArray()))).ToList();
        ArchiveList.ItemsSource = rows; MatchCount.Text = $"匹配 {rows.Count} / {library.State.Posts.Count} 条 · 搜索范围包含评论和回复";
        LibraryStatus.Text = $"QQ {library.State.Target} · {library.State.Posts.Count} 条 · {library.State.Status}\n{library.Folder}";
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) RefreshLibrary(); }
    private void ResetFilter_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); FromDate.SelectedDate = null; ThroughDate.SelectedDate = null; KindBox.SelectedIndex = 0; RefreshLibrary(); }
    private void ArchiveSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (library != null && ArchiveList.SelectedItem is ArchiveRow row) ArchivePreview.Text = ArchiveLibrary.PlainText(library.State, new[] { row.Post }, new(), 1);
    }
    private async void LoadArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "本工具 JSON 档案|*.json", Title = "打开 archive.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { library = await ArchiveLibrary.Load(dialog.FileName); RefreshLibrary(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "读取档案"); }
    }
    private string? ChooseResultFolder(string prefix)
    {
        var dialog = new OpenFolderDialog { Title = "选择保存位置，将自动新建结果文件夹" };
        return dialog.ShowDialog(this) == true ? Path.Combine(dialog.FolderName, prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4]) : null;
    }
    private async void MergeArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "本工具 JSON 档案|*.json", Multiselect = true, Title = "选择同一账号的 JSON 档案，可多选" };
        if (dialog.ShowDialog(this) != true) return;
        var folder = ChooseResultFolder("合并档案"); if (folder == null) return;
        try
        {
            WorkspaceTabs.IsEnabled = false; LibraryStatus.Text = "正在比较正文、合并评论与附件…";
            var result = await Task.Run(() => ArchiveLibrary.Import(dialog.FileNames, folder));
            library = await ArchiveLibrary.Load(Path.Combine(folder, "archive.json")); RefreshLibrary();
            MessageBox.Show(this, $"输入 {result.InputPosts} 条 → 合并后 {result.OutputPosts} 条\n合并重复记录 {result.Duplicates} 条；正文差异 {result.ContentConflicts} 处，已保留候选版本。\n原档案保持不变。\n{folder}", "合并完成");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "合并提示"); }
        finally { WorkspaceTabs.IsEnabled = true; }
    }
    private async void ExportArchive_Click(object sender, RoutedEventArgs e) => await ExportLibrary(false);
    private async void Gallery_Click(object sender, RoutedEventArgs e) => await ExportLibrary(true);
    private async Task ExportLibrary(bool gallery)
    {
        if (library == null) { MessageBox.Show(this, "请先打开或导入 JSON 档案。"); return; }
        var selection = gallery ? LibrarySelection() : LibrarySelection(true);
        var formats = gallery ? new OutputFormats(false, true, false) : new OutputFormats(TxtOut.IsChecked == true, HtmlOut.IsChecked == true, JsonOut.IsChecked == true);
        var folder = ChooseResultFolder(gallery ? "Gallery" : "可读档案"); if (folder == null) return;
        try
        {
            WorkspaceTabs.IsEnabled = false; LibraryStatus.Text = "正在生成可读档案并整理附件…";
            var result = await Task.Run(() => ArchiveLibrary.Export(library, selection, formats, folder));
            RefreshLibrary();
            Process.Start(new ProcessStartInfo(gallery ? Path.Combine(folder, "gallery.html") : folder) { UseShellExecute = true });
            if (!gallery) MessageBox.Show(this, $"已导出 {result.Posts.Count} 条。TXT 可直接交给 AI 阅读。\n待核对项 {result.Issues.Count} 个，请同时保留阅读说明。\n{folder}", "导出完成");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出提示"); }
        finally { WorkspaceTabs.IsEnabled = true; RefreshLibrary(); }
    }
}
