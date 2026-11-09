// GuiCs – aplikacja okienkowa K3 w C# (WinForms, .NET 10).
//
// Użycie i format wyniku – jak w GuiCpp/main.cpp:
//   GuiCs.exe --bench <startup|fill|draw|responsive> --out <plik.csv>
//             [--variant idle|ui-thread|worker] [--param n] [--iter 10] [--warmup 2]
//   język;scenariusz;wariant;parametr;iteracja;rozgrzewka;metryka;wartość
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GuiCs;

internal sealed record Options(string Bench, string Variant, long Param, int Iter, int Warmup, string Out)
{
    public static Options? Parse(string[] args)
    {
        string? bench = null, output = null;
        string variant = "-";
        long param = 0;
        int iter = 10, warmup = 2;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            string value = args[i + 1];
            switch (args[i])
            {
                case "--bench": bench = value; break;
                case "--variant": variant = value; break;
                case "--param": param = long.Parse(value, CultureInfo.InvariantCulture); break;
                case "--iter": iter = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--warmup": warmup = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--out": output = value; break;
            }
        }
        return bench is null || output is null ? null : new Options(bench, variant, param, iter, warmup, output);
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            MessageBox.Show("Użycie: GuiCs.exe --bench <scenariusz> --out <plik> [--variant v] [--param n]", "GuiCs");
            return 1;
        }
        // Bez skalowania DPI – tak jak GuiCpp (brak manifestu) i GuiJava (sun.java2d.uiScale=1).
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new BenchForm(options));
        return 0;
    }
}

internal static class Clock
{
    /// <summary>QueryPerformanceCounter (Stopwatch) w nanosekundach – ta sama skala co w GuiCpp i Runnerze.</summary>
    public static long NowNs()
    {
        long t = Stopwatch.GetTimestamp(), f = Stopwatch.Frequency;
        return t / f * 1_000_000_000L + t % f * 1_000_000_000L / f;
    }
}

internal sealed class BenchForm : Form
{
    // Układ okna – taki sam w GuiCpp i GuiJava.
    private const int ImageSize = 800, ListWidth = 300, Margin_ = 10, ButtonHeight = 30;
    private const int DrawMaxIter = 256, WorkSize = 1000, WorkMaxIter = 1000;
    private const int ProgressEveryRows = 50, ProbeIntervalMs = 10, StartupHoldMs = 1000;

    private readonly Options _o;
    private readonly ListBox _list;
    private readonly Panel _canvas;
    private readonly List<string> _lines = [];
    private bool _firstPaintDone;
    private long _firstPaintNs;
    private Bitmap? _bitmap;

    // responsive
    private readonly List<long> _latencies = [];
    private long _endNs;
    private volatile bool _stop;
    private int _workerImages, _uiImages;
    private Thread? _probe, _worker;
    private readonly int[] _uiImage = new int[WorkSize * WorkSize];

    public BenchForm(Options o)
    {
        _o = o;
        Text = "Bench GUI – C#";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(Margin_ * 3 + ListWidth + ImageSize, Margin_ * 2 + ImageSize);

        _list = new ListBox
        {
            Bounds = new Rectangle(Margin_, Margin_, ListWidth, ImageSize - ButtonHeight - Margin_),
            IntegralHeight = false,
        };
        var button = new Button
        {
            Text = "Start",
            Bounds = new Rectangle(Margin_, Margin_ + ImageSize - ButtonHeight, ListWidth, ButtonHeight),
        };
        _canvas = new Panel { Bounds = new Rectangle(Margin_ * 2 + ListWidth, Margin_, ImageSize, ImageSize) };
        _canvas.Paint += (_, e) =>
        {
            if (_bitmap is not null)
                e.Graphics.DrawImageUnscaled(_bitmap, 0, 0);
        };
        Controls.AddRange([_list, button, _canvas]);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_firstPaintDone)
            return;
        _firstPaintDone = true;
        _firstPaintNs = Clock.NowNs();
        BeginInvoke(new Action(StartScenario));
    }

    private void Record(int iteration, bool warmup, string metric, long value) =>
        _lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"C#;{_o.Bench};{_o.Variant};{_o.Param};{iteration};{(warmup ? 1 : 0)};{metric};{value}"));

    /// <summary>Zapis wyników i zamknięcie aplikacji.</summary>
    private void Finish()
    {
        File.WriteAllLines(_o.Out, _lines);
        Close();
    }

    private void StartScenario()
    {
        switch (_o.Bench)
        {
            case "startup":
                Record(1, false, "first_paint_ns", _firstPaintNs);
                var timer = new System.Windows.Forms.Timer { Interval = StartupHoldMs };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Finish();
                };
                timer.Start();
                break;
            case "fill": RunFill(); break;
            case "draw": RunDraw(); break;
            case "responsive": StartResponsive(); break;
            default: Finish(); break;
        }
    }

    private static void MandelRow(int[] img, int width, int height, int y, int maxIter)
    {
        double cy = -1.5 + 3.0 * y / height;
        for (int x = 0; x < width; x++)
        {
            double cx = -2.0 + 3.0 * x / width;
            double zx = 0.0, zy = 0.0;
            int it = 0;
            while (it < maxIter)
            {
                double zx2 = zx * zx, zy2 = zy * zy;
                if (zx2 + zy2 > 4.0)
                    break;
                zy = 2.0 * zx * zy + cy;
                zx = zx2 - zy2 + cx;
                it++;
            }
            img[y * width + x] = it;
        }
    }

    // ---------- fill ----------
    private void RunFill()
    {
        int n = (int)_o.Param;
        for (int it = 1; it <= _o.Warmup + _o.Iter; it++)
        {
            _list.Items.Clear();
            _list.Update();

            long t0 = Clock.NowNs();
            _list.BeginUpdate();
            for (int i = 0; i < n; i++)
                _list.Items.Add("Element " + i.ToString(CultureInfo.InvariantCulture));
            _list.EndUpdate();
            _list.Refresh(); // unieważnienie + synchroniczne odrysowanie
            Record(it, it <= _o.Warmup, "time_ns", Clock.NowNs() - t0);
        }
        Finish();
    }

    // ---------- draw ----------
    private void RunDraw()
    {
        int frames = (int)_o.Param;
        int count = ImageSize * ImageSize;
        int[] iters = new int[count];
        for (int y = 0; y < ImageSize; y++)
            MandelRow(iters, ImageSize, ImageSize, y, DrawMaxIter);
        int[] palette = new int[256];
        for (int i = 0; i < 256; i++)
            palette[i] = (i << 16) | (((i * 5) & 255) << 8) | ((i * 11) & 255); // 0x00RRGGBB
        int[] pixels = new int[count];
        _bitmap = new Bitmap(ImageSize, ImageSize, PixelFormat.Format32bppRgb);
        var rect = new Rectangle(0, 0, ImageSize, ImageSize);

        using Graphics g = _canvas.CreateGraphics();
        for (int it = 1; it <= _o.Warmup + _o.Iter; it++)
        {
            long t0 = Clock.NowNs();
            for (int f = 0; f < frames; f++)
            {
                for (int i = 0; i < count; i++)
                    pixels[i] = palette[(iters[i] + f) & 255];
                BitmapData data = _bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                Marshal.Copy(pixels, 0, data.Scan0, count);
                _bitmap.UnlockBits(data);
                g.DrawImageUnscaled(_bitmap, 0, 0);
            }
            g.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
            Record(it, it <= _o.Warmup, "time_ns", Clock.NowNs() - t0);
        }
        Finish();
    }

    // ---------- responsive ----------
    private void StartResponsive()
    {
        _endNs = Clock.NowNs() + _o.Param * 1_000_000L;
        // Sonda: co 10 ms wysyła do wątku UI zdarzenie ze znacznikiem czasu.
        _probe = new Thread(() =>
        {
            while (Clock.NowNs() < _endNs)
            {
                Thread.Sleep(ProbeIntervalMs);
                long ts = Clock.NowNs();
                BeginInvoke(new Action(() => _latencies.Add(Clock.NowNs() - ts)));
            }
            BeginInvoke(new Action(StopResponsive));
        }) { IsBackground = true };
        _probe.Start();

        if (_o.Variant == "ui-thread")
            BeginInvoke(new Action(ComputeOnUiThread));
        else if (_o.Variant == "worker")
        {
            _worker = new Thread(() =>
            {
                int[] img = new int[WorkSize * WorkSize];
                while (!_stop)
                {
                    for (int y = 0; y < WorkSize && !_stop; y++)
                    {
                        MandelRow(img, WorkSize, WorkSize, y, WorkMaxIter);
                        if ((y + 1) % ProgressEveryRows == 0)
                        {
                            int row = y + 1;
                            BeginInvoke(new Action(() => Text = $"Bench GUI – C# – wiersz {row}/{WorkSize}"));
                        }
                    }
                    if (!_stop)
                        Interlocked.Increment(ref _workerImages);
                }
            }) { IsBackground = true };
            _worker.Start();
        }
    }

    /// <summary>Obliczenia na wątku UI (błędna praktyka): cały obraz w jednym zdarzeniu.</summary>
    private void ComputeOnUiThread()
    {
        if (Clock.NowNs() >= _endNs)
            return;
        for (int y = 0; y < WorkSize; y++)
            MandelRow(_uiImage, WorkSize, WorkSize, y, WorkMaxIter);
        _uiImages++;
        BeginInvoke(new Action(ComputeOnUiThread));
    }

    private void StopResponsive()
    {
        _stop = true;
        _probe?.Join();
        _worker?.Join();
        for (int i = 0; i < _latencies.Count; i++)
            Record(i + 1, false, "latency_ns", _latencies[i]);
        Record(0, false, "images", _uiImages + Volatile.Read(ref _workerImages));
        Finish();
    }
}
