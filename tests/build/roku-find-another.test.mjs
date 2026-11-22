import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

// Structure check for the Roku client (spec find-another-on-tv, WP4). A Roku cannot run in CI, so this parses the
// component XML and BrightScript files and asserts what can go wrong silently on a device: a script that is not
// in the package, a node that does not exist, an observer with no handler, a wording string that was edited away.

const roku = fileURLToPath(new URL('../../clients/roku/', import.meta.url));
const components = join(roku, 'components');
const read = (path) => readFileSync(path, 'utf8');

const xmlFiles = readdirSync(components).filter((name) => name.endsWith('.xml'));
const component = (name) => ({ xml: read(join(components, `${name}.xml`)), brs: existsSync(join(components, `${name}.brs`)) ? read(join(components, `${name}.brs`)) : '' });
const logic = read(join(components, 'FindAnotherLogic.brs'));

const scriptUris = (xml) => [...xml.matchAll(/<script\b[^>]*\buri="([^"]+)"/g)].map((match) => match[1]);
const definedNames = (brs) => new Set([...brs.matchAll(/^\s*(?:sub|function)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(/gim)].map((match) => match[1].toLowerCase()));

test('every script a Roku component names is in the package', () => {
    for (const file of xmlFiles) {
        const xml = read(join(components, file));
        for (const uri of scriptUris(xml)) {
            assert.match(uri, /^pkg:\//, `${file}: ${uri} is not a package path`);
            assert.ok(existsSync(join(roku, uri.slice('pkg:/'.length))), `${file}: ${uri} does not exist`);
        }
    }
});

test('every component file declares the name its file has and the new ones are registered', () => {
    for (const file of xmlFiles) {
        const name = file.replace(/\.xml$/, '');
        assert.match(read(join(components, file)), new RegExp(`<component\\s+name="${name}"`), `${file} declares a different name`);
    }
    for (const name of ['FindAnotherWindow', 'FindAnotherRow', 'AudioSubtitlesPanel', 'LyricsPanel', 'LyricsLine']) {
        assert.ok(xmlFiles.includes(`${name}.xml`), `${name}.xml is missing`);
    }
});

test('every custom node the scene and the window use exists as a component', () => {
    const known = new Set(xmlFiles.map((file) => file.replace(/\.xml$/, '')));
    const scene = component('MainScene');
    for (const used of ['AudioSubtitlesPanel', 'LyricsPanel', 'FindAnotherWindow', 'ApiTask']) {
        assert.match(scene.xml, new RegExp(`<${used}\\b`), `MainScene does not place ${used}`);
        assert.ok(known.has(used), `${used} component is missing`);
    }
    const windowXml = component('FindAnotherWindow').xml;
    assert.match(windowXml, /itemComponentName="FindAnotherRow"/);
    assert.match(component('FindAnotherWindow').brs, /CreateObject\("roSGNode", "ApiTask"\)/);
});

test('every observer in the new components has a handler in the same component', () => {
    for (const name of ['FindAnotherWindow', 'FindAnotherRow', 'AudioSubtitlesPanel', 'LyricsPanel', 'LyricsLine', 'MainScene']) {
        const { xml, brs } = component(name);
        const handlers = definedNames(brs);
        // The logic file is included by every component, so its functions are also in scope.
        for (const handler of definedNames(logic)) handlers.add(handler);
        for (const match of brs.matchAll(/observeField\("[^"]+",\s*"([^"]+)"\)/g)) {
            assert.ok(handlers.has(match[1].toLowerCase()), `${name}: observer handler ${match[1]} is not defined`);
        }
        for (const match of xml.matchAll(/\bonChange="([^"]+)"/g)) {
            assert.ok(handlers.has(match[1].toLowerCase()), `${name}: onChange handler ${match[1]} is not defined`);
        }
    }
});

test('the pure logic file has no UI, no scene state and no network', () => {
    const code = logic.split(/\r?\n/).filter((line) => !line.trim().startsWith("'")).join('\n');
    for (const forbidden of [/\bm\./, /\bm\[/, /findNode/i, /roSGNode/i, /roUrlTransfer/i, /roRegistry/i, /setFocus/i, /\.visible\b/, /observeField/i]) {
        assert.doesNotMatch(code, forbidden, `FindAnotherLogic.brs must not use ${forbidden}`);
    }
});

test('every component that calls the logic includes it, and every fa function it calls exists', () => {
    const defined = definedNames(logic);
    for (const name of ['FindAnotherWindow', 'FindAnotherRow', 'AudioSubtitlesPanel', 'LyricsPanel', 'MainScene']) {
        const { xml, brs } = component(name);
        assert.ok(scriptUris(xml).includes('pkg:/components/FindAnotherLogic.brs'), `${name} does not include FindAnotherLogic.brs`);
        for (const match of brs.matchAll(/\b(fa[A-Z][A-Za-z0-9]*)\s*\(/g)) {
            assert.ok(defined.has(match[1].toLowerCase()), `${name} calls ${match[1]}, which FindAnotherLogic.brs does not define`);
        }
    }
});

test('the wording of section 4 is in the logic file', () => {
    for (const text of [
        'Looking for subtitles…',
        'Reading…',
        'Could not read this one',
        'Find subtitles…',
        'Find subtitles',
        'Use this',
        'Cancel',
        'Best match',
        'In use',
        'Hearing impaired',
        'Made for your file',
        'A different release, may be out of step',
        'Fits the film',
        'minutes short of the film',
        'minutes past the film',
        'Updating…',
    ]) {
        assert.ok(logic.includes(text), `FindAnotherLogic.brs is missing the wording "${text}"`);
    }
});

test('every status the Engine can answer has a sentence, shown from the Engine message first', () => {
    for (const status of ['found', 'nonefound', 'nosourceon', 'notallowed', 'blocked', 'unavailable', 'assetmissing', 'unsupported', 'identityunverified', 'sourcekeyrequired', 'limitreached', 'languageunknown']) {
        assert.match(logic, new RegExp(`^\\s*${status}:\\s*"`, 'm'), `no fallback sentence for ${status}`);
    }
    assert.match(logic, /function faStatusSentence[\s\S]*?if message <> "" then return message/, 'the Engine message must win over the fallback');
});

test('subtitle rows are read after one second at rest, once, and the best match at once', () => {
    assert.match(logic, /function faDwellSeconds\(\) as Float\s+return 1\.0/);
    assert.match(logic, /function faShouldRead[\s\S]*?if faCacheState\(cache, key\) <> "none" then return false[\s\S]*?if isBest then return true[\s\S]*?return rested/);
    const window = component('FindAnotherWindow');
    assert.match(window.xml, /<Timer id="dwellTimer" duration="1" repeat="false"/);
    assert.match(window.brs, /dwellTimer\.duration = faDwellSeconds\(\)/);
    // Focus changes restart the Timer, so fast scrolling reads nothing.
    assert.match(window.brs, /m\.dwellTimer\.control = "stop"[\s\S]*?m\.dwellTimer\.control = "start"/);
    // Only the timer's own firing or an explicit Select reads a row that is not the best match.
    assert.match(window.brs, /sub onDwell\(\)[\s\S]*?faShouldRead\(m\.cache, faKey\(m\.items\[index\]\), index = 0, true\)/);
});

test('the key map follows section 4', () => {
    for (const [zone, key, target] of [
        ['list', 'right', 'pane'], ['pane', 'right', 'use'], ['use', 'left', 'pane'], ['pane', 'left', 'list'],
    ]) {
        assert.match(logic, new RegExp(`if zone = "${zone}"[\\s\\S]*?if key = "${key}" then return "${target}"`), `${zone} ${key} should go to ${target}`);
    }
    assert.match(logic, /function faBackAction[\s\S]*?"closeLanguages"[\s\S]*?"closeFullWords"[\s\S]*?"closeWindow"/);
    const window = component('FindAnotherWindow');
    assert.match(window.brs, /sub onRowSelected\(\)[\s\S]*?m\.fullOpen = true[\s\S]*?focusZone\("use"\)/, 'Select on a row opens the words in full and focuses Use this');
    assert.match(window.brs, /function onKeyEvent[\s\S]*?faBackAction\(m\.languageOpen, m\.fullOpen\)/);
});

test('the window sits inside the dark theme and the 5 percent safe margins', () => {
    for (const name of ['FindAnotherWindow', 'AudioSubtitlesPanel', 'LyricsPanel', 'FindAnotherRow', 'LyricsLine']) {
        const xml = component(name).xml;
        for (const match of xml.matchAll(/translation="\[(\d+),\s*(\d+)\]"/g)) {
            const [x, y] = [Number(match[1]), Number(match[2])];
            if ((name === 'AudioSubtitlesPanel' || name === 'LyricsPanel') && x === 1320 && y === 0) continue; // the panel's own background runs to the screen edge
            if (name === 'FindAnotherRow' || name === 'LyricsLine') continue; // positions inside a row
            assert.ok(x >= 96 && y >= 54 || name === 'FindAnotherWindow' && y >= 54, `${name}: translation [${x},${y}] is outside the 5 percent margins`);
        }
        assert.doesNotMatch(xml, /color="#(?:FFFFFF|F[0-9A-F]{5})"[^>]*width="1920"/i, `${name} must not paint a light full-screen background`);
    }
});

test('the Engine calls the window and the panel make are in the API task', () => {
    const api = read(join(components, 'ApiTask.brs'));
    for (const [operation, fragment] of [
        ['textTracks', 'textTracksPath(payload.assetId)'],
        ['textTrackSources', '/sources?kind=" + textKind(payload)'],
        ['alternatives', '/alternatives?kind=" + textKind(payload) + languageQuery(payload)'],
        ['alternativePreview', '/alternatives/preview?kind=" + textKind(payload) + "&provider='],
        ['alternativeChoose', '/alternatives/choose'],
        ['textTrackFile', 'target'],
        ['playbackPreferences', '/api/v1/devices/current/playback-preferences'],
    ]) {
        assert.match(api, new RegExp(`m\\.top\\.operation = "${operation}"`), `ApiTask has no ${operation} operation`);
        assert.ok(api.includes(fragment), `ApiTask ${operation} is missing ${fragment}`);
    }
    assert.match(api, /function textTracksPath[\s\S]*?"\/api\/v1\/stream\/" \+ encode\(assetId\) \+ "\/text-tracks"/);
    assert.match(api, /choice = \{ kind: textKind\(payload\), provider: payload\.provider, source_id: payload\.sourceId \}/);
    assert.match(api, /if textKind\(payload\) = "subtitles" then choice\.language = payload\.language/);
    assert.match(api, /"POST", textTracksPath\(payload\.assetId\) \+ "\/alternatives\/choose", choice/);
});

test('the player opens the panel and hides the footer row when there is nothing to search', () => {
    const scene = component('MainScene').brs;
    assert.match(scene, /key = "options" and m\.video\.visible/);
    assert.match(scene, /m\.findAvailable = faSourcesReady\(/);
    assert.match(logic, /if showFind then rows\.Push\(\{ kind: "find"/);
    assert.match(logic, /function faSourcesReady\(status as String\) as Boolean\s+return LCase\(status\) = "ready"/);
    assert.match(scene, /m\.status\.text = faTextUpdating\(\)/);
});

// ---- Lyrics panel and Find lyrics (spec find-another-on-tv WP5) ----

test('the lyrics wording of section 4 is in the logic file', () => {
    for (const text of [
        'Find lyrics',
        'Find lyrics…',
        'Looking for lyrics…',
        'Updating lyrics…',
        'Lyrics',
        'Synced',
        'Unsynced',
        'Words only, not timed',
        'Fits the song',
        'seconds',
        ' longer',
        ' shorter',
    ]) {
        assert.ok(logic.includes(`"${text}`) || logic.includes(text), `FindAnotherLogic.brs is missing the lyrics wording "${text}"`);
    }
    assert.match(logic, /function faTextFindLyricsRow\(\) as String\s+return "Find lyrics…"/);
    assert.match(logic, /function faTextSynced\(\) as String\s+return "Synced"/);
    assert.match(logic, /function faTextWordsOnly\(\) as String\s+return "Words only, not timed"/);
    assert.match(logic, /function faTextFitsTheSong\(\) as String\s+return "Fits the song"/);
});

test('lyrics results show six lines with no times, and no Language button or parameter', () => {
    assert.match(logic, /function faPaneLineLimit\(full as Boolean, kind = "subtitles" as String\)[\s\S]*?if kind = "lyrics" then return 6/);
    assert.match(logic, /if at <> invalid and kind <> "lyrics" then text = faClock\(at\)/);
    assert.match(logic, /function faHasLanguage\(kind as String\) as Boolean\s+return kind <> "lyrics"/);
    assert.match(logic, /function faLanguageRelevant\([^)]*\)[\s\S]*?if not faHasLanguage\(kind\) then return false/);
    assert.match(logic, /if key = "up" and atFirstRow and languageShown then return "language"/);
    const window = component('FindAnotherWindow');
    assert.match(window.brs, /if faStr\(args\.kind\) = "lyrics" then m\.kind = "lyrics"/);
    assert.match(window.brs, /m\.windowTitle\.text = faWindowTitle\(m\.kind\)/);
    assert.match(window.brs, /m\.languageShown = faHasLanguage\(m\.kind\)\s+m\.languageButton\.visible = m\.languageShown/);
    assert.match(window.brs, /faMove\(zone, key, m\.list\.itemFocused = 0, m\.languageShown\)/);
    // The same dwell rule: the kind is passed along, the timer logic is the subtitles one.
    assert.match(window.brs, /runTask\("alternatives", \{ assetId: m\.assetId, kind: m\.kind, language: m\.language \}/);
    assert.match(window.brs, /runTask\("alternativePreview", \{ assetId: m\.assetId, kind: m\.kind,/);
});

test('the API task asks for lyrics with kind=lyrics and no language, and reads the lyrics file as text', () => {
    const api = read(join(components, 'ApiTask.brs'));
    assert.match(api, /function textKind\(payload as Object\) as String[\s\S]*?"lyrics"[\s\S]*?return "subtitles"/);
    assert.match(api, /function languageQuery\(payload as Object\) as String\s+if textKind\(payload\) = "lyrics" then return ""/);
    assert.match(api, /m\.top\.operation = "lyricsText"[\s\S]*?textTracksPath\(payload\.assetId\) \+ "\/" \+ encode\(payload\.trackId\)/);
    assert.match(api, /FormatJson\(\{ text: response\.body \}\)/);
});

test('the Lyrics panel has a heading, a Synced chip, the lines and the Find lyrics footer row', () => {
    const panel = component('LyricsPanel');
    assert.match(panel.xml, /<Label id="panelTitle"[^>]*text="Lyrics"/);
    assert.match(panel.xml, /<Label id="chipLabel"/);
    assert.match(panel.xml, /<MarkupList id="lyricsList"[^>]*itemComponentName="LyricsLine"/);
    assert.match(panel.xml, /<Button id="findButton"[^>]*text="Find lyrics…"/);
    assert.match(panel.xml, /<field id="activeIndex" type="integer"/);
    assert.match(panel.brs, /m\.findButton\.visible = model\.showFind/);
    assert.match(panel.brs, /m\.top\.selection = "find"/);
    assert.match(panel.brs, /if key = "back"[\s\S]*?m\.top\.closeRequested = true/);
    assert.match(component('LyricsLine').brs, /content\.observeField\("isCurrent", "onCurrent"\)/);
});

test('the player offers the Lyrics panel for music and the synced line follows the song position', () => {
    const scene = component('MainScene');
    assert.match(scene.xml, /<LyricsPanel id="lyricsPanel" visible="false"/);
    assert.match(scene.brs, /m\.activeIsMusic = faIsMusic\(faStr\(manifest\.mediaType\)\)/);
    assert.match(scene.brs, /if m\.activeIsMusic then openLyricsPanel\(\) else openAudioSubtitles\(\)/);
    assert.match(scene.brs, /m\.video\.observeField\("position", "onVideoPosition"\)/);
    assert.match(scene.brs, /m\.lyricsPanel\.activeIndex = faActiveLyricIndex\(m\.lyricsParsed\.lines, m\.video\.position\)/);
    // The footer row is hidden unless the lyrics source check says Ready.
    assert.match(scene.brs, /runSide\("textTrackSources", \{ assetId: m\.activeAssetId, kind: "lyrics" \}\)/);
    assert.match(scene.brs, /m\.lyricsAvailable = faSourcesReady\(faStr\(value\.status\)\)/);
    assert.match(scene.brs, /model\.showFind = m\.lyricsAvailable/);
});

test('Find lyrics opens the same window with kind lyrics, Use this chooses with kind lyrics, then the panel reloads', () => {
    const scene = component('MainScene').brs;
    assert.match(scene, /m\.findWindow\.openWith = \{ assetId: m\.activeAssetId, title: m\.lastTitle, kind: "lyrics", language: "" \}/);
    assert.match(scene, /runSide\("alternativeChoose", \{ assetId: m\.activeAssetId, kind: "lyrics", provider: result\.provider/);
    assert.match(scene, /if faStr\(value\.status\) = "Updated"[\s\S]*?runSide\("textTracks", \{ assetId: m\.activeAssetId \}\)/);
    assert.match(scene, /m\.lyricsUpdating = true/);
    assert.match(scene, /sub onFindClosed\(\)[\s\S]*?if m\.activeIsMusic[\s\S]*?onLyricsFindClosed\(result\)/);
});

test('the lyrics file is read the way the Dashboard reads it: time tags make it Synced, the current line follows the position', () => {
    assert.match(logic, /function faParseLyrics\(content as String\) as Object/);
    assert.match(logic, /if timed\.Count\(\) > 0 then return \{ synced: true, lines: faSortLyrics\(timed\) \}/);
    assert.match(logic, /return \{ synced: false, lines: plain \}/);
    assert.match(logic, /function faStampSeconds\(inside as String\) as Dynamic/);
    assert.match(logic, /function faActiveLyricIndex\(lines as Object, seconds as Float\) as Integer[\s\S]*?if line\.at > seconds then exit for/);
    assert.match(logic, /function faPickLyricsTrackId[\s\S]*?"lyrics"[\s\S]*?isPreferred/);
    assert.match(logic, /function faIsMusic\(mediaType as String\) as Boolean[\s\S]*?"audiobook"[\s\S]*?"music"/);
});

test('the Roku README names the Lyrics panel and no longer lists it as missing', () => {
    const readme = read(join(roku, 'README.md'));
    assert.match(readme, /### Lyrics and Find lyrics/);
    assert.match(readme, /`LyricsPanel`/);
    assert.doesNotMatch(readme, /Lyrics panel is a separate packet/i);
});

test('the Roku README no longer lists the Audio & subtitles panel as a gap', () => {
    const readme = read(join(roku, 'README.md'));
    assert.doesNotMatch(readme, /no Audio & subtitles panel/);
    assert.match(readme, /Find another/);
});
