#!/usr/bin/env python3
"""
Build footprint bars (bid/ask volume per price level) from ES tick data.

Input : parquet files with columns aggressor ('Buy'/'Sell'), price, volume, timestamp (UTC)
        e.g. https://huggingface.co/datasets/EdgeArbiter/rithmic-tick-data
Output: one gzip text file per month in --out, format understood by tools/Replay:

    B,<bar_open_epoch_s_utc>,<open>,<high>,<low>,<close>,<trades>
    L,<price>,<bid_volume>,<ask_volume>
    L,...

'Buy' aggressor = trade lifted the offer = ASK volume, 'Sell' = hit the bid = BID volume.
Trades without aggressor (<0.01 %) are ignored.
"""
import argparse, gzip, os
import duckdb

ap = argparse.ArgumentParser()
ap.add_argument("inputs", nargs="+")
ap.add_argument("--out", required=True)
ap.add_argument("--minutes", type=int, default=5)
ap.add_argument("--start", default=None, help="UTC date inclusive, e.g. 2024-07-07")
ap.add_argument("--end", default=None, help="UTC date exclusive")
args = ap.parse_args()
os.makedirs(args.out, exist_ok=True)

con = duckdb.connect()
con.execute("set TimeZone='UTC'")
files = ",".join(f"'{p}'" for p in args.inputs)
where = ["aggressor in ('Buy','Sell')"]
if args.start: where.append(f"timestamp >= TIMESTAMP '{args.start}'")
if args.end:   where.append(f"timestamp <  TIMESTAMP '{args.end}'")
w = " and ".join(where)
sec = args.minutes * 60

src = f"read_parquet([{files}], file_row_number=true, filename=true)"
months = [r[0] for r in con.execute(
    f"select distinct strftime(timestamp, '%Y-%m') from read_parquet([{files}]) where {w} order by 1").fetchall()]
for m in months:
    # one month at a time keeps DuckDB's temp storage small
    y, mo = map(int, m.split("-"))
    nxt = f"{y + (mo == 12)}-{mo % 12 + 1:02d}-01"
    # range predicate (not strftime) so parquet row-group statistics can prune
    mw = f"{w} and timestamp >= TIMESTAMP '{m}-01' and timestamp < TIMESTAMP '{nxt}'"
    bars = con.execute(f"""
        select (epoch(timestamp)::bigint // {sec}) * {sec} bt,
               arg_min(price, (timestamp, filename, file_row_number)) o, max(price) h, min(price) l,
               arg_max(price, (timestamp, filename, file_row_number)) c, count(*) n
        from {src} where {mw} group by 1 order by 1""").fetchall()
    lvls = con.execute(f"""
        select (epoch(timestamp)::bigint // {sec}) * {sec} bt, price,
               sum(case when aggressor='Sell' then volume else 0 end) bid,
               sum(case when aggressor='Buy'  then volume else 0 end) ask
        from {src} where {mw} group by 1, 2 order by 1, 2""").fetchall()
    path = os.path.join(args.out, f"fp_m{args.minutes}_{m}.txt.gz")
    j = 0
    with gzip.open(path, "wt", compresslevel=6) as f:
        for bt, o, h, l, c, n in bars:
            f.write(f"B,{bt},{o:g},{h:g},{l:g},{c:g},{n}\n")
            while j < len(lvls) and lvls[j][0] == bt:
                _, p, b, a = lvls[j]
                f.write(f"L,{p:g},{int(b)},{int(a)}\n")
                j += 1
    print(m, len(bars), "bars", path)
