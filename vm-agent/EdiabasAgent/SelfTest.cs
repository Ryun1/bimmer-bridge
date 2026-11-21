using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using EdiabasAgent.Core;
using EdiabasAgent.Logging;
using EdiabasAgent.Net;

namespace EdiabasAgent;

/// <summary>
/// <c>--selftest</c>: local checks only (no network traffic, no cable). Prints PASS/FAIL/SKIP per
/// check, writes the same report to <c>logs\selftest.txt</c>, and returns true when nothing failed.
/// </summary>
internal static class SelfTest
{
    public const string FirewallRuleName = "ediabas-bridge agent";

    private enum Outcome
    {
        Pass,
        Fail,
        Skip,
    }

    private sealed record Check(string Name, Outcome Outcome, string Detail);

    // Well-known SIDs that must not have any allow access to the token file.
    private static readonly (string Sid, string Name)[] BroadSids =
    [
        ("S-1-1-0", "Everyone"),
        ("S-1-5-11", "Authenticated Users"),
        ("S-1-5-32-545", "Users"),
        ("S-1-5-4", "INTERACTIVE"),
    ];

    public static bool Run(string configPath, ConfigLoadResult loaded, AgentLog log)
    {
        var checks = new List<Check>();

        // 1. Config
        if (loaded.Ok)
        {
            checks.Add(new Check("config", Outcome.Pass, $"{configPath} loads and validates"));
        }
        else
        {
            checks.Add(new Check("config", Outcome.Fail, $"{configPath}: {string.Join("; ", loaded.Errors)}"));
        }

        var config = loaded.Config;
        BindSelection? selection = null;

        if (config is null)
        {
            checks.Add(new Check("token file", Outcome.Skip, "no valid config"));
            checks.Add(new Check("bind address", Outcome.Skip, "no valid config"));
            checks.Add(new Check("firewall rule", Outcome.Skip, "no valid config"));
            checks.Add(new Check("listener", Outcome.Skip, "no valid config"));
        }
        else
        {
            checks.Add(Guard("token file", () => CheckTokenFile(config.TokenFile)));
            checks.Add(Guard("bind address", () =>
            {
                selection = AdapterProbe.Resolve(config);
                return selection.Available
                    ? new Check("bind address", Outcome.Pass,
                        $"{selection.Address} on '{selection.AdapterName}', gateway {selection.Gateway?.ToString() ?? "(none)"}"
                        + (selection.Warning is null ? "" : $" (warning: {selection.Warning})"))
                    : new Check("bind address", Outcome.Fail, selection.Reason);
            }));
            checks.Add(Guard("firewall rule", () => CheckFirewall(config.Port, selection)));
            checks.Add(Guard("listener", () => CheckListener(config.Port, selection)));
        }

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"EdiabasAgent {AgentContext.Version} selftest, {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        foreach (var c in checks)
        {
            var tag = c.Outcome switch
            {
                Outcome.Pass => "PASS",
                Outcome.Fail => "FAIL",
                _ => "SKIP",
            };
            report.AppendLine(CultureInfo.InvariantCulture, $"{tag}  {c.Name}: {c.Detail}");
        }

        var ok = checks.All(c => c.Outcome != Outcome.Fail);
        report.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");

        var text = report.ToString();
        try
        {
            Console.Write(text);
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // No console attached; the file below still has the report.
        }

        try
        {
            Directory.CreateDirectory(log.Directory);
            File.WriteAllText(Path.Combine(log.Directory, "selftest.txt"), text, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"could not write selftest.txt: {ex.GetType().Name}: {ex.Message}");
        }

        log.Info($"selftest {(ok ? "passed" : "failed")}");
        return ok;
    }

    private static Check Guard(string name, Func<Check> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex)
        {
            return new Check(name, Outcome.Fail, $"check threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Check CheckTokenFile(string path)
    {
        const string name = "token file";
        if (!File.Exists(path))
        {
            return new Check(name, Outcome.Fail, $"{path} does not exist");
        }

        var parsed = TokenAuth.ParseTokenFile(File.ReadAllText(path));
        if (!parsed.Ok)
        {
            return new Check(name, Outcome.Fail, $"{path}: {parsed.Error}");
        }

        var acl = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var offenders = new List<string>();
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.FileSystemRights == 0)
            {
                continue;
            }

            var sid = rule.IdentityReference.Value;
            foreach (var (broadSid, broadName) in BroadSids)
            {
                if (string.Equals(sid, broadSid, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{broadName} ({rule.FileSystemRights})");
                }
            }
        }

        return offenders.Count == 0
            ? new Check(name, Outcome.Pass, $"{path} exists, token length ok, ACL grants nothing to Users/Everyone/Authenticated Users")
            : new Check(name, Outcome.Fail, $"{path} ACL grants access to: {string.Join(", ", offenders)}");
    }

    private static Check CheckFirewall(int port, BindSelection? selection)
    {
        const string name = "firewall rule";
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
        if (policyType is null)
        {
            return new Check(name, Outcome.Fail, "Windows Firewall COM API (HNetCfg.FwPolicy2) not available");
        }

        dynamic policy = Activator.CreateInstance(policyType)!;
        var matches = new List<dynamic>();
        foreach (dynamic rule in (IEnumerable)policy.Rules)
        {
            // INetFwRule.Name is the rule's display name (DisplayName in New-NetFirewallRule).
            if (string.Equals((string?)rule.Name, FirewallRuleName, StringComparison.Ordinal))
            {
                matches.Add(rule);
            }
        }

        if (matches.Count == 0)
        {
            return new Check(name, Outcome.Fail, $"no rule named '{FirewallRuleName}'");
        }

        if (matches.Count > 1)
        {
            return new Check(name, Outcome.Fail, $"{matches.Count} rules named '{FirewallRuleName}'; expected exactly one");
        }

        var r = matches[0];
        var problems = new List<string>();

        const int NET_FW_RULE_DIR_IN = 1;
        const int NET_FW_IP_PROTOCOL_TCP = 6;
        const int NET_FW_ACTION_ALLOW = 1;

        if (!(bool)r.Enabled)
        {
            problems.Add("disabled");
        }

        if ((int)r.Direction != NET_FW_RULE_DIR_IN)
        {
            problems.Add("not inbound");
        }

        if ((int)r.Protocol != NET_FW_IP_PROTOCOL_TCP)
        {
            problems.Add($"protocol {(int)r.Protocol}, expected TCP");
        }

        if ((int)r.Action != NET_FW_ACTION_ALLOW)
        {
            problems.Add("action is not allow");
        }

        var localPorts = ((string?)r.LocalPorts ?? "").Trim();
        if (localPorts != port.ToString(CultureInfo.InvariantCulture))
        {
            problems.Add($"local port '{localPorts}', expected {port}");
        }

        var remote = ((string?)r.RemoteAddresses ?? "").Trim();
        if (selection?.Gateway is null)
        {
            problems.Add($"remote address '{remote}' cannot be compared: no gateway resolved");
        }
        else if (!FirewallAddress.IsExactly(remote, selection.Gateway))
        {
            problems.Add($"remote address '{remote}', expected exactly {selection.Gateway}");
        }

        var program = (string?)r.ApplicationName ?? "(any program)";
        return problems.Count == 0
            ? new Check(name, Outcome.Pass, $"enabled, inbound, TCP {port}, remote '{remote}' (= {selection?.Gateway}), program {program}")
            : new Check(name, Outcome.Fail, string.Join("; ", problems));
    }

    private static Check CheckListener(int port, BindSelection? selection)
    {
        const string name = "listener";
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(e => e.Port == port)
            .ToList();
        var running = IsAgentRunning();

        if (!running)
        {
            return listeners.Count == 0
                ? new Check(name, Outcome.Skip, "agent is not running")
                : new Check(name, Outcome.Fail, $"agent is not running but port {port} is in use by: {string.Join(", ", listeners)}");
        }

        if (listeners.Count != 1)
        {
            return new Check(name, Outcome.Fail,
                $"agent is running; expected exactly one listener on port {port}, found {listeners.Count}: [{string.Join(", ", listeners)}]");
        }

        var ep = listeners[0];
        if (ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any))
        {
            return new Check(name, Outcome.Fail, $"listening on wildcard {ep}");
        }

        if (selection?.Address is null || !ep.Address.Equals(selection.Address))
        {
            return new Check(name, Outcome.Fail, $"listening on {ep}, expected {selection?.Address?.ToString() ?? "(unresolved)"}:{port}");
        }

        return new Check(name, Outcome.Pass, $"exactly one listener, on {ep}");
    }

    private static bool IsAgentRunning()
    {
        var self = Environment.ProcessId;
        var others = Process.GetProcessesByName("EdiabasAgent");
        try
        {
            return others.Any(p => p.Id != self);
        }
        finally
        {
            foreach (var p in others)
            {
                p.Dispose();
            }
        }
    }
}
