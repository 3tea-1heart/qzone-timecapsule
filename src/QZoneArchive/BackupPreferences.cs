using System.IO;
using System.Text.Json;
using QZoneArchive.Core;
namespace QZoneArchive;
public partial class MainWindow
{
    private string? rememberedArchive;
    private sealed record BackupPreferences(string Folder, string Target, int Scope, string Limit, DateTime? From, DateTime? Through, int Speed, int Workers, bool Images, bool Videos, bool Comments, bool Avatars, bool Emoji, string? Archive);
    private string PreferencesPath => Path.Combine(Path.GetDirectoryName(profile)!, "preferences.json");
    private void RestorePreferences()
    {
        try
        {
            if (!File.Exists(PreferencesPath)) return;
            var p = JsonSerializer.Deserialize<BackupPreferences>(File.ReadAllText(PreferencesPath)); if (p == null) return;
            FolderBox.Text = p.Folder; TargetBox.Text = p.Target; ScopeBox.SelectedIndex = Math.Clamp(p.Scope, 0, 3); LimitBox.Text = p.Limit;
            BackupFrom.SelectedDate = p.From; BackupThrough.SelectedDate = p.Through; SpeedBox.SelectedIndex = Math.Clamp(p.Speed, 0, 3); WorkersBox.SelectedIndex = Math.Clamp(p.Workers, 0, 2);
            MediaCheck.IsChecked = p.Images; VideoCheck.IsChecked = p.Videos; CollectComments.IsChecked = p.Comments; AvatarCheck.IsChecked = p.Avatars; EmojiCheck.IsChecked = p.Emoji; rememberedArchive = p.Archive;
            UpdateScopeControls();
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { Log("上次的选项未能读取，已使用默认设置。"); }
    }
    private Task SavePreferences() => J.Save(PreferencesPath, new BackupPreferences(FolderBox.Text, TargetBox.Text, ScopeBox.SelectedIndex, LimitBox.Text, BackupFrom.SelectedDate, BackupThrough.SelectedDate, SpeedBox.SelectedIndex, WorkersBox.SelectedIndex, MediaCheck.IsChecked == true, VideoCheck.IsChecked == true, CollectComments.IsChecked == true, AvatarCheck.IsChecked == true, EmojiCheck.IsChecked == true, lastFolder == null ? null : Path.Combine(lastFolder, "archive.json")));
}
