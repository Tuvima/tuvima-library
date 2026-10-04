"""Disposable Home QA data. Run prepare, initialize through Engine/setup, stop Engine, then seed.
Never changes credentials, grants, onboarding status, or application authentication.
"""
import argparse
import hashlib
import json
import re
import shutil
import sqlite3
import uuid
import wave
import zipfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
BASE = REPO / 'tools/reports/home-media-cards-visual'
MARKER = '.home-media-cards-disposable.json'
TASK = 'home-media-cards-2026-10-03'
NS = uuid.UUID('e6c728ee-5a91-4f7b-920b-8c4220fbfb50')
NOW = datetime(2026, 10, 3, 18, 0, tzinfo=timezone.utc)

def uid(key): return uuid.uuid5(NS, key)
def blob(value): return uuid.UUID(str(value)).bytes
def stamp(index=0): return (NOW - timedelta(minutes=index)).isoformat()
def dump(path, value): path.write_text(json.dumps(value, indent=2), encoding='utf-8')
def owned_root(raw):
    root = Path(raw).resolve()
    if root.parent != BASE.resolve() or not root.name.startswith('fixture-'):
        raise ValueError(f'Root must be a fixture-* child of {BASE}')
    return root
def verify(root):
    marker = root / MARKER
    if not marker.exists() or json.loads(marker.read_text(encoding='utf-8')) != {'task': TASK, 'root': str(root)}:
        raise ValueError('Missing or mismatched disposable marker; refusing fixture mutation')

def providers(root):
    verify(root)
    folder=root/'config/providers'; folder.mkdir(exist_ok=True)
    def scrub(value):
        if isinstance(value,list): return [scrub(item) for item in value]
        if not isinstance(value,dict): return value
        clean={}
        for key,item in value.items():
            if key.lower() in ['api_key','access_token','refresh_token','client_secret','password','secret','token']:
                clean[key]=''
            elif key=='enabled': clean[key]=False
            elif key=='endpoints' and isinstance(item,dict): clean[key]={name:'http://127.0.0.1:9' for name in item}
            else: clean[key]=scrub(item)
        return clean
    for source in (REPO/'config/providers').glob('*.json'):
        dump(folder/source.name,scrub(json.loads(source.read_text(encoding='utf-8'))))

def prepare(root):
    if root.exists() and any(root.iterdir()): raise ValueError('Prepare requires a new empty fixture directory')
    root.mkdir(parents=True, exist_ok=True)
    dump(root / MARKER, {'task': TASK, 'root': str(root)})
    config = root / 'config'; config.mkdir()
    for name in ['field_priorities.json', 'hydration.json', 'pipelines.json', 'pipeline-priority-defaults.json',
                 'media_types.json', 'scoring.json', 'disambiguation.json', 'maintenance.json',
                 'writeback.json', 'writeback-fields.json', 'transcoding.json']:
        source = REPO / 'config' / name
        if source.exists(): shutil.copyfile(source, config / name)
    shutil.copytree(REPO / 'config/ui', config / 'ui', ignore=shutil.ignore_patterns('profiles'))
    core = json.loads((REPO / 'config/core.json').read_text(encoding='utf-8'))
    core.update(database_path=str(root/'data/library.db'), data_root=str(root/'data'), library_root=str(root/'media'),
                server_name='Home Cards Disposable QA')
    core['auth']['localhost_bypass'] = False
    core['plugin_catalog']['enabled'] = False
    dump(config/'core.json', core)
    ai = json.loads((REPO/'config/ai.json').read_text(encoding='utf-8'))
    ai['dev_skip_download'] = True; ai['models_directory'] = str(root/'models')
    ai['features'] = {name: False for name in ai['features']}; ai['audio_pack_enabled'] = False
    dump(config/'ai.json', ai)
    libraries = []
    for kind, lane in [('Movies','watch'),('TV','watch'),('Books','read'),('Comics','read'),('Music','listen'),('Audiobooks','listen')]:
        libraries.append(dict(id=str(uid('library-'+kind)), name='QA '+kind, category=kind, kind='catalogued', area=lane,
            presentation='catalogue', metadata_policy='enriched', media_types=[kind], sources=[],
            visibility='household', authorized_profile_ids=[], accepted_intake_modes=[], duplicate_policy='skip_exact',
            organization_policy=dict(mode='keep_original_folders', preserve_originals=True)))
    dump(config/'libraries.json', dict(schema_version='6.0', storage_locations=[dict(id='qa',label='Disposable QA',path=str(root/'media'),allow_write=True)],
        view_storage=dict(storage_location_id='qa',relative_root='View'), libraries=libraries,
        personal_library_policy=dict(allow_mobile_backup=False, allow_browser_upload=False, allow_drag_and_drop=False,
            allow_connected_device_import=False, allow_managed_storage=True, allow_existing_folder_attachment=True,default_visibility='private')))
    providers(root)
    dump(root/'environment.json', {'TUVIMA_CONFIG_DIR':str(config), 'TUVIMA_DB_PATH':str(root/'data/library.db'),
        'TUVIMA_LIBRARY_ROOT':str(root/'media'), 'TUVIMA_LOG_DIR':str(root/'logs'),
        'TUVIMA_BACKUP_DIR':str(root/'backups'), 'TUVIMA_DATA_PROTECTION_DIR':str(root/'keys')})
    print(f'Prepared {root}. Initialize schema and complete normal setup through the Engine, then stop both apps and seed.')

def artwork(root, key, width, height, colour, *, originals_only=False, folder=None):
    from PIL import Image, ImageDraw
    folder=folder or root/'art'; folder.mkdir(parents=True,exist_ok=True)
    paths={}
    for suffix, bound in ([('original',None)] if originals_only else [('original',None),('s',320),('m',960),('l',2160)]):
        ratio=min(1,bound/max(width,height)) if bound else 1; size=(round(width*ratio),round(height*ratio))
        image=Image.new('RGB',size,colour); draw=ImageDraw.Draw(image)
        draw.rectangle((size[0]*.55,0,size[0],size[1]),fill=tuple(min(255,c+25) for c in colour))
        draw.line((0,size[1]*.8,size[0],size[1]*.2),fill=(130,98,190),width=max(2,round(size[0]*.02)))
        draw.text((size[0]*.07,size[1]*.1),key.replace('-',' ').upper(),fill=(245,240,255))
        path=folder/f'{key}-{suffix}.jpg'; image.save(path,quality=92); paths[suffix]=str(path)
    return paths

def refresh_art(root):
    """Refresh only known catalogue rendition files in a marked, seeded fixture."""
    from PIL import Image
    verify(root)
    manifest=json.loads((root/'manifest.json').read_text(encoding='utf-8'))
    if manifest['task'] != TASK or manifest['root'] != str(root):
        raise ValueError('Seeded fixture manifest mismatch')
    art=(root/'art').resolve()
    with sqlite3.connect(root/'data/library.db') as db:
        rows=db.execute('SELECT local_image_path,local_image_path_s,local_image_path_m,local_image_path_l FROM entity_assets').fetchall()
    for original,*renditions in rows:
        paths=[Path(value).resolve() for value in [original,*renditions]]
        if any(path.parent != art or not path.is_file() for path in paths):
            raise ValueError('Artwork refresh encountered a non-catalogue or missing file')
        for path,suffix in zip(paths,['original','s','m','l']):
            if not path.name.endswith('-'+suffix+'.jpg'):
                raise ValueError('Unexpected rendition filename')
    for original,*renditions in rows:
        with Image.open(original) as source:
            for destination,bound in zip(renditions,[320,960,2160]):
                ratio=min(1,bound/max(source.size))
                source.resize(tuple(round(side*ratio) for side in source.size)).save(destination,quality=92)
    print(f'Refreshed {len(rows)} marked catalogue rendition sets; View originals and database unchanged.')

def seed(root, profile):
    verify(root)
    database=root/'data/library.db'
    if not database.exists(): raise ValueError('Engine must initialize this fixture database first')
    if (root/'manifest.json').exists(): raise ValueError('Already seeded. Use a fresh fixture-* root; no reset operation is provided')
    with sqlite3.connect(database) as db:
        db.execute('PRAGMA foreign_keys=ON')
        epoch=db.execute("SELECT value FROM storage_metadata WHERE key='storage_epoch'").fetchone()
        current=re.search(r'CurrentEpoch = "([^"]+)"', (REPO/'src/MediaEngine.Storage/StorageEpochGuard.cs').read_text(encoding='utf-8')).group(1)
        if not epoch or epoch[0] != current: raise ValueError('Unsupported storage epoch; refusing seed')
        if not db.execute('SELECT 1 FROM profiles WHERE id=?',(blob(profile),)).fetchone(): raise ValueError('Requested profile does not exist; complete normal setup first')
        if db.execute('SELECT COUNT(*) FROM works').fetchone()[0]: raise ValueError('Expected empty disposable catalogue; refusing to merge unknown data')
        provider=uid('provider'); db.execute('INSERT INTO metadata_providers(id,name,version,is_enabled) VALUES (?,?,?,0)',(provider.bytes,'Disposable QA','1'))
        manifest={'task':TASK,'root':str(root),'profileId':str(profile),'storageEpoch':current,'items':[],'viewports':[[1536,1024],[1920,1080],[1024,768],[390,844],[320,568],[844,390],[768,1024]]}
        def values(entity, fields):
            for key,value in fields.items():
                if value is not None:
                    db.execute('INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES (?,?,?,?)',(entity.bytes,key,str(value),stamp()))
                    if key in ['author','artist','genre']:
                        for index,part in enumerate(str(value).split(';')):
                            db.execute('INSERT INTO canonical_value_arrays(entity_id,key,ordinal,value) VALUES (?,?,?,?)',(entity.bytes,key,index,part.strip()))
        def art(entity,key,shape,kind='CoverArt'):
            width,height={'portrait':(1000,1500),'square':(1200,1200),'landscape':(1920,1080)}[shape]
            paths=artwork(root,key,width,height,(20+(len(key)*3)%70,25+(len(key)*5)%60,62+(len(key)*7)%70))
            variant=uid('art-'+key+'-'+kind)
            db.execute('INSERT INTO entity_assets(id,entity_id,entity_type,asset_type,local_image_path,local_image_path_s,local_image_path_m,local_image_path_l,width_px,height_px,aspect_class,is_preferred) VALUES (?,?,?,?,?,?,?,?,?,?,?,1)',
                (variant.bytes,entity.bytes,'Work',kind,paths['original'],paths['s'],paths['m'],paths['l'],width,height,shape.title()))
            url=f'/stream/artwork/{variant}'
            prefix='background' if kind=='Background' else 'cover'
            fields={f'{prefix}_url':url, f'{prefix}_width':width, f'{prefix}_height':height,
                    f'{prefix}_width_px':width, f'{prefix}_height_px':height}
            for size in ['s','m','l']: fields[f'{prefix}_url_{size}']=url+f'?size={size}'
            if shape=='square': fields.update({key.replace('cover','square'):value for key,value in list(fields.items())})
            values(entity,fields)
            return paths
        def work(key,title,kind,percent=None,parent=None,episode=None,shape='landscape',still=True):
            workid=uid('work-'+key); asset=uid('asset-'+key); edition=uid('edition-'+key)
            library=uid('library-'+kind); media=root/'media'; media.mkdir(exist_ok=True)
            if kind in ['Movies','TV']:
                path=media/f'{key}.mp4'; shutil.copyfile(REPO/'tests/MediaEngine.Ingestion.Tests/Fixtures/metadata-readback.mp4',path)
            elif kind in ['Books','Comics']:
                path=media/f'{key}.epub'
                with zipfile.ZipFile(path,'w') as epub:
                    epub.writestr('mimetype','application/epub+zip'); epub.writestr('META-INF/container.xml','<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>')
                    epub.writestr('content.opf',f'<package version="3.0" unique-identifier="id" xmlns="http://www.idpf.org/2007/opf"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">{workid}</dc:identifier><dc:title>{title}</dc:title><dc:language>en</dc:language></metadata><manifest><item id="page" href="page.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="page"/></spine></package>')
                    epub.writestr('page.xhtml',f'<html xmlns="http://www.w3.org/1999/xhtml"><body><h1>{title}</h1><p>Disposable reading fixture.</p></body></html>')
            else:
                path=media/f'{key}.wav'
                with wave.open(str(path),'wb') as audio: audio.setnchannels(1); audio.setsampwidth(2); audio.setframerate(8000); audio.writeframes(b'\0\0'*8000)
            # A harmless trailing fixture marker keeps every owned file/hash distinct.
            path.write_bytes(path.read_bytes()+key.encode('ascii'))
            db.execute('INSERT INTO works(id,media_type,work_kind,parent_work_id,curator_state) VALUES (?,?,?,?,?)',(workid.bytes,kind,'child' if parent else 'standalone',parent.bytes if parent else None,'accepted'))
            db.execute('INSERT INTO editions(id,work_id) VALUES (?,?)',(edition.bytes,workid.bytes))
            db.execute('INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,presented_at,library_id) VALUES (?,?,?,?,?,?)',
                (asset.bytes,edition.bytes,hashlib.sha256(path.read_bytes()).hexdigest(),str(path),stamp(len(manifest['items'])),str(library)))
            db.execute('INSERT INTO metadata_claims(id,entity_id,provider_id,claim_key,claim_value,claimed_at) VALUES (?,?,?,?,?,?)',
                (uid('claim-'+key).bytes,asset.bytes,provider.bytes,'title',title,stamp(len(manifest['items']))))
            fields=dict(title=title,short_description=f'{title} follows an unexpected discovery in a quiet coastal town. Old loyalties are tested as the truth comes to light. Every choice brings the characters closer to a turning point.',original_release_date='2024-03-05',year='2024',runtime='45',duration_seconds='900',genre='Drama;Adventure')
            if episode:
                showtitle=db.execute("SELECT value FROM canonical_values WHERE entity_id=? AND key='title'",(parent.bytes,)).fetchone()[0]
                fields.update(season_number='2',episode_number=str(episode),episode_title=title,episode_description=fields['short_description'],show_name=showtitle)
            if kind=='Music': fields.update(album='Signals After Midnight',artist='The QA Ensemble',track_number='1')
            if kind in ['Books','Audiobooks']: fields.update(author='Jamie Rivers',page_count='300',narrator='Morgan Vale')
            values(workid,fields); values(asset,fields)
            art(workid,key,shape)
            if shape=='landscape' and still:
                art(workid,key+'-background','landscape','Background')
                if episode:
                    for row in db.execute("SELECT key,value FROM canonical_values WHERE entity_id=? AND key LIKE 'background_url%'",(workid.bytes,)).fetchall():
                        values(workid,{row[0].replace('background','episode_still'):row[1]})
            if percent is not None:
                db.execute('INSERT INTO user_states(user_id,asset_id,progress_pct,last_accessed,extended_properties) VALUES (?,?,?,?,?)',
                    (blob(profile),asset.bytes,percent,stamp(len(manifest['items'])),json.dumps(dict(position_seconds=str(percent*9),duration_seconds='900'))))
            route=f'/details/tvshow/{parent}?episode={workid}&context=watch' if episode else f'/details/work/{workid}'
            manifest['items'].append(dict(key=key,title=title,workId=str(workid),assetId=str(asset),parentWorkId=str(parent) if parent else None,route=route,percent=percent,shape=shape))
            return workid
        for label in ['untouched-show','partial-show','next-show','missing-still-show']:
            parent=uid('show-'+label)
            db.execute("INSERT INTO works(id,media_type,work_kind,curator_state) VALUES (?,'TV','parent','accepted')",(parent.bytes,))
            values(parent,dict(title='The Northern Signal '+label.replace('-show','').title(),short_description='A community follows a mysterious signal across the northern coast.',year='2023',original_release_date='2023-04-02'))
            art(parent,label+'-poster','portrait'); art(parent,label+'-show','landscape','Background')
            work(label+'-episode', 'A Message Across the Water', 'TV', {'partial-show':42,'next-show':100,'missing-still-show':35}.get(label),parent,5,still=label!='missing-still-show')
            if label=='next-show': work('next-owned-episode','The Next Owned Chapter','TV',parent=parent,episode=7)
            # Provider-only row has no edition/asset and must never be counted or selected.
            providerwork=uid('provider-only-'+label)
            db.execute("INSERT INTO works(id,media_type,work_kind,parent_work_id,is_catalog_only,ownership,curator_state) VALUES (?,'TV','catalog',?,1,'Unowned','accepted')",(providerwork.bytes,parent.bytes))
            values(providerwork,dict(title='Provider-only excluded episode',season_number='2',episode_number='6'))
        work('movie-unstarted','The Quiet Crossing','Movies')
        work('movie-partial','Beyond the Last Lighthouse','Movies',47)
        work('movie-completed','A Journey Completed','Movies',100)
        work('book-partial','A Very Long Book Title About the Extraordinary Journey Across the Northern Coast and the People Who Found Their Way Home','Books',37,shape='portrait')
        album=uid('album'); db.execute("INSERT INTO works(id,media_type,work_kind,curator_state) VALUES (?,'Music','parent','accepted')",(album.bytes,))
        # Complete, identity-free local rows satisfy the existing manifest cache gate.
        # No Apple/MusicBrainz identity or public catalogue lookup is needed.
        local_tracks=json.dumps(dict(tracks=[dict(title='Opening Signals',ordinal=1,track_number=1,disc_number=1,duration_seconds=1)]))
        values(album,dict(title='Signals After Midnight',album='Signals After Midnight',artist='The QA Ensemble',year='2024',child_entities_json=local_tracks,track_count=1)); art(album,'album','square')
        work('album-active','Opening Signals','Music',28,parent=album,shape='square')
        work('audiobook-partial','Voices of the Coast','Audiobooks',23,shape='portrait')
        second=uid('private-profile'); db.execute("INSERT INTO profiles(id,display_name,role,created_at) VALUES (?,?,'RestrictedProfile',?)",(second.bytes,'Private QA Profile',stamp()))
        for owner,label in [(profile,'mine'),(second,'inaccessible')]:
            existing=db.execute('SELECT id,library_id FROM view_personal_spaces WHERE owner_profile_id=?',(blob(owner),)).fetchone()
            if existing: space=uuid.UUID(bytes=existing[0]); library=uuid.UUID(bytes=existing[1])
            else:
                space=uid('space-'+label); library=uid('view-library-'+label)
                db.execute('INSERT INTO view_personal_spaces(id,owner_profile_id,library_id,created_at,updated_at) VALUES (?,?,?,?,?)',(space.bytes,blob(owner),library.bytes,stamp(),stamp()))
            db.execute('INSERT OR IGNORE INTO view_storage_labels(personal_space_id,label) VALUES (?,?)',(space.bytes,'qa-'+str(owner)))
            photos=root/'view-fixtures'/str(owner)/'Photos'
            photos.mkdir(parents=True,exist_ok=True)
            source=uid('view-source-'+label)
            db.execute("INSERT INTO view_sources(id,scope_kind,personal_space_id,library_id,source_type,name,storage_mode,external_path,created_at,updated_at) VALUES (?,'personal',?,?,'folder',?,'linked',?,?,?)",(source.bytes,space.bytes,library.bytes,'QA Photos',str(photos),stamp(),stamp()))
            for index in range(2 if label=='mine' else 1):
                key=f'view-{label}-{index}'; item=uid(key); fileid=uid('file-'+key)
                paths=artwork(root,key,1600,1000,(45,60,100),originals_only=True,folder=photos); path=Path(paths['original'])
                db.execute("INSERT INTO local_items(id,scope_kind,personal_space_id,owner_profile_id,library_id,media_kind,title,primary_file_name,primary_mime_type,created_at,updated_at) VALUES (?,'personal',?,?,?,'image',?,?,'image/jpeg',?,?)",(item.bytes,space.bytes,blob(owner),library.bytes,'QA '+key,path.name,stamp(index),stamp(index)))
                db.execute('INSERT INTO local_item_metadata(item_id,width,height,updated_at) VALUES (?,1600,1000,?)',(item.bytes,stamp()))
                db.execute("INSERT INTO local_files(id,content_hash,byte_size,mime_type,extension,created_at) VALUES (?,?,?,'image/jpeg','.jpg',?)",(fileid.bytes,hashlib.sha256(path.read_bytes()).hexdigest(),path.stat().st_size,stamp()))
                db.execute('INSERT INTO local_file_sources(id,file_id,library_id,source_id,file_path,modified_at,indexed_at) VALUES (?,?,?,?,?,?,?)',(uid('filesource-'+key).bytes,fileid.bytes,library.bytes,source.bytes,str(path),stamp(),stamp()))
                db.execute("INSERT INTO local_item_files(item_id,file_id,role,position,added_at) VALUES (?,?,'primary',0,?)",(item.bytes,fileid.bytes,stamp(index)))
                manifest['items'].append(dict(key=key,viewAssetId=str(item),profileId=str(owner),sourceId=str(source),originalPath=str(path),fileRole='primary',route='/view',expectedVisible=label=='mine',shape='landscape'))
        manifest['privateProfileId']=str(second)
        dump(root/'manifest.json',manifest)
    print(f'Seeded {len(manifest["items"])} fixture entries. IDs/routes: {root / "manifest.json"}')

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command',choices=['prepare','seed','verify','providers','refresh-art'])
    parser.add_argument('--root',required=True)
    parser.add_argument('--profile-id',default='00000000-0000-0000-0000-000000000001')
    args=parser.parse_args(); root=owned_root(args.root)
    if args.command=='prepare': prepare(root)
    elif args.command=='seed': seed(root,uuid.UUID(args.profile_id))
    elif args.command=='providers': providers(root); print('Disabled credential-free provider manifests installed in marked fixture.')
    elif args.command=='refresh-art': refresh_art(root)
    else: verify(root); print('Disposable marker verified; no data changed.')
