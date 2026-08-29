using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vessel.Config;
using Xunit;

namespace Vessel.Tests;

/// <summary>Phase 6 D5 — the README's config table must cover every validated user setting.</summary>
public sealed class ConfigDocumentationTests
{
    [Fact]
    public void Readme_ConfigFieldsMatchValidatedModel()
    {
        string readme = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "README.md"));
        Match marker = Regex.Match(readme, @"<!-- config-fields: (?<fields>[^>]+) -->");
        Assert.True(marker.Success, "README must declare its config-fields marker for parity verification.");

        string[] documented = marker.Groups["fields"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        string[] model = Fields(typeof(VesselConfig), "")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(model, documented);
    }

    private static IEnumerable<string> Fields(Type type, string prefix)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetCustomAttribute<System.Text.Json.Serialization.JsonExtensionDataAttribute>() is not null)
            {
                continue;
            }

            string name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            string path = string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            Type? nestedType = propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                ? propertyType.GetGenericArguments()[1]
                : propertyType.IsClass && propertyType.Namespace == typeof(VesselConfig).Namespace ? propertyType : null;

            if (nestedType is null || nestedType == typeof(string))
            {
                yield return path;
                continue;
            }

            foreach (string child in Fields(nestedType, path))
            {
                yield return child;
            }
        }
    }
}
