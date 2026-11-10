## Czas względem C++ (średnia geometryczna, C++ = 1,00)

| Zadanie | C++ | C# | Java |
|---|---|---|---|

## K3 – Z3.1 start

| Metryka | C++ | C# | Java |
|---|---|---|---|
| warm: czas do 1. okna – mediana [ms] | 69 | 263 | 395 |
| warm: ± 95% CI [ms] | 227 | 14 | 8 |
| warm: szczyt. zestaw roboczy [MB] | 13,9 | 43,7 | 84,1 |
| warm: szczyt. pamięć prywatna [MB] | 2,3 | 13,5 | 375,8 |
| warm: wątki procesu | 4 | 13 | 28 |
| warm: rozmiar programu [KB] | 40 | 174 | 9 |

## K3 – Z3.2 fill / draw

| Konfiguracja | C++ | C# | Java |
|---|---|---|---|
| fill 10000: mediana [ms] | 17,7 | 24,4 | 7,6 |
| fill 10000: ± 95% CI [ms] | 4,9 | 0,8 | 2,2 |
| fill 10000: 1. iteracja [ms] | 163,9 | 51,8 | 55,7 |
| fill 100000: mediana [ms] | 335,5 | 2150,8 | 33,4 |
| fill 100000: ± 95% CI [ms] | 144,2 | 792,7 | 3,8 |
| fill 100000: 1. iteracja [ms] | 364,3 | 439,5 | 123,5 |
| draw: klatki/s | 1514 | 38 | 1211 |

## K3 – Z3.3 responsywność: średnie / p95 / maks. [ms], zamrożenia > 100 ms, obrazy

| Wariant | C++ | C# | Java |
|---|---|---|---|
| idle | 0,10 / 0,25 / 0,5; 0; 0 | 0,26 / 0,45 / 10,5; 0; 0 | 0,14 / 0,27 / 1,6; 0; 0 |
| ui-thread | 221,20 / 487,68 / 566,1; 238; 12 | 237,97 / 504,26 / 581,7; 247; 12 | 227,83 / 498,17 / 571,6; 301; 12 |
| worker | 0,09 / 0,24 / 0,8; 0; 11 | 0,19 / 0,52 / 5,8; 0; 11 | 0,10 / 0,17 / 2,7; 0; 11 |

Niezgodne wyniki kontrolne: brak.
