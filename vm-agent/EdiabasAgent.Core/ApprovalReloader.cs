namespace EdiabasAgent.Core;

/// <summary>The agent's config file, as far as re-reading <c>reviewedJobs</c> needs it.</summary>
public interface IConfigFile
{
    /// <summary>When the file was last written (UTC); null when it is missing or cannot be checked.</summary>
    DateTime? LastWriteUtc();

    /// <summary>The file's text. May throw an I/O exception.</summary>
    string ReadAllText();
}

public sealed class ConfigFile(string path) : IConfigFile
{
    public DateTime? LastWriteUtc()
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public string ReadAllText() => File.ReadAllText(path);
}

/// <summary>
/// Re-reads <c>reviewedJobs</c>, and nothing else, when config.json has changed, so that an
/// approval Ryan adds (or removes) applies without restarting the agent. Called by the runner at
/// the start of every EDIABAS request, before the policy decides anything. A file that is
/// missing, unreadable or not a valid config keeps the previous list, with one warning per
/// change of the file; a later good write is picked up. Runner thread only.
/// </summary>
public sealed class ApprovalReloader(IConfigFile file, ApprovalState state, IEdiabasLog log)
{
    // Unset until the first request: the file is read once then, which also catches a change
    // made between the agent's start-up load and that request.
    private bool _checked;
    private DateTime? _stamp;

    public void Refresh()
    {
        DateTime? stamp;
        try
        {
            stamp = file.LastWriteUtc();
        }
        catch (Exception)
        {
            // An approval check must never fail a request: treated as a file that cannot be checked.
            stamp = null;
        }

        if (_checked && stamp == _stamp)
        {
            return;
        }

        _checked = true;
        _stamp = stamp;
        if (stamp is null)
        {
            log.Warn("config.json is missing or cannot be checked; reviewedJobs keeps its previous list");
            return;
        }

        string text;
        try
        {
            text = file.ReadAllText();
        }
        catch (Exception ex)
        {
            log.Warn($"config.json changed but cannot be read ({ex.GetType().Name}: {ex.Message}); reviewedJobs keeps its previous list");
            return;
        }

        // The whole file must be valid, not only the list: a half-saved file is not trusted.
        var loaded = AgentConfig.Parse(text);
        if (!loaded.Ok)
        {
            log.Warn($"config.json changed but is not valid ({string.Join("; ", loaded.Errors)}); reviewedJobs keeps its previous list");
            return;
        }

        // Once per change of the file: an entry of the wrong shape approves nothing.
        foreach (var warning in loaded.Warnings.Where(w => w.StartsWith("reviewedJobs[", StringComparison.Ordinal)))
        {
            log.Warn(warning);
        }

        var next = new ApprovalList(loaded.Config!.ReviewedJobs);
        if (next.SameAs(state.Current))
        {
            return;
        }

        state.Current = next;
        log.Info($"reviewedJobs re-read: {next.PinnedCount} pinned, {next.UnpinnedCount} unpinned");
    }
}
