[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Apply,
    [switch]$RetentionOnly,
    [string]$SubscriptionId,
    [string]$ResourceGroup = 'rg-chess-trainer-cloud',
    [string]$EnvironmentName = 'chess-trainer-cloud-environment',
    [string]$AppName,
    [string]$SqlServerName,
    [string]$SqlDatabaseName = 'trainer',
    [string]$VisionResourceId,
    [string]$WorkspaceResourceId,
    [Nullable[int]]$ExpectedRetentionDays,
    [string]$NotificationEmail
)

. "$PSScriptRoot\Operations.Common.ps1"

if (-not $Apply) {
    $required = @('SubscriptionId', 'AppName', 'SqlServerName', 'VisionResourceId',
        'WorkspaceResourceId', 'ExpectedRetentionDays')
    if (-not $RetentionOnly) { $required += 'NotificationEmail' }
    [ordered]@{
        Mode = 'PlanOnly'
        Requires = $required
        NotificationControls = if ($RetentionOnly) { 'Deferred; no action group or alert rules' } else { 'Require owner recipient' }
        Changes = @('Reuse an existing workspace with owner-approved retention; never change shared retention',
            'Route environment console/system logs through Azure Monitor',
            'Create a dedicated owner action group and passive app/SQL/vision metric alerts')
        DoesNotDo = @('Create a workspace', 'Send a test notification', 'Call health or SQL endpoints',
            'Schedule credential expiry checks', 'Change scale-to-zero or database pause settings')
    } | ConvertTo-Json -Depth 5
    return
}

Assert-OperationsSubscription $SubscriptionId
if (-not $RetentionOnly) { Assert-OperationsEmail $NotificationEmail }
if (-not $AppName -or -not $SqlServerName -or -not $VisionResourceId) {
    throw 'Apply requires AppName, SqlServerName and the exact VisionResourceId.'
}
if ($WorkspaceResourceId -notmatch '^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft\.OperationalInsights/workspaces/[^/]+$') {
    throw 'Supply an existing Log Analytics WorkspaceResourceId.'
}
if ($null -eq $ExpectedRetentionDays -or $ExpectedRetentionDays -lt 30 -or $ExpectedRetentionDays -gt 730) {
    throw 'Select ExpectedRetentionDays explicitly (30-730 days); shared workspace retention is not changed.'
}
$scope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup"
$environmentId = "$scope/providers/Microsoft.App/managedEnvironments/$EnvironmentName"
$appId = "$scope/providers/Microsoft.App/containerApps/$AppName"
$sqlId = "$scope/providers/Microsoft.Sql/servers/$SqlServerName/databases/$SqlDatabaseName"
if ($VisionResourceId -notmatch "^/subscriptions/$([regex]::Escape($SubscriptionId))/resourceGroups/[^/]+/providers/Microsoft\.CognitiveServices/accounts/[^/]+$") {
    throw 'VisionResourceId must identify the intended Cognitive Services account in this subscription.'
}
$workspace = Invoke-OperationsRest GET "${WorkspaceResourceId}?api-version=2022-10-01"
if ($workspace.properties.retentionInDays -ne $ExpectedRetentionDays) {
    throw 'Workspace retention differs from the approved value. Select an appropriate existing workspace; do not silently change a shared one.'
}
$app = Invoke-OperationsAz @('containerapp', 'show', '-g', $ResourceGroup, '-n', $AppName, '--subscription', $SubscriptionId)
if ($app.properties.managedEnvironmentId -ne $environmentId) { throw 'The selected application does not use this environment.' }
$applications = @(Invoke-OperationsAz @('containerapp', 'list', '--subscription', $SubscriptionId,
    '--query', '[].{id:id,environment:properties.managedEnvironmentId}'))
$inEnvironment = @($applications | Where-Object { $null -ne $_ -and $_.environment -eq $environmentId })
if ($inEnvironment.Count -ne 1 -or $inEnvironment[0].id -ne $appId) {
    throw 'Environment logging affects other applications. Review shared-environment privacy, cost and coverage before configuring it separately.'
}
$environment = Invoke-OperationsAz @('containerapp', 'env', 'show', '-g', $ResourceGroup, '-n', $EnvironmentName, '--subscription', $SubscriptionId)
if ($null -ne $environment.properties.appLogsConfiguration -and
    $environment.properties.appLogsConfiguration.destination -eq 'log-analytics') {
    throw 'This environment already uses a direct Log Analytics sink. Review/reuse it instead of replacing existing monitoring.'
}
$groups = @(Invoke-OperationsAz @('monitor', 'action-group', 'list', '-g', $ResourceGroup, '--subscription', $SubscriptionId))
$groupName = 'chess-trainer-operations'
if (-not $RetentionOnly -and @($groups | Where-Object { $null -ne $_ -and $_.name -eq $groupName }).Count) {
    throw 'The managed action-group name already exists. Review existing coverage rather than overwriting receivers.'
}
$settings = @(Invoke-OperationsAz @('monitor', 'diagnostic-settings', 'list', '--resource', $environmentId, '--subscription', $SubscriptionId))
$managedSettings = @($settings | Where-Object { $null -ne $_ -and $_.name -eq 'chess-trainer-logs' })
$reuseSetting = $managedSettings.Count -eq 1
if ($managedSettings.Count -gt 1) { throw 'Multiple managed diagnostic settings were returned.' }
if ($reuseSetting) {
    $setting = $managedSettings[0]
    $destination = $setting.PSObject.Properties['workspaceId']
    if ($null -eq $destination -or $destination.Value -ne $WorkspaceResourceId) {
        throw 'The managed diagnostic setting has a different destination; refusing to overwrite it.'
    }
    foreach ($name in @('storageAccountId', 'eventHubAuthorizationRuleId', 'serviceBusRuleId', 'marketplacePartnerId')) {
        $extra = $setting.PSObject.Properties[$name]
        if ($null -ne $extra -and $extra.Value) { throw 'The managed setting has additional destinations; review it separately.' }
    }
    $categories = @($setting.logs | Where-Object enabled | ForEach-Object category | Sort-Object)
    if (($categories -join ',') -ne 'ContainerAppConsoleLogs,ContainerAppSystemLogs') {
        throw 'The managed setting has different log categories; review it separately.'
    }
}
$managedRuleNames = @('chess-trainer-http-errors', 'chess-trainer-restarts',
    'chess-trainer-sql-connections', 'chess-trainer-vision-errors')
$existingRules = @(Invoke-OperationsAz @('monitor', 'metrics', 'alert', 'list', '-g', $ResourceGroup, '--subscription', $SubscriptionId))
if (-not $RetentionOnly -and @($existingRules | Where-Object { $null -ne $_ -and $_.name -in $managedRuleNames }).Count) {
    throw 'A managed metric-alert name already exists. Review/reuse its scope and recipients rather than overwriting it.'
}
if (-not $PSCmdlet.ShouldProcess($ResourceGroup, 'Configure selected retained logs/notifications (may incur Azure Monitor charges)')) { return }

if ($environment.properties.appLogsConfiguration.destination -ne 'azure-monitor') {
    Invoke-OperationsAz @('containerapp', 'env', 'update', '-g', $ResourceGroup, '-n', $EnvironmentName,
        '--logs-destination', 'azure-monitor', '--subscription', $SubscriptionId) | Out-Null
}
if (-not $reuseSetting) {
    Invoke-OperationsAz @('monitor', 'diagnostic-settings', 'create', '-n', 'chess-trainer-logs', '--resource', $environmentId,
        '--workspace', $WorkspaceResourceId, '--logs', '[{"category":"ContainerAppConsoleLogs","enabled":true},{"category":"ContainerAppSystemLogs","enabled":true}]',
        '--subscription', $SubscriptionId) | Out-Null
}
if ($RetentionOnly) {
    Write-Output 'Retained logging configured. Notification delivery and alert rules are not configured in RetentionOnly mode.'
    return
}
$action = Invoke-OperationsAz @('monitor', 'action-group', 'create', '-g', $ResourceGroup, '-n', $groupName,
    '--short-name', 'ChessTrainer', '--action', 'email', 'owner', $NotificationEmail, '--subscription', $SubscriptionId)
$rules = @(
    @{ Name = 'chess-trainer-http-errors'; Scope = $appId; Condition = 'total Requests >= 5 where statusCodeCategory includes 5xx'; Detail = 'Repeated HTTP failures: inspect safe operation events and readiness/release identity; see README operations.' },
    @{ Name = 'chess-trainer-restarts'; Scope = $appId; Condition = 'max RestartCount >= 3'; Detail = 'Repeated restarts: inspect system logs and process probe/startup configuration; see README operations.' },
    @{ Name = 'chess-trainer-sql-connections'; Scope = $sqlId; Condition = 'total connection_failed >= 5'; Detail = 'Repeated SQL failures: inspect identity/private DNS and resume behavior; a paused serverless database is expected.' },
    @{ Name = 'chess-trainer-vision-errors'; Scope = $VisionResourceId; Condition = 'total TotalErrors >= 3'; Detail = 'Vision failures: inspect provider health/deployment/quota/identity permissions; never log or resend the source screenshot automatically.' }
)
foreach ($rule in $rules) {
    Invoke-OperationsAz @('monitor', 'metrics', 'alert', 'create', '-g', $ResourceGroup, '-n', $rule.Name,
        '--scopes', $rule.Scope, '--condition', $rule.Condition, '--window-size', '15m', '--evaluation-frequency', '5m',
        '--severity', '2', '--description', $rule.Detail, '--action', $action.id, '--subscription', $SubscriptionId) | Out-Null
}
Write-Output 'Monitoring configuration applied. Retention across restart, metric delivery, action-group notification and credential checks still require controlled acceptance.'
