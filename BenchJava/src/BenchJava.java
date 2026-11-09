// BenchJava – zadania K1 (pamięć) i K2 (wielowątkowość) w Javie (JDK 25).
//
// Użycie i format wyjścia – jak w BenchCpp/main.cpp:
//   java -jar BenchJava.jar <zadanie> <wariant> <rozmiar> <wątki> [--iter 10] [--warmup 3] [--flush on|off]
//   język;zadanie;wariant;rozmiar;wątki;iteracja;rozgrzewka;czas_ns;wynik;cpu_ms;gc_liczba;gc_pauza_ms

import java.lang.management.GarbageCollectorMXBean;
import java.lang.management.ManagementFactory;
import java.util.Locale;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ForkJoinPool;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicLongArray;
import java.util.function.IntConsumer;
import java.util.function.Supplier;
import java.util.stream.IntStream;

public final class BenchJava {
    private static final int FLUSH_BYTES = 128 * 1024 * 1024; // > 5 × L3 (24 MB)
    private static final int CACHE_LINE = 64;
    private static final long SEED = 42;
    private static final long MEM_ACCESSES = 1L << 24;
    private static final int MANDEL_MAX_ITER = 1000;
    private static final int QUEUE_CAPACITY = 1024;

    private static byte[] flushBuffer = new byte[0];
    private static long sink;

    /** Parametry wywołania. */
    record Options(String task, String variant, long size, int threads, int iter, int warmup, boolean flush) {}

    /** Zadanie = przygotowanie iteracji (poza pomiarem) + część mierzona + wynik kontrolny. */
    record Bench(Runnable prepare, Runnable run, Supplier<String> result) {}

    /** Generator SplitMix64 – ta sama sekwencja liczb w każdym języku. */
    static final class SplitMix64 {
        private long state;

        SplitMix64(long seed) { state = seed; }

        long next() {
            long z = (state += 0x9E3779B97F4A7C15L);
            z = (z ^ (z >>> 30)) * 0xBF58476D1CE4E5B9L;
            z = (z ^ (z >>> 27)) * 0x94D049BB133111EBL;
            return z ^ (z >>> 31);
        }

        /** Liczba z przedziału [0, bound). */
        int nextInt(int bound) { return (int) ((next() >>> 33) % bound); }
    }

    /** Pojemnik na wynik obliczony w wyrażeniu lambda. */
    static final class Holder { long value; }

    public static void main(String[] args) throws Exception {
        Options o = parse(args);
        if (o == null) {
            System.err.println("Uzycie: java -jar BenchJava.jar <zadanie> <wariant> <rozmiar> <watki> "
                    + "[--iter 10] [--warmup 3] [--flush on|off]");
            System.exit(1);
        }
        Bench bench = switch (o.task() + " " + o.variant()) {
            case "mem-access seq", "mem-access rand" -> memAccess(o);
            case "matmul ijk", "matmul ikj" -> matmul(o);
            case "objects points-value", "objects points-ref", "objects trees" -> objects(o);
            case "mandelbrot static", "mandelbrot dynamic" -> mandelbrot(o);
            case "sync mutex", "sync atomic", "sync local-shared", "sync local-padded" -> sync(o);
            case "queue blocking" -> o.threads() >= 2 ? queue(o) : null;
            default -> null;
        };
        if (bench == null) {
            System.err.println("Nieznane zadanie/wariant: " + o.task() + " " + o.variant()
                    + " (queue wymaga >= 2 watkow)");
            System.exit(1);
        }
        runIterations(o, bench);
        System.exit(0); // kończy też wątki puli ForkJoinPool
    }

    private static Options parse(String[] args) {
        try {
            if (args.length < 4) return null;
            long size = Long.parseLong(args[2]);
            int threads = Integer.parseInt(args[3]);
            int iter = 10, warmup = 3;
            boolean flush = true;
            for (int i = 4; i + 1 < args.length; i += 2) {
                switch (args[i]) {
                    case "--iter" -> iter = Integer.parseInt(args[i + 1]);
                    case "--warmup" -> warmup = Integer.parseInt(args[i + 1]);
                    case "--flush" -> flush = args[i + 1].equals("on");
                    default -> { return null; }
                }
            }
            if (size < 1 || threads < 1 || iter < 1 || warmup < 0) return null;
            return new Options(args[0], args[1], size, threads, iter, warmup, flush);
        } catch (NumberFormatException e) {
            return null;
        }
    }

    // ===================== Pętla pomiarowa =====================

    private static void runIterations(Options o, Bench b) {
        if (o.flush()) {
            flushBuffer = new byte[FLUSH_BYTES];
            java.util.Arrays.fill(flushBuffer, (byte) 1);
        }
        var os = (com.sun.management.OperatingSystemMXBean) ManagementFactory.getOperatingSystemMXBean();
        var gcs = ManagementFactory.getGarbageCollectorMXBeans();
        StringBuilder line = new StringBuilder();
        for (int it = 1; it <= o.warmup() + o.iter(); it++) {
            if (b.prepare() != null) b.prepare().run();
            System.gc(); // sprzątanie sterty z poprzedniej iteracji – poza pomiarem
            if (o.flush()) flushCache();

            long cpu0 = os.getProcessCpuTime();
            long gcCount0 = 0, gcTime0 = 0;
            for (GarbageCollectorMXBean gc : gcs) { gcCount0 += gc.getCollectionCount(); gcTime0 += gc.getCollectionTime(); }
            long t0 = System.nanoTime();
            b.run().run();
            long t1 = System.nanoTime();
            long gcCount1 = 0, gcTime1 = 0;
            for (GarbageCollectorMXBean gc : gcs) { gcCount1 += gc.getCollectionCount(); gcTime1 += gc.getCollectionTime(); }
            long cpu1 = os.getProcessCpuTime();

            line.setLength(0);
            line.append("Java;").append(o.task()).append(';').append(o.variant()).append(';')
                .append(o.size()).append(';').append(o.threads()).append(';').append(it).append(';')
                .append(it <= o.warmup() ? 1 : 0).append(';').append(t1 - t0).append(';')
                .append(b.result().get()).append(';')
                .append(String.format(Locale.ROOT, "%.3f", (cpu1 - cpu0) / 1e6)).append(';')
                .append(gcCount1 - gcCount0).append(';')
                .append(String.format(Locale.ROOT, "%.3f", (double) (gcTime1 - gcTime0)));
            System.out.println(line);
            System.out.flush();
        }
    }

    /** Czyszczenie cache CPU: zapis i odczyt co 64 B bufora większego niż L3. */
    private static void flushCache() {
        byte[] buf = flushBuffer;
        long sum = 0;
        for (int i = 0; i < buf.length; i += CACHE_LINE) {
            buf[i]++;
            sum += buf[i];
        }
        sink += sum;
    }

    /** Liczba operacji wątku t przy podziale total na threads części. */
    private static long share(long total, int threads, int t) {
        return total / threads + (t < total % threads ? 1 : 0);
    }

    /**
     * Uruchamia threads wątków work(nrWątku) i czeka na ich zakończenie. Bariera startowa sprawia,
     * że wątki zaczynają pracę jednocześnie (inaczej pierwsze mogłyby skończyć, zanim ruszą ostatnie).
     */
    private static void runThreads(int threads, IntConsumer work) {
        AtomicInteger waiting = new AtomicInteger(threads);
        Thread[] pool = new Thread[threads];
        for (int t = 0; t < threads; t++) {
            final int id = t;
            pool[t] = new Thread(() -> {
                waiting.decrementAndGet();
                while (waiting.get() > 0) Thread.yield();
                work.accept(id);
            });
            pool[t].start();
        }
        try {
            for (Thread th : pool) th.join();
        } catch (InterruptedException e) {
            throw new IllegalStateException(e);
        }
    }

    // ===================== K1 – pamięć =====================

    /** Z1.1: tablica int z permutacją jednocyklową (Sattolo); seq – po kolei, rand – pointer chasing. */
    private static Bench memAccess(Options o) {
        int n = (int) (o.size() * 1024 / Integer.BYTES);
        int[] a = new int[n];
        for (int i = 0; i < n; i++) a[i] = i;
        SplitMix64 rng = new SplitMix64(SEED);
        for (int i = n - 1; i > 0; i--) {
            int j = rng.nextInt(i);
            int tmp = a[i];
            a[i] = a[j];
            a[j] = tmp;
        }

        Holder sum = new Holder();
        Runnable run = o.variant().equals("seq")
                ? () -> {
                    long s = 0, left = MEM_ACCESSES;
                    while (left > 0) {
                        int m = (int) Math.min(left, n);
                        for (int i = 0; i < m; i++) s += a[i];
                        left -= m;
                    }
                    sum.value = s;
                }
                : () -> {
                    long s = 0;
                    int idx = 0;
                    for (long k = 0; k < MEM_ACCESSES; k++) {
                        idx = a[idx];
                        s += idx;
                    }
                    sum.value = s;
                };
        return new Bench(null, run, () -> Long.toString(sum.value));
    }

    /** Z1.2: mnożenie macierzy N×N w płaskich tablicach; ijk vs ikj. */
    private static Bench matmul(Options o) {
        int n = (int) o.size();
        double[] a = new double[n * n], b = new double[n * n], c = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) {
                a[i * n + j] = (i + j) % 10;
                b[i * n + j] = (long) i * j % 10;
            }

        Runnable run = o.variant().equals("ijk")
                ? () -> {
                    for (int i = 0; i < n; i++)
                        for (int j = 0; j < n; j++) {
                            double s = 0.0;
                            for (int k = 0; k < n; k++) s += a[i * n + k] * b[k * n + j];
                            c[i * n + j] = s;
                        }
                }
                : () -> {
                    for (int i = 0; i < n; i++)
                        for (int k = 0; k < n; k++) {
                            double aik = a[i * n + k];
                            for (int j = 0; j < n; j++) c[i * n + j] += aik * b[k * n + j];
                        }
                };
        return new Bench(() -> java.util.Arrays.fill(c, 0.0), run, () -> {
            double s = 0.0;
            for (double v : c) s += v;
            return Long.toString((long) s);
        });
    }

    /** Punkt jako obiekt (tablica przechowuje referencje). */
    static final class Point {
        final double x, y, z;

        Point(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    }

    static final class Node {
        final Node left, right;

        Node(Node left, Node right) { this.left = left; this.right = right; }
    }

    private static Node bottomUp(int depth) {
        return depth > 0 ? new Node(bottomUp(depth - 1), bottomUp(depth - 1)) : new Node(null, null);
    }

    private static long check(Node node) {
        return 1 + (node.left == null ? 0 : check(node.left) + check(node.right));
    }

    /**
     * Z1.3: (a) punkty – Java nie ma typów wartościowych, więc „points-value” to trzy tablice double[]
     * (struktura tablic), a „points-ref” to tablica obiektów Point; (b) drzewa binarne (binary-trees z CLBG).
     */
    private static Bench objects(Options o) {
        Holder sum = new Holder();
        Supplier<String> result = () -> Long.toString(sum.value);

        if (o.variant().equals("trees")) {
            int maxDepth = Math.max(6, (int) o.size());
            return new Bench(null, () -> {
                final int minDepth = 4;
                long total = check(bottomUp(maxDepth + 1));
                Node longLived = bottomUp(maxDepth);
                for (int depth = minDepth; depth <= maxDepth; depth += 2) {
                    int iterations = 1 << (maxDepth - depth + minDepth);
                    for (int i = 0; i < iterations; i++) total += check(bottomUp(depth));
                }
                total += check(longLived);
                sum.value = total;
            }, result);
        }

        int n = (int) o.size();
        if (o.variant().equals("points-value")) {
            double[] xs = new double[n], ys = new double[n], zs = new double[n];
            for (int i = 0; i < n; i++) {
                xs[i] = i % 1000;
                ys[i] = (long) i * 7 % 1000;
                zs[i] = (long) i * 13 % 1000;
            }
            return new Bench(null, () -> {
                double s = 0.0;
                for (int i = 0; i < n; i++) s += xs[i] + ys[i] + zs[i];
                sum.value = (long) s;
            }, result);
        }

        Point[] pts = new Point[n];
        for (int i = 0; i < n; i++) pts[i] = new Point(i % 1000, (long) i * 7 % 1000, (long) i * 13 % 1000);
        return new Bench(null, () -> {
            double s = 0.0;
            for (Point p : pts) s += p.x + p.y + p.z;
            sum.value = (long) s;
        }, result);
    }

    // ===================== K2 – wielowątkowość =====================

    /** Jeden wiersz zbioru Mandelbrota (ta sama kolejność działań co w C++ i C#). */
    static void mandelRow(int[] img, int width, int height, int y) {
        double cy = -1.5 + 3.0 * y / height;
        for (int x = 0; x < width; x++) {
            double cx = -2.0 + 3.0 * x / width;
            double zx = 0.0, zy = 0.0;
            int it = 0;
            while (it < MANDEL_MAX_ITER) {
                double zx2 = zx * zx, zy2 = zy * zy;
                if (zx2 + zy2 > 4.0) break;
                zy = 2.0 * zx * zy + cy;
                zx = zx2 - zy2 + cx;
                it++;
            }
            img[y * width + x] = it;
        }
    }

    /** Z2.1: static – bloki wierszy na wątkach Thread; dynamic – strumień równoległy w ForkJoinPool(n). */
    private static Bench mandelbrot(Options o) {
        int size = (int) o.size(), threads = o.threads();
        int[] img = new int[size * size];
        // Pula tworzona raz, poza pomiarem (jak globalna pula wątków w .NET).
        ForkJoinPool pool = o.variant().equals("dynamic") ? new ForkJoinPool(threads) : null;
        Runnable run = pool == null
                ? () -> runThreads(threads, t -> {
                    int from = (int) ((long) size * t / threads), to = (int) ((long) size * (t + 1) / threads);
                    for (int y = from; y < to; y++) mandelRow(img, size, size, y);
                })
                : () -> {
                    try {
                        pool.submit(() -> IntStream.range(0, size).parallel()
                                .forEach(y -> mandelRow(img, size, size, y))).get();
                    } catch (Exception e) {
                        throw new IllegalStateException(e);
                    }
                };
        return new Bench(() -> java.util.Arrays.fill(img, 0), run, () -> {
            long s = 0;
            for (int v : img) s += v;
            return Long.toString(s);
        });
    }

    /** Z2.2: koszt synchronizacji przy inkrementacji licznika. */
    private static Bench sync(Options o) {
        long total = o.size();
        int threads = o.threads();
        Holder sum = new Holder();
        Runnable run = switch (o.variant()) {
            case "mutex" -> () -> {
                Object gate = new Object();
                Holder counter = new Holder();
                runThreads(threads, t -> {
                    for (long k = share(total, threads, t); k > 0; k--)
                        synchronized (gate) { counter.value++; }
                });
                sum.value = counter.value;
            };
            case "atomic" -> () -> {
                AtomicLong counter = new AtomicLong();
                runThreads(threads, t -> {
                    for (long k = share(total, threads, t); k > 0; k--) counter.incrementAndGet();
                });
                sum.value = counter.get();
            };
            // Własny licznik każdego wątku: w sąsiednich komórkach (local-shared – false sharing) albo
            // co 8 elementów = 64 B (local-padded). Ta sama instrukcja co w „atomic” (lock add),
            // więc różnica wynika wyłącznie z układu danych w pamięci.
            default -> () -> {
                int stride = o.variant().equals("local-padded") ? CACHE_LINE / Long.BYTES : 1;
                AtomicLongArray counters = new AtomicLongArray((threads + 1) * stride);
                runThreads(threads, t -> {
                    int idx = (t + 1) * stride - (stride == 1 ? 1 : 0);
                    for (long k = share(total, threads, t); k > 0; k--)
                        counters.incrementAndGet(idx);
                });
                long s = 0;
                for (int i = 0; i < counters.length(); i++) s += counters.get(i);
                sum.value = s;
            };
        };
        return new Bench(null, run, () -> Long.toString(sum.value));
    }

    /** Z2.3: 1 producent, (wątki − 1) konsumentów, ArrayBlockingQueue o pojemności 1024; 0 = koniec. */
    private static Bench queue(Options o) {
        int messages = (int) o.size(), consumers = o.threads() - 1;
        Holder sum = new Holder();
        return new Bench(null, () -> {
            ArrayBlockingQueue<Integer> queue = new ArrayBlockingQueue<>(QUEUE_CAPACITY);
            long[] partial = new long[consumers];
            Thread[] pool = new Thread[consumers];
            for (int c = 0; c < consumers; c++) {
                final int id = c;
                pool[c] = new Thread(() -> {
                    long s = 0;
                    try {
                        for (int v; (v = queue.take()) != 0; ) s += v;
                    } catch (InterruptedException e) {
                        throw new IllegalStateException(e);
                    }
                    partial[id] = s;
                });
                pool[c].start();
            }
            try {
                for (int i = 1; i <= messages; i++) queue.put(i);
                for (int c = 0; c < consumers; c++) queue.put(0);
                for (Thread th : pool) th.join();
            } catch (InterruptedException e) {
                throw new IllegalStateException(e);
            }
            long s = 0;
            for (long v : partial) s += v;
            sum.value = s;
        }, () -> Long.toString(sum.value));
    }
}
