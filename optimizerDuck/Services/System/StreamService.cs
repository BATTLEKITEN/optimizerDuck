using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;

namespace optimizerDuck.Services.System;

public class StreamService(ILogger<StreamService> logger) : IDisposable
{
    private HttpClient? _client;

    /// <summary>
    ///     Downloads a file over HTTPS into the administrators-only downloads directory and keeps it
    ///     only when its SHA-256 matches. Each download gets a fresh name, so nothing already on
    ///     disk is ever reused.
    /// </summary>
    /// <param name="url">The HTTPS URL to download from.</param>
    /// <param name="fileName">The file name to keep; any directory part is dropped.</param>
    /// <param name="expectedSha256">The expected SHA-256 of the content, in hex.</param>
    /// <returns>
    ///     A <see cref="DownloadResult"/> where <c>Ok</c> indicates whether the download completed
    ///     and verified, and <c>FilePath</c> is the full local path on success.
    /// </returns>
    /// <example>
    /// <code language="csharp">
    /// var download = await streamService.TryDownloadAsync("https://example.com/file.zip",
    ///     "file.zip", "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");
    /// if (download.Ok) Console.WriteLine(download.FilePath);
    /// </code>
    /// </example>
    public async Task<DownloadResult> TryDownloadAsync(
        string url,
        string fileName,
        string expectedSha256
    )
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogError("Refusing to download from a non HTTPS URL {Url}", url);
            return new DownloadResult(false, null);
        }

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || string.IsNullOrWhiteSpace(expectedSha256))
        {
            logger.LogError("Refusing to download {Url} without a file name and a hash", url);
            return new DownloadResult(false, null);
        }

        if (!SecureDirectory.EnsureAdminOnly(Shared.SecureDataDirectory, logger))
            return new DownloadResult(false, null);

        var directory = Path.Combine(Shared.DownloadsDirectory, Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, safeName);

        logger.LogInformation("Starting download from {Url} to {FilePath}", url, filePath);

        try
        {
            Directory.CreateDirectory(directory);
            _client ??= HttpClientFactory.CreateClient(logger: logger);
            using var response = await _client.GetAsync(uri).ConfigureAwait(false);

            logger.LogDebug("Received HTTP {StatusCode} from {Url}", response.StatusCode, url);

            response.EnsureSuccessStatusCode();

            byte[] hash;
            long length;
            await using (
                var fs = new FileStream(
                    filePath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None
                )
            )
            {
                await response.Content.CopyToAsync(fs).ConfigureAwait(false);
                length = fs.Length;
                fs.Position = 0;
                hash = await SHA256.HashDataAsync(fs).ConfigureAwait(false);
            }

            if (!HashMatches(hash, expectedSha256))
            {
                logger.LogError(
                    "Downloaded {Url} does not match its expected SHA-256 {Expected} (got {Actual})",
                    url,
                    expectedSha256,
                    Convert.ToHexString(hash)
                );
                TryDeleteDirectory(directory);
                return new DownloadResult(false, null);
            }

            logger.LogInformation(
                "Successfully downloaded {Length} bytes from {Url} to {FilePath}",
                length,
                url,
                filePath
            );

            return new DownloadResult(true, filePath);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error while downloading {Url}", url);
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "File I/O error while saving {FilePath}", filePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogError(ex, "Access denied when writing to {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error downloading {Url} to {FilePath}", url, filePath);
        }

        TryDeleteDirectory(directory);
        return new DownloadResult(false, null);
    }

    internal static bool HashMatches(byte[] actual, string expectedHex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                actual,
                Convert.FromHexString(expectedHex.Trim())
            );
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove the failed download in {Path}", directory);
        }
    }

    /// <summary>Releases the underlying <see cref="HttpClient"/> resources.</summary>
    public void Dispose()
    {
        _client?.Dispose();
    }
}
