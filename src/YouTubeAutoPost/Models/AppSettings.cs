namespace YouTubeAutoPost.Models;

public sealed class AppSettings
{
    public string FolderPath { get; set; } = Directory.Exists(@"E:\vid") ? @"E:\vid" : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public string TargetMode { get; set; } = "channel";
    public string SelectedPlaylistId { get; set; } = "";
    public string SelectedPlaylistTitle { get; set; } = "";
    public string Privacy { get; set; } = "unlisted";
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool AutoPublish { get; set; } = false;
}

public sealed record PlaylistChoice(string Id, string Title);
public sealed record ChannelInfo(string Id, string Title, string? CustomUrl);
public sealed record SyncProgress(string Message, int Completed, int Total, string? CurrentFile = null);
