import sys

print("intentional failure", file=sys.stderr, flush=True)
raise SystemExit(7)
