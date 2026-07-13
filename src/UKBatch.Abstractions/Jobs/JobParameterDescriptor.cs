namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// A parameter a job announces at registration via <c>WithParameter&lt;T&gt;</c>. Declaration is an
/// announcement, not a contract: undeclared keys stay permissive, and a declared default is metadata
/// only (form pre-fill + schema default) — it is NOT merged into the job's default parameters.
/// </summary>
public sealed record class JobParameterDescriptor
{
    /// <summary>Parameter key. Non-empty; must not use the reserved <c>ukbatch.</c> prefix.</summary>
    public required string Name { get; init; }

    /// <summary>Coarse value shape that drives the typed form and schema.</summary>
    public required ParameterValueKind Kind { get; init; }

    /// <summary>
    /// When true, the single-job REST trigger rejects a call that omits this key (subject to
    /// <c>UKBatchOptions.EnforceDeclaredParameters</c>).
    /// </summary>
    public bool Required { get; init; }

    /// <summary>Optional human description surfaced in the form and schema.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Metadata default (form pre-fill + schema default). <c>null</c> for a required parameter (there
    /// is no meaningful default — <c>default(T)</c> for a value type is a real 0/false the form must not
    /// present as a pre-answered value). Boxed CLR value for a non-required parameter; when deserialized
    /// from REST JSON on the dashboard it arrives as a <see cref="System.Text.Json.JsonElement"/>.
    /// </summary>
    public object? DefaultValue { get; init; }

    /// <summary>
    /// Builds a descriptor from a compile-time type argument, mapping <typeparamref name="T"/> to a
    /// <see cref="ParameterValueKind"/> and applying the required→null default rule.
    /// </summary>
    public static JobParameterDescriptor Create<T>(string name, T? defaultValue, bool required, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new JobParameterDescriptor
        {
            Name = name,
            Kind = KindFromClrType(typeof(T)),
            Required = required,
            Description = description,
            DefaultValue = required ? null : defaultValue,
        };
    }

    /// <summary>Maps a CLR type (unwrapping <see cref="System.Nullable{T}"/>) to a value kind.</summary>
    public static ParameterValueKind KindFromClrType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string))
        {
            return ParameterValueKind.String;
        }
        if (t == typeof(bool))
        {
            return ParameterValueKind.Boolean;
        }
        if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
            || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong))
        {
            return ParameterValueKind.Integer;
        }
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal))
        {
            return ParameterValueKind.Number;
        }
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset))
        {
            return ParameterValueKind.DateTime;
        }
        if (t.IsEnum)
        {
            return ParameterValueKind.String;
        }
        return ParameterValueKind.Object;
    }
}
