const journal = document.querySelector('[data-journal]');
if (journal) {
    const board = journal.querySelector('[data-blocks]');
    const template = journal.querySelector('[data-block-template]');
    const status = journal.querySelector('[data-save-status]');
    const error = journal.querySelector('[data-journal-error]');
    const preview = journal.querySelector('[data-preview]');
    const rendered = journal.querySelector('[data-rendered]');
    const source = journal.querySelector('[data-markdown]');
    const remoteNotice = journal.querySelector('[data-remote-notice]');
    const key = journal.dataset.storageKey;
    const types = ['paragraph', 'heading', 'list'];
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    let blocks = [{ id: crypto.randomUUID(), type: 'paragraph', text: '' }];
    const clientId = crypto.randomUUID();
    const draftKey = key + ':draft';
    const endpoint = '/api/journals/' + journal.dataset.gameId;
    const csrf = journal.querySelector('input[name="__RequestVerificationToken"]').value;
    let revision = 0;
    let pending = false;
    let ready = false;
    let syncing = false;
    let conflict = null;
    let edits = 0;
    let timer;
    let draggedId = null;
    let storageReady = true;
    let storageFailed = false;
    let legacyIds = false;
    let receivedSnapshot = false;
    let refreshing = false;
    let refreshQueued = false;
    const highlights = new WeakMap();
    const movements = new WeakMap();

    const showError = message => {
        error.textContent = message;
        error.hidden = false;
    };
    const documentBlocks = () => blocks.map(({ id, type, text }) => ({ id, type, text }));
    const sameBlocks = other => blocks.length === other.length && blocks.every((block, index) =>
        block.type === other[index].type && block.text === other[index].text && (legacyIds || block.id === other[index].id));
    const validId = id => typeof id === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id) &&
        id !== '00000000-0000-0000-0000-000000000000';
    try {
        const saved = sessionStorage.getItem(draftKey) ?? localStorage.getItem(key);
        if (saved !== null) {
            const savedDocument = JSON.parse(saved);
            if (![1, 2, 3].includes(savedDocument.version) || !Array.isArray(savedDocument.blocks) || savedDocument.blocks.length > 100 ||
                !savedDocument.blocks.every(block => types.includes(block.type) && typeof block.text === 'string' && block.text.length <= 10000) ||
                (savedDocument.version >= 2 && (!Number.isInteger(savedDocument.revision) || savedDocument.revision < 0 || typeof savedDocument.pending !== 'boolean')) ||
                (savedDocument.version === 3 && (!savedDocument.blocks.every(block => validId(block.id)) ||
                    new Set(savedDocument.blocks.map(block => block.id.toLowerCase())).size !== savedDocument.blocks.length))) {
                throw new Error('Invalid journal');
            }
            legacyIds = savedDocument.version < 3 || savedDocument.legacyIds === true;
            blocks = savedDocument.blocks.map(block => ({ id: savedDocument.version === 3 ? block.id.toLowerCase() : crypto.randomUUID(), type: block.type, text: block.text }));
            revision = savedDocument.revision ?? 0;
            pending = savedDocument.version === 1 || savedDocument.pending;
        }
    } catch {
        storageReady = false;
        showError('The local journal could not be read. Its stored copy has been kept. You can still use the server copy and export Markdown.');
    }
    status.textContent = 'Connecting…';

    const markdown = () => blocks.map(block => {
        if (block.type === 'heading') return '# ' + block.text.replace(/\r?\n/g, ' ');
        if (block.type === 'list') return block.text.split(/\r?\n/).map(line => '- ' + line).join('\n');
        return block.text;
    }).join('\n\n');

    const updatePreview = () => {
        source.textContent = markdown();
        rendered.replaceChildren();
        blocks.forEach(block => {
            if (!block.text.trim()) return;
            const element = document.createElement(block.type === 'heading' ? 'h3' : block.type === 'list' ? 'ul' : 'p');
            if (block.type === 'list') {
                block.text.split(/\r?\n/).forEach(line => {
                    const item = document.createElement('li');
                    item.textContent = line;
                    element.append(item);
                });
            } else {
                element.textContent = block.text;
            }
            rendered.append(element);
        });
    };

    const saveLocal = () => {
        if (!storageReady) return;
        const saved = JSON.stringify({ version: 3, revision, pending, legacyIds, blocks: documentBlocks() });
        try {
            sessionStorage.setItem(draftKey, saved);
            localStorage.setItem(key, saved);
            storageFailed = false;
        } catch {
            storageFailed = true;
            showError('Local saving is unavailable. Keep this page open until synced, or export Markdown.');
        }
    };
    const showConflict = snapshot => {
        if (!conflict || snapshot.revision >= conflict.revision) conflict = snapshot;
        journal.querySelector('[data-conflict]').hidden = false;
        status.textContent = 'Conflicting changes — choose a copy';
        saveLocal();
    };
    const positions = () => {
        [...board.children].forEach(card => movements.get(card)?.cancel());
        return new Map([...board.children].map(card => [card.dataset.blockId, card.getBoundingClientRect()]));
    };
    const animatePositions = before => {
        if (reducedMotion.matches) return;
        [...board.children].forEach(card => {
            const previous = before.get(card.dataset.blockId);
            const current = card.getBoundingClientRect();
            if (previous && (previous.top !== current.top || previous.left !== current.left)) {
                movements.set(card, card.animate([
                    { transform: `translate(${previous.left - current.left}px, ${previous.top - current.top}px)` },
                    { transform: 'translate(0, 0)' }
                ], { duration: 280, easing: 'ease-out' }));
            }
        });
    };
    const showRemoteChanges = (previous, before) => {
        const changes = { added: 0, edited: 0, moved: 0, removed: 0 };
        const currentIds = new Set(blocks.map(block => block.id));
        const oldOrder = [...previous.keys()].filter(id => currentIds.has(id));
        const newOrder = blocks.filter(block => previous.has(block.id)).map(block => block.id);
        blocks.forEach(block => {
            const old = previous.get(block.id);
            const kinds = [];
            if (!old) kinds.push('added');
            else {
                if (old.type !== block.type || old.text !== block.text) kinds.push('edited');
                if (oldOrder.indexOf(block.id) !== newOrder.indexOf(block.id)) kinds.push('moved');
            }
            kinds.forEach(kind => changes[kind]++);
            if (!kinds.length) return;
            const card = [...board.children].find(item => item.dataset.blockId === block.id);
            clearTimeout(highlights.get(card));
            card.dataset.remoteChange = kinds.join(' ');
            highlights.set(card, setTimeout(() => delete card.dataset.remoteChange, 2400));
        });
        changes.removed = [...previous.keys()].filter(id => !blocks.some(block => block.id === id)).length;
        const summary = Object.entries(changes).filter(([, count]) => count).map(([kind, count]) => `${count} ${count === 1 ? 'block' : 'blocks'} ${kind}`);
        if (summary.length) {
            remoteNotice.textContent = 'Another device: ' + summary.join(', ');
            remoteNotice.hidden = false;
            animatePositions(before);
        }
    };
    const accept = (snapshot, visualize = false) => {
        const previous = new Map(blocks.map((block, index) => [block.id, { ...block, index }]));
        const existing = new Map(blocks.map(block => [block.id, block]));
        const before = visualize ? positions() : null;
        revision = snapshot.revision;
        blocks = snapshot.blocks.map(block => Object.assign(existing.get(block.id) ?? {}, block));
        legacyIds = false;
        pending = false;
        conflict = null;
        journal.querySelector('[data-conflict]').hidden = true;
        render();
        if (visualize) showRemoteChanges(previous, before);
        saveLocal();
        if (storageReady && !storageFailed) error.hidden = true;
        status.textContent = 'Synced to your account';
    };
    const refresh = async () => {
        if (syncing || refreshing || draggedId !== null) {
            refreshQueued = true;
            return;
        }
        refreshing = true;
        try {
            const response = await fetch(endpoint, { cache: 'no-store' });
            if (!response.ok) throw new Error();
            const snapshot = await response.json();
            if (snapshot.ownerId !== journal.dataset.ownerId) {
                ready = false;
                status.textContent = 'Account changed — reopen the journal';
                showError('The signed-in account changed. Your local draft is kept. Reopen the journal from My library.');
                return;
            }
            ready = true;
            if (snapshot.revision < revision) return;
            const visualize = receivedSnapshot && snapshot.revision > revision;
            receivedSnapshot = true;
            if (!pending || sameBlocks(snapshot.blocks)) accept(snapshot, visualize);
            else if (snapshot.revision !== revision) showConflict(snapshot);
            else await save();
        } catch {
            ready = false;
            status.textContent = 'Offline — local draft kept';
        } finally {
            refreshing = false;
            if (refreshQueued) {
                refreshQueued = false;
                setTimeout(refresh, 0);
            }
        }
    };
    const save = async () => {
        clearTimeout(timer);
        saveLocal();
        if (!pending || conflict || syncing || !ready) return;
        syncing = true;
        const sentEdits = edits;
        try {
            const response = await fetch(endpoint, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf },
                body: JSON.stringify({ revision, blocks: documentBlocks(), clientId })
            });
            if (response.status === 409) {
                showConflict(await response.json());
            } else if (!response.ok) {
                ready = false;
                status.textContent = 'Not synced — local draft kept';
                showError(response.status === 401 ? 'Sign in again to sync your journal.' : 'The server could not save this journal. Your local draft is kept; export it if needed.');
            } else {
                const snapshot = await response.json();
                revision = snapshot.revision;
                legacyIds = false;
                pending = sentEdits !== edits;
                saveLocal();
                if (storageReady && !storageFailed) error.hidden = true;
                status.textContent = pending ? 'Syncing…' : 'Synced to your account';
            }
        } catch {
            ready = false;
            status.textContent = 'Offline — local draft kept';
        } finally {
            syncing = false;
            if (refreshQueued && !refreshing) {
                refreshQueued = false;
                refresh();
            }
        }
        if (pending && ready && !conflict) timer = setTimeout(save, 300);
    };
    const changed = () => {
        pending = true;
        edits++;
        status.textContent = conflict ? 'Conflicting changes — choose a copy' : ready ? 'Syncing…' : 'Offline — local draft kept';
        updatePreview();
        saveLocal();
        clearTimeout(timer);
        timer = setTimeout(save, 350);
    };

    const render = () => {
        const cards = new Map([...board.children].map(card => [card.dataset.blockId, card]));
        const focused = document.activeElement;
        const hadFocus = board.contains(focused);
        const selection = focused instanceof HTMLTextAreaElement ? [focused.selectionStart, focused.selectionEnd, focused.selectionDirection] : null;
        const focusedIndex = hadFocus ? [...board.children].indexOf(focused.closest('.journal-block')) : -1;
        blocks.forEach((block, index) => {
            let card = cards.get(block.id);
            if (!card) {
                card = template.content.firstElementChild.cloneNode(true);
                const input = card.querySelector('textarea');
                const select = card.querySelector('select');
                input.addEventListener('input', () => {
                    block.text = input.value;
                    changed();
                });
                select.addEventListener('change', () => {
                    block.type = select.value;
                    render();
                    changed();
                });
                card.querySelector('[data-remove]').addEventListener('click', () => {
                    blocks.splice(blocks.findIndex(item => item.id === block.id), 1);
                    render();
                    changed();
                });
                card.querySelector('[data-up]').addEventListener('click', () => {
                    const from = blocks.findIndex(item => item.id === block.id);
                    move(from, from - 1);
                });
                card.querySelector('[data-down]').addEventListener('click', () => {
                    const from = blocks.findIndex(item => item.id === block.id);
                    move(from, from + 1);
                });
            }
            card.dataset.blockId = block.id;
            card.dataset.blockType = block.type;
            const input = card.querySelector('textarea');
            const select = card.querySelector('select');
            if (input.value !== block.text) input.value = block.text;
            input.placeholder = block.type === 'heading' ? 'Heading' : block.type === 'list' ? 'One item per line' : 'What happened in your last session?';
            input.rows = block.type === 'heading' ? 1 : Math.max(3, Math.min(10, block.text.split('\n').length));
            select.value = block.type;
            card.querySelector('[data-up]').disabled = index === 0;
            card.querySelector('[data-down]').disabled = index === blocks.length - 1;
            if (board.children[index] !== card) board.insertBefore(card, board.children[index] ?? null);
        });
        const ids = new Set(blocks.map(block => block.id));
        cards.forEach((card, id) => { if (!ids.has(id)) card.remove(); });
        if (hadFocus) {
            if (focused.isConnected) {
                if (document.activeElement !== focused) focused.focus({ preventScroll: true });
                if (selection) focused.setSelectionRange(selection[0], selection[1], selection[2]);
            } else {
                board.children[Math.min(focusedIndex, blocks.length - 1)]?.querySelector('textarea').focus({ preventScroll: true });
            }
        }
        journal.querySelectorAll('[data-add]').forEach(button => button.disabled = blocks.length >= 100);
        updatePreview();
    };

    const move = (from, to) => {
        if (from === to || from < 0 || to < 0 || to >= blocks.length) return;
        const before = positions();
        const [block] = blocks.splice(from, 1);
        blocks.splice(to, 0, block);
        render();
        animatePositions(before);
        board.children[to].querySelector('textarea').focus({ preventScroll: true });
        changed();
    };

    journal.querySelectorAll('[data-add]').forEach(button => button.addEventListener('click', () => {
        if (blocks.length >= 100) return;
        blocks.push({ id: crypto.randomUUID(), type: button.dataset.add, text: '' });
        render();
        board.lastElementChild.querySelector('textarea').focus();
        changed();
    }));
    journal.querySelector('[data-preview-toggle]').addEventListener('change', event => {
        preview.hidden = !event.target.checked;
        journal.classList.toggle('has-preview', event.target.checked);
    });
    journal.querySelector('[data-export]').addEventListener('click', () => {
        const url = URL.createObjectURL(new Blob([markdown()], { type: 'text/markdown;charset=utf-8' }));
        const link = document.createElement('a');
        link.href = url;
        link.download = journal.dataset.gameName.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '').slice(0, 100) + '-journal.md';
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    });

    const clearDrag = () => {
        draggedId = null;
        board.querySelectorAll('.dragging, .drop-before, .drop-after').forEach(card => card.classList.remove('dragging', 'drop-before', 'drop-after'));
        if (refreshQueued && !refreshing && !syncing) {
            refreshQueued = false;
            refresh();
        }
    };
    board.addEventListener('dragstart', event => {
        if (!event.target.closest('.journal-drag')) return;
        const card = event.target.closest('[data-block-id]');
        draggedId = card.dataset.blockId;
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', draggedId);
        card.classList.add('dragging');
    });
    board.addEventListener('dragover', event => {
        const card = event.target.closest('[data-block-id]');
        if (!draggedId || !card || card.dataset.blockId === draggedId) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        board.querySelectorAll('.drop-before, .drop-after').forEach(item => item.classList.remove('drop-before', 'drop-after'));
        const after = event.clientY > card.getBoundingClientRect().top + card.offsetHeight / 2;
        card.classList.add(after ? 'drop-after' : 'drop-before');
    });
    board.addEventListener('drop', event => {
        const card = event.target.closest('[data-block-id]');
        if (!draggedId || !card) return;
        event.preventDefault();
        const from = blocks.findIndex(block => block.id === draggedId);
        let to = blocks.findIndex(block => block.id === card.dataset.blockId);
        if (event.clientY > card.getBoundingClientRect().top + card.offsetHeight / 2) to++;
        if (from < to) to--;
        clearDrag();
        move(from, to);
    });
    document.addEventListener('dragend', clearDrag);
    window.addEventListener('pagehide', saveLocal);
    document.addEventListener('visibilitychange', () => { if (document.hidden) saveLocal(); else refresh(); });
    journal.querySelector('[data-use-server]').addEventListener('click', () => {
        if (conflict) accept(conflict, true);
    });
    journal.querySelector('[data-keep-local]').addEventListener('click', () => {
        if (!conflict) return;
        revision = conflict.revision;
        conflict = null;
        journal.querySelector('[data-conflict]').hidden = true;
        save();
    });
    const connection = new signalR.HubConnectionBuilder().withUrl('/hubs/journal').withAutomaticReconnect().build();
    reducedMotion.addEventListener('change', () => {
        if (reducedMotion.matches) [...board.children].forEach(card => movements.get(card)?.cancel());
    });
    const subscribe = async () => {
        await connection.invoke('Subscribe', Number(journal.dataset.gameId));
        await refresh();
    };
    connection.on('JournalChanged', (remoteRevision, writer) => {
        if (writer !== clientId && remoteRevision > revision) refresh();
    });
    connection.onreconnected(() => subscribe().catch(() => setTimeout(connect, 1000)));
    connection.onreconnecting(() => { status.textContent = 'Reconnecting — local draft kept'; });
    const connect = async () => {
        try {
            if (connection.state === signalR.HubConnectionState.Disconnected) await connection.start();
            if (connection.state !== signalR.HubConnectionState.Connected) return;
            await subscribe();
        } catch {
            ready = false;
            status.textContent = 'Offline — local draft kept';
            setTimeout(connect, 5000);
        }
    };
    connection.onclose(() => setTimeout(connect, 5000));
    window.addEventListener('online', () => refresh());
    journal.querySelector('[data-retry]').addEventListener('click', refresh);
    render();
    connect();
}
