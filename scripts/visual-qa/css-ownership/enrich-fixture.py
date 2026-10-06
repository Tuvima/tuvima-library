"""Add CSS ownership scenarios to an existing marked disposable QA fixture.
Run with both fixture hosts stopped. Never opens configured library state.
"""
import argparse
import hashlib
import importlib.util
import json
import sqlite3
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('home_fixture', REPO / 'scripts/visual-qa/home-media-cards/fixture.py')
home = importlib.util.module_from_spec(spec)
spec.loader.exec_module(home)


def source_route(root, result):
    with sqlite3.connect(root / 'data/library.db') as db:
        row = db.execute("SELECT id FROM view_sources WHERE name='QA Photos' ORDER BY id LIMIT 1").fetchone()
    if row:
        result['routes']['view-folder-source'] = '/view/folders?source=' + str(uuid.UUID(bytes=row[0]))
        (root / '.css-ownership-ready.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    return result


def complete_coverage(root, result):
    if result.get('schemaVersion', 1) >= 2:
        return source_route(root, result)
    uid = lambda key: uuid.uuid5(home.NS, 'css-ownership-' + key)
    with sqlite3.connect(root / 'data/library.db') as db:
        db.execute('PRAGMA foreign_keys=ON')
        # Actual History consumes durable system activity, not entity_events.
        for key in ('book', 'series-parent'):
            entity = uid(key).bytes
            db.execute('INSERT INTO system_activity(occurred_at,action_type,entity_id,entity_type,detail) VALUES (?,?,?,?,?)',
                       ('2026-10-04T12:00:00Z', 'MetadataManualOverride', entity, 'Work',
                        'Disposable QA metadata verified for the History presentation.'))
        for key in ('collection', 'playlist'):
            db.execute("UPDATE collections SET resolution='materialized' WHERE id=? AND membership_mode='Manual'", (uid(key).bytes,))
        # A source manifest, rather than a catalogue Work without a file, supplies
        # the missing position. These synthetic QIDs are local fixture identities.
        series = uid('series').bytes
        qid = 'Q990000001'
        db.execute('UPDATE collections SET wikidata_qid=? WHERE id=?', (qid, series))
        db.execute('INSERT INTO series_manifest_hydrations(series_qid,collection_id,series_label,manifest_source,last_hydrated_at) VALUES (?,?,?,?,?)',
                   (qid, series, 'The Coast Sequence', 'Disposable QA', home.stamp()))
        for position, key in [(1,'book'), (2,'book-second'), (3,'book-missing'), (4,'book-fourth')]:
            work = uid(key).bytes if position != 3 else None
            title = ['The First Coast','The Second Coast','The Missing Coast','The Fourth Coast'][position-1]
            db.execute('INSERT INTO series_manifest_items(id,collection_id,series_qid,item_qid,item_label,media_type,raw_ordinal,parsed_ordinal,sort_order,order_source,ownership_state,linked_work_id,last_hydrated_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)',
                       (uid('manifest-'+key).bytes, series, qid, 'Q99000001'+str(position), title, 'Books', str(position), position, position,
                        'P179', 'Owned' if work else 'Missing', work, home.stamp()))
        missing = uid('book-missing').bytes
        row = db.execute('SELECT work_kind,is_catalog_only FROM works WHERE id=?', (missing,)).fetchone()
        if row != ('catalog', 1):
            raise ValueError('Missing fixture Work is not the expected synthetic catalogue row')
        db.execute('DELETE FROM canonical_values WHERE entity_id=?', (missing,))
        db.execute('DELETE FROM works WHERE id=?', (missing,))
        # The existing editor action applies to Smart Galleries. Clone the fixture
        # owner's Gallery instead of changing its manual membership or permissions.
        db.row_factory = sqlite3.Row
        original = db.execute('SELECT * FROM view_galleries LIMIT 1').fetchone()
        if original:
            gallery = dict(original)
            gallery.update(id=uid('smart-gallery').bytes, name='Fixture coast rules', gallery_kind='smart',
                smart_rule_json=json.dumps({'version':1,'groups':[{'id':str(uid('gallery-rule')),'match_mode':'all',
                    'conditions':[{'field':'media_type','op':'eq','value':'image'}]}]}))
            db.execute('INSERT INTO view_galleries('+','.join(gallery)+') VALUES ('+','.join('?' for _ in gallery)+')', tuple(gallery.values()))
            result['routes']['gallery'] = '/view/galleries/'+str(uid('smart-gallery'))
    result['schemaVersion'] = 2
    (root / '.css-ownership-ready.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    return source_route(root, result)


def enrich(raw_root):
    root = home.owned_root(raw_root)
    home.verify(root)
    manifest = json.loads((root / 'manifest.json').read_text())
    if manifest['root'] != str(root) or manifest['task'] != home.TASK:
        raise ValueError('Manifest does not belong to this disposable root')
    marker = root / '.css-ownership-ready.json'
    if marker.exists():
        return complete_coverage(root, json.loads(marker.read_text()))
    routes = {}
    uid = lambda key: uuid.uuid5(home.NS, 'css-ownership-' + key)
    with sqlite3.connect(root / 'data/library.db') as db:
        db.row_factory = sqlite3.Row
        db.execute('PRAGMA foreign_keys=ON')
        def values(entity, fields):
            for key, value in fields.items():
                db.execute('INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES (?,?,?,?) '
                           'ON CONFLICT(entity_id,key) DO UPDATE SET value=excluded.value',
                           (entity, key, str(value), home.stamp()))
        def clone_row(table, row, updates):
            copied = dict(row)
            copied.update(updates)
            columns = ','.join(copied)
            db.execute(f'INSERT INTO {table}({columns}) VALUES ({",".join("?" for _ in copied)})', tuple(copied.values()))
        def copy_canonical(source, target):
            db.execute('INSERT INTO canonical_values(entity_id,key,value,last_scored_at) '
                       'SELECT ?,key,value,last_scored_at FROM canonical_values WHERE entity_id=?', (target, source))
            db.execute('INSERT INTO canonical_value_arrays(entity_id,key,ordinal,value) '
                       'SELECT ?,key,ordinal,value FROM canonical_value_arrays WHERE entity_id=?', (target, source))
        def clone_work(source_key, key, title, kind, parent=None, collection=None, ordinal=None, backdrop=True):
            source_item = next(item for item in manifest['items'] if item['key'] == source_key)
            source_work = uuid.UUID(source_item['workId']).bytes
            source_asset = uuid.UUID(source_item['assetId']).bytes
            work, edition, asset = uid(key).bytes, uid(key+'-edition').bytes, uid(key+'-asset').bytes
            clone_row('works', db.execute('SELECT * FROM works WHERE id=?', (source_work,)).fetchone(),
                      {'id': work, 'media_type': kind, 'work_kind': 'child' if parent else 'standalone',
                       'parent_work_id': parent, 'collection_id': collection, 'ordinal': ordinal, 'ordinal_sort': ordinal})
            db.execute('INSERT INTO editions(id,work_id) VALUES (?,?)', (edition, work))
            source = db.execute('SELECT * FROM media_assets WHERE id=?', (source_asset,)).fetchone()
            source_path = Path(source['file_path_root']).resolve()
            if not source_path.is_relative_to(root / 'media'):
                raise ValueError('Media copy would read outside the disposable fixture')
            target_path = root / 'media' / (key + source_path.suffix)
            target_path.write_bytes(source_path.read_bytes() + key.encode('ascii'))
            clone_row('media_assets', source, {'id': asset, 'edition_id': edition,
                      'content_hash': hashlib.sha256(target_path.read_bytes()).hexdigest(), 'file_path_root': str(target_path)})
            for old, new in ((source_work, work), (source_asset, asset)):
                copy_canonical(old, new)
                values(new, {'title': title, 'description': title + ' is a disposable layout verification work.',
                             'short_description': title + ' is a disposable layout verification work.'})
            for image in db.execute('SELECT * FROM entity_assets WHERE entity_id=?', (source_work,)).fetchall():
                if image['asset_type'] == 'Background' and not backdrop:
                    continue
                image_id = uid(key+'-'+str(uuid.UUID(bytes=image['id']))).bytes
                clone_row('entity_assets', image, {'id': image_id, 'entity_id': work})
                prefix = 'background' if image['asset_type'] == 'Background' else 'cover'
                values(work, {prefix+'_url': '/stream/artwork/'+str(uuid.UUID(bytes=image_id)),
                              **{prefix+'_url_'+size: '/stream/artwork/'+str(uuid.UUID(bytes=image_id))+'?size='+size for size in ('s','m','l')}})
            if not backdrop:
                for entity in (work, asset):
                    db.execute("DELETE FROM canonical_values WHERE entity_id=? AND key LIKE 'background_%'", (entity,))
            routes[key] = '/details/work/'+str(uuid.UUID(bytes=work))
            return work, asset

        series = uid('series').bytes
        parent = uid('series-parent').bytes
        db.execute("INSERT INTO collections(id,display_name,collection_type,primary_area,resolution,rule_hash,created_at) "
                   "VALUES (?,'The Coast Sequence','Series','Read','materialized','local:books:css-ownership-coast',?)", (series, home.stamp()))
        db.execute("INSERT INTO works(id,collection_id,media_type,work_kind,curator_state) VALUES (?,?,'Books','parent','accepted')", (parent, series))
        values(parent, {'title': 'The Coast Sequence', 'series': 'The Coast Sequence', 'sequence_total': 4, 'sequence_total_scope': 'MainSequence'})
        book, book_asset = clone_work('book-partial', 'book', 'The First Coast', 'Books', parent, series, 1)
        second, _ = clone_work('book-partial', 'book-second', 'The Second Coast', 'Books', parent, series, 2)
        fourth, _ = clone_work('book-partial', 'book-fourth', 'The Fourth Coast', 'Books', parent, series, 4)
        missing = uid('book-missing').bytes
        db.execute("INSERT INTO works(id,collection_id,media_type,work_kind,parent_work_id,ordinal,ordinal_sort,is_catalog_only,ownership,curator_state) "
                   "VALUES (?,?,'Books','catalog',?,3,3,1,'Unowned','accepted')", (missing, series, parent))
        values(missing, {'title': 'The Missing Coast', 'series': 'The Coast Sequence', 'series_position': 3})
        for work in (book, second, fourth):
            position = db.execute('SELECT ordinal FROM works WHERE id=?', (work,)).fetchone()[0]
            values(work, {'series': 'The Coast Sequence', 'series_position': position, 'sequence_total': 4, 'sequence_total_scope': 'MainSequence'})
        routes['series'] = '/details/collection/'+str(uuid.UUID(bytes=series))
        clone_work('book-partial', 'comic', 'Coastal Sketches 1', 'Comics')
        clone_work('movie-unstarted', 'movie-fallback', 'A Poster at the Coast', 'Movies', backdrop=False)
        for name, role in (('Jamie Rivers', 'Author'), ('The QA Ensemble', 'Artist')):
            person = uid('person-'+name).bytes
            db.execute('INSERT INTO persons(id,name,biography,date_of_birth,created_at) VALUES (?,?,?,?,?)',
                       (person, name, 'A fictional person for layout verification.', '1980-03-05', home.stamp()))
            db.execute('INSERT INTO person_roles(person_id,role) VALUES (?,?)', (person, role))
            art = db.execute("SELECT * FROM entity_assets WHERE entity_id=? AND asset_type='CoverArt' LIMIT 1", (book,)).fetchone()
            image_id = uid('portrait-'+name).bytes
            clone_row('entity_assets', art, {'id': image_id, 'entity_id': person, 'entity_type': 'Person', 'asset_type': 'Headshot'})
            db.execute('UPDATE persons SET local_headshot_path=? WHERE id=?', (art['local_image_path'], person))
            routes['person' if name == 'Jamie Rivers' else 'artist'] = '/details/person/'+str(uuid.UUID(bytes=person))
        movie_item = next(item for item in manifest['items'] if item['key'] == 'movie-partial')
        for entity in (uuid.UUID(movie_item['workId']).bytes, uuid.UUID(movie_item['assetId']).bytes):
            db.execute("INSERT INTO canonical_value_arrays(entity_id,key,ordinal,value) VALUES (?,'director',0,'Jamie Rivers')", (entity,))
        collection = uid('collection').bytes
        db.execute("INSERT INTO collections(id,display_name,collection_type,membership_mode,description,created_at) "
                   "VALUES (?,'Coast Favorites','Custom','Manual','A disposable mixed-media collection.',?)", (collection, home.stamp()))
        members = [book, uuid.UUID(movie_item['workId']).bytes,
                   uuid.UUID(next(item['workId'] for item in manifest['items'] if item['key'] == 'audiobook-partial')).bytes]
        for index, member in enumerate(members):
            db.execute('INSERT INTO collection_items(id,collection_id,work_id,sort_order,added_at) VALUES (?,?,?,?,?)',
                       (uid('collection-item-'+str(index)).bytes, collection, member, index, home.stamp()))
        routes['collection'] = '/details/collection/'+str(uuid.UUID(bytes=collection))
        playlist = uid('playlist').bytes
        db.execute("INSERT INTO collections(id,display_name,collection_type,membership_mode,primary_area,created_at) "
                   "VALUES (?,'Coast Queue','Playlist','Manual','Listen',?)", (playlist, home.stamp()))
        tracks = db.execute("SELECT id FROM works WHERE media_type='Music' AND work_kind='child'").fetchall()
        for index, track in enumerate(tracks):
            db.execute('INSERT INTO collection_items(id,collection_id,work_id,sort_order,added_at) VALUES (?,?,?,?,?)',
                       (uid('playlist-item-'+str(index)).bytes, playlist, track['id'], index, home.stamp()))
        routes['playlist'] = '/listen/music/playlists/'+str(uuid.UUID(bytes=playlist))
        for key, item in [('book', {'workId':str(uuid.UUID(bytes=book)), 'assetId':str(uuid.UUID(bytes=book_asset))}), ('movie',movie_item)]:
            for entity in (uuid.UUID(item['workId']).bytes, uuid.UUID(item['assetId']).bytes):
                db.execute('INSERT INTO entity_events(id,entity_id,entity_type,event_type,trigger,provider_name,occurred_at,detail) VALUES (?,?,?,?,?,?,?,?)',
                           (uid('history-'+key+'-'+entity.hex()).bytes, entity, 'Work' if entity == uuid.UUID(item['workId']).bytes else 'MediaAsset',
                            'MetadataUpdated', 'Manual', 'Disposable QA', home.stamp(), 'Fixture metadata recorded for History layout.'))
        gallery = db.execute('SELECT id FROM view_galleries LIMIT 1').fetchone()
        if gallery:
            routes['gallery'] = '/view/galleries/'+str(uuid.UUID(bytes=gallery['id']))
    # The declarative Apple adapter uses this loopback-only provider stub.
    provider_path = root / 'config/providers/apple_api.json'
    provider = json.loads(provider_path.read_text())
    provider['enabled'] = True
    provider['endpoints']['api'] = 'http://127.0.0.1:61499'
    provider['rate_limit'] = {'requests_per_minute': 600, 'burst': 100, 'max_concurrency': 4}
    provider_path.write_text(json.dumps(provider, indent=2), encoding='utf-8')
    result = {'schemaVersion': 1, 'routes': routes, 'providerOrigin': 'http://127.0.0.1:61499'}
    marker.write_text(json.dumps(result, indent=2), encoding='utf-8')
    return complete_coverage(root, result)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    args = parser.parse_args()
    print(json.dumps(enrich(args.root)))
