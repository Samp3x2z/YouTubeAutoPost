using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using YouTubeAutoPost.Models;

namespace YouTubeAutoPost.Services;

public sealed class YouTubeAuthService
{
    private static readonly string AppFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubeAutoPost");
    private static readonly string ClientFile = Path.Combine(AppFolder, "client_secret.json");
    private static readonly string TokenFolder = Path.Combine(AppFolder, "oauth-token");

    private static readonly string[] Scopes =
    [
        YouTubeService.Scope.Youtube,
        YouTubeService.Scope.YoutubeReadonly
    ];

    public bool HasClientSecrets => File.Exists(ClientFile);
    public bool HasSavedToken => Directory.Exists(TokenFolder) && Directory.EnumerateFiles(TokenFolder, "*.bin").Any();

    public async Task ImportClientSecretsAsync(string sourcePath)
    {
        Directory.CreateDirectory(AppFolder);
        await using (var input = File.OpenRead(sourcePath))
        {
            var secrets = GoogleClientSecrets.FromStream(input);
            if (secrets.Secrets is null || string.IsNullOrWhiteSpace(secrets.Secrets.ClientId))
                throw new InvalidDataException("Ce fichier ne contient pas de client OAuth Google valide.");
        }

        // The selected source file is never renamed or modified. A private app-data copy is used.
        File.Copy(sourcePath, ClientFile, overwrite: true);
        if (Directory.Exists(TokenFolder)) Directory.Delete(TokenFolder, recursive: true);
    }

    public async Task<YouTubeService> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ClientFile))
            throw new FileNotFoundException("Ajoute d'abord le fichier JSON OAuth depuis les paramètres.");

        GoogleClientSecrets secrets;
        await using (var stream = File.OpenRead(ClientFile))
            secrets = GoogleClientSecrets.FromStream(stream);

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets.Secrets, Scopes, "youtube-autopost-user", cancellationToken,
            new DpapiDataStore(TokenFolder));

        return new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "YouTube AutoPost"
        });
    }

    public async Task<(ChannelInfo? Channel, List<PlaylistChoice> Playlists)> LoadDestinationsAsync(
        YouTubeService youtube, CancellationToken cancellationToken)
    {
        var channelRequest = youtube.Channels.List("snippet");
        channelRequest.Mine = true;
        channelRequest.MaxResults = 10;
        var channelResponse = await channelRequest.ExecuteAsync(cancellationToken);
        var channel = channelResponse.Items.FirstOrDefault();
        ChannelInfo? channelInfo = channel is null ? null : new ChannelInfo(
            channel.Id,
            channel.Snippet?.Title ?? "Chaîne YouTube",
            channel.Snippet?.CustomUrl);

        var playlists = new List<PlaylistChoice>();
        string? pageToken = null;
        do
        {
            var request = youtube.Playlists.List("snippet");
            request.Mine = true;
            request.MaxResults = 50;
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);
            playlists.AddRange(response.Items.Select(p => new PlaylistChoice(
                p.Id, p.Snippet?.Title ?? "(playlist sans titre)")));
            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return (channelInfo, playlists);
    }
}
