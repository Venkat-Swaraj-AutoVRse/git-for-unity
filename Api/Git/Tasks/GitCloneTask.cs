using System.Threading;
using System.Text;
using Unity.Editor.Tasks;

namespace Unity.VersionControl.Git.Tasks
{
    public class GitCloneTask : GitProcessTask<string>
    {
        private readonly string arguments;

        public GitCloneTask(IPlatform platform, string url, string destination, CancellationToken token = default)
            : base(platform, null, outputProcessor: new StringOutputProcessor(), token: token)
        {
            Guard.ArgumentNotNullOrWhiteSpace(url, nameof(url));
            Guard.ArgumentNotNullOrWhiteSpace(destination, nameof(destination));
            Name = "git clone";
            arguments = $"clone -- {Quote(url)} {Quote(destination)}";
        }

        private static string Quote(string value)
        {
            var quoted = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in value)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }
                quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                quoted.Append(c);
            }
            quoted.Append('\\', slashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        public override string ProcessArguments => arguments;
        public override TaskAffinity Affinity { get; set; } = TaskAffinity.Exclusive;
        public override string Message { get; set; } = "Cloning repository...";
    }
}
