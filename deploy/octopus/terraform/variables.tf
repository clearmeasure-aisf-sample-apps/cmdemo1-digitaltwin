variable "octopus_url" {
  description = "Octopus Server URL."
  type        = string
  default     = "https://clearmeasure.octopus.app"
}

variable "octopus_api_key" {
  description = "API key of the person applying this configuration (supplied by the Octopus owner at apply time; never committed)."
  type        = string
  sensitive   = true
}

variable "space_id" {
  description = "Space that holds the twin's project."
  type        = string
  default     = "Spaces-355"
}

variable "project_name" {
  description = "Octopus project for the twin."
  type        = string
  default     = "cmdemo1-digitaltwin"
}

variable "project_group_name" {
  description = "Existing project group for the twin's project (looked up, not created)."
  type        = string
  default     = "Default Project Group"
}

variable "environment_names" {
  description = "Existing environments the twin's lifecycle promotes through, in order (looked up, never changed)."
  type        = list(string)
  default     = ["tdd", "uat", "prod"]
}

variable "rollout_environment" {
  description = "The environment whose deployment rolls the release out to the twin's single instance; the earlier ones are gates (image, Bicep what-if, live health)."
  type        = string
  default     = "prod"
}

variable "auto_deploy" {
  description = "Promote each release through every phase automatically once the previous one succeeds."
  type        = bool
  default     = true
}

variable "worker_container_feed_name" {
  description = "Docker feed used to pull worker_container_image (looked up by name); empty runs the steps directly on the worker."
  type        = string
  default     = "docker-hub"
}

variable "worker_container_image" {
  description = "Execution container with the Azure CLI, jq, curl and python3 (the same image cmdemo1's projects use)."
  type        = string
  default     = "octopusdeploy/worker-tools:6.6.5-ubuntu.24.04"
}

variable "package_id" {
  description = "Package the GitHub Actions release-candidate job builds (cmdemo1-digitaltwin.<version>.zip)."
  type        = string
  default     = "cmdemo1-digitaltwin"
}

variable "worker_pool_id" {
  description = "Worker pool for the steps; empty looks up worker_pool_name."
  type        = string
  default     = ""
}

variable "worker_pool_name" {
  description = "Linux worker pool looked up when worker_pool_id is empty (the steps are Bash). Falls back to the space's default pool if none matches."
  type        = string
  default     = "Hosted Ubuntu"
}

variable "system_slug" {
  description = "Short name of the observed system; names the twin's Azure resources (ca-<slug>-twin, ...)."
  type        = string
  default     = "cmdemo1"
}

variable "github_org" {
  description = "GitHub organization the twin registers webhooks in."
  type        = string
  default     = "clearmeasure-aisf-sample-apps"
}

variable "platform_file" {
  description = "Platform definition inside the package (for example docs/platforms/contoso.platform.json); empty observes cmdemo1."
  type        = string
  default     = ""
}

variable "resource_group" {
  description = "Azure resource group that hosts the twin."
  type        = string
  default     = "rg-cmdemo1-nonprod"
}

variable "container_app_name" {
  description = "The twin's Container App."
  type        = string
  default     = "ca-cmdemo1-twin"
}

variable "acr_name" {
  description = "Container registry the release-candidate job pushes the twin image to."
  type        = string
  default     = "acrcmdemo1chu4gq"
}

variable "image_repository" {
  description = "Repository of the twin image in the registry."
  type        = string
  default     = "cmdemo1/digitaltwin"
}

variable "enable_event_grid" {
  description = "Let the hook registration step manage the Azure Event Grid subscriptions."
  type        = bool
  default     = true
}

# Sensitive project variables: the same values the GitHub repository secrets hold today.
# Supply them as TF_VAR_* from the owner's secret store; they end up in Terraform state, so keep state encrypted and private.
variable "azure_client_id" {
  type      = string
  sensitive = true
}

variable "azure_client_secret" {
  type      = string
  sensitive = true
}

variable "azure_tenant_id" {
  type      = string
  sensitive = true
}

variable "azure_subscription_id" {
  type      = string
  sensitive = true
}

variable "twin_octopus_api_key" {
  description = "Read-mostly API key the twin itself uses for Octopus (the OCTOPUS_API_KEY repository secret)."
  type        = string
  sensitive   = true
}

variable "github_pat" {
  type      = string
  sensitive = true
}

variable "webhook_secret" {
  type      = string
  sensitive = true
}

variable "twin_api_key" {
  type      = string
  sensitive = true
}
