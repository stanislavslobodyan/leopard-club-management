using System.Net.Http.Json;
using System.Text.Json;

namespace LEOPARD.Services;

public sealed class ComputerAgentClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public async Task<bool> IsAvailableAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate($"{endpoint.TrimEnd('/')}/api/v1/health", UriKind.Absolute, out var uri))
            return false;

        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
