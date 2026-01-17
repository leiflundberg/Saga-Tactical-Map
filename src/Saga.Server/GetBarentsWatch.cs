using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration; // Needed to read secrets
using Saga.Shared;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Saga.Server
{
    public class GetBarentsWatch
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GetBarentsWatch> _logger;
        private readonly IConfiguration _configuration;

        // Static variable to cache the token so we don't ask for a new one every 5 seconds
        // In a real production app, use IDistributedCache (Redis)
        private static string? _accessToken;
        private static DateTime _tokenExpiry = DateTime.MinValue;

        public GetBarentsWatch(IHttpClientFactory httpClientFactory, ILogger<GetBarentsWatch> logger, IConfiguration configuration)
        {
            _httpClient = httpClientFactory.CreateClient();
            _logger = logger;
            _configuration = configuration;
        }

        [Function("GetBarentsWatch")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
        {
            _logger.LogInformation("Fetching tactical sea picture for Norway...");

            var tracks = new List<TacticalTrack>();

            try
            {
                // 1. Ensure we have a valid BarentsWatch Access Token
                await EnsureTokenAsync();

                // 2. Fetch Live AIS Data (Snapshot of all latest positions)
                // We use the "latest/combined" endpoint which gives a snapshot similar to OpenSky
                var request = new HttpRequestMessage(HttpMethod.Get, "https://live.ais.barentswatch.no/v1/latest/combined");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"BarentsWatch API failed: {response.StatusCode}");
                    return new OkObjectResult(tracks);
                }

                // 3. Deserialize BarentsWatch Format
                var aisData = await response.Content.ReadFromJsonAsync<List<BarentsWatchShip>>();

                // 4. Map to TacticalTrack
                if (aisData != null)
                {
                    foreach (var ship in aisData)
                    {
                        // Filter: Only show ships that are moving or active (Optional)
                        // BarentsWatch covers a huge area, you might want to filter by Lat/Lon box like OpenSky
                        if (ship.Latitude < MapConstants.Lamin || ship.Latitude > MapConstants.Lamax ||
                            ship.Longitude < MapConstants.Lomin || ship.Longitude > MapConstants.Lomax)
                            continue;

                        tracks.Add(new TacticalTrack
                        {
                            Id = ship.Mmsi.ToString(),
                            Registration = ship.Mmsi.ToString(),
                            Callsign = string.IsNullOrWhiteSpace(ship.Name) ? "UNKNOWN" : ship.Name,
                            Country = "Unknown", // AIS doesn't always send country code easily
                            Lat = ship.Latitude,
                            Lon = ship.Longitude,
                            Altitude = 0, // It's a boat
                            Velocity = ship.SpeedOverGround ?? 0,
                            Heading = ship.CourseOverGround ?? 0,
                            Type = "Sea", // This helps the UI choose a boat icon
                            IsHostile = false
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching sea data: {ex.Message}");
            }

            _logger.LogInformation($"Returned {tracks.Count} sea tracks.");
            return new OkObjectResult(tracks);
        }

        private async Task EnsureTokenAsync()
        {
            // If we have a token and it's valid for at least 5 more minutes, return.
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
            {
                return;
            }

            _logger.LogInformation("Acquiring new BarentsWatch Access Token...");

            var clientId = _configuration["BarentsWatchClientId"];
            var clientSecret = _configuration["BarentsWatchClientSecret"];

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                throw new Exception("BarentsWatch credentials missing from Configuration.");
            }

            // OAuth2 Client Credentials Flow
            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://id.barentswatch.no/connect/token");
            tokenRequest.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("scope", "ais"), // Scope is required
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            var response = await _httpClient.SendAsync(tokenRequest);
            response.EnsureSuccessStatusCode();

            var tokenJson = await response.Content.ReadFromJsonAsync<BarentsWatchTokenResponse>();

            _accessToken = tokenJson?.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenJson?.ExpiresIn ?? 3600);
        }

        // Internal DTOs for BarentsWatch JSON format
        private class BarentsWatchTokenResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("access_token")]
            public string AccessToken { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }
        }

        private class BarentsWatchShip
        {
            [System.Text.Json.Serialization.JsonPropertyName("mmsi")]
            public long Mmsi { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string? Name { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("latitude")]
            public double Latitude { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("longitude")]
            public double Longitude { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("speedOverGround")]
            public double? SpeedOverGround { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("courseOverGround")]
            public double? CourseOverGround { get; set; }
        }
    }
}