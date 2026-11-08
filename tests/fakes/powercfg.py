"""A stand-in for powercfg.exe over a JSON file named by BOSUN_FAKE_POWERCFG, for the campaign hold tests.

Answers /getactivescheme, /q <scheme> <sub> <setting>, /setacvalueindex and /setdcvalueindex <scheme> <sub> <setting>
<value>, and /setactive <scheme>, in powercfg's own output format. With BOSUN_FAKE_POWERCFG_FAIL set, every set
command fails, as a locked-down policy would make it.
"""
import json
import os
import sys

path = os.environ["BOSUN_FAKE_POWERCFG"]
with open(path, encoding="utf-8") as f:
    state = json.load(f)
args = [a.lower() for a in sys.argv[1:]]
scheme = state["scheme"]


def save():
    with open(path + ".tmp", "w", encoding="utf-8", newline="\n") as f:
        json.dump(state, f, indent=2, sort_keys=True)
        f.write("\n")
    os.replace(path + ".tmp", path)


if args[:1] == ["/getactivescheme"]:
    print(f"Power Scheme GUID: {scheme}  (Balanced)")
elif args[:1] == ["/q"] and len(args) == 4:
    key = f"{args[2]}/{args[3]}".upper()
    if args[1] != scheme or key not in state["values"]:
        print("Invalid Parameters -- try \"/?\" for help")
        sys.exit(1)
    v = state["values"][key]
    print(f"    Current AC Power Setting Index: 0x{v['AC']:08x}")
    print(f"    Current DC Power Setting Index: 0x{v['DC']:08x}")
elif args[:1] in (["/setacvalueindex"], ["/setdcvalueindex"]) and len(args) == 5:
    key = f"{args[2]}/{args[3]}".upper()
    if os.environ.get("BOSUN_FAKE_POWERCFG_FAIL") or args[1] != scheme or key not in state["values"]:
        print("An unexpected error condition has occurred.")
        sys.exit(1)
    state["values"][key]["AC" if args[0] == "/setacvalueindex" else "DC"] = int(args[4])
    save()
elif args[:1] == ["/setactive"]:
    pass
else:
    print(f"fake powercfg: unsupported {sys.argv[1:]}")
    sys.exit(2)
