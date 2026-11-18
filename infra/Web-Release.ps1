# Dot-sourced by Deploy-Web.ps1; definitions only.
function Get-WebProbes {
    @(
        @{ type = 'Startup'; httpGet = @{ path = '/health'; port = 8080; scheme = 'HTTP' }; periodSeconds = 5; timeoutSeconds = 2; failureThreshold = 30 }
        @{ type = 'Liveness'; httpGet = @{ path = '/health'; port = 8080; scheme = 'HTTP' }; periodSeconds = 15; timeoutSeconds = 3; failureThreshold = 6 }
        @{ type = 'Readiness'; httpGet = @{ path = '/health/ready'; port = 8080; scheme = 'HTTP' }; periodSeconds = 30; timeoutSeconds = 10; failureThreshold = 1 }
    )
}

function Get-WebApp {
    $appId = "/subscriptions/$($state.SubscriptionId)/resourceGroups/$ResourceGroup/providers/Microsoft.App/containerApps/$web"
    AzJson @('rest', '--method', 'get', '--uri', "${appId}?api-version=2025-07-01")
}

function Assert-SqlPrivateAccess($Server) {
    if ($Server.publicNetworkAccess -ne 'Disabled') {
        throw 'SQL public network access must be Disabled before deployment. Restore private-only access and verify the private endpoint.'
    }
}

function Assert-SqlBaseline($SqlDatabase) {
    if ($SqlDatabase.currentServiceObjectiveName -ne 'GP_S_Gen5_1' -or
        $SqlDatabase.sku.tier -ne 'GeneralPurpose' -or $SqlDatabase.sku.name -notin @('GP_S_Gen5', 'GP_S_Gen5_1') -or
        $SqlDatabase.sku.family -ne 'Gen5' -or $SqlDatabase.sku.capacity -ne 1 -or
        $SqlDatabase.minCapacity -ne 0.5 -or $SqlDatabase.maxSizeBytes -ne 34359738368 -or
        $SqlDatabase.autoPauseDelay -ne 60) {
        throw 'Azure SQL did not report the required GP_S_Gen5_1 baseline. Inspect the database configuration before deploying.'
    }
}

function Get-WebRevision([string]$Revision) {
    AzJson @('containerapp', 'revision', 'show', '--name', $web, '--resource-group', $ResourceGroup, '--revision', $Revision)
}

function Assert-WebReleaseMode($App) {
    if ($App.properties.configuration.activeRevisionsMode -ne 'Single' -or
        $App.properties.template.scale.minReplicas -ne 0 -or $App.properties.template.scale.maxReplicas -ne 1 -or
        -not $App.properties.configuration.ingress.external) {
        throw 'Release requires external ingress, single revision mode and a 0-1 replica template.'
    }
}

function Resolve-WebImage([string]$Image) {
    $hostName = "$registry.azurecr.io/"
    if (-not $Image.StartsWith($hostName, [StringComparison]::OrdinalIgnoreCase) -or
        $Image.Substring($hostName.Length) -notmatch '^(chess-trainer)(?::[\w.-]+|@sha256:[a-f0-9]{64})$') {
        throw 'The web image is not a tagged or digest-pinned image in the expected registry/repository.'
    }
    $reference = $Image.Substring($hostName.Length)
    $manifest = AzJson @('acr', 'repository', 'show', '--name', $registry, '--image', $reference)
    if ($manifest.digest -cnotmatch '^sha256:[a-f0-9]{64}$') { throw 'ACR did not return a valid image digest.' }
    if ($reference.Contains('@') -and $reference.Split('@')[1] -cne $manifest.digest) {
        throw 'ACR returned a different digest for the pinned image.'
    }
    "${hostName}chess-trainer@$($manifest.digest)"
}

function Test-WebReadinessResponse {
    param($Response, [string]$Revision, [string]$Release, [switch]$Legacy)
    $Response.status -ceq 'ready' -and ($Legacy -or
        ($Release -and $Response.release -ceq $Release -and $Response.revision -ceq $Revision))
}

function Wait-WebRelease {
    param([string]$Revision, [string]$Image, [string]$Release, [switch]$Legacy)
    $deadline = (Get-Date).AddMinutes(8)
    do {
        $app = Get-WebApp
        Assert-WebReleaseMode $app
        $liveUrl = "https://$($app.properties.configuration.ingress.fqdn)"
        if ($liveUrl -ne $state.Url -or $app.properties.configuration.ingress.targetPort -ne 8080) {
            throw 'The live app ingress does not match the recorded URL and port.'
        }
        if ($app.properties.latestRevisionName -eq $Revision) {
            $candidate = Get-WebRevision $Revision
            $containers = @($candidate.properties.template.containers)
            if ($containers.Count -ne 1 -or $containers[0].image -cne $Image) {
                throw "Revision $Revision does not have the expected image."
            }
            if ($candidate.properties.provisioningState -in @('Failed', 'ProvisioningFailed', 'Canceled')) {
                throw "Revision $Revision failed provisioning."
            }
        }
        $response = $null
        try {
            # A request to the shared host also wakes a replica scaled to zero.
            $response = Invoke-RestMethod "$liveUrl/health/ready" -TimeoutSec 130 -MaximumRedirection 0 `
                -Headers @{ 'Cache-Control' = 'no-cache' } -ErrorAction Stop
        }
        catch { }
        if (Test-WebReadinessResponse $response $Revision $Release -Legacy:$Legacy) {
            # Recheck routing and revision after the HTTP response, not before.
            $latest = Get-WebApp
            Assert-WebReleaseMode $latest
            if ($latest.properties.latestRevisionName -eq $Revision -and
                $latest.properties.latestReadyRevisionName -eq $Revision -and
                "https://$($latest.properties.configuration.ingress.fqdn)" -eq $liveUrl -and
                $latest.properties.configuration.ingress.targetPort -eq 8080) {
                $verified = Get-WebRevision $Revision
                $verifiedContainers = @($verified.properties.template.containers)
                if ($verified.properties.active -eq $true -and $verified.properties.healthState -eq 'Healthy' -and
                    $verifiedContainers.Count -eq 1 -and $verifiedContainers[0].image -ceq $Image) {
                    return
                }
            }
        }
        if ((Get-Date) -ge $deadline) { break }
        Start-Sleep -Seconds 10
    } while ((Get-Date) -lt $deadline)
    throw "Revision $Revision was not verified as serving the intended ready image. Inspect revision diagnostics."
}

function Save-WebReleaseRecord {
    param([string]$Release, [string]$Image)
    $app = Get-WebApp
    Assert-WebReleaseMode $app
    $prior = $app.properties.latestRevisionName
    if (-not $prior -or $prior -ne $app.properties.latestReadyRevisionName) {
        throw 'An earlier revision has not settled. Verify or recover it before starting a new release.'
    }
    $oldRevision = Get-WebRevision $prior
    $containers = @($oldRevision.properties.template.containers)
    if ($containers.Count -ne 1) { throw 'Release expects exactly one web container.' }
    $oldImage = $containers[0].image
    $previous = $null
    if ($oldImage -cne 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest') {
        $oldDigest = Resolve-WebImage $oldImage
        $oldMarker = ($containers[0].env | Where-Object name -CEQ 'Release__Id').value
        Wait-WebRelease $prior $oldImage $oldMarker -Legacy:(-not $oldMarker)
        $previous = @{ Revision = $prior; ConfiguredImage = $oldImage; PinnedImage = $oldDigest; Release = $oldMarker }
    }
    $record = [pscustomobject]@{
        Version = 1; AppId = $app.id; CandidateRevision = "$web--$Release"; CandidateImage = $Image
        BaseRevision = $prior; Previous = $previous
    }
    $path = Join-Path $output "release-$Release.json"
    $record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8 -ErrorAction Stop
    Write-Host "Release record: $path"
    $record
}

function Start-WebCandidate {
    param($Record, [string]$Release, [hashtable]$EnvironmentValues)
    $app = Get-WebApp
    Assert-WebReleaseMode $app
    if ($app.id -ne $Record.AppId -or $app.properties.latestRevisionName -ne $Record.BaseRevision) {
        throw 'The app revision changed during deployment; refusing to replace it.'
    }
    $template = $app.properties.template
    $containers = @($template.containers)
    if ($containers.Count -ne 1) { throw 'Release expects exactly one web container.' }
    $container = $containers[0]
    $kept = @()
    foreach ($entry in $container.env) {
        if (-not $EnvironmentValues.ContainsKey($entry.name)) {
            if (-not $entry.secretRef) { throw 'Unexpected literal container setting; review the template before deploying.' }
            $kept += @{ name = $entry.name; secretRef = $entry.secretRef }
        }
    }
    $envEntries = $kept + @($EnvironmentValues.GetEnumerator() | ForEach-Object { @{ name = $_.Key; value = $_.Value } })
    $container | Add-Member -NotePropertyName env -NotePropertyValue $envEntries -Force
    $container.image = $Record.CandidateImage
    $container | Add-Member -NotePropertyName probes -NotePropertyValue @(Get-WebProbes) -Force
    $template | Add-Member -NotePropertyName revisionSuffix -NotePropertyValue $Release -Force
    # Keep app-scoped authentication, secrets, registry and ingress settings out of revision updates.
    $body = @{ properties = @{ template = $template } }
    $targetPort = $app.properties.configuration.ingress.targetPort
    if ($targetPort -ne 8080) {
        if ($Record.Previous -or $targetPort -ne 80) { throw 'Unexpected ingress port; refusing to change it.' }
        # First deployment only: the placeholder listens on 80 and has no application rollback target.
        AzJson @('containerapp', 'ingress', 'update', '--name', $web, '--resource-group', $ResourceGroup, '--target-port', '8080') | Out-Null
    }
    $path = Join-Path $output "candidate-$Release.json"
    try {
        [IO.File]::WriteAllText($path, ($body | ConvertTo-Json -Depth 50))
        AzJson @('rest', '--method', 'patch', '--uri', "$($app.id)?api-version=2025-07-01", '--body', "@$path") | Out-Null
    }
    finally { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}

function Restore-WebRelease([string]$Manifest) {
    $record = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
    $app = Get-WebApp
    Assert-WebReleaseMode $app
    if ($record.Version -ne 1 -or $record.AppId -ne $app.id -or -not $record.Previous) {
        throw 'The manifest has no known-good application rollback target for this app.'
    }
    if ($app.properties.latestRevisionName -ne $record.CandidateRevision -or
        $app.properties.configuration.ingress.targetPort -ne 8080 -or
        "https://$($app.properties.configuration.ingress.fqdn)" -ne $state.Url) {
        throw 'The recorded candidate is not the latest revision on the expected ingress; refusing stale rollback.'
    }
    $candidate = Get-WebRevision $record.CandidateRevision
    $source = Get-WebRevision $record.Previous.Revision
    if ($candidate.properties.template.containers.image -cne $record.CandidateImage -or
        $source.properties.template.containers.image -cne $record.Previous.ConfiguredImage) {
        throw 'The candidate or rollback source no longer matches the recorded configuration.'
    }
    $image = Resolve-WebImage $record.Previous.PinnedImage
    if ($image -cne $record.Previous.PinnedImage) { throw 'The previous image must be pinned by digest.' }
    $rollback = 'rb' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    $arguments = @('containerapp', 'revision', 'copy', '--name', $web, '--resource-group', $ResourceGroup,
        '--from-revision', $record.Previous.Revision, '--revision-suffix', $rollback, '--image', $image)
    if ($record.Previous.Release) { $arguments += @('--set-env-vars', "Release__Id=$rollback") }
    AzJson $arguments | Out-Null
    Wait-WebRelease "$web--$rollback" $image $rollback -Legacy:(-not $record.Previous.Release)
    Write-Output "Application rollback verified: $web--$rollback ($image). SQL schema/data were not rolled back."
}
