import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

// Structure check for the Roku player's controls (spec native-apps WP6). A Roku cannot run in CI, so this reads the component XML and
// BrightScript files and asserts what can go wrong silently on a device: a node or script that is not in the package, a word that was
// edited away from the contract, a key that no longer does what the tv-keys table says.

const root = fileURLToPath(new URL('../../', import.meta.url));
const roku = join(root, 'clients/roku/');
const components = join(roku, 'components');
const read = (path) => readFileSync(path, 'utf8');
const component = (name) => ({ xml: read(join(components, `${name}.xml`)), brs: existsSync(join(components, `${name}.brs`)) ? read(join(components, `${name}.brs`)) : '' });
const logic = read(join(components, 'PlayerLogic.brs'));
const controls = read(join(components, 'PlayerControls.brs'));
const contract = read(join(root, 'clients/contracts/v1/player-controls.md'));

const NEW_COMPONENTS = ['PlayerOverlay', 'SidePanel', 'PanelRow', 'TransportButton', 'ToolPill'];

test('the new player components and their images are in the package', () => {
    for (const name of NEW_COMPONENTS) {
        assert.ok(existsSync(join(components, `${name}.xml`)), `${name}.xml is missing`);
        assert.match(component(name).xml, new RegExp(`<component\\s+name="${name}"`), `${name}.xml declares a different name`);
    }
    for (const xml of readdirSync(components).filter((file) => file.endsWith('.xml'))) {
        for (const match of read(join(components, xml)).matchAll(/uri="pkg:\/(images\/[^"]+)"/g)) {
            assert.ok(existsSync(join(roku, match[1])), `${xml} uses ${match[1]}, which is not in the package`);
        }
    }
    for (const icon of ['circle', 'play', 'pause', 'previous', 'next', 'skip-back', 'skip-forward', 'check']) {
        assert.ok(existsSync(join(roku, `images/${icon}.png`)), `images/${icon}.png is missing`);
    }
});

test('the scene places the overlay and the side panel and includes the player scripts', () => {
    const scene = component('MainScene');
    for (const used of ['PlayerOverlay', 'SidePanel', 'AudioSubtitlesPanel']) {
        assert.match(scene.xml, new RegExp(`<${used}\\b`), `MainScene does not place ${used}`);
    }
    assert.match(scene.xml, /<Timer id="overlayTimer" duration="5" repeat="false"/);
    assert.match(scene.xml, /pkg:\/components\/PlayerLogic\.brs/);
    assert.match(scene.xml, /pkg:\/components\/PlayerControls\.brs/);
    assert.match(scene.brs, /initPlayer\(\)/);
});

test('the right-hand panel is 700 of the 1920 pixels, full height, with a fixed header and footer', () => {
    const panel = component('SidePanel').xml;
    assert.match(panel, /<Rectangle id="panelBack" translation="\[1220,0\]" width="700" height="1080"/);
    assert.match(panel, /<Label id="closeLabel"[^>]*text="Close"/);
    assert.match(panel, /<MarkupList id="bodyList"[^>]*itemComponentName="PanelRow"/);
    assert.match(panel, /<MarkupList id="footerList"[^>]*itemComponentName="PanelRow"/);
    // Rows are at least 56 px high at 1080p.
    const row = /itemSize="\[(\d+),(\d+)\]"/.exec(panel);
    assert.ok(row && Number(row[2]) >= 56, 'panel rows must be at least 56 px high');
});

test('no text in the player is smaller than 24 px', () => {
    for (const name of ['PlayerOverlay', 'PanelRow', 'SidePanel', 'ToolPill', 'TransportButton']) {
        for (const match of component(name).brs.matchAll(/(?:setFont|setRowFont)\([^,]+,\s*(\d+),/g)) {
            assert.ok(Number(match[1]) >= 24, `${name}: a ${match[1]} px font is below 24 px`);
        }
        for (const match of component(name).brs.matchAll(/\.size = (\d+)/g)) {
            assert.ok(Number(match[1]) >= 24, `${name}: a ${match[1]} px font is below 24 px`);
        }
    }
});

test('every observer and onChange handler in the new components is defined in the same component', () => {
    const defined = (brs) => new Set([...brs.matchAll(/^\s*(?:sub|function)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(/gim)].map((match) => match[1].toLowerCase()));
    for (const name of [...NEW_COMPONENTS, 'AudioSubtitlesPanel']) {
        const { xml, brs } = component(name);
        const handlers = defined(brs);
        for (const handler of defined(logic)) handlers.add(handler);
        for (const match of brs.matchAll(/observeField\("[^"]+",\s*"([^"]+)"\)/g)) {
            assert.ok(handlers.has(match[1].toLowerCase()), `${name}: observer handler ${match[1]} is not defined`);
        }
        for (const match of xml.matchAll(/\bonChange="([^"]+)"/g)) {
            assert.ok(handlers.has(match[1].toLowerCase()), `${name}: onChange handler ${match[1]} is not defined`);
        }
    }
    const sceneHandlers = new Set([...defined(component('MainScene').brs), ...defined(controls)]);
    for (const match of controls.matchAll(/observeField\("[^"]+",\s*"([^"]+)"\)/g)) {
        assert.ok(sceneHandlers.has(match[1].toLowerCase()), `PlayerControls: observer handler ${match[1]} is not defined`);
    }
});

test('the pure player logic has no screen, no scene state and no network', () => {
    const code = logic.split(/\r?\n/).filter((line) => !line.trim().startsWith("'")).join('\n');
    for (const forbidden of [/\bm\./, /\bm\[/, /findNode/i, /roSGNode/i, /roUrlTransfer/i, /roRegistry/i, /setFocus/i, /\.visible\b/, /observeField/i]) {
        assert.doesNotMatch(code, forbidden, `PlayerLogic.brs must not use ${forbidden}`);
    }
});

test('BrightScript blocks are balanced in the new files', () => {
    for (const file of ['PlayerLogic', 'PlayerControls', 'PlayerOverlay', 'SidePanel', 'PanelRow', 'TransportButton', 'ToolPill', 'AudioSubtitlesPanel']) {
        const stack = [];
        read(join(components, `${file}.brs`)).split(/\r?\n/).forEach((raw, index) => {
            const line = raw.replace(/"[^"]*"/g, '""').split("'")[0].trim().toLowerCase();
            const close = (kind) => assert.equal(stack.pop(), kind, `${file}.brs:${index + 1}: unexpected end ${kind}`);
            if (/^(sub|function)\s/.test(line)) stack.push('func');
            else if (/^end (sub|function)$/.test(line)) close('func');
            else if (/^if\b/.test(line) && (/\bthen$/.test(line) || !/\bthen\b/.test(line))) stack.push('if');
            else if (/^end if$/.test(line)) close('if');
            else if (/^for\b/.test(line)) stack.push('for');
            else if (/^end for$/.test(line)) close('for');
            else if (/^while\b/.test(line)) stack.push('while');
            else if (/^end while$/.test(line)) close('while');
        });
        assert.deepEqual(stack, [], `${file}.brs has an unclosed block`);
    }
});

test('the words are the contract\'s words', () => {
    // Words of the Panels table the Roku shows (the tests of the contract itself compare the table with the Dashboard).
    for (const text of [
        'Episodes', 'Audio & subtitles', 'Bookmarks', 'Playback settings', 'Close',
        'Bookmark at {0}', 'Mine', 'Global', 'Show on timeline', 'Edit', 'Delete', 'Keep', 'Name', 'Save', 'Cancel',
        'Couldn’t save · Try again', 'No Global bookmarks on this title yet', 'No bookmarks of yours yet',
        'Select a bookmark to jump to that moment.', 'Skip back', 'Skip forward', 'Playback information', 'Reset playback preferences',
        'Autoplay next episode', 'Up next', 'Playing', 'Paused', 'Season', 'Starts in {0} seconds', 'Next episode', 'Play now',
        'Previous episode', 'Skip back ', 'Skip forward ', 'Previous', 'Next',
    ]) {
        assert.ok(logic.includes(`"${text}`), `PlayerLogic.brs is missing the contract word "${text}"`);
        assert.ok(contract.includes(text.trim()), `the contract no longer has "${text.trim()}": update the Roku words`);
    }
    assert.match(logic, /"Skip back " \+ skip\.back\.ToStr\(\) \+ " s"/);
    assert.match(logic, /"Skip forward " \+ skip\.forward\.ToStr\(\) \+ " s"/);
});

test('transport: five buttons in the contract\'s order, Previous and Next dimmed in place', () => {
    const body = /function plTransport[\s\S]*?end function/.exec(logic)[0];
    const order = [...body.matchAll(/key: "([a-z-]+)"/g)].map((match) => match[1]);
    assert.deepEqual(order, ['previous', 'skip-back', 'play-pause', 'skip-forward', 'next']);
    assert.match(body, /key: "previous"[^\n]*dimmed: not plIsTrue\(ctx\.canPrevious\)/);
    assert.match(body, /key: "next"[^\n]*dimmed: not plIsTrue\(ctx\.canNext\)/);
    assert.match(body, /key: "skip-back"[^\n]*dimmed: false/);
    assert.match(body, /key: "skip-forward"[^\n]*dimmed: false/);
});

test('tools: Episodes, Audio & subtitles, Bookmarks, Playback settings, with no Volume, Fullscreen or Close', () => {
    const body = /function plTools[\s\S]*?end function/.exec(logic)[0];
    const order = [...body.matchAll(/key: "([a-z-]+)"/g)].map((match) => match[1]);
    assert.deepEqual(order, ['episodes', 'audio-subtitles', 'bookmarks', 'playback-settings']);
    assert.match(body, /if kind = "episode" and plIsTrue\(caps\.hasEpisodes\)/, 'Episodes only for a show that lists episodes');
    assert.match(body, /if plIsTrue\(caps\.canBookmark\)/);
    for (const absent of [/volume/i, /fullscreen/i, /picture/i]) assert.doesNotMatch(body, absent);
});

test('keys follow the tv-keys table', () => {
    const body = /function plKeyEffect[\s\S]*?end function/.exec(logic)[0];
    assert.match(body, /if key = "rev" then return "skip-back"/);
    assert.match(body, /if key = "fwd" then return "skip-forward"/);
    assert.match(body, /if key = "play" then return "toggle-play"/);
    // Back: the open panel first (a nested view goes to its root first), then the overlay.
    assert.match(body, /if key = "back"[\s\S]*?"panel-root"[\s\S]*?"close-panel"[\s\S]*?"hide-overlay"/);
    // Left and Right skip only with the timeline focused; any other key shows the overlay first.
    assert.match(body, /if not plIsTrue\(state\.overlayVisible\) then return "show-overlay"[\s\S]*?state\.zone = "timeline"/);
    assert.match(body, /if key = "left" then return "skip-back"/);
    assert.match(body, /if key = "right" then return "skip-forward"|return "skip-forward"/);
    // The contract table itself still says the same.
    assert.match(contract, /\| Rewind \| Skip back \(the same action as the Skip back button\) \|/);
    assert.match(contract, /\| Fast-forward \| Skip forward \(the same action as the Skip forward button\) \|/);
    assert.match(contract, /\| Back \| Close the open side panel first, then hide the overlay \|/);
});

test('the overlay never hides while a panel, the Find window or the End card is open, nor while paused, and hides after 5 seconds', () => {
    assert.match(logic, /function plOverlaySeconds\(\) as Float\s+return 5\.0/);
    assert.match(logic, /function plOverlayMayHide[\s\S]*?return not panelOpen and not finderOpen and not endCard and not paused/);
    assert.match(controls, /plOverlayMayHide\(p\.panel <> "", m\.findWindow\.visible, p\.endCard, paused\)/);
    assert.match(controls, /m\.overlayTimer\.control = "stop"\s+m\.overlayTimer\.control = "start"/);
});

test('the scene gives the remote to the player first while a video plays', () => {
    const scene = component('MainScene').brs;
    assert.match(scene, /if m\.p <> invalid and m\.video\.visible and not m\.findWindow\.visible and not m\.photoGroup\.visible\s+return playerKey\(key\)/);
    assert.match(scene, /m\.video\.enableUI = false\s+beginPlayer\(manifest, kind, meta\)/);
    assert.match(scene, /m\.video\.enableUI = true/, 'music and audiobooks keep Roku\'s own playback screen');
});

test('Bookmarks use the Engine\'s own Mine and Everyone flags and its audiobook-and-video routes', () => {
    assert.match(logic, /if plIsTrue\(item\.isYours\) then mine\.Push\(item\)/);
    assert.match(logic, /if plIsTrue\(item\.inEveryone\) then everyone\.Push\(item\)/);
    assert.match(logic, /showChoice: everyone\.Count\(\) > 0/, 'Mine | Global shows only when a Global set exists');
    const api = read(join(components, 'ApiTask.brs'));
    assert.match(api, /"\/api\/v1\/player\/audiobooks\/" \+ encode\(workId\) \+ "\/bookmarks"/);
    assert.match(api, /request\("PUT", "\/api\/v1\/player\/audiobooks\/bookmarks\/" \+ encode\(payload\.id\), payload\.body, true\)/);
    assert.match(api, /request\("DELETE", "\/api\/v1\/player\/audiobooks\/bookmarks\/" \+ encode\(payload\.id\), invalid, true\)/);
    assert.match(api, /transfer\.SetRequest\("PUT"\)/);
    assert.match(api, /transfer\.SetRequest\("DELETE"\)/);
    // No note field, no search box and no typing in the panel: only the rename keyboard dialog.
    assert.doesNotMatch(logic, /Add a note…|Search bookmarks/);
    assert.match(controls, /CreateObject\("roSGNode", "KeyboardDialog"\)/);
    // The save at the moment it was pressed, at once, with no note.
    assert.match(logic, /body = \{ assetId: assetId, positionSeconds: plRound1\(position\), isGlobal: false \}/);
});

test('Playback settings: skip seconds and Autoplay stay on this Roku, there is no speed row', () => {
    const body = /function plSettingsModel[\s\S]*?end function/.exec(logic)[0];
    assert.deepEqual([...body.matchAll(/value: "([a-z-]+)"/g)].map((match) => match[1]), ['skip-back', 'skip-forward', 'info', 'reset']);
    assert.doesNotMatch(body, /Playback speed/);
    assert.match(logic, /function plSkipBackChoices\(\) as Object\s+return \[5, 10, 15, 30\]/);
    assert.match(logic, /function plSkipForwardChoices\(\) as Object\s+return \[10, 30, 60, 90\]/);
    assert.match(controls, /m\.registry\.Write\("skip_back", value\)/);
    assert.match(controls, /m\.registry\.Write\("autoplay", boolFlag\(p\.device\.autoplay\)\)/);
    assert.match(logic, /prefs\.skipBackSeconds[\s\S]*?prefs\.skipForwardSeconds/);
});

test('End card: Up next, the countdown, the next episode, Cancel and Play now with Play now focused and Back cancelling', () => {
    assert.match(logic, /function plEndCardSeconds\(\) as Integer\s+return 10/);
    assert.match(logic, /if remaining >= 0 then countdown = plFill\(words\.endCountdown/);
    assert.match(logic, /if plIsTrue\(state\.endCard\)[\s\S]*?if key = "back" then return "cancel-end-card"/);
    assert.match(controls, /p\.endFocus = 1/);
    assert.match(controls, /if p\.endFocus = 1 then playEpisode\(p\.following\) else cancelEndCard\(\)/);
    assert.match(controls, /p\.endRemaining = -1\s+if plAutoplayOn\(p\.prefs, p\.device\) then p\.endRemaining = Int\(remaining \+ 0\.99\)/, 'with autoplay off the card has no countdown');
    const overlay = component('PlayerOverlay');
    assert.match(overlay.brs, /m\.top\.findNode\("playNowRing"\)\.visible = card\.focus = 1/);
});

test('paused details: the title, the one line and, for episodes, the description; hidden while a panel is open', () => {
    assert.match(controls, /showDescription = paused and not panelOpen and p\.description <> "" and p\.kind <> "film"/);
    assert.match(logic, /"S" \+ season\.ToStr\(\) \+ " E" \+ episode\.ToStr\(\)/);
    assert.match(logic, /return minutes\.ToStr\(\) \+ " min"/);
    assert.match(logic, /\(minutes \\ 60\)\.ToStr\(\) \+ "h " \+ \(minutes mod 60\)\.ToStr\(\) \+ "m"/);
});

test('Find subtitles… opens the existing Find another screen from the Audio & subtitles footer', () => {
    const panel = component('AudioSubtitlesPanel');
    assert.match(panel.xml, /<SidePanel id="inner"/);
    assert.match(panel.brs, /footer\.Push\(\{ kind: "value", title: row\.title, second: "", value: "find" \}\)/);
    assert.match(component('MainScene').brs, /m\.findWindow\.openWith = \{ assetId: m\.activeAssetId, title: m\.lastTitle, language: language \}/);
});

test('the Roku README lists the Player gaps and no longer claims the old ones', () => {
    const readme = read(join(roku, 'README.md'));
    assert.match(readme, /### Player controls/);
    assert.doesNotMatch(readme, /it has no\s+skip buttons or Previous\/Next row/);
    for (const gap of ['Playback speed', 'Subtitle appearance', 'Lessons', 'stills', 'bookmark frames not drawn']) {
        assert.ok(readme.includes(gap), `the README does not record the gap: ${gap}`);
    }
});

// ---- Static guards for mistakes that only show on a device (nothing here can be compiled in CI) ----

const GUARDED = ['PlayerLogic', 'PlayerControls', 'PlayerOverlay', 'SidePanel', 'PanelRow', 'TransportButton', 'ToolPill', 'AudioSubtitlesPanel'];
const codeLines = (file) => read(join(components, `${file}.brs`)).split(/\r?\n/).map((raw, index) => ({
    index: index + 1,
    code: raw.replace(/"[^"]*"/g, '""').split("'")[0].trim(),
}));

test('no BrightScript reserved word is used as a variable, parameter or key name', () => {
    const reserved = 'and as box dim each else elseif end endfor endfunction endif endsub endwhile eval exit exitfor false for function goto if invalid let next not or pos print rem return run step stop sub tab then to true type while'.split(' ');
    const name = `(${reserved.join('|')})`;
    const patterns = [
        new RegExp(`^${name}\\s*=(?!=)`, 'i'),
        new RegExp(`[(,]\\s*${name}\\s+as\\b`, 'i'),
        new RegExp(`\\bfor\\s+each\\s+${name}\\s+in\\b`, 'i'),
        new RegExp(`\\bfor\\s+${name}\\s*=`, 'i'),
        new RegExp(`[{,]\\s*${name}\\s*:`, 'i'),
    ];
    for (const file of GUARDED) {
        for (const { index, code } of codeLines(file)) {
            for (const pattern of patterns) {
                assert.doesNotMatch(code, pattern, `${file}.brs:${index}: a reserved word is used as a name: ${code}`);
            }
        }
    }
});

test('an optional key is never compared with true or false directly (a missing value is a type error); plIsTrue is used', () => {
    for (const file of GUARDED) {
        for (const { index, code } of codeLines(file)) {
            // A plain assignment ("m.p.endCard = true") is not a comparison, so the assignment target is removed first.
            const rest = code
                .replace(/^[A-Za-z_][\w.[\]]*\s*=(?!=)\s*/, '')
                .replace(/\b(then|else)\s+[A-Za-z_][\w.[\]]*\s*=(?!=)\s*/g, '$1 ');
            assert.doesNotMatch(rest, /(?:\.\w+|\])\s*(?:=|<>)\s*(?:true|false)\b/, `${file}.brs:${index}: compare with plIsTrue(...), not = or <> true/false: ${code}`);
        }
    }
    assert.match(logic, /function plIsTrue\(value as Dynamic\) as Boolean\s+if value = invalid then return false/);
    for (const file of ['SidePanel', 'PanelRow', 'TransportButton', 'AudioSubtitlesPanel', 'PlayerOverlay']) {
        assert.match(component(file).xml, /pkg:\/components\/PlayerLogic\.brs/, `${file} uses plIsTrue, so it must include PlayerLogic.brs`);
    }
});

test('the Options key and the footer of Audio & subtitles never apply a track by mistake', () => {
    assert.match(component('AudioSubtitlesPanel').brs, /if selection\.kind = "options" then return\s+if selection\.index < 0/);
    assert.match(component('SidePanel').brs, /else if key = "options" and m\.zone = "body"/);
});

test('Play/Pause, Rewind and Fast-forward reach the player while a panel is open', () => {
    for (const file of ['SidePanel', 'AudioSubtitlesPanel']) {
        assert.match(component(file).brs, /if key = "play" or key = "rev" or key = "fwd" then return false/, `${file} swallows the transport keys`);
    }
});

test('a Global or not-your-own bookmark cannot be edited, and a failed list shows an error line, not the empty state', () => {
    assert.match(logic, /canChange: whichTab <> "global" and plIsTrue\(item\.isYours\) and not plIsTrue\(item\.isGlobal\)/);
    assert.match(logic, /loadError/);
    assert.match(controls, /p\.bmLoadError = "Bookmarks could not be read\."/);
});

test('another episode starts only after the position reached is saved', () => {
    assert.match(controls, /sub playEpisode\(item as Dynamic\)[\s\S]*?sendHeartbeat\(\)[\s\S]*?meta = emptyMeta\(\)/);
});
