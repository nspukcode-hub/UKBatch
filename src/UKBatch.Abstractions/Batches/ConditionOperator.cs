namespace UKBatch.Abstractions.Batches;

/// <summary>
/// Comparison operator for a <see cref="StepCondition"/>.
/// <para>
/// Numeric values are stable across versions; new operators will be appended. Consumers switching on this
/// enum MUST include a <c>default:</c> arm so an unrecognised future operator is handled gracefully — the
/// evaluator treats an unknown operator as "condition not met", so the guarded step is skipped rather than
/// run on an unverifiable condition.
/// </para>
/// </summary>
public enum ConditionOperator
{
    /// <summary>Value equals the comparand (numeric when both parse as numbers, boolean when both are booleans, otherwise ordinal string).</summary>
    Equals = 0,

    /// <summary>Value does not equal the comparand (inverse of <see cref="Equals"/>).</summary>
    NotEquals = 1,

    /// <summary>Value is numerically greater than the comparand.</summary>
    GreaterThan = 2,

    /// <summary>Value is numerically greater than or equal to the comparand.</summary>
    GreaterThanOrEqual = 3,

    /// <summary>Value is numerically less than the comparand.</summary>
    LessThan = 4,

    /// <summary>Value is numerically less than or equal to the comparand.</summary>
    LessThanOrEqual = 5,

    /// <summary>The parameter key is present (its value may be anything). Needs no comparand.</summary>
    Exists = 6,

    /// <summary>The parameter key is absent. Needs no comparand.</summary>
    NotExists = 7,

    /// <summary>The value reads as boolean <c>true</c>. Needs no comparand.</summary>
    IsTrue = 8,

    /// <summary>The value reads as boolean <c>false</c>. Needs no comparand.</summary>
    IsFalse = 9,

    /// <summary>The value, rendered as a string, contains the comparand as an ordinal substring.</summary>
    Contains = 10,
}
