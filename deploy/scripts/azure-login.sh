#!/usr/bin/env bash
# Service-principal login shared by the GitHub Actions deploy and the Octopus steps.
set -euo pipefail
: "${ARM_CLIENT_ID:?ARM_CLIENT_ID is required}"
: "${ARM_CLIENT_SECRET:?ARM_CLIENT_SECRET is required}"
: "${ARM_TENANT_ID:?ARM_TENANT_ID is required}"
: "${ARM_SUBSCRIPTION_ID:?ARM_SUBSCRIPTION_ID is required}"
az login --service-principal -u "$ARM_CLIENT_ID" -p "$ARM_CLIENT_SECRET" --tenant "$ARM_TENANT_ID" --output none
az account set --subscription "$ARM_SUBSCRIPTION_ID"
