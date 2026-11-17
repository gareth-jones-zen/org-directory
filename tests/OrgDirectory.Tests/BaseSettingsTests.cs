using PyCompat;

namespace OrgDirectory.Tests;

/// <summary>base = "...": settings shared with other tools, from a base file beside the settings.</summary>
public sealed class BaseSettingsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "org-directory-base-" + Guid.NewGuid().ToString("N"));

    private const string Base = """
        login_hint = "base@example.com"
        tenant = "contoso"
        msal_cache = "%SHARED%/msal-cache.json"
        time_zone = "GMT Standard Time"

        [paths]
        SHARED = "%LOCALAPPDATA%/shared"
        EDGE_PEOPLE_FIRST = "%SHARED%/people-first-edge"

        [git]
        repo = "."
        push = true
        trailers = ["Co-Authored-By: someone"]

        [review]
        file = "%SHARED%/pending.json"
        lock = "shared-pending"
        """;

    private const string Own = """
        base = "base-config.toml"
        [people_first]
        enabled = true
        url = "https://example.people-first.com"
        edge_profile = "%EDGE_PEOPLE_FIRST%"
        """;

    public BaseSettingsTests()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "base-config.toml"), Base);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string? Env(string name) => name switch
    {
        "LOCALAPPDATA" => Path.Combine(dir, "local"),
        "APPDATA" => Path.Combine(dir, "roaming"),
        _ => null,
    };

    private Config Load(string text)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, text);
        return Config.Load(path, Env);
    }

    private string Local(string rest) => PyPath.Pure(Path.Combine(dir, "local") + "/" + rest);

    [Fact]
    public void Sign_in_settings_and_paths_come_from_the_base()
    {
        var config = Load(Own);
        Assert.Equal("base@example.com", config.LoginHint);
        Assert.Equal("contoso", config.Tenant);
        Assert.Equal(Local("shared/msal-cache.json"), config.MsalCache);
        Assert.Equal(Local("shared/people-first-edge"), config.PeopleFirst.EdgeProfile);
        Assert.Equal("base@example.com", config.PeopleFirst.Account);   // the account defaults to login_hint
        Assert.Null(config.Git);      // no [git] or [review] here, so the base's don't apply
        Assert.Null(config.Review);
    }

    [Fact]
    public void The_tools_own_settings_win()
    {
        var config = Load("login_hint = \"own@example.com\"\ntenant = \"own\"\nlog_file = \"%SHARED%/own.log\"\n" + Own);
        Assert.Equal("own@example.com", config.LoginHint);
        Assert.Equal("own", config.Tenant);
        Assert.Equal(Local("shared/own.log"), config.LogFile);
    }

    [Fact]
    public void Git_and_review_apply_only_where_the_tool_has_the_section()
    {
        var config = Load(Own + "\n[git]\npush = false\n[review]\nsource = \"org-directory\"\n");
        Assert.Equal(PyPath.Resolve(dir), config.Git!.Repo);
        Assert.False(config.Git.Push);   // the tool's own
        Assert.Equal(["Co-Authored-By: someone"], config.Git.Trailers);
        Assert.Equal(Local("shared/pending.json"), config.Review!.File);
        Assert.Equal("org-directory", config.Review.Source);
        Assert.Equal("shared-pending", config.Review.Lock);
        var gitOnly = Load(Own + "\n[git]\n");
        Assert.True(gitOnly.Git!.Push);
        Assert.Null(gitOnly.Review);
    }

    [Fact]
    public void A_missing_base_is_a_settings_error()
    {
        var error = Assert.Throws<PyValueError>(() => Load("base = \"nowhere.toml\"\n"));
        Assert.Contains("doesn't exist", error.Message);
    }
}
