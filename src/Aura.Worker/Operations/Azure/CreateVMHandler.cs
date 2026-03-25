using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class CreateVMHandler : IOperationHandler
{
    private readonly ILogger<CreateVMHandler> _logger;

    public CreateVMHandler(ILogger<CreateVMHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("vmName", out var vmNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: vmName");

        if (!parameters.TryGetProperty("resourceGroup", out var rgProp))
            return new LayerExecutionResult(false, "Missing required parameter: resourceGroup");

        if (!parameters.TryGetProperty("adminPassword", out var adminPasswordProp))
            return new LayerExecutionResult(false, "Missing required parameter: adminPassword");

        var vmName = vmNameProp.GetString()!;
        var resourceGroup = rgProp.GetString()!;
        var adminPassword = adminPasswordProp.GetString()!;

        var location = "eastus";
        if (parameters.TryGetProperty("location", out var locationProp))
            location = locationProp.GetString() ?? location;

        var vmSize = "Standard_B1s";
        if (parameters.TryGetProperty("vmSize", out var vmSizeProp))
            vmSize = vmSizeProp.GetString() ?? vmSize;

        var adminUsername = "azureuser";
        if (parameters.TryGetProperty("adminUsername", out var adminUserProp))
            adminUsername = adminUserProp.GetString() ?? adminUsername;

        var osDiskSizeGB = 30;
        if (parameters.TryGetProperty("osDiskSizeGB", out var diskSizeProp))
            osDiskSizeGB = diskSizeProp.GetInt32();

        // OS image defaults (Ubuntu 24.04 LTS)
        var publisher = "Canonical";
        var offer = "ubuntu-24_04-lts";
        var sku = "server";
        var imageVersion = "latest";
        if (parameters.TryGetProperty("osImage", out var osImageProp))
        {
            if (osImageProp.TryGetProperty("publisher", out var pub))
                publisher = pub.GetString() ?? publisher;
            if (osImageProp.TryGetProperty("offer", out var off))
                offer = off.GetString() ?? offer;
            if (osImageProp.TryGetProperty("sku", out var sk))
                sku = sk.GetString() ?? sku;
            if (osImageProp.TryGetProperty("version", out var ver))
                imageVersion = ver.GetString() ?? imageVersion;
        }

        // Open ports (default: [22])
        var openPorts = new List<int> { 22 };
        if (parameters.TryGetProperty("openPorts", out var portsProp) &&
            portsProp.ValueKind == JsonValueKind.Array)
        {
            openPorts.Clear();
            foreach (var portEl in portsProp.EnumerateArray())
                openPorts.Add(portEl.GetInt32());
        }

        var azureLocation = new global::Azure.Core.AzureLocation(location);

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgResource = (await subscription.GetResourceGroupAsync(resourceGroup, ct)).Value;

            // 1. Virtual Network
            _logger.LogInformation("Creating VNet {VNet} for VM {VM}", $"{vmName}-vnet", vmName);
            var vnetData = new VirtualNetworkData
            {
                Location = azureLocation,
            };
            vnetData.AddressPrefixes.Add("10.0.0.0/16");
            vnetData.Subnets.Add(new SubnetData { Name = "default", AddressPrefix = "10.0.0.0/24" });

            var vnetOp = await rgResource.GetVirtualNetworks()
                .CreateOrUpdateAsync(global::Azure.WaitUntil.Completed, $"{vmName}-vnet", vnetData, ct);
            var vnet = vnetOp.Value;
            var subnet = (await vnet.GetSubnetAsync("default", cancellationToken: ct)).Value;

            // 2. Network Security Group
            _logger.LogInformation("Creating NSG {NSG} for VM {VM}", $"{vmName}-nsg", vmName);
            var nsgData = new NetworkSecurityGroupData { Location = azureLocation };
            var priority = 100;
            foreach (var port in openPorts)
            {
                nsgData.SecurityRules.Add(new SecurityRuleData
                {
                    Name = $"Allow-{port}",
                    Priority = priority,
                    Direction = SecurityRuleDirection.Inbound,
                    Access = SecurityRuleAccess.Allow,
                    Protocol = SecurityRuleProtocol.Tcp,
                    SourceAddressPrefix = "*",
                    SourcePortRange = "*",
                    DestinationAddressPrefix = "*",
                    DestinationPortRange = port.ToString(),
                });
                priority += 10;
            }

            var nsgOp = await rgResource.GetNetworkSecurityGroups()
                .CreateOrUpdateAsync(global::Azure.WaitUntil.Completed, $"{vmName}-nsg", nsgData, ct);
            var nsg = nsgOp.Value;

            // 3. Public IP Address
            _logger.LogInformation("Creating Public IP {IP} for VM {VM}", $"{vmName}-ip", vmName);
            var ipData = new PublicIPAddressData
            {
                Location = azureLocation,
                PublicIPAllocationMethod = NetworkIPAllocationMethod.Static,
                Sku = new PublicIPAddressSku { Name = PublicIPAddressSkuName.Standard },
            };

            var ipOp = await rgResource.GetPublicIPAddresses()
                .CreateOrUpdateAsync(global::Azure.WaitUntil.Completed, $"{vmName}-ip", ipData, ct);
            var publicIp = ipOp.Value;

            // 4. Network Interface
            _logger.LogInformation("Creating NIC {NIC} for VM {VM}", $"{vmName}-nic", vmName);
            var nicData = new NetworkInterfaceData
            {
                Location = azureLocation,
                NetworkSecurityGroup = new NetworkSecurityGroupData { Id = nsg.Id },
            };
            nicData.IPConfigurations.Add(new NetworkInterfaceIPConfigurationData
            {
                Name = "ipconfig1",
                Primary = true,
                Subnet = new SubnetData { Id = subnet.Id },
                PublicIPAddress = new PublicIPAddressData { Id = publicIp.Id },
                PrivateIPAllocationMethod = NetworkIPAllocationMethod.Dynamic,
            });

            var nicOp = await rgResource.GetNetworkInterfaces()
                .CreateOrUpdateAsync(global::Azure.WaitUntil.Completed, $"{vmName}-nic", nicData, ct);
            var nic = nicOp.Value;

            // 5. Virtual Machine
            _logger.LogInformation("Creating VM {VM} in {ResourceGroup}", vmName, resourceGroup);
            var vmData = new VirtualMachineData(azureLocation)
            {
                HardwareProfile = new VirtualMachineHardwareProfile { VmSize = vmSize },
                OSProfile = new VirtualMachineOSProfile
                {
                    ComputerName = vmName,
                    AdminUsername = adminUsername,
                    AdminPassword = adminPassword,
                },
                StorageProfile = new VirtualMachineStorageProfile
                {
                    ImageReference = new ImageReference
                    {
                        Publisher = publisher,
                        Offer = offer,
                        Sku = sku,
                        Version = imageVersion,
                    },
                    OSDisk = new VirtualMachineOSDisk(DiskCreateOptionType.FromImage)
                    {
                        Name = $"{vmName}-osdisk",
                        DiskSizeGB = osDiskSizeGB,
                        ManagedDisk = new VirtualMachineManagedDisk
                        {
                            StorageAccountType = StorageAccountType.StandardLrs,
                        },
                    },
                },
            };
            vmData.NetworkProfile = new VirtualMachineNetworkProfile();
            vmData.NetworkProfile.NetworkInterfaces.Add(
                new VirtualMachineNetworkInterfaceReference { Id = nic.Id, Primary = true });

            var vmOp = await rgResource.GetVirtualMachines()
                .CreateOrUpdateAsync(global::Azure.WaitUntil.Completed, vmName, vmData, ct);
            var vm = vmOp.Value;

            // Retrieve the assigned public IP
            var refreshedIp = (await rgResource.GetPublicIPAddressAsync($"{vmName}-ip", cancellationToken: ct)).Value;
            var ipAddress = refreshedIp.Data.IPAddress ?? "pending";

            var sshString = $"ssh {adminUsername}@{ipAddress}";

            return new LayerExecutionResult(true,
                $"VM '{vm.Data.Name}' created. Public IP: {ipAddress}. Connect: {sshString}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create VM {VM}", vmName);
            return new LayerExecutionResult(false, $"Failed to create VM: {ex.Message}");
        }
    }
}
