# Development guide

How to build, test and run Backbloom from source, the code conventions, and how changes move from a branch to a
release. Read [architecture.md](architecture.md) for how the code is organized and [CONTRIBUTING.md](../CONTRIBUTING.md)
for how to propose a change.

## Contents

- [Tools](#tools)
- [Everyday commands](#everyday-commands)
- [Running a development instance](#running-a-development-instance)
- [Tests](#tests)
- [Coverage](#coverage)
- [Code conventions](#code-conventions)
- [Branches and releases](#branches-and-releases)
- [Third-party notices](#third-party-notices)
- [Packaging](#packaging)

## Tools

- **.NET SDK 10.0.402** or a later 10.0 feature band (pinned by `backend/global.json`). If the machine-wide SDK is
  older, install this one side by side and put it first:

  ```powershell
  $env:DOTNET_ROOT = '<sdk folder>'
  $env:PATH = "<sdk folder>;$env:PATH"
  ```

- **Node.js 24** with npm (UI and landing page).
- Optional: the Windows SDK's `fxc` to recompile `Shaders/Downscale.hlsl` (the bytecode is checked in), and Python with
  Pillow to regenerate the icons (`branding/generate_icons.py`).

## Everyday commands

```powershell
cd backend
dotnet build -warnaserror          # warnings are errors, analyzers and code style enforced
dotnet test                        # every test project (xUnit v3 on Microsoft.Testing.Platform)
dotnet test --project tests/Backbloom.Core.Tests -- --filter "FullyQualifiedName~Smoothing"

cd ../ui
npm ci
npx tsc --noEmit
npm test                           # Vitest
npm run dev                        # Vite dev server; open http://localhost:5173/?backend=127.0.0.1:18290
npm run build                      # ui/dist, copied next to the app by the backend build

cd ../site                         # the landing page, see site/README.md
npm ci
npm run dev                        # http://127.0.0.1:5190
npm run build                      # type check + site/dist
```

Run `dotnet test` in `backend/`: its `global.json` switches it to Microsoft.Testing.Platform, which xUnit v3 4
needs; from another folder `dotnet test` falls back to VSTest and fails. It runs the test projects in parallel; on a
machine with few cores, add `--max-parallel-test-modules 4` (as CI does), since a few tests have deadlines that an
overloaded CPU misses.

### UI without a backend

The UI has a built-in mock backend. Open the dev server with one of these query strings instead of `?backend=...`:

| URL | Shows |
|---|---|
| `?mock` | the demo |
| `?mock=welcome` | the first run of a new install |
| `?mock=welcome-imported` | the first run after a HyperHDR import |
| `?mock=hyperhdr` | the same, with HyperHDR running |
| `?mock=imported` | no layout saved yet |
| `?mock=offline` | a device that can't be reached |
| `?mock=auth` | another device, which signs in |

The guided hardware setup has more mock variants; see
[guided-setup.md](guided-setup.md#trying-it-without-hardware).

## Running a development instance

```powershell
cd backend
dotnet run --project src/Backbloom.App -c Release -- --capture synthetic --port 18290 --data-dir .local/dev --legacy-data-dir none
```

- A build from the source tree uses `backend/.local/data` unless `--data-dir` says otherwise, listens on 127.0.0.1
  only, and doesn't import HyperHDR settings unless `--legacy-data-dir` points somewhere.
- `--capture synthetic` drives the LEDs with a test pattern instead of the screen; a fresh data directory uses the
  `file` driver writing to the null device, so no real lights are touched.
- `--capture-image <file>` drives them with a still picture instead (binary PPM, for example
  `magick in.png out.ppm`), so screenshots and demos show known colors, like the landing page's matching its photos.
- `--service` runs without tray and window (logs in the console); `--quit --data-dir <dir>` stops an instance.
- Several instances run side by side with different data directories and ports.

Every option is listed in [Running the backend](backend/running.md#command-line-options).

> **Next to a real setup.** When a real setup runs on the same computer (HyperHDR on 8090/8092/19400/19444, or an
> installed Backbloom on 18290), use other ports, a data directory of your own and `--legacy-data-dir none`, and never
> point a test instance at a real LED controller.

## Tests

- Golden fixtures in `backend/tests/fixtures` pin the behavior ported from HyperHDR. They are frozen (the C++ exporter
  is gone); change one only for a deliberate deviation and document it in its README.
- Allocation tests guard the hot path; new per-frame code must not allocate.
- Tests never listen on the network: hosts use `--bind 127.0.0.1` (or the default, loopback only; remote access tests
  use 127.0.0.2 as the "network" address; the server tests' `X-Test-Peer` header, read by a test-only startup filter,
  makes a request look like it came from another device), fakes bind to loopback, and the `NetworkListenerGuard` assembly fixture
  fails any test assembly whose process listens on another address (Windows Firewall would prompt for each test
  executable).
- HyperHDR may run on a development machine, so test hosts replace the process list with `FakeProcessList`
  (`tests/Shared`) and complete the first run in the data directory they create; otherwise their LEDs would be held.
- Tests that need hardware (desktop capture, sound devices) skip when it isn't available.

## Coverage

```powershell
cd backend
dotnet test --coverage --coverage-output-format cobertura --coverage-settings coverage.config --results-directory TestResults
# one report per test project: TestResults/<guid>.cobertura.xml

cd ../ui
npm run coverage                   # text summary; ui/coverage/lcov.info and cobertura-coverage.xml
```

- The backend counts the product assemblies only (`backbloom.dll` and `Backbloom.*.dll`): no test assemblies, no
  TerraFX, no source-generated code (`[LoggerMessage]`, regexes, JSON contexts), no auto-properties.
- Code that can't run in a test without touching hardware or the user's system (real HID and serial devices, the
  mDNS responder on port 5353, Wi-Fi, the tray icon, the WebView2 window, the installer hooks) is marked
  `[ExcludeFromCodeCoverage(Justification = "...")]`; the logic around it is tested through its interfaces.
- For one merged HTML report: `dotnet tool install -g dotnet-reportgenerator-globaltool`, then
  `reportgenerator -reports:"TestResults/*.cobertura.xml" -targetdir:TestResults/report`.
- The UI's coverage covers `src/` except tests, the dev-only mock backend and type-only files; `npm run coverage`
  fails below the floors set in `ui/vite.config.ts` (a little under the measured numbers).
- On Windows, run the UI's coverage from a path whose drive letter is upper case (`C:\...`, as PowerShell and
  Explorer give it). A terminal opened at `c:\...` makes Vitest miss the source files, and the numbers drop far below
  the floors.

## Code conventions

- C# latest, nullable, file-scoped namespaces, `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`, code style
  enforced in the build (`backend/.editorconfig`).
- Dependency rules between projects: see [architecture.md](architecture.md#projects-and-dependency-rules). Services are
  created only in `Backbloom.App` (constructor injection, no static singletons). Platform code lives only in
  `*.Windows` projects.
- Logging goes through `[LoggerMessage]` methods.
- Ported code names its HyperHDR source file in its doc comments (`sources/...` paths refer to the C++ code at commit
  `5f70189`) and keeps the MIT attribution.
- UI changes follow the [UI guidelines](ui-guidelines.md).
- American English everywhere. Commit messages are plain: a summary line, then what and why.

## Branches and releases

1. **Work on a branch.** Every feature or fix gets its own branch from `main` (`feat/<topic>`, `fix/<topic>`). Commit
   there as often as needed; the version doesn't change.
2. **Try it.** `./packaging/build.ps1 -Dev` builds an installer `<next patch>-dev.<date>.<time>` from the working tree
   without changing any file; each one installs over the previous one.
3. **Merge when done.** Open a pull request. When CI, docs and review are green and the change has been tried, it is
   squash-merged into `main` (one commit per feature or fix) and the branch is deleted. The merge adds the change to
   `## Unreleased` in [CHANGELOG.md](../CHANGELOG.md): one line per change under Added, Changed, Fixed or Removed,
   written for users ("Fixed flicker when switching monitors", not "Refactor the frame smoother").
4. **Release when ready.** `main` accumulates merged changes; a release happens only when decided:
   1. set `<Version>` in `backend/Directory.Build.props` ([SemVer](https://semver.org/): new features raise the minor
      version, fixes only the patch);
   2. rename `## Unreleased` to `## <version> – <yyyy-MM-dd>` and add a new empty `## Unreleased` above it;
   3. commit ("Release &lt;version&gt;"), tag it `v<version>` (annotated) and push the tag. The
      [release workflow](../.github/workflows/release.yml) checks the tag against the version, tests, builds the
      installer and publishes it on GitHub Releases, where installed copies find the update.

   The version section of the changelog is the release note (`packaging/Get-ReleaseNotes.ps1` extracts it).

**Urgent fix after a release** while `main` has unreleased changes: branch from the release tag, fix, release as the
next patch from that branch, then merge the fix into `main` too.

## Third-party notices

After adding or updating a NuGet package used by the app or an npm production dependency, run
`packaging/Update-ThirdPartyNotices.ps1` (it fails on non-permissive licenses; fonts may also be OFL-1.1) and commit
`THIRD-PARTY-NOTICES.md` and `3RD_PARTY_LICENSES`. CI checks that they are up to date, so a Dependabot pull request that
updates such a package fails until the script has run on its branch. Packages that hold only type definitions (such as
`@types/react`) are left out: nothing of theirs is bundled. Vite and Rolldown, build tools, are listed (major version
only) because the bundle carries their small runtime helpers. Dependabot runs once a month, with its rules in `.github/dependabot.yml`;
packages of the .NET release itself (`Microsoft.Extensions.*`, `System.*`, `Microsoft.Data.Sqlite`) keep the target
framework's major version.

The landing page in `site/` isn't shipped with the app, so its dependencies don't go into these files (they must still
be permissively licensed); it lists them in `site/public/licenses.txt`.

## Packaging

Building the installer, updates and release publishing: see [packaging/README.md](../packaging/README.md).
