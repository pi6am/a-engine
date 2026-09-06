# Zork I description audit

Checks every item in `scenarios/zork1/world/` against the original game's
ZIL source (`DESC` = short name, `FDESC` = first-sight text, `LDESC` =
settled description) — verbatim or nothing, no invented prose.

## Usage

```bash
python3 tools/zork1-audit/extract.py   # zork1 .zil source -> zork_objects.json
python3 tools/zork1-audit/ours.py      # our world fragments -> our_objects.json
python3 tools/zork1-audit/compare.py   # mapping-driven diff; exit 1 on problems
```

`ZORK1_SRC` (default `/home/peter/repo/zork1`) locates the ZIL source.

## Files

- `extract.py` — parses `<OBJECT NAME (DESC "...") (FDESC "...") (LDESC "...")>`
  triples out of the ZIL files (plus SYNONYM lists, for matching help).
- `ours.py` — dumps our objects: id, name, description, firstDescription,
  modules.
- `mapping.json` — **the curated table**: our object id → ZIL object name.
  `null` marks deliberate inventions (engine condition templates, the
  echo-room listener, game rules). Deviations that are accepted on purpose
  (names chosen for label disambiguation, the lunch's listing-only ldesc)
  are documented as `# ...-name` / `# ...` comment keys in the file and
  mirrored in `compare.py`'s accepted sets.
- `compare.py` — asserts name/fdesc/ldesc equality per mapped item, flags
  unmapped items, and fails (exit 1) on any problem. Run it after touching
  any zork1 world fragment.

When adding a new item to the scenario, add it to `mapping.json` (or give
it a `null` mapping with a reason) and re-run all three scripts.
