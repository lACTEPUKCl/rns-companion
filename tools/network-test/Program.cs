using System.Net;
using System.Net.Sockets;
using RnsCompanion.Services;

static void Check(string name, bool condition)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
{
    Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json"),
};

var attempts = 0;
using (var client = new ApiClient(new FakeHandler((request, ct) =>
{
    if (++attempts < 3) throw new HttpRequestException("Connection reset after wake");
    return Task.FromResult(Json("{\"ok\":true}"));
})))
{
    var result = await client.GetStatusAsync(default);
    Check("public status recovers after transient network errors", result?.Ok == true && attempts == 3);
}

attempts = 0;
using (var client = new ApiClient(new FakeHandler((request, ct) =>
{
    attempts++;
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
})))
{
    try { await client.GetStatusAsync(default); throw new Exception("Expected 401"); }
    catch (ApiException ex) { Check("authorization error is returned without network retries", ex.IsAuthError && attempts == 1); }
}

attempts = 0;
using (var client = new ApiClient(new FakeHandler((request, ct) =>
{
    if (request.Method == HttpMethod.Get) return Task.FromResult(Json("{\"csrfToken\":\"test\"}"));
    attempts++;
    throw new HttpRequestException("Response lost after purchase");
})))
{
    try { await client.BuyVipAsync(default); throw new Exception("Expected network failure"); }
    catch (HttpRequestException) { Check("purchase is never automatically replayed after a lost response", attempts == 1); }
}

attempts = 0;
using (var client = new ApiClient(new FakeHandler((request, ct) =>
{
    attempts++;
    throw new HttpRequestException(HttpRequestError.NameResolutionError, "An error occurred while sending the request",
        new SocketException((int)SocketError.HostNotFound));
})))
{
    try { await client.GetStatusAsync(default); throw new Exception("Expected DNS failure"); }
    catch (HttpRequestException ex)
    {
        Check("network failure includes concrete category and preserves cause",
            ex.Message.Contains("NameResolutionError") && ex.GetBaseException() is SocketException && attempts == 3);
    }
}

using (var cancel = new CancellationTokenSource())
using (var client = new ApiClient(new FakeHandler((request, ct) =>
{
    cancel.Cancel();
    ct.ThrowIfCancellationRequested();
    throw new Exception("Expected cancellation");
})))
{
    try { await client.GetStatusAsync(cancel.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Check("user cancellation stops network retries", true); }
}
Console.WriteLine("ALL NETWORK TESTS PASSED");

internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}

namespace RnsCompanion
{
    internal static class App { public const string ProtocolScheme = "rnscompanion"; }
}
namespace RnsCompanion.Services
{
    internal static class LogService { public static void Warn(string message) { } }
    internal static class GameProcessService { public static bool IsSafeJoinUrl(string? url) => SteamJoinUrl.IsSafe(url); }
}
