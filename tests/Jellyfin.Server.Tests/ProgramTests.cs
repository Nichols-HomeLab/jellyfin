using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Jellyfin.Server.Tests;

public class ProgramTests
{
    [Fact]
    public async Task MigrateSystem_StartupFailureReturnsNonzeroExitCode()
    {
        var directory = Directory.CreateTempSubdirectory("jellyfin-startup-failure-");
        using var process = new Process();
        var started = false;
        try
        {
            var config = Directory.CreateDirectory(Path.Combine(directory.FullName, "config"));
            var blockedPath = Path.Combine(directory.FullName, "blocked-transcodes");
            await File.WriteAllTextAsync(blockedPath, "A file cannot be used as a transcode directory.", TestContext.Current.CancellationToken);
            new XDocument(new XElement("EncodingOptions", new XElement("TranscodingTempPath", blockedPath)))
                .Save(Path.Combine(config.FullName, "encoding.xml"));
            // Bind an ephemeral port so the test does not conflict with another server.
            new XDocument(new XElement("NetworkConfiguration", new XElement("InternalHttpPort", 0)))
                .Save(Path.Combine(config.FullName, "network.xml"));

            process.StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[]
            {
                typeof(Program).Assembly.Location,
                "--mode", "MigrateSystem", "--nowebclient",
                "--datadir", Path.Combine(directory.FullName, "data"),
                "--configdir", config.FullName,
                "--cachedir", Path.Combine(directory.FullName, "cache"),
                "--logdir", Path.Combine(directory.FullName, "log")
            })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            started = process.Start();
            Assert.True(started);
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout + await stderr;
            // Prove that the failure reached the caught startup path, rather than
            // passing because the executable or runtime could not be found.
            Assert.Contains("Error while starting server", output, StringComparison.Ordinal);
            Assert.Contains(blockedPath, output, StringComparison.Ordinal);
            Assert.Equal(1, process.ExitCode);
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            directory.Delete(recursive: true);
        }
    }
}
