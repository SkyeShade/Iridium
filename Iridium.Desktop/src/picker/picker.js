const grid = document.getElementById('grid');
const tabs = [...document.querySelectorAll('[role="tab"]')];
const audio = document.getElementById('audio');
const audioNote = document.getElementById('audio-note');
const go = document.getElementById('go');
let sources = [];
let kind = 'window';
let selectedId = null;

function render() {
    grid.replaceChildren();
    const visible = sources.filter(source => source.kind === kind);
    if (!visible.length) {
        const empty = document.createElement('p');
        empty.className = 'empty';
        empty.textContent = kind === 'window' ? 'No applications to share.' : 'No screens found.';
        grid.append(empty);
    }
    for (const source of visible) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'source';
        button.setAttribute('aria-pressed', String(source.id === selectedId));
        const thumb = document.createElement('img');
        thumb.className = 'thumb';
        thumb.src = source.thumbnail;
        thumb.alt = '';
        const label = document.createElement('span');
        label.className = 'label';
        if (source.icon) {
            const icon = document.createElement('img');
            icon.src = source.icon;
            icon.alt = '';
            label.append(icon);
        }
        const name = document.createElement('span');
        name.textContent = source.name;
        label.append(name);
        button.append(thumb, label);
        button.addEventListener('click', () => select(source.id));
        button.addEventListener('dblclick', () => { select(source.id); confirm(); });
        grid.append(button);
    }
    audioNote.textContent = kind === 'window'
        ? "Only this app's sound"
        : 'All sound on your PC except Iridium';
    go.disabled = !visible.some(source => source.id === selectedId);
}

function select(id) {
    selectedId = id;
    render();
}

function confirm() {
    if (go.disabled) return;
    window.picker.choose({ sourceId: selectedId, audio: audio.checked });
}

for (const tab of tabs) {
    tab.addEventListener('click', () => {
        kind = tab.dataset.kind;
        for (const other of tabs) other.setAttribute('aria-selected', String(other === tab));
        render();
    });
}
go.addEventListener('click', confirm);
document.getElementById('cancel').addEventListener('click', () => window.picker.choose(null));
document.addEventListener('keydown', event => {
    if (event.key === 'Escape') window.picker.choose(null);
    if (event.key === 'Enter') confirm();
});

window.picker.onSources(list => {
    sources = list;
    // Open on Screens when there are no other windows, like a fresh desktop.
    if (!sources.some(source => source.kind === 'window')) tabs[1].click();
    else render();
});
