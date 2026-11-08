# GitHub Actions deployment

The repository workflow `.github/workflows/deploy.yml` builds and deploys this service on pushes to `master`, or manually through **Actions → Deploy ZappApplyAgentTools → Run workflow**. It uses the same Windows/PowerShell 7/Linux-container pattern as ZappApply and ZapApplyQueue.

## One-time server setup

1. Give this repository access to a self-hosted runner with labels `self-hosted`, `Windows`, `X64`, and `zappapply-deploy`. A runner registered only to another repository cannot pick up this repository's jobs. Register a separate runner installation for this repository, or use an organization runner with access to it. Do not reconfigure/remove the existing app or queue runners.
2. The runner's service account must be able to use Docker, read the server configuration file, and run the all-users PowerShell 7 installation at `C:\Program Files\PowerShell\7\pwsh.exe`. Docker must run Linux containers; Compose must be 2.30.0 or newer. The existing `zappapply-queue_default` network must exist.
3. Create `C:\ProgramData\ZappApplyAgentTools\service.env` on the server using `deploy/service.env.example`. Fill in the actual SQL connection and distinct control/MCP keys; match `App__PolicyKey` to the website's policy key. Keep this file outside the runner checkout. Do not commit credentials to GitHub or paste them into workflow YAML.
4. For the first deployment, run the workflow manually with **initialize_store** selected. It creates the two search-tool SQL tables if missing, using the new image, before starting the service. The SQL identity needs table-creation permissions for this step. Leave the option unchecked for normal deployments. No SQL tables/data are dropped or reverted during rollback.

The workflow uses the `production` GitHub environment. Optional **repository/environment variables**, not secrets containing credentials:

| Variable | Default |
| --- | --- |
| `ZAPPAPPLY_AGENTTOOLS_ENV_FILE` | `C:\ProgramData\ZappApplyAgentTools\service.env` |
| `ZAPPAPPLY_QUEUE_NETWORK` | `zappapply-queue_default` |

No GitHub secrets are needed for the default deployment: actual credentials remain in the server environment file. If the production environment has an approval rule, GitHub waits for that environment approval before deploying.

## What deployment does

- Runs the PowerShell deployment checks with a fake Docker implementation.
- Builds a uniquely tagged local Docker image. Its required verification stage runs the C# tool checks with fake sources/policies, without live AI, external websites, or a SQL server.
- Optionally initializes the SQL store when explicitly requested in the manual workflow.
- Replaces only the `agenttools` service in Compose project `zappapply-agenttools`.
- Waits for the image health check at `/healthz`; this checks that the SQL store is reachable and initialized.
- Restores the previous immutable image if the new container fails readiness. A failed action stays failed even if rollback succeeds. On first deployment failure, stops the failed container.

Builds and SQL initialization happen before replacing the running container. Deployments are serialized. The script does not remove volumes, prune Docker images, modify RabbitMQ/Hermes, enable website feature flags, or manage the app/queue containers. Keep the host port `18643` bound to loopback and use the shared Docker network for service traffic.

After successful deployment, finish the app/queue/Hermes setup in [Deployment.md](Deployment.md) before enabling new searches. This action alone does not enable the controlled-search feature.

For a manual server deployment using the same script (PowerShell 7):

```powershell
$env:ZAPPAPPLY_AGENTTOOLS_ENV_FILE = 'C:\ProgramData\ZappApplyAgentTools\service.env'
./deploy/Deploy.ps1 -Image 'zappapply-agenttools:release-manual-001' -InitializeStore
```

Use a new image tag per deployment, and omit `-InitializeStore` on later runs. Rollback keeps the same server configuration; changing keys while searches are active is a separate operational change and is not undone by an image rollback.

## Local checks

```powershell
./tests/deployment/Deploy.Checks.ps1
dotnet run --project tests/ZappApplyAgentTools.Checks -p:UseAppHost=false
```

The deployment checks never call real Docker. A full Linux image build requires a running Docker engine and access to the SDK/runtime images and package feeds.
