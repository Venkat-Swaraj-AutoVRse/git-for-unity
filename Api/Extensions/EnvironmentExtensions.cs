using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Editor.Tasks;

namespace Unity.VersionControl.Git
{
    using IO;

    public static class EnvironmentExtensions
    {
        /// <summary>
        /// Converts a Unity project path (Assets/…) to a path relative to the repository.
        /// The repository can be the project itself, a folder above it, or a folder inside it
        /// (a nested repository under Assets/ or Packages/). Throws if the path is outside the
        /// repository, which can only happen for a nested repository; use
        /// <see cref="TryRelativeToRepository"/> when that is expected.
        /// </summary>
        public static SPath RelativeToRepository(this SPath path, IGitEnvironment environment)
        {
            if (!TryRelativeToRepository(path, environment, out var result))
                throw new InvalidOperationException($"Path:\"{path}\" is outside RepositoryPath:\"{environment.RepositoryPath}\"");
            return result;
        }

        /// <summary>
        /// Like <see cref="RelativeToRepository"/>, but returns false instead of throwing when the
        /// path is outside the repository (e.g. an asset that isn't part of a nested repository).
        /// </summary>
        public static bool TryRelativeToRepository(this SPath path, IGitEnvironment environment, out SPath result)
        {
            path.ThrowIfNotInitialized();
            Guard.ArgumentNotNull(environment, nameof(environment));

            result = SPath.Default;
            var projectPath = environment.UnityProjectPath.ToSPath();
            var repositoryPath = environment.RepositoryPath;

            if (projectPath == repositoryPath)
            {
                result = path;
                return true;
            }

            if (repositoryPath.IsChildOf(projectPath))
            {
                // Nested repository: only paths inside it have a repository-relative form.
                var fullPath = projectPath.Combine(path);
                if (!fullPath.IsChildOf(repositoryPath))
                    return false;
                result = fullPath.RelativeTo(repositoryPath);
                return true;
            }

            result = projectPath.RelativeTo(repositoryPath).Combine(path);
            return true;
        }

        /// <summary>
        /// Converts a repository-relative path to a Unity project path (Assets/…). Works whether the
        /// repository is the project, above it, or nested inside it. For a repository above the
        /// project, files outside the project come back as ../ paths, as before.
        /// </summary>
        public static SPath RelativeToProject(this SPath path, IGitEnvironment environment)
        {
            path.ThrowIfNotInitialized();
            Guard.ArgumentNotNull(environment, nameof(environment));

            var projectPath = environment.UnityProjectPath.ToSPath();
            var repositoryPath = environment.RepositoryPath;
            if (projectPath == repositoryPath)
            {
                return path;
            }

            return repositoryPath.Combine(path).MakeAbsolute().RelativeTo(projectPath);
        }

        /// <summary>
        /// Like <see cref="RelativeToProject"/>, but returns false when the file is outside the Unity
        /// project (possible when the repository is a folder above the project).
        /// </summary>
        public static bool TryRelativeToProject(this SPath path, IGitEnvironment environment, out SPath result)
        {
            path.ThrowIfNotInitialized();
            Guard.ArgumentNotNull(environment, nameof(environment));

            result = SPath.Default;
            var projectPath = environment.UnityProjectPath.ToSPath();
            var repositoryPath = environment.RepositoryPath;
            if (projectPath == repositoryPath)
            {
                result = path;
                return true;
            }

            var fullPath = repositoryPath.Combine(path).MakeAbsolute();
            if (!fullPath.IsChildOf(projectPath))
                return false;
            result = fullPath.RelativeTo(projectPath);
            return true;
        }

        public static IEnumerable<SPath> ToSPathList(this string envPath, IEnvironment environment)
        {
            return envPath
                    .Split(Path.PathSeparator)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Select(x => environment.ExpandEnvironmentVariables(x.Trim('"', '\'')))
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Select(x => x.ToSPath());
        }
    }
}
