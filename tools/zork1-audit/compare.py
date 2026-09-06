#!/usr/bin/env python3
"""Compare our zork1 item texts against the original, item by item.

Requires zork_objects.json (extract.py) and our_objects.json (ours.py),
and mapping.json — the explicit, curated table of our object ids to ZIL
object names. Checks three fields per item:

  NAME   our `name`            vs zil DESC
  FDESC  our `firstDescription` vs zil FDESC
  LDESC  our `description`      vs zil LDESC

Invented prose on desc-only zil items counts as an LDESC problem, so the
audit enforces "verbatim or nothing". Documented deviations live in
mapping.json (accepted_name / accepted_ldesc sets below must mirror them).

Exit status: 0 when clean, 1 otherwise — usable as a check script.
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

zil = json.load(open(os.path.join(HERE, 'zork_objects.json')))
ours = json.load(open(os.path.join(HERE, 'our_objects.json')))
mapping = json.load(open(os.path.join(HERE, 'mapping.json')))
mapping = {k: v for k, v in mapping.items()
           if not k.startswith('#') and not k.endswith('-name') and not k.endswith('-note')}

# deliberate deviations — keep mapping.json's notes in sync
accepted_name = {'player', 'front_door', 'sceptre'}
accepted_ldesc = {'lunch'}  # zil ldesc renders only in room listings; examine says "nothing special"


def norm(s):
    if s is None:
        return None
    return re.sub(r'\s+', ' ', s).strip()

problems = 0
for oid in sorted(mapping):
    zname = mapping[oid]
    o = ours.get(oid)
    if o is None:
        print(f"!! {oid}: mapped but MISSING in our world")
        problems += 1
        continue
    if zname is None:
        continue  # deliberate invention, no counterpart
    z = zil.get(zname)
    if z is None:
        print(f"!! {oid}: zil object {zname} not found")
        problems += 1
        continue
    zd, zf, zl = z.get('desc'), z.get('fdesc'), z.get('ldesc')
    on, of_, ol = o['name'], o.get('fdesc'), o.get('desc')
    if oid not in accepted_name and norm(on) != norm(zd):
        print(f"NAME   {oid:20} ours={on!r} zil={zd!r}")
        problems += 1
    if norm(of_ or '') != norm(zf or ''):
        print(f"FDESC  {oid:20} ours={(of_ or '-')!r} zil={(zf or '-')!r}")
        problems += 1
    if oid in accepted_ldesc:
        ol, zl = None, None
    if norm(ol or '') != norm(zl or ''):
        print(f"LDESC  {oid:20} ours={(ol or '-')!r} zil={(zl or '-')!r}")
        problems += 1

# our items with no mapping at all — every item must be accounted for
unmapped = [k for k in ours
            if k not in mapping
            and not any(m in ('room', 'portal', 'automation', 'doorstate', 'flag')
                        for m in ours[k]['modules'])]
if unmapped:
    print("\nUNMAPPED our items:")
    for k in sorted(unmapped):
        print(f"  {k:24} {ours[k]['name']!r}")
    problems += len(unmapped)

print(f"\n{problems} problems")
sys.exit(0 if problems == 0 else 1)
