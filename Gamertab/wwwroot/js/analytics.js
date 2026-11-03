const analytics = document.querySelector('[data-analytics]');
if (analytics) {
    const key = 'gamertab:analytics-consent';
    const id = analytics.dataset.measurementId;
    let allowed = false;
    let started = false;
    const remembered = () => { try { return localStorage.getItem(key); } catch { return null; } };
    const event = (name, gameId) => {
        if (!allowed || !started) return;
        const parameters = { page_location: analytics.dataset.pageUrl, page_referrer: '', page_title: 'Gamertab' };
        if (/^[1-9][0-9]*$/.test(gameId ?? '')) parameters.game_id = Number(gameId);
        window.gtag('event', name, parameters);
    };
    const start = () => {
        allowed = true;
        window['ga-disable-' + id] = false;
        if (started) return;
        started = true;
        window.dataLayer = window.dataLayer || [];
        window.gtag = function () { window.dataLayer.push(arguments); };
        window.gtag('consent', 'default', { analytics_storage: 'granted', ad_storage: 'denied', ad_user_data: 'denied', ad_personalization: 'denied' });
        window.gtag('js', new Date());
        window.gtag('config', id, { send_page_view: false, allow_google_signals: false, allow_ad_personalization_signals: false,
            page_location: analytics.dataset.pageUrl, page_referrer: '', page_title: 'Gamertab' });
        const script = document.createElement('script');
        script.async = true;
        script.src = 'https://www.googletagmanager.com/gtag/js?id=' + encodeURIComponent(id);
        document.head.append(script);
        if (analytics.dataset.publicPage === 'true') event('page_view');
        if (analytics.dataset.gameId) event('game_view', analytics.dataset.gameId);
        if (analytics.dataset.libraryAdded) event('library_add', analytics.dataset.libraryAdded);
    };
    const choose = value => {
        try { localStorage.setItem(key, value); } catch { }
        analytics.hidden = true;
        if (value === 'granted') start();
        else {
            allowed = false;
            window['ga-disable-' + id] = true;
            if (started) window.gtag('consent', 'update', { analytics_storage: 'denied' });
            document.cookie.split(';').forEach(cookie => {
                const name = cookie.split('=')[0].trim();
                if (!name.startsWith('_ga')) return;
                const domains = location.hostname.split('.');
                document.cookie = name + '=; Max-Age=0; path=/';
                for (let i = 0; i < domains.length - 1; i++) document.cookie = name + '=; Max-Age=0; path=/; domain=.' + domains.slice(i).join('.');
            });
        }
    };
    analytics.querySelector('[data-analytics-accept]').addEventListener('click', () => choose('granted'));
    analytics.querySelector('[data-analytics-decline]').addEventListener('click', () => choose('denied'));
    document.querySelector('[data-analytics-settings]').addEventListener('click', () => { analytics.hidden = false; });
    document.addEventListener('click', click => {
        const recommendation = click.target.closest('[data-recommendation-game] a');
        if (recommendation) event('recommendation_open', recommendation.closest('[data-recommendation-game]').dataset.recommendationGame);
        if (click.target.closest('[data-video-id] button')) event('trailer_play', analytics.dataset.gameId);
    }, true);
    const consent = remembered();
    if (consent === 'granted') start();
    else if (consent !== 'denied') analytics.hidden = false;
    window.addEventListener('storage', change => { if (change.key === key) { if (change.newValue === 'granted') start(); else choose('denied'); } });
}
