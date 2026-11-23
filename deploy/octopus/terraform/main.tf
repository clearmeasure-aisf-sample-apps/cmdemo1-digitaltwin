# The twin's own Octopus project. It creates only the project, its lifecycle, its variables and its process; environments,
# the project group, the worker pool and the feeds are existing space resources that are looked up and never changed.
# Applied by .github/workflows/octopus-config.yml; see docs/octopus-apply.md.

data "octopusdeploy_environments" "stage" {
  for_each = toset(var.environment_names)
  name     = each.value
  take     = 1
}

data "octopusdeploy_project_groups" "twin" {
  partial_name = var.project_group_name
}

data "octopusdeploy_worker_pools" "linux" {
  partial_name = var.worker_pool_name
  take         = 1
}

data "octopusdeploy_feeds" "built_in" {
  feed_type = "BuiltIn"
  take      = 1
}

data "octopusdeploy_feeds" "container" {
  count        = var.worker_container_feed_name == "" ? 0 : 1
  partial_name = var.worker_container_feed_name
  feed_type    = "Docker"
}

locals {
  environment_ids  = [for name in var.environment_names : data.octopusdeploy_environments.stage[name].environments[0].id]
  rollout_id       = data.octopusdeploy_environments.stage[var.rollout_environment].environments[0].id
  gate_ids         = [for name in var.environment_names : data.octopusdeploy_environments.stage[name].environments[0].id if name != var.rollout_environment]
  project_group_id = one([for g in data.octopusdeploy_project_groups.twin.project_groups : g.id if g.name == var.project_group_name])
  built_in_feed_id = data.octopusdeploy_feeds.built_in.feeds[0].id
  worker_pool_id   = var.worker_pool_id != "" ? var.worker_pool_id : try(data.octopusdeploy_worker_pools.linux.worker_pools[0].id, null)
  container_feed   = var.worker_container_feed_name == "" ? null : one([for f in data.octopusdeploy_feeds.container[0].feeds : f.id if f.name == var.worker_container_feed_name])
}

resource "octopusdeploy_lifecycle" "twin" {
  name        = "${var.project_name} lifecycle"
  description = "The twin's own promotion path: ${join(" -> ", var.environment_names)}. Only the ${var.project_name} project uses it."

  dynamic "phase" {
    for_each = var.environment_names
    content {
      name                                  = phase.value
      automatic_deployment_targets          = var.auto_deploy ? [data.octopusdeploy_environments.stage[phase.value].environments[0].id] : []
      optional_deployment_targets           = var.auto_deploy ? [] : [data.octopusdeploy_environments.stage[phase.value].environments[0].id]
      minimum_environments_before_promotion = 0
      is_optional_phase                     = false
    }
  }
}

resource "octopusdeploy_project" "twin" {
  name                                 = var.project_name
  description                          = "DevOps Digital Twin for cmdemo1, deployed from release candidates built by GitHub Actions (clearmeasure-aisf-sample-apps/cmdemo1-digitaltwin). One instance: ${join(" and ", [for n in var.environment_names : n if n != var.rollout_environment])} are gates, ${var.rollout_environment} rolls it out."
  lifecycle_id                         = octopusdeploy_lifecycle.twin.id
  project_group_id                     = local.project_group_id
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

locals {
  strings = {
    "Twin.ResourceGroup"      = var.resource_group
    "Twin.AppName"            = var.container_app_name
    "Twin.AcrName"            = var.acr_name
    "Twin.Image"              = "${var.acr_name}.azurecr.io/${var.image_repository}:#{Octopus.Release.Number}"
    "Twin.OctopusUrl"         = var.octopus_url
    "Twin.EnableEventGrid"    = var.enable_event_grid ? "true" : "false"
    "Twin.SystemSlug"         = var.system_slug
    "Twin.GitHubOrg"          = var.github_org
    "Twin.OctopusSpace"       = var.space_id
    "Twin.PlatformFile"       = var.platform_file
    "Twin.RolloutEnvironment" = var.rollout_environment
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
