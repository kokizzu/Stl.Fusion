namespace Stl.Fusion.Tests.UIModels;

public record QueryParamModel
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new List<string>() { "1", "one", "two" };
}