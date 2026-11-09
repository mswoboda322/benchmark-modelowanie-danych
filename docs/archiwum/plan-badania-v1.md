# Badanie wydajności: C vs C++ vs C# (obliczenia matematyczne, wielowątkowe)

## 1. Języki i wielowątkowość

| Program | Język | Wielowątkowość | Projekt VS |
|---|---|---|---|
| `BenchC.exe` | C (MSVC, kompilacja jako C `/TC`) | **OpenMP** (`#pragma omp parallel for`, `/openmp`) | Console App (C++) z plikiem `.c` |
| `BenchCpp.exe` | C++17 | **`std::thread`** (ręczny podział na zakresy) | Console App (C++) |
| `BenchCs.exe` | C# (.NET 10) | **`Parallel.For`** (TPL) | Console App (.NET 10) |
| `Runner.exe` | C# (.NET 10) | — (uruchamia i porównuje) | Console App (.NET 10) |

> Zmiana względem pierwotnej wersji: zamiast .NET 8 użyto **.NET 10** (LTS) – jedyny SDK/runtime zainstalowany na stanowisku badawczym.

Dlaczego C: kompiluje się natywnie w Visual Studio bez dodatkowych narzędzi, a OpenMP pokazuje trzecie, odmienne podejście do wątków.

## 2. Zadania (identyczny algorytm we wszystkich 3 językach)

1. **`matmul` – mnożenie macierzy** `N×N` (double, domyślnie `N=1000`).
   Macierze A, B wypełnione deterministycznie: `A[i][j] = (i+j)%10`, `B[i][j] = (i*j)%10`. Pętla `i-k-j`. Wątki dzielą się **wierszami** wyniku.
   Wynik kontrolny: suma wszystkich elementów C.
2. **`primes` – liczenie liczb pierwszych** w zakresie `[2, N]` (domyślnie `N=10 000 000`) metodą dzielenia próbnego do `sqrt(n)`.
   Wątki dzielą się zakresem liczb (podział cykliczny / dynamiczny – większe liczby są droższe).
   Wynik kontrolny: liczba znalezionych liczb pierwszych.
3. **`fibonacci` – ciąg Fibonacciego, naiwna rekurencja** `fib(n) = fib(n-1) + fib(n-2)`, domyślnie `N=40` (ok. 330 mln wywołań, celowo kosztowne).
   Równoległość: drzewo rekurencji rozwijamy do głębokości 5 → lista **32 podzadań** `fib(N-k)` (każda gałąź osobno), które wątki pobierają dynamicznie (C: `schedule(dynamic)`, C++: wspólny licznik `std::atomic<int>`, C#: `Parallel.For`). Wynik = suma wyników podzadań.
   Wynik kontrolny: `fib(N)` jako `uint64` (dla 40 → `102334155`).

## 3. Interfejs programów (wspólny kontrakt)

```
BenchX.exe <zadanie> <N> <liczba_wątków>
np. BenchCpp.exe matmul 1000 8
```
Wyjście – **jedna linia** na stdout:
```
<język>;<zadanie>;<N>;<wątki>;<wynik_kontrolny>;<czas_ms>
```
- `czas_ms` mierzony **wewnątrz programu**, tylko sama część obliczeniowa (bez alokacji danych wejściowych i startu procesu): C – `QueryPerformanceCounter`, C++ – `std::chrono::steady_clock`, C# – `Stopwatch`.
- Kod ma być prosty i czytelny: jedna funkcja na zadanie, komentarze po polsku, bez mikrooptymalizacji (bez SIMD, bez `unsafe`).

## 4. Runner (C#)

1. Szuka `BenchC.exe`, `BenchCpp.exe`, `BenchCs.exe` w folderze `bin\` obok siebie.
2. Dla każdego zadania × języka × liczby wątków `{1, 2, 4, 8, Environment.ProcessorCount}`:
   - 1 przebieg rozgrzewkowy (odrzucany) + **5 pomiarów**,
   - uruchamia przez `Process` (przekierowany stdout), parsuje linię,
   - zapisuje też czas całego procesu (`Stopwatch` w Runnerze) – pokazuje narzut startu (.NET JIT).
3. Liczy: średnia, min, odchylenie std., **przyspieszenie** `T(1)/T(n)`.
4. Sprawdza zgodność wyników kontrolnych między językami (ostrzeżenie przy różnicy).
5. Argumenty opcjonalne: `Runner.exe [powtórzenia] [N_matmul] [N_primes] [N_fibonacci]`.

### Output konsolowy (biblioteka NuGet **Spectre.Console**)
- Nagłówek: nazwa badania, CPU (`Environment.ProcessorCount`), data, parametry N.
- Pasek postępu w trakcie pomiarów (`AnsiConsole.Progress`): „matmul | C++ | 4 wątki | pomiar 3/5”.
- Po każdym zadaniu **osobna tabela** (ramki, wyrównane kolumny):
  `Język | Wątki | Śr. [ms] | Min [ms] | Odch. std | Przyspieszenie | Czas procesu [ms] | Wynik`
  – najszybszy wynik w każdym wierszu liczby wątków **zielony**, najwolniejszy **czerwony**, niezgodny wynik kontrolny oznaczony ⚠.
- Na końcu **podsumowanie**: tabela „zwycięzca dla każdego zadania” + wykres słupkowy (`BarChart`) średnich czasów dla maks. liczby wątków.

### Plik `results.csv`
- Zapis do folderu Runnera, nadpisywany przy każdym uruchomieniu (dodatkowo kopia `results_RRRRMMDD_HHMMSS.csv`).
- Separator `;`, przecinek dziesiętny (kultura `pl-PL`) → otwiera się poprawnie w polskim Excelu, UTF-8 z BOM.
- Kolumny: `Zadanie;Język;N;Wątki;Pomiar_nr;Czas_ms;Czas_procesu_ms;Wynik` (wszystkie surowe pomiary) oraz drugi plik `summary.csv`: `Zadanie;Język;Wątki;Średnia_ms;Min_ms;Odch_std_ms;Przyspieszenie`.

## 5. Struktura rozwiązania

```
Benchmark.sln
├─ BenchC/      main.c        (C, /openmp)
├─ BenchCpp/    main.cpp      (C++17)
├─ BenchCs/     Program.cs    (.NET 10)
├─ Runner/      Program.cs    (.NET 10)
└─ bin/         ← wszystkie 4 exe kopiowane tu (post-build / OutDir)
```

## 6. Ustawienia kompilacji (uczciwe porównanie)

- Wszystko **Release x64**.
- C/C++: `/O2`, `/fp:precise` (domyślne), C: dodatkowo `/TC` i `/openmp`. Uwaga: OpenMP w MSVC (2.0) wymaga **ze znakiem** zmiennej pętli (`int`, nie `size_t`).
- C#: `<Optimize>true</Optimize>`, `<TieredPGO>true</TieredPGO>`, `<ServerGarbageCollector>false</ServerGarbageCollector>`.
- Pomiary uruchamiać bez debuggera (Ctrl+F5), na zasilaniu sieciowym, z zamkniętymi innymi programami.

## 7. Oczekiwany efekt końcowy

- Tabela: zadanie | język | wątki | śr. czas [ms] | min | odch. std | przyspieszenie.
- `results.csv` + `summary.csv` + krótki opis wniosków w `README.md` (który język najszybszy, skalowanie z liczbą wątków, narzut startu .NET).
