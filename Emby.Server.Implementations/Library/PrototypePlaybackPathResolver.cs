using System;
using System.IO;
using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Prototype-only canonical-to-hot path resolver used to prove the playback seam.
/// </summary>
public sealed class PrototypePlaybackPathResolver : IPlaybackPathResolver
{
    private readonly string _canonicalRoot;
    private readonly string _hotRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="PrototypePlaybackPathResolver"/> class.
    /// </summary>
    /// <param name="canonicalRoot">The authoritative media root.</param>
    /// <param name="hotRoot">The disposable hot media root.</param>
    public PrototypePlaybackPathResolver(string canonicalRoot, string hotRoot)
    {
        _canonicalRoot = Path.GetFullPath(canonicalRoot);
        _hotRoot = Path.GetFullPath(hotRoot);
    }

    /// <inheritdoc />
    public PlaybackPathResolution Resolve(in PlaybackPathRequest request)
    {
        var canonicalPath = Path.GetFullPath(request.CanonicalPath);
        var relativePath = Path.GetRelativePath(_canonicalRoot, canonicalPath);
        if (relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return new PlaybackPathResolution(request.CanonicalPath, false, "outside-canonical-root");
        }

        var hotPath = Path.GetFullPath(Path.Combine(_hotRoot, relativePath));
        if (!File.Exists(hotPath))
        {
            return new PlaybackPathResolution(request.CanonicalPath, false, "prototype-miss");
        }

        var resolvedHotPath = File.ResolveLinkTarget(hotPath, returnFinalTarget: true)?.FullName ?? hotPath;
        var relativeResolvedHotPath = Path.GetRelativePath(_hotRoot, resolvedHotPath);
        if (relativeResolvedHotPath.Equals("..", StringComparison.Ordinal)
            || relativeResolvedHotPath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return new PlaybackPathResolution(request.CanonicalPath, false, "prototype-hot-root-escape");
        }

        if (request.ExpectedLength.HasValue && new FileInfo(resolvedHotPath).Length != request.ExpectedLength.Value)
        {
            return new PlaybackPathResolution(request.CanonicalPath, false, "prototype-length-mismatch");
        }

        try
        {
            using var stream = new FileStream(
                resolvedHotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 1,
                FileOptions.SequentialScan);
            if (stream.Length > 0 && stream.ReadByte() < 0)
            {
                return new PlaybackPathResolution(request.CanonicalPath, false, "prototype-unreadable");
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new PlaybackPathResolution(request.CanonicalPath, false, "prototype-unreadable");
        }
        catch (IOException)
        {
            return new PlaybackPathResolution(request.CanonicalPath, false, "prototype-unreadable");
        }

        return new PlaybackPathResolution(hotPath, true, "prototype-hit");
    }
}
