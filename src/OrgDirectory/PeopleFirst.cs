using System.Text.Json;
using System.Text.Json.Nodes;
using PeopleFirstClient;
using PyCompat;

namespace OrgDirectory;

/// <summary>[people_first]: the switch, and where People First is. Off unless enabled = true.</summary>
public sealed record PeopleFirstSettings(bool Enabled = false, string Url = "", string TenantCode = "", string Account = "",
    string? EdgeProfile = null)
{
    public const string SignInHint = "At a console, run: org-directory people-first sign-in";

    /// <summary>The library's settings (url, tenant code, account to pick, Edge profile, and this tool's hint).</summary>
    public Settings ToClient() => new(Url, TenantCode, Account, EdgeProfile, SignInHint);
}

/// <summary>People First (MHR's HR system), read as you: the phone numbers your role lets you see.
///
/// Optional ([people_first] enabled = true), and needs Edge. The sign-in and the API client are background-tools-lib's
/// PeopleFirstClient (the port of people-first-client). What's org-directory's own:
/// - /hrm/people/{id}/contactinfo holds the numbers. For anyone outside your own reporting line the personal fields
///   come back empty, so it reads your own team only, down your reporting line.
/// - Only phone numbers are taken, matched to the directory by work email. Personal email, addresses and next of kin
///   are left alone.
/// - It holds the Edge profile's lock while Edge is open (the same lock as the Python tools), so another tool on the
///   same profile (team-leave, cover-rota) waits rather than fails. Edge closes as soon as the token is taken.</summary>
public static class PeopleFirst
{
    private static readonly string[] Keys = ["companyMobile", "personalMobile", "personalNumber"];

    /// <summary>The numbers in one contactinfo answer: companyMobile, personalMobile and personalNumber, stripped, with
    /// empty and non-text fields left out. Null if there's no work email or no number.</summary>
    public static (string Email, JsonObject Numbers)? Numbers(JsonElement info)
    {
        var email = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("workEmail", out var work) && work.ValueKind == JsonValueKind.String
            ? Py.Lower(PyStr.Strip(work.GetString()!)) : "";
        var numbers = new JsonObject();
        foreach (var key in Keys)
            if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                && PyStr.Strip(value.GetString()!) is { Length: > 0 } text)
                numbers[key] = text;
        return email.Length > 0 && numbers.Count > 0 ? (email, numbers) : null;
    }

    /// <summary>fetch(): phone numbers for you and everyone down your reporting line, by work email (lower case).</summary>
    public static JsonObject Fetch(PeopleFirstSettings settings, Action<string> log) =>
        FetchAsync(settings, log).GetAwaiter().GetResult();

    public static async Task<JsonObject> FetchAsync(PeopleFirstSettings settings, Action<string> log, CancellationToken cancellationToken = default)
    {
        using var api = await Session.OpenAsync(settings.ToClient(), new SessionOptions { Log = log }, cancellationToken).ConfigureAwait(false);
        var people = await ReportingLine.GetAsync(api, cancellationToken: cancellationToken).ConfigureAwait(false);
        var found = new JsonObject();
        foreach (var person in people)
        {
            var info = await api.ContactAsync(person, cancellationToken).ConfigureAwait(false);
            if (Numbers(info) is { } entry)
                found[entry.Email] = entry.Numbers;
        }
        log($"people first: read {people.Count} people in your reporting line, {found.Count} with a number");
        return found;
    }

    /// <summary>A visible window, for a first sign-in or one that needs a password or MFA.</summary>
    public static void SignInWindow(PeopleFirstSettings settings, Action<string> log, double minutes = 5) =>
        PeopleFirstClient.SignInWindow.RunAsync(settings.ToClient(), new SessionOptions { Log = log, Minutes = minutes })
            .GetAwaiter().GetResult();
}
