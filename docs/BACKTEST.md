# Backtest – ES M5, 07/2024 – 06/2026

## Data

| | |
|---|---|
| Zdroj | Hugging Face `EdgeArbiter/rithmic-tick-data`: tick data ES z Rithmicu s aggressor side (Buy/Sell), 2024-07-05 → 2026-06-17, ~526 mil. obchodů |
| Ověření | 1minutové svíčky postavené z ticků jsem porovnal s nezávislým zdrojem (`msj-21/es-futures-1m`, formát Databento) na 6 767 společných minutách: high/low sedí na 100 %, objem na 100 % (korelace 0,999999), close na 99,3 %. Časy jsou v UTC a denní pauza 17–18 ET i posun RTH při letním čase odpovídají CME. |
| Footprint | `tools/data/build_footprint.py`: M5 svíčky, bid/ask objem po cenách (Buy = ask, Sell = bid). 137 609 svíček, ~24 cenových úrovní na svíčku, 505 session. |
| Rolly | Kontinuální front-month řada. 7 kvartálních rollů (+51 až +74 bodů, vždy úterý kolem 17:00 ET) replay pozná a posune o ně ceny v historii. Kontexty a obchody přes roll se uzavřou. |

Data v repozitáři nejsou (licence tržních dat). Jak je stáhnout a přehrát, popisuje [README](../README.md#backtest-a-kalibrace).

## Metodika

* Stejný engine jako v ATAS (`src/Core`), svíčka po svíčce, bez pohledu do budoucnosti. Úrovně, statistiky a ATR se berou jen z historie před hodnocenou svíčkou.
* **Trénink** = 07/2024 – 09/2025, **test** = 10/2025 – 06/2026. Kalibrace test nikdy neviděla.
* **Vstup** = limit na zóně. Fill podle zadání: Low ≤ zóna + 1 tick. Konzervativní varianta: cena musí zónou projít o 1 tick.
* **Stop** = low reversalu − max(2 ticky, 0,05 × ATR). Když svíčka zasáhne stop i target, počítá se stop. Na svíčce fillu se target nepočítá.
* **R (výstup T1)**: výstup na T1 (origin pohybu) nebo na stopu, po 36 svíčkách se ocení na close.
* **R (výstup 1,5R)**: pevný target 1,5 R. Nezávisí na geometrii T1, proto je to hlavní měřítko kvality vstupu.
* **±** = směrodatná chyba průměru. Poplatky nejsou započtené. ES má round-trip ~1,4 ticku včetně skluzu na stopu, tedy ~0,05–0,1 R podle velikosti R.

## Výsledky

| varianta | období | session | reversaly / session | potvrzení / session | vyplněné zóny | T1 zasažen | R (výstup T1) | R (výstup 1,5R) | win 1,5R |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| výchozí (dle zadání) | trénink | 321 | 15.7 | 3.91 | 474 | 32% | +0.38 ± 0.10 | +0.31 ± 0.06 | 53% |
| výchozí (dle zadání) | test | 184 | 15.7 | 3.83 | 307 | 30% | +0.31 ± 0.13 | +0.24 ± 0.07 | 50% |
| fill jen při proobchodování o 1 tick | trénink | 321 | 15.7 | 3.94 | 433 | 30% | +0.31 ± 0.10 | +0.22 ± 0.06 | 49% |
| fill jen při proobchodování o 1 tick | test | 184 | 15.7 | 3.84 | 292 | 28% | +0.26 ± 0.14 | +0.16 ± 0.07 | 47% |
| signály jen v RTH | trénink | 321 | 6.0 | 1.58 | 178 | 28% | +0.31 ± 0.17 | +0.19 ± 0.09 | 48% |
| signály jen v RTH | test | 183 | 5.2 | 1.48 | 114 | 31% | +0.32 ± 0.20 | +0.40 ± 0.12 | 56% |
| jen zóny po VWAP trendu | trénink | 321 | 15.9 | 3.95 | 168 | 29% | +0.22 ± 0.16 | +0.27 ± 0.10 | 51% |
| jen zóny po VWAP trendu | test | 184 | 16.0 | 3.86 | 110 | 38% | +0.46 ± 0.22 | +0.29 ± 0.12 | 52% |
| hybrid ATR | trénink | 321 | 14.5 | 3.73 | 480 | 31% | +0.32 ± 0.10 | +0.28 ± 0.06 | 51% |
| hybrid ATR | test | 184 | 14.7 | 3.75 | 313 | 28% | +0.22 ± 0.13 | +0.20 ± 0.07 | 48% |
| kalibrované váhy (trénink do 09/2025) | trénink | 321 | 16.9 | 4.65 | 493 | 34% | +0.41 ± 0.10 | +0.35 ± 0.06 | 54% |
| kalibrované váhy (trénink do 09/2025) | test | 184 | 16.4 | 4.41 | 310 | 30% | +0.28 ± 0.13 | +0.17 ± 0.07 | 47% |

Sloupec „session" počítá session, ve kterých v daném období vznikl aspoň jeden záznam.

### Podle typu zóny (výstup 1,5R)

| fill | typ | období | session | n | R (1,5R) | win | medián R |
|---|---|---|---|---:|---:|---:|---:|
| zadání | VPOC potvrzení | trénink | RTH | 89 | +0.06 ± 0.13 | 43% | 27 t |
| zadání | VPOC potvrzení | trénink | noc | 120 | +0.19 ± 0.11 | 48% | 14 t |
| zadání | VPOC potvrzení | test | RTH | 64 | +0.05 ± 0.15 | 42% | 34 t |
| zadání | VPOC potvrzení | test | noc | 79 | −0.13 ± 0.13 | 35% | 24 t |
| zadání | **retest** | trénink | RTH | 89 | **+0.32 ± 0.13** | 53% | 14 t |
| zadání | **retest** | trénink | noc | 176 | **+0.52 ± 0.09** | 61% | 8 t |
| zadání | **retest** | test | RTH | 47 | **+0.81 ± 0.16** | 72% | 18 t |
| zadání | **retest** | test | noc | 117 | **+0.37 ± 0.12** | 55% | 10 t |
| proobchodování | VPOC potvrzení | trénink / test | RTH | 81 / 61 | −0.02 / −0.06 | 40% / 38% | |
| proobchodování | **retest** | trénink / test | RTH | 85 / 44 | **+0.32 / +0.65** | 53% / 66% | |
| proobchodování | **retest** | trénink / test | noc | 162 / 111 | **+0.40 / +0.33** | 56% / 53% | |

### Spolehlivost zobrazené pravděpodobnosti (test, kalibrace jen z tréninku)

| zobrazeno | n | průměr zobrazené P | naměřeno |
|---|---:|---:|---:|
| 0,2–0,3 | 160 | 23 % | 18 % |
| 0,3–0,4 | 2 677 | 37 % | 38 % |
| 0,4–0,5 | 186 | 45 % | 51 % |

Kalibrované skóre na testu roste monotónně: 60–70 → 34 %, 70–80 → 38 %, 80–90 → 38 %, 90+ → 43 % (T1 před stopem).

## Co z toho plyne

1. **Samotný reversal není vstup.** Reversaly mají zhruba nulovou očekávanou hodnotu (T1 zasažen v ~30 % případů). To potvrzuje filozofii zadání: reversal je kontext.
2. **Vstup trhem na close potvrzení výhodu nemá.** Vychází −0,03 až +0,01 R (1 959 potvrzení). Limit na zóně ano.
3. **Retest zóna nese většinu výhody.** Je kladná ve všech osmi řezech (trénink/test × RTH/noc × dva modely fillu) a to i po poplatcích. VPOC zóna potvrzující svíčky je kolem nuly. **Doporučení:** brát hlavně R (retest) zóny. VPOC potvrzení vypnout nebo ji brát jen jako informaci.
4. **Výstup na pevných 1,5 R je stabilnější než na T1.** T1 (origin) je často daleko: zasáhne se ve ~30 % případů a výsledek hodně kolísá.
5. **Skóre s vahami ze zadání skoro nic nepředpovídá** (AUC 0,54). Z komponent předpovídají nejvíc G (close location), H (flush + reclaim), E (absorpce) a I (stall). A má záporný vliv, protože větší předchozí pohyb znamená vzdálenější T1. Kalibrované váhy zlepší predikci T1 (AUC 0,60–0,65 out-of-sample), výsledky zón ale nezlepší. Proto se v indikátoru z kalibrace ve výchozím stavu berou jen pravděpodobnosti, váhy zůstávají podle zadání.
6. **Zobrazená pravděpodobnost je poctivá.** Na datech, která model neviděl, sedí na ±5 procentních bodů.
7. **Filtr trendu VWAP a signály jen v RTH** vylepšují část řezů, ale ne konzistentně. Zůstávají jako volby, ne jako výchozí nastavení.
8. **Hybrid ATR**, můj nápad proti zkreslení ATR14 kolem RTH open, klasický ATR14 nepřekonal. Výchozí je proto ATR14 podle zadání.

## Omezení

* Jde o dva roky jednoho trhu. Časy, kdy se ES chová jinak (např. 2020, 2022), v datech nejsou.
* Data z Rithmicu se mohou mírně lišit od datového feedu v ATAS. Po nasazení je proto dobré udělat kalibraci z vlastních logů.
* Akceptační případy ze zadání (ESZ6, září 2026) v datech nejsou, data končí 17. 6. 2026. Pokryly je proto syntetické testy (`tests/Core.Tests/ScenarioTests.cs`) a po doplnění dat lze přehrát konkrétní dny (README).
* Fill limitky na zóně je model. Skutečné pořadí ve frontě neznáme, proto jsou uvedené obě varianty fillu.

## Reprodukce

```bash
python tools/data/build_footprint.py es_tick_rithmic_2024.parquet es_tick_rithmic_2025.parquet es_tick_rithmic_2026.parquet --out data/fp5
dotnet run -c Release --project tools/Replay -- --data data/fp5 --out runs/base
dotnet run -c Release --project tools/Replay -- --data data/fp5 --out runs/fill_through --set FillTolTicks=-1
python calibration/calibrate.py runs/base/log.csv --train-until 2025-10-01 --out runs/cal_train.json
python tools/analysis/report.py runs/base runs/fill_through --names "výchozí|proobchodování"
```
