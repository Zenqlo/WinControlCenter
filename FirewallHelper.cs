using System.Diagnostics;
using WindowsFirewallHelper;
using WindowsFirewallHelper.Addresses;
using WindowsFirewallHelper.FirewallRules;

namespace WinControlCenter
{
    public static class FirewallHelper
    {
        private const string RulePrefix = "WinControlCenter";

        public static void EnsureFirewallRule(int port)
        {
            try
            {
                var appPath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(appPath))
                {
                    Logger.Log("Failed to get application path");
                    return;
                }

                var ruleName = RulePrefix;
                
                try
                {
                    // Get all rules
                    var firewall = FirewallWAS.Instance;
                    
                    // Remove existing rules with our prefix
                    var existingRules = firewall.Rules.Where(r => r.Name.StartsWith(ruleName, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var existingRule in existingRules)
                    {
                        Logger.Log($"Removing existing rule: {existingRule.Name}");
                        firewall.Rules.Remove(existingRule);
                    }

                    // Create new rule
                    var rule = new FirewallWASRuleWin8(
                        ruleName,
                        ruleName,  // Program name/identifier
                        FirewallAction.Allow,
                        FirewallDirection.Inbound,
                        FirewallProfiles.Domain | FirewallProfiles.Private | FirewallProfiles.Public
                    )
                    {
                        // Configure the rule
                        ApplicationName = appPath,
                        Protocol = FirewallProtocol.TCP,
                        LocalPorts = [(ushort)port],
                        RemoteAddresses = new[] { new LocalSubnet() },
                        Description = $"Allow inbound traffic for WinControlCenter port {port}"
                    };                    

                    // Add the rule
                    Logger.Log($"Adding new firewall rule: {ruleName} for port {port}");
                    firewall.Rules.Add(rule);

                    Logger.Log($"Firewall rule '{ruleName}' added successfully");
                }
                catch (UnauthorizedAccessException uaEx)
                {
                    Logger.Log($"Access denied when managing firewall rules. Make sure the application is running with administrative privileges. Error: {uaEx.Message}");
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error managing firewall rule: {ex}");
                throw;
            }
        }
    }
} 