"""Conservative first-party CSS audit. Vendor/build files are never inputs.

Requires the BSD-licensed tinycss2 parser (requirements.txt). Pruning removes
only declarations repeated later for every exact selector, in the identical
conditional/layer context with identical value and importance. Different-value
fallbacks are retained, including values supported only by newer browsers.
It never infers reachability from a screenshot or removes priority markers.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
import tinycss2

ROOT = Path(__file__).resolve().parents[2]
WEB = ROOT / "src/MediaEngine.Web"
GROUPS = {"media", "supports", "container", "layer", "scope"}


def inputs():
    return sorted(p for p in WEB.rglob("*.css")
                  if not {"bin", "obj", "vendor"}.intersection(p.relative_to(WEB).parts))


def source_inventory(root):
    """Reference candidates, not proof of emitted scope or runtime reachability."""
    result = []
    for path in sorted(root.rglob("*")):
        if path.suffix not in {".razor", ".cs", ".cshtml", ".js", ".ts", ".html", ".json", ".resx"}:
            continue
        if {"bin", "obj", "vendor"}.intersection(path.relative_to(root).parts):
            continue
        content = path.read_text(encoding="utf-8-sig")
        tokens = set(re.findall(r"(?<![\w-])[a-zA-Z_][\w-]*", content))
        literal_html = set()
        for tag in re.finditer(r'<[a-z][\w-]*\b[^>]*>', content):
            for attribute in re.finditer(r'\bclass\s*=\s*([\"\'])(.*?)\1', tag[0], re.S):
                value = attribute[2]
                if "@" not in value:
                    literal_html.update(value.split())
        result.append({"path": path, "tokens": tokens, "literal_html": literal_html,
                       "dynamic_prefixes": {token for token in tokens if token.endswith("-")},
                       "renderer": "BuildRenderTree" in content or "RenderFragment" in content})
    return result


def selector_classes(selector):
    """Read selector classes, including selector functions, but never quoted values."""
    found = set()
    def walk(tokens):
        for a, b in zip(tokens, tokens[1:]):
            if a.type == "literal" and a.value == "." and b.type == "ident":
                found.add(b.value)
        for token in tokens:
            if token.type == "function":
                walk(token.arguments)
    walk(tinycss2.parse_component_value_list(selector))
    return sorted(found)


def ownership_report(css_path, text, inventory, root=ROOT):
    entries, errors = inspect(text)
    selectors_seen = set()
    report = []
    for entry in entries:
        for selector in entry["selectors"]:
            key = (entry["context"], selector)
            if "::deep" not in selector or key in selectors_seen:
                continue
            selectors_seen.add(key)
            targets = []
            for name in selector_classes(selector.split("::deep", 1)[1]):
                candidates = []
                for source in inventory:
                    exact = name in source["tokens"]
                    dynamic = any(name.startswith(prefix) for prefix in source["dynamic_prefixes"])
                    if not (exact or dynamic):
                        continue
                    kind = ("literal-html" if name in source["literal_html"] else
                            "renderer-or-fragment-reference" if source["renderer"] else
                            "component-parameter-or-code-reference" if exact else "dynamic-prefix")
                    candidates.append({"path": source["path"].relative_to(root).as_posix(), "kind": kind})
                literal = [candidate["path"] for candidate in candidates if candidate["kind"] == "literal-html"]
                own = css_path.with_suffix("").relative_to(root).as_posix()
                status = ("third-party" if name.startswith("mud-") else
                          "self" if literal == [own] else
                          "child:" + literal[0] if len(literal) == 1 else
                          "shared:" + ",".join(literal) if literal else "unresolved")
                targets.append({"class": name, "owner_status": status, "candidates": candidates,
                                "emitted_scope_verified": False})
            report.append({"selector": selector, "context": entry["context"], "line": entry["line"],
                           "targets": targets, "owner_status": "unresolved" if not targets else
                           "unresolved" if any(t["owner_status"] == "unresolved" for t in targets) else "candidate",
                           "requires_dom_review": True})
    return {"selectors": report, "unresolved_count": sum(r["owner_status"] == "unresolved" for r in report),
            "parse_errors": errors}


def selected_inputs(paths, root=ROOT):
    available = inputs()
    if not paths:
        return available
    requested = {Path(path).resolve() if Path(path).is_absolute() else (root / path).resolve() for path in paths}
    unknown = requested - {path.resolve() for path in available}
    if unknown:
        raise ValueError("File selection must contain first-party CSS inputs: " + ", ".join(map(str, sorted(unknown))))
    return [path for path in available if path.resolve() in requested]


def selectors(tokens):
    groups, current = [], []
    for token in tokens:
        if token.type == "literal" and token.value == ",":
            groups.append(tinycss2.serialize(current).strip()); current = []
        else:
            current.append(token)
    groups.append(tinycss2.serialize(current).strip())
    return tuple(groups)


def inspect(text):
    offsets, cursor = [], 0
    for line in text.splitlines(keepends=True):
        offsets.append(cursor); cursor += len(line)
    def position(token):
        return offsets[token.source_line - 1] + token.source_column - 1
    entries, failures = [], []
    def walk(rules, context=()):
        for rule in rules:
            if rule.type == "error":
                failures.append(str(rule)); continue
            if rule.type == "at-rule" and rule.content is not None:
                if rule.lower_at_keyword in GROUPS:
                    condition = tinycss2.serialize(rule.prelude).strip()
                    # Each anonymous layer has its own cascade position. It
                    # cannot share duplicate keys with another anonymous layer.
                    if rule.lower_at_keyword == "layer" and not condition:
                        condition = f"anonymous:{rule.source_line}:{rule.source_column}"
                    walk(tinycss2.parse_rule_list(rule.content), context +
                         ((rule.lower_at_keyword, condition),))
                continue
            if rule.type != "qualified-rule":
                continue
            declarations = tinycss2.parse_declaration_list(rule.content)
            if any(d.type not in {"declaration", "whitespace", "comment"} for d in declarations):
                failures.append(f"Unsupported declaration block at {rule.source_line}"); continue
            raw = rule.content
            for declaration in declarations:
                if declaration.type != "declaration": continue
                start = position(declaration)
                # A semicolon token in the flat rule content cannot be inside a
                # function/string/block. Its source position bounds this declaration.
                end = next((position(token) + 1 for token in raw
                            if token.type == "literal" and token.value == ";"
                            and position(token) >= start), None)
                # Keep a final declaration without a semicolon. Tokens expose
                # starts, not ends; a quoted brace must not be mistaken for
                # the rule's closing brace when removing source characters.
                name = declaration.name if declaration.name.startswith("--") else declaration.lower_name
                entries.append({"selectors": selectors(rule.prelude), "context": context,
                                "value": tinycss2.serialize(declaration.value).strip(),
                                "name": name, "important": declaration.important,
                                "start": start, "end": end, "line": declaration.source_line})
    walk(tinycss2.parse_stylesheet(text))
    return entries, failures


def prune(text):
    entries, failures = inspect(text)
    if failures:
        return text, [], failures
    seen, removed = set(), []
    for entry in reversed(entries):
        keys = [(entry["context"], selector, entry["name"], entry["value"], entry["important"])
                for selector in entry["selectors"]]
        if entry["end"] is not None and all(key in seen for key in keys):
            removed.append(entry)
        seen.update(keys)
    # Keep source order, comments, surviving declarations, and conditional blocks.
    for entry in sorted(removed, key=lambda item: item["start"], reverse=True):
        text = text[:entry["start"]] + text[entry["end"]:]
    return text, removed, failures


def remove_retired_selectors(text, retired):
    """An absent positive class makes this selector impossible to match.

    Classes inside :not(), :is(), attribute values, etc. are deliberately
    ignored. Retired names come from a separately reviewed, explicit manifest.
    """
    deleted = []
    def walk(rules):
        result = []
        for rule in rules:
            if rule.type == "at-rule" and rule.content is not None and rule.lower_at_keyword in GROUPS:
                rule.content = tinycss2.parse_component_value_list(tinycss2.serialize(walk(tinycss2.parse_rule_list(rule.content))))
            if rule.type == "qualified-rule":
                kept = []
                for selector in selectors(rule.prelude):
                    tokens = tinycss2.parse_component_value_list(selector)
                    absent = [b.value for a, b in zip(tokens, tokens[1:])
                              if a.type == "literal" and a.value == "." and b.type == "ident" and b.value in retired]
                    if absent: deleted.append({"selector": selector, "classes": absent, "line": rule.source_line})
                    else: kept.append(selector)
                if not kept: continue
                if len(kept) != len(selectors(rule.prelude)):
                    rule.prelude = tinycss2.parse_component_value_list(",\n".join(kept) + " ")
                if not any(d.type == "declaration" for d in tinycss2.parse_declaration_list(rule.content)):
                    continue
            result.append(rule)
        return result
    result = tinycss2.serialize(walk(tinycss2.parse_stylesheet(text)))
    result = re.sub(r"(?m)^[ \t]+$", "", result)
    result = re.sub(r"\n{3,}", "\n\n", result)
    return result.rstrip() + "\n", deleted


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--prune", action="store_true")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--retired-class-list", type=Path)
    parser.add_argument("--ownership", action="store_true")
    parser.add_argument("--max-lines", type=int)
    parser.add_argument("--file", action="append", help="Exact repository-relative CSS file; repeat to select files")
    args = parser.parse_args()
    if args.prune and not args.file:
        parser.error("--prune requires an explicit --file selection; review a dry run first")
    if args.max_lines is not None and args.max_lines < 1:
        parser.error("--max-lines must be positive")
    selected = selected_inputs(args.file)
    source_root = ROOT / "src"
    inventory = source_inventory(source_root) if args.ownership else []
    source = "\n".join(p.read_text(encoding="utf-8-sig") for p in source_root.rglob("*")
                       if p.suffix in {".razor", ".cs", ".cshtml", ".js", ".ts", ".html", ".json", ".resx"}
                       and not {"bin", "obj", "vendor"}.intersection(p.relative_to(source_root).parts))
    mentioned = set(re.findall(r"(?<![\w-])[a-zA-Z_][\w-]*", source))
    dynamic = {name for name in mentioned if name.endswith("-")}
    missing = {}
    for path in selected:
        entries, _ = inspect(path.read_text(encoding="utf-8-sig"))
        candidates = set()
        for entry in entries:
            for selector in entry["selectors"]:
                tokens = tinycss2.parse_component_value_list(selector)
                for a, b in zip(tokens, tokens[1:]):
                    if a.type == "literal" and a.value == "." and b.type == "ident":
                        name = b.value
                        if name.startswith(("tl-", "sme-", "app-", "playback-", "listen-", "media-", "view-")) and name not in mentioned and not any(name.startswith(prefix) for prefix in dynamic):
                            candidates.add(name)
        if candidates: missing[path.relative_to(ROOT).as_posix()] = sorted(candidates)
    files = []
    retired_manifest = json.loads(args.retired_class_list.read_text()) if args.retired_class_list else {}
    for path in selected:
        data = path.read_bytes(); text = data.decode("utf-8-sig")
        updated, removed, failures = prune(text)
        retired = set(retired_manifest.get(path.relative_to(ROOT).as_posix(), []))
        if any(name in mentioned or any(name.startswith(prefix) for prefix in dynamic) for name in retired):
            raise ValueError(f"Retired class has a live/dynamic source reference in {path}")
        updated, removed_selectors = remove_retired_selectors(updated, retired) if not failures else (text, [])
        has_removals = bool(removed or removed_selectors)
        if not has_removals:
            updated = text
        entries, _ = inspect(text)
        files.append({"path": path.relative_to(ROOT).as_posix(), "bytes": len(data),
                      "normalized_bytes": len(text.replace("\r\n", "\n").replace("\r", "\n").encode("utf-8")),
                      "lines": len(text.splitlines()), "declarations": len(entries),
                      "isolated": path.name.endswith(".razor.css"),
                      "deep_count": len(re.findall(r"::deep", re.sub(r"/\*.*?\*/", "", text, flags=re.S))),
                      "important_markers": len(re.findall(r"!important\b", re.sub(r"/\*.*?\*/", "", text, flags=re.S))),
                      "important": sum(entry["important"] for entry in entries),
                      "dominated_declarations": len(removed),
                      "dominated_important": sum(entry["important"] for entry in removed),
                      "candidate_bytes": len(updated.encode("utf-8")) if has_removals else len(data),
                      "candidate_important": sum(d["important"] for d in inspect(updated)[0]), "errors": failures,
                      "removed": removed, "removed_selectors": removed_selectors})
        if args.ownership and path.name.endswith(".razor.css"):
            files[-1]["ownership"] = ownership_report(path, text, inventory)
        if args.prune and has_removals and updated != text:
            path.write_text(updated, encoding="utf-8", newline="\n")
    result = {"unreferenced_owned_classes": missing, "files": files, "totals": {key: sum(file[key] for file in files)
              for key in ("bytes", "normalized_bytes", "lines", "declarations", "important", "important_markers", "deep_count",
                          "dominated_declarations", "dominated_important", "candidate_bytes", "candidate_important")}}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result["totals"]))
    print("Unsupported files:", [file["path"] for file in files if file["errors"]])
    print("Owned classes requiring reachability review:", {path: len(names) for path, names in missing.items()})
    offenders = [file for file in files if file["isolated"] and args.max_lines is not None and file["lines"] > args.max_lines]
    if offenders:
        print("Isolated line limit exceeded:", {f["path"]: f["lines"] for f in offenders})
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
