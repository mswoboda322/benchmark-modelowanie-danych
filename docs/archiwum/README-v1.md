# Badanie wydajności: C vs C++ vs C#

Porównanie czasu wykonania trzech zadań obliczeniowych zaimplementowanych identycznym algorytmem
w C (OpenMP), C++17 (`std::thread`) i C# (.NET 10, `Parallel.For`), dla 1, 2, 4, 8 i 14 wątków.
Szczegółowe założenia: [plan-badania.md](plan-badania.md).

## Struktura

```
Benchmark.sln
├─ BenchC/      main.c        C, OpenMP (/TC /openmp)
├─ BenchCpp/    main.cpp      C++17, std::thread
├─ BenchCs/     Program.cs    C# .NET 10, Parallel.For
├─ Runner/      Program.cs    C# .NET 10 + Spectre.Console – uruchamia i porównuje
├─ bin/         ← wszystkie programy po zbudowaniu (+ results.csv, summary.csv)
└─ wyniki/      ← kopia wyników pomiaru opisanego niżej (CSV + log konsoli)
```

| Zadanie | Opis | Podział pracy | Wynik kontrolny |
|---|---|---|---|
| `matmul` | mnożenie macierzy N×N (double), pętla i-k-j | wiersze wyniku (C, C++: ciągłe bloki; C#: `Parallel.For` po wierszach) | suma elementów C |
| `primes` | liczby pierwsze w [2, N], dzielenie próbne do √n | porcje po 10 000 liczb pobierane dynamicznie | liczba liczb pierwszych |
| `fibonacci` | naiwna rekurencja | drzewo rozwinięte do głębokości 5 → 32 podzadania `fib(N-k)` pobierane dynamicznie | `fib(N)` |

## Budowanie i uruchomienie

W Visual Studio 2026: otwórz `Benchmark.sln`, konfiguracja **Release | x64**, *Build → Build Solution*,
następnie uruchom `bin\Runner.exe` (bez debuggera, Ctrl+F5 z projektem Runner jako startowym).

Z linii poleceń:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe" `
    Benchmark.sln /restore /p:Configuration=Release /p:Platform=x64
.\bin\Runner.exe                     # domyślnie: 5 pomiarów, N = 1000 / 10 000 000 / 40
.\bin\Runner.exe 10 1500 20000000 42 # [powtórzenia] [N_matmul] [N_primes] [N_fibonacci]
```

Pojedynczy program można też wywołać ręcznie: `bin\BenchCpp.exe matmul 1000 8` →
`C++;matmul;1000;8;16425000000;147.912`.

Runner zapisuje `results.csv` (wszystkie surowe pomiary), `summary.csv` (statystyki) oraz kopie
z datą `results_RRRRMMDD_HHMMSS.csv` / `summary_…csv` w folderze `bin\`. Pliki mają separator `;`,
przecinek dziesiętny i UTF-8 z BOM – otwierają się bezpośrednio w polskim Excelu.

## Wyniki

Stanowisko: Intel Core Ultra 5 235U (12 rdzeni: 2 P z HT + 8 E + 2 LP-E, 14 wątków logicznych),
Windows 11 (26200), MSVC 14.51 (v145) `/O2`, .NET 10.0.12. Laptop na zasilaniu sieciowym,
11.10.2026, 1 przebieg rozgrzewkowy + 5 pomiarów na kombinację. Pełne dane: [wyniki/](wyniki/).

Średni czas obliczeń [ms] (mierzony wewnątrz programu) i przyspieszenie T(1)/T(n):

| Zadanie | Wątki | C | C++ | C# | Przysp. C | Przysp. C++ | Przysp. C# |
|---|---:|---:|---:|---:|---:|---:|---:|
| matmul (N=1000) | 1 | 1 086,3 | **943,5** | 1 243,2 | 1,00 | 1,00 | 1,00 |
| | 2 | **454,5** | 492,2 | 597,4 | 2,39 | 1,92 | 2,08 |
| | 4 | **258,2** | 268,9 | 323,6 | 4,21 | 3,51 | 3,84 |
| | 8 | **147,1** | 147,9 | 195,7 | 7,38 | 6,38 | 6,35 |
| | 14 | 147,0 | **133,6** | 160,8 | 7,39 | 7,06 | 7,73 |
| primes (N=10⁷) | 1 | 2 414,7 | 2 383,7 | **2 316,8** | 1,00 | 1,00 | 1,00 |
| | 2 | **1 035,6** | 1 068,7 | 1 090,5 | 2,33 | 2,23 | 2,12 |
| | 4 | 514,3 | **491,1** | 598,9 | 4,69 | 4,85 | 3,87 |
| | 8 | 294,8 | **263,8** | 358,0 | 8,19 | 9,03 | 6,47 |
| | 14 | 212,6 | **202,7** | 309,2 | 11,36 | 11,76 | 7,49 |
| fibonacci (N=40) | 1 | **355,0** | 368,7 | 375,9 | 1,00 | 1,00 | 1,00 |
| | 2 | 174,0 | **166,1** | 283,3 | 2,04 | 2,22 | 1,33 |
| | 4 | 103,8 | **103,0** | 190,0 | 3,42 | 3,58 | 1,98 |
| | 8 | **62,3** | 70,0 | 128,7 | 5,69 | 5,26 | 2,92 |
| | 14 | **50,8** | 51,7 | 99,3 | 6,98 | 7,12 | 3,79 |

Wyniki kontrolne były identyczne we wszystkich językach i konfiguracjach
(16 425 000 000; 664 579; 102 334 155).

Średni narzut startu procesu (czas procesu mierzony przez Runner minus czas obliczeń):
**C ≈ 132 ms, C++ ≈ 141 ms, C# ≈ 351 ms** (mediany: 125 / 136 / 312 ms).

## Wnioski

1. **Na jednym wątku języki są praktycznie równorzędne.** Różnice w `primes` i `fibonacci`
   (≤ 6%) mieszczą się w rozrzucie pomiarów (odch. std do ~200 ms przy ~2,3 s). W `matmul`
   C++ był najszybszy (C o ~15%, C# o ~32% wolniejszy); C# prawdopodobnie płaci tu za
   sprawdzanie zakresów tablic w najgłębszej pętli. Kompilator JIT .NET 10 z TieredPGO generuje kod porównywalny z MSVC `/O2` –
   dla rekurencyjnego `fib` wyłączenie PGO (`DOTNET_TieredPGO=0`) wydłużało czas z ~360 do ~470 ms.

2. **Przy wielu wątkach najszybsze są C i C++, C# odstaje.** Dla 14 wątków C/C++ wygrywają
   wszystkie zadania; C# jest wolniejszy o ~20% (`matmul`), ~50% (`primes`) i ~95% (`fibonacci`).
   Część różnicy to stały koszt pierwszego `Parallel.For` w świeżym procesie: dla trywialnej pracy
   (`primes 2`) C# potrzebuje ~25–30 ms (inicjalizacja puli wątków, JIT kodu TPL), C – 0,01–2 ms,
   C++ – 1–3 ms. Im krótsze obliczenia, tym większy udział tego narzutu – dlatego najsłabiej
   wypada `fibonacci` (~50–100 ms obliczeń). Dodatkowo `Parallel.For` dzieli zakres indeksów
   na porcje po swojemu, co przy 32 bardzo nierównych podzadaniach daje gorsze zrównoważenie
   niż przydział „po jednym” (`schedule(dynamic, 1)` / `std::atomic`) – to hipoteza, nie
   weryfikowana osobnym pomiarem.

3. **Skalowanie zależy od zadania i od architektury procesora.**
   - `primes` (czyste obliczenia na liczbach całkowitych, brak współdzielonych danych) skaluje się
     najlepiej: ~11,5× na 14 wątkach w C/C++.
   - `matmul` przestaje przyspieszać powyżej 8 wątków (C: 147 ms dla 8 i 14) – ograniczeniem staje
     się przepustowość pamięci/cache, a dodatkowe wątki trafiają na słabsze rdzenie E/LP-E.
   - Przyspieszenia większe niż liczba wątków (np. 2,39× dla 2 wątków) wynikają z hybrydowej
     architektury: pojedynczy wątek nie zawsze trafia na rdzeń P i nie zawsze działa z maks. zegarem
     turbo (widać to w większym odchyleniu std. dla 1 wątku). Ten procesor ma tylko 2 rdzenie P,
     więc przyspieszenie liniowe do 14× nie jest osiągalne.
   - Na jednym wątku OpenMP ma narzut bliski zeru, `std::thread` tworzy dodatkowy wątek (~1–2 ms).

4. **Narzut startu .NET jest istotny dla krótkich programów.** Proces C# startuje średnio
   ~350 ms vs ~130–140 ms dla programów natywnych (start runtime, ładowanie bibliotek, JIT).
   W czasie całego procesu C# przegrywa więc we wszystkich konfiguracjach, mimo że sam czas
   obliczeń jednowątkowych jest porównywalny. Wysoki narzut natywnych exe (>100 ms) to
   prawdopodobnie skanowanie uruchamianych plików przez oprogramowanie antywirusowe.

**Podsumowanie:** dla długich obliczeń jednowątkowych wybór języka ma niewielkie znaczenie;
przy obliczeniach równoległych o krótkim czasie trwania C i C++ (OpenMP / `std::thread`) mają
przewagę dzięki niskiemu narzutowi uruchamiania wątków. OpenMP daje ten sam efekt co ręczne
`std::thread` przy znacznie mniejszej ilości kodu.

## Ograniczenia pomiaru

- Laptop z hybrydowym CPU – wyniki zależą od harmonogramu Windows (Thread Director), temperatury
  i trybu zasilania; 5 powtórzeń daje odchylenie std. rzędu 2–15%.
- Czas C# obejmuje kompilację warstwową JIT i start puli wątków w obrębie mierzonego fragmentu
  (zgodnie z planem nie ma rozgrzewki wewnątrz procesu) – to świadomy wybór: mierzymy program
  uruchamiany raz, tak jak natywne odpowiedniki.
- Zmiana względem planu: .NET 10 zamiast .NET 8 (na stanowisku zainstalowany był tylko .NET 10).
