#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Creates a new release for ServiceBusToolset with Claude-generated release notes.

.DESCRIPTION
    This script automates the release process:
    1. Validates the working directory is clean and on master
    2. Collects commits since the last release tag
    3. Uses Claude to generate release notes and suggest version bump
    4. Creates a git tag and pushes it
    5. Creates a GitHub release with the generated notes

.PARAMETER BumpType
    Override the version bump type. Valid values: major, minor, patch.
    If not specified, Claude will suggest based on commit history.

.PARAMETER DryRun
    Preview the release without making any changes.

.EXAMPLE
    ./Release.ps1
    # Interactive release with Claude-suggested version

.EXAMPLE
    ./Release.ps1 -BumpType minor
    # Force a minor version bump

.EXAMPLE
    ./Release.ps1 -DryRun
    # Preview without creating release
#>

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateSet('major', 'minor', 'patch')]
    [string]$BumpType,

    [Parameter()]
    [switch]$DryRun,

    [Parameter()]
    [switch]$TestMode  # For testing the script without making changes
)

$ErrorActionPreference = 'Stop'

function Write-Step
{
    param([string]$Message)
    Write-Host "`n▶ $Message" -ForegroundColor Cyan
}

function Write-Success
{
    param([string]$Message)
    Write-Host "✓ $Message" -ForegroundColor Green
}

function Write-Warning
{
    param([string]$Message)
    Write-Host "⚠ $Message" -ForegroundColor Yellow
}

function Write-Error
{
    param([string]$Message)
    Write-Host "✗ $Message" -ForegroundColor Red
}

function Write-Debug-Log
{
    param([string]$Message)
    if ($TestMode)
    {
        Write-Host "[DEBUG] $Message" -ForegroundColor DarkGray
    }
}

function Test-Prerequisites
{
    Write-Step "Checking prerequisites..."

    # Check for git
    if (-not (Get-Command git -ErrorAction SilentlyContinue))
    {
        throw "git is not installed or not in PATH"
    }

    # Check for gh CLI
    if (-not (Get-Command gh -ErrorAction SilentlyContinue))
    {
        throw "GitHub CLI (gh) is not installed. Install from https://cli.github.com/"
    }

    # Check gh auth status
    $ghAuth = gh auth status 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        throw "GitHub CLI is not authenticated. Run 'gh auth login' first."
    }

    # Check for claude CLI
    if (-not (Get-Command claude -ErrorAction SilentlyContinue))
    {
        throw "Claude CLI is not installed. Install from https://claude.ai/code"
    }

    Write-Success "All prerequisites met"
}

function Test-WorkingDirectory
{
    Write-Step "Validating working directory..."

    # Check we're in a git repository
    $gitRoot = git rev-parse --show-toplevel 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        throw "Not in a git repository"
    }

    # Check for uncommitted changes
    $status = git status --porcelain
    if ($status)
    {
        throw "Working directory has uncommitted changes. Please commit or stash them first."
    }

    # Check we're on master
    $branch = git branch --show-current
    if ($branch -ne 'master')
    {
        throw "Not on master branch. Current branch: $branch"
    }

    # Fetch latest and check if we're up to date
    git fetch origin master --quiet
    $behind = git rev-list --count HEAD..origin/master
    $ahead = git rev-list --count origin/master..HEAD

    if ($behind -gt 0)
    {
        throw "Local master is $behind commit(s) behind origin. Please pull first."
    }

    if ($ahead -gt 0)
    {
        Write-Warning "Local master is $ahead commit(s) ahead of origin"
    }

    Write-Success "Working directory is clean and on master"
}

function Get-LastVersionTag
{
    Write-Step "Finding last version tag..."

    $tag = git describe --tags --abbrev=0 --match "v*.*.*" 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        Write-Warning "No version tags found, starting from v1.0.0"
        return $null
    }

    Write-Success "Last version: $tag"
    return $tag
}

function Get-CommitsSinceTag
{
    param([string]$Tag)

    Write-Step "Collecting commits..."

    if ($Tag)
    {
        $commits = git log "$Tag..HEAD" --pretty=format:"%h %s" --no-merges
    }
    else
    {
        $commits = git log --pretty=format:"%h %s" --no-merges
    }

    if (-not $commits)
    {
        throw "No commits found since last release"
    }

    $commitCount = ($commits -split "`n").Count
    Write-Success "Found $commitCount commit(s) since last release"

    return $commits
}

function Get-ClaudeReleaseNotes
{
    param(
        [string]$Commits,
        [string]$LastVersion
    )

    Write-Step "Generating release notes with Claude..."

    $baseVersion = if ($LastVersion)
    {
        $LastVersion.TrimStart('v')
    }
    else
    {
        "0.0.0"
    }

    $prompt = @"
You are helping create release notes for ServiceBusToolset, an Azure Service Bus CLI tool.

Current version: $baseVersion
Commits since last release:
$Commits

Please analyze these commits and provide:

1. SUGGESTED_BUMP: Suggest 'major', 'minor', or 'patch' based on:
   - major: Breaking changes or major new features
   - minor: New features or significant improvements
   - patch: Bug fixes, documentation, or minor improvements

2. RELEASE_NOTES: Write concise release notes in markdown format:
   - Group changes by category (Features, Improvements, Bug Fixes, etc.)
   - Use bullet points
   - Keep it concise but informative
   - Do not include a version header (that will be added separately)

IMPORTANT: Output ONLY the formatted response below with NO other text before or after:
---SUGGESTED_BUMP---
patch
---RELEASE_NOTES---
### Features
- Added new feature X

### Bug Fixes
- Fixed issue with Y
---END---
"@

    $response = $prompt | claude --print 2>&1

    if ($LASTEXITCODE -ne 0)
    {
        throw "Claude CLI failed: $response"
    }

    # Parse the response - normalize line endings first
    Write-Debug-Log "Raw response type: $( $response.GetType().FullName )"
    Write-Debug-Log "Raw response is array: $( $response -is [array] )"
    if ($response -is [array])
    {
        Write-Debug-Log "Response array count: $( $response.Count )"
    }

    $responseText = $response -join "`n"
    $responseText = $responseText -replace "`r`n", "`n"

    Write-Debug-Log "Response text length: $( $responseText.Length )"
    Write-Debug-Log "Response contains SUGGESTED_BUMP: $($responseText.Contains('---SUGGESTED_BUMP---') )"
    Write-Debug-Log "Response contains RELEASE_NOTES: $($responseText.Contains('---RELEASE_NOTES---') )"
    Write-Debug-Log "Response contains END: $($responseText.Contains('---END---') )"

    if ($TestMode)
    {
        Write-Host "`n[DEBUG] Full response text:" -ForegroundColor DarkGray
        Write-Host "─────────────────────────────────────────" -ForegroundColor DarkGray
        Write-Host $responseText -ForegroundColor DarkGray
        Write-Host "─────────────────────────────────────────" -ForegroundColor DarkGray
    }

    $suggestedBump = $null
    $releaseNotes = $null

    # Extract SUGGESTED_BUMP
    if ($responseText -match '---SUGGESTED_BUMP---\s*(\w+)')
    {
        $suggestedBump = $Matches[1].Trim()
        Write-Debug-Log "Parsed SUGGESTED_BUMP: $suggestedBump"
    }
    else
    {
        throw "Failed to parse SUGGESTED_BUMP from Claude response. Raw response:`n$responseText"
    }

    # Extract RELEASE_NOTES
    if ($responseText -match '---RELEASE_NOTES---\s*([\s\S]*?)\s*---END---')
    {
        $releaseNotes = $Matches[1].Trim()
        Write-Debug-Log "Parsed RELEASE_NOTES length: $( $releaseNotes.Length )"
    }
    else
    {
        throw "Failed to parse RELEASE_NOTES from Claude response. Raw response:`n$responseText"
    }

    Write-Success "Claude suggests: $suggestedBump bump"

    return @{
        SuggestedBump = $suggestedBump
        ReleaseNotes = $releaseNotes
    }
}

function Get-NextVersion
{
    param(
        [string]$CurrentVersion,
        [string]$BumpType
    )

    if (-not $CurrentVersion)
    {
        return "1.0.0"
    }

    $version = $CurrentVersion.TrimStart('v')
    $parts = $version -split '\.'

    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = [int]$parts[2]

    switch ($BumpType)
    {
        'major' {
            $major++
            $minor = 0
            $patch = 0
        }
        'minor' {
            $minor++
            $patch = 0
        }
        'patch' {
            $patch++
        }
    }

    return "$major.$minor.$patch"
}

function Confirm-Release
{
    param(
        [string]$Version,
        [string]$BumpType,
        [string]$ReleaseNotes
    )

    Write-Host "`n" -NoNewline
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta
    Write-Host "                     RELEASE PREVIEW                           " -ForegroundColor Magenta
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta
    Write-Host "`nVersion: " -NoNewline -ForegroundColor White
    Write-Host "v$Version" -ForegroundColor Yellow
    Write-Host "Bump type: " -NoNewline -ForegroundColor White
    Write-Host "$BumpType" -ForegroundColor Yellow
    Write-Host "`nRelease Notes:" -ForegroundColor White
    Write-Host "─────────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
    Write-Host $ReleaseNotes -ForegroundColor Gray
    Write-Host "─────────────────────────────────────────────────────────────────" -ForegroundColor DarkGray

    if ($DryRun)
    {
        Write-Host "`n[DRY RUN] No changes will be made." -ForegroundColor Yellow
        return $false
    }

    Write-Host "`nProceed with release? " -NoNewline -ForegroundColor White
    Write-Host "(y/N) " -NoNewline -ForegroundColor Gray
    $confirmation = Read-Host

    return $confirmation -eq 'y' -or $confirmation -eq 'Y'
}

function New-Release
{
    param(
        [string]$Version,
        [string]$ReleaseNotes
    )

    $tag = "v$Version"

    Write-Step "Creating git tag $tag..."
    git tag -a $tag -m "Release $tag"
    Write-Success "Created tag $tag"

    Write-Step "Pushing tag to origin..."
    git push origin $tag
    Write-Success "Pushed tag to origin"

    Write-Step "Creating GitHub release..."

    $title = "v$Version"
    gh release create $tag --title $title --notes $ReleaseNotes

    Write-Success "GitHub release created"

    Write-Host "`n" -NoNewline
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
    Write-Host "                    RELEASE COMPLETE                           " -ForegroundColor Green
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
    Write-Host "`nVersion $tag has been released!" -ForegroundColor Green
    Write-Host "The stable-release workflow will now build and publish to NuGet." -ForegroundColor Gray
    Write-Host "`nView release: " -NoNewline -ForegroundColor White

    $repoUrl = git remote get-url origin
    $repoUrl = $repoUrl -replace '\.git$', ''
    $repoUrl = $repoUrl -replace 'git@github.com:', 'https://github.com/'
    Write-Host "$repoUrl/releases/tag/$tag" -ForegroundColor Cyan
}

# Main execution
try
{
    Write-Host "`n╔═══════════════════════════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║              ServiceBusToolset Release Script                 ║" -ForegroundColor Magenta
    Write-Host "╚═══════════════════════════════════════════════════════════════╝" -ForegroundColor Magenta

    if ($TestMode)
    {
        Write-Host "[TEST MODE] Skipping prerequisites and working directory checks" -ForegroundColor Yellow
    }
    else
    {
        Test-Prerequisites
        Test-WorkingDirectory
    }

    $lastTag = Get-LastVersionTag
    $commits = Get-CommitsSinceTag -Tag $lastTag

    $claudeResult = Get-ClaudeReleaseNotes -Commits $commits -LastVersion $lastTag

    # Use override or Claude suggestion
    $effectiveBumpType = if ($BumpType)
    {
        $BumpType
    }
    else
    {
        $claudeResult.SuggestedBump
    }

    if ($BumpType -and $BumpType -ne $claudeResult.SuggestedBump)
    {
        Write-Warning "Overriding Claude's suggestion ($( $claudeResult.SuggestedBump )) with: $BumpType"
    }

    $nextVersion = Get-NextVersion -CurrentVersion $lastTag -BumpType $effectiveBumpType

    $confirmed = Confirm-Release -Version $nextVersion -BumpType $effectiveBumpType -ReleaseNotes $claudeResult.ReleaseNotes

    if ($confirmed)
    {
        if ($TestMode)
        {
            Write-Host "`n[TEST MODE] Would create release v$nextVersion - skipping actual release" -ForegroundColor Yellow
            Write-Success "Test completed successfully! Parsing works correctly."
        }
        else
        {
            New-Release -Version $nextVersion -ReleaseNotes $claudeResult.ReleaseNotes
        }
    }
    else
    {
        if (-not $DryRun)
        {
            Write-Host "`nRelease cancelled." -ForegroundColor Yellow
        }
    }
}
catch
{
    Write-Error $_.Exception.Message
    exit 1
}
