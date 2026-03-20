using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Hotkey_Translator.Services;

internal sealed class OneOcrProtocolClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private NamedPipeClientStream? _pipe;

    public bool IsConnected => _pipe is { IsConnected: true };

    public async Task ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DisposePipe();
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await _pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    public Task<TResponse> SendAsync<TResponse>(object request, CancellationToken cancellationToken)
    {
        return SendAsync<TResponse>(JsonSerializer.Serialize(request, JsonOptions), cancellationToken);
    }

    public async Task<TResponse> SendAsync<TResponse>(string requestJson, CancellationToken cancellationToken)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("OneOCR pipe is not connected.");
        await WriteMessageAsync(pipe, requestJson, cancellationToken).ConfigureAwait(false);
        var responseJson = await ReadMessageAsync(pipe, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<TResponse>(responseJson, JsonOptions);
        return response ?? throw new InvalidOperationException("OneOCR helper returned an empty response.");
    }

    public async Task<TResponse> ReadAsync<TResponse>(CancellationToken cancellationToken)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("OneOCR pipe is not connected.");
        var responseJson = await ReadMessageAsync(pipe, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<TResponse>(responseJson, JsonOptions);
        return response ?? throw new InvalidOperationException("OneOCR helper returned an empty response.");
    }

    public void Dispose()
    {
        DisposePipe();
        GC.SuppressFinalize(this);
    }

    private void DisposePipe()
    {
        try
        {
            _pipe?.Dispose();
        }
        finally
        {
            _pipe = null;
        }
    }

    private static async Task WriteMessageAsync(Stream stream, string message, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0)
        {
            throw new InvalidDataException("OneOCR helper returned a negative message length.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("OneOCR helper closed the pipe unexpectedly.");
            }

            totalRead += read;
        }
    }
}

internal sealed class OneOcrRequestMessage
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? ImageFormat { get; set; }
    public string? ImageBytesBase64 { get; set; }
    public int? MaxLineCount { get; set; }
}

internal sealed class OneOcrReadyResponse
{
    public string? Type { get; set; }
    public bool Ok { get; set; }
    public string? Version { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

internal sealed class OneOcrRecognizeResponse
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public double DurationMs { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public double ImageAngle { get; set; }
    public OneOcrLinePayload[]? Lines { get; set; }
}

internal sealed class OneOcrLinePayload
{
    public string? Text { get; set; }
    public double[]? Bbox { get; set; }
    public double[][]? Polygon { get; set; }
    public OneOcrWordPayload[]? Words { get; set; }
}

internal sealed class OneOcrWordPayload
{
    public string? Text { get; set; }
    public double Confidence { get; set; }
    public double[]? Bbox { get; set; }
    public double[][]? Polygon { get; set; }
}
