"""Give one existing song a long title in a marked disposable player QA fixture."""
import argparse
import importlib.util
import json
import sqlite3
import uuid
from pathlib import Path

repo = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('home_fixture', repo / 'scripts/visual-qa/home-media-cards/fixture.py')
home = importlib.util.module_from_spec(spec)
spec.loader.exec_module(home)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--root', required=True)
args = parser.parse_args()
root = home.owned_root(args.root)
home.verify(root)
manifest_path = root / 'manifest.json'
manifest = json.loads(manifest_path.read_text())
if manifest['root'] != str(root) or manifest['task'] != home.TASK or not (root / '.player-media-ready').exists():
    raise ValueError('Requires a prepared, marked player fixture with its apps stopped')
item = next(item for item in manifest['items'] if item['key'] == 'player-song-4')
title = 'Coastal Morning: a long recording title that stays readable when the desktop player has limited vertical space'
with sqlite3.connect(root / 'data/library.db') as db:
    db.execute('PRAGMA foreign_keys=ON')
    for identity in (item['workId'], item['assetId']):
        changed = db.execute("UPDATE canonical_values SET value=? WHERE entity_id=? AND key='title'",
                             (title, uuid.UUID(identity).bytes)).rowcount
        if changed != 1:
            raise ValueError('Expected exactly one existing song title per identity')
    album = uuid.UUID(item['parentWorkId']).bytes
    row = db.execute("SELECT value FROM canonical_values WHERE entity_id=? AND key='child_entities_json'", (album,)).fetchone()
    tracks = json.loads(row[0])
    target = next(track for track in tracks['tracks'] if track['track_number'] == 4)
    target['title'] = title
    db.execute("UPDATE canonical_values SET value=? WHERE entity_id=? AND key='child_entities_json'", (json.dumps(tracks), album))
    if db.execute('PRAGMA foreign_key_check').fetchall():
        raise ValueError('Fixture foreign key violations')
item['title'] = title
manifest_path.write_text(json.dumps(manifest, indent=2), encoding='utf-8')
print('Prepared one long-title presentation case; media, identities and playback state are unchanged.')
