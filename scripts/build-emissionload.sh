#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

echo "=== Building EmissionLoad Container Images ==="

CUSTOMER="${1:-Aura}"
echo "Customer: $CUSTOMER"

EMISSIONLOAD_DIR="$REPO_ROOT/EmissionLoad/$CUSTOMER"

if [ ! -d "$EMISSIONLOAD_DIR" ]; then
    echo "ERROR: EmissionLoad directory not found: $EMISSIONLOAD_DIR"
    exit 1
fi

for PROVIDER_DIR in "$EMISSIONLOAD_DIR"/*/; do
    PROVIDER=$(basename "$PROVIDER_DIR")
    IMAGE_NAME="aura/emissionload-${PROVIDER}:latest"

    if [ -f "$PROVIDER_DIR/Dockerfile" ]; then
        echo ""
        echo "--- Building $IMAGE_NAME from $PROVIDER_DIR ---"
        docker build -t "$IMAGE_NAME" "$PROVIDER_DIR"
        echo "--- Built $IMAGE_NAME ---"
    else
        echo "SKIP: No Dockerfile in $PROVIDER_DIR"
    fi
done

echo ""
echo "=== EmissionLoad build complete ==="
docker images | grep "aura/emissionload" || true
