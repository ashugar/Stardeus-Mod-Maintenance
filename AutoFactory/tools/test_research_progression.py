"""Read-only Core + Auto Factory research-cost regression; Python 3 standard library.

Models the cost comparisons inspected in Stardeus 0.15.12.3259.
Does not execute the game, model arbitrary research modifiers, or claim
storage/unlock-value, gameplay, or save/load validation.
"""
import argparse
import copy
import json
from pathlib import Path

SCALES = (0.5, 0.75, 1.0, 1.25, 1.5)


def read_json(path):
    # Preserve quoted strings while removing Core's JSON comments/trailing commas.
    text = Path(path).read_text(encoding="utf-8-sig")
    chars = list(text)
    i = 0
    quoted = False
    while i < len(chars):
        if quoted:
            if chars[i] == "\\":
                i += 2
                continue
            if chars[i] == '"':
                quoted = False
        elif chars[i] == '"':
            quoted = True
        elif text[i:i + 2] == "//":
            end = text.find("\n", i)
            end = len(chars) if end < 0 else end
            chars[i:end] = [" "] * (end - i)
            i = end
            continue
        elif text[i:i + 2] == "/*":
            end = text.find("*/", i + 2)
            if end < 0:
                raise ValueError("Unterminated JSON comment: " + str(path))
            chars[i:end + 2] = [" "] * (end + 2 - i)
            i = end + 2
            continue
        i += 1
    quoted = False
    i = 0
    while i < len(chars):
        if quoted:
            if chars[i] == "\\":
                i += 2
                continue
            if chars[i] == '"':
                quoted = False
        elif chars[i] == '"':
            quoted = True
        elif chars[i] == ",":
            j = i + 1
            while j < len(chars) and chars[j].isspace():
                j += 1
            if j < len(chars) and chars[j] in "]}":
                chars[i] = " "
        i += 1
    return json.loads("".join(chars))


def load_nodes(root):
    nodes = {}
    for path in sorted((Path(root) / "Definitions").rglob("*.json")):
        definition = read_json(path)
        research = definition.get("Researchable", {})
        if not research.get("IsTreeNode"):
            continue
        if definition.get("ParentId"):
            raise ValueError("Inherited research requires separate resolution: " + str(path))
        ident = definition["Id"]
        if ident in nodes:
            raise ValueError("Duplicate research ID: " + ident)
        nodes[ident] = {
            "id": ident, "tier": definition.get("Tier", 0),
            "costs": research.get("DatoidCosts", []),
            "parents": research.get("Prerequisites", []),
            "skip": research.get("SkipCostValidation", False),
        }
    return nodes


def resolve_tiers(nodes):
    # Def.AfterLoad raises a definition to at least every prerequisite's tier.
    for _ in range(len(nodes) + 1):
        changed = False
        for node in nodes.values():
            for parent in node["parents"]:
                if parent not in nodes:
                    raise ValueError("Unresolved prerequisite: " + str(parent))
                if nodes[parent]["tier"] > node["tier"]:
                    node["tier"] = nodes[parent]["tier"]
                    changed = True
        if not changed:
            return nodes
    raise ValueError("Prerequisite tier propagation did not converge")


def costs(nodes, weights, scale=None):
    def quantity(value):
        return value if scale is None else max(1, round(value * scale))
    return {
        ident: sum(weights[item["TypeId"]] * quantity(item["StackSize"])
                   for item in node["costs"])
        for ident, node in nodes.items()
    }


def tier_stats(nodes, totals):
    grouped = {}
    for ident, node in nodes.items():
        if node["tier"] > 0:
            grouped.setdefault(node["tier"], []).append(totals[ident])
    return {tier: (min(values), max(values), sum(values) / len(values))
            for tier, values in grouped.items()}


def evaluator_warnings(nodes, weights, scale):
    totals = costs(nodes, weights, scale)
    tiers = tier_stats(nodes, totals)
    warnings = []
    for ident, node in nodes.items():
        tier, cost = node["tier"], totals[ident]
        if tier == 0 or cost == 0:
            continue
        rules = (
            (tier + 1, 0, 1.25, True, "above-next-tier"),
            (tier - 1, 1, 1 / 1.25, False, "below-previous-tier"),
            (tier + 2, 0, 1.5, True, "above-two-tiers"),
            (tier - 2, 1, 1 / 1.5, False, "below-two-tiers"),
        )
        for other, extremum, factor, above, name in rules:
            if other not in tiers:
                continue
            limit = tiers[other][extremum] * factor
            if (cost > limit if above else cost < limit):
                warnings.append((name, ident))
    return warnings


def tree_cost_warnings(nodes, weights):
    totals = costs(nodes, weights)
    tiers = tier_stats(nodes, totals)
    warnings = []
    for tier, (_, _, average) in tiers.items():
        if tier > 1 and tier - 1 in tiers:
            ratio = average / tiers[tier - 1][2]
            x = min(tier, 8)
            limit = 0.0955598 * x * x - 1.51332 * x + 7.639
            if ratio < 1 or ratio > limit:
                warnings.append(("tier-average", str(tier)))
    for ident, node in nodes.items():
        tier, cost = node["tier"], totals[ident]
        if tier == 0:
            continue
        if tier > 1:
            prior_max = tiers.get(tier - 1, (0, 0, 0))[1]
            if cost < prior_max * 1.05:
                warnings.append(("previous-tier-low", ident))
            if cost > prior_max * 3:
                warnings.append(("previous-tier-high", ident))
        for parent_id in node["parents"]:
            parent = nodes[parent_id]
            gap = tier - parent["tier"]
            if parent_id == ident:
                warnings.append(("self-prerequisite", ident, parent_id))
            if parent["tier"] == 0:
                continue
            if gap < 0 or gap > 2:
                warnings.append(("prerequisite-tier", ident, parent_id))
            parent_cost = totals[parent_id]
            if cost > parent_cost * (2 if gap == 0 else 2 + 2 * gap):
                warnings.append(("parent-cost-limit", ident, parent_id))
            if not node["skip"] and cost < parent_cost:
                warnings.append(("cheaper-than-parent", ident, parent_id))
    return warnings


def validate(core_root, mod_root):
    weights = {d["Id"]: d["InternalValue"]
               for d in (read_json(p) for p in
                         (Path(core_root) / "Config/Materials/Datoids").glob("*.json"))}
    if not weights:
        raise ValueError("No native Datoid weights found")
    native = resolve_tiers(load_nodes(core_root))
    added = load_nodes(mod_root)
    if not native or not added or native.keys() & added.keys():
        raise ValueError("Missing nodes or conflicting native/mod IDs")
    combined = resolve_tiers(dict(copy.deepcopy(native), **added))
    results = []
    for label, nodes in (("Core", native), ("Core+AutoFactory", combined)):
        tree = tree_cost_warnings(nodes, weights)
        for scale in SCALES:
            evaluator = evaluator_warnings(nodes, weights, scale)
            results.append({"scope": label, "scale": scale,
                            "evaluator": evaluator, "tree_cost": tree})
    # Negative controls: exercise both underpriced tier and parent-edge detection.
    low = copy.deepcopy(combined)
    high = copy.deepcopy(combined)
    fab = "Research/Production/NanoFabrication"
    smelt = "Research/Production/NanoSmelting"
    low[fab]["costs"] = [{"TypeId": "DatoidSynthetic", "StackSize": 1}]
    high[smelt]["costs"] = [{"TypeId": "DatoidSynthetic", "StackSize": 1000}]
    if not any(w[0] == "previous-tier-low" and w[1] == fab
               for w in tree_cost_warnings(low, weights)):
        raise AssertionError("Underpriced-node negative control failed")
    if not any(w[0] == "parent-cost-limit" and w[1] == smelt
               for w in tree_cost_warnings(high, weights)):
        raise AssertionError("Parent-edge negative control failed")
    print(json.dumps({"nodes": {"Core": len(native), "Core+AutoFactory": len(combined)},
                      "negative_controls": "passed", "results": results}, indent=2))
    if any(row["evaluator"] or row["tree_cost"] for row in results):
        raise SystemExit("Research cost comparisons failed; inspect results above.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--core-root", required=True, type=Path)
    parser.add_argument("--mod-root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    validate(args.core_root, args.mod_root)
