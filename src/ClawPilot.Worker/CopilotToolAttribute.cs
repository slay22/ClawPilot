namespace ClawPilot.Worker;

[AttributeUsage(AttributeTargets.Method)]
public class CopilotToolAttribute(string name, string description) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; } = description;
}
