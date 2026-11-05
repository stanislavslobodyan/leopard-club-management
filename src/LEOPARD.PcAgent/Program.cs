using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

const int defaultPort = 47831;
const int maxConcurrentRequests = 16;

var port = defaultPort;
if (args.Length > 0 && (!int.TryParse(args[0], out port) || port is < 1024 or > 65535))
{
    Console.Error.WriteLine("Usage: LEOPARD.PcAgent [port 1024..65535]");
    return 2;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

var listener = new TcpListener(IPAddress.Any, port);
var concurrency = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
listener.Start();
Console.WriteLine($"LEOPARD PC Agent {typeof(Program).Assembly.GetName().Version} is listening on TCP {port}.");
Console.WriteLine("Health endpoint: GET /api/v1/health. Press Ctrl+C to stop.");

try
{
    while (!shutdown.IsCancellationRequested)
    {
        await concurrency.WaitAsync(shutdown.Token);
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(shutdown.Token); }
        catch
        {
            concurrency.Release();
            throw;
        }
        _ = HandleClientAsync(client, concurrency, shutdown.Token);
    }
}
catch (OperationCanceledException) { }
catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { }
finally
{
    listener.Stop();
}

return 0;

static async Task HandleClientAsync(TcpClient client, SemaphoreSlim concurrency, CancellationToken shutdown)
{
    using (client)
    using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
    {
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        var entered = true;
        try
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(requestTimeout.Token);
            if (string.IsNullOrWhiteSpace(requestLine)) return;

            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await WriteResponseAsync(stream, 400, "Bad Request", "text/plain", "Bad Request", requestTimeout.Token);
                return;
            }

            var method = parts[0];
            var path = parts[1].Split('?', 2)[0];
            if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 405, "Method Not Allowed", "text/plain", "Method Not Allowed", requestTimeout.Token);
                return;
            }

            if (path.Equals("/api/v1/health", StringComparison.OrdinalIgnoreCase))
            {
                var response = JsonSerializer.Serialize(new HealthResponse(
                    Environment.MachineName,
                    typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    DateTimeOffset.UtcNow,
                    "ready"));
                await WriteResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", response, requestTimeout.Token);
            }
            else
            {
                await WriteResponseAsync(stream, 404, "Not Found", "text/plain", "Not Found", requestTimeout.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (entered) concurrency.Release();
        }
    }
}

static async Task WriteResponseAsync(NetworkStream stream, int status, string reason, string contentType, string body, CancellationToken cancellationToken)
{
    var bodyBytes = Encoding.UTF8.GetBytes(body);
    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
    await stream.WriteAsync(headers, cancellationToken);
    await stream.WriteAsync(bodyBytes, cancellationToken);
    await stream.FlushAsync(cancellationToken);
}

internal sealed record HealthResponse(string AgentId, string AgentVersion, DateTimeOffset ObservedAt, string Health);
