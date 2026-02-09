using System.Text.Json.Serialization;

namespace Agent.Core.Linting;

/// <summary>How serious a lint finding is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    Info,
    Warning,
    Error,
}
