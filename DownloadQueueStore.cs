using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownloadManager;

internal sealed record SavedDownload(
    string Url,
    string DestinationPath,
    DownloadState State,
    string Details,
    long BytesReceived,
    long? TotalBytes,
    Guid Id = default);

internal sealed record SavedDownloadQueue(string DefaultFolder, List<SavedDownload> Downloads);

internal static class DownloadQueueStore
{
    private static readonly string QueuePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IndependentDownloadManager",
        "downloads.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SavedDownloadQueue? Load()
    {
        if (!File.Exists(QueuePath))
        {
            return null;
        }

        using var input = File.OpenRead(QueuePath);
        return JsonSerializer.Deserialize<SavedDownloadQueue>(input, JsonOptions)
            ?? throw new InvalidDataException("The saved download queue is empty or invalid.");
    }

    public static void Save(string defaultFolder, IEnumerable<DownloadJob> jobs)
    {
        var directory = Path.GetDirectoryName(QueuePath)
            ?? throw new InvalidOperationException("The queue storage folder could not be determined.");
        Directory.CreateDirectory(directory);
        var temporaryPath = QueuePath + ".tmp";
        var queue = new SavedDownloadQueue(
            defaultFolder,
            jobs.Select(job => new SavedDownload(
                job.Url,
                job.DestinationPath,
                job.State,
                job.Details,
                job.BytesReceived,
                job.TotalBytes,
                job.Id)).ToList());

        using (var output = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            useAsync: false))
        {
            JsonSerializer.Serialize(output, queue, JsonOptions);
            output.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, QueuePath, overwrite: true);
    }
}
