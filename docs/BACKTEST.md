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

## Break struktury, F5/F7 a obchodní okna (aktuální verze)

Od této verze platí potvrzení C1/C2 až po **breaku struktury** (BOS) a signály se hledají jen v **obchodním okně**: RTH = 15:30–22:12 pražského času (09:30–16:12 New York, okno se drží New Yorku i v týdnech přechodu času), ETH = evropské dopoledne 08:00–15:00 Praha. Každé okno má vlastní kalibraci.

* **BOS** (podle schémat obchodníka): bullish = close nad prvním pivot high vlevo od low A (svíčka, jejíž high vyčnívá nad svíčku nalevo). **Anomálie**: když svíčka A udělá nové low a zároveň high nad předchozí svíčkou (outside bar), láme se high svíčky A. Bearish zrcadlově. Anomálie tvoří ~53 % případů, BOS je v mediánu 1,8 ATR od extrému a 2 svíčky po něm.
* **Platné C**: order-flow svíčka (podmínky beze změny) + BOS buď už proběhl, nebo přijde do 30 minut (6 svíček M5) po ní. Statistika vstupuje na **close svíčky, kterou se potvrzení stalo platným**, ne na close order-flow svíčky.
* **F5/F7**: po BOS (do 24 svíček od A) limit v 61,8 % / 78,9 % impulsu A→B (B = nejvyšší high od A, zmrazí se dotykem F5), stop pod A (stejný buffer), výstup na 1,5 R. Ceny zaokrouhlené na tick. Fill = dotyk (konzervativně: proobchodování o 1 tick).
* Výsledky jsou **po odečtení nákladů 1,4 ticku na obchod** (komise + 1 tick skluzu), v R na obchod ± směrodatná chyba.

| vstup (výstup 1,5 R, net) | RTH trénink | RTH test | ETH trénink | ETH test |
|---|---:|---:|---:|---:|
| trhem na close order-flow svíčky (bez BOS) | −0,08 (476) | −0,05 (255) | −0,14 (318) | −0,09 (181) |
| trhem na close, BOS před/na svíčce | −0,06 (199) | −0,05 (109) | +0,01 (156) | −0,05 (82) |
| **trhem po platném C (BOS do 30 min)** | −0,10 (225) | −0,07 (120) | −0,02 (174) | −0,07 (92) |
| limit F5 po BOS, bez platného C | +0,15 (77) | −0,22 (33) | +0,21 (104) | +0,34 (44) |
| limit F5 po BOS, s platným C | +0,05 (91) | −0,08 (47) | −0,12 (70) | +0,16 (43) |
| **limit F7 po BOS, bez platného C** | **+0,25** ± 0,18 (50) | **+0,44** ± 0,26 (23) | **+0,34** ± 0,14 (73) | **+0,63** ± 0,24 (24) |
| limit F7 po BOS, s platným C | +0,05 (69) | +0,22 (33) | +0,12 (53) | +0,35 (29) |
| F7 s i bez C, fill jen proobchodováním | +0,08 (112) | +0,32 (54) | +0,17 (120) | +0,42 (50) |
| **limit na VPOC retestu (R)** | **+0,21** ± 0,13 (93) | **+0,70** ± 0,16 (53) | **+0,29** ± 0,14 (75) | **+0,55** ± 0,18 (43) |
| R, fill jen proobchodováním | +0,22 (89) | +0,56 (50) | +0,14 (67) | +0,54 (42) |

V závorce počet obchodů (trénink 321 dní, test 183–184 dní). Tabulka je bez market/volume profile úrovní; s nimi (výchozí stav) viz další kapitola. Četnost v jednom okně: tečka 4–6× za den, platné C 0,5–0,7×, R ~0,3×, F7 ~0,3×.

**Co z toho plyne:**

1. **Break struktury zpřísní potvrzení** zhruba na polovinu (1,4–1,5 → 0,6–0,7 za den v RTH) a cena po platném C dojde k originu pohybu (T1) v 61–78 % případů (bez BOS 58–63 %). Jako **vstup trhem** ale platné C výhodu nemá: stop pod A je po breaku daleko (medián rizika 65–80 ticků v RTH) a 1,5 R je pak dlouhá cesta.
2. **Metoda „BOS + návrat do F7" je kladná ve všech čtyřech řezech** i po nákladech a s konzervativním fillem. F5 je v RTH nespolehlivé (test −0,22). Vzorky F7 jsou ale malé (23–73 obchodů na řez), takže rozdíly kolem ±0,2 R jsou v rámci šumu.
3. **Order-flow potvrzení vstup F7 nezlepšilo** (s platným C +0,05/+0,22 RTH, bez platného C +0,25/+0,44). Proto čtvereček F ve výchozím stavu nečeká na C1 (`FiboRequireConf = false`).
4. **Retest (R) zůstává nejstabilnější** v obou oknech a obou modelech fillu.
5. **RTH a ETH se liší** (jiná velikost rizika: medián F7 18–21 vs 9–11 ticků, jiné koeficienty kalibračních modelů, např. váha komponenty I nebo úrovně). Oddělená kalibrace podle okna má proto smysl.

Reprodukce (data viz výše):

```bash
dotnet run -c Release --project tools/Replay -- --data data/fp5 --out runs/rth --set Window=Rth
dotnet run -c Release --project tools/Replay -- --data data/fp5 --out runs/eth --set Window=Eth
dotnet run -c Release --project tools/Replay -- --data data/fp5 --out runs/rth_nobos --set Window=Rth --set RequireBos=false
dotnet run -c Release --project tools/Replay -- --data data/fp5 --out runs/rth_ft --set Window=Rth --set FibFillThroughTicks=1 --set FillTolTicks=-1
python calibration/calibrate.py runs/rth/log.csv --window rth --out calibration/es_m5_rth.json
python calibration/calibrate.py runs/eth/log.csv --window eth --out calibration/es_m5_eth.json
```

## Market a volume profile úrovně

Nové referenční úrovně (výchozí váha): **dnešní developing VAH/VAL/POC** (0,7; v RTH z RTH profilu, před open z Globex session, až po 60 minutách profilu), **VAH/VAL/POC tohoto týdne** (0,8; developing, všechny obchody týdne, od druhé session), **předchozího týdne** (0,9) a **nahé VAH/VAL/POC** (1,0; z posledních 10 dní, na kterých cena od uzavření jejich profilu neobchodovala, po prvním dotyku zmizí). Value area 70 % z objemu (VP), nebo volitelně z TPO (MP, 30min periody). Úrovně vstupují do komponenty B (výběr úrovně, blízkost, konfluence) jako všechny ostatní.

Net po nákladech, výstup 1,5 R, trénink / test:

| | tečky / den | T1 | R (retest) | F7 | trhem po platném C |
|---|---:|---:|---:|---:|---:|
| RTH bez profilových úrovní | 5,8 / 5,2 | 28 % / 29 % | +0,21 / +0,70 | +0,14 / +0,31 | −0,10 / −0,07 |
| RTH volume profile (výchozí) | 6,4 / 5,8 | 28 % / 28 % | +0,18 / +0,57 | +0,14 / +0,33 | −0,08 / −0,04 |
| RTH TPO | 6,3 / 5,9 | 28 % / 29 % | +0,23 / +0,57 | +0,16 / +0,37 | −0,08 / −0,09 |
| ETH bez profilových úrovní | 4,2 / 4,6 | 37 % / 32 % | +0,29 / +0,55 | +0,25 / +0,50 | −0,02 / −0,07 |
| ETH volume profile (výchozí) | 4,9 / 5,3 | 36 % / 32 % | +0,29 / +0,46 | +0,25 / +0,52 | −0,03 / −0,05 |
| ETH TPO | 4,8 / 5,3 | 36 % / 31 % | +0,29 / +0,42 | +0,27 / +0,43 | −0,08 / −0,07 |

F7 zde zahrnuje vstupy s i bez platného C. Směrodatná chyba R je 0,13–0,18, F7 0,11–0,16.

Reversaly podle rodiny vybrané úrovně (volume profile, oba roky, T1 trénink / test):

| úroveň | RTH n | RTH T1 | ETH n | ETH T1 |
|---|---:|---:|---:|---:|
| ostatní (VWAP, OR/IB, H/L, pool, swing, ruční) | 2 178 | 29 % / 29 % | 1 543 | 36 % / 32 % |
| předchozí den VAH/VAL/POC | 325 | 26 % / 27 % | 307 | 36 % / 30 % |
| dnešní developing | 226 | 29 % / 29 % | 426 | 35 % / 36 % |
| tento týden | 171 | 29 % / 17 % | 163 | 32 % / 25 % |
| předchozí týden | 171 | 19 % / 28 % | 97 | 37 % / 27 % |
| nahé VAH/VAL/POC | 33 | 10 % / 31 % | 16 | 9 % / 0 % |

Medián reakce ceny po reversalu (MFE za 12 svíček) je u všech rodin 1,3–1,8 ATR, stop zasažen v 77–88 % do 36 svíček – bez rozdílu mezi profilovými a ostatními úrovněmi. Víc profilových úrovní u lowu (konfluence) T1 nezvýšilo (RTH 29 / 26 / 27 %, ETH 35 / 33 / 28 % pro 0 / 1 / 2+).

**Co z toho plyne:** profilové úrovně přidají 10–17 % reversalů stejné kvality, výsledky vstupů se v rámci šumu nemění a VP i TPO vychází stejně. Nahých úrovní je v datech málo (cena k nim dojde zřídka), na závěr o nich to nestačí. Kalibrace dostala příznaky `level_profile` a `profile_confluence`, takže zobrazená úspěšnost jejich vliv započítá (koeficienty vyšly blízko nuly). Výchozí stav: VP úrovně zapnuté s umírněnými vahami, protože kvalitu nezhoršují a obchodník je chce vidět; tooltip tečky vypíše profilové úrovně u extrému na řádku „Profil u lowu/highu".

## Výsledky první verze (celý den, bez BOS)

Tabulky níže jsou z první verze: signály přes celý den, potvrzení bez breaku struktury, náklady nezapočtené.

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
