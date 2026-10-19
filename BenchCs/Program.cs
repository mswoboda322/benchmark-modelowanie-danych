// BenchCs – wersja w języku C# (wielowątkowość: Parallel.For z TPL).
//
// Użycie:  BenchCs.exe <zadanie> <N> <liczba_wątków>
//          zadanie = matmul | primes | fibonacci
// Wyjście: C#;<zadanie>;<N>;<wątki>;<wynik_kontrolny>;<czas_ms>
//
// Czas mierzony jest tylko dla części obliczeniowej (Stopwatch).
using System.Diagnostics;
using System.Globalization;

namespace BenchCs;

internal static class Program
{
    // Rozmiar porcji liczb przydzielanej wątkowi w zadaniu primes.
    private const int PrimesChunk = 10000;
    // Głębokość rozwinięcia drzewa rekurencji Fibonacciego (2^5 = 32 podzadania).
    private const int FibDepth = 5;

    private static int Main(string[] args)
    {
        if (args.Length != 3
            || !int.TryParse(args[1], out int n)
            || !int.TryParse(args[2], out int threads))
        {
            Console.Error.WriteLine("Uzycie: BenchCs.exe <matmul|primes|fibonacci> <N> <liczba_watkow>");
            return 1;
        }
        if (n < 1 || threads < 1)
        {
            Console.Error.WriteLine("N i liczba watkow musza byc dodatnie");
            return 1;
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = threads };
        (string result, double ms) = args[0] switch
        {
            "matmul" => BenchMatmul(n, options),
            "primes" => BenchPrimes(n, options),
            "fibonacci" => BenchFibonacci(n, options),
            _ => (string.Empty, -1.0),
        };
        if (ms < 0)
        {
            Console.Error.WriteLine($"Nieznane zadanie: {args[0]}");
            return 1;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"C#;{args[0]};{n};{threads};{result};{ms:F3}"));
        return 0;
    }

    // ---------- 1. Mnożenie macierzy N×N (pętla i-k-j, podział wierszami) ----------
    private static (string, double) BenchMatmul(int n, ParallelOptions options)
    {
        var a = new double[n * n];
        var b = new double[n * n];
        var c = new double[n * n];

        // Deterministyczne wypełnienie macierzy wejściowych.
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                a[i * n + j] = (i + j) % 10;
                b[i * n + j] = (i * j) % 10;
            }

        var sw = Stopwatch.StartNew();
        // Wątki dzielą się wierszami macierzy wynikowej.
        Parallel.For(0, n, options, i =>
        {
            for (int k = 0; k < n; k++)
            {
                double aik = a[i * n + k];
                for (int j = 0; j < n; j++)
                    c[i * n + j] += aik * b[k * n + j];
            }
        });
        double ms = sw.Elapsed.TotalMilliseconds;

        // Wynik kontrolny: suma wszystkich elementów C (poza pomiarem czasu).
        double sum = 0.0;
        foreach (double v in c)
            sum += v;

        return (sum.ToString("F0", CultureInfo.InvariantCulture), ms);
    }

    // ---------- 2. Liczby pierwsze w [2, N] – dzielenie próbne do sqrt(n) ----------
    private static bool IsPrime(long n)
    {
        if (n < 2)
            return false;
        if (n % 2 == 0)
            return n == 2;
        for (long d = 3; d * d <= n; d += 2)
            if (n % d == 0)
                return false;
        return true;
    }

    private static (string, double) BenchPrimes(int n, ParallelOptions options)
    {
        // Zakres [2, N] dzielony na porcje; Parallel.For rozdziela je dynamicznie.
        int chunks = (n - 2) / PrimesChunk + 1;
        long count = 0;

        var sw = Stopwatch.StartNew();
        Parallel.For(0, chunks, options,
            () => 0L,                       // licznik lokalny wątku
            (ch, _, local) =>
            {
                long from = 2 + (long)ch * PrimesChunk;
                long to = Math.Min(from + PrimesChunk - 1, n);
                for (long x = from; x <= to; x++)
                    if (IsPrime(x))
                        local++;
                return local;
            },
            local => Interlocked.Add(ref count, local));
        double ms = sw.Elapsed.TotalMilliseconds;

        return (count.ToString(CultureInfo.InvariantCulture), ms);
    }

    // ---------- 3. Fibonacci – naiwna rekurencja ----------
    private static ulong Fib(int n)
    {
        if (n < 2)
            return (ulong)n;
        return Fib(n - 1) + Fib(n - 2);
    }

    // Rozwija drzewo rekurencji do zadanej głębokości – liście to podzadania fib(N-k).
    private static void MakeFibTasks(int n, int depth, List<int> tasks)
    {
        if (depth == 0 || n < 2)
        {
            tasks.Add(n);
            return;
        }
        MakeFibTasks(n - 1, depth - 1, tasks);
        MakeFibTasks(n - 2, depth - 1, tasks);
    }

    private static (string, double) BenchFibonacci(int n, ParallelOptions options)
    {
        var tasks = new List<int>();
        MakeFibTasks(n, FibDepth, tasks);
        var results = new ulong[tasks.Count];

        var sw = Stopwatch.StartNew();
        // Każde podzadanie to osobna iteracja Parallel.For.
        Parallel.For(0, tasks.Count, options, i => results[i] = Fib(tasks[i]));
        ulong sum = 0;
        foreach (ulong v in results)
            sum += v;
        double ms = sw.Elapsed.TotalMilliseconds;

        return (sum.ToString(CultureInfo.InvariantCulture), ms);
    }
}
