using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Shabana.Core.Errors;

namespace Shabana.Data.Sqlite;

/// <summary>What kind of failure a <see cref="SqliteException"/> is, for the code that has to decide what to tell the user.</summary>
public enum DbErrorKind
{
    /// <summary>A rule of the schema (<c>RAISE(ABORT, 'CODE|text')</c>) with a known <see cref="ErrorCode"/>.</summary>
    Domain,
    Check,
    Unique,
    ForeignKey,
    NotNull,

    /// <summary>A STRICT table refused a value of the wrong type (a fraction or text in an INTEGER column).</summary>
    DataType,

    /// <summary>A constraint failure that is none of the above, or a trigger code unknown to <see cref="ErrorCode"/>.</summary>
    OtherConstraint,
    Busy,
    Locked,
    Corrupt,
    NotADatabase,
    DiskFull,
    ReadOnly,
    Io,
    Other,
}

/// <summary>Result of classifying a SQLite failure. <see cref="Code"/> is set only for <see cref="DbErrorKind.Domain"/>.</summary>
public sealed record MappedDbError(DbErrorKind Kind, ErrorCode? Code, string? Detail);

/// <summary>
/// Splits a schema error at the first '|' and maps the CODE to <see cref="ErrorCode"/> (03 section 9, 04 section 7.1).
/// Only a statement aborted by a trigger counts as a domain error: a text that merely LOOKS like a code (a user typed
/// "X|Y" into a note that then violates a CHECK) is not mistaken for one.
/// </summary>
public static partial class SqliteErrorMapper
{
    // SQLite result codes (sqlite3.h).
    private const int Busy = 5;
    private const int Locked = 6;
    private const int ReadOnly = 8;
    private const int IoErr = 10;
    private const int Corrupt = 11;
    private const int Full = 13;
    private const int Constraint = 19;
    private const int NotADb = 26;

    // Extended constraint codes.
    private const int ConstraintCheck = 275;
    private const int ConstraintForeignKey = 787;
    private const int ConstraintNotNull = 1299;
    private const int ConstraintPrimaryKey = 1555;
    private const int ConstraintTrigger = 1811;
    private const int ConstraintUnique = 2067;
    private const int ConstraintDataType = 3091;

    [GeneratedRegex(@"'(?<code>[A-Z][A-Z0-9_]*)\|(?<text>[^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex RaisedMessage();

    public static MappedDbError Classify(SqliteException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var detail = ex.Message;
        var primary = ex.SqliteErrorCode;
        var extended = ex.SqliteExtendedErrorCode;

        switch (primary)
        {
            case Constraint:
                return ClassifyConstraint(ex, extended, detail);
            case Busy:
                return new MappedDbError(DbErrorKind.Busy, null, detail);
            case Locked:
                return new MappedDbError(DbErrorKind.Locked, null, detail);
            case ReadOnly:
                return new MappedDbError(DbErrorKind.ReadOnly, null, detail);
            case IoErr:
                return new MappedDbError(DbErrorKind.Io, null, detail);
            case Corrupt:
                return new MappedDbError(DbErrorKind.Corrupt, null, detail);
            case Full:
                return new MappedDbError(DbErrorKind.DiskFull, null, detail);
            case NotADb:
                return new MappedDbError(DbErrorKind.NotADatabase, null, detail);
            default:
                return new MappedDbError(DbErrorKind.Other, null, detail);
        }
    }

    private static MappedDbError ClassifyConstraint(SqliteException ex, int extended, string detail)
    {
        switch (extended)
        {
            case ConstraintTrigger:
                var match = RaisedMessage().Match(ex.Message);
                if (match.Success && ErrorCodes.TryParse(match.Groups["code"].Value, out var code))
                {
                    return new MappedDbError(DbErrorKind.Domain, code, match.Groups["text"].Value);
                }

                return new MappedDbError(DbErrorKind.OtherConstraint, null, detail);
            case ConstraintCheck:
                return new MappedDbError(DbErrorKind.Check, null, detail);
            case ConstraintForeignKey:
                return new MappedDbError(DbErrorKind.ForeignKey, null, detail);
            case ConstraintNotNull:
                return new MappedDbError(DbErrorKind.NotNull, null, detail);
            case ConstraintDataType:
                return new MappedDbError(DbErrorKind.DataType, null, detail);
            case ConstraintUnique:
            case ConstraintPrimaryKey:
                return new MappedDbError(DbErrorKind.Unique, null, detail);
            default:
                return new MappedDbError(DbErrorKind.OtherConstraint, null, detail);
        }
    }

    /// <summary>
    /// A <see cref="DomainException"/> (Arabic message, no stack trace for the user) for a rule violation, otherwise the original
    /// exception unchanged: the caller logs it and shows the generic message.
    /// </summary>
    public static Exception ToException(SqliteException ex)
    {
        var mapped = Classify(ex);
        return mapped is { Kind: DbErrorKind.Domain, Code: { } code } ? new DomainException(code, ex) : ex;
    }
}
