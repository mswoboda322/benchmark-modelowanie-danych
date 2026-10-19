// Runner – uruchamia BenchC.exe, BenchCpp.exe i BenchCs.exe, zbiera pomiary,
// liczy statystyki i zapisuje wyniki do results.csv / summary.csv.
//
// Użycie: Runner.exe [powtórzenia] [N_matmul] [N_primes] [N_fibonacci]
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Spectre.Console;

namespace Runner;

/// <summary>Badany program (język) i kolor używany na wykresach.</summary>
internal sealed record Language(string Name, string ExePath, Color Color);

/// <summary>Zadanie obliczeniowe z rozmiarem problemu.</summary>
internal sealed record BenchTask(string Name, int N);

/// <summary>Pojedynczy (surowy) pomiar.</summary>
internal sealed record Measurement(
    string Task, string Language, int N, int Threads, int RunNo,
    double TimeMs, double ProcessMs, string Result);

/// <summary>Statystyki dla kombinacji zadanie × język × liczba wątków.</summary>
internal sealed record Stats(
    string Task, string Language, int Threads, int Count,
    double AvgMs, double MinMs, double StdMs, double Speedup,
    double ProcessAvgMs, string Result, bool Mismatch);

internal static class Program
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // Przy przekierowaniu do pliku (Runner.exe > log.txt) Spectre przyjmuje 80 kolumn – za mało na tabele.
        if (Console.IsOutputRedirected)
            AnsiConsole.Profile.Width = 140;

        // ---------- Argumenty ----------
        int[] defaults = [5, 1000, 10_000_000, 40];
        string[] argNames = ["powtórzenia", "N_matmul", "N_primes", "N_fibonacci"];
        var values = (int[])defaults.Clone();
        for (int i = 0; i < args.Length && i < values.Length; i++)
        {
            if (!int.TryParse(args[i], out values[i]) || values[i] < 1)
            {
                AnsiConsole.MarkupLine($"[red]Niepoprawny argument {argNames[i]}: {Markup.Escape(args[i])}[/]");
                AnsiConsole.MarkupLine("Użycie: Runner.exe [powtórzenia] [N_matmul] [N_primes] [N_fibonacci]");
                return 1;
            }
        }
        int repetitions = values[0];
        BenchTask[] tasks =
        [
            new("matmul", values[1]),
            new("primes", values[2]),
            new("fibonacci", values[3]),
        ];
        int[] threadCounts = new[] { 1, 2, 4, 8, Environment.ProcessorCount }
            .Distinct().Order().ToArray();
        int maxThreads = threadCounts.Max();

        // ---------- Wyszukanie programów w folderze Runnera ----------
        string dir = AppContext.BaseDirectory;
        var allLanguages = new[]
        {
            new Language("C", Path.Combine(dir, "BenchC.exe"), Color.DodgerBlue1),
            new Language("C++", Path.Combine(dir, "BenchCpp.exe"), Color.Orange1),
            new Language("C#", Path.Combine(dir, "BenchCs.exe"), Color.MediumPurple),
        };
        var languages = allLanguages.Where(l => File.Exists(l.ExePath)).ToArray();
        foreach (var missing in allLanguages.Except(languages))
            AnsiConsole.MarkupLine($"[yellow]⚠ Nie znaleziono {Markup.Escape(missing.ExePath)} – język {Markup.Escape(missing.Name)} pominięty.[/]");
        if (languages.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Brak programów do uruchomienia. Zbuduj rozwiązanie (Release x64).[/]");
            return 1;
        }

        PrintHeader(repetitions, tasks, threadCounts, languages);

        // ---------- Pomiary ----------
        var allMeasurements = new List<Measurement>();
        var allStats = new List<Stats>();
        foreach (var task in tasks)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold]{task.Name}[/] (N = {task.N.ToString("N0", Pl)})").LeftJustified());

            var (measurements, errors) = MeasureTask(task, languages, threadCounts, repetitions);
            foreach (string error in errors)
                AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(error)}[/]");

            var stats = ComputeStats(task, measurements, languages, threadCounts);
            allMeasurements.AddRange(measurements);
            allStats.AddRange(stats);
            AnsiConsole.Write(BuildTaskTable(task, stats, threadCounts, languages));
        }

        PrintSummary(tasks, allStats, languages, maxThreads);
        SaveCsv(dir, allMeasurements, allStats);
        return 0;
    }

    // ===================== Uruchamianie programów =====================

    /// <summary>Wykonuje wszystkie przebiegi jednego zadania z paskiem postępu.</summary>
    private static (List<Measurement>, List<string>) MeasureTask(
        BenchTask task, Language[] languages, int[] threadCounts, int repetitions)
    {
        var measurements = new List<Measurement>();
        var errors = new List<string>();
        int total = languages.Length * threadCounts.Length * (repetitions + 1);

        AnsiConsole.Progress()
            .AutoClear(true)
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new PercentageColumn(),
                new ElapsedTimeColumn(),
                new SpinnerColumn())
            .Start(ctx =>
            {
                var progress = ctx.AddTask(task.Name, maxValue: total);
                foreach (var lang in languages)
                    foreach (int threads in threadCounts)
                        for (int run = 0; run <= repetitions; run++)
                        {
                            // Przebieg nr 0 to rozgrzewka – jego wynik jest odrzucany.
                            string phase = run == 0 ? "rozgrzewka" : $"pomiar {run}/{repetitions}";
                            progress.Description = Markup.Escape(
                                $"{task.Name} | {lang.Name} | {ThreadsLabel(threads)} | {phase}");

                            if (TryRun(lang, task, threads, out var line, out double processMs, out string? error))
                            {
                                if (run > 0)
                                    measurements.Add(new Measurement(task.Name, lang.Name, task.N, threads, run,
                                        line.TimeMs, processMs, line.Result));
                            }
                            else
                            {
                                errors.Add($"{lang.Name}, {ThreadsLabel(threads)}, {phase}: {error}");
                            }
                            progress.Increment(1);
                        }
            });

        return (measurements, errors);
    }

    /// <summary>Uruchamia program jako osobny proces i parsuje jego jedną linię wyjścia.</summary>
    private static bool TryRun(Language lang, BenchTask task, int threads,
        out (string Result, double TimeMs) line, out double processMs, out string? error)
    {
        line = default;
        processMs = 0;
        error = null;

        var psi = new ProcessStartInfo(lang.ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(task.Name);
        psi.ArgumentList.Add(task.N.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(threads.ToString(CultureInfo.InvariantCulture));

        // Czas całego procesu: od startu do zakończenia (zawiera np. start runtime .NET i JIT).
        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi);
        if (process is null)
        {
            error = "nie udało się uruchomić procesu";
            return false;
        }
        var stderrTask = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        processMs = sw.Elapsed.TotalMilliseconds;

        if (process.ExitCode != 0)
        {
            error = $"kod wyjścia {process.ExitCode}: {stderrTask.Result.Trim()}";
            return false;
        }

        // Format: <język>;<zadanie>;<N>;<wątki>;<wynik_kontrolny>;<czas_ms>
        string[] fields = stdout.Trim().Split(';');
        if (fields.Length != 6
            || fields[1] != task.Name
            || !double.TryParse(fields[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double timeMs))
        {
            error = $"niepoprawne wyjście: \"{stdout.Trim()}\"";
            return false;
        }

        line = (fields[4], timeMs);
        return true;
    }

    // ===================== Statystyki =====================

    private static List<Stats> ComputeStats(
        BenchTask task, List<Measurement> measurements, Language[] languages, int[] threadCounts)
    {
        // Wynik referencyjny = najczęstszy wynik kontrolny wśród wszystkich języków i wątków.
        string? reference = measurements
            .GroupBy(m => m.Result)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var stats = new List<Stats>();
        foreach (var lang in languages)
        {
            double baseAvg = double.NaN;
            foreach (int threads in threadCounts)
            {
                var runs = measurements.Where(m => m.Language == lang.Name && m.Threads == threads).ToList();
                if (runs.Count == 0)
                {
                    stats.Add(new Stats(task.Name, lang.Name, threads, 0,
                        double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, "błąd", true));
                    continue;
                }

                double[] times = runs.Select(m => m.TimeMs).ToArray();
                double avg = times.Average();
                // Odchylenie standardowe z próby (n - 1).
                double std = times.Length > 1
                    ? Math.Sqrt(times.Sum(t => (t - avg) * (t - avg)) / (times.Length - 1))
                    : 0.0;
                if (threads == 1)
                    baseAvg = avg;

                bool mismatch = runs.Any(m => m.Result != reference);
                string result = mismatch
                    ? string.Join(", ", runs.Select(m => m.Result).Distinct())
                    : runs[0].Result;

                stats.Add(new Stats(task.Name, lang.Name, threads, runs.Count,
                    avg, times.Min(), std, baseAvg / avg,
                    runs.Average(m => m.ProcessMs), result, mismatch));
            }
        }
        return stats;
    }

    // ===================== Wyjście konsolowe =====================

    private static void PrintHeader(int repetitions, BenchTask[] tasks, int[] threadCounts, Language[] languages)
    {
        AnsiConsole.Write(new Rule("[bold yellow]Badanie wydajności: C vs C++ vs C#[/]"));

        var grid = new Grid().AddColumn(new GridColumn().PadRight(3)).AddColumn();
        grid.AddRow("[grey]Procesor[/]", Markup.Escape($"{CpuName()} – {Environment.ProcessorCount} wątków logicznych"));
        grid.AddRow("[grey]System[/]", Markup.Escape(System.Runtime.InteropServices.RuntimeInformation.OSDescription));
        grid.AddRow("[grey]Runtime .NET[/]", Markup.Escape(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription));
        grid.AddRow("[grey]Data[/]", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Pl));
        grid.AddRow("[grey]Języki[/]", Markup.Escape(string.Join(", ", languages.Select(l => l.Name))));
        grid.AddRow("[grey]Wątki[/]", string.Join(", ", threadCounts));
        grid.AddRow("[grey]Powtórzenia[/]", $"1 rozgrzewka + {Plural(repetitions, "pomiar", "pomiary", "pomiarów")}");
        foreach (var task in tasks)
            grid.AddRow($"[grey]N {task.Name}[/]", task.N.ToString("N0", Pl));

        AnsiConsole.Write(new Panel(grid).Header("Parametry").RoundedBorder());
    }

    /// <summary>Tabela wyników jednego zadania, pogrupowana według liczby wątków.</summary>
    private static Table BuildTaskTable(BenchTask task, List<Stats> stats, int[] threadCounts, Language[] languages)
    {
        var table = new Table()
            .RoundedBorder()
            .Title($"[bold]{task.Name}[/] – N = {task.N.ToString("N0", Pl)}")
            .AddColumn("Język")
            .AddColumn(new TableColumn("Wątki").RightAligned())
            .AddColumn(new TableColumn("Śr. [[ms]]").RightAligned())
            .AddColumn(new TableColumn("Min [[ms]]").RightAligned())
            .AddColumn(new TableColumn("Odch. std").RightAligned())
            .AddColumn(new TableColumn("Przyspieszenie").RightAligned())
            .AddColumn(new TableColumn("Czas procesu [[ms]]").RightAligned())
            .AddColumn("Wynik");

        for (int g = 0; g < threadCounts.Length; g++)
        {
            int threads = threadCounts[g];
            var group = languages
                .Select(l => stats.First(s => s.Language == l.Name && s.Threads == threads))
                .ToList();
            var valid = group.Where(s => s.Count > 0).ToList();
            // Najszybszy (zielony) i najwolniejszy (czerwony) język w grupie danej liczby wątków.
            Stats? fastest = valid.Count > 1 ? valid.MinBy(s => s.AvgMs) : null;
            Stats? slowest = valid.Count > 1 ? valid.MaxBy(s => s.AvgMs) : null;

            if (g > 0)
                table.AddEmptyRow();
            foreach (var s in group)
            {
                string color = s == fastest ? "green" : s == slowest ? "red" : "default";
                string result = Markup.Escape(s.Result);
                if (s.Mismatch)
                    result = $"[yellow]⚠ {result}[/]";

                table.AddRow(
                    $"[{color}]{Markup.Escape(s.Language)}[/]",
                    threads.ToString(Pl),
                    $"[{color}]{Fmt(s.AvgMs, "N1")}[/]",
                    Fmt(s.MinMs, "N1"),
                    Fmt(s.StdMs, "N1"),
                    double.IsNaN(s.Speedup) ? "—" : $"{s.Speedup.ToString("F2", Pl)}×",
                    Fmt(s.ProcessAvgMs, "N1"),
                    result);
            }
        }

        table.Caption("[green]zielony[/] – najszybszy, [red]czerwony[/] – najwolniejszy dla danej liczby wątków; " +
                      "przyspieszenie = T(1) / T(n)");
        return table;
    }

    private static void PrintSummary(BenchTask[] tasks, List<Stats> stats, Language[] languages, int maxThreads)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold yellow]Podsumowanie[/]"));

        var table = new Table()
            .RoundedBorder()
            .Title("[bold]Zwycięzca dla każdego zadania[/]")
            .AddColumn("Zadanie")
            .AddColumn("Najszybszy (1 wątek)")
            .AddColumn($"Najszybszy ({ThreadsLabel(maxThreads)})")
            .AddColumn("Najlepsze przyspieszenie");

        foreach (var task in tasks)
        {
            var valid = stats.Where(s => s.Task == task.Name && s.Count > 0).ToList();
            table.AddRow(
                $"[bold]{task.Name}[/]",
                Winner(valid.Where(s => s.Threads == 1), s => s.AvgMs, s => $"{Fmt(s.AvgMs, "N1")} ms"),
                Winner(valid.Where(s => s.Threads == maxThreads), s => s.AvgMs, s => $"{Fmt(s.AvgMs, "N1")} ms"),
                Winner(valid.Where(s => s.Threads == maxThreads), s => -s.Speedup, s => $"{s.Speedup.ToString("F2", Pl)}×"));
        }
        AnsiConsole.Write(table);

        // Wykres słupkowy średnich czasów dla maksymalnej liczby wątków (osobno dla każdego zadania,
        // bo czasy zadań różnią się rzędem wielkości).
        foreach (var task in tasks)
        {
            var chart = new BarChart()
                .Width(70)
                .UseValueFormatter(v => v.ToString("N1", Pl))
                .Label($"[bold]{task.Name}[/] – śr. czas [[ms]], {ThreadsLabel(maxThreads)}")
                .LeftAlignLabel();
            foreach (var lang in languages)
            {
                var s = stats.FirstOrDefault(x => x.Task == task.Name && x.Language == lang.Name
                                                   && x.Threads == maxThreads && x.Count > 0);
                if (s is not null)
                    chart.AddItem(lang.Name, Math.Round(s.AvgMs, 1), lang.Color);
            }
            AnsiConsole.WriteLine();
            AnsiConsole.Write(chart);
        }

        if (stats.Any(s => s.Mismatch))
            AnsiConsole.MarkupLine("\n[yellow]⚠ Wykryto niezgodne wyniki kontrolne – sprawdź tabele powyżej.[/]");
    }

    /// <summary>Zwraca opis najlepszego wpisu wg klucza (mniejszy = lepszy).</summary>
    private static string Winner(IEnumerable<Stats> candidates, Func<Stats, double> key, Func<Stats, string> describe)
    {
        var best = candidates.MinBy(key);
        return best is null ? "—" : $"[green]{Markup.Escape(best.Language)}[/] ({describe(best)})";
    }

    // ===================== Pliki CSV =====================

    private static void SaveCsv(string dir, List<Measurement> measurements, List<Stats> stats)
    {
        var raw = new StringBuilder("Zadanie;Język;N;Wątki;Pomiar_nr;Czas_ms;Czas_procesu_ms;Wynik\n");
        foreach (var m in measurements)
            raw.Append(CultureInfo.InvariantCulture, $"{m.Task};{m.Language};{m.N};{m.Threads};{m.RunNo};")
               .Append(m.TimeMs.ToString("F3", Pl)).Append(';')
               .Append(m.ProcessMs.ToString("F3", Pl)).Append(';')
               .Append(m.Result).Append('\n');

        var summary = new StringBuilder("Zadanie;Język;Wątki;Średnia_ms;Min_ms;Odch_std_ms;Przyspieszenie\n");
        foreach (var s in stats.Where(s => s.Count > 0))
            summary.Append(CultureInfo.InvariantCulture, $"{s.Task};{s.Language};{s.Threads};")
                   .Append(s.AvgMs.ToString("F3", Pl)).Append(';')
                   .Append(s.MinMs.ToString("F3", Pl)).Append(';')
                   .Append(s.StdMs.ToString("F3", Pl)).Append(';')
                   .Append(s.Speedup.ToString("F3", Pl)).Append('\n');

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        AnsiConsole.WriteLine();
        WriteCsv(Path.Combine(dir, $"results_{stamp}.csv"), raw.ToString());
        WriteCsv(Path.Combine(dir, $"summary_{stamp}.csv"), summary.ToString());
        WriteCsv(Path.Combine(dir, "results.csv"), raw.ToString());
        WriteCsv(Path.Combine(dir, "summary.csv"), summary.ToString());
    }

    private static void WriteCsv(string path, string content)
    {
        try
        {
            // UTF-8 z BOM – polski Excel poprawnie rozpozna kodowanie i separator ';'.
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            AnsiConsole.MarkupLine($"[grey]Zapisano:[/] {Markup.Escape(path)}");
        }
        catch (IOException ex)
        {
            AnsiConsole.MarkupLine($"[red]Nie udało się zapisać {Markup.Escape(path)}: {Markup.Escape(ex.Message)}[/]");
        }
    }

    // ===================== Pomocnicze =====================

    private static string Fmt(double value, string format) =>
        double.IsNaN(value) ? "—" : value.ToString(format, Pl);

    private static string ThreadsLabel(int n) => Plural(n, "wątek", "wątki", "wątków");

    /// <summary>Polska odmiana liczebnika: 1 wątek, 2–4 wątki, 5+ wątków (12–14 wątków).</summary>
    private static string Plural(int n, string one, string few, string many)
    {
        if (n == 1)
            return $"{n} {one}";
        bool isFew = (n % 10 is >= 2 and <= 4) && (n % 100 is < 12 or > 14);
        return $"{n} {(isFew ? few : many)}";
    }

    private static string CpuName()
    {
        if (!OperatingSystem.IsWindows())
            return "nieznany procesor";
        return Microsoft.Win32.Registry.GetValue(
                   @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                   "ProcessorNameString", null) as string is { } name
            ? name.Trim()
            : "nieznany procesor";
    }
}
