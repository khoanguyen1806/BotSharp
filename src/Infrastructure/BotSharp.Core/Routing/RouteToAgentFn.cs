using BotSharp.Abstraction.Functions;
using BotSharp.Abstraction.Repositories;
using BotSharp.Abstraction.Routing.Models;

namespace BotSharp.Core.Routing;

/// <summary>
/// Router calls this function to set the Active Agent according to the context
/// </summary>
public class RouteToAgentFn : IFunctionCallback
{
    public string Name => "route_to_agent";
    private readonly IServiceProvider _services;

    public RouteToAgentFn(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<bool> Execute(RoleDialogModel message)
    {
        var args = JsonSerializer.Deserialize<RoutingArgs>(message.FunctionArgs);

        if (string.IsNullOrEmpty(args.AgentName))
        {
            message.ExecutionResult = $"missing agent name";
        }
        else
        {
            var missingfield = HasMissingRequiredField(message, out var agentId);
            if (missingfield && message.CurrentAgentId != agentId)
            {
                message.CurrentAgentId = agentId;
            }
            else
            {
                var db = _services.GetRequiredService<IBotSharpRepository>();
                var record = db.Agents.FirstOrDefault(x => x.Name.ToLower() == args.AgentName.ToLower());
                if (record != null)
                {
                    message.CurrentAgentId = record.Id;
                    message.ExecutionResult = $"Routing to {args.AgentName}";
                }
            }
        }

        // Set default execution data
        message.ExecutionData = JsonSerializer.Deserialize<JsonElement>(message.FunctionArgs);
        return true;
    }

    /// <summary>
    /// If the target agent needs some required fields but the
    /// </summary>
    /// <returns></returns>
    private bool HasMissingRequiredField(RoleDialogModel message, out string agentId)
    {
        var args = JsonSerializer.Deserialize<RoutingArgs>(message.FunctionArgs);
        var router = _services.GetRequiredService<IAgentRouting>();

        var routingRules = router.GetRulesByName(args.AgentName);

        if (routingRules == null || !routingRules.Any())
        {
            agentId = message.CurrentAgentId;
            return false;
        }

        agentId = routingRules.First().AgentId;
        // Add routed agent
        message.FunctionArgs = AppendPropertyToArgs(message.FunctionArgs, "route_to", agentId);

        // Check required fields
        var root = JsonSerializer.Deserialize<JsonElement>(message.FunctionArgs);
        var missingFields = new List<string>();
        foreach (var field in routingRules.Where(x => x.Required).Select(x => x.Field))
        {
            if (!root.EnumerateObject().Any(x => x.Name == field))
            {
                missingFields.Add(field);
            }
            else if (root.EnumerateObject().Any(x => x.Name == field) &&
                string.IsNullOrEmpty(root.EnumerateObject().FirstOrDefault(x => x.Name == field).Value.ToString()))
            {
                missingFields.Add(field);
            }
        }

        // Check if states contains the field according conversation context.
        var states = _services.GetRequiredService<IConversationStateService>();
        foreach (var field in missingFields.ToList())
        {
            if (!string.IsNullOrEmpty(states.GetState(field)))
            {
                var value = states.GetState(field);
                message.FunctionArgs = AppendPropertyToArgs(message.FunctionArgs, field, value);
                missingFields.Remove(field);
            }
        }

        if (missingFields.Any())
        {
            // Add field to args
            message.FunctionArgs = AppendPropertyToArgs(message.FunctionArgs, "missing_fields", missingFields);
            message.ExecutionResult = $"missing some information: {string.Join(',', missingFields)}";

            // Handle redirect
            var routingRule = routingRules.FirstOrDefault(x => missingFields.Contains(x.Field));
            if (!string.IsNullOrEmpty(routingRule.RedirectTo))
            {
                var db = _services.GetRequiredService<IBotSharpRepository>();
                var record = db.Agents.First(x => x.Id == routingRule.RedirectTo);

                // Add redirected agent
                message.FunctionArgs = AppendPropertyToArgs(message.FunctionArgs, "redirect_to", record.Name);
            }
            else
            {
                // back to router
                agentId = message.CurrentAgentId;
            }
        }

        return missingFields.Any();
    }

    private string AppendPropertyToArgs(string args, string key, string value)
    {
        return args.Substring(0, args.Length - 1) + $", \"{key}\": \"{value}\"" + "}";
    }

    private string AppendPropertyToArgs(string args, string key, IEnumerable<string> values)
    {
        string fields = string.Join(",", values.Select(x => $"\"{x}\""));
        return args.Substring(0, args.Length - 1) + $", \"{key}\": [{fields}]" + "}";
    }
}
