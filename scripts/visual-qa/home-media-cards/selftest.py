"""Execute fixture seeding against current schema in a fresh task-owned probe (never starts apps)."""
import importlib.util
import json
import sqlite3
import uuid
from PIL import Image
from pathlib import Path

spec=importlib.util.spec_from_file_location('fixture',Path(__file__).with_name('fixture.py'))
fixture=importlib.util.module_from_spec(spec); spec.loader.exec_module(fixture)
root=fixture.BASE/f'fixture-selftest-{uuid.uuid4().hex[:8]}'
fixture.prepare(root)
(root/'data').mkdir()
with sqlite3.connect(root/'data/library.db') as db:
    db.executescript((fixture.REPO/'src/MediaEngine.Storage/Schema/schema.sql').read_text(encoding='utf-8'))
    epoch=fixture.re.search(r'CurrentEpoch = "([^"]+)"',(fixture.REPO/'src/MediaEngine.Storage/StorageEpochGuard.cs').read_text(encoding='utf-8')).group(1)
    db.execute("INSERT OR REPLACE INTO storage_metadata(key,value) VALUES ('storage_epoch',?)",(epoch,))
    db.execute("INSERT INTO profiles(id,display_name,role,created_at) VALUES (?,?,'Administrator',?)",(fixture.blob('00000000-0000-0000-0000-000000000001'),'Fixture script probe',fixture.stamp()))
fixture.seed(root,uuid.UUID('00000000-0000-0000-0000-000000000001'))
with sqlite3.connect(root/'data/library.db') as db:
    assert db.execute('PRAGMA foreign_key_check').fetchall()==[]
    assert db.execute('SELECT count(*) FROM user_states').fetchone()[0]==12
    assert db.execute('SELECT count(*) FROM text_tracks').fetchone()[0]==2
    assert db.execute('SELECT count(*) FROM playback_inspection_cache').fetchone()[0]>0
    assert db.execute('SELECT count(*) FROM view_gallery_items').fetchone()[0]==26
    assert db.execute('SELECT count(*) FROM local_items').fetchone()[0]==28
    assert db.execute('SELECT count(*) FROM local_items WHERE length(id)<>16').fetchone()[0]==0
    assert db.execute('SELECT count(*) FROM media_assets WHERE length(content_hash)<>64').fetchone()[0]==0
    manifest=json.loads((root/'manifest.json').read_text(encoding='utf-8'))
    for source_id,owner,external in db.execute('SELECT s.id,p.owner_profile_id,s.external_path FROM view_sources s JOIN view_personal_spaces p ON p.id=s.personal_space_id'):
        owner_id=str(uuid.UUID(bytes=owner))
        photos=(root/'view-fixtures'/owner_id/'Photos').resolve()
        assert Path(external).resolve()==photos
        expected=27 if owner_id==manifest['profileId'] else 1
        files=list(photos.iterdir())
        assert len(files)==expected and all(p.suffix in ['.jpg','.webm'] for p in files)
        assert all(p.parent==photos and p.is_file() for p in files)
        rows=db.execute('SELECT i.id,i.owner_profile_id,f.file_path,lf.role FROM local_file_sources f JOIN local_item_files lf ON lf.file_id=f.file_id JOIN local_items i ON i.id=lf.item_id WHERE f.source_id=?',(source_id,)).fetchall()
        assert len(rows)==expected
        for item_id,item_owner,path,role in rows:
            assert item_owner==owner and Path(path).resolve().parent==photos and role=='primary'
            entry=next(item for item in manifest['items'] if item.get('viewAssetId')==str(uuid.UUID(bytes=item_id)))
            assert entry['profileId']==owner_id and entry['originalPath']==path and entry['fileRole']==role
            assert entry['sourceId']==str(uuid.UUID(bytes=source_id))
            assert entry['expectedVisible']==(owner_id==manifest['profileId'])
    for (raw,) in db.execute('SELECT extended_properties FROM user_states'):
        saved=json.loads(raw)
        assert isinstance(saved['position_seconds'],str) and isinstance(saved['duration_seconds'],str)
    fields=dict(db.execute('SELECT key,value FROM canonical_values WHERE entity_id=?',(fixture.uid('album').bytes,)))
    tracks=json.loads(fields['child_entities_json'])['tracks']
    # Mirrors NeedsAlbumTrackGapFill's local-row completeness contract; with
    # the existing cover and no provider identity, Ensure returns the cache.
    assert fields['cover_url'] and fields['track_count']=='2' and len(tracks)==2
    assert all(t['title'] and isinstance(t['ordinal'],int) and isinstance(t['track_number'],int) and t['duration_seconds']>0 for t in tracks)
    stream_cover = db.execute("SELECT value FROM canonical_values WHERE entity_id=? AND key='cover_url'", (fixture.uid('work-album-active').bytes,)).fetchone()[0]
    assert stream_cover == f"/stream/{fixture.uid('asset-album-active')}/cover"
    assert not db.execute("SELECT 1 FROM canonical_values WHERE entity_id=? AND key='cover_url'", (fixture.uid('work-album-inherited').bytes,)).fetchone()
    assert db.execute('SELECT count(*) FROM media_assets a JOIN editions e ON e.id=a.edition_id WHERE e.work_id=?', (fixture.uid('work-audiobook-partial').bytes,)).fetchone()[0] == 3
    assert not any('musicbrainz' in key.lower() or 'apple' in key.lower() for key in fields)
    assert not any('apple' in key.lower() or 'musicbrainz' in key.lower() for t in tracks for key in t)
    for original,small,medium,large,width,height in db.execute('SELECT local_image_path,local_image_path_s,local_image_path_m,local_image_path_l,width_px,height_px FROM entity_assets'):
        for path,bound in [(original,None),(small,320),(medium,960),(large,2160)]:
            ratio=min(1,bound/max(width,height)) if bound else 1
            with Image.open(path) as image:
                assert image.size==tuple(round(side*ratio) for side in [width,height])
    view_hashes={p:fixture.hashlib.sha256(p.read_bytes()).hexdigest() for p in (root/'view-fixtures').rglob('*.jpg')}
fixture.refresh_art(root)
assert all(fixture.hashlib.sha256(p.read_bytes()).hexdigest()==digest for p,digest in view_hashes.items())
try: fixture.owned_root(fixture.REPO/'config')
except ValueError: pass
else: raise AssertionError('Real config path accepted')
try: fixture.seed(root,uuid.UUID('00000000-0000-0000-0000-000000000001'))
except ValueError: pass
else: raise AssertionError('Reseeding accepted')
print(f'Self-test passed. Probe retained for task-owned cleanup: {root}')
