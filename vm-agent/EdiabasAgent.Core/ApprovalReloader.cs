namespace EdiabasAgent.Core;

/// <summary>The agent's config file, as far as re-reading <c>reviewedJobs</c> needs it.</summary>
public interface IConfigFile
{
    /// <summary>The file's text. Throws when the file is missing or cannot be read.</summary>
    string ReadAllText();
}

public sealed class ConfigFile(string path) : IConfigFile
{
    public string ReadAllText() => File.ReadAllText(path);
}

/// <summary>
/// Re-reads <c>reviewedJobs</c>, and nothing else, from config.json at the start of every EDIABAS
/// request, before the policy decides anything, so that an approval the user adds or removes
/// applies without restarting the agent. The file is read every time: a failed read cannot
/// hide a later removal, because nothing is remembered about the file except what was warned.
/// A file that is missing, unreadable or not a valid config keeps the previous list, with one
/// warning per distinct problem; a later good read is picked up. Runner thread only.
/// </summary>
public sealed class ApprovalReloader(IConfigFile file, ApprovalState state, IEdiabasLog log)
{
    // The problem last warned about (read failure or invalid file); cleared by a good parse.
    private string? _lastProblem;

    // The file text whose bad reviewedJobs entries were last warned about.
    private string? _entryWarnedText;

    public void Refresh()
    {
        string text;
        try
        {
            text = file.ReadAllText();
        }
        catch (Exception ex)
        {
            // An approval check must never fail a request: the previous list stays.
            Problem($"config.json is missing or cannot be read ({ex.GetType().Name}: {ex.Message}); reviewedJobs keeps its previous list");
            return;
        }

        // The whole file must be valid, not only the list: a half-saved file is not trusted.
        var loaded = AgentConfig.Parse(text);
        if (!loaded.Ok)
        {
            Problem($"config.json is not valid ({string.Join("; ", loaded.Errors)}); reviewedJobs keeps its previous list");
            return;
        }

        _lastProblem = null;

        // Once per distinct file text: an entry of the wrong shape approves nothing.
        if (text != _entryWarnedText)
        {
            _entryWarnedText = text;
            foreach (var warning in loaded.Warnings.Where(w => w.StartsWith("reviewedJobs[", StringComparison.Ordinal)))
            {
                log.Warn(warning);
            }
        }

        var next = new ApprovalList(loaded.Config!.ReviewedJobs);
        if (next.SameAs(state.Current))
        {
            return;
        }

        state.Current = next;
        log.Info($"reviewedJobs re-read: {next.PinnedCount} pinned, {next.UnpinnedCount} unpinned");
    }

    private void Problem(string message)
    {
        if (message != _lastProblem)
        {
            _lastProblem = message;
            log.Warn(message);
        }
    }
}
