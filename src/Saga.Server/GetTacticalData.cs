using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Saga.Shared;
using System.Net.Http.Json;
using System.Text.Json;

namespace Saga.Server
{
    public class GetTacticalData
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GetTacticalData> _logger;

        public GetTacticalData(IHttpClientFactory httpClientFactory, ILogger<GetTacticalData> logger)
        {
            _httpClient = httpClientFactory.CreateClient();
            _logger = logger;
        }

        [Function("GetTacticalData")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
        {
            _logger.LogInformation("Fetching tactical air picture for Norway...");

            var tracks = new List<TacticalTrack>();

            try
            {
                string url = $"https://opensky-network.org/api/states/all?" +
                             $"lamin={MapConstants.Lamin}&" +
                             $"lomin={MapConstants.Lomin}&" +
                             $"lamax={MapConstants.Lamax}&" +
                             $"lomax={MapConstants.Lomax}";

                var response = await _httpClient.GetAsync(url);

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
    }
}