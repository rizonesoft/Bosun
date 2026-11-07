#!/bin/bash
# WSL integration test for the shim. Uses a private slot namespace.
# usage: queue_test.sh <path to bosun.exe>
set -u
export BOSUN_EXE="$1"
SHIM="$(cd "$(dirname "$0")" && pwd)/bosun"
NS="BosunWslTest$$"
WORK="$(mktemp -d)"
fail=0
check() { if [ "$1" = "0" ]; then echo "ok   $2"; else echo "FAIL $2"; fail=$((fail + 1)); fi; }
busy() { "$BOSUN_EXE" status --ns "$NS" --slots "${1:-1}" | tr -d '\r' | head -1; }

job() { "$SHIM" run --ns "$NS" --slots 2 --min-avail-mb 0 --min-guest-mb 0 -- bash -c "echo start \$(date +%s%N) >> $WORK/o.txt; sleep 1; echo end \$(date +%s%N) >> $WORK/o.txt"; }
for _ in 1 2 3 4 5; do job & done
wait
max=$(sort -k2 -n "$WORK/o.txt" | awk '$1=="start"{c++; if(c>m)m=c} $1=="end"{c--} END{print m}')
[ "$max" = "2" ]; check $? "5 jobs on 2 slots overlap by exactly 2 (got $max)"

"$SHIM" run --ns "$NS" --min-avail-mb 0 -- bash -c 'exit 37'
[ $? = 37 ]; check $? "exit code passes through"

"$SHIM" run --ns "$NS" --min-avail-mb 0 -- bash -c 'printf "%s|%s" "$1" "$BOSUN_SLOT" > "$2"' _ 'a b' "$WORK/arg.txt"
[ "$(cat "$WORK/arg.txt")" = "a b|0" ]; check $? "arguments and BOSUN_SLOT reach the command"

# BOSUN_SLOTS reaches bosun.exe, which cannot see Linux variables by itself.
BOSUN_SLOTS=1 "$SHIM" run --ns "$NS" --min-avail-mb 0 -- sleep 3 &
first=$!
sleep 1
BOSUN_SLOTS=1 "$SHIM" run --ns "$NS" --min-avail-mb 0 --max-wait 1 -- bash -c 'printf "%s" "$BOSUN_SLOT" > "$1"' _ "$WORK/slots.txt" 2>/dev/null
[ "$(cat "$WORK/slots.txt")" = "-1" ]; check $? "BOSUN_SLOTS=1 makes a second job wait, and max-wait lets it through"
wait "$first"

# status honours BOSUN_SLOTS too, and a nested shim keeps an explicit BOSUN_EXE.
got="$(BOSUN_SLOTS=3 "$SHIM" status --ns "$NS" | tr -d '\r' | head -1)"
[ "$got" = "slots busy: 0 of 3" ]; check $? "status uses BOSUN_SLOTS (got $got)"
got="$("$SHIM" run --ns "$NS" --min-avail-mb 0 -- bash -c 'printf "%s" "$BOSUN_EXE"')"
[ "$got" = "$BOSUN_EXE" ]; check $? "a child inherits the explicit BOSUN_EXE"

# One deadline covers both waits, so a job that timed out on the slot does not wait again for guest memory.
"$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 -- sleep 4 &
first=$!
sleep 1
SECONDS=0
"$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 --min-guest-mb 99999999 --max-wait 1 -- true 2>/dev/null
elapsed=$SECONDS
[ "$elapsed" -le 2 ]; check $? "a timed-out job does not wait a second time (took $elapsed s)"
wait "$first"

# SIGKILL of the shim must end the command and free the slot.
"$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 -- bash -c 'echo $$ > "$1"; exec sleep 30' _ "$WORK/child.pid" &
pid=$!
sleep 2
[ "$(busy 1)" = "slots busy: 1 of 1" ]; check $? "a WSL job holds a slot visible on Windows"
child="$(cat "$WORK/child.pid")"
kill -0 "$child" 2>/dev/null; check $? "the job's command is running"
kill -9 "$pid" 2>/dev/null
wait "$pid" 2>/dev/null
sleep 2
if kill -0 "$child" 2>/dev/null; then r=1; kill -9 "$child"; else r=0; fi
check "$r" "killing the shim ends its command"
[ "$(busy 1)" = "slots busy: 0 of 1" ]; check $? "killing the shim frees the slot"

# A nested run passes straight through and really runs the command.
timeout 15 "$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 --max-wait 0 -- "$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 --max-wait 0 -- touch "$WORK/nested"
r=$?
[ "$r" = 0 ] && [ -f "$WORK/nested" ]; check $? "nested run does not deadlock (exit $r)"

# The nesting marker crosses to Windows too.
timeout 15 "$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 --max-wait 0 -- "$BOSUN_EXE" run --ns "$NS" --slots 1 --min-avail-mb 0 --max-wait 0 -- cmd.exe /d /c exit 7 </dev/null
r=$?
[ "$r" = 7 ]; check $? "a nested run on Windows does not deadlock (exit $r)"

# A broken slot service runs the job with a warning rather than failing it.
BOSUN_EXE=/bin/false "$SHIM" run --ns "$NS" -- bash -c 'exit 5' 2>/dev/null
[ $? = 5 ]; check $? "job still runs if the slot service is unavailable"

rm -rf "$WORK"
[ "$fail" = 0 ] && echo "all passed" || { echo "$fail failed"; exit 1; }
