resource "octopusdeploy_process" "twin" {
  project_id = octopusdeploy_project.twin.id
}

locals {
  steps = {
    deploy = {
      name  = "Deploy twin"
      notes = "Waits out a running rebuild, deploys infra/main.json (compiled Bicep) with this release's image, waits for the revision to serve the release number."
      body  = file("${path.module}/steps/deploy.sh")
    }
    hooks = {
      name  = "Register hooks and subscriptions"
      notes = "Idempotent: GitHub repository webhooks (quota-guarded), the Octopus subscription and Azure Event Grid."
      body  = file("${path.module}/steps/register-hooks.sh")
    }
    verify = {
      name  = "Verify twin"
      notes = "Health gate: serving the release, no dead letters, database under budget, MCP answering."
      body  = file("${path.module}/steps/verify.sh")
    }
  }
}

resource "octopusdeploy_process_step" "twin" {
  for_each       = local.steps
  process_id     = octopusdeploy_process.twin.id
  name           = each.value.name
  notes          = each.value.notes
  type           = "Octopus.Script"
  environments   = [octopusdeploy_environment.twin.id]
  condition      = "Success"
  start_trigger  = "StartAfterPrevious"
  worker_pool_id = local.worker_pool_id

  container = var.worker_container_feed_id != "" ? {
    feed_id = var.worker_container_feed_id
    image   = var.worker_container_image
  } : null

  packages = {
    twin = {
      package_id           = var.package_id
      feed_id              = local.built_in_feed_id
      acquisition_location = "ExecutionTarget"
      properties = {
        Extract       = "True"
        Purpose       = ""
        SelectionMode = "immediate"
      }
    }
  }

  execution_properties = {
    "Octopus.Action.RunOnServer"         = "true"
    "Octopus.Action.Script.ScriptSource" = "Inline"
    "Octopus.Action.Script.Syntax"       = "Bash"
    "Octopus.Action.Script.ScriptBody"   = each.value.body
  }
}

resource "octopusdeploy_process_steps_order" "twin" {
  process_id = octopusdeploy_process.twin.id
  steps = [
    octopusdeploy_process_step.twin["deploy"].id,
    octopusdeploy_process_step.twin["hooks"].id,
    octopusdeploy_process_step.twin["verify"].id,
  ]
}
