using System.Text.Json.Nodes;
using M365Graph;

namespace OrgDirectory;

/// <summary>The Graph calls this tool makes, so tests can give it recorded replies instead.</summary>
public interface IGraph
{
    /// <summary>Every item across @odata.nextLink pages.</summary>
    IEnumerable<JsonNode?> Pages(string url, IReadOnlyList<string> scopes);

    /// <summary>Make sure there's a token for the tool's scopes (sign-in).</summary>
    void Token();
}

/// <summary>Graph through M365Graph (background-tools-lib): the shared token cache, no browser when nobody's there,
/// retries, IPv4 first. Synchronous, as the Python tool was: one call at a time.</summary>
public sealed class M365GraphClient(GraphClient client) : IGraph
{
    public const string SignInHint = "At a console, run: org-directory sign-in";

    /// <summary>client(config, interactive): the settings' sign-in, and a browser only when interactive says so
    /// (null: only if someone is at the console).</summary>
    public static M365GraphClient For(Config config, bool? interactive, Action<string>? log = null)
    {
        var options = new GraphClientOptions
        {
            Tenant = config.Tenant,
            LoginHint = config.LoginHint,
            Scopes = Org.Scopes,
            Interactive = interactive,
            SignInHint = SignInHint,
            Log = log,
        };
        if (config.MsalCache is not null)
            options = options with { CachePath = config.MsalCache };
        return new M365GraphClient(new GraphClient(options));
    }

    public IEnumerable<JsonNode?> Pages(string url, IReadOnlyList<string> scopes) =>
        client.PagesAsync(url, scopes).ToBlockingEnumerable();

    public void Token() => client.TokenAsync().GetAwaiter().GetResult();
}
