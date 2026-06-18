#requires -Version 5.1

<#
.SYNOPSIS
    Enforces ID-only structured logging as a guardrail, not a convention
    (CORE-OBS-006, threat T7 in docs/07_SECURITY_THREAT_MODEL.md).

.DESCRIPTION
    The Core logs IDs and event types, never sensitive content bodies, PII or
    secrets (docs/15_OBSERVABILITY.md). Until this module that rule was reviewer
    discipline only: the current logs are correctly ID-only, but nothing stopped a
    future content-bearing log statement from shipping.

    This module is the pure analysis behind the CI lint. It scans first-party C#
    source for ILogger calls (LogTrace/LogDebug/LogInformation/LogWarning/
    LogError/LogCritical, the generic Log, and BeginScope) and FAILS a call whose
    message TEMPLATE would put a value into the log text rather than a structured
    identifier:

      - InterpolatedTemplate: the call uses an interpolated string ($"...{x}..."),
        which embeds the runtime value directly in the message text and cannot be
        redacted - the canonical anti-pattern (CA2254). Flagged anywhere in a log
        call's arguments.
      - ConcatenatedValue: the template concatenates a non-literal expression into
        the message ("user " + name + " ..."). A constant concatenation of string
        literals ("a" + "b") is a normal multi-line template and is allowed.
      - ForbiddenPlaceholder: the constant template names a structured property for
        content/PII/a secret (for example {Email}, {DisplayName}, {AccessToken},
        {ContentBody}), so the value WOULD be captured as a log property.

    Existing ID-only templates are unaffected: a placeholder whose last word is an
    identifier/metadata suffix (Id, Count, Type, Status, Code, Version, Slug, ...)
    is always allowed, so {ExportJobId}, {ItemCount}, {ResourceType},
    {ContentBlockId} and {OrganizationSlug} pass; coarse names that are not in the
    forbidden vocabulary ({Provider}, {JobName}, {HeartbeatFile}, {RequestRoute},
    {MissingSettings}, {Window}, {Reason}) pass too.

    The scanner is C#-aware: it masks comments and string CONTENT before locating
    calls and balancing parentheses, so a LogError( in a comment or a string
    literal is never mistaken for a call, and a template that spans several lines
    is handled.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

# A placeholder whose LAST word is one of these is an identifier or a piece of
# low-cardinality metadata, never the content/PII/secret value itself, so it is
# always allowed - this is what keeps the existing ID-only logs (and any future
# {ContentBlockId}/{MessageType}) passing while {ContentBody}/{MessageText} fail.
$script:LogRedactionAllowSuffix = @(
    'id', 'ids', 'guid', 'uuid',
    'count', 'counts', 'total', 'totals', 'number', 'num', 'index', 'indexes',
    'type', 'types', 'kind', 'kinds',
    'status', 'statuses', 'state', 'states',
    'code', 'codes',
    'version', 'versions', 'sequence', 'seq', 'revision',
    'length', 'size', 'sizes', 'bytes',
    'duration', 'ms', 'milliseconds', 'seconds', 'timestamp',
    'slug', 'slugs'
)

# Whole-word (after camelCase/underscore splitting) content/PII/secret vocabulary.
# A placeholder containing any of these as a word - and whose last word is NOT an
# allow-suffix above - names a value the log must never carry.
$script:LogRedactionForbiddenWord = @(
    'email', 'emails',
    'password', 'passwords', 'passphrase', 'passphrases', 'pwd',
    'secret', 'secrets',
    'token', 'tokens',
    'credential', 'credentials',
    'content', 'contents',
    'body', 'bodies',
    'title', 'titles',
    'description', 'descriptions',
    'payload', 'payloads',
    'message', 'messages',
    'note', 'notes',
    'comment', 'comments',
    'address', 'addresses',
    'phone', 'phones',
    'ssn', 'dob', 'birthdate', 'plaintext', 'pii'
)

# Joined multi-word forms that the whole-word split would not catch on its own
# (camelCase {DisplayName} splits to display + name, neither of which is a forbidden
# word, so the joined "displayname" is what catches it). Matched as a case-insensitive
# substring of the joined placeholder name.
$script:LogRedactionForbiddenJoined = @(
    'displayname', 'fullname', 'firstname', 'lastname', 'username', 'surname', 'nickname',
    'emailaddress',
    'apikey', 'accesskey', 'secretkey', 'privatekey', 'clientsecret',
    'accesstoken', 'refreshtoken', 'bearertoken', 'idtoken', 'authtoken', 'invitetoken'
)

# The ILogger members the lint inspects. The generic Log is gated on a
# logger-looking receiver so an unrelated Math.Log/Console.Log is never matched.
$script:LogRedactionCallRegex = New-Object System.Text.RegularExpressions.Regex(
    '(?<![A-Za-z0-9_])(?<recv>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*(?<method>LogTrace|LogDebug|LogInformation|LogWarning|LogError|LogCritical|BeginScope|Log)\s*\(')

function ConvertTo-LiveCoreMaskedCSharp {
    <#
    .SYNOPSIS
        Returns the source with every comment and string-literal CONTENT replaced
        by spaces (length and newlines preserved), plus the list of top-level
        string literals found (start index, interpolated flag, decoded inner text).
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Source
    )

    $sb = New-Object System.Text.StringBuilder $Source.Length
    $literals = New-Object System.Collections.Generic.List[object]
    $i = 0
    $n = $Source.Length

    while ($i -lt $n) {
        $c = $Source[$i]
        if ($i + 1 -lt $n) { $next = $Source[$i + 1] } else { $next = [char]0 }

        # Line comment.
        if ($c -eq '/' -and $next -eq '/') {
            while ($i -lt $n -and $Source[$i] -ne "`n") { [void]$sb.Append(' '); $i++ }
            continue
        }
        # Block comment.
        if ($c -eq '/' -and $next -eq '*') {
            [void]$sb.Append('  '); $i += 2
            while ($i -lt $n -and -not ($Source[$i] -eq '*' -and ($i + 1 -lt $n) -and $Source[$i + 1] -eq '/')) {
                if ($Source[$i] -eq "`n") { [void]$sb.Append("`n") } else { [void]$sb.Append(' ') }
                $i++
            }
            if ($i -lt $n) { [void]$sb.Append('  '); $i += 2 }
            continue
        }
        # Char literal.
        if ($c -eq "'") {
            [void]$sb.Append(' '); $i++
            while ($i -lt $n -and $Source[$i] -ne "'") {
                if ($Source[$i] -eq '\') {
                    [void]$sb.Append(' '); $i++
                    if ($i -lt $n) { [void]$sb.Append(' '); $i++ }
                }
                else { [void]$sb.Append(' '); $i++ }
            }
            if ($i -lt $n) { [void]$sb.Append(' '); $i++ }
            continue
        }

        # String literal: detect the prefix ($@", @$", $", @", ").
        $isInterp = $false
        $isVerbatim = $false
        $prefixLen = 0
        if ($c -eq '$' -and $next -eq '@' -and ($i + 2 -lt $n) -and $Source[$i + 2] -eq '"') { $isInterp = $true; $isVerbatim = $true; $prefixLen = 2 }
        elseif ($c -eq '@' -and $next -eq '$' -and ($i + 2 -lt $n) -and $Source[$i + 2] -eq '"') { $isInterp = $true; $isVerbatim = $true; $prefixLen = 2 }
        elseif ($c -eq '$' -and $next -eq '"') { $isInterp = $true; $isVerbatim = $false; $prefixLen = 1 }
        elseif ($c -eq '@' -and $next -eq '"') { $isInterp = $false; $isVerbatim = $true; $prefixLen = 1 }
        elseif ($c -eq '"') { $isInterp = $false; $isVerbatim = $false; $prefixLen = 0 }
        else {
            [void]$sb.Append($c); $i++; continue
        }

        $start = $i
        $inner = New-Object System.Text.StringBuilder
        for ($p = 0; $p -lt $prefixLen + 1; $p++) { [void]$sb.Append(' ') }
        $i += $prefixLen + 1
        $holeDepth = 0

        while ($i -lt $n) {
            $ch = $Source[$i]
            if ($holeDepth -eq 0) {
                if (-not $isVerbatim -and $ch -eq '\') {
                    [void]$sb.Append(' '); $i++
                    if ($i -lt $n) {
                        if ($Source[$i] -eq "`n") { [void]$sb.Append("`n") } else { [void]$sb.Append(' ') }
                        [void]$inner.Append($Source[$i]); $i++
                    }
                    continue
                }
                if ($ch -eq '"') {
                    if ($isVerbatim -and ($i + 1 -lt $n) -and $Source[$i + 1] -eq '"') {
                        [void]$sb.Append('  '); [void]$inner.Append('"'); $i += 2; continue
                    }
                    [void]$sb.Append(' '); $i++; break
                }
                if ($isInterp -and $ch -eq '{') {
                    if (($i + 1 -lt $n) -and $Source[$i + 1] -eq '{') { [void]$sb.Append('  '); [void]$inner.Append('{{'); $i += 2; continue }
                    $holeDepth = 1; [void]$sb.Append(' '); $i++; continue
                }
                if ($isInterp -and $ch -eq '}') {
                    if (($i + 1 -lt $n) -and $Source[$i + 1] -eq '}') { [void]$sb.Append('  '); [void]$inner.Append('}}'); $i += 2; continue }
                    [void]$sb.Append(' '); $i++; continue
                }
                if ($ch -eq "`n") { [void]$sb.Append("`n") } else { [void]$sb.Append(' ') }
                [void]$inner.Append($ch); $i++
                continue
            }
            else {
                # Inside an interpolation hole: code, masked to spaces; track braces so
                # the string's closing quote is found past any } in the embedded code.
                if ($ch -eq '{') { $holeDepth++; [void]$sb.Append(' '); $i++; continue }
                if ($ch -eq '}') { $holeDepth--; [void]$sb.Append(' '); $i++; continue }
                if ($ch -eq '"') {
                    [void]$sb.Append(' '); $i++
                    while ($i -lt $n -and $Source[$i] -ne '"') {
                        if ($Source[$i] -eq '\') {
                            [void]$sb.Append(' '); $i++
                            if ($i -lt $n) { [void]$sb.Append(' '); $i++ }
                            continue
                        }
                        if ($Source[$i] -eq "`n") { [void]$sb.Append("`n") } else { [void]$sb.Append(' ') }
                        $i++
                    }
                    if ($i -lt $n) { [void]$sb.Append(' '); $i++ }
                    continue
                }
                if ($ch -eq "`n") { [void]$sb.Append("`n") } else { [void]$sb.Append(' ') }
                $i++
                continue
            }
        }

        $literals.Add([pscustomobject]@{
                Start          = $start
                IsInterpolated = $isInterp
                Inner          = $inner.ToString()
            })
    }

    return [pscustomobject]@{
        Masked   = $sb.ToString()
        Literals = $literals
    }
}

function Get-LiveCoreTemplatePlaceholder {
    <#
    .SYNOPSIS
        Extracts the {Name} placeholders from a (non-interpolated) message template
        body, skipping the {{ and }} escapes.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Template
    )

    $names = New-Object System.Collections.Generic.List[string]
    $i = 0
    $n = $Template.Length
    while ($i -lt $n) {
        $c = $Template[$i]
        if ($c -eq '{') {
            if (($i + 1 -lt $n) -and $Template[$i + 1] -eq '{') { $i += 2; continue }
            $close = $Template.IndexOf('}', $i + 1)
            if ($close -lt 0) { break }
            $names.Add($Template.Substring($i + 1, $close - $i - 1))
            $i = $close + 1
            continue
        }
        if ($c -eq '}' -and ($i + 1 -lt $n) -and $Template[$i + 1] -eq '}') { $i += 2; continue }
        $i++
    }
    return , $names.ToArray()
}

function Test-LiveCoreForbiddenPlaceholder {
    <#
    .SYNOPSIS
        Returns $true when a structured-logging placeholder name denotes a
        content/PII/secret value the log must not carry, $false for an
        identifier/metadata placeholder.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Name
    )

    $value = $Name
    # Drop the structured-logging operators ({@x} destructure, {$x} stringify) and
    # any ,alignment / :format specifier.
    $value = $value -replace '^[@$]', ''
    $comma = $value.IndexOf(',')
    if ($comma -ge 0) { $value = $value.Substring(0, $comma) }
    $colon = $value.IndexOf(':')
    if ($colon -ge 0) { $value = $value.Substring(0, $colon) }
    $value = $value.Trim()

    if ($value -eq '') { return $false }
    if ($value -match '^[0-9]+$') { return $false }   # positional {0}/{1}

    $lower = $value.ToLowerInvariant()

    # Split camelCase / PascalCase / acronym runs / digits / underscores into words.
    $spaced = [regex]::Replace($value, '([a-z0-9])([A-Z])', '$1 $2')
    $spaced = [regex]::Replace($spaced, '([A-Z]+)([A-Z][a-z])', '$1 $2')
    $spaced = $spaced -replace '[_0-9]+', ' '
    $words = @(
        $spaced -split '\s+' |
            Where-Object { $_ -ne '' } |
            ForEach-Object { $_.ToLowerInvariant() }
    )
    if ($words.Count -eq 0) { return $false }

    $last = $words[$words.Count - 1]
    if ($script:LogRedactionAllowSuffix -contains $last) { return $false }

    foreach ($word in $words) {
        if ($script:LogRedactionForbiddenWord -contains $word) { return $true }
    }
    foreach ($joined in $script:LogRedactionForbiddenJoined) {
        if ($lower.Contains($joined)) { return $true }
    }
    return $false
}

function Get-LiveCoreLogRedactionFinding {
    <#
    .SYNOPSIS
        Returns the log-redaction violations in one C# source string. Each finding
        is a [pscustomobject] with File, Line, Kind, Placeholder and Snippet.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Source,

        [string]$File = '(in-memory)'
    )

    $findings = New-Object System.Collections.Generic.List[object]
    if ([string]::IsNullOrEmpty($Source)) { return , $findings.ToArray() }

    $maskedResult = ConvertTo-LiveCoreMaskedCSharp -Source $Source
    $masked = $maskedResult.Masked
    $literals = $maskedResult.Literals

    # Newline offsets for index -> line-number lookup.
    $lineStarts = New-Object System.Collections.Generic.List[int]
    $lineStarts.Add(0)
    for ($k = 0; $k -lt $Source.Length; $k++) {
        if ($Source[$k] -eq "`n") { $lineStarts.Add($k + 1) }
    }
    $sourceLines = $Source -split "`n"

    foreach ($match in $script:LogRedactionCallRegex.Matches($masked)) {
        $method = $match.Groups['method'].Value
        $recv = $match.Groups['recv'].Value
        if ($method -eq 'Log' -and ($recv -notmatch '(?i)log')) { continue }

        $parenIndex = $match.Index + $match.Length - 1
        $depth = 0
        $argEnd = -1
        for ($j = $parenIndex; $j -lt $masked.Length; $j++) {
            $mc = $masked[$j]
            if ($mc -eq '(') { $depth++ }
            elseif ($mc -eq ')') { $depth--; if ($depth -eq 0) { $argEnd = $j; break } }
        }
        if ($argEnd -lt 0) { continue }
        $argStart = $parenIndex + 1

        # Line number and snippet for the call.
        $lo = 0; $hi = $lineStarts.Count - 1; $lineIdx = 0
        while ($lo -le $hi) {
            $mid = [int](($lo + $hi) / 2)
            if ($lineStarts[$mid] -le $match.Index) { $lineIdx = $mid; $lo = $mid + 1 } else { $hi = $mid - 1 }
        }
        $line = $lineIdx + 1
        if ($lineIdx -lt $sourceLines.Count) { $snippet = $sourceLines[$lineIdx].Trim() } else { $snippet = '' }

        $callLiterals = @($literals | Where-Object { $_.Start -ge $argStart -and $_.Start -lt $argEnd })

        # Rule 1a: an interpolated string anywhere in the call's arguments.
        if (@($callLiterals | Where-Object { $_.IsInterpolated }).Count -gt 0) {
            $findings.Add([pscustomobject]@{
                    File        = $File
                    Line        = $line
                    Kind        = 'InterpolatedTemplate'
                    Placeholder = ''
                    Snippet     = $snippet
                })
            continue
        }

        # Split the argument list into top-level arguments.
        $argSpans = New-Object System.Collections.Generic.List[object]
        $segStart = $argStart
        $d = 0
        for ($j = $argStart; $j -lt $argEnd; $j++) {
            $mc = $masked[$j]
            if ($mc -eq '(' -or $mc -eq '[' -or $mc -eq '{') { $d++ }
            elseif ($mc -eq ')' -or $mc -eq ']' -or $mc -eq '}') { $d-- }
            elseif ($mc -eq ',' -and $d -eq 0) {
                $argSpans.Add([pscustomobject]@{ Start = $segStart; End = $j })
                $segStart = $j + 1
            }
        }
        $argSpans.Add([pscustomobject]@{ Start = $segStart; End = $argEnd })

        # The message template is the first argument whose LEADING token is a string
        # literal (an exception or a LogLevel argument before it has no literal).
        $templateSpan = $null
        foreach ($span in $argSpans) {
            $spanLiterals = @(
                $literals |
                    Where-Object { $_.Start -ge $span.Start -and $_.Start -lt $span.End } |
                    Sort-Object -Property Start
            )
            if ($spanLiterals.Count -eq 0) { continue }
            $leading = $masked.Substring($span.Start, $spanLiterals[0].Start - $span.Start)
            if ($leading.Trim() -ne '') { continue }   # code before the literal: not the template
            $templateSpan = [pscustomobject]@{ Start = $span.Start; End = $span.End; Literals = $spanLiterals }
            break
        }

        if ($null -eq $templateSpan) { continue }

        # Rule 1b: a non-literal expression concatenated into the template. After
        # masking, the template argument is all spaces except '+' joins; any other
        # residual character is a concatenated value/expression.
        $maskedArg = $masked.Substring($templateSpan.Start, $templateSpan.End - $templateSpan.Start)
        $residual = ($maskedArg -replace '[\s+]', '')
        if ($residual.Length -gt 0) {
            $findings.Add([pscustomobject]@{
                    File        = $File
                    Line        = $line
                    Kind        = 'ConcatenatedValue'
                    Placeholder = ''
                    Snippet     = $snippet
                })
            continue
        }

        # Rule 2: a content/PII/secret-named structured placeholder.
        $templateBody = -join ($templateSpan.Literals | ForEach-Object { $_.Inner })
        foreach ($placeholder in (Get-LiveCoreTemplatePlaceholder -Template $templateBody)) {
            if (Test-LiveCoreForbiddenPlaceholder -Name $placeholder) {
                $findings.Add([pscustomobject]@{
                        File        = $File
                        Line        = $line
                        Kind        = 'ForbiddenPlaceholder'
                        Placeholder = $placeholder
                        Snippet     = $snippet
                    })
            }
        }
    }

    return , $findings.ToArray()
}

function Measure-LiveCoreLoggerCall {
    <#
    .SYNOPSIS
        Counts the ILogger calls the lint inspects in one C# source string (used to
        prove the scan is not vacuously clean).
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Source
    )

    if ([string]::IsNullOrEmpty($Source)) { return 0 }
    $masked = (ConvertTo-LiveCoreMaskedCSharp -Source $Source).Masked
    $count = 0
    foreach ($match in $script:LogRedactionCallRegex.Matches($masked)) {
        $method = $match.Groups['method'].Value
        $recv = $match.Groups['recv'].Value
        if ($method -eq 'Log' -and ($recv -notmatch '(?i)log')) { continue }
        $count++
    }
    return $count
}

function Invoke-LiveCoreLogRedactionScan {
    <#
    .SYNOPSIS
        Scans the tracked first-party C# source under apps/ for log-redaction
        violations and returns the findings plus coverage counts.

    .DESCRIPTION
        Enumerates TRACKED *.cs files under apps/ via `git ls-files` (so generated
        build output and untracked working-tree files are never seen), excludes the
        generated EF migrations, and runs the analysis over each. Fail-closed: if
        the tracked file set cannot be determined it throws rather than returning a
        spuriously clean result.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $RepoRoot = (Resolve-Path -Path $RepoRoot).Path

    # Enumerate TRACKED files under apps/ via `git ls-files` and filter to C# in
    # PowerShell (rather than a git glob), so the enumeration is identical across
    # git versions and platforms. Fail-closed like the boundary scan: if the
    # tracked set cannot be determined, throw rather than return a clean result.
    $tracked = $null
    try {
        $tracked = @(& git -C $RepoRoot ls-files -- apps)
    }
    catch {
        throw "Unable to enumerate tracked files via 'git ls-files' in ${RepoRoot}: $($_.Exception.Message)"
    }
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files exited with code $LASTEXITCODE in $RepoRoot; the log-redaction scan needs a git work tree."
    }

    $tracked = @(
        $tracked |
            Where-Object { $_ -and $_.Trim() -ne '' } |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_.ToLowerInvariant().EndsWith('.cs') }
    )

    # Generated EF migrations are machine-written and carry no logging; skip them.
    $excludedPathPattern = '(^|/)(Migrations)/'

    $findings = New-Object System.Collections.Generic.List[object]
    $filesScanned = 0
    $loggerCalls = 0

    foreach ($relativePath in $tracked) {
        if ($relativePath -match $excludedPathPattern) { continue }
        $fullPath = Join-Path $RepoRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath)) { continue }

        $source = Get-Content -LiteralPath $fullPath -Raw
        if ($null -eq $source) { $source = '' }

        $filesScanned++
        $loggerCalls += (Measure-LiveCoreLoggerCall -Source $source)
        foreach ($finding in (Get-LiveCoreLogRedactionFinding -Source $source -File $relativePath)) {
            $findings.Add($finding)
        }
    }

    return [pscustomobject]@{
        Findings     = $findings.ToArray()
        FilesScanned = $filesScanned
        LoggerCalls  = $loggerCalls
        IsClean      = ($findings.Count -eq 0)
    }
}

Export-ModuleMember -Function `
    ConvertTo-LiveCoreMaskedCSharp, `
    Get-LiveCoreTemplatePlaceholder, `
    Test-LiveCoreForbiddenPlaceholder, `
    Get-LiveCoreLogRedactionFinding, `
    Measure-LiveCoreLoggerCall, `
    Invoke-LiveCoreLogRedactionScan
