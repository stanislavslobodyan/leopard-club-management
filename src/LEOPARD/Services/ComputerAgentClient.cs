using System.Net.Http.Json;
using System.Text.Json;

namespace LEOPARD.Services;

public sealed class ComputerAgentClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public async Task<bool> IsAvailableAsync(string endpoint, string connectionKey = "", CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate($"{endpoint.TrimEnd('/')}/api/v1/health", UriKind.Absolute, out var uri))
            return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(connectionKey))
                request.Headers.TryAddWithoutValidation("X-Leopard-Agent-Key", connectionKey);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            var health = await response.Content.ReadFromJsonAsync<AgentHealthResponse>(cancellationToken: cancellationToken);
            return health?.Health == "ready" && !string.IsNullOrWhiteSpace(health.AgentId);
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
        catch (JsonException) { return false; }
    }

    private sealed record AgentHealthResponse(string AgentId, string AgentVersion, DateTimeOffset ObservedAt, string Health);
}
