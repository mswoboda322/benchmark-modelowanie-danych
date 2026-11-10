# Badanie: C++ vs C# vs Java – pamięć, wielowątkowość, aplikacja okienkowa

Porównanie trzech języków według trzech kryteriów pracy komputera z programem
(szczegóły: [docs/01-zakres-i-kryteria.md](docs/01-zakres-i-kryteria.md), plan i metodyka:
[docs/02-plan-badania.md](docs/02-plan-badania.md), instrukcja uruchomienia i odczytu wyników:
[docs/03-instrukcja.md](docs/03-instrukcja.md)):

| | Kryterium | Zadania |
|---|---|---|
| **K1** | Efektywność pracy z pamięcią | `mem-access` (dostęp sekwencyjny / losowy, 16 KB – 256 MB), `matmul` (kolejność pętli `ijk` / `ikj`), `objects` (struktury vs obiekty, drzewa binarne – alokacja i GC) |
| **K2** | Wykorzystanie wielu rdzeni | `mandelbrot` (podział statyczny / dynamiczny), `sync` (mutex, atomic, false sharing), `queue` (producent–konsument) |
| **K3** | Aplikacja okienkowa | `startup` (czas do pierwszego okna, pamięć), `fill` (lista 10⁴ / 10⁵ elementów), `draw` (klatki/s), `responsive` (opóźnienie zdarzeń przy obliczeniach) |

## Struktura

```
Benchmark.sln
├─ BenchCpp/   main.cpp            K1 + K2, C++17 (MSVC)
├─ BenchCs/    Program.cs          K1 + K2, C# (.NET 10)
├─ BenchJava/  src/BenchJava.java  K1 + K2, Java (JDK 25)        → bin\BenchJava.jar
├─ GuiCpp/     main.cpp            K3, Win32 API
├─ GuiCs/      Program.cs          K3, WinForms (.NET 10)
├─ GuiJava/    src/GuiJava.java    K3, Swing (JDK 25)            → bin\GuiJava.jar
├─ Runner/                         C# + Spectre.Console – uruchamia wszystko, mierzy procesy, zapisuje CSV
├─ Java.targets                    kompilacja Javy (javac + jar) w ramach budowania Runnera
├─ build.ps1                       budowanie całego rozwiązania (MSBuild, Release x64)
├─ bin/                            programy po zbudowaniu + foldery wyniki_RRRRMMDD_GGMMSS
├─ docs/                           zakres, kryteria, plan badania (+ archiwum v1)
└─ wyniki/archiwum-v1/             wyniki poprzedniej wersji (C / C++ / C#, matmul / primes / fibonacci)
```

## Wymagania

- Visual Studio 2026 z obciążeniem **„Programowanie aplikacji klasycznych w języku C++”**
  (zestaw narzędzi MSVC v145 + Windows SDK) oraz **.NET 10 SDK**,
- **JDK 25** (`JAVA_HOME` albo `C:\Program Files\Java\jdk-25`),
- opcjonalnie do zimnego startu GUI: [RAMMap](https://learn.microsoft.com/sysinternals/downloads/rammap)
  (`RAMMap64.exe` w `bin\` lub w `PATH`) i uprawnienia administratora.

## Budowanie i uruchomienie

```powershell
.\build.ps1                      # lub w Visual Studio: Release | x64 → Build Solution

.\bin\Runner.exe --quick         # przebieg próbny (kilka minut) – sprawdzenie, czy wszystko działa
.\bin\Runner.exe                 # pełne badanie K1 + K2 + K3
.\bin\Runner.exe K2 K3           # wybrane kryteria
.\bin\Runner.exe K3 --cold       # + zimny start GUI (jako administrator, wymaga RAMMap64.exe)
.\bin\Runner.exe --seed 7        # inne ziarno losowania kolejności języków
```

W czasie K3 na ekranie pojawiają się okna aplikacji – nie należy wtedy używać komputera.

Pojedyncze programy można uruchamiać ręcznie, np.:

```
bin\BenchCpp.exe matmul ikj 512 1 --iter 10 --warmup 3 --flush on
java -jar bin\BenchJava.jar sync local-padded 10000000 8
bin\GuiCs.exe --bench responsive --variant worker --param 5000 --out wynik.csv
```

## Wyniki

Runner zapisuje w `bin\wyniki_RRRRMMDD_GGMMSS\` (separator `;`, przecinek dziesiętny, UTF-8 z BOM –
pliki otwierają się bezpośrednio w polskim Excelu):

| Plik | Zawartość |
|---|---|
| `K1_iteracje.csv`, `K2_iteracje.csv` | każda iteracja: czas, wynik kontrolny, czas CPU, liczba i pauzy GC |
| `K1_procesy.csv`, `K2_procesy.csv` | każdy proces: czas, szczytowy zestaw roboczy, szczytowa pamięć prywatna, czas CPU |
| `K1_podsumowanie.csv`, `K2_podsumowanie.csv` | mediana, średnia, odch. std, 95% CI, metryka zadania, przyspieszenie, czas względem C++ |
| `K3_pomiary.csv`, `K3_procesy.csv`, `K3_podsumowanie.csv` | surowe wartości z aplikacji GUI, dane procesów, statystyki |

Wykresy i tabele do dokumentacji:

```powershell
node analizaanaliza.mjs binwyniki_RRRRMMDD_GGMMSS   # → binwyniki_…analizawykres_*.svg + tabele.md
```

Wyniki pełnego badania z 2026-11-10 i ich omówienie: [docs/04-wyniki.md](docs/04-wyniki.md)
(dane w [wyniki/2026-11-10/](wyniki/2026-11-10/)).
