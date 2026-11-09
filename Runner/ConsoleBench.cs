// K1 i K2 – uruchamianie programów konsolowych (BenchCpp, BenchCs, BenchJava), statystyki, tabele, CSV.
using System.ComponentModel;
using System.Globalization;
using Spectre.Console;

namespace Runner;

/// <summary>Jedna iteracja zwrócona przez program (linia CSV na stdout).</summary>
internal sealed record IterationRow(BenchConfig Config, string Language, int Iteration, bool Warmup,
    double TimeMs, string Result, double CpuMs, int GcCount, double GcPauseMs);

/// <summary>Jedno uruchomienie procesu (konfiguracja × język).</summary>
internal sealed record RunInfo(BenchConfig Config, string Language, int Order, ProcessResult? Process, string? Error);

/// <summary>Statystyki konfiguracji dla jednego języka (z iteracji w stanie ustalonym).</summary>
internal sealed record Summary(
    BenchConfig Config, string Language, int Count,
    double MedianMs, double MeanMs, double StdMs, double MinMs, double Ci95Ms, double FirstIterMs,
    double CpuRatio, double GcCount, double GcPauseMs,
    double PeakWsMb, double PeakPrivateMb, double ProcessMs, string Result, bool Mismatch)
{
    /// <summary>T(najmniejsza liczba wątków) / T(wątki) – ten sam język i wariant.</summary>
    public double Speedup { get; init; } = double.NaN;

    /// <summary>Mediana czasu względem C++ (C++ = 1).</summary>
    public double VsCpp { get; init; } = double.NaN;
}

/// <summary>Główna metryka zadania (do tabel i CSV).</summary>
internal sealed record TaskMetric(string Name, Func<Summary, double> Value, bool HigherIsBetter, string Format);

internal static class ConsoleBench
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly Dictionary<string, string> TaskTitles = new()
    {
        ["mem-access"] = "Z1.1 Dostęp do pamięci",
        ["matmul"] = "Z1.2 Mnożenie macierzy",
        ["objects"] = "Z1.3 Obiekty w pamięci i alokacja",
        ["mandelbrot"] = "Z2.1 Mandelbrot – skalowanie",
        ["sync"] = "Z2.2 Koszt synchronizacji",
        ["queue"] = "Z2.3 Producent–konsument",
    };

    public static TaskMetric PrimaryMetric(string task) => task switch
    {
        "mem-access" => new("ns/dostęp", s => s.MedianMs * 1e6 / Plan.MemAccesses, false, "N2"),
        "matmul" => new("GFLOP/s", s => 2.0 * Math.Pow(s.Config.Size, 3) / (s.MedianMs * 1e6), true, "N2"),
        "sync" => new("ns/operację", s => s.MedianMs * 1e6 / s.Config.Size, false, "N1"),
        "queue" => new("mln komunikatów/s", s => s.Config.Size / (s.MedianMs / 1000.0) / 1e6, true, "N2"),
        _ => new("mediana [ms]", s => s.MedianMs, false, "N1"),
    };

    public static void Run(string criterion, List<BenchConfig> plan, Settings settings, Language[] languages,
        Random rng, string outDir)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule(criterion == "K1"
            ? "[bold yellow]K1 – efektywność pracy z pamięcią[/]"
            : "[bold yellow]K2 – wykorzystanie wielu rdzeni[/]"));
        var langs = languages.Where(l => l.Console is not null).ToArray();
        if (langs.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Brak programów konsolowych.[/]");
            return;
        }

        var iterations = new List<IterationRow>();
        var runs = new List<RunInfo>();
        AnsiConsole.Progress()
            .AutoClear(true)
            .Columns(new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn(),
                new PercentageColumn(), new ElapsedTimeColumn(), new SpinnerColumn())
            .Start(ctx =>
            {
                var progress = ctx.AddTask(criterion, maxValue: plan.Count * langs.Length);
                int order = 0;
                foreach (var cfg in plan)
                {
                    // Losowa kolejność języków w każdej konfiguracji – nagrzewanie CPU nie faworyzuje żadnego.
                    var shuffled = langs.ToArray();
                    rng.Shuffle(shuffled);
                    foreach (var lang in shuffled)
                    {
                        progress.Description = Markup.Escape($"{cfg.Task} | {cfg.Label} | {lang.Name}");
                        var (rows, run) = RunOne(cfg, lang, settings, ++order);
                        iterations.AddRange(rows);
                        runs.Add(run);
                        progress.Increment(1);
                    }
                }
            });

        foreach (var r in runs.Where(r => r.Error is not null))
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape($"{r.Language} | {r.Config.Task} | {r.Config.Label}: {r.Error}")}[/]");

        var summaries = Summarize(plan, langs, iterations, runs);
        PrintTables(summaries, langs);
        PrintRatioSummary(criterion, summaries, langs);
        SaveCsv(criterion, outDir, iterations, runs, summaries);
    }

    private static (List<IterationRow>, RunInfo) RunOne(BenchConfig cfg, Language lang, Settings s, int order)
    {
        string[] args =
        [
            cfg.Task, cfg.Variant, cfg.Size.ToString(Inv), cfg.Threads.ToString(Inv),
            "--iter", s.Iter.ToString(Inv), "--warmup", s.Warmup.ToString(Inv), "--flush", cfg.Flush ? "on" : "off",
        ];
        var rows = new List<IterationRow>();
        ProcessResult result;
        try
        {
            result = ProcessRunner.Run(lang.Console!, args, TimeSpan.FromMinutes(30));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return (rows, new RunInfo(cfg, lang.Name, order, null, ex.Message));
        }

        string? error = result.TimedOut ? "przekroczono limit czasu"
            : result.ExitCode != 0 ? $"kod wyjścia {result.ExitCode}: {result.Stderr.Trim()}"
            : null;
        foreach (string raw in result.Stdout.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;
            // język;zadanie;wariant;rozmiar;wątki;iteracja;rozgrzewka;czas_ns;wynik;cpu_ms;gc_liczba;gc_pauza_ms
            string[] f = line.Split(';');
            if (f.Length != 12 || f[1] != cfg.Task || f[2] != cfg.Variant
                || !int.TryParse(f[5], Inv, out int iteration)
                || !long.TryParse(f[7], Inv, out long timeNs)
                || !double.TryParse(f[9], NumberStyles.Float, Inv, out double cpuMs)
                || !int.TryParse(f[10], Inv, out int gcCount)
                || !double.TryParse(f[11], NumberStyles.Float, Inv, out double gcPause))
            {
                error ??= $"niepoprawna linia: \"{line}\"";
                continue;
            }
            rows.Add(new IterationRow(cfg, lang.Name, iteration, f[6] == "1", timeNs / 1e6, f[8], cpuMs, gcCount, gcPause));
        }
        if (error is null && rows.Count(r => !r.Warmup) != s.Iter)
            error = $"niepełne wyniki ({rows.Count(r => !r.Warmup)} z {s.Iter})";
        return (rows, new RunInfo(cfg, lang.Name, order, result, error));
    }

    // ===================== Statystyki =====================

    private static List<Summary> Summarize(List<BenchConfig> plan, Language[] langs,
        List<IterationRow> iterations, List<RunInfo> runs)
    {
        var byKey = iterations.ToLookup(i => (i.Config, i.Language));
        var summaries = new List<Summary>();
        foreach (var cfg in plan)
        {
            // Wynik referencyjny = najczęstszy wynik kontrolny wśród wszystkich języków.
            string? reference = langs.SelectMany(l => byKey[(cfg, l.Name)]).Where(i => !i.Warmup)
                .GroupBy(i => i.Result).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            foreach (var lang in langs)
            {
                var all = byKey[(cfg, lang.Name)].ToList();
                var measured = all.Where(i => !i.Warmup).ToList();
                if (measured.Count == 0)
                    continue;
                double[] t = measured.Select(i => i.TimeMs).ToArray();
                var proc = runs.First(r => r.Config == cfg && r.Language == lang.Name).Process;
                summaries.Add(new Summary(cfg, lang.Name, t.Length,
                    Stats.Median(t), Stats.Mean(t), Stats.Std(t), t.Min(), Stats.Ci95(t),
                    all.FirstOrDefault(i => i.Iteration == 1)?.TimeMs ?? double.NaN,
                    measured.Average(i => i.CpuMs / i.TimeMs),
                    measured.Average(i => i.GcCount), measured.Average(i => i.GcPauseMs),
                    proc?.PeakWorkingSetMb ?? double.NaN, proc?.PeakPrivateMb ?? double.NaN, proc?.WallMs ?? double.NaN,
                    string.Join(" / ", measured.Select(i => i.Result).Distinct()),
                    measured.Any(i => i.Result != reference)));
            }
        }

        // Przyspieszenie (względem najmniejszej liczby wątków) i czas względem C++.
        var index = summaries.ToDictionary(s => (s.Config, s.Language));
        return summaries.Select(s =>
        {
            var baseCfg = summaries
                .Where(b => b.Language == s.Language && b.Config with { Threads = 0 } == s.Config with { Threads = 0 })
                .MinBy(b => b.Config.Threads);
            return s with
            {
                Speedup = baseCfg is null ? double.NaN : baseCfg.MedianMs / s.MedianMs,
                VsCpp = index.TryGetValue((s.Config, "C++"), out var cpp) ? s.MedianMs / cpp.MedianMs : double.NaN,
            };
        }).ToList();
    }

    // ===================== Tabele =====================

    private static void PrintTables(List<Summary> summaries, Language[] langs)
    {
        foreach (var group in summaries.GroupBy(s => s.Config.Task))
        {
            string task = group.Key;
            var primary = PrimaryMetric(task);
            var views = new List<PivotMetric<Summary>>
            {
                new(primary.Name, primary.Value, primary.HigherIsBetter, primary.Format),
            };
            switch (task)
            {
                case "matmul":
                    views.Add(new("mediana [ms]", s => s.MedianMs, false, "N1"));
                    break;
                case "objects":
                    views.Add(new("1. iteracja [ms] (bez rozgrzewki)", s => s.FirstIterMs, false, "N1"));
                    views.Add(new("szczyt pamięci procesu [MB]", s => s.PeakWsMb, false, "N0"));
                    views.Add(new("liczba GC / iterację", s => s.GcCount, null, "N1"));
                    views.Add(new("pauzy GC [ms] / iterację", s => s.GcPauseMs, null, "N1"));
                    break;
                case "mandelbrot":
                    views.Add(new("przyspieszenie T(1)/T(n)", s => s.Speedup, true, "N2"));
                    views.Add(new("efektywność S/n", s => s.Speedup / s.Config.Threads, true, "N2"));
                    views.Add(new("czas CPU / czas rzeczywisty", s => s.CpuRatio, null, "N1"));
                    break;
            }

            AnsiConsole.WriteLine();
            foreach (var view in views)
                AnsiConsole.Write(Report.Pivot(TaskTitles.GetValueOrDefault(task, task),
                    group.GroupBy(s => s.Config.Label), langs, s => s.Language, view, s => s.Mismatch));
        }
        if (summaries.Any(s => s.Mismatch))
            AnsiConsole.MarkupLine("[yellow]⚠ Niezgodne wyniki kontrolne – szczegóły w pliku *_podsumowanie.csv.[/]");
    }

    /// <summary>Średnia geometryczna czasu względem C++ dla każdego zadania (C++ = 1,00).</summary>
    private static void PrintRatioSummary(string criterion, List<Summary> summaries, Language[] langs)
    {
        if (langs.All(l => l.Name != "C++"))
            return;
        var ratios = summaries.GroupBy(s => (s.Config.Task, s.Language))
            .Select(g => (Task: g.Key.Task, Language: g.Key.Language, Value: Stats.GeoMean(g.Select(s => s.VsCpp).ToArray())))
            .ToList();
        AnsiConsole.WriteLine();
        AnsiConsole.Write(Report.Pivot($"{criterion} – podsumowanie", ratios.GroupBy(r => TaskTitles.GetValueOrDefault(r.Task, r.Task)),
            langs, r => r.Language,
            new PivotMetric<(string Task, string Language, double Value)>(
                "czas względem C++ (średnia geometryczna, C++ = 1,00)", r => r.Value, false, "N2")));
    }

    // ===================== CSV =====================

    private static void SaveCsv(string criterion, string outDir, List<IterationRow> iterations, List<RunInfo> runs,
        List<Summary> summaries)
    {
        AnsiConsole.WriteLine();
        Report.WriteCsv(Path.Combine(outDir, $"{criterion}_iteracje.csv"),
            "Kryterium;Zadanie;Wariant;Rozmiar;Wątki;Flush;Język;Iteracja;Rozgrzewka;Czas_ms;Wynik;CPU_ms;GC_liczba;GC_pauza_ms",
            iterations.Select(i => new object?[]
            {
                i.Config.Criterion, i.Config.Task, i.Config.Variant, i.Config.Size, i.Config.Threads, i.Config.Flush,
                i.Language, i.Iteration, i.Warmup, i.TimeMs, i.Result, i.CpuMs, i.GcCount, i.GcPauseMs,
            }));

        Report.WriteCsv(Path.Combine(outDir, $"{criterion}_procesy.csv"),
            "Kryterium;Zadanie;Wariant;Rozmiar;Wątki;Flush;Język;Kolejność;Czas_procesu_ms;Szczyt_WS_MB;Szczyt_prywatna_MB;CPU_procesu_ms;Kod_wyjścia;Błąd",
            runs.Select(r => new object?[]
            {
                r.Config.Criterion, r.Config.Task, r.Config.Variant, r.Config.Size, r.Config.Threads, r.Config.Flush,
                r.Language, r.Order, r.Process?.WallMs, r.Process?.PeakWorkingSetMb, r.Process?.PeakPrivateMb,
                r.Process?.CpuMs, r.Process?.ExitCode, r.Error,
            }));

        Report.WriteCsv(Path.Combine(outDir, $"{criterion}_podsumowanie.csv"),
            "Kryterium;Zadanie;Wariant;Rozmiar;Wątki;Flush;Język;Liczba_pomiarów;Mediana_ms;Średnia_ms;Odch_std_ms;" +
            "Min_ms;CI95_ms;Pierwsza_iteracja_ms;Metryka;Wartość_metryki;Przyspieszenie;Czas_względem_C++;" +
            "CPU_do_czasu;GC_liczba;GC_pauza_ms;Szczyt_WS_MB;Szczyt_prywatna_MB;Czas_procesu_ms;Wynik;Wynik_niezgodny",
            summaries.Select(s =>
            {
                var m = PrimaryMetric(s.Config.Task);
                return new object?[]
                {
                    s.Config.Criterion, s.Config.Task, s.Config.Variant, s.Config.Size, s.Config.Threads, s.Config.Flush,
                    s.Language, s.Count, s.MedianMs, s.MeanMs, s.StdMs, s.MinMs, s.Ci95Ms, s.FirstIterMs,
                    m.Name, m.Value(s), s.Speedup, s.VsCpp, s.CpuRatio, s.GcCount, s.GcPauseMs,
                    s.PeakWsMb, s.PeakPrivateMb, s.ProcessMs, s.Result, s.Mismatch,
                };
            }));
    }
}
