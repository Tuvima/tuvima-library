"""Add long TV rails to an existing marked disposable fixture with both hosts stopped."""
import argparse
import hashlib
import importlib.util
import sqlite3
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('home_fixture', REPO / 'scripts/visual-qa/home-media-cards/fixture.py')
home = importlib.util.module_from_spec(spec)
spec.loader.exec_module(home)


def enrich(raw):
    root = home.owned_root(raw)
    home.verify(root)
    with sqlite3.connect(root / 'data/library.db') as db:
        db.execute('PRAGMA foreign_keys=ON')
        db.execute("""
            INSERT OR IGNORE INTO canonical_values(entity_id,key,value,last_scored_at)
            SELECT w.id,'show_name',cv.value,cv.last_scored_at FROM works w
            JOIN canonical_values cv ON cv.entity_id=w.id AND cv.key='title'
            WHERE w.media_type='TV' AND w.work_kind='parent'
            """)
        parent = home.uid('show-partial-show').bytes
        template = home.uid('work-partial-show-episode').bytes
        template_asset = home.uid('asset-partial-show-episode').bytes
        source = db.execute('SELECT file_path_root,library_id FROM media_assets WHERE id=?', (template_asset,)).fetchone()
        if not source:
            raise ValueError('Seeded partial-show episode is required')
        for season, episode in [(1, 1)] + [(2, number) for number in range(6, 17)]:
            key = f'ui-bugfix-s{season}-e{episode}'
            work = home.uid(key).bytes
            if db.execute('SELECT 1 FROM works WHERE id=?', (work,)).fetchone():
                continue
            edition, asset = home.uid('edition-' + key).bytes, home.uid('asset-' + key).bytes
            path = root / 'media' / (key + '.webm')
            path.write_bytes(Path(source[0]).read_bytes() + key.encode('ascii'))
            db.execute("INSERT INTO works(id,media_type,work_kind,parent_work_id,curator_state) VALUES (?,'TV','child',?,'accepted')", (work, parent))
            db.execute('INSERT INTO editions(id,work_id) VALUES (?,?)', (edition, work))
            db.execute('INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,presented_at,library_id) VALUES (?,?,?,?,?,?)',
                       (asset, edition, hashlib.sha256(path.read_bytes()).hexdigest(), str(path), home.stamp(30), source[1]))
            for target, original in [(work, template), (asset, template_asset)]:
                db.execute('INSERT INTO canonical_values(entity_id,key,value,last_scored_at) SELECT ?,key,value,last_scored_at FROM canonical_values WHERE entity_id=?', (target, original))
                for field, value in [('season_number', str(season)), ('episode_number', str(episode)), ('title', f'Fixture episode {episode}'), ('episode_title', f'Fixture episode {episode}')]:
                    db.execute('UPDATE canonical_values SET value=? WHERE entity_id=? AND key=?', (value, target, field))
    print('Disposable QA show roots and long season rails enriched.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    enrich(parser.parse_args().root)
