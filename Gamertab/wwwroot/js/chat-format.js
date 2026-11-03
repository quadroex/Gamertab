window.formatChat = (element, text) => {
    const inline = (parent, value) => {
        const tokens = /\[([^\]\n]+)\]\((\/Games\/Details\/[1-9]\d*)\)|\*\*([^*\n]+)\*\*|__([^_\n]+)__|`([^`\n]+)`|\*([^*\n]+)\*|_([^_\n]+)_/g;
        let position = 0;
        for (const match of value.matchAll(tokens)) {
            parent.append(document.createTextNode(value.slice(position, match.index)));
            const node = document.createElement(match[2] ? 'a' : match[3] || match[4] ? 'strong' : match[5] ? 'code' : 'em');
            node.textContent = match[1] ?? match[3] ?? match[4] ?? match[5] ?? match[6] ?? match[7];
            if (match[2]) node.href = match[2];
            parent.append(node);
            position = match.index + match[0].length;
        }
        parent.append(document.createTextNode(value.slice(position)));
    };
    const fragment = document.createDocumentFragment();
    let paragraph;
    let list;
    let code;
    for (const line of text.replace(/\r\n/g, '\n').split('\n')) {
        if (/^\s*```/.test(line)) {
            paragraph = list = null;
            if (code) code = null;
            else {
                const pre = document.createElement('pre');
                code = document.createElement('code');
                pre.append(code);
                fragment.append(pre);
            }
            continue;
        }
        if (code) { code.textContent += line + '\n'; continue; }
        if (!line.trim()) { paragraph = list = null; continue; }
        const heading = line.match(/^\s*#{1,6}\s+(.+)$/);
        const item = line.match(/^\s*(?:[-*+]\s+|\d+[.)]\s+)(.+)$/);
        if (heading) {
            paragraph = list = null;
            const node = document.createElement('h3');
            inline(node, heading[1]);
            fragment.append(node);
        } else if (item) {
            paragraph = null;
            const tag = /^\s*\d/.test(line) ? 'OL' : 'UL';
            if (!list || list.tagName !== tag) {
                list = document.createElement(tag.toLowerCase());
                fragment.append(list);
            }
            const node = document.createElement('li');
            inline(node, item[1]);
            list.append(node);
        } else {
            list = null;
            if (!paragraph) { paragraph = document.createElement('p'); fragment.append(paragraph); }
            else paragraph.append(document.createElement('br'));
            inline(paragraph, line);
        }
    }
    element.replaceChildren(fragment);
};
