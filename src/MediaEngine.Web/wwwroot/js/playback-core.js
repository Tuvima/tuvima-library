// playback-core.js — Dashboard audio and video playback helpers (classic script)
// Moved out of app.js unchanged: window.tuvimaPopupStateSync, window.listenPlayback (the interop contract called from Listen and Watch)
// and window.playbackTools. App.razor loads this right after app.js and before blazor.web.js, so the globals exist before Blazor starts.

window.tuvimaPopupStateSync = (function () {
    function getLatestState(stateKey) {
        var openerOwnsState = false;
        try {
            var opener = window.opener;
            if (opener && !opener.closed
                && opener.location.origin === window.location.origin
                && opener.listenPlayback
                && typeof opener.listenPlayback.getStoredState === 'function') {
                openerOwnsState = true;
                return opener.listenPlayback.getStoredState();
            }
        } catch (_) {
            // Cross-origin or isolated opener access falls back to same-origin storage.
        }

        if (openerOwnsState) return null;

        try {
            return window.localStorage.getItem(stateKey);
        } catch (_) {
            return null;
        }
    }

    function requestLatestState(sendCommand) {
        if (typeof sendCommand !== 'function') return;
        sendCommand(JSON.stringify({ action: 'request-state' }));
    }

    return { getLatestState: getLatestState, requestLatestState: requestLatestState };
})();

window.listenPlayback = (function () {
    var playbackConfig = {
        immediateActionDedupMilliseconds: 900,
        immediateActionConsumeMilliseconds: 1800,
        audioObserverIntervalMilliseconds: 1200,
        audioObserverMinimumIntervalMilliseconds: 500,
        seekToleranceSeconds: 0.75,
        volumeStep: 0.05,
        defaultVolume: 0.8
    };
    var stateKey = 'tuvima.playback.v2.state';
    var commandKey = 'tuvima.playback.v2.command';
    var channel = typeof BroadcastChannel !== 'undefined' ? new BroadcastChannel('tuvima-listen-playback') : null;
    var stateHandler = null;
    var commandHandler = null;
    var audioObservers = typeof WeakMap !== 'undefined' ? new WeakMap() : null;
    var playerShortcutHandlers = typeof WeakMap !== 'undefined' ? new WeakMap() : null;

    function readConfigValue(options, camelName, snakeName, fallback) {
        if (!options) return fallback;
        if (Object.prototype.hasOwnProperty.call(options, camelName)) return options[camelName];
        var pascalName = camelName.charAt(0).toUpperCase() + camelName.slice(1);
        if (Object.prototype.hasOwnProperty.call(options, pascalName)) return options[pascalName];
        if (snakeName && Object.prototype.hasOwnProperty.call(options, snakeName)) return options[snakeName];
        return fallback;
    }

    function asFiniteNumber(value, fallback, min, max) {
        var next = typeof value === 'number' ? value : Number.parseFloat(value);
        if (!Number.isFinite(next)) return fallback;
        if (typeof min === 'number') next = Math.max(min, next);
        if (typeof max === 'number') next = Math.min(max, next);
        return next;
    }

    function configure(options) {
        playbackConfig.immediateActionDedupMilliseconds = asFiniteNumber(readConfigValue(options, 'immediateActionDedupMilliseconds', 'immediate_action_dedup_milliseconds', playbackConfig.immediateActionDedupMilliseconds), playbackConfig.immediateActionDedupMilliseconds, 100, 5000);
        playbackConfig.immediateActionConsumeMilliseconds = asFiniteNumber(readConfigValue(options, 'immediateActionConsumeMilliseconds', 'immediate_action_consume_milliseconds', playbackConfig.immediateActionConsumeMilliseconds), playbackConfig.immediateActionConsumeMilliseconds, 100, 10000);
        playbackConfig.audioObserverIntervalMilliseconds = asFiniteNumber(readConfigValue(options, 'audioObserverIntervalMilliseconds', 'audio_observer_interval_milliseconds', playbackConfig.audioObserverIntervalMilliseconds), playbackConfig.audioObserverIntervalMilliseconds, 100, 10000);
        playbackConfig.audioObserverMinimumIntervalMilliseconds = asFiniteNumber(readConfigValue(options, 'audioObserverMinimumIntervalMilliseconds', 'audio_observer_minimum_interval_milliseconds', playbackConfig.audioObserverMinimumIntervalMilliseconds), playbackConfig.audioObserverMinimumIntervalMilliseconds, 100, 5000);
        playbackConfig.seekToleranceSeconds = asFiniteNumber(readConfigValue(options, 'seekToleranceSeconds', 'seek_tolerance_seconds', playbackConfig.seekToleranceSeconds), playbackConfig.seekToleranceSeconds, 0.05, 10);
        playbackConfig.volumeStep = asFiniteNumber(readConfigValue(options, 'volumeStep', 'volume_step', playbackConfig.volumeStep), playbackConfig.volumeStep, 0.01, 0.5);
        playbackConfig.defaultVolume = asFiniteNumber(readConfigValue(options, 'defaultVolume', 'default_volume', playbackConfig.defaultVolume), playbackConfig.defaultVolume, 0, 1);
    }

    function notifyState(json) {
        if (stateHandler) {
            var reference = json
                ? DotNet.createJSStreamReference(new Blob([json], { type: 'application/json' }))
                : null;
            stateHandler.invokeMethodAsync('HandlePlaybackState', reference);
        }
    }

    function notifyCommand(json) {
        if (commandHandler && json) {
            commandHandler.invokeMethodAsync('HandlePlaybackCommand', json);
        }
    }

    function readAudioElementState(element) {
        synchronizeCaptionSelection(element);
        if (!element) {
            return {
                currentTime: 0,
                duration: 0,
                volume: playbackConfig.defaultVolume,
                muted: false,
                paused: true,
                playbackRate: 1
            };
        }

        var pendingStart = Number.parseFloat(element.dataset.listenPendingStartPosition || '');
        var currentTime = element.currentTime || 0;
        if (Number.isFinite(pendingStart) && pendingStart > 0) {
            var pendingStartedAt = Number.parseFloat(element.dataset.listenPendingStartAt || '');
            var pendingAge = Number.isFinite(pendingStartedAt) ? Date.now() - pendingStartedAt : 0;
            if (currentTime >= pendingStart - playbackConfig.seekToleranceSeconds || pendingAge > 8000) {
                delete element.dataset.listenPendingStartPosition;
                delete element.dataset.listenPendingStartAt;
            } else {
                currentTime = pendingStart;
            }
        }

        return {
            currentTime: currentTime,
            subtitleCount: element._tuvimaHls?.subtitleTracks?.length || element.textTracks?.length || 0,
            captionTracks: readCaptionTrackChoices(element),
            assetId: element.tagName === 'VIDEO' ? element._tuvimaVideoBinding?.asset || '' : audioAssetId(element),
            requestVersion: element.tagName === 'VIDEO' ? element._tuvimaVideoBinding?.request ?? null : Number(element.dataset.playbackRequestVersion),
            audioTrackCount: element._tuvimaHls?.audioTracks?.length || element.audioTracks?.length || 0,
            duration: isFinite(element.duration) ? element.duration : 0,
            volume: typeof element.volume === 'number' ? element.volume : playbackConfig.defaultVolume,
            muted: !!element.muted,
            paused: !!element.paused,
            playbackRate: typeof element.playbackRate === 'number' ? element.playbackRate : 1
        };
    }

    function readCaptionTrackChoices(element) {
        if (!element) return [];
        var choices = [];
        var textTracks = Array.from(element.textTracks || []);
        textTracks.forEach(function (track, index) {
            if (track.kind !== 'captions' && track.kind !== 'subtitles') return;
            var node = Array.from(element.querySelectorAll('track')).find(function (candidate) { return candidate.track === track; });
            var key = node?.dataset?.playbackTrackKey || ('browser:' + index);
            choices.push({
                key: key,
                label: track.label || track.language || ('Caption ' + (index + 1)),
                language: track.language || '',
                selected: track.mode === 'showing'
            });
        });

        var hls = element._tuvimaHls;
        Array.from(hls?.subtitleTracks || []).forEach(function (track, index) {
            var label = track.name || track.label || track.lang || track.language || ('Subtitle ' + (index + 1));
            var language = track.lang || track.language || '';
            var existing = choices.find(function (choice) {
                return choice.label.toLocaleLowerCase() === String(label).toLocaleLowerCase()
                    && !choice.key.startsWith('managed:')
                    && !Number.isInteger(choice.hlsIndex)
                    && (!language || !choice.language || choice.language.toLocaleLowerCase() === String(language).toLocaleLowerCase());
            });
            var selected = hls.subtitleDisplay !== false && hls.subtitleTrack === index;
            if (existing) {
                existing.selected = existing.selected || selected;
                existing.hlsIndex = index;
            } else choices.push({ key: 'hls:' + index, label: String(label), language: String(language), selected: selected, hlsIndex: index });
        });
        return choices;
    }

    function currentCaptionBinding(element, asset, request, profile) {
        const binding = element?._tuvimaVideoBinding;
        return binding && element.dataset.playbackAssetId === binding.asset
            && Number(element.dataset.playbackRequestVersion) === binding.request
            && (!asset || binding.asset === asset) && (request == null || binding.request === Number(request))
            && (!profile || binding.profile === profile) ? binding : null;
    }

    function synchronizeCaptionSelection(element) {
        const binding = currentCaptionBinding(element);
        if (!binding || element._tuvimaSynchronizingCaptions) return;
        const previous = element._tuvimaCaptionSelection;
        const selection = previous?.asset === binding.asset && previous?.profile === binding.profile
            ? previous : { asset: binding.asset, profile: binding.profile, key: null, explicit: false };
        element._tuvimaCaptionSelection = selection;
        const choices = readCaptionTrackChoices(element);
        if (!selection.explicit) {
            const preferred = Array.from(element.querySelectorAll('track')).find(node => node.dataset.playbackPreferred === 'true');
            const nativeDefault = Array.from(element.querySelectorAll('track')).find(node => node.default);
            // Order: the title's own preferred subtitle, then the profile default ("off" or a track key set by the host), then
            // whatever the browser or file marks as default. When the host changes its mind (for example after the audio
            // track switched), the track the old default turned on no longer counts as the viewer's choice.
            const profileDefault = element.dataset.playbackCaptionDefault || '';
            const profileChanged = selection.profileDefault !== undefined && selection.profileDefault !== profileDefault;
            selection.profileDefault = profileDefault;
            const profileKey = profileDefault && profileDefault !== 'off' && choices.some(choice => choice.key === profileDefault) ? profileDefault : null;
            const fallbackKey = (profileChanged ? null : choices.find(choice => choice.selected)?.key) || nativeDefault?.dataset.playbackTrackKey || null;
            selection.key = preferred?.dataset.playbackTrackKey || (profileDefault === 'off' ? null : profileKey || fallbackKey);
        }
        const selected = choices.filter(choice => choice.selected);
        const showingNative = Array.from(element.textTracks || []).filter(track => (track.kind === 'captions' || track.kind === 'subtitles') && track.mode === 'showing');
        if (selected.length === (selection.key ? 1 : 0) && (!selection.key || selected[0].key === selection.key) && showingNative.length <= 1) return;
        element._tuvimaSynchronizingCaptions = true;
        try {
            // A temporarily missing explicit choice stays off until that track returns.
            selectCaptionTrack(element, choices.some(choice => choice.key === selection.key) ? selection.key : null, null, null, null, false);
        } finally { element._tuvimaSynchronizingCaptions = false; }
    }

    function selectCaptionTrack(element, key, expectedAsset, expectedRequest, expectedProfile, remember = true) {
        if (!element) return false;
        if (expectedAsset && !currentCaptionBinding(element, expectedAsset, expectedRequest, expectedProfile)) return false;
        var hls = element._tuvimaHls;
        var choices = readCaptionTrackChoices(element);
        var choice = key ? choices.find(track => track.key === key) : null;
        if (key && !choice) return false;
        if (remember && currentCaptionBinding(element)) {
            const binding = element._tuvimaVideoBinding;
            element._tuvimaCaptionSelection = { asset: binding.asset, profile: binding.profile, key: key || null, explicit: true };
        }
        for (const track of Array.from(element.textTracks || [])) if (track.mode !== 'disabled') track.mode = 'disabled';
        if (hls) {
            hls.subtitleTrack = -1;
            hls.subtitleDisplay = false;
        }
        if (!choice) return true;
        if (Number.isInteger(choice.hlsIndex) && hls) {
            hls.subtitleTrack = choice.hlsIndex;
            hls.subtitleDisplay = true;
            return true;
        }
        var node = Array.from(element.querySelectorAll('track'))
            .find(track => track.dataset.playbackTrackKey === key);
        var nativeTrack = node?.track;
        if (!nativeTrack && key.startsWith('browser:')) {
            nativeTrack = Array.from(element.textTracks || [])[Number.parseInt(key.slice(8), 10)];
        }
        if (!nativeTrack) return false;
        nativeTrack.mode = 'showing';
        return true;
    }

    function audioObserverFor(element) {
        return audioObservers ? audioObservers.get(element) : element && element.__listenAudioObserver;
    }

    function setAudioObserver(element, observer) {
        if (!element) return;
        if (audioObservers) {
            if (observer) {
                audioObservers.set(element, observer);
            } else {
                audioObservers.delete(element);
            }
            return;
        }

        if (observer) {
            element.__listenAudioObserver = observer;
        } else {
            delete element.__listenAudioObserver;
        }
    }

    function removeAudioObserver(element) {
        var observer = audioObserverFor(element);
        if (!element || !observer) return;

        element.removeEventListener('timeupdate', observer.onTimeUpdate);
        element.removeEventListener('durationchange', observer.onMetadataChanged);
        element.removeEventListener('loadedmetadata', observer.onMetadataChanged);
        element.removeEventListener('ratechange', observer.onMetadataChanged);
        element.removeEventListener('volumechange', observer.onMetadataChanged);
        element.removeEventListener('timeupdate', observer.onSleepTimerProgress);
        element.removeEventListener('durationchange', observer.onSleepTimerProgress);
        element.removeEventListener('loadedmetadata', observer.onSleepTimerProgress);
        element.removeEventListener('ratechange', observer.onSleepTimerProgress);
        element.removeEventListener('seeking', observer.onSleepTimerProgress);
        element.removeEventListener('seeked', observer.onSleepTimerProgress);
        element.removeEventListener('playing', observer.onSleepTimerProgress);
        element.removeEventListener('waiting', observer.onSleepTimerProgress);
        element.removeEventListener('ended', observer.onNativeAudioEnded, true);
        element.removeEventListener('play', observer.onNativePlaybackRestart);
        clearNativeSleepTimer(observer);
        element.textTracks?.removeEventListener('addtrack', observer.onCaptionTracksChanged);
        element.textTracks?.removeEventListener('removetrack', observer.onCaptionTracksChanged);
        element.textTracks?.removeEventListener('change', observer.onCaptionTracksChanged);
        observer.captionTracks.forEach(track => track.removeEventListener('cuechange', observer.onCaptionCueChanged));
        window.removeEventListener('resize', observer.onCaptionLayoutChanged);
        window.visualViewport?.removeEventListener('resize', observer.onCaptionLayoutChanged);
        element.removeEventListener('enterpictureinpicture', observer.onCaptionLayoutChanged);
        element.removeEventListener('leavepictureinpicture', observer.onCaptionLayoutChanged);
        observer.hostObserver?.disconnect();
        observer.captionNodesObserver?.disconnect();
        setAudioObserver(element, null);
    }

    function shortcutHandlerFor(element) {
        return playerShortcutHandlers ? playerShortcutHandlers.get(element) : element && element.__listenPlaybackShortcutHandler;
    }

    function setShortcutHandler(element, handler) {
        if (!element) return;
        if (playerShortcutHandlers) {
            if (handler) {
                playerShortcutHandlers.set(element, handler);
            } else {
                playerShortcutHandlers.delete(element);
            }
            return;
        }

        if (handler) {
            element.__listenPlaybackShortcutHandler = handler;
        } else {
            delete element.__listenPlaybackShortcutHandler;
        }
    }

    function isEditableShortcutTarget(target) {
        if (!target) return false;
        var tagName = (target.tagName || '').toLowerCase();
        if (tagName === 'input' || tagName === 'textarea' || tagName === 'select') return true;
        if (target.isContentEditable) return true;
        if (target.closest && target.closest('[role="combobox"], [role="listbox"], [role="option"]')) return true;
        return !!(target.closest && target.closest('[contenteditable="true"]'));
    }

    function clearNativeSleepTimer(observer) {
        if (!observer) return;
        if (observer.sleepTimerTimeout) window.clearTimeout(observer.sleepTimerTimeout);
        observer.sleepTimerTimeout = null;
        if (observer.sleepTimerBindingCancel) observer.sleepTimerBindingCancel();
        observer.sleepTimerBindingCancel = null;
        observer.pendingSleepTimer = null;
        observer.sleepTimer = null;
    }

    function cancelPendingSleepTimerBinding(observer) {
        if (!observer) return;
        if (observer.sleepTimerBindingCancel) observer.sleepTimerBindingCancel();
        observer.sleepTimerBindingCancel = null;
        observer.pendingSleepTimer = null;
    }

    function samePlaybackUrl(actual, expected) {
        if (!actual || !expected) return false;
        try { return new URL(actual, document.baseURI).href === new URL(expected, document.baseURI).href; }
        catch (_) { return actual === expected; }
    }

    function audioAssetId(element) {
        return element && (element.dataset.playbackAssetId || element.dataset.currentAssetId || '');
    }

    function audioRequestVersionMatches(element, timer) {
        if (!element || !timer) return false;
        var actual = Number(element.dataset.playbackRequestVersion);
        return Number.isSafeInteger(actual) && actual === Number(timer.playbackRequestVersion);
    }

    function audioSourceMatches(element, timer) {
        if (!element || !timer || !timer.sourceUrl || !audioRequestVersionMatches(element, timer)
            || !samePlaybackUrl(element.dataset.playbackSource, timer.sourceUrl)) return false;
        var hls = element._tuvimaHls;
        if (hls) {
            if (!samePlaybackUrl(hls.url, timer.sourceUrl) || hls.media !== element) return false;
            if (timer.hlsInstance && timer.hlsInstance !== hls) return false;
            if (!timer.hlsInstance) timer.hlsInstance = hls;
            return true;
        }
        if (timer.hlsInstance) return false;
        var current = element.currentSrc || element.src || element.getAttribute('src');
        return !!current && samePlaybackUrl(current, timer.sourceUrl);
    }

    function readAudioPositionForSleepTimer(element, expectedAssetId, expectedRequestVersion, expectedSourceUrl) {
        var observer = audioObserverFor(element);
        var active = observer && observer.sleepTimer;
        var probe = {
            boundAssetId: expectedAssetId,
            playbackRequestVersion: expectedRequestVersion,
            sourceUrl: expectedSourceUrl,
            hlsInstance: active
                && active.boundAssetId === expectedAssetId
                && active.playbackRequestVersion === expectedRequestVersion
                && samePlaybackUrl(active.sourceUrl, expectedSourceUrl)
                ? active.hlsInstance
                : null
        };
        if (audioAssetId(element) !== expectedAssetId || !audioSourceMatches(element, probe)) return null;
        var position = Number(element.currentTime);
        return Number.isFinite(position) && position >= 0 ? position : null;
    }

    function notifyNativeSleepTimerExpired(observer, timer) {
        if (!observer || !timer || timer.fired || observer.sleepTimer !== timer) return;
        var assetMatches = audioAssetId(observer.element) === timer.boundAssetId;
        var sourceMatches = audioSourceMatches(observer.element, timer);
        if (!assetMatches || !sourceMatches) {
            return;
        }
        timer.fired = true;
        if (observer.sleepTimerTimeout) window.clearTimeout(observer.sleepTimerTimeout);
        observer.sleepTimerTimeout = null;
        try { observer.element.pause(); } catch (_) { }
        try {
            var invocation = observer.dotNetRef.invokeMethodAsync('HandleNativeSleepTimerExpired',
                timer.timerGeneration, timer.boundAssetId, timer.playbackRequestVersion,
                Number(observer.element.currentTime) || 0, false);
            if (invocation && typeof invocation.catch === 'function') invocation.catch(function (error) {
                console.debug('Could not report audiobook sleep timer expiry.', error);
            });
        } catch (error) { console.debug('Could not report audiobook sleep timer expiry.', error); }
    }

    function scheduleNativeSleepTimer(observer) {
        if (!observer) return;
        if (observer.sleepTimerTimeout) window.clearTimeout(observer.sleepTimerTimeout);
        observer.sleepTimerTimeout = null;
        var timer = observer.sleepTimer;
        if (!timer || timer.fired) return;
        var element = observer.element;
        if (audioAssetId(element) !== timer.boundAssetId || !audioSourceMatches(element, timer)) return;

        if (timer.mode === 'timer') {
            if (!Number.isFinite(timer.monotonicDeadline)) return;
            var remaining = timer.monotonicDeadline - performance.now();
            observer.sleepTimerTimeout = window.setTimeout(function () {
                // UTC is converted once to a monotonic deadline for this live owner.
                if (performance.now() < timer.monotonicDeadline) scheduleNativeSleepTimer(observer);
                else notifyNativeSleepTimerExpired(observer, timer);
            }, Math.max(0, remaining));
            return;
        }

        if (timer.mode !== 'end-current' && timer.mode !== 'end-next') return;
        if (audioAssetId(element) !== timer.targetAssetId) return;
        var currentTime = Number(element.currentTime);
        var targetEnd = Number(timer.targetEndSeconds);
        if (!Number.isFinite(currentTime) || !Number.isFinite(targetEnd)) return;
        if (currentTime >= targetEnd) {
            notifyNativeSleepTimerExpired(observer, timer);
            return;
        }
        if (element.paused || element.waiting || !(Number(element.playbackRate) > 0)) return;
        var delay = Math.max(0, (targetEnd - currentTime) / Number(element.playbackRate) * 1000);
        observer.sleepTimerTimeout = window.setTimeout(function () {
            // Timers can wake early, and seeks/rate changes can move the boundary.
            if (Number(element.currentTime) < targetEnd) scheduleNativeSleepTimer(observer);
            else notifyNativeSleepTimerExpired(observer, timer);
        }, delay);
    }

    function waitForNativeSleepTimerSource(observer, timer) {
        return new Promise(function (resolve) {
            var done = false;
            var timeout;
            var mutationObserver;
            function finish(success) {
                if (done) return;
                done = true;
                window.clearTimeout(timeout);
                observer.element.removeEventListener('loadedmetadata', check);
                observer.element.removeEventListener('canplay', check);
                observer.element.removeEventListener('error', failed);
                mutationObserver?.disconnect();
                if (observer.sleepTimerBindingCancel === cancel) observer.sleepTimerBindingCancel = null;
                if (!success && observer.pendingSleepTimer === timer) observer.pendingSleepTimer = null;
                resolve(success);
            }
            function check() {
                if (observer.pendingSleepTimer !== timer) return finish(false);
                if (audioAssetId(observer.element) === timer.boundAssetId && audioSourceMatches(observer.element, timer)) {
                    observer.sleepTimer = timer;
                    observer.pendingSleepTimer = null;
                    observer.sleepTimerGeneration = timer.timerGeneration;
                    scheduleNativeSleepTimer(observer);
                    finish(true);
                }
            }
            function failed() { finish(false); }
            function cancel() { finish(false); }
            observer.pendingSleepTimer = timer;
            observer.sleepTimerBindingCancel = cancel;
            observer.element.addEventListener('loadedmetadata', check);
            observer.element.addEventListener('canplay', check);
            observer.element.addEventListener('error', failed);
            if (typeof MutationObserver !== 'undefined') {
                mutationObserver = new MutationObserver(check);
                mutationObserver.observe(observer.element, { attributes: true, attributeFilter: ['data-current-asset-id', 'data-playback-asset-id', 'data-playback-request-version', 'src'] });
            }
            timeout = window.setTimeout(function () { finish(false); }, 12000);
            check();
        });
    }

    function unregisterPlayerShortcuts(element) {
        var handler = shortcutHandlerFor(element);
        if (!element || !handler) return;
        element.removeEventListener('keydown', handler);
        setShortcutHandler(element, null);
    }

    function registerPlayerShortcuts(element, dotNetRef) {
        if (!element || !dotNetRef) return;
        unregisterPlayerShortcuts(element);

        var handler = function (event) {
            if (!event || event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || isEditableShortcutTarget(event.target)) return;

            var action = null;
            if (event.code === 'Space' || event.key === ' ') action = 'toggle-play';
            if (event.key === 'ArrowLeft') action = 'skip-back';
            if (event.key === 'ArrowRight') action = 'skip-forward';
            if (event.key === 'ArrowUp') action = 'volume-up';
            if (event.key === 'ArrowDown') action = 'volume-down';
            if (event.key?.toLowerCase() === 'm') action = 'toggle-mute';
            if (!action) return;

            event.preventDefault();
            dotNetRef.invokeMethodAsync('HandlePlayerShortcut', action)
                .catch(function (error) {
                    console.debug('Could not dispatch playback shortcut.', error);
                });
        };

        element.addEventListener('keydown', handler);
        setShortcutHandler(element, handler);
    }

    function audioEngineElement() {
        var element = document.getElementById('listen-audio-engine');
        if (element) return element;

        try {
            if (window.opener && !window.opener.closed && window.opener.document) {
                return window.opener.document.getElementById('listen-audio-engine');
            }
        } catch (error) {
            console.debug("Could not access opener audio engine.", error);
        }

        return null;
    }

    async function startAudioElement(element, options) {
        if (!element) return false;

        var payload = options || {};
        var streamUrl = payload.streamUrl || payload.StreamUrl || '';
        var positionSeconds = payload.positionSeconds ?? payload.PositionSeconds ?? 0;
        var playbackRate = payload.playbackRate ?? payload.PlaybackRate ?? 1;
        var volume = payload.volume ?? payload.Volume;
        var muted = payload.muted ?? payload.Muted;

        // A later start, pause, release, close or source change raises the token while hls.js is still loading;
        // this start then stands down instead of playing a source the page has already moved on from.
        var startToken = raiseStartToken(element);
        try {
            if (streamUrl && element.dataset.playbackSource !== streamUrl) {
                ensureAudioSource(element, streamUrl);
            }
            if (element._tuvimaSourceReady) {
                await element._tuvimaSourceReady;
                if (element._tuvimaStartToken !== startToken || (streamUrl && element.dataset.playbackSource !== streamUrl)) {
                    // AbortError is the superseded-start result the Dashboard already ignores.
                    element.dataset.playbackStartFailure = "AbortError";
                    return false;
                }
            }

            if (typeof volume === 'number') {
                element.volume = Math.max(0, Math.min(1, volume));
            }

            if (typeof muted === 'boolean') {
                element.muted = muted;
            }

            if (typeof playbackRate === 'number' && isFinite(playbackRate) && playbackRate >= 0.5 && playbackRate <= 3) {
                element.playbackRate = playbackRate;
            }

            var target = Math.max(0, positionSeconds || 0);
            if (target > 0) {
                element.dataset.listenPendingStartPosition = String(target);
                element.dataset.listenPendingStartAt = String(Date.now());
            } else {
                delete element.dataset.listenPendingStartPosition;
                delete element.dataset.listenPendingStartAt;
            }

            var applyTargetSeek = function () {
                if (target <= 0) return;
                try {
                    element.currentTime = target;
                } catch (seekError) {
                    console.debug("Audio start seek was rejected.", seekError);
                }
            };

            if (target > 0 && element.readyState >= 1) {
                applyTargetSeek();
            } else if (target > 0) {
                element.addEventListener('loadedmetadata', applyTargetSeek, { once: true });
            }

            delete element.dataset.playbackStartFailure;
            await element.play();
            scheduleNativeSleepTimer(audioObserverFor(element));
            if (target > 0 && element.readyState >= 1 && Math.abs((element.currentTime || 0) - target) > playbackConfig.seekToleranceSeconds) {
                applyTargetSeek();
            }
            if (target <= 0 || (element.currentTime || 0) >= target - playbackConfig.seekToleranceSeconds) {
                delete element.dataset.listenPendingStartPosition;
                delete element.dataset.listenPendingStartAt;
            }
            return true;
        } catch (error) {
            element.dataset.playbackStartFailure = error.name || "PlaybackError";
            console.debug("Audio start request was rejected.", error);
            return false;
        }
    }

    function clearPreparedCaptions(element) {
        element._tuvimaCaptionAbort?.abort();
        element._tuvimaCaptionAbort = null;
        clearTimeout(element._tuvimaCaptionTimer);
        element.querySelectorAll('track[data-prepared-caption]').forEach(track => track.remove());
    }

    function discoverPreparedCaptions(element, streamUrl) {
        const master = new URL(streamUrl, window.location.href);
        if (master.origin !== window.location.origin) return;
        const controller = new AbortController();
        element._tuvimaCaptionAbort = controller;
        let attempts = 0;
        const poll = async () => {
            if (controller.signal.aborted || element.dataset.playbackSource !== streamUrl) return;
            try {
                const response = await fetch(new URL('captions.json', master), { credentials: 'same-origin', signal: controller.signal });
                if (response.ok) {
                    const captions = await response.json();
                    if (controller.signal.aborted || element.dataset.playbackSource !== streamUrl) return;
                    if (!element._tuvimaHls?.subtitleTracks?.length && Array.isArray(captions)) {
                        for (const caption of captions) {
                            if (!/^s\d+\/caption\.vtt$/.test(caption.path)) continue;
                            const track = document.createElement('track');
                            track.kind = 'subtitles';
                            track.label = caption.language || 'und';
                            track.srclang = caption.language || 'und';
                            track.src = new URL(caption.path, master).href;
                            track.dataset.preparedCaption = 'true';
                            element.appendChild(track);
                        }
                    }
                    return;
                }
                if (response.status === 401 || response.status === 403) return;
            } catch (error) {
                if (controller.signal.aborted) return;
            }
            if (++attempts < 150) element._tuvimaCaptionTimer = setTimeout(poll, 2000);
        };
        element._tuvimaCaptionTimer = setTimeout(poll, 2000);
    }

    // hls.js needs Media Source Extensions. Where the browser has none, the address goes straight to the element
    // (as it did when hls.js reported itself unsupported) and the 620 KB file is never downloaded.
    function browserMayUseHlsJs() {
        return !!(window.MediaSource || window.ManagedMediaSource || window.WebKitMediaSource);
    }

    function setNativeSource(element, streamUrl) {
        if (element.getAttribute('src') !== streamUrl) {
            element.setAttribute('src', streamUrl);
            element.load();
            if (/\.m3u8(?:[?#]|$)/i.test(streamUrl)) discoverPreparedCaptions(element, streamUrl);
        }
    }

    // Loads hls.js once, on first use, then attaches it. The element's source marker was set before the wait, so a
    // newer source (or a release) that changed it while the file loaded wins and this one stops quietly.
    async function attachHlsWhenLoaded(element, streamUrl, sourceEpoch) {
        try {
            await window.tuvimaLazy.loadHls();
        } catch (error) {
            console.error('Video player library failed to load.', error);
            if (element.dataset.playbackSource !== streamUrl || (element._tuvimaSourceEpoch || 0) !== sourceEpoch) return;
            // Drop the marker so the next start with this address tries the load again instead of playing with no source.
            delete element.dataset.playbackSource;
            element.dataset.playbackError = 'Video player failed to load';
            element.dispatchEvent(new Event('error'));
            return;
        }
        // A release, close or cast swap while the file loaded cancels this attach, even when the address is unchanged.
        if (element.dataset.playbackSource !== streamUrl || (element._tuvimaSourceEpoch || 0) !== sourceEpoch || element._tuvimaHls) return;
        if (!window.Hls.isSupported()) {
            setNativeSource(element, streamUrl);
            return;
        }
        const hls = new Hls({ startPosition: 0, enableWorker: true });
        element._tuvimaHls = hls;
        hls.on(Hls.Events.ERROR, (_, data) => {
            if (data.fatal) {
                element.dataset.playbackError = data.details || 'Video stream failed';
                element.dispatchEvent(new Event('error'));
            }
        });
        hls.loadSource(streamUrl);
        hls.attachMedia(element);
        discoverPreparedCaptions(element, streamUrl);
    }

    // The start token is raised by every start, pause, release, close and source swap. The source epoch is raised only
    // by the ones that throw the loading source away (release, close, cast swap), so two starts for one address share a load.
    function raiseStartToken(element) {
        element._tuvimaStartToken = (element._tuvimaStartToken || 0) + 1;
        return element._tuvimaStartToken;
    }

    function invalidateAudioSource(element) {
        if (!element) return;
        raiseStartToken(element);
        element._tuvimaSourceEpoch = (element._tuvimaSourceEpoch || 0) + 1;
    }

    // Release and close: also forget the address being loaded so nothing attaches or plays once the file arrives.
    function cancelPendingAudioSource(element) {
        invalidateAudioSource(element);
        if (element._tuvimaSourceReady) delete element.dataset.playbackSource;
    }

    function ensureAudioSource(element, streamUrl) {
        if (!element || !streamUrl) return Promise.resolve();

        try {
            if (element.dataset.playbackSource === streamUrl) return element._tuvimaSourceReady || Promise.resolve();
            clearPreparedCaptions(element);
            if (element._tuvimaHls) {
                element._tuvimaHls.destroy();
                element._tuvimaHls = null;
            }
            element.dataset.playbackSource = streamUrl;
            if (/\.m3u8(?:[?#]|$)/i.test(streamUrl) && browserMayUseHlsJs()) {
                // Playback starts wait on this: the player is attached only once hls.js has loaded.
                const ready = attachHlsWhenLoaded(element, streamUrl, element._tuvimaSourceEpoch || 0).finally(() => {
                    if (element._tuvimaSourceReady === ready) element._tuvimaSourceReady = null;
                });
                element._tuvimaSourceReady = ready;
                return ready;
            }
            setNativeSource(element, streamUrl);
        } catch (error) {
            console.debug("Audio source sync was rejected.", error);
        }
        return Promise.resolve();
    }

    function numberFromDataset(value, fallback) {
        var parsed = Number.parseFloat(value);
        return Number.isFinite(parsed) ? parsed : fallback;
    }

    function boolFromDataset(value, fallback) {
        if (value === 'true') return true;
        if (value === 'false') return false;
        return fallback;
    }

    function startPositionFromDataset(action, fallback) {
        var position = numberFromDataset(action.dataset.listenStartPosition, fallback || 0);
        if (position > 0) return position;

        if ((action.dataset.listenStartKind || '').toLowerCase() !== 'resume') {
            return Math.max(0, position || 0);
        }

        var progress = numberFromDataset(action.dataset.listenStartProgress, 0);
        var duration = numberFromDataset(action.dataset.listenStartDuration, 0);
        var rewind = numberFromDataset(action.dataset.listenStartRewind, 0);
        if (progress > 0 && progress < 99.5 && duration > 0) {
            return Math.max(0, duration * Math.min(100, progress) / 100 - Math.max(0, rewind));
        }

        return Math.max(0, position || 0);
    }

    function captureAudiobookBookmarkPosition(expectedAssetId, expectedRequestVersion, expectedSourceUrl) {
        var audio = document.getElementById('listen-audio-engine');
        if (!audio || !expectedAssetId || !expectedSourceUrl) return null;
        var currentAssetId = audio.dataset && audio.dataset.currentAssetId;
        if (!currentAssetId || currentAssetId.toLowerCase() !== String(expectedAssetId).toLowerCase()) return null;
        var probe = {
            boundAssetId: expectedAssetId,
            playbackRequestVersion: expectedRequestVersion,
            sourceUrl: expectedSourceUrl,
            hlsInstance: audio._tuvimaHls || null
        };
        if (!audioRequestVersionMatches(audio, probe) || !audioSourceMatches(audio, probe)) return null;
        var position = Number(audio.currentTime);
        if (!Number.isFinite(position) || position < 0) return null;
        var duration = Number(audio.duration);
        return {
            assetId: currentAssetId,
            playbackRequestVersion: Number(expectedRequestVersion),
            sourceVerified: true,
            positionSeconds: position,
            durationSeconds: Number.isFinite(duration) && duration > 0 ? duration : null
        };
    }

    function closeAudioSourceMatches(audio, sourceUrl, requestVersion) {
        return !!sourceUrl && audioSourceMatches(audio, {
            sourceUrl: sourceUrl, playbackRequestVersion: requestVersion, hlsInstance: audio._tuvimaHls || null
        });
    }

    function readCurrentAudiobookPosition(expectedAssetId, expectedRequestVersion, expectedSourceUrl) {
        var audio = document.getElementById('listen-audio-engine');
        return readAudioPositionForSleepTimer(audio, expectedAssetId, expectedRequestVersion, expectedSourceUrl);
    }

    function returnToVideo() {
        try {
            if (!window.opener || window.opener.closed) return false;
            window.opener.focus();
            var videoHost = window.opener.document.querySelector('.video-playback-host.is-expanded');
            if (!videoHost) return false;
            videoHost.focus({ preventScroll: true });
            return true;
        } catch (error) {
            console.debug('Could not return focus to the main video player.', error);
            return false;
        }
    }

    var lastImmediateStartAction = null;
    var lastImmediateStartAt = 0;

    function startAudioFromImmediateAction(target, allowDuplicate) {
        var action = target && target.closest ? target.closest('[data-listen-immediate-start]') : null;
        if (!action || action.disabled || action.getAttribute('aria-disabled') === 'true') return;
        if (action.dataset.listenImmediateStart === 'music') {
            const interactive = target.closest('a,button,input,[role="menuitem"]');
            if (interactive && !interactive.hasAttribute('data-listen-row-play')) return;
            if (!action.dataset.listenStartUrl) return;
        }

        var audio = audioEngineElement();
        if (!audio) return;

        var now = Date.now();
        if (!allowDuplicate && action === lastImmediateStartAction && now - lastImmediateStartAt < playbackConfig.immediateActionDedupMilliseconds) {
            return;
        }

        lastImmediateStartAction = action;
        lastImmediateStartAt = now;

        startAudioElement(audio, {
            streamUrl: action.dataset.listenStartUrl || '',
            positionSeconds: startPositionFromDataset(action, 0),
            playbackRate: numberFromDataset(action.dataset.listenStartRate, 1),
            volume: numberFromDataset(action.dataset.listenStartVolume, undefined),
            muted: boolFromDataset(action.dataset.listenStartMuted, undefined)
        });
    }

    var lastImmediateToggleAction = null;
    var lastImmediateToggleAt = 0;
    var immediateToggleConsumedAt = 0;

    function toggleAudioFromImmediateAction(target, allowDuplicate) {
        var action = target && target.closest ? target.closest('[data-listen-audio-toggle="true"]') : null;
        if (!action || action.disabled || action.getAttribute('aria-disabled') === 'true') return;

        var audio = audioEngineElement();
        if (!audio) return;

        var now = Date.now();
        if (!allowDuplicate && action === lastImmediateToggleAction && now - lastImmediateToggleAt < playbackConfig.immediateActionDedupMilliseconds) {
            return;
        }

        lastImmediateToggleAction = action;
        lastImmediateToggleAt = now;

        if (!audio.paused) {
            audio.pause();
            immediateToggleConsumedAt = now;
            return;
        }

        var start = startAudioElement(audio, {
            streamUrl: action.dataset.listenStartUrl || audio.getAttribute('src') || '',
            positionSeconds: startPositionFromDataset(action, audio.currentTime || 0),
            playbackRate: numberFromDataset(action.dataset.listenStartRate, audio.playbackRate || 1),
            volume: numberFromDataset(action.dataset.listenStartVolume, undefined),
            muted: boolFromDataset(action.dataset.listenStartMuted, undefined)
        });
        if (start && typeof start.then === 'function') {
            start.then(function (started) {
                if (started) {
                    immediateToggleConsumedAt = Date.now();
                }
            });
        }
    }

    var lastImmediateSeekAction = null;
    var lastImmediateSeekAt = 0;
    var immediateSeekConsumedAt = 0;

    function seekAudioFromImmediateAction(target, allowDuplicate) {
        var action = target && target.closest ? target.closest('[data-playback-seek-delta]') : null;
        if (!action || action.disabled || action.getAttribute('aria-disabled') === 'true') return;

        var audio = audioEngineElement();
        if (!audio) return;

        var now = Date.now();
        if (!allowDuplicate && action === lastImmediateSeekAction && now - lastImmediateSeekAt < playbackConfig.immediateActionDedupMilliseconds) {
            return;
        }

        var delta = numberFromDataset(action.dataset.playbackSeekDelta, 0);
        if (!delta) return;

        lastImmediateSeekAction = action;
        lastImmediateSeekAt = now;

        var duration = Number.isFinite(audio.duration) && audio.duration > 0 ? audio.duration : Number.MAX_SAFE_INTEGER;
        var current = Number.isFinite(audio.currentTime) ? audio.currentTime : 0;
        var next = Math.max(0, Math.min(duration, current + delta));
        try {
            audio.currentTime = next;
            immediateSeekConsumedAt = now;
        } catch (error) {
            console.debug("Immediate audio seek was rejected.", error);
        }
    }

    document.addEventListener('click', function (event) {
        startAudioFromImmediateAction(event.target, true);
        toggleAudioFromImmediateAction(event.target, false);
        seekAudioFromImmediateAction(event.target, false);
    }, true);

    if (channel) {
        channel.onmessage = function (event) {
            if (!event || !event.data) return;

            if (event.data.type === 'state') {
                notifyState(event.data.json);
            }

            if (event.data.type === 'command') {
                notifyCommand(event.data.json);
            }
        };
    }

    window.addEventListener('storage', function (event) {
        if (!event) return;

        if (event.key === stateKey) {
            notifyState(event.newValue || '');
        }

        if (event.key === commandKey && event.newValue) {
            notifyCommand(event.newValue);
        }
    });

    return {
        configure: configure,
        hasState: function () {
            return Boolean(window.tuvimaPopupStateSync.getLatestState(stateKey));
        },
        getStateStream: function () {
            var json = window.tuvimaPopupStateSync.getLatestState(stateKey);
            if (!json) {
                throw new Error('Playback state is no longer available.');
            }
            return new Blob([json], { type: 'application/json' });
        },
        getStoredState: function () {
            return localStorage.getItem(stateKey);
        },
        setState: function (json) {
            localStorage.setItem(stateKey, json);
            if (channel) {
                channel.postMessage({ type: 'state', json: json });
            }
        },
        clearState: function () {
            localStorage.removeItem(stateKey);
            if (channel) {
                channel.postMessage({ type: 'state', json: '' });
            }
        },
        sendCommand: function (json) {
            if (!json) return;
            localStorage.setItem(commandKey, json);
            if (channel) {
                channel.postMessage({ type: 'command', json: json });
            }
        },
        registerStateHandler: function (dotNetRef) {
            stateHandler = dotNetRef;
            window.tuvimaPopupStateSync.requestLatestState(function (json) {
                window.listenPlayback.sendCommand(json);
            });
        },
        unregisterStateHandler: function (dotNetRef) {
            if (!dotNetRef || stateHandler === dotNetRef) {
                stateHandler = null;
            }
        },
        registerCommandHandler: function (dotNetRef) {
            commandHandler = dotNetRef;
        },
        unregisterCommandHandler: function (dotNetRef) {
            if (!dotNetRef || commandHandler === dotNetRef) {
                commandHandler = null;
            }
        },
        pauseAudioForClose: function (assetId, requestVersion, sourceUrl, predecessorSourceUrl, pendingSource) {
            var audio = document.getElementById('listen-audio-engine');
            if (!audio || String(audio.dataset.currentAssetId || '').toLowerCase() !== String(assetId || '').toLowerCase()
                || Number(audio.dataset.playbackRequestVersion) !== Number(requestVersion)) return null;
            var sourceMatches = closeAudioSourceMatches(audio, sourceUrl, requestVersion);
            var predecessorMatches = pendingSource && closeAudioSourceMatches(audio, predecessorSourceUrl, requestVersion);
            if (!sourceMatches && !predecessorMatches) return null;
            raiseStartToken(audio);
            audio.pause();
            // The predecessor's position must never become the unresolved current work's resume point.
            return sourceMatches ? captureAudiobookBookmarkPosition(assetId, requestVersion, sourceUrl) : null;
        },
        finalizeAudioClose: function (assetId, requestVersion, sourceUrl, predecessorSourceUrl, pendingSource, profileId) {
            var audio = document.getElementById('listen-audio-engine');
            if (!audio || String(audio.dataset.currentAssetId || '').toLowerCase() !== String(assetId || '').toLowerCase()
                || Number(audio.dataset.playbackRequestVersion) !== Number(requestVersion)) return false;
            var stored;
            try { stored = JSON.parse(localStorage.getItem(stateKey) || 'null'); } catch (_) { stored = null; }
            if (stored) {
                var current = stored.queue && stored.queue[stored.current_index];
                var currentStored = current && String(current.asset_id || '').toLowerCase() === String(assetId || '').toLowerCase()
                    && Number(stored.playback_request_version) === Number(requestVersion);
                var stalePredecessor = pendingSource && Number.isSafeInteger(stored.playback_request_version)
                    && stored.playback_request_version < Number(requestVersion);
                if ((!currentStored && !stalePredecessor)
                    || String(stored.profile_id || '').toLowerCase() !== String(profileId || '').toLowerCase()) return false;
            }
            var sourceMatches = closeAudioSourceMatches(audio, sourceUrl, requestVersion);
            var predecessorMatches = pendingSource && closeAudioSourceMatches(audio, predecessorSourceUrl, requestVersion);
            // Closing while hls.js is still loading: no source exists yet, so cancel the pending attach and start here.
            if (audio._tuvimaSourceReady && (pendingSource || samePlaybackUrl(audio.dataset.playbackSource, sourceUrl))) {
                cancelPendingAudioSource(audio);
            }
            if (audio.currentSrc || audio.getAttribute('src') || audio._tuvimaHls) {
                if (!sourceMatches && !predecessorMatches) {
                    // Cancel the logical pending request without releasing an unverified physical source.
                    if (!pendingSource) return false;
                } else {
                    audio.pause();
                    clearPreparedCaptions(audio);
                    if (audio._tuvimaHls) { audio._tuvimaHls.destroy(); audio._tuvimaHls = null; }
                    delete audio.dataset.playbackSource;
                    audio.removeAttribute('src');
                    audio.load();
                }
            }
            window.listenPlayback.clearState();
            return true;
        },
        captureAudiobookBookmarkPosition: captureAudiobookBookmarkPosition,
        readCurrentAudiobookPosition: readCurrentAudiobookPosition,
        returnToVideo: returnToVideo,
        readAudioState: function (element) {
            return readAudioElementState(element);
        },
        readAudioPositionForSleepTimer: function (element, expectedAssetId, expectedRequestVersion, expectedSourceUrl) {
            return readAudioPositionForSleepTimer(element, expectedAssetId, expectedRequestVersion, expectedSourceUrl);
        },
        registerAudioStateObserver: function (element, dotNetRef, intervalMs) {
            if (!element || !dotNetRef) return;
            removeAudioObserver(element);

            var interval = Math.max(
                playbackConfig.audioObserverMinimumIntervalMilliseconds,
                intervalMs || playbackConfig.audioObserverIntervalMilliseconds);
            var lastNotifiedAt = 0;
            var notify = function (force) {
                var now = Date.now();
                if (!force && now - lastNotifiedAt < interval) {
                    return;
                }

                lastNotifiedAt = now;
                try {
                    var invocation = dotNetRef.invokeMethodAsync('HandleObservedAudioState', readAudioElementState(element));
                    if (invocation && typeof invocation.catch === 'function') {
                        invocation.catch(function (error) {
                            console.debug('Could not report listen audio state.', error);
                        });
                    }
                } catch (error) {
                    console.debug('Could not report listen audio state.', error);
                }
            };
            var observer = {
                element: element,
                dotNetRef: dotNetRef,
                sleepTimer: null,
                sleepTimerTimeout: null,
                sleepTimerGeneration: 0,
                pendingSleepTimer: null,
                lastNativeEndedBinding: null,
                onTimeUpdate: function () { notify(false); },
                onMetadataChanged: function () { notify(true); },
                onSleepTimerProgress: function () { scheduleNativeSleepTimer(observer); },
                onNativePlaybackRestart: function () {
                    observer.lastNativeEndedBinding = null;
                    scheduleNativeSleepTimer(observer);
                },
                onNativeAudioEnded: function (event) {
                    if (element.tagName === 'VIDEO') return; // Video binds its loaded source in video-presentation.js.
                    var currentTime = Number(element.currentTime);
                    var duration = Number(element.duration);
                    var assetId = audioAssetId(element);
                    var requestVersion = Number(element.dataset.playbackRequestVersion);
                    var timer = observer.sleepTimer;
                    var sourceUrl = element.dataset.playbackSource;
                    var probe = timer || {
                        sourceUrl: sourceUrl,
                        playbackRequestVersion: requestVersion,
                        hlsInstance: element._tuvimaHls || null
                    };
                    var currentSource = element.currentSrc || element.src || element.getAttribute('src');
                    if (element.ended !== true || !assetId || !Number.isSafeInteger(requestVersion)
                        || !Number.isFinite(currentTime) || currentTime < 0
                        || !Number.isFinite(duration) || duration <= 0 || !currentSource
                        || !audioRequestVersionMatches(element, probe)
                        || !audioSourceMatches(element, probe)) return;

                    var timerGeneration = timer ? Number(timer.timerGeneration) : observer.sleepTimerGeneration;
                    var bindingKey = `${assetId}|${requestVersion}|${timerGeneration}|${currentSource}`;
                    if (observer.lastNativeEndedBinding === bindingKey) return;
                    observer.lastNativeEndedBinding = bindingKey;

                    var capturedTargetEnded = !!timer
                        && (timer.mode === 'end-current' || timer.mode === 'end-next')
                        && assetId === timer.targetAssetId
                        && assetId === timer.boundAssetId
                        && requestVersion === Number(timer.playbackRequestVersion)
                        && audioSourceMatches(element, timer);
                    if (capturedTargetEnded) {
                        // The actual native ended signal is authoritative even when the
                        // container's authored endpoint is rounded beyond media duration.
                        event.preventDefault();
                        event.stopImmediatePropagation();
                        if (!element.paused) element.pause();
                        try {
                            var expiry = observer.dotNetRef.invokeMethodAsync('HandleNativeSleepTimerExpired',
                                timer.timerGeneration, assetId, requestVersion, currentTime, true);
                            if (expiry && typeof expiry.catch === 'function') expiry.catch(function () { });
                        } catch (_) { }
                        return;
                    }

                    try {
                        var completion = observer.dotNetRef.invokeMethodAsync('HandleNativeAudioEnded',
                            assetId, requestVersion, timerGeneration, currentTime);
                        if (completion && typeof completion.catch === 'function') completion.catch(function () { });
                    } catch (_) { }
                },
                captionTracks: new Set(),
                onCaptionLayoutChanged: function () { observer.onCaptionCueChanged(); },
                onCaptionCueChanged: function () { notify(true); },
                onCaptionTracksChanged: function () {
                    synchronizeCaptionSelection(element);
                    for (const track of Array.from(element.textTracks || [])) {
                        if (track.kind !== 'captions' && track.kind !== 'subtitles') continue;
                        if (!observer.captionTracks.has(track)) {
                            observer.captionTracks.add(track);
                            track.addEventListener('cuechange', observer.onCaptionCueChanged);
                        }
                    }
                    observer.onCaptionCueChanged();
                    notify(true);
                }
            };

            element.addEventListener('timeupdate', observer.onTimeUpdate);
            element.addEventListener('durationchange', observer.onMetadataChanged);
            element.addEventListener('loadedmetadata', observer.onMetadataChanged);
            element.addEventListener('ratechange', observer.onMetadataChanged);
            element.addEventListener('volumechange', observer.onMetadataChanged);
            element.addEventListener('timeupdate', observer.onSleepTimerProgress);
            element.addEventListener('durationchange', observer.onSleepTimerProgress);
            element.addEventListener('loadedmetadata', observer.onSleepTimerProgress);
            element.addEventListener('ratechange', observer.onSleepTimerProgress);
            element.addEventListener('seeking', observer.onSleepTimerProgress);
            element.addEventListener('seeked', observer.onSleepTimerProgress);
            element.addEventListener('playing', observer.onSleepTimerProgress);
            element.addEventListener('waiting', observer.onSleepTimerProgress);
            element.addEventListener('ended', observer.onNativeAudioEnded, true);
            element.addEventListener('play', observer.onNativePlaybackRestart);
            element.textTracks?.addEventListener('addtrack', observer.onCaptionTracksChanged);
            element.textTracks?.addEventListener('removetrack', observer.onCaptionTracksChanged);
            element.textTracks?.addEventListener('change', observer.onCaptionTracksChanged);
            window.addEventListener('resize', observer.onCaptionLayoutChanged);
            window.visualViewport?.addEventListener('resize', observer.onCaptionLayoutChanged);
            element.addEventListener('enterpictureinpicture', observer.onCaptionLayoutChanged);
            element.addEventListener('leavepictureinpicture', observer.onCaptionLayoutChanged);
            var host = element.closest('.video-playback-host');
            if (host && typeof MutationObserver !== 'undefined') {
                observer.hostObserver = new MutationObserver(observer.onCaptionLayoutChanged);
                observer.hostObserver.observe(host, { attributes: true, attributeFilter: ['class'] });
                observer.captionNodesObserver = new MutationObserver(observer.onCaptionTracksChanged);
                observer.captionNodesObserver.observe(element, { childList: true, subtree: true, attributes: true,
                    attributeFilter: ['data-playback-preferred', 'default', 'data-playback-caption-default'] });
            }
            setAudioObserver(element, observer);
            observer.onCaptionTracksChanged();
        },
        unregisterAudioStateObserver: function (element) {
            removeAudioObserver(element);
        },
        setAudiobookSleepTimer: async function (element, state, sourceUrl) {
            var observer = audioObserverFor(element);
            if (!observer) return false;
            var generation = Number(state && state.timerGeneration);
            if (!Number.isFinite(generation) || generation < observer.sleepTimerGeneration) return false;
            if (!state || state.mode === 'off') {
                var activeTimer = observer.sleepTimer;
                var offAssetId = typeof state.boundAssetId === 'string' ? state.boundAssetId.replace(/-/g, '').toLowerCase() : '';
                var hasOffAssetScope = offAssetId.length === 32 && offAssetId !== '00000000000000000000000000000000';
                var offRequestVersion = Number(state.playbackRequestVersion);
                var hasOffRequestScope = Number.isFinite(offRequestVersion) && offRequestVersion > 0;
                if (activeTimer
                    && ((hasOffAssetScope && activeTimer.boundAssetId !== state.boundAssetId)
                        || (hasOffRequestScope && activeTimer.playbackRequestVersion !== offRequestVersion))) return false;
                if (observer.sleepTimerTimeout) window.clearTimeout(observer.sleepTimerTimeout);
                observer.sleepTimerTimeout = null;
                cancelPendingSleepTimerBinding(observer);
                observer.sleepTimer = null;
                observer.sleepTimerGeneration = generation;
                return true;
            }
            if (!sourceUrl) return false;
            var oldTimer = observer.sleepTimer;
            if (oldTimer && generation === oldTimer.timerGeneration) {
                // A generation identifies one immutable user choice. Only its live source
                // binding may move as the same audiobook advances between authorized assets.
                // A same-generation retry cannot silently replace the captured target/deadline.
                var sameChoice = oldTimer.mode === state.mode
                    && oldTimer.timerSessionId === state.timerSessionId
                    && oldTimer.profileId === state.profileId
                    && oldTimer.workId === state.workId
                    && oldTimer.originAssetId === state.originAssetId
                    && oldTimer.originChapterIndex === state.originChapterIndex
                    && oldTimer.targetAssetId === state.targetAssetId
                    && oldTimer.targetChapterIndex === state.targetChapterIndex
                    && oldTimer.targetChapterTitle === state.targetChapterTitle
                    && oldTimer.targetEndSeconds === state.targetEndSeconds
                    && oldTimer.chosenMinutes === state.chosenMinutes
                    && oldTimer.deadlineUtc === state.deadlineUtc;
                if (!sameChoice) return false;
            }
            cancelPendingSleepTimerBinding(observer);
            var deadlineUtc = Date.parse(state.deadlineUtc || '');
            var monotonicDeadline = null;
            if (state.mode === 'timer') {
                if (!Number.isFinite(deadlineUtc)) return false;
                monotonicDeadline = oldTimer
                    && oldTimer.timerSessionId === state.timerSessionId
                    && oldTimer.deadlineUtc === state.deadlineUtc
                    && Number.isFinite(oldTimer.monotonicDeadline)
                    ? oldTimer.monotonicDeadline
                    : performance.now() + Math.max(0, deadlineUtc - Date.now());
            }
            var candidate = {
                mode: state.mode,
                timerGeneration: state.timerGeneration,
                timerSessionId: state.timerSessionId,
                profileId: state.profileId,
                workId: state.workId,
                originAssetId: state.originAssetId,
                originChapterIndex: state.originChapterIndex,
                boundAssetId: state.boundAssetId,
                targetAssetId: state.targetAssetId,
                targetChapterIndex: state.targetChapterIndex,
                targetChapterTitle: state.targetChapterTitle,
                targetEndSeconds: state.targetEndSeconds,
                chosenMinutes: state.chosenMinutes,
                deadlineUtc: state.deadlineUtc,
                playbackRequestVersion: state.playbackRequestVersion,
                sourceUrl: sourceUrl,
                hlsInstance: null,
                monotonicDeadline: monotonicDeadline,
                fired: false
            };
            var registered = await waitForNativeSleepTimerSource(observer, candidate);
            return registered;
        },
        clearAudiobookSleepTimer: function (element) {
            var observer = audioObserverFor(element);
            if (observer) clearNativeSleepTimer(observer);
        },
        pauseAudioForSleepTimer: function (element, timerGeneration, assetId, playbackRequestVersion) {
            var observer = audioObserverFor(element);
            var timer = observer && observer.sleepTimer;
            if (!timer
                || timer.timerGeneration !== timerGeneration
                || timer.boundAssetId !== assetId
                || timer.playbackRequestVersion !== playbackRequestVersion
                || audioAssetId(element) !== assetId
                || !audioSourceMatches(element, timer)) return false;
            // Native expiry already paused this exact source before notifying .NET. A
            // concurrent scoped pause request may acknowledge that pause, but must not
            // pause again or mistake a replaced source for the original one.
            if (timer.fired) return element.paused === true;
            try { element.pause(); return true; }
            catch (_) { return false; }
        },
        registerPlayerShortcuts: registerPlayerShortcuts,
        unregisterPlayerShortcuts: unregisterPlayerShortcuts,
        loadAudio: function (element) {
            if (!element) return;
            try {
                element.load();
            } catch (error) {
                console.debug("Audio load was rejected.", error);
            }
        },
        ensureAudioSource: ensureAudioSource,
        invalidateAudioSource: invalidateAudioSource,
        consumeImmediateToggleHandled: function () {
            var now = Date.now();
            if (immediateToggleConsumedAt && now - immediateToggleConsumedAt < playbackConfig.immediateActionConsumeMilliseconds) {
                immediateToggleConsumedAt = 0;
                return true;
            }

            return false;
        },
        consumeImmediateSeekHandled: function () {
            var now = Date.now();
            if (immediateSeekConsumedAt && now - immediateSeekConsumedAt < playbackConfig.immediateActionConsumeMilliseconds) {
                immediateSeekConsumedAt = 0;
                return true;
            }

            return false;
        },
        playAudio: async function (element) {
            if (!element) return false;

            try {
                delete element.dataset.playbackStartFailure;
            await element.play();
                return true;
            } catch (error) {
                element.dataset.playbackStartFailure = error.name || "PlaybackError";
                console.debug("Audio play request was rejected.", error);
                return false;
            }
        },
        startAudio: startAudioElement,
        startFailure: function (element) { return element?.dataset.playbackStartFailure || "PlaybackError"; },
        pauseAudio: function (element) {
            if (!element) return;
            raiseStartToken(element);
            element.pause();
        },
        releaseMedia: function (element, expectedAssetId, expectedRequestVersion) {
            if (!element) return false;
            if (expectedAssetId && (audioAssetId(element)?.toLowerCase() !== String(expectedAssetId).toLowerCase()
                || Number(element.dataset.playbackRequestVersion) !== Number(expectedRequestVersion))) return false;
            cancelPendingAudioSource(element);
            element.pause();
            clearPreparedCaptions(element);
            element._tuvimaHls?.destroy();
            element._tuvimaHls = null;
            delete element.dataset.playbackSource;
            element.removeAttribute('src');
            element.load();
            delete element._tuvimaVideoBinding;
            return true;
        },
        seekAudio: function (element, seconds) {
            if (!element) return;
            var target = Math.max(0, seconds || 0);
            try {
                element.currentTime = target;
            } catch (error) {
                var applyWhenReady = function () {
                    try {
                        element.currentTime = target;
                    } catch (innerError) {
                        console.debug("Audio seek was rejected.", innerError);
                    }
                };
                element.addEventListener('loadedmetadata', applyWhenReady, { once: true });
            }
        },
        setVolume: function (element, volume) {
            if (!element) return;
            var next = Math.max(0, Math.min(1, volume || 0));
            element.volume = next;
        },
        setMuted: function (element, muted) {
            if (!element) return;
            element.muted = !!muted;
        },
        setPlaybackRate: function (element, rate) {
            if (!element) return;
            if (typeof rate !== 'number' || !isFinite(rate) || rate < 0.5 || rate > 3) return;
            try {
                element.playbackRate = rate;
            } catch (error) {
                console.debug("Audio playback rate was rejected.", error);
            }
        },
        toggleCaptions: function (element) {
            if (element?._tuvimaHls?.subtitleTracks?.length) {
                const hls = element._tuvimaHls;
                hls.subtitleTrack = hls.subtitleTrack >= 0 ? -1 : 0;
                hls.subtitleDisplay = hls.subtitleTrack >= 0;
                return hls.subtitleDisplay;
            }
            if (!element || !element.textTracks || element.textTracks.length === 0) return false;
            var shouldShow = true;
            for (var index = 0; index < element.textTracks.length; index++) {
                if (element.textTracks[index].mode === 'showing') {
                    shouldShow = false;
                    break;
                }
            }
            for (var trackIndex = 0; trackIndex < element.textTracks.length; trackIndex++) {
                element.textTracks[trackIndex].mode = shouldShow && trackIndex === 0 ? 'showing' : 'disabled';
            }
            return shouldShow;
        },
        selectCaptionTrack: selectCaptionTrack,
        synchronizeCaptionSelection: synchronizeCaptionSelection,
        selectAudioTrack: function (element, selectedIndex) {
            if (element?._tuvimaHls) {
                const hls = element._tuvimaHls;
                if (selectedIndex < 0 || selectedIndex >= hls.audioTracks.length) return false;
                hls.audioTrack = selectedIndex;
                return true;
            }
            if (!element || !element.audioTracks || element.audioTracks.length === 0) return false;
            var index = Math.max(0, Math.min(element.audioTracks.length - 1, selectedIndex || 0));
            for (var trackIndex = 0; trackIndex < element.audioTracks.length; trackIndex++) {
                element.audioTracks[trackIndex].enabled = trackIndex === index;
            }
            return true;
        },
        toggleFullscreen: async function (element, fallbackVideo) {
            if (!element) return false;
            try {
                if (document.fullscreenElement) {
                    await document.exitFullscreen();
                    return false;
                }
                if (document.webkitFullscreenElement && typeof document.webkitExitFullscreen === 'function') {
                    document.webkitExitFullscreen();
                    return false;
                }
                if (typeof element.requestFullscreen === 'function') {
                    await element.requestFullscreen();
                    return true;
                }
                if (typeof element.webkitRequestFullscreen === 'function') {
                    element.webkitRequestFullscreen();
                    return true;
                }
                const video = fallbackVideo || (element.matches?.('video') ? element : element.querySelector?.('video'));
                if (video?.webkitDisplayingFullscreen && typeof video.webkitExitFullscreen === 'function') {
                    video.webkitExitFullscreen();
                    return false;
                }
                if (typeof video?.webkitEnterFullscreen === 'function') {
                    video.webkitEnterFullscreen();
                    return true;
                }
            } catch (error) {
                console.debug("Video fullscreen request was rejected.", error);
            }
            return false;
        },
        togglePictureInPicture: async function (element) {
            if (!element || !document.pictureInPictureEnabled) return false;
            try {
                if (document.pictureInPictureElement) {
                    await document.exitPictureInPicture();
                    return false;
                }
                if (typeof element.requestPictureInPicture === 'function') {
                    await element.requestPictureInPicture();
                    return true;
                }
            } catch (error) {
                console.debug("Video picture-in-picture request was rejected.", error);
            }
            return false;
        },
        canPictureInPicture: function (element) {
            return !!element && !!document.pictureInPictureEnabled && typeof element.requestPictureInPicture === 'function';
        },
        exitPictureInPicture: async function () {
            if (document.pictureInPictureElement) await document.exitPictureInPicture();
        }
    };
})();

window.playbackTools = window.playbackTools || {
    dockObserver: null,
    dockObserverOwnerKey: null,
    resetContentScroll: function () {
        const content = document.querySelector('.playback-app-frame__content');
        if (content) content.scrollTop = 0;
    },
    isEditableFocus: function () {
        const active = document.activeElement;
        return active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement ||
            active instanceof HTMLSelectElement || !!active?.isContentEditable;
    },
    playerReturnFocus: null,
    capturePlayerFocus: function (element) {
        this.playerReturnFocus = document.activeElement;
        if (element instanceof HTMLElement) element.focus();
    },
    restorePlayerFocus: function () {
        const target = this.playerReturnFocus;
        this.playerReturnFocus = null;
        if (target instanceof HTMLElement && target.isConnected) target.focus();
    },
    observeDock: function (element, ownerKey) {
        this.disconnectDock();
        this.dockObserverOwnerKey = ownerKey || {};
        if (!(element instanceof HTMLElement)) return;
        const update = () => {
            const height = Math.max(0, element.getBoundingClientRect().height);
            document.documentElement.style.setProperty('--tl-audio-dock-height', `${height}px`);
        };
        this.dockObserver = new ResizeObserver(update);
        this.dockObserver.observe(element);
        update();
    },
    disconnectDock: function (ownerKey) {
        if (ownerKey !== undefined && ownerKey !== this.dockObserverOwnerKey) return false;
        this.dockObserver?.disconnect();
        this.dockObserver = null;
        this.dockObserverOwnerKey = null;
        document.documentElement.style.setProperty('--tl-audio-dock-height', '0px');
        return true;
    },
    scrollActiveChapter: function () {
        window.requestAnimationFrame(() => {
            const active = document.querySelector('[data-playback-active-chapter="true"]');
            if (active instanceof HTMLElement) {
                active.scrollIntoView({ block: 'center', behavior: 'smooth' });
            }
        });
    }
};
