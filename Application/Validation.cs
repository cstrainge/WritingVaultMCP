namespace WritingVaultMcp.Application;

public sealed record ValidationError(
    string Code,
    string Field,
    string Message);

public sealed class ValidationResult
{
    private static readonly ValidationResult SuccessfulResult = new([]);

    public ValidationResult(IEnumerable<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors.ToArray();
    }

    public IReadOnlyList<ValidationError> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public static ValidationResult Success => SuccessfulResult;

    public ValidationResult Combine(ValidationResult other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (IsValid)
        {
            return other;
        }

        if (other.IsValid)
        {
            return this;
        }

        return new ValidationResult(Errors.Concat(other.Errors));
    }

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new VaultValidationException(Errors);
        }
    }
}

public sealed class VaultValidationException : Exception
{
    public VaultValidationException(IEnumerable<ValidationError> errors)
        : base("The requested operation contains invalid values.")
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors.ToArray();
    }

    public IReadOnlyList<ValidationError> Errors { get; }
}
