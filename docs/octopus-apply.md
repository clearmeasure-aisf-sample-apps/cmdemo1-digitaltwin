# Deploying the Twin Through Its Own Pipeline: What to Apply in Octopus

The twin's release pipeline is defined in this repository. Nothing in Octopus has been created or changed for it: no project, environment, lifecycle, variable, team, account, credential or subscription. No release has been created and nothing has been promoted. cmdemo1's Octopus space (`Spaces-355`) belongs to its owner, and applying the definitions below is their decision.

## What Runs Today

| Piece | Where | State |
| --- | --- | --- |
| Release candidate: image `acrcmdemo1chu4gq.azurecr.io/cmdemo1/digitaltwin:1.0.<run>` plus Octopus package `cmdemo1-digitaltwin.1.0.<run>.zip` | GitHub Actions `release-candidate` job, every push to `main` | **On.** The package is kept as a workflow artifact for 30 days |
| Push the package to Octopus and create a release | GitHub Actions `octopus-release` job | **Off.** Runs only when the repository variable `TWIN_OCTOPUS_RELEASES` is `true` |
| Deploy | GitHub Actions `deploy` job, which runs `deploy/scripts/deploy-twin.sh` | **On** (direct, as before). Skipped once `TWIN_DEPLOY_VIA_OCTOPUS` is `true` |
| Octopus project and process | `deploy/octopus/terraform` | **Not applied.** CI checks it with `terraform fmt -check` and `terraform validate` only |

The package contains:
- `infra/main.json`: the compiled Bicep, so workers need no Bicep CLI.
- The Bicep sources.
- `deploy/scripts/` (`azure-login.sh`, `deploy-twin.sh`, `verify-twin.sh`, `package-twin.sh`).
- `scripts/register-hooks.sh`.
- `docs/platforms/*.platform.json`: platform definitions.
- `release.json`: package id, version, image, commit and build time.

GitHub Actions and the Octopus steps run the same scripts, so a deploy behaves the same from either side.

## What the Octopus Owner Applies

### 1. Choose the Names and the Worker

These are the defaults in `deploy/octopus/terraform/variables.tf`; override any of them with `-var` or a `*.tfvars` file. Do not commit a tfvars file; `.gitignore` excludes them.

| Variable | Default | Meaning |
| --- | --- | --- |
| `space_id` | `Spaces-355` | Space for the project |
| `project_name` | `cmdemo1-digitaltwin` | The twin's project. The twin attributes its deployments to its own observer pipeline, not to cmdemo1's |
| `project_group_id` / `project_group_name` | empty / `Platform` | Use an existing group id, or create a group with this name |
| `environment_name` | `Platform Twin` | One environment. The twin is a single platform-level instance above tdd, uat and prod |
| `auto_deploy` | `false` | When true, each new release deploys automatically. Otherwise someone deploys it |
| `worker_pool_id` / `worker_pool_name` | empty / `Hosted Ubuntu` | The steps are Bash and need a Linux worker |
| `worker_container_feed_id` / `worker_container_image` | empty / `octopusdeploy/worker-tools:6-ubuntu.22.04` | Optional execution container; needs a Docker Hub feed in the space. Without it, the worker itself needs `az`, `jq`, `curl` and `python3` |
| `resource_group`, `container_app_name`, `acr_name`, `image_repository` | `rg-cmdemo1-nonprod`, `ca-cmdemo1-twin`, `acrcmdemo1chu4gq`, `cmdemo1/digitaltwin` | Where the twin runs |
| `system_slug`, `github_org`, `platform_file` | `cmdemo1`, `clearmeasure-aisf-sample-apps`, empty | The observed platform (docs/setup/README.md) |

### 2. Apply the Terraform

```bash
cd deploy/octopus/terraform
# Sensitive values come from the owner's secret store. They hold the same values as the GitHub repository secrets.
export TF_VAR_octopus_api_key=...        # the applying person's own Octopus key (needs project/environment/lifecycle create in Spaces-355)
export TF_VAR_azure_client_id=...        # AZURE_CLIENT_ID
export TF_VAR_azure_client_secret=...    # AZURE_CLIENT_SECRET
export TF_VAR_azure_tenant_id=...        # AZURE_TENANT_ID
export TF_VAR_azure_subscription_id=...  # AZURE_SUBSCRIPTION_ID
export TF_VAR_twin_octopus_api_key=...   # OCTOPUS_API_KEY (the key the twin uses)
export TF_VAR_github_pat=...             # GH_PAT
export TF_VAR_webhook_secret=...         # WEBHOOK_SECRET
export TF_VAR_twin_api_key=...           # TWIN_API_KEY
terraform init      # configure a private, encrypted remote backend first: state contains the sensitive values
terraform plan
terraform apply
```

`terraform apply` creates exactly:
- A project group `Platform` (only when `project_group_id` is empty).
- An environment `Platform Twin`.
- A lifecycle `cmdemo1-digitaltwin lifecycle`, with one phase for that environment: optional, or automatic when `auto_deploy` is true.
- A project `cmdemo1-digitaltwin`, untenanted, allowing deployments with no targets.
- Project variables:
  - **10 string variables**: `Twin.ResourceGroup`, `Twin.AppName`, `Twin.AcrName`, `Twin.OctopusUrl`, `Twin.EnableEventGrid`, `Twin.SystemSlug`, `Twin.GitHubOrg`, `Twin.OctopusSpace`, `Twin.PlatformFile` (a platform definition inside the package; empty for cmdemo1), and `Twin.Image`, which is `<acr>.azurecr.io/cmdemo1/digitaltwin:#{Octopus.Release.Number}`.
  - **8 sensitive variables**: `Azure.ClientId`, `Azure.ClientSecret`, `Azure.TenantId`, `Azure.SubscriptionId`, `Twin.OctopusApiKey`, `Twin.GitHubPat`, `Twin.WebhookSecret`, `Twin.ApiKey`.
- A deployment process of three Bash script steps, pinned in this order. Each step references the package `cmdemo1-digitaltwin` from the built-in feed, extracted on the worker.
  1. **Deploy twin** (`steps/deploy.sh`): logs in to Azure, then runs `deploy/scripts/deploy-twin.sh`. That script waits out a running rebuild, deploys `infra/main.json` with the release's image, and waits until the revision serves `#{Octopus.Release.Number}`. It sets the output variable `TwinUrl`.
  2. **Register hooks and subscriptions** (`steps/register-hooks.sh`): runs `scripts/register-hooks.sh`, which manages the GitHub webhooks, the Octopus subscription and Event Grid. It is idempotent and keeps within the GitHub quota.
  3. **Verify twin** (`steps/verify.sh`): runs `deploy/scripts/verify-twin.sh`. The deploy fails unless all of these hold: health is Healthy at the release number, dead letters are 0, the database is under 1,500 MB of the Basic tier's 2 GB, and MCP initialize answers.

It does not create accounts, certificates, teams, users, API keys, feeds, worker pools, triggers, channels, runbooks or tenants.

### 3. Let GitHub Create Releases (Optional)

For GitHub Actions to push the package and create releases, the key in the repository secret `OCTOPUS_API_KEY` needs these permissions on the twin's project only:
- `BuiltInFeedPush`.
- `ReleaseCreate` (plus `ReleaseView` and `ProjectView`).

Grant them through a team scoped to that project; that is an Octopus team change for the owner to make. Then set the repository variable `TWIN_OCTOPUS_RELEASES=true`. Each push to `main` uploads `cmdemo1-digitaltwin.<version>.zip` (`overwriteMode=IgnoreIfExists`) and creates release `<version>` through `releases/create/v1`. The job never deploys or promotes; with `auto_deploy=false`, a release waits in Octopus.

Optional repository variables: `TWIN_OCTOPUS_SPACE` (default `Spaces-355`) and `TWIN_OCTOPUS_PROJECT` (default `cmdemo1-digitaltwin`).

### 4. First Octopus Deploy, Then Hand Over

1. Deploy one release to `Platform Twin` from Octopus and confirm the Verify twin step passes.
2. Set the repository variable `TWIN_DEPLOY_VIA_OCTOPUS=true`. The GitHub `deploy` job is then skipped and deploys come only from Octopus. Optionally set `auto_deploy=true` and re-apply.
3. To roll back to direct deploys, delete the variable. To stop creating releases, delete `TWIN_OCTOPUS_RELEASES`.

## How the Twin Sees Its Own Deployments

The twin already subscribes to every event category in the space, so the new project's releases and deployments are ingested like any other. `Cmdemo1PlatformCatalog.ResolvePipelineIdFromOctopusProject("cmdemo1-digitaltwin")` maps them to the `twin-observer` pipeline, with no cmdemo1 deployable, so they never count as cmdemo1 deployments in change chains or DORA metrics. `ReleasePipelineTests` enforces two things:
- Every Octopus step reads only variables defined in `main.tf`.
- Every step runs a script that exists and is packaged, and GitHub Actions and Octopus call the same scripts.
