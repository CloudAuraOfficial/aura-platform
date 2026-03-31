param(
    [Parameter(Mandatory=$true)][string]$vmName,
    [Parameter(Mandatory=$true)][string]$resourceGroup
)

$ErrorActionPreference = "Stop"

Write-Output "[PowerShell-Az] Authenticating with service principal..."

$securePassword = ConvertTo-SecureString $env:AZURE_CLIENT_SECRET -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential($env:AZURE_CLIENT_ID, $securePassword)
Connect-AzAccount -ServicePrincipal -Credential $credential -Tenant $env:AZURE_TENANT_ID | Out-Null

if ($env:AZURE_SUBSCRIPTION_ID) {
    Set-AzContext -SubscriptionId $env:AZURE_SUBSCRIPTION_ID | Out-Null
}

Write-Output "[PowerShell-Az] Authentication successful."

# Delete the VM (force, no confirmation)
Write-Output "[PowerShell-Az] Deleting VM '$vmName' in resource group '$resourceGroup'..."
Remove-AzVM -Name $vmName -ResourceGroupName $resourceGroup -Force

# Delete associated networking resources
Write-Output "[PowerShell-Az] Deleting NIC '$vmName-nic'..."
Remove-AzNetworkInterface -Name "$vmName-nic" -ResourceGroupName $resourceGroup -Force -ErrorAction SilentlyContinue

Write-Output "[PowerShell-Az] Deleting Public IP '$vmName-ip'..."
Remove-AzPublicIpAddress -Name "$vmName-ip" -ResourceGroupName $resourceGroup -Force -ErrorAction SilentlyContinue

Write-Output "[PowerShell-Az] Deleting NSG '$vmName-nsg'..."
Remove-AzNetworkSecurityGroup -Name "$vmName-nsg" -ResourceGroupName $resourceGroup -Force -ErrorAction SilentlyContinue

Write-Output "[PowerShell-Az] Deleting VNet '$vmName-vnet'..."
Remove-AzVirtualNetwork -Name "$vmName-vnet" -ResourceGroupName $resourceGroup -Force -ErrorAction SilentlyContinue

Write-Output "[PowerShell-Az] VM and networking resources deleted."
