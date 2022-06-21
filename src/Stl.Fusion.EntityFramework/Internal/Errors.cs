using Microsoft.EntityFrameworkCore;
using Stl.Fusion.EntityFramework.Multitenancy;

namespace Stl.Fusion.EntityFramework.Internal;

public static class Errors
{
    public static Exception CreateCommandDbContextIsCalledFromInvalidationCode()
        => new InvalidOperationException(
            $"{nameof(DbHub<DbContext>.CreateCommandDbContext)} is called from the invalidation code. " +
            $"If you want to read the data there, use {nameof(DbHub<DbContext>.CreateDbContext)} instead.");
    public static Exception DbContextIsReadOnly()
        => new InvalidOperationException("This DbContext is read-only.");

    public static Exception NoOperationsFrameworkServices()
        => new InvalidOperationException(
            "Operations Framework services aren't registered. " +
            "Call DbContextBuilder<TDbContext>.AddDbOperations before calling this method to add them.");

    public static Exception TenantInfoIsReadOnly()
        => new InvalidOperationException("DbContext is already created, so TenantInfo cannot be changed at this point.");
    public static Exception DefaultDbContextFactoryDoesNotSupportMultitenancy()
        => new NotSupportedException(
            "DefaultDbContextFactory does not support multitenancy, " +
            "but non-null TenantInfo is passed to its CreateDbContext method.");

    public static Exception EntityNotFound<TEntity>()
        => EntityNotFound(typeof(TEntity));
    public static Exception EntityNotFound(Type entityType)
        => new KeyNotFoundException($"Requested {entityType.Name} entity is not found.");

    public static Exception UnsupportedDbHint(DbHint hint)
        => new NotSupportedException($"Unsupported DbHint: {hint}");
}
