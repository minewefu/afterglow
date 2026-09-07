namespace Afterglow.Core.Tests.Fakes;

/// <summary>
/// Redirects <see cref="AppPaths"/> into a throwaway directory for a test's
/// lifetime, so applied-state records and curve files never touch the real
/// install. Test classes that use it must sit in the "AppPaths" collection,
/// which serialises them (the override is process-wide).
/// </summary>
internal sealed class StoreScope : IDisposable
{
    private readonly string _root;

    public StoreScope()
    {
        _root = Path.Combine(Path.GetTempPath(), $"afterglow-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot = _root;
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
