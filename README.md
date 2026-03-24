# Forja - AI-Powered Code Factory

Forja is an autonomous code generation pipeline that uses **Claude AI** to plan, implement, test, and review code changes from a natural language description. It orchestrates four specialized AI agents that collaborate in sequence, with a self-healing loop that automatically fixes failing tests.

You describe what you want built. Forja writes the code, tests it, reviews it, and commits it to a branch.

## How It Works

```
Natural Language Description
        |
        v
  [Spec Generator]  -- Claude converts description to structured YAML spec
        |
        v
  [1. Planner]       -- Analyzes repo, produces implementation plan (read-only)
        |
        v
  [2. Coder]         -- Implements the plan by editing files, runs dotnet build
        |
        v
  [3. Tester]        -- Writes tests from spec ONLY (blind), runs dotnet test
        |                    |
        |               fail + attempts left --> back to Coder with test output
        |
        v
  [4. Reviewer]      -- Reviews code quality, security, spec compliance
        |
        v
  git commit + push
```

### The Four Agents

| Agent | Role | Sees | Writes Code? |
|-------|------|------|:---:|
| **Planner** | Analyze codebase, create implementation plan | Spec + file tree | No |
| **Coder** | Implement the plan (or fix test failures) | Spec + plan + healing feedback | Yes |
| **Tester** | Write and run tests against requirements | Spec + git diff (**blind** -- no plan/coder) | Yes (tests) |
| **Reviewer** | Code review for quality/security/compliance | Spec + plan + tests + diff | No |

### Blind Testing

The Tester agent deliberately never sees the Planner's plan or the Coder's reasoning. It only sees the **spec requirements** and the **git diff**. This prevents implementation bias -- it tests what was *requested*, not what was *built*.

### Self-Healing Loop

When tests fail, the Coder receives the full test output and tries to fix the implementation. The Tester then runs blind again. This loop repeats up to 3 times (configurable). The Coder never modifies tests -- only the implementation.

## Architecture

```
Forja/
├── src/
│   ├── Forja.Core/              # Core logic (no web dependencies)
│   │   ├── Agents/              # PlannerAgent, CoderAgent, TesterAgent, ReviewerAgent
│   │   ├── Models/              # Spec, PipelineRun, AgentContext, StageResult, AppConfig
│   │   └── Services/            # PipelineOrchestrator, ClaudeCliRunner, GitService,
│   │                            # SpecGenerator, DotnetTestRunner, JsonPipelineRunStore
│   └── Forja.Web/               # ASP.NET web API + UI
│       ├── Endpoints/           # REST API (pipeline start/status/history, spec generate)
│       ├── Hubs/                # SignalR hub for real-time updates
│       ├── Services/            # SignalRPipelineNotifier
│       └── wwwroot/             # Single-page frontend (vanilla JS + SignalR)
└── tests/
    └── Forja.Core.Tests/        # 70 unit + integration tests
        ├── Agents/              # Agent prompt building, success/failure paths
        ├── Models/              # Model defaults, computed properties
        ├── Services/            # Orchestrator flow, SpecGenerator YAML parsing, persistence
        └── Integration/         # Real git repo operations in temp directories
```

### Key Design Decisions

- **Claude CLI as subprocess**: Each agent invokes `claude -p` as an external process. Prompts are piped via stdin to avoid Windows command line length limits. The `CLAUDECODE` env var is stripped to prevent nested session detection.
- **Git-native**: All git operations use `git` CLI directly (no libgit2). Branch names are auto-deduplicated. Base branch is auto-detected (main/master/develop).
- **Process isolation**: Each Claude invocation is a fresh process with its own context. No shared state between agents.
- **Non-blocking UI**: Pipeline execution runs in background `Task.Run`. The API returns immediately with a `runId`. Progress streams via SignalR WebSocket.
- **Persistence**: Pipeline runs are stored as JSON on disk with an in-memory cache. Survives server restarts.

## Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code) installed globally via npm
- Git

## Configuration

Edit `src/Forja.Web/appsettings.json`:

```json
{
  "Urls": "http://localhost:5200",
  "Forja": {
    "Claude": {
      "CliPath": "C:\\Users\\you\\AppData\\Roaming\\npm\\claude.cmd",
      "TimeoutMinutes": 15
    },
    "Git": {
      "BaseBranch": "main",
      "BranchPrefix": "forja",
      "AutoPush": true
    },
    "Pipeline": {
      "MaxHealingAttempts": 3
    }
  }
}
```

## Running

```bash
cd src/Forja.Web
dotnet run
```

Open http://localhost:5200 in your browser. Enter a description of what you want to build and the path to the target repository.

## API

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/pipeline/start` | Start a pipeline run `{description?, yaml?, repoPath}` |
| `GET` | `/api/pipeline/{runId}` | Get run status |
| `GET` | `/api/pipeline/history` | Last 20 runs |
| `POST` | `/api/spec/generate` | Generate spec without running pipeline `{description, repoPath}` |
| `POST` | `/api/repo/init` | Initialize a new git repo `{repoPath}` |

Real-time updates available via SignalR at `/hubs/pipeline`.

## Testing

```bash
dotnet test
```

70 tests covering:
- All agent prompt construction and result handling
- Pipeline orchestrator flow (success, failures, healing loop, cleanup)
- YAML spec parsing (preamble/postamble stripping, markdown fences, round-trips)
- JSON persistence (save, load, reload from disk, corrupt file handling)
- Git operations against real temporary repositories

## License

MIT
