using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace DownloadManager;

internal sealed class DesktopBridgeServer : IAsyncDisposable
{
    public const string PipeName = "IndependentDownloadManager.DesktopBridge.v1";
    private const int MaximumMessageBytes = 1024 * 1024;
    private const int MaximumConcurrentClients = 8;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<JsonElement, Task<object?>> _requestHandler;
    private readonly Action<string> _reportError;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _clientSlots = new(MaximumConcurrentClients);
    private readonly ConcurrentDictionary<long, Task> _clientTasks = new();
    private Task? _listenerTask;
    private long _nextClientId;

    public DesktopBridgeServer(
        Func<JsonElement, Task<object?>> requestHandler,
        Action<string> reportError)
    {
        _requestHandler = requestHandler;
        _reportError = reportError;
    }

    public void Start()
    {
        if (_listenerTask is not null)
        {
            throw new InvalidOperationException("The desktop bridge server has already started.");
        }

        _listenerTask = ListenAsync(_shutdown.Token);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            var clientSlotAcquired = false;
            try
            {
                await _clientSlots.WaitAsync(cancellationToken);
                clientSlotAcquired = true;
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    MaximumConcurrentClients,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);

                var connectedClient = server;
                server = null;
                clientSlotAcquired = false;
                var clientId = Interlocked.Increment(ref _nextClientId);
                var clientTask = HandleClientAndReleaseSlotAsync(connectedClient, cancellationToken);
                _clientTasks.TryAdd(clientId, clientTask);
                _ = clientTask.ContinueWith(
                    completedTask => _clientTasks.TryRemove(clientId, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or ObjectDisposedException)
            {
                if (clientSlotAcquired)
                {
                    _clientSlots.Release();
                }
                if (!cancellationToken.IsCancellationRequested)
                {
                    _reportError($"Desktop extension bridge listener failed: {exception.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task HandleClientAndReleaseSlotAsync(
        NamedPipeServerStream client,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (client)
            {
                await HandleClientAsync(client, cancellationToken);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
            OperationCanceledException or ObjectDisposedException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _reportError($"Desktop extension bridge request failed: {exception.Message}");
            }
        }
        finally
        {
            _clientSlots.Release();
        }
    }

    private async Task HandleClientAsync(Stream client, CancellationToken cancellationToken)
    {
        var requestBytes = await ReadFrameAsync(client, cancellationToken);
        using var requestDocument = JsonDocument.Parse(requestBytes);
        var request = requestDocument.RootElement;
        var requestId = request.ValueKind == JsonValueKind.Object &&
                        request.TryGetProperty("requestId", out var requestIdElement) &&
                        requestIdElement.ValueKind == JsonValueKind.String
            ? requestIdElement.GetString()
            : null;

        byte[] responseBytes;
        try
        {
            if (request.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("A bridge request must be a JSON object.");
            }

            var result = await _requestHandler(request);
            responseBytes = JsonSerializer.SerializeToUtf8Bytes(
                new { requestId, ok = true, result },
                JsonOptions);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or NotSupportedException or
            FormatException or System.ComponentModel.Win32Exception)
        {
            responseBytes = JsonSerializer.SerializeToUtf8Bytes(
                new { requestId, ok = false, error = GetSafeError(exception) },
                JsonOptions);
        }

        await WriteFrameAsync(client, responseBytes, cancellationToken);
        await client.FlushAsync(cancellationToken);
    }

    private static string GetSafeError(Exception exception) => exception switch
    {
        InvalidDataException or InvalidOperationException => exception.Message,
        ArgumentException => "The request contains an invalid value.",
        _ => "The desktop request could not be completed."
    };

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("The bridge request size is outside the allowed limit.");
        }

        var payload = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length is 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("The bridge response size is outside the allowed limit.");
        }

        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Task.WhenAll(_clientTasks.Values);
        _clientSlots.Dispose();
        _shutdown.Dispose();
    }
}
