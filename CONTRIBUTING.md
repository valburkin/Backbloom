# Contributing to Backbloom

Thank you for helping. Bug reports, hardware reports, ideas, docs fixes and pull requests are all welcome. This page
explains where to start and what a pull request needs.

## Before you start

- **Questions and ideas**: start a [discussion](https://github.com/valburkin/Backbloom/discussions).
- **Bugs and hardware reports**: search the [open issues](https://github.com/valburkin/Backbloom/issues) first and add
  to an existing one if it matches; otherwise open an [issue](https://github.com/valburkin/Backbloom/issues/new/choose)
  with the form that fits (in the app, Settings > About > Report a problem fills in your system report). Attach the
  log files (`%LOCALAPPDATA%\Backbloom\logs`) after looking through them: issues are public.
- **Larger changes** (a new device driver, a new page, a change to the protocol or the settings schema): open an
  issue first to agree on the approach. [docs/roadmap.md](docs/roadmap.md) lists what's planned.
- **Security issues**: don't open a public issue; see [SECURITY.md](SECURITY.md).

Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## Development setup

You need Windows 10 1809 or later, the .NET SDK pinned by [backend/global.json](backend/global.json) and Node.js 24.
[docs/development.md](docs/development.md) has every command, how to run a development instance safely next to a real
setup, and how the tests are organized. Read [docs/architecture.md](docs/architecture.md) before changing the backend
and [docs/ui-guidelines.md](docs/ui-guidelines.md) before changing the UI.

```powershell
cd ui; npm ci; npx tsc --noEmit; npm test; npm run build
cd ../backend; dotnet build -warnaserror; dotnet test
```

## Pull requests

1. Branch from `main` (`feat/<topic>`, `fix/<topic>`), one feature or fix per pull request.
2. Keep the build and tests green: `dotnet build -warnaserror`, `dotnet test`, and in `ui/` the type check, tests and
   build. CI runs all of them.
3. Add tests for what you change. Device drivers are tested against fakes on `127.0.0.1`; tests never listen on
   another address or contact real hardware.
4. Update the docs that describe what you changed (`docs/`, the backend reference in `docs/backend/`), and add a
   line under `## Unreleased` in [CHANGELOG.md](CHANGELOG.md) for anything users notice, written for them ("Fixed
   flicker when switching monitors", not "Refactor the frame smoother").
5. Commit messages: a summary line in the imperative, a blank line, then what changed and why.

Pull requests are squash-merged, so the summary of the pull request becomes the commit on `main`.

## Conventions

- C# latest with nullable reference types, analyzers at `latest-recommended`, warnings as errors, code style enforced
  by the build ([backend/.editorconfig](backend/.editorconfig)).
- Project dependency rules are in [docs/architecture.md](docs/architecture.md#projects-and-dependency-rules):
  services are composed only in `Backbloom.App`, platform code lives only in `*.Windows` projects.
- The per-frame path doesn't allocate; allocation tests guard it.
- Logging goes through `[LoggerMessage]` methods.
- Code ported from HyperHDR names its source file in a doc comment and keeps its attribution.
- Golden fixtures in `backend/tests/fixtures` are frozen; change one only for a deliberate deviation and document it in
  [its README](backend/tests/fixtures/README.md).
- New dependencies must be under a permissive license (fonts may be OFL-1.1). After adding one, run
  `packaging/Update-ThirdPartyNotices.ps1`.
- American English in code, comments, docs and commit messages.

## AI-assisted contributions

Contributions written with AI coding assistants are welcome on the same terms as any other: you understand every line
you submit, you have built and tested it, and the pull request says what was tried on real hardware. Assistants can
read [CLAUDE.md](CLAUDE.md) for the project's rules.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
