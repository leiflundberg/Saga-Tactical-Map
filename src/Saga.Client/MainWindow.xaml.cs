using Mapsui;
using Mapsui.Layers;
using Mapsui.Manipulations;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.Nts;
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

        private readonly string _planeImageSource;
        private readonly string _boatImageSource;

        private Dictionary<string, TrackedUnit> _units = [];

        private List<IFeature> _mapFeatures = [];

        private readonly HttpClient _httpClient = new();
        
        // TODO: update with deployed azure function URL
        private const string ApiUrl = "http://localhost:7038/api/GetTacticalData";
        private const string SeaApiUrl = "http://localhost:7038/api/GetSeaTacticalData";

        private class TrackedUnit
        {
            public PointFeature Feature { get; set; }
            public MPoint CurrentPosition { get; set; }
            public MPoint TargetPosition { get; set; }
        }

        public MainWindow()
        {
            InitializeComponent();

            var planePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "plane.svg");
            _planeImageSource = "file://" + planePath;

            var boatPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "boat.svg");
            _boatImageSource = "file://" + boatPath;

            var map = new Mapsui.Map();
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
        }

        private void StartLiveTracking()
        {
            // Polling Loop for Air Data
            Task.Run(async () =>
            {
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

                    await Task.Delay(5000); // 5 seconds for Air
                }
            });

            // Polling Loop for Sea Data
            Task.Run(async () =>
            {
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

                    await Task.Delay(10000); // 10 seconds for Sea (slower updates)
                }
            });
        }

        private void UpdateTracks(List<TacticalTrack> tracks)
        {
            bool layerChanged = false;

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
                        Image = new Mapsui.Styles.Image { Source = track.Type == "Sea" ? _boatImageSource : _planeImageSource },
                        SymbolScale = 0.5,
                        SymbolRotation = track.Heading
                    });

                    var newUnit = new TrackedUnit
                    {
                        Feature = feature,
                        CurrentPosition = targetPoint,
                        TargetPosition = targetPoint
                    };

                    _mapFeatures.Add(feature);
                    _units[track.Id] = newUnit;
                    layerChanged = true;
                }
            }

            if (layerChanged)
            {
                _tacticalLayer.Features = _mapFeatures.ToList();
                _tacticalLayer.DataHasChanged();
                mapControl.Refresh();
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            bool anyMoved = false;

            foreach (var unit in _units.Values)
            {
                // TODO: double check that this optimization works
                if (unit.CurrentPosition.Distance(unit.TargetPosition) < 1.0) continue;

                // LERP: Move 5% towards target every frame
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