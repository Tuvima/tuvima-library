#!/usr/bin/env python3
"""Generates a large synthetic media library (placeholder files only, a few hundred MB) for an ingestion test.
Usage: python gen.py <out-root> [--no-network]
Needs Python 3.10+, Pillow, and ffmpeg (on PATH, or its full path in the FFMPEG environment variable).
Track lists come from tracklists.json beside this script; an album missing there is looked up on MusicBrainz
unless --no-network is given. Writes <out-root>/../synthetic-manifest.json listing every file and its hard case."""
import io, json, os, subprocess, sys, time, urllib.parse, urllib.request, zipfile, hashlib
from PIL import Image, ImageDraw

OUT = os.path.abspath(sys.argv[1])
NET = "--no-network" not in sys.argv
WORK = os.path.join(os.path.dirname(OUT), "gen-work")
os.makedirs(WORK, exist_ok=True)
count = {}
manifest = []

def note(kind, path, **facts):
    count[kind] = count.get(kind, 0) + 1
    manifest.append({"kind": kind, "path": os.path.relpath(path, OUT), **facts})

def ensure(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    return path

def safe(name):
    for ch in '<>:"/\\|?*':
        name = name.replace(ch, " " if ch != ":" else " -")
    return " ".join(name.split()).rstrip(". ")

FFMPEG = os.environ.get("FFMPEG", "ffmpeg")
def ff(*args):
    subprocess.run([FFMPEG, "-loglevel", "error", "-y", *args], check=True)

# ---------------------------------------------------------------- base media
def base_video(name, seconds, ext, subs=False):
    path = os.path.join(WORK, f"{name}.{ext}")
    if os.path.exists(path):
        return path
    args = ["-f", "lavfi", "-i", f"color=c=black:s=320x180:r=1:d={seconds}",
            "-f", "lavfi", "-i", f"anullsrc=r=8000:cl=mono"]
    maps = ["-map", "0:v", "-map", "1:a"]
    if subs:
        srt = os.path.join(WORK, "s.srt")
        open(srt, "w").write("1\n00:00:01,000 --> 00:00:04,000\nPlaceholder subtitle\n")
        args += ["-i", srt]
        maps += ["-map", "2:s", "-c:s", "srt" if ext == "mkv" else "mov_text", "-metadata:s:s:0", "language=eng"]
    ff(*args, *maps, "-t", str(seconds), "-c:v", "libx264", "-preset", "ultrafast", "-crf", "45", "-g", "600",
       "-c:a", "aac", "-b:a", "8k", "-metadata:s:a:0", "language=eng", path)
    return path

def base_audio(name, seconds, ext):
    path = os.path.join(WORK, f"{name}.{ext}")
    if os.path.exists(path):
        return path
    codec = {"mp3": ["-c:a", "libmp3lame", "-b:a", "8k"], "flac": ["-c:a", "flac"], "m4a": ["-c:a", "aac", "-b:a", "8k"]}[ext]
    ff("-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-t", str(seconds), *codec, path)
    return path

def tagged_copy(base, out, tags, fmt=None, chapters=None):
    if os.path.exists(out): return
    args = ["-i", base]
    if chapters:
        meta = os.path.join(WORK, "chapters.txt")
        with open(meta, "w") as f:
            f.write(";FFMETADATA1\n")
            for i, (start, end, title) in enumerate(chapters):
                f.write(f"[CHAPTER]\nTIMEBASE=1/1\nSTART={start}\nEND={end}\ntitle={title}\n")
        args += ["-i", meta, "-map_chapters", "1"]
    args += ["-map", "0", "-c", "copy"]
    for k, v in tags.items():
        if v is not None and v != "":
            args += ["-metadata", f"{k}={v}"]
    if fmt:
        args += ["-f", fmt]
    ff(*args, ensure(out))

# ---------------------------------------------------------------- pictures
def cover(title, sub="", size=(300, 450), seed=None):
    h = hashlib.sha1((seed or title).encode()).digest()
    img = Image.new("RGB", size, (40 + h[0] % 120, 40 + h[1] % 120, 40 + h[2] % 120))
    d = ImageDraw.Draw(img)
    y = 30
    for line in [title[i:i + 28] for i in range(0, min(len(title), 112), 28)] + [sub]:
        d.text((16, y), line, fill=(255, 255, 255))
        y += 18
    return img

def jpeg_bytes(img, exif=None):
    b = io.BytesIO()
    img.save(b, "JPEG", quality=70, **({"exif": exif} if exif else {}))
    return b.getvalue()

# ---------------------------------------------------------------- books
def epub(path, title, author, lang="en", isbn=None, series=None, index=None, year=None, with_cover=False, publisher=None, authors=None):
    if os.path.exists(path): return
    uid = isbn or ("urn:uuid:" + hashlib.md5((title + author + path).encode()).hexdigest())
    creators = authors or [author]
    meta = [f'<dc:title>{esc(title)}</dc:title>'] + [f'<dc:creator opf:role="aut">{esc(a)}</dc:creator>' for a in creators]
    meta += [f'<dc:language>{lang}</dc:language>', f'<dc:identifier id="bookid"{" opf:scheme=\"ISBN\"" if isbn else ""}>{uid}</dc:identifier>']
    if year: meta.append(f'<dc:date>{year}-01-01</dc:date>')
    if publisher: meta.append(f'<dc:publisher>{esc(publisher)}</dc:publisher>')
    if series:
        meta.append(f'<meta name="calibre:series" content="{esc(series)}"/>')
        if index is not None: meta.append(f'<meta name="calibre:series_index" content="{index}"/>')
    if with_cover: meta.append('<meta name="cover" content="cover-img"/>')
    opf = f'''<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="bookid">
<metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">{"".join(meta)}</metadata>
<manifest><item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
<item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/><item id="c2" href="c2.xhtml" media-type="application/xhtml+xml"/>
{'<item id="cover-img" href="cover.jpg" media-type="image/jpeg"/>' if with_cover else ''}</manifest>
<spine toc="ncx"><itemref idref="c1"/><itemref idref="c2"/></spine></package>'''
    ncx = f'''<?xml version="1.0" encoding="utf-8"?><ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><head><meta name="dtb:uid" content="{uid}"/></head>
<docTitle><text>{esc(title)}</text></docTitle><navMap><navPoint id="n1" playOrder="1"><navLabel><text>Chapter 1</text></navLabel><content src="c1.xhtml"/></navPoint>
<navPoint id="n2" playOrder="2"><navLabel><text>Chapter 2</text></navLabel><content src="c2.xhtml"/></navPoint></navMap></ncx>'''
    def chap(n):
        body = " ".join(["Placeholder text for a synthetic test library."] * 60)
        return f'<?xml version="1.0" encoding="utf-8"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter {n}</title></head><body><h1>Chapter {n}</h1><p>{esc(title)} — {body}</p></body></html>'
    with zipfile.ZipFile(ensure(path), "w") as z:
        z.writestr(zipfile.ZipInfo("mimetype"), "application/epub+zip")
        z.writestr("META-INF/container.xml", '<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>')
        z.writestr("OEBPS/content.opf", opf, zipfile.ZIP_DEFLATED)
        z.writestr("OEBPS/toc.ncx", ncx, zipfile.ZIP_DEFLATED)
        z.writestr("OEBPS/c1.xhtml", chap(1), zipfile.ZIP_DEFLATED)
        z.writestr("OEBPS/c2.xhtml", chap(2), zipfile.ZIP_DEFLATED)
        if with_cover: z.writestr("OEBPS/cover.jpg", jpeg_bytes(cover(title, author)))

def esc(s):
    return str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")

D = json.load(open(os.path.join(os.path.dirname(__file__), "catalogue.json"), encoding="utf-8"))
B = os.path.join(OUT, "books")
n = 0
for s in D["book_series"]:
    for i, item in enumerate(s["books"], 1):
        title, idx = (item, i) if isinstance(item, str) else (item["t"], item.get("i", None))
        extra = {} if isinstance(item, str) else item
        author = extra.get("a", s["author"])
        n += 1
        style = n % 4
        embed_series = s.get("embed", True) and style != 3   # a quarter carry no series tag at all
        if style == 0:   path = f"{B}/{safe(author)}/{safe(s['name'])}/{idx if idx is not None else 0:02} - {safe(title)}.epub" if idx is not None else f"{B}/{safe(author)}/{safe(s['name'])}/{safe(title)}.epub"
        elif style == 1: path = f"{B}/{safe(author)}/{safe(title)}.epub"
        elif style == 2: path = f"{B}/{safe(title)} - {safe(author)}.epub"
        else:            path = f"{B}/{safe(author)} - {safe(title)}.epub"
        epub(path, title, author, lang=s.get("lang", "en"), isbn=extra.get("isbn"), series=s["name"] if embed_series else None,
             index=idx if embed_series else None, with_cover=(n % 5 == 0), authors=extra.get("authors"))
        note("book", path, title=title, author=author, series=s["name"], position=idx, novella=extra.get("novella", False), series_tag=embed_series)
for b in D["books"]:
    n += 1
    path = f"{B}/{safe(b['a'])}/{safe(b['t'])}.epub" if n % 2 else f"{B}/{safe(b['t'])} - {safe(b['a'])}.epub"
    epub(path, b["t"], b["a"], lang=b.get("lang", "en"), isbn=b.get("isbn"), with_cover=(n % 5 == 0), authors=b.get("authors"))
    note("book", path, title=b["t"], author=b["a"], lang=b.get("lang", "en"))
# second editions of the same book (different publisher, own ISBN-less file), and hard cases
for t, a, pub in [("Dune", "Frank Herbert", "Gollancz"), ("Leviathan Wakes", "James S. A. Corey", "Orbit UK"), ("The Hobbit", "J. R. R. Tolkien", "HarperCollins"), ("Pride and Prejudice", "Jane Austen", "Penguin Classics")]:
    path = f"{B}/_second editions/{safe(t)} ({pub}).epub"
    epub(path, t, a, publisher=pub, year=2010)
    note("book", path, title=t, author=a, hard="second edition of a book already present")
epub(f"{B}/untitled.epub", "", "", lang="en"); note("book", f"{B}/untitled.epub", hard="no title or author inside, meaningless file name")
epub(f"{B}/book1.epub", "Unknown", "Unknown"); note("book", f"{B}/book1.epub", hard="placeholder title and author")
open(ensure(f"{B}/Broken Book - Nobody.epub"), "wb").write(b"this is not a zip file"); note("book", f"{B}/Broken Book - Nobody.epub", hard="corrupt file")
open(ensure(f"{B}/Empty File.epub"), "wb").close(); note("book", f"{B}/Empty File.epub", hard="zero-byte file")
open(ensure(f"{B}/notes.txt"), "w").write("stray text file\n"); open(ensure(f"{B}/Thumbs.db"), "wb").write(b"\0" * 64)

# ---------------------------------------------------------------- comics
C = os.path.join(OUT, "comics")
def cbz(path, series, number, title=None, writer=None, year=None, info=True, volume=None, pages=4, publisher=None, manga=False):
    if os.path.exists(path): return
    with zipfile.ZipFile(ensure(path), "w") as z:
        for p in range(1, pages + 1):
            z.writestr(f"{p:03}.jpg", jpeg_bytes(cover(f"{series} #{number}", f"page {p}", size=(200, 300), seed=f"{path}{p}")))
        if info:
            x = ['<?xml version="1.0" encoding="utf-8"?><ComicInfo>', f"<Series>{esc(series)}</Series>", f"<Number>{number}</Number>"]
            if title: x.append(f"<Title>{esc(title)}</Title>")
            if writer: x.append(f"<Writer>{esc(writer)}</Writer>")
            if year: x.append(f"<Year>{year}</Year>")
            if volume: x.append(f"<Volume>{volume}</Volume>")
            if publisher: x.append(f"<Publisher>{esc(publisher)}</Publisher>")
            if manga: x.append("<Manga>YesAndRightToLeft</Manga>")
            x.append(f"<PageCount>{pages}</PageCount></ComicInfo>")
            z.writestr("ComicInfo.xml", "".join(x))
for k, c in enumerate(D["comics"]):
    for num in range(c.get("from", 1), c["to"] + 1):
        info = k % 3 != 2      # a third of the runs have no ComicInfo, names only
        if c.get("kind") == "volume":
            path = f"{C}/{safe(c['series'])}/{safe(c['series'])} v{num:02}.cbz"
        elif k % 2:
            path = f"{C}/{safe(c['series'])} ({c['year']})/{safe(c['series'])} {num:03} ({c['year'] + (num - 1) // 12}).cbz"
        else:
            path = f"{C}/{safe(c['series'])}/{safe(c['series'])} #{num}.cbz"
        cbz(path, c["series"], num, writer=c.get("writer"), year=c["year"], info=info, publisher=c.get("publisher"), manga=c.get("manga", False))
        note("comic", path, series=c["series"], number=num, comicinfo=info)
cbz(f"{C}/Watchmen (Complete Edition).cbz", "Watchmen", 1, title="Watchmen", writer="Alan Moore", year=1987, pages=6)
note("comic", f"{C}/Watchmen (Complete Edition).cbz", hard="collected edition beside the single issues")
cbz(f"{C}/scan0001.cbz", "", 1, info=False); note("comic", f"{C}/scan0001.cbz", hard="meaningless file name, no details inside")

# ---------------------------------------------------------------- audiobooks
A = os.path.join(OUT, "audiobooks")
m4b_base = base_audio("ab", 3600, "m4a")
mp3_part = base_audio("abpart", 1200, "mp3")
for k, ab in enumerate(D["audiobooks"]):
    t, a, nar = ab["t"], ab["a"], ab.get("n")
    if k % 3 == 2:   # folder of numbered mp3 parts
        parts = 4
        for p in range(1, parts + 1):
            path = f"{A}/{safe(a)}/{safe(t)}/{safe(t)} - Part {p:02}.mp3"
            tagged_copy(mp3_part, path, {"title": f"{t} - Part {p}", "album": t, "artist": a, "album_artist": a, "composer": nar, "track": f"{p}/{parts}", "genre": "Audiobook", "comment": f"u{k}-{p}"})
            note("audiobook-file", path, title=t, author=a, layout="folder of mp3 parts")
    else:
        path = f"{A}/{safe(a)}/{safe(t)}.m4b" if k % 3 == 0 else f"{A}/{safe(a)} - {safe(t)}.m4b"
        ch = [(i * 600, (i + 1) * 600, f"Chapter {i + 1}") for i in range(6)]
        tags = {"title": t, "album": t, "artist": a, "album_artist": a, "composer": nar, "genre": "Audiobook", "comment": f"u{k}"}
        if ab.get("series"): tags["grouping"] = f"{ab['series']} #{ab.get('i', '')}".rstrip(" #")
        tagged_copy(m4b_base, path, tags, fmt="ipod", chapters=ch)
        note("audiobook-file", path, title=t, author=a, layout="single m4b with chapters")
    count["audiobook-title"] = count.get("audiobook-title", 0) + 1

# ---------------------------------------------------------------- films
M = os.path.join(OUT, "movies")
film_mkv = base_video("film", 6000, "mkv", subs=True)
film_mp4 = base_video("film", 6000, "mp4")
for k, f in enumerate(D["films"]):
    t, y = f["t"], f["y"]
    style = k % 5
    ed = f.get("ed")
    label = f"{safe(t)} ({y})" + (f" {{edition-{ed}}}" if ed else "")
    if style == 0:   path, base = f"{M}/{safe(t)} ({y})/{label}.mkv", film_mkv
    elif style == 1: path, base = f"{M}/{label}.mp4", film_mp4
    elif style == 2: path, base = f"{M}/{safe(t).replace(' ', '.')}.{y}.{(ed.replace(' ', '.') + '.') if ed else ''}1080p.BluRay.x264-GRP.mkv", film_mkv
    elif style == 3: path, base = f"{M}/{f.get('group', 'Films')}/{label}.mkv", film_mkv
    else:            path, base = f"{M}/{safe(t)} ({y})/{safe(t)} ({y}){(' - ' + ed) if ed else ''} [2160p HDR].mkv", film_mkv
    embedded = {"title": t if k % 2 == 0 else None, "date": str(y) if k % 2 == 0 else None, "comment": f"f{k}"}
    tagged_copy(base, path, embedded)
    note("film", path, title=t, year=y, edition=ed, name_style=style)
for t, y, ex in [("Dune", 2021, "Trailers/Dune - Official Trailer.mkv"), ("Dune", 2021, "Featurettes/Making of Dune.mkv"), ("Blade Runner", 1982, "Deleted Scenes/Deleted Scene 1.mkv")]:
    path = f"{M}/{t} ({y})/{ex}"
    if not os.path.isdir(f"{M}/{t} ({y})"): path = f"{M}/_extras test/{t} ({y})/{ex}"
    tagged_copy(base_video("clip", 120, "mkv"), path, {"comment": ex}); note("film-extra", path, hard="extra in a named folder")
open(ensure(f"{M}/Dune (2021)/Dune (2021).en.srt"), "w").write("1\n00:00:01,000 --> 00:00:04,000\nPlaceholder\n")
open(ensure(f"{M}/Dune (2021)/movie.nfo"), "w").write("<movie><title>Dune</title><year>2021</year></movie>")
for nm in ["VID_20210704_181500.mp4", "movie.mkv", "sample.mkv", "final_cut_v2 (1).mp4"]:
    tagged_copy(base_video("clip", 120, "mkv") if nm.endswith("mkv") else base_video("clip", 120, "mp4"), f"{M}/{nm}", {"comment": nm}); note("film", f"{M}/{nm}", hard="stray file with a meaningless name")

# ---------------------------------------------------------------- tv
T = os.path.join(OUT, "tv")
ep_mkv = base_video("ep", 2700, "mkv", subs=True)
for k, s in enumerate(D["shows"]):
    name, year = s["name"], s.get("y")
    folder = f"{name} ({year})" if s.get("year_in_folder") else name
    for season, eps in s["seasons"].items():
        season = int(season)
        for e in range(1, eps + 1):
            if s.get("absolute"):
                path = f"{T}/{safe(folder)}/{safe(name)} - {e:02}.mkv"
            elif k % 3 == 0:
                path = f"{T}/{safe(folder)}/Season {season:02}/{safe(name)} - S{season:02}E{e:02}.mkv"
            elif k % 3 == 1:
                path = f"{T}/{safe(folder)}/Season {season}/{safe(name).replace(' ', '.')}.S{season:02}E{e:02}.1080p.WEB.h264-GRP.mkv"
            else:
                path = f"{T}/{safe(folder)}/{safe(name)} {season}x{e:02}.mkv" if season else f"{T}/{safe(folder)}/Specials/{safe(name)} - S00E{e:02}.mkv"
            tagged_copy(ep_mkv, path, {"comment": f"t{k}-{season}-{e}"})
            note("episode", path, show=name, season=season, episode=e)
    for extra in s.get("multi", []):
        path = f"{T}/{safe(folder)}/Season 01/{safe(name)} - {extra}.mkv"
        tagged_copy(ep_mkv, path, {"comment": f"t{k}-{extra}"}); note("episode", path, show=name, hard="one file holding two episodes", label=extra)
for nm in ["Some.Unknown.Show.S01E01.mkv", "The.Expanse.S03E07.720p.mkv", "episode 5.mkv"]:
    tagged_copy(ep_mkv, f"{T}/{nm}", {"comment": nm}); note("episode", f"{T}/{nm}", hard="stray episode at the top of the TV folder")

# ---------------------------------------------------------------- music
MU = os.path.join(OUT, "music")
mp3 = base_audio("trk", 200, "mp3"); flac = base_audio("trk", 200, "flac")
def mb(url):
    for attempt in range(4):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "SyntheticLibraryGenerator/1.0 (test fixture builder)"})
            with urllib.request.urlopen(req, timeout=30) as r:
                out = json.load(r)
            time.sleep(1.1)
            return out
        except Exception as ex:
            time.sleep(3 + attempt * 3)
    return None
cache_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "tracklists.json")
cache = json.load(open(cache_path)) if os.path.exists(cache_path) else {}
def tracklist(artist, album, discs):
    key = f"{artist}|{album}"
    if key in cache: return cache[key]
    media = None
    if NET:
        q = urllib.parse.quote(f'release:"{album}" AND artist:"{artist}" AND status:official')
        res = mb(f"https://musicbrainz.org/ws/2/release?query={q}&fmt=json&limit=10")
        cands = [r for r in (res or {}).get("releases", []) if len(r.get("media", [])) == discs] or (res or {}).get("releases", [])
        if cands:
            full = mb(f"https://musicbrainz.org/ws/2/release/{cands[0]['id']}?inc=recordings+artist-credits&fmt=json")
            if full:
                media = [[{"t": tr["title"], "a": "".join(c["name"] + c.get("joinphrase", "") for c in tr.get("artist-credit", []))} for tr in m["tracks"]] for m in full["media"] if m.get("tracks")]
    if not media:
        media = [[{"t": f"Track {i}", "a": artist} for i in range(1, 11)] for _ in range(discs)]
    cache[key] = media
    json.dump(cache, open(cache_path, "w"))
    return media
for k, al in enumerate(D["albums"]):
    artist, album, year = al["artist"], al["album"], al.get("y")
    media = tracklist(artist, album, al.get("discs", 1))
    base, ext = (flac, "flac") if k % 4 == 0 else (mp3, "mp3")
    various = al.get("various", False)
    for d, tracks in enumerate(media, 1):
        for i, tr in enumerate(tracks[:30], 1):
            disc_dir = f"/CD{d}" if len(media) > 1 and k % 2 == 0 else ""
            prefix = f"{d}-{i:02}" if len(media) > 1 and not disc_dir else f"{i:02}"
            folder = f"{MU}/{safe('Various Artists' if various else artist)}/{safe(album)}{f' ({year})' if year and k % 3 == 0 else ''}{disc_dir}"
            path = f"{folder}/{prefix} - {safe(tr['t'])[:80]}.{ext}"
            tags = {"title": tr["t"], "artist": tr["a"] if various or tr["a"] else artist, "album": album, "album_artist": "Various Artists" if various else artist,
                    "track": f"{i}/{len(tracks)}", "disc": f"{d}/{len(media)}" if len(media) > 1 else None, "date": str(year) if year else None,
                    "compilation": "1" if various else None, "comment": f"m{k}-{d}-{i}"}
            if al.get("untagged"): tags = {"comment": f"m{k}-{d}-{i}"}
            tagged_copy(base, path, tags)
            note("track", path, artist=artist, album=album, disc=d, track=i, tagged=not al.get("untagged", False))
    count["album"] = count.get("album", 0) + 1
    if k % 6 == 0:
        cover(album, artist, size=(300, 300)).save(ensure(f"{MU}/{safe('Various Artists' if various else artist)}/{safe(album)}{f' ({year})' if year and k % 3 == 0 else ''}/cover.jpg"))
for nm, tags in [("01 Track 01.mp3", {}), ("Queen - Bohemian Rhapsody.mp3", {"title": "Bohemian Rhapsody", "artist": "Queen"}), ("AUD-20220101-WA0001.mp3", {}), ("Nirvana - Rainbow Chaser.mp3", {"title": "Rainbow Chaser", "artist": "Nirvana", "date": "1968"})]:
    tagged_copy(mp3, f"{MU}/{nm}", {**tags, "comment": nm}); note("track", f"{MU}/{nm}", hard="stray song at the top of the music folder")

# ---------------------------------------------------------------- videos, personal videos, photos
V = os.path.join(OUT, "videos")
clip = base_video("lesson", 900, "mp4")
for course, sections in D["video_folders"].items():
    for sec, lessons in sections.items():
        for i, l in enumerate(lessons, 1):
            path = f"{V}/{course}/{(sec + '/') if sec else ''}{i:02} - {l}.mp4"
            tagged_copy(clip, path, {"title": l, "comment": path}); note("video", path, folder=course)
for nm in ["Conference Talk [aB3dE5fG7hI].mp4", "screen recording 2024-03-01.mp4", "How to Sharpen a Knife [xYz12345678].mp4", "clip.mp4"]:
    tagged_copy(base_video("clip", 120, "mp4"), f"{V}/{nm}", {"comment": nm}); note("video", f"{V}/{nm}", hard="single video at the top of the Videos folder")
PV = os.path.join(OUT, "personal_videos")
for i in range(1, 21):
    path = f"{PV}/{2018 + i % 6}/VID_{2018 + i % 6}{1 + i % 12:02}{1 + i % 27:02}_1{i:02}000.mp4"
    tagged_copy(base_video("clip", 120, "mp4"), path, {"creation_time": f"{2018 + i % 6}-{1 + i % 12:02}-{1 + i % 27:02}T10:00:00Z", "comment": path}); note("personal-video", path)
P = os.path.join(OUT, "personal_photos")
cities = [("Paris", 48.8584, 2.2945), ("Tokyo", 35.6586, 139.7454), ("New York", 40.6892, -74.0445), ("Sydney", -33.8568, 151.2153), ("Cairo", 29.9792, 31.1342),
          ("Rio de Janeiro", -22.9519, -43.2105), ("Reykjavik", 64.1466, -21.9426), ("Cape Town", -33.9628, 18.4098)]
def dms(v):
    v = abs(v); d = int(v); m = int((v - d) * 60); s = round(((v - d) * 60 - m) * 60, 2)
    return (float(d), float(m), float(s))
pi = 0
for ci, (city, lat, lon) in enumerate(cities):
    for i in range(40):
        pi += 1
        year, month, day = 2015 + (pi % 10), 1 + (pi % 12), 1 + (pi % 28)
        img = cover(f"{city} {i + 1}", f"{year}-{month:02}-{day:02}", size=(640, 480) if i % 3 else (480, 640), seed=f"p{pi}")
        ex = Image.Exif()
        ex[0x010F] = ["Canon", "Apple", "Google", "SONY"][pi % 4]; ex[0x0110] = ["EOS R6", "iPhone 13", "Pixel 7", "ILCE-7M4"][pi % 4]
        if i % 8 != 7:   # one in eight has no date
            ex[0x0132] = f"{year}:{month:02}:{day:02} 12:{i:02}:00"
            ifd = ex.get_ifd(0x8769); ifd[0x9003] = f"{year}:{month:02}:{day:02} 12:{i:02}:00"
        if i % 5 != 4:   # one in five has no place
            gps = ex.get_ifd(0x8825)
            gps[1] = "N" if lat >= 0 else "S"; gps[2] = dms(lat + i * 0.001); gps[3] = "E" if lon >= 0 else "W"; gps[4] = dms(lon + i * 0.001)
        name = f"IMG_{pi:04}.jpg" if i % 2 else f"{year}/{city} trip/DSC{pi:05}.jpg"
        path = ensure(f"{P}/{name}")
        open(path, "wb").write(jpeg_bytes(img, ex.tobytes()))
        note("photo", path, city=city, dated=i % 8 != 7, placed=i % 5 != 4)
for i in range(1, 11):
    cover(f"Screenshot {i}", size=(360, 640)).save(ensure(f"{P}/Screenshots/Screenshot_{i:03}.png")); note("photo", f"{P}/Screenshots/Screenshot_{i:03}.png", hard="screenshot with no camera details")

json.dump({"counts": count, "files": manifest}, open(os.path.join(os.path.dirname(OUT), "synthetic-manifest.json"), "w"), indent=1, ensure_ascii=False)
print(json.dumps(count, indent=1))
