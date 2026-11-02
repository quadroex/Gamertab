const assistant = document.querySelector('[data-assistant]');
if (assistant) {
    const form = assistant.querySelector('form');
    const input = assistant.querySelector('textarea');
    const send = form.querySelector('[type="submit"]');
    const stop = assistant.querySelector('[data-stop]');
    const clear = assistant.querySelector('[data-clear]');
    const messages = assistant.querySelector('[data-messages]');
    const status = assistant.querySelector('[data-status]');
    let history = [];
    let controller;
    const toolLabels = { search_games: 'Searching saved games', get_library: 'Reading your library', get_recommendations: 'Finding recommendations' };
    const add = (label, text) => {
        const card = document.createElement('article');
        card.className = 'content-panel assistant-message';
        const heading = document.createElement('strong');
        heading.textContent = label;
        const body = document.createElement('div');
        body.className = 'assistant-text';
        body.textContent = text;
        card.append(heading, body);
        messages.append(card);
        return body;
    };
    stop.addEventListener('click', () => controller?.abort());
    clear.addEventListener('click', () => { history = []; messages.replaceChildren(); status.textContent = 'Ready'; });
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (controller || !input.value.trim()) return;
        const message = input.value.trim();
        add('You', message);
        const answer = add('Gemini', '');
        const events = document.createElement('div');
        answer.before(events);
        controller = new AbortController();
        send.disabled = true;
        stop.disabled = false;
        clear.disabled = true;
        input.value = '';
        status.textContent = 'Connecting…';
        let complete = false;
        try {
            const response = await fetch('/api/assistant/chat', {
                method: 'POST', signal: controller.signal,
                headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': form.querySelector('input[name="__RequestVerificationToken"]').value },
                body: JSON.stringify({ message, history })
            });
            if (!response.ok) throw new Error(response.status === 429 ? 'Too many requests. Please wait a minute.' : response.status === 503 ? 'The assistant is not configured.' : 'Unable to start the chat. Refresh the page and sign in if needed.');
            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';
            while (true) {
                const { value, done } = await reader.read();
                buffer += decoder.decode(value, { stream: !done }).replace(/\r\n/g, '\n');
                let end;
                while ((end = buffer.indexOf('\n\n')) >= 0) {
                    const lines = buffer.slice(0, end).split('\n');
                    buffer = buffer.slice(end + 2);
                    const type = lines.find(line => line.startsWith('event:'))?.slice(6).trim();
                    const data = JSON.parse(lines.filter(line => line.startsWith('data:')).map(line => line.slice(5).trimStart()).join('\n'));
                    if (type === 'delta') { answer.textContent += data.text; status.textContent = 'Responding…'; }
                    if (type === 'tool_start') { status.textContent = (toolLabels[data.name] ?? 'Using a tool') + '…'; }
                    if (type === 'tool_result') {
                        const detail = document.createElement('details');
                        const summary = document.createElement('summary');
                        summary.textContent = (toolLabels[data.name] ?? 'Tool') + ' · completed';
                        const result = document.createElement('ul');
                        if (Array.isArray(data.result)) {
                            for (const game of data.result) {
                                const item = document.createElement('li');
                                item.textContent = [game.name ?? game.Name, game.status, game.reason ?? game.Reason].filter(Boolean).join(' · ');
                                result.append(item);
                            }
                            if (!data.result.length) {
                                const item = document.createElement('li');
                                item.textContent = 'No games found';
                                result.append(item);
                            }
                        } else {
                            result.textContent = 'No results available';
                        }
                        detail.append(summary, result);
                        events.append(detail);
                    }
                    if (type === 'error') throw new Error(data.message);
                    if (type === 'done') complete = true;
                }
                if (done) break;
            }
            if (!complete) throw new Error('The response was interrupted. Please try again.');
            history.push({ role: 'user', text: message }, { role: 'model', text: answer.textContent.slice(0, 8000) });
            history = history.slice(-10);
            const text = answer.textContent;
            const links = /\[([^\]\n]+)\]\((\/Games\/Details\/[1-9]\d*)\)|\*\*([^*\n]+)\*\*/g;
            let position = 0;
            answer.replaceChildren();
            for (const match of text.matchAll(links)) {
                answer.append(document.createTextNode(text.slice(position, match.index)));
                const link = document.createElement(match[3] ? 'strong' : 'a');
                link.textContent = match[3] ?? match[1];
                if (match[2]) link.href = match[2];
                answer.append(link);
                position = match.index + match[0].length;
            }
            answer.append(document.createTextNode(text.slice(position)));
            status.textContent = 'Done';
        } catch (error) {
            status.textContent = error.name === 'AbortError' ? 'Stopped' : error.message;
        } finally {
            controller?.abort();
            controller = null;
            send.disabled = false;
            stop.disabled = true;
            clear.disabled = false;
        }
    });
}
