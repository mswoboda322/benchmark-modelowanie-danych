# Plan badania

*Wersja robocza, 2026-11-09. Zakres i kryteria: [01-zakres-i-kryteria.md](01-zakres-i-kryteria.md).*

## 1. Stanowisko

| Element | Wartość |
|---|---|
| CPU | Intel Core i5-13500: 6 rdzeni P (z HT) + 8 rdzeni E = 14 rdzeni / 20 wątków; L2 11,5 MB łącznie, **L3 24 MB** |
| RAM | 16 GB DDR4-3200 (2 × 8 GB) |
| System | Windows 11 Pro (build 26300) |
| C++ | MSVC v145 (Visual Studio 2026), C++17 |
| C# | .NET 10 (SDK 10.0.301) |
| Java | Oracle JDK 25.0.1 |

**Przygotowanie przed pomiarami:**
- plan zasilania „Najwyższa wydajność”, zamknięte inne programy, pomiary bez debuggera;
- folder `bin\` dodany do wykluczeń Windows Defender. W poprzedniej wersji skanowanie plików
  wykonywalnych dodawało około 100 ms do startu procesu, co zafałszowałoby wyniki K3;
- dla każdej serii zapisywane są wersje kompilatorów i środowisk, data, temperatura CPU
  (np. HWiNFO) i liczba wątków logicznych.

Liczby wątków w K2: **1, 2, 4, 6, 8, 14, 20**. Wartość 6 to liczba rdzeni P, 14 to liczba rdzeni fizycznych,
20 to liczba wątków logicznych. Wątki nie są przypinane do rdzeni (tak działa zwykły program).
Hybrydowa architektura P/E jest omówiona w ograniczeniach.

## 2. Architektura projektu

```
Benchmark.sln
├─ BenchCpp/    K1 + K2, C++ (konsola)
├─ BenchCs/     K1 + K2, C# (konsola)
├─ BenchJava/   K1 + K2, Java (konsola, budowany przez build.ps1 → BenchJava.jar)
├─ GuiCpp/      K3, Win32 API
├─ GuiCs/       K3, WinForms
├─ GuiJava/     K3, Swing (→ GuiJava.jar)
├─ Runner/      C# + Spectre.Console: uruchamia, mierzy procesy, zapisuje CSV
├─ Java.targets javac + jar, wywoływane przy budowaniu Runnera
├─ build.ps1    MSBuild całego rozwiązania → wszystko do bin\
└─ docs/
```

### Wspólny kontrakt programów

```
K1/K2: <program> <zadanie> <wariant> <rozmiar> <wątki> [--iter 10] [--warmup 3] [--flush on|off]
K3:    <program> --bench <scenariusz> --out <plik.csv> [--variant v] [--param n] [--iter 10] [--warmup 2]
```

K1/K2: każda iteracja to jedna linia na stdout (`;`, kropka dziesiętna; Runner zapisuje CSV w formacie `pl-PL`):

```
język;zadanie;wariant;rozmiar;wątki;iteracja;rozgrzewka(0/1);czas_ns;wynik_kontrolny;cpu_ms;gc_liczba;gc_pauza_ms
```

K3: programy GUI zapisują wyniki do pliku z `--out`, bo nie mają konsoli. Po zakończeniu scenariusza same się zamykają.

```
język;scenariusz;wariant;parametr;iteracja;rozgrzewka(0/1);metryka;wartość
```

**Wspólne elementy zaimplementowane identycznie w 3 językach:**
- generator liczb pseudolosowych **SplitMix64** ze stałym ziarnem, dzięki czemu dane wejściowe
  i wyniki kontrolne są identyczne w każdym języku;
- funkcja `flush_cache()` (sekcja 4.2);
- zegar monotoniczny wysokiej rozdzielczości: C++ `QueryPerformanceCounter`,
  C# `Stopwatch.GetTimestamp()`, Java `System.nanoTime()`. Na Windows wszystkie trzy korzystają z QPC,
  więc znaczniki czasu z różnych procesów są porównywalne. Wykorzystuje to pomiar startu GUI w Z3.1;
  zgodność dla Javy sprawdzono w przebiegu próbnym;
- bariera startowa wątków: w K2 wątki zaczynają pracę jednocześnie. Bez niej przy krótkich pętlach
  pierwsze wątki kończyły, zanim ruszyły ostatnie, i np. *false sharing* był niewidoczny.

## 3. Zadania

### K1 – Efektywność pracy z pamięcią (1 wątek)

| Zadanie | Opis | Warianty | Rozmiary | Metryki |
|---|---|---|---|---|
| **Z1.1 `mem-access`**: dostęp do pamięci („memory mountain”) | Przejście po tablicy `int32` o rozmiarze S. Stała liczba dostępów 2²⁴ niezależnie od S. | `seq`: odczyt sekwencyjny z sumowaniem<br>`rand`: *pointer chasing*, gdzie `a[i]` to indeks następnego elementu, a permutacja tworzy jeden cykl (alg. Sattolo + SplitMix64). Prefetcher nie przewidzi kolejnego adresu. | 16 KB, 128 KB, 1 MB, 4 MB, 16 MB, 64 MB, 256 MB (L1 → L2 → L3 → RAM) | **ns/dostęp**; tryby `flush on/off` |
| **Z1.2 `matmul`**: mnożenie macierzy | `double`, macierze w **płaskiej tablicy** `N·N` w każdym języku. Wypełnienie jak w v1. | `ijk`: dostęp kolumnowy do B, nieprzyjazny dla cache<br>`ikj`: dostęp sekwencyjny | N = 256, 512, 1024 | czas, **GFLOP/s** = 2N³/t; tryby `flush on/off` |
| **Z1.3 `objects`**: obiekty w pamięci i alokacja | (a) **układ danych**: 10⁷ punktów (x, y, z: double), suma x+y+z<br>(b) **alokacja**: drzewa binarne (wzór: *binary-trees* z CLBG), czyli wiele krótko żyjących drzew o głębokości 4…d i jedno długo żyjące drzewo o głębokości d | (a) C++ `vector<Point>` / `vector<unique_ptr<Point>>`; C# `struct[]` / `class[]`; Java `Point[]` (obiekty) / trzy tablice `double[]`<br>(b) C++ `new`/`delete`; C#, Java: obiekty i GC | (a) 10⁷<br>(b) d = 14, 16, 18 | czas, **pamięć szczytowa**, liczba i łączny czas pauz GC (C#: `GC.CollectionCount`, `GC.GetTotalPauseDuration`; Java: `GarbageCollectorMXBean`) |

W Z1.3 czyszczenie cache jest wyłączone. Dane są wielokrotnie większe niż L3, a bufor 128 MB zawyżyłby
pamięć szczytową procesu.

### K2 – Wykorzystanie wielu rdzeni

Wątki: `static` i `sync` używają zwykłych wątków (`std::thread` / `Thread` / `Thread`), a `dynamic`
używa mechanizmu wysokiego poziomu typowego dla języka.

| Zadanie | Opis | Warianty | Parametry | Metryki |
|---|---|---|---|---|
| **Z2.1 `mandelbrot`**: skalowanie obliczeń | Zbiór Mandelbrota 2000×2000, maks. 1000 iteracji. Wiersze mają bardzo różny koszt, więc liczy się równoważenie obciążenia. | `static`: równe bloki wierszy<br>`dynamic`: wiersze pobierane po jednym – C++ wspólny licznik `std::atomic`, C# `Parallel.For`, Java strumień równoległy w `ForkJoinPool(n)` | wątki 1…20 | czas, **przyspieszenie** T(1)/T(p), **efektywność** S/p, czas CPU / czas rzeczywisty; wynik kontrolny: suma iteracji |
| **Z2.2 `sync`**: koszt synchronizacji | p wątków wykonuje łącznie 10⁷ inkrementacji | `mutex` (`std::mutex` / `lock` / `synchronized`): jeden licznik pod blokadą<br>`atomic` (`fetch_add` / `Interlocked.Increment` / `AtomicLong`): jeden wspólny licznik (*true sharing*)<br>`local-shared`: własny licznik wątku, liczniki w sąsiednich komórkach tablicy (*false sharing*)<br>`local-padded`: własny licznik wątku co 64 B | wątki 1…20 | **ns/operację** |
| **Z2.3 `queue`**: producent–konsument | 1 producent, p−1 konsumentów, 10⁶ komunikatów `int`, kolejka ograniczona do 1024 | C++: własna kolejka `mutex` + `condition_variable` (biblioteka standardowa nie ma kolejki blokującej); C#: `BlockingCollection<int>`; Java: `ArrayBlockingQueue<Integer>` (opakowywanie `int` w obiekt – Java nie ma typów generycznych dla typów prostych) | wątki 2…20 | **mln komunikatów/s**; wynik kontrolny: suma |

W `local-shared` i `local-padded` liczniki są zwiększane tą samą atomową instrukcją co w `atomic` (`lock add`).
Różnica wynika więc wyłącznie z położenia danych w pamięci. Pierwsza wersja używała zwykłego odczytu
i zapisu (acquire/release). Kompilator JIT Javy (C2) podstawiał wtedy wartość zapisaną przez ten sam wątek
zamiast odczytu z pamięci, przez co *false sharing* w Javie nie był widoczny.

### K3 – Aplikacja okienkowa

Ta sama aplikacja w 3 bibliotekach: okno 1000×800, po lewej lista, po prawej panel z obrazem, przycisk.
Każdy scenariusz uruchamiany jest automatycznie argumentem `--bench <scenariusz>`.

| Zadanie | Opis | Warianty | Metryki |
|---|---|---|---|
| **Z3.1 `startup`**: start aplikacji | Runner zapisuje znacznik QPC tuż przed `Process.Start`. Aplikacja zapisuje znacznik QPC przy **pierwszym rysowaniu okna** (`WM_PAINT` / `OnPaint` / `paintComponent`), czeka 1 s i kończy działanie. | `cold`: po wyczyszczeniu systemowej pamięci podręcznej plików (sekcja 4.2)<br>`warm`: kolejne uruchomienia | **czas do pierwszego okna**, pamięć (working set, private bytes) po starcie, liczba wątków procesu, rozmiar programu na dysku |
| **Z3.2 `fill`**: wypełnienie listy | Dodanie 10⁴ / 10⁵ napisów do listy ze wstrzymanym odświeżaniem (`WM_SETREDRAW` / `BeginUpdate` / model ustawiany po wypełnieniu). Czas mierzony do zakończenia synchronicznego odrysowania (`UpdateWindow` / `Refresh` / `paintImmediately`). 2 iteracje rozgrzewkowe + 10 pomiarów w jednym procesie. | 10⁴, 10⁵ elementów | czas wypełnienia, 1. iteracja, szczytowa pamięć |
| **Z3.2 `draw`**: rysowanie | 100 klatek obrazu 800×800 z bufora pikseli, w każdej klatce przesunięta paleta (`SetDIBitsToDevice` / `Bitmap.LockBits` + `DrawImageUnscaled` / `BufferedImage` + `paintImmediately`). 1 + 5 powtórzeń. | 100 klatek | **klatki/s** |
| **Z3.3 `responsive`**: responsywność | Przez 5 s wątek „sonda” co 10 ms wysyła do wątku UI zdarzenie ze znacznikiem czasu (`PostMessage` / `BeginInvoke` / `invokeLater`). Wątek UI mierzy opóźnienie obsługi. W tym czasie liczone są kolejne obrazy Mandelbrota 1000×1000 (kod z Z2.1). | `idle`: bez obliczeń<br>`ui-thread`: obliczenia na wątku UI (błędna praktyka)<br>`worker`: obliczenia w wątku roboczym, postęp co 50 wierszy raportowany do UI | opóźnienie zdarzeń: średnia, **p95, max**, liczba opóźnień > 100 ms, liczba obliczonych obrazów |

Wszystkie trzy aplikacje rysują bez skalowania DPI (GuiCpp bez manifestu DPI, WinForms `DpiUnaware`,
Swing `sun.java2d.uiScale=1`), więc liczba pikseli przetwarzanych przez aplikację jest taka sama.

Do Z3.3 nie używamy timera UI. Timery systemowe Windows mają rozdzielczość ok. 15,6 ms, a sonda
z własnym znacznikiem czasu mierzy opóźnienie kolejki zdarzeń niezależnie od tej rozdzielczości.

## 4. Metodyka pomiaru

### 4.1 Tryby i powtórzenia (wg Georges i in. 2007)

| Kryterium | Uruchomienie | Rozgrzewka | Pomiary |
|---|---|---|---|
| K1, K2 | 1 proces na konfigurację (język × zadanie × wariant × rozmiar × wątki) | 3 iteracje w procesie, odrzucane jako stan nieustalony JIT; czas **pierwszej** iteracji zapisywany osobno | 10 iteracji w stanie ustalonym |
| Z3.1 `warm` | nowy proces na każdy pomiar | 2 starty odrzucane | 10 startów |
| Z3.1 `cold` | nowy proces po wyczyszczeniu pamięci podręcznej plików | — | 5 startów |
| Z3.2, Z3.3 | 1 proces na konfigurację | iteracje rozgrzewkowe w procesie (`fill` 2, `draw` 1) | `fill` 10, `draw` 5, `responsive` 5 s ciągłego pomiaru |

**Kolejność uruchomień** jest losowana w obrębie serii (np. C# → C++ → Java, potem Java → C# → C++…),
żeby nagrzewanie się procesora i zmiany taktowania nie faworyzowały jednego języka.

### 4.2 Czyszczenie pamięci podręcznych

| Poziom | Co czyścimy | Sposób | Kiedy |
|---|---|---|---|
| Cache CPU (L1–L3) | dane poprzedniej iteracji | `flush_cache()`: zapis i odczyt co 64 B bufora **128 MB** (> 5 × L3). Bufor alokowany raz, poza pomiarem; suma kontrolna wypisywana, żeby kompilator nie usunął pętli. Ta sama metoda w 3 językach, bo `clflush` nie jest dostępny w C# ani Javie. | przed każdą iteracją, gdy `--flush on` (domyślnie). W Z1.1 i Z1.2 mierzymy oba tryby, żeby pokazać wpływ cache. |
| Sterta / GC | śmieci z poprzedniej iteracji | C#: `GC.Collect()` + `GC.WaitForPendingFinalizers()`; Java: `System.gc()`; C++: nie dotyczy | przed każdą iteracją, poza pomiarem |
| Strony pamięci / TLB | błędy stron przy pierwszym dostępie | dane wejściowe zapisywane w całości (*pre-touch*) przed pomiarem | raz, po alokacji |
| Pamięć podręczna plików systemu | pliki exe, DLL, JAR, biblioteki runtime | `RAMMap64.exe -Es` (Sysinternals, wymaga uprawnień administratora) czyści listę standby; alternatywnie restart | przed każdym startem `cold` w Z3.1 |
| JIT | skompilowany kod | nowy proces | każda konfiguracja K1/K2 i każdy start K3 |

### 4.3 Pomiar zasobów procesu (Runner, jednakowo dla 3 języków)

Po zakończeniu procesu Runner odczytuje przez jego uchwyt:
- `GetProcessMemoryInfo`: **PeakWorkingSetSize**, **PeakPagefileUsage** (pamięć prywatna);
- `GetProcessTimes`: czas CPU (user + kernel), który porównany z czasem rzeczywistym pokazuje
  wykorzystanie rdzeni w K2;
- całkowity czas życia procesu (narzut startu środowiska uruchomieniowego).

Liczniki są dostępne po zakończeniu procesu, dopóki uchwyt jest otwarty (sprawdzono w przebiegu próbnym).
Java jest uruchamiana przez `java.exe` z JDK, a nie przez pośrednika z folderu `Common Files\Oracle\Java\javapath`.
Ten pośrednik uruchamia osobny proces, więc Runner mierzyłby niewłaściwy proces.
W Z3.1 Runner co 50 ms odczytuje też liczbę wątków procesu.

### 4.4 Statystyka

Dla każdej konfiguracji: **mediana**, średnia, odchylenie std., min, **95% przedział ufności**
(rozkład t-Studenta). Wartości odstające nie są usuwane, tylko raportowane.
Do porównania między językami wyniki są normalizowane względem C++ (C++ = 1,00).
Gdy przedziały ufności dwóch języków się nakładają, różnicy nie uznajemy za istotną.

### 4.5 Uczciwość porównania

- identyczny algorytm i dane (SplitMix64), wynik kontrolny porównywany przez Runner;
- kod idiomatyczny, z biblioteką standardową, bez intrinsics SIMD, bez `unsafe` (C#) i bez `Unsafe` (Java);
- domyślne ustawienia środowisk, czyli to, co dostaje programista:

| | Ustawienia |
|---|---|
| C++ | Release x64, `/O2 /EHsc /std:c++17`, `/fp:precise`, CRT `/MD` |
| C# | Release, `net10.0`, `Optimize=true`, kompilacja warstwowa + PGO (domyślnie), GC stacji roboczej |
| Java | `javac --release 25`, `java -jar` bez flag (G1 GC, C2 JIT, sterta maks. = ¼ RAM) |

## 5. Wyniki i wykresy

| Kryterium | Wykres / tabela |
|---|---|
| K1 | ns/dostęp w funkcji rozmiaru danych (oś log), linie: język × `seq`/`rand`, osobno flush on/off; słupki GFLOP/s `ijk` vs `ikj`; tabela czas + pamięć szczytowa + pauzy GC dla Z1.3 |
| K2 | przyspieszenie w funkcji liczby wątków (z linią idealną) dla `static`/`dynamic`; ns/operację dla 4 wariantów `sync`; komunikaty/s |
| K3 | słupki czasu startu `cold`/`warm` i pamięci po starcie; klatki/s; tabela opóźnień (śr., p95, max) dla `idle`/`ui-thread`/`worker` |

Pliki w `bin\wyniki_RRRRMMDD_GGMMSS\`: `K1_/K2_iteracje.csv`, `K1_/K2_procesy.csv`, `K1_/K2_podsumowanie.csv`,
`K3_pomiary.csv`, `K3_procesy.csv`, `K3_podsumowanie.csv` (separator `;`, UTF-8 z BOM, polski Excel).
Runner wyświetla też tabele w konsoli, w tym podsumowanie „czas względem C++” (średnia geometryczna
dla każdego zadania). Wykresy generowane w Excelu albo skryptem.

## 6. Ograniczenia (do opisania w dokumentacji)

- Hybrydowy CPU: wątki trafiają na rdzenie P lub E według harmonogramu Windows (Thread Director),
  więc skalowanie powyżej 6 wątków nie będzie liniowe.
- Turbo Boost i temperatura wpływają na wyniki; ograniczamy to losową kolejnością i medianą.
- Biblioteki GUI nie są równoważne: Win32 to cienka warstwa nad systemem, a WinForms i Swing to frameworki.
  W K3 porównujemy więc **typowy wybór programisty** danego języka, a nie sam język.
- `flush_cache()` nie czyści pamięci podręcznej instrukcji ani predyktora skoków.
- Jedno stanowisko i jeden system operacyjny.

## 7. Etapy prac

| # | Etap | Rezultat |
|---|---|---|
| 1 | Zakres, kryteria, plan | ten dokument ✔ |
| 2 | Szkielet: kontrakt CLI, SplitMix64, `flush_cache`, zegar w 3 językach; `BenchJava` + `build.ps1`; usunięcie `BenchC`, archiwum wyników v1 | ✔ |
| 3 | K1: Z1.1–Z1.3 w 3 językach, zgodne wyniki kontrolne | ✔ (C# i Java – wyniki kontrolne zgodne) |
| 4 | K2: Z2.1–Z2.3 | ✔ |
| 5 | K3: 3 aplikacje GUI ze scenariuszami `--bench` | ✔ |
| 6 | Runner: nowe serie, losowa kolejność, pomiar zasobów, CSV | ✔ |
| 7 | Próbny przebieg `Runner.exe --quick`, korekta parametrów | ✔ dla C# i Javy; C++ czeka na kompilator MSVC |
| 8 | Pomiary właściwe: `Runner.exe` (ok. 30–40 min) + opcjonalnie `Runner.exe K3 --cold` | CSV |
| 9 | Analiza, wykresy, dokumentacja → Google Docs | dokumentacja badania i projektu |

## 8. Struktura dokumentacji końcowej (krótka)

1. Cel i kryteria (z `01-zakres-i-kryteria.md`, skrócone).
2. Przegląd literatury (tabela).
3. Stanowisko i metodyka (sekcje 1 i 4 tego planu).
4. Wyniki K1, K2, K3: po jednym wykresie i jednej tabeli na zadanie, 2–3 zdania komentarza.
5. Weryfikacja hipotez H1–H3.
6. Wnioski, ograniczenia, dalsze prace.
7. Załącznik: opis projektu (struktura, budowanie, uruchomienie).
