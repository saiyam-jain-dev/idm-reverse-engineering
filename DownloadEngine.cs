using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace DownloadManager;

internal sealed record DownloadTransferProgress(long BytesReceived, long? TotalBytes);

internal static class DownloadEngine
{
    private static readonly HttpClient Client = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task DownloadAsync(
        DownloadJob job,
        IProgress<DownloadTransferProgress> progress,
        CancellationToken cancellationToken,
        bool retriedFromZero = false)
    {
        var partialPath = GetPartialPath(job);
        var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, job.Url);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        using var response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var remoteLength = response.Content.Headers.ContentRange?.Length;
            if (remoteLength == offset)
            {
                File.Move(partialPath, job.DestinationPath);
                progress.Report(new DownloadTransferProgress(offset, remoteLength));
                return;
            }

            if (retriedFromZero)
            {
                throw new HttpRequestException("The server rejected the download range after a restart.");
            }

            response.Dispose();
            File.Delete(partialPath);
            await DownloadAsync(job, progress, cancellationToken, retriedFromZero: true);
            return;
        }

        var isPartialResponse = response.StatusCode == HttpStatusCode.PartialContent;
        if (isPartialResponse)
        {
            var rangeStart = response.Content.Headers.ContentRange?.From;
            if (rangeStart != offset)
            {
                throw new InvalidDataException(
                    $"The server returned an unexpected byte range (expected {offset}, received {rangeStart?.ToString() ?? "none"}).");
            }
        }
        else if (offset > 0)
        {
            offset = 0;
        }

        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentRange?.Length;
        if (totalBytes is null && response.Content.Headers.ContentLength is long contentLength)
        {
            totalBytes = contentLength + offset;
        }

        var fileMode = isPartialResponse && offset > 0 ? FileMode.Append : FileMode.Create;
        await using (var output = new FileStream(
            partialPath,
            fileMode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[64 * 1024];
            var received = offset;
            var lastReportAt = Stopwatch.GetTimestamp();

            progress.Report(new DownloadTransferProgress(received, totalBytes));
            try
            {
                int bytesRead;
                while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    received += bytesRead;

                    if (Stopwatch.GetElapsedTime(lastReportAt) >= TimeSpan.FromMilliseconds(200))
                    {
                        progress.Report(new DownloadTransferProgress(received, totalBytes));
                        lastReportAt = Stopwatch.GetTimestamp();
                    }
                }
            }
            finally
            {
                progress.Report(new DownloadTransferProgress(received, totalBytes));
            }
        }

        File.Move(partialPath, job.DestinationPath);
        progress.Report(new DownloadTransferProgress(
            totalBytes ?? new FileInfo(job.DestinationPath).Length,
            totalBytes ?? new FileInfo(job.DestinationPath).Length));
    }

    public static string GetPartialPath(DownloadJob job) => job.DestinationPath + ".part";

    public static void DeletePartialFile(DownloadJob job)
    {
        var partialPath = GetPartialPath(job);
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }
    }
}
