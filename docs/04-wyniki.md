# Wyniki badania

*2026-11-10. Zakres: [01-zakres-i-kryteria.md](01-zakres-i-kryteria.md), metodyka: [02-plan-badania.md](02-plan-badania.md),
odczyt wyników: [03-instrukcja.md](03-instrukcja.md). Dane: [../wyniki/2026-11-10/](../wyniki/2026-11-10/).*

## 1. Przebiegi

| Przebieg | Zakres | Ziarno | Folder | Uwagi |
|---|---|---|---|---|
| A (główny) | K1 + K2 + K3 | 2026 | `wyniki/2026-11-10/A_K1-K3_seed2026` | 47 min. Z K3 odrzucony scenariusz `fill` – patrz pkt 4.2 |
| B | K3 | 2026 | `wyniki/2026-11-10/B_K3` | powtórzenie K3 – główne źródło wyników K3 |
| C (kontrolny) | K1 + K2 | 7 | `wyniki/2026-11-10/C_K1-K2_seed7` | inna kolejność języków – sprawdzenie powtarzalności |

Stanowisko jak w planie (i5-13500, 16 GB, Windows 11 build 26300), środowiska: MSVC 14.51 (VS 2026, v145),
.NET 10.0.9, Oracle JDK 25.0.1. Pomiary wykonano na komputerze roboczym z programami w tle
(przeglądarka, komunikatory, agent bezpieczeństwa, serwer VNC) – patrz ograniczenia.

**Wyniki kontrolne:** we wszystkich konfiguracjach K1 i K2 identyczne w trzech językach (0 niezgodności).

Wszystkie wartości to **mediany z 10 pomiarów** po 3 iteracjach rozgrzewkowych (K1, K2).
Wykresy: `node analiza\analiza.mjs <folder>` → `wykres_*.svg`, tabele → `tabele.md`.

## 2. K1 – efektywność pracy z pamięcią

### Z1.1 Dostęp do pamięci (ns/dostęp, flush on)

| Rozmiar | C++ seq | C# seq | Java seq | C++ rand | C# rand | Java rand |
|---|---|---|---|---|---|---|
| 16 KB (L1) | 0,09 | 0,31 | 0,06 | 1,37 | 1,40 | 1,13 |
| 128 KB (L2) | 0,09 | 0,30 | 0,06 | 2,75 | 2,92 | 2,44 |
| 1 MB (L2) | 0,09 | 0,31 | 0,09 | 5,20 | 4,46 | 4,31 |
| 4 MB (L3) | 0,11 | 0,31 | 0,10 | 12,57 | 12,25 | 12,57 |
| 16 MB (L3) | 0,24 | 0,34 | 0,15 | 45,24 | 51,11 | 38,46 |
| 64 MB (RAM) | 0,25 | 0,37 | 0,22 | 84,50 | 79,10 | 105,11 |
| 256 MB (RAM) | 0,23 | 0,37 | 0,18 | 109,25 | 93,67 | 111,49 |

- Dostęp losowy (*pointer chasing*) rośnie od ~1 ns (L1) do ~100 ns (RAM) – **80× różnicy** zależnie
  od tego, gdzie leżą dane. Między językami różnice mieszczą się w rozrzucie pomiaru: każdy dostęp czeka
  na poprzedni, więc narzut języka (sprawdzanie zakresów) jest niewidoczny.
- Dostęp sekwencyjny: C++ i Java ~0,06–0,09 ns (kompilator wektoryzuje sumowanie), **C# 0,31 ns – 3–5× wolniej**,
  bo JIT .NET nie wektoryzuje automatycznie tej pętli. W RAM różnica maleje do 1,7×, bo ogranicza przepustowość pamięci.
- Rozrzut dla `rand` ≥ 16 MB wynosił 10–35% (zakłócenia w tle) – różnic poniżej ~20% w tym zakresie nie interpretujemy.

### Z1.2 Mnożenie macierzy (GFLOP/s, flush on)

| N | C++ ijk | C# ijk | Java ijk | C++ ikj | C# ikj | Java ikj |
|---|---|---|---|---|---|---|
| 256 | 3,94 | 2,28 | 3,23 | 3,35 | 2,71 | 6,45 |
| 512 | 0,97 | 0,99 | 0,92 | 3,34 | 2,90 | 6,57 |
| 1024 | 0,89 | 0,92 | 0,90 | 3,45 | 2,92 | 6,83 |

- Kolejność pętli zmienia wydajność **3,2–7,6×** w każdym języku; język – najwyżej 2,3×. Dla N ≥ 512 wariant `ijk`
  (kolumnowy dostęp do B) jest ograniczony przez pamięć i **wszystkie języki dają ~0,9–1,0 GFLOP/s**.
- W `ikj` Java jest **2× szybsza niż C++**: JIT C2 kompiluje pod konkretny procesor (AVX2, 4 liczby `double` naraz),
  a MSVC z domyślnymi flagami generuje kod SSE2 (2 liczby naraz) zgodny z każdym procesorem x64.
  To efekt ustawień domyślnych, nie „szybszego języka” – `/arch:AVX2` w C++ zmieniłby wynik.

### Z1.3 Obiekty w pamięci i alokacja

| Konfiguracja | | C++ | C# | Java |
|---|---|---|---|---|
| 10⁷ punktów – ciągła tablica (`value`*) | czas / szczyt pamięci | 12,9 ms / 237 MB | 11,4 ms / 252 MB | 9,4 ms / 295 MB |
| 10⁷ punktów – tablica wskaźników (`ref`) | czas / szczyt pamięci | 21,1 ms / 392 MB | 19,7 ms / 579 MB | 20,1 ms / 625 MB |
| drzewa binarne d = 16 | czas / szczyt pamięci | 443 ms / 17 MB | 111 ms / 49 MB | 79 ms / 264 MB |
| drzewa binarne d = 18 | czas / szczyt pamięci | 2048 ms / 41 MB | 592 ms / 91 MB | 356 ms / 626 MB |
| drzewa binarne d = 18 | GC na iterację / pauzy | — | 176 / 191 ms | 37 / 50 ms |

\* C++ `vector<Point>`, C# `struct[]`, Java trzy tablice `double[]` (brak typów wartościowych).

- Tablica wskaźników jest ~1,7× wolniejsza od ciągłej we wszystkich językach i zajmuje więcej pamięci:
  w C++ +65%, w C# i Javie ponad 2× (nagłówki obiektów).
- **Alokacja wielu małych obiektów: GC jest 3,5–6× szybszy** niż `new`/`delete` w C++
  (alokacja = przesunięcie wskaźnika, zwalnianie całych regionów naraz), **ale kosztem pamięci**: Java przy d = 18
  zajmuje 15× więcej pamięci niż C++, C# ~2×. C# częściej uruchamia GC (176 vs 37 na iterację) i oszczędza pamięć.

**Czas względem C++ (średnia geometryczna wszystkich konfiguracji, C++ = 1,00):**

| Zadanie | C++ | C# | Java |
|---|---|---|---|
| Z1.1 mem-access | 1,00 | 1,64 | 0,85 |
| Z1.2 matmul | 1,00 | 1,19 | 0,73 |
| Z1.3 objects | 1,00 | 0,42 | 0,32 |

## 3. K2 – wykorzystanie wielu rdzeni

### Z2.1 Mandelbrot 2000² – przyspieszenie T(1)/T(n)

| Wątki | C++ static | C# static | Java static | C++ dynamic | C# dynamic | Java dynamic |
|---|---|---|---|---|---|---|
| 1 | 1,00 (1447 ms) | 1,00 (1399 ms) | 1,00 (1454 ms) | 1,00 (1438 ms) | 1,00 (1411 ms) | 1,00 (1498 ms) |
| 4 | 1,99 | 2,02 | 1,95 | 3,82 | 3,86 | 2,80 |
| 6 | 2,33 | 2,37 | 2,40 | 5,62 | 5,00 | 4,68 |
| 14 | 4,37 | 4,29 | 4,26 | 10,91 | 10,54 | 9,00 |
| 20 | 5,88 | 5,55 | 5,68 | **14,50** | **13,75** | **12,84** |

- Na jednym wątku języki są równe (±4%) – obliczenia zmiennoprzecinkowe kompilują się do podobnego kodu.
- **Sposób podziału pracy ważniejszy niż język**: równe bloki wierszy (`static`) dają tylko 5,6–5,9× na 20 wątkach
  (wątek ze środkiem zbioru liczy najdłużej), dynamiczne pobieranie wierszy – 12,8–14,5×.
- Powyżej 6 wątków (rdzenie P) przyrost słabnie – rdzenie E i Hyper-Threading są wolniejsze.
- Java (`parallel stream` w `ForkJoinPool`) skaluje się nieco gorzej (12,8× vs 14,5×) – strumień dzieli
  zakres na większe porcje niż licznik atomowy w C++ czy `Parallel.For` w C#.

### Z2.2 Koszt synchronizacji (ns/operację)

| Wariant | Wątki | C++ | C# | Java |
|---|---|---|---|---|
| `mutex` | 1 / 6 / 20 | 11,9 / 24,1 / 76,0 | 14,6 / 34,2 / 51,6 | 3,7 / 20,2 / 19,2 |
| `atomic` | 1 / 6 / 20 | 4,0 / 18,1 / 22,4 | 3,9 / 10,9 / 14,4 | 4,1 / 10,9 / 14,6 |
| `local-shared` (*false sharing*) | 1 / 6 / 20 | 3,8 / 10,6 / 7,2 | 3,8 / 11,7 / 7,2 | 3,9 / 11,7 / 7,5 |
| `local-padded` | 1 / 6 / 20 | 3,9 / 0,76 / 0,72 | 3,8 / 0,98 / 0,94 | 3,9 / 0,79 / 0,79 |

- Instrukcja atomowa kosztuje tyle samo w każdym języku (~4 ns na 1 wątku). Przy rywalizacji wątków `fetch_add`
  w C++ był w obu przebiegach (A i C) do ~1,5× wolniejszy niż w C# i Javie (np. 20 wątków: 21–22 ns wobec 14–16 ns);
  przyczyny nie badano.
- ***False sharing* spowalnia wszystkie języki jednakowo ~10–14×** (6 wątków: 10,6–11,7 ns zamiast 0,76–0,98 ns)
  – to właściwość procesora, nie języka.
- Różnice między językami pojawiają się w **blokadach**: `synchronized` w Javie trzyma ~20 ns niezależnie
  od liczby wątków, `std::mutex` (MSVC) rośnie do 76 ns przy 20 wątkach, `lock` w C# do 52 ns.

### Z2.3 Producent–konsument (mln komunikatów/s)

| Wątki | C++ (`mutex` + `condition_variable`) | C# (`BlockingCollection`) | Java (`ArrayBlockingQueue`) |
|---|---|---|---|
| 2 | **21,4** | 3,0 | 13,4 |
| 4 | 12,2 | 1,8 | **14,5** |
| 6 | 3,2 | 1,2 | **10,8** |
| 20 | 1,0 | 1,1 | **3,3** |

- Najszybsza przy 2 wątkach jest prosta kolejka C++, ale przy wielu konsumentach traci najwięcej
  (budzenie wątków przez `condition_variable`). `ArrayBlockingQueue` w Javie skaluje się najlepiej mimo opakowywania
  `int` w `Integer`. `BlockingCollection` w C# jest 4–7× wolniejsza od pozostałych przy 2–6 wątkach.
- Wniosek: przy komunikacji między wątkami wynik zależy od **implementacji kolejki w bibliotece**, a nie od języka.

| Czas względem C++ | C++ | C# | Java |
|---|---|---|---|
| Z2.1 mandelbrot | 1,00 | 0,99 | 1,11 |
| Z2.2 sync | 1,00 | 1,04 | 0,83 |
| Z2.3 queue | 1,00 | 2,20 | 0,40 |

## 4. K3 – aplikacja okienkowa (C++ Win32, C# WinForms, Java Swing)

### 4.1 Z3.1 Start aplikacji (ciepły start, 10 pomiarów)

| Metryka | C++ | C# | Java |
|---|---|---|---|
| czas do pierwszego okna – mediana, przebieg A [ms] | **53** | 125 | 294 |
| czas do pierwszego okna – mediana, przebieg B [ms] | **69** | 263 | 395 |
| szczytowy zestaw roboczy [MB] | **14** | 44 | 84 |
| szczytowa pamięć prywatna [MB] | **2,3** | 13,5 | 376 |
| liczba wątków procesu | **4** | 13 | 28 |
| rozmiar programu [KB] (bez środowiska) | 40 | 174 | **9** |

- Kolejność jest stała w obu przebiegach: **Win32 startuje 2–4× szybciej niż WinForms i 4–6× szybciej niż Swing**.
  Wartości bezwzględne różnią się między przebiegami (stan pamięci podręcznej systemu, procesy w tle),
  dlatego porównujemy języki tylko w ramach jednego przebiegu.
- Duży przedział ufności C++ (±230 ms) wynika z jednego odstającego startu (~1,1 s); mediana jest odporna.
- JVM rezerwuje od razu dużą stertę (376 MB pamięci prywatnej, ¼ RAM jako maksimum), choć fizycznie
  zajmuje 84 MB. Środowisko uruchomieniowe tworzy 13 (.NET) i 28 (JVM) wątków – GC, JIT, finalizacja.
- Zimny start (`--cold`) nie był mierzony – wymaga uprawnień administratora i programu RAMMap.

### 4.2 Z3.2 Wypełnienie listy i rysowanie

| Konfiguracja | C++ | C# | Java |
|---|---|---|---|
| lista 10⁴ elementów – mediana [ms] | 17,7 | 24,4 | **7,6** |
| lista 10⁵ elementów – mediana [ms] (± 95% CI) | 336 (± 144) | 2151 (± 793) | **33 (± 4)** |
| rysowanie 800×800 – klatki/s (przebieg B / A) | **1514** / 1708 | 38 / 59 | 1211 / 802 |

- **Swing wypełnia listę 2–60× szybciej** – `JList` jest kontrolką „lekką”: model danych w pamięci Javy,
  rysowane są tylko widoczne wiersze. Win32 `LISTBOX` i WinForms (opakowanie tej samej kontrolki) wysyłają
  komunikat systemowy dla każdego elementu, a WinForms dodatkowo przechowuje kopię listy po stronie .NET.
  Zgadza się to z obserwacją Hassana i Sarhana (2020), że Swing był szybszy od WinForms.
- **Rysowanie w WinForms (GDI+) jest 30–40× wolniejsze** niż `SetDIBitsToDevice` (Win32) i `BufferedImage` (Swing):
  `Graphics.DrawImageUnscaled` konwertuje i komponuje obraz programowo. Szybsze ścieżki w C# istnieją
  (np. format `PArgb`, wywołanie GDI przez P/Invoke), ale nie są pierwszym wyborem programisty.
- **Odrzucony pomiar (przebieg A):** `fill` w C++ i C# trwał wtedy 1,3–8,9 s dla 10⁴ elementów, a dla 10⁵
  przekroczył 10-minutowy limit. Czas CPU procesu był równy czasowi rzeczywistemu, więc czas zużywał kod
  wykonywany wewnątrz procesu przy każdej operacji na natywnej kontrolce. Najpewniej był to program w tle
  przechwytujący zdarzenia okien (np. narzędzie dostępności, zdalnego pulpitu lub agent bezpieczeństwa).
  Swing, który nie korzysta z natywnych kontrolek, nie był zakłócony. Po ustaniu zakłócenia (przebieg B)
  wyniki wróciły do oczekiwanego poziomu. **Wniosek praktyczny:** wydajność natywnych kontrolek zależy
  także od oprogramowania zainstalowanego w systemie.

### 4.3 Z3.3 Responsywność (opóźnienie obsługi zdarzenia co 10 ms przez 5 s)

| Wariant | Miara | C++ | C# | Java |
|---|---|---|---|---|
| `idle` | średnie / p95 / maks. [ms] | 0,10 / 0,25 / 0,5 | 0,26 / 0,45 / 10,5 | 0,14 / 0,27 / 1,6 |
| `ui-thread` | średnie / p95 / maks. [ms] | 221 / 488 / 566 | 238 / 504 / 582 | 228 / 498 / 572 |
| `ui-thread` | zdarzenia opóźnione > 100 ms | 238 | 247 | 301 |
| `worker` | średnie / p95 / maks. [ms] | 0,09 / 0,24 / 0,8 | 0,19 / 0,52 / 5,8 | 0,10 / 0,17 / 2,7 |

- Obliczenia w wątku interfejsu „zamrażają” okno w każdym języku jednakowo (p95 ≈ 0,5 s – czas liczenia jednego obrazu).
- Przeniesienie obliczeń do wątku roboczego przywraca opóźnienia do poziomu `idle` (< 1 ms p95) we wszystkich
  trzech bibliotekach, prawie bez utraty przepustowości (11 obrazów w 5 s zamiast 12).
- **O responsywności decyduje architektura programu, nie język.**

## 5. Powtarzalność – przebieg kontrolny C (ziarno 7)

| Czas względem C++ (śr. geometryczna) | C# – A | C# – C | Java – A | Java – C |
|---|---|---|---|---|
| Z1.1 mem-access | 1,64 | 1,72 | 0,85 | 0,91 |
| Z1.2 matmul | 1,19 | 1,21 | 0,73 | 0,67 |
| Z1.3 objects | 0,42 | 0,36 | 0,32 | 0,25 |
| Z2.1 mandelbrot | 0,99 | 1,03 | 1,11 | 1,15 |
| Z2.2 sync | 1,04 | 1,27 | 0,83 | 0,88 |
| Z2.3 queue | 2,20 | 2,20 | 0,40 | 0,39 |

- Proporcje między językami są takie same jak w przebiegu A, kolejność języków też – poza Mandelbrotem, gdzie C++ i C#
  różnią się o 1–3% (w granicach rozrzutu). Wnioski są powtarzalne.
- Wartości bezwzględne w C są gorsze i bardziej rozrzucone: np. Mandelbrot na 1 wątku 2,35–2,58 s zamiast 1,40–1,50 s,
  przez co przyspieszenie na 20 wątkach wychodzi nadliniowe (23–25×). Pomiary jednowątkowe trafiały wtedy częściej
  na słabsze rdzenie E (harmonogram Windows na procesorze hybrydowym). Dlatego liczby bezwzględne podajemy z przebiegu A,
  a C służy tylko do sprawdzenia powtarzalności.

## 6. Weryfikacja hipotez

| Hipoteza | Wynik | Uzasadnienie |
|---|---|---|
| **H1** – w cache widać narzut języka, w RAM czasy się wyrównują; wzorzec dostępu ważniejszy niż język; Java przegrywa przy tablicach obiektów; GC alokuje szybciej, ale zużywa więcej pamięci | **potwierdzona częściowo** | Wyrównanie w RAM i przewaga wzorca dostępu – tak (`ijk`/`ikj` 3–8×, RAM ~100 ns we wszystkich). GC 3,5–6× szybszy przy alokacji, pamięć do 15× większa – tak. Narzut w cache widać tylko w C# przy dostępie sekwencyjnym (brak wektoryzacji), nie przez sprawdzanie zakresów. Java **nie** przegrywa czasowo przy tablicy obiektów (20 ms jak C++), tylko pamięciowo (625 vs 392 MB). |
| **H2** – atomowe operacje kosztują tyle samo; różnice z blokad, puli i kolejek; *false sharing* jednakowo | **potwierdzona, z zastrzeżeniem** | `atomic` 1 wątek: 3,9–4,1 ns; *false sharing* 10–14× we wszystkich; różnice 2–4× w `mutex` i do 10× w kolejkach – zależne od implementacji bibliotecznej. Zastrzeżenie: przy rywalizacji `atomic` w C++ do 1,5× wolniejszy. |
| **H3** – Win32 startuje najszybciej i zużywa najmniej pamięci, Swing najwolniej; o responsywności decyduje wątek, nie język | **potwierdzona** | Start C++ 53–69 ms, C# 125–263 ms, Java 294–395 ms; pamięć 14 / 44 / 84 MB. Wariant `worker` < 1 ms p95 w każdym języku. Dodatkowo: Swing najszybszy w wypełnianiu listy, WinForms najwolniejszy w rysowaniu. |

## 7. Wnioski

1. **Nie ma języka „najszybszego”.** C++ wygrywa w starcie aplikacji, zużyciu pamięci, rysowaniu i komunikacji dwóch wątków;
   Java – w alokacji obiektów, kolejkach wielowątkowych i (dzięki JIT pod konkretny procesor) w wektoryzowanych pętlach;
   C# zwykle jest pośrodku, ale ma najwolniejsze `BlockingCollection` i rysowanie GDI+.
2. **Sposób napisania programu waży więcej niż język:** kolejność pętli (3–8×), podział pracy między wątki (2,5×),
   rozmieszczenie danych (*false sharing* 10×) i wątek obliczeń w GUI (> 1000×) dają większe różnice niż zmiana języka.
3. **Koszt środowiska zarządzanego to głównie pamięć i start**, nie szybkość obliczeń: Java i C# po rozgrzaniu JIT
   liczą tak samo szybko jak C++, ale startują 2–6× dłużej i zajmują 3–15× więcej pamięci.
4. Wyniki zależą od ustawień domyślnych (MSVC bez AVX2, sterta JVM = ¼ RAM) i od biblioteki
   (Win32 / WinForms / Swing) – porównujemy typowy wybór programisty, a nie sam język.

## 8. Ograniczenia

- Jedno stanowisko (hybrydowy i5-13500, Windows 11), pomiary na komputerze roboczym z programami w tle –
  stąd rozrzut 10–35% w części konfiguracji `mem-access rand` i różnice bezwzględne między przebiegami K3.
  Przebieg kontrolny C (inne ziarno kolejności) potwierdził proporcje między językami (pkt 5).
- Wątki nie są przypinane do rdzeni: na procesorze hybrydowym pomiar jednowątkowy może trafić na rdzeń P lub E.
- Bez zimnego startu GUI (brak uprawnień administratora / RAMMap) i bez przypinania wątków do rdzeni P/E.
- `flush_cache()` nie czyści pamięci podręcznej instrukcji ani predyktora skoków.
- Biblioteki GUI nie są równoważne (Win32 to API systemu, WinForms i Swing to frameworki).
