Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-OperationsAz {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $result = & az @Arguments --only-show-errors --output json 2>&1
    if ($LASTEXITCODE -ne 0) {
        $code = 'Unavailable'
        $match = [regex]::Match(($result -join "`n"), '"code"\s*:\s*"([^"]+)"')
        if ($match.Success -and $match.Groups[1].Value -in @('AuthorizationFailed', 'Forbidden',
            'TooManyRequests', 'InvalidApiVersionParameter', 'InvalidQuery', 'BadRequest',
            'ResourceNotFound', 'OfferNotSupported', 'RBACAccessDenied', 'AccessDenied')) {
            $code = $match.Groups[1].Value
        }
        throw "Azure CLI operation '$($Arguments[0])' failed (exit $LASTEXITCODE, category $code). Inspect permissions and resource state privately in Azure; upstream responses are not printed."
    }
    $stdout = @($result | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
    if ($stdout.Count) { ($stdout -join "`n") | ConvertFrom-Json -Depth 100 }
}

function Invoke-OperationsRest {
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PUT')][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [object]$Body
    )
    if (-not $Path.StartsWith('/subscriptions/')) { throw 'Expected a subscription-scoped ARM path.' }
    $arguments = @('rest', '--method', $Method, '--url', "https://management.azure.com$Path")
    $temporaryBody = $null
    try {
        if ($null -ne $Body) {
            $temporaryBody = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
            [IO.File]::WriteAllText($temporaryBody, ($Body | ConvertTo-Json -Depth 30 -Compress))
            $arguments += @('--body', "@$temporaryBody", '--headers', 'Content-Type=application/json')
        }
        Invoke-OperationsAz -Arguments $arguments
    } finally {
        if ($temporaryBody -and (Test-Path -LiteralPath $temporaryBody)) {
            Remove-Item -LiteralPath $temporaryBody -Force
        }
    }
}

function Assert-OperationsSubscription {
    param([string]$SubscriptionId)
    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParse($SubscriptionId, [ref]$parsed)) {
        throw 'Supply the intended SubscriptionId explicitly.'
    }
}

function Assert-OperationsEmail {
    param([string]$NotificationEmail)
    if ([string]::IsNullOrWhiteSpace($NotificationEmail)) { throw 'An owner-approved NotificationEmail is required.' }
    $parsed = [System.Net.Mail.MailAddress]::new($NotificationEmail)
    if ($parsed.Address -ne $NotificationEmail) { throw 'Supply one plain notification email address without a display name.' }
}
