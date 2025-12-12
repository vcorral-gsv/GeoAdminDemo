using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeoAdminDemo.Services;

public sealed class ArcgisGeocodingService(
    IHttpClientFactory httpClientFactory,
    IArcgisTokenProvider tokenProvider,
    IConfiguration config)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class FindCandidatesResponse
    {
        [JsonPropertyName("candidates")]
        public List<Candidate>? Candidates { get; set; }
    }

    private sealed class Candidate
    {
        [JsonPropertyName("location")]
        public ArcgisLocation? Location { get; set; }
    }

    private sealed class ArcgisLocation
    {
        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }
    }

    public async Task<(double lat, double lon)> GeocodeAsync(
        string address,
        string? language,
        CancellationToken ct)
    {
        var baseUrl = config["ArcGIS:GeocodeBaseUrl"]
            ?? "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer";

        var token = await tokenProvider.GetTokenAsync(ct);

        var url =
            $"{baseUrl}/findAddressCandidates" +
            $"?f=json" +
            $"&singleLine={Uri.EscapeDataString(address)}" +
            $"&maxLocations=1" +
            $"&outFields=*" +
            (string.IsNullOrWhiteSpace(language) ? "" : $"&langCode={Uri.EscapeDataString(language)}") +
            $"&token={Uri.EscapeDataString(token)}";

        var client = httpClientFactory.CreateClient("arcgis-geocode");

        using var res = await client.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize<FindCandidatesResponse>(json, JsonOptions);

        var loc = (dto?.Candidates?.FirstOrDefault()?.Location) ?? throw new InvalidOperationException("ArcGIS: no se encontraron candidatos para esa dirección.");

        // ArcGIS: X=lon, Y=lat
        return (lat: loc.Y, lon: loc.X);
    }
}
