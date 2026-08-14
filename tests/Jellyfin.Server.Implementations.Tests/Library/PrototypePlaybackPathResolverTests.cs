using System;
using System.IO;
using Emby.Server.Implementations.Library;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class PrototypePlaybackPathResolverTests
{
    [Fact]
    public void CanonicalResolver_AlwaysReturnsCanonicalPath()
    {
        var resolver = new CanonicalPlaybackPathResolver();
        const string canonicalPath = "/media/tv/Example/episode.mkv";

        var result = resolver.Resolve(new PlaybackPathRequest(
            canonicalPath,
            123,
            PlaybackPathPurpose.MainMedia));

        Assert.False(result.IsHot);
        Assert.Equal(canonicalPath, result.Path);
        Assert.Equal("hot-cache-disabled", result.Reason);
    }

    [Fact]
    public void Resolve_CompleteSameLengthHotFile_ReturnsHotPath()
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
            var resolver = new PrototypePlaybackPathResolver(canonicalRoot.FullName, hotRoot.FullName);

            var result = resolver.Resolve(new PlaybackPathRequest(
                canonicalPath,
                new FileInfo(canonicalPath).Length,
                PlaybackPathPurpose.MainMedia));

            Assert.True(result.IsHot);
            Assert.Equal(hotPath, result.Path);
            Assert.Equal("prototype-hit", result.Reason);
        }
        finally
        {
            testRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_MissingHotFile_ReturnsCanonicalPath()
    {
        var testRoot = Directory.CreateTempSubdirectory("jellyfin-hot-cache-");

        try
        {
            var canonicalRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "media"));
            var hotRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "hot"));
            var canonicalPath = Path.Combine(canonicalRoot.FullName, "episode.mkv");
            File.WriteAllText(canonicalPath, "cold-media");
            var resolver = new PrototypePlaybackPathResolver(canonicalRoot.FullName, hotRoot.FullName);

            var result = resolver.Resolve(new PlaybackPathRequest(
                canonicalPath,
                new FileInfo(canonicalPath).Length,
                PlaybackPathPurpose.MainMedia));

            Assert.False(result.IsHot);
            Assert.Equal(canonicalPath, result.Path);
            Assert.Equal("prototype-miss", result.Reason);
        }
        finally
        {
            testRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_StaleHotFileLength_ReturnsCanonicalPath()
    {
        var testRoot = Directory.CreateTempSubdirectory("jellyfin-hot-cache-");

        try
        {
            var canonicalRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "media"));
            var hotRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "hot"));
            var canonicalPath = Path.Combine(canonicalRoot.FullName, "episode.mkv");
            var hotPath = Path.Combine(hotRoot.FullName, "episode.mkv");
            File.WriteAllText(canonicalPath, "cold-media");
            File.WriteAllText(hotPath, "stale");
            var resolver = new PrototypePlaybackPathResolver(canonicalRoot.FullName, hotRoot.FullName);

            var result = resolver.Resolve(new PlaybackPathRequest(
                canonicalPath,
                new FileInfo(canonicalPath).Length,
                PlaybackPathPurpose.MainMedia));

            Assert.False(result.IsHot);
            Assert.Equal(canonicalPath, result.Path);
            Assert.Equal("prototype-length-mismatch", result.Reason);
        }
        finally
        {
            testRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_CanonicalPathOutsideConfiguredRoot_ReturnsCanonicalPath()
    {
        var testRoot = Directory.CreateTempSubdirectory("jellyfin-hot-cache-");

        try
        {
            var canonicalRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "media"));
            var hotRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "hot"));
            var outsidePath = Path.Combine(testRoot.FullName, "outside.mkv");
            File.WriteAllText(outsidePath, "cold-media");
            var resolver = new PrototypePlaybackPathResolver(canonicalRoot.FullName, hotRoot.FullName);

            var result = resolver.Resolve(new PlaybackPathRequest(
                outsidePath,
                new FileInfo(outsidePath).Length,
                PlaybackPathPurpose.MainMedia));

            Assert.False(result.IsHot);
            Assert.Equal(outsidePath, result.Path);
            Assert.Equal("outside-canonical-root", result.Reason);
        }
        finally
        {
            testRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_HotSymlinkEscapesRoot_ReturnsCanonicalPath()
    {
        var testRoot = Directory.CreateTempSubdirectory("jellyfin-hot-cache-");

        try
        {
            var canonicalRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "media"));
            var hotRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "hot"));
            var outsideRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "outside"));
            var relativePath = Path.Combine("tv", "Example", "episode.mkv");
            var canonicalPath = Path.Combine(canonicalRoot.FullName, relativePath);
            var hotPath = Path.Combine(hotRoot.FullName, relativePath);
            var outsidePath = Path.Combine(outsideRoot.FullName, "episode.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(canonicalPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(hotPath)!);
            File.WriteAllText(canonicalPath, "cold-media");
            File.WriteAllText(outsidePath, "hot--media");
            File.CreateSymbolicLink(hotPath, outsidePath);
            var resolver = new PrototypePlaybackPathResolver(canonicalRoot.FullName, hotRoot.FullName);

            var result = resolver.Resolve(new PlaybackPathRequest(
                canonicalPath,
                new FileInfo(canonicalPath).Length,
                PlaybackPathPurpose.MainMedia));

            Assert.False(result.IsHot);
            Assert.Equal(canonicalPath, result.Path);
            Assert.Equal("prototype-hot-root-escape", result.Reason);
        }
        finally
        {
            testRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_UnreadableHotFile_ReturnsCanonicalPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Directory.CreateTempSubdirectory("jellyfin-hot-cache-");
        string? hotPath = null;

        try
        {
            var canonicalRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "media"));
            var hotRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "hot"));
            var relativePath = Path.Combine("tv", "Example", "episode.mkv");
            var canonicalPath = Path.Combine(canonicalRoot.FullName, relativePath);
            hotPath = Path.Combine(hotRoot.FullName, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(canonicalPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(hotPath)!);
            File.WriteAllText(canonicalPath, "cold-media");
            File.WriteAllText(hotPath, "hot--media");
            File.SetUnixFileMode(hotPath, UnixFileMode.None);
            var resolver = new PrototypePlaybackPathResolver(canonicalRoot.FullName, hotRoot.FullName);

            var result = resolver.Resolve(new PlaybackPathRequest(
                canonicalPath,
                new FileInfo(canonicalPath).Length,
                PlaybackPathPurpose.MainMedia));

            Assert.False(result.IsHot);
            Assert.Equal(canonicalPath, result.Path);
            Assert.Equal("prototype-unreadable", result.Reason);
        }
        finally
        {
            if (hotPath is not null && File.Exists(hotPath))
            {
                File.SetUnixFileMode(hotPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            testRoot.Delete(recursive: true);
        }
    }
}
