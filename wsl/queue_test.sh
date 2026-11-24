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

# --or-skip starts nothing without its place or its memory, and a job in one namespace takes its place in another.
"$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 -- sleep 12 &
first=$!
sleep 1
"$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 --max-wait 1 --or-skip -- touch "$WORK/skip1" 2>/dev/null
got=$?
[ "$got" = 75 ] && [ ! -e "$WORK/skip1" ]; check $? "--or-skip exits 75 and starts nothing while the place is held (got $got)"
"$SHIM" run --ns "${NS}Outer" --slots 1 --min-avail-mb 0 -- "$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 --max-wait 1 --or-skip -- touch "$WORK/cross" 2>/dev/null
got=$?
[ "$got" = 75 ] && [ ! -e "$WORK/cross" ]; check $? "a job inside one namespace waits for its place in another (got $got)"
wait "$first"
"$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 99999999 --max-wait 1 --or-skip -- touch "$WORK/skip2" 2>/dev/null
got=$?
[ "$got" = 75 ] && [ ! -e "$WORK/skip2" ]; check $? "--or-skip holds the memory floor for the first slot too (got $got)"
"$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 --max-wait 1 --or-skip -- touch "$WORK/skip3"
[ -e "$WORK/skip3" ]; check $? "--or-skip runs the job when its place is free"
"$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 -- "$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 --max-wait 1 --or-skip -- touch "$WORK/same"
[ -e "$WORK/same" ]; check $? "a nested job in the same namespace still passes through"

"$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 -- sleep 6 &
first=$!
sleep 1
"$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 --max-wait 1 -- "$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 --max-wait 1 --or-skip -- touch "$WORK/late" 2>/dev/null
got=$?
[ "$got" = 75 ] && [ ! -e "$WORK/late" ]; check $? "a job that started without a place lends none to a job inside it (got $got)"
wait "$first"
"$SHIM" run --ns 'A;B' --slots 1 --min-avail-mb 0 -- true 2>/dev/null
[ $? = 64 ]; check $? "a namespace holding the separator is refused"

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

# A $ in the command reaches it unexpanded: the scope the job runs in must not rewrite it.
got="$("$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 -- printf '%s' 'a$HOME$$b')"
[ "$got" = 'a$HOME$$b' ]; check $? "a \$ in the command arrives intact (got $got)"

# Whatever a job leaves running ends with it, even a child that detached itself into its own session.
"$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 -- bash -c 'setsid sleep 300 >/dev/null 2>&1 < /dev/null & echo $! > "$1"' _ "$WORK/orphan.pid"
sleep 1
orphan="$(cat "$WORK/orphan.pid")"
if kill -0 "$orphan" 2>/dev/null; then r=1; kill -9 "$orphan"; else r=0; fi
check "$r" "a child the job left behind is stopped when the job ends"

# A child that writes without end is stopped at the write budget while the command itself sits idle.
probe="bosun-probe-$$"
io_ok=$(systemd-run --user --scope --quiet --collect --slice=bosun-jobs.slice --unit="$probe" -p IOAccounting=yes -- bash -c \
  "test -e /sys/fs/cgroup/user.slice/user-$(id -u).slice/user@$(id -u).service/bosun.slice/bosun-jobs.slice/$probe.scope/io.stat && echo yes")
if [ "$io_ok" = yes ]; then
  SECONDS=0
  "$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 --max-write-gb 1 -- bash -c \
    '(dd if=/dev/zero bs=1M count=4096 oflag=direct status=none > "$1"; sleep 120) & sleep 120' _ "$WORK/flood.bin" 2>/dev/null
  r=$?
  size=$(stat -c %s "$WORK/flood.bin")
  [ "$r" = 74 ] && [ "$SECONDS" -lt 60 ]; check $? "a job over its write budget is stopped with 74 (exit $r after $SECONDS s)"
  [ "$size" -lt $((3 * 1073741824)) ]; check $? "the runaway was stopped near the budget ($size bytes)"
  rm -f "$WORK/flood.bin"
else
  echo "skip the WSL write budget: user jobs have no io controller (sudo ~/.local/share/bosun/enable-write-budget.sh turns it on)"
fi

# A nested run and one without a slot are contained too: a slot is about memory, containment about the work.
BOSUN_SLOT=0 "$SHIM" run --ns "$NS" -- bash -c 'setsid sleep 300 >/dev/null 2>&1 < /dev/null & echo $! > "$1"' _ "$WORK/nested.pid"
BOSUN_EXE=/bin/false "$SHIM" run --ns "$NS" -- bash -c 'setsid sleep 300 >/dev/null 2>&1 < /dev/null & echo $! > "$1"' _ "$WORK/noslot.pid" 2>/dev/null
sleep 1
for f in nested noslot; do
  p="$(cat "$WORK/$f.pid")"
  if kill -0 "$p" 2>/dev/null; then r=1; kill -9 "$p"; else r=0; fi
  check "$r" "a $f run stops what its command left behind"
done

# A nested run stays inside the outer job's scope, so the outer job still counts and stops all of it.
got="$("$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 -- "$SHIM" run --ns "$NS" -- cat /proc/self/cgroup)"
case "$got" in */bosun-job-*.scope) r=0 ;; *) r=1 ;; esac
outer=$("$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 -- bash -c 'cat /proc/self/cgroup; "$1" run --ns "$2" -- cat /proc/self/cgroup' _ "$SHIM" "$NS" | sort -u | wc -l)
[ "$r" = 0 ] && [ "$outer" = 1 ]; check $? "a nested run stays in the outer job's scope"

# Queued jobs run in bosun-jobs.slice and nothing else does, so the pressure monitor throttles only them (D05 T02 §5).
if systemctl --user show-environment >/dev/null 2>&1; then
  job_cg="$("$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 -- cat /proc/self/cgroup)"
  case "$job_cg" in */bosun-jobs.slice/bosun-job-*.scope) r=0 ;; *) r=1 ;; esac
  check "$r" "a job's scope is under bosun-jobs.slice (got $job_cg)"
  sibling_cg="$(systemd-run --user --scope --quiet --collect -- cat /proc/self/cgroup)"
  case "$sibling_cg" in */app.slice/*.scope) r=0 ;; *) r=1 ;; esac
  case "$sibling_cg" in *bosun-jobs.slice*) r=1 ;; esac
  check "$r" "a sibling scope stays in app.slice, outside bosun-jobs.slice (got $sibling_cg)"
else
  echo "skip the bosun-jobs.slice check: no systemd user session"
fi

# A budget with a leading zero is read as decimal.
"$SHIM" run --ns "$NS" --slots 1 --min-avail-mb 0 --max-write-gb 08 -- true
[ $? = 0 ]; check $? "--max-write-gb 08 is accepted as eight"

# A broken slot service runs the job with a warning rather than failing it.
BOSUN_EXE=/bin/false "$SHIM" run --ns "$NS" -- bash -c 'exit 5' 2>/dev/null
[ $? = 5 ]; check $? "job still runs if the slot service is unavailable"

rm -rf "$WORK"
[ "$fail" = 0 ] && echo "all passed" || { echo "$fail failed"; exit 1; }
