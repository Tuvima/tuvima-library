param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [string]$OutputPath = (Join-Path $PSScriptRoot "endpoint-registration-ledger.md")
)

$sources = @(
    (Join-Path $RepositoryRoot "src/MediaEngine.Api/Endpoints/*.cs")
    (Join-Path $RepositoryRoot "src/MediaEngine.Api/DevSupport/*Endpoints.cs")
    (Join-Path $RepositoryRoot "src/MediaEngine.Web/Endpoints/*.cs")
    (Join-Path $RepositoryRoot "src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs")
    (Join-Path $RepositoryRoot "src/MediaEngine.Web/Services/Integration/ViewMediaProxyEndpoint.cs")
    (Join-Path $RepositoryRoot "src/MediaEngine.Api/Program.cs")
    (Join-Path $RepositoryRoot "src/MediaEngine.Web/Program.cs")
)

$files = Get-ChildItem $sources -File | Sort-Object FullName -Unique
$rows = [System.Collections.Generic.List[object]]::new()

foreach ($file in $files) {
    $text = Get-Content -Raw $file.FullName
    $groups = @{}
    foreach ($match in [regex]::Matches($text, '(?ms)(?:var|RouteGroupBuilder)\s+(?<name>\w+)\s*=\s*(?<parent>\w+)\.MapGroup\(\s*\$?"(?<path>[^"]+)"')) {
        $parent = $match.Groups['parent'].Value
        $parentPrefix = if ($groups.ContainsKey($parent)) { $groups[$parent].Prefix } else { '' }
        $statementEnd = $text.IndexOf(';', $match.Index)
        if ($statementEnd -lt 0) { $statementEnd = [Math]::Min($text.Length - 1, $match.Index + 1000) }
        $statement = $text.Substring($match.Index, $statementEnd - $match.Index + 1)
        $groupGuards = @()
        foreach ($guard in @('RequireClientScope','RequireAdminOrStandardUser','RequireAnyRole','RequireAdmin','RequireAuthorization','AllowAnonymous')) {
            $guardMatch = [regex]::Match($statement, "\.$guard(?![A-Za-z])(?:\((?<arg>[^)]*)\))?")
            if ($guardMatch.Success) {
                $arg = $guardMatch.Groups['arg'].Value.Trim()
                $groupGuards += if ($arg) { "$guard($arg)" } else { $guard }
            }
        }
        $groups[$match.Groups['name'].Value] = [pscustomobject]@{
            Prefix = $parentPrefix + $match.Groups['path'].Value
            Guards = $groupGuards
        }
    }

    $matches = [regex]::Matches($text, '(?ms)(?<receiver>\w+)\.Map(?<method>Get|Post|Put|Delete|Patch|Methods|HealthChecks|StaticAssets|RazorComponents)\(\s*(?:\$)?"(?<path>[^"]*)"')
    for ($index = 0; $index -lt $matches.Count; $index++) {
        $match = $matches[$index]
        $end = if ($index + 1 -lt $matches.Count) { $matches[$index + 1].Index } else { [Math]::Min($text.Length, $match.Index + 12000) }
        $segment = $text.Substring($match.Index, $end - $match.Index)
        $receiver = $match.Groups['receiver'].Value
        $prefix = if ($groups.ContainsKey($receiver)) { $groups[$receiver].Prefix } else { '' }
        $method = $match.Groups['method'].Value
        if ($method -eq 'Methods') {
            $verbs = [regex]::Matches($segment.Substring(0, [Math]::Min(400, $segment.Length)), 'HttpMethods\.(Get|Head|Post|Put|Patch|Delete)') | ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() }
            $method = ($verbs | Select-Object -Unique) -join '/'
            if ([string]::IsNullOrWhiteSpace($method)) { $method = 'METHODS' }
        } elseif ($method -eq 'HealthChecks') { $method = 'HEALTH' }
        elseif ($method -in @('StaticAssets','RazorComponents')) { $method = 'FRAMEWORK' }
        else { $method = $method.ToUpperInvariant() }

        $guards = @()
        if ($groups.ContainsKey($receiver)) { $guards += @($groups[$receiver].Guards) }
        foreach ($guard in @('RequireClientScope','RequireAdminOrStandardUser','RequireAnyRole','RequireAdmin','RequireAuthorization','AllowAnonymous')) {
            $guardMatch = [regex]::Match($segment, "\.$guard(?![A-Za-z])(?:\((?<arg>[^)]*)\))?")
            if ($guardMatch.Success) {
                $arg = $guardMatch.Groups['arg'].Value.Trim()
                $guards += if ($arg) { "$guard($arg)" } else { $guard }
            }
        }
        if ($guards.Count -eq 0) { $guards = @('fallback policy / middleware') }

        $line = 1 + ($text.Substring(0, $match.Index) -split "`n").Count - 1
        $relative = [IO.Path]::GetRelativePath($RepositoryRoot, $file.FullName).Replace('\','/')
        $route = ($prefix + $match.Groups['path'].Value).Replace('|','\|')
        $environment = if ($relative -like 'src/MediaEngine.Api/DevSupport/*') { 'Development only' } else { 'Production' }
        $rows.Add([pscustomobject]@{ Method=$method; Route=$route; Guard=($guards -join ', '); Source="$relative`:$line"; Environment=$environment })
    }
}

$header = @"
# Endpoint registration ledger

Generated from source by ``generate-endpoint-ledger.ps1``. It is a syntactic inventory, not proof that a guard authorizes the correct resource. Re-run after route changes and reconcile against ``endpoint-matrix.md``. Routes containing interpolated helper fragments are preserved as written.

| Method | Registered route | Declared guard | Environment | Source |
| --- | --- | --- | --- | --- |

"@
$body = $rows | ForEach-Object { "| $($_.Method) | ``$($_.Route)`` | $($_.Guard.Replace('|','\|')) | $($_.Environment) | ``$($_.Source)`` |" }
($header + ($body -join "`n") + "`n") | Set-Content -Encoding utf8 $OutputPath
Write-Output "Wrote $($rows.Count) registrations to $OutputPath"
