const bar = document.getElementById('bar');
const node = document.getElementById('node');
const form = document.getElementById('node-form');
const input = document.getElementById('node-input');
const nodeError = document.getElementById('node-error');
const update = document.getElementById('update');

function showNode(nodeUrl) {
    const host = new URL(nodeUrl).host;
    node.textContent = host;
    node.title = `Connected to ${host}. Click to change node.`;
}

function showUpdate(state) {
    update.classList.toggle('ready', state.status === 'ready');
    update.title = state.status === 'ready'
        ? `Iridium ${state.version} is ready. Click to restart and update.`
        : '';
}

function stopEditing() {
    bar.classList.remove('editing');
    nodeError.textContent = '';
}

node.addEventListener('click', () => {
    input.value = node.textContent;
    bar.classList.add('editing');
    input.focus();
    input.select();
});
input.addEventListener('keydown', event => { if (event.key === 'Escape') stopEditing(); });
input.addEventListener('blur', () => setTimeout(stopEditing, 120));
form.addEventListener('submit', async event => {
    event.preventDefault();
    const result = await window.titlebar.changeNode(input.value);
    if (!result.ok) { nodeError.textContent = result.error; return; }
    showNode(result.nodeUrl);
    stopEditing();
});
update.addEventListener('click', () => window.titlebar.installUpdate());

window.titlebar.onUpdateState(showUpdate);
window.titlebar.getInfo().then(info => {
    showNode(info.nodeUrl);
    showUpdate(info.update);
});
