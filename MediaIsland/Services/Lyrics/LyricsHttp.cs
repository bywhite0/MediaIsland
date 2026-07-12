using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Lyrics;

internal static class LyricsHttp
{
    public const int DefaultTimeoutSeconds = 6;
    public const int DefaultMaxBytes = 512 * 1024;

    public static HttpClient CreateClient(string name)
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-MediaIsland-Client", name);
        return client;
    }

    public static async Task<string?> ReadBoundedStringAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException($"Response exceeded max size of {maxBytes} bytes.");
            }

            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    public static async Task<JsonDocument?> ReadJsonDocumentAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var text = await ReadBoundedStringAsync(response, maxBytes, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return JsonDocument.Parse(text);
    }

    public static void LogHttpFailure(
        ILogger? logger,
        string provider,
        HttpStatusCode statusCode,
        string? reason = null)
    {
        logger?.LogWarning(
            "[Lyrics:{Provider}] HTTP failure status={Status} reason={Reason}",
            provider,
            (int)statusCode,
            reason ?? statusCode.ToString());
    }
}
