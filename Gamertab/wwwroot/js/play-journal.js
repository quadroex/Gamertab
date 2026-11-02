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
    let dirty = false;
    let timer;
    let draggedId = null;
    let storageReady = true;

    const showError = message => {
        error.textContent = message;
        error.hidden = false;
    };
    try {
        const saved = localStorage.getItem(key);
        if (saved !== null) {
            const savedDocument = JSON.parse(saved);
            if (savedDocument.version !== 1 || !Array.isArray(savedDocument.blocks) || savedDocument.blocks.length > 100 ||
                !savedDocument.blocks.every(block => types.includes(block.type) && typeof block.text === 'string' && block.text.length <= 10000)) {
                throw new Error('Invalid journal');
            }
            blocks = savedDocument.blocks.map(block => ({ id: crypto.randomUUID(), type: block.type, text: block.text }));
            status.textContent = 'Restored from this browser';
        } else {
            status.textContent = 'Ready to write';
        }
    } catch {
        storageReady = false;
        showError('The saved journal could not be read. Its stored copy has been kept. This page will not overwrite it; you can still write and export a new Markdown copy.');
        status.textContent = 'Browser saving is unavailable';
    }

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

    const save = () => {
        clearTimeout(timer);
        if (!dirty || !storageReady) return;
        try {
            localStorage.setItem(key, JSON.stringify({ version: 1, blocks: blocks.map(({ type, text }) => ({ type, text })) }));
            dirty = false;
            status.textContent = 'Saved in this browser';
            error.hidden = true;
        } catch {
            status.textContent = 'Not saved';
            showError('This browser could not save your journal. Export Markdown before leaving to keep your changes.');
        }
    };
    const changed = () => {
        dirty = true;
        status.textContent = storageReady ? 'Saving…' : 'Not saved in this browser';
        updatePreview();
        clearTimeout(timer);
        timer = setTimeout(save, 250);
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
    window.addEventListener('pagehide', save);
    document.addEventListener('visibilitychange', () => { if (document.hidden) save(); });
    window.addEventListener('storage', event => {
        if (event.key !== key) return;
        clearTimeout(timer);
        storageReady = false;
        board.querySelectorAll('textarea, select, button').forEach(input => input.disabled = true);
        journal.querySelectorAll('[data-add]').forEach(button => button.disabled = true);
        status.textContent = 'Journal changed in another tab';
        showError('Another tab changed this journal. Export your current notes if needed, then reload to use the latest saved copy.');
    });
    render();
}
