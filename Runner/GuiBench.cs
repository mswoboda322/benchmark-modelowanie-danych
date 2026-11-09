// K3 – uruchamianie aplikacji okienkowych (GuiCpp, GuiCs, GuiJava), statystyki, tabele, CSV.
using System.ComponentModel;
using System.Globalization;
using System.Security.Principal;
using Spectre.Console;

namespace Runner;

/// <summary>Jedno uruchomienie aplikacji GUI.</summary>
internal sealed record GuiJob(string Scenario, string Variant, long Param, int Repetition, bool WarmupRun,
    bool Cold, Language Language, int Iter, int Warmup);

/// <summary>Wartość zapisana przez aplikację GUI (linia pliku --out).</summary>
internal sealed record GuiValue(GuiJob Job, int Iteration, bool Warmup, string Metric, long Value);

/// <summary>Wynik procesu GUI (+ czas startu dla scenariusza startup).</summary>
internal sealed record GuiRun(GuiJob Job, ProcessResult? Process, double StartupMs, string? Error);

/// <summary>Podsumowanie K3 w formacie długim: scenariusz × wariant × parametr × język × metryka.</summary>
internal sealed record GuiStat(string Scenario, string Variant, long Param, string Language, string Metric, double Value);

internal static class GuiBench
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const double FreezeMs = 100.0; // przerwa w obsłudze zdarzeń uznawana za „zamrożenie” interfejsu

    public static void Run(Settings s, Language[] languages, Random rng, string outDir, string bin)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold yellow]K3 – aplikacja okienkowa[/]"));
        var langs = languages.Where(l => l.Gui is not null).ToArray();
        if (langs.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Brak programów okienkowych.[/]");
            return;
        }
        AnsiConsole.MarkupLine("[grey]Podczas pomiarów na ekranie pojawiają się okna aplikacji – nie używaj w tym czasie komputera.[/]");

        string? ramMap = s.Cold ? PrepareColdStart(bin) : null;
        var jobs = BuildJobs(s, langs, rng, ramMap is not null);
        var values = new List<GuiValue>();
        var runs = new List<GuiRun>();

        AnsiConsole.Progress()
            .AutoClear(true)
            .Columns(new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn(),
                new PercentageColumn(), new ElapsedTimeColumn(), new SpinnerColumn())
            .Start(ctx =>
            {
                var progress = ctx.AddTask("K3", maxValue: jobs.Count);
                foreach (var job in jobs)
                {
                    progress.Description = Markup.Escape(
                        $"{job.Scenario} | {job.Variant} | {job.Param} | {job.Language.Name} | powt. {job.Repetition}");
                    if (job.Cold)
                        ClearStandbyList(ramMap!);
                    var (vals, run) = RunOne(job);
                    values.AddRange(vals);
                    runs.Add(run);
                    progress.Increment(1);
                }
            });

        foreach (var r in runs.Where(r => r.Error is not null))
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape($"{r.Job.Language.Name} | {r.Job.Scenario} {r.Job.Variant} {r.Job.Param}: {r.Error}")}[/]");

        var stats = Summarize(langs, values, runs, bin);
        PrintTables(stats, langs);
        SaveCsv(outDir, values, runs, stats);
    }

    // ===================== Plan K3 =====================

    private static List<GuiJob> BuildJobs(Settings s, Language[] langs, Random rng, bool cold)
    {
        var jobs = new List<GuiJob>();
        void AddShuffled(Func<Language, GuiJob> make)
        {
            var order = langs.ToArray();
            rng.Shuffle(order);
            jobs.AddRange(order.Select(make));
        }

        // Z3.1 start: rundy z losową kolejnością języków; pierwsze rundy rozgrzewkowe (odrzucane).
        int startWarmup = s.Quick ? 1 : 2, startRuns = s.Quick ? 3 : 10;
        for (int rep = 1; rep <= startWarmup + startRuns; rep++)
        {
            int r = rep;
            AddShuffled(l => new GuiJob("startup", "warm", 0, r, r <= startWarmup, false, l, 1, 0));
        }
        if (cold)
            for (int rep = 1; rep <= (s.Quick ? 2 : 5); rep++)
            {
                int r = rep;
                AddShuffled(l => new GuiJob("startup", "cold", 0, r, false, true, l, 1, 0));
            }

        // Z3.2 wypełnienie listy i rysowanie – iteracje wewnątrz jednego procesu.
        int iter = s.Quick ? 3 : 10, warmup = s.Quick ? 1 : 2;
        foreach (long n in s.Quick ? new long[] { 10_000 } : [10_000, 100_000])
            AddShuffled(l => new GuiJob("fill", "-", n, 1, false, false, l, iter, warmup));
        long frames = s.Quick ? 30 : 100;
        AddShuffled(l => new GuiJob("draw", "-", frames, 1, false, false, l, s.Quick ? 2 : 5, 1));

        // Z3.3 responsywność – czas trwania w ms.
        long durationMs = s.Quick ? 2000 : 5000;
        foreach (string variant in new[] { "idle", "ui-thread", "worker" })
            AddShuffled(l => new GuiJob("responsive", variant, durationMs, 1, false, false, l, 1, 0));
        return jobs;
    }

    // ===================== Uruchomienie =====================

    private static (List<GuiValue>, GuiRun) RunOne(GuiJob job)
    {
        var values = new List<GuiValue>();
        string outFile = Path.Combine(Path.GetTempPath(), $"bench_gui_{Guid.NewGuid():N}.csv");
        string[] args =
        [
            "--bench", job.Scenario, "--variant", job.Variant, "--param", job.Param.ToString(Inv),
            "--iter", job.Iter.ToString(Inv), "--warmup", job.Warmup.ToString(Inv), "--out", outFile,
        ];
        ProcessResult result;
        try
        {
            result = ProcessRunner.Run(job.Language.Gui!, args, TimeSpan.FromMinutes(10),
                pollThreads: job.Scenario == "startup");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return (values, new GuiRun(job, null, double.NaN, ex.Message));
        }

        string? error = result.TimedOut ? "przekroczono limit czasu"
            : result.ExitCode != 0 ? $"kod wyjścia {result.ExitCode}: {result.Stderr.Trim()}"
            : !File.Exists(outFile) ? "brak pliku wyników"
            : null;
        if (File.Exists(outFile))
        {
            foreach (string line in File.ReadAllLines(outFile))
            {
                // język;scenariusz;wariant;parametr;iteracja;rozgrzewka;metryka;wartość
                string[] f = line.Split(';');
                if (f.Length != 8 || f[1] != job.Scenario
                    || !int.TryParse(f[4], Inv, out int iteration) || !long.TryParse(f[7], Inv, out long value))
                {
                    if (line.Trim().Length > 0)
                        error ??= $"niepoprawna linia: \"{line}\"";
                    continue;
                }
                values.Add(new GuiValue(job, iteration, f[5] == "1", f[6], value));
            }
            File.Delete(outFile);
        }

        double startupMs = double.NaN;
        if (job.Scenario == "startup")
        {
            var paint = values.FirstOrDefault(v => v.Metric == "first_paint_ns");
            if (paint is null)
                error ??= "brak znacznika pierwszego rysowania";
            else
                startupMs = (paint.Value - ProcessRunner.TicksToNs(result.StartTicks)) / 1e6;
        }
        return (values, new GuiRun(job, result, startupMs, error));
    }

    /// <summary>Zimny start wymaga wyczyszczenia systemowej pamięci podręcznej plików (lista standby).</summary>
    private static string? PrepareColdStart(string bin)
    {
        bool admin = OperatingSystem.IsWindows()
                     && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        string? ramMap = new[] { Path.Combine(bin, "RAMMap64.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Path.Combine(p, "RAMMap64.exe")))
            .FirstOrDefault(File.Exists);
        if (!admin || ramMap is null)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ --cold pominięte: wymaga uruchomienia jako administrator i RAMMap64.exe " +
                                   "(Sysinternals) w folderze bin\\ lub w PATH.[/]");
            return null;
        }
        return ramMap;
    }

    private static void ClearStandbyList(string ramMap)
    {
        ProcessRunner.Run(new LaunchSpec(ramMap, []), ["-accepteula", "-Es"], TimeSpan.FromMinutes(2));
        Thread.Sleep(2000); // wyciszenie systemu po czyszczeniu
    }

    // ===================== Statystyki =====================

    private static List<GuiStat> Summarize(Language[] langs, List<GuiValue> values, List<GuiRun> runs, string bin)
    {
        var stats = new List<GuiStat>();
        void Add(GuiJob j, string metric, double value) =>
            stats.Add(new GuiStat(j.Scenario, j.Variant, j.Param, j.Language.Name, metric, value));

        // Z3.1 – start: tylko uruchomienia pomiarowe.
        foreach (var g in runs.Where(r => r.Job.Scenario == "startup" && !r.Job.WarmupRun && r.Error is null)
                     .GroupBy(r => (r.Job.Variant, r.Job.Language.Name)))
        {
            var job = g.First().Job;
            double[] t = g.Select(r => r.StartupMs).ToArray();
            Add(job, "start_mediana_ms", Stats.Median(t));
            Add(job, "start_srednia_ms", Stats.Mean(t));
            Add(job, "start_ci95_ms", Stats.Ci95(t));
            Add(job, "start_min_ms", t.Min());
            Add(job, "szczyt_ws_mb", Stats.Median(g.Select(r => r.Process!.PeakWorkingSetMb).ToArray()));
            Add(job, "szczyt_prywatna_mb", Stats.Median(g.Select(r => r.Process!.PeakPrivateMb).ToArray()));
            Add(job, "watki_max", Stats.Median(g.Select(r => (double)r.Process!.MaxThreads).ToArray()));
            Add(job, "rozmiar_programu_kb", job.Language.GuiFiles
                .Select(f => Path.Combine(bin, f)).Where(File.Exists).Sum(f => new FileInfo(f).Length) / 1024.0);
            Add(job, "pomiary", t.Length);
        }

        // Z3.2 – fill i draw: czasy iteracji (bez rozgrzewki).
        foreach (var g in values.Where(v => v.Metric == "time_ns").GroupBy(v => v.Job))
        {
            var job = g.Key;
            double[] t = g.Where(v => !v.Warmup).Select(v => v.Value / 1e6).ToArray();
            if (t.Length == 0)
                continue;
            Add(job, "mediana_ms", Stats.Median(t));
            Add(job, "ci95_ms", Stats.Ci95(t));
            Add(job, "pierwsza_iteracja_ms", g.FirstOrDefault(v => v.Iteration == 1) is { } first ? first.Value / 1e6 : double.NaN);
            if (job.Scenario == "draw")
                Add(job, "klatki_na_s", job.Param / (Stats.Median(t) / 1000.0));
            var proc = runs.First(r => r.Job == job).Process;
            Add(job, "szczyt_ws_mb", proc?.PeakWorkingSetMb ?? double.NaN);
        }

        // Z3.3 – responsywność: opóźnienia obsługi zdarzeń.
        foreach (var g in values.Where(v => v.Job.Scenario == "responsive").GroupBy(v => v.Job))
        {
            var job = g.Key;
            double[] lat = g.Where(v => v.Metric == "latency_ns").Select(v => v.Value / 1e6).ToArray();
            if (lat.Length == 0)
                continue;
            Add(job, "opoznienie_srednie_ms", Stats.Mean(lat));
            Add(job, "opoznienie_p95_ms", Stats.Percentile(lat, 95));
            Add(job, "opoznienie_max_ms", lat.Max());
            Add(job, "zamrozenia_powyzej_100ms", lat.Count(x => x > FreezeMs));
            Add(job, "zdarzenia", lat.Length);
            Add(job, "obrazy", g.FirstOrDefault(v => v.Metric == "images")?.Value ?? double.NaN);
        }
        return stats;
    }

    // ===================== Tabele =====================

    private static void PrintTables(List<GuiStat> stats, Language[] langs)
    {
        void Pivot(string title, string scenario, Func<GuiStat, string> label, string metric, string metricTitle,
            bool? higherIsBetter, string format)
        {
            var rows = stats.Where(r => r.Scenario == scenario && r.Metric == metric).GroupBy(label).ToList();
            if (rows.Count == 0)
                return;
            AnsiConsole.Write(Report.Pivot(title, rows, langs, r => r.Language,
                new PivotMetric<GuiStat>(metricTitle, r => r.Value, higherIsBetter, format)));
        }

        AnsiConsole.WriteLine();
        Func<GuiStat, string> startLabel = r => r.Variant == "cold" ? "zimny start" : "ciepły start";
        Pivot("Z3.1 Start aplikacji", "startup", startLabel, "start_mediana_ms", "czas do pierwszego okna – mediana [ms]", false, "N1");
        Pivot("Z3.1 Start aplikacji", "startup", startLabel, "start_ci95_ms", "± 95% CI [ms]", null, "N1");
        Pivot("Z3.1 Start aplikacji", "startup", startLabel, "szczyt_ws_mb", "szczytowy zestaw roboczy [MB]", false, "N1");
        Pivot("Z3.1 Start aplikacji", "startup", startLabel, "szczyt_prywatna_mb", "szczytowa pamięć prywatna [MB]", false, "N1");
        Pivot("Z3.1 Start aplikacji", "startup", startLabel, "watki_max", "liczba wątków procesu", false, "N0");
        Pivot("Z3.1 Start aplikacji", "startup", startLabel, "rozmiar_programu_kb", "rozmiar programu [KB] (bez środowiska uruchomieniowego)", false, "N0");

        Func<GuiStat, string> fillLabel = r => $"{r.Param:N0} elementów".Replace(',', ' ');
        Pivot("Z3.2 Wypełnienie listy", "fill", fillLabel, "mediana_ms", "mediana [ms]", false, "N1");
        Pivot("Z3.2 Wypełnienie listy", "fill", fillLabel, "pierwsza_iteracja_ms", "1. iteracja [ms]", false, "N1");
        Pivot("Z3.2 Rysowanie obrazu 800×800", "draw", r => $"{r.Param} klatek", "klatki_na_s", "klatki/s", true, "N1");

        Func<GuiStat, string> respLabel = r => r.Variant;
        Pivot("Z3.3 Responsywność", "responsive", respLabel, "opoznienie_srednie_ms", "średnie opóźnienie zdarzeń [ms]", false, "N2");
        Pivot("Z3.3 Responsywność", "responsive", respLabel, "opoznienie_p95_ms", "opóźnienie p95 [ms]", false, "N2");
        Pivot("Z3.3 Responsywność", "responsive", respLabel, "opoznienie_max_ms", "opóźnienie maks. [ms]", false, "N1");
        Pivot("Z3.3 Responsywność", "responsive", respLabel, "zamrozenia_powyzej_100ms", "zdarzenia opóźnione > 100 ms", false, "N0");
        Pivot("Z3.3 Responsywność", "responsive", respLabel, "obrazy", "obliczone obrazy (przepustowość)", true, "N0");
    }

    // ===================== CSV =====================

    private static void SaveCsv(string outDir, List<GuiValue> values, List<GuiRun> runs, List<GuiStat> stats)
    {
        AnsiConsole.WriteLine();
        Report.WriteCsv(Path.Combine(outDir, "K3_pomiary.csv"),
            "Scenariusz;Wariant;Parametr;Język;Powtórzenie;Iteracja;Rozgrzewka;Metryka;Wartość",
            values.Select(v => new object?[]
            {
                v.Job.Scenario, v.Job.Variant, v.Job.Param, v.Job.Language.Name, v.Job.Repetition,
                v.Iteration, v.Warmup, v.Metric, v.Value,
            }));
        Report.WriteCsv(Path.Combine(outDir, "K3_procesy.csv"),
            "Scenariusz;Wariant;Parametr;Język;Powtórzenie;Rozgrzewka;Zimny_start;Czas_startu_ms;Czas_procesu_ms;" +
            "Szczyt_WS_MB;Szczyt_prywatna_MB;CPU_ms;Wątki_max;Kod_wyjścia;Błąd",
            runs.Select(r => new object?[]
            {
                r.Job.Scenario, r.Job.Variant, r.Job.Param, r.Job.Language.Name, r.Job.Repetition, r.Job.WarmupRun,
                r.Job.Cold, r.StartupMs, r.Process?.WallMs, r.Process?.PeakWorkingSetMb, r.Process?.PeakPrivateMb,
                r.Process?.CpuMs, r.Process?.MaxThreads, r.Process?.ExitCode, r.Error,
            }));
        Report.WriteCsv(Path.Combine(outDir, "K3_podsumowanie.csv"),
            "Scenariusz;Wariant;Parametr;Język;Metryka;Wartość",
            stats.Select(s => new object?[] { s.Scenario, s.Variant, s.Param, s.Language, s.Metric, s.Value }));
    }
}
