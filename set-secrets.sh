#!/usr/bin/env bash
# Interactive helper to set dotnet user-secrets for the repro app.
# Usage: ./set-secrets.sh

set -euo pipefail

echo "=== EXO Memory Leak Repro — User Secrets Setup ==="
echo

read -rp "Tenant ID: " tenant_id
read -rp "Client ID (App Registration): " client_id
read -rp "Username (UPN, e.g. admin@contoso.onmicrosoft.com): " username
read -rsp "Password: " password
echo

dotnet user-secrets set "Exo:TenantId" "$tenant_id"
dotnet user-secrets set "Exo:ClientId" "$client_id"
dotnet user-secrets set "Exo:Username" "$username"
dotnet user-secrets set "Exo:Password" "$password"

echo
echo "Secrets saved. Verify with:"
echo "  dotnet user-secrets list"
