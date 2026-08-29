# Contributing

Thanks for improving Vessel. Specs live in [`docs/`](docs/), with the technical design
in [`docs/architecture.md`](docs/architecture.md).

```bash
dotnet build Vessel.sln
dotnet test Vessel.sln
cd frontend && npm ci && npm test && npm run build && npm run lint
```

Please read [`AGENTS.md`](AGENTS.md) before changing code: keep changes in scope, update
every call site, preserve tests, and never commit from an automated contribution.
