namespace AiObservatory.Data.Entities;

public sealed class GitHubBackfillState
{
    public string Repo { get; init; } = "";
    public bool HasPullRequests { get; set; }
    public bool HasCommits { get; set; }
    public bool HasWorkflowRuns { get; set; }
}
