using Google;
using Google.Apis.Upload;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using YouTubeAutoPost.Models;

namespace YouTubeAutoPost.Services;

public sealed class YouTubeUploader
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".wmv", ".webm",
        ".mpeg", ".mpg", ".3gp", ".ts", ".mts"
    };

    public async Task SyncAsync(YouTubeService youtube, AppSettings settings,
        IProgress<SyncProgress> progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.FolderPath) || !Directory.Exists(settings.FolderPath))
            throw new DirectoryNotFoundException("Le dossier vidéo sélectionné n'existe pas.");

        if (settings.TargetMode == "playlist" && string.IsNullOrWhiteSpace(settings.SelectedPlaylistId))
            throw new InvalidOperationException("Sélectionne une playlist avant de lancer la synchronisation.");

        var files = await Task.Run(() => Directory.EnumerateFiles(settings.FolderPath, "*", SearchOption.AllDirectories)
            .Where(path => Extensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(), cancellationToken);

        using var history = new UploadHistory();
        // La vérification de la chaîne est toujours active, même si la case de détection
        // est décochée : elle constitue la protection anti-double-upload.
        var channelTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        var playlistTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        var channelRequest = youtube.Channels.List("contentDetails");
        channelRequest.Mine = true;
        channelRequest.MaxResults = 10;
        var channelResponse = await channelRequest.ExecuteAsync(cancellationToken);
        var uploadsPlaylistId = channelResponse.Items.FirstOrDefault()
            ?.ContentDetails?.RelatedPlaylists?.Uploads;
        if (string.IsNullOrWhiteSpace(uploadsPlaylistId))
            throw new InvalidOperationException("Impossible de trouver la playlist des vidéos de ta chaîne YouTube.");

        await LoadPlaylistTitlesAsync(youtube, uploadsPlaylistId, channelTitles, cancellationToken);

        if (settings.TargetMode == "playlist")
            await LoadPlaylistTitlesAsync(youtube, settings.SelectedPlaylistId, playlistTitles, cancellationToken);

        var done = 0;
        var uploaded = 0;
        var skipped = 0;
        progress.Report(new SyncProgress($"Analyse de {files.Count} vidéo(s)…", 0, files.Count));

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                skipped++;
                done++;
                progress.Report(new SyncProgress("Fichier vide ou indisponible ignoré", done, files.Count, Path.GetFileName(path)));
                continue;
            }

            var fullPath = info.FullName;
            var mtime = info.LastWriteTimeUtc.Subtract(DateTime.UnixEpoch).TotalSeconds;
            var title = BuildTitle(Path.GetFileNameWithoutExtension(info.Name));
            var previous = history.Get(fullPath);
            var sameFile = previous is not null && previous.Size == info.Length
                && Math.Abs(previous.Mtime - mtime) < 0.001;

            if (channelTitles.TryGetValue(title, out var channelVideoId))
            {
                if (settings.TargetMode == "playlist" && !playlistTitles.ContainsKey(title))
                {
                    await AddToPlaylistAsync(youtube, settings.SelectedPlaylistId, channelVideoId, cancellationToken);
                    playlistTitles[title] = channelVideoId;
                    progress.Report(new SyncProgress("Vidéo déjà sur YouTube : ajoutée à la playlist", done + 1, files.Count, info.Name));
                }
                else
                {
                    progress.Report(new SyncProgress("Vidéo déjà présente sur YouTube — aucun nouvel envoi", done + 1, files.Count, info.Name));
                }

                history.Save(fullPath, info.Length, mtime, channelVideoId, "done");
                skipped++;
                done++;
                continue;
            }

            if (settings.TargetMode == "playlist" && playlistTitles.TryGetValue(title, out var playlistVideoId))
            {
                history.Save(fullPath, info.Length, mtime, playlistVideoId, "done");
                skipped++;
                done++;
                progress.Report(new SyncProgress("Titre déjà présent dans la playlist YouTube — aucun nouvel envoi", done, files.Count, info.Name));
                continue;
            }

            // Dernière vérification en ligne juste avant l'envoi. Elle couvre aussi les
            // états locaux "uploading"/"upload_uncertain" après fermeture ou crash.
            var freshChannelTitles = new Dictionary<string, string>(StringComparer.Ordinal);
            await LoadPlaylistTitlesAsync(youtube, uploadsPlaylistId, freshChannelTitles, cancellationToken);
            foreach (var entry in freshChannelTitles)
                channelTitles[entry.Key] = entry.Value;

            if (settings.TargetMode == "playlist")
            {
                var freshPlaylistTitles = new Dictionary<string, string>(StringComparer.Ordinal);
                await LoadPlaylistTitlesAsync(youtube, settings.SelectedPlaylistId, freshPlaylistTitles, cancellationToken);
                foreach (var entry in freshPlaylistTitles)
                    playlistTitles[entry.Key] = entry.Value;
            }

            if (channelTitles.TryGetValue(title, out channelVideoId))
            {
                if (settings.TargetMode == "playlist" && !playlistTitles.ContainsKey(title))
                {
                    await AddToPlaylistAsync(youtube, settings.SelectedPlaylistId, channelVideoId, cancellationToken);
                    playlistTitles[title] = channelVideoId;
                }
                history.Save(fullPath, info.Length, mtime, channelVideoId, "done");
                skipped++;
                done++;
                progress.Report(new SyncProgress("Vidéo trouvée sur YouTube lors du contrôle final — envoi annulé", done, files.Count, info.Name));
                continue;
            }

            if (sameFile && (previous!.Status is "uploading" or "upload_uncertain" or "uploaded_playlist_pending"))
            {
                throw new InvalidOperationException(
                    $"L'état du précédent envoi de « {info.Name} » reste incertain. Aucune nouvelle publication n'est lancée pour éviter un doublon. Vérifie d'abord les vidéos récentes de ta chaîne YouTube.");
            }

            string? uploadedVideoId = null;
            history.Save(fullPath, info.Length, mtime, null, "uploading");
            try
            {
                var video = new Video
                {
                    Snippet = new VideoSnippet { Title = title, Description = "", CategoryId = "22" },
                    Status = new VideoStatus { PrivacyStatus = settings.Privacy, SelfDeclaredMadeForKids = false }
                };

                await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var insert = youtube.Videos.Insert(video, "snippet,status", stream, GetMimeType(info.Extension));
                insert.ChunkSize = 8 * 1024 * 1024;
                var uploadProgress = await insert.UploadAsync(cancellationToken);
                if (uploadProgress.Status != UploadStatus.Completed || insert.ResponseBody is null)
                    throw uploadProgress.Exception ?? new IOException("L'upload YouTube n'a pas été confirmé.");

                uploadedVideoId = insert.ResponseBody.Id;
                history.Save(fullPath, info.Length, mtime, uploadedVideoId, "uploaded_playlist_pending");

                if (settings.TargetMode == "playlist")
                {
                    await AddToPlaylistAsync(youtube, settings.SelectedPlaylistId, uploadedVideoId, cancellationToken);
                    playlistTitles[title] = uploadedVideoId;
                }

                channelTitles[title] = uploadedVideoId;
                history.Save(fullPath, info.Length, mtime, uploadedVideoId, "done");
                uploaded++;
                done++;
                progress.Report(new SyncProgress("Upload terminé", done, files.Count, info.Name));
            }
            catch (GoogleApiException ex)
            {
                var quota = ex.Error?.Errors?.Any(e =>
                    string.Equals(e.Reason, "uploadLimitExceeded", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Reason, "quotaExceeded", StringComparison.OrdinalIgnoreCase)) == true;
                history.Save(fullPath, info.Length, mtime, uploadedVideoId,
                    uploadedVideoId is null ? (quota ? "error" : "upload_uncertain") : "uploaded_playlist_pending", ex.Message);
                if (quota)
                    throw new InvalidOperationException("Google a refusé l'envoi : limite ou quota YouTube atteint. La synchronisation s'arrête ici ; réessaie après le renouvellement du quota.", ex);
                throw;
            }
            catch (OperationCanceledException)
            {
                history.Save(fullPath, info.Length, mtime, uploadedVideoId,
                    uploadedVideoId is null ? "upload_uncertain" : "uploaded_playlist_pending",
                    "Synchronisation interrompue.");
                throw;
            }
            catch (Exception ex)
            {
                history.Save(fullPath, info.Length, mtime, uploadedVideoId,
                    uploadedVideoId is null ? "upload_uncertain" : "uploaded_playlist_pending", ex.Message);
                throw;
            }
        }

        progress.Report(new SyncProgress(
            $"Synchronisation terminée : {uploaded} envoyé(s), {skipped} ignoré(s).", files.Count, files.Count));
    }

    private static async Task LoadPlaylistTitlesAsync(YouTubeService youtube, string playlistId,
        Dictionary<string, string> titles, CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = youtube.PlaylistItems.List("snippet");
            request.PlaylistId = playlistId;
            request.MaxResults = 50;
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);
            foreach (var item in response.Items)
            {
                var title = item.Snippet?.Title;
                var videoId = item.Snippet?.ResourceId?.VideoId;
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(videoId))
                    titles.TryAdd(title, videoId);
            }
            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    private static async Task AddToPlaylistAsync(YouTubeService youtube, string playlistId,
        string videoId, CancellationToken cancellationToken)
    {
        var item = new PlaylistItem
        {
            Snippet = new PlaylistItemSnippet
            {
                PlaylistId = playlistId,
                ResourceId = new ResourceId { Kind = "youtube#video", VideoId = videoId }
            }
        };
        await youtube.PlaylistItems.Insert(item, "snippet").ExecuteAsync(cancellationToken);
    }

    private static string BuildTitle(string fileName) =>
        fileName[..Math.Min(fileName.Length, 100)];

    private static string GetMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".mkv" => "video/x-matroska",
        ".wmv" => "video/x-ms-wmv",
        ".webm" => "video/webm",
        ".mpeg" or ".mpg" => "video/mpeg",
        ".3gp" => "video/3gpp",
        ".ts" or ".mts" => "video/mp2t",
        _ => "application/octet-stream"
    };
}
