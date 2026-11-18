$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$subscription = '11111111-1111-1111-1111-111111111111'
$base = "/subscriptions/$subscription/resourceGroups/rg-chess-trainer-cloud"
$vision = "/subscriptions/$subscription/resourceGroups/vision/providers/Microsoft.CognitiveServices/accounts/vision"
$workspace = "/subscriptions/$subscription/resourceGroups/logs/providers/Microsoft.OperationalInsights/workspaces/logs"
$credentialId = '22222222-2222-2222-2222-222222222222'
$global:ChessOperationsTestState = @{
    Calls = [Collections.Generic.List[object]]::new()
    Expired = $false; BudgetExists = $false; AllowAz = $false
    Base = $base; CredentialId = $credentialId; Workspace = $workspace; ManagedSinkExists = $false
}

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function az {
    param([Parameter(ValueFromRemainingArguments)][object[]]$Arguments)
    if (-not $global:ChessOperationsTestState.AllowAz) { throw 'Plan mode must not call Azure.' }
    $global:LASTEXITCODE = 0
    $text = $Arguments -join ' '
    $call = @{ Arguments = $Arguments; Body = $null }
    $bodyIndex = [Array]::IndexOf($Arguments, '--body')
    if ($bodyIndex -ge 0) {
        $call.Body = Get-Content -LiteralPath ([string]$Arguments[$bodyIndex + 1]).Substring(1) -Raw | ConvertFrom-Json
    }
    $global:ChessOperationsTestState.Calls.Add($call)
    $result = switch -Wildcard ($text) {
        '*Microsoft.OperationalInsights/workspaces/*' { @{ properties = @{ retentionInDays = 30 } }; break }
        'containerapp show *' { @{ properties = @{ managedEnvironmentId = "$($global:ChessOperationsTestState.Base)/providers/Microsoft.App/managedEnvironments/chess-trainer-cloud-environment" } }; break }
        'containerapp list *' { @(@{ id = "$($global:ChessOperationsTestState.Base)/providers/Microsoft.App/containerApps/trainer"; environment = "$($global:ChessOperationsTestState.Base)/providers/Microsoft.App/managedEnvironments/chess-trainer-cloud-environment" }); break }
        'containerapp env show *' { @{ properties = @{ appLogsConfiguration = @{ destination = 'none' } } }; break }
        'monitor action-group list *' { @(); break }
        'monitor diagnostic-settings list *' {
            if ($global:ChessOperationsTestState.ManagedSinkExists) {
                @(@{ name = 'chess-trainer-logs'; workspaceId = $global:ChessOperationsTestState.Workspace; logs = @(
                    @{ category = 'ContainerAppConsoleLogs'; enabled = $true },
                    @{ category = 'ContainerAppSystemLogs'; enabled = $true }) })
            } else { @() }
            break
        }
        'monitor metrics alert list *' { @(); break }
        'monitor action-group create *' { @{ id = "$($global:ChessOperationsTestState.Base)/providers/Microsoft.Insights/actionGroups/chess-trainer-operations" }; break }
        '*--method GET *Microsoft.Consumption/budgets*' {
            @{ value = $(if ($global:ChessOperationsTestState.BudgetExists) { @(@{ name = 'trainer-test' }) } else { @() }) }; break
        }
        '*Microsoft.CostManagement/query*' { @{ properties = @{ nextLink = $null; columns = @(); rows = @() } }; break }
        'ad app credential list *' {
            @(
                @{ keyId = $global:ChessOperationsTestState.CredentialId; endDateTime = [DateTimeOffset]::UtcNow.AddDays($(if ($global:ChessOperationsTestState.Expired) { 1 } else { 60 })).ToString('o') },
                @{ keyId = '33333333-3333-3333-3333-333333333333'; endDateTime = [DateTimeOffset]::UtcNow.AddYears(2).ToString('o') }
            ); break
        }
        default { @{} }
    }
    ConvertTo-Json -InputObject $result -Depth 20 -Compress
}

$monitor = & "$root\infra\Configure-Monitoring.ps1" | ConvertFrom-Json
$cost = & "$root\infra\Manage-CostControls.ps1" | ConvertFrom-Json
Assert-That ($monitor.Mode -eq 'PlanOnly' -and $cost.Mode -eq 'PlanOnly') 'Default modes must be offline plans.'
Assert-That ($global:ChessOperationsTestState.Calls.Count -eq 0) 'Plans must not call Azure.'

$global:ChessOperationsTestState.AllowAz = $true
$monitorParameters = @{
    Apply = $true; SubscriptionId = $subscription; AppName = 'trainer'
    SqlServerName = 'sql'; VisionResourceId = $vision; WorkspaceResourceId = $workspace
    ExpectedRetentionDays = 30; NotificationEmail = 'owner@example.invalid'
}
$retentionParameters = $monitorParameters.Clone()
$retentionParameters.Remove('NotificationEmail')
$retentionMessage = & "$root\infra\Configure-Monitoring.ps1" @retentionParameters -RetentionOnly
Assert-That ($retentionMessage -like '*not configured*') 'Retention-only mode must explicitly report missing notification controls.'
Assert-That (-not @($global:ChessOperationsTestState.Calls | Where-Object {
    ($_.Arguments -join ' ') -match 'action-group create|metrics alert create' }).Count) 'Retention-only must not invent recipients or create alert rules.'
$global:ChessOperationsTestState.ManagedSinkExists = $true
$message = & "$root\infra\Configure-Monitoring.ps1" @monitorParameters
Assert-That ($message -notmatch 'owner@example') 'Monitor output must not echo the recipient.'
$alerts = @($global:ChessOperationsTestState.Calls | Where-Object { ($_.Arguments -join ' ') -like 'monitor metrics alert create *' })
Assert-That ($alerts.Count -eq 4) 'Expected passive app, restart, SQL and vision alerts.'
Assert-That (@($global:ChessOperationsTestState.Calls | Where-Object {
    ($_.Arguments -join ' ') -like 'monitor diagnostic-settings create *' }).Count -eq 1) 'Adding notifications must reuse the verified retained-log setting.'
Assert-That (-not @($global:ChessOperationsTestState.Calls | Where-Object { ($_.Arguments -join ' ') -match 'workspace create|/health|connection-string' }).Count) 'No paid workspace creation or application/SQL probing.'

$budgetParameters = @{
    Action = 'ApplyBudget'; SubscriptionId = $subscription; VisionResourceId = $vision
    BudgetName = 'trainer-test'; MonthlyAmount = [decimal]12.34
    NotificationEmail = 'owner@example.invalid'; ConfirmExistingCoverageReviewed = $true
}
foreach ($scope in @('Hosting', 'Vision')) {
    & "$root\infra\Manage-CostControls.ps1" @budgetParameters -BudgetScope $scope | Out-Null
    $write = @($global:ChessOperationsTestState.Calls | Where-Object { ($_.Arguments -join ' ') -like '*--method PUT *Microsoft.Consumption/budgets/*' })[-1]
    Assert-That ($write.Body.properties.amount -eq 12.34) 'The owner amount must survive nullable conversion and JSON serialization.'
    Assert-That ($write.Body.properties.notifications.Actual80.threshold -eq 80) 'Expected actual-cost warning notification.'
    if ($scope -eq 'Vision') {
        Assert-That ($write.Body.properties.filter.dimensions.values[0] -eq $vision) 'Vision budget must be filtered to the exact account.'
    } else {
        Assert-That (($write.Arguments -join ' ') -like "*$base/providers/Microsoft.Consumption/budgets/*") 'Hosting budget must use the dedicated RG scope.'
    }
}
$global:ChessOperationsTestState.BudgetExists = $true
$writesBefore = @($global:ChessOperationsTestState.Calls | Where-Object { ($_.Arguments -join ' ') -like '*--method PUT *' }).Count
$blocked = $false
try { & "$root\infra\Manage-CostControls.ps1" @budgetParameters | Out-Null } catch { $blocked = $_.Exception.Message -like '*already exists*' }
Assert-That $blocked 'Existing budget recipients/thresholds must not be overwritten.'
Assert-That (@($global:ChessOperationsTestState.Calls | Where-Object { ($_.Arguments -join ' ') -like '*--method PUT *' }).Count -eq $writesBefore) 'Conflict guard must run before PUT.'

& "$root\infra\Test-CredentialExpiry.ps1" -ApplicationId $subscription -CredentialKeyId $credentialId | Out-Null
$global:ChessOperationsTestState.Expired = $true
$blocked = $false
try { & "$root\infra\Test-CredentialExpiry.ps1" -ApplicationId $subscription -CredentialKeyId $credentialId | Out-Null } catch { $blocked = $_.Exception.Message -like '*expire within*' }
Assert-That $blocked 'A different future credential must not hide expiry of the configured credential.'
Remove-Variable -Name ChessOperationsTestState -Scope Global
Write-Output 'Operations script checks passed (offline Azure stubs; no Azure changes or notifications).'
