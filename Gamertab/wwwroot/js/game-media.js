const viewer = document.querySelector('.screenshot-dialog');
const screenshots = [...document.querySelectorAll('[data-screenshot]')];
if (viewer && typeof viewer.showModal === 'function') {
    const image = viewer.querySelector('[data-full-screenshot]');
    const counter = viewer.querySelector('[data-screenshot-count]');
    let current = 0;
    const show = index => {
        current = (index + screenshots.length) % screenshots.length;
        image.src = screenshots[current].href;
        image.alt = screenshots[current].querySelector('img').alt;
        counter.textContent = `${current + 1} / ${screenshots.length}`;
    };
    screenshots.forEach((link, index) => link.addEventListener('click', event => {
        event.preventDefault();
        show(index);
        viewer.showModal();
    }));
    viewer.querySelector('[data-previous]').addEventListener('click', () => show(current - 1));
    viewer.querySelector('[data-next]').addEventListener('click', () => show(current + 1));
    viewer.addEventListener('keydown', event => {
        if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
            event.preventDefault();
            show(current + (event.key === 'ArrowLeft' ? -1 : 1));
        }
    });
    viewer.addEventListener('click', event => {
        const bounds = viewer.getBoundingClientRect();
        if (event.target === viewer && (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom)) {
            viewer.close();
        }
    });
    viewer.addEventListener('close', () => image.removeAttribute('src'));
}

document.querySelectorAll('[data-video-id]').forEach(player => {
    player.querySelector('.video-play').addEventListener('click', () => {
        const frame = document.createElement('iframe');
        frame.src = `https://www.youtube-nocookie.com/embed/${player.dataset.videoId}?autoplay=1&rel=0`;
        frame.title = player.dataset.videoTitle || 'Game video';
        frame.allow = 'autoplay; encrypted-media; picture-in-picture';
        frame.allowFullscreen = true;
        frame.referrerPolicy = 'strict-origin-when-cross-origin';
        player.replaceChildren(frame);
    }, { once: true });
});
