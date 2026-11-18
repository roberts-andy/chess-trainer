param(
    [string]$ResourceGroup = 'rg-chess-trainer-cloud',
    [string]$Location = 'eastus2',
    [string]$SqlLocation = 'centralus',
    [string]$SqlServerName = '',
    [string]$LocalDatabase = '',
    [string]$PackageSource = '',
    [ValidateRange(1, 60)][int]$EnvironmentWaitMinutes = 25,
    [switch]$ProvisionOnly,
    [switch]$DeployOnly
)
$ErrorActionPreference = 'Stop'
if ($ProvisionOnly -and $DeployOnly) { throw 'Choose either ProvisionOnly or DeployOnly.' }
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\cloud'
New-Item -ItemType Directory -Force $output | Out-Null
$project = Join-Path $root 'src\ChessTrainer.Web'
function AzJson {
    param([string[]]$Arguments)
    $result = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) { throw "Azure operation failed: $($Arguments[0..1] -join ' ')" }
    if ($result) { ($result -join "`n") | ConvertFrom-Json }
}
function ProtectCloudEntry {
    param([string]$WebName, [string]$Group)
    $publicPaths = '/health,/health/ready,/manifest.webmanifest,/icons/*,/sw.js,/app.js,/app*.css,/ChessTrainer.Web*.css,/_framework/*,/Components/*,/pieces/*,/third-party-notices.txt'
    AzJson @('containerapp', 'auth', 'update', '--name', $WebName, '--resource-group', $Group,
        '--enabled', 'true', '--unauthenticated-client-action', 'RedirectToLoginPage', '--redirect-provider', 'azureActiveDirectory',
        '--require-https', 'true', '--excluded-paths', $publicPaths) | Out-Null
}
$account = AzJson @('account', 'show')
$user = AzJson @('ad', 'signed-in-user', 'show')
$suffix = $account.id.Substring(0, 8)
$web = "chess-trainer-web-$suffix"
$server = if ($SqlServerName) { $SqlServerName } else { "chess-trainer-sql-$suffix-central" }
$registry = "chesstrainer$suffix"
$environment = 'chess-trainer-cloud-environment'
$database = 'trainer'
$statePath = Join-Path $output 'deployment.json'
if (-not $DeployOnly) {
    AzJson @('group', 'create', '--name', $ResourceGroup, '--location', $Location,
        '--tags', 'project=chess-trainer', 'workload=web-mobile') | Out-Null
    AzJson @('acr', 'create', '--name', $registry, '--resource-group', $ResourceGroup,
        '--location', $Location, '--sku', 'Basic', '--admin-enabled', 'false') | Out-Null
    $identity = AzJson @('identity', 'create', '--name', 'chess-trainer-cloud', '--resource-group', $ResourceGroup, '--location', $Location)
    $acr = AzJson @('acr', 'show', '--name', $registry, '--resource-group', $ResourceGroup)
    AzJson @('role', 'assignment', 'create', '--assignee-object-id', $identity.principalId,
        '--assignee-principal-type', 'ServicePrincipal', '--role', 'AcrPull', '--scope', $acr.id) | Out-Null
    AzJson @('sql', 'server', 'create', '--name', $server, '--resource-group', $ResourceGroup,
        '--location', $SqlLocation, '--enable-public-network', 'false', '--enable-ad-only-auth',
        '--external-admin-principal-type', 'User', '--external-admin-name', $user.userPrincipalName,
        '--external-admin-sid', $user.id) | Out-Null
    AzJson @('sql', 'db', 'create', '--name', $database, '--server', $server, '--resource-group', $ResourceGroup,
        '--service-objective', 'S0', '--max-size', '250GB', '--backup-storage-redundancy', 'Local') | Out-Null
    $sqlServer = AzJson @('sql', 'server', 'show', '--name', $server, '--resource-group', $ResourceGroup)
    if ($sqlServer.publicNetworkAccess -ne 'Disabled') { throw 'The SQL server must have public network access disabled.' }

    $feature = AzJson @('feature', 'show', '--namespace', 'Microsoft.Network', '--name', 'AllowBringYourOwnPublicIpAddress')
    if ($feature.properties.state -ne 'Registered') {
        $feature = AzJson @('feature', 'register', '--namespace', 'Microsoft.Network', '--name', 'AllowBringYourOwnPublicIpAddress')
        if ($feature.properties.state -ne 'Registered') { throw 'The required Microsoft.Network feature is awaiting registration. Wait for approval/registration before retrying.' }
        AzJson @('provider', 'register', '--namespace', 'Microsoft.Network', '--wait') | Out-Null
    }
    $networkName = 'chess-trainer-network'
    $networks = @(AzJson @('network', 'vnet', 'list', '--resource-group', $ResourceGroup))
    if ($networkName -notin $networks.name) {
        AzJson @('network', 'vnet', 'create', '--name', $networkName, '--resource-group', $ResourceGroup,
            '--location', $Location, '--address-prefixes', '10.43.0.0/16', '--subnet-name', 'web', '--subnet-prefixes', '10.43.4.0/23') | Out-Null
    }
    $subnet = AzJson @('network', 'vnet', 'subnet', 'create', '--name', 'web', '--vnet-name', $networkName,
        '--resource-group', $ResourceGroup, '--address-prefixes', '10.43.4.0/23', '--delegations', 'Microsoft.App/environments')
    $dataSubnet = AzJson @('network', 'vnet', 'subnet', 'create', '--name', 'data', '--vnet-name', $networkName,
        '--resource-group', $ResourceGroup, '--address-prefixes', '10.43.2.0/24', '--disable-private-endpoint-network-policies', 'true')
    $zone = @(AzJson @('network', 'private-dns', 'zone', 'list', '--resource-group', $ResourceGroup)) |
        Where-Object name -eq 'privatelink.database.windows.net'
    if (-not $zone) {
        $zone = AzJson @('network', 'private-dns', 'zone', 'create', '--name', 'privatelink.database.windows.net', '--resource-group', $ResourceGroup)
    }
    $links = @(AzJson @('network', 'private-dns', 'link', 'vnet', 'list', '--zone-name', $zone.name, '--resource-group', $ResourceGroup))
    $network = AzJson @('network', 'vnet', 'show', '--name', $networkName, '--resource-group', $ResourceGroup)
    if ('trainer-network' -notin $links.name) {
        AzJson @('network', 'private-dns', 'link', 'vnet', 'create', '--name', 'trainer-network', '--resource-group', $ResourceGroup,
            '--zone-name', $zone.name, '--virtual-network', $network.id, '--registration-enabled', 'false') | Out-Null
    }
    $endpoints = @(AzJson @('network', 'private-endpoint', 'list', '--resource-group', $ResourceGroup))
    if ('chess-trainer-sql-private' -notin $endpoints.name) {
        AzJson @('network', 'private-endpoint', 'create', '--name', 'chess-trainer-sql-private', '--resource-group', $ResourceGroup,
            '--location', $Location, '--subnet', $dataSubnet.id, '--private-connection-resource-id', $sqlServer.id,
            '--group-id', 'sqlServer', '--connection-name', 'trainer-sql') | Out-Null
    }
    AzJson @('network', 'private-endpoint', 'dns-zone-group', 'create', '--endpoint-name', 'chess-trainer-sql-private',
        '--name', 'default', '--resource-group', $ResourceGroup, '--private-dns-zone', $zone.id, '--zone-name', 'sql') | Out-Null

    $existing = @(AzJson @('containerapp', 'env', 'list', '--resource-group', $ResourceGroup)) | Where-Object name -eq $environment
    if (-not $existing) {
        AzJson @('containerapp', 'env', 'create', '--name', $environment, '--resource-group', $ResourceGroup,
            '--location', $Location, '--infrastructure-subnet-resource-id', $subnet.id, '--logs-destination', 'none', '--no-wait') | Out-Null
    }
    $deadline = (Get-Date).AddMinutes($EnvironmentWaitMinutes)
    do {
        $managedEnvironment = AzJson @('containerapp', 'env', 'show', '--name', $environment, '--resource-group', $ResourceGroup)
        $status = $managedEnvironment.properties.provisioningState
        if ($status -eq 'Succeeded') { break }
        if ($status -in @('Failed', 'Canceled', 'Deleted')) { throw "Container environment provisioning ended with status $status." }
        if ((Get-Date) -ge $deadline) { throw "Container environment is still $status. Azure provisioning has not completed; rerun once it is ready." }
        Start-Sleep -Seconds 15
    } while ($true)
    $vision = AzJson @('cognitiveservices', 'account', 'show', '--name', "chess-trainer-vision-$suffix", '--resource-group', 'rg-chess-trainer-ai')
    AzJson @('role', 'assignment', 'create', '--assignee-object-id', $identity.principalId,
        '--assignee-principal-type', 'ServicePrincipal', '--role', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd', '--scope', $vision.id) | Out-Null
    $sql = "Server=tcp:$server.database.windows.net,1433;Database=$database;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"
    $applicationEnvironment = @("AZURE_CLIENT_ID=$($identity.clientId)", "Authentication__TenantId=$($account.tenantId)",
        "Authentication__OwnerId=$($user.id)", "ConnectionStrings__Trainer=$sql",
        "Vision__Endpoint=https://chess-trainer-vision-$suffix.openai.azure.com/", 'Vision__Deployment=chess-vision',
        'ASPNETCORE_FORWARDEDHEADERS_ENABLED=true', 'ASPNETCORE_HTTP_PORTS=8080')
    $existingApp = @(AzJson @('containerapp', 'list', '--resource-group', $ResourceGroup)) | Where-Object name -eq $web
    if (-not $existingApp) {
        AzJson (@('containerapp', 'create', '--name', $web, '--resource-group', $ResourceGroup,
            '--environment', $environment, '--image', 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest',
            '--ingress', 'external', '--target-port', '80', '--cpu', '0.5', '--memory', '1Gi',
            '--min-replicas', '0', '--max-replicas', '1', '--user-assigned', $identity.id, '--env-vars') + $applicationEnvironment) | Out-Null
    }
    elseif ($existingApp.properties.environmentId -ne $managedEnvironment.id) {
        throw 'The existing app belongs to a different environment. Migrate it explicitly before continuing.'
    }
    else {
        AzJson (@('containerapp', 'update', '--name', $web, '--resource-group', $ResourceGroup, '--set-env-vars') + $applicationEnvironment) | Out-Null
    }
    $app = AzJson @('containerapp', 'show', '--name', $web, '--resource-group', $ResourceGroup)
    $url = "https://$($app.properties.configuration.ingress.fqdn)"
    $registrationName = "Chess Trainer Web ($web)"
    $registrations = @(AzJson @('ad', 'app', 'list', '--display-name', $registrationName))
    if ($registrations.Count -gt 1) { throw 'Multiple matching app registrations; resolve these before deploying.' }
    if ($registrations.Count -eq 1) {
        $registration = $registrations[0]
        AzJson @('ad', 'app', 'update', '--id', $registration.appId, '--web-redirect-uris', "$url/.auth/login/aad/callback") | Out-Null
    }
    else {
        $registration = AzJson @('ad', 'app', 'create', '--display-name', $registrationName,
            '--sign-in-audience', 'AzureADMyOrg', '--web-redirect-uris', "$url/.auth/login/aad/callback", '--enable-id-token-issuance', 'true')
        AzJson @('ad', 'sp', 'create', '--id', $registration.appId) | Out-Null
    }
    $credentials = @(AzJson @('ad', 'app', 'credential', 'list', '--id', $registration.appId))
    $secrets = @(AzJson @('containerapp', 'secret', 'list', '--name', $web, '--resource-group', $ResourceGroup))
    $validCredential = $credentials | Where-Object { $_.displayName -eq 'Container Apps Easy Auth' -and [DateTimeOffset]$_.endDateTime -gt [DateTimeOffset]::UtcNow.AddDays(30) }
    if (-not $validCredential -or 'microsoft-provider-authentication-secret' -notin $secrets.name) {
        $credential = AzJson @('ad', 'app', 'credential', 'reset', '--id', $registration.appId, '--append',
            '--display-name', 'Container Apps Easy Auth', '--years', '1')
        AzJson @('containerapp', 'secret', 'set', '--name', $web, '--resource-group', $ResourceGroup,
            '--secrets', "microsoft-provider-authentication-secret=$($credential.password)") | Out-Null
    }
    AzJson @('containerapp', 'auth', 'microsoft', 'update', '--name', $web, '--resource-group', $ResourceGroup,
        '--client-id', $registration.appId, '--client-secret-name', 'microsoft-provider-authentication-secret',
        '--issuer', "https://login.microsoftonline.com/$($account.tenantId)/v2.0",
        '--allowed-audiences', $registration.appId) | Out-Null
    ProtectCloudEntry $web $ResourceGroup
    AzJson @('containerapp', 'registry', 'set', '--name', $web, '--resource-group', $ResourceGroup,
        '--server', "$registry.azurecr.io", '--identity', $identity.id) | Out-Null
    @{
        ResourceGroup = $ResourceGroup; WebName = $web; SqlServer = $server; Database = $database; Registry = $registry
        Url = $url; ClientId = $registration.appId; PrincipalId = $identity.principalId; IdentityId = $identity.id
        TenantId = $account.tenantId; OwnerId = $user.id; SubscriptionId = $account.id; Environment = $environment
    } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
}
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
if ($state.SubscriptionId -ne $account.id) { throw 'Select the subscription recorded in artifacts\cloud\deployment.json before deploying.' }
$ResourceGroup = $state.ResourceGroup
$web = $state.WebName
$server = $state.SqlServer
$database = $state.Database
$registry = $state.Registry
$environment = $state.Environment
if ($ProvisionOnly) { Write-Output "Provisioned $($state.Url). Ready for private-network database initialization and app deployment."; return }
ProtectCloudEntry $web $ResourceGroup
$runtimeIdentity = AzJson @('identity', 'show', '--ids', $state.IdentityId)
$runtimeClientId = [guid]::Empty
if (-not [guid]::TryParse([string]$runtimeIdentity.clientId, [ref]$runtimeClientId) -or $runtimeClientId -eq [guid]::Empty) {
    throw 'The runtime managed identity must have a valid application client ID.'
}

function SetSqlAdministrator {
    param([string]$Name, [string]$PrincipalId)
    AzJson @('sql', 'server', 'ad-admin', 'update', '--server-name', $server, '--resource-group', $ResourceGroup,
        '--display-name', $Name, '--object-id', $PrincipalId) | Out-Null
}
function PublishImage {
    param([string]$Repository, [string]$Tag, [string]$PublishDirectory, [string]$Snapshot = '')
    $arguments = @('publish', $project, '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'false', '--no-restore',
        '/t:PublishContainer', "-p:ContainerRegistry=$registry.azurecr.io", "-p:ContainerRepository=$Repository",
        "-p:ContainerImageTag=$Tag", '-o', $PublishDirectory, '--verbosity', 'quiet')
    if ($Snapshot) { $arguments += "-p:MigrationDatabase=$Snapshot" }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Container publish failed: $Repository." }
}
$tag = Get-Date -Format 'yyyyMMddHHmmss'
$job = 'chess-trainer-bootstrap'
$snapshot = Join-Path $output "migration-$tag.db"
$migrationPublish = Join-Path $output "migration-publish-$tag"
$bootstrapSucceeded = $false
$administratorChanged = $false
$scaled = $false
$env:DOTNET_CONTAINER_REGISTRY_UNAME = '00000000-0000-0000-0000-000000000000'
$token = AzJson @('acr', 'login', '--name', $registry, '--expose-token')
$env:DOTNET_CONTAINER_REGISTRY_PWORD = $token.accessToken
try {
    $restore = @('restore', $project, '--runtime', 'linux-x64', '--verbosity', 'quiet')
    if ($PackageSource) { $restore += @('--source', $PackageSource) }
    & dotnet @restore
    if ($LASTEXITCODE -ne 0) { throw 'Linux restore failed.' }
    PublishImage 'chess-trainer' $tag (Join-Path $output 'web-publish')
    if ($LocalDatabase) {
        & dotnet run --project $project --no-restore --no-launch-profile -- --task snapshot --source-db $LocalDatabase --destination-db $snapshot
        if ($LASTEXITCODE -ne 0) { throw 'Source database snapshot failed.' }
        PublishImage 'chess-trainer-migrate' $tag $migrationPublish $snapshot
    }
    $bootstrap = AzJson @('identity', 'create', '--name', $job, '--resource-group', $ResourceGroup, '--location', $Location)
    $acr = AzJson @('acr', 'show', '--name', $registry, '--resource-group', $ResourceGroup)
    $bootstrapRole = AzJson @('role', 'assignment', 'create', '--assignee-object-id', $bootstrap.principalId,
        '--assignee-principal-type', 'ServicePrincipal', '--role', 'AcrPull', '--scope', $acr.id)
    $administratorChanged = $true
    SetSqlAdministrator $job $bootstrap.principalId
    if ($LocalDatabase) {
        $scaled = $true
        AzJson @('sql', 'db', 'update', '--name', $database, '--server', $server, '--resource-group', $ResourceGroup, '--service-objective', 'S3') | Out-Null
    }
    $jobEnvironment = @('task=bootstrap', "AZURE_CLIENT_ID=$($bootstrap.clientId)", "Authentication__OwnerId=$($state.OwnerId)",
        "ConnectionStrings__Trainer=Server=tcp:$server.database.windows.net,1433;Database=$database;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;",
        "Bootstrap__ApplicationName=$web", "Bootstrap__ClientId=$runtimeClientId")
    $repository = 'chess-trainer'
    if ($LocalDatabase) { $jobEnvironment += 'Bootstrap__SourceDatabase=/app/migration/trainer.db'; $repository = 'chess-trainer-migrate' }
    AzJson (@('containerapp', 'job', 'create', '--name', $job, '--resource-group', $ResourceGroup, '--environment', $environment, '--no-wait',
        '--trigger-type', 'Manual', '--replica-timeout', '7200', '--replica-retry-limit', '0',
        '--replica-completion-count', '1', '--parallelism', '1', '--cpu', '1', '--memory', '2Gi',
        '--image', "$registry.azurecr.io/${repository}:$tag", '--mi-user-assigned', $bootstrap.id,
        '--registry-server', "$registry.azurecr.io", '--registry-identity', $bootstrap.id, '--env-vars') + $jobEnvironment) | Out-Null
    $jobDeadline = (Get-Date).AddMinutes(10)
    do {
        $jobState = (AzJson @('containerapp', 'job', 'show', '--name', $job, '--resource-group', $ResourceGroup)).properties.provisioningState
        if ($jobState -eq 'Succeeded') { break }
        if ($jobState -in @('Failed', 'Canceled') -or (Get-Date) -ge $jobDeadline) {
            throw "Bootstrap job provisioning did not complete: $jobState."
        }
        Start-Sleep -Seconds 10
    } while ($true)
    $execution = AzJson @('containerapp', 'job', 'start', '--name', $job, '--resource-group', $ResourceGroup)
    $deadline = (Get-Date).AddHours(2)
    $previousStatus = ''
    do {
        $executions = @(AzJson @('containerapp', 'job', 'execution', 'list', '--name', $job, '--resource-group', $ResourceGroup))
        $running = $executions | Where-Object name -eq $execution.name
        $status = $running.properties.status
        if ($status -ne $previousStatus) { Write-Output "Private database bootstrap: $status"; $previousStatus = $status }
        if ($status -eq 'Succeeded') { $bootstrapSucceeded = $true; break }
        if ($status -in @('Failed', 'Degraded', 'Canceled')) { throw "Database bootstrap job failed: $status. Inspect its execution logs." }
        if ((Get-Date) -ge $deadline) { throw 'Database bootstrap did not finish within two hours.' }
        Start-Sleep -Seconds 30
    } while ($true)
}
finally {
    try {
        if ($administratorChanged) { SetSqlAdministrator $user.userPrincipalName $user.id }
    }
    finally {
        if ($scaled) { AzJson @('sql', 'db', 'update', '--name', $database, '--server', $server, '--resource-group', $ResourceGroup, '--service-objective', 'S0') | Out-Null }
        Remove-Item Env:\DOTNET_CONTAINER_REGISTRY_UNAME, Env:\DOTNET_CONTAINER_REGISTRY_PWORD -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $snapshot, (Join-Path $migrationPublish 'migration\trainer.db') -ErrorAction SilentlyContinue
    }
}
if ($bootstrapSucceeded) {
    AzJson @('containerapp', 'job', 'delete', '--name', $job, '--resource-group', $ResourceGroup, '--yes') | Out-Null
    AzJson @('role', 'assignment', 'delete', '--ids', $bootstrapRole.id) | Out-Null
    AzJson @('identity', 'delete', '--name', $job, '--resource-group', $ResourceGroup) | Out-Null
    if ($LocalDatabase) { AzJson @('acr', 'repository', 'delete', '--name', $registry, '--image', "chess-trainer-migrate:$tag", '--yes') | Out-Null }
}
AzJson @('containerapp', 'ingress', 'update', '--name', $web, '--resource-group', $ResourceGroup, '--target-port', '8080') | Out-Null
AzJson @('containerapp', 'update', '--name', $web, '--resource-group', $ResourceGroup, '--image', "$registry.azurecr.io/chess-trainer:$tag") | Out-Null
$ready = $false
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    try {
        $response = Invoke-RestMethod "$($state.Url)/health/ready" -TimeoutSec 30
        if ($response.status -eq 'ready') { $ready = $true; break }
    }
    catch { Write-Warning "Readiness check has not passed: $($_.Exception.Message)" }
    Start-Sleep -Seconds 15
}
if (-not $ready) { throw 'Deployment did not become ready. Inspect Container Apps revision and console diagnostics.' }
Write-Output "Deployed $($state.Url)"
