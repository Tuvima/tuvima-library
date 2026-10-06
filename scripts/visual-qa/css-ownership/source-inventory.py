"""Read-only, reproducible baseline/current Dashboard dependency inventory."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[3]
WEB = "src/MediaEngine.Web/"
PATTERNS = {
    "component_open_tags": r"<Mud[A-Z]\w*\b",
    "material_icon_references": r"\bIcons\.Material\.(?:Filled|Outlined|Uncategorized)\.\w+",
    "dialog_service_references": r"\bIDialogService\b",
    "toast_service_references": r"\bISnackbar\b",
    "dialog_options_references": r"\bDialogOptions\b",
    "dialog_parameters_references": r"\bDialogParameters\b",
    "framework_enum_members": r"\b(?:Severity|Variant|Typo|Color|Size|Adornment|Placement|Origin|InputType|Breakpoint)\.\w+",
    "framework_css_variable_reads": r"var\(\s*--mud-[\w-]+",
    "framework_css_class_selectors": r"\.mud-[\w-]+",
    "framework_namespace_references": r"\bMudBlazor\b",
    "framework_service_registration": r"\bAddMudServices\b",
}

def sha(data):
    return hashlib.sha256(data).hexdigest()

def lf(data):
    return data.replace(b"\r\n", b"\n")

def source(path):
    return path.startswith(WEB) and Path(path).suffix in {".cs", ".razor", ".css", ".js"} and not any(
        part in {"bin", "obj", "vendor"} for part in Path(path).parts)

def inventory(files):
    per_file = {}
    component_types, icon_members = set(), set()
    for name, data in sorted(files.items()):
        if not source(name):
            continue
        text = data.decode("utf-8-sig")
        # Generated icon attribution is permitted; dependency-bearing source is not.
        if name.endswith("/AppMaterialIconPaths.cs"):
            text = re.sub(r"\A(?://[^\n]*\n)+", "", text)
        counts = {key: len(re.findall(pattern, text)) for key, pattern in PATTERNS.items()}
        if any(counts.values()):
            per_file[name] = counts
        component_types.update(re.findall(r"<(Mud[A-Z]\w*)\b", text))
        icon_members.update(re.findall(PATTERNS["material_icon_references"], text))
    return {"files_scanned": sum(source(name) for name in files), "totals": {
        key: sum(counts[key] for counts in per_file.values()) for key in PATTERNS},
        "distinct_components": sorted(component_types), "distinct_material_icons": sorted(icon_members),
        "files_with_coupling": len(per_file), "per_file": per_file}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", default="c765bc91")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    commit = subprocess.check_output(["git", "rev-parse", args.baseline], cwd=ROOT, text=True).strip()
    archive = subprocess.check_output(["git", "archive", commit, "src/MediaEngine.Web", "Directory.Packages.props"], cwd=ROOT)
    with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
        before = {entry.name: tar.extractfile(entry).read() for entry in tar if entry.isfile()}
    after = {p.relative_to(ROOT).as_posix(): p.read_bytes() for p in (ROOT / WEB).rglob("*")
             if p.is_file() and not any(part in {"bin", "obj"} for part in p.relative_to(ROOT).parts)}
    after["Directory.Packages.props"] = (ROOT / "Directory.Packages.props").read_bytes()
    tokens = WEB + "wwwroot/tuvima.tokens.css"
    before_tokens = re.findall(r"(--[\w-]+)\s*:\s*([^;{}]+)", lf(before[tokens]).decode())
    after_tokens = re.findall(r"(--[\w-]+)\s*:\s*([^;{}]+)", lf(after[tokens]).decode())
    vendor = {name: {"before_sha256": sha(before[name]) if name in before else None,
                     "after_sha256": sha(after[name]) if name in after else None,
                     "before_sha256_lf": sha(lf(before[name])) if name in before else None,
                     "after_sha256_lf": sha(lf(after[name])) if name in after else None}
              for name in sorted(set(before) | set(after)) if name.startswith(WEB + "wwwroot/vendor/")}
    snapshot = ROOT / "scripts/icons/material-icon-paths.json"
    output = {"schemaVersion": 1, "baseline_commit": commit,
        "current_revision": "working checkout; includes untracked first-party implementation files",
        "method": "Opening tags only; exact token regexes listed below. Source excludes bin/obj/vendor and generated icon attribution header. No runtime evidence inferred.",
        "patterns": PATTERNS, "lexical_caveat": "DialogOptions is also a retained first-party parameter name; generic enum-shaped matches include instance properties and unrelated domain types. These lexical counts do not prove a remaining framework type. NativeUiDependencyGuardrailTests checks dependency-bearing source and APIs.",
        "before": inventory(before), "after": inventory(after),
        "package_references": {phase: {name: len(re.findall(r'<Package(?:Version|Reference)\s+Include="MudBlazor"', files[name].decode()))
                              for name in ("Directory.Packages.props", WEB + "MediaEngine.Web.csproj")}
                              for phase, files in (("before", before), ("after", after))},
        "preserved_tokens": {"before_sha256": sha(before[tokens]), "after_sha256": sha(after[tokens]),
                              "before_sha256_lf": sha(lf(before[tokens])), "after_sha256_lf": sha(lf(after[tokens])),
                              "declaration_values_equal": before_tokens == after_tokens,
                              "original_declarations": len(before_tokens)},
        "vendor_hashes": vendor, "vendor_raw_bytes_equal": all(v["before_sha256"] == v["after_sha256"] for v in vendor.values()),
        "vendor_files_unchanged_lf": all(v["before_sha256_lf"] == v["after_sha256_lf"] for v in vendor.values()),
        "pinned_material_catalog": {"snapshot_sha256_lf": sha(snapshot.read_text(encoding="utf-8").replace("\r\n", "\n").encode()),
                                    "members": len(json.loads(snapshot.read_text(encoding="utf-8")))}}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"before": output["before"]["totals"], "after": output["after"]["totals"],
                      "tokens": output["preserved_tokens"], "vendor_files": len(vendor),
                      "vendor_files_unchanged_lf": output["vendor_files_unchanged_lf"], "icons": output["pinned_material_catalog"]}))

if __name__ == "__main__":
    main()
