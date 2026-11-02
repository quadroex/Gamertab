const journal = document.querySelector('[data-journal]');
if (journal) {
    const board = journal.querySelector('[data-blocks]');
    const template = journal.querySelector('[data-block-template]');
    const status = journal.querySelector('[data-save-status]');
    const error = journal.querySelector('[data-journal-error]');
    const preview = journal.querySelector('[data-preview]');
    const rendered = journal.querySelector('[data-rendered]');
    const source = journal.querySelector('[data-markdown]');
    const key = journal.dataset.storageKey;
    const types = ['paragraph', 'heading', 'list'];
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
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

    const showError = message => {
        error.textContent = message;
        error.hidden = false;
    };
    const documentBlocks = () => blocks.map(({ type, text }) => ({ type, text }));
    const sameBlocks = other => JSON.stringify(documentBlocks()) === JSON.stringify(other);
    try {
        const saved = sessionStorage.getItem(draftKey) ?? localStorage.getItem(key);
        if (saved !== null) {
            const savedDocument = JSON.parse(saved);
            if (![1, 2].includes(savedDocument.version) || !Array.isArray(savedDocument.blocks) || savedDocument.blocks.length > 100 ||
                !savedDocument.blocks.every(block => types.includes(block.type) && typeof block.text === 'string' && block.text.length <= 10000) ||
                (savedDocument.version === 2 && (!Number.isInteger(savedDocument.revision) || savedDocument.revision < 0 || typeof savedDocument.pending !== 'boolean'))) {
                throw new Error('Invalid journal');
            }
            blocks = savedDocument.blocks.map(block => ({ id: crypto.randomUUID(), type: block.type, text: block.text }));
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
        const saved = JSON.stringify({ version: 2, revision, pending, blocks: documentBlocks() });
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
    const accept = snapshot => {
        revision = snapshot.revision;
        blocks = snapshot.blocks.map(block => ({ id: crypto.randomUUID(), ...block }));
        pending = false;
        conflict = null;
        journal.querySelector('[data-conflict]').hidden = true;
        render();
        saveLocal();
        if (storageReady && !storageFailed) error.hidden = true;
        status.textContent = 'Synced to your account';
    };
    const refresh = async () => {
        if (syncing) {
            setTimeout(refresh, 400);
            return;
        }
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
            if (!pending || sameBlocks(snapshot.blocks)) accept(snapshot);
            else if (snapshot.revision !== revision) showConflict(snapshot);
            else await save();
        } catch {
            ready = false;
            status.textContent = 'Offline — local draft kept';
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
        board.replaceChildren();
        blocks.forEach((block, index) => {
            const card = template.content.firstElementChild.cloneNode(true);
            card.dataset.blockId = block.id;
            card.dataset.blockType = block.type;
            const input = card.querySelector('textarea');
            const select = card.querySelector('select');
            input.value = block.text;
            input.placeholder = block.type === 'heading' ? 'Heading' : block.type === 'list' ? 'One item per line' : 'What happened in your last session?';
            input.rows = block.type === 'heading' ? 1 : Math.max(3, Math.min(10, block.text.split('\n').length));
            select.value = block.type;
            card.querySelector('[data-up]').disabled = index === 0;
            card.querySelector('[data-down]').disabled = index === blocks.length - 1;
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
            card.querySelector('[data-up]').addEventListener('click', () => move(index, index - 1));
            card.querySelector('[data-down]').addEventListener('click', () => move(index, index + 1));
            board.append(card);
        });
        journal.querySelectorAll('[data-add]').forEach(button => button.disabled = blocks.length >= 100);
        updatePreview();
    };

    const move = (from, to) => {
        if (from === to || from < 0 || to < 0 || to >= blocks.length) return;
        const positions = new Map([...board.children].map(card => [card.dataset.blockId, card.getBoundingClientRect()]));
        const [block] = blocks.splice(from, 1);
        blocks.splice(to, 0, block);
        render();
        if (!reducedMotion) {
            [...board.children].forEach(card => {
                const before = positions.get(card.dataset.blockId);
                const after = card.getBoundingClientRect();
                if (before && before.top !== after.top) {
                    card.animate([{ transform: `translateY(${before.top - after.top}px)` }, { transform: 'translateY(0)' }], { duration: 180, easing: 'ease-out' });
                }
            });
        }
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
    document.addEventListener('visibilitychange', () => { if (document.hidden) saveLocal(); });
    journal.querySelector('[data-use-server]').addEventListener('click', () => {
        if (conflict) accept(conflict);
    });
    journal.querySelector('[data-keep-local]').addEventListener('click', () => {
        if (!conflict) return;
        revision = conflict.revision;
        conflict = null;
        journal.querySelector('[data-conflict]').hidden = true;
        save();
    });
    const connection = new signalR.HubConnectionBuilder().withUrl('/hubs/journal').withAutomaticReconnect().build();
    const subscribe = async () => {
        await connection.invoke('Subscribe', Number(journal.dataset.gameId));
        await refresh();
    };
    connection.on('JournalChanged', (remoteRevision, writer) => {
        if (writer !== clientId && remoteRevision > revision) refresh();
    });
    connection.onreconnected(subscribe);
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
