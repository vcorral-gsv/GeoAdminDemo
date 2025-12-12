using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeoAdminDemo.Services;

public interface IArcgisTokenProvider
{
    Task<string> GetTokenAsync(CancellationToken ct);
}

public sealed class ArcgisTokenProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    IHttpContextAccessor httpContextAccessor
) : IArcgisTokenProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private string? _cachedToken;
    private long _expiresAtUnixMs;
    private readonly Lock _lock = new();
    private Task<string>? _inFlight;

    private const int SkewMs = 60_000;

    public Task<string> GetTokenAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!string.IsNullOrWhiteSpace(_cachedToken) && now < _expiresAtUnixMs - SkewMs)
                return Task.FromResult(_cachedToken!);

            _inFlight ??= FetchAsync(ct);
            return _inFlight;
        }
    }

    private async Task<string> FetchAsync(CancellationToken ct)
    {
        try
        {
            var tokenUrl = config["ArcGIS:TokenUrl"]
                ?? throw new InvalidOperationException("Falta config ArcGIS:TokenUrl");

            var ctx = httpContextAccessor.HttpContext;

            // 1) Override por query/header (Postman)
            var overrideToken = ctx?.Request.Query["arcgisAuth"].ToString();
            if (string.IsNullOrWhiteSpace(overrideToken))
                overrideToken = ctx?.Request.Headers["X-ArcGIS-Auth"].ToString();

            // 2) Config (appsettings)
            var configuredToken = config["ArcGIS:AuthToken"];

            // 3) Authorization entrante (proxy)
            var inboundAuth = ctx?.Request.Headers.Authorization.ToString();

            var authValue =
                !string.IsNullOrWhiteSpace(overrideToken) ? overrideToken :
                !string.IsNullOrWhiteSpace(configuredToken) ? configuredToken :
                inboundAuth;

            if (string.IsNullOrWhiteSpace(authValue))
                throw new InvalidOperationException(
                    "Falta ArcGIS auth. Usa ArcGIS:AuthToken, ?arcgisAuth=, X-ArcGIS-Auth o Authorization."
                );

            var scheme = config["ArcGIS:AuthScheme"];
            if (string.IsNullOrWhiteSpace(scheme))
                scheme = "Bearer";

            var headerValue = BuildAuthorizationHeader(authValue, scheme);

            var client = httpClientFactory.CreateClient("arcgis-token");

            using var req = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
            req.Headers.Authorization = headerValue;

            using var res = await client.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                // ArcGIS suele devolver JSON con error, pero aquí damos un mensaje útil igualmente
                throw new InvalidOperationException(
                    $"ArcGIS token endpoint error {(int)res.StatusCode} ({res.ReasonPhrase}). Body: {body}"
                );
            }

            var dto = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions)
                ?? throw new InvalidOperationException("ArcGIS token endpoint: respuesta inválida (JSON null).");

            if (string.IsNullOrWhiteSpace(dto.AccessToken))
                throw new InvalidOperationException("ArcGIS token endpoint: access_token vacío.");

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (_lock)
            {
                _cachedToken = dto.AccessToken;
                _expiresAtUnixMs = now + (dto.ExpiresIn * 1000L);
            }

            return dto.AccessToken;
        }
        finally
        {
            lock (_lock)
            {
                _inFlight = null;
            }
        }
    }

    private static AuthenticationHeaderValue BuildAuthorizationHeader(string authValue, string defaultScheme)
    {
        // Si viene "Bearer xxx" / "Token xxx" / etc, lo respetamos
        var parts = authValue.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
            return new AuthenticationHeaderValue(parts[0], parts[1]);

        // Si viene raw, aplicamos esquema por defecto (Bearer)
        return new AuthenticationHeaderValue(defaultScheme, authValue);
    }
}
