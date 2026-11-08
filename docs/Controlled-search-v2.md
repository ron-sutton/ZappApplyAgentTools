# Controlled parallel search, protocol 2

Hermes chooses queries and sources. The app-owned prompt directs four parallel delegated bots. The C# service assigns worker IDs and disjoint starting-source suggestions, stores actual source-page attempts, saves accepted candidates immediately and maintains **one shared target of 10**, not ten per bot. Suggestions are not a whitelist. User criteria remain authoritative.

The queue's `/control` exchange carries execution data, not instructions. A completed Hermes turn is a checkpoint. If registered children are still running, the queue schedules a status continuation and does not dispatch another turn. Once the pass ends below target, the service supplies another immutable round on the **same logical search**. The native Runs checkpoint uses a distinct deterministic provider-turn ID linked to the app job; its existing idempotent admission and restart recovery are retained. The app job ID and broker completion shape do not change.

`finish_search(...,"sources_exhausted")` cannot finalize a v2 partial. A partial ends only on actual tool/time/continuation limits, owner cancellation or terminal provider failure. Defaults: four bots, 100 reading/submission calls, 30 minutes, eight rounds. `Search__WorkerCount` (1–4) and `Search__MaxRounds` (1–20) are service limits. Native Hermes search/browser calls have separate provider limits and are not counted as service tool calls. Models can ignore instructions or fail; we cannot guarantee that ten suitable openings exist or that every bot will cooperate. Noncooperative rounds are bounded and do not fabricate success.

`get_search_progress` includes current worker states, recent page-access attempts and pending suggested domains. `inspect_source` actually accesses a public page; it cannot certify every job on that site was searched. An unavailable page is recorded as unavailable, not as an empty job market. Native web-search calls that never pass through our tools are not independent coverage evidence. A worker must register before discovery and complete only after its tools stop. Uncertain running workers are not replaced while they may still be doing work; the real deadline remains in force.

Reader-derived application links are retained with source evidence and sent to the website's dedicated authenticated criteria endpoint. They strengthen owner-scoped duplicate checks across boards and employer/ATS wrappers. Model candidate objects cannot inject these verified aliases. Different requisitions remain eligible even with the same employer/title. A board with no usable application link can still be harder to associate; no claim of universal cross-site deduplication is made.

## Rollout order

1. Deploy the updated **ZappApply website** with `RabbitMQ__McpSearchProtocolVersion=1` (default). Existing `UseMcpJobSearch=true` can stay. This installs the service-only ownership/status endpoint and verified-link policy without admitting v2 requests.
2. Deploy updated **AgentTools** and **ZapApplyQueue**. Existing v1 requests continue through their pinned v1 path. Existing SearchToolRuns/Source JSON columns hold the additional state: **no new SQL migration**. The worker adds its provider-turn linkage table automatically to its existing persistent SQLite state volume.
3. Preserve existing MCP server URL, bearer secret and other config. Update `mcp_servers.zappapply_agenttools.tools.include` to `[read_posting, submit_candidates, get_search_progress, finish_search, worker_update, inspect_source]` and keep `supports_parallel_tool_calls=true`. Restart Hermes and confirm `hermes mcp test zappapply_agenttools` discovers **six** tools. Both parent and delegated workers need them. Ensure Docker network membership survives Hermes container recreation.
4. The conditional SQL sections were published during implementation as **task.search-jobs v4** and **feature.job-search v2**, preserving previous content and history. Verify `<!-- search-tools-v2 -->` and `<!-- source-plan-v2 -->` in **Admin / System Prompts**; do not publish duplicates. File defaults contain the same sections. These sections are conditional: v1 requests retain existing behavior.
5. Last, add `RabbitMQ__McpSearchProtocolVersion=2` to the website environment file and **recreate** its container. `docker restart` does not reload an env file. Verify the running value with `docker exec zappapply-web-1 printenv RabbitMQ__McpSearchProtocolVersion`.
6. Start one new search. Check a single shared handle, four distinct worker registrations, actual source attempts, accepted/remaining counts and controlled continuation if a pass ends early. Old failed requests are not automatically reissued.

Example server commands for step 3 (no credentials are printed or changed):

```powershell
@'
import json, subprocess
names = ['read_posting','submit_candidates','get_search_progress','finish_search','worker_update','inspect_source']
subprocess.run(['hermes','config','set','mcp_servers.zappapply_agenttools.tools.include',json.dumps(names)],check=True)
subprocess.run(['hermes','config','set','mcp_servers.zappapply_agenttools.supports_parallel_tool_calls','true'],check=True)
'@ | docker exec -i --user hermes hermes /opt/hermes/.venv/bin/python -
if ($LASTEXITCODE -ne 0) { throw 'MCP configuration failed.' }
docker restart hermes
docker exec hermes hermes mcp test zappapply_agenttools
```

Rollback: switch **new admissions** back to protocol 1. Keep compatible queue/service/MCP tools running until admitted v2 searches drain; do not revert to a worker that cannot understand v2 payloads, delete state, rotate active keys or change app job IDs to force another execution.

## Local verification

`dotnet run --project tests/ZappApplyAgentTools.Checks -- --localdb` uses fictional jobs and a disposable SQL database, covering four-bot cap enforcement, stop-at-two continuation, actual attempts, optional non-listed sources, immutable inputs, parent/child waits, stale workers, replay, restarts, deadline/provider/cancellation and owner isolation. The queue's checks use a fake native Runs provider and control service; the website's SearchToolPolicyChecks use an isolated database. No live AI is needed.
