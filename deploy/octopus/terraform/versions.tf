terraform {
  required_version = ">= 1.6"
  required_providers {
    octopusdeploy = {
      source  = "OctopusDeploy/octopusdeploy"
      version = "~> 1.22"
    }
  }

  # State lives in a private container of the twin's own storage account; the octopus-config workflow passes
  # resource_group_name, storage_account_name, container_name and key with -backend-config. State holds the sensitive
  # project variables, so the container is private and only the deploying principal can read it.
  backend "azurerm" {}
}

provider "octopusdeploy" {
  address  = var.octopus_url
  api_key  = var.octopus_api_key
  space_id = var.space_id
}
