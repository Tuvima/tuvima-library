#!/usr/bin/env python3
"""Compare an ingestion baseline record with the synthetic library's manifest.

    python Analyze-Run.py <baseline-record.json> <synthetic-manifest.json> [--out report.md]

The manifest says what every file is meant to be (Series, position, novella,
show, season, album, hard case). The record says what the Engine made of it.
The report covers: what was registered, match results per type, Series and
Universe grouping, order, editions, hard cases, Needs Review, photos, timings
and outside requests. Plain text, no paths outside the sample root.
"""
import collections
import json
import re
import statistics
import sys

MATCHED = {"RetailMatched", "QidResolved", "Hydrating", "UniverseEnriching", "Ready",
           "ReadyWithoutUniverse", "Completed", "BridgeSearching", "QidNoMatch"}
REVIEW = {"RetailMatchedNeedsReview", "QidNeedsReview"}
NOMATCH = {"RetailNoMatch", "Failed"}


def norm(text):
    return re.sub(r"[^a-z0-9]+", " ", (text or "").lower()).strip()


def pct(part, whole):
    return f"{(100.0 * part / whole):.0f}%" if whole else "n/a"


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    out_path = sys.argv[sys.argv.index("--out") + 1] if "--out" in sys.argv else None
    record = json.load(open(args[0], encoding="utf-8"))
    manifest = json.load(open(args[1], encoding="utf-8"))["files"]
    for f in manifest:
        f["path"] = f["path"].replace("\\", "/")
    items = {i["path"]: i for i in record["items"]}
    views = {v["path"]: v for v in record.get("view_items", [])}
    lines = []
    say = lines.append

    def group_of(item, kinds=("series", "contentgroup")):
        for g in item.get("groups", []):
            if any(k in (g.get("type") or "").lower() for k in kinds):
                return g.get("name") or g.get("qid")
        return None

    def universe_of(item):
        return group_of(item, ("universe", "franchise", "parent"))

    # 1. Registration ---------------------------------------------------
    say("# Synthetic library run: analysis\n")
    meta = record.get("metadata", {})
    say(f"Run: {meta.get('label')} at build {meta.get('build_commit')}; "
        f"total {meta.get('total_duration_seconds')} seconds.\n")
    say("## 1. What was registered\n")
    say("| Kind | Files made | Seen by the Engine | Missing |")
    say("|---|---|---|---|")
    by_kind = collections.defaultdict(list)
    for f in manifest:
        by_kind[f["kind"]].append(f)
    missing_examples = {}
    for kind, files in sorted(by_kind.items()):
        seen = [f for f in files if f["path"] in items or f["path"] in views]
        miss = [f for f in files if f["path"] not in items and f["path"] not in views]
        missing_examples[kind] = miss
        say(f"| {kind} | {len(files)} | {len(seen)} | {len(miss)} |")
    say("")
    for kind, miss in missing_examples.items():
        if miss:
            hard = collections.Counter(f.get("hard") or "ordinary" for f in miss)
            say(f"- Missing {kind}: " + ", ".join(f"{k} {v}" for k, v in hard.most_common(8))
                + f". Examples: {'; '.join(f['path'] for f in miss[:4])}")
    extra = [p for p in items if p not in {f['path'] for f in manifest}]
    if extra:
        say(f"- Registered but not in the manifest: {len(extra)} (for example {extra[:3]})")
    say("")

    # 2. Match results ----------------------------------------------------
    say("## 2. Match results by media type\n")
    say("| Media type | Files | Matched | Needs Review | No match | Still waiting | With Wikidata ID | With cover |")
    say("|---|---|---|---|---|---|---|---|")
    per_type = collections.defaultdict(list)
    for i in items.values():
        per_type[i["media_type"]].append(i)
    for mt, its in sorted(per_type.items()):
        st = collections.Counter(i["identity"].get("job_state") or "none" for i in its)
        m = sum(v for k, v in st.items() if k in MATCHED)
        r = sum(v for k, v in st.items() if k in REVIEW)
        n = sum(v for k, v in st.items() if k in NOMATCH)
        w = len(its) - m - r - n
        qid = sum(1 for i in its if i["work"].get("qid") or i["identity"].get("wikidata_qid")
                  or i["identity"].get("resolved_qid"))
        art = sum(1 for i in its if i.get("artwork"))
        say(f"| {mt} | {len(its)} | {m} ({pct(m, len(its))}) | {r} | {n} | {w} | "
            f"{qid} ({pct(qid, len(its))}) | {art} ({pct(art, len(its))}) |")
    say("")
    say("Job states: " + json.dumps(record["counts"].get("identity_job_states", {})) + "\n")

    # 3. Wrong matches: title check ---------------------------------------
    say("## 3. Likely wrong matches (title the Engine chose does not resemble the file's title)\n")
    wrong = []
    for f in manifest:
        it = items.get(f["path"])
        want = f.get("title") or f.get("album") if f["kind"] in ("book", "film", "audiobook-file") else None
        if not it or not want:
            continue
        got = it["work"].get("title") or it["identity"].get("title")
        if f["kind"] == "audiobook-file" and it.get("parent_work"):
            got = it["identity"].get("title") or got
        a, b = norm(want), norm(got)
        if a and b and a not in b and b not in a and not (set(a.split()) & set(b.split()) - {"the", "a", "of"}):
            wrong.append((f["kind"], f["path"], want, got, it["identity"].get("job_state")))
    say(f"{len(wrong)} found.")
    for k, p, want, got, st in wrong[:60]:
        say(f"- {k}: `{p}` expected \"{want}\", Engine says \"{got}\" ({st})")
    say("")

    # 4. Book Series --------------------------------------------------------
    say("## 4. Book Series grouping\n")
    say("| Series | Books owned | In a Series group | Group names used | Universe | Order right | Novellas in group |")
    say("|---|---|---|---|---|---|---|")
    series = collections.defaultdict(list)
    for f in by_kind.get("book", []):
        if f.get("series"):
            series[f["series"]].append(f)
    totals = collections.Counter()
    problems = []
    for name, files in sorted(series.items()):
        present = [(f, items[f["path"]]) for f in files if f["path"] in items]
        grouped = [(f, i) for f, i in present if group_of(i)]
        names = collections.Counter(group_of(i) for f, i in grouped)
        unis = collections.Counter(universe_of(i) for f, i in present if universe_of(i))
        main_name = names.most_common(1)[0][0] if names else None
        in_main = [(f, i) for f, i in grouped if group_of(i) == main_name]
        ordered = [(f.get("position"), i["work"].get("ordinal")) for f, i in in_main
                   if not f.get("novella") and f.get("position") is not None]
        by_pos = [o for p, o in sorted(ordered, key=lambda x: x[0])]
        order_ok = ("n/a" if len(by_pos) < 2 else
                    "missing numbers" if any(o is None for o in by_pos) else
                    "yes" if by_pos == sorted(by_pos) and len(set(by_pos)) == len(by_pos) else "NO")
        nov = [f for f in files if f.get("novella")]
        nov_in = [f for f, i in in_main if f.get("novella")]
        say(f"| {name} | {len(files)} | {len(grouped)} | "
            f"{'; '.join(f'{n} ({c})' for n, c in names.most_common(4)) or 'none'} | "
            f"{'; '.join(str(u) for u in unis) or 'none'} | {order_ok} | "
            f"{len(nov_in)}/{len(nov)} |")
        totals["series"] += 1
        totals["books"] += len(files)
        totals["grouped"] += len(grouped)
        totals["one_group"] += 1 if len(names) == 1 and len(in_main) == len(present) and present else 0
        totals["split"] += 1 if len(names) > 1 else 0
        totals["none"] += 1 if not names else 0
        totals["order_bad"] += 1 if order_ok in ("NO", "missing numbers") else 0
        for f, i in present:
            if not group_of(i):
                problems.append(f"{name}: \"{f.get('title')}\" not in any Series "
                                f"({i['identity'].get('job_state')}, tag in file: {f.get('series_tag')})")
    say("")
    say(f"Summary: {totals['series']} Series, {totals['books']} books; {totals['grouped']} books sit in a Series "
        f"group ({pct(totals['grouped'], totals['books'])}); {totals['one_group']} Series fully in one group, "
        f"{totals['split']} split across several groups, {totals['none']} with no group at all, "
        f"{totals['order_bad']} with wrong or missing order.\n")
    for p in problems[:80]:
        say(f"- {p}")
    say("")
    stand = [(f, items[f["path"]]) for f in by_kind.get("book", []) if not f.get("series") and f["path"] in items]
    wrongly = [(f, i) for f, i in stand if group_of(i)]
    say(f"Standalone books put in a Series: {len(wrongly)} of {len(stand)} "
        "(some are real Series the catalogue did not list; check by eye).")
    for f, i in wrongly[:40]:
        say(f"- \"{f.get('title')}\" by {f.get('author')} -> {group_of(i)}")
    say("")

    # 5. Cross-format -------------------------------------------------------
    say("## 5. Same story in several formats\n")
    book_by_title = collections.defaultdict(list)
    for f in by_kind.get("book", []):
        book_by_title[norm(f.get("title"))].append(f)
    seen_titles = set()
    linked = unlinked = 0
    rows = []
    for f in by_kind.get("audiobook-file", []):
        key = norm(f.get("title"))
        if key in seen_titles or key not in book_by_title or f["path"] not in items:
            continue
        seen_titles.add(key)
        a = items[f["path"]]
        b = next((items[x["path"]] for x in book_by_title[key] if x["path"] in items), None)
        if not b:
            continue
        aq = a["work"].get("qid") or a["identity"].get("wikidata_qid") or (a.get("parent_work") or {}).get("qid")
        bq = b["work"].get("qid") or b["identity"].get("wikidata_qid")
        same_work = bool(set(a["work"].get("assets", [])) & {x["path"] for x in book_by_title[key]})
        same = same_work or (aq and aq == bq)
        linked += 1 if same else 0
        unlinked += 0 if same else 1
        rows.append(f"- {f.get('title')}: book ID {bq}, audiobook ID {aq}, "
                    f"{'linked' if same else 'NOT linked'}; Series book={group_of(b)} audiobook={group_of(a)}")
    say(f"Audiobooks that also exist as a book: {linked + unlinked}; linked to the same Work or Wikidata ID: {linked}.")
    lines.extend(rows[:60])
    say("")

    # 6. Comics -------------------------------------------------------------
    say("## 6. Comics\n")
    runs = collections.defaultdict(list)
    for f in by_kind.get("comic", []):
        runs[f.get("series") or f["path"].split("/")[1]].append(f)
    say("| Run | Issues | Registered | Matched | Group names | Order right |")
    say("|---|---|---|---|---|---|")
    for name, files in sorted(runs.items()):
        present = [(f, items[f["path"]]) for f in files if f["path"] in items]
        m = sum(1 for f, i in present if i["identity"].get("job_state") in MATCHED)
        names = collections.Counter(group_of(i) or (i.get("parent_work") or {}).get("title") or "none" for f, i in present)
        o = [i["work"].get("ordinal") for f, i in sorted(present, key=lambda x: float(x[0].get("number") or 0))]
        ok = "n/a" if len(o) < 2 else "missing" if None in o else "yes" if o == sorted(o) else "NO"
        say(f"| {name} | {len(files)} | {len(present)} | {m} | "
            f"{'; '.join(f'{n} ({c})' for n, c in names.most_common(3))} | {ok} |")
    say("")

    # 7. Films ----------------------------------------------------------------
    say("## 7. Films\n")
    films = by_kind.get("film", [])
    by_title = collections.defaultdict(list)
    for f in films:
        by_title[(norm(f.get("title")), f.get("year"))].append(f)
    say("Several files of one film (cuts or copies): are they one Work with several Editions?")
    for (t, y), fs in sorted(by_title.items(), key=str):
        if len(fs) < 2:
            continue
        present = [items[f["path"]] for f in fs if f["path"] in items]
        works = {tuple(i["work"].get("assets", [])) for i in present}
        eds = {tuple(i["edition"].get("assets", [])) for i in present}
        say(f"- {fs[0].get('title')} ({y}): {len(fs)} files, {len(present)} registered, "
            f"{len(works)} Work(s), {len(eds)} Edition(s); labels "
            f"{[i['edition'].get('format_label') for i in present]}; file cuts {[f.get('edition') for f in fs]}")
    same_name = collections.defaultdict(list)
    for f in films:
        same_name[norm(f.get("title"))].append(f)
    say("\nSame title, different years (remakes): must stay separate and get the right year.")
    for t, fs in sorted(same_name.items()):
        years = {f.get("year") for f in fs}
        if len(years) < 2:
            continue
        got = []
        for f in fs:
            i = items.get(f["path"])
            sc = (i or {}).get("values", {}).get("asset", {}).get("scalars", {})
            got.append(f"{f.get('year')}->{(sc.get('year') or {}).get('value') if i else 'not registered'}")
        say(f"- {fs[0].get('title')}: {', '.join(got)}")
    grp = collections.Counter(group_of(items[f['path']], ("series", "contentgroup", "collection", "universe", "franchise"))
                              for f in films if f["path"] in items)
    say(f"\nFilm groups found (name: films): {dict(grp.most_common(40))}")
    for kind in ("film-extra",):
        for f in by_kind.get(kind, []):
            i = items.get(f["path"])
            say(f"- Extra `{f['path']}`: " + ("not registered" if not i else
                f"registered as {i['media_type']} \"{i['work'].get('title')}\" ({i['identity'].get('job_state')})"))
    say("")

    # 8. TV ---------------------------------------------------------------------
    say("## 8. TV shows\n")
    shows = collections.defaultdict(list)
    for f in by_kind.get("episode", []):
        shows[f["path"].split("/")[1]].append(f)
    say("| Show folder | Episodes | Registered | Matched | Show names the Engine used | Seasons right | Hard cases |")
    say("|---|---|---|---|---|---|---|")
    for folder, files in sorted(shows.items()):
        present = [(f, items[f["path"]]) for f in files if f["path"] in items]
        m = sum(1 for f, i in present if i["identity"].get("job_state") in MATCHED)
        names = collections.Counter()
        season_ok = season_n = 0
        for f, i in present:
            sc = i.get("values", {}).get("asset", {}).get("scalars", {})
            names[(sc.get("show_name") or sc.get("series") or sc.get("show") or {}).get("value")
                  or (i.get("parent_work") or {}).get("title") or group_of(i) or "none"] += 1
            s = (sc.get("season_number") or sc.get("season") or {}).get("value")
            e = (sc.get("episode_number") or sc.get("episode") or {}).get("value")
            if f.get("season") is not None and s is not None:
                season_n += 1
                season_ok += 1 if str(s) == str(f["season"]) and (e is None or str(e) == str(f.get("episode"))) else 0
        hard = collections.Counter(f.get("hard") for f in files if f.get("hard"))
        say(f"| {folder} | {len(files)} | {len(present)} | {m} | "
            f"{'; '.join(f'{n} ({c})' for n, c in names.most_common(3))} | {season_ok}/{season_n} | "
            f"{dict(hard) or ''} |")
    say("")

    # 9. Music ------------------------------------------------------------------
    say("## 9. Music\n")
    albums = collections.defaultdict(list)
    for f in by_kind.get("track", []):
        albums[(f.get("artist"), f.get("album"))].append(f)
    full = split = 0
    notes = []
    for (artist, album), files in sorted(albums.items(), key=lambda x: str(x[0])):
        present = [(f, items[f["path"]]) for f in files if f["path"] in items]
        parents = collections.Counter(tuple((i.get("parent_work") or i["work"]).get("assets", []))[:1] for f, i in present)
        m = sum(1 for f, i in present if i["identity"].get("job_state") in MATCHED)
        hard = {f.get("hard") for f in files if f.get("hard")}
        if len(parents) == 1 and len(present) == len(files):
            full += 1
        else:
            split += 1
        if len(parents) != 1 or m < len(present) or hard:
            notes.append(f"- {artist} / {album}: {len(files)} tracks, {len(present)} registered, {m} matched, "
                         f"{len(parents)} album group(s){', hard case: ' + ', '.join(sorted(hard)) if hard else ''}")
    say(f"{len(albums)} albums: {full} whole in one album group, {split} split or incomplete.")
    lines.extend(notes[:80])
    say("")

    # 10. Hard cases ----------------------------------------------------------------
    say("## 10. Hard cases, by label\n")
    say("| Hard case | Files | Registered | Matched | Needs Review | No match | Waiting |")
    say("|---|---|---|---|---|---|---|")
    hard = collections.defaultdict(list)
    for f in manifest:
        if f.get("hard"):
            hard[f"{f['kind']}: {f['hard']}"].append(f)
    for label, files in sorted(hard.items()):
        present = [items[f["path"]] for f in files if f["path"] in items]
        st = collections.Counter(i["identity"].get("job_state") for i in present)
        m = sum(v for k, v in st.items() if k in MATCHED)
        r = sum(v for k, v in st.items() if k in REVIEW)
        n = sum(v for k, v in st.items() if k in NOMATCH)
        in_view = sum(1 for f in files if f["path"] in views)
        say(f"| {label} | {len(files)} | {len(present) + in_view} | {m} | {r} | {n} | {len(present) - m - r - n} |")
    say("")

    # 11. Needs Review -----------------------------------------------------------
    say("## 11. Needs Review\n")
    trig = collections.Counter()
    for i in items.values():
        for r in i.get("review", []):
            trig[(i["media_type"], r.get("trigger"), r.get("status"))] += 1
    for r in record.get("unattached_review", []):
        trig[("not tied to a file", r.get("trigger"), r.get("status"))] += 1
    say(f"Total entries: {sum(trig.values())}")
    for (mt, t, s), c in trig.most_common():
        say(f"- {mt}: {t} ({s}): {c}")
    say("")

    # 12. Collections and Universes ----------------------------------------------
    say("## 12. Series and Universe records\n")
    cols = record.get("collections", [])
    types = collections.Counter(c.get("type") for c in cols)
    say(f"{len(cols)} group records: {dict(types)}; Series manifest rows: {len(record.get('series_members', []))}")
    empty = [c for c in cols if not c.get("assets")]
    say(f"Groups with no owned files: {len(empty)}")
    for c in sorted(cols, key=lambda c: (-len(c.get("assets", [])), c.get("name") or ""))[:120]:
        say(f"- {c.get('type')}: {c.get('name')} [{c.get('qid')}] parent={c.get('parent')} "
            f"status={c.get('status')} files={len(c.get('assets', []))}")
    dup = collections.Counter(norm(c.get("name")) for c in cols)
    dups = {n: k for n, k in dup.items() if k > 1 and n}
    say(f"\nGroup names used by more than one record: {dups or 'none'}")
    owned = collections.Counter(m.get("series_qid") for m in record.get("series_members", []) if m.get("owned"))
    total = collections.Counter(m.get("series_qid") for m in record.get("series_members", []))
    say("Owned / total per Series manifest: " + ", ".join(f"{q} {owned[q]}/{total[q]}" for q in sorted(total)))
    say("")

    # 13. People ---------------------------------------------------------------------
    say("## 13. People\n")
    people = collections.defaultdict(set)
    for i in items.values():
        for p in i.get("people", []):
            people[norm(p.get("name"))].add((p.get("name"), p.get("qid")))
    with_id = sum(1 for v in people.values() if any(q for n, q in v))
    say(f"{len(people)} distinct names; {with_id} have a Wikidata ID.")
    for n, v in sorted(people.items()):
        if len({q for _, q in v if q}) > 1:
            say(f"- One name, several people: {sorted(v, key=str)}")
    say("")

    # 14. Photos ----------------------------------------------------------------------
    say("## 14. Photos and personal videos\n")
    ph = by_kind.get("photo", []) + by_kind.get("personal-video", [])
    reg = [(f, views[f["path"]]) for f in ph if f["path"] in views]
    dated_ok = sum(1 for f, v in reg if f.get("dated") and v.get("captured_at"))
    dated_all = sum(1 for f, v in reg if f.get("dated"))
    undated_with = sum(1 for f, v in reg if f.get("dated") is False and v.get("captured_at"))
    placed_ok = sum(1 for f, v in reg if f.get("placed") and (v.get("details") or {}).get("latitude"))
    placed_all = sum(1 for f, v in reg if f.get("placed"))
    say(f"{len(reg)} of {len(ph)} registered. Dated photos with a date: {dated_ok}/{dated_all}. "
        f"Undated photos given a date anyway (from the file's own date): {undated_with}. "
        f"Photos with a place read: {placed_ok}/{placed_all}.")
    years = collections.Counter((v.get("captured_at") or "none")[:4] for f, v in reg)
    say(f"Timeline years: {dict(sorted(years.items()))}\n")

    # 15. Time and requests ---------------------------------------------------------------
    say("## 15. Time and outside requests\n")
    t = record.get("timings", {}).get("registered_to_identified_seconds", {})
    per = collections.defaultdict(list)
    for p, s in t.items():
        if p in items and s is not None:
            per[items[p]["media_type"]].append(s)
    say("| Media type | Titles timed | Median minutes, registered to identified | Slowest tenth | Longest |")
    say("|---|---|---|---|---|")
    for mt, v in sorted(per.items()):
        v.sort()
        say(f"| {mt} | {len(v)} | {statistics.median(v) / 60:.1f} | {v[int(len(v) * 0.9)] / 60:.1f} | {v[-1] / 60:.1f} |")
    say("")
    say("Requests per outside host: " + json.dumps(meta.get("http_hosts", {})))
    say("Facts written per source: " + json.dumps(meta.get("provider_claims", {})))
    say("Counts: " + json.dumps(record.get("counts", {}).get("by_media_type", {})))

    text = "\n".join(lines) + "\n"
    if out_path:
        open(out_path, "w", encoding="utf-8").write(text)
        print(f"Wrote {out_path} ({len(lines)} lines)")
    else:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stdout.write(text)


if __name__ == "__main__":
    main()
