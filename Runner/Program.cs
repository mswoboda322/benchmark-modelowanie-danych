// Runner – uruchamia programy badania (K1 pamięć, K2 wielowątkowość, K3 aplikacja okienkowa),
// mierzy procesy (pamięć szczytowa, czas CPU), liczy statystyki i zapisuje wyniki do CSV.
//
// Użycie: Runner.exe [K1] [K2] [K3] [--quick] [--cold] [--seed n]
//   bez K1/K2/K3  – wszystkie kryteria
//   --quick       – krótki przebieg próbny (mniejsze rozmiary, mniej powtórzeń)
//   --cold        – dodatkowo zimny start aplikacji GUI (wymaga uprawnień administratora i RAMMap64.exe)
//   --seed n      – ziarno losowania kolejności języków (domyślnie 2026)
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;

namespace Runner;

/// <summary>Ustawienia przebiegu.</summary>
internal sealed record Settings(bool K1, bool K2, bool K3, bool Quick, bool Cold, int Seed)
{
    /// <summary>Liczba pomiarów (iteracji w stanie ustalonym) w K1/K2.</summary>
    public int Iter => Quick ? 3 : 10;

    /// <summary>Liczba iteracji rozgrzewkowych w K1/K2 (odrzucane).</summary>
    public int Warmup => Quick ? 1 : 3;
}

/// <summary>Sposób uruchomienia programu: plik wykonywalny + stałe argumenty (np. java -jar X.jar).</summary>
internal sealed record LaunchSpec(string Exe, string[] PrefixArgs);

/// <summary>Badany język: program konsolowy (K1, K2) i okienkowy (K3).</summary>
internal sealed record Language(string Name, Color Color, LaunchSpec? Console, LaunchSpec? Gui, string[] GuiFiles);

internal static class Program
{
    public static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // Przy przekierowaniu do pliku (Runner.exe > log.txt) Spectre przyjmuje 80 kolumn – za mało na tabele.
        if (Console.IsOutputRedirected)
            AnsiConsole.Profile.Width = 160;

        if (!TryParseArgs(args, out var settings))
        {
            AnsiConsole.MarkupLine("Użycie: Runner.exe [[K1]] [[K2]] [[K3]] [[--quick]] [[--cold]] [[--seed n]]");
            return 1;
        }

        string bin = AppContext.BaseDirectory;
        string? java = FindJava();
        var languages = DiscoverLanguages(bin, java);
        if (languages.All(l => l.Console is null && l.Gui is null))
        {
            AnsiConsole.MarkupLine("[red]Brak programów do uruchomienia. Zbuduj rozwiązanie (build.ps1).[/]");
            return 1;
        }

        PrintHeader(settings, languages, java);

        string outDir = Path.Combine(bin, "wyniki_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(outDir);
        var rng = new Random(settings.Seed);

        if (settings.K1)
            ConsoleBench.Run("K1", Plan.K1(settings.Quick), settings, languages, rng, outDir);
        if (settings.K2)
            ConsoleBench.Run("K2", Plan.K2(settings.Quick), settings, languages, rng, outDir);
        if (settings.K3)
            GuiBench.Run(settings, languages, rng, outDir, bin);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Wyniki zapisano w:[/] {Markup.Escape(outDir)}");
        return 0;
    }

    private static bool TryParseArgs(string[] args, out Settings settings)
    {
        bool k1 = false, k2 = false, k3 = false, quick = false, cold = false;
        int seed = 2026;
        settings = null!;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToUpperInvariant())
            {
                case "K1": k1 = true; break;
                case "K2": k2 = true; break;
                case "K3": k3 = true; break;
                case "--QUICK": quick = true; break;
                case "--COLD": cold = true; break;
                case "--SEED" when i + 1 < args.Length && int.TryParse(args[i + 1], out seed): i++; break;
                default: return false;
            }
        }
        if (!k1 && !k2 && !k3)
            k1 = k2 = k3 = true;
        settings = new Settings(k1, k2, k3, quick, cold, seed);
        return true;
    }

    // ===================== Języki =====================

    private static Language[] DiscoverLanguages(string bin, string? java)
    {
        LaunchSpec? Exe(string file) =>
            File.Exists(Path.Combine(bin, file)) ? new LaunchSpec(Path.Combine(bin, file), []) : null;
        LaunchSpec? Jar(string file) =>
            java is not null && File.Exists(Path.Combine(bin, file))
                ? new LaunchSpec(java, ["-jar", Path.Combine(bin, file)])
                : null;

        Language[] all =
        [
            new("C++", Color.Orange1, Exe("BenchCpp.exe"), Exe("GuiCpp.exe"), ["GuiCpp.exe"]),
            new("C#", Color.MediumPurple, Exe("BenchCs.exe"), Exe("GuiCs.exe"),
                ["GuiCs.exe", "GuiCs.dll", "GuiCs.runtimeconfig.json", "GuiCs.deps.json"]),
            new("Java", Color.DeepSkyBlue1, Jar("BenchJava.jar"), Jar("GuiJava.jar"), ["GuiJava.jar"]),
        ];
        foreach (var l in all)
        {
            if (l.Console is null)
                AnsiConsole.MarkupLine($"[yellow]⚠ Brak programu konsolowego dla {l.Name} – pominięty w K1/K2.[/]");
            if (l.Gui is null)
                AnsiConsole.MarkupLine($"[yellow]⚠ Brak programu okienkowego dla {l.Name} – pominięty w K3.[/]");
        }
        if (java is null)
            AnsiConsole.MarkupLine("[yellow]⚠ Nie znaleziono JDK (JAVA_HOME / Program Files\\Java).[/]");
        return all;
    }

    /// <summary>
    /// java.exe z JDK – nie z folderu „javapath” (Oracle), bo tamtejszy java.exe to pośrednik uruchamiający
    /// osobny proces, a Runner mierzy pamięć i czas CPU procesu, który sam uruchomił.
    /// </summary>
    private static string? FindJava()
    {
        string? home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, "bin", "java.exe")))
            return Path.Combine(home, "bin", "java.exe");

        string javaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java");
        if (Directory.Exists(javaDir))
        {
            string? newest = Directory.GetDirectories(javaDir, "jdk*")
                .Select(d => Path.Combine(d, "bin", "java.exe"))
                .Where(File.Exists)
                .OrderBy(p => JdkMajor(p))
                .LastOrDefault();
            if (newest is not null)
                return newest;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.Contains("javapath", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.Combine(p, "java.exe"))
            .FirstOrDefault(File.Exists);

        static int JdkMajor(string javaExe)
        {
            string name = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(javaExe))!);
            string digits = new(name.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out int v) ? v : 0;
        }
    }

    // ===================== Nagłówek =====================

    private static void PrintHeader(Settings s, Language[] languages, string? java)
    {
        AnsiConsole.Write(new Rule("[bold yellow]Badanie: C++ vs C# vs Java – pamięć, wielowątkowość, GUI[/]"));

        var grid = new Grid().AddColumn(new GridColumn().PadRight(3)).AddColumn();
        grid.AddRow("[grey]Procesor[/]", Markup.Escape($"{CpuName()} – {Environment.ProcessorCount} wątków logicznych"));
        grid.AddRow("[grey]Pamięć RAM[/]",
            $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):F1} GB".Replace('.', ','));
        grid.AddRow("[grey]System[/]", Markup.Escape(RuntimeInformation.OSDescription));
        grid.AddRow("[grey].NET[/]", Markup.Escape(RuntimeInformation.FrameworkDescription));
        grid.AddRow("[grey]Java[/]", Markup.Escape(java is null ? "brak" : $"{JavaVersion(java)} ({java})"));
        grid.AddRow("[grey]Data[/]", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Pl));
        grid.AddRow("[grey]Języki[/]", Markup.Escape(string.Join(", ", languages
            .Where(l => l.Console is not null || l.Gui is not null).Select(l => l.Name))));
        grid.AddRow("[grey]Kryteria[/]", string.Join(", ", new[] { (s.K1, "K1"), (s.K2, "K2"), (s.K3, "K3") }
            .Where(x => x.Item1).Select(x => x.Item2)));
        grid.AddRow("[grey]Profil[/]", s.Quick
            ? "[yellow]szybki (--quick) – tylko do sprawdzenia działania[/]"
            : $"pełny: {s.Warmup} iteracje rozgrzewkowe + {s.Iter} pomiarów");
        grid.AddRow("[grey]Ziarno kolejności[/]", s.Seed.ToString(CultureInfo.InvariantCulture));
        AnsiConsole.Write(new Panel(grid).Header("Parametry").RoundedBorder());
    }

    private static string JavaVersion(string java)
    {
        try
        {
            var r = ProcessRunner.Run(new LaunchSpec(java, []), ["-version"], TimeSpan.FromSeconds(30));
            return r.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "?";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "?";
        }
    }

    private static string CpuName() =>
        OperatingSystem.IsWindows()
        && Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString", null) is string name
            ? name.Trim()
            : "nieznany procesor";
}
