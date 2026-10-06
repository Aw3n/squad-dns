using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SquadDns.Controls;

public sealed class MatrixRain : FrameworkElement
{
    private const string Glyphs = "0123456789ｱｲｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉ01ｦｧｨｩｪｫｬｭｮｯ";
    private static readonly Random Random = new();

    private readonly List<Stream> _streams = new();
    private readonly DispatcherTimer _timer;
    private double _glyphHeight;
    private Typeface _typeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private int _pixelDensity = 1;

    public MatrixRain()
    {
        IsHitTestVisible = false;
        Opacity = 0.32;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(85) };
        _timer.Tick += (_, _) => Advance();

        Loaded += (_, _) => Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public static readonly DependencyProperty RunProperty = DependencyProperty.Register(
        nameof(Run), typeof(bool), typeof(MatrixRain), new PropertyMetadata(true, OnRunChanged));

    public bool Run
    {
        get => (bool)GetValue(RunProperty);
        set => SetValue(RunProperty, value);
    }

    public Brush RainBrush
    {
        get => (Brush)GetValue(RainBrushProperty);
        set => SetValue(RainBrushProperty, value);
    }

    public static readonly DependencyProperty RainBrushProperty = DependencyProperty.Register(
        nameof(RainBrush), typeof(Brush), typeof(MatrixRain), new PropertyMetadata(Brushes.Transparent, (d, e) => ((MatrixRain)d).InvalidateVisual()));

    private static void OnRunChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var rain = (MatrixRain)d;
        if ((bool)e.NewValue)
        {
            rain.Start();
        }
        else
        {
            rain._timer.Stop();
            rain.InvalidateVisual();
        }
    }

    private void Start()
    {
        if (!Run || ActualWidth <= 0)
        {
            return;
        }

        _pixelDensity = VisualTreeHelper.GetDpi(this).PixelsPerDip > 0 ? (int)Math.Ceiling(VisualTreeHelper.GetDpi(this).PixelsPerDip) : 1;
        Build();
        _timer.Start();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged || sizeInfo.HeightChanged)
        {
            Build();
        }
    }

    private void Build()
    {
        var columns = Math.Max(1, (int)(ActualWidth / 26));
        var rows = Math.Max(6, (int)(ActualHeight / 18));

        _streams.Clear();
        for (var i = 0; i < columns; i++)
        {
            _streams.Add(new Stream(i * 26.0, Random.Next(-rows, rows), 8 + Random.Next(0, 8)));
        }

        _glyphHeight = 18;
        InvalidateVisual();
    }

    private void Advance()
    {
        if (!Run)
        {
            return;
        }

        var rows = Math.Max(6, (int)(ActualHeight / _glyphHeight));
        foreach (var stream in _streams)
        {
            stream.Head += 1;
            if (stream.Head - stream.Length > rows)
            {
                stream.Head = Random.Next(0, rows / 2);
                stream.Length = 8 + Random.Next(0, 8);
            }
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (!Run || ActualWidth <= 0 || ActualHeight <= 0 || RainBrush is null)
        {
            return;
        }

        var brush = RainBrush;
        var bold = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var rows = Math.Max(6, (int)(ActualHeight / _glyphHeight));

        foreach (var stream in _streams)
        {
            for (var k = 0; k < stream.Length; k++)
            {
                var y = stream.Head - k;
                if (y < 0 || y >= rows)
                {
                    continue;
                }

                var opacity = Math.Max(0.05, 1.0 - (k / (double)stream.Length));
                var text = Glyphs[Random.Next(Glyphs.Length)].ToString();
                var foreground = k == 0
                    ? Freeze(new SolidColorBrush(Color.FromArgb((byte)(200 * opacity), byte.MaxValue, byte.MaxValue, byte.MaxValue)))
                    : Freeze(CloneWithOpacity(brush, opacity));

                var formatted = new FormattedText(
                    text,
                    CultureInfoCurrent(),
                    FlowDirection.LeftToRight,
                    k == 0 ? bold : _typeface,
                    14,
                    foreground,
                    _pixelDensity);

                drawingContext.DrawText(formatted, new Point(stream.X, y * _glyphHeight));
            }
        }
    }

    private static System.Globalization.CultureInfo CultureInfoCurrent() => System.Globalization.CultureInfo.InvariantCulture;

    private static Brush CloneWithOpacity(Brush brush, double opacity)
    {
        if (brush is SolidColorBrush solid)
        {
            var color = solid.Color;
            color.A = (byte)(color.A * opacity);
            return new SolidColorBrush(color);
        }

        return brush;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private sealed class Stream(double x, int head, int length)
    {
        public double X { get; } = x;
        public int Head { get; set; } = head;
        public int Length { get; set; } = length;
    }
}
