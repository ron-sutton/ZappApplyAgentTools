using System.Text.Json;
using ZappApplyAgentTools;

static class DescriptionChecks
{
    public static async Task Run(Action<string,bool> check)
    {
        const string html="""
        <script type="application/ld+json">{"@type":"JobPosting","title":"Engineer","description":"<p>Build documented systems.</p><p>Review changes.</p><ul><li>Operate equipment.</li><li>Maintain records.</li></ul><script>untrusted()</script>"}</script>
        """;
        // Escape an embedded closing script so the surrounding HTML script remains valid.
        var sourceHtml=html.Replace("<script>untrusted()</script>","<script>untrusted()<\\/script>");
        var description=PublicPostingReader.DescriptionFromHtml(sourceHtml)!;
        check("Reader retains paragraph and list boundaries",description.Contains("systems.\n")&&description.Contains("equipment.\n")&&description.Contains("Maintain records."));
        check("Reader description excludes executable source markup",!description.Contains("untrusted()"));
        var store=new MemoryStore();var options=new SearchOptions{ControlKey=new string('c',40),McpKey=new string('d',40),TargetCount=4,WorkerCount=2};
        var service=new SearchService(store,new DescriptionReader(description),new FakePolicy(),options,TimeProvider.System);
        var id=Guid.NewGuid();var request=JsonSerializer.SerializeToElement(new{schemaVersion=1,jobId=id,jobType="AI_Search",ownerId="fixture-owner",payload=new{searchToolsVersion=2}});
        var handle=(await service.Begin(request,default)).Handle;var round=await service.Control(id,new("fixture-owner"),default);
        check("Small test creates two assignments sharing a four-job target",round.Input.GetProperty("assignments").GetArrayLength()==2&&round.Progress.TargetCount==4);
        await service.UpdateWorker(handle,"round-0-worker-1","started",default);
        var source=await service.ReadPosting(handle,"https://fixture.example/jobs/one",default,"round-0-worker-1");
        var submission=new SourcePosting{Url=source.Url,Title="Engineer",Company="Fixture Employer",Source="Fixture",Description="A short AI summary with an invented claim."};
        var progress=await service.Submit(handle,[new(source.Id,submission)],default,"round-0-worker-1");
        var saved=(await store.Read(id,default))!.Accepted.Single().Posting.GetProperty("description").GetString();
        check("AI formatting or summarization cannot cause exact-description rejection",progress.AcceptedCount==1);
        check("Saved description is the full retained source, never AI additions",saved==description&&!saved.Contains("invented"));
        check("Submission does not mutate the caller's metadata object",submission.Description.Contains("invented"));
        var legacy=new SourceRecord(Guid.NewGuid(),id,"https://fixture.example/jobs/legacy","Fixture Employer Engineer. Full retained legacy evidence.","fixture",DateTimeOffset.UtcNow);
        await store.SaveSource(legacy,default);
        await service.Submit(handle,[new(legacy.Id,new(){Url=legacy.Url,Title="Engineer",Company="Fixture Employer",Source="Fixture"})],default,"round-0-worker-1");
        check("Older stored evidence remains usable without a description field",(await store.Read(id,default))!.Accepted.Last().Posting.GetProperty("description").GetString()==legacy.Text);
    }
    sealed class DescriptionReader(string description):IPostingReader
    {
        public Task<(string Url,string Text)> Read(string url,CancellationToken ct)=>Task.FromResult((url,"Fixture Employer Engineer. "+description));
        public Task<PostingEvidence> ReadEvidence(string url,CancellationToken ct)=>Task.FromResult(new PostingEvidence(url,"Fixture Employer Engineer. "+description,[],description));
    }
}
