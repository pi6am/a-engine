#!/usr/bin/env python3
"""Dump our scenario's objects (id, name, descriptions, modules) to
our_objects.json next to this script, and print the item inventory
(non-room/portal/automation/doorstate/flag objects) for mapping review.
"""
import json
import glob
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
WORLD = os.path.normpath(os.path.join(HERE, '..', '..', 'scenarios', 'zork1', 'world'))

ours = {}
for path in sorted(glob.glob(os.path.join(WORLD, '*.json'))):
    d = json.load(open(path))
    def walk(nodes):
        for n in nodes:
            mods = [m if isinstance(m, str) else m.get('module') for m in n.get('modules', [])]
            ours[n['id']] = {
                'name': n.get('name', ''),
                'desc': n.get('description', ''),
                'fdesc': n.get('firstDescription'),
                'modules': mods,
                'file': os.path.basename(path),
            }
            walk(n.get('children', []))
    walk(d['world'])

json.dump(ours, open(os.path.join(HERE, 'our_objects.json'), 'w'), indent=1)

SKIP_MODS = {'room', 'portal', 'automation', 'doorstate', 'flag'}
items = {k: v for k, v in ours.items() if not any(m in SKIP_MODS for m in v['modules'])}
print(f"{len(items)} item-ish objects (see mapping.json):")
for k in sorted(items):
    print(f"  {k:28} {items[k]['name']!r}")
