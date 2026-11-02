document.querySelectorAll('[data-cover-editor]').forEach(editor => {
    const radios = editor.querySelectorAll('input[name="coverSource"]');
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
    };
    editor.querySelectorAll('input[type="file"]:disabled').forEach(input => {
        input.dataset.uploadUnavailable = 'true';
    });
    radios.forEach(radio => radio.addEventListener('change', update));
    update();
});
