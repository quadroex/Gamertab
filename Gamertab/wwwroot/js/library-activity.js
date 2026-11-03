const activity = document.querySelector('[data-activity]');
if (activity) {
    const select = activity.querySelector('[data-activity-mode]');
    const status = activity.querySelector('[data-activity-status]');
    const items = activity.querySelector('[data-activity-items]');
    const metrics = activity.querySelector('[data-activity-metrics]');
    let cursor = 0;
    let generation = 0;
    let socket;
    let hub;
    let timer;
    let abort;
    let requests = 0;
    let responses = 0;
    let bytes = 0;
    let interval = 1000;
    let fetching = false;
    let queued = false;
    const updateMetrics = () => { metrics.textContent = `${requests} ${requests === 1 ? 'fetch' : 'fetches'} · ${responses} ${responses === 1 ? 'payload' : 'payloads'} · ${bytes} bytes received`; };
    const reset = () => { requests = responses = bytes = 0; updateMetrics(); };
    const apply = batch => {
        batch.events.forEach(event => {
            if (items.querySelector(`[data-event-id="${event.id}"]`)) return;
            const card = document.createElement('article');
            card.className = 'content-panel';
            card.dataset.eventId = event.id;
            const heading = document.createElement('h2');
            const link = document.createElement('a');
            link.href = '/Games/Details/' + event.gameId;
            link.textContent = event.gameName;
            heading.append(link);
            const text = document.createElement('p');
            text.textContent = event.action === 'added' ? 'Added to your library' : event.action === 'removed' ? 'Removed from your library' : 'Library entry updated';
            const detail = document.createElement('p');
            detail.className = 'muted';
            detail.textContent = (event.status === 'OnHold' ? 'On hold' : event.status) + ' · ' + new Date(event.createdUtc).toLocaleString();
            card.append(heading, text, detail);
            items.prepend(card);
        });
        while (items.children.length > 100) items.lastElementChild.remove();
        cursor = Math.max(cursor, batch.cursor);
        activity.querySelector('[data-activity-empty]').hidden = items.children.length > 0;
        updateMetrics();
    };
    const receive = text => {
        responses++;
        bytes += new TextEncoder().encode(text).length;
        const batch = JSON.parse(text);
        apply(batch);
        return batch;
    };
    const fetchChanges = async (current, wait = 0) => {
        if (fetching) { queued = true; return null; }
        fetching = true;
        try {
            requests++;
            updateMetrics();
            const response = await fetch(`/api/activity?after=${cursor}&wait=${wait}`, { cache: 'no-store', signal: abort.signal });
            if (current !== generation) return null;
            if (!response.ok) throw new Error(response.status === 401 ? 'Sign in again to see your activity' : 'Connection interrupted — retrying');
            const text = await response.text();
            if (current !== generation) return null;
            const batch = receive(text);
            status.textContent = 'Live updates connected';
            if (batch.hasMore) queued = true;
            return batch;
        } finally {
            if (current === generation) {
                fetching = false;
                if (queued && (select.value === 'signalr' || select.value === 'websocket')) {
                    queued = false;
                    fetchChanges(current).catch(error => { if (current === generation) status.textContent = error.message; });
                }
            }
        }
    };
    const poll = async current => {
        if (current !== generation) return;
        try {
            const batch = await fetchChanges(current, select.value === 'longpoll' ? 20 : 0);
            if (current !== generation) return;
            interval = batch?.events.length ? 1000 : Math.min(interval * 2, 10000);
            const delay = batch?.hasMore || select.value === 'longpoll' ? 0 : interval;
            queued = false;
            timer = setTimeout(() => poll(current), delay);
        } catch (error) {
            if (current !== generation) return;
            status.textContent = error.message;
            timer = setTimeout(() => poll(current), 5000);
        }
    };
    const connectSocket = current => {
        if (current !== generation) return;
        const url = new URL('/api/activity/socket', location.origin);
        url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        url.searchParams.set('after', cursor);
        const connection = new WebSocket(url);
        socket = connection;
        connection.onopen = () => { if (current === generation) status.textContent = 'Live updates connected'; };
        connection.onmessage = event => { if (current === generation) receive(event.data); };
        connection.onclose = () => {
            if (current !== generation) return;
            status.textContent = 'Reconnecting…';
            timer = setTimeout(() => connectSocket(current), 3000);
        };
    };
    const connectHub = async current => {
        if (current !== generation) return;
        const connection = new signalR.HubConnectionBuilder().withUrl('/hubs/activity').withAutomaticReconnect().build();
        hub = connection;
        const refresh = () => fetchChanges(current).catch(error => { if (current === generation) status.textContent = error.message; });
        connection.on('LibraryChanged', refresh);
        connection.onreconnected(refresh);
        connection.onreconnecting(() => { if (current === generation) status.textContent = 'Reconnecting…'; });
        connection.onclose(() => {
            if (current !== generation) return;
            status.textContent = 'Reconnecting…';
            timer = setTimeout(() => connectHub(current), 3000);
        });
        try {
            await connection.start();
            if (current !== generation) { await connection.stop(); return; }
            await refresh();
        } catch {
            if (current !== generation) return;
            status.textContent = 'Connection unavailable — retrying';
            timer = setTimeout(() => connectHub(current), 5000);
        }
    };
    const stop = () => {
        generation++;
        clearTimeout(timer);
        abort?.abort();
        socket?.close();
        hub?.stop();
        socket = hub = null;
        fetching = queued = false;
    };
    const start = () => {
        stop();
        abort = new AbortController();
        interval = 1000;
        reset();
        status.textContent = 'Connecting…';
        if (select.value === 'websocket') connectSocket(generation);
        else if (select.value === 'signalr') connectHub(generation);
        else poll(generation);
    };
    select.addEventListener('change', start);
    activity.querySelector('[data-activity-reset]').addEventListener('click', reset);
    window.addEventListener('pagehide', stop);
    window.addEventListener('pageshow', event => { if (event.persisted) start(); });
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden && (select.value === 'signalr' || select.value === 'websocket'))
            fetchChanges(generation).catch(error => { status.textContent = error.message; });
    });
    start();
}
