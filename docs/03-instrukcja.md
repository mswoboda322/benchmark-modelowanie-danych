# Instrukcja: budowanie, testowanie, uruchomienie i odczyt wyników

Dokument opisuje krok po kroku, jak przygotować komputer, sprawdzić, że programy działają,
przeprowadzić pełne badanie i odczytać wyniki. Kryteria i metodyka: [01-zakres-i-kryteria.md](01-zakres-i-kryteria.md),
[02-plan-badania.md](02-plan-badania.md).

Wszystkie polecenia uruchamiamy w **PowerShell** w głównym folderze repozytorium.

---

## 1. Wymagania i sprawdzenie środowiska

| Składnik | Po co | Jak sprawdzić |
|---|---|---|
| Visual Studio 2026 z obciążeniem **„Programowanie aplikacji klasycznych w języku C++”** (MSVC v145 + Windows SDK) | kompilacja `BenchCpp` i `GuiCpp` | Visual Studio Installer → Modyfikuj → obciążenie zaznaczone |
| **.NET 10 SDK** | `BenchCs`, `GuiCs`, `Runner` | `dotnet --version` → `10.x` |
| **JDK 25** | `BenchJava`, `GuiJava` | `& "$env:ProgramFiles\Java\jdk-25\bin\java.exe" -version` |
| RAMMap64.exe (Sysinternals) – opcjonalnie | tylko zimny start GUI (`--cold`) | plik w `bin\` lub w `PATH` |

Uwaga do Javy: Runner sam szuka JDK (`JAVA_HOME` → najnowszy `C:\Program Files\Java\jdk*` → `PATH`).
Celowo pomija `C:\Program Files\Common Files\Oracle\Java\javapath\java.exe` – to tylko pośrednik
uruchamiający drugi proces, przez co pomiar pamięci i czasu CPU dotyczyłby niewłaściwego procesu.

## 2. Budowanie

```powershell
powershell -NoProfile -File .\build.ps1
```

albo w Visual Studio: konfiguracja **Release | x64** → *Build Solution*. Budowanie Runnera kompiluje
też Javę (`Java.targets`: `javac` + `jar`).

Po zbudowaniu w `bin\` powinny być:

```
BenchCpp.exe   GuiCpp.exe            ← C++
BenchCs.exe    GuiCs.exe  (+ .dll)   ← C#
BenchJava.jar  GuiJava.jar           ← Java
Runner.exe                           ← program sterujący
```

Brak któregoś pliku nie przerywa badania – Runner wypisze żółte ostrzeżenie
`⚠ Brak programu … – pominięty` i zbada pozostałe języki.

Typowy błąd: `MSB8020: nie można odnaleźć narzędzi kompilacji dla v145` → brakuje obciążenia C++ w Visual Studio (pkt 1).

## 3. Testowanie – czy wszystko działa

Testujemy w trzech krokach: od pojedynczego programu, przez krótki przebieg, do pełnego badania.

### 3.1. Ręczne uruchomienie pojedynczych programów (1–2 min)

Programy konsolowe przyjmują: `<zadanie> <wariant> <rozmiar> <wątki> [--iter n] [--warmup n] [--flush on|off]`.

```powershell
.\bin\BenchCpp.exe matmul ikj 256 1 --iter 2 --warmup 1
.\bin\BenchCs.exe  matmul ikj 256 1 --iter 2 --warmup 1
& "$env:ProgramFiles\Java\jdk-25\bin\java.exe" -jar .\bin\BenchJava.jar matmul ikj 256 1 --iter 2 --warmup 1
```

Każdy program wypisuje jedną linię na iterację:

```
język;zadanie;wariant;rozmiar;wątki;iteracja;rozgrzewka;czas_ns;wynik;cpu_ms;gc_liczba;gc_pauza_ms
C#;matmul;ikj;256;1;1;1;12479000;274362360;15.625;0;0.000    ← iteracja 1, rozgrzewka (1)
C#;matmul;ikj;256;1;2;0;12351700;274362360;15.625;0;0.000    ← iteracja 2, pomiar (0)
```

Czas jest w nanosekundach (`12479000` ns = 12,5 ms), liczby z kropką dziesiętną (Runner zamienia je na przecinek w CSV).

**Co sprawdzić:** kolumna `wynik` (wynik kontrolny) musi być **identyczna we wszystkich trzech językach**.
To dowód, że wszystkie programy liczą dokładnie to samo (wspólny generator SplitMix64, te same dane).

| Zadanie | Wariant | Rozmiar oznacza | Przykład |
|---|---|---|---|
| `mem-access` | `seq`, `rand` | rozmiar tablicy w **KB** | `mem-access rand 65536 1` |
| `matmul` | `ijk`, `ikj` | N (macierz N×N) | `matmul ijk 512 1` |
| `objects` | `points-value`, `points-ref`, `trees` | liczba punktów / głębokość drzewa | `objects trees 16 1` |
| `mandelbrot` | `static`, `dynamic` | bok obrazu w pikselach | `mandelbrot dynamic 2000 8` |
| `sync` | `mutex`, `atomic`, `local-shared`, `local-padded` | liczba operacji łącznie | `sync local-padded 10000000 4` |
| `queue` | `blocking` | liczba komunikatów (wątki ≥ 2) | `queue blocking 1000000 4` |

Programy okienkowe (otworzy się okno i samo się zamknie):

```powershell
.\bin\GuiCs.exe --bench fill --param 10000 --out test.csv
Get-Content test.csv
```

### 3.2. Przebieg próbny `--quick` (kilka minut)

```powershell
.\bin\Runner.exe --quick
```

Mniejsze rozmiary, 1 rozgrzewka + 3 pomiary. **Wyniki z `--quick` nie nadają się do dokumentacji** –
służą tylko do sprawdzenia działania. Lista kontrolna:

- [ ] w panelu „Parametry” są wszystkie trzy języki: `C++, C#, Java`, a Java ma poprawną ścieżkę JDK,
- [ ] brak czerwonych linii `✗ … kod wyjścia …` / `niepoprawna linia` / `przekroczono limit czasu`,
- [ ] brak żółtego `⚠` przy wartościach w tabelach (niezgodny wynik kontrolny),
- [ ] okna GUI pojawiają się i same zamykają (K3),
- [ ] na końcu: `Wyniki zapisano w: …\bin\wyniki_RRRRMMDD_GGMMSS` i 9 plików CSV w tym folderze.

Można też testować jedno kryterium: `.\bin\Runner.exe K2 --quick`.

## 4. Pełne badanie

### 4.1. Przygotowanie komputera

Pomiary są wrażliwe na wszystko, co dzieje się w tle. Przed uruchomieniem:

1. Podłączyć zasilanie (laptop), plan zasilania **„Najwyższa wydajność”**, wyłączyć uśpienie i wygaszacz.
2. Zamknąć przeglądarkę, komunikatory, IDE (także Visual Studio), OneDrive/synchronizację.
3. Odczekać kilka minut po starcie systemu (aktualizacje, indeksowanie). Nie uruchamiać w trakcie Windows Update.
4. Ten sam komputer, te same ustawienia dla wszystkich przebiegów, które będą porównywane.
5. **Nie używać komputera w trakcie** – w K3 pojawiają się okna, a ruch myszy czy klawiatura zaburzają pomiar responsywności.

### 4.2. Uruchomienie

```powershell
.\bin\Runner.exe                       # K1 + K2 + K3, szacunkowo 30–40 min
.\bin\Runner.exe | Tee-Object -FilePath log.txt   # to samo + zapis konsoli do pliku (bez kolorów)
```

Inne warianty:

```powershell
.\bin\Runner.exe K1                    # tylko wybrane kryterium (K1, K2, K3 – dowolna kombinacja)
.\bin\Runner.exe --seed 7              # inna losowa kolejność języków (kontrola powtarzalności)
.\bin\Runner.exe K3 --cold             # + zimny start GUI: PowerShell „Uruchom jako administrator”, RAMMap64.exe w bin\
```

Zalecane: badanie wykonać **2 razy** (np. z różnym `--seed`). Jeśli wnioski z obu przebiegów są
takie same, wyniki są wiarygodne. Różnice dużo większe niż przedział ufności oznaczają zakłócenia w tle.

### 4.3. Co się dzieje w trakcie

- Każda konfiguracja (np. `matmul | ijk | N=512 | flush`) to **osobny proces** dla każdego języka,
  języki w losowej kolejności (nagrzewanie procesora nie faworyzuje żadnego).
- W procesie: **3 iteracje rozgrzewkowe** (odrzucane – JIT, ładowanie klas) + **10 pomiarów**.
  Przed każdą iteracją: GC (C#, Java) i czyszczenie cache (zapis/odczyt bufora 128 MB), chyba że `bez flush`.
- Pasek postępu pokazuje bieżące zadanie i język. Tabele pojawiają się po zakończeniu każdego kryterium.

## 5. Jak czytać wyniki w konsoli

### 5.1. Budowa tabel

```
╭──────────────── Z1.2 Mnożenie macierzy – GFLOP/s ────────────────╮
│ Konfiguracja           │    C++ │     C# │   Java │
│ ijk | N=512 | flush    │   0,95 │   0,80 │   0,90 │
│ ikj | N=512 | flush    │   4,10 │   2,30 │   3,80 │
```

*(liczby przykładowe)*

- **wiersz** = konfiguracja (wariant | rozmiar | liczba wątków | flush), **kolumna** = język,
- tytuł = zadanie + metryka; dla jednego zadania może być kilka tabel (różne metryki),
- **zielony** = najlepszy język w wierszu, **czerwony** = najgorszy (dla metryk bez „lepiej/gorzej”,
  np. liczba GC, bez kolorów),
- **⚠** = wynik kontrolny inny niż w pozostałych językach → ten pomiar jest podejrzany (błąd w programie),
- **—** = brak wartości (język pominięty albo błąd procesu).

Wszystkie czasy to **mediany z 10 pomiarów** (mediana jest odporna na pojedyncze zakłócenia).

### 5.2. K1 – efektywność pracy z pamięcią

| Tabela | Metryka | Lepiej | Jak interpretować |
|---|---|---|---|
| **Z1.1 Dostęp do pamięci** | ns/dostęp | mniej | Wiersze od 16 KB do 256 MB przechodzą przez kolejne poziomy pamięci i5-13500: **16 KB → L1**, 128 KB – 1 MB → L2, 4–16 MB → L3, **64–256 MB → RAM**. Dla `rand` czas powinien rosnąć skokowo na granicach (L1 ≈ 1 ns, RAM ≈ 80–100 ns) – to „górka pamięci”. Dla `seq` rośnie słabo, bo prefetcher przewiduje adresy. Porównanie języków: w cache widać narzut języka (sprawdzanie zakresów, wektoryzacja), w RAM czasy powinny się wyrównać (H1). `flush` vs `bez flush` pokazuje, ile daje „ciepły” cache dla małych tablic. |
| **Z1.2 Mnożenie macierzy** | GFLOP/s (więcej = lepiej), mediana [ms] | więcej GFLOP/s | Kluczowe porównanie: `ijk` vs `ikj` w tym samym języku. `ikj` czyta pamięć sekwencyjnie i zwykle jest **kilka razy szybsze** – wzorzec dostępu ważniejszy niż język (H1). |
| **Z1.3 Obiekty w pamięci** | mediana [ms], 1. iteracja, szczyt pamięci [MB], liczba GC, pauzy GC | mniej | `points-value` vs `points-ref`: w C++/C# `value` to ciągła tablica struktur, w Javie (brak typów wartościowych) zastępuje ją tablica `double[]`; `ref` to tablica wskaźników do obiektów → więcej chybień cache i pamięci. `trees` (głębokość d): alokacja milionów małych obiektów – GC (C#, Java) vs `new`/`delete` (C++); patrz też szczyt pamięci i pauzy GC. „1. iteracja” = czas przed rozgrzaniem JIT. |

Na końcu **„K1 – podsumowanie”**: dla każdego zadania średnia geometryczna `czas języka / czas C++`
ze wszystkich konfiguracji. **C++ = 1,00**; np. `1,35` = średnio 35% wolniej niż C++, `0,90` = 10% szybciej.
(Tabela pojawia się tylko, gdy C++ był badany.)

### 5.3. K2 – wykorzystanie wielu rdzeni

| Tabela | Metryka | Lepiej | Jak interpretować |
|---|---|---|---|
| **Z2.1 Mandelbrot – skalowanie** | mediana [ms] | mniej | czas obliczenia obrazu 2000² dla 1, 2, 4, 6, 8, 14, 20 wątków |
| | przyspieszenie T(1)/T(n) | więcej | ideał = n. Na i5-13500 do 6 wątków (rdzenie P) wzrost prawie liniowy, potem wolniej (rdzenie E są słabsze, HT dzieli rdzeń). |
| | efektywność S/n | więcej (maks. 1) | ile „wart” jest każdy dodatkowy wątek |
| | czas CPU / czas rzeczywisty | – | ≈ liczba faktycznie pracujących rdzeni; wyraźnie mniej niż n → wątki czekają |
| | `static` vs `dynamic` | | `static` = równe bloki wierszy → nierówna praca (środek zbioru liczy się dłużej), `dynamic` = wątki pobierają wiersze na bieżąco → lepsze skalowanie. |
| **Z2.2 Koszt synchronizacji** | ns/operację | mniej | `mutex` (blokada) vs `atomic` (jedna wspólna zmienna atomowa) vs `local-shared` (osobne liczniki w sąsiednich komórkach – *false sharing*) vs `local-padded` (liczniki w osobnych liniach cache 64 B). Oczekiwane: przy ≥ 2 wątkach `local-shared` ≈ `atomic` (wiele ns), `local-padded` ≈ 1 ns i skaluje się. Różnice między językami głównie w `mutex` (H2). |
| **Z2.3 Producent–konsument** | mln komunikatów/s | więcej | przepustowość ograniczonej kolejki (1024) – 1 producent + (n−1) konsumentów. Pokazuje koszt kolejek bibliotecznych (Java: dodatkowo pakowanie `int` → `Integer`). |

### 5.4. K3 – aplikacja okienkowa

| Tabela | Metryka | Lepiej | Jak interpretować |
|---|---|---|---|
| **Z3.1 Start aplikacji** | czas do pierwszego okna – mediana [ms] (± 95% CI) | mniej | od uruchomienia procesu do pierwszego narysowania okna. *Ciepły start* = pliki w pamięci systemu, *zimny* (`--cold`) = po wyczyszczeniu pamięci podręcznej plików (doczytanie z dysku, start .NET/JVM). |
| | szczytowy zestaw roboczy [MB], szczytowa pamięć prywatna [MB] | mniej | RAM zajęty przez aplikację (WS = fizycznie w RAM, prywatna = zarezerwowana tylko dla procesu, np. sterta JVM) |
| | liczba wątków procesu, rozmiar programu [KB] | mniej | ile wątków tworzy środowisko (GC, JIT…); rozmiar plików programu **bez** środowiska (.NET, JRE) |
| **Z3.2 Wypełnienie listy** | mediana [ms], 1. iteracja [ms] | mniej | dodanie 10⁴ / 10⁵ elementów do kontrolki listy i odrysowanie |
| **Z3.2 Rysowanie obrazu** | klatki/s | więcej | 100 klatek obrazu 800×800 wyliczonego w pamięci i przeniesionego na ekran |
| **Z3.3 Responsywność** | średnie / p95 / maks. opóźnienie zdarzeń [ms] | mniej | co 10 ms do okna wysyłane jest zdarzenie; mierzymy, po jakim czasie zostaje obsłużone, gdy aplikacja liczy w tle (5 s). `idle` = punkt odniesienia (≈ 0–1 ms), `ui-thread` = obliczenia w wątku interfejsu → okno „zamarza” (setki ms), `worker` = obliczenia w wątku roboczym → opóźnienia wracają do poziomu `idle` (H3). |
| | zdarzenia opóźnione > 100 ms | mniej | liczba „zamrożeń” odczuwalnych dla użytkownika |
| | obliczone obrazy | więcej | ile pracy wykonano w tym czasie (przepustowość) |

## 6. Pliki wyników (CSV)

Folder `bin\wyniki_RRRRMMDD_GGMMSS\`. Pliki mają separator `;`, przecinek dziesiętny i kodowanie
UTF-8 z BOM – otwierają się dwuklikiem w polskim Excelu (najlepiej: *Wstaw → Tabela*, potem filtry / tabela przestawna).

### 6.1. K1 i K2

| Plik | Wiersz = | Najważniejsze kolumny |
|---|---|---|
| `K1_podsumowanie.csv`, `K2_podsumowanie.csv` | konfiguracja × język | **do dokumentacji** – patrz niżej |
| `K1_iteracje.csv`, `K2_iteracje.csv` | jedna iteracja | `Iteracja`, `Rozgrzewka` (1 = odrzucona), `Czas_ms`, `Wynik`, `CPU_ms`, `GC_liczba`, `GC_pauza_ms` – do wykresów rozrzutu i analizy rozgrzewki JIT |
| `K1_procesy.csv`, `K2_procesy.csv` | jeden proces | `Kolejność` (wylosowana), `Czas_procesu_ms` (cały proces z rozgrzewką), `Szczyt_WS_MB`, `Szczyt_prywatna_MB`, `CPU_procesu_ms`, `Kod_wyjścia`, `Błąd` |

Kolumny `*_podsumowanie.csv`:

| Kolumna | Znaczenie |
|---|---|
| `Kryterium`, `Zadanie`, `Wariant`, `Rozmiar`, `Wątki`, `Flush` | konfiguracja (`Rozmiar`: KB / N / liczba punktów / głębokość / bok obrazu / liczba operacji – jak w tabeli w pkt 3.1; `Flush`: 1 = czyszczenie cache) |
| `Liczba_pomiarów` | ile iteracji pomiarowych (powinno być 10) |
| `Mediana_ms`, `Średnia_ms`, `Odch_std_ms`, `Min_ms` | statystyki czasu iteracji |
| `CI95_ms` | połowa szerokości 95% przedziału ufności średniej (t-Studenta): średnia ± CI95 |
| `Pierwsza_iteracja_ms` | pierwsza iteracja (rozgrzewka) – koszt „zimnego” JIT |
| `Metryka`, `Wartość_metryki` | główna metryka zadania (ns/dostęp, GFLOP/s, ns/operację, mln komunikatów/s lub mediana [ms]) |
| `Przyspieszenie` | T(najmniej wątków) / T(n) dla tego samego języka |
| `Czas_względem_C++` | mediana języka / mediana C++ (1 = jak C++, > 1 = wolniej) |
| `CPU_do_czasu` | czas CPU / czas rzeczywisty (≈ liczba zajętych rdzeni) |
| `GC_liczba`, `GC_pauza_ms` | średnio na iterację (C++: 0) |
| `Szczyt_WS_MB`, `Szczyt_prywatna_MB`, `Czas_procesu_ms` | z pomiaru procesu przez Runner (pamięć zawiera bufor 128 MB do czyszczenia cache, jeśli `Flush` = 1) |
| `Wynik`, `Wynik_niezgodny` | wynik kontrolny; `1` = inny niż u pozostałych języków → pomiar do sprawdzenia |

### 6.2. K3

| Plik | Zawartość |
|---|---|
| `K3_podsumowanie.csv` | format „długi”: `Scenariusz;Wariant;Parametr;Język;Metryka;Wartość` – metryki jak w tabelach z pkt 5.4 (`start_mediana_ms`, `start_ci95_ms`, `szczyt_ws_mb`, `klatki_na_s`, `opoznienie_p95_ms`, `zamrozenia_powyzej_100ms` …). W Excelu: tabela przestawna, wiersze = Scenariusz + Wariant + Metryka, kolumny = Język. |
| `K3_procesy.csv` | każde uruchomienie aplikacji: `Czas_startu_ms`, pamięć, `CPU_ms`, `Wątki_max`, `Rozgrzewka` (1 = uruchomienie rozgrzewkowe, nieliczone), `Zimny_start` |
| `K3_pomiary.csv` | surowe wartości z aplikacji: `time_ns` (fill/draw), `latency_ns` (każde zdarzenie w responsive – do histogramu opóźnień), `first_paint_ns`, `images` |

## 7. Ocena wiarygodności wyników

- **Przedział ufności:** jeśli przedziały `średnia ± CI95` dwóch języków się nakładają, nie twierdzimy, że któryś jest szybszy.
- **Rozrzut:** `Odch_std_ms` / `Średnia_ms` powyżej ok. 5–10% oznacza zakłócenia w tle – warto powtórzyć tę konfigurację.
- **Wyniki kontrolne:** każdy `⚠` / `Wynik_niezgodny = 1` trzeba wyjaśnić przed wyciąganiem wniosków.
- **Powtórzenie całego badania** z innym `--seed` (pkt 4.2) – wnioski powinny się nie zmienić.
- **Rozgrzewka:** porównanie `Pierwsza_iteracja_ms` z `Mediana_ms` pokazuje koszt JIT w C#/Javie (w C++ powinny być zbliżone).

## 8. Najczęstsze problemy

| Objaw | Przyczyna / rozwiązanie |
|---|---|
| `⚠ Brak programu konsolowego dla C++` | nie zbudowano `BenchCpp` – zainstalować obciążenie C++ (pkt 1) i uruchomić `build.ps1` |
| `⚠ Nie znaleziono JDK` | ustawić `JAVA_HOME` na folder JDK 25 (np. `C:\Program Files\Java\jdk-25`) |
| `build.ps1` nie uruchamia się (zasady wykonywania) | uruchomić przez `powershell -NoProfile -File .\build.ps1` |
| `--cold pominięte` | PowerShell uruchomiony jako administrator + `RAMMap64.exe` w `bin\` |
| `przekroczono limit czasu` / `kod wyjścia …` | uruchomić tę konfigurację ręcznie (pkt 3.1) i odczytać komunikat błędu |
| bardzo duży rozrzut, „dziwne” pojedyncze wyniki | procesy w tle, tryb oszczędzania energii, przegrzanie – patrz pkt 4.1 i powtórzyć |
| Excel pokazuje wszystko w jednej kolumnie | otworzyć dwuklikiem (nie przez import) lub w imporcie wybrać separator `;` i UTF-8 |

## 9. Co przenieść do dokumentacji badania

1. Panel „Parametry” z konsoli (sprzęt, wersje .NET/Java, data) – opis stanowiska.
2. Z `*_podsumowanie.csv`: mediana ± CI95 lub metryka zadania dla kluczowych konfiguracji, np.:
   - K1: wykres ns/dostęp vs rozmiar (oś X logarytmiczna, linia na język, osobno `seq`/`rand`), GFLOP/s `ijk` vs `ikj`, czas/pamięć/GC dla `objects`;
   - K2: wykres przyspieszenia vs liczba wątków (+ linia idealna), słupki ns/operację dla `sync` przy 1 i 6 wątkach, mln komunikatów/s;
   - K3: słupki czasu startu i pamięci, klatki/s, opóźnienia p95 dla `idle` / `ui-thread` / `worker`.
3. Tabele „K1/K2 – podsumowanie” (czas względem C++) jako zbiorcze porównanie.
4. Weryfikację hipotez H1–H3 z [01-zakres-i-kryteria.md](01-zakres-i-kryteria.md) – potwierdzona / odrzucona, z liczbami.
