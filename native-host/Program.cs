using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace IndependentDownloadManager.NativeHost;

internal static class Program
{
    private const string PipeName = "IndependentDownloadManager.DesktopBridge.v1";
    private const int MaximumMessageBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static async Task<int> Main()
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        byte[]? request = null;
        try
        {
            request = await ReadFrameAsync(input);
            using var requestDocument = JsonDocument.Parse(request);
            EnsureDesktopIsRunning();
            await using var pipe = await ConnectToDesktopAsync();
            await WriteFrameAsync(pipe, request);
            var response = await ReadFrameAsync(pipe);
            await WriteFrameAsync(output, response);
            await output.FlushAsync();
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        {
            var requestId = TryReadRequestId(request);
            var response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                requestId,
                ok = false,
                error = "Could not connect to the desktop download manager. Check that it is installed and try again."
            }, JsonOptions);
            await WriteFrameAsync(output, response);
            await output.FlushAsync();
            return 0;
        }
    }

    private static string? TryReadRequestId(byte[]? request)
    {
        if (request is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(request);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("requestId", out var id) &&
                   id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void EnsureDesktopIsRunning()
    {
        var executablePath = Path.Combine(AppContext.BaseDirectory, "DownloadManager.exe");
        if (!File.Exists(executablePath))
        {
            throw new InvalidOperationException("DownloadManager.exe must be installed beside the native host.");
        }

        var startInfo = new ProcessStartInfo(executablePath)
        {
            ArgumentList = { "--desktop" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var desktop = Process.Start(startInfo);
        if (desktop is not null)
        {
            desktop.StandardInput.Close();
            desktop.StandardOutput.Close();
            desktop.StandardError.Close();
        }
    }

    private static async Task<NamedPipeClientStream> ConnectToDesktopAsync()
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync(500);
                return pipe;
            }
            catch (Exception exception) when (
                exception is TimeoutException or IOException or UnauthorizedAccessException)
            {
                lastException = exception;
                await pipe.DisposeAsync();
                await Task.Delay(250);
            }
        }

        throw new IOException("The desktop application did not start its local bridge in time.", lastException);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream)
    {
        var header = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(header);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("The native messaging frame size is outside the allowed limit.");
        }

        var payload = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(payload);
        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload)
    {
        if (payload.Length is 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("The native messaging response size is outside the allowed limit.");
        }

        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
    }
}
