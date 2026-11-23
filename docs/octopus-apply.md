# The Twin's Own Octopus Project

The twin's release pipeline is defined in this repository and applied to Octopus space `Spaces-355` (clearmeasure.octopus.app) by [`.github/workflows/octopus-config.yml`](../.github/workflows/octopus-config.yml). The workflow plans on pull requests and applies on `main`. It uses the existing `OCTOPUS_URL` and `OCTOPUS_API_KEY` repository secrets (service account `ai-ops`, a Space Manager in `Spaces-355`); no API key, account or team was created for it.

## What the Terraform Creates, and What It Only Reads

Created and owned by the twin (`deploy/octopus/terraform`):
- **Project** `cmdemo1-digitaltwin`, in the existing Default Project Group, untenanted, allowing deployments with no targets.
- **Lifecycle** `cmdemo1-digitaltwin lifecycle`, with phases `tdd`, `uat` and `prod`, each automatic. Only this project uses it.
- **Project variables**:
  - 11 string variables (resource group, app, registry, image `<acr>.azurecr.io/cmdemo1/digitaltwin:#{Octopus.Release.Number}`, Octopus URL and space, Event Grid switch, slug, GitHub org, platform file, rollout environment).
  - 8 sensitive variables, holding the same values as the repository secrets: `Azure.*`, `Twin.OctopusApiKey`, `Twin.GitHubPat`, `Twin.WebhookSecret`, `Twin.ApiKey`.
- **Deployment process**: four Bash steps in the order below. Each runs on the `Hosted Ubuntu` pool in `octopusdeploy/worker-tools:6.6.5-ubuntu.24.04` from the `docker-hub` feed, the same setup cmdemo1's projects use, with the package `cmdemo1-digitaltwin` from the built-in feed.

| Step | Environments | What it does |
| --- | --- | --- |
| Preflight release candidate | tdd, uat | Gate. The image `cmdemo1/digitaltwin:<release>` must be in ACR. A Bicep what-if of the release (`infra/main.json` with that image) must delete nothing. Outputs the live URL |
| Deploy twin | prod | `deploy/scripts/deploy-twin.sh`: waits out a running rebuild, deploys, and waits until the revision serves the release number |
| Register hooks and subscriptions | prod | `scripts/register-hooks.sh`, idempotent |
| Verify twin | tdd, uat, prod | `deploy/scripts/verify-twin.sh`: Healthy, 0 dead letters, database under 1,500 MB, MCP answering. In prod it also checks that the twin serves this release |

The twin has one instance, so tdd and uat are gates in front of the single rollout in prod.

Read only, never changed: the `tdd`, `uat` and `prod` environments, the Default Project Group, the `Hosted Ubuntu` worker pool, and the `docker-hub` and built-in feeds. No other project, lifecycle, team or user is touched.

State is kept in the private `tfstate` container of the twin's storage account in `rg-cmdemo1-nonprod`. It contains the sensitive project variables; only the deploying principal can read it.

## How a Release Flows

1. A push to `main` builds the release candidate: the image `1.0.<run>` in ACR, plus the package `cmdemo1-digitaltwin.1.0.<run>.zip`.
2. The `octopus-release` job uploads the package to the built-in feed and creates release `1.0.<run>`. The lifecycle then deploys it to tdd, then uat, then prod, each once the previous one succeeds. The job waits for all three and fails if any deployment fails.
3. The `deploy` job no longer deploys. It runs the twin's end-to-end checks against what Octopus rolled out.

To go back to direct deploys from GitHub Actions, set the repository variables `TWIN_DEPLOY_VIA_OCTOPUS=false` and `TWIN_OCTOPUS_RELEASES=false`.

## How the Twin Sees Its Own Deployments

The twin subscribes to every event category in the space, so its own releases and deployments are ingested like any other. They map to the `twin-observer` pipeline, so they never count as cmdemo1 deployments: not in change chains, DORA metrics, stage cards or alerts. `TwinOwnDeploymentTests` covers this.
