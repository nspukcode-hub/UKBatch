using System.Diagnostics.CodeAnalysis;

namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Coarse shape of a declared job parameter. Drives the typed dashboard trigger form and the per-job
/// REST/OpenAPI schema — it describes how to render and parse an input, not the exact CLR type (the
/// job's own <c>GetRequired&lt;T&gt;</c> read resolves that).
/// </summary>
// The members mirror JSON value kinds (like System.Text.Json.JsonValueKind, whose public members are
// also String/Object/Number). These are the clearest possible names for a schema-shaped value kind, and
// they are the tokens rendered onto the wire and the OpenAPI schema — CA1720's "identifier contains a
// type name" concern does not apply to enum members read as ParameterValueKind.String.
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "Enum members intentionally mirror JSON value-kind names (cf. JsonValueKind.String/Object/Number).")]
public enum ParameterValueKind
{
    /// <summary>Free text. Also used for enums, which serialize as their string name.</summary>
    String,

    /// <summary>Whole number (byte…ulong).</summary>
    Integer,

    /// <summary>Fractional number (float/double/decimal).</summary>
    Number,

    /// <summary>Boolean true/false.</summary>
    Boolean,

    /// <summary>Date/time (DateTime / DateTimeOffset), ISO-8601 on the wire.</summary>
    DateTime,

    /// <summary>Any structured value (object/array/custom type); edited as raw JSON.</summary>
    Object,
}
