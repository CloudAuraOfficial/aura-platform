"""Health Check — Poll the /health endpoint until the container group is responding."""

import json
import os
import sys
import time
import urllib.request
import urllib.error

params = json.loads(os.environ.get("AURA_PARAMETERS", "{}"))

fqdn = params.get("fqdn") or os.environ.get("AURA_PARAM_FQDN")
port = params.get("port", "8000")
max_retries = int(params.get("maxRetries", "12"))
retry_delay = int(params.get("retryDelaySeconds", "10"))

if not fqdn:
    print("ERROR: fqdn parameter required")
    sys.exit(1)

url = f"http://{fqdn}:{port}/health"
print(f"Polling {url}")
print(f"  Max retries: {max_retries}")
print(f"  Retry delay: {retry_delay}s")
print(f"  Timeout: {max_retries * retry_delay}s total")
print()

for attempt in range(1, max_retries + 1):
    try:
        req = urllib.request.Request(url, method="GET")
        with urllib.request.urlopen(req, timeout=5) as resp:
            status = resp.status
            body = resp.read().decode("utf-8", errors="replace")

        if status == 200:
            print(f"  Attempt {attempt}/{max_retries}: HTTP {status}")
            print()
            print(f"Health check passed.")
            print(f"  Response: {body[:500]}")
            sys.exit(0)
        else:
            print(f"  Attempt {attempt}/{max_retries}: HTTP {status} (not healthy yet)")

    except urllib.error.URLError as e:
        print(f"  Attempt {attempt}/{max_retries}: Connection failed — {e.reason}")
    except Exception as e:
        print(f"  Attempt {attempt}/{max_retries}: Error — {e}")

    if attempt < max_retries:
        time.sleep(retry_delay)

print()
print(f"ERROR: Health check failed after {max_retries} attempts ({max_retries * retry_delay}s)")
print(f"  Check container logs:")
print(f"  az container logs --resource-group cloudaura-rg --name aura-api --container-name aura-api")
sys.exit(1)
