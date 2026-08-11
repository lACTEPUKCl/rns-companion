using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RnsCompanion.Models;

namespace RnsCompanion.Services;

/// <summary>
/// HTTP-клиент API rnserver.ru. Все запросы — только на настраиваемый BaseUrl.
/// Авторизация: Bearer JWT. Мутирующие POST (start/stop) дополнительно требуют
/// CSRF: берём токен с /api/csrf-token (он же ставит подписанную куку, которую
/// держим в CookieContainer) и шлём заголовок X-CSRF-Token.
/// </summary>
internal sealed class ApiClient : IDisposable
{
    public const string DefaultBaseUrl = "https://rnserver.ru";

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private string _baseUrl = DefaultBaseUrl;
    private string? _csrfToken;

    public string? Token { get; set; }

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            var url = string.IsNullOrWhiteSpace(value) ? DefaultBaseUrl : value.Trim().TrimEnd('/');
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            if (_baseUrl != url)
            {
                _baseUrl = url;
                _csrfToken = null; // CSRF-токен привязан к origin
            }
        }
    }

    public ApiClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("RNS-Companion",
                typeof(ApiClient).Assembly.GetName().Version?.ToString() ?? "1.0"));
    }

    public string BuildAuthUrl() =>
        $"{_baseUrl}/api/auth/desktop?redirect={App.ProtocolScheme}://auth";

    /// <summary>POST /api/auth/desktop/exchange {code} → JWT.</summary>
    public async Task<string> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Post, "/api/auth/desktop/exchange",
            JsonContent.Create(new { code }), csrf: false, ct);
        var data = await ReadJsonAsync<ExchangeResponse>(resp, ct);
        if (!resp.IsSuccessStatusCode || data?.Token is not { Length: > 0 } token)
            throw new ApiException(resp.StatusCode, "Не удалось обменять код на токен. Попробуйте войти ещё раз.");
        return token;
    }

    public Task<AutoseedMyResponse?> GetMyAsync(CancellationToken ct) =>
        GetAsync<AutoseedMyResponse>("/api/seed/my", ct);

    public Task<AutoseedStatusResponse?> GetStatusAsync(CancellationToken ct) =>
        GetAsync<AutoseedStatusResponse>("/api/seed/status", ct);

    /// <summary>steam:// ссылка подключения к серверу по имени (публичный join-link).</summary>
    public async Task<string?> GetJoinUrlAsync(string serverName, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get,
            $"/api/sqb/join-link?format=json&name={Uri.EscapeDataString(serverName)}",
            null, csrf: false, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var data = await ReadJsonAsync<JoinLinkResponse>(resp, ct);
        return data?.JoinUrl;
    }

    /// <summary>POST /api/seed/start {client:"desktop"} (CSRF).</summary>
    public Task StartSeedAsync(CancellationToken ct) =>
        PostWithCsrfAsync("/api/seed/start", new { client = "desktop" }, ct);

    /// <summary>POST /api/seed/stop (CSRF).</summary>
    public Task StopSeedAsync(CancellationToken ct) =>
        PostWithCsrfAsync("/api/seed/stop", new { }, ct);

    /// <summary>GET /api/vip/my — баланс бонусов и статус личной VIP.</summary>
    public Task<VipMyResponse?> GetVipMyAsync(CancellationToken ct) =>
        GetAsync<VipMyResponse>("/api/vip/my", ct);

    /// <summary>POST /api/vip/buy (CSRF) — купить/продлить VIP за бонусы.</summary>
    public Task BuyVipAsync(CancellationToken ct) =>
        PostWithCsrfAsync("/api/vip/buy", new { }, ct);

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, path, null, csrf: false, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new ApiException(resp.StatusCode, "Сессия истекла — войдите заново.");
        if (!resp.IsSuccessStatusCode)
            throw new ApiException(resp.StatusCode, $"Сервер ответил {(int)resp.StatusCode} на GET {path}.");
        return await ReadJsonAsync<T>(resp, ct);
    }

    private async Task PostWithCsrfAsync(string path, object body, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await EnsureCsrfAsync(ct);
            using var resp = await SendAsync(HttpMethod.Post, path,
                JsonContent.Create(body), csrf: true, ct);
            var responseBody = await resp.Content.ReadAsStringAsync(ct);

            // Окно набора закрыто — сервер отвечает {code:"seed-window-closed", opensAt}.
            if (TryParseWindowClosed(responseBody, out var opensAt))
                throw new SeedWindowClosedException(opensAt);

            if (resp.IsSuccessStatusCode) return;
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new ApiException(resp.StatusCode, "Сессия истекла — войдите заново.");
            if (resp.StatusCode == HttpStatusCode.BadRequest &&
                responseBody.Contains("insufficient-funds"))
                throw new ApiException(resp.StatusCode, "Не хватает бонусов для покупки VIP.");
            if (resp.StatusCode == HttpStatusCode.Forbidden && attempt == 0)
            {
                // Вероятно, протухший CSRF-токен — обновляем и повторяем один раз.
                _csrfToken = null;
                continue;
            }
            if (resp.StatusCode == HttpStatusCode.Conflict)
                throw new ApiException(resp.StatusCode,
                    "Сервер отклонил запрос: к аккаунту не привязан Steam (привяжите на сайте).");
            throw new ApiException(resp.StatusCode, $"Сервер ответил {(int)resp.StatusCode} на POST {path}.");
        }
    }

    private static bool TryParseWindowClosed(string body, out DateTime? opensAt)
    {
        opensAt = null;
        if (string.IsNullOrWhiteSpace(body) || !body.Contains("seed-window-closed")) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("code", out var code) ||
                code.GetString() != "seed-window-closed")
                return false;
            if (doc.RootElement.TryGetProperty("opensAt", out var oa) &&
                oa.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(oa.GetString(), out var dt))
                opensAt = dt;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private async Task EnsureCsrfAsync(CancellationToken ct)
    {
        if (_csrfToken is not null) return;
        using var resp = await SendAsync(HttpMethod.Get, "/api/csrf-token", null, csrf: false, ct);
        if (!resp.IsSuccessStatusCode)
            throw new ApiException(resp.StatusCode, "Не удалось получить CSRF-токен.");
        var data = await ReadJsonAsync<CsrfResponse>(resp, ct);
        _csrfToken = data?.CsrfToken
            ?? throw new ApiException(resp.StatusCode, "Сервер не вернул CSRF-токен.");
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, HttpContent? content, bool csrf, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, _baseUrl + path);
        if (Token is { } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (csrf && _csrfToken is not null)
            req.Headers.TryAddWithoutValidation("X-CSRF-Token", _csrfToken);
        if (content is not null)
            req.Content = content;
        return _http.SendAsync(req, ct);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct); }
        catch (JsonException) { return default; }
        catch (NotSupportedException) { return default; }
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public ApiException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public bool IsAuthError => StatusCode is HttpStatusCode.Unauthorized;
}

/// <summary>Окно набора закрыто (сервер: {code:"seed-window-closed", opensAt}).</summary>
internal sealed class SeedWindowClosedException : Exception
{
    public DateTime? OpensAt { get; }

    public SeedWindowClosedException(DateTime? opensAt)
        : base("Окно набора закрыто.")
    {
        OpensAt = opensAt;
    }
}
