document.querySelectorAll('[data-cover-editor]').forEach(editor => {
    const radios = editor.querySelectorAll('input[name="coverSource"]');
    const fileInput = editor.querySelector('input[type="file"]');
    const preview = editor.querySelector('[data-file-preview]');
    const image = preview.querySelector('img');
    const info = editor.querySelector('[data-file-info]');
    const error = editor.querySelector('[data-file-error]');
    const currentCover = editor.querySelector('[data-current-cover]');
    let objectUrl = null;
    let selection = 0;
    const resetPreview = () => {
        selection++;
        if (objectUrl) URL.revokeObjectURL(objectUrl);
        objectUrl = null;
        preview.hidden = true;
        image.removeAttribute('src');
        info.textContent = '';
        error.textContent = '';
        error.hidden = true;
        fileInput.setCustomValidity('');
        fileInput.removeAttribute('aria-invalid');
    };
    const showError = message => {
        fileInput.setCustomValidity(message);
        fileInput.setAttribute('aria-invalid', 'true');
        error.textContent = message;
        error.hidden = false;
    };
    const previewFile = async () => {
        resetPreview();
        if (currentCover) currentCover.hidden = false;
        const file = fileInput.files[0];
        if (!file) return;
        if (file.size === 0 || file.size > 2 * 1024 * 1024) {
            showError('Choose a PNG or JPEG image up to 2 MB.');
            return;
        }
        const request = selection;
        fileInput.setCustomValidity('Please wait for the image preview.');
        try {
            const bytes = new Uint8Array(await file.arrayBuffer());
            if (request !== selection) return;
            const png = bytes.length >= 45 && [137, 80, 78, 71, 13, 10, 26, 10].every((value, index) => bytes[index] === value) &&
                [73, 72, 68, 82].every((value, index) => bytes[index + 12] === value);
            const jpeg = bytes.length >= 4 && bytes[0] === 255 && bytes[1] === 216 && bytes[2] === 255 && bytes.at(-2) === 255 && bytes.at(-1) === 217;
            if (!png && !jpeg) {
                showError('This file is not a PNG or JPEG image.');
                return;
            }
            objectUrl = URL.createObjectURL(file);
            image.src = objectUrl;
            await image.decode();
            if (request !== selection) return;
            info.textContent = `${file.name} · ${(file.size / 1024).toFixed(1)} KB · ${image.naturalWidth} x ${image.naturalHeight}`;
            preview.hidden = false;
            if (currentCover) currentCover.hidden = true;
            fileInput.setCustomValidity('');
        } catch {
            if (request !== selection) return;
            if (objectUrl) URL.revokeObjectURL(objectUrl);
            objectUrl = null;
            image.removeAttribute('src');
            showError('This image could not be read. Choose another PNG or JPEG.');
        }
    };
    const update = () => {
        const selected = editor.querySelector('input[name="coverSource"]:checked')?.value;
        editor.querySelectorAll('[data-cover-source]').forEach(group => {
            const active = group.dataset.coverSource === selected;
            group.hidden = !active;
            group.querySelectorAll('input').forEach(input => {
                if (!input.dataset.uploadUnavailable) {
                    input.disabled = !active;
                }
                input.required = active && input.type === 'file' && editor.dataset.hasUpload !== 'true';
            });
        });
        if (selected === 'file') {
            previewFile();
        } else {
            resetPreview();
            if (currentCover) currentCover.hidden = selected === 'none';
        }
    };
    editor.querySelectorAll('input[type="file"]:disabled').forEach(input => {
        input.dataset.uploadUnavailable = 'true';
    });
    radios.forEach(radio => radio.addEventListener('change', update));
    fileInput.addEventListener('change', previewFile);
    window.addEventListener('pagehide', resetPreview);
    window.addEventListener('pageshow', event => { if (event.persisted) update(); });
    update();
});
