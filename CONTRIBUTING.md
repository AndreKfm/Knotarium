# Contributing

Thanks for your interest in Knotarium!

## Expectations first

This is an early-stage project maintained on a **best-effort, spare-time basis**. That means:

- Issues and pull requests are **welcome**, but there are **no guarantees** on response time, and not every
  PR will be merged — direction and scope are still settling.
- **Open an issue to discuss before starting anything large.** A quick "is this wanted?" saves everyone time.
- Small, focused changes are much easier to review than sweeping ones.

## Getting set up

See the [README](README.md) for how to run the app (Docker or from source). To run the checks:

```bash
# Backend tests (run from the Backend/ folder, where the solution lives)
cd Backend && dotnet test

# Frontend tests + type check
cd Frontend && npm install && npm test && npm run build
```

## Pull requests

- Branch off `main`; keep the change focused.
- **Keep the build and tests green.** Add or update tests for behavior you change.
- Match the style of the surrounding code (naming, comments, structure).
- Describe *what* changed and *why* in the PR body.

## Licensing of contributions

No CLA, and no per-commit sign-off. Per **Apache-2.0 §5**, any contribution intentionally submitted for
inclusion is licensed under the project's [Apache-2.0 License](LICENSE) automatically, unless explicitly
stated otherwise. Opening a pull request is your statement that you wrote the change, or otherwise have the
right to submit it under that license.

### AI-assisted contributions

AI-assisted contributions are **welcome** — much of this project is itself agent-written. The rule above
applies unchanged: *you* — the human submitter — have the right to submit the change under Apache-2.0 and
take responsibility for it, whatever tools produced it. Review what you send; you are vouching for it, not
the tool.

## Reporting bugs vs. security issues

- Ordinary bugs → open a **GitHub issue**.
- **Security vulnerabilities → do not open a public issue.** Follow [SECURITY.md](SECURITY.md).

## Code of Conduct

Participation is governed by our [Code of Conduct](CODE_OF_CONDUCT.md).
