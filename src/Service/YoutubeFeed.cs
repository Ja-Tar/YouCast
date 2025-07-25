using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceModel;
using System.ServiceModel.Syndication;
using System.ServiceModel.Web;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Google.Apis.Services;
using Google.Apis.YouTube.v3.Data;
using MoreLinq;
using YoutubeExplode;
using YoutubeExplode.Converter;
using YoutubeExplode.Exceptions;
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
            string language,
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
                    language,
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
                        // NOTE: PublishedAt is Obsolete but PublishedAtDateTimeOffset is making some requests fail | bug report https://github.com/dotnet/runtime/issues/9364
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
            string language,
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
                        language,
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
                        // NOTE: PublishedAt is Obsolete but PublishedAtDateTimeOffset is making some requests fail | bug report https://github.com/dotnet/runtime/issues/9364
                        arguments)
                };
            }
        }

        public async Task GetVideoAsync(string videoId, string encoding, string languageString)
        {
            await GetContentAsync(GetVideoUriAsync);

            async Task<string> GetVideoUriAsync()
            {
                var videoInfo = await _youtubeClient.Videos.GetAsync(videoId);
                Enum.TryParse(languageString ?? string.Empty, out YouTubeLang language);
                var fileName = $"{videoInfo.Id}.mp4";
                var videoDirectory = "Videos";
                string channelDirectory = GetChannelDirectory(videoDirectory, videoInfo.Author.ChannelId, language);
                var filePath = Path.Combine(channelDirectory, fileName);
                var channelConfigFilePath = Path.Combine(channelDirectory, "config.xml");

                if (!Directory.Exists(channelDirectory))
                {
                    Directory.CreateDirectory(channelDirectory);
                }

                if (File.Exists(filePath))
                {
                    Console.WriteLine(filePath);

                    return GenerateFileUri(videoId, videoInfo.Author.ChannelId, language);
                }

                EnsureChannelConfigFile(channelConfigFilePath, videoInfo, language);

                var resolution =
                    int.TryParse(encoding.Remove(encoding.Length - 1).Substring(startIndex: 4), out int parsedResolution) ?
                    parsedResolution : 720;

                StreamManifest streamManifest;

                try
                {
                    streamManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
                }
                catch (VideoUnplayableException ex)
                {
                    Console.WriteLine($"Error fetching stream manifest for video {videoId}: {ex.Message}");
                    // TODO: more error info and popup or some kind of info in the UI
                    return null;
                }
                var muxedStreamInfos = streamManifest.GetMuxedStreams().ToList();

                if (muxedStreamInfos.Count == 0)
                {
                    Console.WriteLine("No muxed streams found for: " + videoId);

                    AudioOnlyStreamInfo audioStreamInfo;
                    try
                    {
                        audioStreamInfo = GetAudioStreamsByLanguage(streamManifest, language);
                    }
                    catch (AudioStreamNotFoundException)
                    {
                        Console.WriteLine($"Audio stream for {languageString} not found, redirecting...");
                        return $"Video.mp4?videoId={videoId}&channelId={videoInfo.Author.ChannelId}&language={YouTubeLang.Original.ToString()}";
                    }

                    var videoStreamInfos = streamManifest
                        .GetVideoOnlyStreams()
                        .Where(s => s.Container == Container.Mp4)
                        .ToList();

                    var videoStreamInfo = videoStreamInfos.FirstOrDefault(_ => _.VideoResolution.Height == resolution) ??
                                         videoStreamInfos.Maxima(_ => _.VideoQuality).FirstOrDefault();

                    // Download and mux streams into a single file
                    var streamInfos = new IStreamInfo[] { audioStreamInfo, videoStreamInfo };
                    await _youtubeClient.Videos.DownloadAsync(streamInfos, new ConversionRequestBuilder(filePath).Build());

                    Console.WriteLine($"Video saved to: {filePath}");

                    return GenerateFileUri(videoId, videoInfo.Author.ChannelId, language);
                }

                var muxedStreamInfo =
                    muxedStreamInfos.FirstOrDefault(_ => _.VideoResolution.Height == resolution) ??
                    muxedStreamInfos.Maxima(_ => _.VideoQuality).FirstOrDefault();

                return muxedStreamInfo?.Url;
            }

            string GenerateFileUri(string _videoId, string _channelId, YouTubeLang _language)
            {
                return $"File.mp4?videoId={_videoId}&channelId={_channelId}&language={_language}";
            }
        }

        private static void EnsureChannelConfigFile(string channelConfigFilePath, YoutubeExplode.Videos.Video videoInfo, YouTubeLang language)
        {
            List<XElement> elements = new List<XElement> {
                new XElement("ChannelId", videoInfo.Author.ChannelId),
                new XElement("ChannelName", videoInfo.Author.ChannelTitle),
                new XElement("ChannelLanguage", language)
            };

            if (File.Exists(channelConfigFilePath))
            {
                var existingXml = XElement.Load(channelConfigFilePath);
                elements = elements
                    .Where(e => existingXml.Element(e.Name) == null)
                    .ToList();

                // Add missing elements to the existing config and save
                if (elements.Count > 0)
                {
                    foreach (var el in elements)
                        existingXml.Add(el);
                    existingXml.Save(channelConfigFilePath);
                }
            }
            else
            {
                var xml = new XElement("FolderConfig", elements);
                xml.Save(channelConfigFilePath);
            }
        }

        public async Task GetAudioAsync(string videoId, string languageString)
        {
            await GetContentAsync(GetAudioUriAsync);

            async Task<string> GetAudioUriAsync()
            {
                var streamManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
                Enum.TryParse(languageString ?? string.Empty, out YouTubeLang language);

                AudioOnlyStreamInfo audios;
                try
                {
                    audios = GetAudioStreamsByLanguage(streamManifest, language);
                }
                catch (AudioStreamNotFoundException)
                {
                    Console.WriteLine($"Audio stream for {languageString} not found, redirecting...");
                    return $"Audio.m4a?videoId={videoId}&language={YouTubeLang.Original.ToString()}";
                }

                return audios?.Url;
            }
        }

        private static AudioOnlyStreamInfo GetAudioStreamsByLanguage(StreamManifest streamManifest, YouTubeLang language)
        {
            // All streams
            var audioStreamList = streamManifest
                .GetAudioOnlyStreams()
                .Where(s => s.Container == Container.Mp4);

            // Language list
            var audioLangList = audioStreamList
                .Select(s => s.AudioLanguage)
                .Where(lang => lang != null)
                .Distinct()
                .ToList();

            if (HasAudioLanguage(audioLangList, language) || language == YouTubeLang.Original)
            {
                Console.WriteLine($"Using {language} audio");
            }
            else
            {
                throw new AudioStreamNotFoundException { Data = { { "Language", language } } };
            }

            // Get highest bitrate audio stream with selected language
            var audioStream = audioStreamList
                .Where(s => audioLangList.Count <= 0
                            || (s.AudioLanguage?.Name.Contains(language.ToString().Replace("Original", "original")) == true))
                .Maxima(s => s.Bitrate)
                .FirstOrDefault();

            return audioStream;
        }

        private static bool HasAudioLanguage(List<YoutubeExplode.Videos.ClosedCaptions.Language?> audioLangList, YouTubeLang language)
        {
            return audioLangList?.Any(lang => lang?.Name.Contains(language.ToString()) == true) == true;
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
                context.OutgoingResponse.StatusCode = HttpStatusCode.InternalServerError;
                return;
            }

            context.OutgoingResponse.RedirectTo(redirectUri);
        }

        public Task<Stream> GetFile(string videoID, string channelID, string languageString)
        {
            var context = WebOperationContext.Current;
            var mainDirectory = "Videos";
            Enum.TryParse(languageString ?? string.Empty, out YouTubeLang language);
            string channelDirectory = GetChannelDirectory(mainDirectory, channelID, language);

            if (!Directory.Exists(channelDirectory))
            {
                context.OutgoingResponse.StatusCode = HttpStatusCode.NotFound;
                return Task.FromException<Stream>(new FileNotFoundException());
            }

            var fileName = $"{videoID}.mp4";
            var filePath = Path.Combine(channelDirectory, fileName);

            if (!File.Exists(filePath))
            {
                context.OutgoingResponse.StatusCode = HttpStatusCode.NotFound;
                return Task.FromException<Stream>(new FileNotFoundException());
            }

            var fileInfo = new FileInfo(filePath);
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);

            context.OutgoingResponse.ContentType = "video/mp4";
            context.OutgoingResponse.Headers.Add("Content-Disposition", $"inline; filename={fileName}");
            context.OutgoingResponse.Headers.Add("Accept-Ranges", "bytes");

            var rangeHeader = context.IncomingRequest.Headers["Range"];
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                Console.WriteLine("Video Send with Range");

                var range = rangeHeader.Replace("bytes=", "").Split('-');
                var start = long.Parse(range[0]);
                var end = range.Length > 1 && !string.IsNullOrEmpty(range[1]) ? long.Parse(range[1]) : fileInfo.Length - 1;

                if (start >= 0 && end >= start && end < fileInfo.Length)
                {
                    context.OutgoingResponse.StatusCode = HttpStatusCode.PartialContent;
                    context.OutgoingResponse.Headers.Add("Content-Range", $"bytes {start}-{end}/{fileInfo.Length}");
                    context.OutgoingResponse.ContentLength = end - start + 1;

                    stream.Seek(start, SeekOrigin.Begin);
                    return Task.FromResult<Stream>(new PartialStream(stream, start, end));
                }
            }
            else
            {
                Console.WriteLine("Video Send");
            }

            context.OutgoingResponse.ContentLength = fileInfo.Length;
            return Task.FromResult<Stream>(stream);
        }

        private static string GetChannelDirectory(string mainDirectory, string channelID, YouTubeLang language)
        {
            // <ChannelID>_<Language> if other then Original
            return Path.Combine(mainDirectory, language != YouTubeLang.Original ?
                $"{channelID}_{language.ToString().ToLower()}" : channelID);
        }

        public class PartialStream : Stream
        {
            private readonly Stream _innerStream;
            private readonly long _start;
            private readonly long _end;
            private long _position;

            public PartialStream(Stream innerStream, long start, long end)
            {
                _innerStream = innerStream;
                _start = start;
                _end = end;
                _position = start;
            }

            public override bool CanRead => _innerStream.CanRead;
            public override bool CanSeek => _innerStream.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _end - _start + 1;
            public override long Position
            {
                get => _position - _start;
                set => Seek(value, SeekOrigin.Begin);
            }

            public override void Flush() => _innerStream.Flush();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position > _end)
                {
                    return 0;
                }

                var bytesToRead = (int)Math.Min(count, _end - _position + 1);
                var bytesRead = _innerStream.Read(buffer, offset, bytesToRead);
                _position += bytesRead;
                return bytesRead;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long newPosition;
                switch (origin)
                {
                    case SeekOrigin.Begin:
                        newPosition = _start + offset;
                        break;
                    case SeekOrigin.Current:
                        newPosition = _position + offset;
                        break;
                    case SeekOrigin.End:
                        newPosition = _end + offset;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(origin), origin, null);
                }

                if (newPosition < _start || newPosition > _end)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset), offset, null);
                }

                _position = newPosition;
                return _innerStream.Seek(newPosition, SeekOrigin.Begin) - _start;
            }

            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
                // NOTE: PublishedAt is Obsolete but PublishedAtDateTimeOffset is making some requests fail | bug report https://github.com/dotnet/runtime/issues/9364
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
                            baseAddress + $"/Audio.m4a?videoId={playlistItem.Snippet.ResourceId.VideoId}&language={arguments.Language}")).CreateReader());
            }
            else
            {
                item.ElementExtensions.Add(
                    new XElement(
                        "enclosure",
                        new XAttribute("type", "video/mp4"),
                        new XAttribute(
                            "url",
                            baseAddress + $"/Video.mp4?videoId={playlistItem.Snippet.ResourceId.VideoId}&encoding={arguments.Encoding}&language={arguments.Language}")).CreateReader());
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