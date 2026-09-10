namespace SupportSatchel.Core.Storage;

/// <summary>Base class for local-store failures.</summary>
public class StoreException : InvalidOperationException
{
    /// <summary>Initializes the exception with a message.</summary>
    public StoreException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with a message and cause.</summary>
    public StoreException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>A profile with the requested id does not exist.</summary>
public sealed class ProfileNotFoundException : StoreException
{
    /// <summary>Initializes the exception for a missing profile id.</summary>
    public ProfileNotFoundException(Guid id)
        : base($"No profile with id {id} exists in the local store.")
    {
    }
}

/// <summary>A run with the requested id does not exist.</summary>
public sealed class RunNotFoundException : StoreException
{
    /// <summary>Initializes the exception for a missing run id.</summary>
    public RunNotFoundException(Guid id)
        : base($"No run with id {id} exists in the local store.")
    {
    }
}

/// <summary>Another profile already owns the requested (unique) name.</summary>
public sealed class ProfileNameConflictException : StoreException
{
    /// <summary>Initializes the exception for a conflicting profile name.</summary>
    public ProfileNameConflictException(string name)
        : base($"A profile named '{name}' already exists; profile names must be unique.")
    {
    }
}

/// <summary>A profile failed <c>ProfileValidator</c> checks and was rejected.</summary>
public sealed class ProfileValidationException : StoreException
{
    /// <summary>Creates the exception with all validation issues attached.</summary>
    public ProfileValidationException(Domain.ValidationResult result)
        : base(BuildMessage(result))
    {
        Issues = result.Issues;
    }

    /// <summary>All validator issues that caused the rejection.</summary>
    public IReadOnlyList<Domain.ValidationIssue> Issues { get; }

    private static string BuildMessage(Domain.ValidationResult result) =>
        "Profile validation failed: " +
        string.Join("; ", result.Issues.Select(i => $"{i.Path}: {i.Message}"));
}
