using System.Text.Json;
using ZappApplyAgentTools;

static class ControlledChecks
{
    public static async Task Run(Action<string, bool> check, ISearchStore? suppliedStore = null)
    {
        var store = suppliedStore ?? new MemoryStore(); var clock = new FakeClock(); var policy = new ControlledPolicy();
        var options = new SearchOptions { ControlKey = new string('c', 40), McpKey = new string('d', 40), MaxRounds = 3 };
        SearchService Service() => new(store, new FakeReader(), policy, options, clock);
        var service = Service(); var id = Guid.NewGuid();
        JsonElement Input(Guid job) => JsonSerializer.SerializeToElement(new { schemaVersion = 1, jobId = job, jobType = "AI_Search", ownerId = "control-owner", payload = new { searchToolsVersion = 2, startingSources = new[] { "one.example", "two.example", "three.example", "four.example", "five.example" } } });
        var handle = (await service.Begin(Input(id), default)).Handle;
        var first = await service.Control(id, new("control-owner"), default);
        check("V2 assigns four bots with distinct starting sources", first.Input.GetProperty("assignments").GetArrayLength() == 4 && (await store.Read(id, default))!.Workers.SelectMany(w => w.Sources).Distinct().Count() == 5);
        check("V2 replay preserves immutable provider input", (await Service().Control(id, new("control-owner"), default)).Input.GetRawText() == first.Input.GetRawText());
        async Task<bool> Reject(Func<Task> call) { try { await call(); return false; } catch (ToolFailure) { return true; } }
        check("V2 worker registration is required", await Reject(() => service.ReadPosting(handle, "https://one.example/jobs/a", default)));
        check("V2 control rejects another owner", await Reject(() => service.Control(id, new("other-owner"), default)));
        check("V2 legacy completion cannot bypass search controls", await Reject(() => service.Complete(id, "control-owner", "agent_stopped", default)));
        var workers = (await store.Read(id, default))!.Workers;
        await Task.WhenAll(workers.Select(w => service.UpdateWorker(handle, w.Id, "started", default)));
        async Task Add(string worker, string url)
        {
            var source = await Service().ReadPosting(handle, url, default, worker);
            await Service().Submit(handle, [new(source.Id, new() { Url = source.Url, Title = "Engineer", Company = "Fixture Employer", Description = "Engineer develops systems. Salary 175000. Remote US.", Source = "Fixture" })], default, worker);
        }
        await Task.WhenAll(Add(workers[0].Id, "https://one.example/jobs/1"), Add(workers[1].Id, "https://elsewhere.example/jobs/2"));
        var afterTwo = await service.Finish(handle, "sources_exhausted", default);
        check("Two results and model source-exhaustion claim cannot finalize", afterTwo.Status == "running" && afterTwo.Remaining == 8);
        var waiting = await service.Control(id, new("control-owner", 0), default);
        check("Completed parent waits for active children", waiting.Decision == "wait");
        await Task.WhenAll(workers.Select(w => service.UpdateWorker(handle, w.Id, "completed", default)));
        var second = await Service().Control(id, new("control-owner"), default);
        check("Early stop continues same search with new provider turn", second.Decision == "run" && second.Sequence == 1 && second.ExecutionId != first.ExecutionId && second.Progress.AcceptedCount == 2);
        check("Source assignments do not restrict autonomous discovery", second.Progress.SourceAttempts!.Any(a => a.Domain == "elsewhere.example"));
        check("Coverage records actual source reads only", second.Progress.PendingSources!.Contains("two.example") && !second.Progress.PendingSources.Contains("one.example"));
        var replay = await Service().Control(id, new("control-owner", 0), default);
        check("Old completion replay does not advance a new turn", replay.Sequence == 1 && replay.ExecutionId == second.ExecutionId && replay.Input.GetRawText() == second.Input.GetRawText());
        check("Old workers cannot submit into new round", await Reject(() => Add(workers[0].Id, "https://one.example/jobs/stale")));
        var nextWorkers = (await store.Read(id, default))!.Workers.Where(w => w.Round == 1).ToList();
        await Task.WhenAll(nextWorkers.Select(w => service.UpdateWorker(handle, w.Id, "started", default)));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Add(nextWorkers[i % 4].Id, "https://elsewhere.example/jobs/new" + i)));
        var final = await service.Control(id, new("control-owner"), default);
        using var json = JsonDocument.Parse(final.Content!);
        check("Four concurrent bots share one cap of ten accepted jobs", final.Decision == "final" && final.Progress.AcceptedCount == 10 && json.RootElement.GetProperty("jobs").GetArrayLength() == 10);
        check("Finalization survives service restart and completion replay", (await Service().Control(id, new("control-owner", 1), default)).Content == final.Content);
        check("Late provider-failure notification cannot rewrite a finished result", (await Service().Control(id, new("control-owner", 1, "provider_failed"), default)).Content == final.Content);
        var failedId = Guid.NewGuid(); var failedHandle = (await service.Begin(Input(failedId), default)).Handle;
        var failedRound = await service.Control(failedId, new("control-owner"), default);
        await service.UpdateWorker(failedHandle, "round-0-worker-1", "started", default);
        var sourceFailed = await service.ReadPosting(failedHandle, "https://one.example/jobs/partial", default, "round-0-worker-1");
        await service.Submit(failedHandle, [new(sourceFailed.Id, new() { Url = sourceFailed.Url, Title = "Engineer", Company = "Fixture Employer", Description = "Engineer develops systems. Salary 175000. Remote US.", Source = "Fixture" })], default, "round-0-worker-1");
        var failed = await service.Control(failedId, new("control-owner", 0, "provider_failed"), default);
        check("Provider failure retains real partial jobs with honest reason", failed.Progress.AcceptedCount == 1 && failed.Content!.Contains("provider_failed"));
        var timeoutId = Guid.NewGuid(); await service.Begin(Input(timeoutId), default); await service.Control(timeoutId, new("control-owner"), default);
        clock.Advance(TimeSpan.FromHours(1));
        check("Actual deadline ends search without inventing jobs", (await service.Control(timeoutId, new("control-owner"), default)).Progress.FinishReason == "budget_exhausted");
        var cancelId = Guid.NewGuid(); await service.Begin(Input(cancelId), default); policy.Cancel = true;
        check("Owner cancellation is checked before another model turn", (await service.Control(cancelId, new("control-owner"), default)).Progress.FinishReason == "cancelled");
        policy.Cancel = false;
        var blockedId = Guid.NewGuid(); var blockedHandle = (await service.Begin(Input(blockedId), default)).Handle;
        await service.Control(blockedId, new("control-owner"), default); await service.UpdateWorker(blockedHandle, "round-0-worker-1", "started", default);
        var blockedService = new SearchService(store, new BlockedReader(), policy, options, clock);
        await Reject(() => blockedService.ReadPosting(blockedHandle, "https://one.example/", default, "round-0-worker-1"));
        var blockedProgress = await service.GetProgress(blockedHandle, default);
        check("Blocked sources are recorded without pretending their jobs were searched", blockedProgress.SourceAttempts!.Single().Outcome == "blocked" && blockedProgress.PendingSources!.Contains("two.example"));
        var heldReader = new HeldReader(); var heldService = new SearchService(store, heldReader, policy, options, clock);
        var activeRead = heldService.ReadPosting(blockedHandle, "https://two.example/jobs/held", default, "round-0-worker-1");
        await heldReader.Entered.Task;
        await service.UpdateWorker(blockedHandle, "round-0-worker-1", "completed", default);
        check("Worker completion waits for a known in-flight tool call", (await service.Control(blockedId, new("control-owner", 0), default)).Decision == "wait");
        heldReader.Release.SetResult(); await activeRead;
        check("Drained tools release a finishing worker before continuing", (await service.Control(blockedId, new("control-owner"), default)).Sequence == 1);
        var budgetId = Guid.NewGuid(); await service.Begin(Input(budgetId), default);
        for (var i = 0; i < options.MaxRounds; i++)
        {
            var round = await service.Control(budgetId, new("control-owner"), default);
            clock.Advance(TimeSpan.FromSeconds(21)); await service.Control(budgetId, new("control-owner", round.Sequence), default);
            clock.Advance(TimeSpan.FromSeconds(21));
        }
        var budget = await service.Control(budgetId, new("control-owner"), default);
        check("Noncooperative model is bounded by durable continuation limit", budget.Decision == "final" && budget.Progress.FinishReason == "continuation_budget_exhausted");
        var links = PublicPostingReader.ApplicationLinks("<a href='https://apply.example/jobs/17'>Apply now</a><a href='https://other.example/'>Unrelated</a>", new("https://board.example/jobs/5"));
        check("Duplicate evidence uses actual application links without unrelated navigation", links.SequenceEqual(new[] { "https://apply.example/jobs/17" }));
    }
    sealed class ControlledPolicy : IApplicationPolicy
    {
        public Task<DuplicateCheckResult> CheckDuplicate(Guid id, string owner, string url, CancellationToken ct) => Task.FromResult(new DuplicateCheckResult(false));
        public bool Cancel;
        public Task<bool> IsCancelled(Guid id, string owner, CancellationToken ct) => Task.FromResult(Cancel);
        public Task<PolicyResult> Evaluate(Guid id, string owner, JsonElement posting, CancellationToken ct) => Task.FromResult(new PolicyResult(true, posting.GetProperty("url").GetString()!, []));
    }
    sealed class BlockedReader : IPostingReader
    { public Task<(string Url,string Text)> Read(string url,CancellationToken ct) => throw new ToolFailure("Posting could not be read: HTTP 403."); }
    sealed class HeldReader : IPostingReader
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<(string Url,string Text)> Read(string url,CancellationToken ct) { Entered.SetResult(); await Release.Task; return (url,"Fixture Employer Engineer develops systems. Remote US. Documented source."); }
    }
}
