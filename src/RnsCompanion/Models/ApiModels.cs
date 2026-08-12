using System.Text.Json.Serialization;

namespace RnsCompanion.Models;

/// <summary>Целевой сервер набора (поле target в ответах API).</summary>
public sealed class TargetInfo
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }
    [JsonPropertyName("map")] public string? Map { get; set; }
}

/// <summary>Открытая сессия набора (поле session в /api/seed/my).</summary>
public sealed class SessionInfo
{
    [JsonPropertyName("startedAt")] public DateTime StartedAt { get; set; }
    [JsonPropertyName("minutes")] public int Minutes { get; set; }
    [JsonPropertyName("bonusesEarned")] public int BonusesEarned { get; set; }
}

/// <summary>Окно набора (из /api/seed/status и /api/seed/my).</summary>
public sealed class SeedWindowInfo
{
    [JsonPropertyName("open")] public bool Open { get; set; }
    [JsonPropertyName("startHour")] public int StartHour { get; set; }
    [JsonPropertyName("endHour")] public int? EndHour { get; set; }
    [JsonPropertyName("opensAt")] public DateTime? OpensAt { get; set; }

    /// <summary>«в 06:00» — по opensAt, иначе по startHour.</summary>
    public string DescribeOpening() =>
        OpensAt is { } t ? "в " + t.ToLocalTime().ToString("HH:mm") : $"в {StartHour:00}:00";
}

/// <summary>GET /api/seed/my</summary>
public sealed class AutoseedMyResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("steamLinked")] public bool SteamLinked { get; set; }
    [JsonPropertyName("onTarget")] public bool OnTarget { get; set; }
    [JsonPropertyName("session")] public SessionInfo? Session { get; set; }
    [JsonPropertyName("target")] public TargetInfo? Target { get; set; }
    [JsonPropertyName("joinUrl")] public string? JoinUrl { get; set; }

    /// <summary>Оценочный курс бонусов для отображения (начисление делает сервер/RNSquadJS;
    /// может отсутствовать — тогда клиент считает по умолчанию 5/мин).</summary>
    [JsonPropertyName("bonusDisplayRate")] public int? BonusDisplayRate { get; set; }

    /// <summary>Окно набора; null у старого бэкенда — считаем открытым.</summary>
    [JsonPropertyName("window")] public SeedWindowInfo? Window { get; set; }

    /// <summary>Все живые серверы выше порога — набор завершён (null у старого бэкенда).</summary>
    [JsonPropertyName("allSeeded")] public bool? AllSeeded { get; set; }
}

/// <summary>Элемент списка серверов в GET /api/seed/status.</summary>
public sealed class ServerStatusInfo
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

/// <summary>GET /api/seed/status (публичный).</summary>
public sealed class AutoseedStatusResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("threshold")] public int Threshold { get; set; }
    [JsonPropertyName("target")] public TargetInfo? Target { get; set; }
    [JsonPropertyName("servers")] public List<ServerStatusInfo>? Servers { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }
    [JsonPropertyName("window")] public SeedWindowInfo? Window { get; set; }
    [JsonPropertyName("allSeeded")] public bool? AllSeeded { get; set; }
}

/// <summary>GET /api/vip/my — баланс бонусов и статус личной VIP.</summary>
public sealed class VipMyResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("bonuses")] public int Bonuses { get; set; }
    [JsonPropertyName("vipEndDate")] public DateTime? VipEndDate { get; set; }
    [JsonPropertyName("vipActive")] public bool VipActive { get; set; }
    [JsonPropertyName("price")] public int Price { get; set; }
    [JsonPropertyName("days")] public int Days { get; set; }
    [JsonPropertyName("missing")] public int Missing { get; set; }
}

/// <summary>GET /api/sqb/join-link?format=json → { joinUrl }</summary>
public sealed class JoinLinkResponse
{
    [JsonPropertyName("joinUrl")] public string? JoinUrl { get; set; }
}

/// <summary>POST /api/auth/desktop/exchange → { ok, token }</summary>
public sealed class ExchangeResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
}

/// <summary>GET /api/csrf-token → { csrfToken }</summary>
public sealed class CsrfResponse
{
    [JsonPropertyName("csrfToken")] public string? CsrfToken { get; set; }
}

/// <summary>Сохранённая сессия авторизации (в DPAPI-хранилище).</summary>
public sealed class AuthState
{
    public string Token { get; set; } = "";
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Элемент списка новостей Squad (GET /api/news, публичный).</summary>
public sealed class NewsItemSummary
{
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("titleRu")] public string? TitleRu { get; set; }
    [JsonPropertyName("excerptRu")] public string? ExcerptRu { get; set; }
    [JsonPropertyName("coverImage")] public string? CoverImage { get; set; }
    [JsonPropertyName("publishedAt")] public DateTime PublishedAt { get; set; }
    [JsonPropertyName("sourceUrl")] public string? SourceUrl { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
}

/// <summary>GET /api/news → { items }</summary>
public sealed class NewsListResponse
{
    [JsonPropertyName("items")] public List<NewsItemSummary>? Items { get; set; }
}

/// <summary>Полная статья (GET /api/news/{slug}, публичный).</summary>
public sealed class NewsItemDetail
{
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("titleRu")] public string? TitleRu { get; set; }
    [JsonPropertyName("contentHtmlRu")] public string? ContentHtmlRu { get; set; }
    [JsonPropertyName("publishedAt")] public DateTime PublishedAt { get; set; }
    [JsonPropertyName("sourceUrl")] public string? SourceUrl { get; set; }
}

/// <summary>GET /api/news/{slug} → { item }</summary>
public sealed class NewsItemResponse
{
    [JsonPropertyName("item")] public NewsItemDetail? Item { get; set; }
}
