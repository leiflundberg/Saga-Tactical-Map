using Mapsui;
using Mapsui.Layers;
using Mapsui.Manipulations;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.Nts;
using Mapsui.Logging;
using Mapsui.Widgets;
using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;
using Saga.Shared;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Color = Mapsui.Styles.Color;
using Pen = Mapsui.Styles.Pen;

namespace Saga
{
    public partial class MainWindow : Window
    {
        private MemoryLayer _tacticalLayer;

        private readonly Image _planeImage;
        private readonly Image _boatImage;
        private readonly IConfiguration _configuration;
        private readonly bool _showDebug;

        private Dictionary<string, TrackedUnit> _units = [];

        private List<IFeature> _mapFeatures = [];

        private readonly HttpClient _httpClient = new();
        
        // TODO: update with deployed azure function URL
        private const string ApiUrl = "http://localhost:7038/api/GetOpenSky";
        private const string SeaApiUrl = "http://localhost:7038/api/GetBarentsWatch";

        private class TrackedUnit
        {
            public PointFeature Feature { get; set; }
            public MPoint CurrentPosition { get; set; }
            public MPoint TargetPosition { get; set; }
        }

        public MainWindow()
        {
            InitializeComponent();

            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

            // Try to find the server's local.settings.json for shared debug config
            // Use AppContext.BaseDirectory to find the path relative to the executable
            var serverSettingsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Saga.Server/local.settings.json"));
            builder.AddJsonFile(serverSettingsPath, optional: true, reloadOnChange: true);

            builder.AddEnvironmentVariables();

            _configuration = builder.Build();

            // Check standard "Debug" key, then override with "Values:DEBUG" from server settings if present
            _showDebug = _configuration.GetValue<bool>("Debug", true);
            
            var serverDebugValue = _configuration["Values:DEBUG"];
            if (!string.IsNullOrEmpty(serverDebugValue) && bool.TryParse(serverDebugValue, out bool serverDebug))
            {
                _showDebug = serverDebug;
            }

            DebugOverlay.Visibility = _showDebug ? Visibility.Visible : Visibility.Collapsed;

            // Simple plane SVG (pointed)
            var planeSvg = "<svg width='24' height='24' viewBox='0 0 24 24'><path d='M12 0 L15 9 L23 14 L23 16 L15 13 L15 20 L19 23 L19 24 L12 22 L5 24 L5 23 L9 20 L9 13 L1 16 L1 14 L9 9 Z' fill='DarkOrange'/></svg>";
            _planeImage = new Image { Source = "svg-content://" + planeSvg };

            // Simple boat SVG (pointed)
            var boatSvg = "<svg width='16' height='24' viewBox='0 0 16 24'><path d='M8 0 L16 8 L16 24 L0 24 L0 8 Z' fill='DarkGreen' /></svg>";
            _boatImage = new Image { Source = "svg-content://" + boatSvg };

            var map = new Map();
            map.Layers.Add(OpenStreetMap.CreateTileLayer());

            var p1 = SphericalMercator.FromLonLat(new MPoint(MapConstants.Lomin, MapConstants.Lamin));
            var p2 = SphericalMercator.FromLonLat(new MPoint(MapConstants.Lomax, MapConstants.Lamin));
            var p3 = SphericalMercator.FromLonLat(new MPoint(MapConstants.Lomax, MapConstants.Lamax));
            var p4 = SphericalMercator.FromLonLat(new MPoint(MapConstants.Lomin, MapConstants.Lamax));

            var polygon = new Polygon(new LinearRing(
            [
                new Coordinate(p1.X, p1.Y),
                new Coordinate(p2.X, p2.Y),
                new Coordinate(p3.X, p3.Y),
                new Coordinate(p4.X, p4.Y),
                new Coordinate(p1.X, p1.Y)
            ]));

            var boundingBoxFeature = new GeometryFeature(polygon);

            var boundingBoxLayer = new MemoryLayer
            {
                Name = "BoundingBoxLayer",
                Features = [boundingBoxFeature],
                Style = new VectorStyle
                {
                    Fill = null,
                    Outline = new Pen
                    {
                        Color = Color.DarkGreen,
                        Width = 2,
                        PenStyle = PenStyle.Dot,
                        PenStrokeCap = PenStrokeCap.Round
                    }
                },
                MinVisible = 0,
                MaxVisible = double.MaxValue
            };

            map.Layers.Add(boundingBoxLayer);

            _tacticalLayer = new MemoryLayer
            {
                Name = "TacticalMarkers",
                Features = _mapFeatures,

                MinVisible = 0,
                MaxVisible = double.MaxValue
            };


            map.Layers.Add(_tacticalLayer);

            mapControl.Map = map;
            mapControl.Loaded += MapControl_Loaded;

            StartLiveTracking();

            CompositionTarget.Rendering += OnRendering;
            mapControl.MouseLeftButtonUp += OnMapClicked;
            mapControl.Map.Navigator.ViewportChanged += OnViewportChanged;
        }

        private void MapControl_Loaded(object sender, RoutedEventArgs e)
        {
            var northernEuropeCenter = SphericalMercator.FromLonLat(new MPoint(14.0, 63.0));

            mapControl.Map.Navigator.CenterOnAndZoomTo(northernEuropeCenter, mapControl.Map.Navigator.Resolutions[5]);

            var performanceWidget = mapControl.Map.Widgets.FirstOrDefault(w => w.GetType().Name == "PerformanceWidget");
            if (performanceWidget != null)
            {
                performanceWidget.Enabled = _showDebug;
            }

            var loggingWidget = mapControl.Map.Widgets.FirstOrDefault(w => w.GetType().Name == "LoggingWidget");
            if (loggingWidget != null)
            {
                loggingWidget.Enabled = _showDebug;
            }
        }

        private void StartLiveTracking()
        {
            // Polling Loop for Air Data
            Task.Run(async () =>
            {
                int delay = _configuration.GetValue<int>("PollingInterval:Air", 10000);
                while (true)
                {
                    try
                    {
                        var tracks = await _httpClient.GetFromJsonAsync<List<TacticalTrack>>(ApiUrl);
                        if (tracks != null)
                        {
                            Application.Current.Dispatcher.Invoke(() => UpdateTracks(tracks));
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error fetching air data: {ex.Message}");
                    }

                    await Task.Delay(delay);
                }
            });

            // Polling Loop for Sea Data
            Task.Run(async () =>
            {
                int delay = _configuration.GetValue<int>("PollingInterval:Sea", 15000);
                while (true)
                {
                    try
                    {
                        var tracks = await _httpClient.GetFromJsonAsync<List<TacticalTrack>>(SeaApiUrl);
                        if (tracks != null)
                        {
                            Application.Current.Dispatcher.Invoke(() => UpdateTracks(tracks));
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error fetching sea data: {ex.Message}");
                    }

                    await Task.Delay(delay);
                }
            });
        }

        private void UpdateTracks(List<TacticalTrack> tracks)
        {
            bool dataChanged = false;

            foreach (var track in tracks)
            {
                var targetPoint = SphericalMercator.FromLonLat(new MPoint(track.Lon, track.Lat));

                if (_units.TryGetValue(track.Id, out var existingUnit))
                {
                    existingUnit.TargetPosition = targetPoint;
                    existingUnit.Feature["Callsign"] = track.Callsign;
                    existingUnit.Feature["Status"] = "LIVE";
                    existingUnit.Feature["Lat"] = track.Lat;
                    existingUnit.Feature["Lon"] = track.Lon;
                    existingUnit.Feature["Country"] = track.Country;
                    existingUnit.Feature["Alt"] = track.Altitude;
                    existingUnit.Feature["Vel"] = track.Velocity;
                    existingUnit.Feature["Hdg"] = track.Heading;

                    if (existingUnit.Feature.Styles.FirstOrDefault() is ImageStyle style)
                    {
                        style.SymbolRotation = track.Heading;
                    }
                }
                else
                {
                    var feature = new PointFeature(targetPoint);
                    feature["Callsign"] = track.Callsign;
                    feature["Status"] = "LIVE";
                    feature["Type"] = track.Type;
                    feature["Lat"] = track.Lat;
                    feature["Lon"] = track.Lon;
                    feature["Country"] = track.Country;
                    feature["Alt"] = track.Altitude;
                    feature["Vel"] = track.Velocity;
                    feature["Hdg"] = track.Heading;

                    feature.Styles.Add(new ImageStyle
                    {
                        Image = track.Type == "Sea" ? _boatImage : _planeImage,
                        SymbolScale = 0.5,
                        SymbolRotation = track.Heading
                    });

                    var newUnit = new TrackedUnit
                    {
                        Feature = feature,
                        CurrentPosition = targetPoint,
                        TargetPosition = targetPoint
                    };

                    _units[track.Id] = newUnit;
                    dataChanged = true;
                }
            }

            if (dataChanged)
            {
                UpdateLayerFeatures();
            }
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            UpdateLayerFeatures();
        }

        private void UpdateLayerFeatures()
        {
            if (_units == null || ChkAir == null || ChkSea == null || _tacticalLayer == null) return;

            bool showAir = ChkAir.IsChecked == true;
            bool showSea = ChkSea.IsChecked == true;

            var visibleFeatures = _units.Values
                .Where(unit =>
                {
                    string type = unit.Feature["Type"]?.ToString() ?? "Air";
                    return (type == "Sea" && showSea) || (type != "Sea" && showAir);
                })
                .Select(unit => unit.Feature)
                .ToList();

            _tacticalLayer.Features = visibleFeatures;
            _tacticalLayer.DataHasChanged();
            mapControl.Refresh();
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            bool anyMoved = false;

            foreach (var unit in _units.Values)
            {
                // Optimization: Skip animation for Sea units to save CPU
                if (unit.Feature["Type"]?.ToString() == "Sea")
                {
                    if (unit.CurrentPosition.Distance(unit.TargetPosition) > 0.1)
                    {
                        unit.CurrentPosition = unit.TargetPosition;
                        unit.Feature.Point.X = unit.CurrentPosition.X;
                        unit.Feature.Point.Y = unit.CurrentPosition.Y;
                        anyMoved = true;
                    }
                    continue;
                }

                if (unit.CurrentPosition.Distance(unit.TargetPosition) < 1.0) continue;

                // LERP: Move 5% towards target every frame (Air only)
                double factor = 0.05;
                double smoothX = Lerp(unit.CurrentPosition.X, unit.TargetPosition.X, factor);
                double smoothY = Lerp(unit.CurrentPosition.Y, unit.TargetPosition.Y, factor);

                unit.CurrentPosition = new MPoint(smoothX, smoothY);

                unit.Feature.Point.X = unit.CurrentPosition.X;
                unit.Feature.Point.Y = unit.CurrentPosition.Y;

                anyMoved = true;
            }

            if (anyMoved)
            {
                // Force redraw of the layer
                _tacticalLayer.DataHasChanged();
                mapControl.RefreshGraphics();
            }
        }

        private void OnMapClicked(object sender, MouseButtonEventArgs e)
        {
            var wpfPoint = e.GetPosition(mapControl);
            var screenPosition = new ScreenPosition(wpfPoint.X, wpfPoint.Y);
            var info = mapControl.GetMapInfo(screenPosition, mapControl.Map.Layers);

            if (info?.Feature != null)
            {
                var feature = info.Feature;

                string callsign = feature["Callsign"]?.ToString() ?? "Unknown";
                string country = feature["Country"]?.ToString() ?? "Unknown";
                string status = feature["Status"]?.ToString() ?? "N/A";
                string altitude = feature["Alt"] is double alt ? $"{alt:N0} m" : "-";
                string velocity = feature["Vel"] is double vel ? $"{vel:N0} m/s" : "-";
                string heading = feature["Hdg"] is double hdg ? $"{hdg:N0}°" : "-";

                TxtCallsign.Text = callsign;
                TxtCountry.Text = country;
                TxtStatus.Text = status;

                TxtAltitude.Text = altitude;
                TxtVelocity.Text = velocity;
                TxtHeading.Text = heading;

                if (feature["Lat"] is double lat && feature["Lon"] is double lon)
                {
                    TxtLocation.Text = $"{lat:0.0000}, {lon:0.0000}";
                }

                TxtStatus.Foreground = status == "HOSTILE" ?
                    new SolidColorBrush(Colors.Red) :
                    new SolidColorBrush(Colors.LightGreen);

                DetailPanel.Visibility = Visibility.Visible;
                e.Handled = true;
            }
            else
            {
                DetailPanel.Visibility = Visibility.Hidden;
            }
        }

        private void OnViewportChanged(object? sender, EventArgs e)
        {
            if (!_showDebug) return;

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var viewport = mapControl.Map.Navigator.Viewport;

                double zoom = viewport.Resolution;
                double x = viewport.CenterX;
                double y = viewport.CenterY;

                var centerPoint = new MPoint(x, y);
                var latLon = SphericalMercator.ToLonLat(centerPoint);

                TxtDebug.Text = $"""
            DEBUG DATA
            ----------
            Res (Zoom) : {zoom:F2}
            Center X   : {x:F0}
            Center Y   : {y:F0}
            Lat/Lon    : {latLon.Y:F4}, {latLon.X:F4}
            """;
            });
        }

        private void ClosePanel_Click(object sender, RoutedEventArgs e)
        {
            DetailPanel.Visibility = Visibility.Hidden;
        }

        private static double Lerp(double start, double end, double factor)
        {
            return start + (end - start) * factor;
        }
    }
}