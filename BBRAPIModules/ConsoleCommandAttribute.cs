using System.Reflection;
using System.Text;

namespace BBRAPIModules;

[AttributeUsage(AttributeTargets.Method)]
public class ConsoleCommandAttribute : Attribute
{
    public string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    public ConsoleCommandAttribute(string name)
    {
        Name = name;
    }
}

public class ConsoleCommandHandler
{
    private Dictionary<string, (APIModule Module, MethodInfo Method)> commandCallbacks = new();

    public void RegisterCommands(APIModule module)
    {

        foreach (var method in module.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var attribute = method.GetCustomAttribute<ConsoleCommandAttribute>();
            if (attribute == null) continue;

            var command = attribute.Name.Trim().ToLower();

            // Prevent duplicate command names in different methods or modules
            if (commandCallbacks.ContainsKey(command))
            {
                if (commandCallbacks[command].Method == method) continue;
                throw new Exception($"Command callback method {method.Name} in module {module.GetType().Name} has the same name as another command callback method in the same module.");
            }

            // Prevent parent commands of subcommands (!perm command does not allow !perm add and !perm remove)
            foreach (var subcommand in commandCallbacks.Keys.Where(c => c.Contains(' ')))
            {
                if (!subcommand.StartsWith(command)) continue;
                throw new Exception($"Command callback {command} in module {module.GetType().Name} conflicts with subcommand {subcommand}.");
            }

            // Prevent subcommands of existing commands (!perm add and !perm remove do not allow !perm)
            if (command.Contains(' '))
            {
                var subcommandChain = command.Split(' ');
                var subcommand = "";
                foreach (var t in subcommandChain)
                {
                    subcommand += $"{t} ";
                    if (commandCallbacks.ContainsKey(subcommand.Trim()))
                    {
                        throw new Exception($"Command callback {command} in module {module.GetType().Name} conflicts with parent command {subcommand.Trim()}.");
                    }
                }
            }

            commandCallbacks.Add(command, (module, method));
        }
    }

    public void UnRegisterCommands(APIModule module)
    {
        foreach (var command in commandCallbacks.Where(c => c.Value.Module == module).ToList())
        {
            commandCallbacks.Remove(command.Key);
        }
    }

    private static string[] parseCommandString(string command)
    {
        List<string> parameterValues = new();
        string[] tokens = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        bool insideQuotes = false;
        StringBuilder currentValue = new();

        foreach (var token in tokens)
        {
            if (!insideQuotes)
            {
                if (token.StartsWith("\""))
                {
                    insideQuotes = true;
                    currentValue.Append(token.Substring(1));
                }
                else
                {
                    parameterValues.Add(token);
                }
            }
            else
            {
                if (token.EndsWith("\""))
                {
                    insideQuotes = false;
                    currentValue.Append(" ").Append(token.Substring(0, token.Length - 1));
                    parameterValues.Add(currentValue.ToString());
                    currentValue.Clear();
                }
                else
                {
                    currentValue.Append(" ").Append(token);
                }
            }
        }

        return parameterValues.Select(unescapeQuotes).ToArray();
    }

    private static string unescapeQuotes(string input)
    {
        return input.Replace("\\\"", "\"");
    }

    private static bool tryParseParameter(ParameterInfo parameterInfo, string input, out object? parsedValue)
    {
        parsedValue = null;

        try
        {
            if (parameterInfo.ParameterType.IsEnum)
            {
                parsedValue = Enum.Parse(parameterInfo.ParameterType, input, true);
            }
            else
            {
                var targetType = Nullable.GetUnderlyingType(parameterInfo.ParameterType) ?? parameterInfo.ParameterType;
                parsedValue = Convert.ChangeType(input, targetType);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void HandleConsoleCommand(string rawCommand)
    {
        if (HandleBuildInCommand(rawCommand)) return;
        HandleModuleCommand(rawCommand);
    }

    private void HandleModuleCommand(string rawCommand)
    {
        var fullCommand = parseCommandString(rawCommand);
        var command = fullCommand[0].Trim().ToLower();

        int subCommandSkip;
        for (subCommandSkip = 1; subCommandSkip < fullCommand.Length && !commandCallbacks.ContainsKey(command); subCommandSkip++)
        {
            command += $" {fullCommand[subCommandSkip]}";
        }

        if (!commandCallbacks.ContainsKey(command)) return;

        fullCommand = new[] { command }.Concat(fullCommand.Skip(subCommandSkip)).ToArray();
        var (module, method) = commandCallbacks[command];
        var parameters = method.GetParameters();

        if (parameters.Length == 0)
        {
            method.Invoke(module, null);
            return;
        }

        var hasOptional = parameters.Any(p => p.IsOptional);
        if (fullCommand.Length - 1 < parameters.Count(p => !p.IsOptional) || fullCommand.Length - 1 > parameters.Length - 1)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Require {(hasOptional ? $"between {parameters.Count(p => !p.IsOptional)} and {parameters.Length - 1}" : $"{parameters.Length - 1}")} but got {fullCommand.Length - 1} argument{((fullCommand.Length - 1) == 1 ? "" : "s")}.");
            Console.ResetColor();
            return;
        }

        object?[] args = new object[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            if (parameter.IsOptional && i >= fullCommand.Length)
            {
                args[i] = parameter.DefaultValue;
                continue;
            }

            var argument = fullCommand[i].Trim();

            if (parameter.ParameterType == typeof(string))
            {
                args[i] = argument;
            }
            else
            {
                if (!tryParseParameter(parameter, argument, out var parsedValue))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Couldn't parse value {argument} to type {parameter.ParameterType.Name}.");
                    Console.ResetColor();
                    return;
                }

                args[i] = parsedValue;
            }
        }

        method.Invoke(module, args);
    }

    private bool HandleBuildInCommand(string rawCommand)
    {
        var fullCommand = parseCommandString(rawCommand);
        var command = fullCommand[0].Trim().ToLower();

        switch (command)
        {
            case "module_help":
                {
                    var arg = fullCommand.Length > 1 ? fullCommand[1] : null;
                    if (arg == null)
                    {
                        HelpCommand();
                    }
                    else
                    {
                        if (int.TryParse(arg, out var page)) HelpCommand(page);
                        CommandHelpCommand(arg);
                    }
                    return true;
                }
        }
        return false;
    }

    private void HelpCommand(int page = 1)
    {
        const int commandsPerPage = 10;
        List<string> helpLines = new();
        foreach (var (_, (_, method)) in commandCallbacks)
        {
            var consoleCommandAttribute = method.GetCustomAttribute<ConsoleCommandAttribute>()!;

            helpLines.Add($"{consoleCommandAttribute.Name}{(string.IsNullOrEmpty(consoleCommandAttribute.Description) ? "" : $": {consoleCommandAttribute.Description}")}");
        }

        var pages = (int)Math.Ceiling((double)helpLines.Count / commandsPerPage);

        if (pages == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No commands available.");
            Console.ResetColor();
            return;
        }

        if (page < 1 || page > pages)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Invalid page number. Must be between 1 and {pages}.");
            Console.ResetColor();
            return;
        }
        Console.WriteLine("Available commands");
        Console.WriteLine(string.Join(Environment.NewLine, helpLines.Skip((page - 1) * commandsPerPage).Take(commandsPerPage)));
        if (pages > 1) Console.WriteLine($"Page {page} of {pages}{(page < pages ? $" - type help {page + 1} for next page" : "")}");
    }

    private void CommandHelpCommand(string command)
    {
        if (!commandCallbacks.TryGetValue(command, out var commandCallback))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Command {command} not found.");
            Console.ResetColor();
            return;
        }

        var consoleCommandAttribute = commandCallback.Method.GetCustomAttribute<ConsoleCommandAttribute>()!;
        var hasOptional = commandCallback.Method.GetParameters().Any(p => p.IsOptional);

        Console.WriteLine($"{commandCallback.Module.GetType().Name} - {consoleCommandAttribute.Name} - {consoleCommandAttribute.Description}");
        Console.WriteLine($"Usage: {consoleCommandAttribute.Name} {string.Join(' ', commandCallback.Method.GetParameters().Select(s => $"{s.Name}{(s.IsOptional ? "*" : "")}"))}");
        if (hasOptional) Console.WriteLine("* Parameter is optional.");
    }
}