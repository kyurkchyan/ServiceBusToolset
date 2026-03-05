# CI/CD for a .NET CLI Tool: A Complete GitHub Actions Pipeline

## What CI/CD Is and Why It Matters

CI/CD stands for Continuous Integration and Continuous Delivery. The words get thrown around a lot, but the core idea is simple: automate the path from code change to shipped software, and make that path enforce quality at every step.

**Continuous Integration** means every code change is automatically built, tested, and validated. Instead of "I'll run the tests before the release," the tests run on every commit. Instead of "we'll review the formatting later," the formatter runs on every pull request. The goal is to catch problems *when they're cheap to fix* — minutes after they're introduced, not weeks later when nobody remembers the context.

**Continuous Delivery** means the software is always in a releasable state. When you push to `master`, an alpha package is automatically built and published. When you tag a release, the stable package ships. There's no manual "build the release" step that somebody has to remember, no "works on my machine but not in production" surprises.

Without CI/CD, you rely on human discipline: remember to run tests, remember to check formatting, remember to update all package versions, remember to build in Release mode. Humans forget. Automation doesn't.

This article walks through the complete CI/CD pipeline for ServiceBusToolset — a .NET CLI tool for managing Azure Service Bus. The pipeline covers pull request validation, alpha releases, stable releases, automated dependency updates, and AI-assisted code review. Every design decision is explained in context, from "why does this YAML line exist" to "what industry practice does this implement."

## Pipeline Architecture Overview

The CI/CD system comprises six components:

```
                     ┌──────────────────────┐
                     │   Pull Request        │
                     └──────────┬───────────┘
                                │
               ┌────────────────┼────────────────┐
               │                │                │
        ┌──────▼──────┐  ┌─────▼──────┐  ┌──────▼──────┐
        │  PR          │  │ CodeRabbit  │  │ Dependabot  │
        │  Validation  │  │ AI Review   │  │ Updates     │
        └──────┬──────┘  └────────────┘  └─────────────┘
               │
               │ merge to master
               │
        ┌──────▼──────┐
        │   Alpha      │
        │   Release    │
        └──────┬──────┘
               │
               │ git tag v*.*.*
               │
        ┌──────▼──────┐
        │   Stable     │
        │   Release    │
        └─────────────┘
```

| Component | File | Trigger | Purpose |
|---|---|---|---|
| PR Validation | `pr-validation.yml` | Pull request to `master` | Quality gate: build, format, vulnerabilities, tests, coverage |
| Alpha Release | `alpha-release.yml` | Push to `master` | Auto-publish pre-release NuGet package |
| Stable Release | `stable-release.yml` | Tag `v*.*.*` | Publish stable NuGet package + GitHub Release with SBOM |
| Shared Setup | `actions/setup-dotnet-build/action.yml` | Reused by all workflows | .NET SDK setup, NuGet caching, restore |
| Dependabot | `dependabot.yml` | Weekly schedule | Auto-update NuGet packages and GitHub Actions |
| CodeRabbit | `.coderabbit.yaml` | Pull request | AI-powered code review with project-specific context |

## The Shared Foundation: Composite Action

Before diving into workflows, it's worth understanding the shared setup that all three workflows reuse. Without it, every workflow would duplicate the same 15 lines for SDK setup, caching, and restore.

```yaml
# .github/actions/setup-dotnet-build/action.yml
name: Setup .NET & Restore
description: Setup .NET SDK, cache NuGet packages, and restore dependencies.

inputs:
  dotnet-version:
    description: '.NET SDK version to install'
    required: false
    default: '10.0.x'

runs:
  using: composite
  steps:
    - name: Setup .NET
      uses: actions/setup-dotnet@baa11fbfe1d6520db94683bd5c7a3818018e4309 # v5.1.0
      with:
        dotnet-version: ${{ inputs.dotnet-version }}

    - name: Cache NuGet packages
      uses: actions/cache@cdf6c1fa76f9f475f3d7449005a359c84ca0f306 # v5.0.3
      with:
        path: ~/.nuget/packages
        key: nuget-${{ hashFiles('**/*.csproj', '**/Directory.Packages.props') }}
        restore-keys: nuget-

    - name: Restore dependencies
      run: dotnet restore
      shell: bash
```

### Why this exists

This is a **composite action** — GitHub's mechanism for creating reusable workflow fragments. Instead of copying the same three steps into every workflow, each workflow uses a single line:

```yaml
- name: Setup .NET & Restore
  uses: ./.github/actions/setup-dotnet-build
```

The `./.github/actions/` path tells GitHub Actions to look for the action *in this repository*, not in a remote one. This avoids an external dependency for core setup logic.

### Cache strategy

The cache key deserves attention:

```yaml
key: nuget-${{ hashFiles('**/*.csproj', '**/Directory.Packages.props') }}
restore-keys: nuget-
```

The key hashes two sets of files:
- `**/*.csproj` — which packages are referenced
- `**/Directory.Packages.props` — which versions those packages use (via Central Package Management)

If either changes — a new package is added, or an existing one is upgraded — the hash changes and NuGet does a fresh restore. The `restore-keys: nuget-` fallback enables **partial cache hits**: if only one package changed, the previous cache still contains every other package. NuGet downloads only the diff instead of everything from scratch.

Without caching, `dotnet restore` downloads ~180 MB of packages on every run. With a warm cache, it downloads zero — saving 15-30 seconds per workflow execution.

### The `.x` version pattern

`dotnet-version: '10.0.x'` installs the latest patch version of .NET 10.0. This is deliberate: you want automatic security patches (10.0.1, 10.0.2, etc.) without manually updating the workflow every time Microsoft releases one. The `x` wildcard only floats within the patch segment, so you won't accidentally jump to .NET 11.

## PR Validation: The Quality Gate

The PR validation pipeline is the most important workflow. It's the gate that separates "someone pushed code" from "this code is safe to merge." Every pull request targeting `master` must pass this pipeline before merging.

### Trigger configuration

```yaml
on:
  pull_request:
    branches: [master]
    paths-ignore:
      - '**.md'
      - 'docs/**'
      - '.coderabbit.yaml'
```

**`branches: [master]`** — only PRs targeting `master` trigger the pipeline. Feature branches don't get validated until they're proposed for merging. This saves CI minutes on experimental work.

**`paths-ignore`** — documentation-only changes skip CI entirely. Editing a README or adding a docs article doesn't need a build + test cycle. The pattern `**.md` catches markdown files at any depth, `docs/**` catches the documentation directory, and `.coderabbit.yaml` is the AI reviewer config.

This is a direct application of the **fast feedback principle**: if a change can't break the build, don't make the author wait for the build.

### Concurrency control

```yaml
concurrency:
  group: pr-${{ github.event.pull_request.number }}
  cancel-in-progress: true
```

Without this, pushing 5 quick commits to a PR creates 5 parallel workflow runs. The first 4 are waste — they validate commits that are already superseded. With `cancel-in-progress: true`, each new push cancels the previous run. The concurrency group is keyed on the PR number, so different PRs run independently.

This is both a **cost optimization** (fewer wasted CI minutes) and a **feedback optimization** (the developer sees results for their latest commit, not a stale one).

### Least-privilege permissions

```yaml
permissions:
  contents: read
  pull-requests: write
  checks: write
```

By default, the `GITHUB_TOKEN` in a workflow gets broad permissions. This explicit declaration overrides that:

- `contents: read` — the workflow can read the repository but not push code, create branches, or modify tags. Even if a third-party action is compromised, it can't alter the repository.
- `pull-requests: write` — needed to post coverage comments on the PR.
- `checks: write` — needed for the test reporter to create check runs with individual test results.

This implements the **principle of least privilege**: grant only the permissions actually needed, nothing more. See the [pipeline hardening article](cicd-pipeline-hardening.md) for a deep dive on why this matters.

### Job 1: Authorization Gate

```yaml
authorize:
  name: Authorize
  runs-on: ubuntu-latest
  environment: ${{ github.event.pull_request.user.login != 'kyurkchyan' && 'pr-approval' || '' }}
  steps:
    - run: echo "PR authorized"
```

This is a **fork safety mechanism**. GitHub Actions workflows triggered by `pull_request` events from forks run with read-only permissions and no access to secrets — by default. But the workflow still consumes CI minutes on the repository owner's account.

The `environment` conditional works as follows:
- If the PR author is the repository owner (`kyurkchyan`), the environment is empty (`''`) and the job runs immediately.
- If the PR author is anyone else, the job requires the `pr-approval` environment, which can be configured with required reviewers. This means an external contributor's PR won't consume CI resources until a maintainer explicitly approves it.

This prevents **resource abuse** where an attacker could open hundreds of PRs to consume CI minutes, and it prevents **secret exfiltration** from forks.

### Job 2: Check Formatting (Parallel)

```yaml
formatting:
  name: Check Formatting
  runs-on: ubuntu-latest
  needs: authorize
  timeout-minutes: 10
```

Formatting runs as its own **separate parallel job**, not as a step inside the unit tests job. This is a deliberate design choice: the formatting check installs the full JetBrains ReSharper CLI tools (~300 MB), builds the solution, and then runs cleanup. That's a non-trivial amount of time. By running it in parallel with unit tests and integration tests, a formatting failure doesn't block test results, and test failures don't delay formatting feedback. The developer sees both results at the same time.

```yaml
- name: Check formatting (Rider Team Cleanup)
  run: |
    dotnet tool install -g JetBrains.ReSharper.GlobalTools
    jb cleanupcode --profile="Team Cleanup" ServiceBusToolset.slnx
    git diff --exit-code || (echo "::error::Code doesn't match Team Cleanup profile. Run Rider's Team Cleanup locally." && exit 1)
```

This enforces consistent code style across the entire codebase. Rather than using `dotnet format` (which only covers `.editorconfig` rules), this uses JetBrains' ReSharper command-line tools to apply the same "Team Cleanup" profile that developers use in Rider IDE.

The Team Cleanup profile (defined in `ServiceBusToolset.sln.DotSettings`) does significantly more than basic formatting:
- Reformats code across all languages (C#, XML, JSON, YAML, etc.)
- Arranges type and member access modifiers
- Sorts modifiers
- Removes redundant parentheses, qualifiers, and code
- Optimizes `using` directives
- Shortens references
- Makes fields `readonly` where possible
- Arranges code body style, braces, attributes, and namespaces

The approach is "apply and diff": run the cleanup, then check if `git diff` shows any changes. If the code already matches the profile, `git diff --exit-code` exits with 0. If anything changed, it exits with 1 and the developer knows to run Team Cleanup locally before pushing.

This is a **consistency enforcement** mechanism. Code reviews shouldn't waste time on formatting debates when a tool can settle them automatically.

### Job 3: Build & Unit Tests

This job runs the core quality checks — vulnerability scanning, unit tests, and coverage:

#### Checkout, Setup, Build

```yaml
- name: Checkout
  uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683 # v4.2.2

- name: Setup .NET & Restore
  uses: ./.github/actions/setup-dotnet-build

- name: Build
  run: dotnet build -c Release --no-restore
```

Note `--no-restore`: the restore already happened in the composite action. Building with `--no-restore` prevents an implicit restore that would bypass the cache step. The `-c Release` flag builds in Release mode — the same configuration that ships. Building in Debug mode for CI and Release mode for publishing can mask optimization-related bugs.

#### Vulnerability scanning

```yaml
- name: Check for vulnerable packages
  run: |
    dotnet list package --vulnerable --include-transitive 2>&1 | tee vulnerable.txt
    if grep -q "has the following vulnerable packages" vulnerable.txt; then
      echo "::error::Vulnerable NuGet packages detected"
      exit 1
    fi
```

`dotnet list package --vulnerable` queries the NuGet vulnerability database (GitHub Advisory Database) and reports any packages with known CVEs. The `--include-transitive` flag is essential: it checks the entire dependency graph, not just direct references. A vulnerability in a package three layers deep is just as exploitable.

The `tee` command writes to both stdout and a file, so the full report appears in the workflow log for debugging while `grep` reads from the file for the pass/fail decision. The `2>&1` captures stderr because `dotnet list package` writes some diagnostics there.

This implements **supply chain security scanning** — one of the most impactful CI checks you can add. It costs seconds to run and catches known vulnerabilities before they ship.

#### Unit tests with coverage

```yaml
- name: Run unit tests
  run: >
    dotnet test tests/ServiceBusToolset.Application.Tests
    -c Release --no-build
    --logger "trx;LogFileName=unit-tests.trx"
    --results-directory ./test-results
    --collect:"XPlat Code Coverage"
    -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura
```

Several design choices are embedded here:

- **Explicit test project path** — only the unit test project runs here. Integration tests run in a separate parallel job. This enables faster feedback: if unit tests fail, you know within a minute.
- **`--no-build`** — the build already happened. No point rebuilding.
- **TRX logger** — produces Visual Studio Test Results files that `dorny/test-reporter` can parse into rich PR checks showing individual test results.
- **`--results-directory`** — deterministic output location for artifact upload.
- **Cobertura coverage** — `coverlet.collector` instruments test assemblies and produces Cobertura XML. The `-- DataCollectionRunSettings...` syntax passes configuration through dotnet test's `--` separator to the test runner.

#### Coverage reporting

The coverage flow has three stages:

**Generation** — ReportGenerator converts raw Cobertura XML into human-readable formats:

```yaml
- name: Generate coverage report
  uses: danielpalme/ReportGenerator-GitHub-Action@ee0ae774f6d3afedcbd1683c1ab21b83670bdf8e # 5.5.1
  with:
    reports: ./test-results/**/coverage.cobertura.xml
    targetdir: ./coverage-report
    reporttypes: MarkdownSummaryGithub;TextSummary
```

**PR comment** — a sticky comment posts the coverage summary directly on the PR:

```yaml
- name: Post coverage summary to PR
  uses: marocchino/sticky-pull-request-comment@773744901bac0e8cbb5a0dc842800d45e9b2b405 # v2.9.4
  continue-on-error: true
  with:
    header: coverage
    path: ./coverage-report/SummaryGithub.md
```

The `header: coverage` makes it "sticky" — each push updates the existing comment instead of creating a new one. No comment spam. `continue-on-error: true` prevents comment failures (rate limits, permissions) from failing the entire pipeline. Coverage comments are informational, not gating.

**Threshold enforcement** — the pipeline fails if line coverage drops below 60%:

```yaml
THRESHOLD=60
if (( $(echo "$LINE_COVERAGE < $THRESHOLD" | bc -l) )); then
  echo "::error::Line coverage ${LINE_COVERAGE}% is below the ${THRESHOLD}% threshold"
  exit 1
fi
```

The threshold is deliberately low. It's a **floor, not a target**. The purpose is to catch regressions where someone adds a large feature with zero tests, not to enforce a specific coverage number. A high target (90%+) incentivizes low-value tests written to game the metric. The `if: always()` condition ensures coverage is reported even when tests fail.

### Job 4: Integration Tests (Parallel)

```yaml
integration-tests:
  name: Integration Tests
  runs-on: [self-hosted, macOS]
  needs: authorize
  timeout-minutes: 10
```

Integration tests run **in parallel with unit tests and formatting** — all three depend only on the `authorize` job, not on each other. This is a deliberate design choice:

```
authorize
   ├── formatting (ubuntu-latest)
   ├── unit-tests (ubuntu-latest)
   └── integration-tests (self-hosted, macOS)
```

Formatting and unit tests run on GitHub-hosted Ubuntu runners (fast, cheap, stateless). Integration tests run on a **self-hosted macOS runner** because they use Testcontainers with the Azure Service Bus emulator, which requires Docker and specific network configuration available on the self-hosted machine.

The `timeout-minutes: 10` prevents runaway containers from consuming unlimited time. The Service Bus emulator can occasionally hang during startup; without a timeout, the job would run for GitHub's default 6-hour maximum.

Two test suites run in this job:

```yaml
- name: Run integration tests
  run: >
    dotnet test tests/ServiceBusToolset.Integration.Tests
    -c Release --no-build

- name: Run CLI integration tests
  run: >
    dotnet test tests/ServiceBusToolset.CLI.Integration.Tests
    -c Release --no-build
```

The separation of integration tests into `Integration.Tests` and `CLI.Integration.Tests` reflects the project architecture: one tests the Application layer against real Service Bus, the other tests the CLI end-to-end.

### Job 5: Unified Test Report

```yaml
test-report:
  name: Publish Test Report
  runs-on: ubuntu-latest
  needs: [unit-tests, integration-tests, formatting]
  if: always()
```

This job runs *after* all three parallel jobs, regardless of whether they passed or failed (`if: always()`). Including `formatting` in the `needs` list ensures the final status of the entire workflow reflects formatting failures too — not just test failures. It downloads TRX artifacts from both test jobs and publishes a unified test report as a GitHub check:

```yaml
- name: Publish test report
  uses: dorny/test-reporter@b082adf0eced0765477756c2a610396589b8c637 # v2.5.0
  with:
    name: Test Results
    path: ./test-results/**/*.trx
    reporter: dotnet-trx
```

The `continue-on-error: true` on the integration test artifact download handles the case where integration tests were skipped or failed before producing artifacts. The test report still publishes whatever results are available.

This creates a **single pane of glass** for test results: instead of digging through logs of two separate jobs, reviewers see a consolidated report on the PR checks tab showing every test, its status, and its duration.

## Alpha Release: Continuous Delivery to NuGet

Every push to `master` triggers the alpha release pipeline. This implements **continuous delivery**: every merge produces a versioned, published artifact.

### Test gate

```yaml
test:
  name: Run Unit Tests
  runs-on: ubuntu-latest
  timeout-minutes: 10
```

The alpha release pipeline runs unit tests *again*, even though they already ran in PR validation. This might seem redundant, but it catches an important edge case: **merge conflicts that pass CI individually but fail together**. PR A passes CI, PR B passes CI, but merging both introduces a conflict that neither CI run detected. The alpha pipeline tests the merged state.

### Coverage badge

A unique feature of the alpha pipeline is the dynamic coverage badge:

```yaml
- name: Update coverage badge gist
  if: steps.coverage.outputs.coverage != '' && env.GIST_TOKEN != ''
  uses: schneegans/dynamic-badges-action@e9a478b16159b4d31420099ba146cdc50f134483 # v1.7.0
  with:
    auth: ${{ secrets.GIST_TOKEN }}
    gistID: ${{ vars.COVERAGE_GIST_ID }}
    filename: coverage-badge.json
    label: coverage
    message: ${{ steps.coverage.outputs.coverage }}%
    color: ${{ steps.coverage.outputs.color }}
```

This updates a JSON file in a GitHub Gist with the latest coverage percentage. The README references this gist via shields.io to display a live badge:

```markdown
[![Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/...)]
```

The color is dynamic: green (>=80%), yellow-green (>=60%), red (<60%). The badge updates on every push to `master`, so the README always reflects the current state of the codebase.

### Versioning

```yaml
- name: Generate version
  env:
    INPUT_BASE_VERSION: ${{ github.event.inputs.base_version }}
  run: |
    BASE="${INPUT_BASE_VERSION:-$BASE_VERSION}"
    if [[ ! "$BASE" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
      echo "::error::Invalid base version '$BASE'. Must match X.Y.Z semver format."
      exit 1
    fi
    ALPHA_VERSION="${BASE}-alpha.${GITHUB_RUN_NUMBER}"
    echo "version=$ALPHA_VERSION" >> $GITHUB_OUTPUT
```

Alpha versions follow the pattern `1.0.0-alpha.42` where `42` is the GitHub Actions run number. This produces **monotonically increasing pre-release versions** that NuGet correctly orders. The `workflow_dispatch` trigger allows manual runs with a custom base version for preparing major/minor bumps.

Note the input validation: the regex `^[0-9]+\.[0-9]+\.[0-9]+$` rejects anything that isn't strict semver. This is defense-in-depth against script injection — see the [pipeline hardening article](cicd-pipeline-hardening.md) for details on why user inputs must never be expanded directly in shell scripts.

### Publishing

```yaml
- name: Push to NuGet
  env:
    NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
  run: |
    if [ -z "$NUGET_API_KEY" ]; then
      echo "::warning::NUGET_API_KEY secret not set, skipping publish"
      exit 0
    fi
    dotnet nuget push ./artifacts/*.nupkg --api-key $NUGET_API_KEY --source https://api.nuget.org/v3/index.json --skip-duplicate
```

The `--skip-duplicate` flag is important: NuGet rejects uploads of versions that already exist, which would fail the pipeline on retries. With this flag, re-running a failed pipeline (e.g., after a transient network error) succeeds without manual intervention.

The `if [ -z "$NUGET_API_KEY" ]` guard allows the pipeline to run in forks where the secret isn't configured. Without it, forks would always fail on the publish step.

## Stable Release: Tag-Triggered Publishing

The stable release pipeline triggers on semantic version tags:

```yaml
on:
  push:
    tags:
      - 'v*.*.*'
```

Pushing a tag like `v1.2.0` initiates the full release process.

### Full test suite

Unlike the alpha pipeline (which only runs unit tests), the stable pipeline runs *all* tests — unit, integration, and CLI integration — on the self-hosted runner:

```yaml
test:
  name: Run All Tests
  runs-on: [self-hosted, macOS]
  timeout-minutes: 10
```

This is a **higher confidence gate** for production releases. Alpha packages are expected to occasionally have issues; stable releases must be thoroughly validated.

### SBOM generation

```yaml
- name: Generate SBOM
  env:
    VERSION: ${{ steps.version.outputs.version }}
    REPO: ${{ github.repository }}
  run: |
    dotnet tool install --global Microsoft.Sbom.DotNetTool
    sbom-tool generate \
      -b ./artifacts \
      -bc src/ServiceBusToolset.CLI \
      -pn ServiceBusToolset \
      -pv "$VERSION" \
      -ps "ServiceBusToolset" \
      -nsb "https://github.com/$REPO"
```

A **Software Bill of Materials (SBOM)** is a formal record of all components in a software artifact. The `sbom-tool` generates an SPDX 2.2 document listing every NuGet package, its version, and its license. This is increasingly required for enterprise software procurement and is a best practice for supply chain transparency.

The SBOM is attached to the GitHub Release alongside the NuGet package:

```yaml
- name: Upload package to GitHub Release
  uses: softprops/action-gh-release@a06a81a03ee405af7f2048a818ed3f03bbf83c7b # v2.5.0
  with:
    files: |
      ./artifacts/*.nupkg
      ./artifacts/*.snupkg
      ./artifacts/_manifest/spdx_2.2/**
```

The `.snupkg` is a symbol package that enables source-level debugging for consumers of the NuGet package.

### Automated release notes

The `release.yml` file configures GitHub's auto-generated release notes:

```yaml
changelog:
  categories:
    - title: Features
      labels: [enhancement]
    - title: Bug Fixes
      labels: [bug]
    - title: Dependencies
      labels: [dependencies]
    - title: Other Changes
      labels: ['*']
```

When a GitHub Release is created, the changelog is auto-generated from merged PR titles, grouped by label. This works in tandem with the `Release.ps1` script, which uses Claude CLI to generate richer release notes from commit messages and determine the appropriate version bump (major/minor/patch).

## Automated Dependency Management

### Dependabot configuration

```yaml
version: 2
updates:
  - package-ecosystem: nuget
    directory: /
    schedule:
      interval: weekly
    open-pull-requests-limit: 10

  - package-ecosystem: github-actions
    directory: /
    schedule:
      interval: weekly
    open-pull-requests-limit: 5

  - package-ecosystem: github-actions
    directory: /.github/actions/setup-dotnet-build
    schedule:
      interval: weekly
    open-pull-requests-limit: 5
```

Three Dependabot configurations cover three dependency surfaces:

1. **NuGet packages** — scans `Directory.Packages.props` (Central Package Management) and proposes version updates. Each update gets its own PR, which runs through the full PR validation pipeline.

2. **GitHub Actions (root)** — scans workflow files in `.github/workflows/` and proposes SHA-pin updates when new action versions are released. This is what makes SHA pinning sustainable: Dependabot automates the update process.

3. **GitHub Actions (composite action)** — a separate entry for the composite action in `.github/actions/setup-dotnet-build/`, because Dependabot scans each directory independently.

The `open-pull-requests-limit` prevents Dependabot from overwhelming the maintainer with 50 PRs at once. With limits of 10 (NuGet) and 5+5 (actions), there's at most 20 pending Dependabot PRs.

### The update cycle

Here's how a typical Dependabot update flows:

1. Dependabot detects a new version of `Azure.Messaging.ServiceBus`
2. It creates a PR updating `Directory.Packages.props` (the single source of truth for versions)
3. The PR validation pipeline runs: build, format check, vulnerability scan, tests
4. If everything passes, the maintainer merges
5. The merge triggers the alpha release pipeline, which publishes a new alpha package with the updated dependency
6. If the alpha is stable, a tag triggers the stable release

This is **automated dependency governance**: updates are proposed automatically, validated automatically, and only require human judgment for the merge decision.

## AI-Assisted Code Review

### CodeRabbit configuration

```yaml
# .coderabbit.yaml
language: en-US

reviews:
  profile: chill
  request_changes_workflow: false
  auto_review:
    enabled: true
    drafts: false
```

CodeRabbit is an AI code reviewer that runs on every PR. The `chill` profile provides suggestions without blocking, and `request_changes_workflow: false` means it never blocks a PR — it only comments.

### Path-specific instructions

```yaml
path_instructions:
  - path: src/ServiceBusToolset.Application/**
    instructions: >
      This is the Application layer using Vertical Slice Architecture.
      Each feature is a self-contained slice with Command, CommandHandler, and Result types.
      Uses martinothanar/Mediator (source-generated) for CQRS and Ardalis.Result for return types.

  - path: .github/workflows/**
    instructions: >
      Review for security best practices: pin action versions, avoid script injection
      via untrusted inputs, ensure secrets are not logged.
```

These instructions give the AI reviewer **architectural context** that it wouldn't have otherwise. Without them, a reviewer might suggest putting business logic in the CLI layer (violating the architecture) or suggest using mutable action tags (violating the security policy).

### Linting tools

```yaml
tools:
  actionlint:
    enabled: true
  markdownlint:
    enabled: true
  yamllint:
    enabled: true
```

CodeRabbit integrates `actionlint` (GitHub Actions linter), `markdownlint`, and `yamllint` into its reviews. These catch structural issues that a human reviewer might miss: invalid workflow syntax, broken markdown tables, incorrect YAML indentation.

## Security Hardening

The pipeline implements several security best practices that are covered in depth in the companion [CI/CD Pipeline Hardening](cicd-pipeline-hardening.md) article. Here's a summary:

### SHA-pinned action references

Every `uses:` reference specifies an immutable commit SHA instead of a mutable tag:

```yaml
# Mutable tag — can be silently repointed to malicious code
- uses: actions/checkout@v4

# Immutable SHA — always resolves to the exact same code
- uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683 # v4.2.2
```

This prevents supply-chain attacks where a compromised action maintainer repoints a tag to inject malicious code. The `# v4.2.2` comment is for human readability. Dependabot keeps these up to date.

### Script injection prevention

All GitHub context expressions are passed through environment variables, never expanded directly in shell scripts:

```yaml
# Unsafe: expression becomes shell source code
run: echo "${{ github.event.inputs.name }}"

# Safe: expression becomes environment variable (data, not code)
env:
  NAME: ${{ github.event.inputs.name }}
run: echo "$NAME"
```

### Least-privilege permissions

Each workflow declares the minimum permissions it needs. The alpha release workflow uses `contents: read` only — it can't push code even if compromised.

## How It All Fits Together

Here's the end-to-end flow for a typical code change:

1. **Developer creates a feature branch** and opens a PR to `master`.

2. **PR Validation runs** (if the author is authorized):
   - Build in Release mode
   - Check formatting matches Rider's Team Cleanup profile
   - Scan for vulnerable NuGet packages
   - Run unit tests with code coverage collection
   - Run integration tests on a self-hosted runner (in parallel)
   - Post a coverage summary comment on the PR
   - Fail if coverage drops below 60%
   - Publish a unified test report as a GitHub check

3. **CodeRabbit reviews** the PR with architecture-aware context and linting.

4. **Dependabot** may have its own PRs running through the same pipeline.

5. **Developer merges** the PR after all checks pass and reviews are addressed.

6. **Alpha Release triggers** on the merge to `master`:
   - Runs unit tests on the merged code
   - Updates the coverage badge
   - Packs and publishes `1.0.0-alpha.N` to NuGet.org

7. **When ready for a stable release**, the developer runs `Release.ps1`:
   - Claude CLI analyzes commits and suggests a version bump
   - The script creates a `v1.2.0` tag and GitHub Release with release notes

8. **Stable Release triggers** on the tag:
   - Runs the full test suite (unit + integration + CLI integration)
   - Packs the NuGet package
   - Generates an SBOM
   - Publishes to NuGet.org
   - Attaches packages, symbols, and SBOM to the GitHub Release

Every step is automated. Every quality check is enforced. The only human decisions are "merge this PR" and "create this release."

## Industry Practices Reference

The pipeline implements practices recommended by several industry frameworks:

| Practice | Implementation | Reference |
|---|---|---|
| Immutable artifact references | SHA-pinned GitHub Actions | [OpenSSF Scorecard](https://github.com/ossf/scorecard), [StepSecurity](https://www.stepsecurity.io/) |
| Dependency scanning | `dotnet list package --vulnerable` | [OWASP Dependency-Check](https://owasp.org/www-project-dependency-check/), [GitHub Advisory Database](https://github.com/advisories) |
| SBOM generation | Microsoft SBOM Tool (SPDX 2.2) | [NTIA SBOM Minimum Elements](https://www.ntia.gov/page/software-bill-of-materials), [Executive Order 14028](https://www.whitehouse.gov/briefing-room/presidential-actions/2021/05/12/executive-order-on-improving-the-nations-cybersecurity/) |
| Least-privilege tokens | Explicit `permissions:` blocks | [GitHub Security Hardening](https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions) |
| Automated dependency updates | Dependabot for NuGet + Actions | [OWASP Top 10: Vulnerable Components](https://owasp.org/Top10/A06_2021-Vulnerable_and_Outdated_Components/) |
| Central package management | `Directory.Packages.props` | [NuGet CPM](https://learn.microsoft.com/nuget/consume-packages/central-package-management) |
| Coverage thresholds | 60% floor with reporting | [Martin Fowler: TestCoverage](https://martinfowler.com/bliki/TestCoverage.html) |
| Deterministic builds | `<Deterministic>true</Deterministic>` | [Reproducible Builds](https://reproducible-builds.org/) |
| Concurrency control | `cancel-in-progress: true` | [GitHub Actions: Using concurrency](https://docs.github.com/en/actions/writing-workflows/choosing-what-your-workflow-does/control-the-concurrency-of-workflows-and-jobs) |
| AI-assisted review | CodeRabbit with architectural context | Emerging practice for code quality |
