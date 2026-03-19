# Repository Guide

## Overview
- `sgr.slnx` is the primary solution file. Use it for restore and build.
- `src/Alkampfer.Sgr` is the reusable library that implements schema-guided reasoning primitives and models.
- `src/Alkampfer.Sgr.Playground` is the interactive console app used to exercise the library against Azure OpenAI and local SQL Server scenarios.

## Prerequisites
- .NET SDK 10 or newer is recommended. The projects target `net9.0`.
- Access to Azure OpenAI for the playground app.
- Optional local SQL Server for the SQL examples. The code defaults to localhost with integrated security.

## Common Commands
- Restore/build solution: `dotnet build sgr.slnx -c Debug`
- Run tests: `dotnet test sgr.slnx -c Debug`
- Run playground: `dotnet run --project src/Alkampfer.Sgr.Playground/Alkampfer.Sgr.Playground.csproj`
- Build library only: `dotnet build src/Alkampfer.Sgr/Alkampfer.Sgr.csproj -c Debug`

## Environment
- The playground walks up from the current working directory and uses the first valid `.env` it finds in the parent chain.
- A `.env` is considered valid when it contains non-empty values for `OPENAI_API_KEY` and `AZURE_ENDPOINT`.
- You can keep a shared `.env` in a parent folder if you want multiple repos to reuse the same local configuration.
- Required variables:
  - `OPENAI_API_KEY`
  - `AZURE_ENDPOINT`

## Notes For Agents
- Prefer changing shared reasoning logic in `src/Alkampfer.Sgr` unless the behavior is playground-specific.
- Keep `sgr.slnx` as the canonical solution file; do not introduce a legacy `.sln` unless explicitly requested.
- The repo includes a test project in `src/Alkampfer.Sgr.Tests`. Use `dotnet build` as the minimum verification step and run `dotnet test sgr.slnx -c Debug` when changes could affect behavior.
- Do not consider a task complete if the solution does not compile or if any test fails. If build or test failures remain, report that explicitly as an open issue.
- Current build is clean for errors but has existing compiler warnings; do not treat the repo as warning-free.
- SonarCloud project key: `alkampfergit_sgr`.
