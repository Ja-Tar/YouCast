using System.IO;
using System.ServiceModel;
using System.ServiceModel.Syndication;
using System.ServiceModel.Web;
using System.Threading.Tasks;

namespace Service
{
    [ServiceContract(SessionMode = SessionMode.NotAllowed)]
    [ServiceKnownType(typeof(Rss20FeedFormatter))]
    public interface IYoutubeFeed
    {
        [OperationContract]
        [WebGet(
            UriTemplate = "GetUserFeed?userId={userId}&encoding={encoding}&language={language}&maxLength={maxLength}&isPopular={isPopular}",
            BodyStyle = WebMessageBodyStyle.Bare)]
        Task<SyndicationFeedFormatter> GetUserFeedAsync(
            string userId,
            string encoding,
            string language,
            int maxLength,
            bool isPopular);

        [OperationContract]
        [WebGet(
            UriTemplate =
                "GetPlaylistFeed?playlistId={playlistId}&encoding={encoding}&language={language}&maxLength={maxLength}&isPopular={isPopular}",
            BodyStyle = WebMessageBodyStyle.Bare)]
        Task<SyndicationFeedFormatter> GetPlaylistFeedAsync(
            string playlistId,
            string encoding,
            string language,
            int maxLength,
            bool isPopular);

        //[OperationContract]
        //[WebGet(
        //    UriTemplate = "GetChannelFeed?channelId={channelId}&encoding={encoding}&maxLength={maxLength}&isPopular={isPopular}&liveCheck={liveCheck}&shortsCheck={shortsCheck}",
        //    BodyStyle = WebMessageBodyStyle.Bare)]

        [OperationContract]
        [WebGet(UriTemplate = "Video.mp4?videoId={videoId}&encoding={encoding}&language={language}")]
        Task GetVideoAsync(string videoId, string encoding, string language);

        [OperationContract]
        [WebGet(UriTemplate = "Audio.m4a?videoId={videoId}&language={language}")]
        Task GetAudioAsync(string videoId, string language);

        [OperationContract]
        [WebGet(UriTemplate = "File.mp4?videoId={videoId}&channelId={channelId}&language={language}")]
        Task<Stream> GetFile(string videoId, string channelId, string language);
    }
}