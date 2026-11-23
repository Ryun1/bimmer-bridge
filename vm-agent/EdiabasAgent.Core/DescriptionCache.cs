namespace EdiabasAgent.Core;

/// <summary>
/// Job descriptions (<c>_JOBCOMMENTS</c> text) that describe has already read, keyed by the
/// SHA-256 of the SGBD file that answered and the job name. A description comes from the file
/// alone, so the same file content always gives the same text: an entry can never be stale, and a
/// changed file is simply a new key. Without it, every describe of a large SGBD pays for one
/// EDIABAS call per job again, and with no car each call waits out the SGBD's INIT (issue #16).
/// <para>Only describe reads from it; the screen before a run always reads the description live.
/// Only successful reads are stored. In memory only: a restart starts empty. Thread-safe.</para>
/// </summary>
public sealed class DescriptionCache
{
    /// <summary>Above this many entries the cache is emptied and starts again. Far more than the
    /// jobs of every SGBD in a full Ecu folder; it only bounds memory.</summary>
    public const int MaxEntries = 200_000;

    private readonly object _gate = new();
    private readonly Dictionary<(string File, string Job), IReadOnlyList<string>> _entries = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryGet(string fileHash, string job, out IReadOnlyList<string> lines)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue((fileHash, job.ToUpperInvariant()), out var found))
            {
                lines = found;
                return true;
            }
        }

        lines = [];
        return false;
    }

    public void Add(string fileHash, string job, IReadOnlyList<string> lines)
    {
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries)
            {
                _entries.Clear();
            }

            _entries[(fileHash, job.ToUpperInvariant())] = lines.ToArray();
        }
    }
}
