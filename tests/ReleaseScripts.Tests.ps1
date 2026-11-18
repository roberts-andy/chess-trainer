#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$deploy = Join-Path $root 'infra\Deploy-Web.ps1'
$releaseScript = Join-Path $root 'infra\Web-Release.ps1'

function Assert-ReleaseTest([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-ReleaseFailure([scriptblock]$Action, [string]$Expected) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -like $Expected) { return }
        throw
    }
    throw "Expected failure matching '$Expected' but the operation succeeded."
}

function az { throw 'Unexpected call to Azure CLI.' }
function dotnet { throw 'Unexpected call to dotnet.' }

foreach ($file in $deploy, $releaseScript) {
    $tokens = $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
    Assert-ReleaseTest ($errors.Count -eq 0) ("PowerShell parse errors in ${file}: " + ($errors -join '; '))
}

$scratch = Join-Path $root ('artifacts\release-script-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
try {
    $missing = Join-Path $scratch 'not-present.json'
    $full = & $deploy -Plan | ConvertFrom-Json
    $provision = & $deploy -Plan -ProvisionOnly | ConvertFrom-Json
    $migration = & $deploy -Plan -DeployOnly -ScaleForMigration -LocalDatabase $missing | ConvertFrom-Json
    $rollback = & $deploy -Plan -RollbackManifest $missing | ConvertFrom-Json
    Assert-ReleaseTest ($full.Mode -eq 'ProvisionAndDeploy' -and $full.TemporarySqlScale.Count -eq 0) 'Wrong default plan mode.'
    Assert-ReleaseTest ($provision.Mode -eq 'ProvisionOnly') 'Wrong provision plan mode.'
    Assert-ReleaseTest ($migration.Mode -eq 'DeployOnly' -and $migration.TemporarySqlScale -contains 'GP_Gen5_2') 'Missing opt-in migration scale.'
    Assert-ReleaseTest ($rollback.Mode -eq 'ApplicationRollback' -and $rollback.RollbackRequiresSchemaConfirmation) 'Wrong rollback plan or missing safety gate.'
    Assert-ReleaseTest ($full.SqlMaxSizeBytes -eq 34359738368 -and $full.SqlBaseline -contains 'GP_S_Gen5_1') 'Wrong SQL baseline.'
    Assert-ReleaseFailure { & $deploy -RollbackManifest $missing } '*ConfirmSchemaCompatibility*'

    . $releaseScript
    $web = 'trainer-test'
    $ResourceGroup = 'release-tests'
    $output = $scratch
    $state = @{ Url = 'https://trainer.invalid' }
    $release = 'r123'
    $revision = "$web--$release"
    $image = 'example.azurecr.io/chess-trainer@sha256:' + ('a' * 64)
    $baseRevision = "$web--placeholder"
    $appId = "/subscriptions/test/resourceGroups/$ResourceGroup/providers/Microsoft.App/containerApps/$web"
    $record = @{ AppId = $appId; BaseRevision = $baseRevision; CandidateImage = $image; Previous = $null }
    $script:latest = $baseRevision
    $script:latestReady = $baseRevision
    $script:targetPort = 80
    $script:calls = [System.Collections.Generic.List[string]]::new()

    function Get-WebApp {
        @{
            id = $appId
            properties = @{
                latestRevisionName = $script:latest; latestReadyRevisionName = $script:latestReady
                configuration = @{ activeRevisionsMode = 'Single'; ingress = @{ external = $true; targetPort = $script:targetPort; fqdn = 'trainer.invalid' } }
                template = [pscustomobject]@{
                    scale = @{ minReplicas = 0; maxReplicas = 1 }
                    containers = @([pscustomobject]@{ image = 'placeholder' })
                }
            }
        }
    }
    function AzJson([string[]]$Arguments) {
        $script:calls.Add(($Arguments[0..2] -join ' '))
        if (($Arguments[0..2] -join ' ') -eq 'containerapp ingress update' -and $Arguments[-1] -eq '8080') { return }
        if (($Arguments[0..2] -join ' ') -ne 'rest --method patch' -or $Arguments[-2] -ne '--body') {
            throw 'Unexpected Azure request in candidate fixture.'
        }
        $body = Get-Content -LiteralPath $Arguments[-1].Substring(1) -Raw | ConvertFrom-Json
        $template = $body.properties.template
        $container = $template.containers[0]
        Assert-ReleaseTest (-not $body.properties.configuration) 'Candidate patch must not overwrite app-scoped configuration.'
        Assert-ReleaseTest ($template.revisionSuffix -ceq $release -and $container.image -ceq $image) 'Candidate patch has the wrong identity.'
        Assert-ReleaseTest ($container.env[0].name -eq 'Release__Id' -and $container.env[0].value -ceq $release) 'Candidate patch is missing its HTTP identity.'
        Assert-ReleaseTest ($container.probes.Count -eq 3) 'Expected explicit startup, liveness and readiness probes.'
        foreach ($probe in $container.probes) {
            $path = if ($probe.type -eq 'Readiness') { '/health/ready' } else { '/health' }
            Assert-ReleaseTest ($probe.httpGet.path -eq $path -and $probe.httpGet.port -eq 8080) "Wrong $($probe.type) probe routing."
        }
    }

    Start-WebCandidate $record $release @{ Release__Id = $release }
    Assert-ReleaseTest (($script:calls -join ',') -eq 'containerapp ingress update,rest --method patch') 'Unexpected first-deployment calls.'
    Assert-ReleaseTest ((Get-ChildItem -LiteralPath $scratch | Measure-Object).Count -eq 0) 'Candidate payload was left on disk.'

    $script:latest = $script:latestReady = $revision
    $script:targetPort = 8080
    $script:revisionImage = $image
    $script:health = 'Healthy'
    $script:readiness = @{ status = 'ready'; release = $release; revision = $revision }
    function Get-WebRevision($Name) {
        Assert-ReleaseTest ($Name -ceq $revision) 'Queried the wrong revision.'
        @{ properties = @{ active = $true; healthState = $script:health; template = @{ containers = @(@{ image = $script:revisionImage }) } } }
    }
    function Invoke-RestMethod($Uri) {
        if ($Uri -ne 'https://trainer.invalid/health/ready') { throw 'Queried the wrong readiness URL.' }
        $script:readiness
    }
    function Get-Date {
        # Expire after one unsuccessful poll; no wall-clock waits.
        ([datetime]'2026-01-01').AddMinutes(10 * $script:tick++)
    }
    function Start-Sleep { throw 'Unexpected wall-clock wait.' }
    function Test-Candidate {
        $script:tick = 0
        Wait-WebRelease $revision $image $release
    }

    Test-Candidate
    $script:readiness.revision = "$web--previous"
    Assert-ReleaseFailure { Test-Candidate } '*not verified*'
    $script:readiness.revision = $revision
    $script:readiness.status = 'unavailable'
    Assert-ReleaseFailure { Test-Candidate } '*not verified*'
    $script:readiness.status = 'ready'
    $script:revisionImage = $image + 'x'
    Assert-ReleaseFailure { Test-Candidate } '*expected image*'
    $script:revisionImage = $image
    $script:health = 'Unhealthy'
    Assert-ReleaseFailure { Test-Candidate } '*not verified*'
    $script:health = 'Healthy'
    $script:latestReady = "$web--previous"
    Assert-ReleaseFailure { Test-Candidate } '*not verified*'

    Write-Output 'Release script tests passed (all Azure and readiness calls mocked).'
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
