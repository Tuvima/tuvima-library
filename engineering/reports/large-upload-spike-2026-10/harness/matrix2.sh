cd "$(dirname "$0")"
E=http://127.0.0.1:5201
cfg() { curl -s -XPOST "$E/config?$1" > /dev/null; }
run() { curl -s -XPOST http://127.0.0.1:5200/clean >/dev/null; echo "== $*" >&2; timeout 3600 node run.mjs "$@" >> results2.jsonl 2>> matrix.err; }
R="--page=http://127.0.0.1:5200/ --base=/relay"
cfg "sink=file&mbps=110"
run --file=src/random-1g.bin --mode=plain $R
run --file=src/random-1g.bin --mode=chunked $R
cfg "mbps=30"
run --file=src/random-1g.bin --mode=chunked $R
run --file=src/sparse-10g.bin --mode=chunked $R --dropAt=6442450944
cfg "sink=file&mbps=0"
echo done2 >&2
