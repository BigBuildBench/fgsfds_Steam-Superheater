using Common.Client.FilesTools.Interfaces;
using Common.Client.Logger;
using Common.Helpers;
using CommunityToolkit.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Common.Client.FilesTools;

public sealed class FilesDownloader : IFilesDownloader
{
    private readonly ProgressReport _progressReport;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;


    public FilesDownloader(
        ProgressReport progressReport,
        HttpClient httpClient,
        ILogger logger
        )
    {
        _progressReport = progressReport;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<Result> CheckAndDownloadFileAsync(
        Uri url,
        string filePath,
        CancellationToken cancellationToken,
        string? hash = null
        )
    {
        _logger.Info($"Started downloading file {url}");

        IProgress<float> progress = _progressReport.Progress;
        var tempFile = filePath + ".temp";

        if (File.Exists(tempFile))
        {
            File.Delete(tempFile);
        }

        _progressReport.OperationMessage = "Downloading...";

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new(ResultEnum.ConnectionError, "Error while downloading a file: " + response.StatusCode);
        }

        //Hash check before download
        if (hash is not null)
        {
            var result = CheckOnlineFileHash(response, hash);

            if (!result.IsSuccess)
            {
                return result;
            }
        }

        //Downloading
        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var contentLength = response.Content.Headers.ContentLength;

        FileStream fileStream = new(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);

        new Task(() => { TrackProgress(fileStream, progress, contentLength); }).Start();

        try
        {
            await source.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            await fileStream.DisposeAsync().ConfigureAwait(false);

            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }

            return new(ResultEnum.Cancelled, "Downloading cancelled");
        }
        catch (HttpIOException)
        {
            await ContinueDownload(url, contentLength, fileStream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new(ResultEnum.Error, ex.ToString());
        }
        finally
        {
            await fileStream.DisposeAsync().ConfigureAwait(false);
        }

        File.Move(tempFile, filePath);

        //Hash check of downloaded file
        if (hash is not null)
        {
            _progressReport.OperationMessage = "Checking hash...";

            var result = await CheckLocalFileHashAsync(filePath, hash).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return result;
            }
        }

        _progressReport.OperationMessage = string.Empty;
        return new(ResultEnum.Success, string.Empty);
    }


    /// <summary>
    /// Continue download after network error
    /// </summary>
    /// <param name="url">Url to the file</param>
    /// <param name="contentLength">Total content length</param>
    /// <param name="fileStream">File stream to write to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task ContinueDownload(
        Uri url,
        long? contentLength,
        FileStream fileStream,
        CancellationToken cancellationToken
        )
    {
        try
        {
            using HttpRequestMessage request = new()
            {
                RequestUri = url,
                Method = HttpMethod.Get
            };

            request.Headers.Range = new RangeHeaderValue(fileStream.Position, contentLength);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is not System.Net.HttpStatusCode.PartialContent)
            {
                ThrowHelper.ThrowInvalidOperationException("Error while downloading a file: " + response.StatusCode);
            }

            using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await source.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpIOException)
        {
            await ContinueDownload(url, contentLength, fileStream, cancellationToken).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// Check md5 of the local file
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <param name="hash">File md5</param>
    /// <returns></returns>
    private async Task<Result> CheckLocalFileHashAsync(string filePath, string hash)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);

        var fileHash = Convert.ToHexString(await md5.ComputeHashAsync(stream).ConfigureAwait(false));

        if (!hash.Equals(fileHash))
        {
            return new(ResultEnum.MD5Error, "Downloaded file's hash doesn't match the database");
        }

        return new(ResultEnum.Success, string.Empty);
    }

    /// <summary>
    /// Check md5 of the online file
    /// </summary>
    /// <param name="response">Http response</param>
    /// <param name="hash">File md5</param>
    /// <returns></returns>
    private Result CheckOnlineFileHash(HttpResponseMessage response, string hash)
    {
        var url = response.RequestMessage!.RequestUri!.ToString();

        if (url.StartsWith(Consts.FilesBucketUrl))
        {
            if (response.Headers.ETag?.Tag is not null)
            {
                var md5 = response.Headers.ETag!.Tag.Replace("\"", "");

                if (!md5.Contains('-') &&
                    !md5.Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    return new(ResultEnum.MD5Error, "File's hash doesn't match the database");
                }
            }
        }
        else if (response.Content.Headers.ContentMD5 is not null &&
                 !hash.Equals(Convert.ToHexString(response.Content.Headers.ContentMD5)))
        {
            return new(ResultEnum.MD5Error, "File's hash doesn't match the database");
        }

        return new(ResultEnum.Success, string.Empty);
    }

    /// <summary>
    /// Report operation progress
    /// </summary>
    /// <param name="streamToTrack">Stream</param>
    /// <param name="progress">Progress</param>
    /// <param name="contentLength">File size</param>
    private static void TrackProgress(
        FileStream streamToTrack,
        IProgress<float> progress,
        long? contentLength
        )
    {
        if (contentLength is null)
        {
            return;
        }

        while (streamToTrack.CanSeek)
        {
            var pos = streamToTrack.Position / (float)contentLength * 100;
            progress.Report(pos);

            Thread.Sleep(50);
        }
    }
}