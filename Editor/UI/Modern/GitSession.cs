using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Unity.Editor.Tasks;
using UnityEditor;

namespace Unity.VersionControl.Git.UI
{
    [Flags]
    enum GitDataKind
    {
        None = 0,
        Repository = 1 << 0,
        Branch = 1 << 1,
        Tracking = 1 << 2,
        Status = 1 << 3,
        Locks = 1 << 4,
        Log = 1 << 5,
        Branches = 1 << 6,
        User = 1 << 7,
        Progress = 1 << 8,
        Operation = 1 << 9,
        All = ~0,
    }

    /// <summary>
    /// The UI Toolkit window's view of the active repository. Repository events can arrive on any
    /// thread, so handlers only set dirty flags; <see cref="Tick"/> (called from the editor's main
    /// thread) reads a fresh snapshot and raises <see cref="Changed"/>. Also owns long-running
    /// operations (commit, get latest, push…) so the whole window can reflect a single busy state.
    /// </summary>
    class GitSession
    {
        public const string AutoFetchPrefKey = "GitForUnity.Modern.AutoFetch";
        private const string LockOwnerNamesPrefKey = "GitForUnity.Modern.LockOwnerNames";
        private const double AutoFetchIntervalSeconds = 300;
        private const double FirstAutoFetchDelaySeconds = 5;

        private static readonly IReadOnlyList<GitStatusEntry> NoChanges = new GitStatusEntry[0];
        private static readonly IReadOnlyList<GitLock> NoLocks = new GitLock[0];
        private static readonly IReadOnlyList<GitLogEntry> NoLog = new GitLogEntry[0];
        private static readonly IReadOnlyList<GitBranch> NoBranches = new GitBranch[0];

        private IApplicationManager manager;
        private IRepository repository;
        private IUser user;
        private int pending;
        private IProgress lastProgress;
        private double nextAutoFetch = -1;
        private bool localLocksPending;

        // LFS lock owners are GitHub logins, which rarely match the git config name. Locks listed by
        // `git lfs locks --local` were taken on this machine, so their owner names are "you".
        private readonly HashSet<string> myLockOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> myLockPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public event Action<GitDataKind> Changed;

        public IApplicationManager Manager => manager;
        public IRepository Repository => repository;
        public IUser User => user;
        public IGitEnvironment Environment => manager?.Environment;
        public bool HasRepository => repository != null;

        // ---- snapshot, refreshed on the main thread ----
        public string BranchName { get; private set; } = string.Empty;
        public bool IsTracking { get; private set; }
        public GitRemote? Remote { get; private set; }
        public int Ahead { get; private set; }
        public int Behind { get; private set; }
        public IReadOnlyList<GitStatusEntry> Changes { get; private set; } = NoChanges;
        public IReadOnlyList<GitLock> Locks { get; private set; } = NoLocks;
        public IReadOnlyList<GitLogEntry> Log { get; private set; } = NoLog;
        public IReadOnlyList<GitBranch> LocalBranches { get; private set; } = NoBranches;
        public IReadOnlyList<GitBranch> RemoteBranches { get; private set; } = NoBranches;
        public string UserName { get; private set; } = string.Empty;
        public string UserEmail { get; private set; } = string.Empty;

        // ---- operation state ----
        public string OperationLabel { get; private set; }
        public float OperationProgress { get; private set; }
        public string OperationDetail { get; private set; }
        public bool OperationIsBackground { get; private set; }
        public string LastError { get; private set; }
        public DateTime? LastChecked { get; private set; }

        public bool HasRemote => Remote.HasValue && !string.IsNullOrEmpty(Remote.Value.Url);
        public string RemoteName => Remote.HasValue ? Remote.Value.Name : null;
        public string RemoteUrl => Remote.HasValue ? Remote.Value.Url : null;
        public bool IsOperationRunning => OperationLabel != null;
        public bool IsBusy => IsOperationRunning || (manager != null && manager.IsBusy);

        public string RepositoryPath => Environment != null && Environment.RepositoryPath.IsInitialized
            ? Environment.RepositoryPath.ToString() : null;

        public string ProjectPath => Environment != null ? Environment.UnityProjectPath.ToString() : null;

        /// <summary>Call regularly from the main thread.</summary>
        public void Tick()
        {
            IApplicationManager currentManager;
            try
            {
                currentManager = EntryPoint.ApplicationManager;
            }
            catch (Exception)
            {
                return; // still initializing
            }

            var currentRepository = currentManager?.Environment?.Repository;
            if (currentManager != manager || currentRepository != repository)
                Bind(currentManager, currentRepository);

            var flags = (GitDataKind)Interlocked.Exchange(ref pending, 0);
            if (flags != GitDataKind.None)
            {
                ReadSnapshot(flags);
                Changed?.Invoke(flags);
            }

            MaybeAutoFetch();
            MaybeReadLocalLocks();
        }

        private void MaybeReadLocalLocks()
        {
            if (!localLocksPending || repository == null || manager?.GitClient == null)
                return;
            localLocksPending = false;
            List<GitLock> result = null;
            var boundRepository = repository;
            manager.GitClient.ListLocks(true)
                .Then(locks => { result = locks; })
                .FinallyInUI((ok, ex) =>
                {
                    if (!ok || result == null || boundRepository != repository)
                        return;
                    myLockPaths.Clear();
                    var ownersChanged = false;
                    foreach (var l in result)
                    {
                        myLockPaths.Add(Normalize(l.Path.ToString()));
                        if (!string.IsNullOrEmpty(l.Owner.Name) && myLockOwners.Add(l.Owner.Name))
                            ownersChanged = true;
                    }
                    if (ownersChanged)
                        EditorPrefs.SetString(LockOwnerNamesPrefKey, string.Join("|", myLockOwners));
                    MarkDirty(GitDataKind.Locks);
                })
                .Start();
        }

        public void Detach()
        {
            Bind(null, null);
        }

        public void MarkDirty(GitDataKind kind)
        {
            int initial, updated;
            do
            {
                initial = pending;
                updated = initial | (int)kind;
            } while (Interlocked.CompareExchange(ref pending, updated, initial) != initial);
        }

        /// <summary>Asks git for fresh data. Locks and remote state may hit the network.</summary>
        public void Refresh(bool includeNetwork = true)
        {
            if (repository == null)
                return;
            repository.Refresh(CacheType.RepositoryInfo);
            repository.Refresh(CacheType.GitStatus);
            repository.Refresh(CacheType.GitLog);
            repository.Refresh(CacheType.Branches);
            repository.Refresh(CacheType.GitAheadBehind);
            if (includeNetwork)
                repository.Refresh(CacheType.GitLocks);
            user?.CheckAndRaiseEventsIfCacheNewer(CacheType.GitUser, default(CacheUpdateEvent));
        }

        public void RefreshLocks()
        {
            repository?.Refresh(CacheType.GitLocks);
        }

        // ---- lock ownership ----

        public bool IsMine(GitLock gitLock)
        {
            var owner = gitLock.Owner.Name;
            if (string.IsNullOrEmpty(owner))
                return false;
            if (string.Equals(owner, UserName, StringComparison.OrdinalIgnoreCase) || myLockOwners.Contains(owner))
                return true;
            if (myLockPaths.Contains(Normalize(gitLock.Path.ToString())))
                return true;
            if (Remote.HasValue)
            {
                var r = Remote.Value;
                if (!string.IsNullOrEmpty(r.user) && string.Equals(owner, r.user, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(r.login) && string.Equals(owner, r.login, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public GitLock? FindLock(string repositoryRelativePath)
        {
            foreach (var l in Locks)
            {
                if (string.Equals(Normalize(l.Path.ToString()), Normalize(repositoryRelativePath), StringComparison.OrdinalIgnoreCase))
                    return l;
            }
            return null;
        }

        // ---- paths ----

        /// <summary>Maps a repository-relative path to a Unity project path (Assets/…), or null if outside the project.</summary>
        public string ToProjectPath(string repositoryRelativePath)
        {
            var repoPath = RepositoryPath;
            var projectPath = ProjectPath;
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(repositoryRelativePath))
                return null;
            try
            {
                var full = Normalize(System.IO.Path.GetFullPath(System.IO.Path.Combine(repoPath, repositoryRelativePath)));
                var root = Normalize(System.IO.Path.GetFullPath(projectPath)).TrimEnd('/') + "/";
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(root.Length);
            }
            catch (Exception)
            {
            }
            return null;
        }

        public string ToRepositoryPath(string projectRelativePath)
        {
            var repoPath = RepositoryPath;
            var projectPath = ProjectPath;
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(projectPath))
                return null;
            var full = Normalize(System.IO.Path.GetFullPath(System.IO.Path.Combine(projectPath, projectRelativePath)));
            var root = Normalize(System.IO.Path.GetFullPath(repoPath)).TrimEnd('/') + "/";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : null;
        }

        public static string Normalize(string path) => path?.Replace('\\', '/');

        // ---- operations ----

        /// <summary>
        /// Runs a git task as the window's single foreground operation. Returns false (and does
        /// nothing) if another operation is already running.
        /// </summary>
        public bool Run(string label, ITask task, Action<bool, Exception> done = null, bool background = false)
        {
            if (IsOperationRunning || task == null)
                return false;

            OperationLabel = label;
            OperationIsBackground = background;
            OperationProgress = 0f;
            OperationDetail = null;
            if (!background)
                LastError = null;
            MarkDirty(GitDataKind.Operation);

            task.FinallyInUI((success, exception) =>
            {
                OperationLabel = null;
                OperationIsBackground = false;
                OperationDetail = null;
                if (!success)
                    LastError = FriendlyError(exception);
                else if (!background)
                    LastError = null;
                try
                {
                    done?.Invoke(success, exception);
                }
                finally
                {
                    MarkDirty(GitDataKind.Operation | GitDataKind.Tracking | GitDataKind.Status);
                }
            }).Start();
            return true;
        }

        public void ClearError()
        {
            LastError = null;
            MarkDirty(GitDataKind.Operation);
        }

        public void Fetch(bool background, Action<bool, Exception> done = null)
        {
            if (repository == null || !HasRemote)
                return;
            nextAutoFetch = EditorApplication.timeSinceStartup + AutoFetchIntervalSeconds;
            Run("Checking for updates…", repository.Fetch(), (ok, ex) =>
            {
                if (ok)
                {
                    LastChecked = DateTime.Now;
                    LastError = null;
                }
                done?.Invoke(ok, ex);
            }, background);
        }

        public static string FriendlyError(Exception exception)
        {
            if (exception == null)
                return "Something went wrong. Check the Console for details.";
            var message = exception.Message ?? string.Empty;
            var lower = message.ToLowerInvariant();
            if (lower.Contains("could not resolve host") || lower.Contains("unable to access") || lower.Contains("timed out") || lower.Contains("network"))
                return "Can't reach GitHub. Check your connection.";
            if (lower.Contains("authentication failed") || lower.Contains("permission denied") || lower.Contains("403"))
                return "GitHub didn't accept your sign-in.";
            if (lower.Contains("non-fast-forward") || lower.Contains("[rejected]") || lower.Contains("fetch first"))
                return "The team pushed new work first. Get latest, then push again.";
            if (lower.Contains("conflict"))
                return "You and a teammate changed the same files.";
            if (lower.Contains("would be overwritten"))
                return "Commit your changes first, then try again.";
            var firstLine = message.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return string.IsNullOrEmpty(firstLine) ? "Something went wrong. Check the Console for details." : firstLine;
        }

        // ---- binding ----

        private void Bind(IApplicationManager newManager, IRepository newRepository)
        {
            if (manager != null)
                manager.OnProgress -= OnProgress;
            DetachRepository(repository);
            if (user != null)
                user.Changed -= OnUserChanged;

            manager = newManager;
            repository = newRepository;
            user = newManager?.Environment?.User;
            OperationLabel = null;
            LastError = null;
            LastChecked = null;

            if (manager != null)
                manager.OnProgress += OnProgress;
            AttachRepository(repository);
            if (user != null)
            {
                user.Changed += OnUserChanged;
                user.CheckAndRaiseEventsIfCacheNewer(CacheType.GitUser, default(CacheUpdateEvent));
            }

            if (repository != null)
            {
                repository.CheckAndRaiseEventsIfCacheNewer(CacheType.RepositoryInfo, default(CacheUpdateEvent));
                repository.CheckAndRaiseEventsIfCacheNewer(CacheType.GitStatus, default(CacheUpdateEvent));
                repository.CheckAndRaiseEventsIfCacheNewer(CacheType.GitLocks, default(CacheUpdateEvent));
                repository.CheckAndRaiseEventsIfCacheNewer(CacheType.GitLog, default(CacheUpdateEvent));
                repository.CheckAndRaiseEventsIfCacheNewer(CacheType.GitAheadBehind, default(CacheUpdateEvent));
                repository.CheckAndRaiseEventsIfCacheNewer(CacheType.Branches, default(CacheUpdateEvent));
            }

            myLockPaths.Clear();
            myLockOwners.Clear();
            foreach (var name in EditorPrefs.GetString(LockOwnerNamesPrefKey, string.Empty).Split('|'))
                if (!string.IsNullOrEmpty(name))
                    myLockOwners.Add(name);
            localLocksPending = repository != null;

            nextAutoFetch = EditorApplication.timeSinceStartup + FirstAutoFetchDelaySeconds;
            MarkDirty(GitDataKind.All);
        }

        private void AttachRepository(IRepository repo)
        {
            if (repo == null)
                return;
            repo.CurrentBranchAndRemoteChanged += OnBranchChanged;
            repo.TrackingStatusChanged += OnTrackingChanged;
            repo.StatusEntriesChanged += OnStatusChanged;
            repo.LocksChanged += OnLocksChanged;
            repo.LogChanged += OnLogChanged;
            repo.LocalAndRemoteBranchListChanged += OnBranchesChanged;
            repo.OnProgress += OnProgress;
        }

        private void DetachRepository(IRepository repo)
        {
            if (repo == null)
                return;
            repo.CurrentBranchAndRemoteChanged -= OnBranchChanged;
            repo.TrackingStatusChanged -= OnTrackingChanged;
            repo.StatusEntriesChanged -= OnStatusChanged;
            repo.LocksChanged -= OnLocksChanged;
            repo.LogChanged -= OnLogChanged;
            repo.LocalAndRemoteBranchListChanged -= OnBranchesChanged;
            repo.OnProgress -= OnProgress;
        }

        private void OnBranchChanged(CacheUpdateEvent e) => MarkDirty(GitDataKind.Branch | GitDataKind.Tracking);
        private void OnTrackingChanged(CacheUpdateEvent e) => MarkDirty(GitDataKind.Tracking);
        private void OnStatusChanged(CacheUpdateEvent e) => MarkDirty(GitDataKind.Status);
        private void OnLocksChanged(CacheUpdateEvent e)
        {
            localLocksPending = true;
            MarkDirty(GitDataKind.Locks);
        }
        private void OnLogChanged(CacheUpdateEvent e) => MarkDirty(GitDataKind.Log | GitDataKind.Tracking);
        private void OnBranchesChanged(CacheUpdateEvent e) => MarkDirty(GitDataKind.Branches);
        private void OnUserChanged(CacheUpdateEvent e) => MarkDirty(GitDataKind.User);

        private void OnProgress(IProgress progress)
        {
            lastProgress = progress;
            MarkDirty(GitDataKind.Progress);
        }

        private void ReadSnapshot(GitDataKind flags)
        {
            var repo = repository;
            if (repo == null)
            {
                BranchName = string.Empty;
                IsTracking = false;
                Remote = null;
                Ahead = Behind = 0;
                Changes = NoChanges;
                Locks = NoLocks;
                Log = NoLog;
                LocalBranches = RemoteBranches = NoBranches;
                return;
            }

            try
            {
                if ((flags & (GitDataKind.Branch | GitDataKind.Repository)) != 0)
                {
                    var branch = repo.CurrentBranch;
                    BranchName = repo.CurrentBranchName ?? string.Empty;
                    IsTracking = branch.HasValue && !string.IsNullOrEmpty(branch.Value.Tracking);
                    Remote = repo.CurrentRemote;
                }

                if ((flags & GitDataKind.Tracking) != 0)
                {
                    Ahead = repo.CurrentAhead;
                    Behind = repo.CurrentBehind;
                }

                if ((flags & GitDataKind.Status) != 0)
                    Changes = repo.CurrentChanges?.Where(x => x.Status != GitFileStatus.Ignored).ToList() ?? (IReadOnlyList<GitStatusEntry>)NoChanges;

                if ((flags & GitDataKind.Locks) != 0)
                    Locks = repo.CurrentLocks ?? (IReadOnlyList<GitLock>)NoLocks;

                if ((flags & GitDataKind.Log) != 0)
                    Log = repo.CurrentLog ?? (IReadOnlyList<GitLogEntry>)NoLog;

                if ((flags & GitDataKind.Branches) != 0)
                {
                    LocalBranches = repo.LocalBranches?.ToList() ?? (IReadOnlyList<GitBranch>)NoBranches;
                    RemoteBranches = repo.RemoteBranches?.ToList() ?? (IReadOnlyList<GitBranch>)NoBranches;
                }
            }
            catch (Exception)
            {
                // A cache can throw while the repository is being swapped out; the next event re-reads.
            }

            if ((flags & GitDataKind.User) != 0 && user != null)
            {
                UserName = user.Name ?? string.Empty;
                UserEmail = user.Email ?? string.Empty;
            }

            if ((flags & GitDataKind.Progress) != 0 && lastProgress != null && IsOperationRunning)
            {
                OperationProgress = lastProgress.Percentage;
                OperationDetail = lastProgress.Message;
            }
        }

        private void MaybeAutoFetch()
        {
            if (repository == null || !HasRemote || IsBusy || nextAutoFetch < 0)
                return;
            if (EditorApplication.timeSinceStartup < nextAutoFetch)
                return;
            if (!EditorPrefs.GetBool(AutoFetchPrefKey, true))
            {
                nextAutoFetch = EditorApplication.timeSinceStartup + AutoFetchIntervalSeconds;
                return;
            }
            Fetch(true);
        }
    }
}
