"""Smoke Test — Verify critical API endpoints are responding correctly."""

import json
import os
import sys
import urllib.request
import urllib.error

params = json.loads(os.environ.get("AURA_PARAMETERS", "{}"))

fqdn = params.get("fqdn") or os.environ.get("AURA_PARAM_FQDN")
port = params.get("port", "8000")

if not fqdn:
    print("ERROR: fqdn parameter required")
    sys.exit(1)

base_url = f"http://{fqdn}:{port}"
failures = []
passed = 0


def check(name, path, expected_status=200, method="GET"):
    """Run a single endpoint check."""
    global passed
    url = f"{base_url}{path}"
    try:
        req = urllib.request.Request(url, method=method)
        with urllib.request.urlopen(req, timeout=10) as resp:
            status = resp.status
            body = resp.read().decode("utf-8", errors="replace")[:200]

        if status == expected_status:
            print(f"  PASS  {name} — {method} {path} → {status}")
            passed += 1
            return True
        else:
            msg = f"  FAIL  {name} — {method} {path} → {status} (expected {expected_status})"
            print(msg)
            failures.append(msg)
            return False

    except urllib.error.HTTPError as e:
        if e.code == expected_status:
            print(f"  PASS  {name} — {method} {path} → {e.code}")
            passed += 1
            return True
        msg = f"  FAIL  {name} — {method} {path} → HTTP {e.code} (expected {expected_status})"
        print(msg)
        failures.append(msg)
        return False
    except Exception as e:
        msg = f"  FAIL  {name} — {method} {path} → {e}"
        print(msg)
        failures.append(msg)
        return False


print(f"Smoke testing {base_url}")
print()

# Core endpoints
check("Health endpoint", "/health")
check("Metrics endpoint", "/metrics")
check("Auth (no token)", "/api/v1/essences", expected_status=401)
check("Dashboard login page", "/dashboard/login")

print()
total = passed + len(failures)
print(f"Results: {passed}/{total} passed")

if failures:
    print()
    print("Failures:")
    for f in failures:
        print(f"  {f}")
    sys.exit(1)

print()
print("All smoke tests passed.")
