// BenchCpp – zadania K1 (pamięć) i K2 (wielowątkowość) w C++17.
//
// Użycie:  BenchCpp.exe <zadanie> <wariant> <rozmiar> <wątki> [--iter 10] [--warmup 3] [--flush on|off]
//   mem-access  seq | rand                          rozmiar = KB tablicy int32
//   matmul      ijk | ikj                           rozmiar = N
//   objects     points-value | points-ref | trees   rozmiar = liczba punktów / głębokość drzewa
//   mandelbrot  static | dynamic                    rozmiar = bok obrazu
//   sync        mutex | atomic | local-shared | local-padded   rozmiar = liczba inkrementacji
//   queue       blocking                            rozmiar = liczba komunikatów (wątki >= 2)
//
// Wyjście – jedna linia na iterację (opis kolumn: docs/02-plan-badania.md, pkt 2):
//   język;zadanie;wariant;rozmiar;wątki;iteracja;rozgrzewka;czas_ns;wynik;cpu_ms;gc_liczba;gc_pauza_ms
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace
{
    // ===================== Wspólne elementy (identyczne w C++, C# i Javie) =====================

    constexpr size_t FlushBytes = 128ull * 1024 * 1024; // > 5 × L3 (24 MB)
    constexpr int CacheLine = 64;
    constexpr uint64_t Seed = 42;
    constexpr long long MemAccesses = 1LL << 24; // liczba dostępów w mem-access
    constexpr int MandelMaxIter = 1000;
    constexpr int QueueCapacity = 1024;

    struct Options
    {
        std::string task, variant;
        long long size = 0;
        int threads = 1;
        int iter = 10;
        int warmup = 3;
        bool flush = true;
    };

    // Generator SplitMix64 – ta sama sekwencja liczb w każdym języku.
    struct SplitMix64
    {
        uint64_t state;
        explicit SplitMix64(uint64_t seed) : state(seed) {}
        uint64_t next()
        {
            uint64_t z = (state += 0x9E3779B97F4A7C15ULL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
            return z ^ (z >> 31);
        }
        // Liczba z przedziału [0, bound).
        int nextInt(int bound) { return static_cast<int>((next() >> 33) % static_cast<uint64_t>(bound)); }
    };

    // Zegar monotoniczny: QueryPerformanceCounter przeliczony na nanosekundy.
    long long nowNs()
    {
        static const long long freq = [] {
            LARGE_INTEGER f;
            QueryPerformanceFrequency(&f);
            return f.QuadPart;
        }();
        LARGE_INTEGER c;
        QueryPerformanceCounter(&c);
        return c.QuadPart / freq * 1'000'000'000LL + c.QuadPart % freq * 1'000'000'000LL / freq;
    }

    // Czas procesora całego procesu (user + kernel) w ms.
    double cpuMs()
    {
        FILETIME creation, exitTime, kernel, user;
        GetProcessTimes(GetCurrentProcess(), &creation, &exitTime, &kernel, &user);
        auto toInt = [](FILETIME ft) { return (static_cast<uint64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime; };
        return static_cast<double>(toInt(kernel) + toInt(user)) / 10'000.0; // jednostki 100 ns
    }

    // Czyszczenie cache CPU: zapis i odczyt co 64 B bufora większego niż L3.
    std::vector<uint8_t> g_flushBuffer;
    volatile uint64_t g_sink = 0;

    void flushCache()
    {
        uint64_t sum = 0;
        for (size_t i = 0; i < g_flushBuffer.size(); i += CacheLine)
        {
            g_flushBuffer[i]++;
            sum += g_flushBuffer[i];
        }
        g_sink = g_sink + sum;
    }

    // Uruchamia `threads` wątków work(nrWątku) i czeka na ich zakończenie. Bariera startowa sprawia,
    // że wątki zaczynają pracę jednocześnie (inaczej pierwsze mogłyby skończyć, zanim ruszą ostatnie).
    template <typename Work>
    void runThreads(int threads, Work work)
    {
        std::atomic<int> waiting{threads};
        std::vector<std::thread> pool;
        pool.reserve(threads);
        for (int t = 0; t < threads; t++)
            pool.emplace_back([&waiting, &work, t] {
                waiting.fetch_sub(1);
                while (waiting.load() > 0)
                    std::this_thread::yield();
                work(t);
            });
        for (auto &th : pool)
            th.join();
    }

    // Zadanie = przygotowanie iteracji (poza pomiarem) + część mierzona + wynik kontrolny (poza pomiarem).
    struct Bench
    {
        std::function<void()> prepare;
        std::function<void()> run;
        std::function<std::string()> result;
    };

    // ===================== K1 – pamięć =====================

    // Z1.1: tablica int32 z permutacją jednocyklową (Sattolo); seq – sumowanie po kolei,
    // rand – pointer chasing (a[i] = indeks następnego elementu).
    Bench memAccess(const Options &o)
    {
        const size_t n = static_cast<size_t>(o.size) * 1024 / sizeof(int32_t);
        auto a = std::make_shared<std::vector<int32_t>>(n);
        for (size_t i = 0; i < n; i++)
            (*a)[i] = static_cast<int32_t>(i);
        SplitMix64 rng(Seed);
        for (size_t i = n - 1; i > 0; i--)
            std::swap((*a)[i], (*a)[rng.nextInt(static_cast<int>(i))]);

        auto sum = std::make_shared<long long>(0);
        Bench b;
        if (o.variant == "seq")
            b.run = [a, sum, n] {
                const int32_t *p = a->data();
                long long s = 0, left = MemAccesses;
                while (left > 0)
                {
                    const size_t m = static_cast<size_t>(std::min<long long>(left, static_cast<long long>(n)));
                    for (size_t i = 0; i < m; i++)
                        s += p[i];
                    left -= static_cast<long long>(m);
                }
                *sum = s;
            };
        else
            b.run = [a, sum] {
                const int32_t *p = a->data();
                long long s = 0;
                int32_t idx = 0;
                for (long long k = 0; k < MemAccesses; k++)
                {
                    idx = p[idx];
                    s += idx;
                }
                *sum = s;
            };
        b.result = [sum] { return std::to_string(*sum); };
        return b;
    }

    // Z1.2: mnożenie macierzy N×N w płaskich tablicach; ijk (kolumny B) vs ikj (wiersze B).
    Bench matmul(const Options &o)
    {
        const int n = static_cast<int>(o.size);
        const size_t size = static_cast<size_t>(n) * n;
        auto a = std::make_shared<std::vector<double>>(size);
        auto b = std::make_shared<std::vector<double>>(size);
        auto c = std::make_shared<std::vector<double>>(size);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                (*a)[static_cast<size_t>(i) * n + j] = (i + j) % 10;
                (*b)[static_cast<size_t>(i) * n + j] = (static_cast<long long>(i) * j) % 10;
            }

        Bench bench;
        bench.prepare = [c] { std::fill(c->begin(), c->end(), 0.0); };
        if (o.variant == "ijk")
            bench.run = [a, b, c, n] {
                const double *A = a->data(), *B = b->data();
                double *C = c->data();
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        double s = 0.0;
                        for (int k = 0; k < n; k++)
                            s += A[static_cast<size_t>(i) * n + k] * B[static_cast<size_t>(k) * n + j];
                        C[static_cast<size_t>(i) * n + j] = s;
                    }
            };
        else
            bench.run = [a, b, c, n] {
                const double *A = a->data(), *B = b->data();
                double *C = c->data();
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < n; k++)
                    {
                        const double aik = A[static_cast<size_t>(i) * n + k];
                        for (int j = 0; j < n; j++)
                            C[static_cast<size_t>(i) * n + j] += aik * B[static_cast<size_t>(k) * n + j];
                    }
            };
        bench.result = [c] {
            double s = 0.0;
            for (double v : *c)
                s += v;
            return std::to_string(static_cast<long long>(s));
        };
        return bench;
    }

    struct Point
    {
        double x, y, z;
    };

    struct Node
    {
        std::unique_ptr<Node> left, right;
    };

    std::unique_ptr<Node> bottomUp(int depth)
    {
        auto node = std::make_unique<Node>();
        if (depth > 0)
        {
            node->left = bottomUp(depth - 1);
            node->right = bottomUp(depth - 1);
        }
        return node;
    }

    long long check(const Node *node)
    {
        return 1 + (node->left ? check(node->left.get()) + check(node->right.get()) : 0);
    }

    // Z1.3: (a) punkty jako wartości vs wskaźniki do obiektów, (b) drzewa binarne (binary-trees z CLBG).
    Bench objects(const Options &o)
    {
        Bench b;
        auto sum = std::make_shared<long long>(0);
        b.result = [sum] { return std::to_string(*sum); };

        if (o.variant == "trees")
        {
            const int maxDepth = std::max(6, static_cast<int>(o.size));
            b.run = [sum, maxDepth] {
                constexpr int minDepth = 4;
                long long total = check(bottomUp(maxDepth + 1).get()); // drzewo „rozciągające”
                auto longLived = bottomUp(maxDepth);
                for (int depth = minDepth; depth <= maxDepth; depth += 2)
                {
                    const int iterations = 1 << (maxDepth - depth + minDepth);
                    for (int i = 0; i < iterations; i++)
                        total += check(bottomUp(depth).get()); // drzewo zwalniane od razu (unique_ptr)
                }
                total += check(longLived.get());
                *sum = total;
            };
            return b;
        }

        const size_t n = static_cast<size_t>(o.size);
        auto makePoint = [](size_t i) {
            const long long k = static_cast<long long>(i);
            return Point{static_cast<double>(k % 1000), static_cast<double>(k * 7 % 1000),
                         static_cast<double>(k * 13 % 1000)};
        };
        if (o.variant == "points-value")
        {
            auto pts = std::make_shared<std::vector<Point>>(n);
            for (size_t i = 0; i < n; i++)
                (*pts)[i] = makePoint(i);
            b.run = [pts, sum] {
                double s = 0.0;
                for (const Point &p : *pts)
                    s += p.x + p.y + p.z;
                *sum = static_cast<long long>(s);
            };
        }
        else
        {
            auto pts = std::make_shared<std::vector<std::unique_ptr<Point>>>(n);
            for (size_t i = 0; i < n; i++)
                (*pts)[i] = std::make_unique<Point>(makePoint(i));
            b.run = [pts, sum] {
                double s = 0.0;
                for (const auto &p : *pts)
                    s += p->x + p->y + p->z;
                *sum = static_cast<long long>(s);
            };
        }
        return b;
    }

    // ===================== K2 – wielowątkowość =====================

    // Jeden wiersz zbioru Mandelbrota (ta sama kolejność działań zmiennoprzecinkowych we wszystkich językach).
    void mandelRow(int *img, int width, int height, int y)
    {
        const double cy = -1.5 + 3.0 * y / height;
        for (int x = 0; x < width; x++)
        {
            const double cx = -2.0 + 3.0 * x / width;
            double zx = 0.0, zy = 0.0;
            int it = 0;
            while (it < MandelMaxIter)
            {
                const double zx2 = zx * zx, zy2 = zy * zy;
                if (zx2 + zy2 > 4.0)
                    break;
                zy = 2.0 * zx * zy + cy;
                zx = zx2 - zy2 + cx;
                it++;
            }
            img[static_cast<size_t>(y) * width + x] = it;
        }
    }

    // Z2.1: static – równe bloki wierszy, dynamic – wiersze pobierane ze wspólnego licznika.
    Bench mandelbrot(const Options &o)
    {
        const int size = static_cast<int>(o.size);
        const int threads = o.threads;
        auto img = std::make_shared<std::vector<int>>(static_cast<size_t>(size) * size);
        Bench b;
        b.prepare = [img] { std::fill(img->begin(), img->end(), 0); };
        if (o.variant == "static")
            b.run = [img, size, threads] {
                runThreads(threads, [&](int t) {
                    const int from = static_cast<int>(static_cast<long long>(size) * t / threads);
                    const int to = static_cast<int>(static_cast<long long>(size) * (t + 1) / threads);
                    for (int y = from; y < to; y++)
                        mandelRow(img->data(), size, size, y);
                });
            };
        else
            b.run = [img, size, threads] {
                std::atomic<int> nextRow{0};
                runThreads(threads, [&](int) {
                    int y;
                    while ((y = nextRow.fetch_add(1)) < size)
                        mandelRow(img->data(), size, size, y);
                });
            };
        b.result = [img] {
            long long s = 0;
            for (int v : *img)
                s += v;
            return std::to_string(s);
        };
        return b;
    }

    // Liczba operacji wątku t przy podziale `total` na `threads` części.
    long long share(long long total, int threads, int t)
    {
        return total / threads + (t < total % threads ? 1 : 0);
    }

    struct alignas(CacheLine) PaddedCounter
    {
        std::atomic<long long> value{0};
    };

    // Z2.2: koszt synchronizacji przy inkrementacji licznika.
    Bench sync(const Options &o)
    {
        const long long total = o.size;
        const int threads = o.threads;
        const std::string variant = o.variant;
        auto sum = std::make_shared<long long>(0);
        Bench b;
        b.run = [=] {
            if (variant == "mutex")
            {
                std::mutex m;
                long long counter = 0;
                runThreads(threads, [&](int t) {
                    for (long long k = share(total, threads, t); k > 0; k--)
                    {
                        std::lock_guard<std::mutex> lock(m);
                        counter++;
                    }
                });
                *sum = counter;
            }
            else if (variant == "atomic")
            {
                std::atomic<long long> counter{0};
                runThreads(threads, [&](int t) {
                    for (long long k = share(total, threads, t); k > 0; k--)
                        counter.fetch_add(1);
                });
                *sum = counter.load();
            }
            else if (variant == "local-shared")
            {
                // Każdy wątek ma własny licznik, ale liczniki leżą w sąsiednich komórkach – kilka wątków
                // dzieli jedną linię cache (false sharing). Ta sama instrukcja co w „atomic” (lock add),
                // więc różnica wynika wyłącznie z układu danych w pamięci.
                std::unique_ptr<std::atomic<long long>[]> counters(new std::atomic<long long>[threads]);
                for (int t = 0; t < threads; t++)
                    counters[t].store(0);
                runThreads(threads, [&](int t) {
                    auto &c = counters[t];
                    for (long long k = share(total, threads, t); k > 0; k--)
                        c.fetch_add(1);
                });
                long long s = 0;
                for (int t = 0; t < threads; t++)
                    s += counters[t].load();
                *sum = s;
            }
            else // local-padded – każdy licznik w osobnej linii cache (64 B)
            {
                std::vector<PaddedCounter> counters(threads);
                runThreads(threads, [&](int t) {
                    auto &c = counters[t].value;
                    for (long long k = share(total, threads, t); k > 0; k--)
                        c.fetch_add(1);
                });
                long long s = 0;
                for (auto &c : counters)
                    s += c.value.load();
                *sum = s;
            }
        };
        b.result = [sum] { return std::to_string(*sum); };
        return b;
    }

    // Ograniczona kolejka blokująca – biblioteka standardowa C++ jej nie zawiera.
    class BlockingQueue
    {
    public:
        explicit BlockingQueue(size_t capacity) : capacity_(capacity) {}

        void put(int value)
        {
            std::unique_lock<std::mutex> lock(mutex_);
            notFull_.wait(lock, [&] { return queue_.size() < capacity_; });
            queue_.push_back(value);
            lock.unlock();
            notEmpty_.notify_one();
        }

        int take()
        {
            std::unique_lock<std::mutex> lock(mutex_);
            notEmpty_.wait(lock, [&] { return !queue_.empty(); });
            const int value = queue_.front();
            queue_.pop_front();
            lock.unlock();
            notFull_.notify_one();
            return value;
        }

    private:
        size_t capacity_;
        std::mutex mutex_;
        std::condition_variable notFull_, notEmpty_;
        std::deque<int> queue_;
    };

    // Z2.3: 1 producent, (wątki − 1) konsumentów; 0 = sygnał końca dla konsumenta.
    Bench queue(const Options &o)
    {
        const int messages = static_cast<int>(o.size);
        const int consumers = o.threads - 1;
        auto sum = std::make_shared<long long>(0);
        Bench b;
        b.run = [=] {
            BlockingQueue q(QueueCapacity);
            std::vector<long long> partial(consumers, 0);
            std::vector<std::thread> pool;
            for (int c = 0; c < consumers; c++)
                pool.emplace_back([&, c] {
                    long long s = 0;
                    for (int v; (v = q.take()) != 0;)
                        s += v;
                    partial[c] = s;
                });
            for (int i = 1; i <= messages; i++)
                q.put(i);
            for (int c = 0; c < consumers; c++)
                q.put(0);
            for (auto &t : pool)
                t.join();
            long long s = 0;
            for (long long v : partial)
                s += v;
            *sum = s;
        };
        b.result = [sum] { return std::to_string(*sum); };
        return b;
    }

    // ===================== Pętla pomiarowa =====================

    void runIterations(const Options &o, const Bench &b)
    {
        if (o.flush)
            g_flushBuffer.assign(FlushBytes, 1);
        for (int it = 1; it <= o.warmup + o.iter; it++)
        {
            if (b.prepare)
                b.prepare();
            if (o.flush)
                flushCache();
            const double cpu0 = cpuMs();
            const long long t0 = nowNs();
            b.run();
            const long long t1 = nowNs();
            const double cpu1 = cpuMs();
            const std::string result = b.result();
            std::printf("C++;%s;%s;%lld;%d;%d;%d;%lld;%s;%.3f;0;0\n", o.task.c_str(), o.variant.c_str(), o.size,
                        o.threads, it, it <= o.warmup ? 1 : 0, t1 - t0, result.c_str(), cpu1 - cpu0);
            std::fflush(stdout);
        }
    }

    int usage()
    {
        std::fprintf(stderr, "Uzycie: BenchCpp.exe <zadanie> <wariant> <rozmiar> <watki> "
                             "[--iter 10] [--warmup 3] [--flush on|off]\n");
        return 1;
    }
}

int main(int argc, char *argv[])
{
    if (argc < 5)
        return usage();
    Options o;
    o.task = argv[1];
    o.variant = argv[2];
    o.size = std::atoll(argv[3]);
    o.threads = std::atoi(argv[4]);
    for (int i = 5; i + 1 < argc; i += 2)
    {
        const std::string key = argv[i], value = argv[i + 1];
        if (key == "--iter")
            o.iter = std::atoi(value.c_str());
        else if (key == "--warmup")
            o.warmup = std::atoi(value.c_str());
        else if (key == "--flush")
            o.flush = value == "on";
        else
            return usage();
    }
    if (o.size < 1 || o.threads < 1 || o.iter < 1 || o.warmup < 0)
        return usage();

    Bench b;
    if (o.task == "mem-access" && (o.variant == "seq" || o.variant == "rand"))
        b = memAccess(o);
    else if (o.task == "matmul" && (o.variant == "ijk" || o.variant == "ikj"))
        b = matmul(o);
    else if (o.task == "objects" && (o.variant == "points-value" || o.variant == "points-ref" || o.variant == "trees"))
        b = objects(o);
    else if (o.task == "mandelbrot" && (o.variant == "static" || o.variant == "dynamic"))
        b = mandelbrot(o);
    else if (o.task == "sync" && (o.variant == "mutex" || o.variant == "atomic" || o.variant == "local-shared" ||
                                  o.variant == "local-padded"))
        b = sync(o);
    else if (o.task == "queue" && o.variant == "blocking" && o.threads >= 2)
        b = queue(o);
    else
    {
        std::fprintf(stderr, "Nieznane zadanie/wariant: %s %s (queue wymaga >= 2 watkow)\n", o.task.c_str(),
                     o.variant.c_str());
        return 1;
    }
    runIterations(o, b);
    return 0;
}
