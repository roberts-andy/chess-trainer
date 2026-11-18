[CmdletBinding()]
param(
    [Parameter(Mandatory)][Guid]$ApplicationId,
    [Parameter(Mandatory)][Guid]$CredentialKeyId,
    [ValidateRange(1,365)][int]$WarningDays = 30
)

. "$PSScriptRoot\Operations.Common.ps1"
$credentials = @(Invoke-OperationsAz @('ad', 'app', 'credential', 'list', '--id', $ApplicationId.ToString()))
$expiry = @($credentials | Where-Object { $_.keyId -eq $CredentialKeyId.ToString() -and $_.endDateTime } |
    ForEach-Object { [DateTimeOffset]::Parse($_.endDateTime) })
if ($expiry.Count -ne 1) { throw 'The configured sign-in credential metadata was not found uniquely. Verify the active key ID privately.' }
if ($expiry[0] -le [DateTimeOffset]::UtcNow.AddDays($WarningDays)) {
    throw "Sign-in credentials expire within $WarningDays days. Rerun provisioning, then validate a fresh owner sign-in; never print secret values."
}
Write-Output "The selected credential is valid beyond $WarningDays days. Schedule this metadata-only check with owner notifications."
