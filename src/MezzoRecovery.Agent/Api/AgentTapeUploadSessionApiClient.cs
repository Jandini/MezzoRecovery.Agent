using MezzoRecovery.Agent.Contracts;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MezzoRecovery.Agent.Api;

/// <summary>
/// HTTP client for the S3 multipart upload session API endpoints.
/// All calls are authenticated with a bearer token from <see cref="AgentApiClient"/>.
/// </summary>
internal sealed class AgentTapeUploadSessionApiClient(
    HttpClient http,
    ILogger<AgentTapeUploadSessionApiClient>? logger = null)
{
    private const string ApiBase = "api/agent/tape";

    public async Task<StartUploadSessionApiResponse?> StartOrResumeSessionAsync(
        Uri baseUri,
        string bearerToken,
        Guid runId,
        Guid fileId,
        StartUploadSessionApiRequest request,
        CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, $"{ApiBase}/runs/{runId}/files/{fileId}/upload-session"))
        {
            Content = JsonContent.Create(request, AgentJsonContext.Default.StartUploadSessionApiRequest),
        };
        msg.Headers.Authorization = Bearer(bearerToken);

        using var resp = await http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode)
        {
            await LogFailureAsync(resp, "StartOrResumeSession", ct);
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(
            stream, AgentJsonContext.Default.StartUploadSessionApiResponse, ct);
    }

    public async Task<SignPartsApiResponse?> SignPartsAsync(
        Uri baseUri,
        string bearerToken,
        Guid uploadSessionId,
        SignPartsApiRequest request,
        CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, $"{ApiBase}/upload-sessions/{uploadSessionId}/parts/sign"))
        {
            Content = JsonContent.Create(request, AgentJsonContext.Default.SignPartsApiRequest),
        };
        msg.Headers.Authorization = Bearer(bearerToken);

        using var resp = await http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode)
        {
            await LogFailureAsync(resp, "SignParts", ct);
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(
            stream, AgentJsonContext.Default.SignPartsApiResponse, ct);
    }

    public async Task<bool> CompletePartAsync(
        Uri baseUri,
        string bearerToken,
        Guid uploadSessionId,
        int partNumber,
        CompletePartApiRequest request,
        CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, $"{ApiBase}/upload-sessions/{uploadSessionId}/parts/{partNumber}/complete"))
        {
            Content = JsonContent.Create(request, AgentJsonContext.Default.CompletePartApiRequest),
        };
        msg.Headers.Authorization = Bearer(bearerToken);

        using var resp = await http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NoContent)
            await LogFailureAsync(resp, $"CompletePart/{partNumber}", ct);
        return resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NoContent;
    }

    public async Task<CompleteUploadApiResponse?> CompleteSessionAsync(
        Uri baseUri,
        string bearerToken,
        Guid uploadSessionId,
        CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, $"{ApiBase}/upload-sessions/{uploadSessionId}/complete"));
        msg.Headers.Authorization = Bearer(bearerToken);

        using var resp = await http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode)
        {
            await LogFailureAsync(resp, "CompleteSession", ct);
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(
            stream, AgentJsonContext.Default.CompleteUploadApiResponse, ct);
    }

    public async Task<bool> AbortSessionAsync(
        Uri baseUri,
        string bearerToken,
        Guid uploadSessionId,
        CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, $"{ApiBase}/upload-sessions/{uploadSessionId}/abort"));
        msg.Headers.Authorization = Bearer(bearerToken);

        using var resp = await http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NoContent)
            await LogFailureAsync(resp, "AbortSession", ct);
        return resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NoContent;
    }

    public async Task<bool> ReportFailedAsync(
        Uri baseUri,
        string bearerToken,
        Guid uploadSessionId,
        FailUploadApiRequest request,
        CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, $"{ApiBase}/upload-sessions/{uploadSessionId}/failed"))
        {
            Content = JsonContent.Create(request, AgentJsonContext.Default.FailUploadApiRequest),
        };
        msg.Headers.Authorization = Bearer(bearerToken);

        using var resp = await http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NoContent)
            await LogFailureAsync(resp, "ReportFailed", ct);
        return resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NoContent;
    }

    private async Task LogFailureAsync(HttpResponseMessage resp, string operation, CancellationToken ct)
    {
        if (logger is null) return;
        string body;
        try
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);
            body = raw.Length > 512 ? raw[..512] + "…" : raw;
        }
        catch
        {
            body = "(unreadable)";
        }
        logger.LogWarning(
            "{Operation} failed: HTTP {StatusCode} — {Body}",
            operation, (int)resp.StatusCode, body);
    }

    private static System.Net.Http.Headers.AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);
}
