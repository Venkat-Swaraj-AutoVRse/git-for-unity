namespace Unity.VersionControl.Git
{
    using IO;

    static class AssetPathExtensions
    {
        /// <summary>
        /// Repository-relative path of a Unity asset path, or false when there is no asset path
        /// (e.g. a scene object) or the asset is outside the selected repository.
        /// </summary>
        public static bool TryRelativeToRepository(this string assetPath, IGitEnvironment environment, out SPath repositoryPath)
        {
            repositoryPath = SPath.Default;
            return !string.IsNullOrEmpty(assetPath) && assetPath.ToSPath().TryRelativeToRepository(environment, out repositoryPath);
        }
    }
}
