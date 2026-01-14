namespace Saga.Shared;

public class TacticalTrack
{
    // Core Data
    public string Id { get; set; }        // icao24 (Unique ID)
    public string Callsign { get; set; }  // "SAS456"
    public string Country { get; set; }   // "Norway"
    public string Registration { get; set; } // Same as Id usually for OpenSky

    // Position
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double Heading { get; set; }   // True Track (0-360)
    public double Altitude { get; set; }  // Meters
    public double Velocity { get; set; }  // m/s

    // Meta (Requested but not available in /states/all)
    public string? Route { get; set; }    // Placeholder
    public string? ImageUrl { get; set; } // Placeholder

    public bool IsHostile => false;       // OpenSky tracks are civilian/neutral
    public string Type => "Air";
}
