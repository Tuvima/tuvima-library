"""Add local alternate lyrics to a marked player QA fixture while its apps are stopped."""
import argparse
import importlib.util
import json
import sqlite3
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('home_fixture', REPO / 'scripts/visual-qa/home-media-cards/fixture.py')
home = importlib.util.module_from_spec(spec)
spec.loader.exec_module(home)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--root', required=True)
args = parser.parse_args()
root = home.owned_root(args.root)
home.verify(root)
manifest = json.loads((root / 'manifest.json').read_text())
if manifest['root'] != str(root) or manifest['task'] != home.TASK or not (root / '.player-media-ready').exists():
    raise ValueError('Requires an already prepared, marked player fixture')
item = next(item for item in manifest['items'] if item['key'] == 'album-active')
variants = [
    ('alternate-timed', 'lrc', 'Line', ''.join(
        f'[{seconds // 60:02d}:{seconds % 60:02d}.00]Alternate coastal signal {index + 1:02d}: listening through the night\n'
        for index, seconds in enumerate(range(0, 180, 10)))),
    ('static', 'txt', 'Static', 'The northern signal stays with us.\nThe coast answers quietly.\nWe listen until morning.\n'),
]
with sqlite3.connect(root / 'data/library.db') as db:
    db.execute('PRAGMA foreign_keys=ON')
    for key, fmt, timing, content in variants:
        path = root / 'media' / f'player-lyrics-{key}.{fmt}'
        path.write_text(content, encoding='utf-8')
        db.execute('INSERT INTO text_tracks(id,asset_id,kind,language,provider,confidence,source_format,normalized_format,local_path,timing_mode,is_preferred,is_user_owned) '
                   'VALUES (?, ?, \'Lyrics\', \'en\', \'Local QA\', 1, ?, ?, ?, ?, 0, 1) '
                   'ON CONFLICT(id) DO UPDATE SET local_path=excluded.local_path,timing_mode=excluded.timing_mode',
                   (home.uid('player-lyrics-' + key).bytes, uuid.UUID(item['assetId']).bytes, fmt, fmt, str(path), timing))
    if db.execute('PRAGMA foreign_key_check').fetchall():
        raise ValueError('Fixture foreign key violations')
print('Added two non-preferred local lyric variants to the disposable player fixture.')
