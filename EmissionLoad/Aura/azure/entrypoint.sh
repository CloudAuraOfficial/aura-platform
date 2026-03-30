#!/bin/bash
set -euo pipefail

echo "[EmissionLoad-Azure] Starting execution..."

# Authenticate using injected BYOS service principal credentials
if [ -n "${AZURE_CLIENT_ID:-}" ] && [ -n "${AZURE_CLIENT_SECRET:-}" ] && [ -n "${AZURE_TENANT_ID:-}" ]; then
    echo "[EmissionLoad-Azure] Authenticating with service principal..."
    az login --service-principal \
        -u "$AZURE_CLIENT_ID" \
        -p "$AZURE_CLIENT_SECRET" \
        --tenant "$AZURE_TENANT_ID" \
        --output none

    if [ -n "${AZURE_SUBSCRIPTION_ID:-}" ]; then
        az account set --subscription "$AZURE_SUBSCRIPTION_ID"
    fi
    echo "[EmissionLoad-Azure] Authentication successful."
else
    echo "[EmissionLoad-Azure] WARNING: No BYOS credentials provided, using default credentials."
fi

# Parse parameters from AURA_PARAMETERS JSON
param() {
    echo "$AURA_PARAMETERS" | jq -r ".$1 // empty"
}

param_default() {
    local val
    val=$(echo "$AURA_PARAMETERS" | jq -r ".$1 // empty")
    echo "${val:-$2}"
}

OPERATION="${AURA_OPERATION_TYPE:-}"
echo "[EmissionLoad-Azure] Operation: $OPERATION"
echo "[EmissionLoad-Azure] Parameters: $AURA_PARAMETERS"

case "$OPERATION" in

    CreateResourceGroup)
        RG_NAME=$(param "resourceGroupName")
        LOCATION=$(param "location")
        echo "[EmissionLoad-Azure] Creating resource group '$RG_NAME' in '$LOCATION'..."
        az group create --name "$RG_NAME" --location "$LOCATION" --output json
        echo "[EmissionLoad-Azure] Resource group created."
        ;;

    CreateVM)
        VM_NAME=$(param "vmName")
        RG=$(param "resourceGroup")
        LOCATION=$(param_default "location" "eastus")
        VM_SIZE=$(param_default "vmSize" "Standard_B1s")
        ADMIN_USER=$(param_default "adminUsername" "azureuser")
        ADMIN_PASS=$(param "adminPassword")
        OS_DISK_SIZE=$(param_default "osDiskSizeGB" "30")
        PUBLISHER=$(echo "$AURA_PARAMETERS" | jq -r '.osImage.publisher // "Canonical"')
        OFFER=$(echo "$AURA_PARAMETERS" | jq -r '.osImage.offer // "ubuntu-24_04-lts"')
        SKU=$(echo "$AURA_PARAMETERS" | jq -r '.osImage.sku // "server"')
        VERSION=$(echo "$AURA_PARAMETERS" | jq -r '.osImage.version // "latest"')
        IMAGE="$PUBLISHER:$OFFER:$SKU:$VERSION"

        # Collect open ports
        PORTS=$(echo "$AURA_PARAMETERS" | jq -r '.openPorts // [22] | join(" ")')

        echo "[EmissionLoad-Azure] Creating VM '$VM_NAME' in '$RG' ($VM_SIZE, $IMAGE)..."
        az vm create \
            --resource-group "$RG" \
            --name "$VM_NAME" \
            --location "$LOCATION" \
            --image "$IMAGE" \
            --size "$VM_SIZE" \
            --admin-username "$ADMIN_USER" \
            --admin-password "$ADMIN_PASS" \
            --os-disk-size-gb "$OS_DISK_SIZE" \
            --nsg "${VM_NAME}-nsg" \
            --vnet-name "${VM_NAME}-vnet" \
            --subnet "default" \
            --public-ip-address "${VM_NAME}-ip" \
            --public-ip-sku Standard \
            --output json

        # Open additional ports via NSG rules
        PRIORITY=100
        for PORT in $PORTS; do
            echo "[EmissionLoad-Azure] Opening port $PORT..."
            az network nsg rule create \
                --resource-group "$RG" \
                --nsg-name "${VM_NAME}-nsg" \
                --name "Allow-${PORT}" \
                --priority "$PRIORITY" \
                --direction Inbound \
                --access Allow \
                --protocol Tcp \
                --source-address-prefixes '*' \
                --destination-port-ranges "$PORT" \
                --output none 2>/dev/null || echo "[EmissionLoad-Azure] NSG rule for port $PORT may already exist."
            PRIORITY=$((PRIORITY + 10))
        done
        echo "[EmissionLoad-Azure] VM created."
        ;;

    StartVM)
        VM_NAME=$(param "vmName")
        RG=$(param "resourceGroup")
        echo "[EmissionLoad-Azure] Starting VM '$VM_NAME'..."
        az vm start --resource-group "$RG" --name "$VM_NAME" --output none
        echo "[EmissionLoad-Azure] VM started."
        ;;

    StopVM)
        VM_NAME=$(param "vmName")
        RG=$(param "resourceGroup")
        echo "[EmissionLoad-Azure] Deallocating VM '$VM_NAME'..."
        az vm deallocate --resource-group "$RG" --name "$VM_NAME" --output none
        echo "[EmissionLoad-Azure] VM deallocated."
        ;;

    DeleteVM)
        VM_NAME=$(param "vmName")
        RG=$(param "resourceGroup")
        DELETE_NET=$(param_default "deleteNetworking" "true")
        echo "[EmissionLoad-Azure] Deleting VM '$VM_NAME'..."
        az vm delete --resource-group "$RG" --name "$VM_NAME" --yes --output none

        if [ "$DELETE_NET" = "true" ]; then
            echo "[EmissionLoad-Azure] Cleaning up networking resources..."
            az network nic delete --resource-group "$RG" --name "${VM_NAME}-nic" --output none 2>/dev/null || true
            az network public-ip delete --resource-group "$RG" --name "${VM_NAME}-ip" --output none 2>/dev/null || true
            az network nsg delete --resource-group "$RG" --name "${VM_NAME}-nsg" --output none 2>/dev/null || true
            az network vnet delete --resource-group "$RG" --name "${VM_NAME}-vnet" --output none 2>/dev/null || true
            echo "[EmissionLoad-Azure] Networking resources cleaned up."
        fi
        echo "[EmissionLoad-Azure] VM deleted."
        ;;

    CreateContainerRegistry)
        REG_NAME=$(param "registryName")
        RG=$(param "resourceGroup")
        REG_SKU=$(param_default "sku" "Basic")
        ADMIN=$(param_default "adminEnabled" "true")
        echo "[EmissionLoad-Azure] Creating container registry '$REG_NAME'..."
        az acr create \
            --resource-group "$RG" \
            --name "$REG_NAME" \
            --sku "$REG_SKU" \
            --admin-enabled "$ADMIN" \
            --output json
        echo "[EmissionLoad-Azure] Container registry created."
        ;;

    BuildContainerImage)
        IMAGE_NAME=$(param "imageName")
        IMAGE_TAG=$(param "imageTag")
        REG_NAME=$(param "registryName")
        SOURCE_URL=$(param "sourceUrl")
        DOCKERFILE=$(param_default "dockerfilePath" "Dockerfile")
        BUILD_TARGET=$(param "buildTarget")
        TIMEOUT=$(param_default "timeoutSeconds" "600")

        echo "[EmissionLoad-Azure] Building image '$IMAGE_NAME:$IMAGE_TAG' in registry '$REG_NAME'..."
        BUILD_CMD="az acr build --registry $REG_NAME --image ${IMAGE_NAME}:${IMAGE_TAG} --file $DOCKERFILE --timeout $TIMEOUT"

        if [ -n "$BUILD_TARGET" ]; then
            BUILD_CMD="$BUILD_CMD --target $BUILD_TARGET"
        fi

        BUILD_CMD="$BUILD_CMD $SOURCE_URL"
        eval "$BUILD_CMD"
        echo "[EmissionLoad-Azure] Image built."
        ;;

    PushContainerImage)
        IMAGE_NAME=$(param "imageName")
        IMAGE_TAG=$(param "imageTag")
        REG_NAME=$(param "registryName")
        echo "[EmissionLoad-Azure] Verifying image '$IMAGE_NAME:$IMAGE_TAG' exists in '$REG_NAME'..."
        az acr repository show --name "$REG_NAME" --repository "$IMAGE_NAME" --output json
        echo "[EmissionLoad-Azure] Image verified."
        ;;

    ImportContainerImage)
        SOURCE_IMAGE=$(param "sourceImage")
        TARGET_IMAGE=$(param "targetImage")
        REG_NAME=$(param "registryName")
        echo "[EmissionLoad-Azure] Importing '$SOURCE_IMAGE' as '$TARGET_IMAGE' into '$REG_NAME'..."
        az acr import \
            --name "$REG_NAME" \
            --source "$SOURCE_IMAGE" \
            --image "$TARGET_IMAGE" \
            --force \
            --output none
        echo "[EmissionLoad-Azure] Image imported."
        ;;

    CreateContainerGroup)
        CG_NAME=$(param "containerGroupName")
        RG=$(param "resourceGroup")
        LOCATION=$(param "location")
        OS_TYPE=$(param_default "osType" "Linux")
        DNS_LABEL=$(param "dnsLabel")
        IP_TYPE=$(param_default "ipType" "Public")
        REG_NAME=$(param "registryName")

        # For complex multi-container groups, use a YAML deployment
        CONTAINER_COUNT=$(echo "$AURA_PARAMETERS" | jq '.containers | length')

        if [ "$CONTAINER_COUNT" -eq 1 ]; then
            # Single container — use az container create directly
            IMAGE=$(echo "$AURA_PARAMETERS" | jq -r '.containers[0].image')
            CPU=$(echo "$AURA_PARAMETERS" | jq -r '.containers[0].cpu // 1')
            MEMORY=$(echo "$AURA_PARAMETERS" | jq -r '.containers[0].memoryInGB // 1.5')
            PORTS=$(echo "$AURA_PARAMETERS" | jq -r '.containers[0].ports // [] | join(" ")')

            CMD="az container create --resource-group $RG --name $CG_NAME --image $IMAGE --cpu $CPU --memory $MEMORY --os-type $OS_TYPE --ip-address $IP_TYPE --location $LOCATION"

            if [ -n "$PORTS" ]; then
                CMD="$CMD --ports $PORTS"
            fi
            if [ -n "$DNS_LABEL" ]; then
                CMD="$CMD --dns-name-label $DNS_LABEL"
            fi
            if [ -n "$REG_NAME" ]; then
                REG_USER=$(az acr credential show --name "$REG_NAME" --query "username" -o tsv)
                REG_PASS=$(az acr credential show --name "$REG_NAME" --query "passwords[0].value" -o tsv)
                CMD="$CMD --registry-login-server ${REG_NAME}.azurecr.io --registry-username $REG_USER --registry-password $REG_PASS"
            fi

            echo "[EmissionLoad-Azure] Creating container group '$CG_NAME' ($IMAGE)..."
            eval "$CMD --output json"
        else
            # Multi-container — generate YAML and deploy
            YAML_FILE="/tmp/aci-deploy-${CG_NAME}.yaml"
            echo "[EmissionLoad-Azure] Generating YAML for $CONTAINER_COUNT containers..."

            # Build YAML
            cat > "$YAML_FILE" << YAMLEOF
apiVersion: '2021-10-01'
location: $LOCATION
name: $CG_NAME
properties:
  osType: $OS_TYPE
  ipAddress:
    type: $IP_TYPE
    ports:
$(echo "$AURA_PARAMETERS" | jq -r '.containers[].ports // [] | .[] | "    - protocol: TCP\n      port: \(.)"')
$(if [ -n "$DNS_LABEL" ]; then echo "    dnsNameLabel: $DNS_LABEL"; fi)
$(if [ -n "$REG_NAME" ]; then
REG_USER=$(az acr credential show --name "$REG_NAME" --query "username" -o tsv)
REG_PASS=$(az acr credential show --name "$REG_NAME" --query "passwords[0].value" -o tsv)
cat << REGEOF
  imageRegistryCredentials:
  - server: ${REG_NAME}.azurecr.io
    username: $REG_USER
    password: $REG_PASS
REGEOF
fi)
  containers:
$(echo "$AURA_PARAMETERS" | jq -r '.containers[] | "  - name: \(.name)\n    properties:\n      image: \(.image)\n      resources:\n        requests:\n          cpu: \(.cpu // 1)\n          memoryInGb: \(.memoryInGB // 1.5)" + (if .ports then "\n      ports:" + (.ports | map("\n      - port: \(.)") | join("")) else "" end)')
YAMLEOF

            echo "[EmissionLoad-Azure] Deploying container group from YAML..."
            az container create --resource-group "$RG" --file "$YAML_FILE" --output json
            rm -f "$YAML_FILE"
        fi
        echo "[EmissionLoad-Azure] Container group created."
        ;;

    StopContainerGroup)
        CG_NAME=$(param "containerGroupName")
        RG=$(param "resourceGroup")
        echo "[EmissionLoad-Azure] Stopping container group '$CG_NAME'..."
        az container stop --resource-group "$RG" --name "$CG_NAME" --output none
        echo "[EmissionLoad-Azure] Container group stopped."
        ;;

    DeleteContainerGroup)
        CG_NAME=$(param "containerGroupName")
        RG=$(param "resourceGroup")
        echo "[EmissionLoad-Azure] Deleting container group '$CG_NAME'..."
        az container delete --resource-group "$RG" --name "$CG_NAME" --yes --output none
        echo "[EmissionLoad-Azure] Container group deleted."
        ;;

    DeleteResourceGroup)
        RG_NAME=$(param "resourceGroupName")
        echo "[EmissionLoad-Azure] Deleting resource group '$RG_NAME' (waiting for completion)..."
        az group delete --name "$RG_NAME" --yes --output none
        echo "[EmissionLoad-Azure] Resource group deleted."
        ;;

    HttpHealthCheck)
        ENDPOINT=$(param "endpoint")
        EXPECTED=$(param_default "expectedStatus" "200")
        MAX_RETRIES=$(param_default "maxRetries" "10")
        RETRY_DELAY=$(param_default "retryDelaySeconds" "10")

        echo "[EmissionLoad-Azure] Health check: $ENDPOINT (expected $EXPECTED, max $MAX_RETRIES retries)..."
        for i in $(seq 1 "$MAX_RETRIES"); do
            STATUS=$(curl -s -o /dev/null -w "%{http_code}" "$ENDPOINT" 2>/dev/null || echo "000")
            echo "[EmissionLoad-Azure] Attempt $i/$MAX_RETRIES: HTTP $STATUS"
            if [ "$STATUS" = "$EXPECTED" ]; then
                echo "[EmissionLoad-Azure] Health check passed."
                exit 0
            fi
            if [ "$i" -lt "$MAX_RETRIES" ]; then
                sleep "$RETRY_DELAY"
            fi
        done
        echo "[EmissionLoad-Azure] Health check FAILED after $MAX_RETRIES attempts."
        exit 1
        ;;

    *)
        echo "[EmissionLoad-Azure] ERROR: Unknown operation type: $OPERATION"
        exit 1
        ;;
esac

echo "[EmissionLoad-Azure] Operation completed successfully."
