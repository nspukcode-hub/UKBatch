using System.Globalization;
using System.Text.Json;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;

namespace UKBatch.Runtime;

/// <summary>
/// Evaluates a <see cref="StepCondition"/> against the parameters a step would receive at dispatch — the
/// batch's initial/trigger parameters merged with earlier steps' forwarded outputs. Reads are kind-aware
/// and tolerate both shapes a value can take: a boxed CLR value (produced locally) and a
/// <see cref="JsonElement"/> (arrived across a service boundary or rehydrated from a durable store on
/// resume). A comparison whose sides cannot be coerced to a common type evaluates to <c>false</c> (the
/// guarded step is skipped) rather than throwing.
/// </summary>
internal static class StepConditionEvaluator
{
    /// <summary>
    /// <c>true</c> when the condition holds (the step should run), <c>false</c> when it does not (skip).
    /// </summary>
    public static bool Evaluate(StepCondition condition, JobParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(parameters);

        var hasKey = parameters.Values.TryGetValue(condition.ParameterKey, out var raw);

        switch (condition.Operator)
        {
            case ConditionOperator.Exists:
                return hasKey;
            case ConditionOperator.NotExists:
                return !hasKey;
        }

        // Every remaining operator inspects the value; a missing key, a null, or an undefined element cannot
        // satisfy them (Undefined is unreachable from real System.Text.Json data, but guarding it keeps the
        // "never throw" contract airtight against a default(JsonElement)).
        if (!hasKey || raw is null || (raw is JsonElement nullEl && nullEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return false;
        }

        switch (condition.Operator)
        {
            case ConditionOperator.IsTrue:
                return TryReadBool(raw, out var bt) && bt;
            case ConditionOperator.IsFalse:
                return TryReadBool(raw, out var bf) && !bf;
        }

        // The comparison operators need a comparand. The validator requires it, but stay defensive:
        // a comparison with no comparand is "not met".
        var comparand = condition.Value;
        if (comparand is null)
        {
            return false;
        }

        switch (condition.Operator)
        {
            case ConditionOperator.GreaterThan:
            case ConditionOperator.GreaterThanOrEqual:
            case ConditionOperator.LessThan:
            case ConditionOperator.LessThanOrEqual:
                if (!TryReadDouble(raw, out var left) ||
                    !double.TryParse(comparand, NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
                {
                    return false;
                }
                return condition.Operator switch
                {
                    ConditionOperator.GreaterThan => left > right,
                    ConditionOperator.GreaterThanOrEqual => left >= right,
                    ConditionOperator.LessThan => left < right,
                    _ => left <= right,
                };

            case ConditionOperator.Equals:
                return ValuesEqual(raw, comparand);
            case ConditionOperator.NotEquals:
                return !ValuesEqual(raw, comparand);

            case ConditionOperator.Contains:
                var s = ReadString(raw);
                return s is not null && s.Contains(comparand, StringComparison.Ordinal);

            default:
                // Unknown / future operator: treat as not met so the step is skipped rather than run on an
                // unverifiable condition (mirrors the enum's forward-compat contract).
                return false;
        }
    }

    // Equality is checked most-specific-first: numeric (so 500 == "500" == "500.0"), then boolean
    // (true == "true"), then ordinal string. This keeps "amount equals 1000" numeric while still letting a
    // plain string value compare by text.
    private static bool ValuesEqual(object? raw, string comparand)
    {
        if (TryReadDouble(raw, out var leftNum) &&
            double.TryParse(comparand, NumberStyles.Float, CultureInfo.InvariantCulture, out var rightNum))
        {
            return leftNum == rightNum;
        }
        if (TryReadBool(raw, out var leftBool) && bool.TryParse(comparand, out var rightBool))
        {
            return leftBool == rightBool;
        }
        var leftStr = ReadString(raw);
        return leftStr is not null && string.Equals(leftStr, comparand, StringComparison.Ordinal);
    }

    private static bool TryReadDouble(object? raw, out double value)
    {
        switch (raw)
        {
            case double d: value = d; return true;
            case float f: value = f; return true;
            case long l: value = l; return true;
            case ulong ul: value = ul; return true;
            case int i: value = i; return true;
            case uint ui: value = ui; return true;
            case short sh: value = sh; return true;
            case ushort us: value = us; return true;
            case byte b: value = b; return true;
            case sbyte sb: value = sb; return true;
            case decimal m: value = (double)m; return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value):
                return true;
            case JsonElement el:
                if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value))
                {
                    return true;
                }
                if (el.ValueKind == JsonValueKind.String &&
                    double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return true;
                }
                break;
        }
        value = 0;
        return false;
    }

    private static bool TryReadBool(object? raw, out bool value)
    {
        switch (raw)
        {
            case bool b:
                value = b;
                return true;
            case string s when bool.TryParse(s, out value):
                return true;
            case JsonElement el:
                switch (el.ValueKind)
                {
                    case JsonValueKind.True: value = true; return true;
                    case JsonValueKind.False: value = false; return true;
                    case JsonValueKind.String when bool.TryParse(el.GetString(), out value): return true;
                }
                break;
        }
        value = false;
        return false;
    }

    private static string? ReadString(object? raw)
    {
        switch (raw)
        {
            case null:
                return null;
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";   // JSON-lowercase, so Contains/string-equals is consistent across shapes
            case JsonElement el:
                return el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Null => null,
                    _ => el.GetRawText(),      // number / bool / object / array → canonical JSON text
                };
            case IFormattable f:
                return f.ToString(null, CultureInfo.InvariantCulture);
            default:
                return raw.ToString();
        }
    }
}
