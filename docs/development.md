# Development guide

## Tools

- .NET SDK 10.0.402 or a later 10.0 feature band (`backend/global.json`). On the development machine it lives in
  `C:\Code\deps\dotnet` (the machine-wide SDK is older):

  ```powershell
  $env:DOTNET_ROOT = 'C:\Code\deps\dotnet'
  $env:PATH = "C:\Code\deps\dotnet;$env:PATH"
  ```

- Node.js 24 with npm (UI).
- Optional: the Windows SDK's `fxc` to recompile `Shaders/Downscale.hlsl` (the bytecode is checked in), Python with
  Pillow to regenerate the icons (`branding/generate_icons.py`).

## Everyday commands

```powershell
cd backend
dotnet build -warnaserror          # warnings are errors, analyzers and code style enforced
dotnet test                        # every test project (xUnit v3)
dotnet test tests/Backbloom.Core.Tests --filter "FullyQualifiedName~Smoothing"

cd ../ui
npm ci
npx tsc --noEmit
npm test                           # vitest
npm run dev                        # Vite dev server; open http://localhost:5173/?backend=127.0.0.1:18290
                                   # ?mock (demo), ?mock=welcome (first run of a new install), ?mock=welcome-imported
                                   # (first run after a HyperHDR import), ?mock=hyperhdr (the same, HyperHDR running),
                                   # ?mock=imported (no layout saved yet), ?mock=offline (a device can't be reached),
                                   # ?mock=auth (another device, signs in)
npm run build                      # ui/dist, copied next to the app by the backend build
```

## Running a development instance

```powershell
cd backend
dotnet run --project src/Backbloom.App -c Release -- --capture synthetic --port 18290 --data-dir .local/dev --legacy-data-dir none
```

- A build from the source tree uses `backend/.local/data` unless `--data-dir` says otherwise, listens on 127.0.0.1
  only, and doesn't import HyperHDR settings unless `--legacy-data-dir` points somewhere.
- `--capture synthetic` drives the LEDs with a test pattern instead of the screen; a fresh data directory uses the
  `file` driver writing to the null device, so no real lights are touched.
- `--service` runs without tray and window (logs in the console); `--quit --data-dir <dir>` stops an instance.
- Several instances run side by side with different data directories and ports.

When a real setup is running on the same computer (HyperHDR on 8090/8092/19400/19444, or an installed Backbloom on
18290), use other ports, a data directory of your own and `--legacy-data-dir none`, and never point a test instance
at a real LED controller.

## Tests

- Golden fixtures in `backend/tests/fixtures` pin the behavior ported from HyperHDR. They are frozen (the C++ exporter
  is gone); change one only for a deliberate deviation and document it in its README.
- Allocation tests guard the hot path; new per-frame code must not allocate.
- Tests never listen on the network: hosts use `--bind 127.0.0.1` (or the default, loopback only; remote access tests
  use 127.0.0.2 as the "network" address; the server tests' `X-Test-Peer` header, read by a test-only startup filter,
  makes a request look like it came from another device), fakes bind to loopback, and the `NetworkListenerGuard` assembly fixture
  fails any test assembly whose process listens on another address (Windows Firewall would prompt for each test
  executable).
- HyperHDR runs on the development machine, so test hosts replace the process list with `FakeProcessList`
  (`tests/Shared`) and complete the first run in the data directory they create; otherwise their LEDs would be held.
- Tests that need hardware (desktop capture, sound devices) skip when it isn't available.

## Code conventions

- C# latest, nullable, file-scoped namespaces, `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`, code
  style enforced in the build (`backend/.editorconfig`).
- Dependency rules between projects: see [architecture.md](architecture.md). Services are created only in
  `Backbloom.App` (constructor injection, no static singletons). Platform code only in `*.Windows` projects.
- Logging through `[LoggerMessage]` methods.
- Ported code names its HyperHDR source file in its doc comments (`sources/...` paths refer to the C++ code at commit
  `9f49bd2`) and keeps the MIT attribution.
- American English everywhere. Commit messages are plain: a summary line, then what and why.

## Branches and releases

- **Work on a branch.** Every feature or fix gets its own branch from `main` (`feat/<topic>`, `fix/<topic>`), or its
  own worktree when several agents work at once. Commit there as often as needed; the version doesn't change.
- **Try it.** `./packaging/build.ps1 -Dev` builds an installer `<next patch>-dev.<date>.<time>` from the working tree
  without changing any file; each one installs over the previous one.
- **Merge when done.** When build, tests and docs are green and the change has been tried, squash-merge the branch
  into `main` (one commit per feature or fix) and delete it. The merge adds the change to `## Unreleased` in
  [CHANGELOG.md](../CHANGELOG.md): one line per change under Added, Changed, Fixed or Removed, written for users ("Fixed
  flicker when switching monitors", not "Refactor the frame smoother").
- **Release when ready.** `main` accumulates merged changes; a release happens only when decided:
  1. set `<Version>` in `backend/Directory.Build.props` ([SemVer](https://semver.org/): new features raise the minor
     version, fixes only the patch);
  2. rename `## Unreleased` to `## <version> – <yyyy-MM-dd>` and add a new empty `## Unreleased` above it;
  3. commit ("Release <version>"), tag it `v<version>` (annotated), and build the installer with
     `./packaging/build.ps1`.

  The version section of the changelog is the release note.
- **Urgent fix after a release** while `main` has unreleased changes: branch from the release tag, fix, release as the
  next patch from that branch, then merge the fix into `main` too.

## Third-party notices

After adding or updating a NuGet package used by the app or an npm production dependency, run
`packaging/Update-ThirdPartyNotices.ps1` (it fails on non-permissive licenses) and commit `THIRD-PARTY-NOTICES.md` and
`3RD_PARTY_LICENSES`. CI checks that they are up to date.

## Packaging

See [packaging/README.md](../packaging/README.md).
