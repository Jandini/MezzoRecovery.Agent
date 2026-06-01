using System.Net.Http.Json;
using System.Text.Json;
using MezzoRecovery.Agent.Contracts;

namespace MezzoRecovery.Agent.Api;

public sealed class AgentApiClient(HttpClient http)
{
    public async Task<EnrollApiResponse?> EnrollAsync(
        Uri baseUri,
        EnrollApiRequest request,
        CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/agent/enroll"))
        {
            Content = JsonContent.Create(request, AgentJsonContext.Default.EnrollApiRequest),
        };
        using var resp = await http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, AgentJsonContext.Default.EnrollApiResponse, ct);
    }

    public async Task<TokenApiResponse?> GetTokenAsync(Uri baseUri, TokenApiRequest request, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/agent/token"))
        {
            Content = JsonContent.Create(request, AgentJsonContext.Default.TokenApiRequest),
        };
        using var resp = await http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, AgentJsonContext.Default.TokenApiResponse, ct);
    }

    public async Task<PendingUploadItem[]?> GetPendingUploadsAsync(
        Uri baseUri, string bearerToken, CancellationToken ct, Guid? runId = null)
    {
        var url = runId.HasValue
            ? $"api/agent/tape/uploads/pending?runId={runId:D}"
            : "api/agent/tape/uploads/pending";
        using var msg = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, url));
        msg.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        using var resp = await http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, AgentJsonContext.Default.PendingUploadItemArray, ct);
    }
}
