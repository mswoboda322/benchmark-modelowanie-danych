/*
 * BenchC – wersja w języku C (wielowątkowość: OpenMP).
 *
 * Użycie:  BenchC.exe <zadanie> <N> <liczba_wątków>
 *          zadanie = matmul | primes | fibonacci
 * Wyjście: C;<zadanie>;<N>;<wątki>;<wynik_kontrolny>;<czas_ms>
 *
 * Czas mierzony jest tylko dla części obliczeniowej (QueryPerformanceCounter).
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <omp.h>
#include <windows.h>

/* Rozmiar porcji liczb przydzielanej wątkowi w zadaniu primes. */
#define PRIMES_CHUNK 10000
/* Głębokość rozwinięcia drzewa rekurencji Fibonacciego (2^5 = 32 podzadania). */
#define FIB_DEPTH 5
#define FIB_MAX_TASKS 32

static LARGE_INTEGER g_freq, g_start;

static void timer_start(void)
{
    QueryPerformanceFrequency(&g_freq);
    QueryPerformanceCounter(&g_start);
}

static double timer_stop_ms(void)
{
    LARGE_INTEGER end;
    QueryPerformanceCounter(&end);
    return (double)(end.QuadPart - g_start.QuadPart) * 1000.0 / (double)g_freq.QuadPart;
}

/* ---------- 1. Mnożenie macierzy N×N (pętla i-k-j, podział wierszami) ---------- */
static void bench_matmul(int n, int threads)
{
    double *a = (double *)malloc((size_t)n * n * sizeof(double));
    double *b = (double *)malloc((size_t)n * n * sizeof(double));
    double *c = (double *)calloc((size_t)n * n, sizeof(double));
    double sum = 0.0, ms;
    size_t idx;
    int i, j, k;

    if (!a || !b || !c) {
        fprintf(stderr, "Brak pamieci\n");
        exit(2);
    }

    /* Deterministyczne wypełnienie macierzy wejściowych. */
    for (i = 0; i < n; i++)
        for (j = 0; j < n; j++) {
            a[(size_t)i * n + j] = (double)((i + j) % 10);
            b[(size_t)i * n + j] = (double)((i * j) % 10);
        }

    timer_start();
    /* Każdy wątek liczy ciągły blok wierszy macierzy wynikowej. */
#pragma omp parallel for private(j, k) schedule(static) num_threads(threads)
    for (i = 0; i < n; i++) {
        for (k = 0; k < n; k++) {
            double aik = a[(size_t)i * n + k];
            for (j = 0; j < n; j++)
                c[(size_t)i * n + j] += aik * b[(size_t)k * n + j];
        }
    }
    ms = timer_stop_ms();

    /* Wynik kontrolny: suma wszystkich elementów C (poza pomiarem czasu). */
    for (idx = 0; idx < (size_t)n * n; idx++)
        sum += c[idx];

    printf("C;matmul;%d;%d;%.0f;%.3f\n", n, threads, sum, ms);
    free(a);
    free(b);
    free(c);
}

/* ---------- 2. Liczby pierwsze w [2, N] – dzielenie próbne do sqrt(n) ---------- */
static int is_prime(long long n)
{
    long long d;
    if (n < 2)
        return 0;
    if (n % 2 == 0)
        return n == 2;
    for (d = 3; d * d <= n; d += 2)
        if (n % d == 0)
            return 0;
    return 1;
}

static void bench_primes(int n, int threads)
{
    /* Zakres [2, N] dzielony na porcje; wątki pobierają je dynamicznie. */
    int chunks = (n - 2) / PRIMES_CHUNK + 1;
    long long count = 0;
    double ms;
    int ch;

    timer_start();
#pragma omp parallel for schedule(dynamic) reduction(+ : count) num_threads(threads)
    for (ch = 0; ch < chunks; ch++) {
        long long from = 2 + (long long)ch * PRIMES_CHUNK;
        long long to = from + PRIMES_CHUNK - 1;
        long long x;
        if (to > n)
            to = n;
        for (x = from; x <= to; x++)
            count += is_prime(x);
    }
    ms = timer_stop_ms();

    printf("C;primes;%d;%d;%lld;%.3f\n", n, threads, count, ms);
}

/* ---------- 3. Fibonacci – naiwna rekurencja ---------- */
static unsigned long long fib(int n)
{
    if (n < 2)
        return (unsigned long long)n;
    return fib(n - 1) + fib(n - 2);
}

/* Rozwija drzewo rekurencji do zadanej głębokości – liście to podzadania fib(N-k). */
static void make_fib_tasks(int n, int depth, int *tasks, int *count)
{
    if (depth == 0 || n < 2) {
        tasks[(*count)++] = n;
        return;
    }
    make_fib_tasks(n - 1, depth - 1, tasks, count);
    make_fib_tasks(n - 2, depth - 1, tasks, count);
}

static void bench_fibonacci(int n, int threads)
{
    int tasks[FIB_MAX_TASKS];
    unsigned long long results[FIB_MAX_TASKS];
    unsigned long long sum = 0;
    int count = 0, t;
    double ms;

    make_fib_tasks(n, FIB_DEPTH, tasks, &count);

    timer_start();
    /* Podzadania mają różny koszt, więc przydział dynamiczny po jednym. */
#pragma omp parallel for schedule(dynamic, 1) num_threads(threads)
    for (t = 0; t < count; t++)
        results[t] = fib(tasks[t]);
    for (t = 0; t < count; t++)
        sum += results[t];
    ms = timer_stop_ms();

    printf("C;fibonacci;%d;%d;%llu;%.3f\n", n, threads, sum, ms);
}

int main(int argc, char *argv[])
{
    int n, threads;

    if (argc != 4) {
        fprintf(stderr, "Uzycie: BenchC.exe <matmul|primes|fibonacci> <N> <liczba_watkow>\n");
        return 1;
    }
    n = atoi(argv[2]);
    threads = atoi(argv[3]);
    if (n < 1 || threads < 1) {
        fprintf(stderr, "N i liczba watkow musza byc dodatnie\n");
        return 1;
    }

    if (strcmp(argv[1], "matmul") == 0)
        bench_matmul(n, threads);
    else if (strcmp(argv[1], "primes") == 0)
        bench_primes(n, threads);
    else if (strcmp(argv[1], "fibonacci") == 0)
        bench_fibonacci(n, threads);
    else {
        fprintf(stderr, "Nieznane zadanie: %s\n", argv[1]);
        return 1;
    }
    return 0;
}
