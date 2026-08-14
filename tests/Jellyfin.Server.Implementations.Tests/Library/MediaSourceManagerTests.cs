using System;
using System.IO;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Server.Implementations.IO;
using Emby.Server.Implementations.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library
{
    public class MediaSourceManagerTests
    {
        private readonly MediaSourceManager _mediaSourceManager;

        public MediaSourceManagerTests()
        {
            IFixture fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
            fixture.Inject<IFileSystem>(fixture.Create<ManagedFileSystem>());
            _mediaSourceManager = fixture.Create<MediaSourceManager>();
        }

        [Theory]
        [InlineData(@"C:\mydir\myfile.ext", MediaProtocol.File)]
        [InlineData("/mydir/myfile.ext", MediaProtocol.File)]
        [InlineData("file:///mydir/myfile.ext", MediaProtocol.File)]
        [InlineData("http://example.com/stream.m3u8", MediaProtocol.Http)]
        [InlineData("https://example.com/stream.m3u8", MediaProtocol.Http)]
        [InlineData("rtsp://media.example.com:554/twister/audiotrack", MediaProtocol.Rtsp)]
        public void GetPathProtocol_ValidArg_Correct(string path, MediaProtocol expected)
            => Assert.Equal(expected, _mediaSourceManager.GetPathProtocol(path));

        [Fact]
        public void GetStaticMediaSources_ServerPlaybackWithCompleteHotFile_ReturnsTransientHotPath()
        {
            var testRoot = Directory.CreateTempSubdirectory("jellyfin-hot-cache-");

            try
            {
                var canonicalRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "media"));
                var hotRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "hot"));
                var relativePath = Path.Combine("tv", "Example", "episode.mkv");
                var canonicalPath = Path.Combine(canonicalRoot.FullName, relativePath);
                var hotPath = Path.Combine(hotRoot.FullName, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(canonicalPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(hotPath)!);
                File.WriteAllText(canonicalPath, "cold-media");
                File.WriteAllText(hotPath, "hot--media");

                IFixture fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
                fixture.Inject<IFileSystem>(fixture.Create<ManagedFileSystem>());
                fixture.Inject<IPlaybackPathResolver>(new PrototypePlaybackPathResolver(canonicalRoot.FullName, hotRoot.FullName));
                var mediaSourceManager = fixture.Create<MediaSourceManager>();
                BaseItem.MediaSourceManager = mediaSourceManager;
                BaseItem.LibraryManager = fixture.Create<ILibraryManager>();
                BaseItem.MediaSegmentManager = fixture.Create<IMediaSegmentManager>();
                Video.RecordingsManager = fixture.Create<IRecordingsManager>();
                var item = new Video
                {
                    Id = Guid.NewGuid(),
                    Path = canonicalPath,
                    Size = new FileInfo(canonicalPath).Length,
                    Container = "mkv",
                    VideoType = VideoType.VideoFile
                };

                var source = Assert.Single(mediaSourceManager.GetStaticMediaSources(item, enablePathSubstitution: false));

                Assert.Equal(hotPath, source.Path);
                Assert.Equal(canonicalPath, item.Path);
            }
            finally
            {
                testRoot.Delete(recursive: true);
            }
        }

        [Fact]
        public void GetStaticMediaSources_ResolverThrows_ReturnsCanonicalPath()
        {
            var testRoot = Directory.CreateTempSubdirectory("jellyfin-hot-cache-");

            try
            {
                var canonicalPath = Path.Combine(testRoot.FullName, "episode.mkv");
                File.WriteAllText(canonicalPath, "cold-media");
                IFixture fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
                fixture.Inject<IFileSystem>(fixture.Create<ManagedFileSystem>());
                fixture.Inject<IPlaybackPathResolver>(new ThrowingPlaybackPathResolver());
                var mediaSourceManager = fixture.Create<MediaSourceManager>();
                BaseItem.MediaSourceManager = mediaSourceManager;
                BaseItem.LibraryManager = fixture.Create<ILibraryManager>();
                BaseItem.MediaSegmentManager = fixture.Create<IMediaSegmentManager>();
                Video.RecordingsManager = fixture.Create<IRecordingsManager>();
                var item = new Video
                {
                    Id = Guid.NewGuid(),
                    Path = canonicalPath,
                    Size = new FileInfo(canonicalPath).Length,
                    Container = "mkv",
                    VideoType = VideoType.VideoFile
                };

                var source = Assert.Single(mediaSourceManager.GetStaticMediaSources(item, enablePathSubstitution: false));

                Assert.Equal(canonicalPath, source.Path);
            }
            finally
            {
                testRoot.Delete(recursive: true);
            }
        }

        private sealed class ThrowingPlaybackPathResolver : IPlaybackPathResolver
        {
            public PlaybackPathResolution Resolve(in PlaybackPathRequest request)
                => throw new IOException("Simulated hot-tier failure.");
        }
    }
}
