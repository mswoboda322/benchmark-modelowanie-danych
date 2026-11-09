// Plan pomiarów K1 i K2 – lista konfiguracji (docs/02-plan-badania.md, pkt 3).
namespace Runner;

/// <summary>Jedna konfiguracja zadania K1/K2 (uruchamiana osobno dla każdego języka).</summary>
internal sealed record BenchConfig(string Criterion, string Task, string Variant, long Size, int Threads, bool Flush)
{
    /// <summary>Opis wiersza tabeli, np. „seq | 1 MB | flush”.</summary>
    public string Label => Task switch
    {
        "mem-access" => $"{Variant} | {FormatKb(Size)} | {(Flush ? "flush" : "bez flush")}",
        "matmul" => $"{Variant} | N={Size} | {(Flush ? "flush" : "bez flush")}",
        "objects" => Variant == "trees" ? $"trees | d={Size}" : $"{Variant} | {Size:N0}".Replace(',', ' '),
        "mandelbrot" => $"{Variant} | {Size}² | {Threads} w.",
        "sync" => $"{Variant} | {Threads} w.",
        "queue" => $"{Threads} w. (1 prod. + {Threads - 1} kons.)",
        _ => $"{Variant} | {Size} | {Threads} w.",
    };

    private static string FormatKb(long kb) => kb >= 1024 ? $"{kb / 1024} MB" : $"{kb} KB";
}

internal static class Plan
{
    /// <summary>Liczba dostępów w mem-access – taka sama jak w programach (2^24).</summary>
    public const long MemAccesses = 1L << 24;

    /// <summary>Liczby wątków: 1, 2, 4, 6 (rdzenie P), 8, 14 (rdzenie fizyczne), 20 (wątki logiczne).</summary>
    public static int[] ThreadCounts(bool quick)
    {
        int logical = Environment.ProcessorCount;
        int[] wanted = quick ? [1, 2, 4] : [1, 2, 4, 6, 8, 14, 20];
        return wanted.Where(t => t <= logical).Append(logical).Distinct().Order().ToArray();
    }

    public static List<BenchConfig> K1(bool quick)
    {
        var list = new List<BenchConfig>();
        long[] memSizes = quick ? [16, 1024, 65536] : [16, 128, 1024, 4096, 16384, 65536, 262144];
        foreach (bool flush in new[] { true, false })
            foreach (string variant in new[] { "seq", "rand" })
                foreach (long kb in memSizes)
                    list.Add(new("K1", "mem-access", variant, kb, 1, flush));

        long[] matmulSizes = quick ? [256] : [256, 512, 1024];
        foreach (bool flush in new[] { true, false })
            foreach (string variant in new[] { "ijk", "ikj" })
                foreach (long n in matmulSizes)
                    list.Add(new("K1", "matmul", variant, n, 1, flush));

        // Z1.3 bez czyszczenia cache: dane i tak są wielokrotnie większe niż L3, a bufor 128 MB
        // zafałszowałby pomiar pamięci szczytowej.
        long points = quick ? 1_000_000 : 10_000_000;
        list.Add(new("K1", "objects", "points-value", points, 1, false));
        list.Add(new("K1", "objects", "points-ref", points, 1, false));
        foreach (long depth in quick ? new long[] { 14 } : [14, 16, 18])
            list.Add(new("K1", "objects", "trees", depth, 1, false));
        return list;
    }

    public static List<BenchConfig> K2(bool quick)
    {
        var list = new List<BenchConfig>();
        int[] threads = ThreadCounts(quick);
        long mandelSize = quick ? 600 : 2000;
        foreach (string variant in new[] { "static", "dynamic" })
            foreach (int t in threads)
                list.Add(new("K2", "mandelbrot", variant, mandelSize, t, true));

        long ops = quick ? 1_000_000 : 10_000_000;
        foreach (string variant in new[] { "mutex", "atomic", "local-shared", "local-padded" })
            foreach (int t in threads)
                list.Add(new("K2", "sync", variant, ops, t, true));

        long messages = quick ? 100_000 : 1_000_000;
        foreach (int t in threads.Where(t => t >= 2))
            list.Add(new("K2", "queue", "blocking", messages, t, true));
        return list;
    }
}
