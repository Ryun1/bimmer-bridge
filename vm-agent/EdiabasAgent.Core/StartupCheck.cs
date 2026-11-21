namespace EdiabasAgent.Core;

/// <summary>What the agent has at startup: ready to bind, or the one reason it is waiting.</summary>
public sealed record StartupState(bool Ready, AgentConfig? Config, string? Token, string? Problem);

/// <summary>
/// Pure decision whether the agent can start: a valid config and a valid token file. The agent
/// never exits for these; it waits, re-reads both and asks again. <see cref="StartupState.Problem"/>
/// never contains token material.
/// </summary>
public static class StartupCheck
{
    /// <param name="config">Result of loading the config file.</param>
    /// <param name="tokenFileContent">Content of the token file, or null if it could not be read.</param>
    /// <param name="tokenReadError">Why the token file could not be read (e.g. the exception type), else null.</param>
    public static StartupState Evaluate(ConfigLoadResult config, string? tokenFileContent, string? tokenReadError)
    {
        if (!config.Ok)
        {
            return new StartupState(false, null, null, "invalid configuration: " + string.Join("; ", config.Errors));
        }

        var cfg = config.Config!;
        if (tokenReadError is not null)
        {
            return new StartupState(false, cfg, null, $"cannot read token file {cfg.TokenFile}: {tokenReadError}");
        }

        var token = TokenAuth.ParseTokenFile(tokenFileContent);
        if (!token.Ok)
        {
            return new StartupState(false, cfg, null, $"token file {cfg.TokenFile}: {token.Error}");
        }

        return new StartupState(true, cfg, token.Token, null);
    }
}

/// <summary>Says whether a reason should be logged: only when it differs from the previous one.</summary>
public sealed class RepeatSuppressor
{
    private string? _last;

    public bool ShouldReport(string? reason)
    {
        if (reason is null || reason == _last)
        {
            return false;
        }

        _last = reason;
        return true;
    }

    public void Reset() => _last = null;
}
