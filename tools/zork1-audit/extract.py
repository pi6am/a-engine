#!/usr/bin/env python3
"""Extract every Zork I object's DESC/FDESC/LDESC from the ZIL source.

Writes zork_objects.json next to this script:
  { "OBJECT-NAME": { "desc": ..., "fdesc": ..., "ldesc": ..., "synonyms": [...] } }

The ZIL source is expected at ZORK1_SRC (default: /home/peter/repo/zork1).
"""
import json
import os
import re
import glob
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ZORK1_SRC = os.environ.get("ZORK1_SRC", "/home/peter/repo/zork1")

src = ""
for path in sorted(glob.glob(os.path.join(ZORK1_SRC, "*.zil"))):
    src += open(path).read()

def unescape(s):
    return s.replace('\\"', '"').strip()

objects = {}
for m in re.finditer(r'<OBJECT\s+([A-Z0-9-]+)(.*?)(?<!<)>', src, re.S):
    name, body = m.group(1), m.group(2)
    entry = {}
    for key in ('DESC', 'FDESC', 'LDESC'):
        km = re.search(r'\(' + key + r'\s+"((?:[^"\\]|\\.)*)"\s*\)', body, re.S)
        if km:
            entry[key.lower()] = unescape(km.group(1)).replace('\n', ' ')
    syn = re.search(r'\(SYNONYM\s+([A-Z ]+)\)', body)
    if syn:
        entry['synonyms'] = syn.group(1).split()
    if entry:
        objects[name] = entry

out = os.path.join(HERE, 'zork_objects.json')
json.dump(objects, open(out, 'w'), indent=1)
print(f"{len(objects)} zil objects -> {out}")
