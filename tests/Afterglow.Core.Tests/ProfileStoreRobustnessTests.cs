using Afterglow.Core.Profiles;

namespace Afterglow.Core.Tests;

/// <summary>
/// F-36: an unwritable/unreadable profile store must degrade, not throw out of
/// the store. The MCP server has no outer handler, so a throw here used to
/// terminate the whole stdio process. ProfileStore.Save still raises
/// UnauthorizedAccessException (the type the MCP dispatch filter was missing);
/// the read and construction paths no longer throw at all.
/// </summary>
public sealed class ProfileStoreRobustnessTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "afterglow-f36-" + Guid.NewGuid().ToString("N"));

    private static TuningProfile Sample(string name) => new()
    {
        Name = name,
        CoreOffsetMHz = 100,
        MemOffsetMHz = 200,
    };

    [Fact]
    public void Constructing_over_an_uncreatable_directory_does_not_throw()
    {
        // A file sitting where the store wants a directory: CreateDirectory fails.
        Directory.CreateDirectory(_scratch);
        string asFile = Path.Combine(_scratch, "store");
        File.WriteAllText(asFile, "not a directory");

        var store = new ProfileStore(asFile);          // must not throw
        var loaded = store.LoadAll();                   // must not throw

        Assert.Empty(loaded);
        Assert.NotEmpty(store.LastLoadErrors);          // the failure is reported, not swallowed silently
    }

    [Fact]
    public void LoadAll_on_a_missing_directory_returns_empty_without_throwing()
    {
        var store = new ProfileStore(Path.Combine(_scratch, "never-created"));
        Directory.Delete(Path.Combine(_scratch, "never-created"), recursive: true);

        var loaded = store.LoadAll();

        Assert.Empty(loaded);
    }

    [Fact]
    public void Save_over_a_readonly_profile_throws_UnauthorizedAccessException_not_IOException()
    {
        string dir = Path.Combine(_scratch, "ro");
        var store = new ProfileStore(dir);
        store.Save(Sample("daily"));

        string file = store.PathFor("daily");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            // The exact type matters: the MCP dispatch filter caught IOException
            // but not UnauthorizedAccessException, so this is what used to escape.
            var ex = Record.Exception(() => store.Save(Sample("daily")));
            Assert.IsType<UnauthorizedAccessException>(ex);
            Assert.IsNotType<IOException>(ex);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    // R-24: JSON null passes straight through System.Text.Json into the
    // profile's non-nullable collections, and validation then dereferenced
    // them: a NullReferenceException out of LoadAll, which ProfilesViewModel
    // calls while the main window is still being built (and which sits behind
    // the MCP server's list_profiles). One hand-edited or half-written file
    // must cost that file, not the app.
    [Theory]
    [InlineData("""{"name":"bad","fanMode":"Curve","fanCurve":{"points":null}}""")]
    [InlineData("""{"name":"bad","fanMode":"Curve","fanCurve":{"points":[null,{"tempC":90,"dutyPct":100}]}}""")]
    [InlineData("""{"name":"bad","fanMode":"Fixed","fanCurve":{"points":null}}""")]
    [InlineData("""{"name":"bad","certifications":null}""")]
    [InlineData("""{"name":"bad","certifications":[null]}""")]
    public void A_profile_with_null_collections_is_isolated_and_reported(string json)
    {
        string dir = Path.Combine(_scratch, "nulls");
        var store = new ProfileStore(dir);
        store.Save(Sample("good"));
        string bad = Path.Combine(dir, "bad.json");
        File.WriteAllText(bad, json);

        var loaded = store.LoadAll();                   // must not throw

        Assert.Equal("good", Assert.Single(loaded).Name);
        var (file, error) = Assert.Single(store.LastLoadErrors);
        Assert.Equal(bad, file);
        Assert.False(string.IsNullOrWhiteSpace(error));

        // Not handed out by name either (startup profile, game rules,
        // automation, MCP apply_profile, the certifier): those collections
        // would only throw later, in the apply or the certification stamp.
        Assert.Null(store.Load("bad"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                foreach (string f in Directory.EnumerateFiles(_scratch, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
