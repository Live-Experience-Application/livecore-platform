#requires -Version 5.1

<#
.SYNOPSIS
    Storage/self-hosting docs RustFS backstop analysis (CORE-STOR-002).

.DESCRIPTION
    Shared logic for the grep backstop that keeps the self-hosting and storage docs
    pointed at RustFS as the DEFAULT EXAMPLE S3-compatible object-storage backend after
    the MinIO -> RustFS migration (CORE-STOR-001 swapped the compose stack; CORE-STOR-002
    swept the docs). The logic lives in this module so the runner
    (scripts/lint-storage-docs-rustfs.ps1) and the seeded tests
    (scripts/test-storage-docs-rustfs.ps1) share one implementation.

    For each supplied document it enforces three invariants:
      1. the document names RustFS (the default example backend);
      2. the document carries NO leftover MinIO setup/console/credential guidance where
         RustFS is now the default (the grep backstop);
      3. where required, the document keeps the any-S3-compatible contract clearly
         stated ("any S3-compatible ...") so operators may still bring their own
         provider (ADR 0006).

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh).
#>

# Returns the backstop findings (an array of human-readable strings; empty when every
# supplied document satisfies the invariants). Each document is an object exposing
# Name (a label), Content (the raw text) and RequireAnyS3 (whether the any-S3-compatible
# contract must be stated in this document).
function Get-LiveCoreStorageDocFinding {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IEnumerable]$Document
    )

    $findings = New-Object System.Collections.Generic.List[string]

    foreach ($doc in $Document) {
        $name = [string]$doc.Name
        $content = [string]$doc.Content
        $requireAnyS3 = [bool]$doc.RequireAnyS3

        # 1) The default example backend must be named.
        if ($content -notmatch '(?i)rustfs') {
            $findings.Add("$name does not reference RustFS (the default example S3-compatible backend).")
        }

        # 2) The grep backstop: no MinIO setup/console/credential guidance may remain
        #    where RustFS is now the default. Report each offending line for a quick fix.
        $lines = $content -split "`r?`n"
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '(?i)minio') {
                $lineNumber = $i + 1
                $snippet = $lines[$i].Trim()
                $findings.Add("$name still references MinIO at line ${lineNumber}: $snippet")
            }
        }

        # 3) The any-S3-compatible contract must stay clearly stated where required.
        if ($requireAnyS3 -and $content -notmatch '(?i)any S3-compatible') {
            $findings.Add("$name no longer states the any-S3-compatible contract ('any S3-compatible ...').")
        }
    }

    return $findings.ToArray()
}

Export-ModuleMember -Function Get-LiveCoreStorageDocFinding
