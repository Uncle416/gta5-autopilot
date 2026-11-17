"""Single source of truth for place names and coordinates.

The LLM only ever handles place ids. Coordinates come from here and nowhere else.
"""
import difflib
import json
import os
import re

DEFAULT_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "places.json")


def norm(text):
    """Lowercase and drop whitespace/punctuation so 'Maze Bank' matches 'mazebank'."""
    return re.sub(r"[\s\W_]+", "", str(text).lower())


class Gazetteer:
    def __init__(self, path=DEFAULT_PATH):
        self.path = path
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        self._raw = data
        self.places = {}
        for p in data["places"]:
            if p["id"] in self.places:
                raise ValueError(f"duplicate place id: {p['id']}")
            self.places[p["id"]] = p
        self.ambiguous = data.get("ambiguous", [])
        for group in self.ambiguous:
            for cid in group["candidates"] + ([group["default"]] if group.get("default") else []):
                if cid not in self.places:
                    raise ValueError(f"ambiguous group refers to unknown place: {cid}")
        # normalized name/alias -> set of ids
        self._names = {}
        for p in self.places.values():
            for n in [p["name"]] + p.get("aliases", []):
                self._names.setdefault(norm(n), set()).add(p["id"])

    def get(self, place_id):
        return self.places.get(place_id)

    def name(self, place_id):
        p = self.places.get(place_id)
        return p["name"] if p else str(place_id)

    def stop(self, place_id):
        """Protocol 'stop' object for set_route."""
        p = self.places[place_id]
        return {"id": p["id"], "name": p["name"], "x": float(p["x"]), "y": float(p["y"]), "z": float(p["z"])}

    def names_of(self, place_id):
        p = self.places[place_id]
        return [p["name"]] + p.get("aliases", [])

    def mentioned_literally(self, place_id, text):
        """True if the text contains this place's own name or one of its aliases."""
        t = norm(text)
        return any(norm(n) in t for n in self.names_of(place_id))

    def ambiguity_for(self, place_id, text):
        """If the text only uses a generic word ('医院') that could mean several places
        including this one, return that group; otherwise None."""
        t = norm(text)
        if self.mentioned_literally(place_id, text):
            # A specific alias was used ('桑迪海岸医院' contains '医院' but is not ambiguous).
            specific = [n for n in self.names_of(place_id) if norm(n) in t]
            generic = {norm(term) for g in self.ambiguous for term in g["terms"]}
            if any(norm(n) not in generic for n in specific):
                return None
        for group in self.ambiguous:
            if place_id not in group["candidates"]:
                continue
            if any(norm(term) in t for term in group["terms"]):
                return group
        return None

    def search(self, query, limit=4):
        """Closest place ids for a free-text name the user used. Never invents a match."""
        q = norm(query)
        if not q:
            return []
        scored = {}
        for key, ids in self._names.items():
            if q == key:
                score = 1.0
            elif q in key or key in q:
                score = 0.75 + 0.2 * min(len(q), len(key)) / max(len(q), len(key))
            else:
                score = difflib.SequenceMatcher(None, q, key).ratio()
            for pid in ids:
                scored[pid] = max(scored.get(pid, 0.0), score)
        ranked = sorted(scored.items(), key=lambda kv: -kv[1])
        return [pid for pid, score in ranked if score >= 0.6][:limit]

    def find_in_text(self, text):
        """Places whose name/alias literally occurs in the text, in order of appearance.
        Used only as the degraded mode when no LLM is available. Returns (place_ids, ambiguous_groups)."""
        t = norm(text)
        hits = []  # (position, -length, ids)
        for key, ids in self._names.items():
            start = t.find(key)
            while start != -1:
                hits.append((start, -len(key), start + len(key), ids))
                start = t.find(key, start + 1)
        generic_groups = []
        for group in self.ambiguous:
            for term in group["terms"]:
                k = norm(term)
                start = t.find(k)
                while start != -1:
                    hits.append((start, -len(k), start + len(k), group))
                    start = t.find(k, start + 1)
        hits.sort(key=lambda h: (h[0], h[1]))
        out, covered_until = [], 0
        for start, _neg, end, target in hits:
            if start < covered_until:
                continue  # inside a longer match that started earlier
            covered_until = end
            if isinstance(target, dict):
                if target.get("default"):
                    out.append(target["default"])
                else:
                    generic_groups.append(target)
            elif len(target) == 1:
                out.append(next(iter(target)))
            else:
                generic_groups.append({"terms": ["该名称"], "candidates": sorted(target), "default": None})
        return out, generic_groups

    def prompt_table(self):
        """Compact listing for the system prompt."""
        rows = []
        for p in self.places.values():
            aliases = "、".join(p.get("aliases", []))
            rows.append(f"{p['id']} | {p['name']} | {aliases} | {p['category']}")
        return "\n".join(rows)

    def ambiguity_table(self):
        rows = []
        for g in self.ambiguous:
            cands = "、".join(f"{c}({self.name(c)})" for c in g["candidates"])
            default = f"；用户没说清时默认 {g['default']}" if g.get("default") else "；用户没说清时必须追问"
            rows.append(f"「{'/'.join(g['terms'])}」可能指：{cands}{default}")
        return "\n".join(rows)

    def set_coordinates(self, place_id, x, y, z):
        """Record in-game captured coordinates and mark the place verified. Rewrites the JSON file."""
        if place_id not in self.places:
            raise KeyError(place_id)
        for p in self._raw["places"]:
            if p["id"] == place_id:
                p.update(x=round(float(x), 1), y=round(float(y), 1), z=round(float(z), 1), verified=True)
        lines = ["{", f'  "_note": {json.dumps(self._raw.get("_note", ""), ensure_ascii=False)},', '  "places": [']
        ps, amb = self._raw["places"], self._raw.get("ambiguous", [])
        lines += ["    " + json.dumps(p, ensure_ascii=False) + ("," if i < len(ps) - 1 else "") for i, p in enumerate(ps)]
        lines += ["  ],", '  "ambiguous": [']
        lines += ["    " + json.dumps(a, ensure_ascii=False) + ("," if i < len(amb) - 1 else "") for i, a in enumerate(amb)]
        lines += ["  ]", "}"]
        tmp = self.path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
        os.replace(tmp, self.path)
