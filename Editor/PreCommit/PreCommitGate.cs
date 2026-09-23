using System;
using System.Collections.Generic;
using Unity.Editor.Tasks.Logging;
using UnityEditor;

namespace Unity.VersionControl.Git.PreCommit
{
    using IO;

    /// <summary>
    /// Runs every discovered pre-commit check against a pending commit and drives the user through
    /// the block / warning / fix dialogs. Shared by the legacy IMGUI Changes view and the UI Toolkit
    /// window so both enforce exactly the same rules.
    /// </summary>
    public static class PreCommitGate
    {
        private static readonly ILogging Logger = LogHelper.GetLogger(typeof(PreCommitGate));

        /// <summary>
        /// Returns true if the commit should proceed.
        /// </summary>
        /// <param name="repositoryPath">Absolute path of the repository working directory.</param>
        /// <param name="files">Repository-relative paths that will be committed.</param>
        public static bool Run(string repositoryPath, List<string> files, string commitMessage, string commitBody)
        {
            PreCommitCheckContext context;
            try
            {
                var repoSPath = string.IsNullOrEmpty(repositoryPath) ? SPath.Default : repositoryPath.ToSPath();

                var absolute = new List<string>();
                if (repoSPath.IsInitialized)
                {
                    foreach (var f in files)
                    {
                        try { absolute.Add(repoSPath.Combine(f.ToSPath()).ToString()); }
                        catch { /* ignore malformed paths */ }
                    }
                }

                context = new PreCommitCheckContext
                {
                    RepositoryPath = repositoryPath,
                    CommittedFilesRelative = files,
                    CommittedFilesAbsolute = absolute,
                    CommitMessage = commitMessage,
                    CommitBody = commitBody,
                };
            }
            catch (Exception e)
            {
                Logger.Warning("Failed to build pre-commit context: {0}", e.Message);
                return true; // never block a commit because the gate itself failed to set up
            }

            // Loop so that after the user runs a Fix, we re-run the checks.
            while (true)
            {
                var report = PreCommitCheckRunner.Run(context);

                if (!report.HasAny)
                    return true; // all checks passed

                var overrideBlocks = PreCommitCheckRunner.OverrideBlocks;
                var hasHardBlock = report.HasBlocks && !overrideBlocks;

                var message = BuildMessage(report, overrideBlocks);

                if (hasHardBlock)
                {
                    // Hard block: no "continue". Offer Fix if any failing check provides one.
                    if (report.HasFixes)
                    {
                        var choice = EditorUtility.DisplayDialogComplex(
                            "Commit blocked by pre-commit checks",
                            message,
                            "Fix…", "Cancel", string.Empty);

                        if (choice == 0) // Fix
                        {
                            InvokeFixes(report);
                            continue; // re-run
                        }
                        return false; // Cancel
                    }

                    EditorUtility.DisplayDialog(
                        "Commit blocked by pre-commit checks",
                        message,
                        "OK");
                    return false;
                }

                // Warning-level (or blocks overridden): Continue / Fix / Cancel.
                if (report.HasFixes)
                {
                    var choice = EditorUtility.DisplayDialogComplex(
                        "Pre-commit checks reported issues",
                        message,
                        "Commit anyway", "Cancel", "Fix…");

                    switch (choice)
                    {
                        case 0: return true;              // Commit anyway
                        case 1: return false;             // Cancel
                        default:                          // Fix…
                            InvokeFixes(report);
                            continue;                     // re-run
                    }
                }

                return EditorUtility.DisplayDialog(
                    "Pre-commit checks reported issues",
                    message,
                    "Commit anyway", "Cancel");
            }
        }

        private static void InvokeFixes(PreCommitRunReport report)
        {
            foreach (var fix in report.Fixes)
            {
                try { fix?.Invoke(); }
                catch (Exception e) { UnityEngine.Debug.LogError($"[PreCommit] Fix action failed: {e}"); }
            }
        }

        private static string BuildMessage(PreCommitRunReport report, bool overrideBlocks)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var failure in report.Failures)
            {
                var check = failure.Key;
                var result = failure.Value;
                var tag = result.IsBlock ? (overrideBlocks ? "BLOCK (overridden)" : "BLOCK") : "WARNING";
                sb.AppendLine($"[{tag}] {check.DisplayName}");
                foreach (var issue in result.Issues)
                    sb.AppendLine("   • " + issue);
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
    }
}
