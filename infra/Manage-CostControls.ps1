[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Plan', 'CaptureBaseline', 'ApplyBudget')][string]$Action = 'Plan',
    [string]$SubscriptionId,
    [string]$ResourceGroup = 'rg-chess-trainer-cloud',
    [string]$VisionResourceId,
    [string]$OutputPath,
    [ValidateSet('Hosting', 'Vision')][string]$BudgetScope = 'Hosting',
    [string]$BudgetName,
    [Nullable[decimal]]$MonthlyAmount,
    [string]$NotificationEmail,
    [switch]$ConfirmExistingCoverageReviewed
)

. "$PSScriptRoot\Operations.Common.ps1"

if ($Action -eq 'Plan') {
    [ordered]@{
        Mode = 'PlanOnly'
        CaptureBaseline = 'Privately capture current-month actual costs and subscription/hosting/vision-group budget coverage'
        ApplyBudget = 'Explicit owner amount in billing currency and recipient; 80% actual, 100% actual/forecast notifications'
        Coverage = 'Hosting resource group and the exact vision account; shared vision allocation must be reviewed'
        Limits = 'No resource creation by default; no guaranteed serverless savings or hard spending cap'
    } | ConvertTo-Json
    return
}
Assert-OperationsSubscription $SubscriptionId
if ($VisionResourceId -notmatch "^(/subscriptions/$([regex]::Escape($SubscriptionId))/resourceGroups/[^/]+)/providers/Microsoft\.CognitiveServices/accounts/[^/]+$") {
    throw 'Supply the exact VisionResourceId in the intended subscription.'
}
$visionGroupScope = $Matches[1]
$subscriptionScope = "/subscriptions/$SubscriptionId"
$hostingScope = "$subscriptionScope/resourceGroups/$ResourceGroup"
$version = '2024-08-01'
$coverage = [ordered]@{}
foreach ($entry in @(@{ Name = 'Subscription'; Scope = $subscriptionScope },
    @{ Name = 'Hosting'; Scope = $hostingScope }, @{ Name = 'VisionGroup'; Scope = $visionGroupScope })) {
    $coverage[$entry.Name] = Invoke-OperationsRest GET "$($entry.Scope)/providers/Microsoft.Consumption/budgets?api-version=$version"
    if ($coverage[$entry.Name].PSObject.Properties.Name -contains 'nextLink' -and $coverage[$entry.Name].nextLink) {
        throw 'Budget coverage is paginated. Review all pages privately in Azure before applying a new budget.'
    }
}
if ($Action -eq 'CaptureBaseline') {
    if (-not $OutputPath) { throw 'Supply a private OutputPath under the ignored artifacts\cloud directory.' }
    $allowed = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\cloud')) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($OutputPath)
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Baseline contains billing and recipient information; OutputPath must be under ignored artifacts\cloud.'
    }
    $query = @{
        type = 'ActualCost'; timeframe = 'MonthToDate'
        dataset = @{
            granularity = 'None'
            aggregation = @{ totalCost = @{ name = 'PreTaxCost'; function = 'Sum' } }
            grouping = @(@{ type = 'Dimension'; name = 'ResourceId' }, @{ type = 'Dimension'; name = 'ServiceName' })
            filter = @{ or = @(
                @{ dimensions = @{ name = 'ResourceGroupName'; operator = 'In'; values = @($ResourceGroup) } },
                @{ dimensions = @{ name = 'ResourceId'; operator = 'In'; values = @($VisionResourceId) } }
            ) }
        }
    }
    $cost = Invoke-OperationsRest POST "$subscriptionScope/providers/Microsoft.CostManagement/query?api-version=2023-11-01" $query
    if ($cost.properties.PSObject.Properties.Name -contains 'nextLink' -and $cost.properties.nextLink) {
        throw 'Cost results are paginated; capture remaining pages privately before claiming a complete baseline.'
    }
    $snapshot = @{
        capturedUtc = [DateTimeOffset]::UtcNow
        period = 'MonthToDate'; costs = $cost; existingBudgetCoverage = $coverage
        assumptions = @{
            serverlessTarget = 'GP_S_Gen5_1; 0.5-1 vCore; 32 GiB; auto-pause 60 minutes (target, not proof of live configuration)'
            attribution = 'Entire selected vision account; review shared usage before assigning it all to this application'
            limitation = 'Partial billing period; not a monthly forecast. Charges can arrive late. Validate invoice currency and credits.'
        }
    }
    if ($PSCmdlet.ShouldProcess($resolved, 'Write private billing baseline')) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolved)) | Out-Null
        [IO.File]::WriteAllText($resolved, ($snapshot | ConvertTo-Json -Depth 40))
        Write-Output 'Private baseline saved; amounts and recipient information are not printed.'
    }
    return
}

Assert-OperationsEmail $NotificationEmail
if (-not $ConfirmExistingCoverageReviewed) { throw 'Review the private coverage inventory first, then explicitly confirm existing coverage was reviewed.' }
if ($null -eq $MonthlyAmount -or $MonthlyAmount -le 0) { throw 'Supply an owner-approved positive MonthlyAmount in the subscription billing currency.' }
if ($BudgetName -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,62}$') { throw 'Supply a distinct BudgetName using letters, numbers, hyphens or underscores.' }
$scope = if ($BudgetScope -eq 'Hosting') { $hostingScope } else { $subscriptionScope }
$existing = if ($BudgetScope -eq 'Hosting') { $coverage.Hosting } else { $coverage.Subscription }
if (@($existing.value | Where-Object { $null -ne $_ -and $_.name -eq $BudgetName }).Count) {
    throw 'This budget name already exists. Review/reuse it rather than overwriting its recipients or thresholds.'
}
$start = [DateTime]::new([DateTime]::UtcNow.Year, [DateTime]::UtcNow.Month, 1, 0, 0, 0, [DateTimeKind]::Utc)
$properties = @{
    category = 'Cost'; amount = [decimal]$MonthlyAmount; timeGrain = 'Monthly'
    timePeriod = @{ startDate = $start.ToString('yyyy-MM-ddTHH:mm:ssZ'); endDate = $start.AddYears(1).ToString('yyyy-MM-ddTHH:mm:ssZ') }
    notifications = @{
        Actual80 = @{ enabled = $true; operator = 'GreaterThanOrEqualTo'; threshold = 80; thresholdType = 'Actual'; contactEmails = @($NotificationEmail) }
        Actual100 = @{ enabled = $true; operator = 'GreaterThanOrEqualTo'; threshold = 100; thresholdType = 'Actual'; contactEmails = @($NotificationEmail) }
        Forecast100 = @{ enabled = $true; operator = 'GreaterThanOrEqualTo'; threshold = 100; thresholdType = 'Forecasted'; contactEmails = @($NotificationEmail) }
    }
}
if ($BudgetScope -eq 'Vision') {
    $properties.filter = @{ dimensions = @{ name = 'ResourceId'; operator = 'In'; values = @($VisionResourceId) } }
}
if ($PSCmdlet.ShouldProcess($BudgetScope, 'Create owner-approved monthly budget notifications (not a hard spending cap)')) {
    Invoke-OperationsRest PUT "$scope/providers/Microsoft.Consumption/budgets/${BudgetName}?api-version=$version" @{ properties = $properties } | Out-Null
    Write-Output 'Budget created. Verify recipient delivery and coverage privately; budget alerts are delayed and do not stop spending.'
}
