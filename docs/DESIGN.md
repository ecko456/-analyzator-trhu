# Návrh a rozhodnutí

Dokument popisuje, jak je zadání „ATAS indikátor – Reversal & Confirmation Entry" implementované. Uvádí také, kde bylo zadání nejednoznačné nebo si odporovalo a jak jsem to rozhodl. Pro každé rozhodnutí je uveden důvod, u odchylek od zadání i data.

## Architektura

```
src/Core      engine bez závislosti na ATAS (sdílený s backtestem a testy)
  Bars.cs         footprint svíčka, orientovaný pohled (bearish = zrcadlo bullish), slučování svíček
  Stats.cs        rolling statistiky, percentily, time-of-day baseline, ATR, Spearman
  Session.cs      session kalendář v ET (DST správně), okna zpráv
  Levels.cs       referenční úrovně (VWAP, pásma, OR/IB, předchozí den, overnight, pooly, swingy, ruční)
  Detector.cs     vrstva 1: komponenty A–I, varianty 1-bar / 2-bar / 3-bar / stall, gate, skóre
  Engine.cs       vrstva 2: kontexty, break struktury, potvrzení C1/C2 (čeká → platné / vypršelo), retest, F5/F7, zóny, stop/targety, obchodní okno
  Outcomes.cs     sledování výsledků (MFE/MAE, targety, R) a CSV schéma
  Calibration.cs  načtení calibration.json (váhy, logistický model, tabulka spolehlivosti)
  Logging.cs      neblokující CSV writer (vlákno na pozadí)
src/Atas      tenký adaptér: ATAS svíčky → engine, vykreslení, alerty, parametry v UI
tools/Replay  backtest: footprint soubory → stejný engine → CSV log
tools/AtasHarness  simulace volání ATAS (historie, ticky, render) na reálných datech: měření výkonu
calibration/calibrate.py  kalibrace + walk-forward validace
```

**Bearish = přesné zrcadlo bullish.** Logika je napsaná jen pro bullish. Pro bearish se svíčka zrcadlí: ceny se znegují a bid se prohodí s ask (`OBar`). Test `BearishIsAnExactMirrorOfBullish` hlídá, že zrcadlová data dají zrcadlové signály se stejným skóre.

**Žádný repaint.** Značky (`Mark`) se jen přidávají a nikdy se nemění. Zóna má neměnnou cenu, typ, začátek a skóre. Posouvá se jen její životní cyklus (aktivní → vyplněná / vypršela / zrušena). Test `NoRepaint_...` to kontroluje po každé svíčce.

## Výkon (ATAS nesmí zamrzat)

* Každá svíčka se analyzuje **jednou, při uzavření**. Ticky tvořící se svíčky nedělají nic, kromě volitelného alertu na dotyk zóny (smyčka přes ≤ 4 aktivní zóny).
* Práce na svíčku je O(počet cenových úrovní + krátká okna), bez přepočtu historie. Percentily drží seřazená okna (binární vyhledávání), time-of-day baseline ring buffery.
* Vykreslení prochází jen viditelné svíčky (binární vyhledávání v seřazených značkách). Fonty a formáty se vytvářejí jednou.
* CSV se zapisuje na vlákně na pozadí. Při plné frontě se řádky zahodí, graf nikdy nečeká na disk.
* Změřeno simulátorem volání ATAS na reálných datech ES (`tools/AtasHarness`):

| operace | čas |
|---|---|
| načtení historie 2 měsíců M5 (8 700 svíček) | 1,1 s |
| načtení historie 6 měsíců M5 (32 000 svíček) | 2,6 s |
| tick na tvořící se svíčce | 0,1 µs (p99 0,3 µs) |
| uzavření svíčky | medián 0,07–0,12 ms, max ~1,7 ms |
| vykreslení (150 viditelných svíček) | medián 10–16 µs |

## Rozpory v zadání a jejich řešení

| # | Problém | Řešení |
|---|---|---|
| 1 | Váhy dávají dohromady 100, ale H (15) a I (13) platí jen pro flush a stall. Jednobarový reversal by tak měl maximum 72 a nikdy by nebyl „silný" (≥ 75), přestože akceptační případ Den 1 16:15 silný být má. | Skóre = 100 · Σ wᵢ·sᵢ / Σ wᵢ **jen přes komponenty, které varianta může splnit**: 1-bar A–G, 2/3-bar A–H, stall A–G + I. Projde-li víc variant, platí vyšší skóre. |
| 2 | Den 3 C2 v 19:40 je 7. svíčka po reversalu 19:05, přitom P = 6. | P se počítá **od posledního „nabití" čekání**: od reversalu, od retestu nebo od vypršení zóny. Odpovídá to diagramu (zóna vypršela → další potvrzení) i případu R → C2. |
| 3 | „30 % / 40 % delty flush baru" nemá u 1-bar a stall varianty referenci. | Reference = nejzápornější delta (v orientovaném směru) svíčky celého pohybu včetně vzoru (okno komponenty A). U 2-bar to je flush. |
| 4 | „Nehonit … žádný tržní vstup se nenavrhuje", jenže zadání tržní vstupy nikde nenavrhuje. | Pravidlo nemá co omezovat, neimplementováno. Backtest potvrdil, že vstup trhem na close potvrzení nemá výhodu. |
| 5 | Den 1 16:05 „prior VWAP neotestován": low 7754 je 0,34 b od 7753,66, což je v toleranci max(2 t, 0,15 ATR). | Tolerance podle zadání, gate projde. Očekávání „nejvýše slabý kandidát" (skóre < 60) je s tím v souladu. |
| 6 | Konfluence „+max 30 %" u B s omezením na 1 se u úrovně s vahou 1,0 neprojeví. | s_B = min(1, váha × blízkost × (1 + min(0,3; 0,15 · počet dalších úrovní v toleranci))). Konfluence zvyšuje B u slabších úrovní. |
| 7 | „Návrat za > 2 bary = neplatné": jde o nulovou komponentu, nebo neplatný signál? | Neplatný signál (gate „slow reclaim"): 3+ uzavření za úrovní znamená, že trh úroveň přijal. 0–1 bar = plné skóre, 2 bary = poloviční. |
| 8 | Efektivita ceny: v ticích na kontrakt delty jsou tiché noční svíčky vždy „efektivnější" než silné RTH svíčky. | Efektivita = (pohyb / ATR) / (|delta| / σ delty pro daný čas dne). U potvrzení se porovnává **jen se svíčkami s iniciativou** (|z| ≥ 1): „kupci byli agresivní, posunuli cenu tolik co obvykle?". |
| 9 | Spearman u F nemá znaménko. | Vyčerpání = agresivní objem proti směru reversalu klesá směrem k extrému: ρ(vzdálenost od extrému, objem) ≥ 0,5. |
| 10 | „Libovolný typ grafu": u tick/volume/range grafů nedává time-of-day srovnání smysl. | U nečasových grafů se automaticky použije rolling okno N svíček (fallback ze zadání). Stav je vidět v panelu. |
| 11 | Časy session: pražský čas se mění jinak než americký. Mezi 25. 10. a 1. 11. 2026 otevírá RTH ve 14:30 pražského času. | Session, RTH, OR/IB i zprávy jsou v **America/New_York** (nastavitelné). Test pokrývá oba přechody. |
| 12 | Liquidity pool: definice „swing low" by vyřadila případ Den 3 (dvě sousední svíčky se stejným low). | Pool = ≥ 2 svíčky za posledních 24, jejichž low leží v pásmu ≤ max(2 t, 0,1 ATR) a které od prvního dotyku nikdo neprorazil. |

## Odchylky a doplňky oproti zadání (a proč)

| Změna | Důvod |
|---|---|
| Časy zpráv ve výchozím nastavení **08:30**; 10:00; 14:00 ET | 08:30 (CPI, NFP, claims) jsou největší zprávy pro ES. Okno se jen označí, potlačení je volitelné. |
| **Obchodní okno** „Obchoduji": RTH 15:30–22:12 nebo ETH 08:00–15:00 (čas obchodníka, `Europe/Prague`), výchozí RTH. Nahrazuje původní „Signály v session". | Přání obchodníka: RTH a evropské dopoledne mají jinou volatilitu i objemy a ostatní časy kalibraci jen rozhodí. Mimo okno se nehledají signály a otevřené kontexty se zavřou (`window end`); svíčky dál krmí úrovně a statistiky. RTH okno se převádí na čas burzy přes standardní rozdíl časových pásem (Praha − New York = 6 h), takže v týdnech přechodu času začíná ve 14:30. |
| **Kalibrace zvlášť pro každé okno** (`calibration_rth.json`, `calibration_eth.json`, celý den `calibration.json`) | `calibrate.py --window rth/eth` bere jen řádky z okna. Indikátor podle přepínače načte správný soubor a v panelu upozorní, když soubor patří jinému oknu. |
| **Potvrzení platí až po breaku struktury (BOS)** | Metoda obchodníka (schémata `reversal_*.svg` na `main`). Bullish: close nad prvním pivot high vlevo od low A (svíčka, jejíž high je nad svíčkou nalevo); anomálie: svíčka A s novým low a zároveň high nad předchozí svíčkou → láme se high svíčky A. Bearish zrcadlově. Úroveň se určí v okamžiku reversalu z uzavřených svíček a už se nemění. |
| **Čekání na BOS 30 minut** po order-flow svíčce | Přání obchodníka. Do té doby prázdný čtvereček (pending), pak plný (platné, `ValidBar` = svíčka s breakem) nebo šedý (expired). Stav se mění jen dopředu, poloha a popisek značky nikdy. Alert, statistika i zóna vznikají až na svíčce s breakem. |
| **Tečkovaná čára pivotu + kroužek na breaku** | Obchodník chce vidět předem, co se tvoří a na jaký break se čeká. Čára žije, dokud může vzniknout platný signál; kroužek nese v tooltipu F5/F7/SL/OP. |
| **Čtvereček F: návrat do F5 po BOS** (SL pod A, TP = OP), výchozí bez nutnosti C1 | Vstup ze schémat obchodníka. Backtest: F7 kladné ve všech řezech, s order-flow potvrzením ne lépe ([BACKTEST](BACKTEST.md#break-struktury-f5f7-a-obchodní-okna-aktuální-verze)). |
| **Market / volume profile úrovně**: dnešní developing VA, tento a předchozí týden, nahé VAH/VAL/POC (10 dní), VA z objemu nebo TPO | Přání obchodníka. Profil se drží v ticích (`Levels.cs`, třída `Profile`), TPO se počítá přírůstkově po svíčkách (cena dostane TPO jednou za 30min periodu, od low do high periody jako v MP). Value area: od POC po jednom ticku k většímu sousedovi do 70 %. Developing VA se bere z dat před hodnocenou svíčkou. Nahé úrovně: VAH/VAL/POC denního profilu se začnou hlídat od jeho uzavření (u RTH profilu od 16:00 ET), dotyk = svíčka s low ≤ úroveň ≤ high; předchozí den je samostatná úroveň, nahé jsou až od předevčírka. Týden = CME týden (nedělní večer patří k pondělí). Váhy umírněné, protože backtest neukázal lepší ani horší kvalitu ([BACKTEST](BACKTEST.md#market-a-volume-profile-úrovně)). |
| **Break režim od 17:45** (čas obchodníka): čtvereček B na svíčce s breakem, skóre z order flow této svíčky | Přání obchodníka: odpoledne obchoduje čistě break struktury. Hodnocení používá stejné složky jako C (`Measure`), nesplněné brány C jen snižují skóre. Kontext v tomto režimu čeká na break až `BosMaxBars` od extrému a tečkovaná čára žije stejně dlouho. Čekající C z doby před 17:45 se po breaku normálně stane platným. Log `kind = BRK`. |
| **Anomálie = outside bar** min. o 1 tick na obou stranách svíčky nalevo | Upřesnění obchodníka. Svíčka A se stejným low jako svíčka nalevo anomálií není (BOS je pak pivot high). |
| **Plán vstupu a řízení rizika** v tooltipu kroužku breaku, platného C, B a F: limit 1 tick před F5/F7, SL 1 tick za A, ticky SL, počet ES/MES z rizika na obchod, OP pro C v F5 a v F7 | Přání obchodníka (nastavení skupina 11). OP = C + A→B, C = extrém korekce; v backtestu se target posouvá s každým novým extrémem korekce (`Tracker.AdaptiveRange`). |
| Reversal stejného směru **nenahradí** kontext, který už má potvrzení z dřívější svíčky | Jinak by nový bod uprostřed čekání na break zrušil rozjetý C1 a přepsal čáru, na kterou se obchodník dívá. Nižší low kontext ruší dál. |
| Parametr **„Jen zóny po směru VWAP trendu"**, výchozí vypnuto | Lepší v části řezů, ne konzistentně ([BACKTEST](BACKTEST.md)). |
| Parametr **ATR: hybrid**, výchozí klasický ATR14 | ATR14 kolem RTH open podhodnocuje volatilitu 2,6× (změřeno). Hybrid ale v backtestu lepší nebyl. |
| Kalibrace = **logistický model + tabulka spolehlivosti** místo pouhých bucketů skóre | Zobrazuje se naměřená úspěšnost bucketu modelu (min. 30 vzorků), ne odhad. Váhy A–I z kalibrace jen volitelně. |
| **Statistický panel** i podle typu zóny a s výstupem 1,5R | Backtest ukázal, že rozdíl mezi retest a VPOC zónou je zásadní, proto je vidět přímo v grafu. |
| Alert „zrušení kontextu" jen u kontextu, který už měl zónu | Jinak to byl spam: jen ~15 % reversalů dojde k zakreslené zóně. |
| Alert **„dotyk zóny (intrabar)"**, výchozí vypnutý | Oficiální fill se vyhodnotí na close, ale limitku chceš vědět hned. |

## Log (CSV) – hlavní sloupce

`kind` = REV (reversal), REJ (odmítnutý kandidát s důvodem v `reject`), CONF (platné potvrzení; řádek vzniká na svíčce, kterou se stalo platným, `conf_bar` = order-flow svíčka, `conf_wait_bars` = čekání na break), ZONE (zóna; `filled`, `bars_to_fill`, `fill_delta_z`), FIB (návrat do F5/F7 po BOS: `fib_level`, `fib_a`, `fib_b`, `fib_range_atr`, `fib_bars`, `had_conf`; T1 = OP).
Úroveň: `level_profile`, `level_naked`, `level_age`, `profile_confluence` (profilové úrovně u extrému).
Break struktury: `bos_level`, `bos_anomaly`, `bos_bars` (od extrému), `bos_dist_atr`. Okno: `window` (rth / eth / prázdné).
Features: `s_A`…`s_I`, surové hodnoty (`drop_atr`, `min_z`, `sweep`, `overshoot_atr`, `div1`, `div2`, `flip`, `poc_pos`, `third_pct`, `rho`, `fin_auction`, `clv`, `flush_z`, `stall_*`), úroveň (`level`, `level_dist_ticks`, `confluence`, `bars_beyond`, `test_order`), kontext (`vwap_side`, `vwap_slope_atr`, `vwap_dist_atr`, `min_from_rth`, `news`).
Výsledky: `mfe_3…36`, `mae_3…36` (ticky), `hit_T1/T2/T3/R15/R2` (+ `hit_tol_*`, `bars_*`, `mindist_*`), `stop_hit`, `result_r` (výstup T1), `result_r15` (výstup 1,5R), `class` (v-reversal / base-breakout / failure), `breakout_vol_pct`, `p_model`.
Záznamy spojuje `context_id`.
