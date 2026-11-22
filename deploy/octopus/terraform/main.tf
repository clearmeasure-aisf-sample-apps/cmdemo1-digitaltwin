# The twin's own Octopus project: one environment, one lifecycle phase, three steps that run the same scripts
# GitHub Actions runs today (deploy/scripts). Nothing here is applied automatically; see docs/octopus-apply.md.

resource "octopusdeploy_project_group" "platform" {
  count       = var.project_group_id == "" ? 1 : 0
  name        = var.project_group_name
  description = "Platform-level projects that sit above the observed system."
}

resource "octopusdeploy_environment" "twin" {
  name                         = var.environment_name
  description                  = "The platform digital twin (one instance, observing tdd, uat and prod)."
  allow_dynamic_infrastructure = false
  use_guided_failure           = false
}

resource "octopusdeploy_lifecycle" "twin" {
  name        = "${var.project_name} lifecycle"
  description = "Single phase: the twin environment."

  phase {
    name                                  = var.environment_name
    automatic_deployment_targets          = var.auto_deploy ? [octopusdeploy_environment.twin.id] : []
    optional_deployment_targets           = var.auto_deploy ? [] : [octopusdeploy_environment.twin.id]
    minimum_environments_before_promotion = 0
    is_optional_phase                     = false
  }
}

resource "octopusdeploy_project" "twin" {
  name                                 = var.project_name
  description                          = "DevOps Digital Twin for cmdemo1, deployed from release candidates built by GitHub Actions (clearmeasure-aisf-sample-apps/cmdemo1-digitaltwin)."
  lifecycle_id                         = octopusdeploy_lifecycle.twin.id
  project_group_id                     = var.project_group_id != "" ? var.project_group_id : octopusdeploy_project_group.platform[0].id
  default_guided_failure_mode          = "Off"
  tenanted_deployment_participation    = "Untenanted"
  is_disabled                          = false
  is_discrete_channel_release          = false
  default_to_skip_if_already_installed = false

  connectivity_policy {
    allow_deployments_to_no_targets = true
    skip_machine_behavior           = "None"
  }
}

data "octopusdeploy_worker_pools" "linux" {
  partial_name = var.worker_pool_name
  take         = 1
}

data "octopusdeploy_feeds" "built_in" {
  feed_type = "BuiltIn"
  take      = 1
}

locals {
  built_in_feed_id = data.octopusdeploy_feeds.built_in.feeds[0].id
  worker_pool_id   = var.worker_pool_id != "" ? var.worker_pool_id : try(data.octopusdeploy_worker_pools.linux.worker_pools[0].id, null)

  strings = {
    "Twin.ResourceGroup"   = var.resource_group
    "Twin.AppName"         = var.container_app_name
    "Twin.AcrName"         = var.acr_name
    "Twin.Image"           = "${var.acr_name}.azurecr.io/${var.image_repository}:#{Octopus.Release.Number}"
    "Twin.OctopusUrl"      = var.octopus_url
    "Twin.EnableEventGrid" = var.enable_event_grid ? "true" : "false"
  }

  sensitive_names = ["Azure.ClientId", "Azure.ClientSecret", "Azure.TenantId", "Azure.SubscriptionId", "Twin.OctopusApiKey", "Twin.GitHubPat", "Twin.WebhookSecret", "Twin.ApiKey"]
  sensitive = {
    "Azure.ClientId"       = var.azure_client_id
    "Azure.ClientSecret"   = var.azure_client_secret
    "Azure.TenantId"       = var.azure_tenant_id
    "Azure.SubscriptionId" = var.azure_subscription_id
    "Twin.OctopusApiKey"   = var.twin_octopus_api_key
    "Twin.GitHubPat"       = var.github_pat
    "Twin.WebhookSecret"   = var.webhook_secret
    "Twin.ApiKey"          = var.twin_api_key
  }
}

resource "octopusdeploy_variable" "string" {
  for_each = local.strings
  owner_id = octopusdeploy_project.twin.id
  name     = each.key
  type     = "String"
  value    = each.value
}

resource "octopusdeploy_variable" "sensitive" {
  for_each        = toset(local.sensitive_names)
  owner_id        = octopusdeploy_project.twin.id
  name            = each.key
  type            = "Sensitive"
  is_sensitive    = true
  sensitive_value = local.sensitive[each.key]
}
