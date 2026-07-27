[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Repository,

    [Parameter(Mandatory = $true)]
    [string] $Tag,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [string] $ExistingBody,

    [switch] $SkipGitHubLookup
)

$ErrorActionPreference = 'Stop'
$startMarker = '<!-- generated-release-notes -->'
$endMarker = '<!-- /generated-release-notes -->'
$authorCache = @{}

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)

    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return $output
}

function Resolve-Author {
    param(
        [string] $Hash,
        [string] $Name,
        [string] $Email
    )

    $cacheKey = $Email.ToLowerInvariant()
    if ($authorCache.ContainsKey($cacheKey)) {
        return $authorCache[$cacheKey]
    }

    $login = $null
    if ($Email -match '^(?:\d+\+)?([^@]+)@users\.noreply\.github\.com$') {
        $login = $Matches[1]
    }
    elseif (-not $SkipGitHubLookup) {
        $apiResult = & gh api "repos/$Repository/commits/$Hash" --jq '.author.login // empty' 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($apiResult)) {
            $login = $apiResult.Trim()
        }
    }

    $display = if ([string]::IsNullOrWhiteSpace($login)) { "**$Name**" } else { "@$login" }
    $author = [pscustomobject]@{
        Display = $display
        Login = $login
        IsBot = ($login -match '\[bot\]$') -or ($Email -match '\[bot\]@') -or ($Name -match '\[bot\]')
    }
    $authorCache[$cacheKey] = $author
    return $author
}

function Format-CommitLine {
    param($Commit)

    $shortHash = $Commit.Hash.Substring(0, [Math]::Min(7, $Commit.Hash.Length))
    $description = $Commit.Description.Replace('[', '\[').Replace(']', '\]')
    return "- $description ([$shortHash](https://github.com/$Repository/commit/$($Commit.Hash))) by $($Commit.Author.Display)"
}

$null = Invoke-Git rev-parse "$Tag^{commit}"
$previousTag = $null
$savedErrorPreference = $ErrorActionPreference
$ErrorActionPreference = 'SilentlyContinue'
$previousTagOutput = & git describe --tags --abbrev=0 "$Tag^" 2>$null
$describeExitCode = $LASTEXITCODE
$ErrorActionPreference = $savedErrorPreference
if ($describeExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($previousTagOutput)) {
    $previousTag = $previousTagOutput.Trim()
}

$range = if ($previousTag) { "$previousTag..$Tag" } else { $Tag }
$separator = [char] 31
$logLines = @(Invoke-Git log $range --no-merges --format='%H%x1f%s%x1f%an%x1f%ae')
$commits = @()

foreach ($line in $logLines) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line.Split($separator, 4)
    if ($parts.Count -ne 4) { continue }

    $subject = $parts[1].Trim()
    $category = 'Other'
    $description = $subject
    if ($subject -match '^(?i:feat)(?:\([^)]+\))?!?:\s*(.+)$') {
        $category = 'Feature'
        $description = $Matches[1]
    }
    elseif ($subject -match '^(?i:fix|bugfix|hotfix)(?:\([^)]+\))?!?:\s*(.+)$') {
        $category = 'BugFix'
        $description = $Matches[1]
    }

    $commits += [pscustomobject]@{
        Hash = $parts[0]
        Subject = $subject
        Description = $description
        Name = $parts[2].Trim()
        Email = $parts[3].Trim()
        Category = $category
        Author = Resolve-Author -Hash $parts[0] -Name $parts[2].Trim() -Email $parts[3].Trim()
    }
}

$previousEmails = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if ($previousTag) {
    foreach ($email in @(Invoke-Git log $previousTag --format='%ae')) {
        if (-not [string]::IsNullOrWhiteSpace($email)) { $null = $previousEmails.Add($email.Trim()) }
    }
}

$features = @($commits | Where-Object Category -eq 'Feature')
$bugFixes = @($commits | Where-Object Category -eq 'BugFix')
$otherChanges = @($commits | Where-Object Category -eq 'Other')
$newContributors = @($commits |
    Where-Object { -not $_.Author.IsBot -and -not $previousEmails.Contains($_.Email) } |
    Group-Object { $_.Author.Display } |
    ForEach-Object { $_.Group[-1] })

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add($startMarker)
$lines.Add('## What''s Changed')
$lines.Add('')
$lines.Add('### Features')
if ($features.Count -eq 0) { $lines.Add('_No new features in this release._') }
else { foreach ($commit in $features) { $lines.Add((Format-CommitLine $commit)) } }
$lines.Add('')
$lines.Add('### Bug Fixes')
if ($bugFixes.Count -eq 0) { $lines.Add('_No bug fixes in this release._') }
else { foreach ($commit in $bugFixes) { $lines.Add((Format-CommitLine $commit)) } }

if ($otherChanges.Count -gt 0) {
    $lines.Add('')
    $lines.Add('### Other Changes')
    foreach ($commit in $otherChanges) { $lines.Add((Format-CommitLine $commit)) }
}

$lines.Add('')
$lines.Add('## New Contributors')
if ($newContributors.Count -eq 0) {
    $lines.Add('_No new contributors in this release._')
}
else {
    foreach ($commit in $newContributors) {
        $shortHash = $commit.Hash.Substring(0, [Math]::Min(7, $commit.Hash.Length))
        $lines.Add("- $($commit.Author.Display) made their first contribution in [$shortHash](https://github.com/$Repository/commit/$($commit.Hash))")
    }
}

$lines.Add('')
if ($previousTag) {
    $lines.Add("**Full Changelog**: [``$previousTag...$Tag``](https://github.com/$Repository/compare/$previousTag...$Tag)")
}
else {
    $lines.Add("**Full Changelog**: [``$Tag``](https://github.com/$Repository/commits/$Tag)")
}
$lines.Add($endMarker)
$generatedNotes = $lines -join "`n"

if (-not $PSBoundParameters.ContainsKey('ExistingBody')) {
    $ExistingBody = (& gh release view $Tag --repo $Repository --json body --jq '.body') -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Unable to read release $Tag." }
}

$existingPattern = '(?s)\s*' + [regex]::Escape($startMarker) + '.*?' + [regex]::Escape($endMarker) + '\s*'
$bodyValue = if ($null -eq $ExistingBody) { '' } else { $ExistingBody }
$cleanBody = ([regex]::Replace($bodyValue, $existingPattern, '')).Trim()
$completeBody = if ([string]::IsNullOrWhiteSpace($cleanBody)) {
    $generatedNotes
}
else {
    "$cleanBody`n`n$generatedNotes"
}

$absoluteOutputPath = if ([IO.Path]::IsPathRooted($OutputPath)) {
    [IO.Path]::GetFullPath($OutputPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputPath))
}
$outputDirectory = [IO.Path]::GetDirectoryName($absoluteOutputPath)
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
[IO.File]::WriteAllText($absoluteOutputPath, $completeBody, [Text.UTF8Encoding]::new($false))
Write-Host "Generated release notes for $Tag using $($commits.Count) commits."
