using Microsoft.Extensions.Options;

namespace UKBatch.Storage.EntityFrameworkCore;

/// <summary>
/// Validates <see cref="EfStorageOptions"/>: a provider MUST be selected (<see cref="EfProvider.None"/>
/// is rejected) and the connection string MUST be non-empty. Used both for eager registration-time
/// fail (<see cref="ValidateOrThrow"/>) and for host-start validation parity with the Core
/// <c>UKBatchOptionsValidator</c>.
/// </summary>
/// <remarks>
/// Calling both <c>UsePostgres</c> and <c>UseSqlite</c> is harmless last-wins; the validator does not
/// need to reject the double-call.
/// </remarks>
internal sealed class EfStorageOptionsValidator : IValidateOptions<EfStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, EfStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Provider == EfProvider.None)
        {
            return ValidateOptionsResult.Fail(
                "EfStorageOptions: no provider selected. Call UsePostgres(connectionString) or UseSqlite(connectionString).");
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return ValidateOptionsResult.Fail(
                "EfStorageOptions: the connection string is empty.");
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>Eager validation for registration-time fail-fast; throws on the first failure.</summary>
    public static void ValidateOrThrow(EfStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = new EfStorageOptionsValidator().Validate(name: null, options);
        if (result.Failed)
        {
            throw new OptionsValidationException(
                nameof(EfStorageOptions),
                typeof(EfStorageOptions),
                result.Failures ?? new[] { result.FailureMessage ?? "EfStorageOptions validation failed." });
        }
    }
}
