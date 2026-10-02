# Reversal & Confirmation Entry – indikátor pro ATAS

Indikátor pro footprint ES (bid × ask), primárně M5, podle zadání *ATAS indikátor – Reversal & Confirmation Entry*. Najde reversal na referenční úrovni, vyznačí pivot, který musí padnout (break struktury), a potvrzení C1/C2 uzná až po tomto breaku. Hlásí i retest (R) a návrat do F5/F7 (F) podle metody obchodníka. Signály hledá jen v obchodním okně (RTH nebo ETH), každé okno má vlastní kalibraci. Každý kandidát se loguje. Pravděpodobnost se neodhaduje, ale měří: kalibruje se z logu.

* Engine je oddělený od ATAS (`src/Core`), takže stejný kód běží v indikátoru i v backtestu.
* Otestováno na **2 letech reálných tick dat ES s aggressor side** (07/2024 – 06/2026, 137 609 M5 svíček) → [docs/BACKTEST.md](docs/BACKTEST.md).
* Rozhodnutí k nejasnostem a rozporům v zadání → [docs/DESIGN.md](docs/DESIGN.md).

## Výsledky v kostce

Po odečtení nákladů (1,4 ticku na obchod), výstup na 1,5 R, v R na obchod. Trénink 07/2024–09/2025, test 10/2025–06/2026 (období, které kalibrace nikdy neviděla).

| vstup | RTH trénink / test | ETH trénink / test |
|---|---:|---:|
| **limit na VPOC retestu (R)** | **+0,21 / +0,70** | **+0,29 / +0,55** |
| **limit F7 po breaku struktury, bez platného C** | **+0,25 / +0,44** | **+0,34 / +0,63** |
| limit F7 po breaku, s platným C1/C2 | +0,05 / +0,22 | +0,12 / +0,35 |
| limit F5 po breaku, bez platného C | +0,15 / −0,22 | +0,21 / +0,34 |
| trhem po platném C1/C2 (close svíčky s breakem) | −0,10 / −0,07 | −0,02 / −0,07 |

RTH = 15:30–22:12, ETH = 08:00–15:00 pražského času. Podrobnosti, počty obchodů a chyby odhadu jsou v [docs/BACKTEST.md](docs/BACKTEST.md).

**Doporučení z dat:**
* Vstupovat přes **R (retest)** nebo **limitem do F7** po breaku struktury. Vzorky F7 jsou malé (23–73 obchodů na řez), ber je jako indicii, ne jistotu.
* Platné C1/C2 je filtr (cena po něm dojde k originu pohybu v 61–78 % případů), ne vstup trhem: stop pod A je po breaku daleko.
* Samotný reversal (tečka) je jen kontext.

## Instalace (Windows)

**Nejjednodušší:** nainstaluj [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (jednou), zavři ATAS a dvojklikni na **`instalace.bat`** v kořeni složky. Skript indikátor sestaví proti tvé instalaci ATAS, zkopíruje ho do `%APPDATA%\ATAS\Indicators` a přidá kalibrace pro RTH, ETH a celý den. Pak spusť ATAS a přidej indikátor **Reversal & Confirmation Entry**.

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
4. Pro zobrazení změřených pravděpodobností zkopíruj do `%APPDATA%\ATAS\ReversalConfirmation` soubory `calibration\es_m5_rth.json` jako `calibration_rth.json`, `calibration\es_m5_eth.json` jako `calibration_eth.json` a `calibration\es_m5_all.json` jako `calibration.json`. Indikátor si vezme ten podle přepínače *Obchoduji*. Jinou cestu můžeš zadat v parametru *Kalibrační JSON*.
5. Na grafu ES (M5, cluster/footprint data) přidej indikátor **Reversal & Confirmation Entry**. Aby fungovalo srovnání podle času dne, načti aspoň ~12 dní historie (D = 10).

## Co indikátor kreslí

Výchozí zobrazení je minimalistické: agent jen označí svíčky, které nejlépe splňují podmínky obratu. Srozumitelný popis všeho najdeš v brožurce [docs/Brozurka.html](docs/Brozurka.html) (otevři v prohlížeči).

| značka | význam |
|---|---|
| tečka pod low / nad high | reversal (kontext). Velikost a sytost rostou se skóre (60–70 / 70–80 / 80+), kroužek = silný (≥ 75) |
| tečkovaná čára | pivot, který musí padnout (break struktury, BOS): bullish poslední nižší high vlevo od low A, u anomálie (A = outside bar s novým low) high svíčky A. Bearish zrcadlově. Čára pokračuje, dokud setup žije. Šedá = break nepřišel |
| kroužek na konci čáry | break struktury (close za pivotem). Tooltip: F5, F7, SL pod A, TP OP |
| čtvereček **C1 / C2** | potvrzení: druhá strana převzala iniciativu (max 2 na reversal). **Prázdný** = čeká na break (max 30 min), **plný** = platné, **šedý** = break nepřišel |
| čtvereček **R** | retest: vyšší low (u bearish nižší high) blízko extrému se slabou deltou, v datech nejstabilnější místo pro vstup |
| čtvereček **F** | návrat do F5 (61,8 %) impulsu A→B po breaku. Tooltip: F5, F7 (78,9 %), SL pod A, TP OP |

Najetí myší na značku zobrazí tooltip v češtině: skóre, naměřenou úspěšnost (s kalibrací pro dané okno), úroveň, variantu a všech devět podmínek A–I s ● (splněno) / ○ (ne) a konkrétními čísly. U C1/C2 je v tooltipu stav breaku struktury (kdy a kde přišel), cena VPOC svíčky a u platného potvrzení F5/F7/SL/OP pro limit.

Volitelně (v sekci *9. Zobrazení*, ve výchozím stavu vypnuté): vstupní zóny, čáry stopu a targetů, značky × (kontext zrušen) a ! (absorpce selhala), statistický panel, referenční úrovně.

Alerty (zvuk + popup): ve výchozím stavu platné C1/C2, C1/C2 čekající na break, retest R a F. Volitelně break struktury, reversal (standardně jen silné), zóny, fill, dotyk zóny, zrušení kontextu.

## Parametry (výchozí hodnoty)

| skupina | parametr | výchozí |
|---|---|---|
| Session | **obchoduji** | **RTH** (ETH, celý den) |
| | RTH okno · ETH okno (můj čas) · moje pásmo | 15:30–22:12 · 08:00–15:00 · Europe/Prague |
| | časové pásmo burzy / ETH / RTH (úrovně) | America/New_York, 18:00, 09:30–16:00 |
| | VWAP session · profil předchozího dne | ETH · RTH |
| | časy zpráv · okno | 08:30; 10:00; 14:00 ET · ±5 min |
| Úrovně | váhy | prior VWAP 1,0 · session VWAP 1,0 · ±1σ/±2σ 0,6 · 1. RTH svíčka 1,0 · OR15/OR30/IB 0,8 · RTH open 0,8 · předchozí den H/L 0,9, POC 1,0, VAH/VAL 0,9 · overnight 0,8 · pool 0,7 · swing 0,6 · ruční 1,0 |
| | tolerance testu | max(2 ticky, 0,15 × ATR) |
| Reversal | váhy A–I | 8;15;12;8;15;7;7;15;13 |
| | sloučené svíčky · stall | 2 · zapnuto |
| | A: K barů · pohyb · z delty | 6 · 2,0 × ATR · 2 |
| | min. skóre · silný | 60 · 75 |
| | ATR · dní pro time-of-day | ATR14 · 10 |
| Potvrzení | platné až po breaku struktury · čekat na BOS · BOS na close | ano · 30 min · ano |
| | F jen po platném C1/C2 | ne |
| | P · max potvrzení | 6 · 2 |
| | z delty · CLV · efektivita · imbalance · min. skóre | 1 · 0,6 · 40. pct · 3:1 · 60 |
| Zóny | typy | VPOC potvrzení + retest |
| | Q · fill tolerance | 6 · +1 tick |
| | jen po VWAP trendu | vypnuto |
| Stop a targety | stop buffer | max(2 ticky, 0,05 × ATR) |
| | T1 origin · T2 VWAP · T3 úroveň · min. poměr k T1 | zapnuto · zapnuto · zapnuto · 1,2 R |
| Filtry šumu | okno zpráv potlačit · mrtvý trh | ne · ano (< 20. percentil objemu) |
| Logování | CSV · složka · kalibrace · kalibrované váhy | ano · `%APPDATA%\ATAS\ReversalConfirmation\logs` · podle okna `…\calibration_rth.json` / `_eth` · ne |
| Zobrazení | tečka od skóre · C1/C2 · R · F · tečkovaně pivot · neplatná C šedě · tooltip | 60 · ano · ano · ano · ano · ano · ano |
| | zóny · stop/targety · × · ! · panel · úrovně | ne |
| Alerty | C1/C2 platné · čeká na break · R · F · break · reversal · zóny/fill/zrušení | ano · ano · ano · ano · ne · ne · ne |

Všechny prahy jsou percentily, z-score (proti stejnému času dne za posledních 10 dní, s rolling fallbackem) nebo násobky ATR s minimem v ticích. Žádný práh není v kontraktech.

## Logování a kalibrace

Indikátor píše CSV (`%APPDATA%\ATAS\ReversalConfirmation\logs\<instrument>_<timeframe>.csv`) na pozadí a při každém přepočtu ho přepíše celé. Loguje každý reversal se skóre ≥ 40 včetně odmítnutých (s důvodem), každé potvrzení a každou zónu, s komponentami, kontextem a výsledky (MFE/MAE, targety, R). Sloupce popisuje [docs/DESIGN.md](docs/DESIGN.md#log-csv--hlavní-sloupce).

```bash
pip install pandas numpy
python calibration/calibrate.py log.csv --window rth --out calibration_rth.json     # jen RTH okno
python calibration/calibrate.py log.csv --window eth --out calibration_eth.json     # jen evropské dopoledne
python calibration/calibrate.py log.csv --train-until 2026-01-01 --out cal.json     # s hold-outem
```

Log z indikátoru obsahuje jen signály z okna, které máš přepnuté (sloupec `window`). Skript natrénuje logistické modely P(T1 před stopem) pro reversal a pro vyplněnou zónu a validuje je walk-forward (trénink na starších datech, test na novějších). Vypíše AUC a Brier a uloží JSON. Indikátor pak zobrazí **naměřenou** úspěšnost bucketu, do kterého signál padne, a jen pokud bucket má ≥ 30 vzorků. Váhy A–I z kalibrace se použijí jen po zapnutí *Použít kalibrované váhy*.

## Backtest a kalibrace

Pro vlastní ověření nebo jiné roky/timeframy:

```bash
# 1) tick data ES (Rithmic, aggressor side), ~1,4 GB
curl -LO https://huggingface.co/datasets/EdgeArbiter/rithmic-tick-data/resolve/main/es_tick_rithmic_2025.parquet
pip install duckdb pandas numpy
# 2) footprint svíčky (M5; --minutes 1/3/15 pro jiné TF)
python tools/data/build_footprint.py es_tick_rithmic_2025.parquet --out data/fp5
# 3) replay stejným enginem jako v ATAS (parametry přes --set Pole=Hodnota)
dotnet run -c Release --project tools/Replay -- --data data/fp5 --out runs/rth --set Window=Rth
dotnet run -c Release --project tools/Replay -- --data data/fp5 --out runs/eth --set Window=Eth
dotnet run -c Release --project tools/Replay -- --data data/fp5 --out runs/rth_nobos --set Window=Rth --set RequireBos=false
# 4) kalibrace a report
python calibration/calibrate.py runs/rth/log.csv --window rth --out runs/rth/calibration.json
python tools/analysis/report.py runs/rth runs/rth_nobos --names "BOS|bez BOS"
```

Replay zvládne ~140 000 svíček za ~10 s a ošetřuje kvartální rolly.

## Vývoj

```bash
dotnet test tests/Core.Tests -c Release                                          # 34 testů
dotnet build src/Atas/ReversalConfirmation.Atas.csproj -c Release -p:AtasStubs=true  # kontrola kompilace bez ATAS
dotnet build tools/AtasHarness -c Release -p:AtasStubs=true                        # simulace volání ATAS + měření výkonu
```

Testy obsahují scénáře podle akceptačních případů ze zadání: 2-bar flush + reclaim s H, „no reclaim" (Den 2 16:45), „no level" (Den 3 18:45), zrušení kontextu novým low, retest → C2 (Den 3 19:35/19:40), stall přes I (Den 2 16:20). Dál break struktury (C1 čeká a stane se platným na svíčce s breakem, C1 bez breaku do 30 min vyprší, anomálie), obchodní okna RTH/ETH v pražském čase včetně týdnů přechodu času, přesnou zrcadlovou symetrii bullish/bearish, žádný repaint, letní čas USA/EU a statistiky.

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
calibration/      calibrate.py + kalibrace ES M5 2024–2026 (RTH, ETH, celý den)
docs/             DESIGN.md, BACKTEST.md
```
