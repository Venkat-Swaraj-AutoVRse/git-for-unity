using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Unity.VersionControl.Git
{
    /// <summary>
    /// Scans the Unity project tree for git repositories, including ones nested inside the
    /// project (e.g. a repo checked out under Assets/ or Packages/). Git for Unity normally
    /// only surfaces the repository found by walking up from the project root; this lets the
    /// UI present every repo it can find so the user can pick which one to operate on.
    /// </summary>
    public struct RepositoryEntry
    {
        /// <summary>Absolute path to the repository working directory (the folder containing .git).</summary>
        public string Path;
        /// <summary>A short label suitable for a dropdown, relative to the project root.</summary>
        public string DisplayName;

        public override string ToString() => DisplayName;
    }

    public static class RepositorySelector
    {
        // Directories that never contain user repositories we care about and are expensive to walk.
        private static readonly HashSet<string> IgnoredDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Library", "Temp", "Logs", "obj", "Build", "Builds", ".vs", ".idea",
            "node_modules", "UserSettings", "MemoryCaptures", "Recordings"
        };

        // Guard against pathological trees / symlink loops.
        private const int MaxDepth = 12;

        /// <summary>
        /// Find all git repositories at or below <paramref name="projectRoot"/>.
        /// The project root repo (if any) is returned first, then nested repos sorted by path.
        /// </summary>
        public static List<RepositoryEntry> Scan(string projectRoot)
        {
            var results = new List<RepositoryEntry>();
            if (string.IsNullOrEmpty(projectRoot) || !Directory.Exists(projectRoot))
                return results;

            projectRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var found = new List<string>();
            CollectRepositories(projectRoot, projectRoot, 0, found);

            foreach (var repoDir in found)
            {
                results.Add(new RepositoryEntry
                {
                    Path = repoDir,
                    DisplayName = MakeDisplayName(projectRoot, repoDir)
                });
            }

            // Root repo first, then the rest alphabetically by display name.
            results.Sort((a, b) =>
            {
                bool aRoot = string.Equals(a.Path, projectRoot, StringComparison.OrdinalIgnoreCase);
                bool bRoot = string.Equals(b.Path, projectRoot, StringComparison.OrdinalIgnoreCase);
                if (aRoot != bRoot)
                    return aRoot ? -1 : 1;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            return results;
        }

        private static void CollectRepositories(string projectRoot, string current, int depth, List<string> found)
        {
            if (depth > MaxDepth)
                return;

            // A folder containing a ".git" entry (folder or file, for submodules/worktrees) is a repo root.
            var gitPath = Path.Combine(current, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                found.Add(current);
                // Do not descend into a repo's own history; but DO keep scanning its
                // subfolders for further nested repos, since those are what we're after.
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var dir in subDirs)
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || name == ".git")
                    continue;
                if (IgnoredDirectoryNames.Contains(name))
                    continue;

                CollectRepositories(projectRoot, dir, depth + 1, found);
            }
        }

        private static string MakeDisplayName(string projectRoot, string repoDir)
        {
            if (string.Equals(repoDir, projectRoot, StringComparison.OrdinalIgnoreCase))
                return "<project root>";

            if (repoDir.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                var rel = repoDir.Substring(projectRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return rel.Replace(Path.DirectorySeparatorChar, '/');
            }

            return repoDir;
        }
    }
}
