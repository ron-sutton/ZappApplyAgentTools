# Deployment and rollback

Nothing is enabled automatically. Deploy the updated app and queue code before enabling the website flag. Existing searches without `searchToolsVersion: 1` keep their existing path.

## 1. Configure service storage and authentication

Copy `deploy/service.env.example` to a server file outside the checkout, for example `C:\ProgramData\ZappApplyAgentTools\service.env`. Supply the SQL connection and three independent random secrets of at least 32 characters:

- `Search__ControlKey`: shared with the queue, never Hermes.
- `Search__McpKey`: shared with Hermes, never browser/user input.
- `App__PolicyKey`: matches `SearchTools__PolicyKey` on the website.

Use the same existing application database or a dedicated database. The tool service uses only `SearchToolRuns` and `SearchToolSources`. Initialization requires table-creation permissions; the normal service account needs data access to those tables. It does not need application-table privileges.

In this repository on the server:

```powershell
$env:ZAPPAPPLY_AGENTTOOLS_ENV_FILE = 'C:\ProgramData\ZappApplyAgentTools\service.env'
docker compose build agenttools
docker compose run --rm --no-deps agenttools --initialize-store
docker compose up --detach agenttools
curl.exe -f http://127.0.0.1:18643/healthz
```

Initialization is explicit and idempotent. No new website EF migration is needed. SQL tables must exist before service readiness succeeds. The service is .NET 11, matching the website; its Docker image uses the same .NET major version.

## 2. Website and queue configuration

Website server environment file:

```text
SearchTools__PolicyKey=THE_APP_POLICY_SECRET
RabbitMQ__UseMcpJobSearch=false
```

Queue worker environment file:

```text
SearchTools__Enabled=true
SearchTools__BaseUrl=http://zappapply-agenttools:8080/
SearchTools__ControlKey=THE_CONTROL_SECRET
SearchTools__AllowInsecureHttp=true
```

Plain HTTP above is for the trusted Docker network only. Use HTTPS for remote service connections. Restart/recreate containers to apply changed environment files; a simple process restart does not reload Docker's environment settings.

The app receives candidate policy checks at `/api/internal/search-tools/evaluate`. Only the dedicated service bearer-key scheme can authorize it; ordinary user cookies do not. It checks the immutable search criteria snapshot, owning user, cancellation, and that user's previously found/tracked openings. It reuses existing app matching rules.

## 3. Register the tools with Hermes

Ensure `hermes` and the new service share the configured Docker network (default `zappapply-queue_default`). Inspect existing connections before changing them. Add the example `deploy/hermes-mcp.example.yaml` entry to the existing `mcp_servers` section in Hermes's persisted config; do not overwrite other servers.

Set `ZAPPAPPLY_AGENTTOOLS_MCP_KEY` in Hermes's persisted secret/environment configuration to the MCP secret, then reload/restart Hermes. This is a different secret from the queue control key. Confirm the parent API agent **and its delegated job-search worker** can see all four tools, along with Hermes's existing web-search tools. Check the installed Hermes version's MCP diagnostics; test discovery before sending a real search. Delegation must retain the supplied handle and these tool permissions.

The YAML follows [Hermes's official MCP configuration reference](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/reference/mcp-config-reference.md). `trust: full` is limited to this server you operate and its four scoped tools, so noninteractive API searches can save candidates without a human approval prompt. Do not grant it to arbitrary external MCP servers.

## 4. Publish the app-owned prompt

The file default `SystemPrompts/Tasks/SearchJobs.md` in ZappApply includes the conditional `<!-- search-tools-v1 -->` section. SQL prompt installations do **not** automatically overwrite published admin edits when files change. Publish this section as a new version of **task.search-jobs** through the app's prompt management service/Admin System Prompts page, preserving existing content and history. It applies only to requests carrying a search-tools handle. No prompts belong in the queue or this service.

## 5. Enable and verify

Once service readiness, authentication, tool visibility, and the SQL prompt are verified, set `RabbitMQ__UseMcpJobSearch=true` on the website and recreate that container using its normal deployment process. Only newly admitted searches receive the version pin. Run one search and inspect the tool calls/counts, SQL saved results, and the existing queue completion journal. Do not resubmit old uncertain requests.

The queue ignores Hermes's narrative as a result in this mode. A model failure or early stop retains accepted jobs and returns an explicit partial warning. If the service/policy endpoint is unavailable, the search cannot silently bypass validation. The existing durable provider-run continuation behavior remains intact.

## Rollback

Set the website flag back to false for **new** searches. Leave the tool service, keys, and compatible worker running until already-admitted tool-enabled searches finish. Their immutable stored requests retain their version pin. Do not delete evidence or rotate capability-signing/control keys midway through those jobs. Never change a job ID to force an automatic AI rerun.
