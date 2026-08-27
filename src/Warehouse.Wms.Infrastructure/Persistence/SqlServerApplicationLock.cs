using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Warehouse.Wms.Infrastructure.Persistence;

public static class SqlServerApplicationLock
{
    public static async Task AcquireAsync(
        WarehouseDbContext db,
        string resource,
        int timeoutMilliseconds = 30_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(resource)) throw new ArgumentException("A lock resource is required.", nameof(resource));
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMilliseconds);

        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("SQL application locks require an active database transaction.");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout; SELECT @result;";
        command.Parameters.Add(new SqlParameter("@resource", resource));
        command.Parameters.Add(new SqlParameter("@timeout", timeoutMilliseconds));

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar is DBNull)
            throw new InvalidOperationException($"SQL application lock '{resource}' returned no acquisition result.");
        var result = Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);

        ThrowIfNotAcquired(result, resource);
    }

    public static void ThrowIfNotAcquired(int result, string resource)
    {
        if (result < 0)
            throw new InvalidOperationException($"SQL application lock '{resource}' was not acquired (result {result}).");
    }
}
