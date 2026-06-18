#requires -Version 5.1

<#
.SYNOPSIS
    Tests the ID-only structured-logging guardrail (CORE-OBS-006).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreLogRedaction.psm1 - no external test
    framework, so it runs as a CI gate and locally on both pwsh and Windows
    PowerShell 5.1. Exits 0 when every assertion holds and 1 when any fails.

    It proves the analysis logic over fixtures (the placeholder classifier allows
    identifier/metadata and coarse names but forbids content/PII/secret names; the
    scanner flags an interpolated template, a value concatenated into a template
    and a forbidden placeholder, and leaves the existing ID-only templates and an
    object BeginScope alone) and then guards the real repository state: every
    ILogger call in the tracked apps/ C# source is ID-only, so a new
    content-bearing log statement fails this test too.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-log-redaction.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreLogRedaction.psm1') -Force
$repoRoot = Split-Path -Parent $scriptDir

$failures = New-Object System.Collections.Generic.List[string]

function AssertTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) { Write-Host "PASS: $Because" }
    else { $failures.Add("FAIL: $Because") }
}

function AssertEqual {
    param([string]$Expected, [string]$Actual, [string]$Because)
    if ($Expected -ceq $Actual) { Write-Host "PASS: $Because" }
    else { $failures.Add("FAIL: $Because`n      expected: '$Expected'`n      actual:   '$Actual'") }
}

# Returns the distinct violation Kinds for a one-line logger snippet.
function Get-FindingKind {
    param([string]$Source)
    $findings = @(Get-LiveCoreLogRedactionFinding -Source $Source)
    return (@($findings | ForEach-Object { $_.Kind } | Sort-Object -Unique) -join ',')
}

# --- Placeholder classifier: identifier/metadata and coarse names are allowed. ---
foreach ($allowed in @(
        'ExportJobId', 'ManifestId', 'ItemCount', 'KindCount', 'AttemptCount', 'MaxAttempts',
        'Provider', 'Examined', 'Reconciled', 'Failed', 'Removed', 'Window',
        'ResourceType', 'ResourceId', 'OrganizationId', 'WorkspaceId', 'OrganizationSlug',
        'JobName', 'HeartbeatFile', 'MissingSettings', 'RequestMethod', 'RequestRoute', 'StatusCode',
        'Status', 'Reason', 'RejectionReason', 'Outcome', 'EventType', 'NotificationType',
        'ContentType', 'ContentBlockId', 'MessageType', 'SchemaVersion', 'Count')) {
    AssertTrue (-not (Test-LiveCoreForbiddenPlaceholder -Name $allowed)) "placeholder {$allowed} is allowed (identifier/metadata or coarse name)"
}

# --- Placeholder classifier: content/PII/secret names are forbidden. ---
foreach ($forbidden in @(
        'Email', 'InvitedEmail', 'EmailAddress',
        'DisplayName', 'InvitedDisplayName', 'UserName', 'FullName',
        'Password', 'Passphrase', 'Secret', 'ApiKey',
        'Token', 'AccessToken', 'InviteToken',
        'Content', 'SceneContent', 'ContentBody', 'Body', 'Title', 'Description',
        'Payload', 'RawPayload', 'Message', 'Note', 'Comment')) {
    AssertTrue (Test-LiveCoreForbiddenPlaceholder -Name $forbidden) "placeholder {$forbidden} is forbidden (content/PII/secret)"
}

# --- Scanner: the existing ID-only templates produce no finding. ---
$clean = @(
    '_logger.LogInformation("Store notification reconciliation sweep complete: examined {Examined}, reconciled {Reconciled}, failed {Failed}.", examined, reconciled, failed);'
    '_logger.LogWarning(exception, "Failed to reconcile a {Provider} purchase; it stays drifted and will be retried on the next sweep.", purchase.Provider);'
    '_logger.LogInformation("Purged {ResourceType} {ResourceId} past its retention window (org {OrganizationId}, workspace {WorkspaceId}).", resourceType, recordId, organizationId, workspaceId);'
    '_logger.LogWarning(exception, "Failed to write the {JobName} worker heartbeat to {HeartbeatFile}.", _jobName, FilePath);'
    '_logger.LogInformation("Removed expired pending asset {AssetId}.", asset.Id);'
    '_logger.LogWarning("Skipping asset {AssetId} during cleanup: it is {Status}, not pending.", asset.Id, asset.Status);'
)
foreach ($snippet in $clean) {
    AssertEqual '' (Get-FindingKind -Source $snippet) "an ID-only template is clean: $($snippet.Substring(0, [Math]::Min(60, $snippet.Length)))..."
}

# A constant multi-line concatenation of string literals is a normal template.
$constConcat = @'
app.Logger.LogCritical(
    "Production configuration is incomplete: the required setting(s) {MissingSettings} are not configured. " +
    "Inject them as environment variables (see .env.example).",
    string.Join(", ", missingRequiredSettings));
'@
AssertEqual '' (Get-FindingKind -Source $constConcat) 'a constant string-literal concatenation template is clean'

# An object BeginScope (the request log context) is not a string template.
AssertEqual '' (Get-FindingKind -Source 'using (_logger.BeginScope(logContext)) { }') 'an object BeginScope is not flagged'

# A LogError(...) inside a comment or a string must not be mistaken for a call.
AssertEqual '' (Get-FindingKind -Source '// example: _logger.LogError($"leak {email}");') 'a logger call in a comment is ignored'
AssertEqual '' (Get-FindingKind -Source 'var help = "call _logger.LogError($\"{email}\") to leak";') 'a logger call inside a string literal is ignored'

# --- Scanner: seeded content-bearing templates fail. ---
AssertEqual 'InterpolatedTemplate' (Get-FindingKind -Source '_logger.LogInformation($"User {email} signed in.");') `
    'an interpolated template is flagged'
AssertEqual 'InterpolatedTemplate' (Get-FindingKind -Source '_logger.LogError(exception, $"Export {job.Id} failed for {participant.DisplayName}.");') `
    'an interpolated template after an exception argument is flagged'
AssertEqual 'ConcatenatedValue' (Get-FindingKind -Source '_logger.LogInformation("User " + userName + " signed in.");') `
    'a value concatenated into a template is flagged'
AssertEqual 'ForbiddenPlaceholder' (Get-FindingKind -Source '_logger.LogInformation("User {Email} signed in.", email);') `
    'a forbidden {Email} placeholder is flagged'
AssertEqual 'ForbiddenPlaceholder' (Get-FindingKind -Source '_logger.LogWarning("Participant {DisplayName} joined session {SessionId}.", name, id);') `
    'a forbidden {DisplayName} placeholder is flagged (and {SessionId} is not)'
AssertEqual 'ForbiddenPlaceholder' (Get-FindingKind -Source '_logger.LogWarning("Invitation {InviteToken} was used for {InvitedEmail}.", token, email);') `
    'forbidden {InviteToken}/{InvitedEmail} placeholders are flagged'
AssertEqual 'ForbiddenPlaceholder' (Get-FindingKind -Source '_logger.LogInformation("Scene {SceneId} content {ContentBody}.", id, body);') `
    'a forbidden {ContentBody} placeholder is flagged (and {SceneId} is not)'

# The forbidden {Email} placeholder names the offending property.
$emailFinding = @(Get-LiveCoreLogRedactionFinding -Source '_logger.LogInformation("User {Email} signed in.", email);')
AssertTrue ($emailFinding.Count -eq 1 -and $emailFinding[0].Placeholder -eq 'Email') 'the forbidden-placeholder finding names {Email}'

# A multi-line interpolated template (the real-world shape) is flagged.
$multiLine = @'
_logger.LogInformation(
    $"Participant {participant.DisplayName} ({participant.Email}) joined session {session.Id}.");
'@
AssertEqual 'InterpolatedTemplate' (Get-FindingKind -Source $multiLine) 'a multi-line interpolated template is flagged'

# --- Guard the real repository state: every tracked apps/ log call is ID-only. ---
$real = Invoke-LiveCoreLogRedactionScan -RepoRoot $repoRoot
AssertTrue ($real.FilesScanned -gt 0) 'the scan covers tracked apps/ C# source'
AssertTrue ($real.LoggerCalls -gt 0) 'the real source contains ILogger calls the lint inspects (not vacuously clean)'
if (-not $real.IsClean) {
    foreach ($finding in $real.Findings) {
        $failures.Add("FAIL: real-tree finding $($finding.File):$($finding.Line) [$($finding.Kind)] $($finding.Placeholder) -> $($finding.Snippet)")
    }
}
AssertTrue ($real.IsClean) 'every ILogger call in the tracked apps/ source is ID-only (no content/PII/secret template)'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Log-redaction guardrail tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host $failure }
    exit 1
}

Write-Host ''
Write-Host 'Log-redaction guardrail tests passed: content-bearing/interpolated templates are flagged and the existing ID-only logs pass.' -ForegroundColor Green
exit 0
