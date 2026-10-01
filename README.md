# Reversal & Confirmation Entry – indikátor pro ATAS

Indikátor pro footprint ES (bid × ask), primárně M5, podle zadání *ATAS indikátor – Reversal & Confirmation Entry*. Najde reversal na referenční úrovni, počká na potvrzující svíčku a nakreslí limitní vstupní zónu na jejím VPOC (nebo na VPOC retestu) se stopem a targety. Každý kandidát se loguje. Pravděpodobnost se neodhaduje, ale měří: kalibruje se z logu.

* Engine je oddělený od ATAS (`src/Core`), takže stejný kód běží v indikátoru i v backtestu.
* Otestováno na **2 letech reálných tick dat ES s aggressor side** (07/2024 – 06/2026, 137 609 M5 svíček) → [docs/BACKTEST.md](docs/BACKTEST.md).
* Rozhodnutí k nejasnostem a rozporům v zadání → [docs/DESIGN.md](docs/DESIGN.md).

## Výsledky v kostce

| | trénink 07/24–09/25 | test 10/25–06/26 |
|---|---:|---:|
| vyplněné zóny, výstup 1,5R | +0,31 R ± 0,06 (n 474) | +0,24 R ± 0,07 (n 307) |
| totéž, konzervativní fill (proobchodovat o 1 tick) | +0,22 R ± 0,06 | +0,16 R ± 0,07 |
| **jen retest zóny**, RTH, výstup 1,5R | **+0,32 R ± 0,13** (n 89) | **+0,81 R ± 0,16** (n 47) |
| zóny VPOC potvrzení, RTH, výstup 1,5R | +0,06 R ± 0,13 | +0,05 R ± 0,15 |
| vstup trhem na close potvrzení, výstup 1,5R | −0,03 R ± 0,03 | 0,00 R ± 0,04 |

Poplatky nejsou započtené (~0,05–0,1 R na obchod). Test je období, které kalibrace nikdy neviděla.

**Doporučení z dat:**
* Vstupovat přes **retest (R) zóny**. Zónu VPOC potvrzení (C1/C2) brát spíš jako informaci, výhodu nemá.
* Cíl **1,5 R** (a T1/T2 jako runner). T1 „origin" je často daleko.
* Samotný reversal (tečka) je jen kontext, ne signál ke vstupu.

## Instalace (Windows)

**Nejjednodušší:** nainstaluj [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (jednou), zavři ATAS a dvojklikni na **`instalace.bat`** v kořeni složky. Skript indikátor sestaví proti tvé instalaci ATAS, zkopíruje ho do `%APPDATA%\ATAS\Indicators` a přidá kalibraci. Pak spusť ATAS a přidej indikátor **Reversal & Confirmation Entry**.

Hotová DLL ke stažení není: indikátor se musí sestavit proti knihovnám tvé verze ATAS (trvá to asi minutu).

Ručně:

1. Nainstaluj [ATAS](https://atas.net) do výchozí složky a [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. V kořeni repozitáře spusť:
   ```bat
   dotnet build src\Atas\ReversalConfirmation.Atas.csproj -c Release
   ```
   * ATAS jinde než v `C:\Program Files (x86)\ATAS Platform`: přidej `-p:ATAS_BASE="D:\cesta\k\ATAS"`.
   * Starší ATAS na .NET 8: přidej `-p:TargetFramework=net8.0-windows`.
   * ATAS X: přidej `-p:AtasX=true`.
3. Zkopíruj `src\Atas\bin\Release\net10.0-windows\ReversalConfirmation.dll` do `%APPDATA%\ATAS\Indicators`, nebo ji přidej v ATAS přes *Indicators → Add custom indicator*.
4. Pro zobrazení změřených pravděpodobností zkopíruj `calibration\es_m5_2024-07_2026-06.json` jako `%APPDATA%\ATAS\ReversalConfirmation\calibration.json`. Jinou cestu můžeš zadat v parametru *Kalibrační JSON*.
5. Na grafu ES (M5, cluster/footprint data) přidej indikátor **Reversal & Confirmation Entry**. Aby fungovalo srovnání podle času dne, načti aspoň ~12 dní historie (D = 10).

## Co indikátor kreslí

Výchozí zobrazení je minimalistické: agent jen označí svíčky, které nejlépe splňují podmínky obratu. Srozumitelný popis všeho najdeš v brožurce [docs/Brozurka.html](docs/Brozurka.html) (otevři v prohlížeči).

| značka | význam |
|---|---|
| tečka pod low / nad high | reversal (kontext). Velikost a sytost rostou se skóre (60–70 / 70–80 / 80+), kroužek = silný (≥ 75) |
| čtvereček **C1 / C2** | potvrzující svíčka: druhá strana převzala iniciativu (max 2 na reversal) |
| čtvereček **R** | retest: vyšší low (u bearish nižší high) blízko extrému se slabou deltou, v datech nejlepší místo pro vstup |

Najetí myší na značku zobrazí tooltip v češtině: skóre, naměřenou úspěšnost (s kalibrací), úroveň, variantu a všech devět podmínek A–I s ● (splněno) / ○ (ne) a konkrétními čísly. U C1/C2 a R je v tooltipu i cena VPOC svíčky pro případný limit.

Volitelně (v sekci *9. Zobrazení*, ve výchozím stavu vypnuté): vstupní zóny, čáry stopu a targetů, značky × (kontext zrušen) a ! (absorpce selhala), statistický panel, referenční úrovně.

Alerty (zvuk + popup): ve výchozím stavu potvrzení C1/C2 a retest R. Volitelně reversal (standardně jen silné), zóny, fill, dotyk zóny, zrušení kontextu.

## Parametry (výchozí hodnoty)

| skupina | parametr | výchozí |
|---|---|---|
| Session | časové pásmo / ETH / RTH | America/New_York, 18:00, 09:30–16:00 |
| | VWAP session · profil předchozího dne | ETH · RTH |
| | signály v session | ETH (celý den) |
| | časy zpráv · okno | 08:30; 10:00; 14:00 ET · ±5 min |
| Úrovně | váhy | prior VWAP 1,0 · session VWAP 1,0 · ±1σ/±2σ 0,6 · 1. RTH svíčka 1,0 · OR15/OR30/IB 0,8 · RTH open 0,8 · předchozí den H/L 0,9, POC 1,0, VAH/VAL 0,9 · overnight 0,8 · pool 0,7 · swing 0,6 · ruční 1,0 |
| | tolerance testu | max(2 ticky, 0,15 × ATR) |
| Reversal | váhy A–I | 8;15;12;8;15;7;7;15;13 |
| | sloučené svíčky · stall | 2 · zapnuto |
| | A: K barů · pohyb · z delty | 6 · 2,0 × ATR · 2 |
| | min. skóre · silný | 60 · 75 |
| | ATR · dní pro time-of-day | ATR14 · 10 |
| Potvrzení | P · max potvrzení | 6 · 2 |
| | z delty · CLV · efektivita · imbalance · min. skóre | 1 · 0,6 · 40. pct · 3:1 · 60 |
| Zóny | typy | VPOC potvrzení + retest |
| | Q · fill tolerance | 6 · +1 tick |
| | jen po VWAP trendu | vypnuto |
| Stop a targety | stop buffer | max(2 ticky, 0,05 × ATR) |
| | T1 origin · T2 VWAP · T3 úroveň · min. poměr k T1 | zapnuto · zapnuto · zapnuto · 1,2 R |
| Filtry šumu | okno zpráv potlačit · mrtvý trh | ne · ano (< 20. percentil objemu) |
| Logování | CSV · složka · kalibrace · kalibrované váhy | ano · `%APPDATA%\ATAS\ReversalConfirmation\logs` · `…\calibration.json` · ne |
| Zobrazení | tečka od skóre · C1/C2 · R · tooltip | 60 · ano · ano · ano |
| | zóny · stop/targety · × · ! · panel · úrovně | ne |
| Alerty | potvrzení C1/C2 · retest R · reversal · zóny/fill/zrušení | ano · ano · ne · ne |

Všechny prahy jsou percentily, z-score (proti stejnému času dne za posledních 10 dní, s rolling fallbackem) nebo násobky ATR s minimem v ticích. Žádný práh není v kontraktech.

## Logování a kalibrace

Indikátor píše CSV (`%APPDATA%\ATAS\ReversalConfirmation\logs\<instrument>_<timeframe>.csv`) na pozadí a při každém přepočtu ho přepíše celé. Loguje každý reversal se skóre ≥ 40 včetně odmítnutých (s důvodem), každé potvrzení a každou zónu, s komponentami, kontextem a výsledky (MFE/MAE, targety, R). Sloupce popisuje [docs/DESIGN.md](docs/DESIGN.md#log-csv--hlavní-sloupce).

```bash
pip install pandas numpy
python calibration/calibrate.py log1.csv log2.csv --out calibration.json            # vše
python calibration/calibrate.py log.csv --train-until 2026-01-01 --out cal.json     # s hold-outem
```

Skript natrénuje logistické modely P(T1 před stopem) pro reversal a pro vyplněnou zónu a validuje je walk-forward (trénink na starších datech, test na novějších). Vypíše AUC a Brier a uloží JSON. Indikátor pak zobrazí **naměřenou** úspěšnost bucketu, do kterého signál padne, a jen pokud bucket má ≥ 30 vzorků. Váhy A–I z kalibrace se použijí jen po zapnutí *Použít kalibrované váhy*.

## Backtest a kalibrace

Pro vlastní ověření nebo jiné roky/timeframy:

```bash
# 1) tick data ES (Rithmic, aggressor side), ~1,4 GB
curl -LO https://huggingface.co/datasets/EdgeArbiter/rithmic-tick-data/resolve/main/es_tick_rithmic_2025.parquet
pip install duckdb pandas numpy
# 2) footprint svíčky (M5; --minutes 1/3/15 pro jiné TF)
python tools/data/build_footprint.py es_tick_rithmic_2025.parquet --out data/fp5
# 3) replay stejným enginem jako v ATAS (parametry přes --set Pole=Hodnota)
dotnet run -c Release --project tools/Replay -- --data data/fp5 --out runs/base
dotnet run -c Release --project tools/Replay -- --data data/fp5 --out runs/rth --set SignalSession=Rth
# 4) kalibrace a report
python calibration/calibrate.py runs/base/log.csv --out runs/base/calibration.json
python tools/analysis/report.py runs/base runs/rth --names "výchozí|RTH"
```

Replay zvládne ~140 000 svíček za ~10 s a ošetřuje kvartální rolly.

## Vývoj

```bash
dotnet test tests/Core.Tests -c Release                                          # 22 testů
dotnet build src/Atas/ReversalConfirmation.Atas.csproj -c Release -p:AtasStubs=true  # kontrola kompilace bez ATAS
dotnet build tools/AtasHarness -c Release -p:AtasStubs=true                        # simulace volání ATAS + měření výkonu
```

Testy obsahují scénáře podle akceptačních případů ze zadání: 2-bar flush + reclaim s H, „no reclaim" (Den 2 16:45), „no level" (Den 3 18:45), zrušení kontextu novým low, retest → C2 (Den 3 19:35/19:40), stall přes I (Den 2 16:20). Dál přesnou zrcadlovou symetrii bullish/bearish, žádný repaint, letní čas USA/EU a statistiky.

`src/AtasStubs` je jen pro kontrolu kompilace mimo Windows: napodobuje část API ATAS (podle oficiálních zdrojů [AtasPlatform/Indicators](https://github.com/AtasPlatform/Indicators)) a do ATAS se nikdy nenahrává. Skutečný build běží proti DLL z instalace ATAS.

## Struktura

```
src/Core/         engine (bez závislosti na ATAS)
src/Atas/         indikátor pro ATAS
src/AtasStubs/    náhrada API ATAS pro kompilaci mimo Windows
tests/Core.Tests/ unit a scénářové testy
tools/Replay/     backtest
tools/AtasHarness/ simulace hostitele ATAS (výkon)
tools/data/       stavba footprint svíček z tick dat
tools/analysis/   report z logů
calibration/      calibrate.py + kalibrace ES M5 2024–2026
docs/             DESIGN.md, BACKTEST.md
```
