"""Extend a marked, disposable Home fixture with real playable player QA media.
Run only while the fixture Engine/Dashboard are stopped. Never reads user media.
"""
import argparse
import hashlib
import importlib.util
import json
import shutil
import sqlite3
import subprocess
import uuid
import wave
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
if manifest['root'] != str(root) or manifest['task'] != home.TASK:
    raise ValueError('Manifest does not belong to the marked fixture')
if (root / '.player-media-ready').exists():
    raise ValueError('Already extended; prepare a new disposable fixture')
ffmpeg = REPO / 'tools/ffmpeg/ffmpeg.exe'
media = root / 'media'
metadata = media / 'player-chapters.txt'
metadata.write_text(';FFMETADATA1\n' + ''.join(
    f'[CHAPTER]\nTIMEBASE=1/1000\nSTART={start * 1000}\nEND={(start + 60) * 1000}\ntitle=Chapter {index + 1}\n'
    for index, start in enumerate([0, 60, 120])), encoding='utf-8')
silent = media / 'player-source.wav'
with wave.open(str(silent), 'wb') as audio:
    audio.setnchannels(1)
    audio.setsampwidth(2)
    audio.setframerate(8000)
    audio.writeframes(b'\0\0' * 8000 * 180)
book = media / 'player-book.m4b'
movie = media / 'player-chaptered.mp4'
plain = media / 'player-plain.mp4'
def encode(arguments):
    subprocess.run([str(ffmpeg), '-hide_banner', '-loglevel', 'error', '-nostdin', *arguments], check=True)
encode(['-i', str(silent), '-i', str(metadata), '-map_metadata', '1', '-c:a', 'aac', '-b:a', '32k', str(book)])
encode(['-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=12:duration=180', '-i', str(silent),
        '-i', str(metadata), '-map_metadata', '2', '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '32',
        '-c:a', 'aac', '-b:a', '32k', '-movflags', '+faststart', str(movie)])
encode(['-i', str(movie), '-map_metadata', '-1', '-map_chapters', '-1', '-c', 'copy', str(plain)])
with sqlite3.connect(root / 'data/library.db') as db:
    db.execute('PRAGMA foreign_keys=ON')
    def canonical(entity, key, value):
        db.execute('INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES (?,?,?,?) '
                   'ON CONFLICT(entity_id,key) DO UPDATE SET value=excluded.value',
                   (entity, key, str(value), home.stamp()))
    for item in manifest['items']:
        if 'assetId' not in item:
            continue
        asset = uuid.UUID(item['assetId']).bytes
        work = uuid.UUID(item['workId']).bytes
        kind = db.execute('SELECT media_type FROM works WHERE id=?', (work,)).fetchone()[0]
        if kind not in ['TV', 'Movies', 'Music', 'Audiobooks']:
            continue
        source = book if kind == 'Audiobooks' else silent if kind == 'Music' else plain if item['key'] == 'movie-unstarted' else movie
        destination = media / (item['key'] + source.suffix)
        shutil.copyfile(source, destination)
        with destination.open('ab') as output:
            output.write(item['key'].encode('ascii'))
        db.execute('UPDATE media_assets SET file_path_root=?,content_hash=? WHERE id=?',
                   (str(destination), hashlib.sha256(destination.read_bytes()).hexdigest(), asset))
        for entity in [asset, work]:
            canonical(entity, 'duration_seconds', 180)
            canonical(entity, 'runtime', '3:00')
        if kind == 'TV':
            # The Home seed uses synthetic Background art for explicit episode-still URLs.
            # Add the typed still record used by video tools, keeping its backdrop intact.
            source_url = db.execute("SELECT value FROM canonical_values WHERE entity_id=? AND key='episode_still_url'", (work,)).fetchone()
            if source_url and source_url[0].startswith('/stream/artwork/'):
                source_id = uuid.UUID(source_url[0].removeprefix('/stream/artwork/').split('?')[0]).bytes
                art = db.execute("SELECT local_image_path,local_image_path_s,local_image_path_m,local_image_path_l,width_px,height_px,aspect_class "
                                 "FROM entity_assets WHERE id=? AND entity_id=? AND asset_type='Background'", (source_id, work)).fetchone()
                if art:
                    db.execute("INSERT INTO entity_assets(id,entity_id,entity_type,asset_type,local_image_path,local_image_path_s,local_image_path_m,local_image_path_l,width_px,height_px,aspect_class,is_preferred) "
                               "VALUES (?,?,'Work','EpisodeStill',?,?,?,?,?,?,?,1)",
                               (home.uid('player-still-' + item['key']).bytes, work, *art))
        db.execute('UPDATE user_states SET progress_pct=0,extended_properties=? WHERE asset_id=?',
                   (json.dumps({'position_seconds': '0', 'duration_seconds': '180'}), asset))
        if kind in ['Music', 'TV', 'Movies']:
            suffix = '.lrc' if kind == 'Music' else '.vtt'
            track = media / (item['key'] + suffix)
            lines = ['Following the northern signal', 'A quiet rhythm across the coast', 'The next light comes into view', 'We keep listening together']
            track.write_text(''.join(f'[{i:02d}:00.00]{line}\n' for i, line in enumerate(lines)) if kind == 'Music'
                             else 'WEBVTT\n\n00:00:00.000 --> 00:02:59.000\nDisposable player caption fixture.\n', encoding='utf-8')
            db.execute('INSERT INTO text_tracks(id,asset_id,kind,language,provider,confidence,source_format,normalized_format,local_path,timing_mode,is_preferred,is_user_owned) VALUES (?,?,?,?,?,1,?,?,?,?,1,1)',
                       (home.uid('player-text-' + item['key']).bytes, asset, 'Lyrics' if kind == 'Music' else 'Subtitles',
                        'en', 'Local', 'lrc' if kind == 'Music' else 'vtt', 'lrc' if kind == 'Music' else 'vtt', str(track), 'Line'))
    album = home.uid('album').bytes
    original = next(item for item in manifest['items'] if item['key'] == 'album-active')
    original_work = uuid.UUID(original['workId']).bytes
    original_asset = uuid.UUID(original['assetId']).bytes
    library = db.execute('SELECT library_id FROM media_assets WHERE id=?', (original_asset,)).fetchone()[0]
    titles = ['Opening Signals', 'Across the Water', 'The Next Light', 'Coastal Morning']
    for number, title in enumerate(titles[1:], 2):
        key = f'player-song-{number}'
        work, edition, asset = (home.uid(prefix + key) for prefix in ['work-', 'edition-', 'asset-'])
        destination = media / (key + '.wav')
        shutil.copyfile(silent, destination)
        with destination.open('ab') as output:
            output.write(key.encode('ascii'))
        db.execute("INSERT INTO works(id,media_type,work_kind,parent_work_id,curator_state) VALUES (?,'Music','child',?,'accepted')", (work.bytes, album))
        db.execute('INSERT INTO editions(id,work_id) VALUES (?,?)', (edition.bytes, work.bytes))
        db.execute('INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,presented_at,library_id) VALUES (?,?,?,?,?,?)',
                   (asset.bytes, edition.bytes, hashlib.sha256(destination.read_bytes()).hexdigest(), str(destination), home.stamp(), library))
        for entity in [work.bytes, asset.bytes]:
            for field, value in db.execute('SELECT key,value FROM canonical_values WHERE entity_id=?', (original_work,)).fetchall():
                canonical(entity, field, value)
            canonical(entity, 'title', title)
            canonical(entity, 'track_number', number)
        manifest['items'].append({'key': key, 'title': title, 'workId': str(work), 'assetId': str(asset), 'parentWorkId': str(home.uid('album')), 'route': f'/details/work/{work}', 'shape': 'square'})
    canonical(album, 'child_entities_json', json.dumps({'tracks': [
        {'title': title, 'ordinal': i, 'track_number': i, 'disc_number': 1, 'duration_seconds': 180}
        for i, title in enumerate(titles, 1)]}))
    canonical(album, 'track_count', len(titles))
    # Match the Engine's ingestion-owned inspection contract using actual ffprobe facts.
    for asset, file_path, source_hash in db.execute('SELECT id,file_path_root,content_hash FROM media_assets').fetchall():
        path = Path(file_path).resolve()
        if path.parent != media.resolve() or path.suffix not in ['.wav', '.m4b', '.mp4']:
            continue
        inspected = json.loads(subprocess.check_output([
            str(ffmpeg.with_name('ffprobe.exe')), '-v', 'error', '-show_format', '-show_streams', '-show_chapters', '-of', 'json', str(path)]))
        duration = float(inspected['format']['duration'])
        probe = {'Duration': f'00:{int(duration // 60):02d}:{duration % 60:06.3f}', 'FileSizeBytes': path.stat().st_size,
                 'Chapters': [{'Index': i, 'Title': chapter.get('tags', {}).get('title'),
                               'StartSeconds': float(chapter['start_time']), 'EndSeconds': float(chapter['end_time'])}
                              for i, chapter in enumerate(inspected.get('chapters', []))]}
        probe['ChapterCount'] = len(probe['Chapters'])
        probe['AudioStreams'] = []
        for stream in inspected['streams']:
            if stream['codec_type'] == 'video':
                probe.update(VideoCodec=stream['codec_name'], Width=stream['width'], Height=stream['height'], PixelFormat=stream.get('pix_fmt'))
            elif stream['codec_type'] == 'audio':
                probe.update(AudioCodec=stream['codec_name'], SampleRate=int(stream['sample_rate']), Channels=stream['channels'])
                probe['AudioStreams'].append({'Index': stream['index'], 'Codec': stream['codec_name'], 'Language': 'en', 'IsDefault': True})
        db.execute('INSERT INTO playback_inspection_cache(asset_id,source_hash,metadata_json,inspected_at) VALUES (?,?,?,?) '
                   'ON CONFLICT(asset_id,source_hash) DO UPDATE SET metadata_json=excluded.metadata_json,inspected_at=excluded.inspected_at',
                   (asset, source_hash, json.dumps(probe), home.stamp()))
    violations = db.execute('PRAGMA foreign_key_check').fetchall()
    if violations:
        raise ValueError('Fixture foreign key violations')
home.dump(root / 'manifest.json', manifest)
(root / '.player-media-ready').write_text('180-second actual files; real movie/book chapters, text tracks, no user media.\n')
print('Disposable player media prepared: 180-second music/book/video, embedded chapters and local text tracks.')
