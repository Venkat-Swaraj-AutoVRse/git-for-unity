using System;
using System.Collections.Generic;

namespace Unity.VersionControl.Git.PreCommit
{
    /// <summary>
    /// How strongly a failed pre-commit check should affect the commit.
    /// </summary>
    public enum PreCommitSeverity
    {
        /// <summary>The check passed; nothing to report.</summary>
        Pass = 0,

        /// <summary>The check found problems but the user may choose to commit anyway.</summary>
        Warning = 1,

        /// <summary>The check found problems that must be fixed; the commit is aborted
        /// (unless the studio-wide override in Settings is enabled).</summary>
        Block = 2,
    }

    /// <summary>
    /// A single problem reported by a check. <see cref="Message"/> is a one-line summary shown in
    /// the blocking/warning dialog; <see cref="Detail"/> is optional extra context.
    /// </summary>
    public class PreCommitIssue
    {
        public string Message;
        public string Detail;

        public PreCommitIssue(string message, string detail = null)
        {
            Message = message;
            Detail = detail;
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Detail) ? Message : Message + " — " + Detail;
        }
    }

    /// <summary>
    /// The outcome of running one <see cref="IPreCommitCheck"/>.
    /// Use the static factories rather than constructing directly.
    /// </summary>
    public class PreCommitCheckResult
    {
        /// <summary>Overall severity of this result.</summary>
        public PreCommitSeverity Severity { get; private set; }

        /// <summary>The problems found (empty when <see cref="Severity"/> is Pass).</summary>
        public List<PreCommitIssue> Issues { get; private set; } = new List<PreCommitIssue>();

        /// <summary>
        /// Optional callback that opens a UI (e.g. a resolution window) letting the user fix the
        /// reported problems. When set, the commit dialog offers a "Fix…" button. After the user
        /// finishes fixing, the checks are re-run.
        /// </summary>
        public Action Fix { get; private set; }

        /// <summary>True when a <see cref="Fix"/> action is available.</summary>
        public bool HasFix => Fix != null;

        public bool IsPass => Severity == PreCommitSeverity.Pass;
        public bool IsWarning => Severity == PreCommitSeverity.Warning;
        public bool IsBlock => Severity == PreCommitSeverity.Block;

        public static PreCommitCheckResult Pass()
        {
            return new PreCommitCheckResult { Severity = PreCommitSeverity.Pass };
        }

        public static PreCommitCheckResult Warning(IEnumerable<PreCommitIssue> issues, Action fix = null)
        {
            var result = new PreCommitCheckResult { Severity = PreCommitSeverity.Warning, Fix = fix };
            if (issues != null)
                result.Issues.AddRange(issues);
            return result;
        }

        public static PreCommitCheckResult Warning(string message, Action fix = null)
        {
            return Warning(new[] { new PreCommitIssue(message) }, fix);
        }

        public static PreCommitCheckResult Block(IEnumerable<PreCommitIssue> issues, Action fix = null)
        {
            var result = new PreCommitCheckResult { Severity = PreCommitSeverity.Block, Fix = fix };
            if (issues != null)
                result.Issues.AddRange(issues);
            return result;
        }

        public static PreCommitCheckResult Block(string message, Action fix = null)
        {
            return Block(new[] { new PreCommitIssue(message) }, fix);
        }
    }

    /// <summary>
    /// Information about the commit being attempted, handed to every check.
    /// </summary>
    public class PreCommitCheckContext
    {
        /// <summary>Absolute path to the repository working directory (contains .git).</summary>
        public string RepositoryPath { get; set; }

        /// <summary>
        /// The files selected for this commit, as paths relative to <see cref="RepositoryPath"/>
        /// using forward slashes. May be empty if the caller could not determine them.
        /// </summary>
        public IReadOnlyList<string> CommittedFilesRelative { get; set; } = new List<string>();

        /// <summary>The same files as absolute filesystem paths.</summary>
        public IReadOnlyList<string> CommittedFilesAbsolute { get; set; } = new List<string>();

        /// <summary>The commit summary the user typed.</summary>
        public string CommitMessage { get; set; }

        /// <summary>The commit description the user typed.</summary>
        public string CommitBody { get; set; }
    }

    /// <summary>
    /// Implement this (with a public parameterless constructor) anywhere in an Editor assembly to
    /// add a pre-commit check. Implementations are discovered automatically by reflection — no
    /// registration needed — and run in ascending <see cref="Order"/> before every commit made
    /// from the Git for Unity window.
    ///
    /// A check decides its own severity per run: return <see cref="PreCommitCheckResult.Block"/>
    /// for a hard stop, <see cref="PreCommitCheckResult.Warning"/> for something the user can
    /// commit past, or <see cref="PreCommitCheckResult.Pass"/> when all is well. Provide an
    /// optional Fix action to open a resolution UI.
    /// </summary>
    public interface IPreCommitCheck
    {
        /// <summary>Human-readable name shown in the results dialog.</summary>
        string DisplayName { get; }

        /// <summary>Run order; lower runs first. Ties broken by DisplayName.</summary>
        int Order { get; }

        /// <summary>
        /// Whether this check applies to the given commit at all. Return false to skip it
        /// entirely (e.g. a check that only concerns a specific repository or file type).
        /// </summary>
        bool AppliesTo(PreCommitCheckContext context);

        /// <summary>Run the check and return its result. Must not throw for expected conditions.</summary>
        PreCommitCheckResult Run(PreCommitCheckContext context);
    }
}
