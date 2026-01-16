using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Saga.Shared;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Saga.Server
{
    public class GetTacticalData
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GetTacticalData> _logger;
        private readonly IConfiguration _configuration;

        // Static variable to cache the token so we don't ask for a new one every request
        private static string? _accessToken;
        private static DateTime _tokenExpiry = DateTime.MinValue;

        public GetTacticalData(IHttpClientFactory httpClientFactory, ILogger<GetTacticalData> logger, IConfiguration configuration)
        {
            _httpClient = httpClientFactory.CreateClient();
            _logger = logger;
            _configuration = configuration;
        }

        [Function("GetTacticalData")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
        {
            _logger.LogInformation("Fetching tactical air picture for Norway...");

            var tracks = new List<TacticalTrack>();

            try
            {
                // 1. Ensure we have a valid OpenSky Access Token
                await EnsureTokenAsync();

                string url = $"https://opensky-network.org/api/states/all?" +
                             $"lamin={MapConstants.Lamin}&" +
                             $"lomin={MapConstants.Lomin}&" +
                             $"lamax={MapConstants.Lamax}&" +
                             $"lomax={MapConstants.Lomax}";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                
                if (!string.IsNullOrEmpty(_accessToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                }

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"OpenSky API failed: {response.StatusCode}");
                    // Return empty list instead of crashing, keeps the UI alive
                    return new OkObjectResult(tracks);
                }

                var json = await response.Content.ReadFromJsonAsync<OpenSkyResponse>();

                // 2. Map Raw Data to Clean DTO
                if (json?.States != null)
                {
                    foreach (var rawState in json.States)
                    {
                        // OpenSky returns a mixed-type array. 
                        // We use JsonElement to safely extract values by index.
                        // Index Map: 0=Icao24, 1=Callsign, 2=Country, 5=Lon, 6=Lat, 7=Alt, 9=Vel, 10=Heading

                        try
                        {
                            var t = new TacticalTrack
                            {
                                Id = rawState[0].GetString() ?? "Unknown",
                                Registration = rawState[0].GetString() ?? "Unknown", // ICAO24 is the best reg we have
                                Callsign = rawState[1].GetString()?.Trim() ?? "N/A",
                                Country = rawState[2].GetString() ?? "Unknown",

                                Lon = GetDouble(rawState[5]),
                                Lat = GetDouble(rawState[6]),
                                Altitude = GetDouble(rawState[7]),
                                Velocity = GetDouble(rawState[9]),
                                Heading = GetDouble(rawState[10]),
                                Type = "Air",

                                // Limitations:
                                Route = "Unknown (Requires Flight Plan API)",
                                ImageUrl = null // OpenSky does not provide images
                            };

                            // Filter out "Null Island" (0,0) or bad data
                            if (Math.Abs(t.Lat) > 0.1 && Math.Abs(t.Lon) > 0.1)
                            {
                                tracks.Add(t);
                            }
                        }
                        catch
                        {
                            // Skip malformed records
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching data: {ex.Message}");
            }

            _logger.LogInformation($"Returned {tracks.Count} tracks.");
            return new OkObjectResult(tracks);
        }

        private async Task EnsureTokenAsync()
        {
            // If we have a token and it's valid for at least 5 more minutes, return.
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
            {
                return;
            }

            _logger.LogInformation("Acquiring new OpenSky Access Token...");

            var clientId = _configuration["OpenSkyClientId"]?.Trim();
            var clientSecret = _configuration["OpenSkyClientSecret"]?.Trim();

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                _logger.LogWarning("OpenSky credentials missing from Configuration. Continuing as Anonymous.");
                return;
            }

            var maskedId = clientId.Length > 5 ? clientId.Substring(0, 5) + "..." : clientId;
            _logger.LogInformation($"Attempting OpenSky OAuth with ClientID: {maskedId}");

            // OAuth2 Client Credentials Flow
            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://auth.opensky-network.org/auth/realms/opensky-network/protocol/openid-connect/token");
            tokenRequest.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            var response = await _httpClient.SendAsync(tokenRequest);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                var msg = $"Failed to acquire OpenSky token. Status: {response.StatusCode}. Response: {errorContent}";
                _logger.LogError(msg);
                throw new Exception(msg);
            }

            var tokenJson = await response.Content.ReadFromJsonAsync<OpenSkyTokenResponse>();

            _accessToken = tokenJson?.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenJson?.ExpiresIn ?? 3600);
            _logger.LogInformation("Successfully acquired OpenSky Access Token.");
        }

        // Helper to safely extract numbers from JSON Mixed Arrays (Handles nulls/ints/floats)
        private double GetDouble(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number)
                return element.GetDouble();
            return 0.0;
        }

        // Internal Class for Deserialization
        private class OpenSkyResponse
        {
            public int Time { get; set; }
            public JsonElement[][] States { get; set; } // Array of Arrays
        }

        private class OpenSkyTokenResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("access_token")]
            public string AccessToken { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }
        }
    }
}