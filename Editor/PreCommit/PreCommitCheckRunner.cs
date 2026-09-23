using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Editor.Tasks.Logging;
using UnityEditor;

namespace Unity.VersionControl.Git.PreCommit
{
    /// <summary>
    /// Aggregated outcome of running every applicable check.
    /// </summary>
    public class PreCommitRunReport
    {
        /// <summary>Per-check results whose severity is Warning or Block (passes are omitted).</summary>
        public List<KeyValuePair<IPreCommitCheck, PreCommitCheckResult>> Failures { get; } =
            new List<KeyValuePair<IPreCommitCheck, PreCommitCheckResult>>();

        public bool HasBlocks => Failures.Any(f => f.Value.IsBlock);
        public bool HasWarnings => Failures.Any(f => f.Value.IsWarning);
        public bool HasAny => Failures.Count > 0;

        /// <summary>All Fix actions offered by failing checks, in check order.</summary>
        public IEnumerable<Action> Fixes => Failures.Where(f => f.Value.HasFix).Select(f => f.Value.Fix);

        public bool HasFixes => Failures.Any(f => f.Value.HasFix);
    }

    /// <summary>
    /// Discovers <see cref="IPreCommitCheck"/> implementations across all loaded Editor assemblies
    /// and runs them before a commit. Auto-discovery means a new check only has to implement the
    /// interface with a public parameterless constructor — nothing has to reference it here.
    /// </summary>
    public static class PreCommitCheckRunner
    {
        /// <summary>
        /// EditorPref that lets a studio bypass hard-block checks (they degrade to warnings). Kept
        /// off by default and surfaced in the Git for Unity Settings tab.
        /// </summary>
        public const string OverrideBlocksPrefKey = "GitForUnity.PreCommit.OverrideBlocks";

        public static bool OverrideBlocks
        {
            get => EditorPrefs.GetBool(OverrideBlocksPrefKey, false);
            set => EditorPrefs.SetBool(OverrideBlocksPrefKey, value);
        }

        private static ILogging Logger { get; } = LogHelper.GetLogger(typeof(PreCommitCheckRunner));

        [NonSerialized] private static List<IPreCommitCheck> cachedChecks;

        /// <summary>
        /// Instantiate every discoverable check. Cached after the first call; call
        /// <see cref="ClearCache"/> after a domain reload if you need a fresh scan.
        /// </summary>
        public static IReadOnlyList<IPreCommitCheck> DiscoverChecks()
        {
            if (cachedChecks != null)
                return cachedChecks;

            var checks = new List<IPreCommitCheck>();
            var interfaceType = typeof(IPreCommitCheck);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Skip obviously irrelevant assemblies for speed and to avoid load exceptions.
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("Unity.") && name.Contains("Editor.Tasks"))
                {
                    // still allow – checks could live anywhere; only truly system libs are skipped
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || type.IsInterface || !interfaceType.IsAssignableFrom(type))
                        continue;

                    // Needs a public parameterless constructor to be auto-instantiated.
                    if (type.GetConstructor(Type.EmptyTypes) == null)
                    {
                        Logger.Warning("Pre-commit check {0} has no public parameterless constructor; skipping.", type.FullName);
                        continue;
                    }

                    try
                    {
                        checks.Add((IPreCommitCheck)Activator.CreateInstance(type));
                    }
                    catch (Exception e)
                    {
                        Logger.Warning("Failed to instantiate pre-commit check {0}: {1}", type.FullName, e.Message);
                    }
                }
            }

            checks.Sort((a, b) =>
            {
                int byOrder = a.Order.CompareTo(b.Order);
                return byOrder != 0 ? byOrder : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            cachedChecks = checks;
            return cachedChecks;
        }

        public static void ClearCache()
        {
            cachedChecks = null;
        }

        /// <summary>
        /// Run all applicable checks against the given context and collect the non-passing results.
        /// Checks that throw are surfaced as a Block so a broken check never silently lets a bad
        /// commit through.
        /// </summary>
        public static PreCommitRunReport Run(PreCommitCheckContext context)
        {
            var report = new PreCommitRunReport();

            foreach (var check in DiscoverChecks())
            {
                bool applies;
                try
                {
                    applies = check.AppliesTo(context);
                }
                catch (Exception e)
                {
                    Logger.Warning("Pre-commit check {0}.AppliesTo threw: {1}", check.DisplayName, e.Message);
                    applies = false;
                }

                if (!applies)
                    continue;

                PreCommitCheckResult result;
                try
                {
                    result = check.Run(context) ?? PreCommitCheckResult.Pass();
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Pre-commit check {0} threw during Run", check.DisplayName);
                    result = PreCommitCheckResult.Block(
                        $"The check '{check.DisplayName}' failed to run: {e.Message}");
                }

                if (!result.IsPass)
                    report.Failures.Add(new KeyValuePair<IPreCommitCheck, PreCommitCheckResult>(check, result));
            }

            return report;
        }
    }
}
