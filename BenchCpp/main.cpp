// BenchCpp – wersja w języku C++17 (wielowątkowość: std::thread).
//
// Użycie:  BenchCpp.exe <zadanie> <N> <liczba_wątków>
//          zadanie = matmul | primes | fibonacci
// Wyjście: C++;<zadanie>;<N>;<wątki>;<wynik_kontrolny>;<czas_ms>
//
// Czas mierzony jest tylko dla części obliczeniowej (std::chrono::steady_clock).
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string>
#include <thread>
#include <vector>

namespace
{
    // Rozmiar porcji liczb przydzielanej wątkowi w zadaniu primes.
    constexpr int PrimesChunk = 10000;
    // Głębokość rozwinięcia drzewa rekurencji Fibonacciego (2^5 = 32 podzadania).
    constexpr int FibDepth = 5;

    using Clock = std::chrono::steady_clock;

    double elapsedMs(Clock::time_point start)
    {
        return std::chrono::duration<double, std::milli>(Clock::now() - start).count();
    }

    // Uruchamia `threads` wątków wykonujących work(nrWątku) i czeka na ich zakończenie.
    template <typename Work>
    void runThreads(int threads, Work work)
    {
        std::vector<std::thread> pool;
        pool.reserve(threads);
        for (int t = 0; t < threads; t++)
            pool.emplace_back(work, t);
        for (auto &th : pool)
            th.join();
    }

    // ---------- 1. Mnożenie macierzy N×N (pętla i-k-j, podział wierszami) ----------
    void benchMatmul(int n, int threads)
    {
        const size_t size = static_cast<size_t>(n) * n;
        std::vector<double> a(size), b(size), c(size, 0.0);

        // Deterministyczne wypełnienie macierzy wejściowych.
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                a[static_cast<size_t>(i) * n + j] = (i + j) % 10;
                b[static_cast<size_t>(i) * n + j] = (i * j) % 10;
            }

        auto start = Clock::now();
        // Każdy wątek liczy ciągły blok wierszy [od, do) macierzy wynikowej.
        runThreads(threads, [&](int t) {
            int from = static_cast<int>(static_cast<long long>(n) * t / threads);
            int to = static_cast<int>(static_cast<long long>(n) * (t + 1) / threads);
            for (int i = from; i < to; i++)
                for (int k = 0; k < n; k++)
                {
                    double aik = a[static_cast<size_t>(i) * n + k];
                    for (int j = 0; j < n; j++)
                        c[static_cast<size_t>(i) * n + j] += aik * b[static_cast<size_t>(k) * n + j];
                }
        });
        double ms = elapsedMs(start);

        // Wynik kontrolny: suma wszystkich elementów C (poza pomiarem czasu).
        double sum = 0.0;
        for (double v : c)
            sum += v;

        std::printf("C++;matmul;%d;%d;%.0f;%.3f\n", n, threads, sum, ms);
    }

    // ---------- 2. Liczby pierwsze w [2, N] – dzielenie próbne do sqrt(n) ----------
    bool isPrime(long long n)
    {
        if (n < 2)
            return false;
        if (n % 2 == 0)
            return n == 2;
        for (long long d = 3; d * d <= n; d += 2)
            if (n % d == 0)
                return false;
        return true;
    }

    void benchPrimes(int n, int threads)
    {
        // Zakres [2, N] dzielony na porcje; wątki pobierają je ze wspólnego licznika.
        const int chunks = (n - 2) / PrimesChunk + 1;
        std::atomic<int> nextChunk{0};
        std::vector<long long> counts(threads, 0);

        auto start = Clock::now();
        runThreads(threads, [&](int t) {
            long long local = 0;
            int ch;
            while ((ch = nextChunk.fetch_add(1)) < chunks)
            {
                long long from = 2 + static_cast<long long>(ch) * PrimesChunk;
                long long to = from + PrimesChunk - 1;
                if (to > n)
                    to = n;
                for (long long x = from; x <= to; x++)
                    if (isPrime(x))
                        local++;
            }
            counts[t] = local;
        });
        long long count = 0;
        for (long long v : counts)
            count += v;
        double ms = elapsedMs(start);

        std::printf("C++;primes;%d;%d;%lld;%.3f\n", n, threads, count, ms);
    }

    // ---------- 3. Fibonacci – naiwna rekurencja ----------
    uint64_t fib(int n)
    {
        if (n < 2)
            return static_cast<uint64_t>(n);
        return fib(n - 1) + fib(n - 2);
    }

    // Rozwija drzewo rekurencji do zadanej głębokości – liście to podzadania fib(N-k).
    void makeFibTasks(int n, int depth, std::vector<int> &tasks)
    {
        if (depth == 0 || n < 2)
        {
            tasks.push_back(n);
            return;
        }
        makeFibTasks(n - 1, depth - 1, tasks);
        makeFibTasks(n - 2, depth - 1, tasks);
    }

    void benchFibonacci(int n, int threads)
    {
        std::vector<int> tasks;
        makeFibTasks(n, FibDepth, tasks);
        std::vector<uint64_t> results(tasks.size(), 0);
        std::atomic<int> nextTask{0};
        const int count = static_cast<int>(tasks.size());

        auto start = Clock::now();
        // Podzadania mają różny koszt, więc wątki pobierają je po jednym ze wspólnego licznika.
        runThreads(threads, [&](int) {
            int i;
            while ((i = nextTask.fetch_add(1)) < count)
                results[i] = fib(tasks[i]);
        });
        uint64_t sum = 0;
        for (uint64_t v : results)
            sum += v;
        double ms = elapsedMs(start);

        std::printf("C++;fibonacci;%d;%d;%llu;%.3f\n", n, threads,
                    static_cast<unsigned long long>(sum), ms);
    }
}

int main(int argc, char *argv[])
{
    if (argc != 4)
    {
        std::fprintf(stderr, "Uzycie: BenchCpp.exe <matmul|primes|fibonacci> <N> <liczba_watkow>\n");
        return 1;
    }
    const std::string task = argv[1];
    const int n = std::atoi(argv[2]);
    const int threads = std::atoi(argv[3]);
    if (n < 1 || threads < 1)
    {
        std::fprintf(stderr, "N i liczba watkow musza byc dodatnie\n");
        return 1;
    }

    if (task == "matmul")
        benchMatmul(n, threads);
    else if (task == "primes")
        benchPrimes(n, threads);
    else if (task == "fibonacci")
        benchFibonacci(n, threads);
    else
    {
        std::fprintf(stderr, "Nieznane zadanie: %s\n", task.c_str());
        return 1;
    }
    return 0;
}
