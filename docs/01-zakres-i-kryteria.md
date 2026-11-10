# Zakres badania i kryteria porównania

*2026-11-09. Plan: [02-plan-badania.md](02-plan-badania.md), wyniki i weryfikacja hipotez: [04-wyniki.md](04-wyniki.md).*

## 1. Cel

Porównać języki **C++, C# i Java** według trzech kryteriów opisujących, **jak komputer pracuje
z programem napisanym w danym języku**: jak program korzysta z pamięci, jak wykorzystuje
wiele rdzeni i jak zachowuje się jako aplikacja okienkowa. Sam czas obliczeń jest tylko jedną z miar.
Interesuje nas przede wszystkim, jak model wykonania wpływa na wynik:

| Język | Model wykonania | Zarządzanie pamięcią |
|---|---|---|
| C++ | kod natywny, kompilacja z wyprzedzeniem (MSVC) | ręczne / RAII |
| C# | kod pośredni IL → kompilator JIT (.NET 10, kompilacja warstwowa + PGO) | odśmiecacz (GC) |
| Java | kod bajtowy → JIT HotSpot (JDK 25, C1 + C2) | odśmiecacz (G1 GC) |

Każde kryterium odpowiada jednemu typowi programu z treści zadania: **matematycznemu,
wielowątkowemu i okienkowemu**.

## 2. Ustalenia

| Kwestia | Decyzja |
|---|---|
| Języki | C++ (MSVC), C# (.NET 10), Java (JDK 25). C wypada z projektu. Pascal pominięty i wymieniony w „dalszych pracach”. |
| Kryteria | 3 kryteria dotyczące pracy komputera (bez metryk pracy programisty, np. LOC) |
| Biblioteki GUI | standardowe dla języka: C++ – **Win32 API**, C# – **WinForms**, Java – **Swing** |
| Urządzenia mobilne | poza zakresem, tylko wzmianka w „dalszych pracach” |
| Dotychczasowe zadania | `matmul` zostaje jako zadanie K1 (wpływ kolejności pętli na cache). `primes` i `fibonacci` zostają usunięte. |

## 3. Przegląd literatury

| Praca | Języki | Co mierzono | Co z niej bierzemy |
|---|---|---|---|
| Pereira i in., *Energy Efficiency across Programming Languages*, SLE 2017 ([PDF](https://greenlab.di.uminho.pt/wp-content/uploads/2017/10/sleFinal.pdf), DOI 10.1145/3136014.3136031) | 27 języków, m.in. C++, C#, Java | czas, pamięć szczytowa, energia; 10 zadań z CLBG | czas i pamięć mierzone razem; normalizacja wyników względem najszybszego języka |
| Prechelt, *An Empirical Comparison of Seven Programming Languages*, IEEE Computer 2000 ([PDF](https://www.cs.tufts.edu/~nr/cs257/archive/lutz-prechelt/comparison.pdf)) | C, C++, Java + 4 skryptowe | czas, pamięć, niezawodność, nakład pracy | Java zużywała wielokrotnie więcej pamięci niż C/C++, a różnice między programistami były większe niż między językami. Wniosek dla nas: używamy identycznych algorytmów. |
| Fourment, Gillings, BMC Bioinformatics 9:82, 2008 ([PDF](https://link.springer.com/content/pdf/10.1186/1471-2105-9-82.pdf)) | C, C++, C#, Java, Perl, Python | czas i pamięć na zadaniach bioinformatycznych | ranking języków był taki sam na Windows i Linux |
| Hassan, Sarhan, *Performance Evaluation of GUI in Java and C#*, CSASE 2020 (DOI 10.1109/CSASE48920.2020.9142075) | Java, C# | tworzenie i modyfikacja kontrolek GUI, 32 scenariusze | jedyna znaleziona praca o wydajności GUI; Java była szybsza we wszystkich scenariuszach. Punkt odniesienia dla K3. |
| Georges, Buytaert, Eeckhout, *Statistically Rigorous Java Performance Evaluation*, OOPSLA 2007 ([PDF](https://dri.es/files/oopsla07-georges.pdf)) | Java | metodyka | rozdzielenie pomiaru **startu** od **stanu ustalonego** (po rozgrzewce JIT); raportowanie przedziałów ufności zamiast najlepszego wyniku |
| Hundt, *Loop Recognition in C++/Java/Go/Scala*, Scala Days 2011 | C++, Java, Go, Scala | czas, pamięć, rozmiar kodu | koszt reprezentacji obiektów i GC w Javie |
| Chen Hao, *Comparative Study of C, C++, C# and Java*, praca dypl., VAMK 2010 ([link](https://www.theseus.fi/handle/10024/16995)) | C, C++, C#, Java | czas zadań algorytmicznych | podobne zestawienie języków, ale tylko czas obliczeń |
| Drepper, *What Every Programmer Should Know About Memory*, 2007 ([PDF](https://people.freebsd.org/~lstewart/articles/cpumemory.pdf)); Bryant, O'Hallaron, *CS:APP*, rozdz. 6 („memory mountain”) | — | hierarchia pamięci | metoda pomiaru dostępu sekwencyjnego i losowego w zależności od rozmiaru danych (K1) |
| Hunold, Carpen-Amarie, *MPI Benchmarking Revisited*, 2015 ([arXiv](https://arxiv.org/pdf/1505.07734)); J. Duffy, [*Clearing dcache between test iterations*](https://joeduffyblog.com/2007/04/16/clearing-dcache-between-test-iterations/) | — | metodyka | czyszczenie cache przez nadpisanie bufora większego niż L3 przed każdym pomiarem |
| Journal of Computer Sciences Institute, Politechnika Lubelska ([archiwum](https://ph.pollub.pl/index.php/jcsi)) | m.in. C++ i Java na Androidzie (2022), Java i C# w mikroserwisach (2024) | czas wykonania | polskie prace porównawcze, do cytowania we wstępie |

**Luka.** Znalezione prace porównują zwykle sam czas obliczeń (Chen, Dr. Dobb's, JCSI) albo
czas, pamięć i energię na zadaniach z CLBG (Pereira). Nie znaleziono pracy, która dla **C++, C# i Javy
na aktualnych środowiskach (.NET 10, JDK 25)** zestawiałaby jednocześnie:
(1) zachowanie przy różnych wzorcach dostępu do pamięci z kontrolą stanu cache,
(2) koszt synchronizacji wątków i (3) start oraz responsywność aplikacji okienkowej.
To jest wkład tego projektu.

> Przeszukano ogólne wyszukiwarki, archiwa uczelni i JCSI. Wiele z tych prac jest też dostępnych na
> ResearchGate, ale nie znaleziono tam dodatkowej pracy obejmującej dokładnie ten zestaw języków i kryteriów.

## 4. Kryteria

| | Kryterium | Pytanie badawcze | Typ programu | Główne metryki |
|---|---|---|---|---|
| **K1** | **Efektywność pracy z pamięcią** | Ile kosztuje dostęp do danych i zarządzanie pamięcią w danym języku, gdy dane są w cache i gdy są w RAM? | matematyczny | ns/dostęp, GFLOP/s, czas, pamięć szczytowa, liczba i czas pauz GC |
| **K2** | **Wykorzystanie wielu rdzeni** | Jak dobrze program skaluje się z liczbą wątków i ile kosztuje synchronizacja? | wielowątkowy | przyspieszenie i efektywność, ns/operację synchronizacji, komunikaty/s, czas CPU / czas rzeczywisty |
| **K3** | **Start, zasoby i responsywność aplikacji okienkowej** | Jak szybko pojawia się okno, ile pamięci zajmuje aplikacja i czy interfejs reaguje w trakcie obliczeń? | okienkowy | czas do pierwszego okna (zimny i ciepły start), pamięć, czas wypełnienia i odrysowania, opóźnienie obsługi zdarzeń (śr., p95, max) |

### Hipotezy

- **H1 (K1).** Gdy dane mieszczą się w cache, widać narzut języka, np. sprawdzanie zakresów tablic
  w C# i Javie. Gdy dane są w RAM, czasy wyrównują się, bo ogranicza przepustowość pamięci.
  Wzorzec dostępu (kolejność pętli) zmienia czas bardziej niż wybór języka. Java przegrywa przy
  tablicach obiektów, bo nie ma typów wartościowych: tablica przechowuje referencje.
  C# ze `struct` jest zbliżony do C++. GC alokuje szybciej niż `new`/`delete`, ale zużywa więcej pamięci.
- **H2 (K2).** Prymitywy atomowe kosztują podobnie we wszystkich językach (te same instrukcje procesora).
  Różnice wynikają z implementacji blokad, puli wątków i kolejek. *False sharing* spowalnia wszystkie
  języki w podobnym stopniu.
- **H3 (K3).** Win32 (C++) startuje najszybciej i zajmuje najmniej pamięci, a Swing (Java) startuje
  najwolniej (start JVM, ładowanie klas). O responsywności decyduje to, czy obliczenia działają
  poza wątkiem UI, a nie wybór języka.

## 5. Zmiany w projekcie

| Obecnie | Po zmianie |
|---|---|
| `BenchC` (C, OpenMP) | usunięty, wyniki v1 przeniesione do archiwum |
| `BenchCpp`, `BenchCs` – zadania `matmul`, `primes`, `fibonacci` | nowe zadania K1 i K2 |
| brak Javy | `BenchJava` (JDK 25, budowany skryptem) |
| brak GUI | `GuiCpp` (Win32), `GuiCs` (WinForms), `GuiJava` (Swing) – zadania K3 |
| `Runner` – jeden typ pomiaru | uruchamia K1–K3, losowa kolejność uruchomień, mierzy pamięć i czas CPU procesów |

## 6. Dalsze prace (poza zakresem)

- Pascal (Free Pascal / Lazarus) jako czwarty język.
- Urządzenia mobilne (Android): Java/Kotlin vs C++ (NDK) dla K1 i K2.
- C# w trybie Native AOT oraz Java z GraalVM Native Image, żeby oddzielić koszt JIT od kosztu języka.
