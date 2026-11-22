'use strict';

const t=(value)=>globalThis.suitoText?.(value) ?? value;
const hostOrigin = 'https://suitoride.local';
const statusLabel = document.getElementById('status');
const scene = document.getElementById('scene');
let apiKey, library, loading = false, failed = false;
let map, marker, routeLine, riderName = '';
let points = [], target, displayed, frame;
const tilt = 58;
const range = 290;

function showStatus(text) {
    statusLabel.textContent = text;
    statusLabel.hidden = text === '';
}

function showError(text) {
    failed = true;
    cancelAnimationFrame(frame);
    frame = undefined;
    showStatus(text);
}

function loadGoogleLibrary(key) {
    return new Promise((resolve, reject) => {
        const script = document.createElement('script');
        const timeout = setTimeout(() => finish(new Error('timeout')), 25000);
        const callbackName = 'suitoGoogle3DLoaded';
        function finish(error, result) {
            clearTimeout(timeout);
            window[callbackName] = () => {};
            if (error) {
                script.remove();
                reject(error);
            } else {
                resolve(result);
            }
        }
        window.gm_authFailure = () => {
            showError(t('Google 3D odmítl klíč. Zkontroluj Maps JavaScript API, fakturaci a omezení klíče.'));
            finish(new Error('authorization'));
        };
        window[callbackName] = async () => {
            try { finish(null, await google.maps.importLibrary('maps3d')); }
            catch (error) { finish(error); }
        };
        script.onerror = () => finish(new Error('network'));
        const params = new URLSearchParams({
            key, v: 'weekly', loading: 'async', callback: callbackName, language: document.documentElement?.lang || 'en'
        });
        script.src = 'https://maps.googleapis.com/maps/api/js?' + params.toString();
        script.referrerPolicy = 'strict-origin-when-cross-origin';
        document.head.append(script);
    });
}

async function ensureMap() {
    if (map || loading || failed || !target || !apiKey) return;
    loading = true;
    showStatus(t('Načítám Google 3D…'));
    try {
        library = await loadGoogleLibrary(apiKey);
        if (failed) return;
        map = new library.Map3DElement({
            center: centerOf(target), heading: target.heading,
            tilt, range, mode: 'SATELLITE', defaultUIHidden: true,
            description: t('Letecký pohled ve směru virtuální jízdy')
        });
        map.addEventListener('gmp-error', () => showError(t('Google 3D se nepodařilo vykreslit. Zkontroluj připojení a podporu WebGL.')));
        marker = new library.Marker3DElement({
            position: {lat: target.lat, lng: target.lon},
            altitudeMode: 'CLAMP_TO_GROUND',
            label: riderName || t('Jezdec')
        });
        scene.append(map);
        map.append(marker);
        drawRoute();
        render(target);
        showStatus('');
    } catch {
        if (!failed) showError(t('Google 3D se nepodařilo načíst. Zkontroluj klíč, fakturaci nebo připojení.'));
    } finally {
        loading = false;
    }
}

function drawRoute() {
    if (!map || failed) return;
    routeLine?.remove();
    routeLine = undefined;
    if (points.length < 2) return;
    routeLine = new library.Polyline3DElement({
        path: points.map(([lat, lng]) => ({lat, lng})),
        altitudeMode: 'CLAMP_TO_GROUND',
        strokeColor: '#20d9ac', strokeWidth: 7,
        outerColor: '#102530', outerWidth: 0.3, drawsOccludedSegments: true
    });
    map.append(routeLine);
}

function centerOf(position) {
    return {lat: position.lat, lng: position.lon, altitude: position.altitude + 3};
}

function render(position) {
    displayed = position;
    map.center = centerOf(position);
    map.heading = position.heading;
    marker.position = {lat: position.lat, lng: position.lon};
}

function angleDifference(from, to) {
    return ((to - from + 540) % 360) - 180;
}

function moveTo(next) {
    const previous = target;
    target = next;
    if (!map) { void ensureMap(); return; }
    if (failed) return;
    cancelAnimationFrame(frame);
    frame = undefined;
    if (!displayed || !previous || next.distance < previous.distance || next.distance - previous.distance > 100) {
        render(next);
        return;
    }

    const start = displayed;
    const longitudeChange = angleDifference(start.lon, next.lon);
    const headingChange = angleDifference(start.heading, next.heading);
    const startTime = performance.now();
    function animate(now) {
        const t = Math.max(0, Math.min(1, (now - startTime) / 1000));
        const mix = (a, b) => a + (b - a) * t;
        render({
            lat: mix(start.lat, next.lat),
            lon: ((start.lon + longitudeChange * t + 540) % 360) - 180,
            altitude: mix(start.altitude, next.altitude),
            heading: (start.heading + headingChange * t + 360) % 360
        });
        frame = t < 1 ? requestAnimationFrame(animate) : undefined;
    }
    frame = requestAnimationFrame(animate);
}

window.addEventListener('message', event => {
    if (event.source !== window.parent || event.origin !== hostOrigin) return;
    const message = event.data;
    if (message.type === 'init') {
        apiKey = message.key;
        riderName = message.riderName || '';
        points = message.points;
        if (message.position) moveTo(message.position);
    } else if (message.type === 'rider-name') {
        riderName = message.name;
        if (marker) marker.label = riderName || t('Jezdec');
    } else if (message.type === 'route') {
        points = message.points;
        target = displayed = undefined;
        cancelAnimationFrame(frame);
        frame = undefined;
        drawRoute();
    } else if (message.type === 'position') {
        moveTo(message.position);
    }
});
