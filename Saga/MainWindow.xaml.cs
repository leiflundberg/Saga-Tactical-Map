using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.Manipulations;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = Mapsui.Styles.Brush;
using Color = Mapsui.Styles.Color;
using Pen = Mapsui.Styles.Pen;

namespace Saga
{
    public partial class MainWindow : Window
    {
        private MemoryLayer _tacticalLayer;

        private List<TrackedUnit> _units = new();

        private MPoint? _currentPosition;
        private MPoint? _targetPosition;

        private class TrackedUnit
        {
            public PointFeature Feature { get; set; }
            public MPoint CurrentPosition { get; set; }
            public MPoint TargetPosition { get; set; }
            public MPoint PatrolPointA { get; set; }
            public MPoint PatrolPointB { get; set; }
            public bool GoingToB { get; set; } = true;
        }

        public MainWindow()
        {
            InitializeComponent();

            var map = new Mapsui.Map();
            map.Layers.Add(OpenStreetMap.CreateTileLayer());

            _tacticalLayer = CreateMarkerLayer();
            map.Layers.Add(_tacticalLayer);

            mapControl.Map = map;
            mapControl.Loaded += MapControl_Loaded;

            StartPatrolSimulation();

            CompositionTarget.Rendering += OnRendering;

            mapControl.MouseLeftButtonDown += OnMapClicked;
        }

        private void MapControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_units.Count != 0)
            {
                // Center on the first unit (Red Viper)
                mapControl.Map.Navigator.CenterOnAndZoomTo(_units[0].CurrentPosition, mapControl.Map.Navigator.Resolutions[9]);
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            bool anyChange = false;

            foreach (var unit in _units)
            {
                // If we are close enough (1 meter), don't calc math
                if (unit.CurrentPosition.Distance(unit.TargetPosition) < 1.0) continue;

                // Move 5% closer
                double factor = 0.05;
                double smoothX = Lerp(unit.CurrentPosition.X, unit.TargetPosition.X, factor);
                double smoothY = Lerp(unit.CurrentPosition.Y, unit.TargetPosition.Y, factor);

                // Update Position
                unit.CurrentPosition = new MPoint(smoothX, smoothY);

                // Update Feature (Visuals)
                unit.Feature.Point.X = unit.CurrentPosition.X;
                unit.Feature.Point.Y = unit.CurrentPosition.Y;

                anyChange = true;
            }

            // Only redraw if pixels actually moved
            if (anyChange)
            {
                _tacticalLayer.DataHasChanged();
                mapControl.RefreshGraphics();
            }
        }

        private void StartPatrolSimulation()
        {
            Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

                while (await timer.WaitForNextTickAsync())
                {
                    // Update every unit in our list
                    foreach (var unit in _units)
                    {
                        // Toggle between Point A and Point B
                        unit.TargetPosition = unit.GoingToB ? unit.PatrolPointB : unit.PatrolPointA;
                        unit.GoingToB = !unit.GoingToB;
                    }
                }
            });
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
                string status = feature["Status"]?.ToString() ?? "N/A";

                TxtCallsign.Text = callsign;
                TxtStatus.Text = status;

                if (info.WorldPosition != null)
                {
                    var latLon = SphericalMercator.ToLonLat(info.WorldPosition);
                    TxtLocation.Text = $"{latLon.Y:0.0000}, {latLon.X:0.0000}";
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

        private void ClosePanel_Click(object sender, RoutedEventArgs e)
        {
            DetailPanel.Visibility = Visibility.Hidden;
        }

        private MemoryLayer CreateMarkerLayer()
        {
            var features = new List<PointFeature>();

            void AddUnit(string id, string status, Color color, double lat, double lon, double patrolOffsetLat, double patrolOffsetLon)
            {
                // 1. Calculate the coordinates
                var startCoords = SphericalMercator.FromLonLat(new MPoint(lon, lat));
                var endCoords = SphericalMercator.FromLonLat(new MPoint(lon + patrolOffsetLon, lat + patrolOffsetLat));

                // 2. Create the feature using the start coordinates
                // This 'startCoords' object becomes the Feature's geometry
                var feature = new PointFeature(startCoords);
                feature["Callsign"] = id;
                feature["Status"] = status;

                feature.Styles.Add(new SymbolStyle
                {
                    Fill = new Brush(color),
                    Outline = new Pen(Color.White, 2),
                    SymbolScale = 0.8,
                    SymbolType = SymbolType.Ellipse
                });

                // 3. THE FIX: Create NEW MPoints for the logic logic
                // We use the X/Y values, but create fresh objects.
                // This ensures modifying the Feature doesn't modify the Patrol Route.
                var safeStart = new MPoint(startCoords.X, startCoords.Y);
                var safeEnd = new MPoint(endCoords.X, endCoords.Y);

                _units.Add(new TrackedUnit
                {
                    Feature = feature,
                    // CurrentPosition needs to be a copy too, or the first 'Lerp' will modify the Feature directly before we are ready
                    CurrentPosition = new MPoint(startCoords.X, startCoords.Y),
                    TargetPosition = new MPoint(startCoords.X, startCoords.Y),
                    PatrolPointA = safeStart, // Now this is safe!
                    PatrolPointB = safeEnd
                });

                features.Add(feature);
            }

            // (The calls below remain exactly the same)
            AddUnit("VIPER 01", "HOSTILE", Color.Red, 59.9139, 10.7522, 0.0, 0.05);
            AddUnit("ANGEL 02", "FRIENDLY", Color.Blue, 59.9139, 10.7000, 0.03, 0.0);
            AddUnit("FERRY A", "NEUTRAL", Color.Green, 59.9000, 10.8000, 0.02, 0.02);

            return new MemoryLayer
            {
                Name = "TacticalMarkers",
                Features = features
            };
        }

        // Math Helper: Linear Interpolation
        private static double Lerp(double start, double end, double factor)
        {
            return start + (end - start) * factor;
        }
    }
}