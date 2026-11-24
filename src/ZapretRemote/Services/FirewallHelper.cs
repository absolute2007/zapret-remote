using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace ZapretRemote.Services;

public enum FirewallRuleStatus
{
    Allowed,
    MissingOrPortMismatch,
    BlockedByRule
}

/// <summary>
/// Non-admin Windows Firewall status inspection via registry,
/// with optional one-time elevated (UAC) rule creation/removal.
/// </summary>
public static class FirewallHelper
{
    public const string RuleName = "ZapretRemote (Xray LAN)";
    private const string FirewallRulesKeyPath = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    public static FirewallRuleStatus CheckRuleStatus(string xrayExePath, IReadOnlyCollection<int> requiredPorts)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(FirewallRulesKeyPath, writable: false);
            if (key is null)
                return FirewallRuleStatus.MissingOrPortMismatch;

            var normalizedExe = Path.GetFullPath(xrayExePath);
            var uncoveredPorts = new HashSet<int>(requiredPorts);

            foreach (var valueName in key.GetValueNames())
            {
                if (key.GetValue(valueName) is not string ruleSpec)
                    continue;

                if (!ruleSpec.Contains("Active=TRUE", StringComparison.OrdinalIgnoreCase) ||
                    !ruleSpec.Contains("Dir=In", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var appValue = ExtractField(ruleSpec, "App");
                var matchesExe = !string.IsNullOrEmpty(appValue) &&
                    string.Equals(ExpandEnvAndNormalize(appValue), normalizedExe, StringComparison.OrdinalIgnoreCase);

                if (matchesExe && ruleSpec.Contains("Action=Block", StringComparison.OrdinalIgnoreCase))
                    return FirewallRuleStatus.BlockedByRule;

                if (!ruleSpec.Contains("Action=Allow", StringComparison.OrdinalIgnoreCase))
                    continue;

                var nameValue = ExtractField(ruleSpec, "Name");
                var matchesRuleName = string.Equals(nameValue, RuleName, StringComparison.OrdinalIgnoreCase);

                if (!matchesRuleName && !matchesExe)
                    continue;

                var proto = ExtractField(ruleSpec, "Protocol");
                if (!string.IsNullOrEmpty(proto) && proto != "6")
                    continue;

                var lport = ExtractField(ruleSpec, "LPort");
                if (string.IsNullOrEmpty(lport))
                {
                    uncoveredPorts.Clear();
                }
                else
                {
                    uncoveredPorts.RemoveWhere(p => PortMatches(lport, p));
                }
            }

            return uncoveredPorts.Count == 0
                ? FirewallRuleStatus.Allowed
                : FirewallRuleStatus.MissingOrPortMismatch;
        }
        catch
        {
            return FirewallRuleStatus.MissingOrPortMismatch;
        }
    }

    public static bool AllowInFirewall(string xrayExePath, IReadOnlyCollection<int> ports, out string? errorMessage)
    {
        errorMessage = null;
        var fullExe = Path.GetFullPath(xrayExePath);
        var portList = string.Join(",", ports.Distinct().OrderBy(p => p));
        if (string.IsNullOrEmpty(portList))
            portList = "10808";

        var cmdArgs =
            $"/c netsh advfirewall firewall delete rule name=\"{RuleName}\" >nul 2>&1" +
            $" & netsh advfirewall firewall delete rule name=all dir=in action=block program=\"{fullExe}\" >nul 2>&1" +
            $" & netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={portList} program=\"{fullExe}\" remoteip=localsubnet,192.168.0.0/16,10.0.0.0/8,172.16.0.0/12 profile=any";

        return RunElevated("cmd.exe", cmdArgs, out errorMessage);
    }

    public static bool RemoveFromFirewall(out string? errorMessage)
    {
        var args = $"advfirewall firewall delete rule name=\"{RuleName}\"";
        return RunElevated("netsh.exe", args, out errorMessage);
    }

    internal static bool RunElevated(
        string fileName,
        string arguments,
        out string? errorMessage,
        int timeoutMs = 15000,
        string contextName = "команды администратора")
    {
        errorMessage = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                errorMessage = $"Не удалось запустить процесс для {contextName}.";
                return false;
            }

            if (!proc.WaitForExit(timeoutMs))
            {
                errorMessage = $"Превышено время ожидания {contextName}.";
                return false;
            }

            if (proc.ExitCode != 0)
            {
                errorMessage = $"Выполнение {contextName} завершилось с кодом {proc.ExitCode}.";
                return false;
            }

            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            errorMessage = "Запрос прав администратора отменён пользователем.";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string? ExtractField(string ruleSpec, string fieldName)
    {
        var prefix = fieldName + "=";
        foreach (var part in ruleSpec.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return part[prefix.Length..];
        }
        return null;
    }

    private static string ExpandEnvAndNormalize(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return path;
        }
    }

    private static bool PortMatches(string lportSpec, int targetPort)
    {
        var targetStr = targetPort.ToString();
        foreach (var token in lportSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(token, targetStr, StringComparison.Ordinal))
                return true;

            var dashIdx = token.IndexOf('-');
            if (dashIdx > 0 &&
                int.TryParse(token[..dashIdx], out var start) &&
                int.TryParse(token[(dashIdx + 1)..], out var end) &&
                targetPort >= start && targetPort <= end)
            {
                return true;
            }
        }
        return false;
    }
}
