using System.Globalization;
using System.Text.Json.Serialization;

namespace WritingVaultMcp.Application;

public enum ApplicationErrorKind
{
    Validation,
    NotFound,
    Duplicate,
    ConstraintViolation,
    DeleteBlocked,
    ConcurrencyConflict,
    StorageFailure
}

public sealed record ResourceReference(string Type, string Key)
{
    public static ResourceReference FromId(string type, int id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return new ResourceReference(type, id.ToString(CultureInfo.InvariantCulture));
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ValidationFailedError), "validation")]
[JsonDerivedType(typeof(NotFoundError), "not_found")]
[JsonDerivedType(typeof(DuplicateError), "duplicate")]
[JsonDerivedType(typeof(ConstraintViolationError), "constraint_violation")]
[JsonDerivedType(typeof(DeleteBlockedError), "delete_blocked")]
[JsonDerivedType(typeof(ConcurrencyConflictError), "concurrency_conflict")]
[JsonDerivedType(typeof(StorageFailureError), "storage_failure")]
public abstract record ApplicationError(
    ApplicationErrorKind Kind,
    string Code,
    string Message);

public sealed record ValidationFailedError : ApplicationError
{
    public ValidationFailedError(IEnumerable<ValidationError> errors)
        : base(
            ApplicationErrorKind.Validation,
            "validation.failed",
            "One or more supplied values are invalid.")
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors.ToArray();
        if (Errors.Count == 0)
        {
            throw new ArgumentException("A validation failure must contain at least one error.", nameof(errors));
        }
    }

    public ValidationFailedError(ValidationResult result)
        : this(GetInvalidErrors(result))
    {
    }

    public IReadOnlyList<ValidationError> Errors { get; }

    private static IReadOnlyList<ValidationError> GetInvalidErrors(ValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsValid)
        {
            throw new ArgumentException("A successful validation result cannot become an error.", nameof(result));
        }

        return result.Errors;
    }
}

public sealed record NotFoundError(ResourceReference Target) : ApplicationError(
    ApplicationErrorKind.NotFound,
    "entity.not_found",
    $"{Target.Type} '{Target.Key}' was not found.");

public sealed record DuplicateError(
    string ResourceType,
    string Field,
    string? ConflictingValue = null) : ApplicationError(
        ApplicationErrorKind.Duplicate,
        "entity.duplicate",
        $"A {ResourceType} with the same {Field} already exists.");

public sealed record ConstraintViolationError(
    string Constraint,
    string Detail) : ApplicationError(
        ApplicationErrorKind.ConstraintViolation,
        "constraint.violation",
        Detail);

public sealed record BlockingReference(string ResourceType, int Count);

public sealed record DeleteBlockedError : ApplicationError
{
    public DeleteBlockedError(
        ResourceReference target,
        IEnumerable<BlockingReference> blockers)
        : base(
            ApplicationErrorKind.DeleteBlocked,
            "delete.blocked",
            $"{target.Type} '{target.Key}' cannot be deleted while related records exist.")
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(blockers);

        Target = target;
        Blockers = blockers.ToArray();
        if (Blockers.Count == 0)
        {
            throw new ArgumentException("A blocked deletion must identify at least one blocker.", nameof(blockers));
        }

        if (Blockers.Any(blocker => string.IsNullOrWhiteSpace(blocker.ResourceType) || blocker.Count <= 0))
        {
            throw new ArgumentException("Every blocker must have a resource type and positive count.", nameof(blockers));
        }
    }

    public ResourceReference Target { get; }
    public IReadOnlyList<BlockingReference> Blockers { get; }
}

public sealed record ConcurrencyConflictError(
    ResourceReference Target,
    DateTime? ExpectedUpdatedAt,
    DateTime? CurrentUpdatedAt) : ApplicationError(
        ApplicationErrorKind.ConcurrencyConflict,
        "concurrency.conflict",
        $"{Target.Type} '{Target.Key}' changed after it was read.");

public sealed record StorageFailureError(
    bool Retryable,
    string Operation) : ApplicationError(
        ApplicationErrorKind.StorageFailure,
        "storage.failure",
        $"The database could not complete the {Operation} operation.");

public sealed record UpdatePrecondition(DateTime ExpectedUpdatedAt);

public sealed class OperationResult
{
    private OperationResult(bool isSuccess, ApplicationError? error)
    {
        if (isSuccess == (error is not null))
        {
            throw new ArgumentException("A result must contain either success or one error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public ApplicationError? Error { get; }

    public static OperationResult Success() => new(true, null);

    public static OperationResult Failure(ApplicationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new OperationResult(false, error);
    }

    public static OperationResult<T> Success<T>(T value) => OperationResult<T>.Success(value);

    public static OperationResult<T> Failure<T>(ApplicationError error) =>
        OperationResult<T>.Failure(error);

    public static OperationResult ValidationFailure(ValidationResult result) =>
        Failure(new ValidationFailedError(result));

    public static OperationResult<T> ValidationFailure<T>(ValidationResult result) =>
        Failure<T>(new ValidationFailedError(result));
}

public sealed class OperationResult<T>
{
    private OperationResult(bool isSuccess, T? value, ApplicationError? error)
    {
        if (isSuccess == (error is not null))
        {
            throw new ArgumentException("A result must contain either success or one error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public T? Value { get; }
    public ApplicationError? Error { get; }

    public static OperationResult<T> Success(T value) => new(true, value, null);

    public static OperationResult<T> Failure(ApplicationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new OperationResult<T>(false, default, error);
    }

    public T GetValueOrThrow()
    {
        if (IsFailure)
        {
            throw new InvalidOperationException(
                $"Cannot retrieve a value from failed result '{Error!.Code}'.");
        }

        return Value!;
    }

    public TResult Match<TResult>(
        Func<T, TResult> success,
        Func<ApplicationError, TResult> failure)
    {
        ArgumentNullException.ThrowIfNull(success);
        ArgumentNullException.ThrowIfNull(failure);

        return IsSuccess ? success(Value!) : failure(Error!);
    }
}
