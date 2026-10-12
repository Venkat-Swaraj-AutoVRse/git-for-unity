using System.Threading;
using Unity.Editor.Tasks;

namespace Unity.VersionControl.Git.Tasks
{
    public class GitMergeTask : GitProcessTask<string>
    {
        private readonly string arguments;

        public GitMergeTask(IPlatform platform, string sourceBranch, CancellationToken token = default)
            : base(platform, null, outputProcessor: new StringOutputProcessor(), token: token)
        {
            Guard.ArgumentNotNullOrWhiteSpace(sourceBranch, nameof(sourceBranch));
            Name = "git merge";
            arguments = "merge --no-edit -- \"" + sourceBranch.Replace("\"", "\\\"") + "\"";
        }

        public override string ProcessArguments => arguments;
        public override TaskAffinity Affinity { get; set; } = TaskAffinity.Exclusive;
        public override string Message { get; set; } = "Merging branch...";
    }
}
