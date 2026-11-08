# Release image retention

After a healthy deployment, Deploy.ps1 removes older unused release tags for this service only. It keeps the current image, the exact previous image for rollback, and every image referenced by a running or stopped container, including image aliases. Failed builds/readiness checks do not trigger cleanup. Inventory or deletion failures warn without rolling back a healthy release. No force deletion, global pruning, container deletion, build-cache pruning or volume removal is performed.

The first successful deployment with this change also clears existing old release tags. It does not require a database migration or configuration flag.

Standalone preview from this repository's checkout on the Docker server:

```powershell
& .\deploy\Cleanup-ReleaseImages.ps1 -Repository zappapply-agenttools -TagPrefix release- -WhatIf
```

Remove `-WhatIf` to clean. Standalone mode protects all container images and retains the newest unused release as an additional rollback candidate. To preserve a particular known-good release, also pass `-KeepImages 'repository:tag'` (or its full sha256 ID). Deployment uses exact current/previous IDs rather than guessing by date.

Images in unrelated repositories, non-release tags and unlabelled dangling build images are not targeted. Docker layers are shared; deleting an old tag may reclaim less than its displayed image size. Stopped containers intentionally keep their images until those containers are removed separately.

Run tests/deployment/ImageCleanup.Checks.ps1 and tests/deployment/DeploymentCleanup.Checks.ps1 with this service's deployment arguments. Both use fake Docker inventory and never delete real images. GitHub deployment runs these checks before building.
