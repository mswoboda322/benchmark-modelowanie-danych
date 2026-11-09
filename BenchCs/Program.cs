// BenchCs – zadania K1 (pamięć) i K2 (wielowątkowość) w C# (.NET 10).
//
// Użycie i format wyjścia – jak w BenchCpp/main.cpp:
//   BenchCs.exe <zadanie> <wariant> <rozmiar> <wątki> [--iter 10] [--warmup 3] [--flush on|off]
//   język;zadanie;wariant;rozmiar;wątki;iteracja;rozgrzewka;czas_ns;wynik;cpu_ms;gc_liczba;gc_pauza_ms
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace BenchCs;

/// <summary>Parametry wywołania.</summary>
internal sealed record Options(string Task, string Variant, long Size, int Threads, int Iter, int Warmup, bool Flush);

/// <summary>Zadanie = przygotowanie iteracji (poza pomiarem) + część mierzona + wynik kontrolny.</summary>
internal sealed class Bench
{
    public Action? Prepare { get; init; }
    public required Action Run { get; init; }
    public required Func<string> Result { get; init; }
}

/// <summary>Generator SplitMix64 – ta sama sekwencja liczb w każdym języku.</summary>
internal sealed class SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Liczba z przedziału [0, bound).</summary>
    public int NextInt(int bound) => (int)((Next() >> 33) % (ulong)bound);
}

internal static class Program
{
    private const int FlushBytes = 128 * 1024 * 1024; // > 5 × L3 (24 MB)
    private const int CacheLine = 64;
    private const ulong Seed = 42;
    private const long MemAccesses = 1L << 24;
    private const int MandelMaxIter = 1000;
    private const int QueueCapacity = 1024;

    private static byte[] _flushBuffer = [];
    private static long _sink;

    private static int Main(string[] args)
    {
        if (!TryParse(args, out var o))
        {
            Console.Error.WriteLine("Użycie: BenchCs.exe <zadanie> <wariant> <rozmiar> <wątki> " +
                                    "[--iter 10] [--warmup 3] [--flush on|off]");
            return 1;
        }

        Bench? bench = (o.Task, o.Variant) switch
        {
            ("mem-access", "seq" or "rand") => MemAccess(o),
            ("matmul", "ijk" or "ikj") => Matmul(o),
            ("objects", "points-value" or "points-ref" or "trees") => Objects(o),
            ("mandelbrot", "static" or "dynamic") => Mandelbrot(o),
            ("sync", "mutex" or "atomic" or "local-shared" or "local-padded") => Sync(o),
            ("queue", "blocking") when o.Threads >= 2 => Queue(o),
            _ => null,
        };
        if (bench is null)
        {
            Console.Error.WriteLine($"Nieznane zadanie/wariant: {o.Task} {o.Variant} (queue wymaga >= 2 wątków)");
            return 1;
        }
        RunIterations(o, bench);
        return 0;
    }

    private static bool TryParse(string[] args, out Options o)
    {
        o = null!;
        if (args.Length < 4
            || !long.TryParse(args[2], out long size) || size < 1
            || !int.TryParse(args[3], out int threads) || threads < 1)
            return false;
        int iter = 10, warmup = 3;
        bool flush = true;
        for (int i = 4; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--iter" when int.TryParse(args[i + 1], out iter) && iter >= 1: break;
                case "--warmup" when int.TryParse(args[i + 1], out warmup) && warmup >= 0: break;
                case "--flush": flush = args[i + 1] == "on"; break;
                default: return false;
            }
        }
        o = new Options(args[0], args[1], size, threads, iter, warmup, flush);
        return true;
    }

    // ===================== Pętla pomiarowa =====================

    private static void RunIterations(Options o, Bench b)
    {
        if (o.Flush)
        {
            _flushBuffer = new byte[FlushBytes];
            Array.Fill(_flushBuffer, (byte)1);
        }
        var proc = Process.GetCurrentProcess();
        for (int it = 1; it <= o.Warmup + o.Iter; it++)
        {
            b.Prepare?.Invoke();
            // Sprzątanie sterty z poprzedniej iteracji – poza pomiarem.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (o.Flush)
                FlushCache();

            proc.Refresh();
            TimeSpan cpu0 = proc.TotalProcessorTime;
            int gc0 = GC.CollectionCount(0);
            TimeSpan pause0 = GC.GetTotalPauseDuration();
            long t0 = Stopwatch.GetTimestamp();
            b.Run();
            long t1 = Stopwatch.GetTimestamp();
            TimeSpan pause1 = GC.GetTotalPauseDuration();
            int gc1 = GC.CollectionCount(0);
            proc.Refresh();
            TimeSpan cpu1 = proc.TotalProcessorTime;

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"C#;{o.Task};{o.Variant};{o.Size};{o.Threads};{it};{(it <= o.Warmup ? 1 : 0)};" +
                $"{TicksToNs(t1 - t0)};{b.Result()};{(cpu1 - cpu0).TotalMilliseconds:F3};" +
                $"{gc1 - gc0};{(pause1 - pause0).TotalMilliseconds:F3}"));
        }
    }

    private static long TicksToNs(long ticks) =>
        ticks / Stopwatch.Frequency * 1_000_000_000L + ticks % Stopwatch.Frequency * 1_000_000_000L / Stopwatch.Frequency;

    /// <summary>Czyszczenie cache CPU: zapis i odczyt co 64 B bufora większego niż L3.</summary>
    private static void FlushCache()
    {
        byte[] buf = _flushBuffer;
        long sum = 0;
        for (int i = 0; i < buf.Length; i += CacheLine)
        {
            buf[i]++;
            sum += buf[i];
        }
        _sink += sum;
    }

    /// <summary>Liczba operacji wątku t przy podziale total na threads części.</summary>
    private static long Share(long total, int threads, int t) => total / threads + (t < total % threads ? 1 : 0);

    /// <summary>
    /// Uruchamia threads wątków work(nrWątku) i czeka na ich zakończenie. Bariera startowa sprawia,
    /// że wątki zaczynają pracę jednocześnie (inaczej pierwsze mogłyby skończyć, zanim ruszą ostatnie).
    /// </summary>
    private static void RunThreads(int threads, Action<int> work)
    {
        int waiting = threads;
        var pool = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int id = t;
            pool[t] = new Thread(() =>
            {
                Interlocked.Decrement(ref waiting);
                while (Volatile.Read(ref waiting) > 0)
                    Thread.Yield();
                work(id);
            });
            pool[t].Start();
        }
        foreach (var th in pool)
            th.Join();
    }

    // ===================== K1 – pamięć =====================

    /// <summary>Z1.1: tablica int z permutacją jednocyklową (Sattolo); seq – po kolei, rand – pointer chasing.</summary>
    private static Bench MemAccess(Options o)
    {
        int n = (int)(o.Size * 1024 / sizeof(int));
        int[] a = new int[n];
        for (int i = 0; i < n; i++)
            a[i] = i;
        var rng = new SplitMix64(Seed);
        for (int i = n - 1; i > 0; i--)
        {
            int j = rng.NextInt(i);
            (a[i], a[j]) = (a[j], a[i]);
        }

        long sum = 0;
        Action run = o.Variant == "seq"
            ? () =>
            {
                long s = 0, left = MemAccesses;
                while (left > 0)
                {
                    int m = (int)Math.Min(left, n);
                    for (int i = 0; i < m; i++)
                        s += a[i];
                    left -= m;
                }
                sum = s;
            }
            : () =>
            {
                long s = 0;
                int idx = 0;
                for (long k = 0; k < MemAccesses; k++)
                {
                    idx = a[idx];
                    s += idx;
                }
                sum = s;
            };
        return new Bench { Run = run, Result = () => sum.ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>Z1.2: mnożenie macierzy N×N w płaskich tablicach; ijk vs ikj.</summary>
    private static Bench Matmul(Options o)
    {
        int n = (int)o.Size;
        double[] a = new double[n * n], b = new double[n * n], c = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                a[i * n + j] = (i + j) % 10;
                b[i * n + j] = (long)i * j % 10;
            }

        Action run = o.Variant == "ijk"
            ? () =>
            {
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        double s = 0.0;
                        for (int k = 0; k < n; k++)
                            s += a[i * n + k] * b[k * n + j];
                        c[i * n + j] = s;
                    }
            }
            : () =>
            {
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < n; k++)
                    {
                        double aik = a[i * n + k];
                        for (int j = 0; j < n; j++)
                            c[i * n + j] += aik * b[k * n + j];
                    }
            };
        return new Bench
        {
            Prepare = () => Array.Clear(c),
            Run = run,
            Result = () =>
            {
                double s = 0.0;
                foreach (double v in c)
                    s += v;
                return ((long)s).ToString(CultureInfo.InvariantCulture);
            },
        };
    }

    /// <summary>Punkt jako typ wartościowy (tablica struktur – dane ciągłe w pamięci).</summary>
    private struct PointValue(double x, double y, double z)
    {
        public double X = x, Y = y, Z = z;
    }

    /// <summary>Punkt jako obiekt (tablica przechowuje referencje).</summary>
    private sealed class PointRef(double x, double y, double z)
    {
        public double X = x, Y = y, Z = z;
    }

    private sealed class Node(Node? left, Node? right)
    {
        public readonly Node? Left = left, Right = right;
    }

    private static Node BottomUp(int depth) =>
        depth > 0 ? new Node(BottomUp(depth - 1), BottomUp(depth - 1)) : new Node(null, null);

    private static long Check(Node node) =>
        1 + (node.Left is null ? 0 : Check(node.Left) + Check(node.Right!));

    /// <summary>Z1.3: (a) struct[] vs class[], (b) drzewa binarne (binary-trees z CLBG).</summary>
    private static Bench Objects(Options o)
    {
        long sum = 0;
        Func<string> result = () => sum.ToString(CultureInfo.InvariantCulture);

        if (o.Variant == "trees")
        {
            int maxDepth = Math.Max(6, (int)o.Size);
            return new Bench
            {
                Run = () =>
                {
                    const int minDepth = 4;
                    long total = Check(BottomUp(maxDepth + 1));
                    var longLived = BottomUp(maxDepth);
                    for (int depth = minDepth; depth <= maxDepth; depth += 2)
                    {
                        int iterations = 1 << (maxDepth - depth + minDepth);
                        for (int i = 0; i < iterations; i++)
                            total += Check(BottomUp(depth));
                    }
                    total += Check(longLived);
                    sum = total;
                },
                Result = result,
            };
        }

        int n = (int)o.Size;
        if (o.Variant == "points-value")
        {
            var pts = new PointValue[n];
            for (int i = 0; i < n; i++)
                pts[i] = new PointValue(i % 1000, (long)i * 7 % 1000, (long)i * 13 % 1000);
            return new Bench
            {
                Run = () =>
                {
                    double s = 0.0;
                    foreach (ref readonly var p in pts.AsSpan())
                        s += p.X + p.Y + p.Z;
                    sum = (long)s;
                },
                Result = result,
            };
        }

        var refs = new PointRef[n];
        for (int i = 0; i < n; i++)
            refs[i] = new PointRef(i % 1000, (long)i * 7 % 1000, (long)i * 13 % 1000);
        return new Bench
        {
            Run = () =>
            {
                double s = 0.0;
                foreach (var p in refs)
                    s += p.X + p.Y + p.Z;
                sum = (long)s;
            },
            Result = result,
        };
    }

    // ===================== K2 – wielowątkowość =====================

    /// <summary>Jeden wiersz zbioru Mandelbrota (ta sama kolejność działań co w C++ i Javie).</summary>
    private static void MandelRow(int[] img, int width, int height, int y)
    {
        double cy = -1.5 + 3.0 * y / height;
        for (int x = 0; x < width; x++)
        {
            double cx = -2.0 + 3.0 * x / width;
            double zx = 0.0, zy = 0.0;
            int it = 0;
            while (it < MandelMaxIter)
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

    /// <summary>Z2.1: static – bloki wierszy na wątkach Thread; dynamic – Parallel.For (pula wątków).</summary>
    private static Bench Mandelbrot(Options o)
    {
        int size = (int)o.Size, threads = o.Threads;
        int[] img = new int[size * size];
        Action run = o.Variant == "static"
            ? () => RunThreads(threads, t =>
            {
                int from = (int)((long)size * t / threads), to = (int)((long)size * (t + 1) / threads);
                for (int y = from; y < to; y++)
                    MandelRow(img, size, size, y);
            })
            : () => Parallel.For(0, size, new ParallelOptions { MaxDegreeOfParallelism = threads },
                y => MandelRow(img, size, size, y));
        return new Bench
        {
            Prepare = () => Array.Clear(img),
            Run = run,
            Result = () =>
            {
                long s = 0;
                foreach (int v in img)
                    s += v;
                return s.ToString(CultureInfo.InvariantCulture);
            },
        };
    }

    /// <summary>Z2.2: koszt synchronizacji przy inkrementacji licznika.</summary>
    private static Bench Sync(Options o)
    {
        long total = o.Size;
        int threads = o.Threads;
        long sum = 0;
        Action run = o.Variant switch
        {
            "mutex" => () =>
            {
                var gate = new Lock();
                long counter = 0;
                RunThreads(threads, t =>
                {
                    for (long k = Share(total, threads, t); k > 0; k--)
                        lock (gate)
                            counter++;
                });
                sum = counter;
            },
            "atomic" => () =>
            {
                long counter = 0;
                RunThreads(threads, t =>
                {
                    for (long k = Share(total, threads, t); k > 0; k--)
                        Interlocked.Increment(ref counter);
                });
                sum = counter;
            },
            // Własny licznik każdego wątku: w sąsiednich komórkach (local-shared – false sharing) albo
            // co 8 elementów = 64 B (local-padded). Ta sama instrukcja co w „atomic” (lock add),
            // więc różnica wynika wyłącznie z układu danych w pamięci.
            _ => () =>
            {
                int stride = o.Variant == "local-padded" ? CacheLine / sizeof(long) : 1;
                long[] counters = new long[(threads + 1) * stride];
                RunThreads(threads, t =>
                {
                    int idx = (t + 1) * stride - (stride == 1 ? 1 : 0);
                    for (long k = Share(total, threads, t); k > 0; k--)
                        Interlocked.Increment(ref counters[idx]);
                });
                sum = counters.Sum();
            },
        };
        return new Bench { Run = run, Result = () => sum.ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>Z2.3: 1 producent, (wątki − 1) konsumentów, BlockingCollection o pojemności 1024.</summary>
    private static Bench Queue(Options o)
    {
        int messages = (int)o.Size, consumers = o.Threads - 1;
        long sum = 0;
        return new Bench
        {
            Run = () =>
            {
                using var queue = new BlockingCollection<int>(QueueCapacity);
                long[] partial = new long[consumers];
                var pool = new Thread[consumers];
                for (int c = 0; c < consumers; c++)
                {
                    int id = c;
                    pool[c] = new Thread(() =>
                    {
                        long s = 0;
                        foreach (int v in queue.GetConsumingEnumerable())
                            s += v;
                        partial[id] = s;
                    });
                    pool[c].Start();
                }
                for (int i = 1; i <= messages; i++)
                    queue.Add(i);
                queue.CompleteAdding();
                foreach (var th in pool)
                    th.Join();
                sum = partial.Sum();
            },
            Result = () => sum.ToString(CultureInfo.InvariantCulture),
        };
    }
}
