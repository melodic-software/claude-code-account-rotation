using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClaudeCodeAccountRotation.Core;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Adapters.Peers;

/// <summary>
/// Reads the follower's loopback token and sends it as a bearer on every peer
/// call. The value is cached. One HTTP 401 reads it again and retries the call
/// once. The token, the file it came from, and the read that produced it are
/// not logged.
/// </summary>
internal sealed partial class FollowerLoopbackTokenHandler : DelegatingHandler
{
    private readonly IFollowerInstanceTokenReader _reader;
    private readonly ILogger<FollowerLoopbackTokenHandler> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cached;
    private bool _disposed;

    public FollowerLoopbackTokenHandler(IFollowerInstanceTokenReader reader, ILogger<FollowerLoopbackTokenHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);
        _reader = reader;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result<string, string> token = await TokenAsync(refresh: false, cancellationToken);
        if (token.IsFailure)
        {
            LogUnavailable();
            return Closed(token.Error);
        }

        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        using HttpRequestMessage first = Clone(request, body, token.Value);
        HttpResponseMessage response = await base.SendAsync(first, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        LogReread();
        Result<string, string> refreshed = await TokenAsync(refresh: true, cancellationToken);
        if (refreshed.IsFailure)
        {
            LogUnavailable();
            return Closed(refreshed.Error);
        }

        using HttpRequestMessage second = Clone(request, body, refreshed.Value);
        return await base.SendAsync(second, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _gate.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    private async Task<Result<string, string>> TokenAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (refresh)
            {
                _cached = null;
            }

            if (_cached is not null)
            {
                return Result<string, string>.Success(_cached);
            }

            Result<string, string> read = await _reader.ReadAsync(cancellationToken);
            if (read.IsSuccess)
            {
                _cached = read.Value;
            }

            return read;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? body, string token)
    {
        HttpRequestMessage clone = new(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };
        foreach ((string key, IEnumerable<string> value) in request.Headers)
        {
            if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            clone.Headers.TryAddWithoutValidation(key, value);
        }

        clone.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is null || request.Content is null)
        {
            return clone;
        }

        ByteArrayContent content = new(body);
        if (request.Content.Headers.ContentType is MediaTypeHeaderValue contentType)
        {
            content.Headers.ContentType = contentType;
        }

        foreach ((string key, IEnumerable<string> value) in request.Content.Headers)
        {
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            content.Headers.TryAddWithoutValidation(key, value);
        }

        clone.Content = content;
        return clone;
    }

    private static HttpResponseMessage Closed(string reason)
    {
        string json = "{\"error\":" + JsonSerializer.Serialize(reason) + "}";
        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "read the follower instance token again after it was refused")]
    private partial void LogReread();

    [LoggerMessage(Level = LogLevel.Warning, Message = "the follower instance token is not available")]
    private partial void LogUnavailable();
}

/// <summary>
/// The follower's current loopback token. A failure is a fixed reason and
/// carries nothing that was read from disk.
/// </summary>
internal interface IFollowerInstanceTokenReader
{
    Task<Result<string, string>> ReadAsync(CancellationToken cancellationToken);
}
