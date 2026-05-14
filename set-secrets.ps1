# Interactive helper to set dotnet user-secrets for the repro app.
# Usage: ./set-secrets.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host '=== EXO Memory Leak Repro — User Secrets Setup ==='
Write-Host

$tenantId = Read-Host 'Tenant ID'
$clientId = Read-Host 'Client ID (App Registration)'
$username = Read-Host 'Username (UPN, e.g. admin@contoso.onmicrosoft.com)'
$password = Read-Host 'Password' -MaskInput

dotnet user-secrets set 'Exo:TenantId' $tenantId
dotnet user-secrets set 'Exo:ClientId' $clientId
dotnet user-secrets set 'Exo:Username' $username
dotnet user-secrets set 'Exo:Password' $password

Write-Host
Write-Host 'Secrets saved. Verify with:'
Write-Host '  dotnet user-secrets list'
