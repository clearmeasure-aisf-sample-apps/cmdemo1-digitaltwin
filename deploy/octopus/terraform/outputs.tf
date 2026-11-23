output "project_id" {
  value = octopusdeploy_project.twin.id
}

output "project_url" {
  value = "${var.octopus_url}/app#/${var.space_id}/projects/${octopusdeploy_project.twin.slug}"
}

output "lifecycle_id" {
  value = octopusdeploy_lifecycle.twin.id
}
