using System.Xml;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;

namespace YouTubeAutoPost.Services;

public sealed record DuplicateVideo(
    string Id,
    string Title,
    string ChannelId,
    string Duration,
    string ThumbnailUrl);

public sealed record DuplicateGroup(string Title, IReadOnlyList<DuplicateVideo> Videos);

public sealed class YouTubeDuplicateService
{
    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicateGroupsAsync(
        YouTubeService youtube, CancellationToken cancellationToken)
    {
        var videos = await GetChannelVideosAsync(youtube, cancellationToken);
        return videos
            .GroupBy(video => video.Title, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new DuplicateGroup(group.Key, group.ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<DuplicateVideo>> GetChannelVideosAsync(
        YouTubeService youtube, CancellationToken cancellationToken)
    {
        var channelRequest = youtube.Channels.List("snippet,contentDetails");
        channelRequest.Mine = true;
        channelRequest.MaxResults = 10;
        var channelResponse = await channelRequest.ExecuteAsync(cancellationToken);
        var channel = channelResponse.Items.FirstOrDefault()
            ?? throw new InvalidOperationException("Aucune chaîne YouTube n'est associée au compte connecté.");
        var channelId = channel.Id;
        var uploadsPlaylistId = channel.ContentDetails?.RelatedPlaylists?.Uploads;
        if (string.IsNullOrWhiteSpace(uploadsPlaylistId))
            throw new InvalidOperationException("Impossible de trouver la playlist des vidéos de ta chaîne.");

        var playlistVideoIds = new List<string>();
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = youtube.PlaylistItems.List("snippet");
            request.PlaylistId = uploadsPlaylistId;
            request.MaxResults = 50;
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);
            playlistVideoIds.AddRange(response.Items
                .Select(item => item.Snippet?.ResourceId?.VideoId)
                .Where(id => !string.IsNullOrWhiteSpace(id))!);
            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        var videos = new List<DuplicateVideo>();
        foreach (var batch in playlistVideoIds.Distinct(StringComparer.Ordinal).Chunk(50))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = youtube.Videos.List("snippet,contentDetails");
            request.Id = string.Join(",", batch);
            var response = await request.ExecuteAsync(cancellationToken);
            foreach (var video in response.Items)
            {
                if (string.IsNullOrWhiteSpace(video.Id) ||
                    !string.Equals(video.Snippet?.ChannelId, channelId, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(video.Snippet?.Title))
                    continue;

                var thumbnail = video.Snippet.Thumbnails?.Uhd?.Url
                    ?? video.Snippet.Thumbnails?.Fhd?.Url
                    ?? video.Snippet.Thumbnails?.Maxres?.Url
                    ?? video.Snippet.Thumbnails?.Standard?.Url
                    ?? video.Snippet.Thumbnails?.High?.Url
                    ?? video.Snippet.Thumbnails?.Medium?.Url
                    ?? video.Snippet.Thumbnails?.Default__?.Url
                    ?? "";
                var duration = "Durée inconnue";
                if (!string.IsNullOrWhiteSpace(video.ContentDetails?.Duration))
                {
                    try { duration = XmlConvert.ToTimeSpan(video.ContentDetails.Duration).ToString(@"hh\:mm\:ss"); }
                    catch (FormatException) { }
                }

                videos.Add(new DuplicateVideo(video.Id, video.Snippet.Title, channelId, duration, thumbnail));
            }
        }

        return videos;
    }

    public async Task<bool> IsVideoStillAvailableAsync(
        YouTubeService youtube, DuplicateVideo expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = youtube.Videos.List("snippet");
        request.Id = expected.Id;
        var response = await request.ExecuteAsync(cancellationToken);
        var current = response.Items.FirstOrDefault();

        return current is not null &&
            string.Equals(current.Snippet?.ChannelId, expected.ChannelId, StringComparison.Ordinal) &&
            string.Equals(current.Snippet?.Title, expected.Title, StringComparison.Ordinal);
    }

    public async Task<bool> DeleteIfStillAvailableAsync(
        YouTubeService youtube, DuplicateVideo candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var request = youtube.Videos.List("snippet");
        request.Id = candidate.Id;
        var response = await request.ExecuteAsync(cancellationToken);
        var current = response.Items.FirstOrDefault();

        if (current is null ||
            !string.Equals(current.Snippet?.ChannelId, candidate.ChannelId, StringComparison.Ordinal) ||
            !string.Equals(current.Snippet?.Title, candidate.Title, StringComparison.Ordinal))
            return false;

        // Avant suppression, on vérifie en direct que deux copies exactes existent
        // encore sur la playlist Uploads et que la vidéo ciblée en fait toujours partie.
        var channelRequest = youtube.Channels.List("contentDetails");
        channelRequest.Mine = true;
        channelRequest.MaxResults = 10;
        var channelResponse = await channelRequest.ExecuteAsync(cancellationToken);
        var uploadsPlaylistId = channelResponse.Items.FirstOrDefault()
            ?.ContentDetails?.RelatedPlaylists?.Uploads;
        if (string.IsNullOrWhiteSpace(uploadsPlaylistId))
            return false;

        var matchingIds = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var playlistRequest = youtube.PlaylistItems.List("snippet");
            playlistRequest.PlaylistId = uploadsPlaylistId;
            playlistRequest.MaxResults = 50;
            playlistRequest.PageToken = pageToken;
            var playlistResponse = await playlistRequest.ExecuteAsync(cancellationToken);
            foreach (var item in playlistResponse.Items)
            {
                var title = item.Snippet?.Title;
                var id = item.Snippet?.ResourceId?.VideoId;
                if (string.Equals(title, candidate.Title, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(id))
                    matchingIds.Add(id);
            }
            pageToken = playlistResponse.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        if (matchingIds.Count <= 1 || !matchingIds.Contains(candidate.Id))
            return false;

        await youtube.Videos.Delete(candidate.Id).ExecuteAsync(cancellationToken);
        return true;
    }
}
