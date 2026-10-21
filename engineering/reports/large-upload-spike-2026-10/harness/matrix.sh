cd "$(dirname "$0")"
E=http://127.0.0.1:5201
cfg() { curl -s -XPOST "$E/config?$1" > /dev/null; }
run() { echo "== $*" >&2; timeout 3600 node run.mjs "$@" >> results.jsonl 2>> matrix.err; }
D="--page=http://127.0.0.1:5201/"
R="--page=http://127.0.0.1:5200/ --base=/relay"
cfg "sink=file&mbps=0"
# 1. Baseline, unthrottled loopback (machine limit), real random data
run --file=src/random-1g.bin --mode=plain $D
run --file=src/random-1g.bin --mode=chunked $D
run --file=src/random-1g.bin --mode=plain $R
run --file=src/random-1g.bin --mode=chunked $R
# 2. 10 GB, emulated gigabit LAN (110 MB/s), through the relay, written to disk
cfg "mbps=110"
run --file=src/sparse-10g.bin --mode=plain $R --plainAttempts=1
run --file=src/sparse-10g.bin --mode=chunked $R
cfg "mbps=30"
run --file=src/sparse-10g.bin --mode=plain $R --plainAttempts=1
cfg "mbps=110"
# 3. Dropped connection at 60% of 10 GB
run --file=src/sparse-10g.bin --mode=plain $R --dropAt=6442450944 --plainAttempts=2
run --file=src/sparse-10g.bin --mode=chunked $R --dropAt=6442450944
# 4. Cancel at 30%
run --file=src/sparse-10g.bin --mode=chunked $R --cancelAt=0.3
# 5. Chunk size vs requests, unthrottled 10 GB direct
cfg "mbps=0"
run --file=src/sparse-10g.bin --mode=chunked $D --chunk=8
run --file=src/sparse-10g.bin --mode=chunked $D --chunk=64
run --file=src/sparse-10g.bin --mode=chunked $D --chunk=256
# 6. 50 GB through the relay, emulated gigabit, discard sink (cloud disk is 29 GB)
cfg "sink=discard&mbps=110"
run --file=src/sparse-50g.bin --mode=chunked $R
run --file=src/sparse-50g.bin --mode=chunked $R --dropAt=32212254720
cfg "sink=file&mbps=0"
echo done >&2
