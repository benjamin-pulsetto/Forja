const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => document.querySelectorAll(sel);

// State
let currentRunId = null;
let stageOutputs = { Planner: '', Coder: '', Tester: '', Reviewer: '' };
let activeOutputTab = 'Planner';

// --- SignalR Connection ---
const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/pipeline')
    .withAutomaticReconnect()
    .build();

connection.on('StageStarted', (stageName) => {
    setStageState(stageName, 'running');
});

connection.on('StageCompleted', (stageName, result) => {
    setStageState(stageName, result.success ? 'completed' : 'failed');
    stageOutputs[stageName] = result.rawOutput || result.summary || '';
    if (activeOutputTab === stageName) {
        $('#stage-output').textContent = stageOutputs[stageName];
    }
    $('#output-section').classList.remove('hidden');
});

connection.on('HealingRetry', (attempt, reason) => {
    $('#healing-info').classList.remove('hidden');
    $('#healing-text').textContent = `Healing attempt ${attempt}/3 — ${reason}`;
    // Reset coder and tester for retry
    setStageState('Coder', 'pending');
    setStageState('Tester', 'pending');
});

connection.on('PipelineCompleted', (success, summary) => {
    const result = $('#pipeline-result');
    result.classList.remove('hidden');
    result.className = `pipeline-result ${success ? 'success' : 'failure'}`;
    result.textContent = success ? `✓ ${summary}` : `✗ ${summary}`;
    $('#healing-info').classList.add('hidden');
    $('#btn-start').disabled = false;
});

connection.on('Log', (message) => {
    appendLog(message);
});

connection.on('PipelineError', (error) => {
    appendLog(`ERROR: ${error}`);
    const result = $('#pipeline-result');
    result.classList.remove('hidden');
    result.className = 'pipeline-result failure';
    result.textContent = `✗ Error: ${error}`;
    $('#btn-start').disabled = false;
});

connection.start().catch(err => console.error('SignalR connection failed:', err));

// --- Input Mode Tabs ---
$$('.tab').forEach(tab => {
    tab.addEventListener('click', () => {
        $$('.tab').forEach(t => t.classList.remove('active'));
        tab.classList.add('active');

        const mode = tab.dataset.mode;
        $('#natural-input').classList.toggle('hidden', mode !== 'natural');
        $('#yaml-input').classList.toggle('hidden', mode !== 'yaml');
    });
});

// --- Output Tabs ---
$$('.output-tab').forEach(tab => {
    tab.addEventListener('click', () => {
        $$('.output-tab').forEach(t => t.classList.remove('active'));
        tab.classList.add('active');
        activeOutputTab = tab.dataset.stage;
        $('#stage-output').textContent = stageOutputs[activeOutputTab] || 'No output yet';
    });
});

// --- Generate Spec Preview ---
$('#btn-generate-spec').addEventListener('click', async () => {
    const description = $('#description').value.trim();
    const repoPath = $('#repo-path').value.trim();

    if (!description || !repoPath) {
        alert('Please provide a description and repository path');
        return;
    }

    $('#btn-generate-spec').textContent = 'Generating...';
    $('#btn-generate-spec').disabled = true;

    try {
        const res = await fetch('/api/spec/generate', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ description, repoPath })
        });

        if (!res.ok) {
            const errData = await res.json().catch(() => null);
            throw new Error(errData?.error || `HTTP ${res.status}`);
        }

        const data = await res.json();
        $('#spec-yaml').textContent = data.yaml;
        $('#spec-preview').classList.remove('hidden');
    } catch (err) {
        alert(`Failed to generate spec: ${err.message}`);
    } finally {
        $('#btn-generate-spec').textContent = 'Preview Spec';
        $('#btn-generate-spec').disabled = false;
    }
});

// --- Start Pipeline ---
$('#btn-start').addEventListener('click', async () => {
    const repoPath = $('#repo-path').value.trim();
    if (!repoPath) {
        alert('Please provide a repository path');
        return;
    }

    const isYamlMode = $('.tab.active').dataset.mode === 'yaml';
    const body = { repoPath };

    if (isYamlMode) {
        body.yaml = $('#yaml-spec').value.trim();
        if (!body.yaml) { alert('Please provide a YAML spec'); return; }
    } else {
        body.description = $('#description').value.trim();
        if (!body.description) { alert('Please provide a description'); return; }
    }

    // Reset UI
    resetPipeline();
    $('#btn-start').disabled = true;
    $('#pipeline-section').classList.remove('hidden');

    try {
        const res = await fetch('/api/pipeline/start', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });

        if (!res.ok) {
            const errData = await res.json().catch(() => null);
            if (errData?.code === 'REPO_NOT_FOUND') {
                await handleRepoNotFound(repoPath);
                $('#btn-start').disabled = false;
                $('#pipeline-section').classList.add('hidden');
                return;
            }
            throw new Error(errData?.error || `HTTP ${res.status}`);
        }

        const data = await res.json();
        currentRunId = data.runId;
        $('#run-id').textContent = `#${data.runId}`;
        appendLog('Pipeline queued — generating spec...');

        // Show description if available
        if (data.description) {
            $('#spec-yaml').textContent = data.description;
            $('#spec-preview').classList.remove('hidden');
        }

        // Join SignalR group for this run
        await connection.invoke('JoinRun', currentRunId);
        appendLog('Connected to live feed. Waiting for Claude CLI...');

    } catch (err) {
        alert(`Failed to start pipeline: ${err.message}`);
        $('#btn-start').disabled = false;
        $('#pipeline-section').classList.add('hidden');
    }
});

// --- Helpers ---
function setStageState(stageName, state) {
    const el = $(`#stage-${stageName}`);
    if (!el) return;
    el.className = `stage ${state}`;
    el.querySelector('.stage-status').textContent = state;
}

async function handleRepoNotFound(repoPath) {
    const create = confirm(
        `Repository not found at:\n${repoPath}\n\nDo you want to create a new git repository there?`
    );
    if (!create) return;

    try {
        const res = await fetch('/api/repo/init', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ repoPath })
        });

        if (!res.ok) {
            const errData = await res.json().catch(() => null);
            throw new Error(errData?.error || `HTTP ${res.status}`);
        }

        const data = await res.json();
        alert(`${data.message}\n\nYou can now start the pipeline.`);
    } catch (err) {
        alert(`Failed to create repository: ${err.message}`);
    }
}

function appendLog(message) {
    const log = $('#live-log');
    let cssClass = '';
    if (message.includes('--- STAGE') || message.includes('--- CODER') || message.includes('--- COMMITTING'))
        cssClass = 'log-stage';
    else if (message.includes('PASSED') || message.includes('COMPLETED'))
        cssClass = 'log-success';
    else if (message.includes('FAILED') || message.includes('ERROR'))
        cssClass = 'log-error';
    else if (message.includes('healing') || message.includes('Self-healing'))
        cssClass = 'log-warning';

    const line = document.createElement('div');
    if (cssClass) line.className = cssClass;
    line.textContent = message;
    log.appendChild(line);
    log.scrollTop = log.scrollHeight;
}

function resetPipeline() {
    ['Planner', 'Coder', 'Tester', 'Reviewer'].forEach(name => {
        setStageState(name, 'pending');
        stageOutputs[name] = '';
    });
    $('#stage-output').textContent = 'Waiting for pipeline to start...';
    $('#live-log').innerHTML = '';
    $('#pipeline-result').classList.add('hidden');
    $('#healing-info').classList.add('hidden');
    $('#output-section').classList.add('hidden');
}

// --- History ---
async function loadHistory() {
    try {
        const res = await fetch('/api/pipeline/history');
        if (!res.ok) return;
        const runs = await res.json();
        const list = $('#history-list');

        if (!runs || runs.length === 0) {
            list.innerHTML = '<div class="history-empty">No runs yet</div>';
            return;
        }

        list.innerHTML = runs.map(run => {
            const status = run.status ?? 'Pending';
            const statusName = typeof status === 'number'
                ? ['Pending','GeneratingSpec','Planning','Coding','Testing','Reviewing','Committing','Completed','Failed'][status]
                : status;
            const time = run.createdAt ? new Date(run.createdAt).toLocaleString() : '';
            const name = run.spec?.name || 'unnamed';
            const branch = run.branchName || '';
            const healCount = run.healingAttempts || 0;
            const error = run.error || '';

            return `<div class="history-item" title="${error}">
                <div class="history-status ${statusName}"></div>
                <div class="history-name">${name}</div>
                ${branch ? `<span class="history-branch">${branch}</span>` : ''}
                ${healCount > 0 ? `<span class="history-meta">${healCount} heals</span>` : ''}
                <span class="history-meta">${statusName}</span>
                <span class="history-meta">${time}</span>
            </div>`;
        }).join('');
    } catch (e) {
        console.error('Failed to load history:', e);
    }
}

// Load history on start and after pipeline completes
loadHistory();

// Re-load history when a pipeline completes
const origPipelineCompleted = connection._methods?.pipelinecompleted;
connection.on('PipelineCompleted', () => setTimeout(loadHistory, 500));
