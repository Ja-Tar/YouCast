using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceModel;
using System.ServiceModel.Syndication;
using System.ServiceModel.Web;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Google.Apis.Services;
using Google.Apis.YouTube.v3.Data;
using MoreLinq;
using YoutubeExplode;
using YoutubeExplode.Converter;
using YoutubeExplode.Videos.Streams;
using Video = Google.Apis.YouTube.v3.Data.Video;
using YouTubeService = Google.Apis.YouTube.v3.YouTubeService;

namespace Service
{
    [ServiceBehavior(
        ConcurrencyMode = ConcurrencyMode.Multiple,
        InstanceContextMode = InstanceContextMode.Single,
        UseSynchronizationContext = false)]
    public sealed class YoutubeFeed : IYoutubeFeed
    {
        private const string _channelUrlFormat = "http://www.youtube.com/channel/{0}";
        private const string _videoUrlFormat = "http://www.youtube.com/watch?v={0}";
        private const string _playlistUrlFormat = "http://www.youtube.com/playlist?list={0}";

        private readonly YoutubeClient _youtubeClient;
        private readonly YouTubeService _youtubeService;

        public YoutubeFeed(string applicationName, string apiKey)
        {
            _youtubeClient = new YoutubeClient();
            _youtubeService =
                new YouTubeService(
                    new BaseClientService.Initializer
                    {
                        ApiKey = apiKey,
                        ApplicationName = applicationName
                    });
        }

        public async Task<SyndicationFeedFormatter> GetUserFeedAsync(
            string userId,
            string encoding,
            int maxLength,
            bool isPopular)
        {
            return await GetFeedFormatterAsync(GetFeedAsync);

            async Task<ItunesFeed> GetFeedAsync(string baseAddress)
            {
                var channel =
                    await GetChannelAsync(userId) ??
                    await FindChannelAsync(userId);

                var arguments = new Arguments(
                    channel.ContentDetails.RelatedPlaylists.Uploads,
                    encoding,
                    maxLength,
                    isPopular);

                return new ItunesFeed(
                    GetTitle(channel.Snippet.Title, arguments),
                    RemoveEmojis(channel.Snippet.Description),
                    new Uri(string.Format(_channelUrlFormat, channel.Id)))
                {
                    ImageUrl = new Uri(channel.Snippet.Thumbnails.Medium.Url),
                    Items = await GenerateItemsAsync(
                    baseAddress,
                        channel.Snippet.PublishedAt.GetValueOrDefault().ToUniversalTime(), 
                        // PublishedAt is Obsolete but PublishedAtDateTimeOffset is making some requests fail | bug report https://github.com/dotnet/runtime/issues/9364
                        arguments)
                };
            }

            async Task<Channel> GetChannelAsync(string id)
            {
                var listRequestForId = _youtubeService.Channels.List("snippet,contentDetails");
                listRequestForId.Id = id;
                listRequestForId.MaxResults = 1;
                listRequestForId.Fields = "items(contentDetails,id,snippet)";

                var channelListResponse = await listRequestForId.ExecuteAsync();
                return channelListResponse.Items?.Single();
            }

            async Task<Channel> FindChannelAsync(string username)
            {
                var listRequestForUsername = _youtubeService.Channels.List("snippet,contentDetails");
                listRequestForUsername.ForUsername = username;
                listRequestForUsername.MaxResults = 1;
                listRequestForUsername.Fields = "items(contentDetails,id,snippet)";

                var channelListResponse = await listRequestForUsername.ExecuteAsync();
                return channelListResponse.Items?.Single();
            }
        }

        public async Task<SyndicationFeedFormatter> GetPlaylistFeedAsync(
            string playlistId,
            string encoding,
            int maxLength,
            bool isPopular)
        {
            return await GetFeedFormatterAsync(GetFeedAsync);

            async Task<ItunesFeed> GetFeedAsync(string baseAddress)
            {
                var arguments =
                    new Arguments(
                        playlistId,
                        encoding,
                        maxLength,
                        isPopular);

                var playlistRequest = _youtubeService.Playlists.List("snippet");
                playlistRequest.Id = playlistId;
                playlistRequest.MaxResults = 1;

                var playlist = (await playlistRequest.ExecuteAsync()).Items.First();

                return new ItunesFeed(
                    GetTitle(playlist.Snippet.Title, arguments),
                    RemoveEmojis(playlist.Snippet.Description),
                    new Uri(string.Format(_playlistUrlFormat, playlist.Id)))
                {
                    ImageUrl = new Uri(playlist.Snippet.Thumbnails.Medium.Url),
                    Items = await GenerateItemsAsync(
                        baseAddress,
                        playlist.Snippet.PublishedAt.GetValueOrDefault().ToUniversalTime(),
                        // PublishedAt is Obsolete but PublishedAtDateTimeOffset is making some requests fail | bug report https://github.com/dotnet/runtime/issues/9364
                        arguments)
                };
            }
        }

        public async Task GetVideoAsync(string videoId, string encoding)
        {
            await GetContentAsync(GetVideoUriAsync);

            async Task<string> GetVideoUriAsync()
            {
                var videoInfo = await _youtubeClient.Videos.GetAsync(videoId);
                var fileName = $"{videoInfo.Id}.mp4";
                var videoDirectory = "Videos";
                var channelDirectory = Path.Combine(videoDirectory, videoInfo.Author.ChannelId);
                var filePath = Path.Combine(channelDirectory, fileName);

                if (!Directory.Exists(channelDirectory))
                {
                    Directory.CreateDirectory(channelDirectory);
                }

                if (File.Exists(filePath))
                {
                    Console.WriteLine(filePath);

                    return GenerateFileUri(videoId, videoInfo.Author.ChannelId);
                }

                var resolution = 720;
                try
                {
                    resolution = int.Parse(encoding.Remove(encoding.Length - 1).Substring(startIndex: 4));
                }
                catch
                {
                }

                var streamManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
                var muxedStreamInfos = streamManifest.GetMuxedStreams().ToList();

                if (muxedStreamInfos.Count == 0)
                {
                    Console.WriteLine("No muxed streams found.");

                    // Select best audio stream (highest bitrate)
                    var audioStreamInfo = streamManifest
                        .GetAudioStreams()
                        .Where(s => s.Container == Container.Mp4)
                        .GetWithHighestBitrate();

                    // Select best video stream (1080p60 in this example)
                    var videoStreamInfos = streamManifest
                        .GetVideoStreams()
                        .Where(s => s.Container == Container.Mp4)
                        .ToList();

                    var videoStreamInfo = videoStreamInfos.FirstOrDefault(_ => _.VideoResolution.Height == resolution) ??
                                         videoStreamInfos.Maxima(_ => _.VideoQuality).FirstOrDefault();

                    Console.WriteLine("Audio stream: " + audioStreamInfo);
                    Console.WriteLine("Video stream: " + videoStreamInfo);

                    // Download and mux streams into a single file
                    var streamInfos = new IStreamInfo[] { audioStreamInfo, videoStreamInfo };
                    await _youtubeClient.Videos.DownloadAsync(streamInfos, new ConversionRequestBuilder(filePath).Build());

                    Console.WriteLine($"Video saved to: {filePath}");

                    return GenerateFileUri(videoId, videoInfo.Author.ChannelId);
                }

                var muxedStreamInfo =
                    muxedStreamInfos.FirstOrDefault(_ => _.VideoResolution.Height == resolution) ??
                    muxedStreamInfos.Maxima(_ => _.VideoQuality).FirstOrDefault();

                return muxedStreamInfo?.Url;
            }

            string GenerateFileUri(string _videoId, string _channelId) => $"File.mp4?videoId={_videoId}&channelId={_channelId}";
        }

        public async Task GetAudioAsync(string videoId)
        {
            await GetContentAsync(GetAudioUriAsync);

            async Task<string> GetAudioUriAsync()
            {
                var streamManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
                var audios = streamManifest.GetAudioOnlyStreams().ToList();
                return audios.Count > 0
                    ? audios.Maxima(audio => audio.Bitrate).FirstOrDefault().Url
                    : null;
            }
        }

        private async Task<SyndicationFeedFormatter> GetFeedFormatterAsync(Func<string, Task<ItunesFeed>> getFeedAsync)
        {
            var transportAddress = OperationContext.Current.IncomingMessageProperties.Via;
            var baseAddress = $"http://{transportAddress.DnsSafeHost}:{transportAddress.Port}/FeedService";

            WebOperationContext.Current.OutgoingResponse.ContentType = "application/rss+xml; charset=utf-8";
            WebOperationContext.Current.OutgoingResponse.Headers.Add("Content-Disposition", "attachment; filename=feed.rss");

            var feed = await getFeedAsync(baseAddress);
            return feed.GetRss20Formatter();
        }

        private async Task GetContentAsync(Func<Task<string>> getContentUriAsync)
        {
            var context = WebOperationContext.Current;

            string redirectUri;
            try
            {
                redirectUri = await getContentUriAsync();
            }
            catch
            {
                redirectUri = null;
            }

            if (redirectUri == null)
            {
                context.OutgoingResponse.StatusCode = HttpStatusCode.NotFound;
                return;
            }

            context.OutgoingResponse.RedirectTo(redirectUri);
        }

        public Task<Stream> GetFile(string videoID, string channelID)
        {
            var context = WebOperationContext.Current;
            var mainDirectory = "Videos";
            var channelDirectory = Path.Combine(mainDirectory, channelID);

            if (!Directory.Exists(channelDirectory))
            {
                context.OutgoingResponse.StatusCode = HttpStatusCode.NotFound;
                return null;
            }

            var fileName = $"{videoID}.mp4";
            var filePath = Path.Combine(channelDirectory, fileName);

            if (!File.Exists(filePath))
            {
                context.OutgoingResponse.StatusCode = HttpStatusCode.NotFound;
                return null;
            }

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            context.OutgoingResponse.ContentType = "video/mp4";

            return Task.FromResult<Stream>(stream);
        }

        private async Task<IEnumerable<SyndicationItem>> GenerateItemsAsync(
            string baseAddress,
            DateTime startDate,
            Arguments arguments)
        {
            IEnumerable<PlaylistItem> playlistItems = (await GetPlaylistItemsAsync(arguments)).ToList();
            var userVideos = playlistItems.Select(_ => GenerateItem(_, baseAddress, arguments));
            if (arguments.IsPopular)
            {
                userVideos = await SortByPopularityAsync(userVideos, playlistItems, startDate);
            }

            return userVideos;
        }

        private async Task<IEnumerable<PlaylistItem>> GetPlaylistItemsAsync(Arguments arguments)
        {
            var playlistItems = new List<PlaylistItem>();
            var nextPageToken = string.Empty;
            while (nextPageToken != null && playlistItems.Count < arguments.MaxLength)
            {
                var playlistItemsListRequest = _youtubeService.PlaylistItems.List("snippet");
                playlistItemsListRequest.PlaylistId = arguments.PlaylistId;
                playlistItemsListRequest.MaxResults = 50;
                playlistItemsListRequest.PageToken = nextPageToken;
                playlistItemsListRequest.Fields = "items(id,snippet),nextPageToken";

                var playlistItemsListResponse = await playlistItemsListRequest.ExecuteAsync();
                playlistItems.AddRange(playlistItemsListResponse.Items);
                nextPageToken = playlistItemsListResponse.NextPageToken;
            }

            return playlistItems.Take(arguments.MaxLength);
        }

        private static SyndicationItem GenerateItem(PlaylistItem playlistItem, string baseAddress, Arguments arguments)
        {
            var item = new SyndicationItem(
                playlistItem.Snippet.Title,
                string.Empty,
                new Uri(string.Format(_videoUrlFormat, playlistItem.Snippet.ResourceId.VideoId)))
            {
                Id = playlistItem.Snippet.ResourceId.VideoId,
                PublishDate = playlistItem.Snippet.PublishedAt.GetValueOrDefault().ToUniversalTime(),
                // PublishedAt is Obsolete but PublishedAtDateTimeOffset is making some requests fail | bug report https://github.com/dotnet/runtime/issues/9364
                Summary = new TextSyndicationContent(RemoveEmojis(playlistItem.Snippet.Description)),
            };

            if (arguments.Encoding == "Audio")
            {
                item.ElementExtensions.Add(
                    new XElement(
                        "enclosure",
                        new XAttribute("type", "audio/mp4"),
                        new XAttribute(
                            "url",
                            baseAddress + $"/Audio.m4a?videoId={playlistItem.Snippet.ResourceId.VideoId}")).CreateReader());
            }
            else
            {
                item.ElementExtensions.Add(
                    new XElement(
                        "enclosure",
                        new XAttribute("type", "video/mp4"),
                        new XAttribute(
                            "url",
                            baseAddress + $"/Video.mp4?videoId={playlistItem.Snippet.ResourceId.VideoId}&encoding={arguments.Encoding}")).CreateReader());
            }

            return item;
        }

        private async Task<IEnumerable<SyndicationItem>> SortByPopularityAsync(
            IEnumerable<SyndicationItem> userVideos,
            IEnumerable<PlaylistItem> playlistItems,
            DateTime startDate)
        {
            var videos = await GetVideosAsync(playlistItems.Select(_ => _.Snippet.ResourceId.VideoId).Distinct());
            var videoDictionary = videos.ToDictionary(_ => _.Id, _ => _);
            userVideos = userVideos.
                OrderByDescending(_ => videoDictionary[_.Id].Statistics.ViewCount.GetValueOrDefault()).
                ToList();
            var i = 0;
            foreach (var userVideo in userVideos)
            {
                userVideo.PublishDate = startDate.AddDays(i);
                i++;
                userVideo.Title = new TextSyndicationContent($"{i}. {userVideo.Title.Text}");
            }

            return userVideos;
        }

        private async Task<IEnumerable<Video>> GetVideosAsync(IEnumerable<string> videoIds) =>
            (await Task.WhenAll(videoIds.Batch(50).Select(GetVideoBatchAsync))).SelectMany(_ => _);

        private async Task<IEnumerable<Video>> GetVideoBatchAsync(IEnumerable<string> videoIds)
        {
            var statisticsRequest = _youtubeService.Videos.List("statistics");
            statisticsRequest.Id = string.Join(",", videoIds);
            statisticsRequest.MaxResults = 50;
            statisticsRequest.Fields = "items(id,statistics)";
            return (await statisticsRequest.ExecuteAsync()).Items;
        }

        private static string GetTitle(string title, Arguments arguments) =>
            arguments.IsPopular ? $"{RemoveEmojis(title)} (By Popularity)" : RemoveEmojis(title);

        private static string RemoveEmojis(string text) =>
            Regex.Replace(text, @"\p{Cs}", string.Empty);
    }

    public static class OutgoingWebResponseContextExtension
    {
        public static void RedirectTo(this OutgoingWebResponseContext context, string redirectUri)
        {
            if (redirectUri != null)
            {
                context.StatusCode = HttpStatusCode.Redirect;
                context.Headers[nameof(HttpResponseHeader.Location)] = redirectUri;
            }
            else
            {
                context.StatusCode = HttpStatusCode.NotFound;
            }
        }
    }
}