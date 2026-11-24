#!/usr/bin/env python3
"""Builds a config folder the ingestion baseline run accepts: a copy of the repo's config/ (provider keys included,
backups left out) whose libraries.json holds one empty library per catalogue type. The baseline run then points each
library at the matching sample folder. A fresh checkout's libraries.json has no libraries, so the run refuses it.
Usage: python Prepare-ConfigSource.py <repo>/config <new-folder>   (delete <new-folder> afterwards: it holds keys)"""
import json, os, shutil, sys, uuid

source, target = map(os.path.abspath, sys.argv[1:3])
if os.path.exists(target):
    sys.exit(f"{target} already exists; give a new folder.")
shutil.copytree(source, target, ignore=shutil.ignore_patterns("backups", "*.bak"))
kinds = {k["key"]: k for k in json.load(open(os.path.join(source, "library_kinds.json"), encoding="utf-8"))["kinds"]}
wanted = [("Books", "books"), ("Audiobooks", "audiobooks"), ("Movies", "movies"), ("TV", "tv"), ("Music", "music"),
          ("Comics", "comics"), ("Videos", "other_videos")]
path = os.path.join(target, "libraries.json")
config = json.load(open(path, encoding="utf-8"))
config["libraries"] = [{
    "id": str(uuid.uuid5(uuid.NAMESPACE_URL, "synthetic-library/" + key)), "name": category, "category": category,
    "kind": "catalogued", "kind_key": key, "area": kinds[key]["area"], "metadata_policy": "enriched",
    "media_types": [kinds[key]["format"]], "youtube_lookup": False, "sources": [],
} for category, key in wanted]
json.dump(config, open(path, "w", encoding="utf-8"), indent=2)
print(f"Config source ready at {target} with {len(wanted)} libraries.")
