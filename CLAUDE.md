# Backbloom: notes for AI-assisted work

Ambient lighting app for Windows (screen/sound -> LED strips). C# / .NET 10 backend in `backend/`, React +
TypeScript UI in `ui/`, installer in `packaging/`, docs in `docs/`, landing page in `site/`. Read
`backend/README.md` before changing the backend and `docs/architecture.md` for the layering. UI changes follow
`docs/ui-guidelines.md` (sections, setting tiers, words, visual rules). Contribution rules: `CONTRIBUTING.md`.

## Build and test

```powershell
cd backend; dotnet build -warnaserror; dotnet test     # .NET SDK pinned by backend/global.json
cd ui; npm ci; npx tsc --noEmit; npm test; npm run build
./packaging/build.ps1        # installer -> artifacts/releases (doesn't install anything)
```

Done means: `dotnet build -warnaserror` and `dotnet test` green, UI type check, tests and build green, docs updated
for what changed (`backend/README.md`, `docs/`), and an entry under `## Unreleased` in `CHANGELOG.md` for
user-facing changes.

## Safety on a developer's machine

- The developer may run HyperHDR (ports 8090/8092/19400/19444) or an installed Backbloom (port 18290, data
  `%LOCALAPPDATA%\Backbloom`, install folder `%LOCALAPPDATA%\BackbloomApp`) driving real lights. Never stop, modify or
  bind any of them, never touch their data, never contact a real LED controller, never run the installer or register
  autostart.
- Test instances: a data directory of their own, `--legacy-data-dir none`, `--capture synthetic`, the default `file`
  driver (null device), a free port (not 8090/8092/18290), and `--bind 127.0.0.1` (the default for builds). Stop them
  afterwards (`backbloom --quit --data-dir <dir>`).
- Nothing may listen on a non-loopback address in tests or dev runs (Windows Firewall would prompt). Fakes bind to
  127.0.0.1; `NetworkListenerGuard` fails test assemblies that don't.

## Conventions

- Project dependency rules, DI only in `Backbloom.App`, platform code only in `*.Windows` projects, no allocations on
  the per-frame path (allocation tests), `[LoggerMessage]` logging, nullable and analyzers on, warnings are errors.
- Golden fixtures in `backend/tests/fixtures` are frozen references from HyperHDR's C++ code. Change expectations only
  for a deliberate deviation, documented in `backend/tests/fixtures/README.md`.
- Ported code keeps its attribution: doc comments name the HyperHDR source file; the copyright notices in `LICENSE`
  stay. New dependencies must be permissively licensed (fonts may be OFL-1.1); run
  `packaging/Update-ThirdPartyNotices.ps1`.
- Branches and releases (details in `docs/development.md`): work on a branch (`feat/...`, `fix/...`), squash-merge
  into `main` through a pull request with its `CHANGELOG.md` entry. Merging doesn't release; releases are cut by
  pushing a `v<version>` tag after setting the version in `backend/Directory.Build.props` (the only place it is set).
- American English in code, comments, docs and commit messages. Commits: a summary line, then what and why.
